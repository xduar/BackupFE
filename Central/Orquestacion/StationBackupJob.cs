using System.Globalization;
using System.Text.RegularExpressions;
using BackupFE.Central.Abstracciones;
using BackupFE.Central.Configuracion;
using BackupFE.Central.Modelos;
using BackupFE.Central.Servicios;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackupFE.Central.Orquestacion;

/// <summary>Flujo completo de UNA estacion: sus bases se respaldan en secuencia (nunca dos BACKUP a la vez en la misma estacion).</summary>
public sealed class StationBackupJob(
    IBackupService backup,
    INetworkCopier copiador,
    IRetentionManager retencion,
    IStatusStore estados,
    IOptions<BackupOptions> opciones,
    IOptions<CredencialesOptions> credenciales,
    ILogger<StationBackupJob> log)
{
    public const string Version = "2.0-central";

    public async Task<IReadOnlyList<ResultadoBase>> EjecutarAsync(EstacionConfig estacion, CancellationToken ct)
    {
        // Tope de tiempo por estacion: una estacion colgada no bloquea su cupo del semaforo indefinidamente.
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (opciones.Value.TimeoutEstacionHoras > 0)
            limite.CancelAfter(TimeSpan.FromHours(opciones.Value.TimeoutEstacionHoras));

        log.LogInformation("[{Estacion}] Inicio ({Ip}) - bases: {Bases}", estacion.Nombre, estacion.Ip, string.Join(", ", estacion.Bases));
        var resultados = new List<ResultadoBase>();
        foreach (var db in estacion.Bases)
        {
            ct.ThrowIfCancellationRequested();   // parada del servicio: no empezar otra base
            resultados.Add(await UnaBaseAsync(estacion, db, limite.Token));
        }
        return resultados;
    }

    async Task<ResultadoBase> UnaBaseAsync(EstacionConfig est, string db, CancellationToken ct)
    {
        var o = opciones.Value;
        var cred = credenciales.Value;
        DateTime inicio = DateTime.Now;
        string sello = inicio.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string archivo = $"{db}_FULL_{sello}.bak";
        string carpetaEstacionNas = Path.Combine(o.RutaNas, est.Nombre);
        string dirAno = Path.Combine(carpetaEstacionNas, inicio.Year.ToString(CultureInfo.InvariantCulture));
        string enNas = Path.Combine(dirAno, archivo);

        var anterior = await estados.LeerAsync(est.Nombre, db, ct);
        var estado = new EstadoBackup
        {
            Estacion = est.Nombre,
            Base = db,
            Resultado = EstadoBackup.EnCurso,
            Inicio = EstadoBackup.F(inicio),
            UltimoOK = anterior?.UltimoOK,
            Mensaje = "Backup en curso",
            Equipo = Environment.MachineName,
            Version = Version,
            Ip = est.Ip
        };
        await GuardarEstadoAsync(estado);

        string? uncLocal = null;
        bool localVerificado = false, copiaIniciada = false, copiaVerificada = false;
        string mensaje;
        NetworkConnection? red = null;

        try
        {
            // 0. Carpeta de la estacion en el NAS (no se crea por defecto: un nombre mal escrito debe fallar).
            if (!Directory.Exists(carpetaEstacionNas))
            {
                if (!o.CrearCarpetaEstacion)
                    throw new FalloBackupException($"No existe la carpeta de la estacion en el NAS: {carpetaEstacionNas}");
                Directory.CreateDirectory(carpetaEstacionNas);
            }

            // 1. Conexion SQL y revision previa
            var info = await backup.InspeccionarAsync(est, db, ct);
            log.LogInformation("[{Estacion}] {Base}: SQL {Version} ({Edicion}), datos {Datos:N0} MB, log {Log:N0} MB, recovery {Recovery}",
                est.Nombre, db, info.Version, info.Edicion, info.DatosMB, info.LogMB, info.Recuperacion);

            string carpetaLocal = ResolverCarpetaLocal(est, info);
            string rutaLocal = Path.Combine(carpetaLocal, archivo);
            uncLocal = Rutas.AUnc(est.Ip, rutaLocal);
            string dirLocalUnc = Path.GetDirectoryName(uncLocal)!;

            // 2. Acceso administrativo a la estacion y espacio libre (estacion y NAS)
            red = NetworkConnection.Abrir(Rutas.ShareAdmin(est.Ip, rutaLocal), cred.AdminUsuario, cred.AdminClave);
            Directory.CreateDirectory(dirLocalUnc);   // SQL Server no crea la carpeta destino
            VerificarEspacio(dirLocalUnc, info.NecesarioGB, "en la estacion");
            Directory.CreateDirectory(dirAno);
            VerificarEspacio(dirAno, info.NecesarioGB, "en el NAS");

            // 3. Backup en el disco LOCAL de la estacion (sin timeout de comando)
            log.LogInformation("[{Estacion}] {Base}: BACKUP DATABASE → {Ruta}", est.Nombre, db, rutaLocal);
            var sql = await backup.BackupAsync(est, db, rutaLocal, info.PermiteCompresion, ct);
            localVerificado = sql.Verificado;
            long tamano = new FileInfo(uncLocal).Length;
            log.LogInformation("[{Estacion}] {Base}: backup local {MB:N1} MB en {Min:N1} min{Verif}",
                est.Nombre, db, tamano / 1048576.0, sql.DuracionBackup.TotalMinutes,
                sql.Verificado ? $", VERIFYONLY OK en {sql.DuracionVerificacion.TotalMinutes:N1} min" : "");

            // 4. Extraccion por red al NAS (Polly + verificacion de tamaño)
            copiaIniciada = true;
            var copia = await copiador.CopiarAsync(uncLocal, enNas, ct);
            copiaVerificada = true;
            log.LogInformation("[{Estacion}] {Base}: copiado al NAS {MB:N1} MB a {MBps:N2} MB/s ({Intentos} intento(s), {Min:N1} min en total)",
                est.Nombre, db, copia.MB, copia.MBps, copia.Intentos, copia.DuracionTotal.TotalMinutes);

            estado.TamanoMB = Math.Round(copia.MB, 1);
            estado.Archivo = enNas;

            // 5. Limpieza: solo con la copia verificada. Un error aqui NO convierte el backup en FALLO.
            Intentar(() => File.Delete(uncLocal), est, db, "borrar el .bak local de la estacion");
            uncLocal = null;
            Intentar(() => LimpiarLocalesViejos(dirLocalUnc, db, inicio, est), est, db, "limpiar .bak locales viejos");
            Intentar(() => retencion.Aplicar(carpetaEstacionNas, db), est, db, "aplicar la retencion en el NAS");

            mensaje = "Backup verificado y copiado al NAS";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            mensaje = "Cancelado: se agoto el tiempo maximo de la estacion o se detuvo el servicio";
            log.LogError("[{Estacion}] {Base}: {Mensaje}", est.Nombre, db, mensaje);
        }
        catch (Exception ex) when (ex is FalloBackupException or Microsoft.Data.SqlClient.SqlException
                                       or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Errores esperados (negocio, SQL, red, permisos): mensaje claro sin stack trace
            mensaje = ex.Message;
            log.LogError("[{Estacion}] {Base}: {Tipo}: {Mensaje}", est.Nombre, db, ex.GetType().Name, mensaje);
        }
        catch (Exception ex)
        {
            mensaje = ex.Message;
            log.LogError(ex, "[{Estacion}] {Base}: error {Tipo}: {Mensaje}", est.Nombre, db, ex.GetType().Name, ex.Message);
        }
        finally
        {
            if (!copiaVerificada) Deshacer(est, db, uncLocal, enNas, localVerificado, copiaIniciada);
            red?.Dispose();
        }

        DateTime fin = DateTime.Now;
        estado.Fin = EstadoBackup.F(fin);
        estado.Minutos = Math.Round((fin - inicio).TotalMinutes, 1);
        estado.Resultado = copiaVerificada ? EstadoBackup.Ok : EstadoBackup.Fallo;
        estado.Mensaje = mensaje;
        if (copiaVerificada) estado.UltimoOK = EstadoBackup.F(fin);
        await GuardarEstadoAsync(estado);

        log.LogInformation("[{Estacion}] {Base}: RESULTADO {Resultado} en {Min:N1} min - {Mensaje}",
            est.Nombre, db, estado.Resultado, estado.Minutos, mensaje);
        return new ResultadoBase(est.Nombre, db, copiaVerificada, mensaje);
    }

    string ResolverCarpetaLocal(EstacionConfig est, InfoServidor info)
    {
        string carpeta = !string.IsNullOrWhiteSpace(est.CarpetaLocal) ? est.CarpetaLocal! : opciones.Value.CarpetaLocalEstacion;
        if (carpeta.Equals("auto", StringComparison.OrdinalIgnoreCase))
            carpeta = info.CarpetaBackupSql
                ?? throw new FalloBackupException("No pude leer la carpeta de backups de SQL Server; define CarpetaLocal para la estacion");
        return carpeta;
    }

    static void VerificarEspacio(string carpeta, double necesarioGB, string donde)
    {
        double libre = Disco.LibreGB(carpeta);
        if (libre < necesarioGB)
            throw new FalloBackupException($"Espacio insuficiente {donde} ({carpeta}): libre {libre:N1} GB, se piden {necesarioGB:N1} GB");
    }

    /// <summary>Si algo fallo: borra la copia parcial del NAS y conserva el .bak local solo si paso VERIFYONLY.</summary>
    void Deshacer(EstacionConfig est, string db, string? uncLocal, string enNas, bool localVerificado, bool copiaIniciada)
    {
        if (copiaIniciada)
        {
            foreach (var f in new[] { enNas + ".partial", enNas })
                Intentar(() => { if (File.Exists(f)) File.Delete(f); }, est, db, "borrar la copia incompleta del NAS");
        }
        if (uncLocal is null) return;
        if (localVerificado)
        {
            log.LogWarning("[{Estacion}] {Base}: se CONSERVA el backup local verificado {Ruta}", est.Nombre, db, uncLocal);
            return;
        }
        Intentar(() => { if (File.Exists(uncLocal)) File.Delete(uncLocal); }, est, db, "borrar el backup local no verificado");
    }

    /// <summary>Borra .bak locales de esta base que quedaron de corridas fallidas de hace mas de un dia.</summary>
    void LimpiarLocalesViejos(string dirLocalUnc, string db, DateTime inicio, EstacionConfig est)
    {
        var patron = new Regex("^" + Regex.Escape(db) + @"_FULL_.*\.bak$", RegexOptions.IgnoreCase);
        foreach (var f in new DirectoryInfo(dirLocalUnc).EnumerateFiles("*_FULL_*.bak"))
        {
            if (!patron.IsMatch(f.Name) || f.LastWriteTime > inicio.AddDays(-1)) continue;
            f.Delete();
            log.LogInformation("[{Estacion}] {Base}: borrado .bak local viejo {Archivo}", est.Nombre, db, f.Name);
        }
    }

    void Intentar(Action accion, EstacionConfig est, string db, string que)
    {
        try { accion(); }
        catch (Exception ex) { log.LogWarning("[{Estacion}] {Base}: no pude {Que}: {Error}", est.Nombre, db, que, ex.Message); }
    }

    async Task GuardarEstadoAsync(EstadoBackup e)
    {
        try { await estados.GuardarAsync(e, CancellationToken.None); }
        catch (Exception ex) { log.LogWarning("[{Estacion}] {Base}: no pude escribir el estado JSON: {Error}", e.Estacion, e.Base, ex.Message); }
    }
}
