using SigmaSync.Contracts;
using SigmaSync.Core.F1;
using SigmaSync.Core.F2;
using SigmaSync.Core.F3;
using SigmaSync.Saint;
using SigmaSync.Saint.F3;

namespace SigmaSync.Agent.Manejadores;

/// <summary>
/// En el PV: factura de Fábrica o CEDI. Siempre crea una OC nueva idéntica a la factura, que el trabajador carga en
/// Compras buscándola por el número de factura (F3).
/// Si algún ítem viene de un pedido sincronizado (F2, decisión D5), además le resta lo facturado a la OC original del PV,
/// como hace SAINT al recibir una OC en partes: la línea cumplida se borra, la parcial queda con el saldo y, si no queda
/// nada, la OC se borra. La OC original conserva su número y sigue mostrando lo que falta por facturar.
/// </summary>
public sealed class ManejadorFacturaOc(ILogger<ManejadorFacturaOc> log) : IManejadorMensaje
{
    public string Tipo => TiposMensaje.FacturaOc;

    public string DocNumeroOrigen(Sobre sobre) => sobre.Leer<FacturaOc>().Factura.Numero;

    public async Task ProcesarAsync(ContextoMensaje ctx, CancellationToken ct)
    {
        var (cn, tx, origen) = (ctx.Cn, ctx.Tx, ctx.Sobre.NodoOrigen);
        var factura = ctx.Sobre.Leer<FacturaOc>();
        var numeroFactura = factura.Factura.Numero;
        var cfg = ConfigNodo.Desde(await RepositorioSync.LeerConfigAsync(cn, tx));

        // F2: OC original de cada ítem facturado desde un pedido que creó el agente (oc_origen, o pedido_origen + DocRef del PV).
        var porOc = new Dictionary<string, List<(string CodItem, decimal Cantidad)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in factura.Items.Where(i => i.OcOrigen is not null || i.PedidoOrigen is not null))
        {
            var oc = i.OcOrigen ?? await RepositorioSync.OcDePedidoAsync(cn, tx, origen, i.PedidoOrigen!);
            if (oc is null) continue;   // pedido hecho a mano en Fábrica o CEDI: el ítem solo va en la OC nueva (F3)
            if (!porOc.TryGetValue(oc, out var lista)) porOc[oc] = lista = [];
            lista.Add((i.CodItem, i.Cantidad));
        }

        if (cfg.CodUbic is null)
            throw new ErrorNegocioException(CodigosError.ConfiguracionIncompleta, $"sync.Config de {cfg.NodoId} no tiene CodUbic (depósito del PV)");
        if (!await Saint.F1.DestinoPedido.DepositoExisteAsync(cn, tx, cfg.CodUbic))
            throw new ErrorNegocioException(CodigosError.DepositoNoExiste, $"El depósito {cfg.CodUbic} no existe en {cfg.NodoId}");

        var codProv = await RepositorioSync.CodTerceroDeNodoAsync(cn, tx, "PROVEEDOR", origen)
            ?? throw new ErrorNegocioException(CodigosError.ProveedorSinMapeo, $"sync.NodoTercero no tiene proveedor para el nodo {origen}");
        var proveedor = await DestinoOc.ProveedorAsync(cn, tx, codProv)
            ?? throw new ErrorNegocioException(CodigosError.ProveedorSinMapeo, $"El proveedor {codProv} de {origen} no existe en SAPROV");

        var productos = await DestinoOc.ProductosAsync(cn, tx, factura.Items.Select(i => i.CodItem), cfg.CodUbic);
        var escalas = await DestinoOc.EscalasAsync(cn, tx);
        var armado = ArmadorOc.Armar(factura, proveedor, productos,
            new ParametrosOc(cfg.CodSucu, cfg.CodUbic, cfg.CodUsua, cfg.CodEsta, origen), escalas);
        if (!armado.Ok) throw new ErrorNegocioException(armado.Errores);
        foreach (var a in armado.Alertas) ctx.Alertar(a.Codigo, a.Mensaje, a.CodItem);

