using SigmaSync.Contracts;
using SigmaSync.Saint;
using SigmaSync.Saint.F1;

namespace SigmaSync.Agent;

/// <summary>Comandos de instalación y diagnóstico del agente.</summary>
public static class Comandos
{
    public const string Ayuda = """
        SigmaSync.Agent — agente de sincronización SAINT

          (sin verbo) --nodo NODO                 corre el agente (consola o servicio Windows)
          instalar-esquema --nodo NODO            crea o actualiza el esquema sync y siembra sync.Config / sync.NodoTercero
          validar --nodo NODO                     valida esquema y configuración sin arrancar
          ping --nodo NODO --destino DESTINO      encola un PING; el destino responde PONG (ver sync.Log)
          enviar-oc --nodo PV --numero NUMERO     encola una OC puntual del PV hacia Fábrica o CEDI (F1)
          enviar-factura --nodo FAB --numero N    encola una factura puntual de Fábrica o CEDI hacia el PV (F3)
          estado --nodo NODO                      diagnóstico: Outbox, rechazos y últimas advertencias del nodo
          reintentar-outbox --nodo NODO           devuelve a PENDIENTE los mensajes que el hub rechazó
          proteger TEXTO                          cifra un secreto con DPAPI (máquina) para appsettings o Smtp:Clave del hub

        Configuración: appsettings.json + appsettings.{NODO}.json (+ appsettings.{NODO}.local.json)
                       y variables de entorno SIGMASYNC_Agente__Clave.
        """;

