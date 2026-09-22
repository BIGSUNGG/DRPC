namespace DRPC.Shared.Interface;

/// <summary>
/// Marker for server-side RPC contract interfaces. The generator assigns the interface's
/// <c>[RemoteProcedure]</c> methods as Incoming on a server hub and Outgoing on a client hub.
/// </summary>
public interface IServerProcedureDeclarations
{
}

/// <summary>
/// Marker for client-side RPC contract interfaces. Incoming on a server hub, Outgoing on a client hub.
/// </summary>
public interface IClientProcedureDeclarations
{
}
