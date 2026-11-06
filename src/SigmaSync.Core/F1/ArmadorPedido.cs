using SigmaSync.Contracts;
using static SigmaSync.Core.Redondeo;

namespace SigmaSync.Core.F1;

public sealed record ResultadoArmado(PedidoArmado? Pedido, IReadOnlyList<ErrorDoc> Errores)
{
    public bool Ok => Pedido is not null;
}

/// <summary>
/// F1: arma el pedido E de Fábrica o CEDI a partir de la OC del PV, al precio de lista 3 del destino.
/// Reglas tomadas del pedido 035829 grabado por SAINT:
/// Precio = Precio3 a 2 decimales; PriceO = Precio3; TotalItem = Precio × Cantidad;
/// MtoTaxO = Cantidad × Precio3 × tarifa; MtoTax = MtoTaxO a la escala de su columna;
/// encabezado: MtoTax = Σ MtoTax de los ítems a 2 decimales, MtoTotal = Monto + MtoTax.
/// Del pedido 035850 grabado en pantalla (SAINT 9.0.2.6, 2026-11-04): Costo = CostPro, Refere = SAPROD.Refere
/// (el código si está vacío), ExistAnt = existencia del depósito y SaldoAct = saldo del cliente.
/// El costo de la OC no se usa: ya trae el descuento del PV.
/// </summary>
public static class ArmadorPedido
{
    public static ResultadoArmado Armar(OcPedido oc, ClienteDestino cliente, IReadOnlyDictionary<string, ProductoDestino> productos,
        ParametrosPedido par, EscalasPedido esc)
    {
        var errores = new List<ErrorDoc>();
        if (oc.Items.Count == 0)
            errores.Add(new ErrorDoc(CodigosError.OcSinItems, $"La OC {oc.Oc.Numero} no tiene ítems"));

        var items = new List<PedidoItem>();
        foreach (var i in oc.Items.OrderBy(i => i.Linea))
        {
            if (!productos.TryGetValue(i.CodItem, out var p))
            {
                errores.Add(new ErrorDoc(CodigosError.ProductoNoExiste, $"El producto {i.CodItem} no existe en el destino", i.CodItem));
                continue;
            }
            if (!p.Activo)
                errores.Add(new ErrorDoc(CodigosError.ProductoInactivo, $"El producto {i.CodItem} está inactivo en el destino", i.CodItem));
            if (p.Precio3 <= 0)
                errores.Add(new ErrorDoc(CodigosError.ProductoSinPrecio, $"El producto {i.CodItem} no tiene precio de lista 3", i.CodItem));
            if (i.Cantidad <= 0)
                errores.Add(new ErrorDoc(CodigosError.CantidadInvalida, $"Línea {i.Linea}: cantidad {i.Cantidad}", i.CodItem));

            var precio = R(p.Precio3, 2);
            var tarifa = p.Exento ? 0m : p.TarifaImpuesto;
            var impuesto = i.Cantidad * p.Precio3 * tarifa / 100m;
            items.Add(new PedidoItem(
                NroLinea: i.Linea,
                CodItem: p.CodProd,
                Refere: string.IsNullOrWhiteSpace(p.Refere) ? p.CodProd : p.Refere,
                Descrip1: p.Descrip,
                Descrip2: p.Descrip2,
                Descrip3: p.Descrip3,
                Cantidad: i.Cantidad,
                Costo: p.CostPro,
                Precio: precio,
                PriceO: R(p.Precio3, esc.PriceO),
                TotalItem: R(precio * i.Cantidad, esc.TotalItem),
                MtoTax: R(impuesto, esc.MtoTax),
                MtoTaxO: R(impuesto, esc.MtoTaxO),
                EsExento: p.Exento,
                ExistAnt: p.ExistenDepo));
        }

        if (errores.Count > 0) return new ResultadoArmado(null, errores);

        var tGravable = items.Where(i => !i.EsExento).Sum(i => i.TotalItem);
        var tExento = items.Where(i => i.EsExento).Sum(i => i.TotalItem);
        var monto = tGravable + tExento;
        var mtoTax = R(items.Sum(i => i.MtoTax), 2);
        var fechaE = new DateTime(par.Ahora.Year, par.Ahora.Month, par.Ahora.Day, par.Ahora.Hour, par.Ahora.Minute, par.Ahora.Second);

        var encabezado = new PedidoEncabezado(
            CodSucu: par.CodSucu,
            CodClie: cliente.CodClie,
            Descrip: cliente.Descrip,
            ID3: cliente.ID3,
            Direc1: cliente.Direc1,
            Direc2: cliente.Direc2,
            Telef: cliente.Telef,
            OrdenC: oc.Oc.Numero,
            CodUbic: par.CodUbic,
            CodVend: par.CodVend,
            CodOper: par.CodOper,
            CodUsua: par.CodUsua,
            CodEsta: par.CodEsta,
            FechaE: fechaE,
            FechaV: fechaE.Date.AddDays(cliente.DiasCred),
            TGravable: tGravable,
            TExento: tExento,
            Monto: monto,
            MtoTax: mtoTax,
            MtoTotal: monto + mtoTax,
            Notas1: Marca(par.NodoOrigen, "OC", oc.Oc.Numero),
            SaldoAct: cliente.Saldo);

        return new ResultadoArmado(new PedidoArmado(encabezado, items), []);
    }

    /// <summary>Marca de sincronización para Notas1 (60 caracteres): "SYNC PV_MAY OC 000003538".</summary>
    public static string Marca(string nodoOrigen, string tipo, string numero)
    {
        var marca = $"SYNC {nodoOrigen} {tipo} {numero}";
        return marca.Length <= 60 ? marca : marca[..60];
    }
}
