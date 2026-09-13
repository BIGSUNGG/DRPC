using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using DRPC.CodeGenerator.Metadata;
using DRPC.CodeGenerator.Reference;

namespace DRPC.CodeGenerator;

/// <summary>
/// 허브 클래스 하나를 검사하고 생성 소스 문자열을 만든다. 진단은 <see cref="System.Action{Diagnostic}"/> 로 보고한다.
/// </summary>
internal static class RpcHubSourceGenerator
{
    public static string? Generate(INamedTypeSymbol hubSymbol, AttributeReferences references,
        System.Action<Diagnostic> report, Location fallbackLocation)
    {
        if (!hubSymbol.DeclaringSyntaxReferences.Any(static s => s.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax c
                && c.Modifiers.Any(static m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword))))
        {
            report(Diagnostic.Create(DiagnosticDescriptors.MustBePartial,
                hubSymbol.Locations.FirstOrDefault() ?? fallbackLocation, hubSymbol.Name));
            return null;
        }

        if (!TryResolveHub(hubSymbol, out INamedTypeSymbol? hubBase, out NetworkKind networkKind, out bool invalidBase))
        {
            if (invalidBase)
            {
                report(Diagnostic.Create(DiagnosticDescriptors.InvalidHubBase,
                    hubSymbol.Locations.FirstOrDefault() ?? fallbackLocation, hubSymbol.Name));
            }

            return null;
        }

        INamedTypeSymbol? serverDeclarations = hubBase!.TypeArguments[0] as INamedTypeSymbol;
        INamedTypeSymbol? clientDeclarations = hubBase.TypeArguments[1] as INamedTypeSymbol;
        if (!Implements(serverDeclarations, AttributeReferences.ServerDeclarationsTypeName) ||
            !Implements(clientDeclarations, AttributeReferences.ClientDeclarationsTypeName))
        {
            report(Diagnostic.Create(DiagnosticDescriptors.InvalidHubBase,
                hubSymbol.Locations.FirstOrDefault() ?? fallbackLocation, hubSymbol.Name));
            return null;
        }

        var server = new DeclarationsMetadata(serverDeclarations!, references);
        var client = new DeclarationsMetadata(clientDeclarations!, references);
        var model = new TypeMetadata(hubSymbol, server, client, networkKind);

        if (!Validate(model.ServerDeclarations, references, report, fallbackLocation) ||
            !Validate(model.ClientDeclarations, references, report, fallbackLocation))
        {
            // 검증 실패 시에도 사용자 partial 구현이 고아가 되지 않게 정의 선언만 배출한다(CS0759 벽 방지).
            // 빌드 실패 자체는 이미 보고된 진단이 담당한다.
            return Emitter.RpcHubEmitter.EmitSkeleton(model);
        }

