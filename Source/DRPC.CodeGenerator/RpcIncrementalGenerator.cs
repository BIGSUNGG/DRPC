using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using DRPC.CodeGenerator.Metadata;
using DRPC.CodeGenerator.Reference;

namespace DRPC.CodeGenerator;

/// <summary>
/// Finds <c>partial class X : ClientHub&lt;…&gt;</c> / <c>ServerHub&lt;…&gt;</c> and generates the RPC stubs.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RpcIncrementalGenerator : IIncrementalGenerator
{
    /// <summary>Registers the syntax providers and source outputs: hub stub generation, declaration type validation (DRPCGEN003), and generic call-site checks (DRPCGEN008).</summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ClassDeclarationSyntax> candidates =
            context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax classDecl
                    && classDecl.BaseList != null
                    // Filter hub candidates by syntax only: non-partial classes must pass
                    // through too, so DRPCGEN001 can be raised for them.
                    && classDecl.BaseList.Types.Any(static b => b.Type.ToString().IndexOf("Hub", System.StringComparison.Ordinal) >= 0),
                static (ctx, _) => (ClassDeclarationSyntax)ctx.Node);

        // Generic stub call sites (DRPCGEN008): structurally validate unresolved {Method}Async calls.
        IncrementalValuesProvider<InvocationExpressionSyntax> stubCalls =
            context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax inv
                    && inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: { } methodName }
                    && methodName.EndsWith("Async", System.StringComparison.Ordinal),
                static (ctx, _) => (InvocationExpressionSyntax)ctx.Node);

        // Declaration type validation (DRPCGEN003): [RemoteProcedure] methods get
        // parameter/return type validation in their declaring assembly regardless of any hub —
        // the diagnostic appears right at the method declaration.
        IncrementalValuesProvider<MethodDeclarationSyntax> rpcDeclarations =
            context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is MethodDeclarationSyntax { AttributeLists.Count: > 0 },
                static (ctx, _) => (MethodDeclarationSyntax)ctx.Node);

        context.RegisterSourceOutput(rpcDeclarations.Combine(context.CompilationProvider), static (spc, pair) =>
        {
            var (syntax, compilation) = pair;
            SemanticModel semanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);
            if (semanticModel.GetDeclaredSymbol(syntax) is not IMethodSymbol method)
            {
                return;
            }

            var references = new AttributeReferences(compilation);
            if (references.RemoteProcedureAttributeType == null ||
                !method.HasAttribute(references.RemoteProcedureAttributeType))
            {
                return; // Method without [RemoteProcedure] — not a target.
            }

            RpcHubSourceGenerator.CheckPayloadTypes(new MethodMetadata(method, references), references,
                spc.ReportDiagnostic, syntax.Identifier.GetLocation());
        });

        context.RegisterSourceOutput(stubCalls.Combine(context.CompilationProvider), static (spc, pair) =>
        {
            var (invocation, compilation) = pair;
            SemanticModel semanticModel = compilation.GetSemanticModel(invocation.SyntaxTree);

            var references = new AttributeReferences(compilation);
            if (references.RemoteProcedureAttributeType == null)
            {
                return; // Project without a DRPC.Attribute reference — not a target.
            }

            Diagnostic? diagnostic = GenericCallSiteCheck.Check(invocation, semanticModel, references);
            if (diagnostic != null)
            {
                spc.ReportDiagnostic(diagnostic);
            }
        });

        context.RegisterSourceOutput(candidates.Combine(context.CompilationProvider), static (spc, pair) =>
        {
            var (syntax, compilation) = pair;
            SemanticModel semanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);
            if (semanticModel.GetDeclaredSymbol(syntax) is not INamedTypeSymbol hubSymbol)
            {
                return;
            }

            var references = new AttributeReferences(compilation);
            if (references.RemoteProcedureAttributeType == null)
            {
                return; // Project without a DRPC.Attribute reference — not a target.
            }

            string? source = RpcHubSourceGenerator.Generate(hubSymbol, references,
                diagnostic => spc.ReportDiagnostic(diagnostic),
                syntax.Identifier.GetLocation());

            if (source != null)
            {
                spc.AddSource($"{hubSymbol.Name}.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        });
    }
}
