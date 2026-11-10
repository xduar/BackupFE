using System.IO;
using BackupFE.Central.Abstracciones;
using BackupFE.Central.Configuracion;
using BackupFE.Central.Orquestacion;
using BackupFE.Central.Servicios;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Serilog;

// Utilidad: cifrar una clave para appsettings.json →  BackupFE.Central.exe proteger "MiClave"
if (args.Length == 2 && args[0].Equals("proteger", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(Secretos.Proteger(args[1]));
    return 0;
}

try
{
    // ContentRoot = carpeta del .exe: el Programador de tareas arranca en System32 y no encontraria appsettings.json.
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });

    // Logging: Serilog configurado desde appsettings (archivo diario + consola)
    builder.Services.AddSerilog((_, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext());

    // Configuracion fuertemente tipada y validada al arrancar
    builder.Services.AddOptions<BackupOptions>()
        .Bind(builder.Configuration.GetSection(BackupOptions.Seccion))
        .Validate(o => !string.IsNullOrWhiteSpace(o.RutaNas), "Backup:RutaNas es obligatorio")
        .Validate(o => o.MaxConcurrencia is >= 1 and <= 5, "Backup:MaxConcurrencia debe estar entre 1 y 5")
        .Validate(o => o.Retencion.ConservarSiempre >= 1 && o.Retencion.Dias >= 1, "Backup:Retencion invalida")
        .Validate(o => o.Reintentos.Intentos >= 0 && o.Reintentos.EsperaSegundos >= 0, "Backup:Reintentos invalido")
        .ValidateOnStart();

    builder.Services.AddOptions<CredencialesOptions>()
        .Bind(builder.Configuration.GetSection(CredencialesOptions.Seccion))
        .PostConfigure(c =>
        {
            c.AdminClave = Secretos.Resolver(c.AdminClave) ?? "";
            c.SqlClave = Secretos.Resolver(c.SqlClave);
        })
        .Validate(c => !string.IsNullOrWhiteSpace(c.AdminUsuario) && !string.IsNullOrWhiteSpace(c.AdminClave),
            "Faltan Credenciales:AdminUsuario / AdminClave")
        .Validate(c => c.UsaSqlIntegrada || !string.IsNullOrEmpty(c.SqlClave), "Falta Credenciales:SqlClave")
        .ValidateOnStart();

    // Polly: reintentos de la copia por red (por defecto 5 reintentos, cada 30 s, espera constante)
    builder.Services.AddResiliencePipeline(UncNetworkCopier.Pipeline, (pipeline, ctx) =>
    {
        var r = ctx.ServiceProvider.GetRequiredService<IOptions<BackupOptions>>().Value.Reintentos;
        var log = ctx.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("BackupFE.CopiaRed");
        pipeline.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = Math.Max(r.Intentos, 1),
            Delay = TimeSpan.FromSeconds(r.EsperaSegundos),
            BackoffType = DelayBackoffType.Constant,
            UseJitter = false,
            // Cortes de red, recurso no disponible, tamaño distinto (IntegridadException hereda de IOException).
            // No se reintenta si el origen no existe ni por falta de permisos.
            ShouldHandle = new PredicateBuilder().Handle<IOException>(ex => ex is not FileNotFoundException),
            OnRetry = a =>
            {
                log.LogWarning("Copia por red fallo ({Error}). Reintento {Intento}/{Max} en {Espera:N0} s",
                    a.Outcome.Exception?.Message, a.AttemptNumber + 1, r.Intentos, a.RetryDelay.TotalSeconds);
                return default;
            }
        });
    });

    // Servicios (todos sin estado → singleton)
    builder.Services.AddSingleton<IStationCatalog, StationCatalog>();
    builder.Services.AddSingleton<IBackupService, SqlBackupService>();
    builder.Services.AddSingleton<INetworkCopier, UncNetworkCopier>();
    builder.Services.AddSingleton<IRetentionManager, RetentionManager>();
    builder.Services.AddSingleton<IStatusStore, JsonStatusStore>();
    builder.Services.AddSingleton<StationBackupJob>();
    builder.Services.AddSingleton<BackupOrchestrator>();
    builder.Services.AddHostedService<BackupWorker>();

    using var host = builder.Build();
    await host.RunAsync();
    return Environment.ExitCode;
}
catch (Exception ex)
{
    // Errores antes de que Serilog exista (appsettings invalido, validacion de opciones, etc.)
    Console.Error.WriteLine("FALLO al iniciar: " + ex.Message);
    try
    {
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "inicio-fallido.log"),
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}");
    }
    catch { /* sin donde escribir */ }
    return 1;
}
