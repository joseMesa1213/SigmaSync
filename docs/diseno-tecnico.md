# Diseño técnico – Sincronización SAINT (SigmaSync)

Oct 28, 2026 · José · Versión 1.0 (borrador para revisión con Carlos)

Documentos relacionados:
- Mapeo de datos – Sincronización SAINT (Fase 1): <https://claude.ai/artifact/RS4EbbaN24fjaVKUfK8tCj>
- Alcance – Sincronización SAINT CEDI · Fábrica · Puntos de Venta (Carlos Forero, 2026-10-14)

## 1. Resumen

Este documento convierte el mapeo de la fase 1 en un diseño que se puede construir. Cubre la arquitectura, la estructura del código, el esquema `sync` de cada nodo, el SyncHub, los cambios al contrato JSON y la resolución de los pendientes de diseño. Al final quedan las dependencias del cliente y la hoja de ruta.

Lo central:
- Cada nodo corre un **agente** (servicio Windows .NET 10, publicado autocontenido; ver D12). El agente captura sus documentos, los publica en el **SyncHub** y aplica en SAINT los mensajes que le llegan.
- La captura se hace por **sondeo**, sin modificar las bases de SAINT: ni Change Tracking ni triggers.
- **Una transacción por mensaje** agrupa el documento SAINT, el consecutivo, la trazabilidad (`DocRef`), la idempotencia (`Inbox`) y la respuesta (`Outbox`).
- Las escrituras usan **solo las columnas comunes** a todas las versiones de SAINT. La validación se hace contra documentos creados por SAINT en cada nodo.

## 2. Hallazgos del Sprint 0 (verificación en copias LocalDB)

| # | Hallazgo | Impacto en el diseño |
|---|---|---|
| H1 | **Fábrica tiene una versión de SAINT más nueva que el PV y el CEDI.** `SAINT_ADM` tiene columnas que no existen en los otros dos nodos (`NIT`, `Status`, `Hacienda*`, `CantidadC`, `TipoTra`, `SACORRELSIS.Prefijo/Desde/Hasta`). El PV y el CEDI tienen `CantidadD` y `FechaP`, que Fábrica no tiene. El supuesto del alcance "misma versión en todos los nodos" no se cumple. | Los `INSERT` usan una lista explícita de columnas comunes. Al arrancar, el agente valida que esas columnas existan y no arranca si falta alguna. |
| H2 | En las cuatro tablas de documento (`SAFACT`, `SAITEMFAC`, `SACOMP`, `SAITEMCOM`) **solo las llaves son obligatorias**; el resto admite `NULL` o tiene default, en los tres nodos. En el pedido `035829`, creado por SAINT en Fábrica, las columnas exclusivas de Fábrica quedan en default o `NULL`. | Las columnas que no son comunes se dejan a su default. El riesgo real son los `NULL` que SAINT no tolera (como `FechaE`): se cubre con pruebas campo a campo contra documentos creados por SAINT. |
| H3 | `LenCorrel` es una **fila** de `SACORRELSIS` (`FieldName = 'LenCorrel'`; en Fábrica está escrita en minúscula y la intercalación no distingue mayúsculas): vale 9 en el PV y 6 en Fábrica y CEDI. | El largo del número se lee de esa fila; no se configura a mano. |
| H4 | En el PV, `SAPROD.Pedido` y `SAEXIS.CantPed` **no cuadran** con la suma de las OC abiertas: hay 822 productos y 1.273 filas por depósito con diferencia. | La prueba P2 (2026-11-05) confirmó en pantalla que SAINT sube `SAPROD.Pedido` y `SAEXIS.CantPed` al grabar la OC y los baja al recibirla o borrarla. El agente hace lo mismo: la bandera `ActualizarPedidoPV` queda encendida. Si falta la fila de `SAEXIS` del depósito (producto no asignado al depósito), solo sube `SAPROD.Pedido`, como hizo SAINT con la OC 3567. El descuadre que ya existía es anterior y no se corrige. |
| H5 | `PrxOrdenC` del PV (3560) coincide con la última OC (`000003559`) + 1. `PrxNotaEn` también vale 3560, pero las notas de entrega llegan a `000003790`. | `PrxOrdenC` es el consecutivo correcto para las OC. `PrxNotaEn` no se toca. |
| H6 | Largos de columna: `CodUsua`/`CodEsta` admiten 10 caracteres, `Notas1` 60, `OrdenC` 30 y `NumeroD` 20. | La marca de sincronización y el usuario `SYNC` caben. |
| H7 | Los usuarios de SAINT están en `SSUSRS` con datos cifrados. El usuario `WORKER` de la integración anterior **no existía** en `SSUSRS`. | El agente puede grabar con `CodUsua = 'SYNC'` aunque el usuario no exista. Se pedirá al cliente crearlo desde SAINT para que quede trazable en la aplicación. |
| H9 | **`SAINT_ADM` y `BODEGA1` corren en nivel de compatibilidad 100 (SQL Server 2008)**, aunque el motor sea 2017. `ADM_MAY` está en 120. | El T-SQL del agente en las bases SAINT no puede usar funciones de 2012 en adelante (`TRY_CONVERT`, `IIF`, `CONCAT`, `STRING_AGG`, `FORMAT`). Lo verifican las pruebas de integración sobre las copias. |
| H10 | Las **escalas decimales de `SAITEMFAC` cambian entre nodos**: Fábrica tiene `PriceO` 3, `MtoTax` 3 y `MtoTaxO` 4; el CEDI tiene `PriceO` 4, `MtoTax` 4 y `MtoTaxO` 3 (y `Precio3` con 4 decimales en lugar de 5). | Los redondeos de F1 se aplican con las escalas que el agente lee de `sys.columns` en cada base, no con valores fijos. |
| H11 | Reglas de cálculo de SAINT para el pedido, deducidas de `035829` y reproducidas al centavo: `Precio` = `Precio3` a 2 decimales; `PriceO` = `Precio3`; `TotalItem` = `Precio` × `Cantidad`; `MtoTaxO` = `Cantidad` × `Precio3` × tarifa; encabezado `MtoTax` = Σ `MtoTax` de los ítems a 2 decimales; `FechaV` = fecha de `FechaE` + `DiasCred`, sin hora. | Implementado en `SigmaSync.Core` (`ArmadorPedido`), con pruebas de oro. |
| H8 | Triggers en las tablas que escribe el agente: solo `TR_ADM_INSERTDOCUMENTOVENTA` (`SAFACT`) y `TR_ADM_INSERTDOCUMENTOCOMPRA` (`SACOMP`), iguales en los tres nodos. No actúan con `E` ni con `L`. | Sin efectos colaterales en los documentos que crea el agente. Se mantiene un `INSERT` por encabezado. |

