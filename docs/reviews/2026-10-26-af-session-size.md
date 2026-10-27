# Agent Framework follow-up: what a conversation costs in history

Written 26 Oct 2026. Internal. Follows `docs/reviews/2026-10-26-agent-framework.md` ("What the numbers say: every step carries the whole session"). Code: `src/Kairos.Agents.AgentFramework/{SnapshotCodec,SessionChain,StepActivities,AgentFrameworkDriver,Wire}.cs`, commit `3ebec1c`.

## What was asked, and what was done

1. **Stop storing the session twice per step.** A step's request no longer carries the session. It names it: `SnapshotHash` (the content hash of the canonical JSON of the session) and `SnapshotFrom` (the step, by idempotency key, whose result recorded it). The worker finds the session in a
   byte-bounded cache (`SnapshotCacheBytes`, LRU, keyed by tenant + namespace + hash, so a session is found by *what it is* and not by the run: a hand-over to a fresh run still finds it). A worker that does not have it (another worker ran the last step, a deploy, the cache dropped it) answers
   `need-snapshot` without calling anything, and the workflow sends the whole session once and asks again. The hash is checked wherever a session is put together.
2. **Deltas against a content-addressed previous snapshot, a full one every N steps.** A result carries the session whole every `FullSnapshotEvery` steps (default 8) and otherwise only the change against the session named by `BaseHash`, **when the change is smaller**
   (the second step of a conversation is recorded whole because the session is still smaller than its own change). A change is a small JSON patch: set / equal / object members / array "drop `f` from the front, keep `k`, append the tail". The last operation is what
   makes a conversation kept to a window (below) cheap. The driver keeps the last whole session and the changes since, and puts the session together only when it has to travel (the `need-snapshot` answer, a hand-over); the result is compared with the hash the step recorded.
3. **Agent Framework compaction as a documented default.** See "Compaction" below: it is the history provider's reducer, not `CompactionProvider`, and the documentation says why.
4. **Corpus.** v4 still replays (the first protocol is kept for runs recorded by the first release; the new shape is taken by a run only when it *starts*, behind the patch `kairos.af.v5`); `Corpus/v5` holds the new shapes: every v4 scenario again, a step that had to be sent the session (`af-need-snapshot`), and a conversation
   recorded whole every third step and as changes in between (`af-snapshot-changes`). Replay of a v4 history on the new code is byte-for-byte: the new wire fields are omitted when they have their default value.

## The figures

Same conversation as the first review (`MeasurementTests`): a support agent, per message two lookups (about 4 KB and 2 KB of JSON) and a short answer, three steps per message, history sealed with client-side encryption. Before = the first release (`e75671b`); after = `3ebec1c`.

| | before | after (no compaction) | after, compaction as documented |
|---|---|---|---|
| **20 messages**: total recorded | 14,374,620 B (718,731 per message) | **1,641,959 B (82,097 per message)** | 2,102,965 B (105,148 per message) |
| 20 messages: session after the 20th | 233,956 B | 163,876 B | 163,493 B |
| 20 messages: bytes recorded for the 20th message | 1,385,446 B | 259,602 B (it holds a whole session) | 72,624 B |
| **100 messages**: total recorded | 352,619,461 B (3,526,194 per message) | **25,774,362 B (257,743 per message)** | **14,290,476 B (142,904 per message)** |
| 100 messages: session after the 100th | 1,169,803 B | 819,403 B | **163,503 B** (flat from message 20 on) |
| 100 messages: bytes recorded for the 100th message | 7,000,527 B | 1,195,470 B (a whole session) | **72,640 B** |
| 100 messages: bytes recorded for the 50th message | 3,491,086 B | **37,534 B** (no whole session in it) | 72,635 B |

What the table says, plainly:

- Without compaction the history grew with the **square** of the conversation (every step wrote the session twice); it now grows with the **conversation plus one whole session every 8 steps**. For 100 messages that is 352.6 MB → 25.8 MB (14×). A message that does not hold a whole session costs 37.5 KB whatever the length of the conversation; the
  messages that hold one cost a whole session, and that is the remaining growth: one whole session every 8 steps, each as large as the session was at that point.
- With the documented compaction the history **stops growing**: the session stops at 163 KB and each message costs 72.6 KB for the rest of the conversation. At 100 messages that is 14.3 MB, about half of the uncompacted 25.8 MB; at **20 messages compaction costs more** (2.10 MB against 1.64 MB), because until the window fills the reducer only
  rewrites older tool output, which makes the changes larger. Compaction is for conversations that go on; the documentation says so.
