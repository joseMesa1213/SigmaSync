# SigmaSync – Sincronización SAINT CEDI · Fábrica · Puntos de Venta

SigmaSync sincroniza documentos entre las bases SAINT de los puntos de venta (PV), la Fábrica y el CEDI:
- **F1:** la OC del PV se convierte en un pedido en Fábrica o CEDI.
- **F2:** la factura hecha desde ese pedido actualiza la OC del PV.
- **F3:** una factura sin pedido crea una OC en el PV.

Diseño: [docs/diseno-tecnico.md](docs/diseno-tecnico.md).

| Componente | Qué es |
|---|---|
| `src/SigmaSync.Contracts` | Contrato JSON v1.1 (sobre y payloads) y firma HMAC |
| `src/SigmaSync.Saint` | Acceso a SAINT y al esquema `sync` (Dapper), scripts SQL y validación de esquema |
| `src/SigmaSync.Agent` | Agente por nodo (servicio Windows): publica su `sync.Outbox`, consume su cola, envía latido |
| `src/SigmaSync.Hub` | SyncHub: API `/v1` (push, pull, ack, latido, admin) y cola en SQL Server |
| `db/` | Esquema `sync`, semillas por nodo, base del hub y usuario SQL `sigmasync` |
| `deploy/` | Publicación autocontenida, instalación de servicios y Cloudflare Tunnel |

Requisitos de desarrollo: SDK .NET 10 y SQL Server LocalDB con las copias `ADM_MAY`, `SAINT_ADM` y `BODEGA1`.

> **Regla de oro:** en los servidores del cliente solo `SELECT` y respaldos `COPY_ONLY`. Toda escritura, incluidas las pruebas, va a `(localdb)\MSSQLLocalDB`.

## Pruebas

```powershell
dotnet test SigmaSync.slnx
```

Las pruebas de integración crean y borran sus propias bases (`SigmaSyncTest_Hub`, `SigmaSyncTest_A`, `SigmaSyncTest_B`) en LocalDB. No tocan las copias SAINT.

## Correr el entorno local (hub + 3 agentes contra las copias SAINT)

```powershell
dotnet build SigmaSync.slnx
$hub = "src\SigmaSync.Hub\bin\Debug\net10.0\SigmaSync.Hub.exe"
$ag  = "src\SigmaSync.Agent\bin\Debug\net10.0\SigmaSync.Agent.exe"

& $hub instalar-bd                                  # crea SigmaSyncHub en LocalDB
& $hub registrar-nodo PV_MAY "PV Mayorista"         # imprime ApiKey y HmacSecret (repetir con FAB y CEDI)
```

Las credenciales que imprime `registrar-nodo` van en `src\SigmaSync.Agent\bin\Debug\net10.0\appsettings.<NODO>.local.json`. Ese archivo lo ignora git; se pueden proteger antes con `& $ag proteger VALOR`:

```json
{ "Agente": { "ApiKey": "...", "HmacSecret": "..." } }
```

```powershell
& $ag instalar-esquema --nodo PV_MAY                # esquema sync + semilla (repetir con FAB y CEDI)
& $hub                                              # terminal 1
& $ag --nodo PV_MAY                                 # terminales 2, 3 y 4 (FAB, CEDI)
& $ag ping --nodo PV_MAY --destino FAB              # el PONG aparece en ADM_MAY.sync.Log en unos 30 s
& $ag enviar-oc --nodo PV_MAY --numero 000003538    # F1: la OC llega como pedido a Fábrica; el PV registra la relación en sync.DocRef
& $ag enviar-factura --nodo FAB --numero "*33570"   # F3: la factura crea en el PV una OC idéntica, lista para cargar en Compras
```

> Las pruebas de F1 de punta a punta escriben pedidos en las copias SAINT. Antes de correrlas conviene tomar snapshots (`CREATE DATABASE ... AS SNAPSHOT OF`) y revertir al terminar con `RESTORE DATABASE ... FROM DATABASE_SNAPSHOT`. Las pruebas automáticas (`dotnet test`) no lo necesitan: trabajan dentro de transacciones que se deshacen.

## Despliegue

1. `deploy\publicar.ps1` genera `publish\agente` y `publish\hub` como ejecutables autocontenidos (no requieren .NET en el servidor).
2. En cada base SAINT, un administrador:
   - ejecuta `SigmaSync.Agent.exe instalar-esquema --nodo NODO`;
   - ejecuta `db\seguridad\usuario_sigmasync.sql` para crear el login `sigmasync` con permisos mínimos.
3. En el servidor del hub: `instalar-hub.ps1`, luego `registrar-nodo` por nodo y Cloudflare Tunnel ([deploy/cloudflared/LEEME.md](deploy/cloudflared/LEEME.md)).
4. En cada nodo: completar `appsettings.<NODO>.json` (cadena de conexión con `sigmasync`, URL del hub y credenciales protegidas con `proteger`) y ejecutar `instalar-agente.ps1 -Nodo NODO`.

## Operación

Ver el [manual de operación](docs/manual-operacion.md). En resumen:
- **Panel:** `/panel` en el hub, con la clave de administración (`Hub:AdminKeyHash`, que se genera con `SigmaSync.Hub.exe hash-admin CLAVE`). Muestra nodos y latido, mensajes (ver, reprocesar, descartar) y alertas.
- **Alertas por correo:** `Hub:Smtp`, con un resumen cada 5 minutos que cubre DLQ, nodos sin latido y advertencias de negocio de los nodos.
- **API de administración** (encabezado `X-Admin-Key`): `/v1/admin/resumen`, `nodos`, `mensajes`, `mensajes/{id}`, `mensajes/{id}/reprocesar`, `mensajes/{id}/descartar` y `alertas`.
- **En cada nodo:** `SigmaSync.Agent.exe estado --nodo NODO` y `reintentar-outbox --nodo NODO`.
