using Microsoft.CodeAnalysis;
using Xunit;

namespace DRPC.CodeGenerator.Tests;

/// <summary>
/// Generic procedure ([GenericProcedure]) generation and diagnostic tests.
/// Wire contract: the first 4 payload bytes = construction index (Cartesian product, slot 0 cycling slowest).
/// </summary>
public class GenericProcedureGeneratorTests
{
    const string ReturnOnlyContract =
        """
        [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 7)]
        [GenericProcedure(typeof(int), typeof(string))]
        T GetDefault<T>();
        """;

    [Fact]
    public void Return_only_generic_stub_emits_typeof_dispatch()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(ReturnOnlyContract));

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("public async global::System.Threading.Tasks.Task<T> GetDefaultAsync<T>(global::System.Threading.CancellationToken cancellationToken = default)", result.GeneratedSource);
        Assert.Contains("if (typeof(T) == typeof(global::System.Int32))", result.GeneratedSource);
        Assert.Contains("if (typeof(T) == typeof(global::System.String))", result.GeneratedSource);
        Assert.Contains("byte[] __payload = __WriteParams_ITestServerProcedures_GetDefault_0();", result.GeneratedSource);
        Assert.Contains("return (T)(object)__ReadReturn_ITestServerProcedures_GetDefault_1(__response);", result.GeneratedSource);
        Assert.Contains("is not declared with [GenericProcedure]", result.GeneratedSource);
    }

    [Fact]
    public void Construction_index_is_written_first_and_read_back_on_server()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ServerHub(
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 7)]
            [GenericProcedure(typeof(int), typeof(string))]
            T GetDefault<T>();
            """,
            hubBody: "private partial Task<T> GetDefault_Implementation<T>() => Task.FromResult<T>(default!);"));

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("__buf.WriteInt32(0);", result.GeneratedSource);
        Assert.Contains("__buf.WriteInt32(1);", result.GeneratedSource);
        Assert.Contains("int __ci = __rd.ReadInt32();", result.GeneratedSource);
        Assert.Contains("switch (__ci)", result.GeneratedSource);
        Assert.Contains("unknown generic construction index", result.GeneratedSource);
    }

    [Fact]
    public void Validation_gate_is_emitted_per_construction()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ServerHub(
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 7, Validation = true)]
            [GenericProcedure(typeof(int), typeof(string))]
            T GetDefault<T>();
            """,
            hubBody: """
                private partial Task<bool> GetDefault_Validate<T>() => Task.FromResult(true);
                private partial Task<T> GetDefault_Implementation<T>() => Task.FromResult<T>(default!);
                """));

        // Each construction gets a gate with closed type arguments, followed once by the open-generic partial declaration.
        Assert.Contains("if (!await GetDefault_Validate<global::System.Int32>().ConfigureAwait(false))", result.GeneratedSource);
        Assert.Contains("if (!await GetDefault_Validate<global::System.String>().ConfigureAwait(false))", result.GeneratedSource);
        Assert.Contains("throw new global::DRPC.Shared.RpcValidationFailedException(\"GetDefault\");", result.GeneratedSource);
        Assert.Contains("private partial global::System.Threading.Tasks.Task<bool> GetDefault_Validate<T>();", result.GeneratedSource);
        Assert.Empty(result.CompileErrors());
    }

    [Fact]
    public void Multi_slot_generic_emits_cartesian_arms_and_closed_implementation_call()
    {
        string contract =
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 8)]
            [GenericProcedure(0, typeof(int), typeof(string))]
            [GenericProcedure(1, typeof(float), typeof(double))]
            T1 Pick<T1, T2>(T2 low, T2 high);
            """;

        // The typeof dispatch arms appear on the calling side (the client hub's outgoing stubs).
        var client = GeneratorHarness.Run(GeneratorHarness.ClientHub(contract));

        Assert.Empty(client.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        // 4 constructions (slot 0 cycles slowest): (int,float)(int,double)(string,float)(string,double).
        Assert.Contains("typeof(T1) == typeof(global::System.Int32) && typeof(T2) == typeof(global::System.Single)", client.GeneratedSource);
        Assert.Contains("typeof(T1) == typeof(global::System.String) && typeof(T2) == typeof(global::System.Double)", client.GeneratedSource);
        Assert.Contains("__WriteReturn_ITestServerProcedures_Pick_3", client.GeneratedSource);

        // Per-construction closed implementation calls and the partial signature appear on the receiving side (the server hub).
        var server = GeneratorHarness.Run(GeneratorHarness.ServerHub(contract,
            hubBody: "private partial Task<T1> Pick_Implementation<T1, T2>(T2 low, T2 high) => Task.FromResult<T1>(default!);"));

        Assert.Empty(server.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("await Pick_Implementation<global::System.Int32, global::System.Single>(low, high)", server.GeneratedSource);
        // The implementation partial signature carries the type parameters.
        Assert.Contains("private partial global::System.Threading.Tasks.Task<T1> Pick_Implementation<T1, T2>(T2 low, T2 high);", server.GeneratedSource);
    }

    [Fact]
    public void Parameter_generic_stub_casts_closed_arguments()
    {
        string contract =
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 9)]
            [GenericProcedure(typeof(int), typeof(string))]
            void Log<T>(T value);
            """;

        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(contract));

        Assert.Contains("public async global::System.Threading.Tasks.Task LogAsync<T>(T value, global::System.Threading.CancellationToken cancellationToken = default)", result.GeneratedSource);
        Assert.Contains("__WriteParams_ITestServerProcedures_Log_1(((global::System.String)(object)value!))", result.GeneratedSource);
    }

    [Fact]
    public void GenericMessage_parameter_derives_slot_from_constructions()
    {
        string source = GeneratorHarness.ClientHub(
                """
                [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 10)]
                void Deliver<T>(Package<T> box);
                """)
            + """
              [MessageProtocol.Message(MessageProtocol.MessageKind.Standalone, 9)]
              public partial class Payload
              {
                  public int Id { get; set; }
              }

              [MessageProtocol.Message(MessageProtocol.MessageKind.Standalone, 50)]
              [MessageProtocol.GenericMessage(typeof(Package<Payload>), ClassId = 1)]
              public partial class Package<T>
              {
                  public T Value { get; set; } = default!;
              }
              """;

        var result = GeneratorHarness.Run(source);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("if (typeof(T) == typeof(global::Payload))", result.GeneratedSource);
        // The closed Package<Payload> uses ID-headered (object dispatch) serialization.
        Assert.Contains("MessageSerializer.SerializeToWriter", result.GeneratedSource);
    }

    [Fact]
    public void DRPCGEN009_when_generic_message_slot_type_is_not_a_message()
    {
        // MessageProtocol serializes [GenericMessage] T members via runtime message dispatch —
        // non-message types like int/string make the configuration itself invalid (a runtime crash promoted to compile time).
        string source = GeneratorHarness.ClientHub(
                """
                [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 10)]
                void Deliver<T>(Package<T> box);
                """)
            + """
              [MessageProtocol.Message(MessageProtocol.MessageKind.Standalone, 50)]
              [MessageProtocol.GenericMessage(typeof(Package<int>), ClassId = 1)]
              public partial class Package<T>
              {
                  public T Value { get; set; } = default!;
              }
              """;

        var result = GeneratorHarness.Run(source);

        Diagnostic diagnostic = Assert.Single(result.WithId("DRPCGEN009"));
        Assert.Contains("ID header", diagnostic.GetMessage());
    }

    [Fact]
    public void Generic_contract_with_implementations_compiles()
    {
        // What the client hub implements is the client contract (Echo). GetDefault is the server contract (send stub only).
        string source = GeneratorHarness.ClientHub(
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 7)]
            [GenericProcedure(typeof(int), typeof(string))]
            T GetDefault<T>();
            """,
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 0)]
            [GenericProcedure(typeof(int), typeof(string))]
            T Echo<T>(T value);
            """,
            """
            private partial Task<T> Echo_Implementation<T>(T value) => Task.FromResult(value);
            """);

        var result = GeneratorHarness.Run(source);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.CompileErrors());
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    [Fact]
    public void DRPCGEN007_when_generic_parameter_is_undeclared()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)] T Bad<T>(T value);"));

        Assert.True(result.HasDiagnostic("DRPCGEN007"));
        Assert.DoesNotContain("MethodCallActions.Add", result.GeneratedSource);
    }

    [Fact]
    public void DRPCGEN009_when_generic_procedure_on_non_generic_method()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)]
            [GenericProcedure(typeof(int))]
            int NotGeneric(int value);
            """));

        Assert.True(result.HasDiagnostic("DRPCGEN009"));
    }

    [Fact]
    public void DRPCGEN009_when_slot_is_out_of_range_or_duplicated()
    {
        var outOfRange = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)]
            [GenericProcedure(1, typeof(int))]
            T Bad<T>(T value);
            """));
        Assert.True(outOfRange.HasDiagnostic("DRPCGEN009"));

        var duplicated = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)]
            [GenericProcedure(typeof(int))]
            [GenericProcedure(typeof(string))]
            T Bad<T>(T value);
            """));
        Assert.True(duplicated.HasDiagnostic("DRPCGEN009"));
    }

    [Fact]
    public void DRPCGEN009_when_type_parameter_is_constrained()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)]
            [GenericProcedure(typeof(int))]
            T Bad<T>(T value) where T : class;
            """));

        Assert.True(result.HasDiagnostic("DRPCGEN009"));
    }

    [Fact]
    public void DRPCGEN008_when_explicit_type_argument_is_undeclared()
    {
        string source = GeneratorHarness.ClientHub(ReturnOnlyContract)
            + """
              public static class Caller
              {
                  public static async System.Threading.Tasks.Task Call(TestClientHub hub)
                      => await hub.GetDefaultAsync<System.DateTime>();
              }
              """;

        var result = GeneratorHarness.Run(source);

        Diagnostic diagnostic = Assert.Single(result.WithId("DRPCGEN008"));
        Assert.Contains("System.DateTime", diagnostic.GetMessage());
    }

    [Fact]
    public void DRPCGEN008_when_inferred_type_argument_is_undeclared()
    {
        string source = GeneratorHarness.ClientHub(
                """
                [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 7)]
                [GenericProcedure(typeof(int), typeof(string))]
                void Log<T>(T value);
                """)
            + """
              public static class Caller
              {
                  public static async System.Threading.Tasks.Task Bad(TestClientHub hub)
                      => await hub.LogAsync(3.5);

                  public static async System.Threading.Tasks.Task Ok(TestClientHub hub)
                      => await hub.LogAsync(42);
              }
              """;

        var result = GeneratorHarness.Run(source);

        Assert.Single(result.WithId("DRPCGEN008"));
    }

    [Fact]
    public void Non_generic_stub_calls_do_not_trigger_DRPCGEN008()
    {
        string source = GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 3)] int Add(int a, int b);")
            + """
              public static class Caller
              {
                  public static async System.Threading.Tasks.Task Call(TestClientHub hub)
                      => await hub.AddAsync(2, 3);
              }
              """;

        var result = GeneratorHarness.Run(source);

        Assert.False(result.HasDiagnostic("DRPCGEN008"));
    }
}
