# Guion de pruebas en el ambiente SAINT del cliente

Pruebas en pantalla que no se pueden hacer con las copias en LocalDB porque necesitan SAINT instalado. Con sus resultados se cierra F2, se confirma el comprometido del PV y se valida que SAINT acepta los documentos que crea SigmaSync.

**Quiénes:** un usuario de SAINT del cliente que conozca Compras y Facturación (opera las pantallas) y José (corre las consultas y anota). Tiempo estimado: 2 horas.

> **Condición:** todo se hace en el **ambiente de pruebas**, con SAINT apuntando a copias de PV Mayorista, Fábrica y CEDI. Nunca en producción. Antes de empezar, confirmar en SAINT el nombre de la base a la que está conectado.

Las consultas son solo `SELECT`. Cada prueba tiene tres pasos: una consulta **antes**, la acción en SAINT y la misma consulta **después**. Guardar ambos resultados (copiar a Excel o captura) para compararlos.

---

## P1. Grabar una compra cargando una OC (PV)

**Para qué:** saber qué hace SAINT con la OC cuando se carga en una compra. Puede borrarla, reducirla, marcarla o dejarla igual. Hasta hoy el PV nunca lo ha hecho: ninguna compra de la copia tiene una OC cargada. F2 ajusta esa OC, así que sin este dato no se puede terminar.

**Elegir** una OC del PV a Fábrica con al menos 2 ítems (en la copia de octubre, `000003537`). Anotar el número:

```sql
SELECT TOP 5 c.NumeroD, c.FechaE, COUNT(*) items, c.MtoTotal
FROM SACOMP c JOIN SAITEMCOM i ON i.TipoCom = c.TipoCom AND i.NumeroD = c.NumeroD AND i.CodProv = c.CodProv
WHERE c.TipoCom = 'L' AND c.CodProv = '890919355-4'
GROUP BY c.NumeroD, c.FechaE, c.MtoTotal ORDER BY c.NumeroD DESC;
```

**Consulta antes y después** (en la base del PV):

```sql
DECLARE @oc varchar(20) = '000003537';
SELECT * FROM SACOMP    WHERE TipoCom = 'L' AND NumeroD = @oc;                        -- encabezado de la OC
SELECT * FROM SAITEMCOM WHERE TipoCom = 'L' AND NumeroD = @oc ORDER BY NroLinea;      -- ítems de la OC
SELECT i.CodItem, p.Pedido, e.CodUbic, e.CantPed, p.Existen, e.Existen AS ExistenDep  -- comprometido y existencia
FROM SAITEMCOM i JOIN SAPROD p ON p.CodProd = i.CodItem
LEFT JOIN SAEXIS e ON e.CodProd = i.CodItem AND e.CodUbic = i.CodUbic
WHERE i.TipoCom = 'L' AND i.NumeroD = @oc;
SELECT ValueInt, FieldName FROM SACORRELSIS WHERE FieldName IN ('PrxOrdenC', 'PrxCompra');
```

**En SAINT:**
1. **P1a, carga completa:** Compras → nueva compra → Cargar → Orden de compra → elegir la OC. Número de factura de prueba `PRUEBA-P1A`. Grabar sin cambiar nada.
2. Correr la consulta **después** y además la de la compra nueva:
   ```sql
   SELECT * FROM SACOMP    WHERE TipoCom = 'H' AND NumeroD = 'PRUEBA-P1A';
   SELECT * FROM SAITEMCOM WHERE TipoCom = 'H' AND NumeroD = 'PRUEBA-P1A';   -- mirar OTipo / ONumero
   ```
3. **P1b, carga parcial:** repetir con otra OC, pero antes de grabar intentar quitar una línea o bajar una cantidad (`PRUEBA-P1B`). Anotar si SAINT lo permite y qué queda en la OC después.
4. **P1c, cargar dos veces:** intentar cargar de nuevo la OC de P1a en otra compra. Anotar si SAINT todavía la ofrece.

**Anotar:**

| Pregunta | P1a | P1b |
|---|---|---|
| ¿La OC sigue existiendo (`SACOMP` L)? | | |
| ¿Cambió algún campo del encabezado o de los ítems? ¿Cuál? | | |
| ¿Bajaron `SAPROD.Pedido` / `SAEXIS.CantPed`? ¿Cuánto? | | |
| ¿La compra quedó con `OTipo`/`ONumero` apuntando a la OC? | | |
| ¿SAINT permite editar líneas al cargar? | — | |

---

## P2. Crear una OC en pantalla (PV)

**Para qué:** confirmar si al grabar una OC SAINT sube el comprometido del PV (`SAPROD.Pedido` y `SAEXIS.CantPed`). En la copia esas cantidades no cuadran con las OC existentes, así que SigmaSync hoy no lo toca (bandera `ActualizarPedidoPV = 0`). Según el resultado, se activa o se deja así.

