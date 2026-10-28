using Microsoft.Data.SqlClient;
using SigmaSync.Agent.Manejadores;
using SigmaSync.Contracts;
using SigmaSync.Saint;

namespace SigmaSync.Agent;

/// <summary>
/// Trae mensajes del hub y procesa cada uno en una transacción:
/// documento SAINT + sync.DocRef + sync.Inbox + respuestas en sync.Outbox. Después confirma (ack) al hub.
/// </summary>
public sealed class Consumidor : ICiclo
{
    private readonly RepositorioSync _repo;
    private readonly ClienteHub _hub;
    private readonly OpcionesAgente _opc;
    private readonly EstadoAgente _estado;
    private readonly ILogger<Consumidor> _log;
    private readonly Dictionary<string, IManejadorMensaje> _manejadores;

    public Consumidor(RepositorioSync repo, ClienteHub hub, IEnumerable<IManejadorMensaje> manejadores,
        OpcionesAgente opc, EstadoAgente estado, ILogger<Consumidor> log)
    {
        _repo = repo;
        _hub = hub;
        _opc = opc;
        _estado = estado;
        _log = log;
        _manejadores = manejadores.ToDictionary(m => m.Tipo, StringComparer.Ordinal);
    }

    public TimeSpan Intervalo => TimeSpan.FromSeconds(_opc.IntervaloPullSeg);

    public async Task EjecutarAsync(CancellationToken ct)
    {
        // Se repite mientras el hub entregue lotes llenos, para vaciar la cola sin esperar al siguiente ciclo.
        IReadOnlyList<Sobre> lote;
        do
        {
            lote = await _hub.PullAsync(_opc.MaxPull, ct);
            foreach (var sobre in lote)
            {
                var (resultado, error) = await ProcesarAsync(sobre, ct);
                try
                {
                    await _hub.AckAsync(sobre.MsgId, resultado, error, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Si el ack no llega, el hub reentrega al vencer el lease y sync.Inbox evita el duplicado.
                    _estado.RegistrarError($"ack {sobre.MsgId}: {ex.Message}");
                    _log.LogWarning("No se pudo confirmar {MsgId} al hub: {Error}", sobre.MsgId, ex.Message);
                }
            }
        }
        while (lote.Count == _opc.MaxPull && !ct.IsCancellationRequested);
    }

    public async Task<(string Resultado, string? Error)> ProcesarAsync(Sobre sobre, CancellationToken ct)
    {
        await using var cn = await _repo.AbrirAsync(ct);

        var inbox = await RepositorioSync.ObtenerInboxAsync(cn, null, sobre.MsgId);
        if (inbox?.Estado == EstadosInbox.Procesado)
        {
            _log.LogInformation("{Tipo} {MsgId} ya estaba procesado; se confirma de nuevo", sobre.Tipo, sobre.MsgId);
            return (ResultadosAck.Ok, null);
        }

        if (!sobre.VersionCompatible)
            return await RechazarAsync(cn, sobre, null, ClaveCorta(sobre),
                [new ErrorDoc(CodigosError.VersionNoSoportada, $"Versión de contrato {sobre.Version} no soportada")], ct);

        if (!_manejadores.TryGetValue(sobre.Tipo, out var manejador))
            return await RechazarAsync(cn, sobre, null, ClaveCorta(sobre),
                [new ErrorDoc(CodigosError.TipoNoSoportado, $"El nodo {_opc.NodoId} no procesa mensajes {sobre.Tipo}")], ct);

        var docNumero = manejador.DocNumeroOrigen(sobre);
        if (await RepositorioSync.DocYaProcesadoAsync(cn, null, sobre, docNumero))
        {
            await RepositorioSync.RegistrarLogAsync(cn, null, "WARN", "Consumidor", sobre.MsgId,
                $"{sobre.Tipo} {docNumero} de {sobre.NodoOrigen} ya se había procesado con otro msg_id; se descarta");
            return (ResultadosAck.Ok, null);
        }

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var ctx = new ContextoMensaje(sobre, cn, tx, _opc.NodoId);
        try
        {
            await manejador.ProcesarAsync(ctx, ct);
            await RepositorioSync.RegistrarInboxAsync(cn, tx, sobre, docNumero, EstadosInbox.Procesado,
                string.Join(",", ctx.Respuestas), null);
            foreach (var a in ctx.Alertas)
                await RepositorioSync.RegistrarLogAsync(cn, tx, "WARN", "Consumidor", sobre.MsgId, $"{a.Codigo}: {a.Mensaje}", a.CodItem);
            await tx.CommitAsync(ct);
            foreach (var a in ctx.Alertas)
                _log.LogWarning("Alerta en {Tipo} {MsgId}: {Codigo}: {Mensaje}", sobre.Tipo, sobre.MsgId, a.Codigo, a.Mensaje);
            _log.LogInformation("Procesado {Tipo} {MsgId} de {Origen}", sobre.Tipo, sobre.MsgId, sobre.NodoOrigen);
            return (ResultadosAck.Ok, null);
        }
        catch (ErrorNegocioException ex)
        {
            await tx.RollbackAsync(ct);
            return await RechazarAsync(cn, sobre, manejador, docNumero, ex.Errores, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch (Exception) { /* la conexión pudo cerrarse */ }
            _estado.RegistrarError($"{sobre.Tipo} {sobre.MsgId}: {ex.Message}");
            _log.LogError(ex, "Error procesando {Tipo} {MsgId}; el hub lo reintentará", sobre.Tipo, sobre.MsgId);
            await RegistrarLogSeguroAsync(sobre.MsgId, "ERROR", $"Error procesando {sobre.Tipo}: {ex.Message}", ex.ToString());
            return (ResultadosAck.Reintentar, ex.Message);
        }
    }

    /// <summary>Rechazo de negocio: Inbox RECHAZADO + respuesta de rechazo al origen, en una transacción aparte.</summary>
    private async Task<(string, string?)> RechazarAsync(SqlConnection cn, Sobre sobre, IManejadorMensaje? manejador,
        string docNumero, IReadOnlyList<ErrorDoc> errores, CancellationToken ct)
    {
        var resumen = string.Join("; ", errores.Select(e => $"{e.Codigo}: {e.Mensaje}"));
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var respuesta = manejador?.RespuestaRechazo(sobre, _opc.NodoId, errores);
        if (respuesta is not null) await RepositorioSync.EncolarAsync(cn, tx, respuesta);
        await RepositorioSync.RegistrarInboxAsync(cn, tx, sobre, docNumero, EstadosInbox.Rechazado, respuesta?.MsgId.ToString(), resumen);
        await RepositorioSync.RegistrarLogAsync(cn, tx, "WARN", "Consumidor", sobre.MsgId, $"{sobre.Tipo} rechazado: {resumen}");
        await tx.CommitAsync(ct);
        _log.LogWarning("Rechazado {Tipo} {MsgId}: {Errores}", sobre.Tipo, sobre.MsgId, resumen);
        return (ResultadosAck.Rechazado, resumen);
    }

    private async Task RegistrarLogSeguroAsync(Guid msgId, string nivel, string mensaje, string detalle)
    {
        try
        {
            await using var cn = await _repo.AbrirAsync();
            await RepositorioSync.RegistrarLogAsync(cn, null, nivel, "Consumidor", msgId, mensaje, detalle);
        }
        catch (Exception ex)
        {
            _log.LogWarning("No se pudo escribir en sync.Log: {Error}", ex.Message);
        }
    }

    private static string ClaveCorta(Sobre sobre) => sobre.MsgId.ToString("N")[..20];
}
