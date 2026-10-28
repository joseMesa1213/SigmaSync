# Pruebas manuales – SigmaSync (estado al 2026-10-28)

Guía para comprobar, con las copias SAINT en LocalDB, que lo construido funciona y que los resultados coinciden con los documentos reales que ya conocemos: el pedido `035829` grabado en SAINT, las OC `000003538` y `000003465` del PV, las facturas `*33570` y `009950`, y las compras `33570` y `009950` que el PV digitó a mano.

> **Regla de oro:** todo se hace en `(localdb)\MSSQLLocalDB`. Antes de las pruebas de punta a punta se toman *snapshots* de las tres copias (paso 2) y al final se revierten (paso 14), para dejarlas exactamente como estaban.

Tiempo estimado: unos 45 minutos (más 15 si se prueba el correo).

---

## 0. Requisitos

- SDK .NET 10 y las copias `ADM_MAY`, `SAINT_ADM` y `BODEGA1` en LocalDB. Ya tienen el esquema `sync` y las semillas.
- Base del hub `SigmaSyncHub` en LocalDB y nodos registrados. Ya están creados.
- Credenciales de desarrollo de cada nodo en `src\SigmaSync.Agent\bin\Debug\net10.0\appsettings.<NODO>.local.json` (cifradas para esta máquina).

Si se borró la carpeta `bin`, hay que regenerar las credenciales:

```powershell
$hub = "src\SigmaSync.Hub\bin\Debug\net10.0\SigmaSync.Hub.exe"
& $hub registrar-nodo PV_MAY "PV Mayorista"   # repetir con FAB y CEDI
# Copiar ApiKey y HmacSecret a src\SigmaSync.Agent\bin\Debug\net10.0\appsettings.<NODO>.local.json:
# { "Agente": { "ApiKey": "...", "HmacSecret": "..." } }
```

Todas las consultas SQL de esta guía se pueden ejecutar en SSMS, Azure Data Studio o `sqlcmd -S "(localdb)\MSSQLLocalDB" -E`.

---

## 1. Pruebas automáticas

Desde la raíz del repositorio:

```powershell
dotnet build SigmaSync.slnx
dotnet test SigmaSync.slnx
```

**Esperado:** 63 pruebas en verde, en tres proyectos:

| Proyecto | Pruebas | Qué cubre |
|---|---|---|
| `SigmaSync.Contracts.Tests` | 12 | JSON del contrato (snake_case, compatibilidad v1/v1.1) y firma HMAC |
| `SigmaSync.Core.Tests` | 17 | Cálculos al centavo: pedido `035829`, OC desde `*33570` y `009950`, exentos, descuentos |
| `SigmaSync.Integration.Tests` | 34 | Hub (orden, DLQ, seguridad), agente (idempotencia, rechazos), F1 y F3 contra las copias SAINT, operación (alertas, correo, panel) |

Las pruebas contra las copias SAINT corren dentro de transacciones que se deshacen: no dejan rastro.

**Comprobación de que no quedó rastro:**

```sql
SELECT (SELECT ValueInt FROM SAINT_ADM.dbo.SACORRELSIS WHERE CodSucu='00000' AND FieldName='PrxPedido') AS prx_pedido_fab,   -- 35841
       (SELECT ValueInt FROM BODEGA1.dbo.SACORRELSIS   WHERE CodSucu='00000' AND FieldName='PrxPedido') AS prx_pedido_cedi,  -- 1
       (SELECT ValueInt FROM ADM_MAY.dbo.SACORRELSIS   WHERE CodSucu='00000' AND FieldName='PrxOrdenC') AS prx_oc_pv,        -- 3560
       (SELECT COUNT(*) FROM SAINT_ADM.dbo.SAFACT WHERE CodUsua='SYNC') + (SELECT COUNT(*) FROM BODEGA1.dbo.SAFACT WHERE CodUsua='SYNC')
     + (SELECT COUNT(*) FROM ADM_MAY.dbo.SACOMP WHERE CodUsua='SYNC') AS docs_sync;                                               -- 0
```

---

## 2. Tomar snapshots de las copias

En PowerShell, desde la raíz del repositorio:

