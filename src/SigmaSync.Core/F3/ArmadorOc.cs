using SigmaSync.Contracts;
using SigmaSync.Core.F1;
using static SigmaSync.Core.Redondeo;

namespace SigmaSync.Core.F3;

public sealed record ResultadoOc(OcArmada? Oc, IReadOnlyList<ErrorDoc> Errores, IReadOnlyList<ErrorDoc> Alertas)
{
    public bool Ok => Oc is not null;
}

/// <summary>
/// F3: arma la OC del PV idéntica a la factura de Fábrica o CEDI, para que el trabajador la cargue en Compras.
/// Costo = Precio facturado × (1 − (Descto1 + Descto2) / Monto): el descuento global se prorratea en el costo.
/// La OC no lleva impuesto (SAINT lo calcula al cargarla en la compra); los ítems exentos van a TExento.
/// Fechas: las de la factura, sin hora.
/// </summary>
public static class ArmadorOc
{
    /// <summary>Tolerancia entre el total de la OC y el neto de la factura antes de alertar (redondeo por línea).</summary>
    public const decimal ToleranciaTotal = 1m;

    public static ResultadoOc Armar(FacturaOc factura, ProveedorPv proveedor, IReadOnlyDictionary<string, ProductoPv> productos,
        ParametrosOc par, EscalasOc esc)
    {
        var f = factura.Factura;
        var errores = new List<ErrorDoc>();
        var alertas = new List<ErrorDoc>();
        if (factura.Items.Count == 0)
            errores.Add(new ErrorDoc(CodigosError.OcSinItems, $"La factura {f.Numero} no tiene ítems"));

        var factor = FactorDescuento(f);
        var items = new List<OcItemPv>();
        foreach (var i in factura.Items.OrderBy(i => i.Linea))
        {
            if (!productos.TryGetValue(i.CodItem, out var p))
            {
                errores.Add(new ErrorDoc(CodigosError.ProductoNoExiste, $"El producto {i.CodItem} no existe en el PV", i.CodItem));
                continue;
            }
            if (!p.Activo)
                errores.Add(new ErrorDoc(CodigosError.ProductoInactivo, $"El producto {i.CodItem} está inactivo en el PV", i.CodItem));
            if (p.EsExento != i.EsExento)
                alertas.Add(new ErrorDoc(CodigosError.ExentoDiferente,
                    $"{i.CodItem}: la factura lo trae {(i.EsExento ? "exento" : "gravado")} y el maestro del PV lo tiene {(p.EsExento ? "exento" : "gravado")}; la OC sigue la factura",
                    i.CodItem));

            var costo = R(i.Precio * factor, esc.Costo);
            items.Add(new OcItemPv(
                NroLinea: i.Linea,
                CodItem: p.CodProd,
                Refere: p.Refere,
                Descrip1: p.Descrip,
                Descrip2: p.Descrip2,
                Descrip3: p.Descrip3,
                Cantidad: i.Cantidad,
                Costo: costo,
                TotalItem: R(costo * i.Cantidad, esc.TotalItem),
                Precio1: p.Precio1,
                Precio2: p.Precio2,
                Precio3: p.Precio3,
                ExistAnt: p.Existen,
                EsExento: i.EsExento));
        }

        if (errores.Count > 0) return new ResultadoOc(null, errores, alertas);

        var tGravable = items.Where(i => !i.EsExento).Sum(i => i.TotalItem);
        var tExento = items.Where(i => i.EsExento).Sum(i => i.TotalItem);
        var monto = tGravable + tExento;

        var neto = f.Monto - f.Descto1 - f.Descto2;
        if (Math.Abs(monto - neto) > ToleranciaTotal)
            alertas.Add(new ErrorDoc(CodigosError.DiferenciaTotal,
                $"La OC suma {monto:N2} y la factura {f.Numero} neta de descuento suma {neto:N2} (diferencia {monto - neto:N2})"));
        if (f.Fletes != 0)
            alertas.Add(new ErrorDoc(CodigosError.FletesNoIncluidos,
                $"La factura {f.Numero} trae fletes por {f.Fletes:N2}; la OC no los incluye, se registran en la compra"));

        var encabezado = new OcEncabezadoPv(
            CodSucu: par.CodSucu,
            CodProv: proveedor.CodProv,
            Descrip: proveedor.Descrip,
            ID3: proveedor.ID3,
            CodUbic: par.CodUbic,
            CodUsua: par.CodUsua,
            CodEsta: par.CodEsta,
            FechaE: f.FechaE.Date,
            TGravable: tGravable,
            TExento: tExento,
            Monto: monto,
            OrdenC: f.Numero,
            Notas1: ArmadorPedido.Marca(par.NodoOrigen, "FAC", f.Numero));

        return new ResultadoOc(new OcArmada(encabezado, items), [], alertas);
    }

    /// <summary>1 − (Descto1 + Descto2) / Monto. Fábrica: 0,95 en *33570; CEDI: 1 (no da descuento).</summary>
    public static decimal FactorDescuento(FacturaEncabezado f) =>
        f.Monto == 0 ? 1m : 1m - (f.Descto1 + f.Descto2) / f.Monto;
}