- The session itself shrank from 1,169,803 B to 819,403 B only because it is now canonical compact JSON (no whitespace, members in order); the content is the same.

Sensitivity to the interval (100 messages, no compaction), so that the default can be judged: 

| `FullSnapshotEvery` | 100 messages, no compaction: total recorded | per message |
|---|---|---|
| 4 | 47,218,691 B | 472,186 B |
| **8 (default)** | **25,774,362 B** | 257,743 B |
| 16 | 14,465,501 B | 144,655 B |
| 32 | 9,390,053 B | 93,900 B |

A larger interval records fewer whole sessions; the cost is that a read applies up to N-1 changes (microseconds) and that a worker without the session is sent it from the last whole one. 16 would be a better default than 8 for sessions this size. I kept 8, which is what the other figures were taken with; the choice is yours.

## Compaction: what I tried, and why the documented default is the history provider's reducer

Agent Framework 1.24.0 has two ways to compact. I measured both.

| | 20 messages: session after | what it did |
|---|---|---|
| `CompactionProvider` (a context provider; pipeline of tool-result collapse and sliding window) | 508,281 B | **Made the session larger** than without it (163,876 B at 20 messages, in the new protocol): it shortens what the model is shown for one call, but keeps its own bookkeeping of the message groups in the session, so the messages are there twice. Not a way to keep a durable session small. |
| `InMemoryChatHistoryProvider` with `ChatReducer = pipeline.AsChatReducer()`, `ReducerTriggerEvent = AfterMessageAdded` | bounded (82 KB at a window of 10 turns in the first trial, 163 KB at the documented 20) | Reduces the history the **session keeps**, so what each step records shrinks, and so does what the model is shown. |

The default is the second: collapse old tool output once it adds up to more than 8,000 tokens, and keep the last 20 turns (at least 10). It is `samples/AgentFramework/ConversationSample.cs` (`#compaction`), quoted by the documentation page, compiled by the sample project and **measured as it is written**: the test project includes that file.
The documentation also says what compaction costs: a conversation kept to a window forgets what left it, and a summarising strategy replaces messages with a model's summary (Agent Framework's own security note applies).

The array change gained an operation for this ("drop `f` from the front"): a window that slides drops its oldest turn as it gains a new one, and without it every step of a full window recorded the whole session.

## How it is proved

- `SessionSizeTests` (25): the canonical form (member order, text that survives being read and written again, a polymorphic tag stays first), 1,600 random pairs of sessions (a change applied to the session before gives the session after, and applying changes nothing it was given), the window case, changes that do not fit are refused; the chain refuses a whole session that is not
  the one its hash names, a change against a session it does not hold, and finds a change that does not make the session its hash names; the cache is bounded by bytes, drops the least recently used, does not keep what cannot fit, and is scoped by tenant and namespace; through conversations: a step names the session and does not carry it, whole every few steps and changes in between, what the agent sees is the whole conversation however it was recorded, a worker without the
  session is sent it once, a hand-over carries the session whole once and the next run goes on by reference, the worker answers `need-snapshot` and calls nothing, refuses a session that is not the one its hash names, serves a first-protocol step in the first protocol, and refuses a session over the limit by name.
- `CorpusTests`: v4 (10 histories) and v5 (15) are verified, replayed, and ask for nothing new.
- All 235 Agent Framework tests (the final run), 338 core and 193 persistence tests pass; the 150 Agent Framework tests that existed before ran unchanged on the new protocol (they use it by default), including the crash-at-every-step matrices.
- Mutation checks: see `docs/reviews/2026-10-26-af-cancel-children.md` (one pass over the new code of all three follow-ups).

## Things to know

- The change from the first protocol is by **patch**, not by version: a run started now takes `kairos.af.v5`; a run recorded before has no marker and keeps the first protocol for the rest of its life, a hand-over included (the fresh run is a new run and takes the new protocol).
- The cache is per process, so a deploy or a worker restart costs one `need-snapshot` per conversation that is active, and one whole session in history. The alternative, asking the store for the session, is not available to a worker that only connects outbound.
- `MaxSnapshotBytes` still bounds the session, and now counts UTF-8 bytes of the canonical form for the new protocol (the first protocol counted characters).
- A client that reads a step's result reads all of it, so a look that finds a step with a whole session reads the whole session. That is why the first look after an answer averages about 520 KB in the waiting note: about three messages in eight have a whole session in one of their steps.
