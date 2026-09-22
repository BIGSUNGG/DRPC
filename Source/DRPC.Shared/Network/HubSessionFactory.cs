using System.Buffers;
using Communication.Network.RUDP;
using Communication.Shared.Channels;
using Communication.Shared.Messages;
using Communication.Shared.Sessions;
using DRPC.Shared.Interface;
using MessageProtocol.Serialize;

namespace DRPC.Shared.Network;

/// <summary>
/// Factory that gathers the hub/session assembly boilerplate. Generated code calls only these helpers.
/// </summary>
public static class HubSessionFactory
{
    /// <summary>MessageProtocol-based message converter (single instance).</summary>
    public static IMessageConverter Converter { get; } = new MessageProtocolConverter();

    /// <summary>
    /// Creates an RPC session over an RUDP channel. The session owns the channel, so disposing the
    /// session also disposes the channel.
    /// </summary>
    public static ISession CreateRudpSession(IMessageChannel channel, IHubBase hub)
        => CreateRudpSession(channel, hub, null);

    /// <summary>
    /// Queue/dispatch options variant — the app manages session-level policies in one place via
    /// <see cref="Communication.Shared.Messages.MessageQueueOptions"/>: <c>FrameTimeout</c>
    /// (slowloris defense, default 30s — deadline to complete a frame after its first byte arrives),
    /// <c>MaxFrameLength</c> (default 4MB), etc. (sibling proposal P3).
    /// To use custom options, compose them through a custom hub factory
    /// (<c>channel => new GameHub(h => HubSessionFactory.CreateRudpSession(channel, h, opts))</c>).
    /// </summary>
    public static ISession CreateRudpSession(IMessageChannel channel, IHubBase hub,
        Communication.Shared.Messages.MessageQueueOptions? queueOptions)
        => new RudpSession(channel, Converter, session => new DRPCMessageHandler(session, hub), queueOptions);

    /// <summary>
    /// Connection options. When <paramref name="connectionKey"/> is null/empty, the transport stack's
    /// default key is used.
    /// A positive <paramref name="connectTimeoutMs"/> bounds silent-host (blackhole) connection failures
    /// to that duration (Communication 2.0.1 <c>RudpTransportOptions.ConnectTimeout</c>); 0 (default) keeps
    /// the transport stack default, negative values are rejected.
    /// A positive <paramref name="maxConnections"/> caps concurrently accepted connections (at the cap,
    /// new connection attempts are rejected immediately while accepts continue;
    /// Communication 2.0.1 <c>RudpTransportOptions.MaxConnections</c> — server side only);
    /// 0 (default) means unlimited, negative values are rejected.
    /// When <paramref name="tls"/> is set, the channel is delivered only after a DTLS 1.2 handshake
    /// completes (Communication 2.5.0 <c>RudpTransportOptions.Tls</c> — server: <c>ServerCertificate</c>,
    /// client: <c>TargetHost</c>/<c>RemoteCertificateValidation</c> required). null (default) means plaintext.
    /// </summary>
    public static RudpTransportOptions CreateTransportOptions(
        string? connectionKey,
        int connectTimeoutMs = 0,
        int maxConnections = 0,
        bool enableCrc32c = false,
        RudpTlsOptions? tls = null)
    {
        if (connectTimeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeoutMs));
        }

        if (maxConnections < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConnections));
        }

        var options = new RudpTransportOptions();
        if (!string.IsNullOrEmpty(connectionKey))
        {
            options.ConnectionKey = connectionKey;
        }

        if (connectTimeoutMs > 0)
        {
            options.ConnectTimeout = connectTimeoutMs;
        }

        if (maxConnections > 0)
        {
            options.MaxConnections = maxConnections;
        }

        if (enableCrc32c)
        {
            options.Crc32cEnabled = true;
        }

        if (tls is not null)
        {
            options.Tls = tls;
        }

        return options;
    }

    sealed class MessageProtocolConverter : IMessageConverter
    {
        public void Serialize(object message, IBufferWriter<byte> writer)
        {
            var buffer = MessageBufferWriter.Create();
            try
            {
                MessageSerializer.SerializeToWriter(message, ref buffer);
                // Hot path — single copy of WrittenSpan with no intermediate array (IBufferWriter.Write extension).
                writer.Write(buffer.WrittenSpan);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        public object Deserialize(ReadOnlySpan<byte> message) => MessageSerializer.Deserialize(message);
    }
}
