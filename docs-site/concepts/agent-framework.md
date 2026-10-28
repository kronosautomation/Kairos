# Microsoft Agent Framework agents, made durable

You already have an agent written with [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/): instructions, a chat client and some tools. `Kairos.Agents.AgentFramework` makes it
durable without changing how it is written. Each model call and each tool call becomes a recorded step, the conversation survives a crash or a redeploy, a call that needs a person waits for them without
holding a thread or a process, and a step that was recorded is **never run again**, whoever picks the run up.

## One line

[!code-csharp[](../../samples/AgentFramework/SupportAgentSample.cs#agent)]

[!code-csharp[](../../samples/AgentFramework/SupportAgentSample.cs#register)]

`AddAgentFramework` follows `AddKairos`, in embedded mode or in a worker that connects to a server. The agent must have a `Name`: it finds the agent after a restart, so it must not change. What you get back is an
ordinary `AIAgent`, so everything that takes one (Agent Framework workflows, hosting layers, `AsAIFunction()`) works with it:

[!code-csharp[](../../samples/AgentFramework/SupportAgentSample.cs#use)]

For Agent Framework *workflows* (graphs of executors) see [workflows, made durable](agent-framework-workflows.md).

The same agent is available as a keyed `AIAgent` service (the key is its name), as `DurableAgents.Get("support")`, and for an agent you already hold, `agent.AsDurable(durableAgents)`.

## What is recorded

| Agent Framework does | Kairos records | After a crash |
|---|---|---|
| One model call | A step: the conversation, the reply, the tokens used | Never repeated once recorded. If the process stopped before the reply was recorded, that one call may be made again, with the same idempotency key. |
| A tool call | A tool step: the arguments, the policy decision, who approved it, the result | Never repeated once recorded. Before that, retried with the idempotency key the tool is given. |
| A call that needs approval | An approval: bound to this exact call, who decided, when | Waits, with nothing running, for as long as it takes. |
| The conversation (the agent's session) | Carried from step to step | Restored from the last recorded step. |

Replaying a run never calls the model or a tool. The agent runs one model call at a time, inside an activity; a small Kairos workflow runs the loop around it. Agent Framework code is never run during replay.

## Which tools run on their own

Agent Framework asks only whether a *call needs approval*. Kairos also asks whether a tool *changes the outside world*, because a tool that does is held for a person unless something allows it. So:

| The tool is | A call |
|---|---|
| marked `ReadOnly("name")` at registration | runs without asking, with a credential that cannot write |
| wrapped in `ApprovalRequiredAIFunction` by its author | waits for a person; no policy can waive what the author asked for |
| marked `AllowWithoutApproval("name")` | runs without asking, as Agent Framework would run it; the decision is recorded with every call so an audit shows it was the application's owner who let it through |
| marked `RequireApproval("name", requirement)` | waits for the people the requirement names |
| none of these | is treated as changing the outside world: it waits for approval unless a policy allows it |

A denial always stands. When the host starts, every tool nobody classified is logged by name. Classification lives in the registration, not in the agent.

### `AllowWithoutApproval` never outranks a policy rule

`AllowWithoutApproval` is the application owner's statement that a tool may run without asking. It lifts one thing: the **default hold** the free policy puts on a tool that changes the outside world, or the default of a policy engine
(a decision whose rule has a name in parentheses, such as `(default)`). It does not outrank a **rule** that matched the call: an application owner's mark must not override what an operator's policy says about this exact call. A rule that asks for approval
still asks; a rule that denies still denies.

Here the owner marks `issue_credit` as allowed, and the operators' policy has a rule for credits over 100:

[!code-csharp[](../../samples/AgentFramework/CreditAgentSample.cs#credit-rule)]

[!code-csharp[](../../samples/AgentFramework/CreditAgentSample.cs#credit-register)]

| The agent calls | What decides | Result |
|---|---|---|
| `issue_credit(C-1, 20)` | no rule matched; the policy's default hold applies, and the owner's mark lifts it | runs without asking; the audit names `af.allow-without-approval` |
| `issue_credit(C-1, 500)` | the rule `credit-limit` matched and asks for finance | waits for finance; the owner's mark changes nothing |
| a rule that denies the call | the rule | refused; the model is told so |

The sample's tests run both calls.

## Approvals

[!code-csharp[](../../samples/AgentFramework/SupportAgentSample.cs#approve)]

The approval is the one [Approvals](approvals.md) describes: the person is whoever your application authenticated, verified before the decision counts, and the request shows the exact tool and arguments (redacted, if you
configured a redactor). The run holds no compute while it waits.

## Budgets, policy, redaction, encryption

They are the ones native Kairos agents use, set up in the same place:

[!code-csharp[](../../samples/AgentFramework/SupportAgentSample.cs#configure-agents)]

- **A budget** is an `IModelCallGuard`. It is asked before every model call, told what the call cost afterwards, and gives its reservation back if the step fails for good. A refused call pauses the run for a person to raise the budget, or ends it.
  Say which model each agent uses with `Model("alias", maxOutputTokens)` (or give the agent's options a `ModelId`), so there is something to reserve and price by.

  [!code-csharp[](../../samples/AgentFramework/SupportAgentSample.cs#budget)]

- **Redaction** is applied to every piece of text before it is recorded, **including the text inside the agent's session**, which is Agent Framework's own JSON. The redactor walks the structure and rewrites the text values; it refuses a session
  that keeps state it does not know (a memory provider's, say) unless you declare that state free of text with `AllowSnapshotState("key")`, because a redactor that skipped what it did not understand would promise more than it does.
- **Encryption.** Everything the steps record, the session included, is sealed on the client like any other payload. The server and the database see ciphertext.
- **Token usage** is recorded with each step, so `GetUsageAsync`, `AgentHistory.TotalUsage` and the cost views read it as they read native agents'.
- **The paid policy engine** and the Policy service for workers without a database connection plug into the same builder and apply to these agents unchanged.

## Conversations

One conversation is one run, and each message is a signal to it. A session id is the last part of the run's workflow id (`af/{agent}/{session id}`), so a conversation can be picked up from another process. Messages are answered once: send again with the same message id and the conversation answers it once.
When the history grows long the run hands over to a fresh one, carrying the session. A conversation ends when it is closed, or after `ConversationIdleTimeout`; a message to an ended conversation is refused with a clear error.

`DurableAIAgent.RunAsync` waits for the answer without polling. It reads what the run has written since the message was sent and, when the answer is not there yet, asks the engine to say when the run is written to again: the wait ends at the
write that has the answer, and `ReplyWait` (30 seconds by default) is only the longest silence before it looks anyway. A look costs what is new, not what the conversation holds. If you would rather not wait, `SendAsync` returns at once and `TryReadAsync`
asks whether the answer is there; a `DurableTurn` is looked at by one caller at a time.

### Keeping long conversations small

A worker loads the whole session for every model call, so a long conversation costs more with every message. What history records is kept far smaller than that: a step names the session an earlier step recorded instead of carrying it again,
the session is recorded whole every `FullSnapshotEvery` steps (16 by default) and otherwise as the change since the step before, and workers remember the sessions they ran (`SnapshotCacheBytes`). A worker that does not have the session, after a deploy
or a move to another worker, is sent it once. This keeps what grows with the length of a conversation from growing with the square of it.

It does not make the session itself smaller, and `MaxSnapshotBytes` (512 KiB by default) fails a step with a clear error instead of letting a session grow without limit. **For conversations that go on, keep the session small with Agent Framework's own
compaction, and use it by default.** Put it in the history the session keeps, so that what is *stored* shrinks, not only what the model is shown:

[!code-csharp[](../../samples/AgentFramework/ConversationSample.cs#compaction)]

[!code-csharp[](../../samples/AgentFramework/ConversationSample.cs#compacted-agent)]

The reducer runs when a message is added, inside the step, so it is recorded like everything else and a step that is run again compacts the same way. Old tool output collapses first (a line per call is kept), and then the oldest turns leave the
session. Tune the numbers to the model's context window and to how much of the past the agent needs. A conversation that must remember more than a window of turns should summarise instead (`SummarizationCompactionStrategy`), with the caveat Agent Framework
gives for it: the summary replaces the original messages, so use a model you trust as much as the main one.

Two things to know. Agent Framework's `CompactionProvider` (a context provider) shortens what the model is shown for one call, but it keeps its own bookkeeping in the session, which makes the session larger, not smaller; for a durable conversation put the
reducer in the history provider as above. And a conversation kept to a window forgets what left it: if something must never be forgotten, keep it in the agent's instructions or in a store a tool reads, not in the conversation.

## Agents that call agents

Offer a registered agent to another as a tool with `DurableAgentFunction.For("researcher")` (or `durableAgents.Get("researcher").AsAIFunction()`). A call to it starts the nested agent as a run of its own: its tools go through the tool policy, its approvals appear
in the approvals inbox, its model calls count against the budgets, and the calling agent waits without holding compute. An agent run by Agent Framework's own `AsAIFunction()` is refused at registration, because its tools would run
outside the policy; allow it by name with `AllowOpaqueNesting("name")` if that is what you want.

### Stopping a conversation, and the agents it asked

[!code-csharp[](../../samples/AgentFramework/ConversationSample.cs#stop)]

A nested agent does not outlive the run that asked it:

- **Cancelling** a conversation (`CancelAsync`) stops it at its next step. The model call or tool call in flight finishes and is recorded; a conversation waiting for a message, for an approval, or for a nested agent stops at once. Each nested agent it is waiting
  for is asked to stop, and asks the agents it is waiting for in turn. The request is the reserved cancel signal with a fixed id, so asking twice, or a worker dying while the rollback runs, still reaches each nested agent once. The runs end as failed with the
  cancellation, and what they undid (the nested agents they asked) is in their history.
- **Terminating** a run (`TerminateAsync`, or an operator in the dashboard) closes it from outside and gives its workflow no chance to cancel anything. `TerminateAsync` asks the nested agents first. A nested agent that is missed, because the run was terminated
  some other way, looks now and then whether the run that asked it is still open (first after `ParentWatchInterval`, one minute by default, then less often, up to a quarter of an hour) and stops when it is not. `null` turns the look off.
- A conversation recorded before this was added does not stop on a cancel; only the runs started after it do.

Nested agents are asked to stop and the watch is made by the adapter's own steps, which call Kairos with the worker's client. A worker that only connects outbound to a server needs a client that may start and signal runs of its tenant for nested agents
to work at all (starting one needs it too).

## Agents that cannot be run one call at a time

An agent that is not a `ChatClientAgent` (an A2A agent, a custom one), one wrapped in `LoopAgent` or `ToolApprovalAgent`, and one with tools that run at the model provider (hosted web search, hosted MCP servers) cannot be cut into steps. Registration refuses it and names the
option: `AllowTurnLevel()`. Such an agent is recorded one turn at a time: a recorded turn is never run again, but a crash during a turn runs that whole turn again, and its tools run inside the turn, outside the tool policy. The approvals it asks for
are still turned into Kairos approvals.

## When the agent changes under a run

A run is pinned to the definition of its agent (instructions, tools, schemas, model) when it starts. If a worker has a different one, nothing is called: a person is asked whether the run may continue on the new definition. The approval names the person your
approver verifier established, and an audit shows it (`AgentFrameworkAudit.Read`). `OnDefinitionChange(requirement)` says who may decide.

## What to know

- Context providers, memory, compaction and middleware run inside the step. A step that is run again runs them again, so a provider that writes should be safe to run twice.
- Tools that an agent receives at run time from a context provider cannot be found by a worker after a restart; register such tools with the agent.
- Streaming is not durable: `RunStreamingAsync` returns the recorded answer whole.
- The agent's `Name`, tool names and schemas are part of what is recorded; renaming a tool is a change of definition.
- Kairos pins the Agent Framework version it is tested against. Tests of the package check what it relies on, so an upgrade that changes it fails in the package's build, not in your production.

## Testing

Run the agent against a scripted `IChatClient` and the in-memory store, as the sample's tests do; see [Testing agents](testing-agents.md). Nothing here needs a model account.
