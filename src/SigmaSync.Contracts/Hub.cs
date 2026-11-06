namespace SigmaSync.Contracts;

/// <summary>DTOs de la API del SyncHub (/v1).</summary>
public sealed record PushRespuesta(bool Insertado);

public sealed record PullRespuesta(IReadOnlyList<Sobre> Mensajes);

public sealed record AckSolicitud(string Resultado, string? Error = null);

public static class ResultadosAck
{
    public const string Ok = "ok";
    public const string Reintentar = "reintentar";
    public const string Rechazado = "rechazado";

    public static bool EsValido(string r) => r is Ok or Reintentar or Rechazado;
}

public sealed record LatidoSolicitud(
    string VersionAgente,
    int OutboxPendientes,
    int OutboxConError,
    string? UltimoError,
    DateTimeOffset Ts,
    IReadOnlyList<AlertaNodo>? Alertas = null);

/// <summary>Advertencia o error de sync.Log del nodo, que viaja con el latido para centralizarse en el hub. Ts en UTC.</summary>
public sealed record AlertaNodo(long LogId, DateTime Ts, string Nivel, string Componente, Guid? MsgId, string Mensaje);

public sealed record AlertaHub(
    long Id,
    DateTime Ts,
    string NodoId,
    string Fuente,
    string Nivel,
    string Mensaje,
    Guid? MsgId,
    bool Notificada);

public sealed record MensajeDetalle(MensajeAdmin Mensaje, Sobre Sobre);

public sealed record MensajeAdmin(
    Guid MsgId,
    string Tipo,
    string NodoOrigen,
    string NodoDestino,
    string ClaveParticion,
    string Estado,
    int Intentos,
    DateTime CreadoTs,
    DateTime? ConfirmadoTs,
    string? Error);
