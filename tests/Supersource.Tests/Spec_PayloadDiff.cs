using System;
using System.Text;
using Supersource.Compare;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Compare.Tests;

public class Spec_PayloadDiff
{
    static KindSchema Schema(PayloadFormat format, PayloadStride stride = default)
    {
        KindSchema schema = default;
        schema.Payload = format;
        schema.Stride = stride;
        return schema;
    }

    static byte[] Apply(byte[] before, in Operation op)
    {
        if (op.Kind == OpKind.ModifyValue)
        {
            return op.Value;
        }

        byte[] result = new byte[before.Length - op.Count + op.Value.Length];
        Array.Copy(before, 0, result, 0, op.Index);
        Array.Copy(op.Value, 0, result, op.Index, op.Value.Length);
        Array.Copy(before, op.Index + op.Count, result,
            op.Index + op.Value.Length, before.Length - op.Index - op.Count);
        return result;
    }

    static void AssertReproduces(in KindSchema schema, byte[] before, byte[] after)
    {
        bool changed = PayloadDiff.Between(
            in schema, new NodeId(0), before, after, out Operation op);

        if (!changed)
        {
            Assert.Equal(before, after);
            return;
        }

        Assert.Equal(after, Apply(before, in op));
    }

    [Fact]
    public void One_pixel_in_a_large_raster_is_a_small_operation()
    {
        const int Header = 8;
        const int Pixels = 256 * 256;

        byte[] before = new byte[Header + Pixels * 4];
        for (int i = 0; i < before.Length; i++)
        {
            before[i] = (byte)(i * 31);
        }

        byte[] after = (byte[])before.Clone();
        int pixel = 40_000;
        after[Header + pixel * 4 + 2] ^= 0xFF;

        KindSchema schema = Schema(PayloadFormat.Record, PayloadStride.Of(Header, 4));
        Assert.True(PayloadDiff.Between(in schema, new NodeId(0), before, after, out Operation op));

        Assert.Equal(OpKind.Splice, op.Kind);
        Assert.Equal(4, op.Count);
        Assert.Equal(4, op.Value.Length);
        Assert.Equal(Header + pixel * 4, op.Index);
        Assert.Equal(after, Apply(before, in op));

        // And without the stride, the same edit is the whole buffer.
        KindSchema blind = Schema(PayloadFormat.Record);
        Assert.True(PayloadDiff.Between(in blind, new NodeId(0), before, after, out Operation coarse));
        Assert.Equal(OpKind.Splice, coarse.Kind);
        Assert.Equal(1, coarse.Count);   // still finds the byte, just not the element
    }

    [Fact]
    public void Array_splices_land_on_element_boundaries()
    {
        KindSchema schema = Schema(PayloadFormat.Record, PayloadStride.Of(3, 10));

        byte[] before = new byte[3 + 10 * 6];
        for (int i = 0; i < before.Length; i++)
        {
            before[i] = (byte)i;
        }

        byte[] after = (byte[])before.Clone();
        after[3 + 10 * 2 + 7] = 0xAA;   // deep inside the third element
        after[3 + 10 * 3 + 1] = 0xBB;   // and the fourth

        Assert.True(PayloadDiff.Between(in schema, new NodeId(0), before, after, out Operation op));

        Assert.Equal(OpKind.Splice, op.Kind);
        Assert.Equal(0, (op.Index - 3) % 10);
        Assert.Equal(0, op.Count % 10);
        Assert.Equal(0, op.Value.Length % 10);
        Assert.Equal(after, Apply(before, in op));
    }

    [Fact]
    public void A_changed_header_is_spliced_from_the_start()
    {
        KindSchema schema = Schema(PayloadFormat.Record, PayloadStride.Of(4, 2));

        byte[] before = new byte[] { 1, 2, 3, 4, 9, 9, 8, 8 };
        byte[] after = new byte[] { 1, 7, 3, 4, 9, 9, 8, 8 };

        Assert.True(PayloadDiff.Between(in schema, new NodeId(0), before, after, out Operation op));
        Assert.Equal(0, op.Index);
        Assert.Equal(after, Apply(before, in op));
    }

    [Fact]
    public void Editing_a_word_in_the_middle_of_a_paragraph_splices_that_word()
    {
        KindSchema schema = Schema(PayloadFormat.Utf8);

        byte[] before = Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");
        byte[] after = Encoding.UTF8.GetBytes("the quick purple fox jumps over the lazy dog");

        Assert.True(PayloadDiff.Between(in schema, new NodeId(0), before, after, out Operation op));

        Assert.Equal(OpKind.Splice, op.Kind);
        Assert.True(op.Count < 10, "expected a small removal, got " + op.Count);
        Assert.True(op.Value.Length < 10);
        Assert.Equal(after, Apply(before, in op));
    }

    [Fact]
    public void A_text_splice_never_lands_inside_a_code_point()
    {
        KindSchema schema = Schema(PayloadFormat.Utf8);

        // 'é' and 'è' share their lead byte; 'ﬀ' and 'ﬁ' share two.
        string[] befores = { "café", "naïve", "ﬀ ligature", "日本語のテキスト" };
        string[] afters = { "cafè", "naÏve", "ﬁ ligature", "日本語のテクスト" };

        for (int i = 0; i < befores.Length; i++)
        {
            byte[] before = Encoding.UTF8.GetBytes(befores[i]);
            byte[] after = Encoding.UTF8.GetBytes(afters[i]);

            Assert.True(PayloadDiff.Between(in schema, new NodeId(0), before, after, out Operation op));

            if (op.Kind == OpKind.Splice)
            {
                Assert.True(IsBoundary(before, op.Index), befores[i] + ": offset splits a character");
                Assert.True(IsBoundary(before, op.Index + op.Count),
                    befores[i] + ": end of range splits a character");
            }

            Assert.Equal(after, Apply(before, in op));
        }
    }

