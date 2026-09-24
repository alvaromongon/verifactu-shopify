# verifactu-shopify

> **In English.** Open-source connector between Shopify and VERI\*FACTU, the Spanish Tax Agency (AEAT) system for registering invoices. It turns Shopify orders into invoice records (simplified, full and corrective) and submits them to the AEAT through the [mdiago/VeriFactu](https://github.com/mdiago/VeriFactu) library.
>
> - **Stateless by design.** Records are hash-chained and the AEAT is the source of truth for the chain, so a fresh container picks up where the last one left off.
> - **Certificate handling without a cloud SDK.** The client certificate is loaded in memory from a mounted secret file; on Linux the private key never touches disk.
> - **.NET 10**, with CI on Linux, macOS and Windows.
>
> **Status: in development, tested only against the AEAT pre-production environment.** The rest of this README, the issues and the milestones are in Spanish, because the users are Spanish businesses.

Conector entre Shopify y VERI\*FACTU, el sistema de la Agencia Tributaria (AEAT) para registrar facturas. Convierte los pedidos de una tienda Shopify en registros de facturación —simplificadas, completas y rectificativas— y los envía a la AEAT con la librería [mdiago/VeriFactu](https://github.com/mdiago/VeriFactu).

> **Estado: en desarrollo. No lo uses para facturar.** Por ahora solo se prueba contra el entorno de preproducción de la AEAT, que no tiene efectos fiscales. El avance está en los [milestones](https://github.com/alvaromongon/verifactu-shopify/milestones).

## Aviso legal

Este repositorio **no es un sistema informático de facturación (SIF) certificado** y se entrega **sin declaración responsable**.

Según la [FAQ de la AEAT](https://sede.agenciatributaria.gob.es/Sede/iva/sistemas-informaticos-facturacion-verifactu/preguntas-frecuentes/certificacion-sistemas-informaticos-declaracion-responsable.html), la empresa que integra código abierto en su sistema de facturación es quien debe emitir la declaración responsable e indicar qué componentes usa. Si despliegas este código para facturar, **tú eres el productor de tu SIF** y respondes de su cumplimiento.

Nada de lo que hay aquí es asesoramiento fiscal. Consulta con tu asesor antes de usarlo en producción.

## Dependencia: mdiago/VeriFactu

- **Versión fijada: 1.0.66.** Solo se usan versiones para las que el autor ha publicado declaración responsable ([listado](https://github.com/mdiago/VeriFactu/tree/main/NetFramework/Doc/Legal)). Antes de subir de versión hay que comprobar que existe la de la nueva; un test lo recuerda.
- **Licencia: AGPL-3.0 con una cláusula adicional de Irene Solutions SL**, que exige comprar licencia comercial si la librería se usa en una actividad comercial sin publicar el código fuente (por ejemplo, un servicio de pago o un producto cerrado). Está en la cabecera de sus ficheros; léela antes de usar este conector con fines comerciales.

## Alcance previsto

| Tipo | Cuándo |
|---|---|
| F2 | Pedido sin NIF del cliente: factura simplificada |
| F1 | Pedido con NIF: factura completa |
| F3 | Factura completa que sustituye a una simplificada ya enviada |
| R5 | Devolución o corrección de una simplificada |
| R1 / R4 | Devolución o corrección de una factura completa |
| Anulación | Registro emitido por error, por ejemplo un pedido procesado dos veces |

## Requisitos

- .NET 10 SDK
- Linux, macOS o Windows. CI prueba los tres en cada cambio.

## Compilar y probar

```bash
dotnet test
```

Los tests no necesitan certificado ni salen a la AEAT: los que cargan un `.pfx` crean uno autofirmado.

## Certificado

La AEAT solo acepta certificados de una autoridad reconocida, también en preproducción; uno autofirmado se rechaza. Además, el titular del certificado tiene que ser una de estas figuras o el envío se rechaza con **error 4112** ([preguntas técnicas frecuentes de la AEAT](https://sede.agenciatributaria.gob.es/Sede/impuestos-tasas/iva/iva-libros-registro-iva-traves-aeat/preguntas-tecnicas-frecuentes.html)):

| Titular | Notas |
|---|---|
| El obligado tributario | Certificado de persona física a su propio nombre |
| Un apoderado para el trámite | Mediante el apoderamiento **IZ860** («Remisión y consulta de registros de facturación por servicio web»). IZ862 e IZ863 son para la app gratuita de la AEAT y no sirven para un programa externo |
| Un colaborador social | — |
| Un sucesor | — |

Sirven tanto los certificados de persona física y de representante como los **sellos electrónicos de persona jurídica** (tipo 4 y 8 en @firma), que la AEAT menciona expresamente para procesos automatizados de máquina a máquina. Para un despliegue desatendido, un sello tiene la ventaja de no exponer la identidad de ninguna persona física; la elección concreta es de quien despliega este conector, no de este repositorio.

De quién es el certificado en cada despliegue es una decisión de la empresa que lo despliega, no de este repositorio: aquí solo se documenta lo que el sistema acepta.

En local, la configuración va en `dotnet user-secrets`, fuera del repositorio. Hay dos formas de indicar el certificado; usa solo una:

- **Instalado en el llavero de macOS (recomendado en local):** por su huella SHA-1, sin fichero ni contraseña. `security find-identity -v -p ssl-client` lista las huellas.

  ```bash
  dotnet user-secrets set "VeriFactu:CertificateThumbprint" "<huella>" --project src/VerifactuShopify
  ```

- **Fichero `.pfx`:** `VeriFactu:CertificatePath` con la ruta y `VeriFactu:CertificatePassword` con la contraseña.

Para enviar hacen falta también el emisor de las facturas y el productor del sistema informático. Sin estos últimos, la librería declararía como productor a Irene Solutions, la autora de la librería. En preproducción, con un certificado personal, usa tu propio NIF en ambos para evitar el error 4112:

| Clave | Valor |
|---|---|
| `VeriFactu:Emisor:NIF` / `VeriFactu:Emisor:Nombre` | Obligado a expedir la factura |
| `VeriFactu:SistemaInformatico:NIF` / `…:NombreRazon` | Productor del SIF |
| `VeriFactu:SistemaInformatico:NumeroInstalacion` | Identificador de esta instalación |

Comandos, todos solo contra preproducción:

```bash
dotnet run --project src/VerifactuShopify -- certificado
```

```bash
dotnet run --project src/VerifactuShopify -- enviar-f2
```

```bash
dotnet run --project src/VerifactuShopify -- anular <numserie> <dd-mm-aaaa>
```

```bash
dotnet run --project src/VerifactuShopify -- consultar <aaaa> <mm>
```

```bash
dotnet run --project src/VerifactuShopify -- cadena
```

`certificado` no envía nada, y `consultar` y `cadena` solo leen de la AEAT. La primera vez que un comando habla con la AEAT, macOS pide permiso para usar la clave del llavero. Si no se concede a tiempo, la conexión falla con un timeout.

- **`consultar`** lista lo que la AEAT tiene del emisor en un mes: por cada factura, su último registro con su huella y su encadenamiento.
- **`cadena`** muestra el último registro de la cadena según la AEAT y el local, y termina con error si no coinciden.
- **`enviar-f2` y `anular`** leen primero la cadena de la AEAT (ver [Cadena de registros](#cadena-de-registros)).

## Despliegue

En un despliegue el certificado no sale del almacén del sistema: llega como **fichero de secreto**, se carga en memoria y se le entrega a la librería sin instalarlo ni copiarlo a ninguna parte. La configuración va por variables de entorno, que tienen prioridad sobre `dotnet user-secrets`:

| Variable | Valor |
|---|---|
| `VeriFactu__CertificatePath` | Ruta al `.pfx` montado |
| `VeriFactu__CertificatePasswordPath` | Ruta al fichero con su contraseña |
| `VeriFactu__CertificatePassword` | Alternativa a la anterior, pero el entorno de un proceso se filtra con más facilidad que un fichero |

Cualquier plataforma sabe entregar un fichero a un proceso: un Secret montado en Kubernetes, `secrets` en Docker, `LoadCredential=` en systemd, el agente de Vault o el CSI driver de cualquier nube. Así no hace falta el SDK de ningún proveedor.

**Solo en Linux la clave privada no llega a tocar el disco.** Se carga con `EphemeralKeySet`, que macOS no admite —no sabe cargar una clave sin un llavero, y eso exige escribir— y que en Windows impide autenticarse contra la AEAT. En esos dos sistemas la clave pasa por el almacén del usuario y se borra al liberar el certificado: sirven para desarrollo, no para el despliegue.

El proceso avisa por la salida de error cuando al certificado le quedan 30 días o menos, y falla si ya ha caducado. La AEAT no avisa.

**Huso horario:** los registros se fechan con la hora local del proceso, que tiene que ser la del territorio desde el que se expiden las facturas (art. 7.e de la Orden HAC/1177/2024). Un contenedor usa UTC si no se indica otra cosa: hay que arrancarlo con `TZ=Europe/Madrid` (o `Atlantic/Canary`) y con los datos de zonas horarias, que las imágenes `mcr.microsoft.com/dotnet/runtime` ya traen. Los comandos de envío muestran el huso que están usando.

## Cadena de registros

Cada registro lleva la huella del anterior. La AEAT es la fuente de verdad de esa cadena: antes de enviar o anular, el conector consulta el último registro de su sistema informático en el mes actual y en el anterior, y continúa desde él. Así el despliegue no necesita guardar estado entre ejecuciones (decisión en [#12](https://github.com/alvaromongon/verifactu-shopify/issues/12)).

- **Sin cadena local** (un contenedor recién creado), se carga la de la AEAT. La carpeta de cadenas tiene que estar vacía.
- **Con cadena local** (desarrollo), solo se comprueba que coincide con la de la AEAT. Si no coincide, no se envía nada. Pasa, por ejemplo, después de enviar desde otra máquina. Si la AEAT tiene razón, basta con mover la carpeta `Blockchains` de los datos locales.
- **El mismo NIF puede facturar desde otros sistemas**, como el TPV de Shopify, y cada uno tiene su propia cadena. El conector solo mira los registros de su sistema: el NIF del productor, `IdSistemaInformatico` y `NumeroInstalacion`. Por eso el número de instalación tiene que ser fijo y no repetirse nunca.
- **Solo se anulan facturas del mes actual o del anterior**, que son los dos meses que se consultan. Lo más antiguo se corrige con una rectificativa.
- **Dos ejecuciones a la vez para el mismo emisor romperían la cadena.** Hasta que exista el cerrojo ([#16](https://github.com/alvaromongon/verifactu-shopify/issues/16)), no puede haber más de una a la vez.

## Datos locales de VeriFactu

La librería guarda su configuración, la cadena de bloques y los registros en una carpeta fija que no se puede cambiar, y la crea en cuanto se usa:

| Sistema | Carpeta |
|---|---|
| macOS | `~/Library/Application Support/VeriFactu` |
| Linux | `/usr/share/VeriFactu` |
| Windows | `C:\ProgramData\VeriFactu` |

- En Linux el usuario que ejecuta la aplicación necesita permiso de escritura en esa carpeta ([mdiago/VeriFactu#273](https://github.com/mdiago/VeriFactu/issues/273)). CI la crea antes de los tests.
- En modalidad VERI\*FACTU no hace falta conservar esa carpeta: los registros ya los tiene la AEAT, y la cadena se lee de ella en cada envío. En desarrollo, si se conserva, tiene que coincidir con la de la AEAT.
- La librería escribe y lee esas fechas con la configuración regional del proceso, así que la aplicación la fija a `es-ES`. Sin eso, una cadena escrita en una máquina no se puede leer en otra: un contenedor con la configuración invariante lee `17/09/2026` como mes 17 y falla al iniciarse.

## Seguridad

Nunca subas certificados (`.pfx`, `.p12`), sus contraseñas ni tokens de Shopify. El `.gitignore` excluye los formatos habituales, pero no sustituye a revisar lo que subes.

## Licencia

[AGPL-3.0](LICENSE)
