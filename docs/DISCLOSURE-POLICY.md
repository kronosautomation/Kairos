# Disclosure Policy: What Is Public and What Stays Internal

Decided 24 Oct 2026. Applies to the documentation site, the marketing site, the public repository, samples, release
notes, talks and anything else outside Kronos Automation.

## Principle
The Apache core is open source, so how the engine works is public by design; docs explain it at the level users and
security reviewers need. What stays private is **why and what next**: strategy, pricing rationale, competitive analysis,
research plans, unfixed weaknesses, commercial internals, and operational detail that helps an attacker more than a user.

## Public (docs-site, marketing site, public repo)
- How to use the public APIs, configuration, deployment, operations and troubleshooting.
- Concepts and guarantees at the user level, **with their limits** (e.g. what encryption does not protect).
- Security model for customers: threat model summary, algorithms used, isolation model, key handling, how to report a
  vulnerability (SECURITY.md). Customers and auditors need this; hiding it costs trust.
- Supported versions, compatibility promise, changelog.
- Edition feature lists and list prices once validated.
- Benchmarks only after methodology review, with the setup described so others can reproduce them.

## Internal only (never in docs-site, marketing, public repo or its history)
| Material | Today's location | Why internal |
|---|---|---|
| Positioning, competitor analysis, target segments | docs/POSITIONING.md, Strategy and Applications docs | Strategy |
| Pricing rationale, revenue scenarios, discount programs | Licensing and Pricing doc | Commercial |
| Research reading list and research-driven plans | docs/RESEARCH.md | Roadmap and differentiation |
| Code and security reviews with open findings | docs/reviews/ | Unfixed weaknesses; publish only as advisories after fixes ship |
| Hosting design, cost model, cell layout, account structure | docs/HOSTING-AWS.md | Operational security, commercial |
| Roadmap, milestone plans, kickoff prompts, work map | docs/MULTI-CLOUD-AND-AGENTS.md, docs/KICKOFF.md, CLAUDE.md, work map | Roadmap |
| Raw benchmark notes | docs/BENCH-GROUP-COMMIT.md | Unreviewed numbers |
| Commercial package source and design | Kairos.Commercial (private) | Paid product |
| License-key format and verification internals | Kairos.Commercial | Anti-tamper |
| Internal tuning constants and attack-relevant internals beyond what operators need (e.g. exact lease/reaper batch heuristics) | code comments, reviews | Explain behaviour, not exploit detail |
| Customer names, design-partner notes, incidents | anywhere | Confidential |

Mixed documents (SECURITY-ARCHITECTURE.md, TEST-STRATEGY.md, LICENSING.md, DOCS-SITE.md) are internal as written; the
docs site gets **rewritten customer-facing versions**, never copies.

## Rules for the docs site
1. DocFX builds only from `docs-site/` and `samples/`. Nothing under `docs/` is ever included, linked or copied.
2. Pages describe behaviour and guarantees, not roadmap: no "coming soon", milestone numbers, internal invariant IDs
   (I1, F2, L3 ...), review IDs, or competitor comparisons beyond the published comparison table.
3. Commercial features are described by what they do, never how they are built.
4. CI check (`build/check-disclosure.ps1`, to be added): fails if `docs-site/` or `samples/` reference `docs/`,
   `reviews`, `Kairos.Commercial`, or contain the terms on a blocklist (milestone, roadmap, revenue, competitor,
   design partner, internal).
5. Every new public page gets a disclosure review before publishing (Aaron approves).

## Before the repository is made public
The current repository mixes code with internal documents, and its git history contains them. Before any public release:
1. Create a private repository (`Kairos.Internal`, or a folder in `Kairos.Commercial`) and move: docs/ except the files
   rewritten for the public, docs/reviews/, KICKOFF.md, the internal CLAUDE.md content, start-kairos.cmd.
2. Publish the open-source repository **from a clean history** (a fresh initial commit of the public tree, or a
   `git filter-repo` pass that removes every internal path from all commits). Never push the current history to a public remote.
3. Add public-facing files: README.md, CONTRIBUTING.md (DCO sign-off), SECURITY.md (private vulnerability reporting),
   CODE_OF_CONDUCT.md, and a short public CLAUDE.md/AGENTS.md with coding rules only.
4. Scan the public tree and history for secrets and internal terms (gitleaks or equivalent plus the disclosure check).
5. Keep the TLA+ spec and the simulator public: they are trust signals. Keep test-strategy details public only in the
   rewritten "How Kairos is tested" page.
