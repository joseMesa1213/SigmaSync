using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SigmaSync.Agent.Manejadores;
using SigmaSync.Contracts;
using SigmaSync.Saint;
using SigmaSync.Saint.F3;

namespace SigmaSync.Integration.Tests;

/// <summary>
/// F3 (y la detección de F2) contra las copias SAINT reales. Cada prueba corre en una transacción que se deshace.
/// </summary>
[Collection(ColeccionSaint.Nombre)]
public sealed class F3Tests(CopiasSaint s)
{
    private static readonly ManejadorFacturaOc Manejador = new(NullLogger<ManejadorFacturaOc>.Instance);

    private static async Task<FacturaOc> LeerFacturaAsync(string cs, string nodo, string numero, string? codClie = null)
    {
        await using var cn = new SqlConnection(cs);
        await cn.OpenAsync();
        var cfg = ConfigNodo.Desde(await RepositorioSync.LeerConfigAsync(cn));
        var f = await OrigenFactura.BuscarAsync(cn, cfg, numero)
                ?? new FacturaCandidata(numero, "00000", codClie ?? "", DateTime.Now, 0, "PV_MAY");   // factura de un cliente que no es PV
        return await OrigenFactura.LeerPayloadAsync(cn, null, f, nodo);
    }

    private static Sobre SobreFactura(FacturaOc f, string origen) =>
        Sobre.Crear(TiposMensaje.FacturaOc, origen, "PV_MAY", Sobre.Clave(origen, "A", f.Factura.Numero), f);

