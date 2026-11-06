using Dapper;
using Microsoft.Data.SqlClient;
using SigmaSync.Contracts;

namespace SigmaSync.Saint;

/// <summary>Documento SAINT que origina un mensaje (para la deduplicación de la captura en Outbox).</summary>
public sealed record DocOrigen(char Tipo, string Numero, int? NroUnico = null);

public sealed record OutboxFila(long Id, Guid MsgId, string Tipo, string NodoDestino, string Sobre);

public sealed record InboxFila(Guid MsgId, string Estado, int Intentos, string? Resultado);

public static class EstadosInbox
{
    public const string Procesado = "PROCESADO";
    public const string Rechazado = "RECHAZADO";
}

/// <summary>Acceso a las tablas del esquema sync de una base SAINT.</summary>
public sealed class RepositorioSync(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public async Task<SqlConnection> AbrirAsync(CancellationToken ct = default)
    {
        var cn = new SqlConnection(ConnectionString);
        await cn.OpenAsync(ct);
        return cn;
    }

    // ── Config ───────────────────────────────────────────────────────────────

    public static async Task<IReadOnlyDictionary<string, string>> LeerConfigAsync(SqlConnection cn, SqlTransaction? tx = null)
    {
        var filas = await cn.QueryAsync<(string Clave, string Valor)>("SELECT clave, valor FROM sync.Config", transaction: tx);
        return filas.ToDictionary(f => f.Clave, f => f.Valor, StringComparer.OrdinalIgnoreCase);
    }

    public static Task GuardarConfigAsync(SqlConnection cn, string clave, string valor) =>
        cn.ExecuteAsync("""
            MERGE sync.Config WITH (HOLDLOCK) AS t USING (SELECT @clave AS clave) AS s ON t.clave = s.clave
            WHEN MATCHED THEN UPDATE SET valor = @valor
            WHEN NOT MATCHED THEN INSERT (clave, valor) VALUES (@clave, @valor);
            """, new { clave, valor });

    // ── Outbox ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Encola un sobre. Si trae documento de origen y ya existe un mensaje del mismo tipo para ese
    /// documento y destino, no inserta y devuelve false (deduplicación de la captura).
    /// </summary>
    public static async Task<bool> EncolarAsync(SqlConnection cn, SqlTransaction? tx, Sobre sobre, DocOrigen? doc = null)
    {
        const string sql = """
            INSERT sync.Outbox (msg_id, tipo, nodo_destino, clave_particion, doc_tipo, doc_numero, doc_nro_unico, sobre)
            SELECT @MsgId, @Tipo, @NodoDestino, @ClaveParticion, @DocTipo, @DocNumero, @DocNroUnico, @Sobre
            WHERE @DocNumero IS NULL
               OR NOT EXISTS (SELECT 1 FROM sync.Outbox WITH (UPDLOCK, HOLDLOCK)
                              WHERE tipo = @Tipo AND doc_tipo = @DocTipo AND doc_numero = @DocNumero AND nodo_destino = @NodoDestino);
            """;
        var filas = await cn.ExecuteAsync(sql, new
        {
            sobre.MsgId,
            sobre.Tipo,
            sobre.NodoDestino,
            sobre.ClaveParticion,
            DocTipo = doc?.Tipo.ToString(),
            DocNumero = doc?.Numero,
            DocNroUnico = doc?.NroUnico,
            Sobre = sobre.ToJson(),
        }, tx);
        return filas == 1;
    }

    public static async Task<IReadOnlyList<OutboxFila>> ObtenerPendientesAsync(SqlConnection cn, int max) =>
        (await cn.QueryAsync<OutboxFila>(
            "SELECT TOP (@max) id, msg_id AS MsgId, tipo, nodo_destino AS NodoDestino, sobre FROM sync.Outbox WHERE estado = 'PENDIENTE' ORDER BY id",
            new { max })).AsList();

    public static Task MarcarEnviadoAsync(SqlConnection cn, long id) =>
        cn.ExecuteAsync("UPDATE sync.Outbox SET estado = 'ENVIADO', enviado_ts = SYSDATETIME(), intentos = intentos + 1, error = NULL WHERE id = @id", new { id });

    /// <summary>Error transitorio de envío: el mensaje sigue PENDIENTE y se reintenta en el próximo ciclo.</summary>
    public static Task RegistrarFalloEnvioAsync(SqlConnection cn, long id, string error) =>
        cn.ExecuteAsync("UPDATE sync.Outbox SET intentos = intentos + 1, error = LEFT(@error, 2000) WHERE id = @id", new { id, error });

    /// <summary>El hub rechazó el mensaje de forma definitiva (4xx): no se reintenta.</summary>
    public static Task MarcarErrorAsync(SqlConnection cn, long id, string error) =>
        cn.ExecuteAsync("UPDATE sync.Outbox SET estado = 'ERROR', intentos = intentos + 1, error = LEFT(@error, 2000) WHERE id = @id", new { id, error });

    public static Task<(int Pendientes, int ConError)> ContarOutboxAsync(SqlConnection cn) =>
        cn.QuerySingleAsync<(int, int)>("""
            SELECT ISNULL(SUM(CASE WHEN estado = 'PENDIENTE' THEN 1 ELSE 0 END), 0),
                   ISNULL(SUM(CASE WHEN estado = 'ERROR' THEN 1 ELSE 0 END), 0)
            FROM sync.Outbox
            """);

    // ── Inbox ────────────────────────────────────────────────────────────────

    public static Task<InboxFila?> ObtenerInboxAsync(SqlConnection cn, SqlTransaction? tx, Guid msgId) =>
        cn.QuerySingleOrDefaultAsync<InboxFila>(
            "SELECT msg_id AS MsgId, estado, intentos, resultado FROM sync.Inbox WHERE msg_id = @msgId", new { msgId }, tx);

    /// <summary>¿El mismo documento de origen ya se procesó con otro msg_id? (idempotencia de negocio)</summary>
    public static async Task<bool> DocYaProcesadoAsync(SqlConnection cn, SqlTransaction? tx, Sobre sobre, string docNumeroOrigen) =>
        await cn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sync.Inbox
            WHERE nodo_origen = @NodoOrigen AND tipo = @Tipo AND doc_numero_origen = @doc
              AND estado = 'PROCESADO' AND msg_id <> @MsgId
            """, new { sobre.NodoOrigen, sobre.Tipo, doc = docNumeroOrigen, sobre.MsgId }, tx) > 0;

    public static Task RegistrarInboxAsync(SqlConnection cn, SqlTransaction? tx, Sobre sobre, string docNumeroOrigen,
        string estado, string? resultado, string? error) =>
        cn.ExecuteAsync("""
            MERGE sync.Inbox WITH (HOLDLOCK) AS t
            USING (SELECT @MsgId AS msg_id) AS s ON t.msg_id = s.msg_id
            WHEN MATCHED THEN UPDATE SET
                estado = @estado, intentos = t.intentos + 1, procesado_ts = SYSDATETIME(),
                resultado = @resultado, error = LEFT(@error, 2000)
            WHEN NOT MATCHED THEN INSERT (msg_id, tipo, nodo_origen, doc_numero_origen, estado, procesado_ts, resultado, error)
                VALUES (@MsgId, @Tipo, @NodoOrigen, @doc, @estado, SYSDATETIME(), @resultado, LEFT(@error, 2000));
            """, new { sobre.MsgId, sobre.Tipo, sobre.NodoOrigen, doc = docNumeroOrigen, estado, resultado, error }, tx);

    // ── NodoTercero y DocRef ─────────────────────────────────────────────────

    /// <summary>Código exacto del tercero (SACLIE / SAPROV de esta base) que representa a un nodo.</summary>
    public static Task<string?> CodTerceroDeNodoAsync(SqlConnection cn, SqlTransaction? tx, string tipoTercero, string nodoId) =>
        cn.QueryFirstOrDefaultAsync<string?>("""
            SELECT cod_tercero FROM sync.NodoTercero
            WHERE tipo_tercero = @tipoTercero AND nodo_id = @nodoId AND activo = 1
            ORDER BY id
            """, new { tipoTercero, nodoId }, tx);

    /// <summary>En el PV: OC que originó un pedido sincronizado de Fábrica o CEDI (relación OC_PEDIDO registrada con el ACK).</summary>
    public static Task<string?> OcDePedidoAsync(SqlConnection cn, SqlTransaction? tx, string nodoPedido, string pedido) =>
        cn.QueryFirstOrDefaultAsync<string?>("""
            SELECT TOP 1 num_origen FROM sync.DocRef
            WHERE relacion = 'OC_PEDIDO' AND activo = 1 AND nodo_destino = @nodoPedido AND tipo_destino = 'E' AND num_destino = @pedido
            ORDER BY id DESC
            """, new { nodoPedido, pedido }, tx);

    /// <summary>Registra la relación entre dos documentos. Si ya existe activa, no la duplica.</summary>
    public static Task InsertarDocRefAsync(SqlConnection cn, SqlTransaction? tx, string relacion,
        string nodoOrigen, char tipoOrigen, string numOrigen, string nodoDestino, char tipoDestino, string numDestino, Guid? msgId) =>
        cn.ExecuteAsync("""
            INSERT sync.DocRef (relacion, nodo_origen, tipo_origen, num_origen, nodo_destino, tipo_destino, num_destino, msg_id)
            SELECT @relacion, @nodoOrigen, @tipoOrigen, @numOrigen, @nodoDestino, @tipoDestino, @numDestino, @msgId
            WHERE NOT EXISTS (SELECT 1 FROM sync.DocRef
                              WHERE relacion = @relacion AND nodo_origen = @nodoOrigen AND tipo_origen = @tipoOrigen AND num_origen = @numOrigen
                                AND nodo_destino = @nodoDestino AND tipo_destino = @tipoDestino AND num_destino = @numDestino AND activo = 1)
            """, new
        {
            relacion, nodoOrigen, tipoOrigen = tipoOrigen.ToString(), numOrigen,
            nodoDestino, tipoDestino = tipoDestino.ToString(), numDestino, msgId,
        }, tx);

    // ── Log ──────────────────────────────────────────────────────────────────

    public static Task<long> MaxLogIdAsync(SqlConnection cn) =>
        cn.ExecuteScalarAsync<long>("SELECT ISNULL(MAX(id), 0) FROM sync.Log");

    /// <summary>
    /// Advertencias y errores de sync.Log posteriores a <paramref name="desdeId"/>, para enviarlos con el latido.
    /// sync.Log guarda la hora local del servidor; Ts sale en UTC, como todas las horas del hub.
    /// </summary>
    public static async Task<IReadOnlyList<AlertaNodo>> AlertasDesdeAsync(SqlConnection cn, long desdeId, int max) =>
        (await cn.QueryAsync<AlertaNodo>("""
            SELECT TOP (@max) id AS LogId, CAST(DATEADD(MINUTE, DATEDIFF(MINUTE, SYSDATETIME(), SYSUTCDATETIME()), ts) AS datetime) AS Ts,
                   nivel, componente, msg_id AS MsgId, mensaje
            FROM sync.Log WHERE id > @desdeId AND nivel IN ('WARN', 'ERROR')
            ORDER BY id
            """, new { desdeId, max })).AsList();

    /// <summary>Purga lo que ya no se necesita: Outbox enviado y Log antiguos. Inbox y DocRef se conservan (idempotencia y trazabilidad).</summary>
    public static async Task<(int Outbox, int Log)> PurgarAsync(SqlConnection cn, int diasOutbox, int diasLog)
    {
        var outbox = await cn.ExecuteAsync(
            "DELETE TOP (5000) FROM sync.Outbox WHERE estado = 'ENVIADO' AND enviado_ts < DATEADD(DAY, -@diasOutbox, SYSDATETIME())", new { diasOutbox });
        var log = await cn.ExecuteAsync(
            "DELETE TOP (5000) FROM sync.Log WHERE ts < DATEADD(DAY, -@diasLog, SYSDATETIME())", new { diasLog });
        return (outbox, log);
    }

    /// <summary>Devuelve a PENDIENTE los mensajes del Outbox que el hub rechazó (tras corregir la causa, p. ej. un nodo no registrado).</summary>
    public static Task<int> ReintentarOutboxConErrorAsync(SqlConnection cn) =>
        cn.ExecuteAsync("UPDATE sync.Outbox SET estado = 'PENDIENTE', error = NULL WHERE estado = 'ERROR'");

    public static Task RegistrarLogAsync(SqlConnection cn, SqlTransaction? tx, string nivel, string componente,
        Guid? msgId, string mensaje, string? detalle = null) =>
        cn.ExecuteAsync("""
            INSERT sync.Log (nivel, componente, msg_id, mensaje, detalle)
            VALUES (@nivel, @componente, @msgId, LEFT(@mensaje, 500), @detalle)
            """, new { nivel, componente, msgId, mensaje, detalle }, tx);
}
