using System.Data;
using System.Diagnostics;
using BackupFE.Central.Abstracciones;
using BackupFE.Central.Configuracion;
using BackupFE.Central.Modelos;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackupFE.Central.Servicios;

public sealed class SqlBackupService(
    IOptions<BackupOptions> opciones,
    IOptions<CredencialesOptions> credenciales,
    ILogger<SqlBackupService> log) : IBackupService
{
    const string SqlInspeccion = @"
DECLARE @dir nvarchar(4000);
BEGIN TRY
    EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'Software\Microsoft\MSSQLServer\MSSQLServer', N'BackupDirectory', @dir OUTPUT;
END TRY
BEGIN CATCH
    SET @dir = NULL;
END CATCH;

SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128));

SELECT d.state_desc, d.recovery_model_desc,
       SUM(CASE WHEN f.type = 0 THEN CAST(f.size AS bigint) ELSE 0 END) / 128,
       SUM(CASE WHEN f.type = 1 THEN CAST(f.size AS bigint) ELSE 0 END) / 128
FROM sys.databases d JOIN sys.master_files f ON f.database_id = d.database_id
WHERE d.name = @b
GROUP BY d.state_desc, d.recovery_model_desc;

SELECT @dir;";

    public async Task<InfoServidor> InspeccionarAsync(EstacionConfig estacion, string baseDatos, CancellationToken ct)
    {
        await using var con = CrearConexion(estacion);
        await con.OpenAsync(ct);

        await using var cmd = new SqlCommand(SqlInspeccion, con) { CommandTimeout = 120 };
        cmd.Parameters.Add("@b", SqlDbType.NVarChar, 128).Value = baseDatos;
        await using var r = await cmd.ExecuteReaderAsync(ct);

        await r.ReadAsync(ct);
        string edicion = r.GetString(0), version = r.GetString(1);

        await r.NextResultAsync(ct);
        if (!await r.ReadAsync(ct))
            throw new FalloBackupException($"La base {baseDatos} no existe en {estacion.ServidorSql}");
        string estado = r.GetString(0), recuperacion = r.GetString(1);
        long datosMB = r.GetInt64(2), logMB = r.GetInt64(3);

        await r.NextResultAsync(ct);
        string? dir = await r.ReadAsync(ct) && !r.IsDBNull(0) ? r.GetString(0) : null;

        if (estado != "ONLINE")
            throw new FalloBackupException($"La base {baseDatos} esta {estado} (debe estar ONLINE)");

        return new InfoServidor(edicion, version, estado, recuperacion, datosMB, logMB, string.IsNullOrWhiteSpace(dir) ? null : dir);
    }

    public async Task<ResultadoBackupSql> BackupAsync(EstacionConfig estacion, string baseDatos, string rutaLocal, bool comprimir, CancellationToken ct)
    {
        await using var con = CrearConexion(estacion);
        // Los mensajes de STATS = 10 ("10 percent processed") llegan como InfoMessage mientras el BACKUP corre.
        con.InfoMessage += (_, e) =>
        {
            foreach (SqlError er in e.Errors)
                log.LogInformation("[{Estacion}] SQL {Base}: {Mensaje}", estacion.Nombre, baseDatos, er.Message);
        };
        await con.OpenAsync(ct);

        string with = (comprimir ? "COMPRESSION, " : "") + "CHECKSUM, FORMAT, INIT, STATS = 10";
        if (!comprimir)
            log.LogWarning("[{Estacion}] {Base}: la edicion de SQL no permite COMPRESSION; backup sin comprimir", estacion.Nombre, baseDatos);

        var sw = Stopwatch.StartNew();
        await EjecutarSinTimeoutAsync(con, $"BACKUP DATABASE @b TO DISK = @f WITH {with};", baseDatos, rutaLocal, ct);
        var tBackup = sw.Elapsed;

        var tVerif = TimeSpan.Zero;
        bool verificado = false;
        if (opciones.Value.VerificarBackup)
        {
            sw.Restart();
            await EjecutarSinTimeoutAsync(con, "RESTORE VERIFYONLY FROM DISK = @f WITH CHECKSUM;", null, rutaLocal, ct);
            tVerif = sw.Elapsed;
            verificado = true;
        }

        return new ResultadoBackupSql(tBackup, tVerif, comprimir, verificado);
    }

    /// <summary>
    /// CommandTimeout = 0 → sin limite: un BACKUP de varias horas no se corta a los 30 s por defecto.
    /// El unico corte posible es el CancellationToken (timeout por estacion o parada del servicio),
    /// que envia un "attention" a SQL Server y aborta el BACKUP de forma limpia.
    /// </summary>
    static async Task EjecutarSinTimeoutAsync(SqlConnection con, string sql, string? baseDatos, string ruta, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, con) { CommandTimeout = 0 };
        if (baseDatos is not null) cmd.Parameters.Add("@b", SqlDbType.NVarChar, 128).Value = baseDatos;
        cmd.Parameters.Add("@f", SqlDbType.NVarChar, 4000).Value = ruta;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    SqlConnection CrearConexion(EstacionConfig estacion)
    {
        var c = credenciales.Value;
        var b = new SqlConnectionStringBuilder
        {
            // tcp: evita que SqlClient pruebe Named Pipes (lento en fallar y va por SMB)
            DataSource = estacion.ServidorSql.Contains(':') ? estacion.ServidorSql : "tcp:" + estacion.ServidorSql,
            InitialCatalog = "master",
            ConnectTimeout = opciones.Value.TimeoutConexionSegundos,
            ApplicationName = "BackupFE.Central",
            // SQL Server de las estaciones sin certificado de confianza: cifra si el servidor lo ofrece.
            Encrypt = SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = true,
            Pooling = false   // conexiones largas y pocas: no tiene sentido mantenerlas en un pool
        };
        if (c.UsaSqlIntegrada) b.IntegratedSecurity = true;
        else { b.UserID = c.SqlUsuario; b.Password = c.SqlClave; }
        return new SqlConnection(b.ConnectionString);
    }
}