        return Emitter.RpcHubEmitter.Emit(model);
    }

    static bool Validate(DeclarationsMetadata declarations, AttributeReferences references,
        System.Action<Diagnostic> report, Location fallbackLocation)
    {
        var methodIds = new HashSet<int>();
        var methodNames = new HashSet<string>();

        foreach (MethodMetadata method in declarations.Methods)
        {
            IMethodSymbol symbol = method.Symbol;
            // 메타데이터(다른 어셈블리)에서 온 선언은 위치가 없다 — 허브 클래스 위치로 잡아 클릭 가능하게 한다.
            Location location = symbol.Locations.FirstOrDefault(static l => l.IsInSource) ?? fallbackLocation;

            if (!methodIds.Add(method.MethodId))
            {
                report(Diagnostic.Create(DiagnosticDescriptors.DuplicateMethodId, location,
                    method.MethodId, declarations.Symbol.Name));
                return false;
            }

            if (!methodNames.Add(symbol.Name))
            {
                report(Diagnostic.Create(DiagnosticDescriptors.UnsupportedType, location, symbol.Name,
                    declarations.Symbol.Name, "overloaded RPC method name — give one of them a different name"));
                return false;
            }

            if (method.OneWay && symbol.ReturnType.SpecialType != SpecialType.System_Void)
            {
                report(Diagnostic.Create(DiagnosticDescriptors.OneWayRequiresVoid, location, symbol.Name));
                return false;
            }

            // 호출별 타임아웃 검증: -1(상속) 아니면 양수여야 한다. 0/-N 은 조용한 무시 대신 컴파일 거부.
            if (method.TimeoutMs != -1 && method.TimeoutMs <= 0)
            {
                report(Diagnostic.Create(DiagnosticDescriptors.InvalidTimeoutMs, location, symbol.Name,
                    method.TimeoutMs));
                return false;
            }

            if (method.OneWay && method.TimeoutMs > 0)
            {
                report(Diagnostic.Create(DiagnosticDescriptors.TimeoutOnOneWay, location, symbol.Name));
            }

            if (symbol.IsGenericMethod)
            {
                foreach (ITypeParameterSymbol typeParameter in symbol.TypeParameters)
                {
                    if (typeParameter.ConstraintTypes.Length > 0 || typeParameter.HasReferenceTypeConstraint ||
                        typeParameter.HasValueTypeConstraint || typeParameter.HasUnmanagedTypeConstraint)
                    {
                        report(Diagnostic.Create(DiagnosticDescriptors.GenericDeclarationInvalid, location, symbol.Name,
                            $"type parameter '{typeParameter.Name}' has constraints; constrained type parameters are not supported"));
                        return false;
                    }
                }

                if (method.Generic?.Error is { } genericError)
                {
                    report(Diagnostic.Create(
                        method.Generic.ErrorId == "DRPCGEN007"
                            ? DiagnosticDescriptors.GenericDeclarationMissing
                            : DiagnosticDescriptors.GenericDeclarationInvalid,
                        location, symbol.Name, genericError));
                    return false;
                }
            }
            else if (method.Symbol.GetAttributes().Any(static a =>
                         a.AttributeClass?.ContainingNamespace?.ToDisplayString() == "DRPC" &&
                         a.AttributeClass?.Name == "GenericProcedureAttribute"))
            {
                report(Diagnostic.Create(DiagnosticDescriptors.GenericDeclarationInvalid, location, symbol.Name,
                    "[GenericProcedure] is only valid on generic methods"));
                return false;
            }

            // 타입 검증: 소스에 선언된 메서드는 선언부 프로바이더가 같은 컴파일레이션에서 이미 진단한다(중복 보고 방지).
            // 메타데이터로 들어온 선언만 허브 쪽 안전망이 직접 진단한다. 어느 쪽이든 검사 자체는
            // 항상 돌아야 한다 — 통과시키면 이미터가 지원 밖 타입에서 쓰러진다(CS8785).
            bool declaredInSource = symbol.Locations.Any(static l => l.IsInSource);
            if (!CheckPayloadTypes(method, references, declaredInSource ? NoReport : report, location))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>진단 없이 검증 결과만 보는 보고 싱크(중복 보고 방지용).</summary>
    static readonly System.Action<Diagnostic> NoReport = static _ => { };

    /// <summary>[RemoteProcedure] 메서드의 매개변수·반환 타입이 페이로드로 직렬화 가능한지 검증(DRPCGEN003).
    /// 선언부 프로바이더(허브 없이)와 허브 쪽 안전망이 공유한다. 제네릭은 닫힌 구성별로 검사한다.</summary>
    internal static bool CheckPayloadTypes(MethodMetadata method, AttributeReferences references,
        System.Action<Diagnostic> report, Location location)
    {
        IEnumerable<IMethodSymbol> signatures = method.IsGeneric ? method.Generic!.ClosedMethods : new[] { method.Symbol };
        foreach (IMethodSymbol signature in signatures)
        {
            foreach (IParameterSymbol parameter in signature.Parameters)
            {
                if (parameter.RefKind != RefKind.None)
                {
                    report(Diagnostic.Create(DiagnosticDescriptors.UnsupportedType, location, method.MethodName,
                        parameter.Type.ToDisplayString(), "ref/in/out parameter"));
                    return false;
                }

                if (!RpcPayload.IsSupported(parameter.Type, references, out _))
                {
                    report(Diagnostic.Create(DiagnosticDescriptors.UnsupportedType, location, method.MethodName,
                        parameter.Type.ToDisplayString(), UnsupportedReason(parameter.Type)));
                    return false;
                }
            }

            if (!RpcPayload.IsSupported(signature.ReturnType, references, allowVoid: true, out _))
            {
                report(Diagnostic.Create(DiagnosticDescriptors.UnsupportedType, location, method.MethodName,
                    signature.ReturnType.ToDisplayString(), UnsupportedReason(signature.ReturnType)));
                return false;
            }
        }

        return true;
    }

    /// <summary>DRPCGEN003 의 "Reason" 자리 문구.</summary>
    static string UnsupportedReason(ITypeSymbol type)
        => IsTask(type)
            ? "declare the contract with a plain return type (Task<int> -> int); the generated stub is already async"
            : "unsupported type";

    static bool IsTask(ITypeSymbol type)
    {
        string name = type.OriginalDefinition.ToDisplayString();
        return name == "System.Threading.Tasks.Task" || name == "System.Threading.Tasks.Task<TResult>";
    }

    static bool Implements(INamedTypeSymbol? type, string interfaceMetadataName)
        => type != null && type.AllInterfaces.Any(i => i.ToDisplayString() == interfaceMetadataName);

    /// <summary>
    /// 베이스 체인에서 허브 베이스를 찾는다. 클라이언트 측 <c>ClientHub&lt;&gt;</c>(DRPC.Client.Network) 는
    /// client endpoint, 서버 측 <c>ServerHub&lt;&gt;</c>(DRPC.Server.Network) 는 server endpoint(ADR-0001).
    /// </summary>
    internal static bool TryResolveHub(INamedTypeSymbol hubSymbol, out INamedTypeSymbol? hubBase, out NetworkKind networkKind,
        out bool invalidBase)
    {
        hubBase = null;
        networkKind = NetworkKind.Client;
        invalidBase = false;

        for (INamedTypeSymbol? current = hubSymbol.BaseType;
             current != null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            if (current.TypeArguments.Length != 2 || current.OriginalDefinition.TypeParameters.Length != 2)
            {
                continue;
            }

            string ns = current.OriginalDefinition.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            string name = current.OriginalDefinition.Name;

            if (ns == "DRPC.Client.Network" && name == "ClientHub")
            {
                hubBase = current;
                networkKind = NetworkKind.Client;
                return true;
            }

            if (ns == "DRPC.Server.Network" && name == "ServerHub")
            {
                hubBase = current;
                networkKind = NetworkKind.Server;
                return true;
            }

            if (ns == "DRPC.Shared.Network" && name == "HubBase")
            {
                // 허브이긴 한데 클라이언트/서버 중 어느 측인지 선언되지 않음 → 생성 불가 사유를 알려준다.
                hubBase = null;
                invalidBase = true;
                return false;
            }
        }

        return false;
    }
}