## 3. Arquitectura

```
          PV (N nodos)                              Servidor Fábrica (24/7)
 ┌──────────────────────────┐            ┌──────────────────────────────────────────┐
 │ SAINT (ADM_MAY)          │            │ SyncHub API (.NET 8, servicio Windows)   │
 │  └ esquema sync          │   HTTPS    │  └ BD SigmaSyncHub (hub.Nodo, hub.Mensaje)│
 │ Agente SigmaSync-PV_MAY  │◄──────────►│ cloudflared (Cloudflare Tunnel)          │
 └──────────────────────────┘  (Tunnel)  │                                          │
                                          │ SAINT_ADM + sync  ◄─ Agente SigmaSync-FAB │
                                          │ BODEGA1   + sync  ◄─ Agente SigmaSync-CEDI│
                                          └──────────────────────────────────────────┘
```

- **Topología hub-and-spoke.** Los agentes solo hablan con el hub, siempre por iniciativa propia (push y pull), así que ningún nodo abre puertos. Los agentes de Fábrica y CEDI corren en el mismo servidor que el hub, pero también pasan por la API; no tienen un atajo directo a la base del hub.
- **Store-and-forward.** Si un nodo pierde conexión, sus mensajes quedan en su `sync.Outbox` y los mensajes dirigidos a él quedan en el hub. Ambos se entregan al reconectar.
- **Latencia objetivo:** menos de 2 minutos de punta a punta (captura cada 30 s, publicación inmediata, pull cada 15 s).

## 4. Estructura de la solución

```
proyecto_sigmaplus/
├─ docs/                      diseño, mapeo, ADRs (docs/adr/NNN-*.md)
├─ db/
│  ├─ sync/                   001_schema.sql, 002_seed_<nodo>.sql … (idempotentes)
│  ├─ hub/                    001_hub.sql …
│  └─ verificacion/           consultas solo-lectura (cuadres, comprometido, consecutivos)
├─ src/
│  ├─ SigmaSync.Contracts     sobre y payloads v1 (records, System.Text.Json snake_case), firma HMAC
│  ├─ SigmaSync.Core          reglas puras: costo neto, redondeos, armado de pedido/OC
│  ├─ SigmaSync.Saint         acceso SAINT y sync.* (Microsoft.Data.SqlClient + Dapper)
│  ├─ SigmaSync.Agent         Worker Service: captura, publicador, consumidor, latido
│  └─ SigmaSync.Hub           Minimal API + acceso a SigmaSyncHub
├─ tests/
│  ├─ SigmaSync.Core.Tests          valores de oro (*33570, 035829, 009950, 035828)
│  └─ SigmaSync.Integration.Tests   contra LocalDB restaurada a línea base
└─ deploy/                    instalación PowerShell, appsettings por nodo, cloudflared
```

Principios:
- **`Core` no conoce la base de datos.** Recibe el payload y una foto de los maestros del destino (precios, `CostAct`, `EsExento`, datos del cliente o proveedor) y devuelve el documento armado. Todo el cálculo de montos se prueba aquí, al centavo.
- **`Saint` no calcula montos.** Lee maestros, toma consecutivos y escribe el documento armado con listas explícitas de columnas comunes (H1).
- **Un solo binario de agente.** El rol (PV, FAB o CEDI) y los valores de cada nodo vienen de `appsettings.<nodo>.json` y de `sync.Config`.
- Librerías: Dapper, Serilog (archivo + `sync.Log`) y xUnit. No hace falta Polly: los reintentos salen de los ciclos del agente y del backoff del hub. El T-SQL debe ser compatible con SQL Server 2017.

