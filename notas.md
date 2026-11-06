# Notas – SigmaSync

Pendientes y próximos pasos. Se actualiza al cerrar cada sesión o al terminar algo.

## Estado al 2026-10-29

Avance ≈ 60 % de las horas sin contingencia (275–395 h estimadas).

| Fase | Estado |
|---|---|
| Sprint 0 · Diseño | Hecho. `docs/diseno-tecnico.md` y mensaje a Carlos (`docs/mensaje-carlos-2026-10-28.md`) |
| 2 · Infraestructura | Hecho. Hub + agente + PING/PONG con 3 nodos en LocalDB. Falta cloudflared real |
| 3 · F1 (OC → pedido) | Hecho en LocalDB (Fábrica y CEDI). Falta verlo cargar en pantalla de SAINT |
| 4 · F3 (factura sin pedido → OC) | Hecho. Cuadra al centavo con la compra 33570 |
| 4 · F2 (factura desde pedido → ajusta OC) | **Hecho el 2026-11-06** con D5: una OC nueva por factura (como F3) y la OC original queda con el saldo o se borra. 72 pruebas en verde |
| 5 · Operación | Hecho. Panel, alertas, DLQ, `estado`, `reintentar-outbox`, manual. Falta SMTP real |
| 6 · UAT y despliegue | No iniciado. Depende del cliente |

63 pruebas automáticas en verde. Pruebas manuales corridas el 2026-10-29: todas OK salvo el correo, que no se probó (`docs/pruebas-manuales.md`). Las copias quedaron revertidas.

Hallazgos del 2026-10-29:
- En el PV ninguna compra se ha grabado cargando una OC (0 con `ONumero`). P1 es terreno desconocido.
- CEDI: `111216` tiene `SAPROD.Compro` = 151 y no existe ningún pedido. El comprometido tampoco cuadra allí.
- Panel: las alertas de los nodos llegaban en hora local y las del hub en UTC. Corregido el mismo día, junto con el texto de la alerta de recuperación.
- Queda vacía la carpeta `C:\SigmaSyncPruebas` (José la borra).

## Sesión en sitio 2026-11-04 (servidor de Fábrica/CEDI por AnyDesk)

La máquina del PV estaba apagada: solo se trabajó en `SERVIDOR\SQLEXPRESS` (SQL 2017 Express, `SAINT_ADM` y `BODEGA1`). Scripts: `docs/reconocimiento-sitio*.sql` y `docs/pruebas-sitio-2026-11-04.sql`. Salidas pegadas por José en `docs/resultado*.md`.

**Ambiente de pruebas montado (sin tocar producción):**
- Copias `SAINT_ADM_SYNCPRB` y `BODEGA1_SYNCPRB` restauradas desde respaldos COPY_ONLY, en recuperación SIMPLE. **Siguen en el servidor.**
- SAINT lee `SAINT.CFG` de la carpeta "Iniciar en" del acceso directo, no de la del .exe. El archivo está enmascarado con XOR 0xAF. Se armó `C:\SigmaSyncPruebas\SaintPrueba` con su propio `SAINT.CFG` (base `SAINT_ADM_SYNCPRB`, nombre PRUEBASYNCFABRICA) y copias de las DLL. Los accesos "PRUEBA SYNC - …" están en `C:\SigmaSyncPruebas\Accesos`.
- `SAINT.CFG` de producción sin cambios: hash `07455FC1…3197`. Respaldo en `SAINT.CFG.produccion-20261104`, en la misma carpeta.
- Solo en la copia: `PrxPedido` 35850, `SACLIE.LimiteCred` de `2853060` = 99.999.999.999 y configuraciones "Venta normal"/"Punto de venta" creadas a GROUPIT.

**Hallazgos:**
- **Integración vieja viva:** POSTransferSystem (`CEDIProcessorWorker1` → SAINT_ADM como "Planta", y `CEDIProcessorWorker2` → BODEGA1 como "CD"). Son servicios automáticos desde el 16/03/2026, en `C:\cediworker1` y `C:\cediworker2`, y leen una cola en Supabase (`transfer_queue`) cada 5 min. Login `serviceworker`. Hoy solo consulta existencias, pero hace lo mismo que SigmaSync. Hay que apagarla o desinstalarla antes de producción, y la decisión es de Carlos o el cliente. Su `appsettings.json` tiene credenciales en texto plano.
- **La colisión de `PrxPedido` bloquea a SAINT mismo:** al grabar un pedido en pantalla sale el error 1004 "Número del documento de venta ya se encuentra registrado" y no se graba nada. Hoy nadie en Fábrica puede grabar pedidos. Arreglo: `PrxPedido` = 35850 (sucursal 00000).
- Pedido 035850 grabado en pantalla en la copia, comparado columna por columna con F1:
  - Ya coincidían precios (Precio3, que en pantalla sale como "Precio 0"), impuestos y escalas, totales, `OrdenC` (= OC), `CodOper` 1069, vendedor 1305, depósito 005, y el comprometido `Compro`/`CantCom` (+cantidad exacta).
  - Corregido en F1: `Costo` = `CostPro` (antes `CostAct`), `Refere` = `SAPROD.Refere` (antes el código), `ExistAnt` = existencia del depósito, `SaldoAct` = `SACLIE.Saldo`. Prueba nueva `Reproduce_el_pedido_035850_de_SAINT`. **64 pruebas en verde.**
