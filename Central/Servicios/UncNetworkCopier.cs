using System.Diagnostics;
using BackupFE.Central.Abstracciones;
using BackupFE.Central.Modelos;
using Microsoft.Extensions.Logging;
using Polly.Registry;

namespace BackupFE.Central.Servicios;

/// <summary>
/// Copia \\IP\C$\Backup\X.bak → \\NAS\...\X.bak usando System.IO.File.Copy dentro del pipeline Polly "copia-red"
/// (reintentos ante IOException / tamaño distinto). Escribe primero "X.bak.partial" y solo renombra al final,
/// asi un archivo a medias nunca coincide con el patron _FULL_*.bak ni lo ve la retencion.
/// </summary>
public sealed class UncNetworkCopier(ResiliencePipelineProvider<string> pipelines, ILogger<UncNetworkCopier> log) : INetworkCopier
{
    public const string Pipeline = "copia-red";

    public async Task<ResultadoCopia> CopiarAsync(string origen, string destino, CancellationToken ct)
    {
        var pipeline = pipelines.GetPipeline(Pipeline);
        string parcial = destino + ".partial";
        int intentos = 0;
        long bytes = 0;
        var ultimo = TimeSpan.Zero;
        var total = Stopwatch.StartNew();

        await pipeline.ExecuteAsync(async token =>
        {
            intentos++;
            long esperado = new FileInfo(origen).Length;
            var sw = Stopwatch.StartNew();

            // File.Copy es bloqueante y no se puede cancelar a mitad; se ejecuta en el pool para no
            // frenar al orquestador. La cancelacion se respeta entre intentos.
            await Task.Run(() => File.Copy(origen, parcial, overwrite: true), token);

            long copiado = new FileInfo(parcial).Length;
            if (copiado != esperado)
                throw new IntegridadException($"Tamaño distinto: origen {esperado:N0} bytes, NAS {copiado:N0} bytes");

            bytes = esperado;
            ultimo = sw.Elapsed;
        }, ct);

        File.Move(parcial, destino, overwrite: false);

        var r = new ResultadoCopia(bytes, ultimo, total.Elapsed, intentos);
        log.LogInformation("Copia verificada {Archivo}: {MB:N1} MB en {Segundos:N0} s ({MBps:N2} MB/s), intentos: {Intentos}",
            Path.GetFileName(destino), r.MB, ultimo.TotalSeconds, r.MBps, intentos);
        return r;
    }
}
