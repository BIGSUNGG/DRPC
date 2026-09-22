using DRPC;
using DRPC.Client.Network;
using DRPC.Server.Network;
using DRPC.Shared.Interface;
using MessageProtocol;

namespace DRPC.E2E.Tests;

/// <summary>
/// Contracts for the RUDP loopback E2E tests. Server contracts (client→server) and client contracts (server→client) live in one file.
/// </summary>
public interface IServerProcedures : IServerProcedureDeclarations
{
    /// <summary>Omitted delivery mode = default ReliableOrdered.</summary>
    [RemoteProcedure(methodId: 0)]
    int Add(int value1, int value2);

    /// <summary>Sequenced override (state-update style calls).</summary>
    [RemoteProcedure(RpcDeliveryMode.Sequenced, 1)]
    string Echo(string text);

    /// <summary>Unreliable override.</summary>
    [RemoteProcedure(RpcDeliveryMode.Unreliable, 2)]
    void Ping(int seq);

    /// <summary>OneWay: delivered only, no response.</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableUnordered, 3, OneWay = true)]
    void Note(string text);

    /// <summary>Message-typed (NonId) parameter and return, plus nested collections and decimal.</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 4)]
    OrderSummary PlaceOrder(Order order);

    /// <summary>When the implementation throws, the caller receives an Unhandled fault.</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 5)]
    int AlwaysFails();

    /// <summary>Responds late enough to trigger the caller-side timeout.</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 6)]
    int Slow(int delayMs);

    /// <summary>Per-call timeout (TimeoutMs=400): expires quickly for this call only, regardless of the hub default (30s).</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 11, TimeoutMs = 400)]
    int SlowWithPerCallTimeout(int delayMs);

    /// <summary>Generic ①: return-only generic. Allowed T = int/string (message-typed returns are covered by ③④).</summary>
    [RemoteProcedure(methodId: 7)]
    [GenericProcedure(typeof(int), typeof(string))]
    T GetDefault<T>();

    /// <summary>Generic ②: parameter generic (caller-side type inference). T = int/string.</summary>
    [RemoteProcedure(methodId: 8)]
    [GenericProcedure(typeof(int), typeof(string))]
    string Describe<T>(T value);

    /// <summary>Generic ③: multiple slots combined (return T1 + parameters T2, T3; Cartesian product).</summary>
    [RemoteProcedure(methodId: 9)]
    [GenericProcedure(0, typeof(int), typeof(string))]
    [GenericProcedure(1, typeof(float), typeof(double))]
    [GenericProcedure(2, typeof(Order), typeof(ChatLine))]
    T1 Blend<T1, T2, T3>(T2 left, T3 right);

    /// <summary>Generic ④: [GenericMessage] parameter. T's allowed set is inherited from the Package configuration declarations (message types only — T members are serialized via runtime message dispatch).</summary>
    [RemoteProcedure(methodId: 10)]
    void Deliver<T>(Package<T> box);

    /// <summary>Validation=true: when _Validate returns true, the implementation is invoked.</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 12, Validation = true)]
    int GuardedAdd(int value1, int value2);

    /// <summary>Validation=true: when _Validate returns false, the implementation is skipped and a ValidationFailed(7) fault is returned.</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 13, Validation = true)]
    int GuardedReject(int value);

    /// <summary>Generic validation: passes only when T is int; anything else yields ValidationFailed.</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 14, Validation = true)]
    [GenericProcedure(typeof(int), typeof(string))]
    T GuardedDefault<T>();
}

public interface IClientProcedures : IClientProcedureDeclarations
{
    /// <summary>The server calls back into the client (bidirectional RPC).</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 0)]
    int ClientValue();

    /// <summary>Group polymorphism: the actual derived type is preserved.</summary>
    [RemoteProcedure(RpcDeliveryMode.ReliableUnordered, 1, OneWay = true)]
    void ReceiveLine(ChatLine line);
}

