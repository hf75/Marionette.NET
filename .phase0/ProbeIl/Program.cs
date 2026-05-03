// ProbeIl — Spike A IL inspector.
// Walks PE metadata for one or more assemblies and reports every type, method,
// field, and assembly reference whose name contains the given substring.
// We intentionally do NOT load the assembly into the runtime: MetadataReader
// works on the raw PE so net10 dependencies and lock-on-disk semantics don't bite us.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ProbeIl <needle> <file-or-dir> [<file-or-dir>...]");
    return 2;
}

string needle = args[0];
var inputs = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    if (Directory.Exists(args[i]))
    {
        foreach (var f in Directory.EnumerateFiles(args[i], "*.dll", SearchOption.TopDirectoryOnly))
            inputs.Add(f);
        foreach (var f in Directory.EnumerateFiles(args[i], "*.exe", SearchOption.TopDirectoryOnly))
            inputs.Add(f);
    }
    else if (File.Exists(args[i]))
    {
        inputs.Add(args[i]);
    }
    else
    {
        Console.Error.WriteLine($"warning: {args[i]} not found, skipping");
    }
}

int totalHits = 0;
foreach (var path in inputs)
{
    int hits = ProbeOne(path, needle);
    totalHits += hits;
    Console.WriteLine($"  -> {Path.GetFileName(path)}: {hits} hit(s)");
}

Console.WriteLine();
Console.WriteLine($"TOTAL hits across {inputs.Count} file(s): {totalHits}");
return totalHits == 0 ? 0 : 1;

static int ProbeOne(string path, string needle)
{
    int hits = 0;
    using var fs = File.OpenRead(path);
    using var pe = new PEReader(fs);
    if (!pe.HasMetadata)
    {
        Console.WriteLine($"[no-metadata] {path}");
        return 0;
    }

    var md = pe.GetMetadataReader();
    string asm = md.GetString(md.GetAssemblyDefinition().Name);
    Console.WriteLine();
    Console.WriteLine($"=== {Path.GetFileName(path)} (assembly={asm}) ===");

    // 1) Assembly references
    foreach (var handle in md.AssemblyReferences)
    {
        var aref = md.GetAssemblyReference(handle);
        var name = md.GetString(aref.Name);
        if (Match(name, needle))
        {
            Console.WriteLine($"  ASSEMBLY-REF  {name}, {aref.Version}");
            hits++;
        }
    }

    // 2) TypeRefs (cross-assembly type imports — what we actually care about for stripping)
    foreach (var handle in md.TypeReferences)
    {
        var tref = md.GetTypeReference(handle);
        var ns = md.GetString(tref.Namespace);
        var name = md.GetString(tref.Name);
        var full = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        if (Match(full, needle))
        {
            Console.WriteLine($"  TYPE-REF      {full}");
            hits++;
        }
    }

    // 3) TypeDefs (types defined inside this assembly that mention the needle)
    foreach (var handle in md.TypeDefinitions)
    {
        var tdef = md.GetTypeDefinition(handle);
        var ns = md.GetString(tdef.Namespace);
        var name = md.GetString(tdef.Name);
        var full = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        if (Match(full, needle))
        {
            Console.WriteLine($"  TYPE-DEF      {full}");
            hits++;
        }
    }

    // 4) MemberRefs (cross-assembly method/field references) — catches calls into runtime/adapter
    foreach (var handle in md.MemberReferences)
    {
        var mref = md.GetMemberReference(handle);
        var name = md.GetString(mref.Name);
        // Resolve the parent type for context
        string parent = "?";
        try
        {
            var parentHandle = mref.Parent;
            if (parentHandle.Kind == HandleKind.TypeReference)
            {
                var t = md.GetTypeReference((TypeReferenceHandle)parentHandle);
                var pns = md.GetString(t.Namespace);
                var pn = md.GetString(t.Name);
                parent = string.IsNullOrEmpty(pns) ? pn : $"{pns}.{pn}";
            }
            else if (parentHandle.Kind == HandleKind.TypeDefinition)
            {
                var t = md.GetTypeDefinition((TypeDefinitionHandle)parentHandle);
                var pns = md.GetString(t.Namespace);
                var pn = md.GetString(t.Name);
                parent = string.IsNullOrEmpty(pns) ? pn : $"{pns}.{pn}";
            }
        }
        catch { /* ignore */ }

        if (Match(parent, needle) || Match(name, needle))
        {
            Console.WriteLine($"  MEMBER-REF    {parent}::{name}");
            hits++;
        }
    }

    return hits;
}

static bool Match(string s, string needle)
    => s.Contains(needle, StringComparison.Ordinal);
