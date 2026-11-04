using System.Buffers.Binary;
using System.Numerics.Tensors;

namespace LocalMailAI.Api.Embeddings;

/// <summary>
/// The embedding post-processing operations, deliberately separated from the model.
///
/// This is where embeddings degrade SILENTLY when something is wrong: none of these
/// functions can fail with an exception, they can only return plausible, worse
/// numbers. They live here with no dependencies so they can be tested with hand-built
/// values, without loading 470 MB of model.
/// </summary>
public static class Pooling
{
    /// <summary>Maximum sequence length of e5-small. More tokens do not fit.</summary>
    public const int MaxTokens = 512;

    /// <summary>XLM-RoBERTa special tokens, verified in milestone 0.</summary>
    public const uint BosId = 0;
    public const uint PadId = 1;
    public const uint EosId = 2;

    /// <summary>
    /// Truncates to <see cref="MaxTokens"/> while keeping the closing token.
    ///
    /// Cutting bluntly would leave the sequence without its trailing &lt;/s&gt;, and the
    /// model was trained with it always present. The last token is replaced rather
    /// than dropped.
    /// </summary>
    public static uint[] Truncate(uint[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Length <= MaxTokens)
        {
            return ids;
        }

        var truncated = ids[..MaxTokens];
        truncated[^1] = EosId;
        return truncated;
    }

    /// <summary>
    /// Mean of the token vectors, weighted by the attention mask.
    ///
    /// Averaging the padding positions too would inject noise proportional to how much
    /// padding the batch happens to have: the SAME text would produce different
    /// vectors depending on who it shares a batch with. It is the easiest mistake to
    /// make here and it produces no visible symptom.
    /// </summary>
    /// <param name="lastHiddenState">last_hidden_state, flattened: tokens * dim.</param>
    /// <param name="mask">1 per real token, 0 per padding position.</param>
    /// <param name="dim">Vector dimension (384 for e5-small).</param>
    public static float[] MeanPool(ReadOnlySpan<float> lastHiddenState, ReadOnlySpan<long> mask, int dim)
    {
        if (dim <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dim), dim, "dimension must be positive");
        }

        if (lastHiddenState.Length != mask.Length * dim)
        {
            throw new ArgumentException(
                $"last_hidden_state has {lastHiddenState.Length} values but the mask has {mask.Length} tokens of {dim} dimensions",
                nameof(lastHiddenState));
        }

        var sum = new float[dim];
        var real = 0;

        for (var t = 0; t < mask.Length; t++)
        {
            if (mask[t] == 0)
            {
                continue;
            }

            real++;
            var from = t * dim;
            for (var i = 0; i < dim; i++)
            {
                sum[i] += lastHiddenState[from + i];
            }
        }

        // A sequence with an all-zero mask should never reach this point, but dividing
        // by zero would produce NaN, and a NaN propagates into every cosine without
        // saying where it came from.
        if (real == 0)
        {
            return sum;
        }

        for (var i = 0; i < dim; i++)
        {
            sum[i] /= real;
        }

        return sum;
    }

    /// <summary>
    /// Normalises to unit length.
    ///
    /// Without this the dot product stops being the cosine and search ranking comes out
    /// in the wrong order: longer texts tend to have larger norms and would win on
    /// length rather than on similarity.
    /// </summary>
    public static float[] Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var norm = MathF.Sqrt(TensorPrimitives.Dot<float>(vector, vector));

        if (norm < 1e-12f)
        {
            return vector;
        }

        TensorPrimitives.Divide<float>(vector, norm, vector);
        return vector;
    }

    /// <summary>
    /// Cosine between two ALREADY normalised vectors: the dot product, SIMD-accelerated.
    /// For fewer than 100,000 messages, brute force is instant (§11), so there is no
    /// approximate index until it has been measured to be necessary.
    /// </summary>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"different dimensions: {a.Length} and {b.Length}", nameof(b));
        }

        return TensorPrimitives.Dot(a, b);
    }

    /// <summary>float32 little-endian, which is what the `vec` column in §4 declares.</summary>
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
            throw new ArgumentException($"{blob.Length} bytes are not whole float32 values", nameof(blob));
        }

        var vector = new float[blob.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(blob[(i * sizeof(float))..]);
        }

        return vector;
    }
}
