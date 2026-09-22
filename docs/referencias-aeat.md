# Referencias normativas y técnicas

Fuentes de la AEAT y del BOE en las que se apoyan las decisiones del conector, con lo que dice cada una sobre los puntos que nos afectan. Solo se enlazan, sin copiarlas: la AEAT sustituye los PDF en la misma URL al publicar versiones nuevas, así que se anota la versión consultada y su huella SHA-256 para detectar cuándo han cambiado.

Consultadas el 22-09-2026.

## Fuentes

| Documento | Versión | Enlace |
|---|---|---|
| Real Decreto 1007/2023, Reglamento de requisitos de los SIF (RRSIF) | Consolidado, última actualización 03-12-2025 | [BOE-A-2023-24840](https://www.boe.es/buscar/act.php?id=BOE-A-2023-24840) |
| Orden HAC/1177/2024, especificaciones técnicas | Consolidado, última actualización 28-10-2024 | [BOE-A-2024-22138](https://www.boe.es/buscar/act.php?id=BOE-A-2024-22138) |
| FAQ de la AEAT: trazabilidad | Página actualizada 22-07-2026 | [sede.agenciatributaria.gob.es](https://sede.agenciatributaria.gob.es/Sede/iva/sistemas-informaticos-facturacion-verifactu/preguntas-frecuentes/caracteristicas-requisitos-sif-trazabilidad.html) |
| FAQ de la AEAT: conservación, accesibilidad y legibilidad | Página actualizada 22-07-2026 | [sede.agenciatributaria.gob.es](https://sede.agenciatributaria.gob.es/Sede/iva/sistemas-informaticos-facturacion-verifactu/preguntas-frecuentes/caracteristicas-requisitos-sif-conservacion-accesibilidad-legibilidad.html) |
| FAQ de la AEAT: integridad e inalterabilidad | Página actualizada 22-07-2026 | [sede.agenciatributaria.gob.es](https://sede.agenciatributaria.gob.es/Sede/iva/sistemas-informaticos-facturacion-verifactu/preguntas-frecuentes/caracteristicas-requisitos-sif-integridad-inalterabilidad.html) |
| Aclaraciones a dudas de los desarrolladores (PDF) | 04-12-2025 · `73906dc8…c09d5e` | [FAQs-Desarrolladores.pdf](https://sede.agenciatributaria.gob.es/static_files/AEAT_Desarrolladores/EEDD/IVA/VERI-FACTU/FAQs-Desarrolladores.pdf) |
| Descripción del servicio web (PDF) | 1.0.3 · `b3570f6a…9e553c02` | [Veri-Factu_Descripcion_SWeb.pdf](https://www.agenciatributaria.es/static_files/AEAT_Desarrolladores/EEDD/IVA/VERI-FACTU/Veri-Factu_Descripcion_SWeb.pdf) |
| Validaciones y errores (PDF) | 1.2.2 · `426eb926…2d3513440` | [Validaciones_Errores_Veri-Factu.pdf](https://www.agenciatributaria.es/static_files/AEAT_Desarrolladores/EEDD/IVA/VERI-FACTU/Validaciones_Errores_Veri-Factu.pdf) |
| Especificaciones de la huella (PDF) | 0.1.2 · `f4334c25…2653d7de` | [Veri-Factu_especificaciones_huella_hash_registros.pdf](https://www.agenciatributaria.es/static_files/AEAT_Desarrolladores/EEDD/IVA/VERI-FACTU/Veri-Factu_especificaciones_huella_hash_registros.pdf) |

Portal con el resto de documentación técnica (esquemas XSD, WSDL, QR): [Sistemas Informáticos de Facturación y Sistemas VERI\*FACTU](https://www.agenciatributaria.es/AEAT.desarrolladores/Desarrolladores/_menu_/Documentacion/Sistemas_Informaticos_de_Facturacion_y_Sistemas_VERI_FACTU/Sistemas_Informaticos_de_Facturacion_y_Sistemas_VERI_FACTU.html).

Para comprobar si un PDF ha cambiado:

```bash
curl -sL <url> | shasum -a 256
```

## Conservación de los registros

- **Un SIF VERI\*FACTU no está obligado a conservar los registros.** El art. 3 de la Orden exime a estos sistemas de los arts. 6.b–f, 7.f, 7.h, 7.i, 7.j, 8 (conservación) y 9 (registro de eventos). La FAQ de conservación lo confirma para el SIF y para el obligado tributario: no tienen que conservarlos «ya que los han remitido a la Agencia Tributaria».
- **Si se pierden, se recuperan de la AEAT.** Es la respuesta de la FAQ de integridad a la pérdida total de registros por un virus o incidente similar.
- La obligación de conservar las **facturas** (art. 19.1 del Real Decreto 1619/2012) sigue en pie, pero es otra cosa: las facturas viven en Shopify.

## Encadenamiento

- **Una única cadena por par (SIF, obligado tributario)** (Orden, art. 7.c), con las altas y las anulaciones en orden de generación (art. 7.d). La FAQ de trazabilidad lo repite y añade que un cambio de año o de serie no afecta.
- Cada registro lleva el NIF, el número y la fecha de la factura del registro anterior, y su huella (art. 7.a).
- **`PrimerRegistro` solo en el primer registro desde la instalación** (art. 7.b).
- **El SIF se identifica por el NIF del productor, `IdSistemaInformatico` y `NumeroInstalacion`**, y ese número no puede repetirse nunca (FAQ de desarrolladores, apartado 4). Reinstalar es crear otro SIF. En un despliegue sin estado, el número tiene que venir de la configuración: la librería usa la MAC si no se indica, y la MAC de un contenedor cambia en cada ejecución.
- La fecha y hora de generación son las del territorio desde el que se expide la factura, con su huso horario (art. 7.e y 7.g).

## Qué valida la AEAT al recibir un registro

Según el documento de validaciones, la AEAT **no rechaza** un registro mal encadenado:

- De la huella del registro anterior solo comprueba el formato: SHA-256, 64 caracteres hexadecimales en mayúsculas. Si falla, es un aviso, no un rechazo.
- Una huella propia que no coincide con la que calcula la AEAT es un error **admisible**: el registro se acepta, pero hay que subsanarlo.
- Marcar `PrimerRegistro = S` cuando ya hay registros para ese SIF y ese NIF también es un error admisible.

Por tanto, un «Correcto» no demuestra que la cadena esté bien. Hay que comprobarla por nuestra cuenta.

## Consulta de registros presentados

Descripción del servicio web, apartado 6.4:

- Solo disponible en remisión voluntaria (VERI\*FACTU).
- Filtro obligatorio: ejercicio y periodo **de imputación**, que salen de la fecha de operación o, si no la hay, de la de expedición. No se puede consultar por fecha de generación ni de presentación.
- Filtros opcionales dentro del periodo: número de factura, rango de fechas de expedición, contraparte, SIF (desde la versión 1.0.2) y `RefExterna`, un campo libre de hasta 60 caracteres que pone el propio SIF.
- Como máximo 10.000 registros por respuesta, paginados por fecha de presentación. El coste depende del volumen de un mes, no del histórico.
- Por cada factura devuelve su último registro, sea alta o anulación. No está documentado; se comprobó en preproducción el 22-09-2026.
- No documenta límites de uso.

## Remisión

- **Control de flujo:** de entrada hay que esperar 60 segundos entre envíos, y la AEAT actualiza ese valor en cada respuesta. También se puede enviar antes si se completa el lote máximo (Orden, art. 16.2).
- **Ante una incidencia:** hay que reintentar al menos una vez por hora, respetando el orden de generación, y marcar el envío como afectado por la incidencia (art. 16.4).
