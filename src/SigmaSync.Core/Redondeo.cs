namespace SigmaSync.Core;

/// <summary>Redondeo como SQL Server al guardar en una columna decimal: mitad hacia afuera del cero.</summary>
public static class Redondeo
{
    public static decimal R(decimal valor, int decimales) => Math.Round(valor, decimales, MidpointRounding.AwayFromZero);
}
