namespace BackupFE.Central.Configuracion;

/// <summary>Seccion "Backup" de appsettings.json.</summary>
public sealed class BackupOptions
{
    public const string Seccion = "Backup";

    /// <summary>Raiz del NAS. Los backups quedan en RutaNas\Estacion\Año\.</summary>
    public string RutaNas { get; set; } = @"\\192.168.0.3\backup bd negosys";

    /// <summary>CSV con las estaciones (relativo a la carpeta del .exe o absoluto). Vacio = solo la lista Estaciones.</summary>
    public string? ArchivoEstaciones { get; set; } = "estaciones.csv";

    /// <summary>Estaciones definidas directamente en appsettings (se suman a las del CSV).</summary>
    public List<EstacionConfig> Estaciones { get; set; } = new();

    /// <summary>Opcional: procesar solo estas estaciones (nombres separados por coma). Ej: --Backup:Solo="1.TEXACO AGUILARES"</summary>
    public string? Solo { get; set; }

    /// <summary>Estaciones procesadas a la vez (SemaphoreSlim). Entre 1 y 5.</summary>
    public int MaxConcurrencia { get; set; } = 4;

    /// <summary>Carpeta en el disco de la estacion donde SQL Server escribe el .bak. "auto" = la carpeta de backups configurada en SQL Server.</summary>
    public string CarpetaLocalEstacion { get; set; } = @"C:\Backup";

    /// <summary>Ejecutar RESTORE VERIFYONLY WITH CHECKSUM antes de copiar.</summary>
    public bool VerificarBackup { get; set; } = true;

    public int TimeoutConexionSegundos { get; set; } = 15;

    /// <summary>Tiempo maximo total por estacion (todas sus bases). 0 = sin limite.</summary>
    public int TimeoutEstacionHoras { get; set; } = 8;

    /// <summary>Si es false y no existe RutaNas\Estacion, la estacion falla (protege contra nombres mal escritos).</summary>
    public bool CrearCarpetaEstacion { get; set; }

    public RetencionOptions Retencion { get; set; } = new();
    public ReintentosOptions Reintentos { get; set; } = new();
}

public sealed class RetencionOptions
{
    public int Dias { get; set; } = 60;
    public int ConservarSiempre { get; set; } = 2;
}

public sealed class ReintentosOptions
{
    public int Intentos { get; set; } = 5;
    public int EsperaSegundos { get; set; } = 30;
}

/// <summary>
/// Seccion "Credenciales". Las claves pueden ir cifradas con DPAPI ("dpapi:...", ver comando "proteger")
/// o llegar por variables de entorno (Credenciales__AdminClave) en lugar de texto plano.
/// </summary>
public sealed class CredencialesOptions
{
    public const string Seccion = "Credenciales";

    /// <summary>Administrador local de las estaciones, para \\IP\C$. Ej: "Administrador".</summary>
    public string AdminUsuario { get; set; } = "";
    public string AdminClave { get; set; } = "";

    /// <summary>Login SQL. Vacio = autenticacion integrada de Windows con la cuenta que corre el servicio.</summary>
    public string? SqlUsuario { get; set; }
    public string? SqlClave { get; set; }

    public bool UsaSqlIntegrada => string.IsNullOrWhiteSpace(SqlUsuario);
}

public sealed class EstacionConfig
{
    /// <summary>Nombre de la carpeta de la estacion en el NAS. Ej: "1.TEXACO AGUILARES".</summary>
    public string Nombre { get; set; } = "";
    public string Ip { get; set; } = "";
    public List<string> Bases { get; set; } = new();

    /// <summary>Opcional: sustituye a Backup:CarpetaLocalEstacion para esta estacion.</summary>
    public string? CarpetaLocal { get; set; }

    /// <summary>Opcional: "IP\INSTANCIA" o "IP,puerto". Por defecto, la IP (instancia por defecto).</summary>
    public string? InstanciaSql { get; set; }

    public string ServidorSql => string.IsNullOrWhiteSpace(InstanciaSql) ? Ip : InstanciaSql!;
}
