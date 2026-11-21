using System.Net;
using OpenMagicka.Net;

namespace OpenMagicka.Tests.Net;

public class TcpNetTransportTests
{
    [Fact]
    public void HostAndClient_ConnectExchangeMessagesAndPreserveOrder()
    {
        using var host = TcpNetTransport.Host(port: 0, bindAddress: IPAddress.Loopback);
        using var client = TcpNetTransport.Connect("127.0.0.1", host.Port);

        Assert.Equal(TcpNetTransport.HostPeer, host.LocalPeer);
        Assert.Equal(new PeerId(1), client.LocalPeer);
        Assert.True(WaitUntil(() => host.ConnectedPeers.Count == 1));
        Assert.Equal(client.LocalPeer, Assert.Single(host.ConnectedPeers));
        Assert.Equal(TcpNetTransport.HostPeer, Assert.Single(client.ConnectedPeers));

        for (byte i = 0; i < 16; i++)
        {
            client.Send(TcpNetTransport.HostPeer, DeliveryChannel.ReliableOrdered, [i, (byte)(i + 1)]);
        }

        for (byte i = 0; i < 16; i++)
        {
            Assert.True(TryReceiveWithWait(host, out var msg));
            Assert.Equal(client.LocalPeer, msg.From);
            Assert.Equal(DeliveryChannel.ReliableOrdered, msg.Channel);
            Assert.Equal(new byte[] { i, (byte)(i + 1) }, msg.Payload.ToArray());
        }

        host.Broadcast(DeliveryChannel.Unreliable, [42, 99]);
        Assert.True(TryReceiveWithWait(client, out var reply));
        Assert.Equal(TcpNetTransport.HostPeer, reply.From);
        Assert.Equal(DeliveryChannel.Unreliable, reply.Channel);
        Assert.Equal(new byte[] { 42, 99 }, reply.Payload.ToArray());
    }

    [Fact]
    public void MultipleClients_GetDistinctPeerIds_AndHostSeesDisconnect()
    {
        using var host = TcpNetTransport.Host(port: 0, bindAddress: IPAddress.Loopback);
        using var c1 = TcpNetTransport.Connect("127.0.0.1", host.Port);
        using var c2 = TcpNetTransport.Connect("127.0.0.1", host.Port);

        Assert.Equal(new PeerId(1), c1.LocalPeer);
        Assert.Equal(new PeerId(2), c2.LocalPeer);
        Assert.True(WaitUntil(() => host.ConnectedPeers.Count == 2));

        c1.Dispose();
        Assert.True(WaitUntil(() => host.ConnectedPeers.Count == 1));
        Assert.Equal(new PeerId(2), Assert.Single(host.ConnectedPeers));
    }

    [Theory]
    [InlineData("100.64.0.1", "100.64.0.1", TcpNetTransport.DefaultPort)]
    [InlineData("100.64.0.1:28000", "100.64.0.1", 28000)]
    [InlineData("wizard-host:27016", "wizard-host", 27016)]
    [InlineData("[::1]:27017", "::1", 27017)]
    public void ParseEndpoint_HandlesHostAndOptionalPort(string input, string expectedHost, int expectedPort)
    {
        var (host, port) = TcpNetTransport.ParseEndpoint(input);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    private static bool TryReceiveWithWait(INetTransport transport, out NetMessage message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (transport.TryReceive(out message))
            {
                return true;
            }
            Thread.Sleep(5);
        }
        message = default;
        return false;
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(5);
        }
        return false;
    }
}
