using System.Net;
using System.Net.Sockets;
using System.Text;
using ServiceLib.Models;
using ServiceLib.Services.FailoverRelay;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverRelayPlainHttpTests
{
    [Fact]
    public async Task RelayPlainHttpGet_FirstCandidateStallsFallsBackToSecond()
    {
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p1 = await StartPlainHttpCandidateAsync(p1Port, responseBody: "p1-ok", stallAfterRequest: true);
        using var p2 = await StartPlainHttpCandidateAsync(p2Port, responseBody: "p2-ok");
        var relay = CreateRelay(p1Port, p2Port);
        await relay.StartAsync(CancellationToken.None);

        var response = await PlainHttpClientRawAsync(
            relay.ListenPort,
            "GET http://example.test/path HTTP/1.1\r\nHost: example.test\r\nConnection: close\r\n\r\n"u8.ToArray());

        await relay.StopAsync();
        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("p2-ok", response);
    }

    [Fact]
    public async Task RelayPlainHttpPost_ReplaysSmallContentLengthBodyToFallbackCandidate()
    {
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p1 = await StartPlainHttpCandidateAsync(p1Port, responseBody: "p1-ok", stallAfterRequest: true);
        using var p2 = await StartPlainHttpCandidateAsync(p2Port, responseBody: "p2-ok");
        var relay = CreateRelay(p1Port, p2Port);
        await relay.StartAsync(CancellationToken.None);

        var response = await PlainHttpClientRawAsync(
            relay.ListenPort,
            "POST http://example.test/token HTTP/1.1\r\nHost: example.test\r\nContent-Length: 11\r\nConnection: close\r\n\r\nhello=world"u8.ToArray());

        await relay.StopAsync();
        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("p2-ok:hello=world", response);
    }

    [Fact]
    public async Task RelayPlainHttpChunkedBodyUsesSingleCandidateWithoutFallbackReplay()
    {
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p1 = await StartPlainHttpCandidateAsync(p1Port, responseBody: "p1-ok", readChunkedBody: true);
        using var p2 = await StartPlainHttpCandidateAsync(p2Port, responseBody: "p2-ok");
        var relay = CreateRelay(p1Port, p2Port);
        await relay.StartAsync(CancellationToken.None);

        var response = await PlainHttpClientRawAsync(
            relay.ListenPort,
            "POST http://example.test/upload HTTP/1.1\r\nHost: example.test\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n5\r\nhello\r\n0\r\n\r\n"u8.ToArray());

        await relay.StopAsync();
        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("p1-ok", response);
        Assert.DoesNotContain("p2-ok", response);
    }

    [Fact]
    public async Task RelayPlainHttpLoopbackTargetBypassesCandidatesAndAllowsSlowLocalResponse()
    {
        var localPort = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var localServer = await StartPlainHttpCandidateAsync(
            localPort,
            responseBody: "local-ok",
            responseDelay: TimeSpan.FromMilliseconds(450));
        var relay = CreateRelay(
            FailoverRelayTestHelpers.GetUnusedLoopbackPort(),
            FailoverRelayTestHelpers.GetUnusedLoopbackPort());
        await relay.StartAsync(CancellationToken.None);

        var response = await PlainHttpClientRawAsync(
            relay.ListenPort,
            Encoding.ASCII.GetBytes($"GET http://127.0.0.1:{localPort}/refresh HTTP/1.1\r\nHost: 127.0.0.1:{localPort}\r\nConnection: close\r\n\r\n"));

        await relay.StopAsync();
        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("local-ok", response);
    }

    [Fact]
    public async Task RelayPlainHttpLoopbackTargetSendsOriginFormAndRemovesProxyOnlyHeaders()
    {
        var localPort = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var localServer = await StartPlainHttpHeaderEchoServerAsync(localPort);
        var relay = CreateRelay(FailoverRelayTestHelpers.GetUnusedLoopbackPort());
        await relay.StartAsync(CancellationToken.None);

        var response = await PlainHttpClientRawAsync(
            relay.ListenPort,
            Encoding.ASCII.GetBytes(
                $"GET http://127.0.0.1:{localPort}/quota?refresh=1 HTTP/1.1\r\nHost: 127.0.0.1:{localPort}\r\nProxy-Connection: keep-alive\r\nConnection: keep-alive\r\n\r\n"));

        await relay.StopAsync();
        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("first-line=GET /quota?refresh=1 HTTP/1.1", response);
        Assert.Contains("connection=close", response);
        Assert.Contains("proxy-connection=<none>", response);
    }

    private static FailoverRelayService CreateRelay(params int[] ports)
    {
        var candidates = ports
            .Select((port, index) => new FailoverRelayCandidate(
                $"p{index + 1}",
                $"failover-p{index + 1}-in",
                $"proxy-{index + 1}",
                port,
                $"P{index + 1}"))
            .ToArray();
        return new FailoverRelayService(new FailoverRelayOptions
        {
            ListenPort = 0,
            CandidateFirstByteTimeout = TimeSpan.FromMilliseconds(250),
        }, candidates);
    }

    private static async Task<string> PlainHttpClientRawAsync(int relayPort, byte[] request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relayPort);
        await using var stream = client.GetStream();
        await stream.WriteAsync(request);

        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private static async Task<TcpListener> StartPlainHttpCandidateAsync(
        int port,
        string responseBody,
        bool stallAfterRequest = false,
        bool readChunkedBody = false,
        TimeSpan? responseDelay = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var request = await ReadPlainHttpRequestAsync(stream, readChunkedBody);
            if (stallAfterRequest)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                return;
            }

            if (responseDelay is { } delay)
            {
                await Task.Delay(delay);
            }

            var body = GetBody(request);
            var response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {responseBody.Length + body.Length + 1}\r\nConnection: close\r\n\r\n{responseBody}:{body}");
            await stream.WriteAsync(response);
        });
        return listener;
    }

    private static async Task<TcpListener> StartPlainHttpHeaderEchoServerAsync(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var request = await ReadPlainHttpRequestAsync(stream, readChunkedBody: false);
            var lines = request.Split("\r\n", StringSplitOptions.None);
            var connection = lines.FirstOrDefault(x => x.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim() ?? "<none>";
            var proxyConnection = lines.FirstOrDefault(x => x.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim() ?? "<none>";
            var body = $"first-line={lines[0]};connection={connection};proxy-connection={proxyConnection}";
            var response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(response);
        });
        return listener;
    }

    private static async Task<string> ReadPlainHttpRequestAsync(NetworkStream stream, bool readChunkedBody)
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
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
            {
                break;
            }
        }

        var header = Encoding.ASCII.GetString(buffer.ToArray());
        var contentLength = header
            .Split("\r\n")
            .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (contentLength is not null
            && int.TryParse(contentLength.Split(':', 2)[1].Trim(), out var length)
            && length > 0)
        {
            var body = new byte[length];
            await stream.ReadExactlyAsync(body);
            buffer.AddRange(body);
        }
        else if (readChunkedBody)
        {
            while (buffer.Count < 16 * 1024)
            {
                var read = await stream.ReadAsync(one);
                if (read == 0)
                {
                    break;
                }
                buffer.Add(one[0]);
                if (buffer.Count >= 5
                    && buffer[^5] == '0'
                    && buffer[^4] == '\r'
                    && buffer[^3] == '\n'
                    && buffer[^2] == '\r'
                    && buffer[^1] == '\n')
                {
                    break;
                }
            }
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    private static string GetBody(string request)
    {
        var separator = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return separator >= 0 ? request[(separator + 4)..] : string.Empty;
    }
}