**Consulta antes y después** con dos productos que se vayan a usar, por ejemplo `121060080080` y `121120160400`:

```sql
SELECT p.CodProd, p.Pedido, e.CodUbic, e.CantPed
FROM SAPROD p LEFT JOIN SAEXIS e ON e.CodProd = p.CodProd
WHERE p.CodProd IN ('121060080080', '121120160400');
```

**En SAINT:**
1. Compras → Orden de compra → proveedor `890919355-4`, depósito `115`, 2 líneas con cantidades fáciles de reconocer (por ejemplo 7 y 13). Grabar y anotar el número.
2. Correr la consulta **después**.
3. Borrar o anular esa OC desde SAINT y correr la consulta otra vez.

**Anotar:** si `Pedido`/`CantPed` subieron exactamente 7 y 13, y si bajaron al borrar la OC.

Además, guardar `SELECT * FROM SACOMP WHERE TipoCom='L' AND NumeroD='<OC>'` y los ítems. Sirven de modelo para comparar, columna por columna, con las OC que crea F3.

---

## P3. Crear un pedido en el CEDI

**Para qué:** el CEDI nunca ha usado pedidos y F1 los crea tomando como modelo los de Fábrica. Un pedido grabado en pantalla confirma qué columnas llena SAINT en esa base (versión distinta a la de Fábrica), el depósito y el vendedor.

**Consulta antes y después** (en la base del CEDI):

```sql
SELECT FieldName, ValueInt FROM SACORRELSIS WHERE FieldName IN ('PrxPedido', 'LenCorrel');
SELECT p.CodProd, p.Compro, e.CodUbic, e.CantCom FROM SAPROD p LEFT JOIN SAEXIS e ON e.CodProd = p.CodProd
WHERE p.CodProd IN ('111216');   -- y los demás productos que se usen
```

**En SAINT:** Facturación → Pedido → cliente `9012380209` (PV Mayorista), 2 o 3 productos (incluir `111216`). Elegir el depósito y el vendedor que usarían en la operación real y anotarlos. Grabar.

**Después:**

```sql
SELECT * FROM SAFACT    WHERE TipoFac = 'E' ORDER BY NroUnico DESC;   -- el pedido nuevo es el primero
SELECT * FROM SAITEMFAC WHERE TipoFac = 'E' AND NumeroD = '<pedido>';
```

**Anotar:** número asignado y su formato (largo, ceros), `CodUbic`, `CodVend`, `CodOper`, y si subieron `Compro`/`CantCom`. Así se confirman los valores del CEDI que hoy están por confirmar (`CodUbic` 100, `CodVend` 9810).

---

## P4. Cargar en pantalla los documentos de SigmaSync

**Requiere:** SigmaSync instalado contra el ambiente de pruebas (hub y los tres agentes, como en `docs/pruebas-manuales.md`).

1. **Pedido de F1 en Fábrica:** desde el PV, grabar una OC a Fábrica. A los ~2 min, en Fábrica: Facturación → Factura → Cargar → Pedido, buscando por el número de la OC.
   - Anotar si aparece, si carga sin errores y si los precios y totales coinciden con los de `SAFACT`.
2. **Lo mismo en el CEDI** con una OC al proveedor `8909193545`.
3. **Facturar desde el pedido** (esta es la condición operativa de F2): grabar la factura cargando el pedido del paso 1.
   - Antes y después, consultar `SAFACT`/`SAITEMFAC` del pedido (`TipoFac='E'`) y `SAPROD.Compro` / `SAEXIS.CantCom`.
   - Anotar qué pasa con el pedido y si la factura queda con `OTipo='E'` / `ONumero` del pedido.
   - Repetir facturando **solo una parte** del pedido.
4. **OC de F3 en el PV:** grabar en Fábrica una factura directa (sin pedido) al cliente del PV. A los ~2 min, en el PV: Compras → Cargar → Orden de compra, buscando por el número de la factura.
   - Anotar si carga y si los totales son iguales a los de la factura.

---

## Hoja de resultados

| Prueba | Resultado | Observaciones |
|---|---|---|
| P1a carga completa de OC | | |
| P1b carga parcial de OC | | |
| P1c cargar dos veces | | |
| P2 comprometido al crear OC | | |
| P2 comprometido al borrar OC | | |
| P3 pedido en CEDI (número, depósito, vendedor) | | |
| P4.1 pedido F1 carga en Facturación (Fábrica) | | |
| P4.2 pedido F1 carga en Facturación (CEDI) | | |
| P4.3 factura desde pedido (completa / parcial) | | |
| P4.4 OC F3 carga en Compras | | |
