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
        await FijarActualizarPedidoAsync(cn, tx, "1");
        // 121080180080 queda sin fila en SAEXIS del depósito 115 (le pasa a 4.089 productos del PV): como SAINT, no se crea.
        await cn.ExecuteAsync("DELETE FROM SAEXIS WHERE CodProd = '121080180080' AND CodUbic = '115'", transaction: tx);
        var antes = await ComprometidoAsync(cn, tx);

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

        // Comprometido como SAINT al grabar la OC (prueba P2): SAPROD.Pedido y SAEXIS.CantPed suben la cantidad.
        var cant = factura.Items.ToDictionary(i => i.CodItem, i => i.Cantidad);
        var despues = await ComprometidoAsync(cn, tx);
        Assert.Equal(antes["1I21033062400P4"].Pedido + cant["1I21033062400P4"], despues["1I21033062400P4"].Pedido);
        Assert.Equal(antes["1I21033062400P4"].CantPed + cant["1I21033062400P4"], despues["1I21033062400P4"].CantPed);
        Assert.Equal(antes["121080180080"].Pedido + cant["121080180080"], despues["121080180080"].Pedido);
        Assert.Null(antes["121080180080"].CantPed);
        Assert.Null(despues["121080180080"].CantPed);   // sin fila: solo sube SAPROD.Pedido (OC 3567 de SAINT)

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
    public async Task Con_ActualizarPedidoPV_en_0_la_OC_no_toca_el_comprometido()
    {
        var factura = await LeerFacturaAsync(s.Fab, "FAB", "*33570");

        await using var cn = new SqlConnection(s.Pv);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();
        await FijarActualizarPedidoAsync(cn, tx, "0");
        var antes = await ComprometidoAsync(cn, tx);

        await Manejador.ProcesarAsync(new ContextoMensaje(SobreFactura(factura, "FAB"), cn, tx, "PV_MAY"), default);

        Assert.Equal(antes, await ComprometidoAsync(cn, tx));
        Assert.Equal(1, await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM SACOMP WHERE TipoCom = 'L' AND OrdenC = '*33570'", transaction: tx));
    }

    private static Task FijarActualizarPedidoAsync(SqlConnection cn, SqlTransaction tx, string valor) =>
        cn.ExecuteAsync("""
            UPDATE sync.Config SET valor = @valor WHERE clave = 'ActualizarPedidoPV';
            IF @@ROWCOUNT = 0 INSERT sync.Config (clave, valor) VALUES ('ActualizarPedidoPV', @valor);
            """, new { valor }, tx);

    /// <summary>SAPROD.Pedido y SAEXIS.CantPed (depósito 115; null si no hay fila) de los dos productos de la factura *33570.</summary>
    private static async Task<Dictionary<string, (decimal Pedido, decimal? CantPed)>> ComprometidoAsync(SqlConnection cn, SqlTransaction tx) =>
        (await cn.QueryAsync<(string CodProd, decimal Pedido, decimal? CantPed)>("""
            SELECT p.CodProd, p.Pedido, e.CantPed FROM SAPROD p
            LEFT JOIN SAEXIS e ON e.CodProd = p.CodProd AND e.CodUbic = '115'
            WHERE p.CodProd IN ('1I21033062400P4', '121080180080')
            """, transaction: tx)).ToDictionary(r => r.CodProd, r => (r.Pedido, r.CantPed));

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

    /// <summary>
    /// Factura de Fábrica hecha desde el pedido 035850, que F1 creó a partir de la OC 000003538 del PV (A 250, B 90, C 180, D 200).
    /// Los ítems llevan pedido_origen y la relación OC_PEDIDO está en el DocRef del PV, como queda después del ACK_PEDIDO.
    /// </summary>
    private static FacturaOc FacturaDePedido(string numero, params (string CodItem, decimal Cantidad, decimal Precio)[] items)
    {
        var lineas = items.Select((x, k) => new FacturaItem(k + 1, x.CodItem, x.Cantidad, x.Precio, x.Precio, x.Cantidad * x.Precio, false, "035850", null))
            .ToList();
        var monto = lineas.Sum(l => l.TotalItem);
        var iva = Math.Round(monto * 0.19m, 2);
        return new FacturaOc(
            new FacturaEncabezado(numero, "A", "2853060", DateTimeOffset.Parse("2026-11-06T10:00:00-05:00"), monto, 0, 0, 0, monto, 0, iva, monto + iva),
            lineas, [new FacturaImpuesto("IVA", 19, monto, iva)]);
    }

    private static async Task<List<(int NroLinea, string CodItem, decimal Cantidad, decimal TotalItem)>> LineasAsync(SqlConnection cn, SqlTransaction tx, string oc) =>
        (await cn.QueryAsync<(int, string, decimal, decimal)>(
            "SELECT CAST(NroLinea AS int), CodItem, Cantidad, TotalItem FROM SAITEMCOM WHERE TipoCom = 'L' AND NumeroD = @oc ORDER BY NroLinea",
            new { oc }, tx)).ToList();

    private static async Task<Dictionary<string, decimal>> PedidoPvAsync(SqlConnection cn, SqlTransaction tx, IEnumerable<string> codigos) =>
        (await cn.QueryAsync<(string, decimal)>("SELECT CodProd, Pedido FROM SAPROD WHERE CodProd IN @codigos", new { codigos }, tx))
            .ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public async Task F2_dos_facturas_parciales_crean_cada_una_su_OC_y_van_cerrando_la_OC_original()
    {
        string[] productos = ["1I21085125150SM", "1I24110120130PP", "1I21033062400P4", "1I21100140140T1"];
        await using var cn = new SqlConnection(s.Pv);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();
        await FijarActualizarPedidoAsync(cn, tx, "1");
        await RepositorioSync.InsertarDocRefAsync(cn, tx, "OC_PEDIDO", "PV_MAY", 'L', "000003538", "FAB", 'E', "035850", null);
        var pedidoAntes = await PedidoPvAsync(cn, tx, productos);

        // Factura 1 (igual que la compra parcial PRUEBA-2 de P1b): SM completa, PP 45 de 90, P4 completa, T1 nada.
        var f1 = FacturaDePedido("F2-PRUEBA-1", ("1I21085125150SM", 250, 9437.0m), ("1I24110120130PP", 45, 5180.0m), ("1I21033062400P4", 180, 19117.64m));
        var ctx1 = new ContextoMensaje(SobreFactura(f1, "FAB"), cn, tx, "PV_MAY");
        await Manejador.ProcesarAsync(ctx1, default);

        // OC nueva idéntica a la factura, buscable por el número de factura, con la OC original en la marca.
        var nueva1 = await cn.QuerySingleAsync<(string NumeroD, string Notas1, decimal Monto)>(
            "SELECT NumeroD, Notas1, Monto FROM SACOMP WHERE TipoCom = 'L' AND OrdenC = 'F2-PRUEBA-1'", transaction: tx);
        Assert.Equal("SYNC FAB FAC F2-PRUEBA-1 OC 000003538", nueva1.Notas1);
        Assert.Equal(f1.Factura.Monto, nueva1.Monto);
        Assert.Equal([250m, 45m, 180m], (await LineasAsync(cn, tx, nueva1.NumeroD)).Select(l => l.Cantidad));

        // La OC original conserva su número y queda con el saldo, como la dejó SAINT en P1b, pero con los totales al día.
        Assert.Equal([(2, "1I24110120130PP", 45m, 221445m), (4, "1I21100140140T1", 200m, 2666385.9m)], await LineasAsync(cn, tx, "000003538"));
        Assert.Equal(2887830.9m, await cn.ExecuteScalarAsync<decimal>(
            "SELECT MtoTotal FROM SACOMP WHERE TipoCom = 'L' AND NumeroD = '000003538'", transaction: tx));
        var ack1 = Sobre.FromJson(await cn.ExecuteScalarAsync<string>("SELECT sobre FROM sync.Outbox WHERE msg_id = @id",
            new { id = ctx1.Respuestas.Single() }, tx) ?? "").Leer<AckFactura>();
        Assert.Equal([new AccionAck(TiposAccion.OcCreada, nueva1.NumeroD), new AccionAck(TiposAccion.OcActualizada, "000003538")], ack1.Acciones);
        Assert.Empty(ack1.Alertas);

        // Comprometido: la OC nueva suma y la original resta lo mismo; queda igual hasta que el PV haga la compra.
        Assert.Equal(pedidoAntes, await PedidoPvAsync(cn, tx, productos));

        // Factura 2: el resto. La OC original queda en cero y se borra, como hace SAINT.
        var f2 = FacturaDePedido("F2-PRUEBA-2", ("1I24110120130PP", 45, 5180.0m), ("1I21100140140T1", 200, 14033.61m));
        var ctx2 = new ContextoMensaje(SobreFactura(f2, "FAB"), cn, tx, "PV_MAY");
        await Manejador.ProcesarAsync(ctx2, default);

        Assert.Equal(0, await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM SACOMP WHERE TipoCom = 'L' AND NumeroD = '000003538'", transaction: tx));
        Assert.Empty(await LineasAsync(cn, tx, "000003538"));
        var ack2 = Sobre.FromJson(await cn.ExecuteScalarAsync<string>("SELECT sobre FROM sync.Outbox WHERE msg_id = @id",
            new { id = ctx2.Respuestas.Single() }, tx) ?? "").Leer<AckFactura>();
        Assert.Equal(TiposAccion.OcCerrada, ack2.Acciones[1].Tipo);
        Assert.Equal(pedidoAntes, await PedidoPvAsync(cn, tx, productos));
    }

    [Fact]
    public async Task F2_exceso_producto_no_pedido_y_OC_original_que_ya_no_existe_dejan_alertas_y_la_OC_nueva_igual()
    {
        await using var cn = new SqlConnection(s.Pv);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();
        await RepositorioSync.InsertarDocRefAsync(cn, tx, "OC_PEDIDO", "PV_MAY", 'L', "000003538", "FAB", 'E', "035850", null);

        // PP: 100 facturados contra 90 pedidos; 121080180080 no está en la OC 3538.
        var f = FacturaDePedido("F2-PRUEBA-3", ("1I24110120130PP", 100, 5180.0m), ("121080180080", 5, 7655.47m));
        var ctx = new ContextoMensaje(SobreFactura(f, "FAB"), cn, tx, "PV_MAY");
        await Manejador.ProcesarAsync(ctx, default);

        Assert.Equal([100m, 5m], (await LineasAsync(cn, tx,
            await cn.ExecuteScalarAsync<string>("SELECT NumeroD FROM SACOMP WHERE TipoCom = 'L' AND OrdenC = 'F2-PRUEBA-3'", transaction: tx) ?? "")).Select(l => l.Cantidad));
        Assert.DoesNotContain(await LineasAsync(cn, tx, "000003538"), l => l.CodItem == "1I24110120130PP");   // baja los 90 y no más
        Assert.Equal([CodigosError.CantidadExcedida, CodigosError.ProductoNoPedido], ctx.Alertas.Select(a => a.Codigo));

        // D7: la OC original ya no existe (el PV la cargó antes de llegar la factura). Se crea la OC nueva con alerta.
        var g0 = FacturaDePedido("F2-PRUEBA-4", ("1I21085125150SM", 10, 9437.0m));
        var g = g0 with { Items = [g0.Items[0] with { PedidoOrigen = null, OcOrigen = "000009999" }] };
        var ctxG = new ContextoMensaje(SobreFactura(g, "FAB"), cn, tx, "PV_MAY");
        await Manejador.ProcesarAsync(ctxG, default);
        Assert.Equal(1, await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM SACOMP WHERE TipoCom = 'L' AND OrdenC = 'F2-PRUEBA-4'", transaction: tx));
        Assert.Equal(CodigosError.OcNoEncontrada, Assert.Single(ctxG.Alertas).Codigo);
    }

    [Fact]
    public async Task Factura_de_pedido_hecho_a_mano_en_Fabrica_sigue_por_F3()
    {
        var factura = await LeerFacturaAsync(s.Fab, "FAB", "*31819", "9012085494");   // ítems con OTipo E, pedido 035828 sin relación OC_PEDIDO
        Assert.All(factura.Items, i => Assert.Equal("035828", i.PedidoOrigen));

        await using var cn = new SqlConnection(s.Pv);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();

        // Sin relación OC ↔ pedido no hay OC original que reducir: es F3 (aquí falla por productos que no están en el maestro del PV).
        var f3 = await Assert.ThrowsAsync<ErrorNegocioException>(() =>
            Manejador.ProcesarAsync(new ContextoMensaje(SobreFactura(factura, "FAB"), cn, tx, "PV_MAY"), default));
        Assert.All(f3.Errores, e => Assert.Equal(CodigosError.ProductoNoExiste, e.Codigo));
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
