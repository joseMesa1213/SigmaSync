using System.Security.Cryptography;

namespace SigmaSync.Hub;

/// <summary>
/// Cifra los secretos HMAC de los nodos con DPAPI (ámbito máquina). Una base del hub restaurada en otro
/// servidor no expone los secretos: hay que volver a registrar los nodos.
/// </summary>
#pragma warning disable CA1416 // El hub corre en Windows (servicio Windows).
public sealed class ProtectorSecretos
{
    private static readonly byte[] Entropia = "SigmaSync.Hub.v1"u8.ToArray();

    public byte[] Proteger(byte[] secreto) => ProtectedData.Protect(secreto, Entropia, DataProtectionScope.LocalMachine);

    public byte[] Revelar(byte[] cifrado) => ProtectedData.Unprotect(cifrado, Entropia, DataProtectionScope.LocalMachine);
}
#pragma warning restore CA1416