    public static async Task<int> InstalarEsquemaAsync(OpcionesAgente opc)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "sql", "sync");
        var archivos = Directory.GetFiles(dir, "*.sql", SearchOption.TopDirectoryOnly).ToList();
        var seed = Path.Combine(dir, "seed", $"{opc.NodoId}.sql");
        await ScriptsSql.EjecutarAsync(opc.ConnectionString, archivos, a => Console.WriteLine($"  aplicado {Path.GetFileName(a)}"));
        if (File.Exists(seed))
            await ScriptsSql.EjecutarAsync(opc.ConnectionString, [seed], a => Console.WriteLine($"  semilla  {Path.GetFileName(a)}"));
        else
            Console.WriteLine($"  sin semilla para {opc.NodoId} ({seed}); sync.Config debe llenarse a mano");

        return await ValidarAsync(opc);
    }

    public static async Task<int> ValidarAsync(OpcionesAgente opc)
    {
        await using var cn = await new RepositorioSync(opc.ConnectionString).AbrirAsync();
        var (problemas, rol) = await ValidadorEsquema.ValidarNodoAsync(cn, opc.NodoId);
        foreach (var p in problemas) Console.WriteLine($"  ✗ {p}");
        if (problemas.Count == 0) Console.WriteLine($"  ✓ esquema sync, configuración y columnas SAINT de {opc.NodoId} ({rol}) correctos");
        return problemas.Count == 0 ? 0 : 1;
    }

    public static async Task<int> PingAsync(OpcionesAgente opc, Argumentos a)
    {
        if (a.Opciones.GetValueOrDefault("destino") is not { Length: > 0 } destino)
        {
            Console.Error.WriteLine("Falta --destino.");
            return 2;
        }

        var ahora = DateTimeOffset.Now;
        var sobre = Sobre.Crear(TiposMensaje.Ping, opc.NodoId, destino,
            Sobre.Clave(opc.NodoId, "PING", ahora.ToString("yyyyMMddHHmmssfff")),
            new Ping(a.Opciones.GetValueOrDefault("texto") ?? $"ping desde {opc.NodoId}", ahora));

        await using var cn = await new RepositorioSync(opc.ConnectionString).AbrirAsync();
        await RepositorioSync.EncolarAsync(cn, null, sobre);
        Console.WriteLine($"PING {sobre.MsgId} encolado hacia {destino}. El PONG quedará en sync.Log (componente 'Ping').");
        return 0;
    }

    /// <summary>Encola una OC puntual del PV sin esperar la captura (p. ej. una OC anterior a CapturaDesde).</summary>
    public static async Task<int> EnviarOcAsync(OpcionesAgente opc, Argumentos a)
    {
        if (a.Opciones.GetValueOrDefault("numero") is not { Length: > 0 } numero)
        {
            Console.Error.WriteLine("Falta --numero (número de la OC, p. ej. 000003538).");
            return 2;
        }

        await using var cn = await new RepositorioSync(opc.ConnectionString).AbrirAsync();
        var cfg = ConfigNodo.Desde(await RepositorioSync.LeerConfigAsync(cn));
        if (cfg.Rol != Roles.Pv)
        {
            Console.Error.WriteLine($"enviar-oc solo aplica en un PV; {cfg.NodoId} es {cfg.Rol}.");
            return 2;
        }

        var oc = await OrigenOc.BuscarAsync(cn, cfg, numero);
        if (oc is null)
        {
            Console.Error.WriteLine($"La OC {numero} no existe o su proveedor no corresponde a Fábrica ni a CEDI en sync.NodoTercero.");
            return 1;
        }

        var encolada = await CapturaOc.EncolarAsync(cn, cfg, oc, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, default);
        Console.WriteLine(encolada
            ? $"OC {numero} encolada hacia {oc.NodoDestino}."
            : $"OC {numero} no se encoló: ya tenía mensaje en sync.Outbox o no tiene ítems.");
        return encolada ? 0 : 1;
    }

    /// <summary>Encola una factura puntual de Fábrica o CEDI hacia el PV de su cliente, sin esperar la captura.</summary>
    public static async Task<int> EnviarFacturaAsync(OpcionesAgente opc, Argumentos a)
    {
        if (a.Opciones.GetValueOrDefault("numero") is not { Length: > 0 } numero)
        {
            Console.Error.WriteLine("Falta --numero (número de la factura tal como está en SAINT, p. ej. *33570 o 009950).");
            return 2;
        }

        await using var cn = await new RepositorioSync(opc.ConnectionString).AbrirAsync();
        var cfg = ConfigNodo.Desde(await RepositorioSync.LeerConfigAsync(cn));
        if (!Roles.EsProveedorInterno(cfg.Rol))
        {
            Console.Error.WriteLine($"enviar-factura solo aplica en Fábrica o CEDI; {cfg.NodoId} es {cfg.Rol}.");
            return 2;
        }

        var f = await Saint.F3.OrigenFactura.BuscarAsync(cn, cfg, numero);
        if (f is null)
        {
            Console.Error.WriteLine($"La factura {numero} no existe o su cliente no corresponde a un PV en sync.NodoTercero.");
            return 1;
        }

        var encolada = await CapturaFactura.EncolarAsync(cn, cfg, f, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, default);
        Console.WriteLine(encolada
            ? $"Factura {numero} encolada hacia {f.NodoDestino}."
            : $"Factura {numero} no se encoló: ya tenía mensaje en sync.Outbox o no tiene ítems.");
        return encolada ? 0 : 1;
    }

    /// <summary>Diagnóstico rápido del nodo: Outbox, Inbox rechazados y últimas advertencias o errores.</summary>
    public static async Task<int> EstadoAsync(OpcionesAgente opc)
    {
        await using var cn = await new RepositorioSync(opc.ConnectionString).AbrirAsync();
        var (pendientes, conError) = await RepositorioSync.ContarOutboxAsync(cn);
        Console.WriteLine($"Nodo {opc.NodoId} · Outbox: {pendientes} pendientes, {conError} con error (rechazados por el hub)");

        Console.WriteLine("\nÚltimos mensajes rechazados (sync.Inbox):");
        foreach (var r in await Dapper.SqlMapper.QueryAsync(cn,
                     "SELECT TOP 10 procesado_ts, tipo, nodo_origen, doc_numero_origen, error FROM sync.Inbox WHERE estado = 'RECHAZADO' ORDER BY procesado_ts DESC"))
            Console.WriteLine($"  {r.procesado_ts:yyyy-MM-dd HH:mm} {r.tipo} {r.doc_numero_origen} de {r.nodo_origen}: {r.error}");

        Console.WriteLine("\nÚltimas advertencias y errores (sync.Log):");
        foreach (var r in await Dapper.SqlMapper.QueryAsync(cn,
                     "SELECT TOP 15 ts, nivel, componente, mensaje FROM sync.Log WHERE nivel IN ('WARN', 'ERROR') ORDER BY id DESC"))
            Console.WriteLine($"  {r.ts:yyyy-MM-dd HH:mm} {r.nivel,-5} {r.componente}: {r.mensaje}");

        Console.WriteLine("\nOutbox con error:");
        foreach (var r in await Dapper.SqlMapper.QueryAsync(cn,
                     "SELECT TOP 10 creado_ts, tipo, nodo_destino, doc_numero, error FROM sync.Outbox WHERE estado = 'ERROR' ORDER BY id DESC"))
            Console.WriteLine($"  {r.creado_ts:yyyy-MM-dd HH:mm} {r.tipo} {r.doc_numero} → {r.nodo_destino}: {r.error}");
        return 0;
    }

    public static async Task<int> ReintentarOutboxAsync(OpcionesAgente opc)
    {
        await using var cn = await new RepositorioSync(opc.ConnectionString).AbrirAsync();
        var n = await RepositorioSync.ReintentarOutboxConErrorAsync(cn);
        Console.WriteLine($"{n} mensajes del Outbox vuelven a PENDIENTE; el agente los publica en su próximo ciclo.");
        return 0;
    }

    public static int Proteger(Argumentos a)
    {
        if (a.Posicionales.Count == 0)
        {
            Console.Error.WriteLine("Uso: proteger TEXTO");
            return 2;
        }
        Console.WriteLine(Secretos.Proteger(a.Posicionales[0]));
        return 0;
    }
}
