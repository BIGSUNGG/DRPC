using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DRPC.CodeGenerator.Metadata;
using DRPC.CodeGenerator.Reference;

namespace DRPC.CodeGenerator;

/// <summary>
/// Generic stub call-site check (DRPCGEN008). The generated <c>{Method}Async&lt;…&gt;</c> stubs do
/// not exist yet in the input compilation, so their calls stay unresolved. This catches such
/// calls structurally: when the receiver is a hub and the name is a contract generic method
/// + "Async", it validates the slot sets via explicit type arguments or argument-type
/// inference. Positions that cannot be inferred (return-only slots, etc.) are skipped — a
/// runtime backstop (stub throw) remains.
/// </summary>
internal static class GenericCallSiteCheck
{
    /// <summary>Returns a diagnostic when the call site binds a generic slot to an undeclared type, otherwise null.</summary>
    public static Diagnostic? Check(InvocationExpressionSyntax invocation, SemanticModel semanticModel, AttributeReferences references)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name: SimpleNameSyntax name } access)
        {
            return null;
        }

        string invokedName = name.Identifier.ValueText;
        if (!invokedName.EndsWith("Async", StringComparison.Ordinal))
        {
            return null;
        }

        // If it already resolves to a member the user defined, it is not a generated-stub call.
        if (semanticModel.GetSymbolInfo(invocation).Symbol != null)
        {
            return null;
        }

        if (semanticModel.GetTypeInfo(access.Expression).Type is not INamedTypeSymbol receiverType)
        {
            return null;
        }

        if (!RpcHubSourceGenerator.TryResolveHub(receiverType, out INamedTypeSymbol? hubBase, out _, out _))
        {
            return null;
        }

        string contractMethodName = invokedName[..^"Async".Length];

        foreach (INamedTypeSymbol? contract in new[] { hubBase!.TypeArguments[0] as INamedTypeSymbol, hubBase.TypeArguments[1] as INamedTypeSymbol })
        {
            if (contract == null)
            {
                continue;
            }

            var declarations = new DeclarationsMetadata(contract, references);
            MethodMetadata? method = declarations.Methods.FirstOrDefault(m => m.MethodName == contractMethodName);

            if (method?.Generic is not { Error: null } generic)
            {
                continue;
            }

            ITypeSymbol?[] bindings = new ITypeSymbol?[generic.SlotTypes.Length];

            // Explicit type arguments: <int, string>
            if (name is GenericNameSyntax genericName &&
                genericName.TypeArgumentList.Arguments.Count == bindings.Length)
            {
                for (int k = 0; k < bindings.Length; k++)
                {
                    bindings[k] = semanticModel.GetSymbolInfo(genericName.TypeArgumentList.Arguments[k]).Symbol as ITypeSymbol;
                }
            }
            else if (invocation.ArgumentList.Arguments.Count == method.Parameters.Length)
            {
                // Inference: bind slots from arguments when the parameter type is a bare type
                // parameter or a single-position [GenericMessage] usage.
                for (int j = 0; j < method.Parameters.Length; j++)
                {
                    ITypeSymbol parameterType = method.Parameters[j].Type;
                    ITypeSymbol? argumentType = semanticModel.GetTypeInfo(invocation.ArgumentList.Arguments[j].Expression).Type;

                    BindSlot(parameterType, argumentType, method, generic, bindings);
                }
            }

            for (int k = 0; k < bindings.Length; k++)
            {
                if (bindings[k] is not { } bound)
                {
                    continue;
                }

                if (!generic.SlotTypes[k]!.Any(allowed => SymbolEqualityComparer.Default.Equals(allowed, bound)))
                {
                    Location location = (name as GenericNameSyntax)?.TypeArgumentList.GetLocation()
                        ?? invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.GetLocation()
                        ?? invocation.GetLocation();

                    return Diagnostic.Create(DiagnosticDescriptors.GenericTypeArgumentNotDeclared, location,
                        contractMethodName, bound.ToDisplayString(), contractMethodName, k);
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>Binds a slot from the argument type when the parameter type is (a) the type parameter itself or (b) a [GenericMessage] using that type parameter as an argument.</summary>
    static void BindSlot(ITypeSymbol parameterType, ITypeSymbol? argumentType, MethodMetadata method, GenericMethodMetadata generic, ITypeSymbol?[] bindings)
    {
        if (argumentType == null)
        {
            return;
        }

        for (int k = 0; k < method.Symbol.TypeParameters.Length; k++)
        {
            ITypeParameterSymbol typeParameter = method.Symbol.TypeParameters[k];

            if (parameterType is ITypeParameterSymbol direct &&
                SymbolEqualityComparer.Default.Equals(direct, typeParameter))
            {
                bindings[k] = argumentType;
                return;
            }

            if (parameterType is INamedTypeSymbol message &&
                message.IsGenericType &&
                method.References.HasGenericMessageAttribute(message) &&
                message.TypeArguments.Any(t => SymbolEqualityComparer.Default.Equals(t, typeParameter)) &&
                argumentType is INamedTypeSymbol closed &&
                SymbolEqualityComparer.Default.Equals(closed.ConstructedFrom, message.ConstructedFrom))
            {
                for (int m = 0; m < message.TypeArguments.Length; m++)
                {
                    if (SymbolEqualityComparer.Default.Equals(message.TypeArguments[m], typeParameter))
                    {
                        bindings[k] = closed.TypeArguments.Length == message.TypeArguments.Length ? closed.TypeArguments[m] : null;
                        return;
                    }
                }
            }
        }
    }
}
