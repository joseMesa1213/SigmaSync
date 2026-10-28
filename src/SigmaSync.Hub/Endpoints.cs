using System.Text.Json;
using Microsoft.Extensions.Options;
using SigmaSync.Contracts;

namespace SigmaSync.Hub;

public static class Endpoints
{
    public static void MapearEndpoints(this WebApplication app)
    {
        app.MapGet("/salud", async (RepositorioHub repo) =>
            await repo.SaludAsync() ? Results.Ok(new { estado = "ok" }) : Results.StatusCode(503));

        var v1 = app.MapGroup("/v1");

        v1.MapPost("/mensajes", async (HttpContext ctx, RepositorioHub repo, ILogger<RepositorioHub> log) =>
        {
            var nodo = AutenticacionNodo.NodoAutenticado(ctx);
            Sobre? sobre;
            try { sobre = await ctx.Request.ReadFromJsonAsync<Sobre>(SigmaJson.Opciones, ctx.RequestAborted); }
            catch (JsonException ex) { return Results.BadRequest(new { error = $"Sobre JSON inválido: {ex.Message}" }); }

            if (sobre is null || sobre.MsgId == Guid.Empty || string.IsNullOrWhiteSpace(sobre.Tipo)
                || string.IsNullOrWhiteSpace(sobre.ClaveParticion) || sobre.ClaveParticion.Length > 60 || sobre.Tipo.Length > 20)
                return Results.BadRequest(new { error = "Sobre incompleto: msg_id, tipo y clave_particion (máx. 60) son obligatorios" });
            if (!string.Equals(sobre.NodoOrigen, nodo, StringComparison.Ordinal))
                return Results.Json(new { error = $"nodo_origen '{sobre.NodoOrigen}' no corresponde al nodo autenticado '{nodo}'" },
                    statusCode: StatusCodes.Status403Forbidden);
            if (!await repo.ExisteNodoActivoAsync(sobre.NodoDestino))
                return Results.UnprocessableEntity(new { error = $"nodo_destino '{sobre.NodoDestino}' no existe o está inactivo" });

            var insertado = await repo.InsertarAsync(sobre);
            log.LogInformation("Push {Tipo} {MsgId} {Origen} → {Destino}{Dup}", sobre.Tipo, sobre.MsgId, sobre.NodoOrigen, sobre.NodoDestino,
                insertado ? "" : " (duplicado)");
            return Results.Ok(new PushRespuesta(insertado));
        });

        v1.MapPost("/mensajes/pull", async (HttpContext ctx, int? max, RepositorioHub repo, IOptions<OpcionesHub> opc) =>
        {
            var nodo = AutenticacionNodo.NodoAutenticado(ctx);
            var sobres = await repo.PullAsync(nodo, Math.Clamp(max ?? 10, 1, 100), opc.Value.LeaseMinutos);
            return Results.Ok(new PullRespuesta(sobres.Select(Sobre.FromJson).ToList()));
        });

        v1.MapPost("/mensajes/{id:guid}/ack", async (HttpContext ctx, Guid id, AckSolicitud ack, RepositorioHub repo,
            IOptions<OpcionesHub> opc, ILogger<RepositorioHub> log) =>
        {
            var nodo = AutenticacionNodo.NodoAutenticado(ctx);
            if (!ResultadosAck.EsValido(ack.Resultado))
                return Results.BadRequest(new { error = "resultado debe ser ok, reintentar o rechazado" });

            var (resultado, estado) = await repo.AckAsync(id, nodo, ack.Resultado, ack.Error, opc.Value);
            if (estado == EstadosMensaje.Dlq)
                log.LogWarning("Mensaje {MsgId} de {Nodo} pasó a DLQ: {Error}", id, nodo, ack.Error);
            return resultado switch
            {
                ResultadoAckHub.Aplicado => Results.Ok(new { estado }),
                ResultadoAckHub.NoEncontrado => Results.NotFound(new { error = "Mensaje no encontrado para este nodo" }),
                _ => Results.Conflict(new { error = $"El mensaje está en estado {estado}; solo se confirma un mensaje EN_PROCESO" }),
            };
        });

        v1.MapPost("/latido", async (HttpContext ctx, LatidoSolicitud latido, RepositorioHub repo) =>
        {
            await repo.LatidoAsync(AutenticacionNodo.NodoAutenticado(ctx), latido);
            return Results.Ok();
        });

        var admin = v1.MapGroup("/admin");
        admin.MapGet("/resumen", async (RepositorioHub repo) => Results.Ok(await repo.ResumenAsync()));
        admin.MapGet("/nodos", async (RepositorioHub repo) => Results.Ok(await repo.ListarNodosAsync()));
        admin.MapGet("/mensajes", async (string? estado, string? nodo, int? top, RepositorioHub repo) =>
            Results.Ok(await repo.ListarMensajesAsync(Vacio(estado), Vacio(nodo), Math.Clamp(top ?? 100, 1, 1000))));
        admin.MapGet("/mensajes/{id:guid}", async (Guid id, RepositorioHub repo) =>
            await repo.ObtenerMensajeAsync(id) is { } m ? Results.Ok(m) : Results.NotFound());
        admin.MapPost("/mensajes/{id:guid}/reprocesar", async (Guid id, RepositorioHub repo, ILogger<RepositorioHub> log) =>
        {
            if (!await repo.ReprocesarAsync(id))
                return Results.Conflict(new { error = "Solo se reprocesan mensajes en DLQ, RECHAZADO o DESCARTADO" });
            log.LogWarning("Mensaje {MsgId} devuelto a la cola por un operador", id);
            return Results.Ok();
        });
        admin.MapPost("/mensajes/{id:guid}/descartar", async (Guid id, DescarteSolicitud d, RepositorioHub repo, ILogger<RepositorioHub> log) =>
        {
            if (string.IsNullOrWhiteSpace(d.Motivo)) return Results.BadRequest(new { error = "El motivo es obligatorio" });
            if (!await repo.DescartarAsync(id, d.Motivo.Trim()))
                return Results.Conflict(new { error = "Solo se descartan mensajes PENDIENTE, DLQ o RECHAZADO" });
            log.LogWarning("Mensaje {MsgId} descartado por un operador: {Motivo}", id, d.Motivo);
            return Results.Ok();
        });
        admin.MapGet("/alertas", async (string? nodo, bool? pendientes, int? top, RepositorioHub repo) =>
            Results.Ok(await repo.ListarAlertasAsync(Vacio(nodo), pendientes ?? false, Math.Clamp(top ?? 200, 1, 1000))));

        // Panel de operación: página estática; los datos se piden a /v1/admin con la clave de administración.
        var panel = Path.Combine(AppContext.BaseDirectory, "panel.html");
        app.MapGet("/panel", () => File.Exists(panel)
            ? Results.Content(File.ReadAllText(panel), "text/html; charset=utf-8")
            : Results.NotFound());
    }

    public sealed record DescarteSolicitud(string Motivo);

    private static string? Vacio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
