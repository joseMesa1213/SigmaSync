using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SigmaSync.Contracts;

namespace SigmaSync.Hub;

/// <summary>
/// Autentica las rutas /v1: API key + firma HMAC por nodo; /v1/admin usa la clave de administración.
/// El nodo autenticado queda en HttpContext.Items para los endpoints.
/// </summary>
public sealed class AutenticacionNodo(RequestDelegate next, ILogger<AutenticacionNodo> log)
{
    public const string ClaveNodo = "SigmaSync.Nodo";
    public const string HeaderAdmin = "X-Admin-Key";

    public async Task InvokeAsync(HttpContext ctx, RepositorioHub repo, IOptions<OpcionesHub> opciones, ProtectorSecretos protector)
    {
        var opc = opciones.Value;
        var ruta = ctx.Request.Path;
        if (!ruta.StartsWithSegments("/v1"))
        {
            await next(ctx);
            return;
        }

        if (ruta.StartsWithSegments("/v1/admin"))
        {
            if (!AdminValido(ctx.Request.Headers[HeaderAdmin].ToString(), opc.AdminKeyHash))
            {
                await RechazarAsync(ctx, StatusCodes.Status403Forbidden, "Clave de administración inválida o administración desactivada");
                return;
            }
            await next(ctx);
            return;
        }

        var h = ctx.Request.Headers;
        var nodoId = h[FirmaHmac.HeaderNodo].ToString();
        var apiKey = h[FirmaHmac.HeaderApiKey].ToString();
        var firma = h[FirmaHmac.HeaderFirma].ToString();
        if (nodoId.Length == 0 || apiKey.Length == 0 || firma.Length == 0 || !long.TryParse(h[FirmaHmac.HeaderTimestamp], out var ts))
        {
            await RechazarAsync(ctx, StatusCodes.Status401Unauthorized, "Faltan encabezados de autenticación");
            return;
        }

        if (!FirmaHmac.TimestampVigente(ts, TimeSpan.FromSeconds(opc.DesfaseMaxSegundos)))
        {
            await RechazarAsync(ctx, StatusCodes.Status401Unauthorized, "Timestamp fuera de rango: revise el reloj del servidor del nodo", nodoId);
            return;
        }

        var nodo = await repo.ObtenerNodoAsync(nodoId);
        if (nodo is null || !nodo.Activo || !CryptographicOperations.FixedTimeEquals(FirmaHmac.HashApiKey(apiKey), nodo.ApiKeyHash))
        {
            await RechazarAsync(ctx, StatusCodes.Status401Unauthorized, "Nodo o API key inválidos", nodoId);
            return;
        }

        ctx.Request.EnableBuffering();
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        ctx.Request.Body.Position = 0;

        var rutaYQuery = ctx.Request.PathBase.Add(ctx.Request.Path).Add(ctx.Request.QueryString);
        if (!FirmaHmac.Verificar(protector.Revelar(nodo.HmacSecret), ctx.Request.Method, rutaYQuery, ts, ms.ToArray(), firma))
        {
            await RechazarAsync(ctx, StatusCodes.Status401Unauthorized, "Firma inválida", nodoId);
            return;
        }

        ctx.Items[ClaveNodo] = nodo.NodoId;
        await next(ctx);
    }

    public static string NodoAutenticado(HttpContext ctx) =>
        ctx.Items[ClaveNodo] as string ?? throw new InvalidOperationException("Petición sin nodo autenticado.");

    private static bool AdminValido(string clave, string hashEsperado)
    {
        if (clave.Length == 0 || hashEsperado.Length != 64) return false;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(clave));
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(hashEsperado));
    }

    private async Task RechazarAsync(HttpContext ctx, int status, string motivo, string? nodoId = null)
    {
        log.LogWarning("Petición rechazada ({Status}) {Metodo} {Ruta} nodo={Nodo}: {Motivo}",
            status, ctx.Request.Method, ctx.Request.Path, nodoId ?? "-", motivo);
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { error = motivo });
    }
}
