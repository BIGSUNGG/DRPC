using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using DRPC.CodeGenerator.Reference;

namespace DRPC.CodeGenerator.Metadata;

/// <summary>
/// Instantiation metadata for a generic [RemoteProcedure] method.
///
/// Derives the closed set of instantiations from the per-slot allowed type lists declared via
/// [GenericProcedure], in a deterministic order (odometer with the last slot spinning fastest).
/// This order IS the wire instantiation index (the first 4 bytes of the payload), so identical
/// contracts on both sides produce identical tables.
///
/// A slot without a [GenericProcedure] declaration inherits its allowed set from the
/// constructions of a [GenericMessage] parameter/return in the signature (e.g. Package&lt;T&gt;) —
/// there T is not serialized directly; the message header (MessageId, ClassId) identifies T.
/// </summary>
internal sealed class GenericMethodMetadata
{
    public IMethodSymbol Symbol { get; }

    /// <summary>Slot (type-parameter ordinal) → allowed type list (declaration order).</summary>
    public IReadOnlyList<ITypeSymbol>?[] SlotTypes { get; }

    /// <summary>All closed instantiations. Order = wire instantiation index.</summary>
    public IReadOnlyList<ITypeSymbol[]> Instantiations { get; }

    /// <summary>The Construct-ed closed method per instantiation (parameters and return type substituted).</summary>
    public IReadOnlyList<IMethodSymbol> ClosedMethods { get; }

    /// <summary>Names of the method's type parameters (for stub and implementation partial signatures).</summary>
    public IReadOnlyList<string> TypeParameterNames { get; }

    /// <summary>Why validation failed. Null when valid.</summary>
    public string? Error { get; }

    /// <summary>Diagnostic id reported on failure (DRPCGEN007 undeclared / DRPCGEN009 invalid declaration).</summary>
    public string ErrorId { get; } = "DRPCGEN009";

    /// <summary>Upper bound on instantiations the generator emits. Excess is rejected with a diagnostic.</summary>
    public const int MaxInstantiations = 64;

    GenericMethodMetadata(
        IMethodSymbol symbol,
        IReadOnlyList<ITypeSymbol>?[] slotTypes,
        IReadOnlyList<ITypeSymbol[]> instantiations,
        IReadOnlyList<IMethodSymbol> closedMethods,
        string? error,
        string errorId = "DRPCGEN009")
    {
        ErrorId = errorId;
        Symbol = symbol;
        SlotTypes = slotTypes;
        Instantiations = instantiations;
        ClosedMethods = closedMethods;
        TypeParameterNames = symbol.TypeParameters.Select(static p => p.Name).ToArray();
        Error = error;
    }

    /// <summary>Builds instantiation metadata from the method symbol. Returns the failure reason via <see cref="Error"/> instead of throwing.</summary>
    public static GenericMethodMetadata Build(IMethodSymbol method, AttributeReferences references)
    {
        int arity = method.TypeParameters.Length;
        var slotTypes = new IReadOnlyList<ITypeSymbol>?[arity];

        // 1) Parse [GenericProcedure] declarations
        foreach (var attribute in method.GetAttributes())
        {
            if (!references.IsGenericProcedureAttribute(attribute.AttributeClass))
            {
                continue;
            }

            // (params Type[]) → slot 0; (int slot, params Type[]) → the declared slot.
            int slot;
            var typeArgs = attribute.ConstructorArguments;
            if (typeArgs.Length == 1)
            {
                slot = 0;
            }
            else if (typeArgs.Length == 2 && typeArgs[0].Value is int declaredSlot)
            {
                slot = declaredSlot;
            }
            else
            {
                return Failed(method, "invalid [GenericProcedure] constructor shape", "DRPCGEN009");
            }

            if (slot < 0 || slot >= arity)
            {
                return Failed(method, $"[GenericProcedure] slot {slot} is out of range; the method has {arity} type parameter(s)", "DRPCGEN009");
            }

            if (slotTypes[slot] != null)
            {
                return Failed(method, $"[GenericProcedure] slot {slot} is declared more than once", "DRPCGEN009");
            }

            var types = typeArgs[^1].Values
                .Select(static v => v.Value)
                .OfType<ITypeSymbol>()
                .ToList();
            if (types.Count == 0)
            {
                return Failed(method, $"[GenericProcedure] slot {slot} declares no types", "DRPCGEN009");
            }

            if (types.Distinct(SymbolEqualityComparer.Default).Count() != types.Count)
            {
                return Failed(method, $"[GenericProcedure] slot {slot} declares the same type more than once", "DRPCGEN009");
            }

            slotTypes[slot] = types;
        }

        // 2) Undeclared slots: inherit from [GenericMessage] usages in the signature (Package<T> pattern).
        foreach (ITypeParameterSymbol typeParameter in method.TypeParameters)
        {
            int slot = typeParameter.Ordinal;
            if (slotTypes[slot] != null)
            {
                continue;
            }

            if (TryDeriveFromGenericMessage(method, typeParameter, references, out IReadOnlyList<ITypeSymbol>? derived, out string? deriveError))
            {
                slotTypes[slot] = derived;
            }
            else
            {
                return Failed(method, deriveError!, "DRPCGEN007");
            }
        }

        // 2.5) Slots flowing into [GenericMessage] type arguments are serialized through the
        // MessageProtocol runtime message dispatch — every allowed type must be an ID-header
        // message type (reject at compile time rather than crash at runtime).
        foreach (ITypeParameterSymbol typeParameter in method.TypeParameters)
        {
            if (SlotUsedAsGenericMessageArgument(method, typeParameter, references) is not { } message)
            {
                continue;
            }

            foreach (ITypeSymbol allowed in slotTypes[typeParameter.Ordinal]!)
            {
                if (references.MessageStyleOf(allowed) != MessageStyle.HasId)
                {
                    return Failed(method,
                        $"type '{allowed.ToDisplayString()}' is used inside [GenericMessage] '{message.Name}' whose constructions are dispatched by ID header — declare an ID-header message type (Standalone/Group) instead",
                        "DRPCGEN009");
                }
            }
        }

        // 3) Cartesian product + Construct.
        var instantiations = Cartesian(slotTypes!).ToList();
        if (instantiations.Count > MaxInstantiations)
        {
            return Failed(method, $"{instantiations.Count} instantiations exceed the cap of {MaxInstantiations}; reduce [GenericProcedure] types or slots", "DRPCGEN009");
        }

        var closed = instantiations
            .Select(args => method.Construct(args))
            .ToList();

        return new GenericMethodMetadata(method, slotTypes, instantiations, closed, null);
    }

