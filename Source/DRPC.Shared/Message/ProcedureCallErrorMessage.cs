using MessageProtocol;

namespace DRPC.Shared.Message;

/// <summary>RPC failure response. The calling side observes it as a <c>RpcFaultException</c>.</summary>
[MessageProtocol.Message(MessageProtocol.MessageKind.Standalone, 2, MessageProtocol.MessageCategory.Category1)]
public partial class ProcedureCallErrorMessage
{
    /// <summary>The CallId of the faulted call.</summary>
    public uint CallId { get; private set; }

    /// <summary>The <see cref="RpcErrorCode"/> value.</summary>
    public int ErrorCode { get; private set; }

    /// <summary>Human-readable fault description from the remote side.</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>Creates an empty message (required by the wire deserializer).</summary>
    public ProcedureCallErrorMessage()
    {
    }

    /// <summary>Creates a failure response for the given call.</summary>
    public ProcedureCallErrorMessage(uint callId, int errorCode, string message)
    {
        CallId = callId;
        ErrorCode = errorCode;
        Message = message ?? string.Empty;
    }
}
