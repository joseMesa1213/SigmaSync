using System.Security.Cryptography;
using System.Text;

namespace SigmaSync.Contracts;

/// <summary>
/// Firma de las peticiones al hub:
/// X-Firma = Base64(HMAC-SHA256(secreto, "MÉTODO\nruta?query\ntimestamp\nsha256hex(cuerpo)")).
/// </summary>
public static class FirmaHmac
{
    public const string HeaderNodo = "X-Nodo";
    public const string HeaderApiKey = "X-Api-Key";
    public const string HeaderTimestamp = "X-Timestamp";
    public const string HeaderFirma = "X-Firma";

    public static string Calcular(byte[] secreto, string metodo, string rutaYQuery, long timestamp, ReadOnlySpan<byte> cuerpo)
    {
        var hashCuerpo = Convert.ToHexStringLower(SHA256.HashData(cuerpo));
        var texto = $"{metodo.ToUpperInvariant()}\n{rutaYQuery}\n{timestamp}\n{hashCuerpo}";
        return Convert.ToBase64String(HMACSHA256.HashData(secreto, Encoding.UTF8.GetBytes(texto)));
    }

    public static bool Verificar(byte[] secreto, string metodo, string rutaYQuery, long timestamp, ReadOnlySpan<byte> cuerpo, string firma)
    {
        byte[] recibida;
        try { recibida = Convert.FromBase64String(firma); }
        catch (FormatException) { return false; }

        var esperada = Convert.FromBase64String(Calcular(secreto, metodo, rutaYQuery, timestamp, cuerpo));
        return CryptographicOperations.FixedTimeEquals(esperada, recibida);
    }

    public static bool TimestampVigente(long timestamp, TimeSpan desfaseMaximo, DateTimeOffset? ahora = null)
    {
        var actual = (ahora ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return Math.Abs(actual - timestamp) <= desfaseMaximo.TotalSeconds;
    }

    public static byte[] HashApiKey(string apiKey) => SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
}

/// <summary>Agrega X-Nodo, X-Api-Key, X-Timestamp y X-Firma a cada petición hacia el hub.</summary>
public sealed class ManejadorFirmaHmac(string nodoId, string apiKey, byte[] secreto) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        byte[] cuerpo = [];
        if (request.Content is not null)
        {
            // Se fija el cuerpo en bytes para que lo firmado sea exactamente lo que viaja.
            cuerpo = await request.Content.ReadAsByteArrayAsync(ct);
            var original = request.Content;
            var fijo = new ByteArrayContent(cuerpo);
            foreach (var h in original.Headers.Where(h => h.Key != "Content-Length"))
                fijo.Headers.TryAddWithoutValidation(h.Key, h.Value);
            request.Content = fijo;
        }

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ruta = request.RequestUri!.PathAndQuery;
        request.Headers.Remove(FirmaHmac.HeaderFirma);
        request.Headers.Add(FirmaHmac.HeaderNodo, nodoId);
        request.Headers.Add(FirmaHmac.HeaderApiKey, apiKey);
        request.Headers.Add(FirmaHmac.HeaderTimestamp, ts.ToString());
        request.Headers.Add(FirmaHmac.HeaderFirma, FirmaHmac.Calcular(secreto, request.Method.Method, ruta, ts, cuerpo));
        return await base.SendAsync(request, ct);
    }
}
