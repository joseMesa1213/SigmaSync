using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using SigmaSync.Contracts;
using SigmaSync.Hub;
using SigmaSync.Saint;

namespace SigmaSync.Integration.Tests;

/// <summary>Fase 5: alertas centralizadas, latido, correo, descarte y panel.</summary>
[Collection(ColeccionLocalDb.Nombre)]
public sealed class OperacionTests(Entorno e) : IAsyncLifetime
{
    public async Task InitializeAsync() => await e.LimpiarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task LogAsync(string nodo, string nivel, string mensaje)
    {
        await using var cn = new SqlConnection(e.CsDe(nodo));
        await RepositorioSync.RegistrarLogAsync(cn, null, nivel, "Prueba", null, mensaje);
    }

    private Task<int> AlertasAsync(string where = "1 = 1") =>
        e.ConsultarAsync<int>(e.CsHub, $"SELECT COUNT(*) FROM hub.Alerta WHERE {where}");

    [Fact]
    public async Task El_latido_lleva_al_hub_las_advertencias_nuevas_del_nodo_sin_repetirlas()
    {
        await LogAsync(Entorno.NodoA, "WARN", "histórico anterior a la instalación");
        var latido = e.Latido(Entorno.NodoA);

        await latido.EjecutarAsync(default);   // primera vez: fija el cursor, no envía el histórico
        Assert.Equal(0, await AlertasAsync());

        await LogAsync(Entorno.NodoA, "WARN", "EXENTO_DIFERENTE: 4180");
        await LogAsync(Entorno.NodoA, "INFO", "no es alerta");
        await LogAsync(Entorno.NodoA, "ERROR", "Error procesando OC_PEDIDO: timeout");
        await latido.EjecutarAsync(default);
        await latido.EjecutarAsync(default);   // no repite

        Assert.Equal(2, await AlertasAsync("fuente = 'NODO' AND nodo_id = 'NODO_A'"));
        Assert.Equal(1, await AlertasAsync("nivel = 'ERROR' AND mensaje LIKE 'Prueba: Error procesando%'"));
    }

    [Fact]
    public async Task Un_nodo_sin_latido_genera_una_alerta_por_caida_y_otra_al_volver()
    {
        var repo = new RepositorioHub(e.CsHub);
        await using (var cn = new SqlConnection(e.CsHub))
            await cn.ExecuteAsync("UPDATE hub.Nodo SET ultimo_latido = DATEADD(HOUR, -1, SYSUTCDATETIME()) WHERE nodo_id = 'NODO_B'");

        Assert.Equal(["NODO_B"], await repo.DetectarNodosSinLatidoAsync(15));
        Assert.Empty(await repo.DetectarNodosSinLatidoAsync(15));   // una sola alerta por caída
        Assert.Equal(1, await AlertasAsync("nodo_id = 'NODO_B' AND nivel = 'ERROR' AND mensaje LIKE '%no reporta latido%'"));

        await e.Latido(Entorno.NodoB).EjecutarAsync(default);

        Assert.Equal(1, await AlertasAsync("nodo_id = 'NODO_B' AND nivel = 'INFO' AND mensaje LIKE '%volvió a reportar latido%'"));
        Assert.Null(await e.ConsultarAsync<DateTime?>(e.CsHub, "SELECT sin_latido_desde FROM hub.Nodo WHERE nodo_id = 'NODO_B'"));
    }

    [Fact]
    public async Task Un_mensaje_en_DLQ_genera_alerta_y_el_notificador_envia_un_solo_correo()
    {
        var sobre = Sobre.Crear(TiposMensaje.Ping, Entorno.NodoA, Entorno.NodoB, "NODO_A:PING:dlq", new Ping("x", DateTimeOffset.Now));
        var hubA = e.ClienteHub(Entorno.NodoA);
        var hubB = e.ClienteHub(Entorno.NodoB);
        await hubA.PushAsync(sobre.ToJson(), default);
        for (var i = 0; i < 2; i++)   // MaxIntentos = 2 en el entorno de pruebas
        {
            await hubB.PullAsync(10, default);
            await hubB.AckAsync(sobre.MsgId, ResultadosAck.Reintentar, "deadlock", default);
        }
        Assert.Equal(1, await AlertasAsync("fuente = 'HUB' AND nivel = 'ERROR' AND mensaje LIKE '%DLQ%'"));

        await e.Notificador.EjecutarCicloAsync(default);
        await e.Notificador.EjecutarCicloAsync(default);   // nada nuevo: no reenvía

        var (asunto, cuerpo) = Assert.Single(e.Correo.Enviados);
        Assert.StartsWith("[SigmaSync] 1 alerta (1 con error)", asunto);
        Assert.Contains("── NODO_B ──", cuerpo);
        Assert.Contains(sobre.MsgId.ToString(), cuerpo);
        Assert.Equal(0, await AlertasAsync("notificada = 0"));
    }

