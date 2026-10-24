# Versioning

A run can last days or months, and you will deploy new code while it is running. Two tools keep that safe. Use pinning for changes of
shape; use patching for small changes to a workflow that already has runs in flight.

## Pinning: a run stays on the build it started with

Every run is pinned to a **build id** when it starts. Register each build of a workflow under its own id and keep the old builds
registered until their runs have finished:

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#pinned-builds)]

New runs start on the build you choose (`KairosClient.StartAsync(..., buildId: "2026-11-01")`); runs already under way keep being
handled by the code they started with. A worker only picks up runs whose build it has registered, so a worker that has not been given
an old build leaves those runs alone for the workers that have it. Nothing is lost while the old build is absent; the run waits.

Pinning is the right tool when a workflow changes its steps, its order or its activity names, because old runs never see the new code.

## Patching: a small change that applies to new runs only

When you only want to add or change a step, and you cannot keep two builds, gate the change with `ctx.IsPatched`:

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#version-1)]

[!code-csharp[](../../samples/Concepts/ConceptExamples.cs#version-2)]

`IsPatched("fraud-check")` behaves like this:

- For a run that reaches that point for the first time under the new code, it returns `true` and records a marker in the history, so
  every later replay of that run returns `true` as well.
- For a run whose history was written before the patch existed, replay reaches the same point with no marker, and it returns `false`:
  the run takes the old path, exactly as it did the first time.

So new runs get the fraud check, in-flight runs finish as they were, and nothing diverges.

Without the gate, inserting the new step would make every in-flight run diverge on replay and raise a
`NonDeterminismException` ([Determinism](determinism.md)). Nothing is damaged in that case: the runs wait, unchanged, and continue as
soon as the code is corrected or the change is gated. Roll back the deployment and they resume.

## Practical advice

- Use a descriptive patch id and keep the patch in the code until no run that predates it remains.
- Do not rename a workflow or an activity that has runs in flight without pinning.
- Test the change before you deploy it: the simulator kit replays recorded histories against new code, which is how the examples on
  this page are tested.