## 5. Agente

### 5.1 Procesos internos

| Proceso | Frecuencia | Qué hace |
|---|---|---|
| Captura | 30 s | Busca documentos nuevos y los serializa en `sync.Outbox` |
| Publicador | 5 s | Envía a `POST /v1/mensajes` los mensajes `PENDIENTE` de `Outbox` y los marca `ENVIADO` |
| Consumidor | 15 s | Hace `pull` del hub, procesa cada mensaje en una transacción y responde con `ack` |
| Latido | 60 s | `POST /v1/latido` con versión, pendientes y último error |
| Validación de esquema | Al arrancar | Comprueba las columnas que usan los escritores y que existan `sync.Config` y `sync.NodoTercero`; si algo falla, el agente no arranca |

### 5.2 Captura (sondeo con ventana de recaptura)

No se usa Change Tracking: exige `ALTER DATABASE` sobre SAINT. Tampoco se usa una marca de agua estricta por `NroUnico`: un `NroUnico` mayor puede confirmarse antes que uno menor y el menor se perdería. En su lugar, cada ciclo:

1. Busca los documentos candidatos de los últimos `VentanaDias` días (por defecto 3) cuyo `FechaT` sea de hace más de `EsperaSegundos` (por defecto 60). La espera evita capturar un documento a medio grabar.
2. Descarta los que ya están en `Outbox`, con la llave `(tipo, doc_tipo, doc_numero, nodo_destino)`.
3. Arma el payload e inserta en `Outbox` en una transacción. El índice único garantiza que no haya duplicados aunque dos ciclos se crucen.

| Nodo | Candidatos | Mensaje |
|---|---|---|
| PV | `SACOMP` con `TipoCom='L'`, `CodSucu='00000'`, `CodProv` en `sync.NodoTercero` (rol FAB o CEDI), **`CodUsua <> 'SYNC'`** y sin `DocRef` como OC creada por sincronización | `OC_PEDIDO` al nodo del proveedor |
| FAB / CEDI | `SAFACT` con `TipoFac='A'`, `CodSucu='00000'`, `CodClie` en `sync.NodoTercero` (rol PV) | `FACTURA_OC` al PV del cliente |

**Autoexclusión (crítica).** Las OC que crea el agente en F3/F2 tienen como proveedor a Fábrica o CEDI. Sin el filtro por `CodUsua` y `DocRef`, el PV las capturaría como F1 y Fábrica crearía pedidos duplicados.

**Carga inicial.** La primera vez que se activa un nodo, la captura arranca desde `sync.Config.CapturaDesde`, para no reenviar el histórico.

### 5.3 Consumo

```
pull → por cada mensaje:
  si msg_id ∈ sync.Inbox           → ack (ya procesado, se reenvía la respuesta)
  BEGIN TRAN (READ COMMITTED)
    validar → handler (escrituras SAINT) → sync.DocRef → sync.Inbox → sync.Outbox (ACK de respuesta)
  COMMIT
  ack al hub
```

- Si el agente cae entre el `COMMIT` y el `ack`, el hub reentrega el mensaje cuando vence el lease y `Inbox` lo descarta. No hay duplicados.
- **Idempotencia de negocio.** Además del `msg_id`, `Inbox` tiene una llave única (nodo origen, tipo, número de documento origen). Si una misma factura llega con otro `msg_id`, por ejemplo tras reconstruir un `Outbox`, no se procesa dos veces.

| Error | Ejemplo | Tratamiento |
|---|---|---|
| Transitorio | Deadlock (1205), timeout, conexión | Rollback; `ack reintentar` con backoff exponencial (1 min, 5, 15, 60); tras 6 intentos → DLQ |
| De negocio | Producto inexistente en el destino, cliente sin mapeo, depósito inválido | Rollback; `Inbox` = `RECHAZADO`; ACK con `errores[]`; `ack rechazado` (no se reintenta) |
| Inesperado | Cualquier otra excepción | Se trata como transitorio y llega a DLQ si persiste |

### 5.4 Configuración por nodo

`appsettings.<nodo>.json` guarda lo de infraestructura: cadena de conexión, URL del hub, `NodoId`, API key y secreto HMAC (cifrado con DPAPI) e intervalos.

`sync.Config` guarda lo de negocio, para cambiarlo sin redesplegar:

| Clave | PV_MAY | FAB | CEDI |
|---|---|---|---|
| `NodoId` | `PV_MAY` | `FAB` | `CEDI` |
| `Rol` | `PV` | `FAB` | `CEDI` |
| `CodSucu` | `00000` | `00000` | `00000` |
| `CodUsua` / `CodEsta` | `SYNC` / `SYNC` | `SYNC` / `SYNC` | `SYNC` / `SYNC` |
| `CodUbic` | `115` | `005` | por confirmar |
| `CodVend` | — | `1305` | por confirmar |
| `ActualizarPedidoPV` | `1` | — | — |
| `CapturaDesde` | fecha de activación | fecha de activación | fecha de activación |
| `VentanaDias` / `EsperaSegundos` | 3 / 60 | 3 / 60 | 3 / 60 |

