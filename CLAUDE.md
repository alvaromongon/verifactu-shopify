# CLAUDE.md

Guidance for Claude Code in this repository. **`README.md` is the source of truth** (in Spanish):
read the relevant sections before working and keep them up to date; do not duplicate them here.

- Project: .NET 10 console connector that turns paid Shopify orders into VERI\*FACTU records and
  sends them to the AEAT through `mdiago/VeriFactu`, run as a stateless scheduled job.
- Plan and decisions: GitHub issues and milestones. Read the issue before implementing; issues that
  need a design start with an analysis of options and the decision recorded in a comment.

## Where to look in the README

| Topic | README section |
|---|---|
| What it does, how to run it | *De un vistazo*, *Sincronización con Shopify* |
| Build, test and local gate | *Compilar y probar* |
| Folder layout and conventions | *Estructura del proyecto*, *Convenciones de desarrollo* |
| Certificate, configuration and deployment | *Certificado*, *Despliegue*, *Despliegue como tarea periódica* |
| Chain rules and VeriFactu local data | *Cadena de registros*, *Datos locales de VeriFactu* |
| CI checks | *Puertas de calidad* |

## Rules for Claude

- TDD, and tests mirror `src` folders. Never call the real Shopify or AEAT from tests: component
  tests use the stubs in `tests/VerifactuShopify.ComponentTests/TestDoubles/`.
- Only run commands against the AEAT **pre-production** environment, and only when the user asks.
- Before finishing, run the local gate: `.githooks/pre-push`.
- VeriFactu is pinned to a version with a published responsible declaration: never bump it without
  checking the declaration exists (see *Dependencia: mdiago/VeriFactu*).
- Code and code comments in English; README, issues, commit messages and user-facing messages in
  Spanish.
- Commits are authored by the repository owner: no `Co-Authored-By` trailers.
- **Only `git push` after an explicit confirmation from the owner** for that push.

## Overrides of the personal baseline

- **No Dockerfile or image scan yet**: the container image is issue #23.
- **No SLO or load test yet**: pending, to be defined for a sync run against the stubs.
