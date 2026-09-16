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

## Datos locales de VeriFactu

La librería guarda su configuración, la cadena de bloques y los registros en una carpeta fija que no se puede cambiar, y la crea en cuanto se usa:

| Sistema | Carpeta |
|---|---|
| macOS | `~/Library/Application Support/VeriFactu` |
| Linux | `/usr/share/VeriFactu` |

- En Linux el usuario que ejecuta la aplicación necesita permiso de escritura en esa carpeta ([mdiago/VeriFactu#273](https://github.com/mdiago/VeriFactu/issues/273)). CI la crea antes de los tests.
- La cadena de bloques encadena cada registro con el anterior: esa carpeta tiene que sobrevivir a despliegues y copias de seguridad.

## Seguridad

Nunca subas certificados (`.pfx`, `.p12`), sus contraseñas ni tokens de Shopify. El `.gitignore` excluye los formatos habituales, pero no sustituye a revisar lo que subes.

## Licencia

[AGPL-3.0](LICENSE)
