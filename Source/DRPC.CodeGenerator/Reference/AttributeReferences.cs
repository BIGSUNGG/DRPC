using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace DRPC.CodeGenerator.Reference;

/// <summary>
/// Resolves the types the generator references by metadata name.
/// This is the seam that cuts compile-time coupling to the DRPC·MessageProtocol projects
/// (the name strings are the only contract).
/// </summary>
internal sealed class AttributeReferences
{
    /// <summary>Metadata name of [RemoteProcedure].</summary>
    public const string RemoteProcedureTypeName = "DRPC.RemoteProcedure";
    /// <summary>Metadata name of [GenericProcedure].</summary>
    public const string GenericProcedureTypeName = "DRPC.GenericProcedureAttribute";
    /// <summary>Metadata name of RpcDeliveryMode.</summary>
    public const string RpcDeliveryModeTypeName = "DRPC.RpcDeliveryMode";
    /// <summary>Metadata name of the client hub base.</summary>
    public const string ClientHubTypeName = "DRPC.Client.Network.ClientHub";
    /// <summary>Metadata name of the server hub base.</summary>
    public const string ServerHubTypeName = "DRPC.Server.Network.ServerHub";
    /// <summary>Metadata name of the server contract interface.</summary>
    public const string ServerDeclarationsTypeName = "DRPC.Shared.Interface.IServerProcedureDeclarations";
    /// <summary>Metadata name of the client contract interface.</summary>
    public const string ClientDeclarationsTypeName = "DRPC.Shared.Interface.IClientProcedureDeclarations";

    /// <summary>MessageProtocol's message display attribute family ([Message(MessageKind, …)], Generic).</summary>
    public const string MessageNamespace = "MessageProtocol";

    /// <summary>Frozen value of MessageProtocol.MessageKind.NonId (an immutable contract, like a wire flag). From 3.0.0 on, kind·ID·category are declared via the single [Message] attribute.</summary>
    public const int MessageKindNonIdValue = 4;

    /// <summary>The resolved [RemoteProcedure] attribute type, or null when not referenced.</summary>
    public INamedTypeSymbol? RemoteProcedureAttributeType { get; }
    /// <summary>The resolved [GenericProcedure] attribute type, or null when not referenced.</summary>
    public INamedTypeSymbol? GenericProcedureAttributeType { get; }
    /// <summary>The resolved RpcDeliveryMode type, or null when not referenced.</summary>
    public INamedTypeSymbol? RpcDeliveryModeType { get; }

    /// <summary>Resolves the DRPC attribute types from the user compilation.</summary>
    /// <param name="compilation">The compilation being analyzed.</param>
    public AttributeReferences(Compilation compilation)
    {
        RemoteProcedureAttributeType = compilation.GetTypeByMetadataName(RemoteProcedureTypeName);
        GenericProcedureAttributeType = compilation.GetTypeByMetadataName(GenericProcedureTypeName);
        RpcDeliveryModeType = compilation.GetTypeByMetadataName(RpcDeliveryModeTypeName);
    }

    /// <summary>Whether the attribute class is DRPC's [GenericProcedure].</summary>
    /// <param name="attributeClass">Attribute class to test, or null.</param>
    public bool IsGenericProcedureAttribute(INamedTypeSymbol? attributeClass)
        => attributeClass != null
            && attributeClass.ContainingNamespace?.ToDisplayString() == "DRPC"
            && attributeClass.Name == "GenericProcedureAttribute";

    /// <summary>Whether this message type (including closed constructions) declares at least one [GenericMessage] construction.</summary>
    public bool HasGenericMessageAttribute(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (IsGenericMessageAttribute(attribute))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>From [GenericMessage(typeof(X&lt;…&gt;), ClassId=…)] construction declarations, extracts only the position-th type argument, in declaration order.</summary>
    public IReadOnlyList<ITypeSymbol> GetGenericConstructionArguments(INamedTypeSymbol type, int position)
    {
        var result = new System.Collections.Generic.List<ITypeSymbol>();

        foreach (var attribute in type.GetAttributes())
        {
            if (!IsGenericMessageAttribute(attribute))
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is INamedTypeSymbol construction &&
                construction.TypeArguments.Length > position)
            {
                result.Add(construction.TypeArguments[position]);
            }
        }

        return result;
    }

    static bool IsGenericMessageAttribute(AttributeData attribute)
        => attribute.AttributeClass?.ContainingNamespace?.ToDisplayString() == MessageNamespace
            && attribute.AttributeClass.Name == "GenericMessageAttribute";

    /// <summary>Whether the type carries any MessageProtocol message display attribute.</summary>
    /// <param name="type">Type to test.</param>
    public bool HasMessageAttribute(ITypeSymbol type)
        => MessageStyleOf(type) != MessageStyle.None;

    /// <summary>
    /// Serialization style of a message type. Distinguishes the kinds that put an ID header on
    /// the wire (Standalone/Group/Generic) from NonId, which is sent type-fixed without a
    /// header — the basis for choosing which payload API to use for nested values.
    /// </summary>
    public MessageStyle MessageStyleOf(ITypeSymbol type)
    {
        var style = MessageStyle.None;
        foreach (var attribute in type.GetAttributes())
        {
            string? name = attribute.AttributeClass?.Name;
            string? ns = attribute.AttributeClass?.ContainingNamespace?.ToDisplayString();
            if (ns != MessageNamespace || name == null)
            {
                continue;
            }

            switch (name)
            {
                case "MessageAttribute":
                    // [Message(kind, id, category)] — NonId kinds use header-less, type-fixed
                    // serialization; all others (Automatic/Standalone/Parent/Child) put an ID
                    // header on the wire.
                    return IsNonIdKind(attribute)
                        ? MessageStyle.NonId
                        : MessageStyle.HasId;
                case "GenericMessageAttribute":
                    style = MessageStyle.HasId;
                    break;
            }
        }

        return style;
    }

    /// <summary>Whether the Kind argument of [Message] is NonId — checks both the positional and the named (kind:) argument. With no argument, Automatic (= an ID-header kind) is assumed.</summary>
    static bool IsNonIdKind(AttributeData attribute)
    {
        foreach (var argument in attribute.ConstructorArguments)
        {
            if (argument.Type?.TypeKind == TypeKind.Enum &&
                argument.Type.Name == "MessageKind" &&
                argument.Type.ContainingNamespace?.ToDisplayString() == MessageNamespace)
            {
                return argument.Value is int value && value == MessageKindNonIdValue;
            }
        }

        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "kind" &&
                named.Value.Type?.TypeKind == TypeKind.Enum &&
                named.Value.Type.Name == "MessageKind")
            {
                return named.Value.Value is int value && value == MessageKindNonIdValue;
            }
        }

        return false;
    }

}

/// <summary>How a message type is written into the payload.</summary>
internal enum MessageStyle
{
    /// <summary>Not a MessageProtocol message.</summary>
    None,

    /// <summary><c>[Message(MessageKind.NonId)]</c> — round-trips via generated static Serialize/Deserialize. Without the display attribute, NonId is not granted (strict rule).</summary>
    NonId,

    /// <summary>Standalone/Group/Generic — routed by the header ID, so object dispatch is possible (group polymorphism preserved).</summary>
    HasId,
}
