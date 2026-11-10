using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace BackupFE
{
    class InfoBase
    {
        public string Nombre;
        public string Estado;
        public string Recuperacion;
        public string EsperaLog;
        public double DatosMB;
        public double LogMB;

        /// <summary>Espacio que se pide libre antes de un backup: 50% de datos + log.</summary>
        public double NecesarioGB => (DatosMB + LogMB) * 0.5 / 1024;
    }

    static class Sql
    {
        public static SqlConnection Abrir(string servidor)
        {
            var c = new SqlConnection("Data Source=" + servidor + ";Initial Catalog=master;Integrated Security=True;Connect Timeout=15;Application Name=BackupFE " + Const.Version);
            c.Open();
            return c;
        }

        /// <summary>Consulta con parametros: Tabla(con, sql, "@a", valorA, "@b", valorB).</summary>
        public static DataTable Tabla(SqlConnection c, string sql, params object[] pares)
        {
            using (var cmd = new SqlCommand(sql, c) { CommandTimeout = 120 })
            {
                for (int i = 0; i + 1 < pares.Length; i += 2)
                    cmd.Parameters.AddWithValue((string)pares[i], pares[i + 1] ?? DBNull.Value);
                var t = new DataTable();
                using (var r = cmd.ExecuteReader()) t.Load(r);
                return t;
            }
        }

        /// <summary>Ejecuta un comando largo (BACKUP, RESTORE) sin limite de tiempo y pasa los mensajes de progreso.</summary>
        public static void EjecutarLargo(SqlConnection c, string sql, Action<string> mensaje, params object[] pares)
        {
            SqlInfoMessageEventHandler h = (s, e) =>
            {
                foreach (SqlError er in e.Errors) mensaje(er.Message);
            };
            c.InfoMessage += h;
            try
            {
                using (var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 })
                {
                    for (int i = 0; i + 1 < pares.Length; i += 2)
                        cmd.Parameters.AddWithValue((string)pares[i], pares[i + 1]);
                    cmd.ExecuteNonQuery();
                }
            }
            finally
            {
                c.InfoMessage -= h;
            }
        }

        public static string Edicion(SqlConnection c) =>
            Convert.ToString(Tabla(c, "SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(128))").Rows[0][0]);

        public static string Version(SqlConnection c) =>
            Convert.ToString(Tabla(c, "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))").Rows[0][0]);

        /// <summary>Express y Web no permiten backups comprimidos.</summary>
        public static bool PermiteCompresion(string edicion) =>
            edicion.IndexOf("Express", StringComparison.OrdinalIgnoreCase) < 0 &&
            edicion.IndexOf("Web", StringComparison.OrdinalIgnoreCase) < 0;

        public static List<InfoBase> Bases(SqlConnection c)
        {
            var t = Tabla(c, @"
SELECT d.name, d.state_desc, d.recovery_model_desc, d.log_reuse_wait_desc,
       SUM(CASE WHEN f.type = 0 THEN CAST(f.size AS bigint) ELSE 0 END) / 128 AS datos_mb,
       SUM(CASE WHEN f.type = 1 THEN CAST(f.size AS bigint) ELSE 0 END) / 128 AS log_mb
FROM sys.databases d JOIN sys.master_files f ON f.database_id = d.database_id
WHERE d.database_id > 4
GROUP BY d.name, d.state_desc, d.recovery_model_desc, d.log_reuse_wait_desc
ORDER BY d.name");
            var l = new List<InfoBase>();
            foreach (DataRow r in t.Rows)
            {
                l.Add(new InfoBase
                {
                    Nombre = Convert.ToString(r["name"]),
                    Estado = Convert.ToString(r["state_desc"]),
                    Recuperacion = Convert.ToString(r["recovery_model_desc"]),
                    EsperaLog = Convert.ToString(r["log_reuse_wait_desc"]),
                    DatosMB = Convert.ToDouble(r["datos_mb"]),
                    LogMB = Convert.ToDouble(r["log_mb"])
                });
            }
            return l;
        }

        /// <summary>Carpeta de backups configurada en SQL Server (null si no se puede leer).</summary>
        public static string CarpetaBackup(SqlConnection c)
        {
            try
            {
                var t = Tabla(c, @"DECLARE @d nvarchar(4000);
EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'Software\Microsoft\MSSQLServer\MSSQLServer', N'BackupDirectory', @d OUTPUT;
SELECT @d;");
                string d = Convert.ToString(t.Rows[0][0]);
                return string.IsNullOrWhiteSpace(d) ? null : d;
            }
            catch
            {
                return null;
            }
        }
    }
}