- **El pedido carga en Facturación** (Cargar → Pedido, muestra la OC). La OC no pasa a la factura: SAINT exige escribirla de nuevo. La factura **no se pudo grabar**: Aceptar queda gris con el usuario GROUPIT. En la copia se probó sin éxito: subir el límite de crédito, crear en el Configurador (Roles) la configuración "Venta normal" y "Punto de venta" de GROUPIT, y poner días de vencimiento en 1. La estación SERVIDOR está en modo "Punto de venta"; no se cambió porque puede vivir en el registro (`HKCU\Software\Saint\...\Modo facturación`), que comparte el SAINT de producción en esa máquina. Resolver con JFM, que sí factura (desde DESKTOP-HJ).
- TotalApp (facturación electrónica, The Factory HKA, en producción): el spooler solo vigila `SAINT_ADM` y las estaciones ASGERENCIA, CONTABLE21, GERMAN, INVENTARIO y JHON_MESA. Facturar en la copia desde la estación SERVIDOR no sale a la DIAN.
- Confirmados: CEDI `CodUbic` 100 y `CodVend` 9810. Fábrica factura a `2853060` directo, sin pedido (OrdenC "B19"), a crédito y con 0 días.
- SAINT se conecta como `sa`.

- **F2 resuelto en gran parte por el histórico** (copia de octubre, Fábrica): 18.989 facturas tienen líneas con `OTipo='E'`/`ONumero`, así que la detección de F2 es correcta. Al facturar, SAINT borra el pedido completo; si es parcial, borra las líneas facturadas completas y deja el saldo en las parciales (035828, 035660, 035661). Los pedidos de WORKER 035833–035836 se facturaron en dic-2025. Falta ver si baja el comprometido. Consulta en la sección 3 de `docs/pruebas-sitio-2026-11-04.sql`.

- Configuración de usuarios en SAINT: `SSPARM`/`SSPARD`, con `CodParm = '_U_<usuario>'` (o `_N_xx` para niveles) y un módulo por pantalla. Está en texto plano, así que se puede comparar y clonar en una copia.

**Plan para el 2026-11-05:** `docs/plan-sitio-2026-11-05.md`, con los scripts `docs/sitio-2026-11-05.sql` y `docs/sitio-carpeta-prueba.ps1`. Este último ya está probado: el enmascarado y la sintaxis se validaron en LocalDB.

## Sesión en sitio 2026-11-05 (máquina del PV Mayorista por AnyDesk)

Equipo `SERVIDOR`, con SQL y SAINT en la misma máquina. `@@SERVERNAME` dice `DESKTOP-26TKMEC\SQLEXPRESS` porque el equipo se renombró. SQL 2019 Express, base `ADM_MAY` (compat 120, 1,1 GB). SAINT 9.0.2.6 en `C:\Administrativo 926`. Salidas en `docs/resultados_pv/`, y los scripts `p1-foto.sql` y `p1a-despues.sql` en la misma carpeta.

**Montaje:**
- Copia `ADM_MAY_SYNCPRB` restaurada (COPY_ONLY y luego SIMPLE). **Sigue en el PV.**
- Carpeta `C:\SigmaSyncPruebas\PV` con su propio `SAINT.CFG` (estación SERVIDOR).
- Hash del `SAINT.CFG` de producción: `89F7E3B9…CAA9`.
- Usuario de SAINT: DEICELLY PAOLA CANO RAMIREZ (`CodUsua` 21739589, el mismo que hace casi todas las compras y OC). En el PV no hay `SSPARM` por usuario.
- **Compras de prueba grabadas en la copia:** `PRUEBA-1` y `PRUEBA-2`. El campo "Documento compra" admite 8 caracteres como máximo.

