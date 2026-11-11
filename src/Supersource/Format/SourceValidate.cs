using System;
using Supersource.Kinds;

namespace Supersource.Format;
public enum ProblemKind
{
    /// <summary>A parent link and a child run disagree. Genuine corruption.</summary>
    BrokenContainment,

    /// <summary>A child index names a slot that was never allocated.</summary>
    ChildOutOfRange,

    /// <summary>Containment is cyclic, or a node is its own ancestor.</summary>
    ContainmentCycle,

    /// <summary>A reference names a slot that was never allocated.</summary>
    ReferenceOutOfRange,

    /// <summary>The source's root names a slot that is not live.</summary>
    RootNotLive,

    /// <summary>A live node is not reachable from the root by containment.</summary>
    Unreachable,
}

public struct Problem
{
    public ProblemKind Kind;
    public NodeId Node;
    public string Detail;
}

/// <summary>
/// Checks graph structure, including containment and address bounds. Unbound references, references
/// to deleted nodes and absent schemas are permitted; schema conformance is checked by rules.
/// </summary>
public static class SourceValidate
{
    public static bool Check(in SourceFile file, out Problem[] problems)
    {
        Problem[] found = new Problem[8];
        int count = 0;

        Source src = file.Src;

        if (src.Root.IsSome && !src.IsLive(src.Root))
        {
            Add(ref found, ref count, ProblemKind.RootNotLive, src.Root,
                "root names a slot that is not live");
        }

        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId id = new NodeId(i);
            if (!src.IsLive(id))
            {
                continue;
            }

            int childOffset = src.Nodes[i].ChildOffset;
            int childCount = src.Nodes[i].ChildCount;

            for (int c = 0; c < childCount; c++)
            {
                long childIndex = src.Children[childOffset + c];
                if (childIndex < 0 || childIndex >= src.NodeCount)
                {
                    Add(ref found, ref count, ProblemKind.ChildOutOfRange, id,
                        "child " + c + " names slot " + childIndex);
                    continue;
                }

                if (src.Nodes[childIndex].Parent != id)
                {
                    Add(ref found, ref count, ProblemKind.BrokenContainment, new NodeId(childIndex),
                        "is a child of " + id + " but claims parent " + src.Nodes[childIndex].Parent);
                }
            }

            int refOffset = src.Nodes[i].RefOffset;
            int refCount = src.Nodes[i].RefCount;
            for (int e = 0; e < refCount; e++)
            {
                NodeId target = src.Refs[refOffset + e].Target;
                // Unbound and deleted targets are valid; unallocated targets are not.
                if (target.IsSome && target.Index >= src.NodeCount)
                {
                    Add(ref found, ref count, ProblemKind.ReferenceOutOfRange, id,
                        "reference " + e + " names slot " + target.Index);
                }
            }

            if (IsCyclic(in src, id))
            {
                Add(ref found, ref count, ProblemKind.ContainmentCycle, id,
                    "is its own ancestor");
            }
        }

        CheckReachable(in src, ref found, ref count);

        problems = new Problem[count];
        Array.Copy(found, problems, count);
        return count == 0;
    }

    static bool IsCyclic(in Source src, NodeId id)
    {
        NodeId slow = id;
        NodeId fast = id;

        while (true)
        {
            if (fast.IsNone || !src.WasAllocated(fast))
            {
                return false;
            }
            fast = src.Nodes[fast.Index].Parent;

            if (fast.IsNone || !src.WasAllocated(fast))
            {
                return false;
            }
            fast = src.Nodes[fast.Index].Parent;

            slow = src.Nodes[slow.Index].Parent;
            if (slow.IsSome && slow == fast)
            {
                return true;
            }
        }
    }

    static void CheckReachable(in Source src, ref Problem[] found, ref int count)
    {
        if (src.Root.IsNone || !src.IsLive(src.Root))
        {
            return;
        }

        bool[] seen = new bool[src.NodeCount];
        NodeId[] stack = new NodeId[16];
        int top = 0;
        stack[top] = src.Root;
        top++;

        while (top > 0)
        {
            top--;
            NodeId at = stack[top];
            if (seen[at.Index])
            {
                continue;
            }
            seen[at.Index] = true;

            int offset = src.Nodes[at.Index].ChildOffset;
            int children = src.Nodes[at.Index].ChildCount;
            for (int c = 0; c < children; c++)
            {
                long child = src.Children[offset + c];
                if (child < 0 || child >= src.NodeCount || seen[child])
                {
                    continue;
                }
                if (top == stack.Length)
                {
                    Array.Resize(ref stack, stack.Length == 0 ? 16 : stack.Length * 2);
                }
                stack[top] = new NodeId(child);
                top++;
            }
        }

        for (long i = 0; i < src.NodeCount; i++)
        {
            if (src.IsLive(new NodeId(i)) && !seen[i])
            {
                Add(ref found, ref count, ProblemKind.Unreachable, new NodeId(i),
                    "live but not reachable from the root");
            }
        }
    }

    static void Add(ref Problem[] into, ref int count, ProblemKind kind, NodeId node, string detail)
    {
        if (count == into.Length)
        {
            Array.Resize(ref into, into.Length == 0 ? 16 : into.Length * 2);
        }
        into[count].Kind = kind;
        into[count].Node = node;
        into[count].Detail = detail;
        count++;
    }
}
