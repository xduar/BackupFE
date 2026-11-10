using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace BackupFE
{
    class FalloBackup : Exception
    {
        public FalloBackup(string mensaje) : base(mensaje) { }
    }

    /// <summary>Modos "backup" y "preflight". Lo ejecuta la tarea programada con la cuenta .\Administrador.</summary>
    static class Respaldo
    {
        public static int Ejecutar(Argumentos a, bool preflight)
        {
            string err = a.Validar("base", "config");
            if (err != null) { Pantalla.Linea("FALLO", err); return 1; }

            string rutaCfg = a.Valor("config") ?? Path.Combine(Util.CarpetaExe, "config.json");
            Config cfg;
            try
            {
                cfg = Config.Cargar(rutaCfg);
            }
            catch (Exception ex)
            {
                Pantalla.Linea("FALLO", "No pude leer " + rutaCfg + ": " + ex.Message);
                Pantalla.Linea("INFO", "Primero instala con: BackupFE.exe instalar");
                return 1;
            }
            string falta = cfg.QueFalta();
            if (falta != null) { Pantalla.Linea("FALLO", "A config.json le falta: " + falta); return 1; }

            List<string> bases = cfg.bases;
            string pedida = a.Valor("base");
            if (pedida != null)
            {
                string enCfg = cfg.bases.FirstOrDefault(x => x.Equals(pedida, StringComparison.OrdinalIgnoreCase));
                if (enCfg == null)
                {
                    Pantalla.Linea("FALLO", "La base " + pedida + " no esta en config.json (bases: " + string.Join(", ", cfg.bases) + ")");
                    return 1;
                }
                bases = new List<string> { enCfg };
            }

            if (preflight) return Preflight(cfg, bases);

            int codigo = 0;
            foreach (string b in bases)
                if (UnaBase(cfg, b) != 0) codigo = 1;
            return codigo;
        }

        static int Preflight(Config cfg, List<string> bases)
        {
            using (var log = new Log(Path.Combine(cfg.carpetaLogs, "PREFLIGHT_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log")))
            {
                log.Info("BackupFE " + Const.Version + " - revision sin backup - estacion " + cfg.estacion +
                         " - equipo " + Environment.MachineName + " - usuario " + Util.UsuarioActual);
                bool ok = true;
                try
                {
                    using (var con = Sql.Abrir(cfg.servidorSql))
                    {
                        log.Ok("Conexion a SQL Server: " + Sql.Version(con) + " (" + Sql.Edicion(con) + ")");
                        foreach (string b in bases)
                            if (!Revisar(cfg, con, b, log, out _)) ok = false;
                    }
                }
                catch (Exception ex)
                {
                    log.Fallo("Error: " + ex.Message);
                    ok = false;
                }
                if (ok) log.Ok("RESULTADO: todo listo para hacer backups");
                else log.Fallo("RESULTADO: hay problemas (ver lineas FALLO)");
                return ok ? 0 : 1;
            }
        }

        /// <summary>Revisa todo lo necesario para hacer el backup de una base, sin hacerlo.</summary>
        static bool Revisar(Config cfg, SqlConnection con, string nombre, Log log, out InfoBase info)
        {
            info = Sql.Bases(con).FirstOrDefault(x => x.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase));
            if (info == null) { log.Fallo(nombre + ": la base no existe en SQL Server"); return false; }
            if (info.Estado != "ONLINE") { log.Fallo(nombre + ": la base esta " + info.Estado + " (debe estar ONLINE)"); return false; }
            log.Ok(nombre + ": ONLINE, datos " + info.DatosMB.ToString("N0") + " MB, log " + info.LogMB.ToString("N0") + " MB, recovery " + info.Recuperacion);
            if (info.LogMB > 3 * Math.Max(info.DatosMB, 1))
                log.Aviso(nombre + ": el log es " + (info.LogMB / Math.Max(info.DatosMB, 1)).ToString("N1") +
                          " veces los datos (motivo: " + info.EsperaLog + "). El backup sera mucho mas grande de lo necesario.");

            bool ok = true;
            double necesario = info.NecesarioGB;

            // Carpeta local
            if (!Directory.Exists(cfg.carpetaLocal))
            {
                log.Fallo("No existe la carpeta local de backups: " + cfg.carpetaLocal);
                ok = false;
            }
            else
            {
                string e = Util.ProbarEscritura(cfg.carpetaLocal);
                if (e != null) { log.Fallo("No puedo escribir en la carpeta local " + cfg.carpetaLocal + ": " + e); ok = false; }
                double libre = Util.LibreGB(cfg.carpetaLocal);
                if (libre < necesario)
                {
                    log.Fallo("Espacio local insuficiente: libre " + libre.ToString("N1") + " GB, se piden " + necesario.ToString("N1") + " GB");
                    ok = false;
                }
                else log.Ok("Espacio local: libre " + libre.ToString("N1") + " GB (se piden " + necesario.ToString("N1") + " GB)");
            }

            // NAS
            string dirEstacion = Path.Combine(cfg.nas, cfg.estacion);
            if (!Directory.Exists(dirEstacion)) { log.Fallo("No llego a la carpeta del NAS: " + dirEstacion); return false; }
            string dirAno = Path.Combine(dirEstacion, DateTime.Now.Year.ToString());
            try { Directory.CreateDirectory(dirAno); }
            catch (Exception ex) { log.Fallo("No pude crear " + dirAno + ": " + ex.Message); return false; }
            string err = Util.ProbarEscritura(dirAno);
            if (err != null) { log.Fallo("No puedo escribir en el NAS (" + dirAno + "): " + err); return false; }
            double libreNas = Util.LibreGB(dirAno);
            if (libreNas < necesario)
            {
                log.Fallo("Espacio insuficiente en el NAS: libre " + libreNas.ToString("N1") + " GB, se piden " + necesario.ToString("N1") + " GB");
                ok = false;
            }
            else log.Ok("NAS: escritura OK en " + dirAno + ", libre " + libreNas.ToString("N0") + " GB");
            return ok;
        }

        static int UnaBase(Config cfg, string nombre)
        {
            DateTime inicio = DateTime.Now;
            string sello = inicio.ToString("yyyyMMdd_HHmmss");
            using (var log = new Log(Path.Combine(cfg.carpetaLogs, nombre + "_" + sello + ".log")))
            {
                log.Info("BackupFE " + Const.Version + " - backup FULL de " + nombre + " - estacion " + cfg.estacion +
                         " - equipo " + Environment.MachineName + " - usuario " + Util.UsuarioActual);

                string rutaEstado = Estado.Ruta(cfg.nas, cfg.estacion, nombre);
                Estado anterior = Estado.Leer(rutaEstado);
                var estado = new Estado
                {
                    estacion = cfg.estacion,
                    @base = nombre,
                    inicio = Util.F(inicio),
                    ultimoOK = anterior?.ultimoOK,
                    equipo = Environment.MachineName,
                    version = Const.Version,
                    resultado = "EN CURSO",
                    mensaje = "Backup en curso"
                };
                GuardarEstado(estado, rutaEstado, log);

                string archivo = nombre + "_FULL_" + sello + ".bak";
                string local = Path.Combine(cfg.carpetaLocal, archivo);
                string dirAno = Path.Combine(cfg.nas, cfg.estacion, inicio.Year.ToString());
                string enNas = Path.Combine(dirAno, archivo);

                bool localVerificado = false, copiaIniciada = false, copiaVerificada = false;
                string mensaje = null;
                Mutex turno = null;
                bool tengoTurno = false;

                try
                {
                    // 1. Turno: un solo backup a la vez en este servidor, porque el NAS es lento.
                    turno = new Mutex(false, @"Global\BackupFE");
                    tengoTurno = Esperar(turno, TimeSpan.Zero);
                    if (!tengoTurno)
                    {
                        log.Info("Otro backup de este servidor esta en curso. Espero mi turno (maximo 12 horas)...");
                        tengoTurno = Esperar(turno, TimeSpan.FromHours(12));
                        if (!tengoTurno) throw new FalloBackup("Pasaron 12 horas esperando turno; no se hizo el backup");
                        log.Ok("Ya es mi turno");
                    }

                    using (var con = Sql.Abrir(cfg.servidorSql))
                    {
                        string edicion = Sql.Edicion(con);
                        log.Ok("Conexion a SQL Server: " + Sql.Version(con) + " (" + edicion + ")");

                        // 2. Revision previa
                        if (!Revisar(cfg, con, nombre, log, out _))
                            throw new FalloBackup("La revision previa encontro problemas (ver lineas FALLO); no se hizo el backup");
                        EsperarOtroBackup(con, nombre, log);

                        // 3. Backup local
                        bool comprimir = Sql.PermiteCompresion(edicion);
                        log.Info("Paso 1 de 4: backup local en " + local + (comprimir ? "" : " (sin compresion: esta edicion no la permite)"));
                        DateTime t0 = DateTime.Now;
                        Sql.EjecutarLargo(con,
                            "BACKUP DATABASE @b TO DISK = @f WITH " + (comprimir ? "COMPRESSION, " : "") + "CHECKSUM, FORMAT, INIT, STATS = 10",
                            m => log.Info("  SQL: " + m), "@b", nombre, "@f", local);
                        long tamano = new FileInfo(local).Length;
                        log.Ok("Backup local terminado: " + Util.MB(tamano).ToString("N1") + " MB en " + (DateTime.Now - t0).TotalMinutes.ToString("N1") + " min");

                        // 4. Verificacion
                        log.Info("Paso 2 de 4: verificando el backup (RESTORE VERIFYONLY WITH CHECKSUM)");
                        Sql.EjecutarLargo(con, "RESTORE VERIFYONLY FROM DISK = @f WITH CHECKSUM", m => log.Info("  SQL: " + m), "@f", local);
                        localVerificado = true;
                        log.Ok("El backup local es valido");

                        // 5. Copia al NAS y comparacion de tamano
                        log.Info("Paso 3 de 4: copiando al NAS: " + enNas);
                        copiaIniciada = true;
                        DateTime t1 = DateTime.Now;
                        int rc = Util.Robocopy(cfg.carpetaLocal, dirAno, archivo, l => log.Info("  robocopy: " + l));
                        if (rc >= 8) throw new FalloBackup("La copia al NAS fallo (robocopy codigo " + rc + ")");
                        if (!File.Exists(enNas)) throw new FalloBackup("robocopy termino pero el archivo no esta en el NAS");
                        long tamanoNas = new FileInfo(enNas).Length;
                        if (tamanoNas != tamano)
                            throw new FalloBackup("El tamano no coincide: local " + tamano.ToString("N0") + " bytes, NAS " + tamanoNas.ToString("N0") + " bytes");
                        copiaVerificada = true;
                        double segundos = Math.Max((DateTime.Now - t1).TotalSeconds, 1);
                        log.Ok("Copia verificada en el NAS (" + tamano.ToString("N0") + " bytes) en " + (segundos / 60).ToString("N1") +
                               " min, " + (Util.MB(tamano) / segundos).ToString("N2") + " MB/s");

                        estado.tamanoMB = Math.Round(Util.MB(tamano), 1);
                        estado.archivo = enNas;
                    }

                    // 6. Limpieza: solo despues de verificar la copia. Si algo falla aqui, el backup ya esta seguro.
                    log.Info("Paso 4 de 4: limpieza");
                    try
                    {
                        File.Delete(local);
                        log.Ok("Backup local borrado (ya esta verificado en el NAS)");
                    }
                    catch (Exception ex) { log.Aviso("No pude borrar el backup local " + local + ": " + ex.Message); }
                    try { LimpiarLocalesViejos(cfg, nombre, inicio, log); }
                    catch (Exception ex) { log.Aviso("No pude limpiar backups locales viejos: " + ex.Message); }
                    try { Retencion(cfg, nombre, log); }
                    catch (Exception ex) { log.Aviso("No pude aplicar la retencion en el NAS: " + ex.Message); }

                    mensaje = "Backup verificado y copiado al NAS";
                }
                catch (FalloBackup ex)
                {
                    mensaje = ex.Message;
                    log.Fallo(ex.Message);
                }
                catch (Exception ex)
                {
                    mensaje = ex.Message;
                    log.Fallo("Error: " + ex.Message);
                }
                finally
                {
                    if (!copiaVerificada) Deshacer(local, enNas, localVerificado, copiaIniciada, log);
                    if (tengoTurno) turno.ReleaseMutex();
                    turno?.Dispose();
                }

                DateTime fin = DateTime.Now;
                estado.fin = Util.F(fin);
                estado.minutos = Math.Round((fin - inicio).TotalMinutes, 1);
                estado.resultado = copiaVerificada ? "OK" : "FALLO";
                estado.mensaje = mensaje;
                if (copiaVerificada) estado.ultimoOK = Util.F(fin);

                if (copiaVerificada) log.Ok("RESULTADO: OK - " + estado.tamanoMB.ToString("N1") + " MB en " + estado.minutos.ToString("N1") + " min");
                else log.Fallo("RESULTADO: FALLO - " + mensaje);

                GuardarEstado(estado, rutaEstado, log);
                CopiarLogAlNas(cfg, log, inicio);
                return copiaVerificada ? 0 : 1;
            }
        }

        static bool Esperar(Mutex m, TimeSpan tiempo)
        {
            try { return m.WaitOne(tiempo); }
            catch (AbandonedMutexException) { return true; }   // el proceso anterior murio sin soltarlo
        }

        /// <summary>Si otro proceso (no BackupFE) esta haciendo backup de la misma base, espera hasta 6 horas.</summary>
        static void EsperarOtroBackup(SqlConnection con, string nombre, Log log)
        {
            DateTime limite = DateTime.Now.AddHours(6);
            bool avisado = false;
            while (true)
            {
                var t = Sql.Tabla(con, @"
SELECT r.command, r.percent_complete, s.program_name, s.login_name, s.host_name
FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
WHERE r.command LIKE 'BACKUP%' AND r.database_id = DB_ID(@b) AND r.session_id <> @@SPID", "@b", nombre);
                if (t.Rows.Count == 0)
                {
                    if (avisado) log.Ok("El otro backup termino; continuo");
                    return;
                }
                if (!avisado)
                {
                    var r = t.Rows[0];
                    log.Aviso("Otro proceso esta haciendo " + r["command"] + " de " + nombre + " ahora mismo (programa: " + r["program_name"] +
                              ", usuario: " + r["login_name"] + ", equipo: " + r["host_name"] + ", avance " +
                              Convert.ToDouble(r["percent_complete"]).ToString("N0") + "%). Espero a que termine (maximo 6 horas)...");
                    avisado = true;
                }
                if (DateTime.Now > limite) throw new FalloBackup("Otro proceso lleva mas de 6 horas haciendo backup de " + nombre + "; no se hizo el backup");
                Thread.Sleep(60000);
            }
        }

        /// <summary>Si algo fallo: conservar el backup local verificado y borrar la copia parcial del NAS.</summary>
        static void Deshacer(string local, string enNas, bool localVerificado, bool copiaIniciada, Log log)
        {
            if (copiaIniciada)
            {
                try
                {
                    if (File.Exists(enNas)) { File.Delete(enNas); log.Info("Se borro la copia incompleta del NAS"); }
                }
                catch (Exception ex) { log.Aviso("No pude borrar la copia incompleta del NAS " + enNas + ": " + ex.Message); }
            }
            try
            {
                if (!File.Exists(local)) return;
                if (localVerificado)
                {
                    log.Aviso("Se CONSERVA el backup local verificado: " + local);
                }
                else
                {
                    File.Delete(local);
                    log.Info("Se borro el backup local porque no paso la verificacion");
                }
            }
            catch (Exception ex) { log.Aviso("No pude revisar el backup local: " + ex.Message); }
        }

        static Regex Patron(string nombre) =>
            new Regex("^" + Regex.Escape(nombre) + @"_FULL_.*\.bak$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Borra backups locales de esta base que quedaron de corridas fallidas de hace mas de un dia.
        /// Solo se llama cuando ya hay un backup mas nuevo verificado en el NAS.
        /// </summary>
        static void LimpiarLocalesViejos(Config cfg, string nombre, DateTime inicio, Log log)
        {
            var rx = Patron(nombre);
            foreach (var f in new DirectoryInfo(cfg.carpetaLocal).GetFiles("*.bak"))
            {
                if (!rx.IsMatch(f.Name) || f.LastWriteTime > inicio.AddDays(-1)) continue;
                f.Delete();
                log.Info("Borrado backup local viejo de una corrida anterior: " + f.Name);
            }
        }

        /// <summary>
        /// En el NAS, solo en la carpeta de ESTA estacion y solo archivos Base_FULL_*.bak:
        /// borra los de mas de 60 dias y conserva siempre los 2 mas recientes.
        /// </summary>
        static void Retencion(Config cfg, string nombre, Log log)
        {
            var rx = Patron(nombre);
            string dirEstacion = Path.Combine(cfg.nas, cfg.estacion);
            var archivos = Directory.GetDirectories(dirEstacion)
                .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d{4}$"))
                .SelectMany(d => new DirectoryInfo(d).GetFiles("*.bak"))
                .Where(f => rx.IsMatch(f.Name))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
            DateTime limite = DateTime.Now.AddDays(-Const.DiasRetencion);
            int borrados = 0;
            foreach (var f in archivos.Skip(Const.ConservarSiempre).Where(f => f.LastWriteTime < limite))
            {
                f.Delete();
                borrados++;
                log.Info("Retencion: borrado " + f.FullName + " (" + f.LastWriteTime.ToString("yyyy-MM-dd") + ")");
            }
            log.Ok("Retencion: " + borrados + " borrado(s); quedan " + (archivos.Count - borrados) + " backup(s) de " + nombre + " en el NAS");
        }

        static void GuardarEstado(Estado e, string ruta, Log log)
        {
            try { e.Guardar(ruta); }
            catch (Exception ex) { log.Aviso("No pude escribir el estado en el NAS (" + ruta + "): " + ex.Message); }
        }

        static void CopiarLogAlNas(Config cfg, Log log, DateTime inicio)
        {
            try
            {
                // Nunca crear la carpeta de la estacion: si no existe, el nombre esta mal o el NAS no responde.
                string dirEstacion = Path.Combine(cfg.nas, cfg.estacion);
                if (!Directory.Exists(dirEstacion)) { log.Aviso("No copie el log al NAS: no existe " + dirEstacion); return; }
                string dir = Path.Combine(dirEstacion, inicio.Year.ToString(), "Logs");
                Directory.CreateDirectory(dir);
                Util.CopiarAbierto(log.Ruta, Path.Combine(dir, Path.GetFileName(log.Ruta)));
            }
            catch (Exception ex) { log.Aviso("No pude copiar el log al NAS: " + ex.Message); }
        }
    }
}