**Hallazgos:**
- Los proveedores `890919355-4` y `8909193545` ya existen en el PV (punto 6 cerrado para este PV).
- **Más integraciones vivas en el PV:**
  - Servicio `SyncPedidos` (`C:\posworker`, POSTransferSystem). Cada 5 min lee las OC de `ADM_MAY`, las sube a Supabase y lleva control en `ADM_MAY.dbo.TransferenciasOC_Control`. La última transferencia es del 29/04/2026. **Es el F1 viejo: si sigue prendido, cada OC se duplicará como pedido.**
  - Tarea programada `sync compras`, que corre cada hora `C:\SyncStoreSaint\auto.exe` (GroupIT 2024, `sigmapi.groupit.com.co`, login SQL `siigo`). El token venció en abril de 2024 y parece muerta. Su `config.ini` tiene claves en texto plano.
  - Tablas `Siigo*` en `ADM_MAY`, de una integración con Siigo (oct-2025).
- El trigger `TR_ADM_INSERTDOCUMENTOCOMPRA` es de SAINT (2022, cifrado). Solo toca las estadísticas `SAECOM`/`SAEPRV`/`SAPROV`. LocalDB ya lo tiene.
- **P1a (compra que carga toda la OC 3567):**
  - SAINT **borra la OC completa**.
  - La compra **no guarda enlace** (`OTipo`, `ONumero` y `OrdenC` en NULL). `OrdenC` solo se llena si se escribe a mano en Totalizar (P1b).
  - `SAPROD.Pedido` y `SAEXIS.CantPed` bajan lo recibido. Si no había `SAEXIS` en el depósito, la crea.
- **P1b (parcial, OC 3538):**
  - Borra las líneas recibidas completas. La línea recibida a medias queda con el saldo en `Cantidad`, pero **sin recalcular `TotalItem`**. La línea no recibida queda igual.
  - En el encabezado **baja `Monto`, pero `MtoTotal` no cambia**. Para leer saldos hay que usar `Cantidad × Costo`.
- **P1c:** la 3567 ya no se puede cargar. La 3538 carga solo el saldo y SAINT lo recalcula con `Cantidad × Costo`.
- **IVA:** las OC del PV siempre tienen `MtoTax = 0` (la pantalla lo muestra, pero no lo guarda). Las compras sí llevan IVA (277 de 277). F3 ya graba la OC sin IVA.
- **P2 (OC 3568 en pantalla con 7 y 13 unidades):**
  - Suben `SAPROD.Pedido` **y** `SAEXIS.CantPed`, y `PrxOrdenC` pasa a 3569.
  - Al borrar la OC el comprometido vuelve exacto, pero el correlativo no se devuelve.
  - El `SELECT *` de la OC hecha en pantalla coincide con lo que graba F3 (`DestinoOc`), salvo `OrdenC`/`Notas1` (intencional) y `Refere`. **Se puede encender `ActualizarPedidoPV`.**
- `SAEXIS.CantPed` de producción ya venía descuadrado contra `SAPROD.Pedido` (ej. 1020 vs. 100).

**Consecuencias para el código:**
- [x] Fila faltante en `SAEXIS` (producto no asignado al depósito): **SAINT no la crea** ni con OC ni con pedido, y solo sube `SAPROD.Pedido`/`Compro`.
  - Pedido 035852 en la copia de Fábrica, grabado y luego borrado: solo un aviso de "excede la existencia", no bloquea.
  - En el PV, la OC 3567 de producción ya estaba así.
  - En pantalla, el PV avisa "Depósito 115 no está asignado para este producto", pero deja grabar. Se confirmó con la OC 3569 en la copia (producto 128100160130): `Pedido` 0→4→0 al grabarla y borrarla, sin crear `SAEXIS` (`resultados_pv/resultado_PV_saexis.md`).
  - F1 y F3 ya hacían eso. Se probó crear la fila en `DestinoOc` y se revirtió (2026-11-06). La compra sí crea la fila (P1a).