```powershell
$snap = "C:\SigmaSyncPruebas\snap"; New-Item -ItemType Directory -Force $snap | Out-Null
foreach ($db in 'ADM_MAY','SAINT_ADM','BODEGA1') {
  $files = sqlcmd -S "(localdb)\MSSQLLocalDB" -E -h -1 -W -Q "SET NOCOUNT ON; SELECT name FROM sys.master_files WHERE database_id = DB_ID('$db') AND type = 0"
  $on = ($files | Where-Object { $_.Trim() } | ForEach-Object { "(NAME = [$($_.Trim())], FILENAME = '$snap\$db`_$($_.Trim()).ss')" }) -join ', '
  sqlcmd -S "(localdb)\MSSQLLocalDB" -E -b -Q "CREATE DATABASE [$db`_snap] ON $on AS SNAPSHOT OF [$db];"
}
```

**Esperado:** existen `ADM_MAY_snap`, `SAINT_ADM_snap` y `BODEGA1_snap` (`SELECT name FROM sys.databases WHERE name LIKE '%_snap'`).

---

## 3. Arrancar el hub y los tres agentes

Cuatro terminales en la raíz del repositorio. Las variables `$hub` y `$ag` se usan en el resto de la guía:

```powershell
$hub = "src\SigmaSync.Hub\bin\Debug\net10.0\SigmaSync.Hub.exe"
$ag  = "src\SigmaSync.Agent\bin\Debug\net10.0\SigmaSync.Agent.exe"
```

| Terminal | Comando |
|---|---|
| 1 | `& $hub` |
| 2 | `& $ag --nodo PV_MAY` |
| 3 | `& $ag --nodo FAB` |
| 4 | `& $ag --nodo CEDI` |

**Esperado:**
- El hub muestra `Now listening on: http://localhost:5080`.
- Cada agente muestra `SigmaSync.Agent 0.1.0 iniciando: nodo X (ROL)`.
- No aparecen líneas `FTL` ni `ERR`.

Antes de arrancar, se puede validar cada nodo:

```powershell
& $ag validar --nodo PV_MAY   # ✓ esquema sync, configuración y columnas SAINT de PV_MAY (PV) correctos
& $ag validar --nodo FAB
& $ag validar --nodo CEDI
```

> La captura automática solo toma documentos de los últimos 3 días y desde `CapturaDesde` (la fecha de instalación). Las copias no tienen documentos recientes, así que no se captura nada solo: los documentos se envían a mano con `enviar-oc` y `enviar-factura`.

---

## 4. Conectividad: PING / PONG

En una quinta terminal:

```powershell
& $ag ping --nodo PV_MAY --destino FAB
& $ag ping --nodo PV_MAY --destino CEDI
```

**Esperado:** en unos 30 s aparece en la terminal 2 `PONG de FAB … ida y vuelta NN s`, y lo mismo del CEDI. En SQL:

```sql
SELECT ts, mensaje FROM ADM_MAY.sync.Log WHERE componente = 'Ping' ORDER BY id DESC;
```

---

## 5. F1 en Fábrica: OC `000003538` → pedido

Antes de enviar, guardar el comprometido de un producto para comparar después:

```sql
SELECT p.Compro, e.CantCom FROM SAINT_ADM.dbo.SAPROD p
JOIN SAINT_ADM.dbo.SAEXIS e ON e.CodProd = p.CodProd AND e.CodUbic = '005'
WHERE p.CodProd = '1I21033062400P4';
```

```powershell
& $ag enviar-oc --nodo PV_MAY --numero 000003538     # OC 000003538 encolada hacia FAB.
```

**Esperado en la terminal 3 (FAB):**
- La alerta `CONSECUTIVO_COLISION: PrxPedido apuntaba a pedidos que ya existen (035841 … 035849); se usó 035850`. Es la colisión que dejó WORKER: el agente la salta y avisa.
- La línea `OC 000003538 de PV_MAY → pedido 035850`.

**Comprobar el pedido:**

```sql
SELECT NumeroD, CodClie, OrdenC, CodUbic, CodVend, CodOper, CodUsua, FechaE, FechaV, Monto, MtoTax, MtoTotal, Notas1
FROM SAINT_ADM.dbo.SAFACT WHERE TipoFac = 'E' AND CodUsua = 'SYNC';

SELECT NroLinea, CodItem, Cantidad, Precio, PriceO, TotalItem, MtoTax, MtoTaxO, Costo
FROM SAINT_ADM.dbo.SAITEMFAC WHERE TipoFac = 'E' AND NumeroD = '035850' ORDER BY NroLinea;
```

