using SigmaSync.Saint;

namespace SigmaSync.Agent;

/// <summary>Envía al hub los mensajes PENDIENTE de sync.Outbox, en orden de id.</summary>
public sealed class Publicador(RepositorioSync repo, ClienteHub hub, OpcionesAgente opc, EstadoAgente estado, ILogger<Publicador> log) : ICiclo
{
    public TimeSpan Intervalo => TimeSpan.FromSeconds(opc.IntervaloPublicacionSeg);

    public async Task EjecutarAsync(CancellationToken ct)
    {
        await using var cn = await repo.AbrirAsync(ct);
        foreach (var fila in await RepositorioSync.ObtenerPendientesAsync(cn, opc.MaxPublicacion))
        {
            try
            {
                var insertado = await hub.PushAsync(fila.Sobre, ct);
                await RepositorioSync.MarcarEnviadoAsync(cn, fila.Id);
                log.LogInformation("Publicado {Tipo} {MsgId} → {Destino}{Dup}", fila.Tipo, fila.MsgId, fila.NodoDestino,
                    insertado ? "" : " (ya estaba en el hub)");
            }
            catch (HubException ex) when (ex.EsDefinitivo)
            {
                // El hub no acepta el mensaje (p. ej. destino desconocido): se aparta para revisión y se sigue con el resto.
                await RepositorioSync.MarcarErrorAsync(cn, fila.Id, ex.Message);
                await RepositorioSync.RegistrarLogAsync(cn, null, "ERROR", "Publicador", fila.MsgId, "El hub rechazó el mensaje", ex.Message);
                estado.RegistrarError(ex.Message);
                log.LogError("El hub rechazó {Tipo} {MsgId}: {Error}", fila.Tipo, fila.MsgId, ex.Message);
            }
            catch (Exception ex) when (ex is HubException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Hub inaccesible: se corta el lote para conservar el orden y se reintenta en el próximo ciclo.
                await RepositorioSync.RegistrarFalloEnvioAsync(cn, fila.Id, ex.Message);
                estado.RegistrarError(ex.Message);
                log.LogWarning("No se pudo publicar {MsgId}; se reintenta en el próximo ciclo: {Error}", fila.MsgId, ex.Message);
                break;
            }
        }
    }
}
