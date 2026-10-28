namespace SigmaSync.Core.F1;

/// <summary>Cliente que representa al PV en la base destino (SACLIE).</summary>
public sealed record ClienteDestino(
    string CodClie,
    string Descrip,
    string? ID3,
    string? Direc1,
    string? Direc2,
    string? Telef,
    int DiasCred);

/// <summary>Producto en la base destino (SAPROD + tarifa de impuestos de venta de SATAXPRD, sin retenciones).</summary>
public sealed record ProductoDestino(
    string CodProd,
    string Descrip,
    string? Descrip2,
    string? Descrip3,
    decimal Precio3,
    decimal CostAct,
    bool EsExento,
    decimal TarifaImpuesto,
    bool Activo)
{
    /// <summary>Sin tarifa de impuesto el ítem va exento aunque el maestro no lo marque.</summary>
    public bool Exento => EsExento || TarifaImpuesto == 0;
}

/// <summary>
/// Decimales de las columnas de SAITEMFAC en la base destino. Varían entre versiones de SAINT
/// (Fábrica: PriceO 3, MtoTax 3, MtoTaxO 4; CEDI: PriceO 4, MtoTax 4, MtoTaxO 3), por eso se leen de sys.columns.
/// </summary>
public sealed record EscalasPedido(int PriceO, int TotalItem, int MtoTax, int MtoTaxO)
{
    public static readonly EscalasPedido Fabrica = new(PriceO: 3, TotalItem: 4, MtoTax: 3, MtoTaxO: 4);
}

/// <summary>Valores del nodo destino (sync.Config) y del momento de grabación.</summary>
public sealed record ParametrosPedido(
    string CodSucu,
    string CodUbic,
    string? CodVend,
    string? CodOper,
    string CodUsua,
    string CodEsta,
    string NodoOrigen,
    DateTime Ahora);

public sealed record PedidoEncabezado(
    string CodSucu,
    string CodClie,
    string Descrip,
    string? ID3,
    string? Direc1,
    string? Direc2,
    string? Telef,
    string OrdenC,
    string CodUbic,
    string? CodVend,
    string? CodOper,
    string CodUsua,
    string CodEsta,
    DateTime FechaE,
    DateTime FechaV,
    decimal TGravable,
    decimal TExento,
    decimal Monto,
    decimal MtoTax,
    decimal MtoTotal,
    string Notas1);

public sealed record PedidoItem(
    int NroLinea,
    string CodItem,
    string Descrip1,
    string? Descrip2,
    string? Descrip3,
    decimal Cantidad,
    decimal Costo,
    decimal Precio,
    decimal PriceO,
    decimal TotalItem,
    decimal MtoTax,
    decimal MtoTaxO,
    bool EsExento);

public sealed record PedidoArmado(PedidoEncabezado Encabezado, IReadOnlyList<PedidoItem> Items);
