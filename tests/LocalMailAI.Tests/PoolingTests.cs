using LocalMailAI.Api.Embeddings;

namespace LocalMailAI.Tests;

/// <summary>
/// Embedding post-processing, with hand-built values and without loading the model.
///
/// None of these operations throws when it is wrong: they return plausible, worse
/// numbers, and search and the classifier degrade with nothing to warn anyone. They
/// are exactly the kind of arithmetic that has to be pinned by a test.
/// </summary>
public class PoolingTests
{
    [Fact]
    public void Padding_does_not_enter_the_mean()
    {
        // THE important test. If pooling averaged the padding positions too, the same
        // text would produce different vectors depending on who it shares a batch
        // with, and nothing would give it away.
        float[] twoTokens = [1, 2, /**/ 3, 4];
        float[] sameTwoPlusPadding = [1, 2, /**/ 3, 4, /**/ 99, 99, /**/ 99, 99];

        var withoutPadding = Pooling.MeanPool(twoTokens, [1, 1], dim: 2);
        var withPadding = Pooling.MeanPool(sameTwoPlusPadding, [1, 1, 0, 0], dim: 2);

        Assert.Equal(withoutPadding, withPadding);
        Assert.Equal([2f, 3f], withoutPadding);
    }

    [Fact]
    public void The_mean_divides_by_real_tokens_not_by_all_positions()
    {
        // Three positions, two real: the mean must divide by 2, not by 3.
        float[] layer = [2, /**/ 4, /**/ 1000];

        Assert.Equal([3f], Pooling.MeanPool(layer, [1, 1, 0], dim: 1));
    }

    [Fact]
    public void An_all_zero_mask_does_not_produce_NaN()
    {
        // It should never happen, but a NaN would propagate into every cosine without
        // saying where it came from.
        var v = Pooling.MeanPool([1, 2, 3], [0], dim: 3);

        Assert.All(v, x => Assert.False(float.IsNaN(x)));
    }

    [Fact]
    public void A_layer_and_mask_that_do_not_match_fail_instead_of_returning_garbage()
    {
        Assert.Throws<ArgumentException>(() => Pooling.MeanPool([1, 2, 3], [1, 1], dim: 2));
    }

    [Fact]
    public void Normalising_yields_unit_length()
    {
        var v = Pooling.Normalize([3, 4]);

        Assert.Equal(0.6f, v[0], 5);
        Assert.Equal(0.8f, v[1], 5);
        Assert.Equal(1.0f, MathF.Sqrt(v[0] * v[0] + v[1] * v[1]), 5);
    }

    [Fact]
    public void Normalising_the_zero_vector_does_not_blow_up()
    {
        var v = Pooling.Normalize([0, 0, 0]);

        Assert.All(v, x => Assert.Equal(0f, x));
    }

    [Fact]
    public void Truncation_keeps_the_end_of_sequence_token()
    {
        // Cutting bluntly would leave the sequence without its </s>, and the model was
        // trained with it always present.
        var longSequence = new uint[Pooling.MaxTokens + 50];
        Array.Fill(longSequence, 7u);
        longSequence[0] = Pooling.BosId;
        longSequence[^1] = Pooling.EosId;

        var truncated = Pooling.Truncate(longSequence);

        Assert.Equal(Pooling.MaxTokens, truncated.Length);
        Assert.Equal(Pooling.BosId, truncated[0]);
        Assert.Equal(Pooling.EosId, truncated[^1]);
    }

    [Fact]
    public void What_fits_is_left_alone()
    {
        uint[] shortSequence = [0, 100, 200, 2];

        Assert.Same(shortSequence, Pooling.Truncate(shortSequence));
    }

    [Fact]
    public void The_blob_round_trips_without_loss()
    {
        // float32 little-endian is what the `vec` column in §4 declares. If the
        // serialisation went out of alignment, the stored vectors would be noise.
        float[] original = [0.1f, -0.2f, 3.5f, float.MaxValue, float.MinValue, 0f];

        var back = Pooling.FromBlob(Pooling.ToBlob(original));

        Assert.Equal(original, back);
        Assert.Equal(original.Length * 4, Pooling.ToBlob(original).Length);
    }

    [Fact]
    public void A_truncated_blob_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Pooling.FromBlob(new byte[7]));
    }

    [Fact]
    public void The_cosine_of_a_vector_with_itself_is_one()
    {
        var v = Pooling.Normalize([1, 2, 3, 4]);

        Assert.Equal(1.0f, Pooling.Cosine(v, v), 5);
    }

    [Fact]
    public void The_cosine_of_two_perpendicular_vectors_is_zero()
    {
        Assert.Equal(0f, Pooling.Cosine([1, 0], [0, 1]), 5);
    }

    [Fact]
    public void Comparing_different_dimensions_fails()
    {
        Assert.Throws<ArgumentException>(() => Pooling.Cosine([1, 0], [0, 1, 0]));
    }
}
