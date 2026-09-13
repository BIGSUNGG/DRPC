using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using DRPC.CodeGenerator.Metadata;
using DRPC.CodeGenerator.Reference;

namespace DRPC.CodeGenerator;

/// <summary>
/// <c>partial class X : ClientHub&lt;…&gt;</c> / <c>ServerHub&lt;…&gt;</c> 를 찾아 RPC 스텁을 생성한다.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RpcIncrementalGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ClassDeclarationSyntax> candidates =
            context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax classDecl
                    && classDecl.BaseList != null
                    // 구문만 보고 허브 후보를 거른다: partial 이 아닌 클래스도 지나가야 DRPCGEN001 을 낼 수 있다.
                    && classDecl.BaseList.Types.Any(static b => b.Type.ToString().IndexOf("Hub", System.StringComparison.Ordinal) >= 0),
                static (ctx, _) => (ClassDeclarationSyntax)ctx.Node);

        // 제네릭 스텁 호출 지점(DRPCGEN008): 미해결 {Method}Async 호출을 구조적으로 검사한다.
        IncrementalValuesProvider<InvocationExpressionSyntax> stubCalls =
            context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax inv
                    && inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: { } methodName }
                    && methodName.EndsWith("Async", System.StringComparison.Ordinal),
                static (ctx, _) => (InvocationExpressionSyntax)ctx.Node);

        // 선언부 타입 검증(DRPCGEN003): [RemoteProcedure] 메서드는 허브 유무와 무관하게
        // 선언한 어셈블리에서 매개변수·반환 타입 검증을 받는다 — 메서드 선언 위치에 바로 진단이 뜬다.
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
                return; // [RemoteProcedure] 가 붙지 않은 메서드 — 대상 아님.
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
                return; // DRPC.Attribute 미참조 프로젝트 — 대상 아님.
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
                return; // DRPC.Attribute 미참조 프로젝트 — 대상 아님.
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
