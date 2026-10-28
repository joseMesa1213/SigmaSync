using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using SigmaSync.Contracts;
using SigmaSync.Hub;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
    return await ComandosHub.EjecutarAsync(args);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // La configuración se lee junto al ejecutable, se lance desde donde se lance (como servicio el directorio actual es System32).
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables("SIGMASYNC_");

var seccion = builder.Configuration.GetSection("Hub");
builder.Services.Configure<OpcionesHub>(seccion);
var dirLogs = seccion["DirectorioLogs"] ?? "logs";
if (!Path.IsPathRooted(dirLogs)) dirLogs = Path.Combine(AppContext.BaseDirectory, dirLogs);

builder.Services.AddSerilog(lc => lc
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [HUB] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(dirLogs, "hub-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

builder.Services.AddWindowsService(o => o.ServiceName = "SigmaSync-Hub");
builder.WebHost.UseUrls(seccion["Url"] ?? "http://localhost:5080");
builder.Services.ConfigureHttpJsonOptions(o => SigmaJson.Aplicar(o.SerializerOptions));

builder.Services.AddSingleton<ProtectorSecretos>();
builder.Services.AddSingleton(sp => new RepositorioHub(sp.GetRequiredService<IOptions<OpcionesHub>>().Value.ConnectionString));
builder.Services.AddHostedService<Mantenimiento>();
builder.Services.AddSingleton<IEnviadorCorreo, EnviadorSmtp>();
builder.Services.AddSingleton<Notificador>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Notificador>());

var app = builder.Build();
app.UseMiddleware<AutenticacionNodo>();
app.MapearEndpoints();

await app.RunAsync();
return 0;

public partial class Program;
