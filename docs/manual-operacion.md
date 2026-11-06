# Manual de operación – SigmaSync

Para el soporte de TI del cliente y de Datalari. Explica cómo saber si la sincronización está funcionando, qué hacer ante cada alerta y cómo resolver los casos más comunes. Para la arquitectura y las reglas de negocio, ver [diseno-tecnico.md](diseno-tecnico.md).

## 1. Qué hay instalado

| Dónde | Servicio Windows | Qué hace |
|---|---|---|
| Servidor de Fábrica | `SigmaSync-Hub` | Cola de mensajes entre nodos, alertas por correo y panel de operación |
| Servidor de Fábrica | `cloudflared` | Publica el hub en `https://sync.<dominio>` (los PV se conectan por ahí) |
| Servidor de Fábrica | `SigmaSync-FAB` | Agente de Fábrica (base `SAINT_ADM`) |
| Servidor de Fábrica | `SigmaSync-CEDI` | Agente del CEDI (base `BODEGA1`) |
| Cada PV | `SigmaSync-<PV>` | Agente del punto de venta |

Los cuatro servicios de SigmaSync arrancan solos con Windows y se reinician solos ante un fallo: al minuto, al minuto y a los 5 minutos.

Carpetas:
- **Instalación:** `C:\SigmaSync\hub` y `C:\SigmaSync\agente`.
- **Logs de archivo:** subcarpeta `logs\`, uno por día, se conservan 30 días.
- **Logs en base de datos:** cada base SAINT tiene el esquema `sync`:
  - `sync.Log`: lo que pasó en ese nodo.
  - `sync.Outbox`: lo que el nodo envía.
  - `sync.Inbox`: lo que el nodo recibió y procesó.
  - `sync.DocRef`: qué documento originó a cuál.

## 2. Qué hace cada flujo

| Flujo | Disparador | Resultado | Dónde se ve |
|---|---|---|---|
| **F1** | Un PV graba una OC con proveedor SIGMAPLAS (`890919355-4`) o CENTRO DE DISTRICION (`8909193545`) | Pedido en Fábrica o CEDI a precio de lista 3, con `OrdenC` = número de la OC | En Facturación → Cargar → buscar por **Orden de compra** |
| **F3** | Fábrica o CEDI factura a un PV, sin pedido sincronizado | OC en el PV idéntica a la factura, con `OrdenC` = número de factura | En Compras → Cargar → Orden de compra → buscar por el número de factura |
| **F2** | Fábrica o CEDI factura desde un pedido sincronizado (completo o en partes) | Como F3, una **OC nueva idéntica a la factura**. Además, a la **OC original** del PV se le resta lo facturado: queda con lo que falta por llegar y, cuando ya no falta nada, se borra | La OC nueva: igual que F3, por el número de factura. La OC original sigue con su número y muestra el saldo |

Los documentos que crea SigmaSync llevan usuario y estación `SYNC` y, en `Notas1`, una marca como `SYNC PV_MAY OC 000003538` o `SYNC FAB FAC *33570`. En F2, la marca de la OC nueva dice también de qué OC original viene: `SYNC FAB FAC *33647 OC 000003538`.

> **En el PV, cargar siempre la OC nueva** (la que se busca por el número de factura), **no la original**. La original solo indica lo que falta por llegar. Si se carga la original, la mercancía se registra con el costo de la OC y no con el de la factura, y la OC nueva queda sobrando.

La latencia normal es de menos de 2 minutos entre que se graba un documento y aparece en el otro nodo.

## 3. Panel de operación

Se abre en `https://sync.<dominio>/panel`, o en `http://localhost:5080/panel` desde el servidor del hub. Pide la **clave de administración**. Para configurarla:
1. Generar el hash: `SigmaSync.Hub.exe hash-admin CLAVE`.
2. Copiarlo en `Hub:AdminKeyHash` del `appsettings.json` del hub.
3. Reiniciar el servicio `SigmaSync-Hub`.

