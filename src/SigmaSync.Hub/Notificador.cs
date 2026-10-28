using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SigmaSync.Contracts;

namespace SigmaSync.Hub;

public interface IEnviadorCorreo
{
    bool Configurado { get; }
    Task EnviarAsync(string asunto, string cuerpo, CancellationToken ct);
}

/// <summary>Correo por el SMTP existente del cliente (STARTTLS en 587 por defecto).</summary>
public sealed class EnviadorSmtp(IOptions<OpcionesHub> opciones) : IEnviadorCorreo
{
    public bool Configurado => opciones.Value.Smtp.Configurado;

    public async Task EnviarAsync(string asunto, string cuerpo, CancellationToken ct)
    {
        var s = opciones.Value.Smtp;
        using var cliente = new SmtpClient(s.Host, s.Puerto) { EnableSsl = s.Ssl };
        if (s.Usuario.Length > 0) cliente.Credentials = new NetworkCredential(s.Usuario, Revelar(s.Clave));
        using var correo = new MailMessage { From = new MailAddress(s.De), Subject = asunto, Body = cuerpo, BodyEncoding = Encoding.UTF8 };
        foreach (var para in s.Para.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            correo.To.Add(para);
        await cliente.SendMailAsync(correo, ct);
    }

#pragma warning disable CA1416 // El hub corre en Windows.
    private static string Revelar(string valor) =>
        valor.StartsWith("dpapi:", StringComparison.Ordinal)
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(valor[6..]), null, DataProtectionScope.LocalMachine))
            : valor;
#pragma warning restore CA1416
}

/// <summary>
/// Cada IntervaloNotificacionMin: marca los agentes sin latido (una alerta por caída) y envía un solo correo con
/// las alertas nuevas (DLQ, rechazos, latido y advertencias de los nodos). Si el correo falla, se reintenta en el
/// siguiente ciclo; las alertas siguen visibles en el panel.
/// </summary>
public sealed class Notificador(RepositorioHub repo, IEnviadorCorreo correo, IOptions<OpcionesHub> opciones, ILogger<Notificador> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!opciones.Value.AlertasActivas)
        {
            log.LogWarning("Alertas desactivadas (Hub:AlertasActivas = false): no se revisan latidos ni se envían correos");
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, opciones.Value.IntervaloNotificacionMin)));
        do
        {
            try { await EjecutarCicloAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { log.LogError(ex, "Error en el notificador"); }
        }
        while (await EsperarAsync(timer, stoppingToken));
    }

    public async Task EjecutarCicloAsync(CancellationToken ct)
    {
        foreach (var nodo in await repo.DetectarNodosSinLatidoAsync(opciones.Value.MinutosSinLatido))
            log.LogWarning("El agente de {Nodo} no reporta latido", nodo);

        var pendientes = await repo.ListarAlertasAsync(null, soloPendientes: true, top: 200);
        if (pendientes.Count == 0 || !correo.Configurado) return;

        var (asunto, cuerpo) = Componer(pendientes);
        await correo.EnviarAsync(asunto, cuerpo, ct);
        await repo.MarcarNotificadasAsync(pendientes.Select(a => a.Id));
        log.LogInformation("Correo de alertas enviado: {N} alertas", pendientes.Count);
    }

    public static (string Asunto, string Cuerpo) Componer(IReadOnlyList<AlertaHub> alertas)
    {
        var errores = alertas.Count(a => a.Nivel == "ERROR");
        var asunto = $"[SigmaSync] {alertas.Count} alerta{(alertas.Count == 1 ? "" : "s")}" + (errores > 0 ? $" ({errores} con error)" : "");

        var sb = new StringBuilder();
        sb.AppendLine("Alertas de la sincronización SAINT (horas en UTC). Detalle y reproceso en el panel del hub: /panel").AppendLine();
        foreach (var grupo in alertas.OrderBy(a => a.Id).GroupBy(a => a.NodoId).OrderBy(g => g.Key))
        {
            sb.AppendLine($"── {grupo.Key} ──");
            foreach (var a in grupo)
                sb.AppendLine($"{a.Ts:yyyy-MM-dd HH:mm} {a.Nivel,-5} {a.Mensaje}{(a.MsgId is { } id ? $" [msg {id}]" : "")}");
            sb.AppendLine();
        }
        return (asunto, sb.ToString());
    }

    private static async Task<bool> EsperarAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
