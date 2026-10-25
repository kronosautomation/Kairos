using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kairos.Agents;
using Kairos.Cloud.SelfHosted;
using Kairos.Mcp;
using Kairos.Testing;
using Kairos.Worker;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Kairos.Mcp.TaskStore.Tests;

public sealed class KairosTaskStoreTests
{
    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, InputRequest> Ask(string key) =>
        new() { [key] = InputRequest.ForElicitation(new ElicitRequestParams { Message = "approve " + key }) };

    private static Dictionary<string, InputResponse> Answer(string key) =>
        new() { [key] = InputResponse.FromElicitResult(new ElicitResult { Action = "accept" }) };

    // ---- durability -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_task_survives_a_restart_of_the_server()
    {
        var first = new TaskRig();
        McpTaskInfo task = await first.Store.CreateTaskAsync(default);
        await first.Store.SetInputRequestsAsync(task.TaskId, Ask("approval-1"), default);

        // A new process: nothing is shared with the old one except the database.
        var second = new TaskRig(shared: first.Kairos, time: first.Time);
        McpTaskInfo afterRestart = (await second.Store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.InputRequired, afterRestart.Status);
        Assert.Equal(["approval-1"], afterRestart.InputRequests!.Keys);

        await second.Store.ResolveInputRequestsAsync(task.TaskId, Answer("approval-1"), default);
        await second.Store.SetCompletedAsync(task.TaskId, Json("""{"content":[{"type":"text","text":"ok"}]}"""), default);

        var third = new TaskRig(shared: first.Kairos, time: first.Time);
        Assert.Equal(McpTaskStatus.Completed, (await third.Store.GetTaskAsync(task.TaskId, default))!.Status);
    }

    [Fact]
    public async Task The_answer_to_an_input_request_can_be_read_back_by_another_process()
    {
        var first = new TaskRig();
        McpTaskInfo task = await first.Store.CreateTaskAsync(default);
        await first.Store.SetInputRequestsAsync(task.TaskId, Ask("q"), default);
        await first.Store.ResolveInputRequestsAsync(task.TaskId, Answer("q"), default);

        var other = new TaskRig(shared: first.Kairos, time: first.Time);
        InputResponse? response = await other.Store.TryGetInputResponseAsync(task.TaskId, "q", default);

        Assert.NotNull(response);
        Assert.Equal("accept", response.Deserialize(InputResponse.ElicitResultJsonTypeInfo)?.Action);
        Assert.Null(await other.Store.TryGetInputResponseAsync(task.TaskId, "never-asked", default));
    }

    [Fact]
    public async Task A_plain_task_is_a_run_that_ends_with_the_task()
    {
        var rig = new TaskRig();
        McpTaskInfo task = await rig.Store.CreateTaskAsync(default);
        RunKey run = RunOf(task.TaskId, rig);
        Assert.Equal(RunState.Running, await rig.RunAsync(run));

        await rig.Store.SetCompletedAsync(task.TaskId, Json("""{"x":1}"""), default);

        Assert.Equal(RunState.Completed, await rig.RunAsync(run));
        IReadOnlyList<HistoryEvent> history = await rig.Client.GetOpenedHistoryAsync(run);
        HashChain.Verify(history);
        Assert.Equal("""{"x":1}""", Encoding.UTF8.GetString(((WorkflowCompleted)history[^1]).Result));
    }

    [Fact]
    public async Task A_failed_or_cancelled_plain_task_ends_its_run_as_failed()
    {
        var rig = new TaskRig();
        McpTaskInfo failing = await rig.Store.CreateTaskAsync(default);
        McpTaskInfo cancelling = await rig.Store.CreateTaskAsync(default);
        await rig.Store.SetFailedAsync(failing.TaskId, Json("""{"message":"boom"}"""), default);
        await rig.Store.SetCancelledAsync(cancelling.TaskId, default);

        Assert.Equal(RunState.Failed, await rig.RunAsync(RunOf(failing.TaskId, rig)));
        Assert.Equal(RunState.Failed, await rig.RunAsync(RunOf(cancelling.TaskId, rig)));
        Assert.Equal(nameof(McpTaskCancelledException), ((WorkflowFailed)(await rig.Client.GetOpenedHistoryAsync(RunOf(cancelling.TaskId, rig)))[^1]).ErrorType);
    }

