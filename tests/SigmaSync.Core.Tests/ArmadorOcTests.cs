using SigmaSync.Contracts;
using SigmaSync.Core.F3;

namespace SigmaSync.Core.Tests;

public class ArmadorOcTests
{
    private static readonly ParametrosOc Pv = new("00000", "115", "SYNC", "SYNC", "FAB");
    private static readonly ProveedorPv Sigmaplas = new("890919355-4", "SIGMAPLAS S.A.", "890919355-4");

    private static ProductoPv Prod(string cod, bool exento = false, bool activo = true, decimal existen = 10) =>
        new(cod, $"Producto {cod}", "Desc2", null, "IMPRESO", 1, 2, 3, exento, activo, existen);

    private static Dictionary<string, ProductoPv> Dic(params ProductoPv[] p) => p.ToDictionary(x => x.CodProd);

    /// <summary>Factura POS *33570 de Fábrica: 2 líneas con 5 % de descuento global (Descto1 65.058,88).</summary>
    private static FacturaOc Factura33570() => new(
        new FacturaEncabezado("*33570", "A", "2853060", DateTimeOffset.Parse("2026-10-19T14:09:38-05:00"),
            1301177.60m, 65058.88m, 0, 0, 1236118.72m, 0, 234862.56m, 1470981.28m),
        [
            new FacturaItem(1, "1I21033062400P4", 12, 19117.65m, 19117.647m, 229411.80m, false, null, null),
            new FacturaItem(2, "121080180080", 140, 7655.47m, 7655.471m, 1071765.80m, false, null, null),
        ],
        [new FacturaImpuesto("IVA", 19, 1236118.72m, 234862.557m)]);

    /// <summary>Cuadra al centavo con la compra 33570 que el PV digitó a mano a partir de esa factura.</summary>
    [Fact]
    public void Reproduce_la_compra_33570_digitada_en_el_PV()
    {
        var r = ArmadorOc.Armar(Factura33570(), Sigmaplas, Dic(Prod("1I21033062400P4", existen: 115.48m), Prod("121080180080")), Pv, EscalasOc.Pv);

        Assert.True(r.Ok);
        var (e, items) = (r.Oc!.Encabezado, r.Oc.Items);
        Assert.Equal(18161.7675m, items[0].Costo);
        Assert.Equal(217941.21m, items[0].TotalItem);
        Assert.Equal(7272.6965m, items[1].Costo);
        Assert.Equal(1018177.51m, items[1].TotalItem);
        Assert.Equal(1236118.72m, e.Monto);          // = TGravable de la factura (neto de descuento)
        Assert.Equal(1236118.72m, e.TGravable);
        Assert.Equal(0m, e.TExento);
        Assert.Equal(new DateTime(2026, 10, 19), e.FechaE);   // fecha de la factura, sin hora
        Assert.Equal("*33570", e.OrdenC);
        Assert.Equal("SYNC FAB FAC *33570", e.Notas1);
        Assert.Equal(("890919355-4", "SIGMAPLAS S.A."), (e.CodProv, e.Descrip));
        Assert.Equal(115.48m, items[0].ExistAnt);
        Assert.Equal("IMPRESO", items[0].Refere);
        Assert.Empty(r.Alertas);
    }

    [Fact]
    public void Sin_descuento_el_costo_es_el_precio_facturado()
    {
        // CEDI: factura normal, sin descuento.
        var f = Factura33570() with
        {
            Factura = Factura33570().Factura with { Numero = "009950", Descto1 = 0, Monto = 229411.80m, TGravable = 229411.80m },
            Items = [Factura33570().Items[0]],
        };

        var r = ArmadorOc.Armar(f, Sigmaplas, Dic(Prod("1I21033062400P4")), Pv with { NodoOrigen = "CEDI" }, EscalasOc.Pv);

        Assert.Equal(19117.65m, r.Oc!.Items[0].Costo);
        Assert.Equal("SYNC CEDI FAC 009950", r.Oc.Encabezado.Notas1);
    }

    [Fact]
    public void Item_exento_va_a_TExento_y_alerta_si_el_maestro_del_PV_difiere()
    {
        // 009950 del CEDI: el 4180 viene exento y en el PV está como gravado.
        var f = new FacturaOc(
            new FacturaEncabezado("009950", "A", "9012380209", DateTimeOffset.Parse("2026-10-20T08:01:33-05:00"),
                228888m, 0, 0, 0, 69240m, 159648m, 13155.60m, 242043.60m),
            [
                new FacturaItem(1, "3487", 60, 1154m, 1154m, 69240m, false, null, null),
                new FacturaItem(16, "4180", 24, 6652m, 6652m, 159648m, true, null, null),
            ],
            []);

        var r = ArmadorOc.Armar(f, Sigmaplas, Dic(Prod("3487"), Prod("4180", exento: false)), Pv, EscalasOc.Pv);

        Assert.Equal(69240m, r.Oc!.Encabezado.TGravable);
        Assert.Equal(159648m, r.Oc.Encabezado.TExento);
        Assert.Equal(228888m, r.Oc.Encabezado.Monto);
        Assert.True(r.Oc.Items[1].EsExento);               // la OC sigue a la factura
        Assert.Equal(16, r.Oc.Items[1].NroLinea);
        var alerta = Assert.Single(r.Alertas);
        Assert.Equal((CodigosError.ExentoDiferente, "4180"), (alerta.Codigo, alerta.CodItem));
    }

    [Fact]
    public void Alerta_fletes_y_diferencias_de_total()
    {
        var f = Factura33570() with { Factura = Factura33570().Factura with { Fletes = 5000, Monto = 1301190m } };

        var r = ArmadorOc.Armar(f, Sigmaplas, Dic(Prod("1I21033062400P4"), Prod("121080180080")), Pv, EscalasOc.Pv);

        Assert.True(r.Ok);
        Assert.Contains(r.Alertas, a => a.Codigo == CodigosError.FletesNoIncluidos);
        Assert.Contains(r.Alertas, a => a.Codigo == CodigosError.DiferenciaTotal);
    }

    [Fact]
    public void Productos_faltantes_o_inactivos_en_el_PV_rechazan_la_factura()
    {
        var r = ArmadorOc.Armar(Factura33570(), Sigmaplas, Dic(Prod("1I21033062400P4", activo: false)), Pv, EscalasOc.Pv);

        Assert.False(r.Ok);
        Assert.Equal([CodigosError.ProductoInactivo, CodigosError.ProductoNoExiste], r.Errores.Select(e => e.Codigo));
    }

    [Theory]
    [InlineData(1301177.60, 65058.88, 0, 0.95)]
    [InlineData(1000, 0, 0, 1)]
    [InlineData(1000, 50, 50, 0.9)]
    [InlineData(0, 0, 0, 1)]
    public void Factor_de_descuento(decimal monto, decimal d1, decimal d2, decimal esperado)
    {
        var f = Factura33570().Factura with { Monto = monto, Descto1 = d1, Descto2 = d2 };

        Assert.Equal(esperado, ArmadorOc.FactorDescuento(f));
    }
}