- [x] F2 con D5 (2026-11-06). Carlos delegó el diseño. Cada factura de un pedido sincronizado crea una OC nueva idéntica a la factura, y a la OC original se le resta lo facturado: las líneas cumplidas se borran, la OC se borra en cero, y `TotalItem` y los totales se recalculan con `Cantidad × Costo`. El comprometido queda neto. Alertas: `CANTIDAD_EXCEDIDA`, `PRODUCTO_NO_PEDIDO` y `OC_NO_ENCONTRADA`. ACK: `OC_CREADA` + `OC_ACTUALIZADA`/`OC_CERRADA`. Código en `Core/F2/SaldoOc.cs`, `DestinoOc` y `ManejadorFacturaOc`. Diseño (D5, D6, D7 y D14) y manual de operación actualizados.
- [x] F2 de punta a punta en local (hub + 3 agentes, 2026-11-06): dos facturas parciales del pedido 035850 → OC 3560 y 3561, y la 3538 primero con saldo y luego cerrada; comprometido del PV neto. Quedó como paso 10b de `docs/pruebas-manuales.md`, con `db/pruebas/factura-desde-pedido.sql`.
- [ ] Probar F2 en pantalla en la UAT: que el PV cargue la OC nueva en Compras y vea el saldo en la original.
- [ ] Capacitar al PV: **cargar siempre la OC nueva** (por número de factura), no la original.
- [x] Consultas de seguimiento del piloto en el manual (6.9), validadas en LocalDB (2026-11-06). Se decidió no montar alertas automáticas de hábitos: no están en el alcance, y el riesgo se concentra en el primer mes.
- [ ] Definir qué pasa en Fábrica o CEDI cuando el PV borra o recibe una OC que ya generó un pedido: la OC desaparece sin dejar rastro.

### Fábrica, misma noche: F2 en vivo (copia `SAINT_ADM_SYNCPRB`)

Salidas en `docs/resultados_fabrica/` (script `f2-foto.sql`).
- **Opción A** (carpeta `C:\SigmaSyncPruebas\FAB_HJ` con `STATION=DESKTOP-HJ`) **no sirvió**: el Configurador seguía mostrando la estación SERVIDOR. SAINT toma la estación del registro, no del `SAINT.CFG`.
- **Opción B** sí destrabó el Aceptar. F.2b copió a GROUPIT los parámetros de ventas de JFM (módulos 101, 102 y 108), **solo en la copia**. El sospechoso es el parámetro 58 del módulo 102, el único que JFM tenía activo y GROUPIT no.
- **Factura completa `*33647`** desde el pedido 035850:
  - las líneas quedan con `OTipo='E'`/`ONumero='035850'` y el encabezado con NULL;
  - `OrdenC` pasa sola del pedido a la factura;
  - el pedido se borra;
  - `Compro`/`CantCom` bajan exacto.
- **Pedido 035851** grabado en pantalla: `Compro`/`CantCom` suben exacto.
- **Factura parcial `*33648`** (línea 1 completa, línea 2 en 6 de 13, línea 3 sin facturar):
  - la línea 1 se borra del pedido; la línea 2 queda con `Cantidad` 7, pero con `TotalItem` viejo; la línea 3 queda igual;
  - en el encabezado baja `Monto`, pero `MtoTax`/`MtoTotal` no cambian;
  - el comprometido baja solo lo facturado.
- `PrxFact` no cambia: la serie `*336xx` es otra.
- Al grabar el pedido puede imprimirse una copia en la impresora predeterminada, porque esa pantalla no da la opción "Pantalla".

### CEDI, misma noche: P3 (copia `BODEGA1_SYNCPRB`)

Salidas en `docs/resultados_fabrica/resultado_P3.md`, con los scripts `p3-*.sql`.
- GROUPIT ya existía en el CEDI. `p3-clonar-groupit.sql` le copió la configuración de Fábrica (101, 102 y 108), **solo en la copia**.
- **Pedido 000001** grabado en pantalla. Es el primero del CEDI (`PrxPedido` 1→2) y coincide con F1:
  - precios en "Precio 0" = Precio3;
  - escalas `MtoTax`/`MtoTaxO` y `FechaV` = FechaE + 360 días;
  - `SaldoAct`, `ExistAnt`, `Costo` = `CostPro` y `Refere` (el código si está vacío);
  - `CodOper` NULL;
  - `Compro`/`CantCom` suben exacto.
- Los pedidos **no llevan `SATAXVTA`/`SATAXITF`**: de 2.335 pedidos de Fábrica en producción, ninguno tiene filas ahí. F1 está bien.
- **La base del CEDI está atrasada respecto a su SAINT 9.0.2.6.** Le faltan unas 30 columnas que Fábrica sí tiene: `SAFACT.Status`/`Estado`/`NIT`/`TipoTra`…, `SAITEMFAC.Status`/`CantidadC`/`TipoPVP`, `SACORRELSIS.Prefijo`/`Desde`/`Hasta`, y otras de `SACLIE`/`SAPROD`.
  - Al grabar un pedido sale el error "columna 'Status' no es válida", y al facturarlo "'Estado' no es válida".
  - En los dos casos **los datos quedan completos**: la factura `*00001` tiene líneas con `OTipo='E'`, IVA y CxC; el pedido se borra y el comprometido vuelve.
  - Hay que pedir al soporte de SAINT que actualice `BODEGA1` antes de usar pedidos.
