# BackupFE.Central (v2, arquitectura pull)

Un solo proceso en el servidor central: se conecta a cada estación, hace que SQL Server escriba el `.bak`
en su disco local, lo extrae por `\\IP\C$` al NAS y aplica la retención. Reemplaza las 38 tareas programadas de la v1.

```
Central/
├─ Program.cs                     DI, opciones validadas, Serilog, pipeline Polly "copia-red"
├─ appsettings.json               NAS, concurrencia, retención, reintentos, credenciales, Serilog
├─ estaciones.csv                 Carpeta,IP,Base[,CarpetaLocal,InstanciaSql]
├─ Abstracciones/Interfaces.cs    IBackupService, INetworkCopier, IRetentionManager, IStatusStore
├─ Configuracion/                 BackupOptions, CredencialesOptions, StationCatalog (CSV + appsettings)
├─ Modelos/Modelos.cs             resultados, EstadoBackup (mismo JSON que la v1), excepciones
├─ Servicios/
│  ├─ SqlBackupService.cs         BACKUP / VERIFYONLY con CommandTimeout = 0
│  ├─ UncNetworkCopier.cs         File.Copy + Polly + verificación de tamaño (.partial → rename)
│  ├─ RetentionManager.cs         conservar 2 más recientes, borrar > 60 días, solo Base_FULL_*.bak
│  ├─ JsonStatusStore.cs          NAS\_Estado\Estacion\Base.json (escritura atómica)
│  └─ Infraestructura.cs          WNetAddConnection2 (\\IP\C$), rutas UNC, espacio libre, DPAPI
└─ Orquestacion/
   ├─ BackupOrchestrator.cs       SemaphoreSlim (1–5 estaciones a la vez)
   ├─ StationBackupJob.cs         flujo por estación/base
   └─ BackupWorker.cs             una corrida y sale (código 0 OK / 1 fallo / 2 cancelado)
```

## Puesta en marcha

1. `dotnet publish -c Release -r win-x64 --self-contained false -o C:\BackupFE.Central`
2. Llenar `estaciones.csv` con las 38 estaciones (la columna `Carpeta` es el nombre exacto de la carpeta en el NAS).
3. Cifrar claves en el servidor central y pegarlas en `appsettings.json`:
   `BackupFE.Central.exe proteger "ClaveAdministrador"` → `"AdminClave": "dpapi:AQAA..."`.
   Alternativa: variables de entorno `Credenciales__AdminClave`, `Credenciales__SqlClave`.
4. Prueba con una sola estación: `BackupFE.Central.exe --Backup:Solo="1.TEXACO AGUILARES"`
5. Una tarea programada en el servidor central (cuenta con escritura en el NAS, "No iniciar una nueva instancia").

## Requisitos en cada estación

- **SQL remoto**: TCP/IP habilitado, firewall 1433 (o el puerto de la instancia) abierto al servidor central.
- **Login SQL** (`SqlUsuario`): `sysadmin`, o como mínimo `db_backupoperator` en cada base + `VIEW ANY DEFINITION`.
  Vacío = autenticación de Windows con la cuenta de la tarea (solo sirve con dominio).
- **SMB**: firewall 445 abierto y recurso compartido administrativo `C$` activo.
  Si la cuenta no es el Administrador integrado (RID 500): `LocalAccountTokenFilterPolicy = 1`.
- **Carpeta local** (`C:\Backup`): el programa la crea; la cuenta del servicio SQL Server
  (`NT Service\MSSQLSERVER`) debe poder escribir en ella. `"auto"` usa la carpeta de backups de SQL,
  que ya tiene esos permisos.
- **Edición Express/Web**: no admite `COMPRESSION`; se hace el backup sin comprimir y queda un aviso en el log.
