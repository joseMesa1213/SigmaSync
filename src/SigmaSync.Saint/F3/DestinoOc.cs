using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.SqlClient;
using SigmaSync.Core.F3;

namespace SigmaSync.Saint.F3;

/// <summary>F2/F3 en el PV: lectura de maestros y escritura de la OC L (SACOMP / SAITEMCOM).</summary>
public static class DestinoOc
{
    public static readonly (string Tabla, string Columna)[] Columnas =
    [
        .. Cols("SACOMP", "TipoCom", "NumeroD", "CodSucu", "CodProv", "Descrip", "ID3", "CodUbic", "CodEsta", "CodUsua", "FechaE", "FechaI",
            "FechaV", "FechaT", "Signo", "Factor", "TotalPrd", "Monto", "TGravable", "TExento", "MtoTax", "MtoTotal", "Credito", "OrdenC", "Notas1"),
        .. Cols("SAITEMCOM", "TipoCom", "NumeroD", "CodProv", "CodSucu", "NroLinea", "CodItem", "Refere", "Descrip1", "Descrip2", "Descrip3",
            "Cantidad", "Costo", "TotalItem", "Precio1", "Precio2", "Precio3", "ExistAnt", "EsExento", "CodUbic", "FechaE", "FechaL", "Signo"),
        .. Cols("SAPROD", "CodProd", "Descrip", "Descrip2", "Descrip3", "Refere", "Precio1", "Precio2", "Precio3", "EsExento", "Activo", "Pedido"),
        .. Cols("SAEXIS", "CodProd", "CodUbic", "Existen", "CantPed"),
        .. Cols("SAPROV", "CodProv", "Descrip", "ID3"),
        .. Cols("SADEPO", "CodUbic"),
        .. Cols("SACORRELSIS", "CodSucu", "FieldName", "ValueInt"),
    ];

    private static readonly ConcurrentDictionary<string, EscalasOc> CacheEscalas = new();

    public static Task<ProveedorPv?> ProveedorAsync(SqlConnection cn, SqlTransaction tx, string codProv) =>
        cn.QuerySingleOrDefaultAsync<ProveedorPv>(
            "SELECT CodProv, ISNULL(Descrip, '') AS Descrip, ID3 FROM dbo.SAPROV WHERE CodProv = @codProv", new { codProv }, tx);

    public static async Task<IReadOnlyDictionary<string, ProductoPv>> ProductosAsync(SqlConnection cn, SqlTransaction tx,
        IEnumerable<string> codigos, string codUbic) =>
        (await cn.QueryAsync<ProductoPv>("""
            SELECT p.CodProd, ISNULL(p.Descrip, '') AS Descrip, p.Descrip2, p.Descrip3, p.Refere,
                   ISNULL(p.Precio1, 0) AS Precio1, ISNULL(p.Precio2, 0) AS Precio2, ISNULL(p.Precio3, 0) AS Precio3,
                   CAST(CASE WHEN p.EsExento = 1 THEN 1 ELSE 0 END AS bit) AS EsExento,
                   CAST(CASE WHEN p.Activo = 1 THEN 1 ELSE 0 END AS bit) AS Activo,
                   ISNULL(e.Existen, 0) AS Existen
            FROM dbo.SAPROD p
            LEFT JOIN dbo.SAEXIS e ON e.CodProd = p.CodProd AND e.CodUbic = @codUbic
            WHERE p.CodProd IN @codigos
            """, new { codigos = codigos.Distinct().ToArray(), codUbic }, tx)).ToDictionary(p => p.CodProd, StringComparer.OrdinalIgnoreCase);

    public static async Task<EscalasOc> EscalasAsync(SqlConnection cn, SqlTransaction tx)
    {
        var clave = $"{cn.DataSource}|{cn.Database}";
        if (CacheEscalas.TryGetValue(clave, out var esc)) return esc;

        var escalas = (await cn.QueryAsync<(string Columna, int Escala)>("""
            SELECT name, CAST(scale AS int) FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.SAITEMCOM') AND name IN ('Costo', 'TotalItem')
            """, transaction: tx)).ToDictionary(x => x.Columna, x => x.Escala, StringComparer.OrdinalIgnoreCase);

        return CacheEscalas.GetOrAdd(clave, new EscalasOc(escalas["Costo"], escalas["TotalItem"]));
    }

