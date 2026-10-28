using System.Security.Cryptography;
using System.Text;

namespace SigmaSync.Agent;

/// <summary>
/// Secretos en la configuración del agente. Un valor "dpapi:..." está cifrado con DPAPI a nivel de máquina:
/// solo se puede leer en el mismo servidor donde se protegió.
/// </summary>
#pragma warning disable CA1416 // El agente solo corre en Windows (servicio Windows).
public static class Secretos
{
    private const string Prefijo = "dpapi:";

    public static string Proteger(string texto) =>
        Prefijo + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(texto), null, DataProtectionScope.LocalMachine));

    public static string Revelar(string valor) =>
        valor.StartsWith(Prefijo, StringComparison.Ordinal)
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(valor[Prefijo.Length..]), null, DataProtectionScope.LocalMachine))
            : valor;

    /// <summary>Secreto HMAC en bytes (el valor revelado viene en Base64).</summary>
    public static byte[] SecretoHmac(string valor) => Convert.FromBase64String(Revelar(valor));
}
#pragma warning restore CA1416
