using System.Net;
using System.Net.Http.Json;
using System.Text;
using SigmaSync.Agent;
using SigmaSync.Contracts;

namespace SigmaSync.Integration.Tests;

[Collection(ColeccionLocalDb.Nombre)]
public sealed class HubTests(Entorno e) : IAsyncLifetime
{
    public async Task InitializeAsync() => await e.LimpiarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Sobre Ping(string origen, string destino, string clave) =>
        Sobre.Crear(TiposMensaje.Ping, origen, destino, clave, new Ping("prueba", DateTimeOffset.Now));

    private async Task<string> EstadoAsync(Guid msgId) =>
        await e.ConsultarAsync<string>(e.CsHub, "SELECT estado FROM hub.Mensaje WHERE msg_id = @msgId", new { msgId });

    [Fact]
    public async Task Push_es_idempotente_por_msg_id()
    {
        var hub = e.ClienteHub(Entorno.NodoA);
        var sobre = Ping(Entorno.NodoA, Entorno.NodoB, "NODO_A:PING:1");

        Assert.True(await hub.PushAsync(sobre.ToJson(), default));
        Assert.False(await hub.PushAsync(sobre.ToJson(), default));
        Assert.Equal(1, await e.ConsultarAsync<int>(e.CsHub, "SELECT COUNT(*) FROM hub.Mensaje"));
    }

    [Fact]
    public async Task Pull_entrega_de_a_un_mensaje_por_particion_y_en_orden()
    {
        var hubA = e.ClienteHub(Entorno.NodoA);
        var hubB = e.ClienteHub(Entorno.NodoB);
        var primero = Ping(Entorno.NodoA, Entorno.NodoB, "NODO_A:L:000001");
        var segundo = Ping(Entorno.NodoA, Entorno.NodoB, "NODO_A:L:000001");
        var otraParticion = Ping(Entorno.NodoA, Entorno.NodoB, "NODO_A:L:000002");
        foreach (var s in new[] { primero, segundo, otraParticion }) await hubA.PushAsync(s.ToJson(), default);

        var lote1 = await hubB.PullAsync(10, default);
        Assert.Equal([primero.MsgId, otraParticion.MsgId], lote1.Select(m => m.MsgId));

        Assert.Empty(await hubB.PullAsync(10, default));   // segundo espera a primero (y el lease bloquea reentregas)

        await hubB.AckAsync(primero.MsgId, ResultadosAck.Ok, null, default);
        var lote2 = await hubB.PullAsync(10, default);
        Assert.Equal([segundo.MsgId], lote2.Select(m => m.MsgId));
    }

