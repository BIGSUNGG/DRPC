using System.Security.Cryptography.X509Certificates;
using Communication.Network.RUDP;

namespace DRPC.Shared.Network;

/// <summary>
/// Bundle of transport options for a connect/listen endpoint — converted into transport-stack options via
/// <see cref="HubSessionFactory.CreateTransportOptions"/>. Unlike the per-call connect timeout and
/// connection cap, these values are shared per endpoint, hence the bundle type.
/// </summary>
public sealed class RpcEndpointOptions
{
    /// <summary>Connection key. null/empty uses the transport stack's default key.</summary>
    public string? ConnectionKey { get; set; }

    /// <summary>
    /// Client-side connect attempt limit (ms). Bounds silent-host (blackhole) connection failures to this
    /// duration. 0 (default) uses the transport stack default (about 5s). Negative values are rejected.
    /// </summary>
    public int ConnectTimeoutMs { get; set; }

    /// <summary>
    /// Server-side cap on concurrently accepted connections. At the cap, connection attempts are rejected
    /// immediately while accepts continue (defends against connection-exhaustion attacks).
    /// 0 (default) means unlimited. Negative values are rejected.
    /// </summary>
    public int MaxConnections { get; set; }

    /// <summary>
    /// Enables packet integrity checking (CRC32c) — every send appends a checksum (4 bytes) and the receiver
    /// discards violating packets before protocol processing
    /// (Communication <c>RudpTransportOptions.Crc32cEnabled</c>). <b>Both ends must use the same setting</b>
    /// (wire-incompatible otherwise). This detects, but does not prevent, tampering (keyless CRC — an active
    /// attacker can recompute it). No confidentiality or authentication. Default <c>false</c>.
    /// </summary>
    public bool EnableCrc32c { get; set; }

    /// <summary>
    /// Server-side DTLS certificate — when set, this endpoint's packets are DTLS 1.2 encrypted
    /// (Communication 2.5.0 <c>RudpTransportOptions.Tls</c>). Used only in the server role.
    /// <b>If any TLS field is set on either end, both ends must run in encrypted mode</b> (wire-incompatible
    /// otherwise — a plaintext end reaches the RUDP connection but no RPC ever completes).
    /// Default <c>null</c> = plaintext (existing behavior preserved).
    /// </summary>
    public X509Certificate2? ServerCertificate { get; set; }

    /// <summary>
    /// Client-side server certificate validation by target hostname (SAN/CN match). Set when connecting to a
    /// server with <see cref="ServerCertificate"/>. Because name-only matching can be passed by a
    /// man-in-the-middle with a self-signed certificate of the same name, it is opt-in since
    /// Communication 2.7.0 — <see cref="TlsAllowNameOnlyCertificateMatch"/> must also be set for it to be
    /// accepted (otherwise the handshake is rejected, fail-closed).
    /// When opted in, expired certificates (NotBefore/NotAfter) are also rejected.
    /// Production deployments should prefer <see cref="TlsCertificateValidation"/> pinning.
    /// </summary>
    public string? TlsTargetHost { get; set; }

    /// <summary>
    /// Opt-in for name-only certificate matching (<see cref="TlsTargetHost"/>) — forwarded to Communication
    /// 2.7.0 <c>RudpTlsOptions.AllowNameOnlyCertificateMatch</c>. Default <c>false</c> (fail-closed, same as
    /// the transport stack). Meaningless when pinning (<see cref="TlsCertificateValidation"/>) is used.
    /// </summary>
    public bool TlsAllowNameOnlyCertificateMatch { get; set; }

    /// <summary>
    /// Client-side server certificate validation — pinning callback (DER bytes → trusted?).
    /// Compare fingerprints via <see cref="RudpTlsOptions.GetSha256Fingerprint(byte[])"/> (the standard game
    /// path). Never install an accept-all callback.
    /// </summary>
    public RudpRemoteCertificateValidation? TlsCertificateValidation { get; set; }

    /// <summary>
    /// Converts to transport-stack options — per-field contracts (negative rejected, 0 = unset) match the
    /// parameterized version of <see cref="HubSessionFactory.CreateTransportOptions"/>.
    /// </summary>
    public RudpTransportOptions ToTransportOptions()
        => HubSessionFactory.CreateTransportOptions(
            ConnectionKey, ConnectTimeoutMs, MaxConnections, EnableCrc32c, TlsOptions);

    /// <summary>Assembles an <see cref="RudpTlsOptions"/> from the set TLS fields. <c>null</c> (plaintext) when nothing is set.</summary>
    private RudpTlsOptions? TlsOptions
        => ServerCertificate is null && TlsTargetHost is null && TlsCertificateValidation is null
            ? null
            : new RudpTlsOptions
            {
                ServerCertificate = ServerCertificate,
                TargetHost = TlsTargetHost,
                RemoteCertificateValidation = TlsCertificateValidation,
                AllowNameOnlyCertificateMatch = TlsAllowNameOnlyCertificateMatch,
            };
}
