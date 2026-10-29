# Monitoring and alerts

[Observability](../concepts/observability.md) lists every span and metric. This page picks the ones to alert on, and says plainly where Kairos gives you no signal and you have to make one. Every name below is
checked by a test in `samples/Operations.Tests`, so a rename that would break your alert breaks the build first.

The meter and the activity source are both called `Kairos`, and `AddKairos` subscribes your OpenTelemetry setup to them. Metric attributes are bounded: workflow type, activity type, outcome, queue. Run and tenant ids are never metric attributes.

[!code-csharp[](../../samples/Operations/ProductionSetup.cs#signals)]

## The alerts that matter

| Alert | Signal | Notes |
|---|---|---|
| **A run is stuck** | Gauge `kairos.workflow_tasks.stuck` above 0. The health check `kairos` reports *degraded*. A warning is logged once per run: `Run {RunId} ({WorkflowType}, build {BuildId}) is parked: its workflow task expired {Attempts} times...` | A parked run stays open and loses nothing, but does not run until an operator resumes or fails it ([Leases and heartbeats](../concepts/leases-and-heartbeats.md#runs-that-get-stuck)). Page on it: nobody else will notice. Only instances with workers report it. |
| **Leases are expiring** | Counter `kairos.lease.expiries`, alert on its rate | Workers die, stall or lose the network. A burst after a deploy is normal; a steady rate is not. |
| **A retry storm** | Counter `kairos.activity.retries`, by `kairos.activity.type`; counter `kairos.activity.attempts` by type and `kairos.outcome` (`completed`, `failed`, `retry`, `lease_lost`, `cancelled`) | Alert on retries as a share of attempts for one activity type. A downstream service that is down shows up here first. |
| **A bad deploy** | Counter `kairos.workflow_task.faults`, and the error log `Workflow task for run {RunId} failed replay and was released` | Tasks that fail replay are released and offered again after a delay; runs do not advance. Any sustained rate means the running code does not replay a run it should. |
| **Work is not keeping up** | Gauge `kairos.queue.depth` by `queue` (`workflow_task`, `activity_task`, `timer`) | Growth means the workers are too few or too slow. Only instances with workers report it. |
| **The database is contended** | Counter `kairos.store.retries` | A trickle of retries after deadlocks or serialization failures is normal; growth is contention. |
| **The workers or the store are down** | Health check `kairos`: unhealthy when the store cannot be reached, when no worker loop is running, or when none has made progress for `WorkerStallThreshold` (30 seconds) while none is busy | Use it as the readiness or liveness probe of the instance. A process with `WorkerCount = 0` is checked for the store only. |
| **Plaintext mode is on** | The log warning `Payload encryption is OFF (AllowUnencryptedPayloads): workflow inputs, results and signals are stored readable in the database`, written at every start | Alert on any occurrence in production. It exists because someone wrote `AllowUnencryptedPayloads()`; production should never show it. |
| **Approvals are unverified** | The log warning `Approver verification is OFF (AllowUnverifiedApprovers)...`, at every start. Without a verifier the other warning, `No approver verifier is configured, so every approval decision will be refused...`, is logged instead | Alert on both in production. |
| **The database login is too powerful** | The log warning `The database login is privileged and AllowPrivilegedDatabaseRole was called...`, at every start | Without the call the host does not start at all. |

## Where there is no signal

Some things people ask to alert on have no signal in Kairos. Do not wait for one; make one, or look elsewhere.

- **Key-use failures.** There is no metric or log line for a failed call to the key provider. The hook is `IKeyUseObserver`: the protector tells it about every wrap and unwrap, with the outcome, and never a key
  or a payload. This one counts them with a meter of your own; alert on `succeeded=false`:

  [!code-csharp[](../../samples/Operations/ProductionSetup.cs#key-use-metrics)]

  Add `AddMeter("MyCompany.Kairos")` to your OpenTelemetry setup, since `AddKairos` subscribes only to its own. Your key service's own audit log is the other source, and the better one for who asked.
- **Budget refusals.** The open-source packages have no counter for them. A model call that a budget refuses raises a `BudgetExceededException` in your workflow code; unless the code catches it the run closes as failed, which you can see in
  `kairos.runs.closed` with `kairos.outcome` = `failed`, but not told apart from other failures. If you use a budget library, its documentation is the place for its signals.
- **Tool calls the policy denied.** The span of the tool step (`execute_tool {name}`) carries `kairos.agent.tool.outcome` (`completed`, `denied` or `approval_required`). There is no counter; if you want one, derive it from the spans.
- **Approvals waiting too long.** Nothing alerts on an approval that has been pending for days. `AgentHistory.PendingApprovals` lists them with the time they were requested (see [Approvals](../concepts/approvals.md)); build the reminder in your application.
- **Log and history retention.** Kairos sets none for logs or metrics; that is the job of your logging and metrics systems. This page gives no default because Kairos defines none.

## What telemetry never contains

Spans, metrics and logs carry ids, names, counts, sizes and outcomes. They never carry a payload, an exception message, a credential or a prompt. A test runs workflows with a known secret in every place a payload can be and fails if any trace of it reaches telemetry.
