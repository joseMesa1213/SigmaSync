namespace SigmaSync.Hub;

/// <summary>Sección "Hub" de appsettings.json (+ variables SIGMASYNC_Hub__*).</summary>
public sealed class OpcionesHub
{
    public string Url { get; set; } = "http://localhost:5080";
    public string ConnectionString { get; set; } = "";

    /// <summary>Entregas de un mensaje antes de pasarlo a DLQ.</summary>
    public int MaxIntentos { get; set; } = 6;
    public int LeaseMinutos { get; set; } = 5;
    public int DesfaseMaxSegundos { get; set; } = 300;

    /// <summary>
    /// Espera antes de reentregar tras un "reintentar", según el número de intento (el último se repite).
    /// Vacío por defecto: el binder de configuración agrega a los arreglos en vez de reemplazarlos.
    /// </summary>
    public int[] BackoffSegundos { get; set; } = [];

    private static readonly int[] BackoffPorDefecto = [60, 300, 900, 3600];

    public int RetencionDias { get; set; } = 30;

    /// <summary>SHA-256 (hex) de la clave de administración; vacío desactiva /v1/admin. Se genera con "hash-admin".</summary>
    public string AdminKeyHash { get; set; } = "";

    public string DirectorioLogs { get; set; } = "logs";

    /// <summary>false pausa la revisión de latidos y el correo de alertas (mantenimientos programados).</summary>
    public bool AlertasActivas { get; set; } = true;

    /// <summary>Minutos sin latido para declarar caído un agente.</summary>
    public int MinutosSinLatido { get; set; } = 15;

    /// <summary>Cada cuánto se revisan los latidos y se envía el correo de resumen de alertas.</summary>
    public int IntervaloNotificacionMin { get; set; } = 5;

    public OpcionesSmtp Smtp { get; set; } = new();

    public sealed class OpcionesSmtp
    {
        /// <summary>Vacío desactiva el correo: las alertas siguen visibles en el panel.</summary>
        public string Host { get; set; } = "";
        public int Puerto { get; set; } = 587;
        public bool Ssl { get; set; } = true;
        public string Usuario { get; set; } = "";

        /// <summary>Texto plano o "dpapi:..." (protegido con "SigmaSync.Agent.exe proteger" en el mismo servidor).</summary>
        public string Clave { get; set; } = "";
        public string De { get; set; } = "";

        /// <summary>Destinatarios separados por coma o punto y coma.</summary>
        public string Para { get; set; } = "";

        public bool Configurado => Host.Length > 0 && De.Length > 0 && Para.Length > 0;
    }

    public TimeSpan Backoff(int intentos)
    {
        var tabla = BackoffSegundos.Length > 0 ? BackoffSegundos : BackoffPorDefecto;
        return TimeSpan.FromSeconds(tabla[Math.Clamp(intentos - 1, 0, tabla.Length - 1)]);
    }
}
