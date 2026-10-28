using SigmaSync.Contracts;
using SigmaSync.Core.F1;
using SigmaSync.Saint;
using SigmaSync.Saint.F1;

namespace SigmaSync.Agent.Manejadores;

/// <summary>
/// F1 en Fábrica o CEDI: convierte la OC del PV en un pedido E al precio de lista 3, en una transacción
/// (consecutivo, SAFACT, SAITEMFAC, comprometido, DocRef) y responde ACK_PEDIDO al PV.
/// </summary>
public sealed class ManejadorOcPedido(ILogger<ManejadorOcPedido> log) : IManejadorMensaje
{
    public string Tipo => TiposMensaje.OcPedido;

    public string DocNumeroOrigen(Sobre sobre) => sobre.Leer<OcPedido>().Oc.Numero;

    public async Task ProcesarAsync(ContextoMensaje ctx, CancellationToken ct)
    {
        var (cn, tx) = (ctx.Cn, ctx.Tx);
        var oc = ctx.Sobre.Leer<OcPedido>();
        var cfg = ConfigNodo.Desde(await RepositorioSync.LeerConfigAsync(cn, tx));

        if (cfg.CodUbic is null)
            throw new ErrorNegocioException(CodigosError.ConfiguracionIncompleta, $"sync.Config de {cfg.NodoId} no tiene CodUbic (depósito de despacho)");
        if (!await DestinoPedido.DepositoExisteAsync(cn, tx, cfg.CodUbic))
            throw new ErrorNegocioException(CodigosError.DepositoNoExiste, $"El depósito {cfg.CodUbic} no existe en {cfg.NodoId}");

        var codClie = await RepositorioSync.CodTerceroDeNodoAsync(cn, tx, "CLIENTE", ctx.Sobre.NodoOrigen)
            ?? throw new ErrorNegocioException(CodigosError.ClienteSinMapeo, $"sync.NodoTercero no tiene cliente para el nodo {ctx.Sobre.NodoOrigen}");
        var cliente = await DestinoPedido.ClienteAsync(cn, tx, codClie)
            ?? throw new ErrorNegocioException(CodigosError.ClienteSinMapeo, $"El cliente {codClie} de {ctx.Sobre.NodoOrigen} no existe en SACLIE");

        var productos = await DestinoPedido.ProductosAsync(cn, tx, oc.Items.Select(i => i.CodItem));
        var escalas = await DestinoPedido.EscalasAsync(cn, tx);
        var parametros = new ParametrosPedido(cfg.CodSucu, cfg.CodUbic, cfg.CodVend, cfg.CodOper, cfg.CodUsua, cfg.CodEsta,
            ctx.Sobre.NodoOrigen, DateTime.Now);

        var armado = ArmadorPedido.Armar(oc, cliente, productos, parametros, escalas);
        if (!armado.Ok) throw new ErrorNegocioException(armado.Errores);

        var consecutivo = await Consecutivos.TomarAsync(cn, tx, cfg.CodSucu, Consecutivos.PrxPedido,
            n => DestinoPedido.PedidoExisteAsync(cn, tx, cfg.CodSucu, n));
        if (consecutivo.Saltados.Count > 0)
            ctx.Alertar(CodigosError.ConsecutivoColision,
                $"PrxPedido apuntaba a pedidos que ya existen ({string.Join(", ", consecutivo.Saltados)}); se usó {consecutivo.Numero}");

        await DestinoPedido.InsertarAsync(cn, tx, consecutivo.Numero, armado.Pedido!);
        await RepositorioSync.InsertarDocRefAsync(cn, tx, "OC_PEDIDO",
            ctx.Sobre.NodoOrigen, 'L', oc.Oc.Numero, ctx.NodoId, 'E', consecutivo.Numero, ctx.Sobre.MsgId);
        await ctx.ResponderAsync(TiposMensaje.AckPedido, new AckPedido(oc.Oc.Numero, EstadosAck.Creado, consecutivo.Numero, []));

        log.LogInformation("OC {Oc} de {Origen} → pedido {Pedido} ({Items} ítems, total {Total:N2})",
            oc.Oc.Numero, ctx.Sobre.NodoOrigen, consecutivo.Numero, oc.Items.Count, armado.Pedido!.Encabezado.MtoTotal);
    }

    public Sobre? RespuestaRechazo(Sobre sobre, string nodoId, IReadOnlyList<ErrorDoc> errores)
    {
        var numeroOc = sobre.Payload.TryGetProperty("oc", out var o) && o.TryGetProperty("numero", out var n) ? n.GetString() ?? "?" : "?";
        return Sobre.Crear(TiposMensaje.AckPedido, nodoId, sobre.NodoOrigen, sobre.ClaveParticion,
            new AckPedido(numeroOc, EstadosAck.Rechazado, null, errores));
    }
}
