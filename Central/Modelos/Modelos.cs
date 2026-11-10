using System.Globalization;
using System.Text.Json.Serialization;

namespace BackupFE.Central.Modelos;

public sealed record InfoServidor(
    string Edicion,
    string Version,
    string EstadoBase,
    string Recuperacion,
    long DatosMB,
    long LogMB,
    string? CarpetaBackupSql)
{
    /// <summary>Express y Web no permiten WITH COMPRESSION.</summary>
    public bool PermiteCompresion =>
        !Edicion.Contains("Express", StringComparison.OrdinalIgnoreCase) &&
        !Edicion.Contains("Web", StringComparison.OrdinalIgnoreCase);

    /// <summary>Espacio libre que se pide antes del backup: 50% de datos + log (misma regla que la version 1).</summary>
    public double NecesarioGB => (DatosMB + LogMB) * 0.5 / 1024;
}

public sealed record ResultadoBackupSql(TimeSpan DuracionBackup, TimeSpan DuracionVerificacion, bool Comprimido, bool Verificado);

public sealed record ResultadoCopia(long Bytes, TimeSpan DuracionUltimoIntento, TimeSpan DuracionTotal, int Intentos)
{
    public double MB => Bytes / 1048576.0;
    public double MBps => MB / Math.Max(DuracionUltimoIntento.TotalSeconds, 0.001);
}

public sealed record ResultadoRetencion(int Borrados, int Quedan);

public sealed record ResultadoBase(string Estacion, string Base, bool Ok, string Mensaje);

public sealed record ResumenEjecucion(int Total, int Ok, IReadOnlyList<ResultadoBase> Fallos);

/// <summary>Fallo de negocio esperado (mensaje claro, sin stack trace en el log).</summary>
public sealed class FalloBackupException(string mensaje) : Exception(mensaje);

/// <summary>El archivo copiado no coincide con el origen. Hereda de IOException para que Polly lo reintente.</summary>
public sealed class IntegridadException(string mensaje) : IOException(mensaje);

/// <summary>
/// Mismo formato JSON que escribia la version 1 (NAS\_Estado\Estacion\Base.json),
/// para que "BackupFE.exe reporte" y cualquier sistema externo lo sigan leyendo igual.
/// </summary>
public sealed class EstadoBackup
{
    public const string Ok = "OK";
    public const string Fallo = "FALLO";
    public const string EnCurso = "EN CURSO";

    [JsonPropertyName("estacion")] public string Estacion { get; set; } = "";
    [JsonPropertyName("base")] public string Base { get; set; } = "";
    [JsonPropertyName("resultado")] public string Resultado { get; set; } = EnCurso;
    [JsonPropertyName("inicio")] public string? Inicio { get; set; }
    [JsonPropertyName("fin")] public string? Fin { get; set; }
    [JsonPropertyName("ultimoOK")] public string? UltimoOK { get; set; }
    [JsonPropertyName("mensaje")] public string? Mensaje { get; set; }
    [JsonPropertyName("tamanoMB")] public double TamanoMB { get; set; }
    [JsonPropertyName("minutos")] public double Minutos { get; set; }
    [JsonPropertyName("archivo")] public string? Archivo { get; set; }
    [JsonPropertyName("equipo")] public string? Equipo { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("ip")] public string? Ip { get; set; }

    public static string F(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
