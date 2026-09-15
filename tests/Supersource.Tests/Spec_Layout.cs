using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Supersource.Compare;
using Supersource.Kinds;
using Supersource.Timeline;
using Xunit;

namespace Supersource.Tests;

public class Spec_Layout
{
    [Fact]
    public void The_ids_are_the_width_they_claim()
    {
        // A node id is 64-bit: slots are never reused, so the space is spent by
        // everything ever authored rather than by what is still alive.
        Assert.Equal(8, Unsafe.SizeOf<NodeId>());
        Assert.Equal(4, Unsafe.SizeOf<KindId>());
        Assert.Equal(4, Unsafe.SizeOf<RelationId>());
        Assert.Equal(16, Unsafe.SizeOf<SourceId>());
    }

    [Fact]
    public void Node_is_the_size_and_shape_it_is_meant_to_be()
    {
        // 48, and the 64-bit fields lead so the seven bytes of padding are at the end
        // where a future field can have them, rather than in a hole in the middle.
        Assert.Equal(48, Unsafe.SizeOf<Node>());

        Assert.Equal(0, Offset<Node>("Parent"));
        Assert.Equal(8, Offset<Node>("PayloadOffset"));
        Assert.Equal(16, Offset<Node>("Kind"));
        Assert.Equal(20, Offset<Node>("ChildOffset"));
        Assert.Equal(24, Offset<Node>("ChildCount"));
        Assert.Equal(28, Offset<Node>("RefOffset"));
        Assert.Equal(32, Offset<Node>("RefCount"));
        Assert.Equal(36, Offset<Node>("PayloadLength"));
        Assert.Equal(40, Offset<Node>("Flags"));

        Assert.Equal(12, Unsafe.SizeOf<RunCapacity>());
        Assert.Equal(0, Offset<RunCapacity>("Child"));
        Assert.Equal(4, Offset<RunCapacity>("Ref"));
        Assert.Equal(8, Offset<RunCapacity>("Payload"));
    }

    [Fact]
    public void A_reference_is_three_words()
    {
        Assert.Equal(24, Unsafe.SizeOf<Ref>());

        Assert.Equal(0, Offset<Ref>("Relation"));
        Assert.Equal(8, Offset<Ref>("Target"));
        Assert.Equal(16, Offset<Ref>("LabelOffset"));
        Assert.Equal(20, Offset<Ref>("LabelLength"));
    }

    [Fact]
    public void A_backref_is_a_node_and_an_index()
    {
        Assert.Equal(16, Unsafe.SizeOf<Backref>());
        Assert.Equal(0, Offset<Backref>("From"));
        Assert.Equal(8, Offset<Backref>("RefIndex"));
    }

    [Fact]
    public void A_stride_is_two_shorts()
    {
        Assert.Equal(4, Unsafe.SizeOf<PayloadStride>());
        Assert.Equal(0, Offset<PayloadStride>("Header"));
        Assert.Equal(2, Offset<PayloadStride>("Unit"));
    }

    [Fact]
    public void The_graph_holds_no_managed_references()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<Node>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<Ref>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<Backref>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<NodeId>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<PayloadStride>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<RunCapacity>());
    }

    static int Offset<T>(string field)
    {
        return (int)Marshal.OffsetOf<T>(field);
    }
}
