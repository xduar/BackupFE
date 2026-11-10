using System.Diagnostics;
using BackupFE.Central.Configuracion;
using BackupFE.Central.Modelos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackupFE.Central.Orquestacion;

/// <summary>
/// Lanza todas las estaciones en paralelo, pero un SemaphoreSlim deja pasar solo MaxConcurrencia (1-5) a la vez.
/// La unidad de trabajo es la ESTACION: sus bases van en secuencia dentro del mismo cupo.
/// </summary>
public sealed class BackupOrchestrator(
    IStationCatalog catalogo,
    StationBackupJob trabajo,
    IOptions<BackupOptions> opciones,
    ILogger<BackupOrchestrator> log)
{
    public async Task<ResumenEjecucion> EjecutarAsync(CancellationToken ct)
    {
        var estaciones = catalogo.Cargar();
        int max = opciones.Value.MaxConcurrencia;
        log.LogInformation("Inicio de la corrida: {Estaciones} estacion(es), {Bases} base(s), maximo {Max} en paralelo",
            estaciones.Count, estaciones.Sum(e => e.Bases.Count), max);

        var reloj = Stopwatch.StartNew();
        using var semaforo = new SemaphoreSlim(max, max);

        var tareas = estaciones.Select(async estacion =>
        {
            await semaforo.WaitAsync(ct);
            try
            {
                return await trabajo.EjecutarAsync(estacion, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return estacion.Bases.Select(b => new ResultadoBase(estacion.Nombre, b, false, "Cancelado")).ToList();
            }
            catch (Exception ex)
            {
                // Red de seguridad: StationBackupJob ya captura por base; esto solo evita que una estacion tumbe la corrida.
                log.LogError(ex, "[{Estacion}] Error no controlado", estacion.Nombre);
                return estacion.Bases.Select(b => new ResultadoBase(estacion.Nombre, b, false, ex.Message)).ToList();
            }
            finally
            {
                semaforo.Release();
            }
        }).ToList();

        // Si se cancela mientras una tarea espera turno, WaitAsync lanza antes de entrar al try (sin Release): correcto.
        var resultados = new List<ResultadoBase>();
        foreach (var t in tareas)
        {
            try { resultados.AddRange(await t); }
            catch (OperationCanceledException) { /* nunca llego a tener turno */ }
        }

        var fallos = resultados.Where(r => !r.Ok).ToList();
        log.LogInformation("Fin de la corrida en {Min:N1} min: {Ok} OK, {Fallos} FALLO de {Total}",
            reloj.Elapsed.TotalMinutes, resultados.Count - fallos.Count, fallos.Count, resultados.Count);
        foreach (var f in fallos)
            log.LogWarning("  FALLO {Estacion} / {Base}: {Mensaje}", f.Estacion, f.Base, f.Mensaje);

        return new ResumenEjecucion(resultados.Count, resultados.Count - fallos.Count, fallos);
    }
}
