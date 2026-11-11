using System;
using System.Buffers.Binary;
using System.IO;
using Supersource.Format;
using Supersource.Kinds;
using Xunit;

namespace Supersource.StrangerTests;
/// <summary>Reads property fixtures using published layouts without linking the property vocabulary.</summary>
public class Spec_StrangerProperty
{

    const ulong PropertyHi = 0x5375706572736372UL;
    const ulong PropertyLo = 0x6970745F70726F70UL;

    const ulong RasterLo = 0x6970745F72617374UL;

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

    static QualifiedName NameOf(in SourceFile file, NodeId node)
    {
        return KindTable.NameOf(in file.Table, file.Src.Nodes[node.Index].Kind);
    }

    [Fact]
    public void A_property_belongs_to_the_property_library()
    {
        SourceFile file = Sprite();

        string[] properties = { "colour", "blend", "opacity", "visible", "bounds" };

        for (int i = 0; i < properties.Length; i++)
        {
            NodeId node = FirstOfKind(in file, properties[i]);
            Assert.True(node.IsSome, properties[i] + " should be in the sprite fixture");

            QualifiedName name = NameOf(in file, node);
            Assert.Equal(PropertyHi, name.Library.Hi);
            Assert.Equal(PropertyLo, name.Library.Lo);
            Assert.NotEqual(RasterLo, name.Library.Lo);
        }
    }

    [Fact]
    public void The_image_kinds_did_not_move()
    {
        SourceFile file = Sprite();

        string[] raster = { "image", "layer", "pixels", "palette" };

        for (int i = 0; i < raster.Length; i++)
        {
            NodeId node = FirstOfKind(in file, raster[i]);
            Assert.True(node.IsSome);
            Assert.Equal(RasterLo, NameOf(in file, node).Library.Lo);
        }
    }

    [Fact]
    public void A_stranger_can_read_the_values()
    {
        SourceFile file = Sprite();

        // A colour is four bytes, RGBA.
        NodeId colour = FirstOfKind(in file, "colour");
        Assert.Equal(4, SourceWalk.Payload(in file.Src, colour).Length);

        // Visibility is a Bool payload: one byte.
        NodeId visible = FirstOfKind(in file, "visible");
        ReadOnlySpan<byte> shown = SourceWalk.Payload(in file.Src, visible);
        Assert.Equal(1, shown.Length);
        Assert.True(shown[0] == 0 || shown[0] == 1);

        // Bounds is four int32s — x, y, width, height.
        NodeId bounds = FirstOfKind(in file, "bounds");
        ReadOnlySpan<byte> rect = SourceWalk.Payload(in file.Src, bounds);
        Assert.Equal(16, rect.Length);
        Assert.True(BinaryPrimitives.ReadInt32LittleEndian(rect.Slice(8, 4)) > 0);
        Assert.True(BinaryPrimitives.ReadInt32LittleEndian(rect.Slice(12, 4)) > 0);
    }

    [Fact]
    public void A_property_hangs_off_the_thing_it_describes()
    {
        SourceFile file = Sprite();

        NodeId opacity = FirstOfKind(in file, "opacity");
        NodeId subject = SourceWalk.Parent(in file.Src, opacity);

        Assert.True(subject.IsSome);
        Assert.Equal("layer", NameOf(in file, subject).Name);
    }
}
