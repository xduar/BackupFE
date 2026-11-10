using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BackupFE.Central.Servicios;

/// <summary>
/// Abre \\IP\C$ con las credenciales del Administrador de la estacion (equivale a "net use" sin letra de unidad).
/// Mientras el objeto vive, File.Copy/Delete sobre esa ruta usan esas credenciales; el NAS sigue usando la
/// cuenta del servicio. Se prefiere a LogonUser+impersonacion porque LOGON32_LOGON_NEW_CREDENTIALS aplicaria
/// las credenciales de la estacion tambien al NAS.
/// </summary>
public sealed class NetworkConnection : IDisposable
{
    const int RecursoDisco = 1;
    const int ErrorCredencialesEnConflicto = 1219;

    readonly string recurso;
    bool liberado;

    NetworkConnection(string recurso) => this.recurso = recurso;

    public static NetworkConnection Abrir(string recurso, string usuario, string clave)
    {
        var nr = new NetResource { dwType = RecursoDisco, lpRemoteName = recurso };
        int rc = WNetAddConnection2(nr, clave, usuario, 0);
        if (rc == ErrorCredencialesEnConflicto)
        {
            // Quedo una conexion de una corrida anterior al mismo recurso: se cierra y se reintenta una vez.
            WNetCancelConnection2(recurso, 0, true);
            rc = WNetAddConnection2(nr, clave, usuario, 0);
        }
        if (rc != 0)
        {
            string pista = rc switch
            {
                5 => " Si la cuenta no es el Administrador integrado, revisa LocalAccountTokenFilterPolicy=1 en la estacion.",
                53 or 67 => " Revisa la IP, el firewall (puerto 445) y que el recurso compartido administrativo exista.",
                1219 => " Este servidor ya tiene otra conexion a esa estacion con otro usuario (revisa 'net use').",
                1326 => " Usuario o contraseña incorrectos.",
                _ => ""
            };
            throw new Win32Exception(rc, $"No pude conectar a {recurso} como {usuario}: {new Win32Exception(rc).Message} (codigo {rc}).{pista}");
        }
        return new NetworkConnection(recurso);
    }

    public void Dispose()
    {
        if (liberado) return;
        liberado = true;
        WNetCancelConnection2(recurso, 0, true);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    sealed class NetResource
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetAddConnection2(NetResource recurso, string? clave, string? usuario, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetCancelConnection2(string nombre, int flags, bool forzar);
}

public static class Rutas
{
    /// <summary>"C:\Backup\X.bak" en la estacion 192.168.1.2 → "\\192.168.1.2\C$\Backup\X.bak".</summary>
    public static string AUnc(string ip, string rutaLocal)
    {
        Validar(rutaLocal);
        return $@"\\{ip}\{char.ToUpperInvariant(rutaLocal[0])}$\{rutaLocal[3..]}";
    }

    /// <summary>Recurso administrativo de la unidad: "\\192.168.1.2\C$".</summary>
    public static string ShareAdmin(string ip, string rutaLocal)
    {
        Validar(rutaLocal);
        return $@"\\{ip}\{char.ToUpperInvariant(rutaLocal[0])}$";
    }

    static void Validar(string ruta)
    {
        if (ruta.Length < 3 || !char.IsLetter(ruta[0]) || ruta[1] != ':' || ruta[2] != '\\')
            throw new ArgumentException($"La carpeta local de la estacion debe ser una ruta local con letra de unidad (ej. C:\\Backup): '{ruta}'");
    }
}

public static class Disco
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetDiskFreeSpaceEx(string carpeta, out ulong libresUsuario, out ulong total, out ulong libres);

    /// <summary>Espacio libre en GB. Funciona con rutas locales y UNC.</summary>
    public static double LibreGB(string carpeta)
    {
        string r = carpeta.EndsWith('\\') ? carpeta : carpeta + "\\";
        if (!GetDiskFreeSpaceEx(r, out ulong libres, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return libres / 1073741824.0;
    }
}

/// <summary>
/// Claves de appsettings cifradas con DPAPI (ambito equipo): "dpapi:BASE64". Solo se descifran en el
/// servidor donde se generaron. Generar con: BackupFE.Central.exe proteger "la-clave"
/// </summary>
public static class Secretos
{
    const string Prefijo = "dpapi:";
    static readonly byte[] Entropia = "BackupFE.Central"u8.ToArray();

    public static string Proteger(string texto) =>
        Prefijo + Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(texto), Entropia, DataProtectionScope.LocalMachine));

    public static string? Resolver(string? valor) =>
        valor is not null && valor.StartsWith(Prefijo, StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(valor[Prefijo.Length..]), Entropia, DataProtectionScope.LocalMachine))
            : valor;
}
