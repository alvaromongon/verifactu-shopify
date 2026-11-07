# CLAUDE.md

Guidance for Claude Code in this repository. **`README.md` is the source of truth** (in Spanish):
read the relevant sections before working and keep them up to date; do not duplicate them here.

- Project: .NET 10 console connector that turns paid Shopify orders into VERI\*FACTU records and
  sends them to the AEAT through `mdiago/VeriFactu`, run as a stateless scheduled job.
- Plan: GitHub issues and milestones. Read the issue before implementing. The issue cycle (labels,
  triage, refine, implement, review, escalation) is in [`docs/agents/workflow.md`](docs/agents/workflow.md).
- Decisions: ADRs in `docs/decisions/` (`adr` skill). An issue that needs a design gets an ADR proposed
  in a PR, with the options analysed; read the accepted ADRs that affect a change before making it.

## Where to look in the README

| Topic | README section |
|---|---|
| What it does, how to run it | *De un vistazo*, *Sincronización con Shopify* |
| Build, test and local gate | *Compilar y probar* |
| Folder layout and conventions | *Estructura del proyecto*, *Convenciones de desarrollo* |
| Architecture decisions | [`docs/decisions/`](docs/decisions/README.md) |
| Certificate, configuration and deployment | *Certificado*, *Despliegue*, *Despliegue como tarea periódica* |
| Chain rules and VeriFactu local data | *Cadena de registros*, *Datos locales de VeriFactu* |
| SLO of a sync run and load test | *SLO de una pasada* |
| CI checks | *Puertas de calidad* |

## Rules for Claude

- TDD, and tests mirror `src` folders. Never call the real Shopify or AEAT from tests: component
  tests use the stubs in `tests/VerifactuShopify.ComponentTests/TestDoubles/`.
- Only run commands against the AEAT **pre-production** environment, and only when the user asks.
- Before finishing, run the local gate: `.githooks/pre-push`.
- The SLO in the README and the objectives, latencies and call bounds in
  `tests/VerifactuShopify.LoadTests/Sync/OrderSyncSloTests.cs` must stay in sync.
- VeriFactu is pinned to a version with a published responsible declaration: never bump it without
  checking the declaration exists (see *Dependencia: mdiago/VeriFactu*).
- Code and code comments in English; README, ADRs, issues, commit messages and user-facing messages
  in Spanish. ADR headings and statuses are the Spanish translation of MADR used in `docs/decisions/`:
  *Propuesto*, *Aceptado*, *Rechazado*, *Obsoleto*, *Sustituido por NNNN*.
- Commits are authored by the repository owner: no `Co-Authored-By` trailers.
- Push and merge: see *Push and merge policy* below.

## Push and merge policy

Agents may push `issue-*` branches and open PRs. The work queue (`/github-work-queue`) may merge
a PR with `gh pr merge --squash` once both reviews are clean and CI is green
([Unattended merge](docs/agents/workflow.md#autonomía)). Outside the work queue, merging is done by
the owner. Ask before any other push; never merge with `--admin`.

## Overrides of the personal baseline

- **No Dockerfile or image scan yet**: the container image is issue #23.
- **SLO of a job, not a service**: measured per sync run, in-process against the stubs, and run on
  demand (no schedule).
