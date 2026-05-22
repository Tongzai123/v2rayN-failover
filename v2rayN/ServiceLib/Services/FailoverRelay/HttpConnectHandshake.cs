namespace ServiceLib.Services.FailoverRelay;

public static class HttpConnectHandshake
{
    public static async Task<HttpConnectRequest> ReadClientConnectAsync(
        Stream stream,
        int maxHeaderBytes,
        CancellationToken cancellationToken)
    {
        var headerText = await ReadHeaderAsync(stream, maxHeaderBytes, cancellationToken);
        return ParseClientConnect(headerText);
    }

    public static HttpConnectRequest ParseClientConnect(string headerText)
    {
        var firstLine = headerText.Split("\r\n", StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only HTTP CONNECT is supported.");
        }

        var hostPort = parts[1];
        var separator = hostPort.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(hostPort[(separator + 1)..], out var port))
        {
            throw new InvalidDataException("HTTP CONNECT target is invalid.");
        }

        return new HttpConnectRequest(hostPort[..separator], port, headerText);
    }

    public static async Task<bool> ConnectCandidateAsync(
        Stream stream,
        HttpConnectRequest request,
        int maxHeaderBytes,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.RawHeaderText), cancellationToken);
        var response = await ReadHeaderAsync(stream, maxHeaderBytes, cancellationToken);
        var firstLine = response.Split("\r\n", StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && int.TryParse(parts[1], out var statusCode)
            && statusCode is >= 200 and < 300;
    }

    public static Task WriteClientFailureAsync(Stream stream, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(
            Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"),
            cancellationToken).AsTask();
    }

    public static Task WritePlainHttpUnsupportedAsync(Stream stream, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(
            Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n"),
            cancellationToken).AsTask();
    }

    private static async Task<string> ReadHeaderAsync(Stream stream, int maxHeaderBytes, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < maxHeaderBytes)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
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
                return Encoding.ASCII.GetString(buffer.ToArray());
            }
        }

        throw new InvalidDataException("HTTP header is incomplete or too large.");
    }
}
