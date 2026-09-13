using System.Linq;
using Microsoft.CodeAnalysis;
using DRPC.CodeGenerator.Reference;

namespace DRPC.CodeGenerator;

internal static class Extensions
{
    public static bool HasAttribute(this ISymbol self, INamedTypeSymbol? attributeSymbol)
    {
        if (attributeSymbol == null)
        {
            return false;
        }

        return self.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeSymbol));
    }

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

    /// <summary>페이로드에 넣을 수 있는 메시지 타입인지. MessageProtocol 표시 속성([Message]/[GenericMessage])이
    /// 반드시 붙어 있어야 한다 — 속성 없는 IMessageSerializable 구현만으로는 통과하지 않는다(엄격 규칙).</summary>
    public static bool IsMessage(this ITypeSymbol self, AttributeReferences references)
        => references.MessageStyleOf(self) != MessageStyle.None;
}
