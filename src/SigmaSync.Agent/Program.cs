using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using Serilog.Events;
using SigmaSync.Agent;
using SigmaSync.Agent.Manejadores;
using SigmaSync.Contracts;
using SigmaSync.Saint;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var argumentos = Argumentos.Parsear(args);

if (argumentos.Verbo is "ayuda" or "help" || argumentos.Opciones.ContainsKey("help"))
{
    Console.WriteLine(Comandos.Ayuda);
    return 0;
}
if (argumentos.Verbo == "proteger") return Comandos.Proteger(argumentos);

if (argumentos.Nodo is not { Length: > 0 } nodo)
{
    Console.Error.WriteLine("Falta --nodo NODO.\n");
    Console.WriteLine(Comandos.Ayuda);
    return 2;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    // Los argumentos se interpretan arriba; no se pasan a la configuración.
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration
    .AddJsonFile($"appsettings.{nodo}.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"appsettings.{nodo}.local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("SIGMASYNC_");

var opc = builder.Configuration.GetSection("Agente").Get<OpcionesAgente>() ?? new OpcionesAgente();
if (string.IsNullOrWhiteSpace(opc.NodoId)) opc.NodoId = nodo;

var dirLogs = Path.IsPathRooted(opc.DirectorioLogs) ? opc.DirectorioLogs : Path.Combine(AppContext.BaseDirectory, opc.DirectorioLogs);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .Enrich.WithProperty("Nodo", opc.NodoId)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{Nodo}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(dirLogs, $"agente-{opc.NodoId}-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    switch (argumentos.Verbo)
    {
        case "instalar-esquema": return await Comandos.InstalarEsquemaAsync(opc);
        case "validar": return await Comandos.ValidarAsync(opc);
        case "ping": return await Comandos.PingAsync(opc, argumentos);
        case "enviar-oc": return await Comandos.EnviarOcAsync(opc, argumentos);
        case "enviar-factura": return await Comandos.EnviarFacturaAsync(opc, argumentos);
        case "estado": return await Comandos.EstadoAsync(opc);
        case "reintentar-outbox": return await Comandos.ReintentarOutboxAsync(opc);
        case null: break;
        default:
            Console.Error.WriteLine($"Verbo desconocido: {argumentos.Verbo}\n");
            Console.WriteLine(Comandos.Ayuda);
            return 2;
    }

    var erroresConfig = opc.Validar();
    if (erroresConfig.Count > 0)
    {
        foreach (var e in erroresConfig) Log.Fatal("Configuración inválida: {Error}", e);
        return 1;
    }

    string rol;
    await using (var cn = await new RepositorioSync(opc.ConnectionString).AbrirAsync())
    {
        var (problemas, rolNodo) = await ValidadorEsquema.ValidarNodoAsync(cn, opc.NodoId);
        if (problemas.Count > 0 || rolNodo is null)
        {
            foreach (var p in problemas) Log.Fatal("Validación de esquema: {Problema}", p);
            return 1;
        }
        rol = rolNodo;
    }

    builder.Services.AddSerilog();
    builder.Services.AddWindowsService(o => o.ServiceName = $"SigmaSync-{opc.NodoId}");

    builder.Services.AddSingleton(opc);
    builder.Services.AddSingleton<EstadoAgente>();
    builder.Services.AddSingleton(new RepositorioSync(opc.ConnectionString));

    var secreto = Secretos.SecretoHmac(opc.HmacSecret);
    var apiKey = Secretos.Revelar(opc.ApiKey);
    builder.Services.AddHttpClient<ClienteHub>(c =>
        {
            c.BaseAddress = new Uri(opc.HubUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new ManejadorFirmaHmac(opc.NodoId, apiKey, secreto));

    builder.Services.AddSingleton<IManejadorMensaje, ManejadorPing>();
    builder.Services.AddSingleton<IManejadorMensaje, ManejadorPong>();
    if (rol == Roles.Pv)
    {
        // F1 en el PV: captura de OC y registro del pedido creado en el destino.
        builder.Services.AddSingleton<IManejadorMensaje, ManejadorAckPedido>();
        builder.Services.AddSingleton<CapturaOc>();
        builder.Services.AddHostedService<ServicioCiclico<CapturaOc>>();
        // F3 (y detección de F2) en el PV: factura → OC.
        builder.Services.AddSingleton<IManejadorMensaje, ManejadorFacturaOc>();
    }
    else
    {
        // F1 en Fábrica o CEDI: OC del PV → pedido.
        builder.Services.AddSingleton<IManejadorMensaje, ManejadorOcPedido>();
        // F2/F3 en Fábrica o CEDI: captura de facturas a los PV y registro de la OC resultante.
        builder.Services.AddSingleton<IManejadorMensaje, ManejadorAckFactura>();
        builder.Services.AddSingleton<CapturaFactura>();
        builder.Services.AddHostedService<ServicioCiclico<CapturaFactura>>();
    }

    builder.Services.AddSingleton<Publicador>();
    builder.Services.AddSingleton<Consumidor>();
    builder.Services.AddSingleton<Latido>();
    builder.Services.AddSingleton<MantenimientoNodo>();
    builder.Services.AddHostedService<ServicioCiclico<Publicador>>();
    builder.Services.AddHostedService<ServicioCiclico<Consumidor>>();
    builder.Services.AddHostedService<ServicioCiclico<Latido>>();
    builder.Services.AddHostedService<ServicioCiclico<MantenimientoNodo>>();

    Log.Information("SigmaSync.Agent {Version} iniciando: nodo {Nodo} ({Rol}), hub {Hub}{Servicio}", Latido.Version, opc.NodoId, rol,
        opc.HubUrl, WindowsServiceHelpers.IsWindowsService() ? " (servicio Windows)" : "");
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "El agente se detuvo por un error no controlado");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
