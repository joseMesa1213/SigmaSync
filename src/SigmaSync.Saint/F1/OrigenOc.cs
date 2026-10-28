using Dapper;
using Microsoft.Data.SqlClient;
using SigmaSync.Contracts;

namespace SigmaSync.Saint.F1;

/// <summary>OC del PV candidata a enviarse, con el nodo destino resuelto por su proveedor (sync.NodoTercero).</summary>
public sealed record OcCandidata(string NumeroD, string CodSucu, string CodProv, DateTime FechaE, DateTime FechaT, int NroUnico, string NodoDestino);

/// <summary>F1 en el PV: lectura de las OC (SACOMP / SAITEMCOM, TipoCom L) dirigidas a Fábrica o CEDI.</summary>
public static class OrigenOc
{
    public static readonly (string Tabla, string Columna)[] Columnas =
    [
        .. new[] { "TipoCom", "NumeroD", "CodSucu", "CodProv", "FechaE", "FechaT", "NroUnico", "CodUsua" }.Select(c => ("SACOMP", c)),
        .. new[] { "TipoCom", "NumeroD", "CodSucu", "CodProv", "NroLinea", "CodItem", "Cantidad", "Costo" }.Select(c => ("SAITEMCOM", c)),
    ];

    private const string SelectOc = """
        SELECT c.NumeroD, c.CodSucu, c.CodProv, c.FechaE, c.FechaT, c.NroUnico, nt.nodo_id AS NodoDestino
        FROM dbo.SACOMP c
        JOIN sync.NodoTercero nt ON nt.tipo_tercero = 'PROVEEDOR' AND nt.cod_tercero = c.CodProv AND nt.activo = 1 AND nt.rol IN ('FAB', 'CEDI')
        """;

    /// <summary>
    /// OC nuevas por enviar: dentro de la ventana de recaptura, grabadas hace más de EsperaSegundos (para no tomar una OC
    /// a medio grabar) y todavía sin mensaje en Outbox. Se excluyen las OC que crea el propio agente (F2/F3): si no,
    /// volverían a Fábrica como pedidos.
    /// </summary>
    public static async Task<IReadOnlyList<OcCandidata>> CandidatasAsync(SqlConnection cn, ConfigNodo cfg, DateTime ahora,
        SqlTransaction? tx = null) =>
        (await cn.QueryAsync<OcCandidata>(SelectOc + """

            WHERE c.TipoCom = 'L' AND c.CodSucu = @CodSucu
              AND c.FechaT >= @desde AND c.FechaT <= @hasta
              AND ISNULL(c.CodUsua, '') <> @CodUsua
              AND NOT EXISTS (SELECT 1 FROM sync.Outbox o
                              WHERE o.tipo = 'OC_PEDIDO' AND o.doc_tipo = 'L' AND o.doc_numero = c.NumeroD AND o.nodo_destino = nt.nodo_id)
              AND NOT EXISTS (SELECT 1 FROM sync.DocRef d
                              WHERE d.nodo_destino = @NodoId AND d.tipo_destino = 'L' AND d.num_destino = c.NumeroD)
            ORDER BY c.NroUnico
            """, new
        {
            cfg.CodSucu,
            cfg.CodUsua,
            cfg.NodoId,
            desde = Max(cfg.CapturaDesde, ahora.AddDays(-cfg.VentanaDias)),
            hasta = ahora.AddSeconds(-cfg.EsperaSegundos),
        }, tx)).AsList();

    /// <summary>Una OC puntual por número, sin filtros de ventana (comando "enviar-oc").</summary>
    public static Task<OcCandidata?> BuscarAsync(SqlConnection cn, ConfigNodo cfg, string numeroD) =>
        cn.QuerySingleOrDefaultAsync<OcCandidata>(SelectOc + """

            WHERE c.TipoCom = 'L' AND c.CodSucu = @CodSucu AND c.NumeroD = @numeroD
            """, new { cfg.CodSucu, numeroD });

    public static async Task<OcPedido> LeerPayloadAsync(SqlConnection cn, SqlTransaction? tx, OcCandidata oc)
    {
        var items = (await cn.QueryAsync<OcItem>("""
            SELECT CAST(NroLinea AS int) AS Linea, CodItem, Cantidad, Costo AS CostoOc
            FROM dbo.SAITEMCOM
            WHERE TipoCom = 'L' AND NumeroD = @NumeroD AND CodProv = @CodProv AND CodSucu = @CodSucu
            ORDER BY NroLinea
            """, oc, tx)).AsList();

        return new OcPedido(
            new OcEncabezado(oc.NumeroD, oc.CodSucu, oc.CodProv, DateOnly.FromDateTime(oc.FechaE),
                new DateTimeOffset(oc.FechaT, TimeZoneInfo.Local.GetUtcOffset(oc.FechaT)), oc.NroUnico),
            items);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
