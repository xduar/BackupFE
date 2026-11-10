using System;

namespace BackupFE
{
    static class Program
    {
        static int Main(string[] args)
        {
            var a = new Argumentos(args);
            try
            {
                switch (a.Modo)
                {
                    case "instalar": return Instalador.Ejecutar(a, false);
                    case "revisar": return Instalador.Ejecutar(a, true);
                    case "backup": return Respaldo.Ejecutar(a, false);
                    case "preflight": return Respaldo.Ejecutar(a, true);
                    case "reporte": return Reporte.Ejecutar(a);
                    case "":
                    case "ayuda":
                    case "/?":
                        Ayuda();
                        return 0;
                    default:
                        Pantalla.Linea("FALLO", "Modo desconocido: " + a.Modo);
                        Ayuda();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Pantalla.Linea("FALLO", "Error inesperado: " + ex.Message);
                Console.WriteLine(ex);
                return 1;
            }
        }

        static void Ayuda()
        {
            Console.WriteLine();
            Console.WriteLine("BackupFE " + Const.Version + " - backups FULL de SQL Server al NAS (Grupo FE)");
            Console.WriteLine();
            Console.WriteLine("En el servidor de la estacion (PowerShell como administrador):");
            Console.WriteLine("  BackupFE.exe revisar                   revisa todo sin cambiar nada");
            Console.WriteLine("  BackupFE.exe instalar                  instala (detecta la estacion por la IP)");
            Console.WriteLine("  BackupFE.exe instalar -estacion \"1.TEXACO AGUILARES\"");
            Console.WriteLine("     opciones: -bases \"A,B\"  -dia Lunes  -hora 00:30  -crearCarpeta  -sinBackup");
            Console.WriteLine("               -carpetaLocal \"ruta\"  -usuario Administrador  -nas \"ruta\"");
            Console.WriteLine("  BackupFE.exe backup -base JosaraEstacion    hace el backup (lo usa la tarea programada)");
            Console.WriteLine("  BackupFE.exe preflight                 revisa sin hacer backup (lo usa el instalador)");
            Console.WriteLine();
            Console.WriteLine("Desde tu PC:");
            Console.WriteLine("  BackupFE.exe reporte  [-base X] [-estaciones estaciones.csv] [-nas \"ruta\"]");
            Console.WriteLine();
        }
    }
}
