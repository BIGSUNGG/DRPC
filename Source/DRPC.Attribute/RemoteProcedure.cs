namespace DRPC;

/// <summary>
/// Marks an interface method as an RPC contract. The source generator (DRPC.CodeGenerator) uses this
/// attribute to generate call stubs and inbound dispatch, so you never write transport or
/// serialization code by hand.
/// </summary>
/// <example>
/// <code>
/// [RemoteProcedure]                          // default ReliableOrdered
/// int Add(int a, int b);
///
/// [RemoteProcedure(RpcDeliveryMode.Unreliable, 7)]
/// void SetPosition(float x, float y);        // overrides the delivery mode
///
/// [RemoteProcedure(RpcDeliveryMode.ReliableUnordered, 8, OneWay = true)]
/// void Chat(string text);                    // one-way: no response
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RemoteProcedure : System.Attribute
{
    /// <summary>The delivery mode for this method. Defaults to <see cref="RpcDeliveryMode.ReliableOrdered"/>.</summary>
    public RpcDeliveryMode Mode { get; }

    /// <summary>
    /// The number identifying the method on the wire. When omitted (default -1), it is assigned
    /// automatically as the FNV-1a hash of the interface FQN, method name, and parameter signature —
    /// the same name always yields the same value regardless of declaration order.
    /// Hash collisions (duplicate MethodId within one declaration) are blocked with compile error DRPCGEN005.
    /// </summary>
    public int MethodId { get; }

    /// <summary>When true, only the request is sent and no response is awaited or sent back. The return type must be void.</summary>
    public bool OneWay { get; set; }

    /// <summary>
    /// When true, dispatch awaits <c>_Validate</c> before invoking <c>_Implementation</c>.
    /// <c>_Validate</c> is a <c>Task&lt;bool&gt;</c> method you implement in a partial class (same parameters
    /// as the original); <c>_Implementation</c> runs only if it returns true. On false, a
    /// <c>RpcErrorCode.ValidationFailed</c> (7) error response is sent without invoking the
    /// implementation (one-way calls skip silently). A missing implementation is a compile error
    /// (fail-closed).
    /// </summary>
    public bool Validation { get; set; }

    /// <summary>
    /// The maximum wait for this call's response, in milliseconds. The default -1 follows the hub
    /// default (<c>HubBase.RpcTimeout</c>). A positive value applies that budget to this call only —
    /// useful for giving slow batch calls a generous limit while everything else sticks to the hub
    /// default (per-call timeout policy). One-way calls never wait for a response, so this is
    /// meaningless for them (DRPCGEN011 warning). Values of 0 or below (except -1) are rejected by
    /// generator diagnostic DRPCGEN010.
    /// </summary>
    public int TimeoutMs { get; set; } = -1;

    /// <summary>Initializes the attribute with an optional delivery mode and wire method id.</summary>
    public RemoteProcedure(
        RpcDeliveryMode mode = RpcDeliveryMode.ReliableOrdered,
        int methodId = -1)
    {
        Mode = mode;
        MethodId = methodId;
    }
}
