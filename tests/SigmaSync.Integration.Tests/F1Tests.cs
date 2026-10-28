using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SigmaSync.Agent.Manejadores;
using SigmaSync.Contracts;
using SigmaSync.Saint;
using SigmaSync.Saint.F1;

namespace SigmaSync.Integration.Tests;

/// <summary>
/// F1 contra las copias SAINT reales. Cada prueba corre en una transacción que se deshace al final.
/// </summary>
[Collection(ColeccionSaint.Nombre)]
public sealed class F1Tests(CopiasSaint s)
{
    private static readonly ManejadorOcPedido Manejador = new(NullLogger<ManejadorOcPedido>.Instance);

    private static async Task<OcPedido> LeerOcDelPvAsync(string csPv, string numero)
    {
        await using var cn = new SqlConnection(csPv);
        await cn.OpenAsync();
        var cfg = ConfigNodo.Desde(await RepositorioSync.LeerConfigAsync(cn));
        var oc = await OrigenOc.BuscarAsync(cn, cfg, numero) ?? throw new InvalidOperationException($"OC {numero} no encontrada en el PV");
        return await OrigenOc.LeerPayloadAsync(cn, null, oc);
    }

    private static Sobre SobreOc(OcPedido oc, string destino) =>
        Sobre.Crear(TiposMensaje.OcPedido, "PV_MAY", destino, Sobre.Clave("PV_MAY", "L", oc.Oc.Numero), oc);

    private static Task<decimal> CantidadAsync(SqlConnection cn, SqlTransaction tx, string sql, object p) =>
        cn.ExecuteScalarAsync<decimal>(sql, p, tx);

