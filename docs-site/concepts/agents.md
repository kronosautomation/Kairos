# AI agents: durable model and tool steps

An agent is a loop: ask a model what to do, do it with a tool, tell the model what happened, repeat. It is slow, it costs money on every
model call, it calls systems that must not be called twice, and it often waits for a person. That is exactly the shape of work Kairos
already makes durable, so `Kairos.Agents` turns each part of the loop into a recorded step:

| Step | Method | What is recorded |
|---|---|---|
| Ask the model | `ctx.ChatAsync(model, messages, tools)` | The conversation sent, the model's reply, the tokens used. |
| Run a tool | `ctx.InvokeToolAsync(call)` | The arguments, the policy decision, who approved it if anyone, the tool's answer. |
| Ask a person | `ctx.RequestApprovalAsync(request)` | The request, and the decision with the approver's identity. See [Approvals](approvals.md). |
| Undo earlier work | `ctx.RunSagaAsync(...)` | The undo steps and how far the rollback got. See [Sagas and compensation](sagas.md). |

Because they are ordinary workflow steps, everything in [Durable execution and replay](durable-execution.md) applies: the run survives
a crash, a deployment or a week of waiting, and **replaying it never calls the model or a tool again**. The recorded answer is returned.

## Setting up

Describe your models and tools once, build the runtime, and register its activities and your workflow:

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#configure)]

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#host)]

`agents.Activities` are the steps' activities. Register them on every worker that can run the workflow, in embedded mode with
`AddActivities(agents.Activities)`. Models are named, so a workflow says `"support-model"` and the deployment decides which provider and
deployment stands behind that name; you can resolve the model per run (for example a different deployment per namespace) by passing a
function to `AddModel`.

Models are used through `Microsoft.Extensions.AI`'s `IChatClient`, so any provider that has one works, and `Kairos.Agents` depends on no
model vendor's SDK. Pass the provider's own client. Do not wrap it in automatic function invocation: tools must run through
`InvokeToolAsync`, otherwise they run outside the record.

## The loop

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#agent-loop)]

`ChatAsync` returns an `AgentReply`: the messages to append to the conversation, the text, any tool calls, and the token counts.
`InvokeToolAsync` returns the tool-role message to append, **including when the call did not run** (it was refused by policy, or a person
said no): the model is told, and carries on.

If the conversation will be long, check `ctx.Workflow.ShouldContinueAsNew` and hand over to a fresh run with a summary; see
[Long-running workflows](continue-as-new.md).

## Model steps

- **Recorded once.** The reply and the token usage are written to the history when the step completes. `AgentHistory.TotalUsage(history)`
  adds them up for a run.
- **Retried like any activity.** A rate limit, a timeout or a network error is retried with backoff (three attempts by default; set it
  with `WithModelRetry`). A model alias nobody registered fails at once, because retrying cannot help.
- **A crash can cost one repeated call.** If the process stops after the provider answered but before the answer was recorded, the step
  runs again and the model is called again. Every attempt of one step carries the same idempotency key, passed to the client in
  `ChatOptions.AdditionalProperties["kairos.idempotency_key"]`, for providers and wrappers that can use it to avoid a second charge.
- **Streaming.** Register an `IAgentObserver` with `UseObserver` to show text to a person while the model is still writing it. Only the
  final reply is recorded, and an observer that throws never fails the step.
- **Redaction.** `UseRedactor` receives every piece of text before it is recorded (messages, tool arguments and results, approval
  details). Because the model call is made from the recorded step, the model sees the redacted text too.
- **What can be recorded.** Text, reasoning text, tool calls and tool results. Other content types (images, files) are refused with a
  `NotSupportedException` when you send them, and become a visible placeholder when a model returns them, never silently dropped.
- **Settings.** `AgentChatOptions` carries temperature, maximum output tokens and a seed. A model call has a timeout (two minutes by
  default).

## Tool steps

A tool is a name, a description for the model, a JSON schema for its arguments, what it may do, and the code behind it:

[!code-csharp[](../../samples/AgentSamples/SupportAgent.cs#tools)]

Each call is an activity with an idempotency key made of the run and the step number, available as `call.IdempotencyKey`. Pass it to the
service you call: a retry after a lost answer then changes nothing. This is the same guarantee as for any
[activity](retries.md#idempotency-keys): the tool *runs* at least once and its *effect* happens once if the service honours the key.

What the model sees when something goes wrong:

| Situation | What happens |
|---|---|
| The tool throws `ToolException` | Its message goes to the model as the result, with no retry. Use it for errors the model can act on ("order 9 does not exist"). |
| The tool throws anything else | The step is retried (three attempts by default, `WithToolRetry`). If it keeps failing, the model is told "the tool failed" with the exception type, not its message. |
| The model asks for a tool that does not exist | The model is told so. |
| Policy refuses the call, or a person rejects it | The model is told, and the tool never runs. See [Tool policy and credentials](tool-policy-and-credentials.md). |

`InvokeToolWithOutcomeAsync` returns the same message together with a `ToolStepOutcome` (`Completed`, `Failed`, `Denied`, `Rejected`,
`Expired`), for workflows that react differently to a rejection than to an error.

## Things to know

- **Arguments are untrusted.** A model can be talked into asking for anything. Validate arguments in the tool, and let policy and
  approvals, not the prompt, decide what is allowed.
- **Everything recorded is sealed** when [payload encryption](history.md) is on: prompts, replies, tool arguments and results. Activity names, including the names of your
  own activities such as undo steps, are not payloads and stay readable; do not put secrets in them.
- **Workflow rules still apply.** The agent loop is workflow code: no clock, no random numbers, no I/O (see [Determinism](determinism.md)). The build-time analyzers check agent workflows too.
- **History grows with the conversation.** Each model call and tool call adds events; long agents should continue as new.

Already have an agent written with Microsoft Agent Framework? See [Agent Framework agents, made durable](agent-framework.md): the same steps, approvals, policy and budgets, without rewriting the agent.
