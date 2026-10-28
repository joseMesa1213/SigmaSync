using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SigmaSync.Saint;

/// <summary>Ejecuta scripts .sql con separadores GO (como sqlcmd), en orden de nombre de archivo.</summary>
public static partial class ScriptsSql
{
    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SeparadorGo();

    public static IReadOnlyList<string> DividirLotes(string script) =>
        SeparadorGo().Split(script).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public static async Task EjecutarAsync(string connectionString, IEnumerable<string> archivos, Action<string>? alAplicar = null, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(ct);
        foreach (var archivo in archivos.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var lote in DividirLotes(await File.ReadAllTextAsync(archivo, ct)))
            {
                await using var cmd = new SqlCommand(lote, cn) { CommandTimeout = 300 };
                await cmd.ExecuteNonQueryAsync(ct);
            }
            alAplicar?.Invoke(archivo);
        }
    }
}
