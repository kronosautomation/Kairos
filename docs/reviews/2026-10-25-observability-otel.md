# Observability F4: OpenTelemetry, GenAI conventions, trace context in history, Aspire (design decisions and open items)

Written 25 Oct 2026. Internal. Plan: `docs/OBSERVABILITY-PLAN.md` (F4). Placement: Apache (`docs/REPO-LAYOUT.md`); packaged dashboards stay in
Kairos.Telemetry (Pro), see `docs/LICENSING.md`. Public page: `docs-site/concepts/observability.md`.

## What was built

- `KairosTelemetry` (ActivitySource `Kairos`) and the existing `KairosMetrics` meter, in Core. `AddKairos` subscribes the host's OpenTelemetry providers
  through `ConfigureOpenTelemetryTracerProvider` / `ConfigureOpenTelemetryMeterProvider` (`OpenTelemetry.Api.ProviderBuilderExtensions`, API only: no SDK
  dependency). A test proves the zero-setup path with a real SDK and no `AddSource`.
- Spans: `kairos.run.start`, `kairos.workflow_task`, `kairos.activity.attempt`, `kairos.timer`, `kairos.signal(.send)`, `kairos.approval(.decide)`, `kairos.run`.
- Metrics: runs started/closed, tasks processed by outcome, replay duration, history events and bytes, activity attempts/retries/duration, lease expiries,
  timers, signals, queue depth (gauge, new `IKairosStore.GetBacklogAsync`), and the existing stuck/faults/store-retries.
- GenAI: `chat {model}` and `execute_tool {tool}` spans and the two client metrics, inside the activity attempt.
- Health: store reachability, worker loops running and progressing (`KairosWorkerPulse`), parked tasks. Registered by `AddKairos`.
- `Kairos.Aspire.Hosting`: `AddKairos(name).WithReference(postgres | database)`; a connection-string resource, not a container. Sample AppHost and app in `samples/Aspire`.

## Decisions

1. **Trace context lives in history as trailing, optional fields** (`WorkflowStarted`, `ActivityScheduled`, `SignalReceived`), encoded only when
   non-empty, the same way `SignalId` was added. Histories written before this keep their exact bytes and hashes (the golden corpus passes unchanged).
   The `SignalReceived` rule (an id is always written when a trace follows) keeps the two optional fields unambiguous; the decoder rejects a
   non-canonical empty trace. No SQL migration: both stores already persist the encoded event body, and the activity task row keeps the encoded
   `ActivityScheduled`. Continue-as-new copies the first run's context, so one trace spans the chain.
2. **No spans from workflow code.** Workflow code replays, so a span started there would repeat. Everything derived from history (timer, signal,
   approval, run) is emitted by the worker from the events a task consumed (those after the last `WorkflowTaskCompleted`), with their recorded
   timestamps, and only after `CompleteWorkflowTaskAsync` returns `Applied`. A faulted, stale or conflicting task emits nothing; a test counts.
3. **The run span is emitted when the run closes**, with its start time taken from the start event, parented to the span that started it. A span cannot
   be held open across processes, and an operator-terminated run (`RunTerminated`) produces no run span (it has no workflow task). Open item.
4. **Sender spans are links, not parents,** on signals and approvals: the run's trace stays one tree, and the sender's request is one click away.
5. **Nothing from a payload, ever, and no switch.** Only ids, names, counts, sizes and outcomes. Failures record the exception type, never the message
   (messages quote data). The identity an agent acts for and approver names/comments are not recorded. OpenTelemetry's GenAI content-capture settings are
   not read. `TelemetryConfidentialityTests` runs a workflow with a canary in every payload and message position (plain and encrypted), reads back every
   tag, tag object, event, link, baggage, status text, metric name/label/value, and the host's exported spans, metrics and log lines, and asserts the canary
   (raw, upper-case, hex, base64, a base64 fragment, URL-escaped) is absent. A control test proves the detector finds a canary in each of those places.
6. **Metric labels are bounded:** workflow type, activity type, outcome, queue. Run and tenant ids appear on spans only (asserted).
7. **Readable metadata, stated honestly:** span attributes include run id, workflow type, build id, activity type, signal and approval ids, and tenant and
   namespace ids. This is the metadata `SECURITY-ARCHITECTURE.md` section 12 already lists as readable. The trace context itself is random ids.
8. **Health check semantics:** a worker loop busy in a long activity is alive; stall means no iteration and nobody busy for `WorkerStallThreshold`.
   A host with `WorkerCount = 0` is not judged on workers.

## Open items

- A server (Milestone 7) must bound the length of trace contexts it accepts on start and signal (they are validated to W3C form by the client helpers
  only); the stores accept any string.
- `tracestate` is not stored (bounded size, no vendor data in history). Baggage is not propagated.
- No span for the time a task waited in the queue; `kairos.queue.depth` shows the backlog, not its age.
- The .NET 11 / Runtime Async build leg was not run for the new code.
- The Aspire sample was built and its resource model tested; it has not been launched against Docker in this session.
