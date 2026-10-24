namespace Kairos.Testing;

public static class Driver
{
    /// <summary>Steps the worker (workflow tasks, activities, timers) until nothing is runnable at the current time.</summary>
    public static async Task DriveAsync(WorkerRuntime worker, int maxSteps = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worker);
        for (int i = 0; i < maxSteps; i++)
        {
            bool did = await worker.ProcessWorkflowTaskAsync(cancellationToken);
            did |= await worker.ProcessActivityTaskAsync(cancellationToken);
            did |= await worker.FireTimersAsync(cancellationToken) > 0;
            if (!did)
            {
                return;
            }
        }

        throw new InvalidOperationException($"Worker still had work after {maxSteps} steps.");
    }
}
