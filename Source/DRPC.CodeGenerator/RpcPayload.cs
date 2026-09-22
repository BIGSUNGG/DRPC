using System.Text;
using Microsoft.CodeAnalysis;
using DRPC.CodeGenerator.Reference;

namespace DRPC.CodeGenerator;

/// <summary>
/// Unrolls RPC payload (parameter/return value) byte encoding into code.
///
/// Approach: flat concatenation — everything is appended to a single buffer in declaration
/// order. Primitives, strings, enums, nullables, byte[], and arrays/Lists are written directly
/// by this class; MessageProtocol message types are delegated to the MessageProtocol runtime
/// (never re-implement nested serialization).
///
/// Locals in the emitted code are numbered by field depth, so sibling containers in the same
/// scope never collide on variable names.
/// </summary>
internal static class RpcPayload
{
    const string Buf = "__buf";
    const string Rd = "__rd";

    internal static readonly SymbolDisplayFormat Qualified = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>Whether the type can be written/read as payload. On false, <paramref name="reason"/> carries the type display.</summary>
    public static bool IsSupported(ITypeSymbol type, AttributeReferences references, out string reason)
        => IsSupported(type, references, allowVoid: false, out reason);

    /// <summary>Whether the type can be written/read as payload, optionally allowing void.</summary>
    public static bool IsSupported(ITypeSymbol type, AttributeReferences references, bool allowVoid, out string reason)
    {
        reason = string.Empty;

        if (type.SpecialType == SpecialType.System_Void)
        {
            return allowVoid;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_String:
                return true;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return underlyingType(type) != null;
        }

        if (type is IArrayTypeSymbol array)
        {
            return array.Rank == 1 && IsSupported(array.ElementType, references, out reason);
        }

        if (type is INamedTypeSymbol named)
        {
            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                return IsSupported(named.TypeArguments[0], references, out reason);
            }

            if (named.IsMessage(references))
            {
                return true;
            }

            if (TryListOfT(named, out ITypeSymbol? element))
            {
                return IsSupported(element!, references, out reason);
            }
        }