| Campo | Esperado |
|---|---|
| NumeroD / CodClie / OrdenC | `035850` / `2853060` / `000003538` |
| CodUbic / CodVend / CodOper | `005` / `1305` / `1069` (los de las facturas de Fábrica al PV) |
| FechaV | Igual a la fecha de FechaE, sin hora (DiasCred 0) |
| Monto / MtoTax / MtoTotal | 9.073.342,40 / 1.723.935,31 / 10.797.277,71 |
| Notas1 | `SYNC PV_MAY OC 000003538` |

| Línea | Producto | Cant. | Precio | PriceO | TotalItem | MtoTax |
|---|---|---|---|---|---|---|
| 1 | 1I21085125150SM | 250 | 9.436,97 | 9.436,975 | 2.359.242,50 | 448.256,303 |
| 2 | 1I24110120130PP | 90 | 5.180,01 | 5.180,010 | 466.200,90 | 88.578,171 |
| 3 | 1I21033062400P4 | 180 | 19.117,65 | 19.117,647 | 3.441.177,00 | 653.823,529 |
| 4 | 1I21100140140T1 | 200 | 14.033,61 | 14.033,613 | 2.806.722,00 | 533.277,311 |

**Comparaciones con datos reales:**

1. **Precio = lista 3 de Fábrica, y no el costo de la OC:** la línea 3 vale 19.117,65, el mismo precio de esa referencia en la factura `*33570`. La OC trae 18.161,76, que ya incluye el 5 % de descuento.

   ```sql
   SELECT 'factura *33570' doc, Precio FROM SAINT_ADM.dbo.SAITEMFAC WHERE TipoFac='A' AND NumeroD='*33570' AND CodItem='1I21033062400P4'
   UNION ALL SELECT 'pedido 035850', Precio FROM SAINT_ADM.dbo.SAITEMFAC WHERE TipoFac='E' AND NumeroD='035850' AND CodItem='1I21033062400P4'
   UNION ALL SELECT 'OC 000003538 (costo PV)', Costo FROM ADM_MAY.dbo.SAITEMCOM WHERE TipoCom='L' AND NumeroD='000003538' AND CodItem='1I21033062400P4';
   ```

2. **Mismos campos que un pedido de SAINT:** comparar a ojo las columnas llenas del pedido `035850` con las del `035829`, que se grabó en pantalla. La prueba automática `F1Tests` lo hace columna por columna; la única diferencia esperada es `Notas1`.

   ```sql
   SELECT * FROM SAINT_ADM.dbo.SAFACT WHERE TipoFac = 'E' AND NumeroD IN ('035829', '035850');
   SELECT * FROM SAINT_ADM.dbo.SAITEMFAC WHERE TipoFac = 'E' AND NumeroD IN ('035829', '035850') ORDER BY NumeroD, NroLinea;
   ```

3. **Comprometido:** repetir la consulta del inicio de este paso. `Compro` y `CantCom` deben subir en 180.
4. **Consecutivo:** `SELECT ValueInt FROM SAINT_ADM.dbo.SACORRELSIS WHERE CodSucu='00000' AND FieldName='PrxPedido'` → 35851.
5. **Relación OC ↔ pedido en ambos lados:**

   ```sql
   SELECT 'FAB' n, * FROM SAINT_ADM.sync.DocRef UNION ALL SELECT 'PV', * FROM ADM_MAY.sync.DocRef;
   -- Ambos con OC_PEDIDO · PV_MAY L 000003538 → FAB E 035850
   ```

---

## 6. F1 en el CEDI: OC `000003465` → pedido

```powershell
& $ag enviar-oc --nodo PV_MAY --numero 000003465     # OC 000003465 encolada hacia CEDI.
```

```sql
SELECT NumeroD, CodClie, OrdenC, CodUbic, CodVend, CodOper, Monto, MtoTax, MtoTotal
FROM BODEGA1.dbo.SAFACT WHERE TipoFac = 'E' AND CodUsua = 'SYNC';
```

| Campo | Esperado |
|---|---|
| NumeroD | `000001` (el CEDI nunca había usado pedidos: `PrxPedido` era 1) |
| CodClie | `9012380209` (el código del PV en el CEDI, distinto del de Fábrica) |
| CodUbic / CodVend / CodOper | `100` / `9810` / NULL |
| Monto / MtoTax / MtoTotal | 509.793,00 / 96.860,67 / 606.653,67 |

**Comparación:** en la línea del producto `111216`, `MtoTax` = 23.516,87 (20 × 6.188,65 × 19 %). Este valor muestra que se respetan las escalas decimales del CEDI, distintas de las de Fábrica.

