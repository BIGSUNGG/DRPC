using DRPC;
using DRPC.Shared.Interface;
using MessageProtocol;

namespace Sandbox.Contracts;

/// <summary>
/// Contract implemented by the server and called by the client. Return types are written plainly, without <c>Task</c>
/// (the generated client stubs already expose <c>{Method}Async</c>).
/// </summary>
public interface IGameServerProcedures : IServerProcedureDeclarations
{
    /// <summary>Omitting the delivery mode defaults to ReliableOrdered.</summary>
    [RemoteProcedure(methodId: 0)]
    int Add(int value1, int value2);

    /// <summary>MessageProtocol message types ([Message(MessageKind.NonId)]) can be used directly as parameters and return values.</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)]
    PlayerJoined Join(Player player);

    /// <summary>Sequenced state updates: loss is tolerated and updates arrive in order, so the latest received update is the current state.</summary>
    [RemoteProcedure(RpcDeliveryMode.Sequenced, 2)]
    void SetPosition(int playerId, float x, float y);

    /// <summary>OneWay: does not wait for a response.</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableUnordered, 3, OneWay = true)]
    void LogChat(string text);

    /// <summary>Polymorphism: declaring the group root type preserves the actual derived type, which is restored on delivery.</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableUnordered, 4, OneWay = true)]
    void ChatMessage(ChatLine line);

    /// <summary>Generic ①: return-only. Pre-declare the allowed T set via [GenericProcedure].</summary>
    [RemoteProcedure(methodId: 5)]
    [GenericProcedure(typeof(int), typeof(string))]
    T GetConfig<T>();

    /// <summary>Generic ②: parameter generic. Called like a normal method without type arguments (T is inferred).</summary>
    [RemoteProcedure(methodId: 6)]
    [GenericProcedure(typeof(int), typeof(string))]
    string Describe<T>(T value);

    /// <summary>Generic ③: multiple slots combined (Cartesian product). T2/T3 are parameters, T1 is the return.</summary>
    [RemoteProcedure(methodId: 7)]
    [GenericProcedure(0, typeof(int), typeof(string))]
    [GenericProcedure(1, typeof(float), typeof(double))]
    [GenericProcedure(2, typeof(Player), typeof(ChatLine))]
    T1 Blend<T1, T2, T3>(T2 left, T3 right);

    /// <summary>Generic ④: [GenericMessage] parameter. T's allowed set is inherited from the GiftBox configuration declarations.</summary>
    [RemoteProcedure(methodId: 8)]
    void Unwrap<T>(GiftBox<T> box);

    /// <summary>F14 validation gate: _Implementation runs only when _Validate returns true. On false,
    /// the implementation never executes and the client receives an RpcErrorCode.ValidationFailed(7) fault.</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 9, Validation = true)]
    int TransferGold(int fromPlayer, int toPlayer, int amount);
}

/// <summary>Contract implemented by the client and called by the server (bidirectional RPC).</summary>
public interface IGameClientProcedures : IClientProcedureDeclarations
{
    [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 0)]
    float EchoSum(List<float> values);

    /// <summary>Example mixing nullable and array parameters.</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)]
    int CountConfig(string? label, int[] values);

    [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 2, OneWay = true)]
    void NotifyScore(ScoreBoard score);
}

/// <summary>DTOs carry the MessageProtocol message attribute — RPC reuses this serialization as-is. (NonId does not allow id/category arguments)</summary>
[Message(MessageKind.NonId)]
public partial class Player
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

[Message(MessageKind.NonId)]
public partial class PlayerJoined
{
    public int PlayerId { get; set; }
    public int RoomId { get; set; }
}

[Message(MessageKind.NonId)]
public partial class ScoreLine
{
    public int PlayerId { get; set; }
    public int Score { get; set; }
}

[Message(MessageKind.NonId)]
public partial class ScoreBoard
{
    public string Map { get; set; } = string.Empty;
    public List<ScoreLine> Lines { get; set; } = new();
}

/// <summary>Group root. When a parameter is declared as this type, the derived element types below arrive as-is.</summary>
[Message(MessageKind.Parent, 11, MessageCategory.Category2)]
public partial class ChatLine
{
    public string Text { get; set; } = string.Empty;

    public virtual string Describe() => $"chat: {Text}";
}

// 3.0.0 migration: manual positional id 0 from the old syntax is not expressible in the new syntax (id 0 = omitted → FullName hash) — a hash id is used instead.
[Message(MessageKind.Child)]
public partial class ShoutChatLine : ChatLine
{
    public override string Describe() => $"SHOUT: {Text.ToUpperInvariant()}";
}

/// <summary>
/// [GenericMessage] for generic ④. T must be an ID-headered message (Standalone/Group) —
/// configuration registration requires (MessageId, ClassId) dispatch (NonId and primitives are rejected with DRPCGEN009).
/// </summary>
[Message(MessageKind.Standalone, 60, MessageCategory.Category2)]
[GenericMessage(typeof(GiftBox<ChatLine>), ClassId = 1)]
[GenericMessage(typeof(GiftBox<Token>), ClassId = 2)]
public partial class GiftBox<T>
{
    public T Gift { get; set; } = default!;
}

[Message(MessageKind.Standalone, 61, MessageCategory.Category2)]
public partial class Token
{
    public int Value { get; set; }
}
