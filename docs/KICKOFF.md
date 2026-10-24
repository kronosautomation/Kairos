# Kickoff prompt for Claude Code (Milestone 1)

Paste this into Claude Code started in C:\Kronos\Kairos:

---
Read CLAUDE.md and docs/SECURITY-ARCHITECTURE.md. Then start Milestone 1:

1. Initialize git, add a .NET .gitignore, and create Kairos.sln with the solution layout from the architecture doc
   (create the projects for Abstractions, Core, and Core.Tests now; stub the rest later).
2. Add Directory.Build.props (net10.0, nullable, TreatWarningsAsErrors, analyzers) and Directory.Packages.props
   with lock files and NuGetAudit.
3. In Kairos.Abstractions, define: IWorkflowContext (Now, NewGuid, Delay/CreateTimer, ExecuteActivityAsync with
   retry policy, WaitForSignalAsync), workflow/activity attributes, RetryPolicy, and the history event types
   (WorkflowStarted, ActivityScheduled/Completed/Failed, TimerStarted/Fired, SignalReceived, WorkflowCompleted/Failed),
   each carrying tenant/namespace/run ids, sequence number, and hash fields.
4. In Kairos.Core, implement deterministic replay: run a workflow against an existing history, emit new commands,
   and fail with a NonDeterminismException if replayed commands don't match history. Implement the hash chain.
5. Write xUnit tests: happy path, activity retry then success, timer, signal, crash-and-resume via replay,
   non-determinism detection, and hash-chain tamper detection.
6. Add a Kairos.Benchmarks project (BenchmarkDotNet, MemoryDiagnoser) measuring replay of a 1,000-event history and
   hash-chain append; keep replay hot paths allocation-light (pooled buffers, no LINQ).
7. Run dotnet build and dotnet test until everything is green, then commit.
---
