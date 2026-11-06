using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.SqlClient;
using SigmaSync.Core.F1;

namespace SigmaSync.Saint.F1;

/// <summary>F1 en Fábrica o CEDI: lectura de maestros y escritura del pedido E (SAFACT / SAITEMFAC).</summary>
public static class DestinoPedido
{
    /// <summary>Columnas SAINT que usa F1 en el destino: se validan al arrancar el agente (difieren entre versiones).</summary>
    public static readonly (string Tabla, string Columna)[] Columnas =
    [
        .. Cols("SAFACT", "TipoFac", "NumeroD", "CodSucu", "CodClie", "Descrip", "ID3", "Direc1", "Direc2", "Telef", "OrdenC", "CodUbic",
            "CodVend", "CodOper", "CodEsta", "CodUsua", "FechaE", "FechaI", "FechaV", "FechaT", "Signo", "Factor", "EsCorrel",
            "TotalPrd", "TGravable", "TExento", "Monto", "MtoTax", "MtoTotal", "Credito", "Notas1", "SaldoAct"),
        .. Cols("SAITEMFAC", "TipoFac", "NumeroD", "CodSucu", "NroLinea", "CodItem", "Refere", "Descrip1", "Descrip2", "Descrip3",
            "Cantidad", "CantMayor", "Costo", "Precio", "PriceO", "TotalItem", "MtoTax", "MtoTaxO", "EsExento", "CodUbic", "CodVend",
            "FechaE", "Signo", "ExistAnt"),
        .. Cols("SACLIE", "CodClie", "Descrip", "ID3", "Direc1", "Direc2", "Telef", "DiasCred", "Saldo"),
        .. Cols("SAPROD", "CodProd", "Descrip", "Descrip2", "Descrip3", "Precio3", "CostPro", "EsExento", "Activo", "Compro", "Refere"),
        .. Cols("SAEXIS", "CodProd", "CodUbic", "CantCom", "Existen"),
        .. Cols("SATAXPRD", "CodProd", "CodTaxs", "Monto", "EsPorct"),
        .. Cols("SATAXES", "CodTaxs", "Activo", "EsReten", "EsTaxVenta"),
        .. Cols("SADEPO", "CodUbic"),
        .. Cols("SACORRELSIS", "CodSucu", "FieldName", "ValueInt"),
    ];

    private static readonly ConcurrentDictionary<string, EscalasPedido> CacheEscalas = new();

    public static Task<ClienteDestino?> ClienteAsync(SqlConnection cn, SqlTransaction tx, string codClie) =>
        cn.QuerySingleOrDefaultAsync<ClienteDestino>("""
            SELECT CodClie, ISNULL(Descrip, '') AS Descrip, ID3, Direc1, Direc2, Telef, CAST(ISNULL(DiasCred, 0) AS int) AS DiasCred,
                   ISNULL(Saldo, 0) AS Saldo
            FROM dbo.SACLIE WHERE CodClie = @codClie
            """, new { codClie }, tx);

