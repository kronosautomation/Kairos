# Commercial features

These pages describe what the commercial features do, how to turn them on, and where their limits are. The open-source core is documented in [Concepts](../concepts/index.md).

| Page | In one line |
|---|---|
| [The standalone server](standalone-server.md) | The same engine as a multi-tenant service: sign-in, roles, isolation, limits, events and audit evidence. |
| [The dashboard](dashboard.md) | Runs, approvals, agents and cost in a browser, with payloads decrypted in the browser through a key broker you host. |
| [The dashboard inside your application](dashboard-in-your-application.md) | The same dashboard mounted in your own ASP.NET Core application, signed in with your own authentication and your own claims. |
| [Policies, prices and budgets](policies-and-budgets.md) | Declarative rules for tool calls, recorded before the tool runs, and budgets reserved before a model is called. |
| [Publishing agents as an MCP server](mcp-server.md) | Workflows as MCP tools, runs as tasks, approvals answered by the verified caller. |
| [Publishing agents over A2A](a2a-server.md) | Workflows as A2A skills on a signed Agent Card. |

## License keys

The commercial packages need a license key for production use. Development, testing and CI never need one, and the open-source packages never check for one.

You give the application the key as text or as a file, and tell it that it is running in production:

```
export KAIROS_LICENSE_FILE=/run/secrets/kairos-license
export KAIROS_ENVIRONMENT=Production
```

`KAIROS_LICENSE` holds the key itself instead of a path. Production is whatever `KAIROS_ENVIRONMENT` says and nothing else; the host's own environment name is not consulted.

- A key is checked offline, against public keys inside the package. Nothing is sent anywhere, so air-gapped sites work the same way.
- Each commercial feature checks the key once when it starts, and says in the log what it found. A warning that stands is repeated once a day.
- **Nothing is ever switched off.** A missing, expired, unreadable or lower-tier key produces a warning and the feature keeps working, and so does every running workflow. The key is a contract, not a lock.
- The number of production applications is recorded in the key and shown, not enforced.
