using System.Linq;
using Microsoft.CodeAnalysis;
using DRPC.CodeGenerator.Reference;

namespace DRPC.CodeGenerator.Metadata;

/// <summary>A single contract method marked with [RemoteProcedure].</summary>
internal sealed class MethodMetadata
{
    /// <summary>The contract method symbol.</summary>
    public IMethodSymbol Symbol { get; }
    /// <summary>The RPC method name (the contract method's name).</summary>
    public string MethodName => Symbol.Name;

    /// <summary>Name-resolution table for type classification (e.g. message-kind checks). Passed through to the emitter unchanged.</summary>
    public AttributeReferences References { get; }

    /// <summary>Name of the declaring interface. Used as a prefix to keep generated payload helper names collision-free.</summary>
    public string DeclarationName { get; }

    /// <summary>The wire method id.</summary>
    public int MethodId { get; }
    /// <summary>Whether the id came from an explicit [RemoteProcedure] argument (vs. the name-hash fallback).</summary>
    public bool HasExplicitMethodId { get; }
    /// <summary>Whether the call is one-way (fire-and-forget, no response wait).</summary>
    public bool OneWay { get; }

    /// <summary>Per-call response wait budget in milliseconds. -1 (default) = inherit the hub default; positive = this call's own budget.</summary>
    public int TimeoutMs { get; }

    /// <summary>When true, gates the _Implementation call behind a _Validate (Task&lt;bool&gt;) check.</summary>
    public bool Validation { get; }

    /// <summary>Example: <c>global::DRPC.RpcDeliveryMode.Unreliable</c></summary>
    public string ModeExpression { get; }

    public ParameterMetadata[] Parameters { get; }
    public ITypeSymbol ReturnType { get; }

    /// <summary>Instantiation table for a generic method. Null for non-generic methods.</summary>
    public GenericMethodMetadata? Generic { get; }

    public bool IsGeneric => Generic != null;

    public bool IsVoidReturn => ReturnType.SpecialType == SpecialType.System_Void;

    /// <summary>Signature of the generated user implementation (partial) method.</summary>
    public string ImplementationSignature
    {
        get
        {
            string typeParams = IsGeneric ? $"<{string.Join(", ", Generic!.TypeParameterNames)}>" : string.Empty;
            return IsVoidReturn
                ? $"global::System.Threading.Tasks.Task {MethodName}_Implementation{typeParams}({ParameterDeclarationList()})"
                : $"global::System.Threading.Tasks.Task<{ReturnTypeDisplay}> {MethodName}_Implementation{typeParams}({ParameterDeclarationList()})";
        }
    }

    /// <summary>Signature of the generated validation (partial) method when Validation=true. Parameters are identical to the original.</summary>
    public string ValidateSignature
    {
        get
        {
            string typeParams = IsGeneric ? $"<{string.Join(", ", Generic!.TypeParameterNames)}>" : string.Empty;
            return $"global::System.Threading.Tasks.Task<bool> {MethodName}_Validate{typeParams}({ParameterDeclarationList()})";
        }
    }

    /// <summary>Return type display used inside generated code (always fully qualified, safe across namespace differences).</summary>
    public string ReturnTypeDisplay => ReturnType.ToDisplayString(RpcPayload.Qualified);

    public MethodMetadata(IMethodSymbol methodSymbol, AttributeReferences references)
    {
        Symbol = methodSymbol;
        References = references;
        DeclarationName = methodSymbol.ContainingType?.Name ?? "Procedure";
        ReturnType = methodSymbol.ReturnType;
        Parameters = methodSymbol.Parameters
            .Select(p => new ParameterMetadata(p.Name, p.Type))
            .ToArray();

        AttributeData? attribute = methodSymbol.FindAttribute(references.RemoteProcedureAttributeType);
        ModeExpression = BuildModeExpression(attribute, references);
        (MethodId, HasExplicitMethodId) = ResolveMethodId(attribute, methodSymbol);
        OneWay = ResolveNamedFlag(attribute, nameof(OneWay));
        TimeoutMs = ResolveNamedInt(attribute, nameof(TimeoutMs), -1);
        Validation = ResolveNamedFlag(attribute, nameof(Validation));
        Generic = methodSymbol.IsGenericMethod ? GenericMethodMetadata.Build(methodSymbol, references) : null;
    }

    public string ParameterDeclarationList() => string.Join(", ", Parameters.Select(p =>
        $"{p.Type.ToDisplayString(RpcPayload.Qualified)} {p.Name}"));

    /// <summary>Builds the stable identity used for automatic wire MethodId assignment: interface FQN + method name + parameter-type signature. The same name always yields the same value, regardless of declaration order.</summary>
    static string BuildWireIdentity(IMethodSymbol method)
    {
        string owner = method.ContainingType?.ToDisplayString(RpcPayload.Qualified) ?? "";
        string signature = string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString(RpcPayload.Qualified)));
        return $"{owner}.{method.Name}({signature})";
    }

    /// <summary>FNV-1a 32-bit. string.GetHashCode is randomized per process, so it cannot serve as a wire contract.</summary>
    static int StableHash(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)hash;
        }
    }

    static (int methodId, bool explicitId) ResolveMethodId(AttributeData? attribute, IMethodSymbol method)
    {
        if (attribute != null)
        {
            if (attribute.ConstructorArguments.Length >= 2 && attribute.ConstructorArguments[1].Value is int ctorId && ctorId >= 0)
            {
                return (ctorId, true);
            }

            foreach (var named in attribute.NamedArguments)
            {
                if (named.Key == "MethodId" && named.Value.Value is int namedId && namedId >= 0)
                {
                    return (namedId, true);
                }
            }
        }

        return (StableHash(BuildWireIdentity(method)), false);
    }

    static bool ResolveNamedFlag(AttributeData? attribute, string key)
    {
        if (attribute == null)
        {
            return false;
        }

        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == key && named.Value.Value is bool flag)
            {
                return flag;
            }
        }

        return false;
    }

    static int ResolveNamedInt(AttributeData? attribute, string key, int fallback)
    {
        if (attribute == null)
        {
            return fallback;
        }

        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == key && named.Value.Value is int value)
            {
                return value;
            }
        }

        return fallback;
    }

    static string BuildModeExpression(AttributeData? attribute, AttributeReferences references)
    {
        const string fallback = "global::DRPC.RpcDeliveryMode.ReliableOrdered";

        if (attribute == null || attribute.ConstructorArguments.Length == 0)
        {
            return fallback;
        }

        object? raw = attribute.ConstructorArguments[0].Value;
        if (raw == null)
        {
            return fallback;
        }

        int value = System.Convert.ToInt32(raw);
        INamedTypeSymbol? mode = references.RpcDeliveryModeType;
        if (mode != null)
        {
            foreach (ISymbol member in mode.GetMembers())
            {
                if (member is IFieldSymbol { HasConstantValue: true } field &&
                    field.ConstantValue is int constant &&
                    constant == value)
                {
                    return $"global::DRPC.RpcDeliveryMode.{field.Name}";
                }
            }
        }

        return $"((global::DRPC.RpcDeliveryMode){value})";
    }

    internal sealed class ParameterMetadata
    {
        /// <summary>Parameter name.</summary>
        public string Name { get; }
        /// <summary>Parameter type.</summary>
        public ITypeSymbol Type { get; }

        /// <summary>Wraps a parameter symbol's name and type.</summary>
        public ParameterMetadata(string name, ITypeSymbol type)
        {
            Name = name;
            Type = type;
        }
    }
}
