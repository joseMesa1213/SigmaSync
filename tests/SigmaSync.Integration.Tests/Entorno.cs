using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SigmaSync.Agent;
using SigmaSync.Agent.Manejadores;
using SigmaSync.Contracts;
using SigmaSync.Hub;
using SigmaSync.Saint;

namespace SigmaSync.Integration.Tests;

[CollectionDefinition(Nombre)]
public sealed class ColeccionLocalDb : ICollectionFixture<Entorno>
{
    public const string Nombre = "LocalDB";
}

/// <summary>
/// Entorno de integración en LocalDB con bases propias (SigmaSyncTest_*), separadas de las copias SAINT:
/// un hub en memoria (WebApplicationFactory) y dos nodos, NODO_A y NODO_B, con el esquema sync.
/// </summary>
public sealed class Entorno : IAsyncLifetime
{
    public const string NodoA = "NODO_A";
    public const string NodoB = "NODO_B";
    public const string ClaveAdmin = "admin-pruebas";

    private const string Servidor = @"(localdb)\MSSQLLocalDB";
    private readonly Dictionary<string, (string ApiKey, byte[] Secreto)> _credenciales = new()
    {
        [NodoA] = ("api-key-a", Enumerable.Repeat((byte)0xA1, 32).ToArray()),
        [NodoB] = ("api-key-b", Enumerable.Repeat((byte)0xB2, 32).ToArray()),
    };

    public string CsHub { get; } = Cs("SigmaSyncTest_Hub");
    public string CsA { get; } = Cs("SigmaSyncTest_A");
    public string CsB { get; } = Cs("SigmaSyncTest_B");
    public WebApplicationFactory<Program> Hub { get; private set; } = null!;

    public static string Cs(string db) =>
        $"Server={Servidor};Database={db};Integrated Security=true;TrustServerCertificate=true;Application Name=SigmaSync.Tests";

    public string CsDe(string nodo) => nodo == NodoA ? CsA : CsB;

