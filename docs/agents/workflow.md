# Flujo de trabajo con agentes

Este repositorio usa el flujo de GitHub de las skills `~/.claude/skills/github-*` (diseño:
[ADR 0001](https://github.com/alvaromongon/claude-config/blob/main/docs/decisions/0001-github-driven-triage-refinement-and-autonomous-implementation.md)
y [ADR 0004](https://github.com/alvaromongon/claude-config/blob/main/docs/decisions/0004-three-layer-baseline-decisions-shaping-implementation.md)
de `claude-config`). Una issue `spec` recoge el qué y el porqué. Su milestone es la épica, y cada issue es un ticket *tracer bullet*. Las dependencias son las relaciones nativas de bloqueo entre issues de GitHub. El orden entre milestones lo dan los bloqueos reales entre sus specs.

## Ciclo

0. **Dar forma** (`github-shape`): una idea del propietario se convierte en una issue `spec` (problema, por qué importa, métrica de éxito, presupuesto de tiempo, opciones y lo que no se hace). Cada pregunta abierta es una issue `decision` que la bloquea. Se salta cuando el trabajo ya está acotado. Al cerrar el milestone se cierra la spec, comprobando la métrica contra su objetivo.
1. **Clasificar** (`github-triage`): las issues externas entran con `needs-triage` y se clasifican como `needs-info`, `ready-for-agent` o `ready-for-human`, o se cierran como `wontfix`. Los PR externos se revisan como texto: nunca se descargan ni se ejecutan en el equipo.
2. **Refinar** (`github-refine`): una spec con métrica de éxito se divide en issues *tracer bullet* con sus bloqueos. Solo se detalla la frontera, que se etiqueta `ready-for-agent` o `ready-for-human`. El resto de tickets quedan como esbozos y se detallan cuando llegan a la frontera.
3. **Implementar** (`github-implement`): una issue `ready-for-agent` cada vez, con TDD y en un subagente aislado. Termina con una rama `issue-*` pusheada y un PR abierto.
4. **Revisar**: dos subagentes revisan la rama en paralelo, cada uno en su worktree y solo informando: `code-review` (corrección) y `security-review` (seguridad). Si alguno encuentra algo bloqueante, se hace una ronda de arreglos.
5. **Cola de trabajo** (`github-work-queue`): encadena los pasos 3 y 4 sobre las issues `ready-for-agent`, una detrás de otra. Espera a la CI, hace el merge y escala a una persona las issues que lo necesiten (ver *Cuándo se para y escala a una persona*) sin parar el resto de la cola.

## Autonomía

Por issue, con labels:

- `ready-for-agent`: el agente puede implementarla sin pedir permiso en cada paso.
- `ready-for-human`: necesita supervisión, criterio humano o una prueba manual. La cola de trabajo se la salta.

Por repositorio:

- **Unattended merge**: yes. La cola de trabajo hace el merge (squash) cuando las dos revisiones están limpias y la CI en verde. La etiqueta `ready-for-agent` es el acuerdo previo sobre qué hacer: la pone el propietario cuando la issue tiene su brief. Fuera de la cola, el merge lo hace el propietario.
  Por qué `yes` aquí: un merge a `main` no llega a ningún despliegue, porque todavía no hay imagen ni publicación ([#23](https://github.com/alvaromongon/verifactu-shopify/issues/23)). `main` está protegida y exige la CI en verde. Se revisa cuando #23 publique una imagen desde `main`.
- **Política de push**: los agentes pueden hacer push de ramas `issue-*` y abrir PR; cualquier otro push se pregunta (ver [`CLAUDE.md`](../../CLAUDE.md) › *Push and merge policy*, que aplica `.claude/settings.json`).
- **AEAT**: ningún agente lanza nada contra la AEAT, ni siquiera contra preproducción, sin que el propietario lo pida en ese momento. Una issue que necesita una prueba en preproducción es `ready-for-human`.

## Entrada de issues

Las issues nuevas entran por los formularios de `.github/ISSUE_TEMPLATE/`, que ponen `needs-triage`. Cuando el autor responde a una issue `needs-info`, el workflow `needs-info-reply` la devuelve a `needs-triage`, y GitHub avisa de la respuesta como siempre.

## Cuándo se para y escala a una persona

El agente deja la rama como está, etiqueta la issue `ready-for-human` y explica el motivo en un comentario cuando:

- los tests no se estabilizan tras 3 intentos,
- los criterios de aceptación resultan ambiguos a mitad de implementación,
- el cambio toca un área sensible que no está en *Áreas sensibles* del brief:
  - el certificado y los envíos a la AEAT (endpoints, entorno de preproducción o producción),
  - el encadenamiento de registros y su estado (ver *Cadena de registros* en el [README](../../README.md)),
  - la versión de `mdiago/VeriFactu`, que exige una declaración responsable publicada,
  - CI, rulesets y `.githooks/`,
- la issue no tiene `ready-for-agent` o todavía tiene un bloqueo abierto,
- la ronda de arreglos de la revisión no deja una revisión limpia.

## Diferencias con el flujo por defecto

- Documento en español, como el resto de la documentación del repositorio. Los nombres de las labels se mantienen en inglés.
