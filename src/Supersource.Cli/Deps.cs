using System;
using System.IO;
using Supersource.Format;
using Supersource.Kinds;
using Supersource.Resolve;
using Supersource;

namespace Supersource.Cli;
/// <summary>
/// Lists external dependencies and resolves them against caller-supplied libraries. Missing
/// libraries do not prevent inspection.
/// </summary>
public static class Deps
{
    public static int Run(string path, string[] libraries)
    {
        if (string.IsNullOrEmpty(path))
        {
            Console.Error.WriteLine("usage: ss deps <file.ss> [library.ss ...]");
            return 1;
        }

        SourceFile file = SourceRead.FromFile(path);
        ExternalKinds.Ids ids = ExternalKinds.Install(ref file.Table);

        Workspace.Set set = Workspace.Create();

        if (libraries != null)
        {
            for (int i = 0; i < libraries.Length; i++)
            {
                // Mapped, not read: a library is the same scale as the source that
                // depends on it, and both stay open for the length of the answer.
                SourceFile lib = SourceRead.FromFile(libraries[i]);

                // The hash is of the bytes on disk, because that is what a pin names.
                Workspace.Add(ref set, lib.Src.Identity, ContentHash.OfFile(libraries[i]), in lib.Src);
            }
        }

        CrossRef[] links = null;
        int count = Workspace.LinkAll(in set, in file.Src, in file.Table, ids.ExternalRef, ref links);

        if (count == 0)
        {
            Console.WriteLine("no cross-source references.");
            Console.WriteLine("§3.8: the ordinary reference is intra-source and is nothing but an id.");
            return 0;
        }

        int unresolved = 0;

        for (int i = 0; i < count; i++)
        {
            CrossRef link = links[i];

            Console.Write("  " + link.From + "  " + Describe(link.How));

            if (link.IsBound)
            {
                Console.Write("  -> source " + link.Source + " " + link.Node);
            }
            else
            {
                unresolved++;
            }

            if (!link.Pinned)
            {
                // Report missing pins without rejecting the source.
                Console.Write("   [unpinned]");
            }

            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine(count + " cross-source reference(s), " + unresolved + " unresolved.");
        Console.WriteLine("An unresolved one is a hole (§6) — a valid source that says what is missing.");
        return 0;
    }

    static string Describe(CrossResolution how)
    {
        switch (how)
        {
            case CrossResolution.Bound: return "bound";
            case CrossResolution.DocumentMissing: return "source not here";
            case CrossResolution.VersionMismatch: return "wrong version (pin)";
            case CrossResolution.NodeMissing: return "deleted upstream";
            case CrossResolution.IdentityCollision: return "two files claim this identity";
            default: return "unknown";
        }
    }
}
