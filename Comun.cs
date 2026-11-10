using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace BackupFE
{
    static class Const
    {
        public const string Version = "1.0";
        public const string NasPorDefecto = @"\\192.168.0.3\backup bd negosys";
        public const string CarpetaPrograma = @"C:\Scripts";
        public const string CarpetaLogs = @"C:\LogsBackup";
        public const string CarpetaLocalPorDefecto = @"C:\Program Files\Microsoft SQL Server\MSSQL13.MSSQLSERVER\MSSQL\Backup";
        public const string PrefijoTarea = "Backup SQL Server - ";
        public const string TareaAntigua = "Backup SQL Server Semanal";
        public const string TareaPrueba = "BackupFE - Prueba";
        public const int DiasRetencion = 60;
        public const int ConservarSiempre = 2;
        public static readonly string[] BasesConocidas = { "JosaraEstacion", "GFEEstacion", "EDEMEstacion" };
    }

    /// <summary>Lee la linea de comandos: primero el modo y luego pares -nombre valor.</summary>
    class Argumentos
    {
        public string Modo = "";
        public readonly List<string> Sueltos = new List<string>();
        readonly Dictionary<string, string> valores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Argumentos(string[] args)
        {
            int i = 0;
            if (args.Length > 0 && !args[0].StartsWith("-"))
            {
                Modo = args[0].ToLowerInvariant();
                i = 1;
            }
            for (; i < args.Length; i++)
            {
                string arg = args[i];
                if (!arg.StartsWith("-")) { Sueltos.Add(arg); continue; }
                string nombre = arg.TrimStart('-');
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                {
                    valores[nombre] = args[i + 1];
                    i++;
                }
                else
                {
                    valores[nombre] = "true";
                }
            }
        }

        public string Valor(string nombre) => valores.TryGetValue(nombre, out var v) ? v : null;

        public bool Bandera(string nombre) => valores.ContainsKey(nombre);

        public string Validar(params string[] permitidos)
        {
            foreach (string n in valores.Keys)
                if (!permitidos.Contains(n, StringComparer.OrdinalIgnoreCase))
                    return "Opcion desconocida: -" + n + ". Ejecuta BackupFE.exe sin nada para ver la ayuda.";
            if (Sueltos.Count > 0)
                return "No entiendo: " + string.Join(" ", Sueltos) + ". Si un valor tiene espacios, ponlo entre comillas.";
            return null;
        }
    }

    static class Pantalla
    {
        static readonly object candado = new object();

        public static void Linea(string nivel, string texto)
        {
            lock (candado)
            {
                Color(nivel == "OK" ? ConsoleColor.Green : nivel == "AVISO" ? ConsoleColor.Yellow : nivel == "FALLO" ? ConsoleColor.Red : (ConsoleColor?)null);
                Console.Write(nivel == "INFO" ? "        " : "  " + nivel.PadRight(6));
                Color(null);
                Console.WriteLine(texto);
            }
        }

        public static void Titulo(string texto)
        {
            lock (candado)
            {
                Console.WriteLine();
                Color(ConsoleColor.Cyan);
                Console.WriteLine(texto);
                Color(null);
            }
        }

        static void Color(ConsoleColor? c)
        {
            try
            {
                if (c.HasValue) Console.ForegroundColor = c.Value;
                else Console.ResetColor();
            }
            catch { }
        }

        public static bool Preguntar(string texto)
        {
            Console.Write(texto + " (S/N): ");
            string r = Console.ReadLine();
            return r != null && r.Trim().StartsWith("s", StringComparison.OrdinalIgnoreCase);
        }

        public static string LeerClave()
        {
            if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
            var sb = new StringBuilder();
            while (true)
            {
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Enter) break;
                if (k.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
                    continue;
                }
                if (!char.IsControl(k.KeyChar)) { sb.Append(k.KeyChar); Console.Write('*'); }
            }
            Console.WriteLine();
            return sb.ToString();
        }
    }

    /// <summary>Escribe en pantalla y en un archivo de log con fecha y hora.</summary>
    class Log : IDisposable
    {
        readonly object candado = new object();
        StreamWriter w;
        public string Ruta { get; }
        public int Avisos { get; private set; }
        public int Fallos { get; private set; }

        public Log(string ruta)
        {
            Ruta = ruta;
            if (ruta == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ruta));
            var fs = new FileStream(ruta, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            w = new StreamWriter(fs, new UTF8Encoding(true)) { AutoFlush = true };
        }

        public void Ok(string t) => Escribir("OK", t);
        public void Info(string t) => Escribir("INFO", t);
        public void Aviso(string t) { Avisos++; Escribir("AVISO", t); }
        public void Fallo(string t) { Fallos++; Escribir("FALLO", t); }

        public void Paso(int n, int total, string t)
        {
            lock (candado) w?.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===== [" + n + "/" + total + "] " + t);
            Pantalla.Titulo("[" + n + "/" + total + "] " + t);
        }

        void Escribir(string nivel, string t)
        {
            lock (candado) w?.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + nivel.PadRight(5) + " " + t);
            Pantalla.Linea(nivel, t);
        }

        public void Dispose()
        {
            lock (candado) { w?.Dispose(); w = null; }
        }
    }

    static class Util
    {
        public static string ExeActual => Assembly.GetExecutingAssembly().Location;
        public static string CarpetaExe => Path.GetDirectoryName(ExeActual);
        public static string UsuarioActual => WindowsIdentity.GetCurrent().Name;

        public static bool EsAdmin() =>
            new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        public static string F(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        public static DateTime? LeerFecha(string s)
        {
            if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            return null;
        }

        public static double MB(long bytes) => bytes / 1048576.0;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool GetDiskFreeSpaceEx(string carpeta, out ulong libresUsuario, out ulong total, out ulong libres);

        /// <summary>Espacio libre en GB. Sirve con rutas locales y UNC.</summary>
        public static double LibreGB(string carpeta)
        {
            string r = carpeta.EndsWith("\\") ? carpeta : carpeta + "\\";
            if (!GetDiskFreeSpaceEx(r, out ulong libres, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return libres / 1073741824.0;
        }

        /// <summary>Crea, lee y borra un archivo de prueba. Devuelve null si todo bien o el error.</summary>
        public static string ProbarEscritura(string carpeta)
        {
            string f = Path.Combine(carpeta, "_prueba_backupfe_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            try
            {
                File.WriteAllText(f, "prueba BackupFE " + F(DateTime.Now));
                if (new FileInfo(f).Length == 0) return "el archivo de prueba quedo vacio";
                File.Delete(f);
                return null;
            }
            catch (Exception ex)
            {
                try { File.Delete(f); } catch { }
                return ex.Message;
            }
        }

        public static string Sha256(string ruta)
        {
            using (var s = SHA256.Create())
            using (var f = File.OpenRead(ruta))
                return BitConverter.ToString(s.ComputeHash(f)).Replace("-", "");
        }

        /// <summary>Copia un archivo aunque otro proceso lo tenga abierto para escribir (por ejemplo un log).</summary>
        public static void CopiarAbierto(string origen, string destino)
        {
            using (var i = new FileStream(origen, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var o = new FileStream(destino, FileMode.Create, FileAccess.Write))
                i.CopyTo(o);
        }

        public static List<string> LeerLineas(string ruta)
        {
            var l = new List<string>();
            using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8))
            {
                string s;
                while ((s = r.ReadLine()) != null) l.Add(s);
            }
            return l;
        }

        public static int Proceso(string exe, string argumentos, Action<string> salida)
        {
            var enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            var psi = new ProcessStartInfo(exe, argumentos)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc,
                CreateNoWindow = true
            };
            using (var p = new Process { StartInfo = psi })
            {
                DataReceivedEventHandler h = (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) salida?.Invoke(e.Data.Trim());
                };
                p.OutputDataReceived += h;
                p.ErrorDataReceived += h;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        /// <summary>Copia un archivo con robocopy (reintenta 5 veces cada 30 s). Codigo 8 o mas = fallo.</summary>
        public static int Robocopy(string carpetaOrigen, string carpetaDestino, string archivo, Action<string> salida)
        {
            string args = Q(carpetaOrigen) + " " + Q(carpetaDestino) + " \"" + archivo + "\" /R:5 /W:30 /NP /NJH /NDL";
            return Proceso("robocopy.exe", args, salida);
        }

        static string Q(string ruta)
        {
            ruta = ruta.TrimEnd('\\');
            if (ruta.EndsWith(":")) ruta += "\\.";
            return "\"" + ruta + "\"";
        }
    }

    static class Json
    {
        public static string Escribir(object o) => Bonito(new JavaScriptSerializer().Serialize(o));

        public static T Leer<T>(string ruta) => new JavaScriptSerializer().Deserialize<T>(File.ReadAllText(ruta));

        static string Bonito(string json)
        {
            var sb = new StringBuilder();
            int nivel = 0;
            bool enTexto = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (enTexto)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]);
                    else if (c == '"') enTexto = false;
                    continue;
                }
                switch (c)
                {
                    case '"': enTexto = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(c).Append(json[++i]); break; }
                        nivel++;
                        sb.Append(c).AppendLine().Append(' ', nivel * 2);
                        break;
                    case '}':
                    case ']':
                        nivel--;
                        sb.AppendLine().Append(' ', nivel * 2).Append(c);
                        break;
                    case ',': sb.Append(c).AppendLine().Append(' ', nivel * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>C:\Scripts\config.json: lo escribe el instalador y lo lee el backup.</summary>
    class Config
    {
        public string estacion { get; set; }
        public string nas { get; set; }
        public string servidorSql { get; set; } = ".";
        public string carpetaLocal { get; set; }
        public string carpetaLogs { get; set; } = Const.CarpetaLogs;
        public string usuario { get; set; }
        public List<string> bases { get; set; } = new List<string>();
        public string version { get; set; }
        public string instalado { get; set; }

        public static Config Cargar(string ruta) => Json.Leer<Config>(ruta);

        public void Guardar(string ruta) => File.WriteAllText(ruta, Json.Escribir(this), new UTF8Encoding(false));

        public string QueFalta()
        {
            if (string.IsNullOrWhiteSpace(estacion)) return "estacion";
            if (string.IsNullOrWhiteSpace(nas)) return "nas";
            if (string.IsNullOrWhiteSpace(carpetaLocal)) return "carpetaLocal";
            if (string.IsNullOrWhiteSpace(carpetaLogs)) return "carpetaLogs";
            if (bases == null || bases.Count == 0) return "bases";
            if (string.IsNullOrWhiteSpace(servidorSql)) servidorSql = ".";
            return null;
        }
    }

    /// <summary>NAS\_Estado\estacion\base.json: lo escribe cada backup y lo lee el reporte.</summary>
    class Estado
    {
        public string estacion { get; set; }
        public string @base { get; set; }
        public string resultado { get; set; }
        public string inicio { get; set; }
        public string fin { get; set; }
        public string ultimoOK { get; set; }
        public string mensaje { get; set; }
        public double tamanoMB { get; set; }
        public double minutos { get; set; }
        public string archivo { get; set; }
        public string equipo { get; set; }
        public string version { get; set; }

        public static string Ruta(string nas, string estacion, string nombreBase) =>
            Path.Combine(nas, "_Estado", estacion, nombreBase + ".json");

        public static Estado Leer(string ruta)
        {
            try { return File.Exists(ruta) ? Json.Leer<Estado>(ruta) : null; }
            catch { return null; }
        }

        public void Guardar(string ruta)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ruta));
            File.WriteAllText(ruta, Json.Escribir(this), new UTF8Encoding(false));
        }
    }

    static class Dias
    {
        static readonly string[] Es = { "Domingo", "Lunes", "Martes", "Miercoles", "Jueves", "Viernes", "Sabado" };
        static readonly string[] En = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

        /// <summary>0 = domingo ... 6 = sabado. -1 si no es un dia valido.</summary>
        public static int Indice(string dia)
        {
            if (string.IsNullOrWhiteSpace(dia)) return -1;
            string d = SinTildes(dia.Trim()).ToLowerInvariant();
            for (int i = 0; i < 7; i++)
                if (Es[i].ToLowerInvariant() == d || En[i].ToLowerInvariant() == d) return i;
            return -1;
        }

        public static string Nombre(int i) => Es[i];
        public static string Ingles(int i) => En[i];

        /// <summary>Mascara del Programador de tareas: Dom=1, Lun=2, Mar=4, Mie=8, Jue=16, Vie=32, Sab=64.</summary>
        public static int Mascara(int i) => 1 << i;

        public static string DeMascara(int m)
        {
            var l = new List<string>();
            for (int i = 0; i < 7; i++) if ((m & (1 << i)) != 0) l.Add(Es[i]);
            return l.Count == 0 ? "(sin dia)" : string.Join("+", l);
        }

        /// <summary>Normaliza a HH:mm. null si no es valida.</summary>
        public static string Hora(string h)
        {
            if (h == null) return null;
            if (TimeSpan.TryParseExact(h.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t) && t.TotalHours < 24)
                return t.Hours.ToString("00") + ":" + t.Minutes.ToString("00");
            return null;
        }

        static string SinTildes(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString();
        }
    }

    static class Cuentas
    {
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LogonUser(string usuario, string dominio, string clave, int tipo, int proveedor, out IntPtr token);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        /// <summary>
        /// Valida la contrasena de una cuenta local con un inicio de sesion "por lotes", que es justo
        /// el que usa el Programador de tareas. Devuelve 0 si es correcta o el codigo de error de Windows.
        /// </summary>
        public static int Validar(string usuario, string clave)
        {
            if (LogonUser(usuario, ".", clave, 4, 0, out IntPtr t))
            {
                CloseHandle(t);
                return 0;
            }
            return Marshal.GetLastWin32Error();
        }

        public static string Explicar(int codigo)
        {
            switch (codigo)
            {
                case 1326: return "usuario o contrasena incorrectos";
                case 1327: return "restriccion de la cuenta (por ejemplo, contrasena vacia)";
                case 1330: return "la contrasena expiro";
                case 1331: return "la cuenta esta deshabilitada";
                case 1385: return "la cuenta no tiene el permiso 'Iniciar sesion como proceso por lotes'";
                case 1909: return "la cuenta esta bloqueada por intentos fallidos";
                default: return "error de Windows " + codigo;
            }
        }
    }
}