[Message(MessageKind.NonId)]
public partial class Order
{
    public string Item { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public List<int> Tags { get; set; } = new();
}

[Message(MessageKind.NonId)]
public partial class OrderSummary
{
    public string Receipt { get; set; } = string.Empty;
    public decimal Total { get; set; }
}

[Message(MessageKind.Parent, 20)]
public partial class ChatLine
{
    public string Text { get; set; } = string.Empty;
}

[Message(MessageKind.Child, 1)]
public partial class ShoutChatLine : ChatLine
{
}

/// <summary>[GenericMessage] for generic ④. Each configuration (ClassId) is identified on the wire by (MessageId, ClassId).
/// T must be an ID-headered message (Standalone/Group) — NonId blocks generic configuration registration.</summary>
[Message(MessageKind.Standalone, 50)]
[GenericMessage(typeof(Package<ChatLine>), ClassId = 1)]
[GenericMessage(typeof(Package<Receipt>), ClassId = 2)]
public partial class Package<T>
{
    public T Value { get; set; } = default!;
}

[Message(MessageKind.Standalone, 51)]
public partial class Receipt
{
    public string Tag { get; set; } = string.Empty;
}

/// <summary>
/// Server-side hub. Incoming = server contract (implemented here), Outgoing = client contract (calls the peer).
/// </summary>
public partial class E2EServerHub : ServerHub<IServerProcedures, IClientProcedures>
{
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> ReceivedNotes = new();
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> ReceivedGeneric = new();

    private partial Task<int> Add_Implementation(int value1, int value2) => Task.FromResult(value1 + value2);

    private partial Task<string> Echo_Implementation(string text) => Task.FromResult("echo:" + text);

    private partial Task Ping_Implementation(int seq) => Task.CompletedTask;

    private partial Task Note_Implementation(string text)
    {
        ReceivedNotes.Enqueue(text);
        return Task.CompletedTask;
    }

    private partial Task<OrderSummary> PlaceOrder_Implementation(Order order)
        => Task.FromResult(new OrderSummary
        {
            Receipt = $"{order.Item}x{order.Quantity}",
            Total = order.Quantity * 1.5m + order.Tags.Count,
        });

    private partial Task<int> AlwaysFails_Implementation() => throw new InvalidOperationException("intentional failure");

    private partial async Task<int> Slow_Implementation(int delayMs)
    {
        await Task.Delay(delayMs).ConfigureAwait(false);
        return delayMs;
    }

    private partial async Task<int> SlowWithPerCallTimeout_Implementation(int delayMs)
    {
        await Task.Delay(delayMs).ConfigureAwait(false);
        return delayMs;
    }

    private partial Task<T> GetDefault_Implementation<T>() => Task.FromResult<T>(default!);

    private partial Task<string> Describe_Implementation<T>(T value)
        => Task.FromResult($"{typeof(T).FullName}:{value}");

    private partial Task<T1> Blend_Implementation<T1, T2, T3>(T2 left, T3 right)
    {
        ReceivedGeneric.Enqueue($"Blend:{typeof(T2).Name}:{left?.GetType().Name ?? typeof(T2).Name}:{typeof(T3).Name}:{right?.GetType().Name ?? typeof(T3).Name}");
        return Task.FromResult<T1>(default!);
    }

    private partial Task Deliver_Implementation<T>(Package<T> box)
    {
        string detail = box.Value switch { ChatLine c => c.Text, Receipt r => r.Tag, _ => "?" };
        ReceivedGeneric.Enqueue($"Deliver:{typeof(T).Name}:{detail}");
        return Task.CompletedTask;
    }

    private partial Task<bool> GuardedAdd_Validate(int value1, int value2) => Task.FromResult(value1 >= 0);

    private partial Task<int> GuardedAdd_Implementation(int value1, int value2) => Task.FromResult(value1 + value2);

    private partial Task<bool> GuardedReject_Validate(int value) => Task.FromResult(false);

    // _Validate always returns false, so this implementation must never be invoked — if it ever runs, it surfaces as Unhandled(1) instead of ValidationFailed.
    private partial Task<int> GuardedReject_Implementation(int value) => throw new InvalidOperationException("GuardedReject_Implementation must not run");

    private partial Task<bool> GuardedDefault_Validate<T>() => Task.FromResult(typeof(T) == typeof(int));

    private partial Task<T> GuardedDefault_Implementation<T>() => Task.FromResult<T>(default!);
}

/// <summary>
/// Client-side hub. Outgoing = server contract, Incoming = client contract.
/// Only the methods the server calls back are implemented here.
/// </summary>
public partial class E2EClientHub : ClientHub<IServerProcedures, IClientProcedures>
{
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> ReceivedLines = new();

    private partial Task<int> ClientValue_Implementation() => Task.FromResult(4242);

    private partial Task ReceiveLine_Implementation(ChatLine line)
    {
        ReceivedLines.Enqueue(line.GetType().Name + ":" + line.Text);
        return Task.CompletedTask;
    }
}
