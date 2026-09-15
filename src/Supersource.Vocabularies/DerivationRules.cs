using System;
using Supersource.Derive;

namespace Supersource.Rules;

/// <summary>Optional diagnostics for applications using the derivation vocabulary.</summary>
public static class DerivationRules
{
    /// <summary>Append findings for derivations without a local transform; return the total count.</summary>
    public static int FullyReDerivable(
        in Source src,
        in DeriveKinds.Ids derive,
        ref RuleViolation[] into,
        int found = 0,
        Severity severity = Severity.Warn)
    {
        into ??= new RuleViolation[8];
        int before = found;
        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node))
            {
                continue;
            }

            KindId kind = src.Nodes[i].Kind;
            if (kind != derive.Derivation && kind != derive.BuildTarget)
            {
                continue;
            }

            NodeId transform = SourceWalk.Target(in src, node, derive.Transform);
            if (transform.IsNone || !src.IsLive(transform))
            {
                found = RuleSet.Report(ref into, found, node,
                    "derivation names no transform present in this source");
            }
        }
        for (int i = before; i < found; i++)
        {
            into[i].Rule = "fully-re-derivable";
            into[i].Severity = severity;
        }
        return found;
    }

    /// <summary>Append findings for generators that do not claim bit-exact output; return the total count.</summary>
    public static int GeneratorsAreBitExact(
        in Source src,
        in DeriveKinds.Ids derive,
        ref RuleViolation[] into,
        int found = 0,
        Severity severity = Severity.Warn)
    {
        into ??= new RuleViolation[8];
        int before = found;
        // Build one incoming-edge index for the scan.
        Backrefs index = default;
        Backrefs.Build(in src, ref index);

        for (long i = 0; i < src.NodeCount; i++)
        {
            NodeId node = new NodeId(i);
            if (!src.IsLive(node) || src.Nodes[i].Kind != derive.Derivation)
            {
                continue;
            }

            // This optional rule applies to derivations with generated output.
            if (!Generates(in src, in index, node))
            {
                continue;
            }

            Tier tier = DeriveKinds.DecodeTier(SourceWalk.Payload(in src, node));
            if (tier != Tier.BitExact)
            {
                found = RuleSet.Report(ref into, found, node,
                    "generator does not declare the bit-exact tier required by this rule");
            }
        }
        for (int i = before; i < found; i++)
        {
            into[i].Rule = "generators-are-bit-exact";
            into[i].Severity = severity;
        }
        return found;
    }

    static bool Generates(in Source src, in Backrefs index, NodeId derivation)
    {
        ReadOnlySpan<Backref> pointing = Backrefs.To(in index, derivation);
        for (int i = 0; i < pointing.Length; i++)
        {
            if (src.Nodes[pointing[i].From.Index].IsGenerated)
            {
                return true;
            }
        }
        return false;
    }
}
