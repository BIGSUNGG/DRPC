using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace DRPC.CodeGenerator.Reference;

/// <summary>
/// 생성기가 참조하는 타입을 메타데이터 이름으로 해석한다.
/// DRPC·MessageProtocol 프로젝트와의 컴파일 타임 의존을 끊는 지점(이름 문자열이 유일한 계약).
/// </summary>
internal sealed class AttributeReferences
{
    public const string RemoteProcedureTypeName = "DRPC.RemoteProcedure";
    public const string GenericProcedureTypeName = "DRPC.GenericProcedureAttribute";
    public const string RpcDeliveryModeTypeName = "DRPC.RpcDeliveryMode";
    public const string ClientHubTypeName = "DRPC.Client.Network.ClientHub";
    public const string ServerHubTypeName = "DRPC.Server.Network.ServerHub";
    public const string ServerDeclarationsTypeName = "DRPC.Shared.Interface.IServerProcedureDeclarations";
    public const string ClientDeclarationsTypeName = "DRPC.Shared.Interface.IClientProcedureDeclarations";

    /// <summary>MessageProtocol 의 메시지 표시 속성류 ([Message(MessageKind, …)], Generic).</summary>
    public const string MessageNamespace = "MessageProtocol";

    /// <summary>MessageProtocol.MessageKind.NonId 열거값과 동결(와이어 플래그처럼 불변 계약). 3.0.0 부터 종류·ID·카테고리는 [Message] 단일 속성로 선언한다.</summary>
    public const int MessageKindNonIdValue = 4;

    public INamedTypeSymbol? RemoteProcedureAttributeType { get; }
    public INamedTypeSymbol? GenericProcedureAttributeType { get; }
    public INamedTypeSymbol? RpcDeliveryModeType { get; }

    public AttributeReferences(Compilation compilation)
    {
        RemoteProcedureAttributeType = compilation.GetTypeByMetadataName(RemoteProcedureTypeName);
        GenericProcedureAttributeType = compilation.GetTypeByMetadataName(GenericProcedureTypeName);
        RpcDeliveryModeType = compilation.GetTypeByMetadataName(RpcDeliveryModeTypeName);
    }

    public bool IsGenericProcedureAttribute(INamedTypeSymbol? attributeClass)
        => attributeClass != null
            && attributeClass.ContainingNamespace?.ToDisplayString() == "DRPC"
            && attributeClass.Name == "GenericProcedureAttribute";

    /// <summary>이 메시지 타입(닫힌 구성 포함)이 [GenericMessage] 구성을 하나 이상 선언했는지.</summary>
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

    /// <summary>[GenericMessage(typeof(X&lt;…&gt;), ClassId=…)] 구성 선언에서 position 번째 타입 인자만 순서대로 뽑는다.</summary>
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

    public bool HasMessageAttribute(ITypeSymbol type)
        => MessageStyleOf(type) != MessageStyle.None;

    /// <summary>
    /// 메시지 타입의 직렬화 스타일. 와이어에 ID 헤더를 얹는 종류(Standalone/Group/Generic)와
    /// 헤더 없이 타입 고정으로 얹는 NonId 를 가른다 — 중첩 값을 쓸 때 어느 API 를 써야 하는지의 근거.
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
                    // [Message(kind, id, category)] — Kind 가 NonId 면 헤더 없는 타입 고정 직렬화,
                    // 나머지(Automatic/Standalone/Parent/Child)는 모두 ID 헤더를 얹는다.
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

    /// <summary>[Message] 의 Kind 인자가 NonId 인지 — 위치 인자와 명명 인자(kind:) 양쪽을 본다. 인자가 없으면 Automatic(=ID 헤더 종류).</summary>
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

/// <summary>메시지 타입의 페이로드 기록 방식 구분.</summary>
internal enum MessageStyle
{
    /// <summary>MessageProtocol 메시지가 아님.</summary>
    None,

    /// <summary><c>[Message(MessageKind.NonId)]</c> — 생성된 정적 Serialize/Deserialize 로 왕복한다. 표시 속성이 없으면 NonId 로 인정하지 않는다(엄격 규칙).</summary>
    NonId,

    /// <summary>Standalone/Group/Generic — 헤더의 ID 로 라우팅하므로 object dispatch 가 가능하다(그룹 다형성 유지).</summary>
    HasId,
}
