using Dapper;
using Microsoft.Data.SqlClient;

namespace SigmaSync.Saint;

/// <summary>
/// Validación al arrancar el agente: el esquema sync está instalado en la versión esperada y la
/// configuración del nodo coincide. Las columnas SAINT de cada escritor se agregan con cada flujo (F1, F2, F3).
/// </summary>
public static class ValidadorEsquema
{
    public const int VersionRequerida = 1;

    private static readonly string[] TablasSync = ["SchemaVersion", "Config", "NodoTercero", "Outbox", "Inbox", "DocRef", "Log"];
    private static readonly string[] ClavesConfig = ["NodoId", "Rol", "CodSucu", "CodUsua", "CodEsta"];

    public static async Task<IReadOnlyList<string>> ValidarAsync(SqlConnection cn, string nodoIdEsperado,
        IEnumerable<(string Tabla, string Columna)>? columnasSaint = null)
    {
        var problemas = new List<string>();

        var tablas = (await cn.QueryAsync<string>(
            "SELECT t.name FROM sys.tables t WHERE t.schema_id = SCHEMA_ID('sync')")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in TablasSync.Where(t => !tablas.Contains(t)))
            problemas.Add($"Falta la tabla sync.{t}. Ejecute 'instalar-esquema'.");
        if (problemas.Count > 0) return problemas;

        var version = await cn.ExecuteScalarAsync<int?>("SELECT MAX(version) FROM sync.SchemaVersion") ?? 0;
        if (version < VersionRequerida)
            problemas.Add($"El esquema sync está en la versión {version}; se requiere {VersionRequerida}. Ejecute 'instalar-esquema'.");

        var config = await RepositorioSync.LeerConfigAsync(cn);
        foreach (var c in ClavesConfig.Where(c => !config.ContainsKey(c)))
            problemas.Add($"Falta la clave '{c}' en sync.Config.");
        if (config.TryGetValue("NodoId", out var nodo) && !string.Equals(nodo, nodoIdEsperado, StringComparison.OrdinalIgnoreCase))
            problemas.Add($"sync.Config.NodoId = '{nodo}' no coincide con el agente configurado como '{nodoIdEsperado}'.");

        if (columnasSaint is not null)
        {
            var existentes = (await cn.QueryAsync<(string Tabla, string Columna)>("""
                SELECT t.name, c.name FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
                WHERE t.schema_id = SCHEMA_ID('dbo')
                """)).Select(x => $"{x.Tabla}.{x.Columna}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (tabla, columna) in columnasSaint.Where(x => !existentes.Contains($"{x.Tabla}.{x.Columna}")))
                problemas.Add($"La columna SAINT {tabla}.{columna} no existe en esta base.");
        }

        return problemas;
    }

    /// <summary>Validación completa del nodo: esquema sync, configuración y columnas SAINT que usa su rol.</summary>
    public static async Task<(IReadOnlyList<string> Problemas, string? Rol)> ValidarNodoAsync(SqlConnection cn, string nodoId)
    {
        var problemas = await ValidarAsync(cn, nodoId);
        if (problemas.Count > 0) return (problemas, null);

        var rol = (await RepositorioSync.LeerConfigAsync(cn))["Rol"].Trim().ToUpperInvariant();
        IReadOnlyList<(string, string)> columnas;
        try { columnas = ColumnasPorRol.Para(rol); }
        catch (InvalidOperationException ex) { return ([ex.Message], null); }

        problemas = await ValidarAsync(cn, nodoId, columnas);
        return (problemas, problemas.Count == 0 ? rol : null);
    }
}