## 6. Esquema `sync` (cada base SAINT)

> **El DDL vigente es [db/sync/001_schema.sql](../db/sync/001_schema.sql).** Al implementarlo cambió lo siguiente respecto del borrador:
> - `Outbox.payload` pasó a llamarse `sobre` y guarda el sobre completo.
> - `Inbox` tiene ahora las columnas `intentos` y `error`.
> - La llave única de `Inbox` por documento es filtrada (`WHERE estado = 'PROCESADO'`): así un mensaje `RECHAZADO` se puede reprocesar después de corregir los maestros.

Borrador original:

```sql
CREATE SCHEMA sync AUTHORIZATION dbo;
GO
CREATE TABLE sync.SchemaVersion (
    version     int          NOT NULL PRIMARY KEY,
    descripcion varchar(200) NOT NULL,
    aplicado_ts datetime2(0) NOT NULL DEFAULT SYSDATETIME()
);
CREATE TABLE sync.Config (
    clave varchar(50)  NOT NULL PRIMARY KEY,
    valor varchar(200) NOT NULL
);
CREATE TABLE sync.NodoTercero (
    id           int IDENTITY PRIMARY KEY,
    tipo_tercero varchar(10) NOT NULL CHECK (tipo_tercero IN ('CLIENTE','PROVEEDOR')),
    cod_tercero  varchar(15) NOT NULL,          -- código exacto en SACLIE/SAPROV de esta base
    nodo_id      varchar(10) NOT NULL,          -- PV_MAY, FAB, CEDI…
    rol          varchar(4)  NOT NULL CHECK (rol IN ('PV','FAB','CEDI')),
    activo       bit         NOT NULL DEFAULT 1,
    CONSTRAINT UQ_NodoTercero UNIQUE (tipo_tercero, cod_tercero)
);
CREATE TABLE sync.Outbox (
    id              bigint IDENTITY PRIMARY KEY,
    msg_id          uniqueidentifier NOT NULL CONSTRAINT UQ_Outbox_msg UNIQUE,
    tipo            varchar(20)  NOT NULL,      -- OC_PEDIDO, ACK_PEDIDO, FACTURA_OC, ACK_FACTURA
    nodo_destino    varchar(10)  NOT NULL,
    clave_particion varchar(60)  NOT NULL,
    doc_tipo        char(1)      NULL,          -- L, A… (NULL en ACK)
    doc_numero      varchar(20)  NULL,
    doc_nro_unico   int          NULL,
    payload         nvarchar(max) NOT NULL,
    estado          varchar(10)  NOT NULL DEFAULT 'PENDIENTE',   -- PENDIENTE, ENVIADO, ERROR
    intentos        int          NOT NULL DEFAULT 0,
    creado_ts       datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
    enviado_ts      datetime2(3) NULL,
    error           nvarchar(2000) NULL
);
CREATE UNIQUE INDEX UQ_Outbox_doc ON sync.Outbox (tipo, doc_tipo, doc_numero, nodo_destino)
    WHERE doc_numero IS NOT NULL;
CREATE INDEX IX_Outbox_estado ON sync.Outbox (estado, id);

CREATE TABLE sync.Inbox (
    msg_id            uniqueidentifier NOT NULL PRIMARY KEY,
    tipo              varchar(20)  NOT NULL,
    nodo_origen       varchar(10)  NOT NULL,
    doc_numero_origen varchar(20)  NOT NULL,
    estado            varchar(10)  NOT NULL,    -- PROCESADO, RECHAZADO
    recibido_ts       datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
    procesado_ts      datetime2(3) NULL,
    resultado         nvarchar(max) NULL,       -- ACK enviado (para reenviarlo si se repite)
    CONSTRAINT UQ_Inbox_doc UNIQUE (nodo_origen, tipo, doc_numero_origen)
);
CREATE TABLE sync.DocRef (
    id            bigint IDENTITY PRIMARY KEY,
    relacion      varchar(12) NOT NULL,         -- OC_PEDIDO, FACTURA_OC, OC_SALDO
    nodo_origen   varchar(10) NOT NULL,
    tipo_origen   char(1)     NOT NULL,
    num_origen    varchar(20) NOT NULL,
    nodo_destino  varchar(10) NOT NULL,
    tipo_destino  char(1)     NOT NULL,
    num_destino   varchar(20) NOT NULL,
    activo        bit         NOT NULL DEFAULT 1,   -- OC_PEDIDO re-apuntado a la OC de saldo → la anterior queda en 0
    msg_id        uniqueidentifier NULL,
    creado_ts     datetime2(3) NOT NULL DEFAULT SYSDATETIME()
);
CREATE INDEX IX_DocRef_origen  ON sync.DocRef (nodo_origen, tipo_origen, num_origen);
CREATE INDEX IX_DocRef_destino ON sync.DocRef (nodo_destino, tipo_destino, num_destino);

CREATE TABLE sync.Log (
    id         bigint IDENTITY PRIMARY KEY,
    ts         datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
    nivel      varchar(5)   NOT NULL,           -- INFO, WARN, ERROR
    componente varchar(30)  NOT NULL,
    msg_id     uniqueidentifier NULL,
    mensaje    nvarchar(500) NOT NULL,
    detalle    nvarchar(max) NULL
);
CREATE INDEX IX_Log_ts ON sync.Log (ts);
```

