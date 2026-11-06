using SigmaSync.Contracts;
using SigmaSync.Core.F1;

namespace SigmaSync.Core.Tests;

public class ArmadorPedidoTests
{
    private static readonly ParametrosPedido Fab = new("00000", "005", "1305", "1069", "SYNC", "SYNC", "PV_MAY",
        new DateTime(2025, 10, 30, 13, 53, 26, 850));

    private static ProductoDestino Prod(string cod, decimal precio3, decimal costo = 100, bool exento = false, decimal tarifa = 19, bool activo = true) =>
        new(cod, $"Producto {cod}", null, $"Ref {cod}", precio3, costo, exento, tarifa, activo);

    private static OcPedido Oc(params (string Cod, decimal Cant)[] items) =>
        new(new OcEncabezado("000003538", "00000", "890919355-4", new DateOnly(2026, 9, 1), DateTimeOffset.Now, 18047),
            items.Select((x, i) => new OcItem(i + 1, x.Cod, x.Cant, 1m)).ToList());

    private static Dictionary<string, ProductoDestino> Dic(params ProductoDestino[] p) => p.ToDictionary(x => x.CodProd);

    private static readonly ClienteDestino Cliente = new("2853060", "SGMPL ITAGUI B19", "2853060", "CR 53 32 80", null, "6048586", 0);

    /// <summary>
    /// Reproduce al centavo el pedido 035829 grabado por SAINT en Fábrica: 60 × 1I63176230070PB y
    /// 15 × 1I63140176060PB, con el Precio3 vigente en octubre de 2025 (23.121,84874 y 10.857,14286).
    /// </summary>
    [Fact]
    public void Reproduce_el_pedido_035829_de_SAINT()
    {
        var r = ArmadorPedido.Armar(
            Oc(("1I63176230070PB", 60), ("1I63140176060PB", 15)),
            Cliente with { DiasCred = 5 },
            Dic(Prod("1I63176230070PB", 23121.84874m, 15200), Prod("1I63140176060PB", 10857.14286m, 7000)),
            Fab, EscalasPedido.Fabrica);

        Assert.True(r.Ok);
        var (e, items) = (r.Pedido!.Encabezado, r.Pedido.Items);

        Assert.Equal(23121.85m, items[0].Precio);
        Assert.Equal(23121.849m, items[0].PriceO);
        Assert.Equal(1387311.00m, items[0].TotalItem);
        Assert.Equal(263589.076m, items[0].MtoTax);
        Assert.Equal(263589.0756m, items[0].MtoTaxO);
        Assert.Equal(15200m, items[0].Costo);

        Assert.Equal(10857.14m, items[1].Precio);
        Assert.Equal(10857.143m, items[1].PriceO);
        Assert.Equal(162857.10m, items[1].TotalItem);
        Assert.Equal(30942.857m, items[1].MtoTax);
        Assert.Equal(30942.8572m, items[1].MtoTaxO);

        Assert.Equal(1550168.10m, e.TGravable);
        Assert.Equal(1550168.10m, e.Monto);
        Assert.Equal(0m, e.TExento);
        Assert.Equal(294531.93m, e.MtoTax);
        Assert.Equal(1844700.03m, e.MtoTotal);
        Assert.Equal(new DateTime(2025, 10, 30, 13, 53, 26), e.FechaE);   // sin milisegundos
        Assert.Equal(new DateTime(2025, 11, 4), e.FechaV);                 // FechaE + 5 días de crédito, sin hora
    }

    /// <summary>
    /// Reproduce el pedido 035850 grabado en pantalla en la copia de Fábrica (2026-11-04, SAINT 9.0.2.6):
    /// Costo = CostPro, Refere = SAPROD.Refere (el código si está vacío), ExistAnt = existencia del depósito, SaldoAct = saldo del cliente.
    /// </summary>
    [Fact]
    public void Reproduce_el_pedido_035850_de_SAINT()
    {
        var r = ArmadorPedido.Armar(
            Oc(("162240340070", 7), ("161240340090", 13), ("3458", 3)),
            Cliente with { Saldo = 13355954910.64m },
            Dic(Prod("162240340070", 3857.15126m, 1572.9539m) with { Refere = "618NE", ExistenDepo = 1920 },
                Prod("161240340090", 4731.10084m, 1929.3418m) with { Refere = "16103", ExistenDepo = 790 },
                Prod("3458", 7352.94958m, 3200m) with { Refere = "", ExistenDepo = 330 }),
            Fab, EscalasPedido.Fabrica);

        Assert.True(r.Ok);
        var (e, items) = (r.Pedido!.Encabezado, r.Pedido.Items);

        Assert.Equal(["618NE", "16103", "3458"], items.Select(i => i.Refere));
        Assert.Equal([1572.9539m, 1929.3418m, 3200m], items.Select(i => i.Costo));
        Assert.Equal([1920m, 790m, 330m], items.Select(i => i.ExistAnt));
        Assert.Equal([27000.05m, 61504.30m, 22058.85m], items.Select(i => i.TotalItem));
        Assert.Equal([5130.011m, 11685.819m, 4191.181m], items.Select(i => i.MtoTax));
        Assert.Equal([5130.0112m, 11685.8191m, 4191.1813m], items.Select(i => i.MtoTaxO));
        Assert.Equal([3857.151m, 4731.101m, 7352.950m], items.Select(i => i.PriceO));

        Assert.Equal(110563.20m, e.Monto);
        Assert.Equal(21007.01m, e.MtoTax);
        Assert.Equal(131570.21m, e.MtoTotal);
        Assert.Equal(13355954910.64m, e.SaldoAct);
    }

