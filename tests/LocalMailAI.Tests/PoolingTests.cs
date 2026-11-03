using LocalMailAI.Api.Embeddings;

namespace LocalMailAI.Tests;

/// <summary>
/// El post-procesado de los embeddings, con valores a mano y sin cargar el modelo.
///
/// Ninguna de estas operaciones falla con una excepcion si esta mal: dan vectores
/// plausibles y peores, y la busqueda y el clasificador se degradan sin que nada avise.
/// Son exactamente la clase de calculo que hay que fijar con un test.
/// </summary>
public class PoolingTests
{
    [Fact]
    public void El_relleno_no_entra_en_la_media()
    {
        // EL test importante. Si el pooling promediara tambien las posiciones de
        // relleno, el mismo texto daria vectores distintos segun con quien le toque
        // compartir lote, y nada lo delataria.
        float[] dosTokens = [1, 2, /**/ 3, 4];
        float[] mismosDosMasRelleno = [1, 2, /**/ 3, 4, /**/ 99, 99, /**/ 99, 99];

        var sinRelleno = Pooling.MeanPool(dosTokens, [1, 1], dim: 2);
        var conRelleno = Pooling.MeanPool(mismosDosMasRelleno, [1, 1, 0, 0], dim: 2);

        Assert.Equal(sinRelleno, conRelleno);
        Assert.Equal([2f, 3f], sinRelleno);
    }

    [Fact]
    public void La_media_es_de_los_tokens_reales_no_de_todas_las_posiciones()
    {
        // Tres posiciones, dos reales: la media tiene que dividir entre 2, no entre 3.
        float[] capa = [2, /**/ 4, /**/ 1000];

        Assert.Equal([3f], Pooling.MeanPool(capa, [1, 1, 0], dim: 1));
    }

    [Fact]
    public void Una_mascara_entera_a_cero_no_da_NaN()
    {
        // No deberia llegar nunca, pero un NaN se propagaria a todos los cosenos sin
        // decir de donde salio.
        // Un token de dimension 3, con la mascara a cero.
        var v = Pooling.MeanPool([1, 2, 3], [0], dim: 3);

        Assert.All(v, x => Assert.False(float.IsNaN(x)));
    }

    [Fact]
    public void Una_capa_y_una_mascara_que_no_cuadran_fallan_en_vez_de_dar_basura()
    {
        Assert.Throws<ArgumentException>(() => Pooling.MeanPool([1, 2, 3], [1, 1], dim: 2));
    }

    [Fact]
    public void Normalizar_deja_norma_uno()
    {
        var v = Pooling.Normalize([3, 4]);

        Assert.Equal(0.6f, v[0], 5);
        Assert.Equal(0.8f, v[1], 5);
        Assert.Equal(1.0f, MathF.Sqrt(v[0] * v[0] + v[1] * v[1]), 5);
    }

    [Fact]
    public void Normalizar_el_vector_cero_no_revienta()
    {
        var v = Pooling.Normalize([0, 0, 0]);

        Assert.All(v, x => Assert.Equal(0f, x));
    }

    [Fact]
    public void El_recorte_conserva_el_cierre_de_secuencia()
    {
        // Cortar por lo sano dejaria la secuencia sin su </s>, y el modelo se entreno
        // siempre con el.
        var largo = new uint[Pooling.MaxTokens + 50];
        Array.Fill(largo, 7u);
        largo[0] = Pooling.BosId;
        largo[^1] = Pooling.EosId;

        var recortado = Pooling.Truncate(largo);

        Assert.Equal(Pooling.MaxTokens, recortado.Length);
        Assert.Equal(Pooling.BosId, recortado[0]);
        Assert.Equal(Pooling.EosId, recortado[^1]);
    }

    [Fact]
    public void Lo_que_cabe_no_se_toca()
    {
        uint[] corto = [0, 100, 200, 2];

        Assert.Same(corto, Pooling.Truncate(corto));
    }

    [Fact]
    public void El_blob_va_y_vuelve_sin_perder_nada()
    {
        // float32 little-endian es lo que declara la columna `vec` de §4. Si la
        // serializacion se desalineara, los vectores guardados serian ruido.
        float[] original = [0.1f, -0.2f, 3.5f, float.MaxValue, float.MinValue, 0f];

        var vuelta = Pooling.FromBlob(Pooling.ToBlob(original));

        Assert.Equal(original, vuelta);
        Assert.Equal(original.Length * 4, Pooling.ToBlob(original).Length);
    }

    [Fact]
    public void Un_blob_a_medias_se_rechaza()
    {
        Assert.Throws<ArgumentException>(() => Pooling.FromBlob(new byte[7]));
    }

    [Fact]
    public void El_coseno_de_un_vector_consigo_mismo_es_uno()
    {
        var v = Pooling.Normalize([1, 2, 3, 4]);

        Assert.Equal(1.0f, Pooling.Cosine(v, v), 5);
    }

    [Fact]
    public void El_coseno_de_dos_vectores_perpendiculares_es_cero()
    {
        Assert.Equal(0f, Pooling.Cosine([1, 0], [0, 1]), 5);
    }

    [Fact]
    public void Comparar_dimensiones_distintas_falla()
    {
        Assert.Throws<ArgumentException>(() => Pooling.Cosine([1, 0], [0, 1, 0]));
    }
}
