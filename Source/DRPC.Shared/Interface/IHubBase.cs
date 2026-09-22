using DRPC.Shared.Message;

namespace DRPC.Shared.Interface;

/// <summary>
/// The contract the hub runtime exposes to the inbound path (<see cref="DRPCMessageHandler"/>).
/// </summary>
public interface IHubBase
{
    /// <summary>Handles an inbound RPC request message.</summary>
    void OnReceiveRPCRequestMessage(ProcedureCallRequestMessage message);

    /// <summary>Handles an inbound RPC response message.</summary>
    void OnReceiveRPCResponseMessage(ProcedureCallResponseMessage message);

    /// <summary>Handles an inbound RPC error message.</summary>
    void OnReceiveRPCErrorMessage(ProcedureCallErrorMessage message);

    /// <summary>Fails all pending outgoing RPCs with <paramref name="reason"/>.</summary>
    void CancelPendingCalls(Exception reason);

    /// <summary>Notifies that the session has ended. Cancels pending calls, then raises the <c>Disconnected</c> event (once).</summary>
    void NotifyDisconnected(Exception? reason);

    /// <summary>
    /// Disconnection notification that carries the cause (sibling proposal P4 — e.g. a
    /// <c>FlowControl</c> backpressure signal). The default implementation delegates to the
    /// cause-less overload (compatible with existing implementations).
    /// </summary>
    void NotifyDisconnected(Exception? reason, Communication.Shared.Connection.DisconnectReason disconnectReason)
        => NotifyDisconnected(reason);
}
