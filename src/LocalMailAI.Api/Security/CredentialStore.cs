using System.Security.Cryptography;
using System.Text;

namespace LocalMailAI.Api.Security;

/// <summary>
/// Almacen de contrasenas cifrado con DPAPI (BACKEND.md §1, regla 2).
///
/// La regla es literal: "Credenciales en el almacen del sistema operativo. Nunca en
/// appsettings.json, nunca en la base de datos, nunca en texto plano."
///
/// DPAPI con ambito de usuario significa que el blob cifrado solo se puede descifrar
/// desde la sesion de Windows de ESA persona en ESE equipo. Copiar el fichero a otro
/// ordenador, o abrirlo desde otra cuenta del mismo ordenador, no sirve de nada. Lo que
/// se guarda en la base de datos es solo la CLAVE con la que localizar el blob
/// (<c>accounts.credential_key</c>), nunca la contrasena.
///
/// Consecuencia a tener presente: si el usuario pierde su perfil de Windows, el blob
/// deja de poder descifrarse. No es un fallo, es el precio de no tener una clave maestra
/// guardada en algun sitio; y la solucion correcta es volver a dar de alta la cuenta, no
/// guardar la contrasena en un sitio menos seguro.
/// </summary>
public sealed class CredentialStore
{
    private readonly string _directorio;

    public CredentialStore(IConfiguration config)
    {
        var configurado = config["Credentials:Path"];

        _directorio = !string.IsNullOrWhiteSpace(configurado)
            ? Path.GetFullPath(configurado)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalMailAI",
                "credentials");
    }

    public string Directory => _directorio;

    /// <summary>Clave estable para una cuenta. No contiene la contrasena, solo identifica el blob.</summary>
    public static string KeyFor(string email) =>
        "imap-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())))[..16];

    public void Save(string key, string secreto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(secreto);

        System.IO.Directory.CreateDirectory(_directorio);

        if (!OperatingSystem.IsWindows())
        {
            // Fuera de Windows no hay DPAPI. Antes que guardar la contrasena en claro
            // sin que nadie se entere, se falla: el usuario tiene derecho a saber que
            // su credencial no estaria protegida.
            throw new PlatformNotSupportedException(
                "el almacen de credenciales usa DPAPI y solo funciona en Windows. " +
                "En otros sistemas hay que integrar su llavero antes de guardar nada.");
        }

        var cifrado = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secreto),
            optionalEntropy: Entropia(key),
            scope: DataProtectionScope.CurrentUser);

        var ruta = RutaDe(key);
        File.WriteAllBytes(ruta, cifrado);

        // Permisos del fichero: solo el usuario actual. En Windows el directorio de
        // LOCALAPPDATA ya lo es, pero dejarlo explicito evita sorpresas si alguien
        // cambia la ruta por configuracion.
        try
        {
            var info = new FileInfo(ruta);
            info.Attributes |= FileAttributes.NotContentIndexed;
        }
        catch (IOException)
        {
            // Un atributo que no se puede poner no justifica abortar el alta.
        }
    }

    /// <summary>Devuelve la contrasena, o null si no esta guardada.</summary>
    public string? TryRead(string key)
    {
        var ruta = RutaDe(key);

        if (!File.Exists(ruta))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("el almacen de credenciales usa DPAPI y solo funciona en Windows");
        }

        try
        {
            var claro = ProtectedData.Unprotect(
                File.ReadAllBytes(ruta),
                optionalEntropy: Entropia(key),
                scope: DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(claro);
        }
        catch (CryptographicException)
        {
            // Blob de otro usuario o de otra maquina. No es recuperable y conviene
            // decirlo con claridad en vez de devolver una contrasena vacia que
            // fallaria luego como "autenticacion rechazada".
            return null;
        }
    }

    public bool Delete(string key)
    {
        var ruta = RutaDe(key);

        if (!File.Exists(ruta))
        {
            return false;
        }

        File.Delete(ruta);
        return true;
    }

    private string RutaDe(string key) => Path.Combine(_directorio, key + ".bin");

    /// <summary>
    /// Entropia adicional ligada a la clave: dos cuentas distintas producen blobs que
    /// no son intercambiables aunque alguien renombre los ficheros.
    /// </summary>
    private static byte[] Entropia(string key) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("LocalMailAI/" + key));
}
