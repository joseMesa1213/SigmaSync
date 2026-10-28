namespace SigmaSync.Agent;

/// <summary>
/// Línea de comandos: SigmaSync.Agent.exe [verbo] --nodo NODO [--opcion valor]…
/// Sin verbo, corre el servicio.
/// </summary>
public sealed record Argumentos(string? Verbo, IReadOnlyList<string> Posicionales, IReadOnlyDictionary<string, string> Opciones)
{
    public string? Nodo => Opciones.GetValueOrDefault("nodo");

    public static Argumentos Parsear(string[] args)
    {
        var posicionales = new List<string>();
        var opciones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var clave = args[i][2..];
                var valor = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
                opciones[clave] = valor;
            }
            else
            {
                posicionales.Add(args[i]);
            }
        }
        return new Argumentos(posicionales.FirstOrDefault(), posicionales.Skip(1).ToList(), opciones);
    }
}