    static bool IsBoundary(byte[] utf8, int at)
    {
        return at == 0 || at >= utf8.Length || (utf8[at] & 0xC0) != 0x80;
    }

    [Theory]
    [InlineData(PayloadFormat.Int64)]
    [InlineData(PayloadFormat.Float64)]
    [InlineData(PayloadFormat.Bool)]
    public void An_atomic_payload_is_replaced_whole(PayloadFormat format)
    {
        KindSchema schema = Schema(format);

        byte[] before = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        byte[] after = new byte[] { 1, 2, 3, 4, 5, 6, 7, 9 };

        Assert.True(PayloadDiff.Between(in schema, new NodeId(0), before, after, out Operation op));
        Assert.Equal(OpKind.ModifyValue, op.Kind);
        Assert.Equal(after, op.Value);
    }

    [Fact]
    public void A_change_with_nothing_in_common_is_a_replacement_and_says_so()
    {
        KindSchema schema = Schema(PayloadFormat.Utf8);

        Assert.True(PayloadDiff.Between(in schema, new NodeId(0),
            Encoding.UTF8.GetBytes("alpha"), Encoding.UTF8.GetBytes("omegan"), out Operation op));
        Assert.Equal(OpKind.ModifyValue, op.Kind);

        // And sharing even one byte at the end is enough to make it a range.
        Assert.True(PayloadDiff.Between(in schema, new NodeId(0),
            Encoding.UTF8.GetBytes("alpha"), Encoding.UTF8.GetBytes("omega"), out Operation shared));
        Assert.Equal(OpKind.Splice, shared.Kind);
        Assert.Equal(0, shared.Index);
        Assert.Equal(4, shared.Count);
    }

    [Fact]
    public void Identical_payloads_produce_nothing()
    {
        KindSchema schema = Schema(PayloadFormat.Utf8);
        byte[] same = Encoding.UTF8.GetBytes("unchanged");

        Assert.False(PayloadDiff.Between(in schema, new NodeId(0), same, (byte[])same.Clone(), out _));
    }

    [Fact]
    public void A_payload_that_does_not_match_its_stride_still_diffs()
    {
        KindSchema schema = Schema(PayloadFormat.Record, PayloadStride.Of(4, 8));

        byte[] before = new byte[] { 1, 2, 3 };            // shorter than its own header
        byte[] after = new byte[] { 1, 2, 3, 4, 5 };

        AssertReproduces(in schema, before, after);
    }

    [Theory]
    [InlineData(PayloadFormat.Utf8)]
    [InlineData(PayloadFormat.Record)]
    [InlineData(PayloadFormat.Opaque)]
    [InlineData(PayloadFormat.None)]
    public void Applying_what_it_produced_reproduces_the_payload(PayloadFormat format)
    {
        for (int seed = 0; seed < 400; seed++)
        {
            Random random = new Random(seed);

            PayloadStride stride = format == PayloadFormat.Record && seed % 3 == 0
                ? PayloadStride.Of((ushort)(seed % 5), (ushort)(1 + seed % 7))
                : default;
            KindSchema schema = Schema(format, stride);

            byte[] before = Bytes(random, format, random.Next(0, 200));
            byte[] after = Mutate(random, format, before);

            try
            {
                AssertReproduces(in schema, before, after);
            }
            catch (Exception e)
            {
                throw new Exception("seed " + seed + " (" + format + "): " + e.Message, e);
            }
        }
    }

    static byte[] Bytes(Random random, PayloadFormat format, int length)
    {
        if (format == PayloadFormat.Utf8)
        {
            // Deliberately full of multi-byte characters that share lead bytes.
            string alphabet = "aéèöﬀﬁ日本語z ";
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < length / 2; i++)
            {
                text.Append(alphabet[random.Next(alphabet.Length)]);
            }
            return Encoding.UTF8.GetBytes(text.ToString());
        }

        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    static byte[] Mutate(Random random, PayloadFormat format, byte[] before)
    {
        switch (random.Next(5))
        {
            case 0:
                return Bytes(random, format, random.Next(0, 200));

            case 1:
            {
                if (before.Length == 0)
                {
                    return Bytes(random, format, 8);
                }
                byte[] after = (byte[])before.Clone();
                after[random.Next(after.Length)] ^= 0xFF;
                return after;
            }

            case 2:
            {
                byte[] insert = Bytes(random, format, random.Next(1, 16));
                int at = random.Next(before.Length + 1);
                byte[] after = new byte[before.Length + insert.Length];
                Array.Copy(before, 0, after, 0, at);
                Array.Copy(insert, 0, after, at, insert.Length);
                Array.Copy(before, at, after, at + insert.Length, before.Length - at);
                return after;
            }

            case 3:
            {
                if (before.Length < 2)
                {
                    return Array.Empty<byte>();
                }
                int at = random.Next(before.Length - 1);
                int take = random.Next(1, before.Length - at);
                byte[] after = new byte[before.Length - take];
                Array.Copy(before, 0, after, 0, at);
                Array.Copy(before, at + take, after, at, before.Length - at - take);
                return after;
            }

            default:
                return (byte[])before.Clone();
        }
    }
}