    /// <summary>
    /// Productos con su tarifa de impuestos de venta (suma de los porcentuales activos, sin retenciones)
    /// y la existencia en el depósito de despacho.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, ProductoDestino>> ProductosAsync(SqlConnection cn, SqlTransaction tx,
        IEnumerable<string> codigos, string codUbic) =>
        (await cn.QueryAsync<ProductoDestino>("""
            SELECT p.CodProd, ISNULL(p.Descrip, '') AS Descrip, p.Descrip2, p.Descrip3, p.Precio3, ISNULL(p.CostPro, 0) AS CostPro,
                   CAST(CASE WHEN p.EsExento = 1 THEN 1 ELSE 0 END AS bit) AS EsExento,
                   ISNULL(t.Tarifa, 0) AS TarifaImpuesto,
                   CAST(CASE WHEN p.Activo = 1 THEN 1 ELSE 0 END AS bit) AS Activo,
                   p.Refere, ISNULL(e.Existen, 0) AS ExistenDepo
            FROM dbo.SAPROD p
            OUTER APPLY (SELECT SUM(x.Monto) AS Tarifa
                         FROM dbo.SATAXPRD x JOIN dbo.SATAXES s ON s.CodTaxs = x.CodTaxs
                         WHERE x.CodProd = p.CodProd AND x.EsPorct = 1 AND s.Activo = 1 AND s.EsReten = 0 AND s.EsTaxVenta = 1) t
            LEFT JOIN dbo.SAEXIS e ON e.CodProd = p.CodProd AND e.CodUbic = @codUbic
            WHERE p.CodProd IN @codigos
            """, new { codigos = codigos.Distinct().ToArray(), codUbic }, tx)).ToDictionary(p => p.CodProd, StringComparer.OrdinalIgnoreCase);

    public static async Task<bool> DepositoExisteAsync(SqlConnection cn, SqlTransaction tx, string codUbic) =>
        await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.SADEPO WHERE CodUbic = @codUbic", new { codUbic }, tx) > 0;

    /// <summary>Decimales de las columnas de SAITEMFAC en esta base (cambian entre versiones de SAINT).</summary>
    public static async Task<EscalasPedido> EscalasAsync(SqlConnection cn, SqlTransaction tx)
    {
        var clave = $"{cn.DataSource}|{cn.Database}";
        if (CacheEscalas.TryGetValue(clave, out var esc)) return esc;

        var escalas = (await cn.QueryAsync<(string Columna, int Escala)>("""
            SELECT name, CAST(scale AS int) FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.SAITEMFAC') AND name IN ('PriceO', 'TotalItem', 'MtoTax', 'MtoTaxO')
            """, transaction: tx)).ToDictionary(x => x.Columna, x => x.Escala, StringComparer.OrdinalIgnoreCase);

        esc = new EscalasPedido(escalas["PriceO"], escalas["TotalItem"], escalas["MtoTax"], escalas["MtoTaxO"]);
        return CacheEscalas.GetOrAdd(clave, esc);
    }

    public static async Task<bool> PedidoExisteAsync(SqlConnection cn, SqlTransaction tx, string codSucu, string numeroD) =>
        await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.SAFACT WHERE TipoFac = 'E' AND NumeroD = @numeroD AND CodSucu = @codSucu",
            new { codSucu, numeroD }, tx) > 0;

    /// <summary>
    /// Inserta el pedido: un INSERT de encabezado (los triggers de SAFACT leen una sola fila; con TipoFac E no actúan),
    /// luego los ítems, y suma el comprometido (SAPROD.Compro y SAEXIS.CantCom del depósito, si la fila existe).
    /// </summary>
    public static async Task InsertarAsync(SqlConnection cn, SqlTransaction tx, string numeroD, PedidoArmado pedido)
    {
        var e = pedido.Encabezado;
        await cn.ExecuteAsync("""
            INSERT dbo.SAFACT (TipoFac, NumeroD, CodSucu, CodClie, Descrip, ID3, Direc1, Direc2, Telef, OrdenC, CodUbic, CodVend, CodOper,
                               CodEsta, CodUsua, FechaE, FechaI, FechaV, FechaT, Signo, Factor, EsCorrel,
                               TotalPrd, TGravable, TExento, Monto, MtoTax, MtoTotal, Credito, Notas1, SaldoAct)
            VALUES ('E', @numeroD, @CodSucu, @CodClie, @Descrip, @ID3, @Direc1, @Direc2, @Telef, @OrdenC, @CodUbic, @CodVend, @CodOper,
                    @CodEsta, @CodUsua, @FechaE, @FechaE, @FechaV, GETDATE(), 1, 1, 1,
                    @Monto, @TGravable, @TExento, @Monto, @MtoTax, @MtoTotal, @MtoTotal, @Notas1, @SaldoAct);
            """, new
        {
            numeroD, e.CodSucu, e.CodClie, e.Descrip, e.ID3, e.Direc1, e.Direc2, e.Telef, e.OrdenC, e.CodUbic, e.CodVend, e.CodOper,
            e.CodEsta, e.CodUsua, e.FechaE, e.FechaV, e.Monto, e.TGravable, e.TExento, e.MtoTax, e.MtoTotal, e.Notas1, e.SaldoAct,
        }, tx);

        var items = pedido.Items.Select(i => new
        {
            numeroD, e.CodSucu, i.NroLinea, i.CodItem, i.Refere, i.Descrip1, i.Descrip2, i.Descrip3, i.Cantidad, i.Costo, i.Precio, i.PriceO,
            i.TotalItem, i.MtoTax, i.MtoTaxO, EsExento = i.EsExento ? 1 : 0, e.CodUbic, e.CodVend, e.FechaE, i.ExistAnt,
        }).ToList();

        await cn.ExecuteAsync("""
            INSERT dbo.SAITEMFAC (TipoFac, NumeroD, CodSucu, NroLinea, CodItem, Refere, Descrip1, Descrip2, Descrip3, Cantidad, CantMayor,
                                  Costo, Precio, PriceO, TotalItem, MtoTax, MtoTaxO, EsExento, CodUbic, CodVend, FechaE, Signo, ExistAnt)
            VALUES ('E', @numeroD, @CodSucu, @NroLinea, @CodItem, @Refere, @Descrip1, @Descrip2, @Descrip3, @Cantidad, 1,
                    @Costo, @Precio, @PriceO, @TotalItem, @MtoTax, @MtoTaxO, @EsExento, @CodUbic, @CodVend, @FechaE, 1, @ExistAnt);
            """, items, tx);

        await cn.ExecuteAsync("""
            UPDATE dbo.SAPROD SET Compro = Compro + @Cantidad WHERE CodProd = @CodItem;
            UPDATE dbo.SAEXIS SET CantCom = CantCom + @Cantidad WHERE CodProd = @CodItem AND CodUbic = @CodUbic;
            """, items, tx);
    }

    private static IEnumerable<(string, string)> Cols(string tabla, params string[] columnas) => columnas.Select(c => (tabla, c));
}
