using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Communication.Network.RUDP;
using Communication.Shared.Channels;
using DRPC.Client.Network;
using DRPC.Server.Network;
using DRPC.Shared;
using DRPC.Shared.Network;
using Xunit;

namespace DRPC.E2E.Tests;

/// <summary>
/// DRPC 옵션 표면(<see cref="RpcEndpointOptions"/>) 위의 DTLS 1.2 암호화 경로 — 핀닝·TargetHost 검증 왕복,
/// 핀 불일치·검증 수단 없음 거부(fail-closed), 평문 클라이언트 와이어 비호환.
/// 인증서 생성은 Communication RudpTlsTests 패턴(PFX 재수입)을 따른다.
/// </summary>
public class RudpTlsLoopbackTests
{
    const string Key = "e2e-key";

    static int NextPort()
    {
        // RudpLoopbackTests 와 동일 — 고정 포트는 예약 범위·잔여 리스너와 충돌해 플레이크를 일으킨다.
        using var probe = new System.Net.Sockets.UdpClient(0);
        return ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    static X509Certificate2 CreateTestCertificate(string commonName = "localhost")
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, critical: false));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(30));
        // 플랫폼 키 저장소와 무관하게 소유 키가 동작하도록 PFX 로 재수입한다.
        return new X509Certificate2(
            ephemeral.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
    }

    static Task<RpcListenHandle> ListenTls(int port, RpcEndpointOptions serverOptions)
        => RpcHost.ListenWithOptionsAsync(port, serverOptions,
            channel => new E2EServerHub(hub => HubSessionFactory.CreateRudpSession(channel, hub)),
            _ => Task.CompletedTask);

    [Fact]
    public async Task Tls_pinned_roundtrip()
    {
        int port = NextPort();
        using X509Certificate2 certificate = CreateTestCertificate();

        var serverOptions = new RpcEndpointOptions { ConnectionKey = Key, ServerCertificate = certificate };
        await using var handle = await ListenTls(port, serverOptions);

        byte[] expectedDer = certificate.Export(X509ContentType.Cert);
        var clientOptions = new RpcEndpointOptions
        {
            ConnectionKey = Key,
            ConnectTimeoutMs = 5000,
            TlsCertificateValidation = der => der.AsSpan().SequenceEqual(expectedDer),
        };
        using var client = await RpcClient.ConnectWithOptionsAsync("127.0.0.1", port, clientOptions,
            channel => new E2EClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub)));

        Assert.Equal(5, await Within(client.AddAsync(2, 3)));
        Assert.Equal("echo:udp", await Within(client.EchoAsync("udp")));
        client.Dispose();
    }

    [Fact]
    public async Task Tls_target_host_roundtrip()
    {
        int port = NextPort();
        using X509Certificate2 certificate = CreateTestCertificate("localhost");

        var serverOptions = new RpcEndpointOptions { ConnectionKey = Key, ServerCertificate = certificate };
        await using var handle = await ListenTls(port, serverOptions);

        var clientOptions = new RpcEndpointOptions
        {
            ConnectionKey = Key,
            ConnectTimeoutMs = 5000,
            TlsTargetHost = "localhost", // 접속 주소(127.0.0.1)가 아니라 인증서 CN/SAN 과 일치하면 된다.
            TlsAllowNameOnlyCertificateMatch = true, // Communication 2.7.0 — 이름 전용 매칭은 옵트인제
        };
        using var client = await RpcClient.ConnectWithOptionsAsync("127.0.0.1", port, clientOptions,
            channel => new E2EClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub)));

        Assert.Equal(5, await Within(client.AddAsync(2, 3)));
        client.Dispose();
    }

    [Fact]
    public async Task Tls_target_host_without_optin_fails_closed()
    {
        int port = NextPort();
        using X509Certificate2 certificate = CreateTestCertificate("localhost");

        var serverOptions = new RpcEndpointOptions { ConnectionKey = Key, ServerCertificate = certificate };
        await using var handle = await ListenTls(port, serverOptions);

        // Communication 2.7.0 — TlsTargetHost 만으로는 이름 전용 매칭이 거부된다(옵트인 미설정, fail-closed).
        var clientOptions = new RpcEndpointOptions
        {
            ConnectionKey = Key,
            ConnectTimeoutMs = 5000,
            TlsTargetHost = "localhost",
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RpcClient.ConnectWithOptionsAsync("127.0.0.1", port, clientOptions,
                channel => new E2EClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub))));
    }

    [Fact]
    public async Task Tls_pin_mismatch_rejects_connection()
    {
        int port = NextPort();
        using X509Certificate2 certificate = CreateTestCertificate();

        var serverOptions = new RpcEndpointOptions { ConnectionKey = Key, ServerCertificate = certificate };
        await using var handle = await ListenTls(port, serverOptions);

        // 핀 불일치 — 핸드셰이크 중 즉시 거부돼 연결 실패로 확정된다(ConnectTimeout 소진이 아니다).
        var clientOptions = new RpcEndpointOptions
        {
            ConnectionKey = Key,
            ConnectTimeoutMs = 5000,
            TlsCertificateValidation = _ => false,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RpcClient.ConnectWithOptionsAsync("127.0.0.1", port, clientOptions,
                channel => new E2EClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub))));
    }

    [Fact]
    public async Task Tls_client_without_validation_means_fails_closed()
    {
        int port = NextPort();
        using X509Certificate2 certificate = CreateTestCertificate();

        var serverOptions = new RpcEndpointOptions { ConnectionKey = Key, ServerCertificate = certificate };
        await using var handle = await ListenTls(port, serverOptions);

        // 서버 옵션을 그대로 클라이언트에 복사하는 전형적 실수 — TLS 는 켜졌지만 검증 수단(TargetHost·핀)이 없으면
        // 서버 인증서는 기본 거부된다(fail-closed).
        var clientOptions = new RpcEndpointOptions
        {
            ConnectionKey = Key,
            ConnectTimeoutMs = 5000,
            ServerCertificate = certificate,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RpcClient.ConnectWithOptionsAsync("127.0.0.1", port, clientOptions,
                channel => new E2EClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub))));
    }

    [Fact]
    public async Task Tls_server_discards_plain_client()
    {
        int port = NextPort();
        using X509Certificate2 certificate = CreateTestCertificate();

        var serverOptions = new RpcEndpointOptions { ConnectionKey = Key, ServerCertificate = certificate };
        await using var handle = await ListenTls(port, serverOptions);

        // 와이어 비호환 — 평문 클라이언트는 RUDP 레벨 연결까진 성공하지만 서버의 DTLS 게이트를 통과하지 못해
        // 어떤 RPC 도 완료되지 않는다.
        var plainOptions = new RpcEndpointOptions { ConnectionKey = Key, ConnectTimeoutMs = 5000 };
        using var plain = await RpcClient.ConnectWithOptionsAsync("127.0.0.1", port, plainOptions,
            channel => new E2EClientHub(hub => HubSessionFactory.CreateRudpSession(channel, hub)));

        await Assert.ThrowsAsync<TimeoutException>(() => Within(plain.AddAsync(2, 3), 2000));
        plain.Dispose();
    }

    static async Task<T> Within<T>(Task<T> task, int timeoutMs = 8000)
    {
        if (await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false) != task)
        {
            throw new TimeoutException($"주어진 시간({timeoutMs}ms) 내에 완료되지 않았습니다.");
        }

        return await task.ConfigureAwait(false);
    }
}
