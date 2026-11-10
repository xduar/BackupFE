using System.Globalization;
using System.Text.RegularExpressions;
using BackupFE.Central.Abstracciones;
using BackupFE.Central.Configuracion;
using BackupFE.Central.Modelos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackupFE.Central.Servicios;

public sealed class RetentionManager(IOptions<BackupOptions> opciones, ILogger<RetentionManager> log) : IRetentionManager
{
    static readonly Regex CarpetaAno = new(@"^\d{4}$", RegexOptions.Compiled);

    public ResultadoRetencion Aplicar(string carpetaEstacionNas, string baseDatos)
    {
        var o = opciones.Value.Retencion;
        // Solo Base_FULL_*.bak de ESTA base; la fecha sale del nombre (yyyyMMdd_HHmmss) o, si no, de LastWriteTime.
        var patron = new Regex("^" + Regex.Escape(baseDatos) + @"_FULL_(.*)\.bak$", RegexOptions.IgnoreCase);

        var archivos = Directory.EnumerateDirectories(carpetaEstacionNas)
            .Where(d => CarpetaAno.IsMatch(Path.GetFileName(d)))
            .SelectMany(d => new DirectoryInfo(d).EnumerateFiles("*_FULL_*.bak"))
            .Select(f => (Archivo: f, M: patron.Match(f.Name)))
            .Where(x => x.M.Success)
            .Select(x => (x.Archivo, Fecha: FechaDelNombre(x.M.Groups[1].Value) ?? x.Archivo.LastWriteTime))
            .OrderByDescending(x => x.Fecha)
            .ToList();

        var limite = DateTime.Now.AddDays(-o.Dias);
        int borrados = 0;
        foreach (var (archivo, fecha) in archivos.Skip(o.ConservarSiempre).Where(x => x.Fecha < limite))
        {
            try
            {
                archivo.Delete();
                borrados++;
                log.LogInformation("Retencion: borrado {Archivo} ({Fecha:yyyy-MM-dd})", archivo.FullName, fecha);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Retencion: no pude borrar {Archivo}: {Error}", archivo.FullName, ex.Message);
            }
        }

        var r = new ResultadoRetencion(borrados, archivos.Count - borrados);
        log.LogInformation("Retencion {Carpeta} / {Base}: {Borrados} borrado(s), quedan {Quedan}",
            Path.GetFileName(carpetaEstacionNas), baseDatos, r.Borrados, r.Quedan);
        return r;
    }

    static DateTime? FechaDelNombre(string sello) =>
        DateTime.TryParseExact(sello, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
