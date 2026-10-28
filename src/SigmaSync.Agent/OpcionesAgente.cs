namespace SigmaSync.Agent;

/// <summary>Sección "Agente" de appsettings.json + appsettings.{nodo}.json (+ variables SIGMASYNC_Agente__*).</summary>
public sealed class OpcionesAgente
{
    public string NodoId { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public string HubUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";

    /// <summary>Secreto HMAC en Base64, o "dpapi:..." si se protegió con el comando "proteger".</summary>
    public string HmacSecret { get; set; } = "";

    public int IntervaloCapturaSeg { get; set; } = 30;
    public int IntervaloPublicacionSeg { get; set; } = 5;
    public int IntervaloPullSeg { get; set; } = 15;
    public int IntervaloLatidoSeg { get; set; } = 60;
    public int MaxPull { get; set; } = 10;
    public int MaxPublicacion { get; set; } = 50;
    public string DirectorioLogs { get; set; } = "logs";

    public IReadOnlyList<string> Validar()
    {
        var errores = new List<string>();
        if (string.IsNullOrWhiteSpace(NodoId)) errores.Add("Agente:NodoId está vacío.");
        if (string.IsNullOrWhiteSpace(ConnectionString)) errores.Add("Agente:ConnectionString está vacío.");
        if (!Uri.TryCreate(HubUrl, UriKind.Absolute, out _)) errores.Add("Agente:HubUrl no es una URL válida.");
        if (string.IsNullOrWhiteSpace(ApiKey)) errores.Add("Agente:ApiKey está vacío (se obtiene con 'registrar-nodo' en el hub).");
        if (string.IsNullOrWhiteSpace(HmacSecret)) errores.Add("Agente:HmacSecret está vacío (se obtiene con 'registrar-nodo' en el hub).");
        return errores;
    }
}

/// <summary>Estado compartido entre los ciclos, para reportarlo en el latido.</summary>
public sealed class EstadoAgente
{
    private string? _ultimoError;

    public string? UltimoError => _ultimoError;

    public void RegistrarError(string error) => _ultimoError = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {error}";
}
