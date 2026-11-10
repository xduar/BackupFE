using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BackupFE.Central.Abstracciones;
using BackupFE.Central.Configuracion;
using BackupFE.Central.Modelos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackupFE.Central.Servicios;

public sealed class JsonStatusStore(IOptions<BackupOptions> opciones, ILogger<JsonStatusStore> log) : IStatusStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // tildes y "\" legibles
    };

    string Ruta(string estacion, string baseDatos) =>
        Path.Combine(opciones.Value.RutaNas, "_Estado", estacion, baseDatos + ".json");

    public async Task<EstadoBackup?> LeerAsync(string estacion, string baseDatos, CancellationToken ct)
    {
        string ruta = Ruta(estacion, baseDatos);
        try
        {
            if (!File.Exists(ruta)) return null;
            await using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<EstadoBackup>(fs, Json, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.LogWarning("No pude leer el estado anterior {Ruta}: {Error}", ruta, ex.Message);
            return null;
        }
    }

    /// <summary>Escritura atomica (tmp + rename) para que el sistema de reportes nunca lea un JSON a medias.</summary>
    public async Task GuardarAsync(EstadoBackup estado, CancellationToken ct)
    {
        string ruta = Ruta(estado.Estacion, estado.Base);
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        string tmp = ruta + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(estado, Json), new UTF8Encoding(false), ct);
        File.Move(tmp, ruta, overwrite: true);
    }
}