Semilla de `NodoTercero` (solo la fila de cada base):

| Base | tipo_tercero | cod_tercero | nodo_id | rol |
|---|---|---|---|---|
| `ADM_MAY` | PROVEEDOR | `890919355-4` | FAB | FAB |
| `ADM_MAY` | PROVEEDOR | `8909193545` | CEDI | CEDI |
| `SAINT_ADM` | CLIENTE | `2853060` | PV_MAY | PV |
| `BODEGA1` | CLIENTE | `9012380209` | PV_MAY | PV |

**Seguridad.** Se crea un login SQL dedicado `sigmasync` con estos permisos:
- Control del esquema `sync`.
- `SELECT` sobre las tablas SAINT.
- `INSERT`/`UPDATE` solo en `SAFACT`, `SAITEMFAC`, `SACOMP`, `SAITEMCOM`, `SAPROD`, `SAEXIS` y `SACORRELSIS`.
- `DELETE` solo en `SAITEMCOM`, para las líneas de OC que se eliminan en F2.

## 7. SyncHub

### 7.1 Base `SigmaSyncHub`

> **El DDL vigente es [db/hub/001_hub.sql](../db/hub/001_hub.sql).** Al implementarlo cambió lo siguiente respecto del borrador:
> - `hub.Mensaje` guarda el sobre completo (`sobre`).
> - Tiene una columna `seq` (identity) como orden de llegada.
> - El orden por partición se evalúa por `seq` y dentro de la misma cola de destino.

```sql
CREATE TABLE hub.Nodo (
    nodo_id        varchar(10)  NOT NULL PRIMARY KEY,
    descripcion    varchar(100) NOT NULL,
    api_key_hash   varbinary(32) NOT NULL,      -- SHA-256 de la API key
    hmac_secret    varbinary(256) NOT NULL,     -- cifrado con DPAPI (máquina)
    activo         bit NOT NULL DEFAULT 1,
    ultimo_latido  datetime2(0) NULL,
    version_agente varchar(20) NULL,
    estado_latido  nvarchar(max) NULL           -- pendientes, último error
);
CREATE TABLE hub.Mensaje (
    msg_id          uniqueidentifier NOT NULL PRIMARY KEY,
    tipo            varchar(20)  NOT NULL,
    nodo_origen     varchar(10)  NOT NULL,
    nodo_destino    varchar(10)  NOT NULL,
    clave_particion varchar(60)  NOT NULL,
    payload         nvarchar(max) NOT NULL,
    estado          varchar(12)  NOT NULL DEFAULT 'PENDIENTE', -- PENDIENTE, EN_PROCESO, CONFIRMADO, RECHAZADO, DLQ
    intentos        int          NOT NULL DEFAULT 0,
    visible_desde   datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    lease_hasta     datetime2(3) NULL,
    creado_ts       datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    confirmado_ts   datetime2(3) NULL,
    error           nvarchar(2000) NULL
);
CREATE INDEX IX_Mensaje_cola ON hub.Mensaje (nodo_destino, estado, visible_desde) INCLUDE (clave_particion, creado_ts);
CREATE INDEX IX_Mensaje_particion ON hub.Mensaje (clave_particion, creado_ts);
```

### 7.2 API (`/v1`)

| Método | Ruta | Uso |
|---|---|---|
| POST | `/mensajes` | Push. Idempotente: un `msg_id` repetido responde 200 sin duplicar |
| POST | `/mensajes/pull?max=10` | Entrega mensajes del nodo que llama con un lease de 5 min |
| POST | `/mensajes/{id}/ack` | `{ "resultado": "ok" \| "reintentar" \| "rechazado", "error": "..." }` |
| POST | `/latido` | Estado del agente |
| GET | `/admin/mensajes?estado=DLQ` | Consulta de errores (requiere la llave de administración) |
| POST | `/admin/mensajes/{id}/reprocesar` | Devuelve un mensaje en DLQ o rechazado a `PENDIENTE` |

**Orden por partición.** `pull` solo entrega un mensaje cuando no existe un mensaje anterior de la misma `clave_particion` en estado `PENDIENTE`, `EN_PROCESO` o `DLQ`. Un mensaje bloqueado en DLQ detiene su propia partición y no el resto de la cola.