    [Fact]
    public async Task Descartar_un_mensaje_en_DLQ_libera_su_particion_y_se_puede_reprocesar()
    {
        var hubA = e.ClienteHub(Entorno.NodoA);
        var hubB = e.ClienteHub(Entorno.NodoB);
        var malo = Sobre.Crear(TiposMensaje.Ping, Entorno.NodoA, Entorno.NodoB, "NODO_A:L:000123", new Ping("1", DateTimeOffset.Now));
        var siguiente = Sobre.Crear(TiposMensaje.Ping, Entorno.NodoA, Entorno.NodoB, "NODO_A:L:000123", new Ping("2", DateTimeOffset.Now));
        await hubA.PushAsync(malo.ToJson(), default);
        await hubA.PushAsync(siguiente.ToJson(), default);
        for (var i = 0; i < 2; i++)
        {
            await hubB.PullAsync(10, default);
            await hubB.AckAsync(malo.MsgId, ResultadosAck.Reintentar, "falla", default);
        }
        Assert.Empty(await hubB.PullAsync(10, default));   // bloqueada detrás del DLQ

        using var admin = e.ClienteAdmin();
        var sinMotivo = await admin.PostAsJsonAsync($"v1/admin/mensajes/{malo.MsgId}/descartar", new { motivo = "" });
        var r = await admin.PostAsJsonAsync($"v1/admin/mensajes/{malo.MsgId}/descartar", new { motivo = "OC registrada a mano" });

        Assert.Equal(HttpStatusCode.BadRequest, sinMotivo.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(siguiente.MsgId, (await hubB.PullAsync(10, default)).Single().MsgId);

        var detalle = await admin.GetFromJsonAsync<MensajeDetalle>($"v1/admin/mensajes/{malo.MsgId}", SigmaJson.Opciones);
        Assert.Equal("DESCARTADO", detalle!.Mensaje.Estado);
        Assert.Contains("OC registrada a mano", detalle.Mensaje.Error);
        Assert.Equal(malo.MsgId, detalle.Sobre.MsgId);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"v1/admin/mensajes/{malo.MsgId}/reprocesar", null)).StatusCode);
    }

    [Fact]
    public async Task Panel_y_consultas_de_administracion()
    {
        using var anonimo = e.Hub.CreateClient();
        var panel = await anonimo.GetAsync("panel");
        Assert.Equal(HttpStatusCode.OK, panel.StatusCode);
        Assert.Contains("SigmaSync · Operación", await panel.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await anonimo.GetAsync("v1/admin/alertas")).StatusCode);

        await e.ClienteHub(Entorno.NodoA).PushAsync(
            Sobre.Crear(TiposMensaje.Ping, Entorno.NodoA, Entorno.NodoB, "NODO_A:PING:r", new Ping("x", DateTimeOffset.Now)).ToJson(), default);
        using var admin = e.ClienteAdmin();
        var resumen = await admin.GetFromJsonAsync<Dictionary<string, int>>("v1/admin/resumen");
        Assert.Equal(1, resumen!["pendiente"]);
        Assert.Contains("\"rechazados\"", await admin.GetStringAsync("v1/admin/nodos"));
    }

    [Fact]
    public async Task El_agente_reintenta_el_outbox_rechazado_por_el_hub()
    {
        var malo = Sobre.Crear(TiposMensaje.Ping, Entorno.NodoA, "NO_EXISTE", "NODO_A:PING:z", new Ping("x", DateTimeOffset.Now));
        await using var cn = new SqlConnection(e.CsA);
        await cn.OpenAsync();
        await RepositorioSync.EncolarAsync(cn, null, malo);
        await e.Publicador(Entorno.NodoA).EjecutarAsync(default);
        Assert.Equal((0, 1), await RepositorioSync.ContarOutboxAsync(cn));

        Assert.Equal(1, await RepositorioSync.ReintentarOutboxConErrorAsync(cn));
        Assert.Equal((1, 0), await RepositorioSync.ContarOutboxAsync(cn));
    }
}