    [Fact]
    public async Task El_PV_crea_la_OC_desde_la_factura_33570_igual_que_una_OC_de_SAINT()
    {
        var factura = await LeerFacturaAsync(s.Fab, "FAB", "*33570");
        Assert.Equal(2, factura.Items.Count);
        Assert.Equal(new FacturaImpuesto("IVA", 19, 1236118.72m, 234862.557m), factura.Impuestos.Single());

        await using var cn = new SqlConnection(s.Pv);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();
        var pedidoAntes = await cn.ExecuteScalarAsync<decimal>("SELECT Pedido FROM SAPROD WHERE CodProd = '1I21033062400P4'", transaction: tx);

        var ctx = new ContextoMensaje(SobreFactura(factura, "FAB"), cn, tx, "PV_MAY");
        await Manejador.ProcesarAsync(ctx, default);

        var e = await cn.QuerySingleAsync("""
            SELECT NumeroD, CodProv, Descrip, CodUbic, CodUsua, OrdenC, Notas1, FechaE, FechaV, Monto, TGravable, TExento, MtoTax, MtoTotal, Credito
            FROM SACOMP WHERE TipoCom = 'L' AND OrdenC = '*33570'
            """, transaction: tx);
        string numero = e.NumeroD;
        Assert.Equal("000003560", numero);   // PrxOrdenC del PV
        Assert.Equal(3561, await cn.ExecuteScalarAsync<int>("SELECT ValueInt FROM SACORRELSIS WHERE CodSucu = '00000' AND FieldName = 'PrxOrdenC'", transaction: tx));
        Assert.Equal(("890919355-4", "SIGMAPLAS S.A.", "115", "SYNC"), ((string)e.CodProv, (string)e.Descrip, (string)e.CodUbic, (string)e.CodUsua));
        Assert.Equal("SYNC FAB FAC *33570", (string)e.Notas1);
        Assert.Equal(new DateTime(2026, 10, 19), (DateTime)e.FechaE);
        Assert.Equal(new DateTime(2026, 10, 19), (DateTime)e.FechaV);
        // Idéntica a la compra 33570 que el PV digitó a mano: 1.236.118,72, y sin impuesto (SAINT lo calcula al cargarla).
        Assert.Equal(1236118.72m, (decimal)e.Monto);
        Assert.Equal(1236118.72m, (decimal)e.MtoTotal);
        Assert.Equal(0m, (decimal)e.MtoTax);

        var costos = (await cn.QueryAsync<(string CodItem, decimal Costo, decimal TotalItem)>(
            "SELECT CodItem, Costo, TotalItem FROM SAITEMCOM WHERE TipoCom = 'L' AND NumeroD = @numero ORDER BY NroLinea", new { numero }, tx)).ToList();
        Assert.Equal([("1I21033062400P4", 18161.7675m, 217941.21m), ("121080180080", 7272.6965m, 1018177.51m)], costos);

        // Mismas columnas llenas que la OC 000003538 grabada en SAINT (más OrdenC y Notas1, que SAINT no usa en la OC).
        var patron = await CopiasSaint.ColumnasConValorAsync(cn, tx, "SACOMP", "TipoCom = 'L' AND NumeroD = '000003538'", new { });
        var generado = await CopiasSaint.ColumnasConValorAsync(cn, tx, "SACOMP", "TipoCom = 'L' AND NumeroD = @numero", new { numero });
        generado.Remove("OrdenC");
        generado.Remove("Notas1");
        Assert.Equal(patron, generado);
        var patronItem = await CopiasSaint.ColumnasConValorAsync(cn, tx, "SAITEMCOM", "TipoCom = 'L' AND NumeroD = '000003538' AND NroLinea = 3", new { });
        var generadoItem = await CopiasSaint.ColumnasConValorAsync(cn, tx, "SAITEMCOM", "TipoCom = 'L' AND NumeroD = @numero AND NroLinea = 1", new { numero });
        Assert.Equal(patronItem, generadoItem);   // mismo producto (1I21033062400P4) en ambas

        // Con ActualizarPedidoPV = 0 no se toca el comprometido; sí la trazabilidad y la respuesta.
        Assert.Equal(pedidoAntes, await cn.ExecuteScalarAsync<decimal>("SELECT Pedido FROM SAPROD WHERE CodProd = '1I21033062400P4'", transaction: tx));
        Assert.Equal(1, await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sync.DocRef WHERE relacion = 'FACTURA_OC' AND nodo_origen = 'FAB' AND num_origen = '*33570' AND num_destino = @numero",
            new { numero }, tx));
        var ack = Sobre.FromJson(await cn.ExecuteScalarAsync<string>("SELECT sobre FROM sync.Outbox WHERE msg_id = @id",
            new { id = ctx.Respuestas.Single() }, tx) ?? "").Leer<AckFactura>();
        Assert.Equal(EstadosAck.Aplicada, ack.Estado);
        Assert.Equal(new AccionAck(TiposAccion.OcCreada, numero), ack.Acciones.Single());
        Assert.Empty(ack.Alertas);
    }

    [Fact]
    public async Task Factura_del_CEDI_usa_su_proveedor_lleva_exentos_y_alerta_el_maestro_distinto()
    {
        var factura = await LeerFacturaAsync(s.Cedi, "CEDI", "009950");
        Assert.Equal(52, factura.Items.Count);

        await using var cn = new SqlConnection(s.Pv);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();
        var ctx = new ContextoMensaje(SobreFactura(factura, "CEDI"), cn, tx, "PV_MAY");
        await Manejador.ProcesarAsync(ctx, default);

        var e = await cn.QuerySingleAsync("SELECT CodProv, Descrip, Monto, TGravable, TExento FROM SACOMP WHERE TipoCom = 'L' AND OrdenC = '009950'", transaction: tx);
        Assert.Equal("8909193545", (string)e.CodProv);   // no SIGMAPLAS: así se distinguen las compras del CEDI
        Assert.Equal(159648m, (decimal)e.TExento);
        Assert.Equal(9451196.37m, (decimal)e.TGravable);
        Assert.Equal(9610844.37m, (decimal)e.Monto);       // = Monto de la factura (el CEDI no da descuento)
        Assert.Equal(1, await cn.ExecuteScalarAsync<int>(
            "SELECT EsExento FROM SAITEMCOM WHERE TipoCom = 'L' AND NumeroD = (SELECT NumeroD FROM SACOMP WHERE TipoCom = 'L' AND OrdenC = '009950') AND CodItem = '4180'",
            transaction: tx));
        var alerta = Assert.Single(ctx.Alertas);
        Assert.Equal((CodigosError.ExentoDiferente, "4180"), (alerta.Codigo, alerta.CodItem));
    }

    [Fact]
    public async Task Factura_desde_pedido_sincronizado_se_retiene_como_F2()
    {
        var factura = await LeerFacturaAsync(s.Fab, "FAB", "*31819", "9012085494");   // ítems con OTipo E, pedido 035828
        Assert.All(factura.Items, i => Assert.Equal("035828", i.PedidoOrigen));

        await using var cn = new SqlConnection(s.Pv);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();

        // Sin relación OC ↔ pedido en el PV, el pedido fue manual en Fábrica: sigue por F3 (aquí falla por productos del maestro).
        var f3 = await Assert.ThrowsAsync<ErrorNegocioException>(() =>
            Manejador.ProcesarAsync(new ContextoMensaje(SobreFactura(factura, "FAB"), cn, tx, "PV_MAY"), default));
        Assert.All(f3.Errores, e => Assert.Equal(CodigosError.ProductoNoExiste, e.Codigo));

        // Con la relación registrada por F1 (ACK_PEDIDO), es F2: se retiene antes de tocar nada.
        await RepositorioSync.InsertarDocRefAsync(cn, tx, "OC_PEDIDO", "PV_MAY", 'L', "000003538", "FAB", 'E', "035828", null);
        var f2 = await Assert.ThrowsAsync<ErrorNegocioException>(() =>
            Manejador.ProcesarAsync(new ContextoMensaje(SobreFactura(factura, "FAB"), cn, tx, "PV_MAY"), default));
        Assert.Equal(CodigosError.F2NoDisponible, f2.Errores.Single().Codigo);
        Assert.Contains("OC 000003538", f2.Errores.Single().Mensaje);
    }

    [Fact]
    public async Task Fabrica_captura_facturas_al_PV_y_anota_la_OC_de_origen_de_los_pedidos_sincronizados()
    {
        await using var cn = new SqlConnection(s.Fab);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();
        var cfg = ConfigNodo.Desde(await RepositorioSync.LeerConfigAsync(cn, tx)) with
        {
            CapturaDesde = new DateTime(2026, 10, 19), VentanaDias = 3650, EsperaSegundos = 0,
        };

        var candidatas = await OrigenFactura.CandidatasAsync(cn, cfg, new DateTime(2026, 10, 20), tx);
        Assert.Contains(candidatas, c => c is { NumeroD: "*33570", NodoDestino: "PV_MAY", CodClie: "2853060" });
        Assert.All(candidatas, c => Assert.Equal("2853060", c.CodClie));

        // Un pedido creado por F1 (DocRef OC_PEDIDO en Fábrica) hace que la factura lleve oc_origen.
        await RepositorioSync.InsertarDocRefAsync(cn, tx, "OC_PEDIDO", "PV_MAY", 'L', "000003999", "FAB", 'E', "035828", null);
        var payload = await OrigenFactura.LeerPayloadAsync(cn, tx, new FacturaCandidata("*31819", "00000", "9012085494", DateTime.Now, 0, "PV_MAY"), "FAB");
        Assert.All(payload.Items, i => Assert.Equal(("035828", "000003999"), (i.PedidoOrigen, i.OcOrigen)));
    }

    [Fact]
    public async Task Fabrica_registra_la_OC_creada_al_recibir_el_ACK()
    {
        var sobre = Sobre.Crear(TiposMensaje.AckFactura, "PV_MAY", "FAB", "FAB:A:*33570",
            new AckFactura("*33570", EstadosAck.Aplicada, [new AccionAck(TiposAccion.OcCreada, "000003560")],
                [new ErrorDoc(CodigosError.ExentoDiferente, "prueba", "X")], []));

        await using var cn = new SqlConnection(s.Fab);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();
        var ctx = new ContextoMensaje(sobre, cn, tx, "FAB");
        await new ManejadorAckFactura(NullLogger<ManejadorAckFactura>.Instance).ProcesarAsync(ctx, default);

        Assert.Equal(1, await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sync.DocRef WHERE relacion = 'FACTURA_OC' AND nodo_origen = 'FAB' AND num_origen = '*33570' AND nodo_destino = 'PV_MAY' AND num_destino = '000003560'",
            transaction: tx));
        Assert.Empty(ctx.Alertas);   // la alerta ya la reportó el PV; aquí queda como INFO
        Assert.Equal(1, await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sync.Log WHERE msg_id = @MsgId AND nivel = 'INFO' AND mensaje LIKE '%EXENTO_DIFERENTE%'", sobre, tx));
    }
}
