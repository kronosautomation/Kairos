---
title: Kairos documentation
---

# Kairos

Kairos is durable execution for .NET. You write a workflow as an ordinary `async` C# method; Kairos records every step it takes in
PostgreSQL, so the workflow keeps going through crashes, deployments and long waits, and no step is lost or recorded twice.

It runs inside your application (`services.AddKairos(...)`) with nothing to operate but the PostgreSQL database you already have.

| Start here | |
|---|---|
| [Overview](overview.md) | What Kairos is for, and when it is the wrong tool. |
| [Getting started](getting-started/index.md) | A working workflow, run, interrupted and resumed, in about ten minutes. |
| [Concepts](concepts/index.md) | Workflows, activities, replay, determinism, history, timers, signals, retries, leases, versions. |
| [Commercial features](pro/index.md) | The standalone server, the dashboard, policies and budgets, and publishing agents over MCP and A2A. |
| [API reference](api/index.md) | The public types of every package. |

All C# on these pages is taken from sample projects that are compiled and tested with every build.
