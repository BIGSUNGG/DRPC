using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit;

namespace DRPC.CodeGenerator.Tests;

/// <summary>
/// 생성기 진단(DRPCGEN001–011, 004 제외)과 생성 결과의 형태를 검사한다.
/// 레거시 대비 확정 동작: Outgoing 은 <c>{Method}Async</c> 만 생성한다(sync [Obsolete] 스텁 없음).
/// </summary>
public class RpcHubGeneratorTests
{
    const string AddContract = "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 3)] int Add(int value1, int value2);";

    /// <summary>
    /// 제너레이터와 동일한 FNV-1a 32비트. 암시 MethodId가 프로세스 랜덤(string.GetHashCode)이
    /// 아니라는 와이어 계약을 테스트가 고정한다.
    /// </summary>
    static int NameHash(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)hash;
        }
    }

    [Fact]
    public void Outgoing_generates_async_only_stub()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(AddContract));

        Assert.Contains("public async global::System.Threading.Tasks.Task<global::System.Int32> AddAsync(global::System.Int32 value1, global::System.Int32 value2, global::System.Threading.CancellationToken cancellationToken = default)", result.GeneratedSource);
        Assert.Contains("RequestRPC(3, __payload, global::DRPC.RpcDeliveryMode.ReliableOrdered, cancellationToken)", result.GeneratedSource);
        Assert.DoesNotContain("[global::System.Obsolete", result.GeneratedSource);
        Assert.DoesNotContain("public int Add(", result.GeneratedSource);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Omitted_mode_defaults_to_reliable_ordered()
    {
        // 인자 없는 [RemoteProcedure] = 기본 ReliableOrdered. MethodId 는 이름 해시로 자동 할당.
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub("[RemoteProcedure] int A();"));

        Assert.Contains($"RequestRPC({NameHash("global::ITestServerProcedures.A()")}, __payload, global::DRPC.RpcDeliveryMode.ReliableOrdered, cancellationToken)", result.GeneratedSource);
    }

    [Fact]
    public void Per_method_delivery_override_is_emitted()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.Unreliable, 4)] void Ping(int seq);"));

        Assert.Contains("RequestRPC(4, __payload, global::DRPC.RpcDeliveryMode.Unreliable, cancellationToken)", result.GeneratedSource);
    }

    [Fact]
    public void OneWay_uses_send_rpc_and_skips_response_read()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableUnordered, 5, OneWay = true)] void Note(string text);"));

        Assert.Contains("await SendRPC(5, __payload, global::DRPC.RpcDeliveryMode.ReliableUnordered)", result.GeneratedSource);
        Assert.DoesNotContain("OneWayMethodIds", result.GeneratedSource);
    }

    [Fact]
    public void Roundtrip_stubs_accept_cancellation_token_but_oneway_does_not()
    {
        var roundtrip = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 7)] int Q();"));

        // 무득수 왕복 스텁은 앞 쉼표 없이 취소 토큰을 받는다.
        Assert.Contains("QAsync(global::System.Threading.CancellationToken cancellationToken = default)", roundtrip.GeneratedSource);

        var oneWay = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 8, OneWay = true)] void N();"));

        Assert.DoesNotContain("NAsync(global::System.Threading.CancellationToken", oneWay.GeneratedSource);
        Assert.Contains("await SendRPC(8, __payload", oneWay.GeneratedSource);
    }

    [Fact]
    public void Void_without_oneway_still_waits_for_empty_response()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 6)] void Do();"));

        Assert.Contains("await RequestRPC(6, __payload", result.GeneratedSource);
    }

    [Fact]
    public void Incoming_generates_dispatch_and_partial_implementation_hook()
    {
        string source = GeneratorHarness.ServerHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)] int Compute(int value);",
            hubBody: "private partial Task<int> Compute_Implementation(int value) => Task.FromResult(value);");

        var result = GeneratorHarness.Run(source);

        Assert.Contains("private async global::System.Threading.Tasks.Task<byte[]> Compute_Requested(byte[] __parameterData)", result.GeneratedSource);
        Assert.Contains("private partial global::System.Threading.Tasks.Task<global::System.Int32> Compute_Implementation(global::System.Int32 value);", result.GeneratedSource);
        Assert.Contains("MethodCallActions.Add(1, Compute_Requested);", result.GeneratedSource);
        Assert.Contains("MethodDeliveryModes[1] = global::DRPC.RpcDeliveryMode.ReliableOrdered;", result.GeneratedSource);
    }

    [Fact]
    public void Server_endpoint_generates_listen_async_and_client_endpoint_connect_async()
    {
        var server = GeneratorHarness.Run(GeneratorHarness.ServerHub(AddContract));
        var client = GeneratorHarness.Run(GeneratorHarness.ClientHub(AddContract));

        Assert.Contains("ListenAsync(int port, string? connectionKey, global::System.Func<TestServerHub, global::System.Threading.Tasks.Task> onConnected", server.GeneratedSource);
        Assert.Contains("global::DRPC.Server.Network.RpcHost.ListenAsync", server.GeneratedSource);
        Assert.Contains("ConnectAsync(string host, int port, string? connectionKey", client.GeneratedSource);
        Assert.Contains("global::DRPC.Client.Network.RpcClient.ConnectAsync", client.GeneratedSource);
    }

    [Fact]
    public void Endpoint_generates_options_overload_routing_to_with_options_helpers()
    {
        var server = GeneratorHarness.Run(GeneratorHarness.ServerHub(AddContract));
        var client = GeneratorHarness.Run(GeneratorHarness.ClientHub(AddContract));

        Assert.Contains("ListenAsync(int port, global::DRPC.Shared.Network.RpcEndpointOptions options, global::System.Func<TestServerHub, global::System.Threading.Tasks.Task> onConnected", server.GeneratedSource);
        Assert.Contains("global::DRPC.Server.Network.RpcHost.ListenWithOptionsAsync(port, options,", server.GeneratedSource);
        Assert.Contains("ConnectAsync(string host, int port, global::DRPC.Shared.Network.RpcEndpointOptions options", client.GeneratedSource);
        Assert.Contains("global::DRPC.Client.Network.RpcClient.ConnectWithOptionsAsync(host, port, options,", client.GeneratedSource);
    }

    [Fact]
    public void Registration_is_seeded_only_from_incoming_contract()
    {
        // 서버 허브: Incoming = 서버 계약. 클라이언트 계약은 outgoing 이므로 등록하지 않는다.
        var result = GeneratorHarness.Run(GeneratorHarness.ServerHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)] int In();",
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)] int Out();"));

        Assert.Contains("MethodCallActions.Add(1, In_Requested);", result.GeneratedSource);
        Assert.DoesNotContain("Out_Requested", result.GeneratedSource);
        Assert.Contains("Task<global::System.Int32> OutAsync(global::System.Threading.CancellationToken cancellationToken = default)", result.GeneratedSource);
    }

    [Fact]
    public void Primitive_payload_is_flat_concatenation()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 9)] int Mix(int a, string label, float ratio);"));

        Assert.Contains("__buf.WriteInt32(a);", result.GeneratedSource);
        Assert.Contains("__buf.WriteString(label);", result.GeneratedSource);
        Assert.Contains("__buf.WriteSingle(ratio);", result.GeneratedSource);
        Assert.Contains("a = __rd.ReadInt32();", result.GeneratedSource);
    }

    [Fact]
    public void NonId_message_argument_uses_typed_serializer()
    {
        string source = GeneratorHarness.ClientHub(
                "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 8)] void Send(Player player);")
            + """
              [MessageProtocol.Message(MessageProtocol.MessageKind.NonId)]
              public partial class Player
              {
                  public int Id { get; set; }
              }
              """;

        var result = GeneratorHarness.Run(source);

        Assert.Contains("MessageSerializer.Serialize(player, ref __buf);", result.GeneratedSource);
        Assert.Contains("MessageSerializer.Deserialize<global::Player>(ref __rd)", result.GeneratedSource);
    }

    [Fact]
    public void Named_kind_argument_Message_attribute_is_recognized()
    {
        // 3.0.0 [Message] 신문법 커버리지: kind 를 명명 인자(kind:)로 준 NonId 선언도
        // 위치 인자와 동일하게 NonId 스타일(타입 고정 직렬화)로 인식해야 한다.
        string source = GeneratorHarness.ClientHub(
                "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 8)] void Send(Player player);")
            + """
              [MessageProtocol.Message(kind: MessageProtocol.MessageKind.NonId)]
              public partial class Player
              {
                  public int Id { get; set; }
              }
              """;

        var result = GeneratorHarness.Run(source);

        Assert.Contains("MessageSerializer.Serialize(player, ref __buf);", result.GeneratedSource);
        Assert.Contains("MessageSerializer.Deserialize<global::Player>(ref __rd)", result.GeneratedSource);
    }

    [Fact]
    public void Group_root_argument_keeps_polymorphic_object_dispatch()
    {
        string source = GeneratorHarness.ClientHub(
                "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 8)] void Send(ChatLine line);")
            + """
              [MessageProtocol.Message(MessageProtocol.MessageKind.Parent, 30)]
              public partial class ChatLine
              {
                  public string Text { get; set; } = string.Empty;
              }

              [MessageProtocol.Message(MessageProtocol.MessageKind.Child, 1)]
              public partial class ShoutChatLine : ChatLine
              {
              }
              """;

        var result = GeneratorHarness.Run(source);

        Assert.Contains("MessageSerializer.SerializeToWriter(line, ref __buf);", result.GeneratedSource);
        Assert.Contains("(global::ChatLine)global::MessageProtocol.Serialize.MessageSerializer.DeserializeFromReader(ref __rd)", result.GeneratedSource);
    }

    [Fact]
    public void Collection_payload_emits_length_prefix_and_loop()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 7)] int Sum(List<int> values, byte[] blob);"));

        Assert.Contains("__buf.WriteInt32(values.Count);", result.GeneratedSource);
        Assert.Contains("for (int __i1 = 0; __i1 < values.Count; __i1++)", result.GeneratedSource);
        Assert.Contains("__buf.WriteInt32(blob.Length);", result.GeneratedSource);
        Assert.Contains("__buf.WriteBytes(blob);", result.GeneratedSource);
        Assert.Contains("values = new global::System.Collections.Generic.List<global::System.Int32>(__length1);", result.GeneratedSource);
    }

    [Fact]
    public void Nullable_payload_emits_presence_flag()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 7)] int Opt(int? maybe);"));

        Assert.Contains("__buf.WriteBoolean(maybe.HasValue);", result.GeneratedSource);
        Assert.Contains("if (maybe.HasValue)", result.GeneratedSource);
        Assert.Contains("if (__rd.ReadBoolean())", result.GeneratedSource);
    }

    // ── 진단 ───────────────────────────────────────────────────────────

    [Fact]
    public void DRPCGEN001_when_hub_is_not_partial()
    {
        string source = GeneratorHarness.ClientHub(AddContract).Replace("public partial class TestClientHub", "public class TestClientHub");
        var result = GeneratorHarness.Run(source);

        Assert.True(result.HasDiagnostic("DRPCGEN001"));
        Assert.Empty(result.GeneratedSource);
    }

    [Fact]
    public void DRPCGEN002_when_inheriting_hubbase_directly()
    {
        string source = GeneratorHarness.ClientHub(AddContract).Replace(
            "ClientHub<ITestServerProcedures, ITestClientProcedures>",
            "global::DRPC.Shared.Network.HubBase<ITestServerProcedures, ITestClientProcedures>");
        var result = GeneratorHarness.Run(source);

        Assert.True(result.HasDiagnostic("DRPCGEN002"));
    }

    [Fact]
    public void DRPCGEN003_when_parameter_type_is_unsupported()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)] int Bad(System.Collections.Generic.Dictionary<string,int> map);"));

        Diagnostic_AssertIds(result, "DRPCGEN003");
    }

    [Fact]
    public void DRPCGEN003_when_contract_returns_task()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)] global::System.Threading.Tasks.Task<int> Bad();"));

        Diagnostic_AssertIds(result, "DRPCGEN003");
        Assert.Contains("plain return type", result.WithId("DRPCGEN003").Single().GetMessage());
    }

    [Fact]
    public void DRPCGEN003_when_methods_are_overloaded()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)] int Same(int a);
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 2)] int Same(string a);
            """));

        Diagnostic_AssertIds(result, "DRPCGEN003");
    }

    [Fact]
    public void Implicit_method_id_is_a_stable_name_hash()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered)] int Implicit();"));

        // 같은 식별자면 항상 같은 값 — string.GetHashCode(프로세스 랜덤)가 아니라는 고정.
        Assert.DoesNotContain("RequestRPC(0,", result.GeneratedSource);
        Assert.Contains($"RequestRPC({NameHash("global::ITestServerProcedures.Implicit()")}, __payload", result.GeneratedSource);
    }

    [Fact]
    public void Implicit_method_ids_do_not_shift_when_declaration_order_changes()
    {
        var ab = GeneratorHarness.Run(GeneratorHarness.ClientHub("[RemoteProcedure] int A();\n[RemoteProcedure] int B();"));
        var ba = GeneratorHarness.Run(GeneratorHarness.ClientHub("[RemoteProcedure] int B();\n[RemoteProcedure] int A();"));

        int a = NameHash("global::ITestServerProcedures.A()");
        int b = NameHash("global::ITestServerProcedures.B()");

        foreach (string source in new[] { ab.GeneratedSource, ba.GeneratedSource })
        {
            Assert.Contains($"RequestRPC({a}, __payload", source);
            Assert.Contains($"RequestRPC({b}, __payload", source);
        }

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DRPCGEN005_when_method_ids_duplicate()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)] int A();
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)] int B();
            """));

        Diagnostic_AssertIds(result, "DRPCGEN005");
        Assert.DoesNotContain("MethodCallActions", result.GeneratedSource);
    }

    [Fact]
    public void DRPCGEN006_when_oneway_returns_value()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1, OneWay = true)] int Bad();"));

        Diagnostic_AssertIds(result, "DRPCGEN006");
    }

    [Fact]
    public void Generated_hub_compiles_with_user_implementations()
    {
        // 여긴 MessageProtocol 생성기를 같이 돌리지 않으니 메시지 타입은 쓰지 않는다.
        // (DTO 왕복은 DRPC.E2E.Tests 의 RUDP 루프백이 실제 생성기와 함께 검증한다.)
        string source = GeneratorHarness.ClientHub(
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 3)] int Add(int value1, int value2);
            [RemoteProcedure(RpcDeliveryMode.Sequenced, 4)] string Greet(string name);
            [RemoteProcedure(RpcDeliveryMode.ReliableUnordered, 5, OneWay = true)] void Note(string text);
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 6)] float[] History(int count, float? last);
            """,
            """
            [RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 0)] int ClientSide(int value);
            """,
            "private partial Task<int> ClientSide_Implementation(int value) => Task.FromResult(value);");

        var result = GeneratorHarness.Run(source);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.CompileErrors());
    }

    [Fact]
    public void Validation_emits_gate_and_partial_declaration()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ServerHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 3, Validation = true)] int Add(int value1, int value2);",
            hubBody: """
                private partial Task<bool> Add_Validate(int value1, int value2) => Task.FromResult(true);
                private partial Task<int> Add_Implementation(int value1, int value2) => Task.FromResult(value1 + value2);
                """));

        // 게이트는 구현 호출 앞에, 미구현 시 컴파일 에러(fail-closed)를 내는 partial 선언이 따라온다.
        Assert.Contains("if (!await Add_Validate(value1, value2).ConfigureAwait(false))", result.GeneratedSource);
        Assert.Contains("throw new global::DRPC.Shared.RpcValidationFailedException(\"Add\");", result.GeneratedSource);
        Assert.Contains("private partial global::System.Threading.Tasks.Task<bool> Add_Validate(global::System.Int32 value1, global::System.Int32 value2);", result.GeneratedSource);
        Assert.Empty(result.CompileErrors());
    }

    [Fact]
    public void Validation_without_user_validate_partial_fails_to_compile()
    {
        // 구현부가 없으면 partial 선언이 컴파일에서 사라져 호출 지점이 컴파일 에러로 실패한다(fail-closed).
        var result = GeneratorHarness.Run(GeneratorHarness.ServerHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 3, Validation = true)] int Add(int value1, int value2);",
            hubBody: "private partial Task<int> Add_Implementation(int value1, int value2) => Task.FromResult(value1 + value2);"));

        Assert.NotEmpty(result.CompileErrors());
    }

    [Fact]
    public void Validation_absent_emits_no_validate()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ServerHub(AddContract,
            hubBody: "private partial Task<int> Add_Implementation(int value1, int value2) => Task.FromResult(value1 + value2);"));

        Assert.DoesNotContain("_Validate", result.GeneratedSource);
        Assert.Empty(result.CompileErrors());
    }

    static void Diagnostic_AssertIds(GeneratorHarness.GeneratorResult result, string id)
    {
        Assert.Contains(id, result.Diagnostics.Select(static d => d.Id));
        // 진단으로 중단된 허브는 스텁을 남기지 않는다.
        Assert.DoesNotContain("MethodCallActions.Add", result.GeneratedSource);
    }
    [Fact]
    public void Per_call_timeout_is_emitted_into_request_calls()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 9, TimeoutMs = 1500)] int Slow();"));

        Assert.Contains(
            "RequestRPC(9, __payload, global::DRPC.RpcDeliveryMode.ReliableOrdered, global::System.TimeSpan.FromMilliseconds(1500), cancellationToken)",
            result.GeneratedSource);
        Assert.False(result.HasDiagnostic("DRPCGEN010"));
        Assert.False(result.HasDiagnostic("DRPCGEN011"));
    }

    [Fact]
    public void Omitted_timeout_keeps_call_shape_byte_identical()
    {
        // TimeoutMs 미지정(-1)이면 호출 인수에 아무것도 끼워 넣지 않는다 — 기존 소비자 생성 텍스트 불변.
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(AddContract));

        Assert.Contains("RequestRPC(3, __payload, global::DRPC.RpcDeliveryMode.ReliableOrdered, cancellationToken)",
            result.GeneratedSource);
        Assert.DoesNotContain("TimeSpan.FromMilliseconds", result.GeneratedSource);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public void Invalid_timeout_value_is_rejected(int timeoutMs)
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            $"[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 2, TimeoutMs = {timeoutMs})] int Bad();"));

        Assert.True(result.HasDiagnostic("DRPCGEN010"));
    }

    [Fact]
    public void Timeout_on_one_way_warns_but_still_generates()
    {
        var result = GeneratorHarness.Run(GeneratorHarness.ClientHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 4, OneWay = true, TimeoutMs = 100)] void N();"));

        Assert.True(result.HasDiagnostic("DRPCGEN011"));
        Assert.Contains("await SendRPC(4, __payload, global::DRPC.RpcDeliveryMode.ReliableOrdered)", result.GeneratedSource);
    }

    [Fact]
    public void DRPCGEN003_fires_in_declaration_assembly_without_hub()
    {
        // 허브 없이 계약만 있어도 선언부에서 타입 검증이 돈다 — 계약 어셈블리 단독 빌드도 걸린다.
        var result = GeneratorHarness.Run("""
            using DRPC;
            using DRPC.Shared.Interface;

            public interface ITestServerProcedures : IServerProcedureDeclarations
            {
                [RemoteProcedure(0)]
                int Join(Untagged player);
            }

            public class Untagged { public int Id { get; set; } }
            """);

        Diagnostic_AssertIds(result, "DRPCGEN003");
        // 메서드 선언 위치에 찍힌다 — 위치 없는 베어 CSC 에러가 아니다.
        Assert.True(result.WithId("DRPCGEN003").Single().Location.IsInSource);
        Assert.Contains("Join", result.WithId("DRPCGEN003").Single().GetMessage());
    }

    [Fact]
    public void DRPCGEN003_when_type_implements_message_serializable_without_attribute()
    {
        // 엄격 규칙: IMessageSerializable<T> 구현만으로는 부족 — [Message] 표시 속성이 있어야 한다.
        var result = GeneratorHarness.Run("""
            using DRPC;
            using DRPC.Shared.Interface;
            using MessageProtocol.Serialize;

            public interface ITestServerProcedures : IServerProcedureDeclarations
            {
                [RemoteProcedure(0)]
                void Send(ManualMessage message);
            }

            public class ManualMessage : IMessageSerializable<ManualMessage>
            {
            }
            """);

        Diagnostic_AssertIds(result, "DRPCGEN003");
    }

    [Fact]
    public void validation_failure_emits_skeleton_so_no_CS0759_wall()
    {
        // 검증 실패 시 정의 선언만 담은 스켈레톤이 나와 사용자 partial 구현이 고아가 되지 않는다.
        var result = GeneratorHarness.Run(GeneratorHarness.ServerHub(
            "[RemoteProcedure(RpcDeliveryMode.ReliableOrdered, 1)] int Join(Untagged player);",
            hubBody: """
                private partial global::System.Threading.Tasks.Task<int> Join_Implementation(Untagged player)
                    => global::System.Threading.Tasks.Task.FromResult(0);
                """) + """

            public class Untagged { public int Id { get; set; } }
            """);

        Diagnostic_AssertIds(result, "DRPCGEN003");
        // 스켈레톤: 사용자 구현이 붙을 정의 선언이 존재한다.
        Assert.Contains("Join_Implementation", result.GeneratedSource);
        // 후속 컴파일 에러(CS0759 벽) 없음 — DRPCGEN003 이 유일한 에러다.
        Assert.Empty(result.CompileErrors());
    }
}
