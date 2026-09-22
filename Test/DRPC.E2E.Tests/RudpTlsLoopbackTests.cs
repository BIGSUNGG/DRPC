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
/// The DTLS 1.2 encryption path over the DRPC options surface (<see cref="RpcEndpointOptions"/>): pinning and TargetHost verification roundtrips,
/// rejection of pin mismatch and missing validation means (fail-closed), and plaintext-client wire incompatibility.
/// Certificate creation follows the Communication RudpTlsTests pattern (PFX re-import).
/// </summary>
public class RudpTlsLoopbackTests
{
    const string Key = "e2e-key";

    static int NextPort()
    {
        // Same as RudpLoopbackTests — fixed ports collide with reserved ranges and leftover listeners, causing flakes (ephemeral ports).
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
        // Re-import as PFX so the private key works regardless of the platform key store.
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
            TlsTargetHost = "localhost", // must match the certificate CN/SAN, not the connect address (127.0.0.1).
            TlsAllowNameOnlyCertificateMatch = true, // Communication 2.7.0 — name-only matching is opt-in
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

        // Communication 2.7.0 — TlsTargetHost alone is rejected for name-only matching (no opt-in, fail-closed).
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

        // Pin mismatch — rejected immediately during the handshake, surfacing as a connection failure (not ConnectTimeout exhaustion).
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

        // The classic mistake of copying the server options to the client — with TLS on but no validation means (TargetHost or pin),
        // the server certificate is rejected by default (fail-closed).
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

        // Wire incompatibility — the plaintext client gets as far as the RUDP-level connection but never passes the server's DTLS gate,
        // so no RPC ever completes.
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
            throw new TimeoutException($"The task did not complete within {timeoutMs}ms.");
        }

        return await task.ConfigureAwait(false);
    }
}
