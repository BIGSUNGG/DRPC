using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using DRPC.CodeGenerator.Reference;

namespace DRPC.CodeGenerator.Metadata;

/// <summary>A contract interface (IServerProcedureDeclarations / IClientProcedureDeclarations) and its RPC methods.</summary>
internal sealed class DeclarationsMetadata
{
    /// <summary>The contract interface symbol.</summary>
    public INamedTypeSymbol Symbol { get; }
    /// <summary>The [RemoteProcedure] methods declared on the interface, in declaration order.</summary>
    public MethodMetadata[] Methods { get; }

    /// <summary>Collects the [RemoteProcedure] methods from the interface.</summary>
    public DeclarationsMetadata(INamedTypeSymbol declarationSymbol, AttributeReferences references)
    {
        Symbol = declarationSymbol;

        var methods = new List<MethodMetadata>();

        foreach (IMethodSymbol method in declarationSymbol
                     .GetMembers()
                     .OfType<IMethodSymbol>()
                     .Where(static m => m.MethodKind == MethodKind.Ordinary)
                     .Where(static m => !m.IsImplicitlyDeclared)
                     .Where(m => m.FindAttribute(references.RemoteProcedureAttributeType) != null))
        {
            methods.Add(new MethodMetadata(method, references));
        }

        Methods = methods.ToArray();
    }
}
