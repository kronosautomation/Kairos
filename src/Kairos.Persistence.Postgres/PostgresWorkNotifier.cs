using Npgsql;

namespace Kairos.Persistence.Postgres;

/// <summary>
/// Push instead of poll: holds one connection in <c>LISTEN kairos_work</c> and wakes idle workers when the store reports new
/// work. Notifications are only a hint. They can be lost (reconnects, restarts), so <see cref="WaitAsync"/> always has a fallback
/// timeout and workers re-check the store every time they wake; correctness never depends on a notification arriving.
/// </summary>
public sealed class PostgresWorkNotifier : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;

    private PostgresWorkNotifier(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
        _listener = Task.Run(ListenAsync);
    }

    public static PostgresWorkNotifier Start(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return new PostgresWorkNotifier(dataSource);
    }

    /// <summary>
    /// Raised for every notification with what the writer said (scope, queue, how many tasks, which build). Handlers run on the listening connection's thread and must not block.
    /// A payload that cannot be read is reported as a notice with <see cref="WorkNotice.IsUnparsed"/>.
    /// </summary>
    public event Action<WorkNotice>? Notified;

    /// <summary>
    /// Raised each time the connection starts listening (the first time too) and after a lost connection comes back. Notifications sent while it was down are gone, so a listener that keeps waiters
    /// should look at the store for everything now.
    /// </summary>
    public event Action? Listening;

    /// <summary>Completes when work may be available: a notification arrived, or <paramref name="fallback"/> elapsed.</summary>
    public async Task WaitAsync(TimeSpan fallback, CancellationToken cancellationToken)
    {
        try
        {
            await _signal.WaitAsync(fallback, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down: return and let the caller observe the token
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _listener.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        _stop.Dispose();
        _signal.Dispose();
    }

    private void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // a wake-up is already pending
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(_stop.Token).ConfigureAwait(false);
                conn.Notification += (_, e) =>
                {
                    if (e.Channel == PostgresKairosStore.NotifyChannel)
                    {
                        Wake();
                        Notified?.Invoke(WorkNotice.TryParse(e.Payload, out WorkNotice notice) ? notice : new WorkNotice(Guid.Empty, Guid.Empty, WorkNotice.Unknown, 0, null));
                    }
                };
                await using (var listen = new NpgsqlCommand($"LISTEN {PostgresKairosStore.NotifyChannel}", conn))
                {
                    await listen.ExecuteNonQueryAsync(_stop.Token).ConfigureAwait(false);
                }

                Wake(); // anything that happened while we were not listening
                Listening?.Invoke();
                while (!_stop.IsCancellationRequested)
                {
                    await conn.WaitAsync(_stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (NpgsqlException)
            {
                // Connection lost: workers keep polling on the fallback interval; try to listen again shortly.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