    [Fact]
    public async Task Fabrica_crea_el_pedido_desde_la_OC_000003538_igual_que_SAINT()
    {
        var oc = await LeerOcDelPvAsync(s.Pv, "000003538");
        Assert.Equal("890919355-4", oc.Oc.CodProv);
        Assert.Equal(4, oc.Items.Count);

        await using var cn = new SqlConnection(s.Fab);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();
        var item = oc.Items.First(i => i.CodItem == "1I21033062400P4");
        var comproAntes = await CantidadAsync(cn, tx, "SELECT Compro FROM SAPROD WHERE CodProd = @CodItem", item);
        var cantComAntes = await CantidadAsync(cn, tx, "SELECT CantCom FROM SAEXIS WHERE CodProd = @CodItem AND CodUbic = '005'", item);

        var ctx = new ContextoMensaje(SobreOc(oc, "FAB"), cn, tx, "FAB");
        await Manejador.ProcesarAsync(ctx, default);

        // Consecutivo: PrxPedido = 35841 choca con 035841–035849 (integración WORKER); se salta y se alerta.
        var numero = await cn.ExecuteScalarAsync<string>(
            "SELECT NumeroD FROM SAFACT WHERE TipoFac = 'E' AND OrdenC = '000003538' AND CodUsua = 'SYNC'", transaction: tx);
        Assert.Equal("035850", numero);
        Assert.Equal(35851, await cn.ExecuteScalarAsync<int>("SELECT ValueInt FROM SACORRELSIS WHERE CodSucu = '00000' AND FieldName = 'PrxPedido'", transaction: tx));
        Assert.Contains(ctx.Alertas, a => a.Codigo == CodigosError.ConsecutivoColision && a.Mensaje.Contains("035849"));

        // Mismas columnas llenas que el pedido 035829 grabado por SAINT (más la marca Notas1).
        var patron = await CopiasSaint.ColumnasConValorAsync(cn, tx, "SAFACT", "TipoFac = 'E' AND NumeroD = '035829'", new { });
        var generado = await CopiasSaint.ColumnasConValorAsync(cn, tx, "SAFACT", "TipoFac = 'E' AND NumeroD = @numero", new { numero });
        generado.Remove("Notas1");
        Assert.Equal(patron, generado);

        var patronItem = await CopiasSaint.ColumnasConValorAsync(cn, tx, "SAITEMFAC", "TipoFac = 'E' AND NumeroD = '035829' AND NroLinea = 1", new { });
        var generadoItem = await CopiasSaint.ColumnasConValorAsync(cn, tx, "SAITEMFAC", "TipoFac = 'E' AND NumeroD = @numero AND NroLinea = 3", new { numero });
        foreach (var c in new[] { "Descrip2", "Descrip3" }) { patronItem.Remove(c); generadoItem.Remove(c); }   // dependen del maestro del producto
        Assert.Equal(patronItem, generadoItem);

        var e = await cn.QuerySingleAsync("""
            SELECT CodClie, CodUbic, CodVend, CodOper, CodUsua, Signo, Factor, EsCorrel, Monto, TGravable, TotalPrd, MtoTax, MtoTotal, Credito,
                   FechaE, FechaI, FechaV, Notas1
            FROM SAFACT WHERE TipoFac = 'E' AND NumeroD = @numero
            """, new { numero }, tx);
        Assert.Equal("2853060", (string)e.CodClie);
        Assert.Equal("005", (string)e.CodUbic);
        Assert.Equal("1305", (string)e.CodVend);
        Assert.Equal("1069", (string)e.CodOper);
        Assert.Equal(1, Convert.ToInt32(e.Signo));
        Assert.Equal(1, Convert.ToInt32(e.EsCorrel));
        Assert.Equal((decimal)e.TGravable, (decimal)e.Monto);
        Assert.Equal((decimal)e.Monto + (decimal)e.MtoTax, (decimal)e.MtoTotal);
        Assert.Equal((decimal)e.MtoTotal, (decimal)e.Credito);
        Assert.Equal(((DateTime)e.FechaE).Date, (DateTime)e.FechaV);   // DiasCred 0
        Assert.Equal("SYNC PV_MAY OC 000003538", (string)e.Notas1);

        var linea = await cn.QuerySingleAsync("SELECT Precio, PriceO, Cantidad, TotalItem, Refere FROM SAITEMFAC WHERE TipoFac = 'E' AND NumeroD = @numero AND NroLinea = 3",
            new { numero }, tx);
        Assert.Equal(19117.65m, (decimal)linea.Precio);    // Precio3 de Fábrica, el mismo de la factura *33570
        Assert.Equal(19117.647m, (decimal)linea.PriceO);
        Assert.Equal(3441177.00m, (decimal)linea.TotalItem);
        Assert.Equal("1I21033062400P4", (string)linea.Refere);

        // Comprometido, trazabilidad y respuesta al PV.
        Assert.Equal(comproAntes + 180, await CantidadAsync(cn, tx, "SELECT Compro FROM SAPROD WHERE CodProd = @CodItem", item));
        Assert.Equal(cantComAntes + 180, await CantidadAsync(cn, tx, "SELECT CantCom FROM SAEXIS WHERE CodProd = @CodItem AND CodUbic = '005'", item));
        Assert.Equal(1, await cn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sync.DocRef WHERE relacion = 'OC_PEDIDO' AND num_origen = '000003538' AND num_destino = @numero", new { numero }, tx));
        var ack = Sobre.FromJson(await cn.ExecuteScalarAsync<string>("SELECT sobre FROM sync.Outbox WHERE tipo = 'ACK_PEDIDO' AND msg_id = @id",
            new { id = ctx.Respuestas.Single() }, tx) ?? "").Leer<AckPedido>();
        Assert.Equal(("000003538", EstadosAck.Creado, "035850"), (ack.OcNumero, ack.Estado, ack.PedidoNumero));
        Assert.Empty(ack.Errores);
        // tx se deshace al salir.
    }

    [Fact]
    public async Task CEDI_crea_el_pedido_con_sus_propios_valores_y_escalas()
    {
        var oc = await LeerOcDelPvAsync(s.Pv, "000003465");
        Assert.Equal("8909193545", oc.Oc.CodProv);

        await using var cn = new SqlConnection(s.Cedi);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();
        var comproAntes = await CantidadAsync(cn, tx, "SELECT Compro FROM SAPROD WHERE CodProd = '111216'", new { });

        var ctx = new ContextoMensaje(SobreOc(oc, "CEDI"), cn, tx, "CEDI");
        await Manejador.ProcesarAsync(ctx, default);

        var e = await cn.QuerySingleAsync(
            "SELECT NumeroD, CodClie, CodUbic, CodVend, CodOper, Monto, MtoTax, MtoTotal FROM SAFACT WHERE TipoFac = 'E' AND OrdenC = '000003465'",
            transaction: tx);
        Assert.Equal("000001", (string)e.NumeroD);        // el CEDI nunca ha usado pedidos: PrxPedido = 1
        Assert.Equal("9012380209", (string)e.CodClie);
        Assert.Equal("100", (string)e.CodUbic);
        Assert.Equal("9810", (string)e.CodVend);
        Assert.Null((string?)e.CodOper);
        Assert.Empty(ctx.Alertas);

        // 20 × 6.188,65 (Precio3 del CEDI) × 19 % = 23.516,87: MtoTax con 4 decimales y MtoTaxO con 3 en esta versión.
        var l = await cn.QuerySingleAsync("SELECT Precio, TotalItem, MtoTax, MtoTaxO FROM SAITEMFAC WHERE TipoFac = 'E' AND NumeroD = '000001' AND CodItem = '111216'",
            transaction: tx);
        Assert.Equal(6188.65m, (decimal)l.Precio);
        Assert.Equal(123773.00m, (decimal)l.TotalItem);
        Assert.Equal(23516.87m, (decimal)l.MtoTax);
        Assert.Equal(comproAntes + 20, await CantidadAsync(cn, tx, "SELECT Compro FROM SAPROD WHERE CodProd = '111216'", new { }));
    }

    [Fact]
    public async Task Producto_inexistente_rechaza_la_OC_completa()
    {
        var oc = await LeerOcDelPvAsync(s.Pv, "000003538");
        oc = oc with { Items = [.. oc.Items, new OcItem(9, "NO_EXISTE_XYZ", 1, 1)] };

        await using var cn = new SqlConnection(s.Fab);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();

        var ex = await Assert.ThrowsAsync<ErrorNegocioException>(() =>
            Manejador.ProcesarAsync(new ContextoMensaje(SobreOc(oc, "FAB"), cn, tx, "FAB"), default));

        Assert.Equal(CodigosError.ProductoNoExiste, ex.Errores.Single().Codigo);
        Assert.Equal("NO_EXISTE_XYZ", ex.Errores.Single().CodItem);
        var rechazo = Manejador.RespuestaRechazo(SobreOc(oc, "FAB"), "FAB", ex.Errores)!.Leer<AckPedido>();
        Assert.Equal(("000003538", EstadosAck.Rechazado), (rechazo.OcNumero, rechazo.Estado));
    }

    [Fact]
    public async Task Captura_toma_solo_OC_a_Fabrica_o_CEDI_y_excluye_las_creadas_por_el_agente()
    {
        await using var cn = new SqlConnection(s.Pv);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();
        var cfg = ConfigNodo.Desde(await RepositorioSync.LeerConfigAsync(cn, tx)) with
        {
            CapturaDesde = new DateTime(2026, 3, 1), VentanaDias = 3650, EsperaSegundos = 0,
        };
        var ahora = new DateTime(2026, 10, 28, 12, 0, 0);

        var candidatas = await OrigenOc.CandidatasAsync(cn, cfg, ahora, tx);
        Assert.Contains(candidatas, c => c is { NumeroD: "000003538", NodoDestino: "FAB" });
        Assert.Contains(candidatas, c => c is { NumeroD: "000003465", NodoDestino: "CEDI" });
        Assert.DoesNotContain(candidatas, c => c.NumeroD == "000003559");   // proveedor externo
        Assert.All(candidatas, c => Assert.Contains(c.CodProv, new[] { "890919355-4", "8909193545" }));

        // Una OC grabada por el usuario de sincronización (F3) no vuelve a Fábrica como pedido.
        await cn.ExecuteAsync("UPDATE SACOMP SET CodUsua = 'SYNC' WHERE TipoCom = 'L' AND NumeroD = '000003538'", transaction: tx);
        Assert.DoesNotContain(await OrigenOc.CandidatasAsync(cn, cfg, ahora, tx), c => c.NumeroD == "000003538");
    }

    [Fact]
    public async Task El_PV_registra_la_relacion_OC_pedido_al_recibir_el_ACK()
    {
        var sobre = Sobre.Crear(TiposMensaje.AckPedido, "FAB", "PV_MAY", "PV_MAY:L:000003538",
            new AckPedido("000003538", EstadosAck.Creado, "035850", []));

        await using var cn = new SqlConnection(s.Pv);
        await cn.OpenAsync();
        await using var tx = cn.BeginTransaction();
        await new ManejadorAckPedido(NullLogger<ManejadorAckPedido>.Instance).ProcesarAsync(new ContextoMensaje(sobre, cn, tx, "PV_MAY"), default);

        var r = await cn.QuerySingleAsync("SELECT nodo_origen, tipo_origen, num_origen, nodo_destino, tipo_destino, num_destino FROM sync.DocRef WHERE msg_id = @MsgId",
            sobre, tx);
        Assert.Equal(("PV_MAY", "L", "000003538", "FAB", "E", "035850"),
            ((string)r.nodo_origen, (string)r.tipo_origen, (string)r.num_origen, (string)r.nodo_destino, (string)r.tipo_destino, (string)r.num_destino));
    }

    [Fact]
    public async Task Los_tres_nodos_pasan_la_validacion_de_columnas_de_su_rol()
    {
        foreach (var (cs, nodo, rol) in new[] { (s.Pv, "PV_MAY", "PV"), (s.Fab, "FAB", "FAB"), (s.Cedi, "CEDI", "CEDI") })
        {
            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync();
            var (problemas, rolLeido) = await ValidadorEsquema.ValidarNodoAsync(cn, nodo);
            Assert.Empty(problemas);
            Assert.Equal(rol, rolLeido);
        }
    }
}