---

## 7. Idempotencia

```powershell
& $ag enviar-oc --nodo PV_MAY --numero 000003538
```

**Esperado:** `OC 000003538 no se encoló: ya tenía mensaje en sync.Outbox o no tiene ítems.`, y sigue habiendo un solo pedido con `OrdenC = '000003538'`.

---

## 8. F3 desde Fábrica: factura `*33570` → OC en el PV

```powershell
& $ag enviar-factura --nodo FAB --numero "*33570"    # Factura *33570 encolada hacia PV_MAY.
```

```sql
SELECT NumeroD, CodProv, Descrip, OrdenC, FechaE, Monto, TGravable, TExento, MtoTax, MtoTotal, Notas1
FROM ADM_MAY.dbo.SACOMP WHERE TipoCom = 'L' AND CodUsua = 'SYNC';
```

| Campo | Esperado |
|---|---|
| NumeroD | `000003560` (el `PrxOrdenC` del PV) |
| CodProv / Descrip | `890919355-4` / SIGMAPLAS S.A. |
| OrdenC | `*33570` (con esto el trabajador encuentra la OC al cargarla en Compras) |
| FechaE | 2026-10-19 (fecha de la factura, sin hora) |
| Monto / MtoTax / MtoTotal | 1.236.118,72 / 0 / 1.236.118,72 |
| Notas1 | `SYNC FAB FAC *33570` |

**Comparación clave:** la OC debe cuadrar al centavo con la **compra `33570` que el PV digitó a mano**:

```sql
SELECT 'compra manual 33570' doc, i.NroLinea, i.CodItem, i.Cantidad, i.Costo, i.TotalItem
FROM ADM_MAY.dbo.SAITEMCOM i WHERE i.TipoCom = 'H' AND i.NumeroD = '33570'
UNION ALL
SELECT 'OC SigmaSync', i.NroLinea, i.CodItem, i.Cantidad, i.Costo, i.TotalItem
FROM ADM_MAY.dbo.SAITEMCOM i WHERE i.TipoCom = 'L' AND i.NumeroD = '000003560'
ORDER BY NroLinea, doc;
```

| Línea | Producto | Cant. | Costo (ambas) | TotalItem (ambas) |
|---|---|---|---|---|
| 1 | 1I21033062400P4 | 12 | 18.161,7675 | 217.941,21 |
| 2 | 121080180080 | 140 | 7.272,6965 | 1.018.177,51 |

El costo es el precio facturado × 0,95, porque la factura trae un 5 % de descuento global (`Descto1`).

**Comprobaciones adicionales:**
- **Mismos campos que una OC de SAINT:** comparar la OC `000003560` con la `000003538` (`SELECT * FROM ADM_MAY.dbo.SACOMP WHERE TipoCom='L' AND NumeroD IN ('000003538','000003560')`). Solo se agregan `OrdenC` y `Notas1`.
- **Respuesta en Fábrica:** `SELECT * FROM SAINT_ADM.sync.DocRef WHERE relacion = 'FACTURA_OC'` → `*33570` → `PV_MAY L 000003560`.

---

## 9. F3 desde el CEDI: factura `009950` → OC en el PV

```powershell
& $ag enviar-factura --nodo CEDI --numero 009950
```

**Esperado en la terminal 2 (PV):** `Alerta … EXENTO_DIFERENTE: 4180: la factura lo trae exento y el maestro del PV lo tiene gravado`.

```sql
SELECT NumeroD, CodProv, Descrip, OrdenC, Monto, TGravable, TExento FROM ADM_MAY.dbo.SACOMP
WHERE TipoCom = 'L' AND OrdenC = '009950';
```

| Campo | Esperado |
|---|---|
| NumeroD | `000003561` |
| CodProv / Descrip | `8909193545` / CENTRO DE DISTRICION. Hoy el PV registra las compras del CEDI como si fueran de SIGMAPLAS; con esto se distinguen |
| TGravable / TExento / Monto | 9.451.196,37 / 159.648,00 / 9.610.844,37 (iguales a la factura del CEDI) |

**Comparación con la compra que el PV digitó a mano** (muestra el error de digitación que se elimina):

```sql
SELECT 'factura CEDI' doc, TGravable, TExento, Monto FROM BODEGA1.dbo.SAFACT WHERE TipoFac='A' AND NumeroD='009950'
UNION ALL SELECT 'compra manual PV', TGravable, TExento, Monto FROM ADM_MAY.dbo.SACOMP WHERE TipoCom='H' AND NumeroD='009950'
UNION ALL SELECT 'OC SigmaSync', TGravable, TExento, Monto FROM ADM_MAY.dbo.SACOMP WHERE TipoCom='L' AND OrdenC='009950';
```

