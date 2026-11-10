using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BackupFE
{
    /// <summary>Modo "reporte": desde la PC del administrador, lee NAS\_Estado y muestra una fila por base.</summary>
    static class Reporte
    {
        class Fila
        {
            public string Estacion, Base, Estado, UltimoOK, Detalle;
            public int Numero => int.TryParse(Estacion.Split('.')[0], out int n) ? n : 9999;
        }

        public static int Ejecutar(Argumentos a)
        {
            string err = a.Validar("nas", "estaciones", "base");
            if (err != null) { Pantalla.Linea("FALLO", err); return 1; }
            string nas = (a.Valor("nas") ?? Const.NasPorDefecto).TrimEnd('\\');
            if (!Directory.Exists(nas)) { Pantalla.Linea("FALLO", "No llego al NAS: " + nas); return 1; }

            // Estados que escribieron los backups
            var estados = new List<Estado>();
            string dirEstado = Path.Combine(nas, "_Estado");
            if (Directory.Exists(dirEstado))
            {
                foreach (string dir in Directory.GetDirectories(dirEstado))
                    foreach (string f in Directory.GetFiles(dir, "*.json"))
                    {
                        var e = Estado.Leer(f);
                        if (e == null) continue;
                        e.estacion = Path.GetFileName(dir);
                        e.@base = e.@base ?? Path.GetFileNameWithoutExtension(f);
                        estados.Add(e);
                    }
            }

            var filas = new Dictionary<string, Fila>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in estados) filas[e.estacion + "|" + e.@base] = Evaluar(e);

            // Catalogo de estaciones: para mostrar tambien las que nunca han reportado
            string csv = a.Valor("estaciones") ?? new[]
            {
                Path.Combine(Util.CarpetaExe, "estaciones.csv"),
                Path.Combine(Environment.CurrentDirectory, "estaciones.csv")
            }.FirstOrDefault(File.Exists);
            if (csv != null && File.Exists(csv))
            {
                var lineas = File.ReadAllLines(csv, Encoding.UTF8).Where(l => l.Trim().Length > 0).ToList();
                var cab = lineas[0].Split(',').Select(x => x.Trim()).ToList();
                int iCarpeta = cab.IndexOf("Carpeta"), iBase = cab.IndexOf("Base");
                foreach (string l in lineas.Skip(1))
                {
                    var c = l.Split(',');
                    if (iCarpeta < 0 || c.Length <= iCarpeta) continue;
                    string carpeta = c[iCarpeta].Trim();
                    string b = iBase >= 0 && c.Length > iBase ? c[iBase].Trim() : "";
                    if (carpeta.Length == 0) continue;
                    if (b.Length == 0)
                    {
                        if (!estados.Any(e => e.estacion.Equals(carpeta, StringComparison.OrdinalIgnoreCase)))
                            filas[carpeta + "|?"] = new Fila { Estacion = carpeta, Base = "(sin definir)", Estado = "SIN BACKUP", UltimoOK = "", Detalle = "falta definir la base en estaciones.csv" };
                        continue;
                    }
                    if (!filas.ContainsKey(carpeta + "|" + b))
                        filas[carpeta + "|" + b] = new Fila { Estacion = carpeta, Base = b, Estado = "SIN BACKUP", UltimoOK = "", Detalle = "BackupFE nunca ha reportado" };
                }
            }

            var lista = filas.Values.AsEnumerable();
            string filtro = a.Valor("base");
            if (filtro != null) lista = lista.Where(f => f.Base.Equals(filtro, StringComparison.OrdinalIgnoreCase));
            var orden = lista.OrderBy(f => f.Numero).ThenBy(f => f.Estacion).ThenBy(f => f.Base).ToList();

            int ancho = 120;
            try { if (!Console.IsOutputRedirected) ancho = Math.Max(Console.WindowWidth - 1, 80); } catch { }

            Console.WriteLine();
            Console.WriteLine("Estado de los backups - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " - " + nas);
            Console.WriteLine();
            string cabecera = "Estacion".PadRight(28) + "Base".PadRight(17) + "Estado".PadRight(11) + "Ultimo OK".PadRight(18) + "Detalle";
            Console.WriteLine(cabecera);
            Console.WriteLine(new string('-', Math.Min(ancho, 120)));
            foreach (var f in orden)
            {
                Console.Write(Cortar(f.Estacion, 27).PadRight(28) + Cortar(f.Base, 16).PadRight(17));
                Colorear(f.Estado);
                Console.Write(f.Estado.PadRight(11));
                try { Console.ResetColor(); } catch { }
                Console.WriteLine((f.UltimoOK ?? "").PadRight(18) + Cortar(f.Detalle ?? "", Math.Max(ancho - 74, 20)));
            }
            Console.WriteLine();
            Console.WriteLine("Total: " + string.Join("  ", orden.GroupBy(f => f.Estado).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count())));
            if (csv == null) Pantalla.Linea("AVISO", "No encontre estaciones.csv: solo se muestran las estaciones que ya reportaron.");
            return orden.All(f => f.Estado == "OK" || f.Estado == "EN CURSO") ? 0 : 1;
        }

        static Fila Evaluar(Estado e)
        {
            DateTime ahora = DateTime.Now;
            DateTime? inicio = Util.LeerFecha(e.inicio), ok = Util.LeerFecha(e.ultimoOK);
            var f = new Fila { Estacion = e.estacion, Base = e.@base, UltimoOK = ok?.ToString("yyyy-MM-dd HH:mm") ?? "nunca" };
            switch ((e.resultado ?? "").ToUpperInvariant())
            {
                case "EN CURSO":
                    if (inicio.HasValue && (ahora - inicio.Value).TotalHours < 26)
                    {
                        f.Estado = "EN CURSO";
                        f.Detalle = "desde " + inicio.Value.ToString("yyyy-MM-dd HH:mm");
                    }
                    else
                    {
                        f.Estado = "FALLO";
                        f.Detalle = "se interrumpio: quedo en curso desde " + (inicio?.ToString("yyyy-MM-dd HH:mm") ?? "?");
                    }
                    break;
                case "FALLO":
                    f.Estado = "FALLO";
                    f.Detalle = e.mensaje;
                    break;
                case "OK":
                    if (ok.HasValue && (ahora - ok.Value).TotalDays > 8)
                    {
                        f.Estado = "ATRASADO";
                        f.Detalle = "ultimo backup bueno hace " + (int)(ahora - ok.Value).TotalDays + " dias";
                    }
                    else
                    {
                        f.Estado = "OK";
                        f.Detalle = e.tamanoMB.ToString("N0") + " MB en " + e.minutos.ToString("N0") + " min";
                    }
                    break;
                default:
                    f.Estado = "FALLO";
                    f.Detalle = "estado desconocido: " + e.resultado;
                    break;
            }
            return f;
        }

        static void Colorear(string estado)
        {
            try
            {
                switch (estado)
                {
                    case "OK": Console.ForegroundColor = ConsoleColor.Green; break;
                    case "EN CURSO": Console.ForegroundColor = ConsoleColor.Cyan; break;
                    case "ATRASADO": Console.ForegroundColor = ConsoleColor.Yellow; break;
                    default: Console.ForegroundColor = ConsoleColor.Red; break;
                }
            }
            catch { }
        }

        static string Cortar(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "~";
    }
}
