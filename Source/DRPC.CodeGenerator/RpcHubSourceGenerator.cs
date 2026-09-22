using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using DRPC.CodeGenerator.Metadata;
using DRPC.CodeGenerator.Reference;

namespace DRPC.CodeGenerator;

/// <summary>
/// Inspects a hub class and produces the generated source string. Diagnostics are reported via
/// <see cref="System.Action{Diagnostic}"/>.
/// </summary>
internal static class RpcHubSourceGenerator
{
    /// <summary>
    /// Generates the partial hub source for <paramref name="hubSymbol"/>. Returns null when a
    /// structural blocking diagnostic (non-partial hub, invalid hub base) was reported; on
    /// declaration-validation failure, returns a minimal skeleton so user partials keep their
    /// definitions.
    /// </summary>
    /// <param name="hubSymbol">The hub class marked with [RpcHub].</param>
    /// <param name="references">Cached references to DRPC attribute symbols.</param>
    /// <param name="report">Receives diagnostics.</param>
    /// <param name="fallbackLocation">Location used when a symbol has no source location.</param>
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
            // On validation failure, still emit definition declarations so the user's partial
            // implementations are not left orphaned (prevents a wall of CS0759 follow-up errors).
            // The build failure itself is covered by the already-reported diagnostic.
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
            // Declarations from metadata (another assembly) have no source location —
            // anchor them to the hub class location so the diagnostic stays clickable.
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

            // Per-call timeout validation: must be -1 (inherit) or positive. 0/-N is rejected
            // at compile time instead of being silently ignored.
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

            // Type validation: methods declared in source are already diagnosed by the
            // declarations provider within the same compilation (avoids duplicate reporting).
            // Only declarations arriving via metadata are diagnosed directly by this hub-side
            // safety net. Either way the check itself must always run — letting unsupported
            // types through crashes the emitter (CS8785).
            bool declaredInSource = symbol.Locations.Any(static l => l.IsInSource);
            if (!CheckPayloadTypes(method, references, declaredInSource ? NoReport : report, location))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A report sink that discards diagnostics (used to observe validation results without duplicate reporting).</summary>
    static readonly System.Action<Diagnostic> NoReport = static _ => { };

    /// <summary>Validates that [RemoteProcedure] parameter and return types are serializable as payloads (DRPCGEN003).
    /// Shared by the declarations-only provider and the hub-side safety net. Generics are checked per closed instantiation.</summary>
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

    /// <summary>The "Reason" phrase slot of DRPCGEN003.</summary>
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
    /// Finds the hub base along the base-type chain. Client-side <c>ClientHub&lt;&gt;</c>
    /// (DRPC.Client.Network) is a client endpoint; server-side <c>ServerHub&lt;&gt;</c>
    /// (DRPC.Server.Network) is a server endpoint (ADR-0001).
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
                // It is a hub, but neither the client nor server side is declared →
                // report why generation is impossible.
                hubBase = null;
                invalidBase = true;
                return false;
            }
        }

        return false;
    }
}
