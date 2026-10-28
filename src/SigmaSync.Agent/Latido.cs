using System.Reflection;
using SigmaSync.Contracts;
using SigmaSync.Saint;

namespace SigmaSync.Agent;

/// <summary>
/// Informa al hub que el agente está vivo, con su versión, lo que tiene pendiente y las advertencias o errores
/// nuevos de sync.Log (el hub los centraliza y los envía por correo). El cursor de lo ya enviado se guarda en
/// sync.Config ("AlertasEnviadasHasta"); el hub además deduplica por id de log.
/// </summary>
public sealed class Latido(RepositorioSync repo, ClienteHub hub, OpcionesAgente opc, EstadoAgente estado) : ICiclo
{
    public const string ClaveCursor = "AlertasEnviadasHasta";
    private const int MaxAlertasPorLatido = 50;

    public static readonly string Version =
        typeof(Latido).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public TimeSpan Intervalo => TimeSpan.FromSeconds(opc.IntervaloLatidoSeg);

    public async Task EjecutarAsync(CancellationToken ct)
    {
        await using var cn = await repo.AbrirAsync(ct);
        var (pendientes, conError) = await RepositorioSync.ContarOutboxAsync(cn);

        var config = await RepositorioSync.LeerConfigAsync(cn);
        IReadOnlyList<AlertaNodo> alertas = [];
        if (!config.TryGetValue(ClaveCursor, out var valor) || !long.TryParse(valor, out var cursor))
        {
            // Primera vez: se reporta desde ahora, no el histórico.
            await RepositorioSync.GuardarConfigAsync(cn, ClaveCursor, (await RepositorioSync.MaxLogIdAsync(cn)).ToString());
        }
        else
        {
            alertas = await RepositorioSync.AlertasDesdeAsync(cn, cursor, MaxAlertasPorLatido);
        }

        await hub.LatidoAsync(new LatidoSolicitud(Version, pendientes, conError, estado.UltimoError, DateTimeOffset.Now, alertas), ct);
        if (alertas.Count > 0)
            await RepositorioSync.GuardarConfigAsync(cn, ClaveCursor, alertas[^1].LogId.ToString());
    }
}

/// <summary>Cada 6 horas purga Outbox enviado (90 días) y sync.Log (180 días) del nodo.</summary>
public sealed class MantenimientoNodo(RepositorioSync repo, ILogger<MantenimientoNodo> log) : ICiclo
{
    public TimeSpan Intervalo => TimeSpan.FromHours(6);

    public async Task EjecutarAsync(CancellationToken ct)
    {
        await using var cn = await repo.AbrirAsync(ct);
        var (outbox, registros) = await RepositorioSync.PurgarAsync(cn, diasOutbox: 90, diasLog: 180);
        if (outbox + registros > 0)
            log.LogInformation("Purga: {Outbox} mensajes enviados y {Log} registros de log antiguos", outbox, registros);
    }
}
