; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
KAI001 | Kairos.Determinism | Error | Workflow code reads the clock
KAI002 | Kairos.Determinism | Error | Workflow code generates random values
KAI003 | Kairos.Determinism | Error | Workflow code performs I/O or reads the environment
KAI004 | Kairos.Determinism | Error | Workflow code uses threads or its own scheduling
KAI005 | Kairos.Determinism | Error | Workflow code uses mutable static state
KAI006 | Kairos.Security | Warning | Credential passed through workflow history
