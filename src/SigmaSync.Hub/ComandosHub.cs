using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SigmaSync.Contracts;
using SigmaSync.Saint;

namespace SigmaSync.Hub;

/// <summary>Comandos de instalación del hub.</summary>
public static class ComandosHub
{
    public const string Ayuda = """
        SigmaSync.Hub — SyncHub de SigmaSync

          (sin verbo)                              corre el hub (consola o servicio Windows)
          instalar-bd                              crea la base del hub si no existe y aplica los scripts
          registrar-nodo NODO "Descripción"        crea o rota la API key y el secreto HMAC de un nodo
          hash-admin CLAVE                         calcula Hub:AdminKeyHash para habilitar /v1/admin

        Configuración: appsettings.json (+ appsettings.local.json) y variables SIGMASYNC_Hub__Clave.
        """;

    public static IConfiguration CargarConfiguracion() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.local.json", optional: true)
            .AddEnvironmentVariables("SIGMASYNC_")
            .Build();

    public static async Task<int> EjecutarAsync(string[] args)
    {
        var opc = CargarConfiguracion().GetSection("Hub").Get<OpcionesHub>() ?? new OpcionesHub();
        switch (args[0])
        {
            case "instalar-bd":
                await InstalarBdAsync(opc.ConnectionString);
                return 0;
            case "registrar-nodo" when args.Length >= 3:
                await RegistrarNodoAsync(opc.ConnectionString, args[1], args[2]);
                return 0;
            case "hash-admin" when args.Length >= 2:
                Console.WriteLine(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(args[1]))));
                return 0;
            default:
                Console.WriteLine(Ayuda);
                return args[0] is "ayuda" or "help" or "--help" ? 0 : 2;
        }
    }

    public static async Task InstalarBdAsync(string connectionString)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);
        var baseDatos = csb.InitialCatalog;
        if (string.IsNullOrWhiteSpace(baseDatos)) throw new InvalidOperationException("La cadena de conexión no indica la base (Database=...).");

        csb.InitialCatalog = "master";
        await using (var cn = new SqlConnection(csb.ConnectionString))
        {
            await cn.OpenAsync();
            await using var cmd = new SqlCommand("""
                IF DB_ID(@db) IS NULL
                BEGIN
                    DECLARE @sql nvarchar(300) = N'CREATE DATABASE ' + QUOTENAME(@db);
                    EXEC (@sql);
                END
                """, cn);
            cmd.Parameters.AddWithValue("@db", baseDatos);
            await cmd.ExecuteNonQueryAsync();
        }
        Console.WriteLine($"  base {baseDatos} lista");

        var archivos = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "sql", "hub"), "*.sql");
        await ScriptsSql.EjecutarAsync(connectionString, archivos, a => Console.WriteLine($"  aplicado {Path.GetFileName(a)}"));
    }

    public static async Task RegistrarNodoAsync(string connectionString, string nodoId, string descripcion)
    {
        if (nodoId.Length is 0 or > 10) throw new ArgumentException("El nodo debe tener entre 1 y 10 caracteres.");

        var apiKey = Base64Url(RandomNumberGenerator.GetBytes(32));
        var secreto = RandomNumberGenerator.GetBytes(32);
        await new RepositorioHub(connectionString)
            .RegistrarNodoAsync(nodoId, descripcion, FirmaHmac.HashApiKey(apiKey), new ProtectorSecretos().Proteger(secreto));

        Console.WriteLine($"""
            Nodo {nodoId} registrado. Guarde estos valores: no se pueden volver a consultar.
            En el servidor del nodo, protéjalos con "SigmaSync.Agent.exe proteger VALOR" y póngalos en appsettings.{nodoId}.json:

              "ApiKey": "{apiKey}",
              "HmacSecret": "{Convert.ToBase64String(secreto)}"
            """);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
