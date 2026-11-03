using System.Buffers.Binary;
using System.Numerics.Tensors;

namespace LocalMailAI.Api.Embeddings;

/// <summary>
/// Las operaciones del post-procesado de embeddings, separadas del modelo a proposito.
///
/// Aqui es donde los embeddings se degradan EN SILENCIO si algo esta mal: ninguna de
/// estas funciones puede fallar con una excepcion, solo dar numeros plausibles y
/// peores. Estan sueltas y sin dependencias para poder probarlas con valores a mano,
/// sin cargar 470 MB de modelo.
/// </summary>
public static class Pooling
{
    /// <summary>Maximo de e5-small. Mas tokens no caben en el modelo.</summary>
    public const int MaxTokens = 512;

    /// <summary>Tokens especiales de XLM-RoBERTa, verificados en el hito 0.</summary>
    public const uint BosId = 0;
    public const uint PadId = 1;
    public const uint EosId = 2;

    /// <summary>
    /// Recorta a <see cref="MaxTokens"/> conservando el cierre.
    ///
    /// Cortar por lo sano dejaria la secuencia sin su &lt;/s&gt; final, y el modelo se
    /// entreno siempre con el. El ultimo token se sustituye en lugar de descartarse.
    /// </summary>
    public static uint[] Truncate(uint[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Length <= MaxTokens)
        {
            return ids;
        }

        var recortado = ids[..MaxTokens];
        recortado[^1] = EosId;
        return recortado;
    }

    /// <summary>
    /// Media de los vectores de token ponderada por la mascara de atencion.
    ///
    /// Promediar tambien las posiciones de relleno meteria ruido proporcional a cuanto
    /// relleno tenga el lote: el MISMO texto daria vectores distintos segun con quien
    /// le toque compartir lote. Es el error mas facil de cometer aqui y no da ningun
    /// sintoma visible.
    /// </summary>
    /// <param name="ultimaCapa">last_hidden_state aplanado: tokens * dim.</param>
    /// <param name="mascara">1 por token real, 0 por relleno.</param>
    /// <param name="dim">Dimension del vector (384 en e5-small).</param>
    public static float[] MeanPool(ReadOnlySpan<float> ultimaCapa, ReadOnlySpan<long> mascara, int dim)
    {
        if (dim <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dim), dim, "la dimension tiene que ser positiva");
        }

        if (ultimaCapa.Length != mascara.Length * dim)
        {
            throw new ArgumentException(
                $"last_hidden_state tiene {ultimaCapa.Length} valores y la mascara {mascara.Length} tokens de {dim} dimensiones",
                nameof(ultimaCapa));
        }

        var suma = new float[dim];
        var reales = 0;

        for (var t = 0; t < mascara.Length; t++)
        {
            if (mascara[t] == 0)
            {
                continue;
            }

            reales++;
            var desde = t * dim;
            for (var i = 0; i < dim; i++)
            {
                suma[i] += ultimaCapa[desde + i];
            }
        }

        // Una secuencia con la mascara entera a cero no deberia llegar aqui, pero
        // dividir por cero daria NaN y un NaN se propaga a todos los cosenos sin
        // decir de donde vino.
        if (reales == 0)
        {
            return suma;
        }

        for (var i = 0; i < dim; i++)
        {
            suma[i] /= reales;
        }

        return suma;
    }

    /// <summary>
    /// Normaliza a norma 1.
    ///
    /// Sin esto el producto escalar deja de ser el coseno y el ranking de la busqueda
    /// sale mal ordenado: los textos largos tienden a vectores de mayor norma y
    /// ganarian por longitud, no por parecido.
    /// </summary>
    public static float[] Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var norma = MathF.Sqrt(TensorPrimitives.Dot<float>(vector, vector));

        if (norma < 1e-12f)
        {
            return vector;
        }

        TensorPrimitives.Divide<float>(vector, norma, vector);
        return vector;
    }

    /// <summary>
    /// Coseno entre dos vectores YA normalizados: el producto escalar, con SIMD.
    /// Para menos de 100.000 correos esto por fuerza bruta es instantaneo (§11), asi
    /// que no hay indice aproximado hasta haber medido que hace falta.
    /// </summary>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"dimensiones distintas: {a.Length} y {b.Length}", nameof(b));
        }

        return TensorPrimitives.Dot(a, b);
    }

    /// <summary>float32 little-endian, que es lo que declara la columna `vec` de §4.</summary>
    public static byte[] ToBlob(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    public static float[] FromBlob(ReadOnlySpan<byte> blob)
    {
        if (blob.Length % sizeof(float) != 0)
        {
            throw new ArgumentException($"{blob.Length} bytes no son float32 completos", nameof(blob));
        }

        var vector = new float[blob.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(blob[(i * sizeof(float))..]);
        }

        return vector;
    }
}