    public async Task InitializeAsync()
    {
        var raiz = RaizRepositorio();
        foreach (var cs in new[] { CsHub, CsA, CsB }) await RecrearBaseAsync(cs);

        await ScriptsSql.EjecutarAsync(CsHub, Directory.GetFiles(Path.Combine(raiz, "db", "hub"), "*.sql"));
        foreach (var (cs, nodo) in new[] { (CsA, NodoA), (CsB, NodoB) })
        {
            await ScriptsSql.EjecutarAsync(cs, Directory.GetFiles(Path.Combine(raiz, "db", "sync"), "*.sql"));
            await using var cn = new SqlConnection(cs);
            await cn.ExecuteAsync("""
                INSERT sync.Config (clave, valor) VALUES
                  ('NodoId', @nodo), ('Rol', 'PV'), ('CodSucu', '00000'), ('CodUsua', 'SYNC'), ('CodEsta', 'SYNC')
                """, new { nodo });
        }

        var repoHub = new RepositorioHub(CsHub);
        var protector = new ProtectorSecretos();
        foreach (var (nodo, (apiKey, secreto)) in _credenciales)
            await repoHub.RegistrarNodoAsync(nodo, $"Nodo de prueba {nodo}", FirmaHmac.HashApiKey(apiKey), protector.Proteger(secreto));

        Hub = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hub:ConnectionString"] = CsHub,
                ["Hub:MaxIntentos"] = "2",
                ["Hub:BackoffSegundos:0"] = "0",
                ["Hub:AdminKeyHash"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(ClaveAdmin))),
                ["Hub:AlertasActivas"] = "false",   // las pruebas ejecutan el notificador a mano
            }));
            b.ConfigureTestServices(s => s.AddSingleton<IEnviadorCorreo>(Correo));
        });
    }

    public CorreoFalso Correo { get; } = new();

    public Notificador Notificador => Hub.Services.GetRequiredService<Notificador>();

    public async Task DisposeAsync()
    {
        if (Hub is not null) await Hub.DisposeAsync();
        SqlConnection.ClearAllPools();
    }

    /// <summary>Deja vacías la cola del hub y las tablas de mensajes de ambos nodos.</summary>
    public async Task LimpiarAsync()
    {
        await using (var cn = new SqlConnection(CsHub))
            await cn.ExecuteAsync("DELETE hub.Mensaje; DELETE hub.Alerta; UPDATE hub.Nodo SET sin_latido_desde = NULL, ultimo_latido = SYSUTCDATETIME();");
        foreach (var cs in new[] { CsA, CsB })
        {
            await using var cn = new SqlConnection(cs);
            await cn.ExecuteAsync("DELETE sync.Outbox; DELETE sync.Inbox; DELETE sync.Log; DELETE sync.DocRef; DELETE sync.Config WHERE clave = 'AlertasEnviadasHasta';");
        }
        Correo.Enviados.Clear();
    }

    public Latido Latido(string nodo) => new(new RepositorioSync(CsDe(nodo)), ClienteHub(nodo), Opciones(nodo), new EstadoAgente());

    /// <summary>Captura los correos en memoria en lugar de enviarlos.</summary>
    public sealed class CorreoFalso : IEnviadorCorreo
    {
        public List<(string Asunto, string Cuerpo)> Enviados { get; } = [];
        public bool Configurado => true;

        public Task EnviarAsync(string asunto, string cuerpo, CancellationToken ct)
        {
            Enviados.Add((asunto, cuerpo));
            return Task.CompletedTask;
        }
    }

    public HttpClient ClienteHttp(string nodo, string? apiKey = null, byte[]? secreto = null)
    {
        var (k, s) = _credenciales[nodo];
        return Hub.CreateDefaultClient(new Uri("http://localhost/"), new ManejadorFirmaHmac(nodo, apiKey ?? k, secreto ?? s));
    }

    public HttpClient ClienteAdmin()
    {
        var http = Hub.CreateClient();
        http.DefaultRequestHeaders.Add(AutenticacionNodo.HeaderAdmin, ClaveAdmin);
        return http;
    }

    public ClienteHub ClienteHub(string nodo) => new(ClienteHttp(nodo));

    public OpcionesAgente Opciones(string nodo) => new() { NodoId = nodo, ConnectionString = CsDe(nodo), MaxPull = 10, MaxPublicacion = 50 };

    public Publicador Publicador(string nodo) =>
        new(new RepositorioSync(CsDe(nodo)), ClienteHub(nodo), Opciones(nodo), new EstadoAgente(), NullLogger<Publicador>.Instance);

    public Consumidor Consumidor(string nodo, params IManejadorMensaje[] extra) =>
        new(new RepositorioSync(CsDe(nodo)), ClienteHub(nodo),
            [new ManejadorPing(NullLogger<ManejadorPing>.Instance), new ManejadorPong(NullLogger<ManejadorPong>.Instance), .. extra],
            Opciones(nodo), new EstadoAgente(), NullLogger<Consumidor>.Instance);

    public async Task<T> ConsultarAsync<T>(string cs, string sql, object? param = null)
    {
        await using var cn = new SqlConnection(cs);
        return await cn.ExecuteScalarAsync<T>(sql, param) ?? default!;
    }

    private static async Task RecrearBaseAsync(string cs)
    {
        var db = new SqlConnectionStringBuilder(cs).InitialCatalog;
        await using var cn = new SqlConnection(Cs("master"));
        await cn.ExecuteAsync("""
            DECLARE @sql nvarchar(400);
            IF DB_ID(@db) IS NOT NULL
            BEGIN
                SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@db);
                EXEC (@sql);
            END
            SET @sql = N'CREATE DATABASE ' + QUOTENAME(@db);
            EXEC (@sql);
            """, new { db });
    }

    public static string RaizRepositorio()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SigmaSync.slnx"))) return dir.FullName;
        throw new InvalidOperationException("No se encontró SigmaSync.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
