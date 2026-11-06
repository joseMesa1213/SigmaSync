using System.Globalization;

namespace SigmaSync.Saint;

public static class Roles
{
    public const string Pv = "PV";
    public const string Fabrica = "FAB";
    public const string Cedi = "CEDI";

    public static bool EsProveedorInterno(string rol) => rol is Fabrica or Cedi;
}

/// <summary>Valores de negocio del nodo en sync.Config. Se leen en cada uso para poder cambiarlos sin redesplegar.</summary>
public sealed record ConfigNodo(
    string NodoId,
    string Rol,
    string CodSucu,
    string CodUsua,
    string CodEsta,
    string? CodUbic,
    string? CodVend,
    string? CodOper,
    int VentanaDias,
    int EsperaSegundos,
    DateTime CapturaDesde,
    bool ActualizarPedidoPV = true)
{
    public static ConfigNodo Desde(IReadOnlyDictionary<string, string> c)
    {
        string? Opcional(string clave) => c.TryGetValue(clave, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        string Requerido(string clave) => Opcional(clave) ?? throw new InvalidOperationException($"Falta '{clave}' en sync.Config.");

        return new ConfigNodo(
            NodoId: Requerido("NodoId"),
            Rol: Requerido("Rol"),
            CodSucu: Requerido("CodSucu"),
            CodUsua: Requerido("CodUsua"),
            CodEsta: Requerido("CodEsta"),
            CodUbic: Opcional("CodUbic"),
            CodVend: Opcional("CodVend"),
            CodOper: Opcional("CodOper"),
            VentanaDias: int.TryParse(Opcional("VentanaDias"), out var v) ? v : 3,
            EsperaSegundos: int.TryParse(Opcional("EsperaSegundos"), out var e) ? e : 60,
            CapturaDesde: DateTime.TryParse(Opcional("CapturaDesde"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : DateTime.Today,
            // Encendida salvo '0': la prueba P2 (2026-11-05) confirmó que SAINT sube Pedido y CantPed al grabar la OC.
            ActualizarPedidoPV: Opcional("ActualizarPedidoPV") != "0");
    }
}
