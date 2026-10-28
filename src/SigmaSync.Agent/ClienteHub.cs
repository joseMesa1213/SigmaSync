using System.Net;
using System.Net.Http.Json;
using System.Text;
using SigmaSync.Contracts;

namespace SigmaSync.Agent;

public sealed class HubException(HttpStatusCode status, string detalle)
    : Exception($"El hub respondió {(int)status} {status}: {detalle}")
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>
    /// El hub rechazó la petición por su contenido (400, 403, 404, 409, 422): reintentarla no sirve.
    /// 401 (credenciales o reloj) y los 5xx se consideran transitorios.
    /// </summary>
    public bool EsDefinitivo => Status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
        or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity;
}

/// <summary>Cliente HTTP del SyncHub. La firma HMAC la agrega <see cref="ManejadorFirmaHmac"/>.</summary>
public sealed class ClienteHub(HttpClient http)
{
    public async Task<bool> PushAsync(string sobreJson, CancellationToken ct)
    {
        using var contenido = new StringContent(sobreJson, Encoding.UTF8, "application/json");
        using var r = await http.PostAsync("v1/mensajes", contenido, ct);
        await AsegurarExitoAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<PushRespuesta>(SigmaJson.Opciones, ct))!.Insertado;
    }

    public async Task<IReadOnlyList<Sobre>> PullAsync(int max, CancellationToken ct)
    {
        using var r = await http.PostAsync($"v1/mensajes/pull?max={max}", content: null, ct);
        await AsegurarExitoAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<PullRespuesta>(SigmaJson.Opciones, ct))!.Mensajes;
    }

    public async Task AckAsync(Guid msgId, string resultado, string? error, CancellationToken ct)
    {
        using var r = await http.PostAsJsonAsync($"v1/mensajes/{msgId}/ack", new AckSolicitud(resultado, error), SigmaJson.Opciones, ct);
        await AsegurarExitoAsync(r, ct);
    }

    public async Task LatidoAsync(LatidoSolicitud latido, CancellationToken ct)
    {
        using var r = await http.PostAsJsonAsync("v1/latido", latido, SigmaJson.Opciones, ct);
        await AsegurarExitoAsync(r, ct);
    }

    private static async Task AsegurarExitoAsync(HttpResponseMessage r, CancellationToken ct)
    {
        if (r.IsSuccessStatusCode) return;
        var detalle = await r.Content.ReadAsStringAsync(ct);
        throw new HubException(r.StatusCode, detalle.Length > 500 ? detalle[..500] : detalle);
    }
}
