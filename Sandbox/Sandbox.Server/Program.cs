using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Communication.Network.RUDP;   // RudpTlsOptions — certificate fingerprint output
using DRPC.Shared.Network;
using Sandbox.Contracts;
using Sandbox.Server;

const int Port = 9050;
const string ConnectionKey = "sandbox-key";

// --tls: create a self-signed certificate and encrypt packets with DTLS 1.2. Clients pin-verify the server using the printed fingerprint.
bool tls = args.Contains("--tls");

var serverOptions = new RpcEndpointOptions { ConnectionKey = ConnectionKey };
if (tls)
{
    X509Certificate2 certificate = CreateSelfSignedCertificate();
    serverOptions.ServerCertificate = certificate;
    Console.WriteLine($"[server] DTLS 1.2 ON — certificate fingerprint (SHA-256): {RudpTlsOptions.GetSha256Fingerprint(certificate.Export(X509ContentType.Cert))}");
    Console.WriteLine("[server] Run the client: dotnet run --project Sandbox/Sandbox.Client -- --tls <fingerprint>");
}

await using var handle = await GameServerHub.ListenAsync(Port, serverOptions, async hub =>
{
    Console.WriteLine("[server] client connected — server starts calling back into the client");

    // Outgoing stubs of the client contract (IGameClientProcedures): simply await them.
    float sum = await hub.EchoSumAsync(new List<float> { 1.5f, 2.25f, 4f });
    Console.WriteLine($"[server] EchoSum -> {sum}");

    int count = await hub.CountConfigAsync("arena", new[] { 1, 2, 3 });
    Console.WriteLine($"[server] CountConfig -> {count}");

    // OneWay is sent without waiting for a response.
    await hub.NotifyScoreAsync(new ScoreBoard
    {
        Map = "arena",
        Lines = { new ScoreLine { PlayerId = 7, Score = 42 } },
    });
    Console.WriteLine("[server] NotifyScore(one-way) sent");
});

Console.WriteLine($"[server] RUDP listener running (127.0.0.1:{Port}, key={ConnectionKey}{(tls ? ", DTLS ON" : "")})");
Console.WriteLine("[server] Press ENTER to stop.");
Console.ReadLine();

static X509Certificate2 CreateSelfSignedCertificate()
{
    using RSA rsa = RSA.Create(2048);
    CertificateRequest request = new("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        new OidCollection { new("1.3.6.1.5.5.7.3.1") }, critical: false));
    using X509Certificate2 ephemeral = request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
    // Re-import as PFX so the private key works regardless of the platform key store (same pattern as the tests and Communication).
    return new X509Certificate2(
        ephemeral.Export(X509ContentType.Pfx),
        (string?)null,
        X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
}
