# Security policy

Kairos keeps the state of long-running work in your database and runs your code in your processes. A flaw in it can cost you data or let one tenant see
another's, so we want to hear about one the day you find it, and we will treat you as a colleague when you tell us.

## Reporting a vulnerability

**Please do not open a public issue, pull request or discussion about a vulnerability.**

Report it privately in either of these ways:

1. **GitHub private vulnerability reporting** (preferred): on the repository's *Security* tab choose *Report a vulnerability*
   (<https://github.com/kronosautomation/Kairos/security/advisories/new>). Only the maintainers and you can see the report, and the advisory we later publish is created from it.
2. **Email** [REPLACE-SECURITY-EMAIL](mailto:REPLACE-SECURITY-EMAIL), if you cannot use GitHub. Do not put exploit details in the subject line.

Tell us what you can of: the package, version or image tag; what you did; what you expected and what happened; and what an attacker gains (data of another tenant, a forged
approval, a payload read by the server, code execution, a bypassed limit...). A short reproduction is the most useful thing you can send. If you have a fix, say so; you do not need one.

Please send **no real customer data**, no credentials and no payloads from systems you do not own. If a report needs sample data, make it up.

## What happens next

| When | What you get |
|---|---|
| Within 3 working days | An acknowledgement from a person, and the name of who is handling it. |
| Within 10 working days | Our assessment: whether we can reproduce it, how severe we think it is (CVSS v4 vector), which versions are affected, and how long we expect a fix to take. |
| While it is open | Updates at least every 14 days, and a chance to review the advisory text and the fix before they are public. |
| Fixed | A release with the fix, a GitHub security advisory (with a CVE where one applies), and credit to you by name or handle if you want it. |

**Targets, not promises:** a fix for a critical issue within 30 days of the report, high within 60, other issues within 90. A fix that needs a change of the on-disk or on-wire
format can take longer, and then we tell you why and what mitigation to apply meanwhile.

### Coordinated disclosure

We ask you to keep the details private until the fix is released, and in any case for up to **90 days** after your report. If a fix is ready sooner we release sooner. If we
need more than 90 days we will ask you for it and give the reason; if you are not willing to wait we will talk about it rather than argue. If you find that the flaw is already
being exploited, tell us at once and we will move as fast as we can; public warning to users is then more important than the date.

## What is in scope

- Every package built from this repository (`Kairos.*` on NuGet), the `kairos` command line tool, and, for the commercial editions, the server, dashboard and agent packages.
- Container images we publish, including what is in them.
- The documented behaviour of tenant isolation, payload encryption, the hash-chained history, approvals and their evidence, authentication, and rate limits.
- Our build and release process: for example a way to publish a package or image that we did not build, or to make a signature or an attestation say something untrue.

## What is not

- Findings that need an attacker who already has your database superuser, your payload keys, or code execution in your worker; those are not isolation boundaries we claim.
  (What each protection does and does not cover is written down in the documentation; a gap *between* that text and the behaviour is in scope.)
- Volumetric denial of service, and automated scanner output with no demonstrated impact.
- Social engineering of our staff or customers, physical attacks, and anything in a deployment that is not ours (your cluster, your cloud account, your identity provider).
- A flaw in a dependency that Kairos does not make reachable. Report those upstream; if Kairos makes one exploitable, that is in scope, and we will coordinate with the upstream project.

## Safe harbour

If you act in good faith, we will not take legal action against you or ask a platform to remove your account for research that: stays within this policy; does not read, change or
keep data that is not yours beyond the least needed to show the problem; does not degrade a service for others; and gives us a reasonable time to fix it before disclosure. Test
against your own installation, never against someone else's deployment or against a hosted service without written permission. If you are unsure whether something is covered, ask first.

## Supported versions

Security fixes are made to the **latest release**. While Kairos is before 1.0, earlier releases do not receive fixes; upgrade to the release that carries the advisory's fix. This
table is updated when a support policy for older releases is published.

| Version | Fixes |
|---|---|
| latest release | yes |
| anything older | no |

## Verifying what you install

Every release is built reproducibly from a tagged commit, and each package and image ships with a CycloneDX SBOM, SLSA build provenance and a signature you can check. The steps are
in the documentation page *Verifying a release*. If a package or image you downloaded does not verify, treat that as a vulnerability and report it here.

A server running Kairos publishes where to report at `/.well-known/security.txt` when its operator has configured it.
