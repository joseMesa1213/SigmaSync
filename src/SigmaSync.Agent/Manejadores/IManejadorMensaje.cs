using Microsoft.Data.SqlClient;
using SigmaSync.Contracts;
using SigmaSync.Saint;

namespace SigmaSync.Agent.Manejadores;

/// <summary>Procesa un tipo de mensaje dentro de la transacción del consumidor.</summary>
public interface IManejadorMensaje
{
    string Tipo { get; }

    /// <summary>Número del documento de origen (idempotencia de negocio en sync.Inbox, máx. 20 caracteres).</summary>
    string DocNumeroOrigen(Sobre sobre);

    /// <summary>Escribe en la base. Lanza <see cref="ErrorNegocioException"/> para rechazar sin reintento.</summary>
    Task ProcesarAsync(ContextoMensaje ctx, CancellationToken ct);

    /// <summary>Respuesta al origen cuando el mensaje se rechaza (p. ej. ACK_PEDIDO RECHAZADO); null si no aplica.</summary>
    Sobre? RespuestaRechazo(Sobre sobre, string nodoId, IReadOnlyList<ErrorDoc> errores);
}

/// <summary>Error de negocio: el mensaje se rechaza, se responde al origen y no se reintenta.</summary>
public sealed class ErrorNegocioException(IReadOnlyList<ErrorDoc> errores)
    : Exception(string.Join("; ", errores.Select(e => $"{e.Codigo}: {e.Mensaje}")))
{
    public IReadOnlyList<ErrorDoc> Errores { get; } = errores;

    public ErrorNegocioException(string codigo, string mensaje, string? codItem = null)
        : this([new ErrorDoc(codigo, mensaje, codItem)]) { }
}

/// <summary>Lo que un manejador necesita: el mensaje, la conexión y la transacción, y cómo responder.</summary>
public sealed class ContextoMensaje(Sobre sobre, SqlConnection cn, SqlTransaction tx, string nodoId)
{
    private readonly List<Guid> _respuestas = [];
    private readonly List<ErrorDoc> _alertas = [];

    public Sobre Sobre { get; } = sobre;
    public SqlConnection Cn { get; } = cn;
    public SqlTransaction Tx { get; } = tx;
    public string NodoId { get; } = nodoId;
    public IReadOnlyList<Guid> Respuestas => _respuestas;
    public IReadOnlyList<ErrorDoc> Alertas => _alertas;

    /// <summary>Encola una respuesta al nodo de origen en la misma partición y la misma transacción.</summary>
    public async Task<Sobre> ResponderAsync<T>(string tipo, T payload)
    {
        var respuesta = Sobre.Crear(tipo, NodoId, Sobre.NodoOrigen, Sobre.ClaveParticion, payload);
        await RepositorioSync.EncolarAsync(Cn, Tx, respuesta);
        _respuestas.Add(respuesta.MsgId);
        return respuesta;
    }

    /// <summary>Diferencia no bloqueante: queda en sync.Log (WARN) y viaja en el ACK.</summary>
    public void Alertar(string codigo, string mensaje, string? codItem = null) => _alertas.Add(new ErrorDoc(codigo, mensaje, codItem));
}
