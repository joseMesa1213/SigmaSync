using Dapper;
using Microsoft.Data.SqlClient;
using SigmaSync.Agent.Manejadores;
using SigmaSync.Contracts;
using SigmaSync.Saint;

namespace SigmaSync.Integration.Tests;

[Collection(ColeccionLocalDb.Nombre)]
public sealed class AgenteTests(Entorno e) : IAsyncLifetime
{
    public async Task InitializeAsync() => await e.LimpiarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task EncolarAsync(string nodo, Sobre sobre, DocOrigen? doc = null)
    {
        await using var cn = new SqlConnection(e.CsDe(nodo));
        await cn.OpenAsync();
        await RepositorioSync.EncolarAsync(cn, null, sobre, doc);
    }

    private static Sobre PingDe(string origen, string destino) =>
        Sobre.Crear(TiposMensaje.Ping, origen, destino, Sobre.Clave(origen, "PING", Guid.NewGuid().ToString("N")[..12]),
            new Ping("prueba", DateTimeOffset.Now));

    [Fact]
    public async Task Ping_va_y_vuelve_entre_dos_nodos_por_el_hub()
    {
        var ping = PingDe(Entorno.NodoA, Entorno.NodoB);
        await EncolarAsync(Entorno.NodoA, ping);

        await e.Publicador(Entorno.NodoA).EjecutarAsync(default);   // A → hub
        await e.Consumidor(Entorno.NodoB).EjecutarAsync(default);   // hub → B: procesa PING y encola PONG
        await e.Publicador(Entorno.NodoB).EjecutarAsync(default);   // B → hub
        await e.Consumidor(Entorno.NodoA).EjecutarAsync(default);   // hub → A: procesa PONG

        Assert.Equal("ENVIADO", await e.ConsultarAsync<string>(e.CsA, "SELECT estado FROM sync.Outbox WHERE msg_id = @MsgId", ping));
        Assert.Equal("PROCESADO", await e.ConsultarAsync<string>(e.CsB, "SELECT estado FROM sync.Inbox WHERE msg_id = @MsgId", ping));
        Assert.Equal(1, await e.ConsultarAsync<int>(e.CsA, "SELECT COUNT(*) FROM sync.Inbox WHERE tipo = 'PONG' AND estado = 'PROCESADO'"));
        Assert.Contains("ida y vuelta", await e.ConsultarAsync<string>(e.CsA, "SELECT mensaje FROM sync.Log WHERE componente = 'Ping'"));
        Assert.Equal(0, await e.ConsultarAsync<int>(e.CsHub, "SELECT COUNT(*) FROM hub.Mensaje WHERE estado <> 'CONFIRMADO'"));
        Assert.Equal(2, await e.ConsultarAsync<int>(e.CsHub, "SELECT COUNT(*) FROM hub.Mensaje"));
    }

    [Fact]
    public async Task Un_mensaje_reentregado_no_se_procesa_dos_veces()
    {
        var ping = PingDe(Entorno.NodoA, Entorno.NodoB);
        var consumidor = e.Consumidor(Entorno.NodoB);

        var primero = await consumidor.ProcesarAsync(ping, default);
        var segundo = await consumidor.ProcesarAsync(ping, default);   // p. ej. el ack no llegó al hub

        Assert.Equal((ResultadosAck.Ok, (string?)null), primero);
        Assert.Equal((ResultadosAck.Ok, (string?)null), segundo);
        Assert.Equal(1, await e.ConsultarAsync<int>(e.CsB, "SELECT COUNT(*) FROM sync.Outbox WHERE tipo = 'PONG'"));
    }

    [Fact]
    public async Task Tipo_desconocido_se_rechaza_sin_reintento()
    {
        var raro = Sobre.Crear("TIPO_RARO", Entorno.NodoA, Entorno.NodoB, "NODO_A:X:1", new { dato = 1 });

        var (resultado, error) = await e.Consumidor(Entorno.NodoB).ProcesarAsync(raro, default);

        Assert.Equal(ResultadosAck.Rechazado, resultado);
        Assert.Contains(CodigosError.TipoNoSoportado, error);
        Assert.Equal("RECHAZADO", await e.ConsultarAsync<string>(e.CsB, "SELECT estado FROM sync.Inbox WHERE msg_id = @MsgId", raro));
    }

    [Fact]
    public async Task Error_de_negocio_hace_rollback_responde_al_origen_y_permite_reproceso()
    {
        var falla = new ManejadorDePrueba(fallar: true);
        var sobre = Sobre.Crear(ManejadorDePrueba.TipoPrueba, Entorno.NodoA, Entorno.NodoB, "NODO_A:L:000777", new { numero = "000777" });

        var (resultado, _) = await e.Consumidor(Entorno.NodoB, falla).ProcesarAsync(sobre, default);

        Assert.Equal(ResultadosAck.Rechazado, resultado);
        Assert.Equal(0, await e.ConsultarAsync<int>(e.CsB, "SELECT COUNT(*) FROM sync.DocRef"));   // la escritura se deshizo
        Assert.Equal(1, await e.ConsultarAsync<int>(e.CsB, "SELECT COUNT(*) FROM sync.Outbox WHERE tipo = 'ACK_PRUEBA'"));

        // Corregido el problema (p. ej. se creó el producto), el reproceso del mismo msg_id se aplica.
        var (reproceso, _) = await e.Consumidor(Entorno.NodoB, new ManejadorDePrueba(fallar: false)).ProcesarAsync(sobre, default);
        Assert.Equal(ResultadosAck.Ok, reproceso);
        Assert.Equal(1, await e.ConsultarAsync<int>(e.CsB, "SELECT COUNT(*) FROM sync.DocRef"));
        Assert.Equal("PROCESADO", await e.ConsultarAsync<string>(e.CsB, "SELECT estado FROM sync.Inbox WHERE msg_id = @MsgId", sobre));
    }

