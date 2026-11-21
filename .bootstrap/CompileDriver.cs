using System;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

public static class CompileDriver
{
    public static int Main(string[] args)
    {
        if (args.Length < 4) { Console.WriteLine("usage: CompileDriver <srcDir> <outDll> <etsDir> <runtimeDir>"); return 2; }
        string srcDir = args[0];
        string outDll = args[1];
        string etsDir = args[2];
        string runtimeDir = args[3];

        string[] files = Directory.GetFiles(srcDir, "*.cs");
        SyntaxTree[] trees = new SyntaxTree[files.Length];
        for (int i = 0; i < files.Length; i++)
            trees[i] = CSharpSyntaxTree.ParseText(File.ReadAllText(files[i]), new CSharpParseOptions(LanguageVersion.Latest), files[i]);
        Console.WriteLine("sources: " + files.Length);

        string[] sys = Directory.GetFiles(runtimeDir, "System*.dll");
        int extra = 0;
        string ns = Path.Combine(runtimeDir, "netstandard.dll");
        if (File.Exists(ns)) extra++;

        string[] etsRefs = new string[] { "ETS2LA.Shared.dll", "ETS2LA.Backend.dll", "ETS2LA.Game.dll", "ETS2LA.Settings.dll", "ETS2LA.Overlay.dll", "ETS2LA.Logging.dll", "Hexa.NET.ImGui.dll", "TruckLib.dll", "TruckLib.Core.dll" };
        var refList = new System.Collections.Generic.List<MetadataReference>();
        for (int i = 0; i < sys.Length; i++)
        {
            try
            {
                System.Reflection.AssemblyName.GetAssemblyName(sys[i]);
                refList.Add(MetadataReference.CreateFromFile(sys[i]));
            }
            catch (Exception) { Console.WriteLine("SKIP NATIVE: " + sys[i]); }
        }
        if (File.Exists(ns)) refList.Add(MetadataReference.CreateFromFile(ns));
        for (int i = 0; i < etsRefs.Length; i++)
        {
            string p = Path.Combine(etsDir, etsRefs[i]);
            if (!File.Exists(p)) Console.WriteLine("MISSING REF: " + p);
            refList.Add(MetadataReference.CreateFromFile(p));
        }
        MetadataReference[] refs = refList.ToArray();
        Console.WriteLine("references: " + refs.Length);

        CSharpCompilationOptions options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable);
        CSharpCompilation compilation = CSharpCompilation.Create("AutoRestPark", trees, refs, options);
        var result = compilation.Emit(outDll);
        int errors = 0;
        foreach (Diagnostic d in result.Diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Error) errors++;
            Console.WriteLine(d.ToString());
        }
        Console.WriteLine(result.Success ? "BUILD OK -> " + outDll : "BUILD FAILED, errors=" + errors);
        return result.Success ? 0 : 1;
    }
}
