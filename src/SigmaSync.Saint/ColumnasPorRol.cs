using SigmaSync.Saint.F1;
using SigmaSync.Saint.F3;

namespace SigmaSync.Saint;

/// <summary>Columnas SAINT que el agente lee o escribe según el rol del nodo; las valida al arrancar.</summary>
public static class ColumnasPorRol
{
    public static IReadOnlyList<(string Tabla, string Columna)> Para(string rol) => rol switch
    {
        Roles.Pv => [.. OrigenOc.Columnas, .. DestinoOc.Columnas],
        Roles.Fabrica or Roles.Cedi => [.. DestinoPedido.Columnas, .. OrigenFactura.Columnas],
        _ => throw new InvalidOperationException($"Rol desconocido en sync.Config: '{rol}' (se espera PV, FAB o CEDI)."),
    };
}