> Se recomienda proteger además `/panel` y `/v1/admin` con Cloudflare Access (inicio de sesión del equipo de soporte).

| Pestaña | Para qué |
|---|---|
| **Nodos** | Latido de cada agente (verde = reportó en los últimos 15 min), versión, mensajes en cola, en DLQ y rechazados, y el último error del agente. Se refresca cada minuto |
| **Mensajes** | Buscar por estado o nodo. **Ver** muestra el contenido completo del mensaje. **Reprocesar** lo devuelve a la cola. **Descartar** lo saca de la cola, con un motivo obligatorio |
| **Alertas** | Todo lo que se notificó o está por notificarse por correo |

### Estados de un mensaje

| Estado | Significado | Acción |
|---|---|---|
| `PENDIENTE` | Esperando que el nodo destino lo recoja | Ninguna. Si lleva mucho tiempo, revisar el latido del destino |
| `EN_PROCESO` | El destino lo tomó y lo está procesando (reserva de 5 min) | Ninguna |
| `CONFIRMADO` | Aplicado en el destino | Ninguna (se purga a los 30 días) |
| `RECHAZADO` | El destino no lo aplicó por un problema de negocio: producto inexistente, tercero sin mapeo… | Corregir la causa (sección 5) y **Reprocesar**, o **Descartar** si ya no aplica |
| `DLQ` | Falló 6 veces por un error técnico. **Detiene los mensajes siguientes del mismo documento** | Revisar el error, corregir y **Reprocesar**; o **Descartar** |
| `DESCARTADO` | Un operador lo sacó de la cola | Ninguna (se puede reprocesar si fue un error) |

## 4. Alertas por correo

El hub envía, cada 5 minutos, **un solo correo** con las alertas nuevas, agrupadas por nodo. Se configura en `Hub:Smtp` del `appsettings.json` del hub:
- `Host`, `Puerto` (587) y `Ssl`.
- `Usuario` y `Clave`. La clave se puede proteger con `SigmaSync.Agent.exe proteger CLAVE`, ejecutado en el mismo servidor.
- `De` y `Para`; en `Para` se separan varios destinatarios con coma.

Si `Host` está vacío no se envía correo, pero las alertas quedan en el panel. Para un mantenimiento programado se pueden pausar con `Hub:AlertasActivas = false`.

| Alerta | Origen | Qué significa | Qué hacer |
|---|---|---|---|
| `El agente de X no reporta latido` | Hub | El servicio del nodo está detenido, el servidor apagado o sin internet | Ver sección 6.1. Llega otra alerta cuando el agente vuelve |
| `… pasó a DLQ` | Hub | Un mensaje falló 6 veces por error técnico | Ver el error en el panel (sección 6.3) |
| `… rechazado: PRODUCTO_NO_EXISTE` | Nodo | El producto de la OC o de la factura no existe en el destino | Crear u homologar el producto en ese SAINT y **Reprocesar** |
| `PRODUCTO_INACTIVO`, `PRODUCTO_SIN_PRECIO` | Nodo | El producto está inactivo o sin precio de lista 3 | Corregir el maestro y **Reprocesar** |
| `CLIENTE_SIN_MAPEO`, `PROVEEDOR_SIN_MAPEO` | Nodo | Falta la correspondencia del tercero en `sync.NodoTercero` | Ver sección 6.5 y **Reprocesar** |
| `DEPOSITO_NO_EXISTE`, `CONFIGURACION_INCOMPLETA` | Nodo | Falta o es inválido `CodUbic` en `sync.Config` | Corregir `sync.Config` y **Reprocesar** |
| `F2_NO_DISPONIBLE` | Nodo (PV) | Solo en mensajes anteriores al 2026-11-06, cuando F2 aún no estaba habilitado | **Reprocesar**: ahora se aplica como F2. Si el PV ya registró esa compra a mano, **Descartar** |
| `CANTIDAD_EXCEDIDA` | Nodo (PV) | F2: se facturó más de lo que faltaba en la OC original | La OC nueva lleva lo facturado. Confirmar con Fábrica o CEDI que el exceso es correcto |
| `PRODUCTO_NO_PEDIDO` | Nodo (PV) | F2: la factura trae un producto que no estaba en la OC original | Va solo en la OC nueva. Confirmar que el PV lo quiere |
| `OC_NO_ENCONTRADA` | Nodo (PV) | F2: la OC original ya no existe (se cargó en una compra o se borró antes de llegar la factura) | Se creó igual la OC nueva. **Revisar que la mercancía no se reciba dos veces**; si ya se recibió con la original, borrar la nueva |
| `EXENTO_DIFERENTE` | Nodo (PV) | El producto viene exento en la factura y el maestro del PV dice otra cosa | La OC sigue a la factura. Corregir el maestro del PV |
| `DIFERENCIA_TOTAL` | Nodo (PV) | La OC no suma lo mismo que la factura neta (más de $1) | Revisar la factura en Fábrica o CEDI antes de cargar la OC |
| `FLETES_NO_INCLUIDOS` | Nodo (PV) | La factura trae fletes, que la OC no incluye | Registrar los fletes al cargar la compra |
| `CONSECUTIVO_COLISION` | Nodo | El consecutivo de SAINT apuntaba a números ya usados; el agente los saltó | Informativo. Si se repite, avisar al soporte de SAINT |

