# Observability: traces, metrics and .NET Aspire

Kairos emits OpenTelemetry **spans** and **metrics** for runs, tasks, activities, timers, signals and approvals. `AddKairos` subscribes
the OpenTelemetry providers of the host to them, so in an application that already exports telemetry (a .NET Aspire service, for
example) runs show up with no further setup. Without OpenTelemetry nothing is recorded and nothing is paid for.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())
    .UseOtlpExporter();                  // no AddSource("Kairos"), no AddMeter("Kairos")

builder.Services.AddKairos(options => options.UsePostgres(connectionString));
```

If you prefer to subscribe yourself, set `options.RegisterTelemetry = false` and add the source and the meter, both named `Kairos`
(`KairosTelemetry.SourceName`, `KairosMetrics.MeterName`).

## Spans

| Span | Kind | What it covers |
|---|---|---|
| `kairos.run.start` | producer | Starting a run. A child of whatever is current, for example the HTTP request that asked for it. |
| `kairos.workflow_task` | consumer | One workflow task: replay plus running the workflow code to its next wait. |
| `kairos.activity.attempt` | consumer | One attempt of an activity. Retries are siblings. |
| `kairos.timer` | in-process | A timer, from when it was started to when it fired. |
| `kairos.signal` and `kairos.signal.send` | consumer, producer | Delivery of a signal, and the call that sent it (linked to each other). |
| `kairos.approval` and `kairos.approval.decide` | consumer, producer | An approval decision as it reached the run, and the call that sent it. |
| `kairos.run` | in-process | The whole run, from its start to its closing event, with the outcome (`completed`, `failed`, `continued`). |

Spans carry ids, names, counts and outcomes: run id, workflow type, build id, activity type and attempt number, timer duration, signal
or approval id, the number of events in the history. Tenant and namespace ids are included as well, since they are what you filter on in
a shared environment.

Timers, signals, approvals and the closing `kairos.run` span are produced from history, with the times recorded there. They are
emitted once, when the task that consumed them is recorded: a workflow that replays many times does not report the same timer many
times.

### One trace across processes and restarts

The span that starts a run is stored in the run's history. Every workflow task of the run continues that trace, whichever process
runs it and however long after the start. The span of the task that schedules an activity is stored with the activity, so every
attempt, retry and re-run after a crash is a child of it. A signal stores the span that sent it, and the span that receives it links
back. The trace therefore does not depend on anything held in memory, and it survives restarts and deployments.

## What a span never carries

No payload, in any form: not workflow inputs or results, not activity inputs or results, not signal payloads. Failures are recorded
with the exception **type** only, never its message, because a message can quote the data it was about. Nothing that identifies a person
is recorded either: not who an agent acts for, not an approver's name or comment.

There is no setting that turns content capture on, including the OpenTelemetry GenAI content-capture options: they do not apply to
Kairos spans. A test runs workflows with a known secret in every place a payload or a message can be (inputs, results, signals, prompts,
replies, tool arguments and results, error messages, approval comments), with client-side encryption off and on, and fails if any trace
of it reaches a span, a log line or a metric.

## Agent steps

Model and tool steps follow the OpenTelemetry GenAI semantic conventions (still in development, so attribute names may change with the
specification):

| Span | Attributes |
|---|---|
| `chat {model}` (client) | `gen_ai.operation.name`, `gen_ai.request.model` (the alias you registered), `gen_ai.response.model`, `gen_ai.provider.name`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`, `gen_ai.response.finish_reasons`, `gen_ai.conversation.id` (the run id), request temperature, token limit and seed |
| `execute_tool {tool}` | `gen_ai.operation.name`, `gen_ai.tool.name`, `gen_ai.tool.call.id`, `gen_ai.tool.type`, and whether the call completed, was denied or needed approval |

They are children of the activity attempt that ran the step. The metrics `gen_ai.client.operation.duration` and
`gen_ai.client.token.usage` are recorded on the same meter.

## Metrics

All instruments are on the meter `Kairos`. Labels are bounded (workflow type, activity type, outcome, queue); run and tenant ids are
never metric labels.

| Instrument | Type | Meaning |
|---|---|---|
| `kairos.runs.started`, `kairos.runs.closed` | counter | Runs started, and closed by outcome |
| `kairos.workflow_tasks.processed` | counter | Workflow tasks by outcome (`applied`, `stale_lease`, `sequence_conflict`, `faulted`) |
| `kairos.workflow_task.faults` | counter | Tasks that failed replay and were released. A burst means a bad deploy |
| `kairos.replay.duration` | histogram, `s` | Time to replay and run the workflow code for one task |
| `kairos.history.events`, `kairos.history.size` | histogram | Events, and payload bytes, in the history a task replayed |
| `kairos.activity.attempts`, `kairos.activity.retries` | counter | Attempts by outcome, and attempts that will run again |
| `kairos.activity.duration` | histogram, `s` | Duration of activity attempts |
| `kairos.lease.expiries` | counter | Leases that expired and were handled. A burst means workers are unhealthy |
| `kairos.queue.depth` | gauge | Work ready but not claimed, by `queue` (`workflow_task`, `activity_task`, `timer`) |
| `kairos.workflow_tasks.stuck` | gauge | Workflow tasks parked after repeated lease expiry |
| `kairos.store.retries` | counter | Store transactions retried after a deadlock or serialization failure |
| `kairos.timers.fired`, `kairos.signals.received` | counter | Timers fired, signals and approval decisions delivered |

## Health

`AddKairos` adds a health check named `kairos` to the host. It is **unhealthy** when the store cannot be reached, when no worker loop
is running, or when none has made progress for `WorkerStallThreshold` (30 seconds by default) while none is busy running a task. A
long activity keeps its loop busy, which counts as alive. It is **degraded** while any workflow task is parked.

## .NET Aspire

The `Kairos.Aspire.Hosting` package adds Kairos to an Aspire AppHost. Kairos runs inside your application, so the resource is the
database connection: it names the PostgreSQL database that holds the history, hands the connection string to the apps that
reference it, and appears in the dashboard.

[!code-csharp[](../../samples/Aspire/Kairos.Samples.Aspire.AppHost/Program.cs#apphost)]

In the application, the connection string arrives as `ConnectionStrings:kairos`:

[!code-csharp[](../../samples/Aspire/Kairos.Samples.Aspire.Orders/Program.cs#kairos)]

Aspire gives every service its OpenTelemetry endpoint, and `AddKairos` subscribes to its own spans and metrics, so the dashboard shows
each request with the run it started, the tasks, activity attempts and timers beneath it, and the Kairos metrics next to the web ones.

[!code-csharp[](../../samples/Aspire/Kairos.Samples.Aspire.Orders/Program.cs#telemetry)]

The sample creates the Kairos database roles and schema for you in development. In a real deployment, provisioning and migration run
from your pipeline and the application connects as the `kairos_dataplane` role.
