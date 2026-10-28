using Microsoft.Data.SqlClient;
using SigmaSync.Contracts;
using SigmaSync.Saint;
using SigmaSync.Saint.F1;

namespace SigmaSync.Agent;

/// <summary>F1 en el PV: detecta las OC nuevas a Fábrica o CEDI y las deja en sync.Outbox como OC_PEDIDO.</summary>
public sealed class CapturaOc(RepositorioSync repo, OpcionesAgente opc, ILogger<CapturaOc> log) : ICiclo
{
    public TimeSpan Intervalo => TimeSpan.FromSeconds(opc.IntervaloCapturaSeg);

    public async Task EjecutarAsync(CancellationToken ct)
    {
        await using var cn = await repo.AbrirAsync(ct);
        var cfg = ConfigNodo.Desde(await RepositorioSync.LeerConfigAsync(cn));
        foreach (var oc in await OrigenOc.CandidatasAsync(cn, cfg, DateTime.Now))
            await EncolarAsync(cn, cfg, oc, log, ct);
    }

    /// <summary>Encola una OC. Devuelve false si ya tenía mensaje (no se duplica) o si todavía no tiene ítems.</summary>
    public static async Task<bool> EncolarAsync(SqlConnection cn, ConfigNodo cfg, OcCandidata oc, ILogger log, CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var payload = await OrigenOc.LeerPayloadAsync(cn, tx, oc);
        if (payload.Items.Count == 0)
        {
            log.LogDebug("OC {Oc} sin ítems todavía; se revisa en el próximo ciclo", oc.NumeroD);
            return false;
        }

        var sobre = Sobre.Crear(TiposMensaje.OcPedido, cfg.NodoId, oc.NodoDestino, Sobre.Clave(cfg.NodoId, "L", oc.NumeroD), payload);
        var encolada = await RepositorioSync.EncolarAsync(cn, tx, sobre, new DocOrigen('L', oc.NumeroD, oc.NroUnico));
        await tx.CommitAsync(ct);
        if (encolada)
            log.LogInformation("OC {Oc} ({Items} ítems) capturada para {Destino}", oc.NumeroD, payload.Items.Count, oc.NodoDestino);
        return encolada;
    }
}
