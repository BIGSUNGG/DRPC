using System.Linq;
using Microsoft.CodeAnalysis;
using DRPC.CodeGenerator.Reference;

namespace DRPC.CodeGenerator;

internal static class Extensions
{
    /// <summary>Whether the symbol carries the given attribute.</summary>
    public static bool HasAttribute(this ISymbol self, INamedTypeSymbol? attributeSymbol)
    {
        if (attributeSymbol == null)
        {
            return false;
        }

        return self.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeSymbol));
    }

    /// <summary>Returns the symbol's attribute data for the given attribute, or null when absent.</summary>
    public static AttributeData? FindAttribute(this ISymbol self, INamedTypeSymbol? attributeSymbol)
    {
        if (attributeSymbol == null)
        {
            return null;
        }

        foreach (var a in self.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeSymbol))
            {
                return a;
            }
        }

        return null;
    }

    /// <summary>Whether the type can go into a payload as a message type. It must carry a MessageProtocol display attribute ([Message]/[GenericMessage]) — implementing IMessageSerializable alone does not qualify (strict rule).</summary>
    public static bool IsMessage(this ITypeSymbol self, AttributeReferences references)
        => references.MessageStyleOf(self) != MessageStyle.None;
}
