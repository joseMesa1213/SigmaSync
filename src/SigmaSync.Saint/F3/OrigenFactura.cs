using Dapper;
using Microsoft.Data.SqlClient;
using SigmaSync.Contracts;

namespace SigmaSync.Saint.F3;

/// <summary>Factura a un PV candidata a enviarse, con el PV destino resuelto por su cliente (sync.NodoTercero).</summary>
public sealed record FacturaCandidata(string NumeroD, string CodSucu, string CodClie, DateTime FechaT, int NroUnico, string NodoDestino);

/// <summary>F2/F3 en Fábrica o CEDI: lectura de las facturas (SAFACT / SAITEMFAC / SATAXVTA, TipoFac A) dirigidas a un PV.</summary>
public static class OrigenFactura
{
    public static readonly (string Tabla, string Columna)[] Columnas =
    [
        .. new[] { "TipoFac", "NumeroD", "CodSucu", "CodClie", "FechaE", "FechaT", "NroUnico", "Monto", "Descto1", "Descto2", "Fletes",
            "TGravable", "TExento", "MtoTax", "MtoTotal" }.Select(c => ("SAFACT", c)),
        .. new[] { "TipoFac", "NumeroD", "CodSucu", "NroLinea", "CodItem", "Cantidad", "Precio", "PriceO", "TotalItem", "EsExento",
            "OTipo", "ONumero" }.Select(c => ("SAITEMFAC", c)),
        .. new[] { "TipoFac", "NumeroD", "CodSucu", "CodTaxs", "Monto", "MtoTax", "TGravable", "EsReten" }.Select(c => ("SATAXVTA", c)),
    ];

    private const string SelectFactura = """
        SELECT f.NumeroD, f.CodSucu, f.CodClie, f.FechaT, f.NroUnico, nt.nodo_id AS NodoDestino
        FROM dbo.SAFACT f
        JOIN sync.NodoTercero nt ON nt.tipo_tercero = 'CLIENTE' AND nt.cod_tercero = f.CodClie AND nt.activo = 1 AND nt.rol = 'PV'
        """;

    /// <summary>Facturas nuevas a un PV: dentro de la ventana, grabadas hace más de EsperaSegundos y sin mensaje en Outbox.</summary>
    public static async Task<IReadOnlyList<FacturaCandidata>> CandidatasAsync(SqlConnection cn, ConfigNodo cfg, DateTime ahora,
        SqlTransaction? tx = null) =>
        (await cn.QueryAsync<FacturaCandidata>(SelectFactura + """

            WHERE f.TipoFac = 'A' AND f.CodSucu = @CodSucu
              AND f.FechaT >= @desde AND f.FechaT <= @hasta
              AND NOT EXISTS (SELECT 1 FROM sync.Outbox o
                              WHERE o.tipo = 'FACTURA_OC' AND o.doc_tipo = 'A' AND o.doc_numero = f.NumeroD AND o.nodo_destino = nt.nodo_id)
            ORDER BY f.NroUnico
            """, new
        {
            cfg.CodSucu,
            desde = cfg.CapturaDesde > ahora.AddDays(-cfg.VentanaDias) ? cfg.CapturaDesde : ahora.AddDays(-cfg.VentanaDias),
            hasta = ahora.AddSeconds(-cfg.EsperaSegundos),
        }, tx)).AsList();

    /// <summary>Una factura puntual por número, sin filtros de ventana (comando "enviar-factura").</summary>
    public static Task<FacturaCandidata?> BuscarAsync(SqlConnection cn, ConfigNodo cfg, string numeroD) =>
        cn.QuerySingleOrDefaultAsync<FacturaCandidata>(SelectFactura + """

            WHERE f.TipoFac = 'A' AND f.CodSucu = @CodSucu AND f.NumeroD = @numeroD
            """, new { cfg.CodSucu, numeroD });

    private sealed record FilaEncabezado(string NumeroD, string CodClie, DateTime FechaE, decimal Monto, decimal Descto1, decimal Descto2,
        decimal Fletes, decimal TGravable, decimal TExento, decimal MtoTax, decimal MtoTotal);

    /// <summary>
    /// Payload FACTURA_OC. Cada ítem lleva el pedido de origen (OTipo E) y, si ese pedido lo creó el agente (F1),
    /// la OC del PV que lo originó (oc_origen, desde sync.DocRef de este nodo).
    /// </summary>
    public static async Task<FacturaOc> LeerPayloadAsync(SqlConnection cn, SqlTransaction? tx, FacturaCandidata f, string nodoId)
    {
        var e = await cn.QuerySingleAsync<FilaEncabezado>("""
            SELECT NumeroD, CodClie, FechaE, ISNULL(Monto, 0) AS Monto, ISNULL(Descto1, 0) AS Descto1, ISNULL(Descto2, 0) AS Descto2,
                   ISNULL(Fletes, 0) AS Fletes, ISNULL(TGravable, 0) AS TGravable, ISNULL(TExento, 0) AS TExento,
                   ISNULL(MtoTax, 0) AS MtoTax, ISNULL(MtoTotal, 0) AS MtoTotal
            FROM dbo.SAFACT WHERE TipoFac = 'A' AND NumeroD = @NumeroD AND CodSucu = @CodSucu
            """, f, tx);

        var items = (await cn.QueryAsync<FacturaItem>("""
            SELECT CAST(i.NroLinea AS int) AS Linea, i.CodItem, i.Cantidad, i.Precio, ISNULL(i.PriceO, i.Precio) AS PrecioO, i.TotalItem,
                   CAST(CASE WHEN i.EsExento = 1 THEN 1 ELSE 0 END AS bit) AS EsExento,
                   CASE WHEN i.OTipo = 'E' THEN i.ONumero END AS PedidoOrigen,
                   d.num_origen AS OcOrigen
            FROM dbo.SAITEMFAC i
            OUTER APPLY (SELECT TOP 1 r.num_origen FROM sync.DocRef r
                         WHERE i.OTipo = 'E' AND r.relacion = 'OC_PEDIDO' AND r.activo = 1
                           AND r.nodo_destino = @nodoId AND r.tipo_destino = 'E' AND r.num_destino = i.ONumero
                         ORDER BY r.id DESC) d
            WHERE i.TipoFac = 'A' AND i.NumeroD = @NumeroD AND i.CodSucu = @CodSucu
            ORDER BY i.NroLinea
            """, new { f.NumeroD, f.CodSucu, nodoId }, tx)).AsList();

        var impuestos = (await cn.QueryAsync<FacturaImpuesto>("""
            SELECT CodTaxs, MtoTax AS Tarifa, TGravable AS Base, Monto
            FROM dbo.SATAXVTA
            WHERE TipoFac = 'A' AND NumeroD = @NumeroD AND CodSucu = @CodSucu AND ISNULL(EsReten, 0) = 0
            """, f, tx)).AsList();

        return new FacturaOc(
            new FacturaEncabezado(e.NumeroD, "A", e.CodClie, new DateTimeOffset(e.FechaE, TimeZoneInfo.Local.GetUtcOffset(e.FechaE)),
                e.Monto, e.Descto1, e.Descto2, e.Fletes, e.TGravable, e.TExento, e.MtoTax, e.MtoTotal),
            items, impuestos);
    }
}