    /// <summary>Número de OC ya usado (con cualquier proveedor): el consecutivo PrxOrdenC es único por sucursal.</summary>
    public static async Task<bool> OcExisteAsync(SqlConnection cn, SqlTransaction tx, string codSucu, string numeroD) =>
        await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.SACOMP WHERE TipoCom = 'L' AND NumeroD = @numeroD AND CodSucu = @codSucu",
            new { codSucu, numeroD }, tx) > 0;

    /// <summary>
    /// Inserta la OC: un INSERT de encabezado (el trigger de SACOMP no actúa con TipoCom L) y luego los ítems.
    /// Con <paramref name="actualizarPedido"/> suma también SAPROD.Pedido y SAEXIS.CantPed (pendiente de confirmar
    /// en pantalla qué hace SAINT; por eso va detrás de la bandera ActualizarPedidoPV).
    /// </summary>
    public static async Task InsertarAsync(SqlConnection cn, SqlTransaction tx, string numeroD, OcArmada oc, bool actualizarPedido)
    {
        var e = oc.Encabezado;
        await cn.ExecuteAsync("""
            INSERT dbo.SACOMP (TipoCom, NumeroD, CodSucu, CodProv, Descrip, ID3, CodUbic, CodEsta, CodUsua, FechaE, FechaI, FechaV, FechaT,
                               Signo, Factor, TotalPrd, Monto, TGravable, TExento, MtoTax, MtoTotal, Credito, OrdenC, Notas1)
            VALUES ('L', @numeroD, @CodSucu, @CodProv, @Descrip, @ID3, @CodUbic, @CodEsta, @CodUsua, @FechaE, @FechaE, @FechaE, GETDATE(),
                    1, 1, @Monto, @Monto, @TGravable, @TExento, 0, @Monto, @Monto, @OrdenC, @Notas1);
            """, new
        {
            numeroD, e.CodSucu, e.CodProv, e.Descrip, e.ID3, e.CodUbic, e.CodEsta, e.CodUsua, e.FechaE, e.Monto, e.TGravable, e.TExento,
            e.OrdenC, e.Notas1,
        }, tx);

        var items = oc.Items.Select(i => new
        {
            numeroD, e.CodProv, e.CodSucu, i.NroLinea, i.CodItem, i.Refere, i.Descrip1, i.Descrip2, i.Descrip3, i.Cantidad, i.Costo,
            i.TotalItem, i.Precio1, i.Precio2, i.Precio3, i.ExistAnt, EsExento = i.EsExento ? 1 : 0, e.CodUbic, e.FechaE,
        }).ToList();

        await cn.ExecuteAsync("""
            INSERT dbo.SAITEMCOM (TipoCom, NumeroD, CodProv, CodSucu, NroLinea, CodItem, Refere, Descrip1, Descrip2, Descrip3, Cantidad, Costo,
                                  TotalItem, Precio1, Precio2, Precio3, ExistAnt, EsExento, CodUbic, FechaE, FechaL, Signo)
            VALUES ('L', @numeroD, @CodProv, @CodSucu, @NroLinea, @CodItem, @Refere, @Descrip1, @Descrip2, @Descrip3, @Cantidad, @Costo,
                    @TotalItem, @Precio1, @Precio2, @Precio3, @ExistAnt, @EsExento, @CodUbic, @FechaE, @FechaE, 1);
            """, items, tx);

        if (actualizarPedido)
            await cn.ExecuteAsync("""
                UPDATE dbo.SAPROD SET Pedido = Pedido + @Cantidad WHERE CodProd = @CodItem;
                UPDATE dbo.SAEXIS SET CantPed = CantPed + @Cantidad WHERE CodProd = @CodItem AND CodUbic = @CodUbic;
                """, items, tx);
    }

    private static IEnumerable<(string, string)> Cols(string tabla, params string[] columnas) => columnas.Select(c => (tabla, c));
}
