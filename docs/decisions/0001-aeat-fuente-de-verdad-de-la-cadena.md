---
estado: Sustituido por [0002](0002-estado-de-la-cadena-en-almacenamiento-externo.md)
fecha: 2026-10-02
decisores: Álvaro Montero
---

# 0001. La AEAT como fuente de verdad de la cadena, sin almacén de estado

- **Issue**: [#12](https://github.com/alvaromongon/verifactu-shopify/issues/12) ([análisis y decisión](https://github.com/alvaromongon/verifactu-shopify/issues/12#issuecomment-5776445183))
- **Registrado a posteriori** el 2026-09-28, a partir de la issue.

## Contexto y problema

Cada registro de facturación lleva la huella del anterior del mismo sistema informático (SIF) y emisor. La librería mdiago/VeriFactu guarda esa cadena en una carpeta local. El conector se quiere desplegar como una tarea periódica sin estado (cronjob, función serverless), así que cada ejecución arranca con la carpeta vacía y tiene que saber con qué registro encadenar el siguiente.

## Factores de decisión

- Despliegue sin estado, independiente de la nube, en Linux, macOS y Windows.
- En modalidad VERI\*FACTU no hay que conservar los registros: los tiene la AEAT (art. 3 de la Orden HAC/1177/2024 y FAQ de conservación, en [referencias-aeat.md](../referencias-aeat.md)).
- La FAQ de integridad indica que, ante una pérdida total de registros, el estado se recupera de la AEAT.
- La AEAT no rechaza un encadenamiento incorrecto: un «Correcto» no prueba que la cadena esté bien.
- Depender lo menos posible de formatos internos de la librería.

## Opciones consideradas

- A. Sincronizar la carpeta de la librería con un almacén externo.
- B. Pedir en mdiago/VeriFactu una forma pública de fijar la cabeza de la cadena.
- C. Leer la cabeza de la AEAT en cada ejecución.

## Decisión

Opción elegida: «C, sin almacén de estado», porque la consulta de la AEAT devuelve el último registro de cada factura con su huella, su encadenamiento y su fecha de generación, refleja lo enviado al instante y basta para reconstruir la cabeza sin guardar nada. B se propuso en paralelo ([mdiago/VeriFactu#293](https://github.com/mdiago/VeriFactu/issues/293)) sin bloquear la decisión.

En cada ejecución, por emisor:

1. Se consultan el mes actual y el anterior y se filtran los registros de nuestro SIF (NIF del productor, `IdSistemaInformatico` y `NumeroInstalacion`). La cabeza es el registro con la fecha de generación más reciente. Si no hay ninguno, el siguiente va como `PrimerRegistro`.
2. La cabeza se escribe en la carpeta vacía de la librería, en su fichero interno `_<NIF>.csv`, y se recarga la cadena.
3. Ante una excepción que no sea un rechazo de la AEAT, se para: nunca se encadena sobre una cabeza dudosa, y la siguiente ejecución vuelve al paso 1.

**Confianza**: media. Se revisaría si la normativa exige encadenar con registros que la AEAT no tiene, si la librería cambia el formato del fichero de cabeza o si aparece una forma pública de fijarla.

### Consecuencias

- Buena: ninguna pieza de almacenamiento que desplegar, respaldar o proteger.
- Buena: la recuperación tras una caída o una pérdida de datos es la operación normal.
- Mala: el conector escribe un fichero interno de la librería, que puede cambiar en cualquier versión.
- Mala: solo se consultan dos meses; un conector parado más de un mes no encuentra la cabeza ([#22](https://github.com/alvaromongon/verifactu-shopify/issues/22)).
- Mala: solo se pueden anular facturas del mes actual o del anterior; lo más antiguo se corrige con una rectificativa.
- Mala: la AEAT no resuelve la exclusión entre ejecuciones; dos a la vez romperían la cadena ([#16](https://github.com/alvaromongon/verifactu-shopify/issues/16)).
- Mala: un registro que la AEAT rechaza no cuenta como cabeza, así que la cadena sigue desde el último aceptado. Este efecto no se analizó al decidir; se vio al rechazarse [mdiago/VeriFactu#293](https://github.com/mdiago/VeriFactu/issues/293).

### Confirmación

- Tests de `BlockchainSetup`: búsqueda de la cabeza, filtrado por SIF y formato del fichero de cabeza, fijado a la versión de la librería.
- Comando `cadena`: compara la cabeza de la AEAT con la local y termina con error si no coinciden.

## Pros y contras de las opciones

### A. Sincronizar la carpeta de la librería

- Bien: la cadena local queda completa, incluidos registros que la AEAT no tenga.
- Mal: depende de más formatos internos, entre ellos CSV con fechas que cambian según la configuración regional.
- Mal: no resuelve un fallo a mitad de envío: tras un timeout, la librería deshace el eslabón aunque la AEAT lo haya aceptado, y lo guardado queda desfasado.
- Mal: añade un almacén que desplegar en cada entorno.

### B. Método público en la librería

- Bien: sin formatos internos.
- Mal: depende de un tercero y de sus plazos. Finalmente fue rechazado (ver Más información).

### C. La AEAT como fuente de verdad

- Bien: sin estado y sin almacén.
- Mal: sigue escribiendo un fichero interno, aunque sea uno solo.
- Mal: el ámbito de la consulta, por meses, limita el tiempo parado y las anulaciones.

## Más información

- Fuentes normativas: [docs/referencias-aeat.md](../referencias-aeat.md).
- Plazos y frecuencia de ejecución: [comentario en #12](https://github.com/alvaromongon/verifactu-shopify/issues/12#issuecomment-5776900455).
- El 2026-09-28 el autor de la librería rechazó [mdiago/VeriFactu#293](https://github.com/mdiago/VeriFactu/issues/293). Sostiene que la cadena es estado persistente del SIF y que un registro rechazado por la AEAT sigue formando parte de ella. Si eso se confirma, esta decisión se revisará en un ADR que la sustituya.