    [Fact]
    public void Usa_el_precio_de_lista_3_del_destino_y_no_el_costo_de_la_OC()
    {
        // 1I21033062400P4: la factura *33570 al PV usó 19.117,65 = Precio3 de Fábrica; la OC trae 18.161,76 (con 5 % de descuento).
        var oc = new OcPedido(Oc().Oc, [new OcItem(3, "1I21033062400P4", 180, 18161.76m)]);

        var r = ArmadorPedido.Armar(oc, Cliente, Dic(Prod("1I21033062400P4", 19117.64706m, 10200)), Fab, EscalasPedido.Fabrica);

        var i = r.Pedido!.Items.Single();
        Assert.Equal(3, i.NroLinea);
        Assert.Equal(19117.65m, i.Precio);
        Assert.Equal(3441177.00m, i.TotalItem);
        Assert.Equal(r.Pedido.Encabezado.FechaE.Date, r.Pedido.Encabezado.FechaV);   // DiasCred 0
        Assert.Equal("000003538", r.Pedido.Encabezado.OrdenC);
        Assert.Equal("SYNC PV_MAY OC 000003538", r.Pedido.Encabezado.Notas1);
    }

    [Fact]
    public void Producto_exento_va_a_TExento_sin_impuesto()
    {
        var r = ArmadorPedido.Armar(Oc(("G", 2), ("X", 3)), Cliente,
            Dic(Prod("G", 100m), Prod("X", 50m, exento: true)), Fab, EscalasPedido.Fabrica);

        var e = r.Pedido!.Encabezado;
        Assert.Equal(200m, e.TGravable);
        Assert.Equal(150m, e.TExento);
        Assert.Equal(350m, e.Monto);
        Assert.Equal(38m, e.MtoTax);
        Assert.Equal(388m, e.MtoTotal);
        Assert.True(r.Pedido.Items[1].EsExento);
        Assert.Equal(0m, r.Pedido.Items[1].MtoTax);
    }

    [Fact]
    public void Sin_tarifa_de_impuesto_el_item_va_exento()
    {
        var r = ArmadorPedido.Armar(Oc(("S", 1)), Cliente, Dic(Prod("S", 100m, tarifa: 0)), Fab, EscalasPedido.Fabrica);

        Assert.True(r.Pedido!.Items[0].EsExento);
        Assert.Equal(100m, r.Pedido.Encabezado.TExento);
    }

    [Fact]
    public void Respeta_las_escalas_del_CEDI()
    {
        var cedi = new EscalasPedido(PriceO: 4, TotalItem: 4, MtoTax: 4, MtoTaxO: 3);

        var r = ArmadorPedido.Armar(Oc(("111216", 20)), Cliente, Dic(Prod("111216", 6188.65m)), Fab with { CodOper = null }, cedi);

        var i = r.Pedido!.Items[0];
        Assert.Equal(6188.65m, i.PriceO);
        Assert.Equal(23516.87m, i.MtoTax);    // 20 × 6.188,65 × 19 % = 23.516,87
        Assert.Equal(23516.87m, i.MtoTaxO);
        Assert.Null(r.Pedido.Encabezado.CodOper);
    }

    [Fact]
    public void Reune_todos_los_errores_de_productos_antes_de_rechazar()
    {
        var r = ArmadorPedido.Armar(Oc(("NO_EXISTE", 1), ("INACTIVO", 1), ("SIN_PRECIO", 1), ("OK", 0)), Cliente,
            Dic(Prod("INACTIVO", 10, activo: false), Prod("SIN_PRECIO", 0), Prod("OK", 10)), Fab, EscalasPedido.Fabrica);

        Assert.False(r.Ok);
        Assert.Equal(
            [CodigosError.ProductoNoExiste, CodigosError.ProductoInactivo, CodigosError.ProductoSinPrecio, CodigosError.CantidadInvalida],
            r.Errores.Select(e => e.Codigo));
        Assert.Equal("NO_EXISTE", r.Errores[0].CodItem);
    }

    [Fact]
    public void OC_sin_items_se_rechaza()
    {
        var r = ArmadorPedido.Armar(Oc(), Cliente, Dic(), Fab, EscalasPedido.Fabrica);

        Assert.Equal(CodigosError.OcSinItems, r.Errores.Single().Codigo);
    }

    [Fact]
    public void La_marca_no_supera_60_caracteres()
    {
        Assert.Equal(60, ArmadorPedido.Marca("NODO", "OC", new string('9', 80)).Length);
    }
}
