using Dapper;
using Microsoft.Data.SqlClient;
using SigmaSync.Saint;

namespace SigmaSync.Integration.Tests;

[CollectionDefinition(Nombre)]
public sealed class ColeccionSaint : ICollectionFixture<CopiasSaint>
{
    public const string Nombre = "Copias SAINT";
}

/// <summary>
/// Copias SAINT restauradas en LocalDB (ADM_MAY, SAINT_ADM, BODEGA1). Al iniciar asegura el esquema sync y las
/// semillas (idempotente). Las pruebas escriben dentro de una transacción que se deshace: no dejan rastro.
/// </summary>
public sealed class CopiasSaint : IAsyncLifetime
{
    public string Pv { get; } = Entorno.Cs("ADM_MAY");
    public string Fab { get; } = Entorno.Cs("SAINT_ADM");
    public string Cedi { get; } = Entorno.Cs("BODEGA1");

    public async Task InitializeAsync()
    {
        var sync = Path.Combine(Entorno.RaizRepositorio(), "db", "sync");
        foreach (var (cs, nodo) in new[] { (Pv, "PV_MAY"), (Fab, "FAB"), (Cedi, "CEDI") })
        {
            await ScriptsSql.EjecutarAsync(cs, Directory.GetFiles(sync, "*.sql"));
            await ScriptsSql.EjecutarAsync(cs, [Path.Combine(sync, "seed", $"{nodo}.sql")]);
        }
    }

    public Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        return Task.CompletedTask;
    }

    /// <summary>Columnas de una fila con valor (no NULL, no 0, no vacías): sirve para comparar contra un documento de SAINT.</summary>
    public static async Task<SortedSet<string>> ColumnasConValorAsync(SqlConnection cn, SqlTransaction? tx, string tabla, string where, object param)
    {
        // Sin TRY_CONVERT: SAINT_ADM y BODEGA1 están en nivel de compatibilidad 100.
        var columnas = await cn.QueryAsync<(string Nombre, string Tipo)>("""
            SELECT c.name, TYPE_NAME(c.system_type_id) FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(@tabla)
              AND TYPE_NAME(c.system_type_id) NOT IN ('image', 'varbinary', 'text', 'ntext', 'timestamp', 'uniqueidentifier', 'sql_variant', 'xml')
            """, new { tabla = "dbo." + tabla }, tx);
        var numericos = new[] { "int", "smallint", "tinyint", "bigint", "bit", "decimal", "numeric", "money", "smallmoney", "float", "real" };
        var texto = new[] { "char", "varchar", "nchar", "nvarchar" };
        var expresiones = columnas.Select(c =>
            numericos.Contains(c.Tipo) ? $"CASE WHEN ISNULL([{c.Nombre}], 0) = 0 THEN 0 ELSE 1 END AS [{c.Nombre}]"
            : texto.Contains(c.Tipo) ? $"CASE WHEN LTRIM(ISNULL([{c.Nombre}], '')) = '' THEN 0 ELSE 1 END AS [{c.Nombre}]"
            : $"CASE WHEN [{c.Nombre}] IS NULL THEN 0 ELSE 1 END AS [{c.Nombre}]");
        var fila = (IDictionary<string, object>)await cn.QuerySingleAsync($"SELECT {string.Join(", ", expresiones)} FROM dbo.{tabla} WHERE {where}", param, tx);
        return new SortedSet<string>(fila.Where(kv => Convert.ToInt32(kv.Value) == 1).Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
    }
}
