using System.Buffers.Binary;

namespace ServiceLib.Services.FailoverRelay;

public static class Socks5Handshake
{
    public static async Task<Socks5ConnectRequest> ReadClientConnectAsync(Stream stream, CancellationToken cancellationToken)
    {
        var head = await ReadExactAsync(stream, 2, cancellationToken);
        if (head[0] != 0x05)
        {
            throw new InvalidDataException("Unsupported SOCKS version.");
        }

        var methods = await ReadExactAsync(stream, head[1], cancellationToken);
        if (!methods.Contains((byte)0x00))
        {
            await stream.WriteAsync(new byte[] { 0x05, 0xff }, cancellationToken);
            throw new InvalidDataException("SOCKS no-auth method is not supported by client.");
        }

        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken);

        var req = await ReadExactAsync(stream, 4, cancellationToken);
        if (req[0] != 0x05 || req[1] != 0x01)
        {
            throw new InvalidDataException("Only SOCKS5 CONNECT is supported.");
        }

        var host = req[3] switch
        {
            0x01 => new IPAddress(await ReadExactAsync(stream, 4, cancellationToken)).ToString(),
            0x03 => Encoding.ASCII.GetString(await ReadExactAsync(stream, (await ReadExactAsync(stream, 1, cancellationToken))[0], cancellationToken)),
            0x04 => new IPAddress(await ReadExactAsync(stream, 16, cancellationToken)).ToString(),
            _ => throw new InvalidDataException("Unsupported SOCKS address type.")
        };
        var portBytes = await ReadExactAsync(stream, 2, cancellationToken);
        var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
        return new Socks5ConnectRequest(host, port);
    }

    public static async Task<bool> ConnectCandidateAsync(Stream stream, Socks5ConnectRequest request, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken);
        var methodResponse = await ReadExactAsync(stream, 2, cancellationToken);
        if (methodResponse[0] != 0x05 || methodResponse[1] != 0x00)
        {
            return false;
        }

        var hostBytes = Encoding.ASCII.GetBytes(request.Host);
        if (hostBytes.Length is <= 0 or > 255)
        {
            return false;
        }

        var buffer = new byte[7 + hostBytes.Length];
        buffer[0] = 0x05;
        buffer[1] = 0x01;
        buffer[2] = 0x00;
        buffer[3] = 0x03;
        buffer[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(buffer.AsSpan(5));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(5 + hostBytes.Length), (ushort)request.Port);
        await stream.WriteAsync(buffer, cancellationToken);

        var response = await ReadExactAsync(stream, 4, cancellationToken);
        if (response[0] != 0x05 || response[1] != 0x00)
        {
            return false;
        }

        var addressLength = response[3] switch
        {
            0x01 => 4,
            0x03 => (await ReadExactAsync(stream, 1, cancellationToken))[0],
            0x04 => 16,
            _ => 0
        };
        if (addressLength <= 0)
        {
            return false;
        }

        _ = await ReadExactAsync(stream, addressLength + 2, cancellationToken);
        return true;
    }

    public static Task WriteClientSuccessAsync(Stream stream, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0 }, cancellationToken).AsTask();
    }

    public static Task WriteClientFailureAsync(Stream stream, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, 0, 0 }, cancellationToken).AsTask();
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        return buffer;
    }
}