        var ocNueva = armado.Oc!;
        if (porOc.Count > 0)   // la marca dice de qué OC original viene: "SYNC FAB FAC *33647 OC 000003538"
            ocNueva = ocNueva with
            {
                Encabezado = ocNueva.Encabezado with
                {
                    Notas1 = ArmadorPedido.Marca(origen, "FAC", $"{numeroFactura} OC {string.Join(",", porOc.Keys)}"),
                },
            };

        var consecutivo = await Consecutivos.TomarAsync(cn, tx, cfg.CodSucu, Consecutivos.PrxOrdenC,
            n => DestinoOc.OcExisteAsync(cn, tx, cfg.CodSucu, n));
        if (consecutivo.Saltados.Count > 0)
            ctx.Alertar(CodigosError.ConsecutivoColision,
                $"PrxOrdenC apuntaba a OC que ya existen ({string.Join(", ", consecutivo.Saltados)}); se usó {consecutivo.Numero}");

        await DestinoOc.InsertarAsync(cn, tx, consecutivo.Numero, ocNueva, cfg.ActualizarPedidoPV);
        await RepositorioSync.InsertarDocRefAsync(cn, tx, "FACTURA_OC", origen, 'A', numeroFactura, ctx.NodoId, 'L', consecutivo.Numero,
            ctx.Sobre.MsgId);
        var acciones = new List<AccionAck> { new(TiposAccion.OcCreada, consecutivo.Numero) };

        // F2 (D5): restar lo facturado a cada OC original.
        foreach (var (ocOriginal, facturado) in porOc)
        {
            var lineas = await DestinoOc.LineasOcAsync(cn, tx, cfg.CodSucu, ocOriginal);
            if (lineas is null)
            {
                ctx.Alertar(CodigosError.OcNoEncontrada,
                    $"La OC {ocOriginal} ya no existe en el PV (¿se cargó en una compra antes de llegar la factura {numeroFactura}?). " +
                    $"Se creó la OC {consecutivo.Numero}: revisar que la mercancía no se reciba dos veces");
                continue;
            }

            var saldo = SaldoOc.Restar(ocOriginal, lineas, facturado, escalas.TotalItem);
            foreach (var a in saldo.Alertas) ctx.Alertar(a.Codigo, a.Mensaje, a.CodItem);
            if (saldo.Cambios.Count == 0) continue;

            await DestinoOc.AplicarSaldoAsync(cn, tx, cfg.CodSucu, ocOriginal, saldo, cfg.ActualizarPedidoPV);
            acciones.Add(new AccionAck(saldo.Cerrada ? TiposAccion.OcCerrada : TiposAccion.OcActualizada, ocOriginal));
            log.LogInformation("Factura {Factura}: OC original {Oc} {Estado}", numeroFactura, ocOriginal,
                saldo.Cerrada ? "cerrada" : $"con saldo de {saldo.LineasQueQuedan} líneas ({saldo.Monto:N2})");
        }

        await ctx.ResponderAsync(TiposMensaje.AckFactura, new AckFactura(numeroFactura, EstadosAck.Aplicada, acciones, ctx.Alertas.ToList(), []));

        log.LogInformation("Factura {Factura} de {Origen} → OC {Oc} ({Items} ítems, {Total:N2}){F2}",
            numeroFactura, origen, consecutivo.Numero, factura.Items.Count, ocNueva.Encabezado.Monto,
            porOc.Count > 0 ? $"; F2 sobre {string.Join(", ", porOc.Keys)}" : "");
    }

    public Sobre? RespuestaRechazo(Sobre sobre, string nodoId, IReadOnlyList<ErrorDoc> errores)
    {
        var numero = sobre.Payload.TryGetProperty("factura", out var f) && f.TryGetProperty("numero", out var n) ? n.GetString() ?? "?" : "?";
        return Sobre.Crear(TiposMensaje.AckFactura, nodoId, sobre.NodoOrigen, sobre.ClaveParticion,
            new AckFactura(numero, EstadosAck.Rechazada, [], [], errores));
    }
}
