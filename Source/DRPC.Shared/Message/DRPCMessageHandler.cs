using Communication.Shared.Messages;
using Communication.Shared.Sessions;
using DRPC.Shared.Interface;
using DRPC.Shared.Message;

namespace DRPC.Shared;

/// <summary>
/// A <see cref="MessageHandler"/> that routes inbound messages into the hub runtime.
/// The transport invokes this handler synchronously and the hub only serializes and enqueues.
/// </summary>
public sealed class DRPCMessageHandler : MessageHandler
{
    readonly IHubBase _hub;

    /// <summary>Registers the RPC message handlers on the given session's handler pipeline.</summary>
    public DRPCMessageHandler(ISession session, IHubBase hub)
        : base(session)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));

        Register<ProcedureCallRequestMessage>(message => _hub.OnReceiveRPCRequestMessage(message));
        Register<ProcedureCallResponseMessage>(message => _hub.OnReceiveRPCResponseMessage(message));
        Register<ProcedureCallErrorMessage>(message => _hub.OnReceiveRPCErrorMessage(message));

        // Session.Disconnected is the only disconnection signal (heartbeats belong to the transport layer).
        Session.Disconnected += OnSessionDisconnected;
    }

    void OnSessionDisconnected(object? sender, Communication.Shared.Connection.DisconnectedEventArgs e)
        => _hub.NotifyDisconnected(
            new InvalidOperationException($"RPC session disconnected ({e.Reason})."),
            e.Reason);
}