La compra manual tiene todo gravado. La OC de SigmaSync separa los 159.648 exentos igual que la factura.

---

## 10. Las OC de F3 no vuelven a Fábrica como pedidos

Las OC `000003560` y `000003561` tienen proveedor de Fábrica o del CEDI y fueron grabadas ahora, así que entran en la ventana de captura. Hay que esperar unos 2 minutos (60 s de espera + un ciclo de captura) y consultar:

```sql
SELECT COUNT(*) FROM ADM_MAY.sync.Outbox WHERE tipo = 'OC_PEDIDO';   -- 2 (solo las de los pasos 5 y 6)
```

**Esperado:** 2. Si saliera 4, la autoexclusión no funciona: el agente reenviaría a Fábrica sus propias OC.

---

## 11. Rechazo y reproceso desde el panel

### 11.1 Activar el panel

```powershell
& $hub hash-admin prueba123     # copia el hash que imprime
```

Crear el archivo `src\SigmaSync.Hub\bin\Debug\net10.0\appsettings.local.json` (git lo ignora) con este contenido. Para ver rápido la alerta de latido del paso 12, bajar también los tiempos:

```json
{ "Hub": { "AdminKeyHash": "<hash>", "MinutosSinLatido": 2, "IntervaloNotificacionMin": 1 } }
```

Reiniciar el hub (terminal 1), abrir `http://localhost:5080/panel` e ingresar `prueba123`.

**Esperado:**
- En **Nodos**: los tres en verde, "hace 0–1 min".
- En **Mensajes**: los OC_PEDIDO, ACK_PEDIDO, FACTURA_OC y ACK_FACTURA de los pasos anteriores, en CONFIRMADO.
- En **Alertas**: la de EXENTO_DIFERENTE y la de CONSECUTIVO_COLISION, llegadas con el latido.

### 11.2 Provocar un rechazo

Elegir otra OC del PV a Fábrica:

```sql
SELECT TOP 5 c.NumeroD, c.FechaE, COUNT(*) items FROM ADM_MAY.dbo.SACOMP c
JOIN ADM_MAY.dbo.SAITEMCOM i ON i.TipoCom=c.TipoCom AND i.NumeroD=c.NumeroD AND i.CodProv=c.CodProv
WHERE c.TipoCom = 'L' AND c.CodProv = '890919355-4' AND c.NumeroD <> '000003538'
GROUP BY c.NumeroD, c.FechaE ORDER BY c.NumeroD DESC;
```

Inactivar en Fábrica uno de sus productos. Es la copia, dentro del snapshot:

```sql
UPDATE SAINT_ADM.dbo.SAPROD SET Activo = 0
WHERE CodProd = (SELECT TOP 1 CodItem FROM ADM_MAY.dbo.SAITEMCOM WHERE TipoCom='L' AND NumeroD='<OC elegida>');
```

```powershell
& $ag enviar-oc --nodo PV_MAY --numero <OC elegida>
```

**Esperado:**
- La terminal 3 (FAB) muestra `Rechazado OC_PEDIDO …: PRODUCTO_INACTIVO …`.
- En el panel, el mensaje aparece en `RECHAZADO` y llega una alerta de FAB.
- En el PV queda el detalle como INFO: `SELECT TOP 3 * FROM ADM_MAY.sync.Log ORDER BY id DESC`.

Si además otro producto de esa OC no existe en Fábrica, también aparece `PRODUCTO_NO_EXISTE`: el rechazo reúne todos los errores.

### 11.3 Corregir y reprocesar

```sql
UPDATE SAINT_ADM.dbo.SAPROD SET Activo = 1 WHERE CodProd = '<el mismo>';
```

En el panel, en **Mensajes** → filtrar `RECHAZADO` → **Reprocesar**.

**Esperado:** en ~15 s se crea el pedido (`035851`), el mensaje pasa a `CONFIRMADO` y el PV registra la relación en `sync.DocRef`. Si la OC tenía productos inexistentes en Fábrica, volverá a rechazarse; en ese caso se prueba **Descartar**, que pide un motivo.

---

## 12. Nodo caído y recuperación