## 5. Reproceso y descarte

- **Reprocesar** se usa después de corregir la causa. El mensaje vuelve a la cola con sus intentos en cero. Es seguro hacerlo varias veces: el nodo destino nunca aplica dos veces el mismo documento, porque lo controla con `sync.Inbox`.
- **Descartar** se usa cuando el documento ya no debe sincronizarse; por ejemplo, la OC se anuló o se registró a mano. El motivo queda guardado.
- Si un mensaje en DLQ bloquea su documento, al reprocesarlo o descartarlo los mensajes siguientes de ese documento continúan solos.

## 6. Casos comunes

### 6.1 Un agente no reporta latido
1. En el servidor del nodo: `Get-Service SigmaSync-*`. Si está detenido, `Start-Service SigmaSync-<NODO>`.
2. Si no arranca, revisar el log del día en `C:\SigmaSync\agente\logs\agente-<NODO>-AAAAMMDD.log`. Errores típicos al arrancar:
   - **"Validación de esquema"**: falta el esquema `sync` o cambió la versión de SAINT. Ejecutar `SigmaSync.Agent.exe validar --nodo <NODO>`, que dice qué falta.
   - **"Configuración inválida"**: revisar `appsettings.<NODO>.json`.
3. Si corre pero no llega al hub, ver 6.2.

### 6.2 El agente corre pero no se conecta al hub
- Desde el nodo, abrir `https://sync.<dominio>/salud`. Debe responder `{"estado":"ok"}`.
  - Si no responde, revisar el internet del nodo y el servicio `cloudflared` y el `SigmaSync-Hub` en el servidor del hub.
- **`401 Timestamp fuera de rango`** en el log: el reloj del servidor del nodo está desfasado más de 5 minutos. Sincronizar la hora de Windows.
- **`401 Nodo o API key inválidos`** o **`Firma inválida`**: las credenciales no coinciden. Volver a registrar el nodo (6.6).
- Mientras no hay conexión no se pierde nada: los documentos esperan en `sync.Outbox` y salen al reconectar.

### 6.3 Un mensaje en DLQ
1. Panel → Mensajes → DLQ → **Ver**: el campo de error dice qué pasó.
2. Revisar el log del agente destino a esa hora; ahí está el detalle técnico completo.
3. Corregir (base caída, bloqueo, permisos…) y **Reprocesar**.

### 6.4 Diagnóstico rápido de un nodo
`SigmaSync.Agent.exe estado --nodo <NODO>` muestra:
- Pendientes de envío.
- Últimos rechazos.
- Últimas advertencias y errores.