    [Fact]
    public async Task Mismo_documento_con_otro_msg_id_no_se_duplica()
    {
        var m1 = Sobre.Crear(ManejadorDePrueba.TipoPrueba, Entorno.NodoA, Entorno.NodoB, "NODO_A:L:000888", new { numero = "000888" });
        var m2 = Sobre.Crear(ManejadorDePrueba.TipoPrueba, Entorno.NodoA, Entorno.NodoB, "NODO_A:L:000888", new { numero = "000888" });
        var consumidor = e.Consumidor(Entorno.NodoB, new ManejadorDePrueba(fallar: false));

        await consumidor.ProcesarAsync(m1, default);
        var (resultado, _) = await consumidor.ProcesarAsync(m2, default);

        Assert.Equal(ResultadosAck.Ok, resultado);
        Assert.Equal(1, await e.ConsultarAsync<int>(e.CsB, "SELECT COUNT(*) FROM sync.DocRef"));
    }

    [Fact]
    public async Task Outbox_no_duplica_la_captura_del_mismo_documento()
    {
        var doc = new DocOrigen('L', "000003538", 18047);
        await using var cn = new SqlConnection(e.CsA);
        await cn.OpenAsync();

        var primera = await RepositorioSync.EncolarAsync(cn, null, PingDe(Entorno.NodoA, Entorno.NodoB), doc);
        var segunda = await RepositorioSync.EncolarAsync(cn, null, PingDe(Entorno.NodoA, Entorno.NodoB), doc);

        Assert.True(primera);
        Assert.False(segunda);
    }

    [Fact]
    public async Task Publicador_aparta_el_mensaje_que_el_hub_rechaza_y_sigue_con_el_resto()
    {
        var malo = Sobre.Crear(TiposMensaje.Ping, Entorno.NodoA, "NO_EXISTE", "NODO_A:PING:x", new Ping("x", DateTimeOffset.Now));
        var bueno = PingDe(Entorno.NodoA, Entorno.NodoB);
        await EncolarAsync(Entorno.NodoA, malo);
        await EncolarAsync(Entorno.NodoA, bueno);

        await e.Publicador(Entorno.NodoA).EjecutarAsync(default);

        Assert.Equal("ERROR", await e.ConsultarAsync<string>(e.CsA, "SELECT estado FROM sync.Outbox WHERE msg_id = @MsgId", malo));
        Assert.Equal("ENVIADO", await e.ConsultarAsync<string>(e.CsA, "SELECT estado FROM sync.Outbox WHERE msg_id = @MsgId", bueno));
    }

    [Fact]
    public async Task Validador_detecta_nodo_equivocado_y_columnas_SAINT_faltantes()
    {
        await using var cn = new SqlConnection(e.CsA);
        await cn.OpenAsync();

        Assert.Empty(await ValidadorEsquema.ValidarAsync(cn, Entorno.NodoA));
        var problemas = await ValidadorEsquema.ValidarAsync(cn, "OTRO", [("SAFACT", "FechaE")]);
        Assert.Contains(problemas, p => p.Contains("no coincide"));
        Assert.Contains(problemas, p => p.Contains("SAFACT.FechaE"));
    }

    /// <summary>Simula un manejador de negocio: escribe en sync.DocRef y puede fallar con un error de negocio.</summary>
    private sealed class ManejadorDePrueba(bool fallar) : IManejadorMensaje
    {
        public const string TipoPrueba = "PRUEBA";

        public string Tipo => TipoPrueba;

        public string DocNumeroOrigen(Sobre sobre) => sobre.Payload.GetProperty("numero").GetString()!;

        public async Task ProcesarAsync(ContextoMensaje ctx, CancellationToken ct)
        {
            await ctx.Cn.ExecuteAsync("""
                INSERT sync.DocRef (relacion, nodo_origen, tipo_origen, num_origen, nodo_destino, tipo_destino, num_destino, msg_id)
                VALUES ('OC_PEDIDO', @NodoOrigen, 'L', 'x', @NodoDestino, 'E', 'y', @MsgId)
                """, ctx.Sobre, ctx.Tx);
            if (fallar) throw new ErrorNegocioException(CodigosError.ProductoNoExiste, "El producto X no existe", "X");
        }

        public Sobre? RespuestaRechazo(Sobre sobre, string nodoId, IReadOnlyList<ErrorDoc> errores) =>
            Sobre.Crear("ACK_PRUEBA", nodoId, sobre.NodoOrigen, sobre.ClaveParticion, new { errores });
    }
}
