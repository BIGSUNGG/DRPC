using Communication.Shared.Sessions;
using DRPC.Shared.Interface;
using DRPC.Shared.Network;

namespace DRPC.Server.Network;

/// <summary>
/// Hub base inherited once per peer on the server (ADR-0001: owner = server).
/// The generated partial derivative supplies <c>ListenAsync</c>, outgoing stubs for the
/// client contract (<typeparamref name="TCPD"/>), and incoming dispatch for the server
/// contract (<typeparamref name="TSPD"/>).
/// </summary>
/// <typeparam name="TSPD">Function-declaration interface implemented by the server.</typeparam>
/// <typeparam name="TCPD">Function-declaration interface implemented by the client.</typeparam>
public abstract class ServerHub<TSPD, TCPD> : HubBase<TSPD, TCPD>
    where TSPD : IServerProcedureDeclarations
    where TCPD : IClientProcedureDeclarations
{
    protected ServerHub(Func<HubBase, ISession> sessionFactory)
        : base(sessionFactory)
    {
    }
}