Si hay mensajes "con error" en el Outbox (el hub los rechazó, por ejemplo por un nodo destino no registrado), se corrige la causa y luego se ejecuta `SigmaSync.Agent.exe reintentar-outbox --nodo <NODO>`.

### 6.5 Agregar un PV nuevo
1. En el hub: `SigmaSync.Hub.exe registrar-nodo PV_XXX "Nombre del PV"`. Guardar la API key y el secreto que imprime.
2. En la base SAINT del PV, un administrador:
   - ejecuta `SigmaSync.Agent.exe instalar-esquema --nodo PV_XXX`, con una semilla `db/sync/seed/PV_XXX.sql` que traiga sus proveedores de Fábrica y CEDI y su `CodUbic`;
   - ejecuta `db\seguridad\usuario_sigmasync.sql`.
3. En Fábrica y en el CEDI, agregar el código de cliente del PV en `sync.NodoTercero`:

   ```sql
   INSERT sync.NodoTercero (tipo_tercero, cod_tercero, nodo_id, rol) VALUES ('CLIENTE', '<código en SACLIE>', 'PV_XXX', 'PV')
   ```

4. En el PV, completar `appsettings.PV_XXX.json` con la cadena de conexión, la URL del hub y las credenciales protegidas con `proteger`. Luego ejecutar `instalar-agente.ps1 -Nodo PV_XXX`.
5. Probar: `SigmaSync.Agent.exe ping --nodo PV_XXX --destino FAB`. El PONG queda en `sync.Log` en menos de un minuto.

### 6.6 Cambiar las credenciales de un nodo
Ejecutar `SigmaSync.Hub.exe registrar-nodo <NODO> "<descripción>"`: genera una API key y un secreto nuevos. Luego actualizar `appsettings.<NODO>.json` y reiniciar el servicio del agente.

### 6.7 Reenviar un documento puntual
- **OC de un PV:** `SigmaSync.Agent.exe enviar-oc --nodo <PV> --numero 000003538`.
- **Factura de Fábrica o CEDI:** `SigmaSync.Agent.exe enviar-factura --nodo FAB --numero "*33570"`.

Si el documento ya se había enviado, no se duplica. Para enviarlo de nuevo, usar **Reprocesar** en el panel.

### 6.8 Actualizar la versión
Ejecutar de nuevo `instalar-hub.ps1` o `instalar-agente.ps1` con la versión nueva publicada. La configuración existente se conserva, y el instalador valida y vuelve a arrancar los servicios.

### 6.9 Seguimiento del piloto (una vez por semana, el primer mes)

SigmaSync funciona bien solo si se cumplen dos hábitos: **Fábrica y el CEDI facturan cargando el pedido** que creó F1, y **el PV carga en Compras la OC** que creó SigmaSync (la que se busca por el número de factura). Si no se cumplen, nada falla, pero se acumulan documentos abiertos y el comprometido se infla. Estas consultas lo detectan. Son solo de lectura y se corren en SSMS sobre la base SAINT de cada nodo. `@dias` es la tolerancia: 7 días es un buen punto de partida.

**1. En Fábrica y en el CEDI: ¿se están cargando los pedidos al facturar?**

```sql
DECLARE @dias int = 7;
-- Pedidos creados por F1 que siguen abiertos después de @dias
SELECT d.nodo_origen AS pv, d.num_origen AS oc_pv, d.num_destino AS pedido, d.creado_ts AS creado,
       DATEDIFF(DAY, d.creado_ts, GETDATE()) AS dias, f.MtoTotal
FROM sync.DocRef d
JOIN dbo.SAFACT f ON f.TipoFac = 'E' AND f.NumeroD = d.num_destino
WHERE d.relacion = 'OC_PEDIDO' AND d.activo = 1 AND d.creado_ts < DATEADD(DAY, -@dias, GETDATE())
ORDER BY d.creado_ts;
-- Facturas a los PV de los últimos 30 días hechas SIN cargar un pedido
SELECT t.nodo_id AS pv, COUNT(*) AS facturas_sin_pedido, MAX(f.FechaE) AS ultima
FROM dbo.SAFACT f
JOIN sync.NodoTercero t ON t.tipo_tercero = 'CLIENTE' AND t.cod_tercero = f.CodClie AND t.activo = 1
WHERE f.TipoFac = 'A' AND f.FechaE >= DATEADD(DAY, -30, GETDATE())
  AND NOT EXISTS (SELECT 1 FROM dbo.SAITEMFAC i WHERE i.TipoFac = 'A' AND i.NumeroD = f.NumeroD AND i.OTipo = 'E')
GROUP BY t.nodo_id;
```

