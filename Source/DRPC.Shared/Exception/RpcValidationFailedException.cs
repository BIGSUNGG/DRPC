namespace DRPC.Shared;

/// <summary>
/// Thrown by generated dispatch when <c>_Validate</c> returns false and <c>_Implementation</c> is
/// skipped. The hub catches it and converts it into a <see cref="Message.RpcErrorCode.ValidationFailed"/>
/// (7) error response (one-way calls have no response channel and skip silently). This is an expected
/// rejection, so it is not logged as unhandled.
/// </summary>
public sealed class RpcValidationFailedException(string methodName)
    : SystemException($"The call to {methodName} was rejected by validation (_Validate returned false).");
