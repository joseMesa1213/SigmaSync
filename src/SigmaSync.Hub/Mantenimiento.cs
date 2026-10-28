using Microsoft.Extensions.Options;

namespace SigmaSync.Hub;

/// <summary>Cada hora: pasa a DLQ los leases vencidos sin intentos restantes y purga los mensajes confirmados antiguos.</summary>
public sealed class Mantenimiento(RepositorioHub repo, IOptions<OpcionesHub> opciones, ILogger<Mantenimiento> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var opc = opciones.Value;
                var (aDlq, purgados) = await repo.MantenimientoAsync(opc.MaxIntentos, opc.RetencionDias);
                if (aDlq > 0) log.LogWarning("{N} mensajes pasaron a DLQ por lease vencido sin intentos restantes", aDlq);
                if (purgados > 0) log.LogInformation("{N} mensajes confirmados purgados", purgados);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Error en el mantenimiento del hub");
            }
        }
        while (await EsperarAsync(timer, stoppingToken));
    }

    private static async Task<bool> EsperarAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
