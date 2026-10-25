using System.Text.Json;
using Kairos.Mcp;
using Kairos.Testing;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Kairos.Mcp.TaskStore.Tests;

/// <summary>
/// What every <see cref="IMcpTaskStore"/> must do, whatever backs it. The same tests run against the SDK's in-memory reference store (to
/// pin down what the SDK expects) and against the Kairos store.
/// </summary>
public abstract class TaskStoreContractTests
{
    protected abstract (IMcpTaskStore Store, Func<Task> Settle) Create();

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, InputRequest> Ask(params string[] keys) =>
        keys.ToDictionary(k => k, k => InputRequest.ForElicitation(new ElicitRequestParams { Message = "ok " + k }));

    private static Dictionary<string, InputResponse> Answer(params string[] keys) =>
        keys.ToDictionary(k => k, k => InputResponse.FromElicitResult(new ElicitResult { Action = "accept" }));

    [Fact]
    public async Task A_new_task_is_working_and_can_be_read_back()
    {
        (IMcpTaskStore store, _) = Create();

        McpTaskInfo created = await store.CreateTaskAsync(default);
        McpTaskInfo? read = await store.GetTaskAsync(created.TaskId, default);

        Assert.Equal(McpTaskStatus.Working, created.Status);
        Assert.False(string.IsNullOrEmpty(created.TaskId));
        Assert.Equal(created.TaskId, read?.TaskId);
        Assert.Equal(McpTaskStatus.Working, read?.Status);
        Assert.True(read!.PollIntervalMs > 0);
    }

    [Fact]
    public async Task Two_tasks_have_different_ids()
    {
        (IMcpTaskStore store, _) = Create();
        Assert.NotEqual((await store.CreateTaskAsync(default)).TaskId, (await store.CreateTaskAsync(default)).TaskId);
    }