1. Detener el agente del CEDI: `Ctrl+C` en la terminal 4.
2. Esperar unos 2–3 minutos (con los tiempos reducidos del paso 11.1).
3. En el panel, en **Nodos**, el CEDI aparece "caído", y en **Alertas** aparece `El agente de CEDI no reporta latido…`.
4. Volver a arrancarlo (`& $ag --nodo CEDI`). Al minuto llega la alerta `…volvió a reportar latido` y el nodo vuelve a verde.

### Correo (opcional)

Para ver el correo sin un SMTP real, usar `smtp4dev` (un servidor SMTP local con bandeja web):

```powershell
dotnet tool install -g Rnwood.Smtp4dev
smtp4dev          # bandeja en http://localhost:5000, SMTP en el puerto 25
```

Agregar al `appsettings.local.json` del hub:

```json
"Smtp": { "Host": "localhost", "Puerto": 25, "Ssl": false, "De": "sigmasync@prueba.local", "Para": "soporte@prueba.local" }
```

Reiniciar el hub y repetir el paso 12. **Esperado:** llega **un** correo `[SigmaSync] N alertas (…)`, agrupado por nodo, y en **Alertas** la columna Correo pasa a "enviado".

---

## 13. Diagnóstico por nodo

```powershell
& $ag estado --nodo PV_MAY
& $ag estado --nodo FAB
```

**Esperado:** pendientes y errores del Outbox (0 / 0), los rechazos del paso 11 y las últimas advertencias del nodo.

---

## 14. Cerrar y revertir las copias

Detener las cuatro terminales (`Ctrl+C`) y revertir:

```powershell
foreach ($db in 'ADM_MAY','SAINT_ADM','BODEGA1') {
  sqlcmd -S "(localdb)\MSSQLLocalDB" -E -b -Q "ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [$db] FROM DATABASE_SNAPSHOT = '$db`_snap'; ALTER DATABASE [$db] SET MULTI_USER; DROP DATABASE [$db`_snap];"
}
```

Repetir la consulta de comprobación del paso 1 (`35841`, `1`, `3560`, `0`). Las copias vuelven a su estado original, y cualquier `Activo = 0` del paso 11 también se deshace.

Para dejar el hub como antes, borrar el `appsettings.local.json` del hub y, si se quiere, la cola de prueba: `DELETE SigmaSyncHub.hub.Mensaje; DELETE SigmaSyncHub.hub.Alerta;`.

---

## Lista de verificación

| # | Prueba | Resultado esperado | OK |
|---|---|---|---|
| 1 | `dotnet test` | 63 en verde; copias sin rastro | ☐ |
| 4 | PING / PONG | Ida y vuelta < 1 min con FAB y CEDI | ☐ |
| 5 | F1 Fábrica `000003538` | Pedido `035850`, precio lista 3 (19.117,65), totales 10.797.277,71, salto de colisión avisado, comprometido +180 | ☐ |
| 6 | F1 CEDI `000003465` | Pedido `000001`, cliente `9012380209`, depósito `100`, total 606.653,67 | ☐ |
| 7 | Idempotencia | Reenviar la OC no duplica el pedido | ☐ |
| 8 | F3 Fábrica `*33570` | OC `000003560` idéntica al centavo a la compra manual `33570` | ☐ |
| 9 | F3 CEDI `009950` | OC `000003561` con proveedor del CEDI, 159.648 exentos y alerta del `4180` | ☐ |
| 10 | Autoexclusión | Solo 2 `OC_PEDIDO` en el Outbox del PV | ☐ |
| 11 | Rechazo y reproceso | RECHAZADO con alerta; tras corregir, Reprocesar crea el pedido | ☐ |
| 12 | Latido | Alerta de caída y de recuperación; correo único (opcional) | ☐ |
| 13 | `estado` | Diagnóstico legible por nodo | ☐ |
| 14 | Reversión | Copias en su estado original | ☐ |

## Lo que todavía no se puede probar aquí

- **Que SAINT cargue los documentos en pantalla:** el pedido en Facturación (buscado por OC) y la OC en Compras (buscada por número de factura). Necesita el ambiente de pruebas con SAINT instalado.
- **F2 de punta a punta:** requiere facturar en SAINT desde un pedido sincronizado. Hoy solo se prueba que esas facturas se retienen con `F2_NO_DISPONIBLE`, en la prueba automática `F3Tests.Factura_desde_pedido_sincronizado_se_retiene_como_F2`.
- **Cloudflare Tunnel y SMTP reales del cliente.**
