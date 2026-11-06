using SigmaSync.Contracts;
using SigmaSync.Core.F2;

namespace SigmaSync.Core.Tests;

/// <summary>D5: a la OC original del PV se le resta lo facturado, como hace SAINT al recibir una OC en partes (P1b).</summary>
public class SaldoOcTests
{
    /// <summary>OC 000003538 del PV, tal como está en la copia: 4 líneas en el depósito 115.</summary>
    private static readonly LineaOc[] Oc3538 =
    [
        new(1, "1I21085125150SM", "115", 250, 8965.1215m, false),
        new(2, "1I24110120130PP", "115", 90, 4921m, false),
        new(3, "1I21033062400P4", "115", 180, 18161.76m, false),
        new(4, "1I21100140140T1", "115", 200, 13331.9295m, false),
    ];

    [Fact]
    public void Factura_parcial_borra_lo_cumplido_y_deja_el_saldo_como_SAINT_en_P1b()
    {
        // Mismo caso que la compra parcial PRUEBA-2 en pantalla: línea 1 completa, línea 2 a la mitad, línea 3 completa, línea 4 nada.
        var s = SaldoOc.Restar("000003538", Oc3538,
            [("1I21085125150SM", 250), ("1I24110120130PP", 45), ("1I21033062400P4", 180)], escalaTotalItem: 4);

        Assert.False(s.Cerrada);
        Assert.Equal(2, s.LineasQueQuedan);
        Assert.Equal(
            [
                new CambioLineaOc(1, "1I21085125150SM", "115", 0, 0, 250),
                new CambioLineaOc(2, "1I24110120130PP", "115", 45, 221445m, 45),
                new CambioLineaOc(3, "1I21033062400P4", "115", 0, 0, 180),
            ], s.Cambios);
        // Saldo bien calculado: 45 × 4.921 + 200 × 13.331,9295 (SAINT dejó MtoTotal en 8.619.673,08 y recalcula al cargar).
        Assert.Equal(221445m + 2666385.9m, s.Monto);
        Assert.Equal(0m, s.TExento);
        Assert.Empty(s.Alertas);
    }

    [Fact]
    public void Factura_completa_cierra_la_OC()
    {
        var s = SaldoOc.Restar("000003538", Oc3538, Oc3538.Select(l => (l.CodItem, l.Cantidad)), 4);

        Assert.True(s.Cerrada);
        Assert.All(s.Cambios, c => Assert.True(c.Borrar));
        Assert.Equal(Oc3538.Select(l => l.Cantidad), s.Cambios.Select(c => c.Reducido));
        Assert.Equal(0m, s.Monto);
    }

    [Fact]
    public void Dos_facturas_seguidas_terminan_de_cerrar_la_OC()
    {
        var primera = SaldoOc.Restar("000003538", Oc3538, [("1I21085125150SM", 250), ("1I24110120130PP", 45)], 4);
        var quedan = Oc3538.Where(l => !primera.Cambios.Any(c => c.NroLinea == l.NroLinea && c.Borrar))
            .Select(l => primera.Cambios.FirstOrDefault(c => c.NroLinea == l.NroLinea) is { } c ? l with { Cantidad = c.CantidadNueva } : l)
            .ToList();

        var segunda = SaldoOc.Restar("000003538", quedan, [("1I24110120130PP", 45), ("1I21033062400P4", 180), ("1I21100140140T1", 200)], 4);

        Assert.True(segunda.Cerrada);
        Assert.Empty(segunda.Alertas);
    }

    [Fact]
    public void Exceso_y_producto_no_pedido_dejan_alerta_y_el_comprometido_baja_solo_lo_pendiente()
    {
        var s = SaldoOc.Restar("000003538", Oc3538, [("1I24110120130PP", 100), ("NUEVO", 5)], 4);

        var pp = Assert.Single(s.Cambios);
        Assert.Equal((0m, 90m), (pp.CantidadNueva, pp.Reducido));   // baja 90, no 100
        Assert.Equal(
            [(CodigosError.CantidadExcedida, "1I24110120130PP"), (CodigosError.ProductoNoPedido, "NUEVO")],
            s.Alertas.Select(a => (a.Codigo, a.CodItem!)));
        Assert.Contains("10", s.Alertas[0].Mensaje);
    }

    [Fact]
    public void Producto_repetido_en_la_OC_se_consume_en_orden_de_linea()
    {
        LineaOc[] oc = [new(1, "A", "115", 10, 2m, false), new(2, "B", "115", 3, 1m, true), new(3, "A", "115", 10, 2m, false)];

        var s = SaldoOc.Restar("X", oc, [("a", 15)], 2);   // el código no distingue mayúsculas, como SQL Server

        Assert.Equal([new CambioLineaOc(1, "A", "115", 0, 0, 10), new CambioLineaOc(3, "A", "115", 5, 10m, 5)], s.Cambios);
        Assert.Equal((10m, 3m), (s.TGravable, s.TExento));
    }
}