    /// <summary>Returns the message declaration if this type parameter is used in a [GenericMessage] type-argument position.</summary>
    static INamedTypeSymbol? SlotUsedAsGenericMessageArgument(IMethodSymbol method, ITypeParameterSymbol typeParameter, AttributeReferences references)
    {
        foreach (ITypeSymbol? usage in method.Parameters.Select(static p => p.Type)
                     .Append(method.ReturnType))
        {
            if (usage is INamedTypeSymbol named &&
                named.IsGenericType &&
                references.HasGenericMessageAttribute(named) &&
                named.TypeArguments.Any(t => SymbolEqualityComparer.Default.Equals(t, typeParameter)))
            {
                return named;
            }
        }

        return null;
    }

    /// <summary>For slots used only as a [GenericMessage] type argument (like Package&lt;T&gt;), derives the allowed set from that message's construction declarations.</summary>
    static bool TryDeriveFromGenericMessage(
        IMethodSymbol method,
        ITypeParameterSymbol typeParameter,
        AttributeReferences references,
        out IReadOnlyList<ITypeSymbol>? derived,
        out string? error)
    {
        derived = null;
        error = null;

        string slotName = typeParameter.Name;
        IReadOnlyList<ITypeSymbol>? found = null;
        int foundAtPosition = -1;
        INamedTypeSymbol? foundMessage = null;

        foreach (ITypeSymbol? usage in method.Parameters.Select(static p => p.Type)
                     .Append(method.ReturnType))
        {
            if (usage is not INamedTypeSymbol named || !named.IsGenericType)
            {
                continue;
            }

            if (!references.HasGenericMessageAttribute(named))
            {
                continue;
            }

            // Read the message's construction declarations ([GenericMessage(typeof(X<...>), ...)]).
            for (int position = 0; position < named.TypeArguments.Length; position++)
            {
                if (!SymbolEqualityComparer.Default.Equals(named.TypeArguments[position], typeParameter))
                {
                    continue;
                }

                if (found != null && (!SymbolEqualityComparer.Default.Equals(named, foundMessage) || position != foundAtPosition))
                {
                    error = $"type parameter '{slotName}' has no [GenericProcedure] declaration and is used in more than one [GenericMessage] position — declare [GenericProcedure] for slot {typeParameter.Ordinal} explicitly";
                    return false;
                }

                foundMessage = named;
                foundAtPosition = position;
                found = references.GetGenericConstructionArguments(named, position);
            }
        }

        if (found == null || found.Count == 0)
        {
            error = $"type parameter '{slotName}' has no [GenericProcedure] declaration and is not derivable from a [GenericMessage] parameter — declare [GenericProcedure({typeParameter.Ordinal}, …)]";
            return false;
        }

        derived = found;
        return true;
    }

    /// <summary>Odometer (last slot spins fastest). Row-major order with slot 0 slowest.</summary>
    static IEnumerable<ITypeSymbol[]> Cartesian(IReadOnlyList<ITypeSymbol>[] slots)
    {
        int[] index = new int[slots.Length];
        long total = slots.Aggregate(1L, static (acc, s) => acc * s.Count);

        for (long n = 0; n < total; n++)
        {
            yield return slots.Select((s, k) => s[index[k]]).ToArray();

            for (int k = slots.Length - 1; k >= 0; k--)
            {
                if (++index[k] < slots[k].Count)
                {
                    break;
                }

                index[k] = 0;
            }
        }
    }

    static GenericMethodMetadata Failed(IMethodSymbol method, string error, string errorId)
        => new(method, new IReadOnlyList<ITypeSymbol>?[method.TypeParameters.Length],
            System.Array.Empty<ITypeSymbol[]>(), System.Array.Empty<IMethodSymbol>(), error, errorId);
}