    [Fact]
    public async Task An_unknown_task_reads_as_null_and_cannot_be_updated()
    {
        (IMcpTaskStore store, _) = Create();

        Assert.Null(await store.GetTaskAsync("no-such-task", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetCompletedAsync("no-such-task", Json("1"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetFailedAsync("no-such-task", Json("1"), default));
        Assert.False(await store.SetCancelledAsync("no-such-task", default));
    }

    [Fact]
    public async Task Completing_a_task_records_its_result()
    {
        (IMcpTaskStore store, Func<Task> settle) = Create();
        McpTaskInfo task = await store.CreateTaskAsync(default);

        await store.SetCompletedAsync(task.TaskId, Json("""{"content":[{"type":"text","text":"done"}]}"""), default);
        await settle();

        McpTaskInfo read = (await store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.Completed, read.Status);
        Assert.Equal("done", read.Result!.Value.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Failing_a_task_records_its_error()
    {
        (IMcpTaskStore store, Func<Task> settle) = Create();
        McpTaskInfo task = await store.CreateTaskAsync(default);

        await store.SetFailedAsync(task.TaskId, Json("""{"code":-32000,"message":"boom"}"""), default);
        await settle();

        McpTaskInfo read = (await store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.Failed, read.Status);
        Assert.Equal("boom", read.Error!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_first_ending_wins_and_later_ones_are_ignored()
    {
        (IMcpTaskStore store, Func<Task> settle) = Create();
        McpTaskInfo task = await store.CreateTaskAsync(default);

        await store.SetCompletedAsync(task.TaskId, Json("1"), default);
        await settle();
        await store.SetCompletedAsync(task.TaskId, Json("2"), default);
        await store.SetFailedAsync(task.TaskId, Json("3"), default);
        Assert.False(await store.SetCancelledAsync(task.TaskId, default));
        await settle();

        McpTaskInfo read = (await store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.Completed, read.Status);
        Assert.Equal(1, read.Result!.Value.GetInt32());
        Assert.Null(read.Error);
    }

    [Fact]
    public async Task Cancelling_a_working_task_succeeds_once()
    {
        (IMcpTaskStore store, Func<Task> settle) = Create();
        McpTaskInfo task = await store.CreateTaskAsync(default);

        Assert.True(await store.SetCancelledAsync(task.TaskId, default));
        await settle();
        Assert.Equal(McpTaskStatus.Cancelled, (await store.GetTaskAsync(task.TaskId, default))!.Status);
        Assert.False(await store.SetCancelledAsync(task.TaskId, default));
    }

    [Fact]
    public async Task Asking_for_input_moves_the_task_to_input_required_and_answering_moves_it_back()
    {
        (IMcpTaskStore store, _) = Create();
        McpTaskInfo task = await store.CreateTaskAsync(default);

        await store.SetInputRequestsAsync(task.TaskId, Ask("a", "b"), default);
        McpTaskInfo waiting = (await store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.InputRequired, waiting.Status);
        Assert.Equal(["a", "b"], waiting.InputRequests!.Keys.Order());

        await store.ResolveInputRequestsAsync(task.TaskId, Answer("a"), default);
        McpTaskInfo half = (await store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.InputRequired, half.Status);
        Assert.Equal(["b"], half.InputRequests!.Keys);

        await store.ResolveInputRequestsAsync(task.TaskId, Answer("b"), default);
        McpTaskInfo done = (await store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.Working, done.Status);
        Assert.True(done.InputRequests is null || done.InputRequests.Count == 0);
    }

    [Fact]
    public async Task An_answer_to_a_request_that_was_never_made_changes_nothing()
    {
        (IMcpTaskStore store, _) = Create();
        McpTaskInfo task = await store.CreateTaskAsync(default);
        await store.SetInputRequestsAsync(task.TaskId, Ask("a"), default);

        await store.ResolveInputRequestsAsync(task.TaskId, Answer("zzz"), default);

        McpTaskInfo read = (await store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.InputRequired, read.Status);
        Assert.Equal(["a"], read.InputRequests!.Keys);
    }

    [Fact]
    public async Task Every_answer_raises_the_event()
    {
        (IMcpTaskStore store, _) = Create();
        McpTaskInfo task = await store.CreateTaskAsync(default);
        await store.SetInputRequestsAsync(task.TaskId, Ask("a", "b"), default);
        var seen = new List<string>();
        store.InputResponseReceived += e => seen.Add($"{e.TaskId}/{e.RequestId}");

        await store.ResolveInputRequestsAsync(task.TaskId, Answer("a", "b"), default);

        Assert.Equal([$"{task.TaskId}/a", $"{task.TaskId}/b"], seen.Order());
    }

    [Fact]
    public async Task Asking_for_input_after_the_task_ended_changes_nothing()
    {
        (IMcpTaskStore store, Func<Task> settle) = Create();
        McpTaskInfo task = await store.CreateTaskAsync(default);
        await store.SetCompletedAsync(task.TaskId, Json("1"), default);
        await settle();

        await store.SetInputRequestsAsync(task.TaskId, Ask("a"), default);
        await settle();

        Assert.Equal(McpTaskStatus.Completed, (await store.GetTaskAsync(task.TaskId, default))!.Status);
    }

    [Fact]
    public async Task Timestamps_move_forward()
    {
        (IMcpTaskStore store, Func<Task> settle) = Create();
        McpTaskInfo task = await store.CreateTaskAsync(default);
        await store.SetCompletedAsync(task.TaskId, Json("1"), default);
        await settle();

        McpTaskInfo read = (await store.GetTaskAsync(task.TaskId, default))!;
        Assert.True(read.LastUpdatedAt >= read.CreatedAt);
    }
}

/// <summary>The SDK's own store: the specification of what the contract above means.</summary>
public sealed class InMemoryReferenceStoreTests : TaskStoreContractTests
{
    protected override (IMcpTaskStore Store, Func<Task> Settle) Create() => (new InMemoryMcpTaskStore(), () => Task.CompletedTask);
}

/// <summary>The Kairos store, driven by a worker the way a deployment would be.</summary>
public sealed class KairosStoreContractTests : TaskStoreContractTests
{
    protected override (IMcpTaskStore Store, Func<Task> Settle) Create()
    {
        var rig = new TaskRig();
        return (rig.Store, rig.SettleAsync);
    }
}
