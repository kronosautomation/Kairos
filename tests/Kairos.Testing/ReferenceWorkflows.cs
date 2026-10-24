namespace Kairos.Testing;

/// <summary>Workflow code shared by contract tests and the golden history corpus. Frozen: changing it changes what the corpus replays.</summary>
public static class ReferenceWorkflows
{
    public static byte[] Pack(params int[] values) => [.. values.SelectMany(BitConverter.GetBytes)];

    public static byte[] SumInput(int first, int last) => Pack(first, 0, last);

    /// <summary>
    /// Sums first..last, one "echo" activity per number, handing over to a fresh run whenever the history reaches
    /// <paramref name="handoverAt"/> events (input: next, running total, last; result: the total).
    /// </summary>
    public static WorkflowFunc Summer(int handoverAt) => async (ctx, input) =>
    {
        int next = BitConverter.ToInt32(input, 0), total = BitConverter.ToInt32(input, 4), last = BitConverter.ToInt32(input, 8);
        while (next <= last)
        {
            if (ctx.HistoryLength >= handoverAt)
            {
                throw ctx.ContinueAsNew(Pack(next, total, last));
            }

            byte[] r = await ctx.ExecuteActivityAsync("echo", BitConverter.GetBytes(next));
            total += BitConverter.ToInt32(r, 0);
            next++;
        }

        return BitConverter.GetBytes(total);
    };

    /// <summary>Waits for a signal that never comes (a long-lived run an operator later terminates).</summary>
    public static WorkflowFunc WaitsForever() => async (ctx, _) => await ctx.WaitForSignalAsync("never");

    /// <summary>Schedules activities without end (a runaway run the history cap must stop).</summary>
    public static WorkflowFunc Runaway() => async (ctx, _) =>
    {
        while (true)
        {
            await ctx.ExecuteActivityAsync("echo", ReadOnlyMemory<byte>.Empty);
        }
    };
}
