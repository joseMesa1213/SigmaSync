using SigmaSync.Contracts;
using static SigmaSync.Core.Redondeo;

namespace SigmaSync.Core.F2;

/// <summary>Línea de la OC original del PV (SAITEMCOM, TipoCom L).</summary>
public sealed record LineaOc(int NroLinea, string CodItem, string CodUbic, decimal Cantidad, decimal Costo, bool EsExento);

/// <summary>
/// Cambio en una línea de la OC original. <see cref="CantidadNueva"/> = 0 significa que la línea se borra.
/// <see cref="Reducido"/> es lo que baja el comprometido (SAPROD.Pedido / SAEXIS.CantPed).
/// </summary>
public sealed record CambioLineaOc(int NroLinea, string CodItem, string CodUbic, decimal CantidadNueva, decimal TotalItemNuevo, decimal Reducido)
{
    public bool Borrar => CantidadNueva == 0;
}

/// <summary>Resultado de restar una factura a la OC original. Si no queda ninguna línea, la OC se borra.</summary>
public sealed record SaldoCalculado(
    IReadOnlyList<CambioLineaOc> Cambios,
    int LineasQueQuedan,
    decimal TGravable,
    decimal TExento,
    IReadOnlyList<ErrorDoc> Alertas)
{
    public bool Cerrada => LineasQueQuedan == 0;
    public decimal Monto => TGravable + TExento;
}

/// <summary>
/// F2 (D5): la factura de un pedido sincronizado genera en el PV una OC nueva idéntica a la factura (como F3), y a la OC
/// original se le resta lo facturado, igual que hace SAINT cuando el PV recibe una OC en partes (prueba P1b, 2026-11-05):
/// la línea cumplida se borra, la parcial queda con el saldo y, si no queda nada, se borra la OC.
/// A diferencia de SAINT, aquí se recalculan TotalItem y los totales del encabezado (SAINT los deja viejos, pero
/// al cargar la OC recalcula con Cantidad × Costo, así que no le afecta).
/// </summary>
public static class SaldoOc
{
    public static SaldoCalculado Restar(string ocNumero, IReadOnlyList<LineaOc> lineas,
        IEnumerable<(string CodItem, decimal Cantidad)> facturado, int escalaTotalItem)
    {
        var alertas = new List<ErrorDoc>();
        var pendiente = lineas.ToDictionary(l => l.NroLinea, l => l.Cantidad);
        var reducido = lineas.ToDictionary(l => l.NroLinea, _ => 0m);

        foreach (var g in facturado.GroupBy(f => f.CodItem.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var porRestar = g.Sum(f => f.Cantidad);
            var delProducto = lineas.Where(l => string.Equals(l.CodItem.Trim(), g.Key, StringComparison.OrdinalIgnoreCase))
                                    .OrderBy(l => l.NroLinea).ToList();
            if (delProducto.Count == 0)
            {
                alertas.Add(new ErrorDoc(CodigosError.ProductoNoPedido,
                    $"{g.Key}: se facturaron {porRestar:0.####} y no está en la OC {ocNumero}; va solo en la OC nueva", g.Key));
                continue;
            }

            foreach (var l in delProducto)
            {
                var toma = Math.Min(pendiente[l.NroLinea], porRestar);
                pendiente[l.NroLinea] -= toma;
                reducido[l.NroLinea] += toma;
                porRestar -= toma;
                if (porRestar <= 0) break;
            }
            if (porRestar > 0)
                alertas.Add(new ErrorDoc(CodigosError.CantidadExcedida,
                    $"{g.Key}: se facturaron {porRestar:0.####} más de lo pendiente en la OC {ocNumero}; la OC nueva lleva lo facturado", g.Key));
        }

        var cambios = lineas.Where(l => reducido[l.NroLinea] > 0)
            .Select(l => new CambioLineaOc(l.NroLinea, l.CodItem, l.CodUbic, pendiente[l.NroLinea],
                R(l.Costo * pendiente[l.NroLinea], escalaTotalItem), reducido[l.NroLinea]))
            .ToList();

        var quedan = lineas.Where(l => pendiente[l.NroLinea] > 0).ToList();
        decimal Total(bool exento) => quedan.Where(l => l.EsExento == exento).Sum(l => R(l.Costo * pendiente[l.NroLinea], escalaTotalItem));

        return new SaldoCalculado(cambios, quedan.Count, Total(false), Total(true), alertas);
    }
}
