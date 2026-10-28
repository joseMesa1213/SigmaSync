using SigmaSync.Contracts;
using SigmaSync.Saint;

namespace SigmaSync.Agent.Manejadores;

/// <summary>
/// F1 en el PV: registra la relación OC ↔ pedido (sync.DocRef), que F2 usa para ubicar la OC cuando llega la
/// factura. Un rechazo queda como alerta en sync.Log con los errores que envió el destino.
/// </summary>
public sealed class ManejadorAckPedido(ILogger<ManejadorAckPedido> log) : IManejadorMensaje
{
    public string Tipo => TiposMensaje.AckPedido;

    /// <summary>OC + estado: un CREADO posterior a un RECHAZADO de la misma OC (tras reproceso) no se descarta como duplicado.</summary>
    public string DocNumeroOrigen(Sobre sobre)
    {
        var ack = sobre.Leer<AckPedido>();
        return $"{ack.OcNumero}:{ack.Estado[..1]}";
    }

    public async Task ProcesarAsync(ContextoMensaje ctx, CancellationToken ct)
    {
        var ack = ctx.Sobre.Leer<AckPedido>();
        if (ack.Estado == EstadosAck.Creado && ack.PedidoNumero is { Length: > 0 } pedido)
        {
            await RepositorioSync.InsertarDocRefAsync(ctx.Cn, ctx.Tx, "OC_PEDIDO",
                ctx.NodoId, 'L', ack.OcNumero, ctx.Sobre.NodoOrigen, 'E', pedido, ctx.Sobre.MsgId);
            await RepositorioSync.RegistrarLogAsync(ctx.Cn, ctx.Tx, "INFO", "F1", ctx.Sobre.MsgId,
                $"OC {ack.OcNumero} → pedido {pedido} en {ctx.Sobre.NodoOrigen}");
            log.LogInformation("OC {Oc} → pedido {Pedido} en {Nodo}", ack.OcNumero, pedido, ctx.Sobre.NodoOrigen);
            return;
        }

        // Queda como INFO: el nodo que rechazó ya lo reportó como alerta al hub (evita duplicarla en el correo).
        var detalle = ack.Errores.Count == 0 ? "sin detalle" : string.Join("; ", ack.Errores.Select(e => $"{e.Codigo}: {e.Mensaje}"));
        await RepositorioSync.RegistrarLogAsync(ctx.Cn, ctx.Tx, "INFO", "F1", ctx.Sobre.MsgId,
            $"OC {ack.OcNumero} rechazada por {ctx.Sobre.NodoOrigen}: {detalle}");
        log.LogWarning("OC {Oc} rechazada por {Nodo}: {Errores}", ack.OcNumero, ctx.Sobre.NodoOrigen,
            string.Join("; ", ack.Errores.Select(e => e.Mensaje)));
    }

    public Sobre? RespuestaRechazo(Sobre sobre, string nodoId, IReadOnlyList<ErrorDoc> errores) => null;
}
