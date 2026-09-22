using Communication.Shared.Sessions;
using DRPC.Shared.Interface;
using DRPC.Shared.Network;

namespace DRPC.Client.Network;

/// <summary>
/// Hub base inherited by the client application (ADR-0001: owner = client).
/// The generated partial derivative supplies <c>ConnectAsync</c>, outgoing stubs for the
/// server contract (<typeparamref name="TSPD"/>), and incoming dispatch for the client
/// contract (<typeparamref name="TCPD"/>).
/// </summary>
/// <typeparam name="TSPD">Function-declaration interface implemented by the server.</typeparam>
/// <typeparam name="TCPD">Function-declaration interface implemented by the client.</typeparam>
public abstract class ClientHub<TSPD, TCPD> : HubBase<TSPD, TCPD>
    where TSPD : IServerProcedureDeclarations
    where TCPD : IClientProcedureDeclarations
{
    protected ClientHub(Func<HubBase, ISession> sessionFactory)
        : base(sessionFactory)
    {
    }
}
