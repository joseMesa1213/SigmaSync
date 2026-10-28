using Dapper;
using Microsoft.Data.SqlClient;

namespace SigmaSync.Saint;

public sealed record Consecutivo(string Numero, IReadOnlyList<string> Saltados);

/// <summary>
/// Consecutivos de SAINT (SACORRELSIS: ValueInt = siguiente número; la fila 'LenCorrel' fija los dígitos).
/// Se toman dentro de la transacción del documento: el UPDATE deja la fila bloqueada hasta el COMMIT,
/// así que SAINT y el agente se serializan, y un rollback no pierde el número.
/// </summary>
public static class Consecutivos
{
    public const string PrxPedido = "PrxPedido";
    public const string PrxOrdenC = "PrxOrdenC";

    /// <summary>
    /// Toma el siguiente número libre. Si el número ya existe (p. ej. la colisión de PrxPedido que dejó la
    /// integración WORKER en Fábrica), avanza y lo informa en <see cref="Consecutivo.Saltados"/>.
    /// </summary>
    public static async Task<Consecutivo> TomarAsync(SqlConnection cn, SqlTransaction tx, string codSucu, string campo,
        Func<string, Task<bool>> existe, int maxSaltos = 50)
    {
        var digitos = await cn.ExecuteScalarAsync<int?>(
            "SELECT ValueInt FROM dbo.SACORRELSIS WHERE CodSucu = @codSucu AND FieldName = 'LenCorrel'", new { codSucu }, tx)
            ?? throw new InvalidOperationException($"SACORRELSIS no tiene la fila LenCorrel para la sucursal {codSucu}.");

        var saltados = new List<string>();
        for (var i = 0; i <= maxSaltos; i++)
        {
            var valor = await cn.ExecuteScalarAsync<int?>("""
                DECLARE @n int;
                UPDATE dbo.SACORRELSIS WITH (ROWLOCK) SET @n = ValueInt = ValueInt + 1
                WHERE CodSucu = @codSucu AND FieldName = @campo;
                SELECT @n - 1;
                """, new { codSucu, campo }, tx)
                ?? throw new InvalidOperationException($"SACORRELSIS no tiene la fila {campo} para la sucursal {codSucu}.");

            var numero = valor.ToString().PadLeft(digitos, '0');
            if (!await existe(numero)) return new Consecutivo(numero, saltados);
            saltados.Add(numero);
        }
        throw new InvalidOperationException($"{campo}: más de {maxSaltos} números seguidos ya existen ({saltados[0]}…{saltados[^1]}).");
    }
}
