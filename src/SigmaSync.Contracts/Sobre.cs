using System.Text.Json;

namespace SigmaSync.Contracts;

/// <summary>Sobre común de todos los mensajes (contrato v1.x). El payload depende de <see cref="Tipo"/>.</summary>
public sealed record Sobre(
    string Version,
    Guid MsgId,
    string Tipo,
    string NodoOrigen,
    string NodoDestino,
    DateTimeOffset CreadoTs,
    string ClaveParticion,
    JsonElement Payload)
{
    public const string VersionActual = "1.1";

    public static Sobre Crear<T>(string tipo, string nodoOrigen, string nodoDestino, string claveParticion, T payload) =>
        new(VersionActual, Guid.NewGuid(), tipo, nodoOrigen, nodoDestino, DateTimeOffset.Now, claveParticion,
            JsonSerializer.SerializeToElement(payload, SigmaJson.Opciones));

    public T Leer<T>() =>
        Payload.Deserialize<T>(SigmaJson.Opciones)
        ?? throw new InvalidOperationException($"Payload vacío en el mensaje {MsgId} ({Tipo}).");

    /// <summary>Acepta cualquier versión 1.x: los cambios dentro de la versión mayor son aditivos.</summary>
    public bool VersionCompatible => Version.Split('.')[0] == "1";

    public string ToJson() => JsonSerializer.Serialize(this, SigmaJson.Opciones);

    public static Sobre FromJson(string json) =>
        JsonSerializer.Deserialize<Sobre>(json, SigmaJson.Opciones)
        ?? throw new InvalidOperationException("Sobre JSON vacío.");

    /// <summary>Clave de partición estándar: nodo + tipo de documento + número del documento de origen.</summary>
    public static string Clave(string nodo, string tipoDoc, string numero) => $"{nodo}:{tipoDoc}:{numero}";
}
