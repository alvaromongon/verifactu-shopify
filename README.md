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

La AEAT solo acepta certificados de una autoridad reconocida, también en preproducción; uno autofirmado se rechaza. En local, la configuración va en `dotnet user-secrets`, fuera del repositorio. Hay dos formas de indicar el certificado; usa solo una:

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

`certificado` no envía nada. `enviar-f2` y `anular` escriben en la cadena de bloques local y la primera vez macOS pide permiso para usar la clave del llavero.

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

## Datos locales de VeriFactu

La librería guarda su configuración, la cadena de bloques y los registros en una carpeta fija que no se puede cambiar, y la crea en cuanto se usa:

| Sistema | Carpeta |
|---|---|
| macOS | `~/Library/Application Support/VeriFactu` |
| Linux | `/usr/share/VeriFactu` |
| Windows | `C:\ProgramData\VeriFactu` |

- En Linux el usuario que ejecuta la aplicación necesita permiso de escritura en esa carpeta ([mdiago/VeriFactu#273](https://github.com/mdiago/VeriFactu/issues/273)). CI la crea antes de los tests.
- La cadena de bloques encadena cada registro con el anterior: esa carpeta tiene que sobrevivir a despliegues y copias de seguridad.
- La librería escribe y lee esas fechas con la configuración regional del proceso, así que la aplicación la fija a `es-ES`. Sin eso, una cadena escrita en una máquina no se puede leer en otra: un contenedor con la configuración invariante lee `17/09/2026` como mes 17 y falla al iniciarse.

## Seguridad

Nunca subas certificados (`.pfx`, `.p12`), sus contraseñas ni tokens de Shopify. El `.gitignore` excluye los formatos habituales, pero no sustituye a revisar lo que subes.

## Licencia

[AGPL-3.0](LICENSE)
