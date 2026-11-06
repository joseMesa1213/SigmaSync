using Dapper;
using Microsoft.Data.SqlClient;
using SigmaSync.Contracts;

namespace SigmaSync.Hub;

public sealed record NodoFila(string NodoId, byte[] ApiKeyHash, byte[] HmacSecret, bool Activo);

public enum ResultadoAckHub { Aplicado, NoEncontrado, EstadoInvalido }

public static class EstadosMensaje
{
    public const string Pendiente = "PENDIENTE";
    public const string EnProceso = "EN_PROCESO";
    public const string Confirmado = "CONFIRMADO";
    public const string Rechazado = "RECHAZADO";
    public const string Dlq = "DLQ";
    public const string Descartado = "DESCARTADO";
}

/// <summary>Acceso a la base SigmaSyncHub (hub.Nodo, hub.Mensaje).</summary>
public sealed class RepositorioHub(string connectionString)
{
    private async Task<SqlConnection> AbrirAsync(CancellationToken ct = default)
    {
        var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(ct);
        return cn;
    }

    // ── Nodos ────────────────────────────────────────────────────────────────

    public async Task<NodoFila?> ObtenerNodoAsync(string nodoId)
    {
        await using var cn = await AbrirAsync();
        return await cn.QuerySingleOrDefaultAsync<NodoFila>(
            "SELECT nodo_id AS NodoId, api_key_hash AS ApiKeyHash, hmac_secret AS HmacSecret, activo FROM hub.Nodo WHERE nodo_id = @nodoId",
            new { nodoId });
    }

    public async Task RegistrarNodoAsync(string nodoId, string descripcion, byte[] apiKeyHash, byte[] secretoCifrado)
    {
        await using var cn = await AbrirAsync();
        await cn.ExecuteAsync("""
            MERGE hub.Nodo WITH (HOLDLOCK) AS t USING (SELECT @nodoId AS nodo_id) AS s ON t.nodo_id = s.nodo_id
            WHEN MATCHED THEN UPDATE SET descripcion = @descripcion, api_key_hash = @apiKeyHash, hmac_secret = @secretoCifrado, activo = 1
            WHEN NOT MATCHED THEN INSERT (nodo_id, descripcion, api_key_hash, hmac_secret) VALUES (@nodoId, @descripcion, @apiKeyHash, @secretoCifrado);
            """, new { nodoId, descripcion, apiKeyHash, secretoCifrado });
    }

