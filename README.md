# verifactu-shopify

Conector entre Shopify y VERI\*FACTU, el sistema de la Agencia Tributaria (AEAT) para registrar facturas. Convierte los pedidos de una tienda Shopify en registros de facturación —simplificadas, completas y rectificativas— y los envía a la AEAT con la librería [mdiago/VeriFactu](https://github.com/mdiago/VeriFactu).

> **Estado: en desarrollo. No lo uses para facturar.** Por ahora solo se prueba contra el entorno de preproducción de la AEAT, que no tiene efectos fiscales. El avance está en los [milestones](https://github.com/alvaromongon/verifactu-shopify/milestones).

## Aviso legal

Este repositorio **no es un sistema informático de facturación (SIF) certificado** y se entrega **sin declaración responsable**.

Según la [FAQ de la AEAT](https://sede.agenciatributaria.gob.es/Sede/iva/sistemas-informaticos-facturacion-verifactu/preguntas-frecuentes/certificacion-sistemas-informaticos-declaracion-responsable.html), la empresa que integra código abierto en su sistema de facturación es quien debe emitir la declaración responsable e indicar qué componentes usa. Si despliegas este código para facturar, **tú eres el productor de tu SIF** y respondes de su cumplimiento.

Nada de lo que hay aquí es asesoramiento fiscal. Consulta con tu asesor antes de usarlo en producción.

## Dependencia: mdiago/VeriFactu

- **Versión fijada: 1.0.65.** Solo se usan versiones para las que el autor ha publicado declaración responsable ([listado](https://github.com/mdiago/VeriFactu/tree/main/NetFramework/Doc/Legal)). Antes de subir de versión hay que comprobar que existe la de la nueva; un test lo recuerda.
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

## Seguridad

Nunca subas certificados (`.pfx`, `.p12`), sus contraseñas ni tokens de Shopify. El `.gitignore` excluye los formatos habituales, pero no sustituye a revisar lo que subes.

## Licencia

[AGPL-3.0](LICENSE)
