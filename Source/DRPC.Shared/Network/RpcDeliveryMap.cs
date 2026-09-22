using Communication.Network.RUDP;

namespace DRPC.Shared.Network;

/// <summary>
/// Maps DRPC delivery modes (<see cref="RpcDeliveryMode"/>) to RUDP send options.
/// This is the boundary that keeps the transport enum out of the user's contract surface, and the only
/// place that bridges the two stacks.
/// </summary>
public static class RpcDeliveryMap
{
    /// <summary>Shared options instance per delivery mode (no allocation on the send path).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Unknown value.</exception>
    public static RudpSendOptions ToSendOptions(this RpcDeliveryMode mode) => mode switch
    {
        RpcDeliveryMode.Unreliable => RudpSendOptions.Unreliable,
        RpcDeliveryMode.ReliableUnordered => RudpSendOptions.ReliableUnordered,
        RpcDeliveryMode.Sequenced => RudpSendOptions.Sequenced,
        RpcDeliveryMode.ReliableSequenced => RudpSendOptions.ReliableSequenced,
        RpcDeliveryMode.ReliableOrdered => RudpSendOptions.ReliableOrdered,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown RpcDeliveryMode value."),
    };
}
