using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackupFE.Central.Configuracion;

public interface IStationCatalog
{
    IReadOnlyList<EstacionConfig> Cargar();
}

/// <summary>
/// Junta las estaciones de appsettings (Backup:Estaciones) y del CSV.
/// CSV: cabecera con Carpeta (o Estacion), IP, Base y opcionalmente CarpetaLocal, InstanciaSql.
/// Varias bases: una fila por base, o separadas con ';' en la misma celda. Lineas con '#' se ignoran.
/// </summary>
public sealed class StationCatalog(IOptions<BackupOptions> opciones, ILogger<StationCatalog> log) : IStationCatalog
{
    public IReadOnlyList<EstacionConfig> Cargar()
    {
        var o = opciones.Value;
        var todas = new List<EstacionConfig>(o.Estaciones);

        if (!string.IsNullOrWhiteSpace(o.ArchivoEstaciones))
        {
            string ruta = Path.IsPathRooted(o.ArchivoEstaciones)
                ? o.ArchivoEstaciones
                : Path.Combine(AppContext.BaseDirectory, o.ArchivoEstaciones);
            if (File.Exists(ruta)) todas.AddRange(LeerCsv(ruta));
            else log.LogWarning("No existe el archivo de estaciones {Ruta}", ruta);
        }

        var unidas = todas
            .Where(e => !string.IsNullOrWhiteSpace(e.Nombre))
            .GroupBy(e => e.Nombre.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new EstacionConfig
            {
                Nombre = g.Key,
                Ip = g.Select(e => e.Ip?.Trim()).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "",
                CarpetaLocal = g.Select(e => e.CarpetaLocal).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                InstanciaSql = g.Select(e => e.InstanciaSql).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                Bases = g.SelectMany(e => e.Bases).Select(b => b.Trim()).Where(b => b.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            })
            .ToList();

        var validas = new List<EstacionConfig>();
        foreach (var e in unidas)
        {
            if (string.IsNullOrWhiteSpace(e.Ip)) { log.LogError("Estacion {Estacion} sin IP: se omite", e.Nombre); continue; }
            if (e.Bases.Count == 0) { log.LogError("Estacion {Estacion} sin bases definidas: se omite", e.Nombre); continue; }
            validas.Add(e);
        }

        foreach (var g in validas.GroupBy(e => e.Ip).Where(g => g.Count() > 1))
            log.LogWarning("La IP {Ip} esta repetida en: {Estaciones}", g.Key, string.Join(", ", g.Select(e => e.Nombre)));

        if (!string.IsNullOrWhiteSpace(o.Solo))
        {
            var solo = o.Solo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);
            validas = validas.Where(e => solo.Contains(e.Nombre) || solo.Contains(e.Ip)).ToList();
            log.LogInformation("Filtro Backup:Solo activo: {Cantidad} estacion(es)", validas.Count);
        }

        return validas;
    }

    IEnumerable<EstacionConfig> LeerCsv(string ruta)
    {
        var lineas = File.ReadAllLines(ruta, Encoding.UTF8)
            .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'))
            .ToList();
        if (lineas.Count == 0) yield break;

        var cab = lineas[0].Split(',').Select(c => c.Trim()).ToList();
        int Col(params string[] nombres) => cab.FindIndex(c => nombres.Contains(c, StringComparer.OrdinalIgnoreCase));
        int iNombre = Col("Carpeta", "Estacion"), iIp = Col("IP"), iBase = Col("Base", "Bases"),
            iLocal = Col("CarpetaLocal"), iInst = Col("InstanciaSql", "Instancia");

        if (iNombre < 0 || iIp < 0 || iBase < 0)
        {
            log.LogError("{Ruta}: la cabecera debe tener las columnas Carpeta, IP y Base", ruta);
            yield break;
        }

        foreach (var linea in lineas.Skip(1))
        {
            var c = linea.Split(',');
            string Val(int i) => i >= 0 && i < c.Length ? c[i].Trim() : "";
            yield return new EstacionConfig
            {
                Nombre = Val(iNombre),
                Ip = Val(iIp),
                Bases = Val(iBase).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                CarpetaLocal = Val(iLocal) is { Length: > 0 } l ? l : null,
                InstanciaSql = Val(iInst) is { Length: > 0 } s ? s : null
            };
        }
    }
}
