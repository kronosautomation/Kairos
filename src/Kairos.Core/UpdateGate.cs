namespace Kairos;

/// <summary>
/// Wakes every waiter at once when something changed. A waiter takes <see cref="Next"/> <b>before</b> it looks at the thing it waits for, looks, and awaits the task it took if there was nothing yet: a change that
/// happens between the look and the await completes that task, so it is never missed. Unlike a semaphore it wakes all waiters, and it holds no wake-up for later, so it cannot take one away from anything else.
/// </summary>
public sealed class UpdateGate
{
    private TaskCompletionSource _next = NewSource();

    /// <summary>A task that completes the next time <see cref="Pulse"/> is called.</summary>
    public Task Next => Volatile.Read(ref _next).Task;

    /// <summary>Completes the task <see cref="Next"/> returned, for everyone who holds it. Cheap and non-blocking: safe inside a lock and in a notification handler.</summary>
    public void Pulse() => Interlocked.Exchange(ref _next, NewSource()).TrySetResult();

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
