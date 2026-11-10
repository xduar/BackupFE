using BackupFE.Central.Configuracion;
using BackupFE.Central.Modelos;

namespace BackupFE.Central.Abstracciones;

/// <summary>Todo lo que ocurre dentro de SQL Server de la estacion.</summary>
public interface IBackupService
{
    /// <summary>Revisa que la base exista y este ONLINE; devuelve edicion, tamaños y carpeta de backups de SQL.</summary>
    Task<InfoServidor> InspeccionarAsync(EstacionConfig estacion, string baseDatos, CancellationToken ct);

    /// <summary>
    /// BACKUP DATABASE a una ruta LOCAL de la estacion (ej. C:\Backup\X.bak), sin timeout de comando,
    /// y opcionalmente RESTORE VERIFYONLY. Lanza excepcion si algo falla.
    /// </summary>
    Task<ResultadoBackupSql> BackupAsync(EstacionConfig estacion, string baseDatos, string rutaLocal, bool comprimir, CancellationToken ct);
}

/// <summary>Copia por red con reintentos y verificacion de tamaño.</summary>
public interface INetworkCopier
{
    /// <summary>Copia origen → destino (via ".partial" + rename). Solo vuelve si los tamaños coinciden.</summary>
    Task<ResultadoCopia> CopiarAsync(string origen, string destino, CancellationToken ct);
}

/// <summary>Retencion en el NAS por estacion y base.</summary>
public interface IRetentionManager
{
    /// <summary>
    /// En carpetaEstacionNas\AAAA\ y solo para archivos Base_FULL_*.bak: conserva siempre los N mas recientes
    /// y borra el resto que tenga mas de X dias.
    /// </summary>
    ResultadoRetencion Aplicar(string carpetaEstacionNas, string baseDatos);
}

/// <summary>Archivos JSON de estado en NAS\_Estado\Estacion\Base.json que lee el sistema de reportes.</summary>
public interface IStatusStore
{
    Task<EstadoBackup?> LeerAsync(string estacion, string baseDatos, CancellationToken ct);
    Task GuardarAsync(EstadoBackup estado, CancellationToken ct);
}
