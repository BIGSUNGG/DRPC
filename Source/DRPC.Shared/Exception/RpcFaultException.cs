namespace DRPC.Shared;

/// <summary>
/// An RPC error response sent by the peer (<see cref="Message.RpcErrorCode"/>). The calling side
/// observes this exception when awaiting the call.
/// </summary>
public sealed class RpcFaultException : SystemException
{
    /// <summary>The CallId of the call that produced the fault.</summary>
    public uint CallId { get; }

    /// <summary>The <see cref="Message.RpcErrorCode"/> value reported by the remote side.</summary>
    public int ErrorCode { get; }

    /// <summary>Initializes the exception with the faulting call's id, the remote error code, and the message.</summary>
    public RpcFaultException(uint callId, int errorCode, string message)
        : base(message)
    {
        CallId = callId;
        ErrorCode = errorCode;
    }
}
