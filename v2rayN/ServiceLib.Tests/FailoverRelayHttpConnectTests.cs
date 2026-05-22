using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ServiceLib.Models;
using ServiceLib.Services.FailoverRelay;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverRelayHttpConnectTests
{
    [Fact]
    public async Task ReadClientConnectAsync_ParsesConnectRequest()
    {
        using var pair = LoopbackStreamPair.Create();
        var clientTask = Task.Run(async () =>
        {
            await pair.Client.WriteAsync("CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n"u8.ToArray());
        });

        var request = await HttpConnectHandshake.ReadClientConnectAsync(pair.Server, 16 * 1024, CancellationToken.None);

        await clientTask;
        Assert.Equal("example.test", request.Host);
        Assert.Equal(443, request.Port);
        Assert.StartsWith("CONNECT example.test:443", request.RawHeaderText);
    }

    [Fact]
    public async Task RelayHttpConnect_FirstCandidateClosedFallsBackToSecond()
    {
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p2 = await FailoverRelayTestHelpers.StartHttpConnectCandidateAsync(p2Port);
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", FailoverRelayTestHelpers.GetUnusedLoopbackPort(), "P1"),
            new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", p2Port, "P2"),
        };
        var relay = new FailoverRelayService(new FailoverRelayOptions { ListenPort = 0 }, candidates);
        await relay.StartAsync(CancellationToken.None);

        var header = await FailoverRelayTestHelpers.HttpConnectClientRawAsync(relay.ListenPort, "example.test", 443);

        await relay.StopAsync();
        Assert.StartsWith("HTTP/1.1 200", header);
    }

    [Fact]
    public async Task RelayHttpConnect_FirstCandidateConnectSucceedsButPayloadStallsFallsBackToSecond()
    {
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p1 = await FailoverRelayTestHelpers.StartHttpConnectCandidateWithFirstPayloadResponseAsync(
            p1Port,
            "p1-ok"u8.ToArray(),
            stallAfterConnect: true);
        using var p2 = await FailoverRelayTestHelpers.StartHttpConnectCandidateWithFirstPayloadResponseAsync(
            p2Port,
            "p2-ok"u8.ToArray());
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", p1Port, "P1"),
            new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", p2Port, "P2"),
        };
        var relay = new FailoverRelayService(new FailoverRelayOptions
        {
            ListenPort = 0,
            CandidateFirstByteTimeout = TimeSpan.FromMilliseconds(200),
        }, candidates);
        await relay.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.ListenPort);
        await using var stream = client.GetStream();
        await stream.WriteAsync("CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n"u8.ToArray());
        var header = await ReadHeaderAsync(stream);
        await stream.WriteAsync("client-hello"u8.ToArray());
        var payloadResponse = new byte[5];

        await stream.ReadExactlyAsync(payloadResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        await relay.StopAsync();
        Assert.StartsWith("HTTP/1.1 200", header);
        Assert.Equal("p2-ok"u8.ToArray(), payloadResponse);
    }

    [Fact]
    public async Task RelayHttpConnect_DefaultSequentialMode_WaitsForFirstCandidateBeforeTryingSecond()
    {
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p1 = await FailoverRelayTestHelpers.StartHttpConnectCandidateWithFirstPayloadResponseAsync(
            p1Port,
            "p1-ok"u8.ToArray(),
            stallAfterConnect: true);
        using var p2 = await FailoverRelayTestHelpers.StartHttpConnectCandidateWithFirstPayloadResponseAsync(
            p2Port,
            "p2-ok"u8.ToArray());
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", p1Port, "P1"),
            new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", p2Port, "P2"),
        };
        var relay = new FailoverRelayService(new FailoverRelayOptions
        {
            ListenPort = 0,
            CandidateFirstByteTimeout = TimeSpan.FromMilliseconds(300),
        }, candidates);
        await relay.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.ListenPort);
        await using var stream = client.GetStream();
        await stream.WriteAsync("CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n"u8.ToArray());
        var header = await ReadHeaderAsync(stream);
        var stopwatch = Stopwatch.StartNew();
        await stream.WriteAsync("client-hello"u8.ToArray());
        var payloadResponse = new byte[5];

        await stream.ReadExactlyAsync(payloadResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        await relay.StopAsync();
        Assert.StartsWith("HTTP/1.1 200", header);
        Assert.Equal("p2-ok"u8.ToArray(), payloadResponse);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(250), $"Elapsed: {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task RelayHttpConnect_AllCandidatesClosed_Returns502()
    {
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", FailoverRelayTestHelpers.GetUnusedLoopbackPort(), "P1"),
            new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", FailoverRelayTestHelpers.GetUnusedLoopbackPort(), "P2"),
        };
        var relay = new FailoverRelayService(new FailoverRelayOptions { ListenPort = 0 }, candidates);
        await relay.StartAsync(CancellationToken.None);

        var header = await FailoverRelayTestHelpers.HttpConnectClientRawAsync(relay.ListenPort, "example.test", 80);

        await relay.StopAsync();
        Assert.StartsWith("HTTP/1.1 502", header);
    }

    private static async Task<string> ReadHeaderAsync(Stream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 16 * 1024)
        {
            var read = await stream.ReadAsync(one);
            if (read == 0)
            {
                break;
            }
            buffer.Add(one[0]);
            if (buffer.Count >= 4
                && buffer[^4] == '\r'
                && buffer[^3] == '\n'
                && buffer[^2] == '\r'
                && buffer[^1] == '\n')
            {
                break;
            }
        }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }
}
