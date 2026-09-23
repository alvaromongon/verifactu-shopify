# verifactu-shopify

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

```bash
dotnet run --project src/VerifactuShopify -- sincronizar
```

`certificado` no envía nada, y `consultar` y `cadena` solo leen de la AEAT. La primera vez que un comando habla con la AEAT, macOS pide permiso para usar la clave del llavero. Si no se concede a tiempo, la conexión falla con un timeout.

- **`consultar`** lista lo que la AEAT tiene del emisor en un mes: por cada factura, su último registro con su huella y su encadenamiento.
- **`cadena`** muestra el último registro de la cadena según la AEAT y el local, y termina con error si no coinciden.
- **`enviar-f2` y `anular`** leen primero la cadena de la AEAT (ver [Cadena de registros](#cadena-de-registros)).
- **`sincronizar`** envía los pedidos pagados de Shopify que aún no tengan registro (ver [Sincronización con Shopify](#sincronización-con-shopify)).

## Sincronización con Shopify

`sincronizar` lee los pedidos pagados de la tienda y envía a la AEAT un registro por cada uno que aún no lo tenga. No sustituye a la facturación de la tienda: por ahora solo va a preproducción. Cada ejecución hace una sola pasada. Lo previsto es lanzarla cada pocos minutos, con un cronjob o similar, y nunca dos a la vez hasta que exista el cerrojo ([#16](https://github.com/alvaromongon/verifactu-shopify/issues/16)). El diseño está en [#4](https://github.com/alvaromongon/verifactu-shopify/issues/4).

### App de Shopify

- **Se crea en el [Dev Dashboard](https://dev.shopify.com)**, en la misma organización que la tienda, y se instala en ella.
- **Solo pide `read_orders`** y ningún dato protegido de cliente. El permiso tiene que estar en una versión **publicada** de la app y aprobado en la tienda: si no, el token llega sin permisos y el conector lo indica. [`shopify.app.example.toml`](shopify.app.example.toml) es la plantilla. El `shopify.app.toml` real no se sube al repositorio.
- **El token se pide en cada ejecución** con el client ID y el secreto ([client credentials grant](https://shopify.dev/docs/apps/build/authentication-authorization/client-credentials-grant)). No se guarda en ningún sitio. Esta vía solo funciona si la app y la tienda están en la misma organización.

### Configuración

| Clave | Valor |
|---|---|
| `Shopify:Tienda` | Dominio `*.myshopify.com` de la tienda, no el público. Está en el admin, en Configuración > Dominios |
| `Shopify:ClientId` / `Shopify:ClientSecret` | Credenciales de la app en el Dev Dashboard |
| `Sincronizacion:Desde` | Fecha y hora de corte, p. ej. `2026-09-23T00:00:00+02:00`. Los pedidos cobrados antes no se facturan |
| `Sincronizacion:MargenMinutos` | Espera desde el cobro antes de facturar. Por defecto, 10 |
| `Facturacion:Prefijo` | Prefijo de la serie, solo letras y dígitos, p. ej. `PRE` |
| `Facturacion:Semilla` | Opcional, `aaaa:n`: último número usado ese año fuera del conector. Solo si se continúa una serie existente (ver abajo) |
| `Facturacion:LimiteSimplificada` | Importe máximo de una F2. Por defecto, 400 |

### Qué se factura

- **Un pedido se factura cuando**:
  - está pagado;
  - no está cancelado ni es de prueba;
  - se cobró después de la fecha de corte;
  - y ha pasado el margen.
- **Un reembolso anterior a la factura la deja fuera.** Esos pedidos llegarán con [#5](https://github.com/alvaromongon/verifactu-shopify/issues/5).
- **Todo sale como F2** hasta que el checkout recoja el NIF.
- **Algunos pedidos no se envían y se informan para revisarlos a mano**, y la ejecución termina con código 2:
  - una línea sin IVA o con más de un tipo;
  - un pedido editado después del checkout;
  - un pedido por encima del límite de la F2.

  No gastan número. Vuelven a salir en cada ejecución hasta que se resuelvan.
- **Serie propia**, `{prefijo}-{aaaa}-{nnnnnn}`, compartida por F1 y F2. Se reinicia cada año. El último número usado se lee de la AEAT.
  - **Con una serie nueva no hace falta semilla**: empieza en 1. Es el caso de preproducción, con su propio prefijo.
  - **Al pasar a producción hay que decidir** si se continúa la serie con la que la tienda ya facturaba o se abre una nueva. El Reglamento de facturación (RD 1619/2012, art. 6.1.a) exige numeración correlativa dentro de cada serie y admite series separadas «cuando existan razones que lo justifiquen». Consúltalo con tu asesor ([#4](https://github.com/alvaromongon/verifactu-shopify/issues/4#issuecomment-5796211394)).
  - **Para continuar una serie existente**, `Facturacion:Semilla` indica el último número usado. El formato del número tiene que ser el mismo, y desde el corte toda venta tiene que entrar como pedido de Shopify, porque las ventas que no pasan por el conector gastarían números de la misma serie.
- **Fecha de expedición**: el día en que se envía.

### Sin duplicados y sin estado

La descripción de cada registro empieza por el pedido: `Pedido #1001 (5812345678901): …`. Antes de enviar, el conector lee de la AEAT los registros de su sistema informático del mes actual y del anterior, y no vuelve a facturar ningún pedido que aparezca en ellos. Cuentan también los de facturas anuladas. No se usa `RefExterna` porque la librería lo sobrescribe ([mdiago/VeriFactu#294](https://github.com/mdiago/VeriFactu/issues/294)).

- **Si un envío se queda sin respuesta**, la siguiente ejecución lo comprueba en la AEAT: si no llegó, lo envía otra vez con el mismo número.
- **Si la AEAT rechaza un registro**, la ejecución se para y termina con error.

### Códigos de salida

| Código | Significado |
|---|---|
| 0 | Todo enviado, o nada que enviar |
| 1 | Error: la AEAT rechazó un registro, Shopify o la AEAT no respondieron, o la configuración no es válida. Hay que mirarlo |
| 2 | Todo lo que se podía enviar se envió, pero quedan pedidos para revisar a mano |

Separar el 2 del 1 sirve para que la alerta del planificador salte solo cuando algo se rompe. Un pedido pendiente de revisión vuelve a salir en cada ejecución hasta que se resuelve.
- **Un conector parado más de un mes pierde pedidos.** Los cobrados antes del mes anterior ya no se pueden comprobar, así que no se facturan solos.

## Despliegue

En un despliegue el certificado no sale del almacén del sistema: llega como **fichero de secreto**, se carga en memoria y se le entrega a la librería sin instalarlo ni copiarlo a ninguna parte. La configuración va por variables de entorno, que tienen prioridad sobre `dotnet user-secrets`:

| Variable | Valor |
|---|---|
| `VeriFactu__CertificatePath` | Ruta al `.pfx` montado |
| `VeriFactu__CertificatePasswordPath` | Ruta al fichero con su contraseña |
| `VeriFactu__CertificatePassword` | Alternativa a la anterior, pero el entorno de un proceso se filtra con más facilidad que un fichero |
| `Shopify__ClientSecretPath` | Ruta al fichero con el secreto de la app de Shopify (o `Shopify__ClientSecret`, con la misma salvedad) |

Cualquier plataforma sabe entregar un fichero a un proceso: un Secret montado en Kubernetes, `secrets` en Docker, `LoadCredential=` en systemd, el agente de Vault o el CSI driver de cualquier nube. Así no hace falta el SDK de ningún proveedor.

**Solo en Linux la clave privada no llega a tocar el disco.** Se carga con `EphemeralKeySet`, que macOS no admite —no sabe cargar una clave sin un llavero, y eso exige escribir— y que en Windows impide autenticarse contra la AEAT. En esos dos sistemas la clave pasa por el almacén del usuario y se borra al liberar el certificado: sirven para desarrollo, no para el despliegue.

El proceso avisa por la salida de error cuando al certificado le quedan 30 días o menos, y falla si ya ha caducado. La AEAT no avisa.

**Huso horario:** los registros se fechan con la hora local del proceso, que tiene que ser la del territorio desde el que se expiden las facturas (art. 7.e de la Orden HAC/1177/2024). Un contenedor usa UTC si no se indica otra cosa: hay que arrancarlo con `TZ=Europe/Madrid` (o `Atlantic/Canary`) y con los datos de zonas horarias, que las imágenes `mcr.microsoft.com/dotnet/runtime` ya traen. Los comandos de envío muestran el huso que están usando.

## Cadena de registros

Cada registro lleva la huella del anterior. La AEAT es la fuente de verdad de esa cadena: antes de enviar o anular, el conector consulta el último registro de su sistema informático en el mes actual y en el anterior, y continúa desde él. Así el despliegue no necesita guardar estado entre ejecuciones (decisión en [#12](https://github.com/alvaromongon/verifactu-shopify/issues/12)).

- **Sin cadena local** (un contenedor recién creado), se carga la de la AEAT. La carpeta de cadenas tiene que estar vacía.
- **Con cadena local** (desarrollo), solo se comprueba que coincide con la de la AEAT. Si no coincide, no se envía nada. Pasa, por ejemplo, después de enviar desde otra máquina. Si la AEAT tiene razón, basta con mover la carpeta `Blockchains` de los datos locales.
- **El mismo NIF puede facturar desde otros sistemas**, como el TPV de Shopify, y cada uno tiene su propia cadena. El conector solo mira los registros de su sistema: el NIF del productor, `IdSistemaInformatico` y `NumeroInstalacion`. Por eso el número de instalación tiene que ser fijo y no repetirse nunca.
- **Si el último registro tiene más de un mes, hoy no se encuentra**: el siguiente saldría como primer registro y la cadena quedaría rota. Pasa si la tienda pasa más de un mes sin ventas o el conector está parado. Pendiente en [#22](https://github.com/alvaromongon/verifactu-shopify/issues/22).
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
