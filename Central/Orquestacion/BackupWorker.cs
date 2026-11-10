using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupFE.Central.Orquestacion;

/// <summary>
/// Ejecuta UNA corrida completa y cierra la aplicacion. La periodicidad la da el Programador de tareas
/// del servidor central (una sola tarea en vez de 38). Codigo de salida: 0 = todo OK, 1 = algun fallo, 2 = cancelado.
/// </summary>
public sealed class BackupWorker(BackupOrchestrator orquestador, IHostApplicationLifetime vida, ILogger<BackupWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var r = await orquestador.EjecutarAsync(stoppingToken);
            Environment.ExitCode = r.Fallos.Count == 0 ? 0 : 1;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            log.LogWarning("Corrida cancelada");
            Environment.ExitCode = 2;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Error fatal en la corrida");
            Environment.ExitCode = 1;
        }
        finally
        {
            vida.StopApplication();
        }
    }
}
