namespace Supersource;

/// <summary>What a reference points at, as far as the source holding it can say.</summary>
public enum RefState
{
    /// <summary>A live node. The ordinary case.</summary>
    Bound,

    /// <summary>Nothing: an unbound reference, which carries a label instead of a target.</summary>
    Unbound,

    /// <summary>An id that is not live: the node was deleted, and deletion means gone.</summary>
    Dangling,

    /// <summary>An id this source never gave out. Only a damaged file holds one.</summary>
    OutOfRange,
}
