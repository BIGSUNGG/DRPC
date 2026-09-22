using System.Reflection;
using DRPC.Shared.Interface;
using Xunit;

namespace DRPC.E2E.Tests;

/// <summary>
/// Verifies the **shape** of the generated output via real-assembly reflection.
/// (Emitting generated files through MSBuild's EmitCompilerGeneratedFiles and grepping them has a pitfall:
///  the property propagates to referenced projects and duplicates MessageProtocol generation — this approach is stronger.)
/// </summary>
public class GeneratedShapeTests
{
    static readonly Type[] Hubs = { typeof(E2EClientHub), typeof(E2EServerHub) };

    static readonly MethodInfo[] ContractMethods =
        typeof(IServerProcedures).GetMethods().Concat(typeof(IClientProcedures).GetMethods()).ToArray();

    const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    [Fact]
    public void Every_contract_method_has_an_async_stub_and_no_sync_stub()
    {
        foreach (MethodInfo contract in ContractMethods)
        {
            Assert.Contains(Hubs, hub => hub.GetTypeInfo().GetDeclaredMethod(contract.Name + "Async") is not null);

            foreach (Type hub in Hubs)
            {
                // A sync stub (named like the contract) must not exist — guards the async-only decision (ADR-0002) against regressions.
                Assert.Null(hub.GetTypeInfo().GetDeclaredMethod(contract.Name));
            }
        }
    }

    [Fact]
    public void No_member_is_marked_obsolete()
    {
        foreach (Type hub in Hubs)
        {
            Assert.DoesNotContain(hub.GetMembers(PublicInstance | BindingFlags.Static),
                m => m.GetCustomAttribute<ObsoleteAttribute>() is not null);
        }
    }

    [Fact]
    public void Connection_factories_are_generated_with_connectionkey_overloads()
    {
        // Generated members are not referenced via nameof — if generation breaks, exactly one test should fail;
        // the whole test assembly failing to compile would be worse for diagnostics.
        MethodInfo? connect = typeof(E2EClientHub).GetMethod(
            "ConnectAsync",
            new[] { typeof(string), typeof(int), typeof(string), typeof(CancellationToken) });
        Assert.NotNull(connect);
        Assert.True(connect!.ReturnParameter.ParameterType.IsGenericType); // Task<THub>

        MethodInfo? listen = typeof(E2EServerHub).GetMethod(
            "ListenAsync",
            new[] { typeof(int), typeof(string), typeof(Func<E2EServerHub, Task>), typeof(CancellationToken) });
        Assert.NotNull(listen);
    }

    static bool HasDeclared(Type hub, string name)
        => hub.GetTypeInfo().GetDeclaredMethods(name).Any();

    [Fact]
    public void Incoming_contract_methods_get_implementation_hooks_on_the_owning_side()
    {
        // The server contract gets implementation hooks on the server hub, the client contract on the client hub.
        // (After compilation, partial definitions/implementations merge into one method, so existence is checked by name.)
        Assert.True(HasDeclared(typeof(E2EServerHub), "Add_Implementation"));
        Assert.True(HasDeclared(typeof(E2EServerHub), "PlaceOrder_Implementation"));
        Assert.True(HasDeclared(typeof(E2EClientHub), "ClientValue_Implementation"));
        Assert.True(HasDeclared(typeof(E2EClientHub), "ReceiveLine_Implementation"));

        Assert.False(HasDeclared(typeof(E2EServerHub), "ClientValue_Implementation"));
        Assert.False(HasDeclared(typeof(E2EClientHub), "Add_Implementation"));
    }

    [Fact]
    public void Contract_markers_are_respected_by_the_generator()
    {
        // Only contracts inheriting the marker interfaces are accepted as hub type arguments (the basis for generator DRPCGEN002).
        Assert.True(typeof(IServerProcedureDeclarations).IsAssignableFrom(typeof(IServerProcedures)));
        Assert.True(typeof(IClientProcedureDeclarations).IsAssignableFrom(typeof(IClientProcedures)));
        Assert.False(typeof(IServerProcedureDeclarations).IsAssignableFrom(typeof(IClientProcedures)));
    }
}
