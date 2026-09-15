using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Supersource.Format;
using Supersource.Kinds;
using Xunit;

namespace Supersource.StrangerTests;
/// <summary>Reads raster fixtures using published layouts without linking the raster library.</summary>
public class Spec_StrangerRaster
{
    static SourceFile Sprite()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "sprite.ss");
        Assert.True(File.Exists(path), "the sprite fixture should ship with the tests");
        return SourceRead.FromBytes(File.ReadAllBytes(path));
    }

    static NodeId FirstOfKind(in SourceFile file, string kindName)
    {
        for (int i = 0; i < file.Src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!file.Src.IsLive(node))
            {
                continue;
            }
            if (KindTable.NameOf(in file.Table, file.Src.Nodes[i].Kind).Name == kindName)
            {
                return node;
            }
        }
        return NodeId.None;
    }

    [Fact]
    public void A_stranger_can_find_the_image_and_read_its_size()
    {
        SourceFile file = Sprite();

        NodeId image = FirstOfKind(in file, "image");
        Assert.True(image.IsSome, "a stranger should be able to locate the image by kind name");

        // The published layout: one byte of format, then two little-endian int32s.
        ReadOnlySpan<byte> payload = SourceWalk.Payload(in file.Src, image);
        Assert.True(payload.Length >= 9);

        int width = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(1, 4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(5, 4));

        Assert.Equal(4, width);
        Assert.Equal(4, height);
        Assert.Equal(0, payload[0]);            // RGBA8
    }

    [Fact]
    public void A_stranger_can_read_the_pixels()
    {
        SourceFile file = Sprite();

        NodeId pixels = FirstOfKind(in file, "pixels");
        Assert.True(pixels.IsSome);

        ReadOnlySpan<byte> payload = SourceWalk.Payload(in file.Src, pixels);

        const int header = 1 + 4 + 4 + 4 + 4;
        Assert.True(payload.Length > header);

        int width = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(1, 4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(5, 4));
        ReadOnlySpan<byte> data = payload.Slice(header);

        Assert.Equal(4, width);
        Assert.Equal(4, height);
        Assert.Equal(4 * 4 * 4, data.Length);

        // The checkerboard, decoded. A stranger's editor could render this.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * width + x) * 4;
                byte expected = (byte)(((x + y) % 2) == 0 ? 0xFF : 0x00);

                Assert.Equal(expected, data[at + 0]);
                Assert.Equal(expected, data[at + 1]);
                Assert.Equal(expected, data[at + 2]);
                Assert.Equal(0xFF, data[at + 3]);
            }
        }
    }

    [Fact]
    public void A_stranger_reads_the_structural_facts_of_a_library_it_never_shipped()
    {
        SourceFile file = Sprite();

        NodeId image = FirstOfKind(in file, "image");
        NodeId layer = FirstOfKind(in file, "layer");

        KindSchema imageSchema = KindTable.SchemaOf(in file.Table, file.Src.Nodes[image.Index].Kind);
        KindSchema layerSchema = KindTable.SchemaOf(in file.Table, file.Src.Nodes[layer.Index].Kind);

        // An image scopes what it contains, and layer order is z-order.
        Assert.True((imageSchema.Facts & StructuralFact.OpensScope) != 0);
        Assert.True((imageSchema.Facts & StructuralFact.ChildOrderSignificant) != 0);

        // The layer declares its name for binding.
        Assert.True((layerSchema.Facts & StructuralFact.Declaration) != 0);
        Assert.Equal(PayloadFormat.Utf8, layerSchema.Payload);

        Assert.Equal("background", Encoding.UTF8.GetString(SourceWalk.Payload(in file.Src, layer)));
    }

    [Fact]
    public void A_stranger_follows_an_animation_to_its_frames_rather_than_to_indices()
    {
        SourceFile file = Sprite();

        NodeId animation = FirstOfKind(in file, "animation");
        Assert.True(animation.IsSome);
        Assert.Equal("walk", Encoding.UTF8.GetString(SourceWalk.Payload(in file.Src, animation)));

        Assert.Equal(2, SourceWalk.ChildCount(in file.Src, animation));

        for (int i = 0; i < 2; i++)
        {
            NodeId step = SourceWalk.ChildAt(in file.Src, animation, i);
            ReadOnlySpan<Ref> refs = SourceWalk.References(in file.Src, step);

            Assert.Equal(1, refs.Length);
            Assert.True(refs[0].IsBound, "an animation step should point at a frame node");
            Assert.Equal("shows", KindTable.RelationNameOf(in file.Table, refs[0].Relation).Name);

            NodeId frame = refs[0].Target;
            Assert.True(file.Src.IsLive(frame));
            Assert.Equal("frame", KindTable.NameOf(in file.Table, file.Src.Nodes[frame.Index].Kind).Name);
        }
    }
}
