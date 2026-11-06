# Plan en sitio 2026-11-05

Scripts: [sitio-2026-11-05.sql](sitio-2026-11-05.sql), [sitio-carpeta-prueba.ps1](sitio-carpeta-prueba.ps1) y, para las fotos de la factura, las secciones 1 y 2 de [pruebas-sitio-2026-11-04.sql](pruebas-sitio-2026-11-04.sql).

Reglas de siempre:
- En producción solo se corre SELECT. Lo que escribe va únicamente a las copias `*_SYNCPRB`.
- Antes de tocar nada en SAINT, revisar dos cosas:
  - que la barra de abajo diga la conexión de PRUEBA;
  - que en SSMS `sysprocesses` muestre la base `_SYNCPRB`.
- Al terminar, `Hide-AccesosPrueba` y comprobar que el hash del `SAINT.CFG` de producción no cambió.

## 1. Fábrica: destrabar la factura (≈ 30 min)

Objetivo: grabar la factura desde el pedido 035850, una completa y otra parcial. Así se ve en vivo si baja el comprometido. Lo demás de F2 ya lo confirmó el histórico.

1. **F.0** en producción (solo lectura). Confirma con datos de noviembre lo que se vio en la copia de octubre: el pedido se borra al facturarlo completo; si es parcial, quedan los saldos.
2. **Opción A: estación de JFM.**
   - Pegar `New-CarpetaPrueba` y correr la llamada `FAB_HJ`.
   - Abrir **PRUEBA FAB_HJ - Configurador → Roles**. El último nodo del árbol debe decir **DESKTOP-HJ. Estación 'DESKTOP-HJ'** y "Venta normal".
     - Si sigue diciendo SERVIDOR, SAINT toma la estación del registro. En ese caso se descarta la A y se pasa a la B.
   - Facturar con **PRUEBA FAB_HJ - Ventas**: cliente 2853060, vendedor 1305, depósito 005, Cargar → Pedido 035850, OC `PRUEBA-SYNC-1` y tipo de operación 1069.
3. **Opción B: clonar la configuración de JFM (si la A no destraba).**
   - Correr **F.2a** y pegar la salida. Ahí se ve qué parámetros de ventas difieren.
   - Si tiene sentido, correr **F.2b** (escribe solo en la copia) y volver a facturar.
4. **Fotos.**
   - Antes y después de facturar: sección 1 con `@ped = '035850'`.
   - Con la factura grabada: sección 2 con `@fac`.
5. **Factura parcial.**
   - Grabar otro pedido (035851) con 3 líneas.
   - Facturarlo cargándolo, pero quitar una línea y bajar la cantidad de otra.
   - Sacar las mismas fotos.
6. **Si nada funciona:** pedir a JFM que grabe las dos facturas en la copia, con la carpeta FAB_HJ y su usuario.

## 2. PV: P1 y P2 (≈ 1 h 30 min)

1. **PV.0** (solo lectura).
   - Pegar la salida.
   - Confirmar el nombre de la base, si SQL Server y SAINT están en la misma máquina, y la ruta de SAINT.
2. **PV.1.**
   - Quitar los comentarios del bloque y revisar `@origen`.
   - Respaldo COPY_ONLY y restauración como `ADM_MAY_SYNCPRB`.
   - Borrar el `.bak` al final.
3. **Carpeta de prueba.** Correr `New-CarpetaPrueba -Nombre PV -Base ADM_MAY_SYNCPRB -Etiqueta 'PRUEBA SYNC PV'`. Ajustar `-Adm` si SAINT está en otra ruta.
4. **P1: compra cargando una OC.** Se hace en PRUEBA PV - Compras, con las fotos PV.2 antes y después.
   - P1a: compra completa, número `PRUEBA-P1A`.
   - P1b: compra parcial, quitando una línea o bajando una cantidad. Número `PRUEBA-P1B`.
   - P1c: intentar cargar otra vez la OC de P1a.
5. **P2: crear una OC en pantalla.** Proveedor `890919355-4`, depósito 115, cantidades 7 y 13. Foto PV.3 antes, después de grabar y después de borrarla. Guardar `SELECT *` de la OC como modelo para comparar con F3.
6. **Si GROUPIT no puede grabar en Compras del PV,** usar el mismo truco de la opción B con el usuario que compra en el PV (módulos 2xx).

## 3. CEDI: P3 (opcional, ≈ 20 min)

Correr `New-CarpetaPrueba -Nombre CEDI -Base BODEGA1_SYNCPRB -Etiqueta 'PRUEBA SYNC CEDI'`, grabar un pedido al cliente `9012380209` (incluir el producto 111216) y comparar con F1, igual que el 035850.

## 4. Cierre

- `Hide-AccesosPrueba` en cada máquina.
- Comprobar el hash del `SAINT.CFG` de producción.
- **Borrar las copias:** solo si ya no quedan pruebas pendientes, con la sección LIMPIEZA TOTAL de `sitio-2026-11-05.sql`.
- Actualizar `notas.md` y redactar el mensaje a Carlos con:
  - los hallazgos (la integración POSTransferSystem viva y el `PrxPedido` que bloquea a SAINT);
  - los resultados de F2/P1/P2;
  - lo que sigue: D5, apagar la integración vieja, Cloudflare y SMTP.
