using Microsoft.Data.SqlClient;
using SigmaSync.Contracts;
using SigmaSync.Saint;
using SigmaSync.Saint.F3;

namespace SigmaSync.Agent;

/// <summary>F2/F3 en Fábrica o CEDI: detecta las facturas nuevas a un PV y las deja en sync.Outbox como FACTURA_OC.</summary>
public sealed class CapturaFactura(RepositorioSync repo, OpcionesAgente opc, ILogger<CapturaFactura> log) : ICiclo
{
    public TimeSpan Intervalo => TimeSpan.FromSeconds(opc.IntervaloCapturaSeg);

    public async Task EjecutarAsync(CancellationToken ct)
    {
        await using var cn = await repo.AbrirAsync(ct);
        var cfg = ConfigNodo.Desde(await RepositorioSync.LeerConfigAsync(cn));
        foreach (var f in await OrigenFactura.CandidatasAsync(cn, cfg, DateTime.Now))
            await EncolarAsync(cn, cfg, f, log, ct);
    }

    /// <summary>Encola una factura. Devuelve false si ya tenía mensaje o si todavía no tiene ítems.</summary>
    public static async Task<bool> EncolarAsync(SqlConnection cn, ConfigNodo cfg, FacturaCandidata f, ILogger log, CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var payload = await OrigenFactura.LeerPayloadAsync(cn, tx, f, cfg.NodoId);
        if (payload.Items.Count == 0)
        {
            log.LogDebug("Factura {Factura} sin ítems todavía; se revisa en el próximo ciclo", f.NumeroD);
            return false;
        }

        var sobre = Sobre.Crear(TiposMensaje.FacturaOc, cfg.NodoId, f.NodoDestino, Sobre.Clave(cfg.NodoId, "A", f.NumeroD), payload);
        var encolada = await RepositorioSync.EncolarAsync(cn, tx, sobre, new DocOrigen('A', f.NumeroD, f.NroUnico));
        await tx.CommitAsync(ct);
        if (encolada)
            log.LogInformation("Factura {Factura} ({Items} ítems) capturada para {Destino}", f.NumeroD, payload.Items.Count, f.NodoDestino);
        return encolada;
    }
}