```sql
;WITH candidatos AS (
  SELECT TOP (@max) m.msg_id
  FROM hub.Mensaje m WITH (READPAST, UPDLOCK, ROWLOCK)
  WHERE m.nodo_destino = @nodo
    AND (m.estado = 'PENDIENTE' OR (m.estado = 'EN_PROCESO' AND m.lease_hasta < SYSUTCDATETIME()))
    AND m.visible_desde <= SYSUTCDATETIME()
    AND NOT EXISTS (SELECT 1 FROM hub.Mensaje p
                    WHERE p.clave_particion = m.clave_particion AND p.creado_ts < m.creado_ts
                      AND p.estado IN ('PENDIENTE','EN_PROCESO','DLQ'))
  ORDER BY m.creado_ts)
UPDATE m SET estado = 'EN_PROCESO', lease_hasta = DATEADD(MINUTE, 5, SYSUTCDATETIME()), intentos = intentos + 1
OUTPUT inserted.*
FROM hub.Mensaje m JOIN candidatos c ON c.msg_id = m.msg_id;
```

### 7.3 Seguridad y operación

- **Autenticación.** Cada petición lleva `X-Nodo`, `X-Api-Key`, `X-Timestamp` y `X-Firma` = HMAC-SHA256(secreto, `método \n ruta \n timestamp \n SHA256(cuerpo)`). Se rechaza un desfase mayor de 5 minutos. Un nodo solo puede hacer `pull` y `ack` de mensajes dirigidos a él, y solo puede publicar con su propio `nodo_origen`.
- **Exposición.** Kestrel escucha solo en `localhost`; `cloudflared` publica el hub con TLS gestionado por Cloudflare, sin abrir puertos.
- **Purga diaria:** se eliminan los mensajes `CONFIRMADO` con más de 30 días.
- **Alertas SMTP:** cuando un mensaje entra a DLQ y cuando un nodo pasa 15 minutos sin latido (en horario laboral configurable).

## 8. Contrato JSON v1.1 (cambios aditivos)

Los cambios son compatibles con v1: un consumidor v1 ignora los campos nuevos.

| Mensaje | Campo nuevo | Motivo |
|---|---|---|
| `FACTURA_OC`.items[] | `oc_origen` (string o null) | Fábrica o CEDI lo resuelven desde su propio `DocRef` (pedido → OC). Así F2 no depende solo de que el `ACK_PEDIDO` haya llegado al PV. El PV usa `oc_origen` y, si viene nulo, cae a `pedido_origen` + `DocRef` |
| `FACTURA_OC`.factura | `cod_clie` | Trazabilidad del código de cliente con el que se facturó |
| `OC_PEDIDO`.oc | `cod_prov` | Trazabilidad; el destino ya está en el sobre |
| `ACK_*` | `errores[].codigo` | Códigos estables: `PRODUCTO_NO_EXISTE`, `CLIENTE_SIN_MAPEO`, `DEPOSITO_NO_EXISTE`, `OC_NO_ENCONTRADA`, `CONSECUTIVO_COLISION` |

## 9. Reglas de escritura comunes

### 9.1 Consecutivos

```sql
DECLARE @n int, @len int, @num varchar(20);
SELECT @len = ValueInt FROM dbo.SACORRELSIS WHERE CodSucu = @sucu AND FieldName = 'LenCorrel';
-- El UPDATE deja un bloqueo exclusivo sobre la fila hasta el COMMIT: SAINT y el agente se serializan aquí.
UPDATE dbo.SACORRELSIS WITH (ROWLOCK)
   SET @n = ValueInt = ValueInt + 1
 WHERE CodSucu = @sucu AND FieldName = @campo;          -- PrxPedido / PrxOrdenC
SET @num = RIGHT(REPLICATE('0', @len) + CAST(@n - 1 AS varchar(10)), @len);
```

- **Salvaguarda de colisión.** Si `@num` ya existe en la tabla destino (es el caso de la colisión actual de `PrxPedido` en Fábrica), el agente toma el siguiente número, deja una alerta `CONSECUTIVO_COLISION` en `sync.Log` y reintenta hasta 50 veces. Así el agente no se bloquea, pero el aviso queda. La corrección de fondo sigue siendo del cliente o del soporte de SAINT.
- El consecutivo se toma **dentro** de la misma transacción del documento. Si hay rollback, el número no se pierde.

### 9.2 Documentos

- Un `INSERT` por encabezado, con lista explícita de las **columnas comunes** a los tres nodos (H1). Después, los ítems.
- **Ninguna fecha que SAINT lee va en `NULL`:** `FechaE`, `FechaI`, `FechaV` y `FechaT` del encabezado; `FechaE` y `FechaL` de los ítems.
- Marca en `Notas1`: `SYNC <NODO_ORIGEN> <TIPO> <NUMERO>`. Ejemplos: `SYNC PV_MAY OC 000003538` en el pedido; `SYNC FAB FAC *33570` en la OC.
- `OrdenC`: en el pedido, el número de la OC; en la OC, el número de la factura (búsqueda desde "Cargar").
- Los mapeos campo a campo son los del documento de la fase 1, sin cambios.

## 10. Decisiones de diseño

