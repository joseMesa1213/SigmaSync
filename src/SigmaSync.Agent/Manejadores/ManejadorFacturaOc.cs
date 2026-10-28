using SigmaSync.Contracts;
using SigmaSync.Core.F3;
using SigmaSync.Saint;
using SigmaSync.Saint.F3;

namespace SigmaSync.Agent.Manejadores;

/// <summary>
/// En el PV: factura de Fábrica o CEDI. Sin pedido sincronizado (F3) crea una OC idéntica a la factura, que el
/// trabajador carga en Compras. Si algún ítem viene de un pedido sincronizado (F2), la factura se rechaza con
/// F2_NO_DISPONIBLE y queda en el hub para reprocesarla cuando F2 esté habilitado.
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

        // F2: ítems facturados desde un pedido que creó el agente (oc_origen, o pedido_origen + DocRef del PV).
        var deF2 = new List<string>();
        foreach (var i in factura.Items.Where(i => i.OcOrigen is not null || i.PedidoOrigen is not null))
        {
            var oc = i.OcOrigen ?? await RepositorioSync.OcDePedidoAsync(cn, tx, origen, i.PedidoOrigen!);
            if (oc is not null) deF2.Add($"{i.CodItem} (pedido {i.PedidoOrigen}, OC {oc})");
        }
        if (deF2.Count > 0)
            throw new ErrorNegocioException(CodigosError.F2NoDisponible,
                $"La factura {numeroFactura} viene de pedidos sincronizados: {string.Join(", ", deF2)}. Se retiene hasta habilitar F2");

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

        var consecutivo = await Consecutivos.TomarAsync(cn, tx, cfg.CodSucu, Consecutivos.PrxOrdenC,
            n => DestinoOc.OcExisteAsync(cn, tx, cfg.CodSucu, n));
        if (consecutivo.Saltados.Count > 0)
            ctx.Alertar(CodigosError.ConsecutivoColision,
                $"PrxOrdenC apuntaba a OC que ya existen ({string.Join(", ", consecutivo.Saltados)}); se usó {consecutivo.Numero}");

        await DestinoOc.InsertarAsync(cn, tx, consecutivo.Numero, armado.Oc!, cfg.ActualizarPedidoPV);
        await RepositorioSync.InsertarDocRefAsync(cn, tx, "FACTURA_OC", origen, 'A', numeroFactura, ctx.NodoId, 'L', consecutivo.Numero,
            ctx.Sobre.MsgId);
        await ctx.ResponderAsync(TiposMensaje.AckFactura, new AckFactura(numeroFactura, EstadosAck.Aplicada,
            [new AccionAck(TiposAccion.OcCreada, consecutivo.Numero)], ctx.Alertas.ToList(), []));

        log.LogInformation("Factura {Factura} de {Origen} → OC {Oc} ({Items} ítems, {Total:N2})",
            numeroFactura, origen, consecutivo.Numero, factura.Items.Count, armado.Oc!.Encabezado.Monto);
    }

    public Sobre? RespuestaRechazo(Sobre sobre, string nodoId, IReadOnlyList<ErrorDoc> errores)
    {
        var numero = sobre.Payload.TryGetProperty("factura", out var f) && f.TryGetProperty("numero", out var n) ? n.GetString() ?? "?" : "?";
        return Sobre.Crear(TiposMensaje.AckFactura, nodoId, sobre.NodoOrigen, sobre.ClaveParticion,
            new AckFactura(numero, EstadosAck.Rechazada, [], [], errores));
    }
}
