using Supersource.Relate;
using Xunit;

namespace Supersource.Relate.Tests;

public class Spec_SpanShift
{
    static SpanRange Range(int offset, int length)
    {
        SpanRange range;
        range.Offset = offset;
        range.Length = length;
        range.Edge = 0;
        return range;
    }

    [Fact]
    public void An_insertion_before_a_range_moves_it()
    {
        SpanRange after = SpanCodec.Shifted(Range(10, 4), 2, 0, 3);
        Assert.Equal(13, after.Offset);
        Assert.Equal(4, after.Length);
    }

    [Fact]
    public void An_insertion_after_a_range_leaves_it_alone()
    {
        SpanRange after = SpanCodec.Shifted(Range(2, 4), 20, 0, 3);
        Assert.Equal(2, after.Offset);
        Assert.Equal(4, after.Length);
    }

    [Fact]
    public void Typing_at_the_front_of_a_range_stays_outside_it()
    {
        SpanRange after = SpanCodec.Shifted(Range(4, 4), 4, 0, 2);
        Assert.Equal(6, after.Offset);
        Assert.Equal(4, after.Length);
    }

    [Fact]
    public void Typing_inside_a_range_grows_it()
    {
        SpanRange after = SpanCodec.Shifted(Range(4, 4), 6, 0, 2);
        Assert.Equal(4, after.Offset);
        Assert.Equal(6, after.Length);
    }

    [Fact]
    public void Typing_at_the_end_of_a_range_continues_it()
    {
        SpanRange after = SpanCodec.Shifted(Range(4, 4), 8, 0, 2);
        Assert.Equal(4, after.Offset);
        Assert.Equal(6, after.Length);
    }

    [Fact]
    public void A_deletion_inside_a_range_shortens_it()
    {
        SpanRange after = SpanCodec.Shifted(Range(4, 6), 5, 2, 0);
        Assert.Equal(4, after.Offset);
        Assert.Equal(4, after.Length);
    }

    [Fact]
    public void A_deletion_across_the_front_clamps_it()
    {
        SpanRange after = SpanCodec.Shifted(Range(6, 6), 4, 4, 0);
        Assert.Equal(4, after.Offset);
        Assert.Equal(4, after.Length);
    }

    [Fact]
    public void A_deletion_that_swallows_a_range_leaves_a_point()
    {
        SpanRange after = SpanCodec.Shifted(Range(6, 4), 4, 10, 0);
        Assert.Equal(4, after.Offset);
        Assert.Equal(0, after.Length);
    }

    [Fact]
    public void A_replacement_moves_what_follows_by_the_difference()
    {
        SpanRange after = SpanCodec.Shifted(Range(20, 4), 2, 6, 1);
        Assert.Equal(15, after.Offset);
        Assert.Equal(4, after.Length);
    }

    [Fact]
    public void An_empty_edit_is_the_identity()
    {
        SpanRange after = SpanCodec.Shifted(Range(6, 4), 6, 0, 0);
        Assert.Equal(6, after.Offset);
        Assert.Equal(4, after.Length);
    }
}
