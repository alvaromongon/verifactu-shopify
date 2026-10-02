---
estado: Propuesto
fecha: 2026-09-28
decisores: Álvaro Montero
---

# 0002. Estado de la cadena en un almacenamiento de objetos externo

- **Issue**: [#36](https://github.com/alvaromongon/verifactu-shopify/issues/36)
- **Sustituiría a**: [0001](0001-aeat-fuente-de-verdad-de-la-cadena.md), si se acepta.

## Contexto y problema

El [ADR-0001](0001-aeat-fuente-de-verdad-de-la-cadena.md) reconstruye la cabeza de la cadena en cada ejecución a partir de lo que tiene la AEAT. Al proponer en la librería un método para fijar esa cabeza ([mdiago/VeriFactu#293](https://github.com/mdiago/VeriFactu/issues/293)), su autor lo rechazó por una razón de fondo: la cadena la genera y la mantiene el SIF, y **un registro que la AEAT rechaza sigue formando parte de ella**. Los registros rechazados no llegan a la AEAT, así que ella no puede ser la fuente de verdad.

Hay que decidir dónde vive el estado de la cadena. La condición es mantener un despliegue barato y sin estado en el host (cronjob o función serverless periódica). Si hace falta estado, tiene que estar en una pieza externa, la más sencilla posible.

## Qué dice la normativa

- **Orden HAC/1177/2024, art. 7.a**: cada registro contiene los datos del registro «inmediatamente anterior por orden cronológico». Los arts. 7.b, 7.c y 7.i hablan siempre de registros **generados**, sin distinguir si la AEAT los acepta.
- **FAQ de desarrolladores (04-12-2025), apartado sobre rectificaciones, anulaciones y subsanaciones**:
  - Un registro rechazado «no figuraría jamás en los sistemas de la AEAT (aunque constaría un rechazo)».
  - Si el error se corrige sin factura rectificativa, se genera un alta de subsanación con `Subsanacion = S` y `RechazoPrevio = X`, porque el registro original «no existe en la AEAT».
- **Ningún texto dice de forma expresa** con qué se encadena el registro siguiente a uno rechazado. La lectura literal del art. 7 (encadenar con el último generado) y el tratamiento de la FAQ (el rechazado existió, aunque la AEAT no lo tenga) apoyan la postura del autor.
- **La propia librería hace lo contrario por defecto.** En la 1.0.67, `InvoiceEntry.Save()` borra el eslabón cuando la respuesta no trae CSV, que es lo que pasa en un rechazo. Solo lo conserva con `Settings.DisableBlockchainDelete = true`. Tras [mdiago/VeriFactu#297](https://github.com/mdiago/VeriFactu/issues/297), el autor ha hecho de `true` el valor por defecto en `main` (2026-09-29): la librería también considera que el rechazado sigue en la cadena.
- **El asesor fiscal (2026-10-01)**: cuando la AEAT rechaza un registro, hay que subsanar los errores y reenviar el registro corregido; no se hace una factura nueva. Coincide con la FAQ (alta de subsanación con `RechazoPrevio = X`). No se pronuncia sobre el encadenamiento, que es una cuestión técnica.

**Conclusión**: lo más seguro es tratar los registros rechazados como parte de la cadena. Hoy el conector no lo hace. Tras un rechazo, que ya para la ejecución, la siguiente encadenaría con el último registro aceptado. Y la factura rechazada no se vuelve a enviar como nueva: se corrige con un alta de subsanación.

## Prueba con la 1.0.68 en preproducción (2026-09-28)

Programa aislado, con su propia carpeta (`VeriFactuEnvironment.Path`), su propio `NumeroInstalacion` y `DisableBlockchainDelete = true`:

| Paso | Resultado |
|---|---|
| Carpeta en una ruta propia | Funciona: la librería crea allí `Blockchains`, `Invoices`, `Outbox` e `Inbox` |
| Envío sin respuesta (fallo de TLS o puerto cerrado) | El eslabón se queda en la cadena local |
| Alta rechazada (3000, duplicado) | El eslabón se queda en la cadena local |
| Alta siguiente | Encadena con el rechazado y la AEAT la acepta como «Correcto», aunque nunca recibió esa huella |
| Reenvío con `InvoiceRetrySend` | La AEAT recibe el registro original (misma huella, mismo anterior, misma fecha de generación), pero **la librería añade en local un eslabón nuevo** que encadena con el original y nunca se envía |

- **`InvoiceRetrySend` está pensado para el modo por defecto**, en el que el eslabón ya se ha borrado y se vuelve a añadir con la misma huella. Con `DisableBlockchainDelete` deja un eslabón fantasma: el siguiente registro encadenaría con una huella que no corresponde a ningún registro. Los dos mecanismos no se pueden combinar en la 1.0.68. El autor lo ha corregido en `main` ([mdiago/VeriFactu#297](https://github.com/mdiago/VeriFactu/issues/297)): `InvoiceRetrySend` solo añade el eslabón si no está ya en la cadena y conserva la huella original. Todavía no está publicado.
- **Tamaño**: unos 7,5 KB por envío (el XML en `Invoices` y en `Outbox`, y la respuesta en `Inbox`) y unos 380 bytes por eslabón en la cadena. Con 1.000 pedidos al mes, unos 90 MB al año.

## Factores de decisión

- Cumplir el art. 7: encadenar con el último registro generado, también si fue rechazado.
- Reenviar el mismo registro cuando un envío se queda sin respuesta, como pide la FAQ de la AEAT, en lugar de generar otro.
- No descargar en cada ejecución una carpeta que crece sin límite.
- Despliegue sin estado en el host: cronjob o función serverless.
- Si hace falta estado, la pieza externa más sencilla y disponible en cualquier nube.
- Depender lo menos posible de formatos internos de la librería y alinearse con cómo espera usarse. El autor recomienda persistir su carpeta.
- La exclusión entre ejecuciones ([#16](https://github.com/alvaromongon/verifactu-shopify/issues/16)) sigue pendiente.

## Opciones consideradas

- A. Mantener el ADR-0001 y tratar un rechazo como una incidencia manual.
- B. La carpeta de la librería en un almacenamiento de objetos (S3, Azure Blob o compatible), descargada al empezar y subida tras cada envío.
- C. Solo la cabeza de la cadena, en formato propio, en un almacenamiento de objetos.
- D. Un volumen persistente en el host.

## Decisión

Opción propuesta: «B, la carpeta de la librería en un almacenamiento de objetos». Es la única que cumple el art. 7 sin escribir ficheros internos de la librería, y es la forma de uso que recomienda el autor. El despliegue sigue sin estado en el host: el estado está en un bucket.

Cómo sería una ejecución, por emisor:

1. Adquirir el cerrojo con una escritura condicional en el mismo bucket: `If-None-Match` en S3, o un *lease* en Blob. Esto resuelve también [#16](https://github.com/alvaromongon/verifactu-shopify/issues/16).
2. Descargar la cadena (`Blockchains`) y los envíos pendientes a un directorio temporal, y apuntar la librería a él con `VeriFactuEnvironment.Path`. Lo ya enviado se queda en el bucket y no se descarga.
3. Comprobar la cabeza local contra la AEAT, como hace hoy `cadena`. Si la AEAT está un registro por delante y ese registro encadena con la cabeza local, fue un envío aceptado cuyo estado no llegó a subirse. En cualquier otro caso, parar.
4. Enviar con `DisableBlockchainDelete = true` (valor por defecto a partir de la versión que publique el arreglo de #297), para que un rechazo o un envío sin respuesta no saquen el registro de la cadena. Subir los ficheros cambiados tras cada envío.
5. Si hay un envío pendiente de respuesta, reenviarlo con `InvoiceRetrySend` antes de generar otro. Exige la versión que publique el arreglo de [mdiago/VeriFactu#297](https://github.com/mdiago/VeriFactu/issues/297); con la 1.0.68 añade un eslabón fantasma.
6. Tras un rechazo, parar y avisar, como hoy. La factura se corrige con un alta de subsanación (`Subsanacion = S`, `RechazoPrevio = X`), que se encadena como cualquier otro registro. Cómo se genera (a mano o desde el conector) queda para su propia issue.
7. Liberar el cerrojo.

**Confianza**: alta. La normativa (art. 7), la FAQ de desarrolladores, el autor de la librería (que cambia su valor por defecto) y el asesor apuntan en la misma dirección, aunque ningún texto lo diga de forma expresa.
- Lo que falta confirmar:
  - Que la versión con el arreglo de #297 se publique con declaración responsable, y repetir con ella la prueba en preproducción.
  - La consulta a la AEAT sobre el encadenamiento tras un rechazo queda como confirmación; no bloquea la decisión.
- Lo que haría revisar esta decisión: que la AEAT responda lo contrario, o que la librería ofrezca otra forma de persistir su estado.

### Consecuencias

- Buena: la cadena incluye los registros rechazados y los envíos sin respuesta se reenvían tal cual.
- Buena: deja de escribirse el fichero interno de cabeza; la librería gestiona su propio estado.
- Buena: el mismo bucket da el cerrojo de #16, sin otra pieza.
- Buena: desaparece el límite de dos meses de consulta ([#22](https://github.com/alvaromongon/verifactu-shopify/issues/22)) y el de anular solo facturas recientes: la cabeza ya no se busca en la AEAT.
- Mala: el despliegue necesita un bucket, sus credenciales y su copia de seguridad. Perderlo exige recuperar la cabeza de la AEAT, que es el mecanismo actual y queda como plan de emergencia.
- Mala: exige una versión de la librería posterior a la 1.0.68, que aún no está publicada, para reenviar con `InvoiceRetrySend`.
- Mala: un rechazo exige generar un alta de subsanación con `RechazoPrevio = X`, que el conector todavía no sabe hacer.
- Mala: hay que abstraer el almacenamiento para no depender de una nube, con al menos dos implementaciones (S3 y Blob) y sus tests.
- Mala: entre un envío aceptado y la subida hay una ventana en la que el estado del bucket queda por detrás de la AEAT. El paso 3 lo detecta.

### Confirmación

- Tests de componente con un almacenamiento de objetos simulado: rechazo, envío sin respuesta, caída entre envío y subida, y cerrojo ocupado.
- La prueba en preproducción de este ADR, repetida con el conector: un registro rechazado a propósito y un envío sin respuesta, comprobando que el siguiente encadena con cada uno y que no hay eslabones fantasma.

## Pros y contras de las opciones

### A. Mantener el ADR-0001 y tratar un rechazo como una incidencia manual

- Bien: sin cambios ni piezas nuevas.
- Mal: el rechazado se pierde con el contenedor. Para encadenar con él habría que guardarlo en algún sitio, que es tener estado. En la práctica, la cadena seguiría desde el último aceptado, contra la lectura del art. 7.
- Mal: sigue generando otro registro tras un envío sin respuesta, en lugar de reenviar el mismo.
- Mal: sigue escribiendo un fichero interno que el autor no quiere que se toque.

### B. La carpeta de la librería en un almacenamiento de objetos

- Bien: cumple el art. 7 con el mecanismo de la propia librería (`DisableBlockchainDelete`) y permite reenviar el mismo registro.
- Neutral: sin las facturas ya enviadas en la carpeta, la librería no detecta en local un número duplicado; lo rechaza la AEAT (3000), y ese rechazo queda en la cadena como cualquier otro.
- Bien: host sin estado; el bucket es la pieza externa más sencilla y existe en todas las nubes, también en compatibles con S3 (MinIO, R2).
- Bien: la escritura condicional resuelve el cerrojo.
- Mal: depende de la estructura de la carpeta, aunque solo para copiarla. Es la dependencia que el ADR-0001 quería evitar, pero ahora es el uso previsto por el autor, no un rodeo.
- Mal: la carpeta crece unos 7,5 KB por envío. Por eso solo se descargan la cadena y los envíos pendientes.

### C. Solo la cabeza, en formato propio, en un almacenamiento de objetos

- Bien: estado mínimo, fácil de inspeccionar.
- Mal: sigue escribiendo el fichero interno de cabeza para cargarla en la librería.
- Mal: para reenviar tras un envío sin respuesta habría que guardar también el XML, es decir, reproducir a mano lo que la librería ya hace.

### D. Volumen persistente en el host

- Bien: es la recomendación literal del autor; sin código de sincronización.
- Mal: descarta las funciones serverless y obliga a un host con disco persistente (PVC, máquina virtual). Va contra la condición de despliegue.
- Mal: no resuelve el cerrojo si hay más de una réplica o un despliegue manual.

## Más información

- Respuesta del autor: [mdiago/VeriFactu#293](https://github.com/mdiago/VeriFactu/issues/293#issuecomment-5854003990).
- Fuentes normativas: [docs/referencias-aeat.md](../referencias-aeat.md), sección *Registros rechazados*.
- Código de la librería consultado en la versión 1.0.67: `InvoiceActionPost.Save`, `InvoiceRetrySend` y `Settings.DisableBlockchainDelete`. `VeriFactuEnvironment.Path` llega en la 1.0.68, en el mismo commit que su declaración responsable (2026-09-26).
- Conviene preguntar al autor por qué `Save()` quita de la cadena un registro rechazado si considera que forma parte de ella.