    [Fact]
    public async Task Reintentar_agota_intentos_pasa_a_DLQ_bloquea_la_particion_y_se_reprocesa()
    {
        var hubA = e.ClienteHub(Entorno.NodoA);
        var hubB = e.ClienteHub(Entorno.NodoB);
        var fallido = Ping(Entorno.NodoA, Entorno.NodoB, "NODO_A:L:000009");
        var siguiente = Ping(Entorno.NodoA, Entorno.NodoB, "NODO_A:L:000009");
        await hubA.PushAsync(fallido.ToJson(), default);
        await hubA.PushAsync(siguiente.ToJson(), default);

        // MaxIntentos = 2 y backoff 0 s en el entorno de pruebas.
        Assert.Single(await hubB.PullAsync(10, default));
        await hubB.AckAsync(fallido.MsgId, ResultadosAck.Reintentar, "deadlock", default);
        Assert.Equal("PENDIENTE", await EstadoAsync(fallido.MsgId));

        Assert.Equal(fallido.MsgId, (await hubB.PullAsync(10, default)).Single().MsgId);
        await hubB.AckAsync(fallido.MsgId, ResultadosAck.Reintentar, "deadlock", default);
        Assert.Equal("DLQ", await EstadoAsync(fallido.MsgId));

        Assert.Empty(await hubB.PullAsync(10, default));   // la partición queda detenida detrás del DLQ

        using var admin = e.ClienteAdmin();
        var r = await admin.PostAsync($"v1/admin/mensajes/{fallido.MsgId}/reprocesar", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(fallido.MsgId, (await hubB.PullAsync(10, default)).Single().MsgId);
    }

    [Fact]
    public async Task Rechazado_no_detiene_la_particion()
    {
        var hubA = e.ClienteHub(Entorno.NodoA);
        var hubB = e.ClienteHub(Entorno.NodoB);
        var rechazado = Ping(Entorno.NodoA, Entorno.NodoB, "NODO_A:L:000010");
        var siguiente = Ping(Entorno.NodoA, Entorno.NodoB, "NODO_A:L:000010");
        await hubA.PushAsync(rechazado.ToJson(), default);
        await hubA.PushAsync(siguiente.ToJson(), default);

        await hubB.PullAsync(10, default);
        await hubB.AckAsync(rechazado.MsgId, ResultadosAck.Rechazado, "PRODUCTO_NO_EXISTE", default);

        Assert.Equal("RECHAZADO", await EstadoAsync(rechazado.MsgId));
        Assert.Equal(siguiente.MsgId, (await hubB.PullAsync(10, default)).Single().MsgId);
    }

    [Fact]
    public async Task Un_nodo_no_puede_confirmar_mensajes_de_otro()
    {
        var sobre = Ping(Entorno.NodoA, Entorno.NodoB, "NODO_A:PING:2");
        await e.ClienteHub(Entorno.NodoA).PushAsync(sobre.ToJson(), default);
        await e.ClienteHub(Entorno.NodoB).PullAsync(10, default);

        var ex = await Assert.ThrowsAsync<HubException>(() =>
            e.ClienteHub(Entorno.NodoA).AckAsync(sobre.MsgId, ResultadosAck.Ok, null, default));
        Assert.Equal(HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task Rechaza_firma_o_api_key_invalidas()
    {
        var cuerpo = Ping(Entorno.NodoA, Entorno.NodoB, "NODO_A:PING:3").ToJson();

        using var otroSecreto = e.ClienteHttp(Entorno.NodoA, secreto: new byte[32]);
        var r1 = await otroSecreto.PostAsync("v1/mensajes", new StringContent(cuerpo, Encoding.UTF8, "application/json"));
        using var otraClave = e.ClienteHttp(Entorno.NodoA, apiKey: "falsa");
        var r2 = await otraClave.PostAsync("v1/mensajes", new StringContent(cuerpo, Encoding.UTF8, "application/json"));
        using var sinFirma = e.Hub.CreateClient();
        var r3 = await sinFirma.PostAsync("v1/mensajes/pull", null);

        Assert.All(new[] { r1, r2, r3 }, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.Equal(0, await e.ConsultarAsync<int>(e.CsHub, "SELECT COUNT(*) FROM hub.Mensaje"));
    }

    [Fact]
    public async Task Rechaza_suplantar_origen_y_destinos_desconocidos()
    {
        var hubA = e.ClienteHub(Entorno.NodoA);

        var suplantado = await Assert.ThrowsAsync<HubException>(() =>
            hubA.PushAsync(Ping(Entorno.NodoB, Entorno.NodoA, "NODO_B:PING:1").ToJson(), default));
        var destino = await Assert.ThrowsAsync<HubException>(() =>
            hubA.PushAsync(Ping(Entorno.NodoA, "NO_EXISTE", "NODO_A:PING:4").ToJson(), default));

        Assert.Equal(HttpStatusCode.Forbidden, suplantado.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, destino.Status);
        Assert.True(suplantado.EsDefinitivo && destino.EsDefinitivo);
    }

    [Fact]
    public async Task Latido_queda_registrado_en_el_nodo()
    {
        await e.ClienteHub(Entorno.NodoA).LatidoAsync(new LatidoSolicitud("0.1.0", 3, 1, "error de prueba", DateTimeOffset.Now), default);

        using var admin = e.ClienteAdmin();
        var nodos = await admin.GetStringAsync("v1/admin/nodos");
        Assert.Contains("\"version_agente\":\"0.1.0\"", nodos);
        Assert.Contains("error de prueba", nodos);
    }

    [Fact]
    public async Task Admin_requiere_la_clave_de_administracion()
    {
        using var sinClave = e.Hub.CreateClient();
        var r = await sinClave.GetAsync("v1/admin/mensajes");

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }
}
