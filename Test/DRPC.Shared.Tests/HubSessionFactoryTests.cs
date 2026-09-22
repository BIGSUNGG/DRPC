using Communication.Network.RUDP;
using Communication.Shared.Messages;
using DRPC.Shared.Message;
using DRPC.Shared.Network;
using Xunit;

namespace DRPC.Shared.Tests;

/// <summary>
/// HubSessionFactory.CreateTransportOptions contract: connection key pass-through and connect-timeout cap mapping (Communication 2.0.1 ConnectTimeout adoption).
/// </summary>
public class HubSessionFactoryTests
{
    [Fact]
    public void Transport_options_default_keeps_key_and_timeout_unset()
    {
        var options = HubSessionFactory.CreateTransportOptions(null);

        Assert.Equal(RudpTransportOptions.DefaultConnectionKey, options.ConnectionKey);
        Assert.Null(options.ConnectTimeout); // keeps the transport-stack default (~5s)
    }

    [Fact]
    public void Transport_options_applies_nonempty_connection_key()
    {
        var options = HubSessionFactory.CreateTransportOptions("game-key");

        Assert.Equal("game-key", options.ConnectionKey);
    }

    [Theory]
    [InlineData(0)]
    public void Transport_options_zero_timeout_keeps_transport_default(int connectTimeoutMs)
    {
        // 0 means "unset" (the default parameter value) — keeps the transport-stack default (~5s).
        var options = HubSessionFactory.CreateTransportOptions(null, connectTimeoutMs);

        Assert.Null(options.ConnectTimeout);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Transport_options_negative_timeout_is_rejected(int connectTimeoutMs)
    {
        // Only a positive cap is meaningful — a negative value violates the contract, so it is rejected rather than silently ignored.
        Assert.Throws<ArgumentOutOfRangeException>("connectTimeoutMs", () =>
        {
            HubSessionFactory.CreateTransportOptions(null, connectTimeoutMs);
        });
    }

    [Fact]
    public void Transport_options_positive_timeout_is_applied()
    {
        var options = HubSessionFactory.CreateTransportOptions("game-key", 1500);

        Assert.Equal("game-key", options.ConnectionKey);
        Assert.Equal(1500, options.ConnectTimeout);
    }

    [Fact]
    public void Transport_options_max_connections_maps_and_defaults()
    {
        // Default and explicit 0 mean unlimited (null) — only a positive value sets a cap.
        Assert.Null(HubSessionFactory.CreateTransportOptions(null).MaxConnections);
        Assert.Null(HubSessionFactory.CreateTransportOptions(null, 0, 0).MaxConnections);
        Assert.Equal(1, HubSessionFactory.CreateTransportOptions(null, 0, 1).MaxConnections);
        Assert.Equal(64, HubSessionFactory.CreateTransportOptions("game-key", 0, 64).MaxConnections);
    }

    [Fact]
    public void Transport_options_negative_max_connections_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>("maxConnections", () =>
        {
            HubSessionFactory.CreateTransportOptions(null, 0, -1);
        });
    }

    [Fact]
    public void Converter_roundtrips_rpc_messages_without_intermediate_copy()
    {
        // Send hot-path contract — byte-exact roundtrips must hold even after removing the intermediate array (single copy).
        IMessageConverter converter = HubSessionFactory.Converter;
        var request = new ProcedureCallRequestMessage(7u, 42, new byte[] { 1, 2, 3 });
        var writer = new System.Buffers.ArrayBufferWriter<byte>();

        converter.Serialize(request, writer);

        var roundtripped = Assert.IsType<ProcedureCallRequestMessage>(converter.Deserialize(writer.WrittenSpan));
        Assert.Equal(7u, roundtripped.CallId);
        Assert.Equal(42, roundtripped.MethodId);
        Assert.Equal(new byte[] { 1, 2, 3 }, roundtripped.ParameterData);
    }

    [Fact]
    public void Converter_rejects_invalid_header_flags_with_InvalidDataException()
    {
        // Trust-boundary contract (MessageProtocol 2.3.7 adoption pin): an illegal frame whose flag nibble raises neither the
        // standalone nor group bits is rejected with a descriptive InvalidDataException. In 2.3.4 the same frame produced an
        // InvalidCastException where no cast ever happened, hiding the cause (illegal flag bits) —
        // downgrading the sibling package makes this test fail (adoption pin).
        IMessageConverter converter = HubSessionFactory.Converter;
        var request = new ProcedureCallRequestMessage(7u, 42, new byte[] { 1, 2, 3 });
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        converter.Serialize(request, writer);

        byte[] corrupt = writer.WrittenSpan.ToArray();
        // Header high nibble = flags (NonId 0x01 alone — neither standalone, group root, nor group element); low nibble = category, kept.
        corrupt[0] = (byte)((corrupt[0] & 0x0F) | (0x01 << 4));

        Assert.Throws<System.IO.InvalidDataException>(() => converter.Deserialize(corrupt));
    }
}
