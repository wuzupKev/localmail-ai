using LocalMailAI.Api.Security;
using Microsoft.Extensions.Configuration;

namespace LocalMailAI.Tests;

/// <summary>
/// Almacen de credenciales (BACKEND.md §1, regla 2): nunca en texto plano, nunca en la
/// base de datos, nunca en appsettings.
/// </summary>
public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _directorio = Path.Combine(
        Path.GetTempPath(), $"localmail-cred-{Guid.NewGuid():N}");

    private readonly CredentialStore _store;

    public CredentialStoreTests()
    {
        _store = new CredentialStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Credentials:Path"] = _directorio })
            .Build());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directorio))
        {
            Directory.Delete(_directorio, recursive: true);
        }
    }

    [Fact]
    public void La_contrasena_va_y_vuelve()
    {
        var key = CredentialStore.KeyFor("kevin@sagetasolutions.com");

        _store.Save(key, "abcd efgh ijkl mnop");

        Assert.Equal("abcd efgh ijkl mnop", _store.TryRead(key));
    }

    [Fact]
    public void En_disco_no_queda_la_contrasena_en_claro()
    {
        // La comprobacion que de verdad importa: que el fichero no contenga el secreto.
        const string secreto = "contrasena-muy-reconocible-12345";
        var key = CredentialStore.KeyFor("kevin@sagetasolutions.com");

        _store.Save(key, secreto);

        var ficheros = Directory.GetFiles(_directorio);
        Assert.Single(ficheros);

        var bytes = File.ReadAllBytes(ficheros[0]);
        var comoTexto = System.Text.Encoding.UTF8.GetString(bytes);
        var comoUtf16 = System.Text.Encoding.Unicode.GetString(bytes);

        Assert.DoesNotContain(secreto, comoTexto, StringComparison.Ordinal);
        Assert.DoesNotContain(secreto, comoUtf16, StringComparison.Ordinal);
    }

    [Fact]
    public void Una_clave_que_no_existe_devuelve_null_en_vez_de_fallar()
    {
        Assert.Null(_store.TryRead(CredentialStore.KeyFor("nadie@ejemplo.com")));
    }

    [Fact]
    public void Dos_cuentas_distintas_tienen_claves_distintas()
    {
        var a = CredentialStore.KeyFor("kevin@sagetasolutions.com");
        var b = CredentialStore.KeyFor("kevin@personal.com");

        Assert.NotEqual(a, b);

        _store.Save(a, "secreto-a");
        _store.Save(b, "secreto-b");

        Assert.Equal("secreto-a", _store.TryRead(a));
        Assert.Equal("secreto-b", _store.TryRead(b));
    }

    [Fact]
    public void La_clave_no_depende_de_mayusculas_ni_de_espacios()
    {
        Assert.Equal(
            CredentialStore.KeyFor("kevin@sagetasolutions.com"),
            CredentialStore.KeyFor("  Kevin@SagetaSolutions.COM  "));
    }

    [Fact]
    public void La_clave_no_contiene_la_direccion()
    {
        // El nombre del fichero acaba en disco y puede verse en una captura de
        // pantalla o en una copia de seguridad.
        var key = CredentialStore.KeyFor("kevin@sagetasolutions.com");

        Assert.DoesNotContain("kevin", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sagetasolutions", key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Un_blob_de_otra_clave_no_se_puede_descifrar()
    {
        // La entropia adicional esta ligada a la clave: renombrar el fichero no sirve.
        var a = CredentialStore.KeyFor("kevin@sagetasolutions.com");
        var b = CredentialStore.KeyFor("otro@ejemplo.com");

        _store.Save(a, "secreto-de-a");

        var origen = Path.Combine(_directorio, a + ".bin");
        var destino = Path.Combine(_directorio, b + ".bin");
        File.Copy(origen, destino);

        Assert.Null(_store.TryRead(b));
    }

    [Fact]
    public void Borrar_quita_la_credencial()
    {
        var key = CredentialStore.KeyFor("kevin@sagetasolutions.com");
        _store.Save(key, "secreto");

        Assert.True(_store.Delete(key));
        Assert.Null(_store.TryRead(key));
        Assert.False(_store.Delete(key));
    }
}
