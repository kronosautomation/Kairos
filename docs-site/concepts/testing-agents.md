# Testing agents

A test that calls a real model is slow, costs money and gives a different answer tomorrow. `Kairos.Testing` provides stand-ins for the
model that answer from a recording, so an agent's behaviour (its tool calls, its approvals, what it does when the provider fails) can be
tested exactly and repeatedly.

## Recorded model fixtures

A `ModelFixture` holds exchanges: the conversation that was sent, and what the model answered. `FixtureChatClient` plays them back:

[!code-csharp[](../../samples/AgentSamples.Tests/AgentSamplesTests.cs#fixture)]

- **It matches on the conversation.** The same messages get the same answer. A conversation nobody recorded throws
  `FixtureMissException`, so a test can never silently reach a real model or pass by accident.
- **Several exchanges for one conversation play in order**, and the last one repeats. That is how you script "the provider rate-limits
  once, then answers", and it tolerates a step that legitimately runs twice after a crash.
- **Failures are first-class.** `AddError` records a rate limit, a timeout or a malformed answer, and the agent's retry behaviour runs
  for real.
- **A refusal is just a reply**, with a finish reason such as `content_filter`, that the workflow can branch on.
- **`CallCount` counts model calls.** Replaying a workflow must not call the model again: assert that the count equals the number of
  distinct steps.

Save a fixture with `fixture.Save(path)`, check the file in, and load it with `ModelFixture.Load(path)`.

## Recording from a real model

Wrap the real client in `RecordingChatClient`, run the scenario once by hand, and save what it recorded. The recorder passes every call
through and keeps the exchange; later runs use `FixtureChatClient` over the saved file. Re-record when your prompts change: the
conversation is the key, so a changed prompt is a different conversation.

## Scripted models

When a test is about the workflow and not the model's wording, `ScriptedChatClient` takes a function from the conversation to a reply.
It also records the idempotency key of every call, so a test can check that the retries of one step used the same key.

## What to assert

| Property | How |
|---|---|
| Replay never calls the model | `CallCount` equals the number of distinct model steps after the run replays several times. |
| A tool takes effect once | Make the tool record its `call.IdempotencyKey`, fail it after the effect, and check one effect for one key. |
| A risky tool waits for a person | Run until the run is idle, check `AgentHistory.PendingApprovals`, then approve or reject as a named identity. |
| Wrong approvers are ignored | Approve as someone outside the requirement and check the run is still waiting. |
| A rollback completes | After a failure, `SagaHistory.CheckCompensationsComplete(history)` returns `null` and every undo's effect exists once. |

The simulator kit in `Kairos.Testing` (virtual clock, fault-injecting store, crash-at-every-step helpers) works with agent workflows the
same way it does with plain ones; see [Determinism](determinism.md) for what workflow code may do.
