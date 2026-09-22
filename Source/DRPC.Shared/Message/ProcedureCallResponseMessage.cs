using MessageProtocol;

namespace DRPC.Shared.Message;

/// <summary>RPC success response. Echoes the request's CallId.</summary>
[MessageProtocol.Message(MessageProtocol.MessageKind.Standalone, 1, MessageProtocol.MessageCategory.Category1)]
public partial class ProcedureCallResponseMessage
{
    /// <summary>The CallId echoed from the request.</summary>
    public uint CallId { get; private set; }

    /// <summary>Serialized return-value payload. Empty when the method returns void.</summary>
    public byte[] ReturnData { get; private set; } = System.Array.Empty<byte>();

    /// <summary>Creates an empty message (required by the wire deserializer).</summary>
    public ProcedureCallResponseMessage()
    {
    }

    /// <summary>Creates a success response for the given call.</summary>
    public ProcedureCallResponseMessage(uint callId, byte[] returnData)
    {
        CallId = callId;
        ReturnData = returnData ?? System.Array.Empty<byte>();
    }
}