    [Fact]
    public async Task A_run_an_operator_terminated_shows_as_a_failed_task()
    {
        var rig = new TaskRig();
        McpTaskInfo task = await rig.Store.CreateTaskAsync(default);
        await rig.Kairos.FailRunAsync(RunOf(task.TaskId, rig), "stopped by an operator", "ops", rig.Time.GetUtcNow(), default);

        McpTaskInfo read = (await rig.Store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.Failed, read.Status);
        Assert.Contains("stopped by an operator", read.Error!.Value.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private static RunKey RunOf(string taskId, TaskRig rig)
    {
        string encoded = taskId["kmt1_".Length..].Replace('-', '+').Replace('_', '/');
        byte[] bytes = Convert.FromBase64String(encoded.PadRight(encoded.Length + ((4 - (encoded.Length % 4)) % 4), '='));
        return new RunKey(rig.Scope.TenantId, rig.Scope.NamespaceId, new Guid(bytes.AsSpan(32, 16), bigEndian: true));
    }

    // ---- attaching a workflow run ---------------------------------------------------------------------------

    [Fact]
    public async Task An_attached_run_reports_working_then_its_result()
    {
        var rig = new TaskRig();
        rig.Workflow("report", async (ctx, input) =>
        {
            await ctx.WaitForSignalAsync("go");
            return Encoding.UTF8.GetBytes("""{"content":[{"type":"text","text":"report ready"}]}""");
        });
        RunKey run = await rig.StartAsync("report", "r1");
        McpTaskInfo task = await rig.Store.AttachRunAsync(run, default);
        Assert.Equal(McpTaskStatus.Working, task.Status);
        Assert.Equal(task.TaskId, (await rig.Store.AttachRunAsync(run, default)).TaskId); // the same run is the same task

        await rig.Client.SignalAsync(run, "go", []);
        await rig.RunAsync(run);

        McpTaskInfo done = (await rig.Store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.Completed, done.Status);
        Assert.Equal("report ready", done.Result!.Value.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_result_that_is_not_json_is_reported_as_text()
    {
        var rig = new TaskRig();
        rig.Workflow("plain", (ctx, input) => Task.FromResult(Encoding.UTF8.GetBytes("just words")));
        RunKey run = await rig.StartAsync("plain", "p1");
        McpTaskInfo task = await rig.Store.AttachRunAsync(run, default);
        await rig.RunAsync(run);

        McpTaskInfo read = (await rig.Store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal("just words", read.Result!.Value.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_failed_workflow_is_a_failed_task_with_the_error_type()
    {
        var rig = new TaskRig();
        rig.Workflow("bad", (ctx, input) => throw new InvalidOperationException("no stock"));
        RunKey run = await rig.StartAsync("bad", "b1");
        McpTaskInfo task = await rig.Store.AttachRunAsync(run, default);
        await rig.RunAsync(run);

        McpTaskInfo read = (await rig.Store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.Failed, read.Status);
        Assert.Equal("InvalidOperationException", read.Error!.Value.GetProperty("type").GetString());
        Assert.Equal("no stock", read.Error!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Cancelling_an_attached_task_rolls_the_workflow_back_and_reports_cancelled()
    {
        var rig = new TaskRig();
        var ran = new List<string>();
        rig.Activity("reserve", (_, input, _) =>
        {
            ran.Add("reserve");
            return Task.FromResult(input);
        });
        rig.Activity("release", (_, input, _) =>
        {
            ran.Add("release");
            return Task.FromResult(input);
        });
        var agents = new AgentsBuilder().Build();
        agents.RegisterInto(rig.Activities);
        rig.Workflow("trip", agents.Workflow(async (ctx, input) =>
        {
            await ctx.RunSagaAsync(async saga =>
            {
                await ctx.Workflow.ExecuteActivityAsync("reserve", input);
                saga.Compensate("release", "release", input);
                await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "Confirm the trip" }); // a wait that notices a cancel request
            });
            return input;
        }));
        RunKey run = await rig.StartAsync("trip", "t1", [1]);
        McpTaskInfo task = await rig.Store.AttachRunAsync(run, default);
        await rig.RunAsync(run);

        Assert.True(await rig.Store.SetCancelledAsync(task.TaskId, default));
        await rig.RunAsync(run);

        Assert.Equal(McpTaskStatus.Cancelled, (await rig.Store.GetTaskAsync(task.TaskId, default))!.Status);
        Assert.Equal(["reserve", "release"], ran);
        Assert.False(await rig.Store.SetCancelledAsync(task.TaskId, default));
    }

    [Fact]
    public async Task An_attached_task_follows_the_run_through_continue_as_new()
    {
        var rig = new TaskRig();
        rig.Workflow("long", async (ctx, input) =>
        {
            if (input.Length == 0)
            {
                throw ctx.ContinueAsNew(new byte[] { 1 });
            }

            await ctx.WaitForSignalAsync("finish");
            return Encoding.UTF8.GetBytes("""{"generation":2}""");
        });
        RunKey run = await rig.StartAsync("long", "l1");
        McpTaskInfo task = await rig.Store.AttachRunAsync(run, default);
        await rig.RunAsync(run);

        Assert.Equal(McpTaskStatus.Working, (await rig.Store.GetTaskAsync(task.TaskId, default))!.Status);
        RunKey next = (await rig.Kairos.GetCurrentRunAsync(rig.Scope, "l1", default))!.Value;
        Assert.NotEqual(run.RunId, next.RunId);
        await rig.Client.SignalAsync(next, "finish", []);
        await rig.RunAsync(next);

        McpTaskInfo done = (await rig.Store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.Completed, done.Status);
        Assert.Equal(2, done.Result!.Value.GetProperty("generation").GetInt32());
    }

    [Fact]
    public async Task An_attached_task_cannot_be_completed_or_failed_from_outside()
    {
        var rig = new TaskRig();
        rig.Workflow("w", async (ctx, input) =>
        {
            await ctx.WaitForSignalAsync("go");
            return input;
        });
        McpTaskInfo task = await rig.Store.AttachRunAsync(await rig.StartAsync("w", "w1"), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Store.SetCompletedAsync(task.TaskId, Json("1"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Store.SetFailedAsync(task.TaskId, Json("1"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Store.SetInputRequestsAsync(task.TaskId, Ask("x"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Store.ResolveInputRequestsAsync(task.TaskId, Answer("x"), default));
    }

    // ---- input for attached runs: who answers is decided by the caller, not the response ------------------------

    private sealed class FakeInputs : IRunInputProjection, IRunInputResponder
    {
        public List<(RunKey Run, string Key, string AuthenticatedAs)> Answers { get; } = [];

        public string Authenticated { get; set; } = "user:dana";

        public IReadOnlyDictionary<string, InputRequest> PendingInputs(IReadOnlyList<HistoryEvent> history) =>
            AgentHistory.PendingApprovals(history).ToDictionary(
                p => p.ApprovalId, p => InputRequest.ForElicitation(new ElicitRequestParams { Message = p.Action }), StringComparer.Ordinal);

        public ValueTask RespondAsync(RunKey run, string requestKey, InputResponse response, CancellationToken cancellationToken)
        {
            // The identity comes from the authenticated caller. The response body is not consulted for it.
            Answers.Add((run, requestKey, Authenticated));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Pending_approvals_show_as_input_required_and_answers_go_to_the_responder()
    {
        var fake = new FakeInputs();
        var agents = new AgentsBuilder().Build();
        var rig = new TaskRig(new McpTaskStoreOptions { Inputs = fake, Responder = fake });
        agents.RegisterInto(rig.Activities);
        rig.Workflow("approve", agents.Workflow(async (ctx, input) =>
        {
            (await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "Wire 500 EUR" })).ThrowIfNotApproved();
            return input;
        }));
        RunKey run = await rig.StartAsync("approve", "a1", [7]);
        McpTaskInfo task = await rig.Store.AttachRunAsync(run, default);
        await rig.RunAsync(run);

        McpTaskInfo waiting = (await rig.Store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.InputRequired, waiting.Status);
        string key = Assert.Single(waiting.InputRequests!).Key;

        // The client tries to say who it is inside the response; the responder never sees that.
        var pretending = new Dictionary<string, InputResponse>
        {
            [key] = InputResponse.FromElicitResult(new ElicitResult { Action = "accept", Content = new Dictionary<string, JsonElement> { ["approver"] = Json("\"user:boss\"") } }),
        };
        await rig.Store.ResolveInputRequestsAsync(task.TaskId, pretending, default);

        (RunKey answeredRun, string answeredKey, string by) = Assert.Single(fake.Answers);
        Assert.Equal(run, answeredRun);
        Assert.Equal(key, answeredKey);
        Assert.Equal("user:dana", by);
    }

    // ---- isolation --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_namespaces_task_id_is_indistinguishable_from_an_unknown_one()
    {
        var a = new TaskRig(scope: TaskRig.ScopeA);
        McpTaskInfo task = await a.Store.CreateTaskAsync(default);
        var b = new TaskRig(scope: TaskRig.ScopeB, shared: a.Kairos, time: a.Time);

        Assert.Null(await b.Store.GetTaskAsync(task.TaskId, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Store.SetCompletedAsync(task.TaskId, Json("1"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Store.ResolveInputRequestsAsync(task.TaskId, Answer("x"), default));
        Assert.False(await b.Store.SetCancelledAsync(task.TaskId, default));
        Assert.Null(await b.Store.TryGetInputResponseAsync(task.TaskId, "x", default));
        Assert.Equal(McpTaskStatus.Working, (await a.Store.GetTaskAsync(task.TaskId, default))!.Status); // and nothing leaked into the real task
        await Assert.ThrowsAsync<ArgumentException>(() => b.Store.AttachRunAsync(RunOf(task.TaskId, a), default));
    }

    [Fact]
    public async Task Guessed_malformed_or_altered_ids_are_unknown()
    {
        var rig = new TaskRig(new McpTaskStoreOptions { IdKey = RandomNumberGenerator.GetBytes(32) });
        McpTaskInfo task = await rig.Store.CreateTaskAsync(default);
        string flipped = task.TaskId[..^2] + (task.TaskId[^2] == 'A' ? 'B' : 'A') + task.TaskId[^1];

        Assert.NotNull(await rig.Store.GetTaskAsync(task.TaskId, default));
        Assert.Null(await rig.Store.GetTaskAsync(flipped, default));
        foreach (string junk in new[] { "", "kmt1_", "kmt1_!!!", "kmt2_AAAA", Guid.NewGuid().ToString("N"), new string('x', 500) })
        {
            Assert.Null(await rig.Store.GetTaskAsync(junk, default));
        }

        // An id minted without the key (a bare run key) is refused when ids are protected.
        var plain = new TaskRig(shared: rig.Kairos, time: rig.Time);
        McpTaskInfo bare = await plain.Store.AttachRunAsync(RunOf(task.TaskId, rig), default);
        Assert.Null(await rig.Store.GetTaskAsync(bare.TaskId, default));
    }

    [Fact]
    public void A_short_id_key_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new TaskRig(new McpTaskStoreOptions { IdKey = new byte[16] }));
    }

    // ---- confidentiality ----------------------------------------------------------------------------------------

    [Fact]
    public async Task With_client_side_encryption_the_server_holds_no_result_or_request_text()
    {
        var protector = new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral());
        var rig = new TaskRig(protector: protector);
        McpTaskInfo task = await rig.Store.CreateTaskAsync(default);
        const string Canary = "CANARY-5555-secret";
        await rig.Store.SetInputRequestsAsync(task.TaskId, new Dictionary<string, InputRequest> { ["q"] = InputRequest.ForElicitation(new ElicitRequestParams { Message = Canary }) }, default);
        await rig.Store.SetCompletedAsync(task.TaskId, Json($$"""{"content":[{"type":"text","text":"{{Canary}}"}]}"""), default);
        await rig.SettleAsync();

        McpTaskInfo read = (await rig.Store.GetTaskAsync(task.TaskId, default))!;
        Assert.Equal(McpTaskStatus.Completed, read.Status);
        Assert.Contains(Canary, read.Result!.Value.GetRawText(), StringComparison.Ordinal);

        foreach (HistoryEvent e in await rig.Kairos.GetHistoryAsync(RunOf(task.TaskId, rig), default))
        {
            string raw = e switch
            {
                SignalReceived s => Encoding.UTF8.GetString(s.Payload),
                WorkflowCompleted c => Encoding.UTF8.GetString(c.Result),
                WorkflowFailed f => f.Message,
                _ => "",
            };
            Assert.DoesNotContain(Canary, raw, StringComparison.Ordinal);
        }
    }

    // ---- a server that died with work in flight -------------------------------------------------------------------

    [Fact]
    public async Task A_task_nobody_has_touched_for_too_long_is_recorded_as_abandoned_and_stays_that_way()
    {
        var time = new VirtualTimeProvider();
        var options = new McpTaskStoreOptions { AbandonedAfter = TimeSpan.FromMinutes(5), Time = time };
        var rig = new TaskRig(options, time: time);
        McpTaskInfo quiet = await rig.Store.CreateTaskAsync(default);
        McpTaskInfo alive = await rig.Store.CreateTaskAsync(default);

        time.Advance(TimeSpan.FromMinutes(4));
        await rig.Store.SetInputRequestsAsync(alive.TaskId, Ask("q"), default); // an update is a sign of life
        time.Advance(TimeSpan.FromMinutes(2));

        McpTaskInfo abandoned = (await rig.Store.GetTaskAsync(quiet.TaskId, default))!;
        Assert.Equal(McpTaskStatus.Failed, abandoned.Status);
        Assert.Equal("TaskAbandoned", abandoned.Error!.Value.GetProperty("type").GetString());
        Assert.Equal(McpTaskStatus.InputRequired, (await rig.Store.GetTaskAsync(alive.TaskId, default))!.Status);

        // The server comes back and finishes the task it had forgotten: too late, the record already says what happened.
        await rig.Store.SetCompletedAsync(quiet.TaskId, Json("1"), default);
        Assert.Equal(McpTaskStatus.Failed, (await rig.Store.GetTaskAsync(quiet.TaskId, default))!.Status);
    }

    [Fact]
    public async Task Without_the_option_a_quiet_task_is_never_abandoned()
    {
        var time = new VirtualTimeProvider();
        var rig = new TaskRig(new McpTaskStoreOptions { Time = time }, time: time);
        McpTaskInfo task = await rig.Store.CreateTaskAsync(default);

        time.Advance(TimeSpan.FromDays(30));

        Assert.Equal(McpTaskStatus.Working, (await rig.Store.GetTaskAsync(task.TaskId, default))!.Status);
    }

    // ---- many at once ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Many_tasks_created_at_once_are_all_distinct_and_independent()
    {
        var rig = new TaskRig();
        McpTaskInfo[] tasks = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => rig.Store.CreateTaskAsync(default)));
        Assert.Equal(25, tasks.Select(t => t.TaskId).Distinct().Count());

        await Task.WhenAll(tasks.Select((t, i) => i % 2 == 0 ? rig.Store.SetCompletedAsync(t.TaskId, Json(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), default) : Task.CompletedTask));

        for (int i = 0; i < tasks.Length; i++)
        {
            McpTaskInfo read = (await rig.Store.GetTaskAsync(tasks[i].TaskId, default))!;
            Assert.Equal(i % 2 == 0 ? McpTaskStatus.Completed : McpTaskStatus.Working, read.Status);
        }
    }
}
