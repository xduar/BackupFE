using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace BackupFE
{
    /// <summary>
    /// Modos "instalar" (9 pasos) y "revisar" (pasos 1 a 4, no cambia nada).
    /// Es idempotente: volver a ejecutarlo reemplaza programa, configuracion y tareas.
    /// </summary>
    class Instalador
    {
        class Plan
        {
            public string Base;
            public int Dia;
            public string Hora;
            public InfoBase Info;
        }

        readonly Argumentos a;
        readonly bool revisar;
        readonly int total;
        Log log;
        SqlConnection con;
        bool admin;
        string nas, estacion, usuario, cuenta, clave, carpetaLocal, exeDestino;
        int diaForzado = -1;
        string horaForzada;
        List<InfoBase> todas;
        readonly List<Plan> plan = new List<Plan>();

        public static int Ejecutar(Argumentos a, bool revisar) => new Instalador(a, revisar).Correr();

        Instalador(Argumentos a, bool revisar)
        {
            this.a = a;
            this.revisar = revisar;
            total = revisar ? 4 : 9;
        }

        int Correr()
        {
            string err = revisar
                ? a.Validar("estacion", "nas", "bases", "carpetaLocal")
                : a.Validar("estacion", "nas", "bases", "dia", "hora", "carpetaLocal", "usuario", "crearCarpeta", "sinBackup");
            if (err != null) { Pantalla.Linea("FALLO", err); return 1; }

            nas = (a.Valor("nas") ?? Const.NasPorDefecto).TrimEnd('\\');
            usuario = a.Valor("usuario") ?? "Administrador";
            cuenta = Environment.MachineName + "\\" + usuario;
            if (a.Valor("dia") != null)
            {
                diaForzado = Dias.Indice(a.Valor("dia"));
                if (diaForzado < 0) { Pantalla.Linea("FALLO", "Dia no valido: " + a.Valor("dia") + ". Usa Lunes, Martes, Miercoles, ..."); return 1; }
            }
            if (a.Valor("hora") != null)
            {
                horaForzada = Dias.Hora(a.Valor("hora"));
                if (horaForzada == null) { Pantalla.Linea("FALLO", "Hora no valida: " + a.Valor("hora") + ". Usa formato de 24 horas, por ejemplo 00:30"); return 1; }
            }

            Console.WriteLine();
            Console.WriteLine("BackupFE " + Const.Version + " - " + (revisar ? "REVISION (no cambia nada)" : "INSTALACION") + " - equipo " + Environment.MachineName);

            try
            {
                if (!Paso1() || !Paso2() || !Paso3() || !Paso4()) return Fin(false);
                if (revisar) return Fin(true);
                if (!Paso5() || !Paso6() || !Paso7() || !Paso8()) return Fin(false);
                return Paso9();
            }
            catch (Exception ex)
            {
                if (log == null) log = new Log(null);
                string extra = ex is COMException ce && (uint)ce.ErrorCode == 0x8007052E ? " (usuario o contrasena incorrectos)" : "";
                log.Fallo("Error inesperado: " + ex.Message + extra);
                return Fin(false);
            }
            finally
            {
                con?.Dispose();
                log?.Dispose();
            }
        }

        bool Paso1()
        {
            admin = Util.EsAdmin();
            if (!revisar && admin)
            {
                try { log = new Log(Path.Combine(Const.CarpetaLogs, "INSTALAR_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log")); }
                catch { log = new Log(null); }
            }
            else log = new Log(null);

            log.Paso(1, total, "Permisos de administrador");
            if (admin) { log.Ok("Se esta ejecutando como administrador (" + Util.UsuarioActual + ")"); return true; }
            if (revisar)
            {
                log.Aviso("No eres administrador: algunas revisiones pueden fallar. Para instalar, abre PowerShell como administrador.");
                return true;
            }
            log.Fallo("Hay que ejecutarlo como administrador: clic derecho en PowerShell > Ejecutar como administrador.");
            return false;
        }

        bool Paso2()
        {
            log.Paso(2, total, "Carpeta de la estacion en el NAS");
            if (!Directory.Exists(nas))
            {
                log.Fallo("No llego al NAS: " + nas);
                log.Info("Revisa la red y que esa ruta abra en el Explorador de archivos.");
                return false;
            }
            log.Ok("NAS accesible: " + nas);

            estacion = a.Valor("estacion");
            if (estacion == null)
            {
                estacion = DetectarEstacion(out string ip);
                if (estacion == null)
                {
                    log.Fallo("No pude saber que estacion es este servidor. Indicala con -estacion \"N.NOMBRE\"");
                    MostrarCarpetas();
                    return false;
                }
                log.Info("Estacion detectada por la IP " + ip + ": " + estacion);
                if (!revisar && !Pantalla.Preguntar("        Es correcta la estacion \"" + estacion + "\"?"))
                {
                    log.Fallo("Cancelado. Indica la estacion con -estacion \"N.NOMBRE\"");
                    return false;
                }
            }

            string dirEstacion = Path.Combine(nas, estacion);
            if (!Directory.Exists(dirEstacion))
            {
                if (a.Bandera("crearCarpeta") && !revisar)
                {
                    Directory.CreateDirectory(dirEstacion);
                    log.Ok("Carpeta creada: " + dirEstacion);
                }
                else
                {
                    log.Fallo("No existe la carpeta \"" + estacion + "\" en el NAS.");
                    MostrarCarpetas();
                    log.Info("Si es una estacion nueva, agrega -crearCarpeta al comando de instalar.");
                    return false;
                }
            }
            else log.Ok("Carpeta de la estacion: " + dirEstacion);

            string dirAno = Path.Combine(dirEstacion, DateTime.Now.Year.ToString());
            string dirPrueba = dirAno;
            if (!Directory.Exists(dirAno))
            {
                if (revisar) dirPrueba = dirEstacion;
                else Directory.CreateDirectory(dirAno);
            }
            string e = Util.ProbarEscritura(dirPrueba);
            if (e != null) { log.Fallo("No puedo escribir en " + dirPrueba + ": " + e); return false; }
            log.Ok("Escritura en el NAS: OK (" + dirPrueba + ")");

            if (!revisar)
            {
                string dirEstado = Path.Combine(nas, "_Estado", estacion);
                Directory.CreateDirectory(dirEstado);
                e = Util.ProbarEscritura(dirEstado);
                if (e != null) { log.Fallo("No puedo escribir en " + dirEstado + ": " + e); return false; }
                log.Ok("Carpeta de estado para el reporte: " + dirEstado);
            }
            return true;
        }

        bool Paso3()
        {
            log.Paso(3, total, "SQL Server, bases y otros procesos de backup");
            try { con = Sql.Abrir("."); }
            catch (Exception ex) { log.Fallo("No pude conectar a SQL Server: " + ex.Message); return false; }
            log.Ok("SQL Server " + Sql.Version(con) + " (" + Sql.Edicion(con) + ")");

            todas = Sql.Bases(con);
            string pedidas = a.Valor("bases");
            if (pedidas != null)
            {
                foreach (string p in pedidas.Split(',', ';').Select(x => x.Trim()).Where(x => x.Length > 0))
                {
                    var info = todas.FirstOrDefault(x => x.Nombre.Equals(p, StringComparison.OrdinalIgnoreCase));
                    if (info == null)
                    {
                        log.Fallo("La base " + p + " no existe en este servidor. Bases que hay: " + string.Join(", ", todas.Select(x => x.Nombre)));
                        return false;
                    }
                    if (!plan.Any(x => x.Base == info.Nombre)) plan.Add(new Plan { Base = info.Nombre, Info = info });
                }
            }
            else
            {
                foreach (string conocida in Const.BasesConocidas)
                {
                    var info = todas.FirstOrDefault(x => x.Nombre.Equals(conocida, StringComparison.OrdinalIgnoreCase));
                    if (info == null) continue;
                    if (conocida == "EDEMEstacion")
                    {
                        log.Aviso(info.Nombre + " existe pero NO se instala hasta resolver el tema EDEM. Para incluirla a proposito usa -bases.");
                        continue;
                    }
                    plan.Add(new Plan { Base = info.Nombre, Info = info });
                }
            }
            if (plan.Count == 0)
            {
                log.Fallo("No encontre JosaraEstacion ni GFEEstacion en este servidor. Bases que hay: " + string.Join(", ", todas.Select(x => x.Nombre)));
                return false;
            }

            foreach (var p in plan)
            {
                HorarioPorDefecto(p.Base, out int dia, out string hora);
                p.Dia = diaForzado >= 0 ? diaForzado : dia;
                p.Hora = horaForzada ?? hora;
                if (p.Info.Estado != "ONLINE") { log.Fallo(p.Base + ": la base esta " + p.Info.Estado + " (debe estar ONLINE)"); return false; }
                log.Ok(p.Base + ": se programara " + Dias.Nombre(p.Dia) + " " + p.Hora + " - datos " + p.Info.DatosMB.ToString("N0") +
                       " MB, log " + p.Info.LogMB.ToString("N0") + " MB, recovery " + p.Info.Recuperacion);
                if (p.Info.LogMB > 3 * Math.Max(p.Info.DatosMB, 1))
                    log.Aviso(p.Base + ": el log es " + (p.Info.LogMB / Math.Max(p.Info.DatosMB, 1)).ToString("N1") +
                              " veces los datos (motivo: " + p.Info.EsperaLog + "). Cada backup sera mucho mas grande de lo necesario.");
            }

            RevisarOtrosBackups();
            return true;
        }

        static void HorarioPorDefecto(string nombre, out int dia, out string hora)
        {
            switch (nombre.ToLowerInvariant())
            {
                case "josaraestacion": dia = 1; hora = "00:00"; break;
                case "gfeestacion": dia = 1; hora = "00:30"; break;
                case "edemestacion": dia = 2; hora = "02:00"; break;
                default: dia = 1; hora = "01:00"; break;
            }
        }

        /// <summary>Busca otros procesos que hagan backups para detectar choques (solo lectura).</summary>
        void RevisarOtrosBackups()
        {
            try
            {
                var t = Sql.Tabla(con, @"
SELECT TOP 10 bs.database_name, bs.backup_finish_date, bs.user_name, bmf.physical_device_name,
       CAST(bs.compressed_backup_size / 1048576 AS bigint) AS mb
FROM msdb.dbo.backupset bs JOIN msdb.dbo.backupmediafamily bmf ON bmf.media_set_id = bs.media_set_id
WHERE bs.type = 'D' AND bs.backup_finish_date > DATEADD(day, -21, GETDATE())
ORDER BY bs.backup_finish_date DESC");
                if (t.Rows.Count == 0) log.Info("SQL Server no registra backups completos en los ultimos 21 dias.");
                else
                {
                    log.Info("Ultimos backups completos que registra SQL Server (21 dias):");
                    foreach (DataRow r in t.Rows)
                        log.Info("  " + Convert.ToDateTime(r["backup_finish_date"]).ToString("yyyy-MM-dd HH:mm") + "  " + r["database_name"] +
                                 "  " + r["mb"] + " MB  por " + r["user_name"] + "  ->  " + r["physical_device_name"]);
                }
            }
            catch (Exception ex) { log.Aviso("No pude leer el historial de backups de SQL Server: " + ex.Message); }

            try
            {
                var t = Sql.Tabla(con, @"
SELECT DISTINCT j.name, j.enabled
FROM msdb.dbo.sysjobs j JOIN msdb.dbo.sysjobsteps s ON s.job_id = j.job_id
WHERE s.command LIKE '%BACKUP%' OR s.subsystem = 'SSIS'");
                foreach (DataRow r in t.Rows)
                    log.Aviso("Job del Agente SQL que puede hacer backups: \"" + r["name"] + "\" (" +
                              (Convert.ToInt32(r["enabled"]) == 1 ? "activo" : "desactivado") + ")");
            }
            catch { }

            try
            {
                var otras = Tareas.OtrasDeBackup();
                foreach (var o in otras)
                    log.Aviso("Otra tarea programada que puede hacer backups: " + o.Ruta + "  ->  " + o.Accion + "  (proxima: " + o.Proxima + ")");
                if (otras.Count == 0) log.Ok("No hay otras tareas programadas de backup");
                else log.Info("Si otro proceso hace backups a la misma hora, BackupFE espera a que termine, pero conviene desactivarlo.");
            }
            catch (Exception ex) { log.Aviso("No pude revisar las tareas programadas: " + ex.Message); }
        }

        bool Paso4()
        {
            log.Paso(4, total, "Carpeta local de backups");
            carpetaLocal = a.Valor("carpetaLocal") ?? Sql.CarpetaBackup(con) ?? Const.CarpetaLocalPorDefecto;
            if (!Directory.Exists(carpetaLocal))
            {
                log.Fallo("No existe la carpeta local de backups: " + carpetaLocal);
                log.Info("Indicala con -carpetaLocal \"ruta\"");
                return false;
            }
            string e = Util.ProbarEscritura(carpetaLocal);
            if (e != null)
            {
                if (!admin) log.Aviso("No pude escribir en " + carpetaLocal + " (normal si no eres administrador): " + e);
                else { log.Fallo("No puedo escribir en " + carpetaLocal + ": " + e); return false; }
            }
            double libre = Util.LibreGB(carpetaLocal);
            double necesario = plan.Max(p => p.Info.NecesarioGB);
            if (libre < necesario)
                log.Aviso("Poco espacio en " + carpetaLocal + ": libre " + libre.ToString("N1") + " GB y la base mas grande pide " +
                          necesario.ToString("N1") + " GB. El backup fallara hasta que haya espacio.");
            else log.Ok("Carpeta local: " + carpetaLocal + " (libre " + libre.ToString("N1") + " GB)");
            return true;
        }

        bool Paso5()
        {
            log.Paso(5, total, "Programa y configuracion en " + Const.CarpetaPrograma);
            Directory.CreateDirectory(Const.CarpetaPrograma);
            exeDestino = Path.Combine(Const.CarpetaPrograma, "BackupFE.exe");
            string exeActual = Util.ExeActual;
            if (!string.Equals(Path.GetFullPath(exeActual), Path.GetFullPath(exeDestino), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(exeActual, exeDestino, true); }
                catch (IOException ex)
                {
                    log.Fallo("No pude copiar el programa: " + ex.Message);
                    log.Info("Puede haber un backup en curso usando " + exeDestino + ". Espera a que termine y vuelve a intentarlo.");
                    return false;
                }
            }
            string h1 = Util.Sha256(exeActual), h2 = Util.Sha256(exeDestino);
            if (h1 != h2) { log.Fallo("La copia de " + exeDestino + " no coincide con el original (hash distinto)"); return false; }
            log.Ok("Programa copiado y verificado: " + exeDestino + " (SHA256 " + h2.Substring(0, 16) + "...)");

            var cfg = new Config
            {
                estacion = estacion,
                nas = nas,
                servidorSql = ".",
                carpetaLocal = carpetaLocal,
                carpetaLogs = Const.CarpetaLogs,
                usuario = usuario,
                bases = plan.Select(p => p.Base).ToList(),
                version = Const.Version,
                instalado = Util.F(DateTime.Now)
            };
            string rutaCfg = Path.Combine(Const.CarpetaPrograma, "config.json");
            cfg.Guardar(rutaCfg);
            var leida = Config.Cargar(rutaCfg);
            if (leida.estacion != estacion || leida.carpetaLocal != carpetaLocal || !leida.bases.SequenceEqual(cfg.bases))
            {
                log.Fallo("config.json no quedo como se escribio: " + rutaCfg);
                return false;
            }
            log.Ok("config.json escrito y verificado: estacion \"" + estacion + "\", bases " + string.Join(", ", cfg.bases));
            return true;
        }

        bool Paso6()
        {
            log.Paso(6, total, "Contrasena de la cuenta .\\" + usuario);
            log.Info("Las tareas se ejecutan con esta cuenta porque es la que tiene acceso al NAS.");
            for (int intento = 1; intento <= 3; intento++)
            {
                Console.Write("        Escribe la contrasena de .\\" + usuario + " (intento " + intento + " de 3): ");
                string c = Pantalla.LeerClave();
                int codigo = Cuentas.Validar(usuario, c);
                if (codigo == 0)
                {
                    clave = c;
                    log.Ok("Contrasena validada");
                    return true;
                }
                if (codigo == 1385 || codigo == 1331 || codigo == 1330)
                {
                    log.Fallo("La cuenta no sirve para tareas programadas: " + Cuentas.Explicar(codigo));
                    return false;
                }
                log.Aviso("Contrasena rechazada: " + Cuentas.Explicar(codigo));
            }
            log.Fallo("Tres intentos fallidos. No se creo ni cambio ninguna tarea.");
            return false;
        }

        bool Paso7()
        {
            log.Paso(7, total, "Tareas programadas");
            int rc = Util.Proceso("wevtutil.exe", "set-log Microsoft-Windows-TaskScheduler/Operational /enabled:true", null);
            if (rc == 0) log.Ok("Historial del Programador de tareas activado");
            else log.Aviso("No pude activar el historial del Programador de tareas (wevtutil codigo " + rc + ")");

            if (Tareas.Existe(Const.TareaAntigua))
            {
                Tareas.Borrar(Const.TareaAntigua);
                log.Ok("Eliminada la tarea antigua \"" + Const.TareaAntigua + "\"");
            }
            foreach (string nombre in Tareas.NombresEnRaiz().Where(n => n.StartsWith(Const.PrefijoTarea, StringComparison.OrdinalIgnoreCase)))
            {
                string b = nombre.Substring(Const.PrefijoTarea.Length);
                if (plan.Any(p => p.Base.Equals(b, StringComparison.OrdinalIgnoreCase))) continue;
                if (!todas.Any(x => x.Nombre.Equals(b, StringComparison.OrdinalIgnoreCase)))
                {
                    Tareas.Borrar(nombre);
                    log.Ok("Eliminada \"" + nombre + "\": esa base ya no existe en este servidor");
                }
                else log.Aviso("La tarea \"" + nombre + "\" ya existia y NO se toca (esa base no esta en esta instalacion)");
            }

            foreach (var p in plan)
            {
                string nombre = Const.PrefijoTarea + p.Base;
                string xml = Tareas.Xml("BackupFE " + Const.Version + ": backup FULL de " + p.Base + " al NAS", cuenta, "Password",
                                        exeDestino, "backup -base \"" + p.Base + "\"", Const.CarpetaPrograma, Dias.Ingles(p.Dia), p.Hora);
                Tareas.Registrar(nombre, xml, cuenta, clave, Tareas.LogonConContrasena);
                var t = Tareas.Leer(nombre);
                string texto = "\"" + nombre + "\": " + Dias.DeMascara(t?.Dias ?? 0) + " " + t?.Hora + ", cuenta " + t?.Usuario +
                               ", proxima " + (t?.Proxima.ToString("yyyy-MM-dd HH:mm") ?? "?");
                bool bien = t != null && t.Activa && t.Dias == Dias.Mascara(p.Dia) && t.Hora == p.Hora &&
                            string.Equals(t.Comando, exeDestino, StringComparison.OrdinalIgnoreCase);
                if (bien) log.Ok(texto);
                else { log.Fallo("La tarea quedo distinta a lo pedido: " + texto); return false; }
            }
            return true;
        }

        bool Paso8()
        {
            log.Paso(8, total, "Prueba con la cuenta de la tarea (revision sin backup)");
            Tareas.Borrar(Const.TareaPrueba);
            string xml = Tareas.Xml("BackupFE: prueba temporal del instalador (se borra sola)", cuenta, "Password",
                                    exeDestino, "preflight", Const.CarpetaPrograma, null, null);
            Tareas.Registrar(Const.TareaPrueba, xml, cuenta, clave, Tareas.LogonConContrasena);
            DateTime desde = DateTime.Now;
            Tareas.Ejecutar(Const.TareaPrueba);
            log.Info("Ejecutando la prueba con la cuenta .\\" + usuario + " (puede tardar un par de minutos)...");
            int? resultado = Tareas.EsperarFin(Const.TareaPrueba, desde, TimeSpan.FromMinutes(10));

            var archivoLog = new DirectoryInfo(Const.CarpetaLogs).GetFiles("PREFLIGHT_*.log")
                .Where(f => f.LastWriteTime >= desde.AddSeconds(-5))
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (archivoLog != null)
            {
                log.Info("Resultado de la prueba (" + archivoLog.FullName + "):");
                foreach (string l in Util.LeerLineas(archivoLog.FullName)) log.Info("  | " + l);
            }
            try { Tareas.Borrar(Const.TareaPrueba); } catch { }

            if (resultado == null) { log.Fallo("La prueba no termino en 10 minutos"); return false; }
            if (resultado == 0) { log.Ok("La tarea funciona con la cuenta y la contrasena guardadas"); return true; }
            log.Fallo("La prueba termino con codigo " + Tareas.Codigo(resultado.Value) + ": " + Tareas.ExplicarCodigo(resultado.Value));
            return false;
        }

        int Paso9()
        {
            log.Paso(9, total, "Backup real de prueba");
            if (a.Bandera("sinBackup"))
            {
                log.Info("Omitido (-sinBackup). El primer backup sera en la fecha programada.");
                return Fin(true);
            }
            var p = plan[0];
            string tarea = Const.PrefijoTarea + p.Base;
            log.Info("Recomendado para confirmar que todo funciona de punta a punta. " + p.Base + " ocupa unos " +
                     (p.Info.DatosMB + p.Info.LogMB).ToString("N0") + " MB; con el NAS lento puede tardar horas.");
            if (!Pantalla.Preguntar("        Hacer AHORA un backup real de " + p.Base + "?"))
            {
                log.Info("Omitido. Para lanzarlo despues: schtasks /run /tn \"" + tarea + "\"");
                return Fin(true);
            }

            DateTime desde = DateTime.Now;
            Tareas.Ejecutar(tarea);
            log.Info("Backup iniciado. Puedes cerrar esta ventana: el backup sigue solo en segundo plano.");
            int resultado = SeguirBackup(tarea, p.Base, desde);
            if (resultado == 0)
            {
                log.Ok("El backup de prueba termino bien");
                return Fin(true);
            }
            log.Fallo("El backup de prueba termino con codigo " + Tareas.Codigo(resultado) + ": " + Tareas.ExplicarCodigo(resultado));
            return Fin(false, "LAS TAREAS QUEDARON INSTALADAS, PERO EL BACKUP DE PRUEBA FALLO: revisa el log de arriba");
        }

        /// <summary>Muestra el log del backup mientras avanza, hasta que la tarea termina.</summary>
        int SeguirBackup(string tarea, string nombreBase, DateTime desde)
        {
            string archivoLog = null;
            int mostradas = 0;
            Thread.Sleep(2000);
            while (true)
            {
                var t = Tareas.Leer(tarea);
                bool termino = t != null && !t.Corriendo && t.UltimaEjecucion >= desde.AddSeconds(-5);
                if (archivoLog == null)
                    archivoLog = new DirectoryInfo(Const.CarpetaLogs).GetFiles(nombreBase + "_*.log")
                        .Where(f => f.LastWriteTime >= desde.AddSeconds(-5))
                        .OrderByDescending(f => f.LastWriteTime).Select(f => f.FullName).FirstOrDefault();
                if (archivoLog != null)
                {
                    var lineas = Util.LeerLineas(archivoLog);
                    for (; mostradas < lineas.Count; mostradas++) Console.WriteLine("    | " + lineas[mostradas]);
                }
                if (termino) return t.UltimoResultado;
                Thread.Sleep(3000);
            }
        }

        int Fin(bool ok, string texto = null)
        {
            ok = ok && log.Fallos == 0;
            Console.WriteLine();
            if (texto == null)
            {
                if (revisar) texto = ok ? "REVISION TERMINADA: se puede instalar" : "REVISION TERMINADA: hay problemas que corregir antes de instalar";
                else texto = ok ? "INSTALACION TERMINADA" : "LA INSTALACION NO SE COMPLETO: corrige el FALLO y vuelve a ejecutar el mismo comando";
            }
            if (ok) log.Ok(texto);
            else log.Fallo(texto);
            if (log.Avisos > 0) log.Info("Hay " + log.Avisos + " aviso(s): leelos arriba.");
            if (log.Ruta != null) log.Info("Registro de esta instalacion: " + log.Ruta);
            return ok ? 0 : 1;
        }

        string DetectarEstacion(out string ipUsada)
        {
            ipUsada = null;
            var ips = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => u.Address.ToString())
                .Where(s => s.StartsWith("192.168.") && !s.StartsWith("192.168.0."))
                .OrderBy(s => s.EndsWith(".2") ? 0 : 1)
                .ToList();
            var carpetas = CarpetasEstacion();
            foreach (string ip in ips)
            {
                string n = ip.Split('.')[2];
                var c = carpetas.Where(x => x.StartsWith(n + ".")).ToList();
                if (c.Count == 1) { ipUsada = ip; return c[0]; }
            }
            return null;
        }

        List<string> CarpetasEstacion() =>
            Directory.GetDirectories(nas).Select(Path.GetFileName)
                .Where(x => Regex.IsMatch(x, @"^\d+\."))
                .OrderBy(x => int.Parse(x.Split('.')[0]))
                .ToList();

        void MostrarCarpetas()
        {
            log.Info("Carpetas de estaciones en el NAS:");
            foreach (string c in CarpetasEstacion()) log.Info("  " + c);
        }
    }
}