| # | Tema | Decisión | Estado |
|---|---|---|---|
| D1 | Captura | Sondeo con ventana de recaptura y deduplicación en `Outbox`. Sin Change Tracking ni triggers | Decidido |
| D2 | Autoexclusión | Las OC con `CodUsua = 'SYNC'` o con `DocRef` de creación no se capturan como F1 | Decidido |
| D3 | Esquemas distintos | Columnas comunes explícitas, validación de esquema al arrancar y prueba de oro por nodo | Decidido |
| D4 | Consecutivos | `UPDATE` atómico dentro de la transacción del documento, con salvaguarda de colisión y alerta | Decidido |
| D5 | **Facturación F2 (completa o parcial)** | **Una OC por factura; la OC original queda como saldo.** Cada factura de un pedido sincronizado crea en el PV una **OC nueva idéntica a la factura**, igual que F3 (`OrdenC` = factura, `Notas1 = SYNC <nodo> FAC <factura> OC <original>`). A la **OC original** se le resta lo facturado, como hace SAINT cuando el PV recibe una OC en partes (P1b): la línea cumplida se borra, la parcial queda con el saldo y, si no queda nada, se borra la OC. A diferencia de SAINT, se recalculan `TotalItem` y los totales. El comprometido sube en la nueva y baja en la original, así que queda neto hasta la compra. El `DocRef OC_PEDIDO` no se toca. ACK: `OC_CREADA` (nueva) + `OC_ACTUALIZADA` u `OC_CERRADA` (original) | Decidido (2026-11-06; Carlos delegó el diseño) |
| D6 | Excesos y productos nuevos en F2 | La OC nueva lleva lo facturado. Si se factura más de lo pendiente, la original baja solo lo pendiente y queda la alerta `CANTIDAD_EXCEDIDA`. Un producto que no estaba en la OC original va solo en la nueva, con la alerta `PRODUCTO_NO_PEDIDO` | Decidido |
| D7 | OC original que ya no existe al llegar la factura | El PV ya la cargó en una compra o la borró. Se crea igual la OC nueva, con la alerta `OC_NO_ENCONTRADA`, para que revisen que la mercancía no se reciba dos veces | Decidido (P1: SAINT borra la OC al cargarla completa) |
| D8 | Comprometido del PV | Bandera `ActualizarPedidoPV`, encendida desde la prueba P2 del 2026-11-05 (H4). Con `0` se apaga | Decidido |
| D9 | Usuario y estación | `SYNC` / `SYNC`; se pide al cliente crear el usuario en SAINT (H7) | Decidido |
| D10 | Ubicación del hub | Servidor de Fábrica, que también aloja `BODEGA1` | Propuesto (confirmar disponibilidad 24/7) |
| D11 | Latencia | Menos de 2 minutos de punta a punta | Propuesto (confirmar con el cliente) |
| D12 | Plataforma | **.NET 10 (LTS hasta noviembre de 2028)** en lugar de .NET 8, que sale de soporte el 10 de noviembre de 2026. Se publica autocontenido (win-x64, un solo ejecutable), así que los servidores no necesitan .NET instalado | Decidido |
| D14 | Detección de F2 | El PV detecta F2 por ítem: `oc_origen`, o `pedido_origen` con relación `OC_PEDIDO` en su `DocRef`. Un ítem de un pedido hecho a mano en Fábrica (sin relación) va solo en la OC nueva (F3). Hasta el 2026-11-06 estas facturas se rechazaban con `F2_NO_DISPONIBLE`; las que quedaron `RECHAZADO` en el hub se reprocesan con `/v1/admin/mensajes/{id}/reprocesar` | Decidido (F2 habilitado el 2026-11-06) |
| D15 | Comprometido del PV en F3 | La OC suma `SAPROD.Pedido` y `SAEXIS.CantPed` del depósito, igual que SAINT (prueba P2). Sin fila en `SAEXIS`, no la crea (igual que SAINT con la OC 3567 y con el pedido 035852) | Decidido |
| D13 | Diagnóstico | Mensajes `PING`/`PONG` entre nodos: prueban de punta a punta hub, credenciales y esquema sin tocar SAINT | Decidido |

### Ejemplo D5: OC `000003538` (100 und A, 50 und B) → pedido `035850`

| Evento | OC original `000003538` | OC nueva (la que se carga en Compras) |
|---|---|---|
| Factura 1: 100 A | Queda **B 50** (saldo pendiente) | `000003570`: A 100, idéntica a la factura 1 (`OrdenC` = factura 1) |
| Factura 2: 50 B | **Se borra** (quedó en cero) | `000003571`: B 50, idéntica a la factura 2 |

El operador del PV siempre carga la OC buscándola por el número de factura, igual que en F3. Mientras tanto, la OC original le muestra lo que falta por llegar, con su mismo número. Probado en `F3Tests.F2_dos_facturas_parciales_…` y `SaldoOcTests`.

## 11. Dependencias del cliente y análisis de bloqueo

Pruebas en pantalla que solo puede hacer alguien con SAINT instalado:
- **P1:** grabar una compra cargando una OC y observar qué pasa con la OC (se borra, se reduce o se marca) y con `SAPROD.Pedido` / `SAEXIS.CantPed`.
- **P2:** crear una OC en pantalla y observar su efecto en `Pedido` / `CantPed`.
- **P3:** crear un pedido en pantalla en el CEDI, como patrón de oro de F1 para el CEDI.

