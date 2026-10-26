using System.Threading.Channels;

namespace Kairos.Persistence.Postgres;

/// <summary>
/// Group commit: concurrent workers hand their workflow-task completions to one writer that
/// commits up to <c>maxBatch</c> of them in a single round trip (several writers keep batches in flight concurrently) and transaction, waiting at most <c>linger</c> for the batch to
/// fill. Trades a little latency for far fewer commits under load. Each task keeps its own guards (lease token, last sequence, last hash), so a stale or forged
/// completion fails alone; a request the store refuses while preparing is retried task by task so it cannot fail its neighbours. Not wired into <c>AddKairos</c> by default; the benchmarks
/// (<c>benchmarks/Kairos.Benchmarks</c>, <c>groupcommit</c>, and the server benchmark) decide whether and when it should be.
/// </summary>
public sealed class WorkflowTaskGroupCommitter : IAsyncDisposable
{
    private readonly PostgresKairosStore _store;
    private readonly int _maxBatch;
    private readonly TimeSpan _linger;
    private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = false });
    private readonly Task[] _writers;
    private long _batches;
    private long _completions;

    public WorkflowTaskGroupCommitter(PostgresKairosStore store, int maxBatch, TimeSpan linger, int writers = 4)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatch, 1);
        _store = store;
        _maxBatch = maxBatch;
        _linger = linger;
        ArgumentOutOfRangeException.ThrowIfLessThan(writers, 1);
        _writers = [.. Enumerable.Range(0, writers).Select(_ => Task.Run(WriteLoopAsync))];
    }

    /// <summary>Number of batches committed (for reporting average batch size).</summary>
    public long Batches => Interlocked.Read(ref _batches);

    public long Completions => Interlocked.Read(ref _completions);

    public Task<CompletionOutcome> SubmitAsync(WorkflowTask task, IReadOnlyList<HistoryEvent> newEvents, DateTimeOffset now)
    {
        var item = new Item(new WorkflowTaskCompletion(task, newEvents), now, new TaskCompletionSource<CompletionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously));
        ObjectDisposedException.ThrowIf(!_queue.Writer.TryWrite(item), this);

        return item.Result.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await Task.WhenAll(_writers).ConfigureAwait(false);
    }

#pragma warning disable CA1031 // A failed commit fails every waiter in the batch with the same error; the writer keeps running.
    private async Task WriteLoopAsync()
    {
        var batch = new List<Item>(_maxBatch);
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < _maxBatch && _queue.Reader.TryRead(out Item? item))
            {
                batch.Add(item);
            }

            if (batch.Count < _maxBatch && _linger > TimeSpan.Zero)
            {
                using var cts = new CancellationTokenSource(_linger);
                try
                {
                    while (batch.Count < _maxBatch && await _queue.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                    {
                        while (batch.Count < _maxBatch && _queue.Reader.TryRead(out Item? item))
                        {
                            batch.Add(item);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // linger elapsed: commit what we have
                }
            }

            try
            {
                // One tenant scope per commit: split by scope (embedded mode has exactly one).
                foreach (IGrouping<TenantScope, Item> group in batch.GroupBy(i => i.Completion.Task.Run.Scope))
                {
                    Item[] items = [.. group];
                    CompletionOutcome[] outcomes;
                    try
                    {
                        outcomes = await _store.CompleteWorkflowTasksAsync(
                            [.. items.Select(i => i.Completion)], items.Max(i => i.Now), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (ArgumentException) when (items.Length > 1)
                    {
                        // The store refused a request while preparing the batch, before writing anything. Complete the tasks one by one so that only the offender gets the error.
                        foreach (Item single in items)
                        {
                            try
                            {
                                single.Result.SetResult(await _store.CompleteWorkflowTaskAsync(single.Completion.Task, single.Completion.NewEvents, single.Now, CancellationToken.None).ConfigureAwait(false));
                            }
                            catch (Exception ex)
                            {
                                single.Result.TrySetException(ex);
                            }

                            Interlocked.Increment(ref _batches);
                            Interlocked.Increment(ref _completions);
                        }

                        continue;
                    }

                    for (int i = 0; i < items.Length; i++)
                    {
                        items[i].Result.SetResult(outcomes[i]);
                    }

                    Interlocked.Increment(ref _batches);
                    Interlocked.Add(ref _completions, items.Length);
                }
            }
            catch (Exception ex)
            {
                foreach (Item item in batch)
                {
                    item.Result.TrySetException(ex);
                }
            }
        }
    }
#pragma warning restore CA1031

    private sealed record Item(WorkflowTaskCompletion Completion, DateTimeOffset Now, TaskCompletionSource<CompletionOutcome> Result);
}
