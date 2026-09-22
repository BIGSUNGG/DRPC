using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DRPC.CodeGenerator.Tests;

/// <summary>
/// Drives the generator directly over an in-memory compilation to inspect diagnostics and generated output.
/// </summary>
internal static class GeneratorHarness
{
    static GeneratorHarness()
    {
        References = BuildReferences();
    }

    /// <summary>Assemblies referenced by the compilation under test (DRPC, MessageProtocol, and Communication included).</summary>
    public static readonly ImmutableArray<MetadataReference> References;

    static ImmutableArray<MetadataReference> BuildReferences()
    {
        // Merge the TPA (runtime framework) assemblies with the output-directory assemblies by file name (avoids duplicate references).
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string tpa = (string)(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty);
        foreach (string path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            byName[Path.GetFileName(path)] = path;
        }

        foreach (string path in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
        {
            byName[Path.GetFileName(path)] = path;
        }

        return byName.Values.Select(static p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToImmutableArray();
    }

    public static GeneratorResult Run(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "DrpcGenTests",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        IIncrementalGenerator generator = new RpcIncrementalGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator.AsSourceGenerator());

        // Running returns a new driver — discarding the return value would read empty results.
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation updated, out _);
        GeneratorDriverRunResult result = driver.GetRunResult();

        return new GeneratorResult(
            // With zero diagnostics, Diagnostics comes back as a default ImmutableArray (the IsDefault check is mandatory).
            result.Results
                .SelectMany(r => r.Diagnostics.IsDefault
                    ? Enumerable.Empty<Diagnostic>()
                    : r.Diagnostics)
                .ToImmutableArray(),
            string.Concat(result.GeneratedTrees.Select(static t => t.ToString())),
            updated);
    }

    /// <summary>Swaps in server/client contract bodies to build a client-side hub source.</summary>
    public static string ClientHub(string serverContractBody, string clientContractBody = "", string hubBody = "")
        => $$"""
           using DRPC;
           using DRPC.Client.Network;
           using DRPC.Shared.Interface;
           using MessageProtocol;
           using System;
           using System.Collections.Generic;
           using System.Threading.Tasks;

           public interface ITestServerProcedures : IServerProcedureDeclarations
           {
           {{serverContractBody}}
           }

           public interface ITestClientProcedures : IClientProcedureDeclarations
           {
           {{clientContractBody}}
           }

           public partial class TestClientHub : ClientHub<ITestServerProcedures, ITestClientProcedures>
           {
           {{hubBody}}
           }
           """;

    /// <summary>Server-side hub source (for verifying listening wiring).</summary>
    public static string ServerHub(string serverContractBody, string clientContractBody = "", string hubBody = "")
        => $$"""
           using DRPC;
           using DRPC.Server.Network;
           using DRPC.Shared.Interface;
           using MessageProtocol;
           using System;
           using System.Collections.Generic;
           using System.Threading.Tasks;

           public interface ITestServerProcedures : IServerProcedureDeclarations
           {
           {{serverContractBody}}
           }

           public interface ITestClientProcedures : IClientProcedureDeclarations
           {
           {{clientContractBody}}
           }

           public partial class TestServerHub : ServerHub<ITestServerProcedures, ITestClientProcedures>
           {
           {{hubBody}}
           }
           """;

    public sealed record GeneratorResult(ImmutableArray<Diagnostic> Diagnostics, string GeneratedSource, Compilation Compilation)
    {
        public bool HasDiagnostic(string id) => Diagnostics.Any(d => d.Id == id);

        public ImmutableArray<Diagnostic> WithId(string id) => Diagnostics.Where(d => d.Id == id).ToImmutableArray();

        /// <summary>Checks that the generated code compiles on its own (members and type references resolve).</summary>
        public ImmutableArray<Diagnostic> CompileErrors()
            => Compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error)
                .ToImmutableArray();
    }
}
