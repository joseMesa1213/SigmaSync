using SigmaSync.Contracts;
using SigmaSync.Saint;

namespace SigmaSync.Agent.Manejadores;

/// <summary>
/// En Fábrica o CEDI: registra qué OC del PV quedó para cada factura (sync.DocRef) y deja en sync.Log las
/// alertas o el rechazo que informó el PV.
/// </summary>
public sealed class ManejadorAckFactura(ILogger<ManejadorAckFactura> log) : IManejadorMensaje
{
    public string Tipo => TiposMensaje.AckFactura;

    public string DocNumeroOrigen(Sobre sobre)
    {
        var ack = sobre.Leer<AckFactura>();
        return $"{ack.FacturaNumero}:{ack.Estado[..1]}";
    }

    public async Task ProcesarAsync(ContextoMensaje ctx, CancellationToken ct)
    {
        var ack = ctx.Sobre.Leer<AckFactura>();
        var pv = ctx.Sobre.NodoOrigen;

        foreach (var a in ack.Acciones)
        {
            await RepositorioSync.InsertarDocRefAsync(ctx.Cn, ctx.Tx, "FACTURA_OC", ctx.NodoId, 'A', ack.FacturaNumero, pv, 'L', a.OcNumero,
                ctx.Sobre.MsgId);
            await RepositorioSync.RegistrarLogAsync(ctx.Cn, ctx.Tx, "INFO", "F3", ctx.Sobre.MsgId,
                $"Factura {ack.FacturaNumero} → {a.Tipo} {a.OcNumero} en {pv}");
            log.LogInformation("Factura {Factura} → {Accion} {Oc} en {Pv}", ack.FacturaNumero, a.Tipo, a.OcNumero, pv);
        }

        // Quedan como INFO: el PV ya las reportó como alertas al hub (evita duplicarlas en el correo).
        foreach (var a in ack.Alertas)
            await RepositorioSync.RegistrarLogAsync(ctx.Cn, ctx.Tx, "INFO", "F3", ctx.Sobre.MsgId,
                $"Alerta del PV en la factura {ack.FacturaNumero}: {a.Codigo}: {a.Mensaje}", a.CodItem);
        foreach (var e in ack.Errores)
            await RepositorioSync.RegistrarLogAsync(ctx.Cn, ctx.Tx, "INFO", "F3", ctx.Sobre.MsgId,
                $"Factura {ack.FacturaNumero} rechazada por {pv}: {e.Codigo}: {e.Mensaje}", e.CodItem);
        if (ack.Errores.Count > 0)
            log.LogWarning("Factura {Factura} rechazada por {Pv}: {Errores}", ack.FacturaNumero, pv,
                string.Join("; ", ack.Errores.Select(e => e.Mensaje)));
    }

    public Sobre? RespuestaRechazo(Sobre sobre, string nodoId, IReadOnlyList<ErrorDoc> errores) => null;
}
