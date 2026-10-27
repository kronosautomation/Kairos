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

A denial always stands, and so does a rule of a policy engine that matches the call; `AllowWithoutApproval` only lifts the free policy's default hold. When the host starts, every tool nobody classified is logged by name.
Classification lives in the registration, not in the agent.

## Approvals

[!code-csharp[](../../samples/AgentFramework/SupportAgentSample.cs#approve)]

The approval is the one [Approvals](approvals.md) describes: the person is whoever your application authenticated, verified before the decision counts, and the request shows the exact tool and arguments (redacted, if you
configured a redactor). The run holds no compute while it waits.

## Budgets, policy, redaction, encryption

They are the ones native Kairos agents use, set up in the same place:

```csharp
af.ConfigureAgents(agents => agents.UseModelCallGuard(budget).UseRedactor(Redact).UseCredentials(issuer).UseApproverVerifier(verifier));
```

- **A budget** is an `IModelCallGuard`. It is asked before every model call, told what the call cost afterwards, and gives its reservation back if the step fails for good. A refused call pauses the run for a person to raise the budget, or ends it.
  Say which model each agent uses with `Model("alias", maxOutputTokens)` (or give the agent's options a `ModelId`), so there is something to reserve and price by.

  [!code-csharp[](../../samples/AgentFramework/SupportAgentSample.cs#budget)]

- **Redaction** is applied to every piece of text before it is recorded, **including the text inside the agent's session**, which is Agent Framework's own JSON. The redactor walks the structure and rewrites the text values; it refuses a session
  that keeps state it does not know (a memory provider's, say) unless you declare that state free of text with `AllowSnapshotState("key")`, because a redactor that skipped what it did not understand would promise more than it does.
- **Encryption.** Everything the steps record, the session included, is sealed on the client like any other payload. The server and the database see ciphertext.
- **Token usage** is recorded with each step, so `GetUsageAsync`, `AgentHistory.TotalUsage` and the cost views read it as they read native agents'.
- **The paid policy engine** and the Policy service for workers without a database connection plug into the same builder and apply to these agents unchanged.

## Conversations

One conversation is one run, and each message is a signal to it. A session id is the run's workflow id, so a conversation can be picked up from another process. Messages are answered once: send again with the same message id and the conversation answers it once.
When the history grows long the run hands over to a fresh one, carrying the session. A conversation ends when it is closed, or after `ConversationIdleTimeout`; a message to an ended conversation is refused with a clear error.

`DurableAIAgent.RunAsync` waits for the answer by looking at the run's history, quickly at first and then less often. If you would rather not wait, `SendAsync` returns at once and `TryReadAsync` asks whether the answer is there.

A session is carried whole by every step, so a long conversation moves more bytes as it grows. `MaxSnapshotBytes` (512 KiB by default) fails a step with a clear error instead of letting it grow without limit; Agent Framework's own compaction strategies
run inside the step and keep a session small.

## Agents that call agents

Offer a registered agent to another as a tool with `DurableAgentFunction.For("researcher")` (or `durableAgents.Get("researcher").AsAIFunction()`). A call to it starts the nested agent as a run of its own: its tools go through the tool policy, its approvals appear
in the approvals inbox, its model calls count against the budgets, and the calling agent waits without holding compute. An agent run by Agent Framework's own `AsAIFunction()` is refused at registration, because its tools would run
outside the policy; allow it by name with `AllowOpaqueNesting("name")` if that is what you want.

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
