using Microsoft.CodeAnalysis;

namespace DRPC.CodeGenerator.Metadata;

internal enum NetworkKind
{
    /// <summary>Server-side hub (one per peer). Incoming = server contract, Outgoing = client contract.</summary>
    Server,

    /// <summary>Client-side hub. Incoming = client contract, Outgoing = server contract.</summary>
    Client,
}

internal sealed class TypeMetadata
{
    /// <summary>The hub class symbol.</summary>
    public INamedTypeSymbol Symbol { get; }
    /// <summary>The server contract declarations.</summary>
    public DeclarationsMetadata ServerDeclarations { get; }
    /// <summary>The client contract declarations.</summary>
    public DeclarationsMetadata ClientDeclarations { get; }
    /// <summary>Which side this hub owns.</summary>
    public NetworkKind NetworkKind { get; }
    /// <summary>The hub's namespace, or null for the global namespace.</summary>
    public string? Namespace { get; }

    /// <summary>Whether this hub is a server endpoint.</summary>
    public bool IsServerEndpoint => NetworkKind == NetworkKind.Server;

    /// <summary>Call stubs this hub sends to the peer.</summary>
    public MethodMetadata[] Outgoing => IsServerEndpoint ? ClientDeclarations.Methods : ServerDeclarations.Methods;

    /// <summary>Calls this hub receives and implements.</summary>
    public MethodMetadata[] Incoming => IsServerEndpoint ? ServerDeclarations.Methods : ClientDeclarations.Methods;

    /// <summary>Assembles hub metadata from both contract declarations.</summary>
    public TypeMetadata(
        INamedTypeSymbol symbol,
        DeclarationsMetadata serverDeclarations,
        DeclarationsMetadata clientDeclarations,
        NetworkKind networkKind)
    {
        Symbol = symbol;
        ServerDeclarations = serverDeclarations;
        ClientDeclarations = clientDeclarations;
        NetworkKind = networkKind;
        Namespace = symbol.ContainingNamespace?.IsGlobalNamespace == true
            ? null
            : symbol.ContainingNamespace?.ToDisplayString();
    }
}
