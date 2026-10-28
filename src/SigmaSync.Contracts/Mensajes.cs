namespace SigmaSync.Contracts;

public static class TiposMensaje
{
    public const string OcPedido = "OC_PEDIDO";
    public const string AckPedido = "ACK_PEDIDO";
    public const string FacturaOc = "FACTURA_OC";
    public const string AckFactura = "ACK_FACTURA";

    /// <summary>Diagnóstico de conectividad entre dos nodos (ida y vuelta por el hub).</summary>
    public const string Ping = "PING";
    public const string Pong = "PONG";
}

public static class EstadosAck
{
    public const string Creado = "CREADO";
    public const string Rechazado = "RECHAZADO";
    public const string Aplicada = "APLICADA";
    public const string Rechazada = "RECHAZADA";
}

public static class TiposAccion
{
    public const string OcCreada = "OC_CREADA";
    public const string OcActualizada = "OC_ACTUALIZADA";
}

public static class CodigosError
{
    public const string ProductoNoExiste = "PRODUCTO_NO_EXISTE";
    public const string ProductoInactivo = "PRODUCTO_INACTIVO";
    public const string ProductoSinPrecio = "PRODUCTO_SIN_PRECIO";
    public const string CantidadInvalida = "CANTIDAD_INVALIDA";
    public const string OcSinItems = "OC_SIN_ITEMS";
    public const string ConfiguracionIncompleta = "CONFIGURACION_INCOMPLETA";
    public const string ProveedorSinMapeo = "PROVEEDOR_SIN_MAPEO";

    /// <summary>La factura viene de un pedido sincronizado (F2); se retiene hasta que F2 esté habilitado.</summary>
    public const string F2NoDisponible = "F2_NO_DISPONIBLE";

    // Alertas (no bloquean)
    public const string ExentoDiferente = "EXENTO_DIFERENTE";
    public const string DiferenciaTotal = "DIFERENCIA_TOTAL";
    public const string FletesNoIncluidos = "FLETES_NO_INCLUIDOS";
    public const string ClienteSinMapeo = "CLIENTE_SIN_MAPEO";
    public const string DepositoNoExiste = "DEPOSITO_NO_EXISTE";
    public const string OcNoEncontrada = "OC_NO_ENCONTRADA";
    public const string ConsecutivoColision = "CONSECUTIVO_COLISION";
    public const string TipoNoSoportado = "TIPO_NO_SOPORTADO";
    public const string VersionNoSoportada = "VERSION_NO_SOPORTADA";
}

/// <summary>Error o alerta de negocio que viaja en los ACK.</summary>
public sealed record ErrorDoc(string Codigo, string Mensaje, string? CodItem = null);

// ── OC_PEDIDO (F1: PV → Fábrica o CEDI) ──────────────────────────────────────

public sealed record OcPedido(OcEncabezado Oc, IReadOnlyList<OcItem> Items);

public sealed record OcEncabezado(
    string Numero,
    string CodSucu,
    string CodProv,          // v1.1
    DateOnly FechaE,
    DateTimeOffset FechaT,
    int NroUnico);

public sealed record OcItem(int Linea, string CodItem, decimal Cantidad, decimal CostoOc);

// ── ACK_PEDIDO (F1: Fábrica o CEDI → PV) ─────────────────────────────────────

public sealed record AckPedido(string OcNumero, string Estado, string? PedidoNumero, IReadOnlyList<ErrorDoc> Errores);

// ── FACTURA_OC (F2/F3: Fábrica o CEDI → PV) ──────────────────────────────────

public sealed record FacturaOc(
    FacturaEncabezado Factura,
    IReadOnlyList<FacturaItem> Items,
    IReadOnlyList<FacturaImpuesto> Impuestos);

public sealed record FacturaEncabezado(
    string Numero,
    string TipoFac,
    string CodClie,          // v1.1
    DateTimeOffset FechaE,
    decimal Monto,
    decimal Descto1,
    decimal Descto2,
    decimal Fletes,
    decimal TGravable,
    decimal TExento,
    decimal MtoTax,
    decimal MtoTotal);

public sealed record FacturaItem(
    int Linea,
    string CodItem,
    decimal Cantidad,
    decimal Precio,
    decimal PrecioO,
    decimal TotalItem,
    bool EsExento,
    string? PedidoOrigen,
    string? OcOrigen);       // v1.1

public sealed record FacturaImpuesto(string CodTaxs, decimal Tarifa, decimal Base, decimal Monto);

// ── ACK_FACTURA (PV → Fábrica o CEDI) ────────────────────────────────────────

public sealed record AckFactura(
    string FacturaNumero,
    string Estado,
    IReadOnlyList<AccionAck> Acciones,
    IReadOnlyList<ErrorDoc> Alertas,
    IReadOnlyList<ErrorDoc> Errores);

public sealed record AccionAck(string Tipo, string OcNumero);

// ── PING / PONG ──────────────────────────────────────────────────────────────

public sealed record Ping(string Texto, DateTimeOffset EnviadoTs);

public sealed record Pong(Guid PingMsgId, DateTimeOffset PingEnviadoTs, DateTimeOffset RecibidoTs);
