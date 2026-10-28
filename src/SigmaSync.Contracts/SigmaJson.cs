using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SigmaSync.Contracts;

/// <summary>Opciones JSON del contrato: snake_case, sin indentar. Las usan el hub, los agentes y las pruebas.</summary>
public static class SigmaJson
{
    public static readonly JsonSerializerOptions Opciones = Crear();

    public static void Aplicar(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        o.WriteIndented = false;
    }

    private static JsonSerializerOptions Crear()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        Aplicar(o);
        o.MakeReadOnly();
        return o;
    }
}
