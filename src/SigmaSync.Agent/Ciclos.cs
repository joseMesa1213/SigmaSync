namespace SigmaSync.Agent;

/// <summary>Trabajo periódico del agente (publicar, consumir, latido…).</summary>
public interface ICiclo
{
    TimeSpan Intervalo { get; }
    Task EjecutarAsync(CancellationToken ct);
}

/// <summary>
/// Ejecuta un <see cref="ICiclo"/> cada <see cref="ICiclo.Intervalo"/>. Un error en un ciclo se registra
/// y no detiene el servicio. Es genérico para que el host registre un servicio por ciclo.
/// </summary>
public sealed class ServicioCiclico<T>(T ciclo, EstadoAgente estado, ILogger<ServicioCiclico<T>> log) : BackgroundService
    where T : ICiclo
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ciclo.Intervalo);
        do
        {
            try
            {
                await ciclo.EjecutarAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                estado.RegistrarError($"{typeof(T).Name}: {ex.Message}");
                log.LogError(ex, "Error en el ciclo {Ciclo}", typeof(T).Name);
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