    /// <summary>
    /// Registra el latido y las alertas del nodo (deduplicadas por id de sync.Log). Si el nodo estaba marcado
    /// como caído, deja una alerta de recuperación.
    /// </summary>
    public async Task LatidoAsync(string nodoId, LatidoSolicitud latido)
    {
        await using var cn = await AbrirAsync();
        await using var tx = cn.BeginTransaction();
        var caida = await cn.QuerySingleOrDefaultAsync<(DateTime? Desde, DateTime? UltimoLatido)>("""
            DECLARE @desde datetime2(0), @ultimo datetime2(0);
            UPDATE hub.Nodo SET @desde = sin_latido_desde, @ultimo = ultimo_latido, sin_latido_desde = NULL, ultimo_latido = SYSUTCDATETIME(),
                   version_agente = LEFT(@VersionAgente, 20), estado_latido = @estado
            WHERE nodo_id = @nodoId;
            SELECT @desde AS Desde, @ultimo AS UltimoLatido;
            """, new
        {
            nodoId, latido.VersionAgente,
            estado = System.Text.Json.JsonSerializer.Serialize(latido with { Alertas = null }, SigmaJson.Opciones),
        }, tx);

        if (caida.Desde is not null)
            await InsertarAlertaAsync(cn, tx, nodoId, "INFO", caida.UltimoLatido is { } ultimo
                ? $"El agente de {nodoId} volvió a reportar latido (el anterior fue el {ultimo:yyyy-MM-dd HH:mm} UTC)"
                : $"El agente de {nodoId} reportó su primer latido", null);

        if (latido.Alertas is { Count: > 0 } alertas)
            await cn.ExecuteAsync("""
                INSERT hub.Alerta (ts, nodo_id, fuente, nivel, mensaje, msg_id, log_id)
                SELECT @Ts, @nodoId, 'NODO', LEFT(@Nivel, 5), LEFT(@Componente + ': ' + @Mensaje, 1000), @MsgId, @LogId
                WHERE NOT EXISTS (SELECT 1 FROM hub.Alerta WHERE nodo_id = @nodoId AND log_id = @LogId)
                """, alertas.Select(a => new { a.Ts, nodoId, a.Nivel, a.Componente, a.Mensaje, a.MsgId, a.LogId }), tx);

        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<dynamic>> ListarNodosAsync()
    {
        await using var cn = await AbrirAsync();
        return (await cn.QueryAsync("""
            SELECT n.nodo_id, n.descripcion, n.activo, n.ultimo_latido, n.sin_latido_desde, n.version_agente, n.estado_latido,
                   (SELECT COUNT(*) FROM hub.Mensaje m WHERE m.nodo_destino = n.nodo_id AND m.estado IN ('PENDIENTE','EN_PROCESO')) AS en_cola,
                   (SELECT COUNT(*) FROM hub.Mensaje m WHERE m.nodo_destino = n.nodo_id AND m.estado = 'DLQ') AS en_dlq,
                   (SELECT COUNT(*) FROM hub.Mensaje m WHERE m.nodo_destino = n.nodo_id AND m.estado = 'RECHAZADO') AS rechazados
            FROM hub.Nodo n ORDER BY n.nodo_id
            """)).AsList();
    }

    /// <summary>Marca como caídos los nodos activos sin latido en <paramref name="minutos"/> y crea una alerta por cada uno (una por caída).</summary>
    public async Task<IReadOnlyList<string>> DetectarNodosSinLatidoAsync(int minutos)
    {
        await using var cn = await AbrirAsync();
        await using var tx = cn.BeginTransaction();
        var caidos = (await cn.QueryAsync<(string NodoId, DateTime? Ultimo)>("""
            UPDATE hub.Nodo SET sin_latido_desde = SYSUTCDATETIME()
            OUTPUT inserted.nodo_id, inserted.ultimo_latido
            WHERE activo = 1 AND sin_latido_desde IS NULL
              AND (ultimo_latido IS NULL OR ultimo_latido < DATEADD(MINUTE, -@minutos, SYSUTCDATETIME()))
            """, new { minutos }, tx)).ToList();
        foreach (var (nodo, ultimo) in caidos)
            await InsertarAlertaAsync(cn, tx, nodo, "ERROR",
                ultimo is null ? $"El agente de {nodo} nunca ha reportado latido"
                               : $"El agente de {nodo} no reporta latido desde {ultimo:yyyy-MM-dd HH:mm} UTC (más de {minutos} min)", null);
        await tx.CommitAsync();
        return caidos.Select(c => c.NodoId).ToList();
    }

    // ── Alertas ──────────────────────────────────────────────────────────────

    private static Task InsertarAlertaAsync(SqlConnection cn, SqlTransaction tx, string nodoId, string nivel, string mensaje, Guid? msgId) =>
        cn.ExecuteAsync("""
            INSERT hub.Alerta (nodo_id, fuente, nivel, mensaje, msg_id) VALUES (@nodoId, 'HUB', @nivel, LEFT(@mensaje, 1000), @msgId)
            """, new { nodoId, nivel, mensaje, msgId }, tx);

    public async Task<IReadOnlyList<AlertaHub>> ListarAlertasAsync(string? nodo, bool soloPendientes, int top)
    {
        await using var cn = await AbrirAsync();
        return (await cn.QueryAsync<AlertaHub>("""
            SELECT TOP (@top) id, ts, nodo_id AS NodoId, fuente, nivel, mensaje, msg_id AS MsgId, notificada
            FROM hub.Alerta
            WHERE (@nodo IS NULL OR nodo_id = @nodo) AND (@soloPendientes = 0 OR notificada = 0)
            ORDER BY id DESC
            """, new { nodo, soloPendientes, top })).AsList();
    }

    public async Task MarcarNotificadasAsync(IEnumerable<long> ids)
    {
        await using var cn = await AbrirAsync();
        await cn.ExecuteAsync("UPDATE hub.Alerta SET notificada = 1 WHERE id IN @ids", new { ids = ids.ToArray() });
    }

    // ── Mensajes ─────────────────────────────────────────────────────────────

    public async Task<bool> ExisteNodoActivoAsync(string nodoId)
    {
        await using var cn = await AbrirAsync();
        return await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM hub.Nodo WHERE nodo_id = @nodoId AND activo = 1", new { nodoId }) > 0;
    }

    /// <summary>Inserta el mensaje si su msg_id no existe. Devuelve false si ya estaba (push idempotente).</summary>
    public async Task<bool> InsertarAsync(Sobre sobre)
    {
        await using var cn = await AbrirAsync();
        var filas = await cn.ExecuteAsync("""
            INSERT hub.Mensaje (msg_id, tipo, nodo_origen, nodo_destino, clave_particion, sobre)
            SELECT @MsgId, @Tipo, @NodoOrigen, @NodoDestino, @ClaveParticion, @json
            WHERE NOT EXISTS (SELECT 1 FROM hub.Mensaje WITH (UPDLOCK, HOLDLOCK) WHERE msg_id = @MsgId)
            """, new { sobre.MsgId, sobre.Tipo, sobre.NodoOrigen, sobre.NodoDestino, sobre.ClaveParticion, json = sobre.ToJson() });
        return filas == 1;
    }

    /// <summary>
    /// Entrega hasta <paramref name="max"/> mensajes al nodo con un lease. De cada partición solo sale el mensaje
    /// más antiguo sin confirmar: el siguiente espera a que el anterior se confirme, se rechace o salga de DLQ.
    /// </summary>
    public async Task<IReadOnlyList<string>> PullAsync(string nodoId, int max, int leaseMinutos)
    {
        await using var cn = await AbrirAsync();
        var filas = await cn.QueryAsync<(long Seq, string Sobre)>("""
            WITH candidatos AS (
                SELECT TOP (@max) m.msg_id
                FROM hub.Mensaje m WITH (READPAST, UPDLOCK, ROWLOCK)
                WHERE m.nodo_destino = @nodoId
                  AND m.visible_desde <= SYSUTCDATETIME()
                  AND (m.estado = 'PENDIENTE' OR (m.estado = 'EN_PROCESO' AND m.lease_hasta < SYSUTCDATETIME()))
                  AND NOT EXISTS (SELECT 1 FROM hub.Mensaje p
                                  WHERE p.nodo_destino = m.nodo_destino AND p.clave_particion = m.clave_particion
                                    AND p.seq < m.seq AND p.estado IN ('PENDIENTE', 'EN_PROCESO', 'DLQ'))
                ORDER BY m.seq)
            UPDATE m SET estado = 'EN_PROCESO', lease_hasta = DATEADD(MINUTE, @leaseMinutos, SYSUTCDATETIME()), intentos = m.intentos + 1
            OUTPUT inserted.seq, inserted.sobre
            FROM hub.Mensaje m JOIN candidatos c ON c.msg_id = m.msg_id;
            """, new { nodoId, max, leaseMinutos });
        return filas.OrderBy(f => f.Seq).Select(f => f.Sobre).ToList();
    }

    private sealed record FilaEstado(string Estado, int Intentos, string Tipo, string NodoOrigen);

    public async Task<(ResultadoAckHub Resultado, string? Estado)> AckAsync(Guid msgId, string nodoId, string resultado, string? error,
        OpcionesHub opc)
    {
        await using var cn = await AbrirAsync();
        await using var tx = cn.BeginTransaction();
        var fila = await cn.QuerySingleOrDefaultAsync<FilaEstado>(
            "SELECT estado, intentos, tipo, nodo_origen AS NodoOrigen FROM hub.Mensaje WITH (UPDLOCK, ROWLOCK) WHERE msg_id = @msgId AND nodo_destino = @nodoId",
            new { msgId, nodoId }, tx);

        if (fila is null) return (ResultadoAckHub.NoEncontrado, null);
        if (fila.Estado == EstadosMensaje.Confirmado && resultado == ResultadosAck.Ok) return (ResultadoAckHub.Aplicado, fila.Estado);
        if (fila.Estado != EstadosMensaje.EnProceso) return (ResultadoAckHub.EstadoInvalido, fila.Estado);

        var nuevo = resultado switch
        {
            ResultadosAck.Ok => EstadosMensaje.Confirmado,
            ResultadosAck.Rechazado => EstadosMensaje.Rechazado,
            _ => fila.Intentos >= opc.MaxIntentos ? EstadosMensaje.Dlq : EstadosMensaje.Pendiente,
        };
        await cn.ExecuteAsync("""
            UPDATE hub.Mensaje SET
                estado = @nuevo,
                lease_hasta = NULL,
                confirmado_ts = CASE WHEN @nuevo = 'CONFIRMADO' THEN SYSUTCDATETIME() ELSE confirmado_ts END,
                visible_desde = CASE WHEN @nuevo = 'PENDIENTE' THEN DATEADD(SECOND, @backoff, SYSUTCDATETIME()) ELSE visible_desde END,
                error = CASE WHEN @nuevo = 'CONFIRMADO' THEN error ELSE LEFT(@error, 2000) END
            WHERE msg_id = @msgId
            """, new { msgId, nuevo, backoff = (int)opc.Backoff(fila.Intentos).TotalSeconds, error }, tx);

        // Los rechazos de negocio los reporta el nodo que procesa (sync.Log → latido); el hub alerta lo de infraestructura.
        if (nuevo == EstadosMensaje.Dlq)
            await InsertarAlertaAsync(cn, tx, nodoId, "ERROR",
                $"{fila.Tipo} de {fila.NodoOrigen} pasó a DLQ en {nodoId} tras {fila.Intentos} intentos: {error}", msgId);

        await tx.CommitAsync();
        return (ResultadoAckHub.Aplicado, nuevo);
    }

    // ── Administración y mantenimiento ───────────────────────────────────────

    public async Task<IReadOnlyList<MensajeAdmin>> ListarMensajesAsync(string? estado, string? nodo, int top)
    {
        await using var cn = await AbrirAsync();
        return (await cn.QueryAsync<MensajeAdmin>("""
            SELECT TOP (@top) msg_id AS MsgId, tipo, nodo_origen AS NodoOrigen, nodo_destino AS NodoDestino,
                   clave_particion AS ClaveParticion, estado, intentos, creado_ts AS CreadoTs, confirmado_ts AS ConfirmadoTs, error
            FROM hub.Mensaje
            WHERE (@estado IS NULL OR estado = @estado) AND (@nodo IS NULL OR nodo_destino = @nodo OR nodo_origen = @nodo)
            ORDER BY seq DESC
            """, new { estado, nodo, top })).AsList();
    }

    public async Task<MensajeDetalle?> ObtenerMensajeAsync(Guid msgId)
    {
        await using var cn = await AbrirAsync();
        var fila = await cn.QuerySingleOrDefaultAsync<FilaDetalle>("""
            SELECT msg_id AS MsgId, tipo, nodo_origen AS NodoOrigen, nodo_destino AS NodoDestino, clave_particion AS ClaveParticion,
                   estado, intentos, creado_ts AS CreadoTs, confirmado_ts AS ConfirmadoTs, error, sobre
            FROM hub.Mensaje WHERE msg_id = @msgId
            """, new { msgId });
        return fila is null ? null : new MensajeDetalle(
            new MensajeAdmin(fila.MsgId, fila.Tipo, fila.NodoOrigen, fila.NodoDestino, fila.ClaveParticion, fila.Estado, fila.Intentos,
                fila.CreadoTs, fila.ConfirmadoTs, fila.Error),
            Sobre.FromJson(fila.Sobre));
    }

    private sealed record FilaDetalle(Guid MsgId, string Tipo, string NodoOrigen, string NodoDestino, string ClaveParticion,
        string Estado, int Intentos, DateTime CreadoTs, DateTime? ConfirmadoTs, string? Error, string Sobre);

    public async Task<IReadOnlyDictionary<string, int>> ResumenAsync()
    {
        await using var cn = await AbrirAsync();
        return (await cn.QueryAsync<(string Estado, int N)>("SELECT estado, COUNT(*) FROM hub.Mensaje GROUP BY estado"))
            .ToDictionary(x => x.Estado, x => x.N);
    }

    /// <summary>Devuelve a la cola un mensaje en DLQ, RECHAZADO o DESCARTADO (reproceso manual).</summary>
    public async Task<bool> ReprocesarAsync(Guid msgId)
    {
        await using var cn = await AbrirAsync();
        return await cn.ExecuteAsync("""
            UPDATE hub.Mensaje SET estado = 'PENDIENTE', intentos = 0, visible_desde = SYSUTCDATETIME(), lease_hasta = NULL
            WHERE msg_id = @msgId AND estado IN ('DLQ', 'RECHAZADO', 'DESCARTADO')
            """, new { msgId }) == 1;
    }

    /// <summary>
    /// Descarta un mensaje que no debe procesarse (p. ej. un documento ya registrado a mano). Libera su partición:
    /// un mensaje en DLQ detiene los siguientes del mismo documento hasta reprocesarlo o descartarlo.
    /// </summary>
    public async Task<bool> DescartarAsync(Guid msgId, string motivo)
    {
        await using var cn = await AbrirAsync();
        return await cn.ExecuteAsync("""
            UPDATE hub.Mensaje SET estado = 'DESCARTADO', lease_hasta = NULL,
                   error = LEFT(ISNULL(error + ' | ', '') + 'descartado: ' + @motivo, 2000)
            WHERE msg_id = @msgId AND estado IN ('DLQ', 'RECHAZADO', 'PENDIENTE')
            """, new { msgId, motivo }) == 1;
    }

    /// <summary>Pasa a DLQ los leases vencidos que agotaron intentos (con alerta) y purga los confirmados antiguos.</summary>
    public async Task<(int ADlq, int Purgados)> MantenimientoAsync(int maxIntentos, int retencionDias)
    {
        await using var cn = await AbrirAsync();
        var aDlq = await cn.ExecuteScalarAsync<int>("""
            SET NOCOUNT ON;
            DECLARE @dlq TABLE (msg_id uniqueidentifier, nodo_destino varchar(10), tipo varchar(20), intentos int);
            UPDATE hub.Mensaje SET estado = 'DLQ', lease_hasta = NULL, error = LEFT(ISNULL(error + ' | ', '') + 'lease vencido sin ack', 2000)
            OUTPUT inserted.msg_id, inserted.nodo_destino, inserted.tipo, inserted.intentos INTO @dlq
            WHERE estado = 'EN_PROCESO' AND lease_hasta < SYSUTCDATETIME() AND intentos >= @maxIntentos;
            INSERT hub.Alerta (nodo_id, fuente, nivel, mensaje, msg_id)
            SELECT nodo_destino, 'HUB', 'ERROR', tipo + ' pasó a DLQ: el agente de ' + nodo_destino + ' no confirmó tras '
                   + CAST(intentos AS varchar(10)) + ' entregas', msg_id
            FROM @dlq;
            SELECT COUNT(*) FROM @dlq;
            """, new { maxIntentos });
        var purgados = await cn.ExecuteAsync("""
            DELETE TOP (5000) FROM hub.Mensaje
            WHERE estado = 'CONFIRMADO' AND confirmado_ts < DATEADD(DAY, -@retencionDias, SYSUTCDATETIME())
            """, new { retencionDias });
        await cn.ExecuteAsync("DELETE TOP (5000) FROM hub.Alerta WHERE notificada = 1 AND ts < DATEADD(DAY, -90, SYSUTCDATETIME())");
        return (aDlq, purgados);
    }

    public async Task<bool> SaludAsync()
    {
        await using var cn = await AbrirAsync();
        return await cn.ExecuteScalarAsync<int>("SELECT 1") == 1;
    }
}
