using System.Net;
using System.Text;

namespace SigmaSync.Contracts.Tests;

public class FirmaHmacTests
{
    private static readonly byte[] Secreto = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Cuerpo = Encoding.UTF8.GetBytes("""{"msg_id":"x"}""");
    private const long Ts = 1_790_000_000;

    [Fact]
    public void Verifica_una_firma_correcta()
    {
        var firma = FirmaHmac.Calcular(Secreto, "post", "/v1/mensajes", Ts, Cuerpo);

        Assert.True(FirmaHmac.Verificar(Secreto, "POST", "/v1/mensajes", Ts, Cuerpo, firma));
    }

    [Fact]
    public void Rechaza_cuerpo_ruta_timestamp_o_secreto_distintos()
    {
        var firma = FirmaHmac.Calcular(Secreto, "POST", "/v1/mensajes", Ts, Cuerpo);
        var otroSecreto = Secreto.Reverse().ToArray();

        Assert.False(FirmaHmac.Verificar(Secreto, "POST", "/v1/mensajes", Ts, Encoding.UTF8.GetBytes("""{"msg_id":"y"}"""), firma));
        Assert.False(FirmaHmac.Verificar(Secreto, "POST", "/v1/mensajes/pull?max=10", Ts, Cuerpo, firma));
        Assert.False(FirmaHmac.Verificar(Secreto, "POST", "/v1/mensajes", Ts + 1, Cuerpo, firma));
        Assert.False(FirmaHmac.Verificar(otroSecreto, "POST", "/v1/mensajes", Ts, Cuerpo, firma));
        Assert.False(FirmaHmac.Verificar(Secreto, "POST", "/v1/mensajes", Ts, Cuerpo, "no-es-base64!"));
    }

    [Fact]
    public void Timestamp_fuera_del_desfase_no_es_vigente()
    {
        var ahora = DateTimeOffset.FromUnixTimeSeconds(Ts);

        Assert.True(FirmaHmac.TimestampVigente(Ts - 299, TimeSpan.FromMinutes(5), ahora));
        Assert.False(FirmaHmac.TimestampVigente(Ts - 301, TimeSpan.FromMinutes(5), ahora));
        Assert.False(FirmaHmac.TimestampVigente(Ts + 301, TimeSpan.FromMinutes(5), ahora));
    }

    [Fact]
    public async Task El_manejador_firma_exactamente_el_cuerpo_y_la_ruta_enviados()
    {
        var captura = new Captura();
        using var http = new HttpClient(new ManejadorFirmaHmac("PV_MAY", "api-key", Secreto) { InnerHandler = captura })
        {
            BaseAddress = new Uri("http://hub.local/"),
        };

        await http.PostAsync("v1/mensajes/pull?max=10", new StringContent("""{"a":1}""", Encoding.UTF8, "application/json"));

        var r = captura.Recibida!;
        Assert.Equal("PV_MAY", r.Headers.GetValues(FirmaHmac.HeaderNodo).Single());
        Assert.Equal("api-key", r.Headers.GetValues(FirmaHmac.HeaderApiKey).Single());
        var ts = long.Parse(r.Headers.GetValues(FirmaHmac.HeaderTimestamp).Single());
        Assert.True(FirmaHmac.Verificar(Secreto, "POST", "/v1/mensajes/pull?max=10", ts, captura.Cuerpo,
            r.Headers.GetValues(FirmaHmac.HeaderFirma).Single()));
        Assert.Equal("application/json", r.Content!.Headers.ContentType!.MediaType);
    }

    private sealed class Captura : HttpMessageHandler
    {
        public HttpRequestMessage? Recibida { get; private set; }
        public byte[] Cuerpo { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Recibida = request;
            Cuerpo = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
