using SigmaSync.Contracts;
using SigmaSync.Saint;

namespace SigmaSync.Agent.Manejadores;

/// <summary>Responde cada PING con un PONG al nodo de origen.</summary>
public sealed class ManejadorPing(ILogger<ManejadorPing> log) : IManejadorMensaje
{
    public string Tipo => TiposMensaje.Ping;

    public string DocNumeroOrigen(Sobre sobre) => sobre.MsgId.ToString("N")[..20];

    public async Task ProcesarAsync(ContextoMensaje ctx, CancellationToken ct)
    {
        var ping = ctx.Sobre.Leer<Ping>();
        await ctx.ResponderAsync(TiposMensaje.Pong, new Pong(ctx.Sobre.MsgId, ping.EnviadoTs, DateTimeOffset.Now));
        log.LogInformation("PING de {Origen} ({Texto}); PONG encolado", ctx.Sobre.NodoOrigen, ping.Texto);
    }

    public Sobre? RespuestaRechazo(Sobre sobre, string nodoId, IReadOnlyList<ErrorDoc> errores) => null;
}

/// <summary>Registra la llegada del PONG y el tiempo de ida y vuelta.</summary>
public sealed class ManejadorPong(ILogger<ManejadorPong> log) : IManejadorMensaje
{
    public string Tipo => TiposMensaje.Pong;

    public string DocNumeroOrigen(Sobre sobre) => sobre.MsgId.ToString("N")[..20];

    public async Task ProcesarAsync(ContextoMensaje ctx, CancellationToken ct)
    {
        var pong = ctx.Sobre.Leer<Pong>();
        var idaYVuelta = DateTimeOffset.Now - pong.PingEnviadoTs;
        var texto = $"PONG de {ctx.Sobre.NodoOrigen} para el PING {pong.PingMsgId}: ida y vuelta {idaYVuelta.TotalSeconds:N1} s";
        await RepositorioSync.RegistrarLogAsync(ctx.Cn, ctx.Tx, "INFO", "Ping", ctx.Sobre.MsgId, texto);
        log.LogInformation("{Texto}", texto);
    }

    public Sobre? RespuestaRechazo(Sobre sobre, string nodoId, IReadOnlyList<ErrorDoc> errores) => null;
}