- En el CEDI, `OrdenC` **no pasa** del pedido a la factura (en Fábrica sí pasa).
- `Compro` de 111216 volvió a 151 después del ciclo: la anomalía es anterior y ajena.

**Pendiente de esta sesión:**
- [x] P3 en el CEDI (2026-11-05).
- [x] Factura completa y parcial desde pedido (F2), 2026-11-05.
- [x] P1/P2 en el PV (2026-11-05).
- [ ] Fábrica: `Hide-AccesosPrueba` y comprobar el hash `07455FC1…3197`.
- [ ] PV: `Hide-AccesosPrueba` y comprobar el hash `89F7E3B9…CAA9`. Decidir cuándo se borra `ADM_MAY_SYNCPRB`.
- [x] `ActualizarPedidoPV` encendida (2026-11-05): seed en `1`, encendida por defecto si falta la clave, y `0` la apaga. Diseño actualizado (H4, D8, D15). **65 pruebas en verde.**
- [x] Mensaje a Carlos redactado: `docs/mensaje-carlos-2026-11-06.md` (falta que José lo envíe). Temas: integraciones viejas vivas en los 3 nodos (POSTransferSystem, `SyncPedidos`, `sync compras`), `PrxPedido`, base del CEDI atrasada (columnas `Status`/`Estado`), parámetro 58 del módulo 102 que bloquea a GROUPIT, resultados de P1/P2/P3/F2, D5, Cloudflare y SMTP.
- [ ] Al terminar: borrar `*_SYNCPRB` (sección LIMPIEZA), `C:\SigmaSyncPruebas` y el respaldo `SAINT.CFG.produccion-20261104`.
- [ ] Sacar del repo los `docs/resultado*.md`: tienen contraseñas y la llave de Supabase.

## Bloqueado por el cliente / Carlos

1. **Ambiente de pruebas con SAINT instalado** (copias de PV Mayorista, Fábrica y CEDI) y alguien que opere las pantallas:
   - P1: grabar una compra cargando una OC. Ver si SAINT borra, reduce o marca la OC, y qué pasa con `SAPROD.Pedido` / `SAEXIS.CantPed`.
   - P2: crear una OC en pantalla. Confirmar el comprometido del PV (hoy va detrás de una bandera).
   - P3: crear un pedido en el CEDI, que nunca los ha usado, como modelo de referencia.
   - Ver que el pedido de F1 carga en Facturación (buscado por OC) y que la OC de F3 carga en Compras → Cargar.
2. **Condición operativa:** Fábrica y CEDI deben facturar cargando el pedido sincronizado. Si siguen facturando directo, todo entra como F3 y F2 nunca se usa.
3. ~~**D5 – facturación parcial**~~: Carlos delegó el diseño. Resuelto e implementado el 2026-11-06.
4. **Colisión de `PrxPedido` en Fábrica** (35841 vs pedidos 035841–035849 de la integración "WORKER"). Corregir el consecutivo a 35850. Confirmado el 2026-11-04: también bloquea a SAINT (error 1004). WORKER **no** está desinstalado: es POSTransferSystem y sigue corriendo como servicio (ver la sesión del 2026-11-04). Bloquea F1 en producción.
5. **Usuario SYNC** creado desde SAINT en cada base.
6. Proveedores `890919355-4` (Fábrica) y `8909193545` (CEDI) creados en cada PV.
7. Número real de PV y código de cliente de cada uno en Fábrica y CEDI.
8. Servidor del hub (Fábrica): 24/7, salida 443, cuenta/dominio de Cloudflare. Datos SMTP.
9. Confirmar latencia < 2 min. (`CodUbic` 100 y `CodVend` 9810 del CEDI: confirmados el 2026-11-04.)
10. Usuario de SAINT con permiso para facturar, para la prueba de F2 en la copia.

## Siguiente de mi lado (sin depender del cliente)

- [x] Correr la lista de verificación manual (2026-10-29).
- [x] Guion de pruebas para el ambiente del cliente: `docs/guion-pruebas-cliente.md` (P1–P4 y hoja de resultados). Enviarlo a Carlos con la solicitud del ambiente.
- [x] Corregir las horas de `hub.Alerta` (UTC vs. local) y el texto de la alerta de recuperación (2026-10-29).
- [ ] Redactar el mensaje a Carlos: seguimiento del 28/10, guion de pruebas y propuesta de montar nosotros el ambiente de pruebas en una máquina aislada (licencia SAINT, cómo elige la base, autorización, usuario operador).
- [x] F2 implementado con D5 (2026-11-06).
- [ ] Hacer seguimiento al mensaje de Carlos del 2026-10-28.
