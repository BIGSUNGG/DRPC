namespace DRPC.Shared.Message;

/// <summary>RPC error codes (wire values). Carried in the error response's <c>ErrorCode</c>.</summary>
public static class RpcErrorCode
{
    /// <summary>The implementation (body) threw an exception.</summary>
    public const int Unhandled = 1;

    /// <summary>Request for an unregistered MethodId.</summary>
    public const int UnknownMethod = 2;

    /// <summary>The response wait limit (<c>HubBase.RpcTimeout</c>) was exceeded. Generated on the calling side.</summary>
    public const int Timeout = 3;

    /// <summary>The connection dropped while a call was pending. Generated on the calling side.</summary>
    public const int Disconnected = 4;

    /// <summary>Incoming processing exceeded the <c>MaxConcurrentIncoming</c> limit.</summary>
    public const int Overloaded = 5;

    /// <summary><c>HubBase.AuthorizeRequestAsync</c> rejected the request (no permission to call).</summary>
    public const int PermissionDenied = 6;

    /// <summary><c>_Validate</c> returned false, rejecting the <c>_Implementation</c> call.</summary>
    public const int ValidationFailed = 7;
}
