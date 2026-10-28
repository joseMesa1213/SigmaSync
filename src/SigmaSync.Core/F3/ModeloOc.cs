namespace SigmaSync.Core.F3;

/// <summary>Proveedor que representa en el PV al nodo que facturó (SAPROV del PV).</summary>
public sealed record ProveedorPv(string CodProv, string Descrip, string? ID3);

/// <summary>Producto en el maestro del PV (SAPROD) con su existencia en el depósito de la OC (SAEXIS).</summary>
public sealed record ProductoPv(
    string CodProd,
    string Descrip,
    string? Descrip2,
    string? Descrip3,
    string? Refere,
    decimal Precio1,
    decimal Precio2,
    decimal Precio3,
    bool EsExento,
    bool Activo,
    decimal Existen);

/// <summary>Decimales de SAITEMCOM en el PV (se leen de sys.columns).</summary>
public sealed record EscalasOc(int Costo, int TotalItem)
{
    public static readonly EscalasOc Pv = new(Costo: 4, TotalItem: 4);
}

public sealed record ParametrosOc(
    string CodSucu,
    string CodUbic,
    string CodUsua,
    string CodEsta,
    string NodoOrigen);

public sealed record OcEncabezadoPv(
    string CodSucu,
    string CodProv,
    string Descrip,
    string? ID3,
    string CodUbic,
    string CodUsua,
    string CodEsta,
    DateTime FechaE,
    decimal TGravable,
    decimal TExento,
    decimal Monto,
    string OrdenC,
    string Notas1);

public sealed record OcItemPv(
    int NroLinea,
    string CodItem,
    string? Refere,
    string Descrip1,
    string? Descrip2,
    string? Descrip3,
    decimal Cantidad,
    decimal Costo,
    decimal TotalItem,
    decimal Precio1,
    decimal Precio2,
    decimal Precio3,
    decimal ExistAnt,
    bool EsExento);

public sealed record OcArmada(OcEncabezadoPv Encabezado, IReadOnlyList<OcItemPv> Items);