| Dependencia | Bloquea | No bloquea |
|---|---|---|
| Colisión de `PrxPedido` en Fábrica + desinstalación confirmada de `WORKER` | Salida a producción de F1 en Fábrica | Desarrollo y pruebas (la salvaguarda de 9.1 la maneja) |
| Ambiente de pruebas con SAINT instalado (P1, P2, P3) | Cierre de F2 (P1), comprometido del PV (P2), validación de F1 en el CEDI (P3) y la UAT | Infraestructura, F1 en Fábrica, F3 |
| ~~Aprobación de D5 (facturación parcial)~~ Resuelta el 2026-11-06: Carlos delegó el diseño | — | — |
| Condición operativa: Fábrica y CEDI facturan cargando el pedido | Que F2 se use en producción | Desarrollo de F2 (sin ella, todo cae en F3) |
| Usuario `SYNC` creado en cada base | Nada técnico (H7) | — |
| Número real de PV y códigos de cliente/proveedor de cada uno | Despliegue al resto de PV | Piloto con PV Mayorista |
| Servidor del hub 24/7, salida 443 y cuenta/dominio de Cloudflare | Piloto en el ambiente del cliente | Desarrollo local (el hub corre en la máquina de desarrollo) |
| Cada PV usa `890919355-4` para Fábrica y `8909193545` para el CEDI | Enrutamiento correcto de F1 en producción | Desarrollo |
| Validación de códigos, unidades y `EsExento` entre nodos | Calidad en producción (habrá rechazos o alertas) | Desarrollo |

## 12. Plan de pruebas

| Nivel | Qué se prueba | Casos de oro |
|---|---|---|
| Unitarias (`Core`) | Costo neto, `TotalItem`, `TGravable`/`TExento`, redondeo de `Precio3` (`Precio` vs `PriceO`), IVA del pedido, saldo de la OC original (D5) | `*33570` (costo 18.161,7675), `009950` (ítem exento `4180`), pedido `035829`, pedido `035828` con facturas `*31819`/`*31820` |
| Integración (LocalDB) | Escritura real con rollback o restauración a línea base: una fila por encabezado, consecutivos, `Compro`/`CantCom`, idempotencia por `msg_id` y por documento, autoexclusión de las OC de F3, concurrencia de consecutivos (dos consumidores y un `UPDATE` simulado de SAINT) | OC `000003538` → pedido comparado campo a campo con `035829` |
| Punta a punta local | Hub + 3 agentes contra LocalDB: OC → pedido → `ACK_PEDIDO` → `DocRef` en el PV; factura → OC (F3, F2, mixta) | Escenarios D5 completo y parcial |
| Pantalla (ambiente del cliente) | El pedido carga en Facturación por número de OC; la OC carga en Compras → Cargar con totales iguales a la factura | P1, P2, P3 |

## 13. Hoja de ruta

| Fase | Horas | Entregable | Depende de |
|---|---|---|---|
| Diseño técnico (este documento) | 16 – 24 | Diseño aprobado, DDL v1, contrato v1.1 | Revisión de Carlos |
| 2. Infraestructura | 40 – 60 | Esquema `sync`, hub, agente con `PING` de punta a punta en local. **Hecho el 2026-10-28**; falta probar cloudflared en el servidor del cliente | — |
| 3. F1 OC → pedido | 40 – 56 | F1 probado en LocalDB (Fábrica y CEDI). **Hecho el 2026-10-28**: el pedido generado tiene las mismas columnas llenas que `035829`, y la prueba de punta a punta con datos reales pasó. Falta la validación en pantalla (P3 y facturar desde el pedido) | P3 para cerrar el CEDI |
| 4. F3 y F2 | 56 – 80 | F3 primero; F2 después de D5 y P1. **F3 hecho el 2026-10-28**: la OC generada tiene las mismas columnas que una OC grabada en SAINT y cuadra al centavo con la compra 33570 que el PV digitó a mano. **F2 hecho el 2026-11-06** con la regla D5 (una OC por factura y la original queda como saldo), después de verlo en pantalla el 2026-11-05: facturas completa y parcial desde pedido en Fábrica (`*33647`, `*33648`), y P1 y P2 en el PV | — |
| 5. Operación | 24 – 36 | Consulta de DLQ, reproceso, alertas, manual. **Hecho el 2026-10-28**: alertas centralizadas en `hub.Alerta` (DLQ y latido desde el hub; advertencias de negocio desde el `sync.Log` de cada nodo con el latido), correo de resumen por SMTP, panel `/panel`, descarte de mensajes, comandos `estado` y `reintentar-outbox`, purga en los nodos y [manual de operación](manual-operacion.md). Falta probar el SMTP real del cliente | — |
| 6. Pruebas y UAT | 32 – 48 | Acta de UAT con piloto Mayorista + Fábrica + CEDI | Ambiente de pruebas, colisión resuelta |
| Despliegue ~6 PV | 18 – 24 | Agentes instalados y `NodoTercero` sembrado | Número real de PV |
| Documentación | 12 – 16 | Manual técnico y de operación | — |
| Contingencia 15 % | 36 – 52 | — | — |
| **Total** | **≈ 275 – 395** | | |
