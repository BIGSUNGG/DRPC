using Communication.Network.RUDP;
using DRPC.Shared.Network;
using Xunit;

namespace DRPC.Shared.Tests;

/// <summary>
/// RpcEndpointOptions.ToTransportOptions contract — connection key, timeout, cap, and CRC32c mapping, plus defaults and negative-value rejection (Communication 2.2.1 adoption).
/// </summary>
public class RpcEndpointOptionsTests
{
    [Fact]
    public void Endpoint_options_map_all_fields_to_transport_options()
    {
        var endpoint = new RpcEndpointOptions
        {
            ConnectionKey = "game-key",
            ConnectTimeoutMs = 1500,
            MaxConnections = 64,
            EnableCrc32c = true,
        };

        RudpTransportOptions options = endpoint.ToTransportOptions();

        Assert.Equal("game-key", options.ConnectionKey);
        Assert.Equal(1500, options.ConnectTimeout);
        Assert.Equal(64, options.MaxConnections);
        Assert.True(options.Crc32cEnabled);
    }

    [Fact]
    public void Endpoint_options_defaults_map_to_transport_defaults()
    {
        RudpTransportOptions options = new RpcEndpointOptions().ToTransportOptions();

        Assert.Equal(RudpTransportOptions.DefaultConnectionKey, options.ConnectionKey);
        Assert.Null(options.ConnectTimeout);
        Assert.Null(options.MaxConnections);
        Assert.False(options.Crc32cEnabled);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void Endpoint_options_negative_values_are_rejected(int connectTimeoutMs, int maxConnections)
    {
        var endpoint = new RpcEndpointOptions { ConnectTimeoutMs = connectTimeoutMs, MaxConnections = maxConnections };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            endpoint.ToTransportOptions();
        });
    }
}
