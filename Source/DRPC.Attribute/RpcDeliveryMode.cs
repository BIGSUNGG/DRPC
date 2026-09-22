namespace DRPC;

/// <summary>
/// The delivery mode of an RPC call. Defined by DRPC itself so that user code never references the
/// communication stack's (DS_Communication RUDP) enum. DRPC.Shared maps it to transport options.
/// </summary>
/// <remarks>
/// The values and meanings correspond 1:1 with RUDP's <c>RudpDeliveryMethod</c>. If that correspondence
/// drifts, update the mapping switch.
/// </remarks>
public enum RpcDeliveryMode
{
    /// <summary>Loss, duplication, and reordering are all possible. Use for frequent periodic state updates.</summary>
    Unreliable,

    /// <summary>No loss or duplication; ordering is not guaranteed.</summary>
    ReliableUnordered,

    /// <summary>Loss is possible, no duplication, ordered: a lost packet is skipped and later ones still arrive. Commonly used for frequent state updates.</summary>
    Sequenced,

    /// <summary>No loss or duplication, ordered. The default for RPC calls.</summary>
    ReliableOrdered,

    /// <summary>Only the most recent message arrives. Cannot be fragmented.</summary>
    ReliableSequenced,
}
