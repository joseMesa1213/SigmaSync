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
| **F2** | Fábrica o CEDI factura desde un pedido sincronizado | *Pendiente de habilitar.* Hoy la factura queda retenida en el hub con `F2_NO_DISPONIBLE` | Panel → Mensajes → RECHAZADO |

Los documentos que crea SigmaSync llevan usuario y estación `SYNC` y, en `Notas1`, una marca como `SYNC PV_MAY OC 000003538` o `SYNC FAB FAC *33570`.

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
| `RECHAZADO` | El destino no lo aplicó por un problema de negocio: producto inexistente, F2 pendiente… | Corregir la causa (sección 5) y **Reprocesar**, o **Descartar** si ya no aplica |
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
| `F2_NO_DISPONIBLE` | Nodo (PV) | Factura hecha desde un pedido sincronizado; F2 aún no está habilitado | Dejarla `RECHAZADO`; se reprocesa cuando F2 se habilite. Mientras tanto, el PV registra esa compra a mano |
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