        reason = type.ToDisplayString();
        return false;
    }

    /// <summary>Writes <paramref name="valueExpression"/> into the buffer (<c>__buf</c>).</summary>
    public static void EmitWrite(StringBuilder sb, string indent, ITypeSymbol type, string valueExpression,
        AttributeReferences references, int depth)
    {
        string? writer = PrimitiveWriter(type.SpecialType);
        if (writer != null)
        {
            sb.AppendLine($"{indent}{Buf}.{writer}({valueExpression});");
            return;
        }

        if (type.SpecialType == SpecialType.System_String)
        {
            sb.AppendLine($"{indent}{Buf}.WriteString({valueExpression});");
            return;
        }

        ITypeSymbol? underlying = underlyingType(type);
        if (underlying != null)
        {
            EmitWrite(sb, indent, underlying,
                $"(({underlying.ToDisplayString(Qualified)}){valueExpression})", references, depth);
            return;
        }

        if (type is IArrayTypeSymbol array)
        {
            if (array.ElementType.SpecialType == SpecialType.System_Byte)
            {
                sb.AppendLine($"{indent}{Buf}.WriteInt32({valueExpression}.Length);");
                sb.AppendLine($"{indent}{Buf}.WriteBytes({valueExpression});");
                return;
            }

            EmitVectorWrite(sb, indent, array.ElementType, valueExpression, "Length", references, depth);
            return;
        }

        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            string inner = nullable.TypeArguments[0].ToDisplayString(Qualified);
            sb.AppendLine($"{indent}{Buf}.WriteBoolean({valueExpression}.HasValue);");
            sb.AppendLine($"{indent}if ({valueExpression}.HasValue)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    {inner} __value{depth} = {valueExpression}.Value;");
            EmitWrite(sb, indent + "    ", nullable.TypeArguments[0], $"__value{depth}", references, depth + 1);
            sb.AppendLine($"{indent}}}");
            return;
        }

        if (type is INamedTypeSymbol list && TryListOfT(list, out ITypeSymbol? element))
        {
            EmitVectorWrite(sb, indent, element!, valueExpression, "Count", references, depth);
            return;
        }

        if (type.IsMessage(references))
        {
            sb.AppendLine($"{indent}{MessageWriteCall(references, type)}({valueExpression}, ref {Buf});");
            return;
        }

        throw new System.NotSupportedException($"EmitWrite: unsupported type {type.ToDisplayString()}");
    }

    /// <summary>Reads a value from the reader (<c>__rd</c>) into <paramref name="target"/>.</summary>
    public static void EmitRead(StringBuilder sb, string indent, ITypeSymbol type, string target, bool declare,
        AttributeReferences references, int depth)
    {
        if (declare)
        {
            string literal = type.IsReferenceType ? "default!" : "default";
            sb.AppendLine($"{indent}{type.ToDisplayString(Qualified)} {target} = {literal};");
        }

        string? reader = PrimitiveReader(type.SpecialType);
        if (reader != null)
        {
            sb.AppendLine($"{indent}{target} = {Rd}.{reader}();");
            return;
        }

        if (type.SpecialType == SpecialType.System_String)
        {
            sb.AppendLine($"{indent}{target} = {Rd}.ReadString()!;");
            return;
        }

        ITypeSymbol? underlying = underlyingType(type);
        if (underlying != null)
        {
            string? underlyingReader = PrimitiveReader(underlying.SpecialType);
            sb.AppendLine($"{indent}{target} = ({type.ToDisplayString(Qualified)}){Rd}.{underlyingReader}();");
            return;
        }

        if (type is IArrayTypeSymbol array)
        {
            if (array.ElementType.SpecialType == SpecialType.System_Byte)
            {
                sb.AppendLine($"{indent}int __length{depth} = {Rd}.ReadInt32();");
                sb.AppendLine($"{indent}{target} = {Rd}.ReadBytes(__length{depth}).ToArray();");
                return;
            }

            string elementType = array.ElementType.ToDisplayString(Qualified);
            sb.AppendLine($"{indent}int __length{depth} = {Rd}.ReadInt32();");
            sb.AppendLine($"{indent}{target} = new {elementType}[__length{depth}];");
            sb.AppendLine($"{indent}for (int __i{depth} = 0; __i{depth} < __length{depth}; __i{depth}++)");
            sb.AppendLine($"{indent}{{");
            EmitRead(sb, indent + "    ", array.ElementType, $"{target}[__i{depth}]", false, references, depth + 1);
            sb.AppendLine($"{indent}}}");
            return;
        }

        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            sb.AppendLine($"{indent}if ({Rd}.ReadBoolean())");
            sb.AppendLine($"{indent}{{");
            EmitRead(sb, indent + "    ", nullable.TypeArguments[0], $"__raw{depth}", true, references, depth + 1);
            sb.AppendLine($"{indent}    {target} = __raw{depth};");
            sb.AppendLine($"{indent}}}");
            return;
        }

        if (type is INamedTypeSymbol list && TryListOfT(list, out ITypeSymbol? element))
        {
            sb.AppendLine($"{indent}int __length{depth} = {Rd}.ReadInt32();");
            sb.AppendLine($"{indent}{target} = new {type.ToDisplayString(Qualified)}(__length{depth});");
            sb.AppendLine($"{indent}for (int __i{depth} = 0; __i{depth} < __length{depth}; __i{depth}++)");
            sb.AppendLine($"{indent}{{");
            EmitRead(sb, indent + "    ", element!, $"__item{depth}", true, references, depth + 1);
            sb.AppendLine($"{indent}    {target}.Add(__item{depth});");
            sb.AppendLine($"{indent}}}");
            return;
        }

        if (type.IsMessage(references))
        {
            sb.AppendLine($"{indent}{MessageReadStatement(references, type, target)}");
            return;
        }

        throw new System.NotSupportedException($"EmitRead: unsupported type {type.ToDisplayString()}");
    }

    /// <summary>
    /// Writes a message value. NonId uses type-fixed serialization (the generated static
    /// methods); messages with an ID header (Standalone/Group/Generic) use object dispatch to
    /// preserve group polymorphism.
    /// </summary>
    static string MessageWriteCall(AttributeReferences references, ITypeSymbol type)
        => references.MessageStyleOf(type) == MessageStyle.HasId
            ? "global::MessageProtocol.Serialize.MessageSerializer.SerializeToWriter"
            : "global::MessageProtocol.Serialize.MessageSerializer.Serialize";

    static string MessageReadStatement(AttributeReferences references, ITypeSymbol type, string target)
    {
        string display = type.ToDisplayString(Qualified);
        return references.MessageStyleOf(type) == MessageStyle.HasId
            ? $"{target} = ({display})global::MessageProtocol.Serialize.MessageSerializer.DeserializeFromReader(ref {Rd});"
            : $"{target} = global::MessageProtocol.Serialize.MessageSerializer.Deserialize<{display}>(ref {Rd});";
    }

    static void EmitVectorWrite(StringBuilder sb, string indent, ITypeSymbol element, string valueExpression,
        string countMember, AttributeReferences references, int depth)
    {
        sb.AppendLine($"{indent}{Buf}.WriteInt32({valueExpression}.{countMember});");
        sb.AppendLine($"{indent}for (int __i{depth} = 0; __i{depth} < {valueExpression}.{countMember}; __i{depth}++)");
        sb.AppendLine($"{indent}{{");
        EmitWrite(sb, indent + "    ", element, $"{valueExpression}[__i{depth}]", references, depth + 1);
        sb.AppendLine($"{indent}}}");
    }

    /// <summary>The underlying integer type for enums, otherwise null. (Roslyn exposes EnumUnderlyingType only on INamedTypeSymbol.)</summary>
    static ITypeSymbol? underlyingType(ITypeSymbol type)
        => type is INamedTypeSymbol { TypeKind: TypeKind.Enum } named ? named.EnumUnderlyingType : null;

    static bool TryListOfT(ITypeSymbol type, out ITypeSymbol? element)
    {
        element = null;
        if (type is INamedTypeSymbol { IsGenericType: true, IsUnboundGenericType: false } named &&
            named.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.List<T>")
        {
            element = named.TypeArguments[0];
            return true;
        }

        return false;
    }

    static string? PrimitiveWriter(SpecialType specialType) => specialType switch
    {
        SpecialType.System_Boolean => "WriteBoolean",
        SpecialType.System_Byte => "WriteByte",
        SpecialType.System_SByte => "WriteSByte",
        SpecialType.System_Int16 => "WriteInt16",
        SpecialType.System_UInt16 => "WriteUInt16",
        SpecialType.System_Int32 => "WriteInt32",
        SpecialType.System_UInt32 => "WriteUInt32",
        SpecialType.System_Int64 => "WriteInt64",
        SpecialType.System_UInt64 => "WriteUInt64",
        SpecialType.System_Single => "WriteSingle",
        SpecialType.System_Double => "WriteDouble",
        SpecialType.System_Decimal => "WriteDecimal",
        SpecialType.System_Char => "WriteChar",
        _ => null,
    };

    static string? PrimitiveReader(SpecialType specialType) => specialType switch
    {
        SpecialType.System_Boolean => "ReadBoolean",
        SpecialType.System_Byte => "ReadByte",
        SpecialType.System_SByte => "ReadSByte",
        SpecialType.System_Int16 => "ReadInt16",
        SpecialType.System_UInt16 => "ReadUInt16",
        SpecialType.System_Int32 => "ReadInt32",
        SpecialType.System_UInt32 => "ReadUInt32",
        SpecialType.System_Int64 => "ReadInt64",
        SpecialType.System_UInt64 => "ReadUInt64",
        SpecialType.System_Single => "ReadSingle",
        SpecialType.System_Double => "ReadDouble",
        SpecialType.System_Decimal => "ReadDecimal",
        SpecialType.System_Char => "ReadChar",
        _ => null,
    };
}