Si las dos consultas devuelven filas, Fábrica o el CEDI están facturando directo y dejan los pedidos de F1 abiertos. Hay que pedirles que facturen con **Cargar → Pedido** y borrar los pedidos que ya se facturaron por fuera.

**2. En el PV: ¿se están cargando las OC de SigmaSync?**

```sql
DECLARE @dias int = 7;
SELECT c.NumeroD AS oc, c.OrdenC AS factura, c.CodProv, c.FechaE, DATEDIFF(DAY, c.FechaE, GETDATE()) AS dias, c.Monto, c.Notas1
FROM dbo.SACOMP c
WHERE c.TipoCom = 'L' AND c.CodUsua = 'SYNC' AND c.FechaE < DATEADD(DAY, -@dias, GETDATE())
ORDER BY c.FechaE;
```

Cada fila es una factura cuya mercancía el PV no ha registrado con la OC. Si la registró digitando la compra a mano, la OC sobra: hay que borrarla y recordarle al PV que use **Cargar → Orden de compra**.

**3. En el PV: OC originales que siguen con saldo**

```sql
DECLARE @dias int = 7;
SELECT d.num_origen AS oc_original, d.nodo_destino AS nodo, d.num_destino AS pedido, d.creado_ts AS enviada,
       DATEDIFF(DAY, d.creado_ts, GETDATE()) AS dias, c.Monto AS saldo
FROM sync.DocRef d
JOIN dbo.SACOMP c ON c.TipoCom = 'L' AND c.NumeroD = d.num_origen
WHERE d.relacion = 'OC_PEDIDO' AND d.activo = 1 AND d.creado_ts < DATEADD(DAY, -@dias, GETDATE())
ORDER BY d.creado_ts;
```

Es normal que haya OC con saldo mientras Fábrica o el CEDI terminan de despachar. Hay que cruzar cada fila con la consulta 1 en el nodo indicado. **Si el pedido ya no aparece abierto allá**, el saldo no va a llegar: el pedido se canceló o se facturó por fuera. En ese caso, con el PV, se borra la OC original o el saldo que quedó.

## 7. Respaldos y mantenimiento

- **Base del hub (`SigmaSyncHub`):** incluirla en el respaldo diario del servidor de Fábrica. Si se restaura en **otro** servidor, hay que volver a registrar los nodos, porque los secretos están cifrados con la llave de la máquina.
- **Esquema `sync`:** vive dentro de cada base SAINT, así que entra en los respaldos normales de SAINT.
- **Purga automática:**

  | Dónde | Qué se purga | Antigüedad |
  |---|---|---|
  | Hub | Mensajes confirmados | 30 días |
  | Hub | Alertas notificadas | 90 días |
  | Nodos | Outbox enviado | 90 días |
  | Nodos | `sync.Log` | 180 días |

  `sync.Inbox` y `sync.DocRef` se conservan, porque sirven para la idempotencia y la trazabilidad.
- **Actualizaciones de SAINT:** antes de aplicar una actualización de SAINT en un nodo, avisar a Datalari. Después de aplicarla, ejecutar `SigmaSync.Agent.exe validar --nodo <NODO>`: comprueba que siguen existiendo todas las columnas que usa el agente.
