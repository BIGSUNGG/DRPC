using MessageProtocol;

namespace DRPC.Shared.Message;

/// <summary>RPC request. Calls that wait for a response and one-way calls share this message (one-way has <see cref="CallId"/> of 0).</summary>
[MessageProtocol.Message(MessageProtocol.MessageKind.Standalone, 0, MessageProtocol.MessageCategory.Category1)]
public partial class ProcedureCallRequestMessage
{
    /// <summary>Call identifier. 0 means one-way (no response).</summary>
    public uint CallId { get; private set; }

    /// <summary>The <c>[RemoteProcedure]</c> MethodId.</summary>
    public int MethodId { get; private set; }

    /// <summary>Serialized parameter payload. Empty when the call has no parameters.</summary>
    public byte[] ParameterData { get; private set; } = System.Array.Empty<byte>();

    /// <summary>Creates an empty message (required by the wire deserializer).</summary>
    public ProcedureCallRequestMessage()
    {
    }

    /// <summary>Creates a request message for the given call.</summary>
    public ProcedureCallRequestMessage(uint callId, int methodId, byte[] parameterData)
    {
        CallId = callId;
        MethodId = methodId;
        ParameterData = parameterData ?? System.Array.Empty<byte>();
    }
}
