using System.Net;
using System.Net.Sockets;
using System.Text;
using ServiceLib.Enums;
using ServiceLib.Models;

namespace ServiceLib.Tests;

internal sealed class LoopbackStreamPair : IDisposable
{
    private readonly TcpListener _listener;
    private readonly TcpClient _clientTcp;
    private readonly TcpClient _serverTcp;

    private LoopbackStreamPair(TcpListener listener, TcpClient clientTcp, TcpClient serverTcp)
    {
        _listener = listener;
        _clientTcp = clientTcp;
        _serverTcp = serverTcp;
        Client = clientTcp.GetStream();
        Server = serverTcp.GetStream();
    }

    public NetworkStream Client { get; }
    public NetworkStream Server { get; }

    public static LoopbackStreamPair Create()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var client = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync();
        client.Connect(IPAddress.Loopback, port);
        return new LoopbackStreamPair(listener, client, acceptTask.GetAwaiter().GetResult());
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        _clientTcp.Dispose();
        _serverTcp.Dispose();
        _listener.Stop();
    }
}

internal static class FailoverRelayTestHelpers
{
    public static int GetUnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static async Task<(byte ReplyCode, byte[] Raw)> SocksClientConnectRawAsync(
        int relayPort,
        string host,
        int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relayPort);
        await using var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greetingResponse = new byte[2];
        await stream.ReadExactlyAsync(greetingResponse);

        var hostBytes = Encoding.ASCII.GetBytes(host);
        var request = new byte[7 + hostBytes.Length];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x03;
        request[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request.AsSpan(5));
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)(port & 0xff);
        await stream.WriteAsync(request);

        var response = new byte[10];
        await stream.ReadExactlyAsync(response);
        return (response[1], response);
    }

    public static async Task<string> HttpConnectClientRawAsync(int relayPort, string host, int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relayPort);
        await using var stream = client.GetStream();

        var request = Encoding.ASCII.GetBytes($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n");
        await stream.WriteAsync(request);

        return await ReadHttpHeaderAsync(stream);
    }

    public static async Task<TcpListener> StartSocksCandidateAsync(int port, string expectedHost, int expectedPort)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var greeting = new byte[3];
            await stream.ReadExactlyAsync(greeting);
            await stream.WriteAsync(new byte[] { 0x05, 0x00 });

            var head = new byte[5];
            await stream.ReadExactlyAsync(head);
            var hostLength = head[4];
            var hostBytes = new byte[hostLength];
            await stream.ReadExactlyAsync(hostBytes);
            var portBytes = new byte[2];
            await stream.ReadExactlyAsync(portBytes);
            var host = Encoding.ASCII.GetString(hostBytes);
            var portValue = (portBytes[0] << 8) | portBytes[1];
            if (host != expectedHost || portValue != expectedPort)
            {
                await stream.WriteAsync(new byte[] { 0x05, 0x04, 0x00, 0x01, 127, 0, 0, 1, 0, 0 });
                return;
            }

            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0 });
        });
        return listener;
    }

    public static async Task<TcpListener> StartSocksCandidateWithFirstPayloadResponseAsync(
        int port,
        byte[] response,
        bool stallAfterConnect = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await CompleteSocksConnectAsync(stream);
            var payload = new byte[16];
            _ = await stream.ReadAsync(payload);
            if (!stallAfterConnect)
            {
                await stream.WriteAsync(response);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
        });
        return listener;
    }

    public static async Task<TcpListener> StartHttpConnectCandidateAsync(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await ReadHttpHeaderAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
        });
        return listener;
    }

    public static async Task<TcpListener> StartHttpConnectCandidateWithFirstPayloadResponseAsync(
        int port,
        byte[] response,
        bool stallAfterConnect = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await ReadHttpHeaderAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
            var payload = new byte[16];
            _ = await stream.ReadAsync(payload);
            if (!stallAfterConnect)
            {
                await stream.WriteAsync(response);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
        });
        return listener;
    }

    public static ProfileItem CreateProxyNode(string indexId, ECoreType coreType = ECoreType.Xray)
    {
        return new ProfileItem
        {
            IndexId = indexId,
            Remarks = indexId,
            Subid = "source-sub",
            ConfigType = EConfigType.SOCKS,
            CoreType = coreType,
            Address = "198.51.100.30",
            Port = 443,
        };
    }

    public static FailoverGroupItem CreateFailoverItem(string groupId, string sourceProfileId, int sort)
    {
        return new FailoverGroupItem
        {
            Id = ServiceLib.Common.Utils.GetGuid(false),
            GroupId = groupId,
            SourceProfileId = sourceProfileId,
            FailoverProfileId = sourceProfileId,
            Sort = sort,
            Enabled = true,
        };
    }

    private static async Task<string> ReadHttpHeaderAsync(Stream stream)
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
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    private static async Task CompleteSocksConnectAsync(NetworkStream stream)
    {
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting);
        await stream.WriteAsync(new byte[] { 0x05, 0x00 });

        var head = new byte[5];
        await stream.ReadExactlyAsync(head);
        var hostBytes = new byte[head[4]];
        await stream.ReadExactlyAsync(hostBytes);
        var portBytes = new byte[2];
        await stream.ReadExactlyAsync(portBytes);
        await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0 });
    }
}
