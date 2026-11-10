using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace BackupFE
{
    class InfoTarea
    {
        public string Nombre;
        public string Usuario;
        public string Comando;
        public string Argumentos;
        public int Dias;
        public string Hora;
        public bool Activa;
        public int Estado;            // 2 = en cola, 3 = lista, 4 = ejecutandose
        public DateTime Proxima;
        public DateTime UltimaEjecucion;
        public int UltimoResultado;

        public bool Corriendo => Estado == 2 || Estado == 4;
    }

    class OtraTarea
    {
        public string Ruta;
        public string Accion;
        public string Proxima;
    }

    /// <summary>Programador de tareas de Windows, por COM (sin pasar la contrasena por linea de comandos).</summary>
    static class Tareas
    {
        public const int LogonConContrasena = 1;
        public const int LogonInteractivo = 3;

        static dynamic raiz;

        static dynamic Raiz()
        {
            if (raiz == null)
            {
                dynamic servicio = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                servicio.Connect();
                raiz = servicio.GetFolder("\\");
            }
            return raiz;
        }

        static dynamic Buscar(string nombre)
        {
            try { return Raiz().GetTask(nombre); }
            catch { return null; }
        }

        public static bool Existe(string nombre) => Buscar(nombre) != null;

        /// <summary>Crea o reemplaza la tarea (flag 6 = crear o actualizar).</summary>
        public static void Registrar(string nombre, string xml, string usuario, string clave, int tipoLogon)
        {
            Raiz().RegisterTask(nombre, xml, 6, usuario, clave, tipoLogon, null);
        }

        public static void Borrar(string nombre)
        {
            if (Existe(nombre)) Raiz().DeleteTask(nombre, 0);
        }

        public static void Ejecutar(string nombre)
        {
            dynamic t = Buscar(nombre);
            if (t == null) throw new InvalidOperationException("No existe la tarea " + nombre);
            t.Run(null);
        }

        public static List<string> NombresEnRaiz()
        {
            var l = new List<string>();
            dynamic ts = Raiz().GetTasks(1);
            int n = ts.Count;
            for (int i = 1; i <= n; i++) l.Add((string)ts.Item(i).Name);
            return l;
        }

        /// <summary>Lee la tarea tal como quedo guardada en Windows.</summary>
        public static InfoTarea Leer(string nombre)
        {
            dynamic t = Buscar(nombre);
            if (t == null) return null;
            var i = new InfoTarea
            {
                Nombre = nombre,
                Activa = (bool)t.Enabled,
                Estado = (int)t.State,
                Proxima = (DateTime)t.NextRunTime,
                UltimaEjecucion = (DateTime)t.LastRunTime,
                UltimoResultado = (int)t.LastTaskResult
            };
            dynamic def = t.Definition;
            i.Usuario = (string)def.Principal.UserId;
            dynamic disparadores = def.Triggers;
            if ((int)disparadores.Count > 0)
            {
                dynamic d = disparadores.Item(1);
                if ((int)d.Type == 3) i.Dias = (int)d.DaysOfWeek;
                string inicio = d.StartBoundary;
                if (inicio != null && inicio.Length >= 16) i.Hora = inicio.Substring(11, 5);
            }
            dynamic acciones = def.Actions;
            if ((int)acciones.Count > 0)
            {
                dynamic ac = acciones.Item(1);
                if ((int)ac.Type == 0)
                {
                    i.Comando = (string)ac.Path;
                    i.Argumentos = (string)ac.Arguments;
                }
            }
            return i;
        }

        /// <summary>Espera a que la tarea termine. Devuelve su codigo de salida o null si se acaba el tiempo.</summary>
        public static int? EsperarFin(string nombre, DateTime desde, TimeSpan maximo)
        {
            var limite = DateTime.Now + maximo;
            Thread.Sleep(1500);
            while (true)
            {
                var t = Leer(nombre);
                if (t != null && !t.Corriendo && t.UltimaEjecucion >= desde.AddSeconds(-5)) return t.UltimoResultado;
                if (DateTime.Now > limite) return null;
                Thread.Sleep(2000);
            }
        }

        /// <summary>Tareas fuera de \Microsoft cuyas acciones parecen backups o scripts (posibles choques).</summary>
        public static List<OtraTarea> OtrasDeBackup()
        {
            var res = new List<OtraTarea>();
            Recorrer(Raiz(), res);
            return res;
        }

        static void Recorrer(dynamic carpeta, List<OtraTarea> res)
        {
            string ruta = carpeta.Path;
            if (ruta.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;
            dynamic tareas = carpeta.GetTasks(1);
            int n = tareas.Count;
            for (int i = 1; i <= n; i++)
            {
                try
                {
                    dynamic t = tareas.Item(i);
                    string nombre = t.Name;
                    if (ruta == "\\" && (nombre.StartsWith(Const.PrefijoTarea, StringComparison.OrdinalIgnoreCase)
                                         || nombre.Equals(Const.TareaAntigua, StringComparison.OrdinalIgnoreCase)
                                         || nombre.Equals(Const.TareaPrueba, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    string acciones = AccionesDeXml((string)t.Xml);
                    if (Regex.IsMatch(acciones, @"backup|sqlcmd|osql|robocopy|\.bak|\.ps1|\.bat|\.cmd", RegexOptions.IgnoreCase))
                    {
                        DateTime prox = t.NextRunTime;
                        res.Add(new OtraTarea
                        {
                            Ruta = (string)t.Path,
                            Accion = acciones,
                            Proxima = prox.Year < 2000 ? "sin programar" : prox.ToString("yyyy-MM-dd HH:mm")
                        });
                    }
                }
                catch { }
            }
            dynamic sub = carpeta.GetFolders(0);
            int m = sub.Count;
            for (int i = 1; i <= m; i++)
            {
                try { Recorrer(sub.Item(i), res); } catch { }
            }
        }

        static string AccionesDeXml(string xml)
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            return string.Join(" | ", doc.Descendants(ns + "Exec")
                .Select(e => ((string)e.Element(ns + "Command") + " " + (string)e.Element(ns + "Arguments")).Trim()));
        }

        /// <summary>
        /// XML de la tarea. diaIngles null = sin disparador (solo se ejecuta a mano).
        /// Limite de 24 h, se ejecuta al volver a estar disponible si se perdio la hora.
        /// </summary>
        public static string Xml(string descripcion, string usuario, string tipoLogon, string comando, string argumentos,
                                 string carpetaTrabajo, string diaIngles, string hora)
        {
            string disparador = diaIngles == null ? "" : @"
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>" + DateTime.Today.ToString("yyyy-MM-dd") + "T" + hora + @":00</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByWeek>
        <DaysOfWeek><" + diaIngles + @" /></DaysOfWeek>
        <WeeksInterval>1</WeeksInterval>
      </ScheduleByWeek>
    </CalendarTrigger>
  </Triggers>";
            return @"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>" + SecurityElement.Escape(descripcion) + @"</Description>
  </RegistrationInfo>" + disparador + @"
  <Principals>
    <Principal id=""Author"">
      <UserId>" + SecurityElement.Escape(usuario) + @"</UserId>
      <LogonType>" + tipoLogon + @"</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT24H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>" + SecurityElement.Escape(comando) + @"</Command>
      <Arguments>" + SecurityElement.Escape(argumentos) + @"</Arguments>
      <WorkingDirectory>" + SecurityElement.Escape(carpetaTrabajo) + @"</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
        }

        public static string Codigo(int r) => "0x" + r.ToString("X");

        public static string ExplicarCodigo(int r)
        {
            switch ((uint)r)
            {
                case 0x0: return "correcto";
                case 0x1: return "el programa encontro problemas (ver el resultado arriba)";
                case 0x41301: return "todavia se esta ejecutando";
                case 0x41303: return "nunca se ha ejecutado";
                case 0x41306: return "fue detenida por el Programador (limite de tiempo o a mano)";
                case 0x8007052E: return "usuario o contrasena incorrectos";
                case 0x800710E0: return "el operador o administrador rechazo la solicitud";
                default: return "codigo no reconocido";
            }
        }
    }
}
