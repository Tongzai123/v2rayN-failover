namespace ServiceLib.Services.FailoverRelay;

public static class HttpProxyRequestReader
{
    public static async Task<HttpProxyRequest> ReadAsync(
        Stream stream,
        int maxHeaderBytes,
        int maxReplayBodyBytes,
        CancellationToken cancellationToken)
    {
        var headerText = await ReadHeaderAsync(stream, maxHeaderBytes, cancellationToken);
        var firstLine = headerText.Split("\r\n", StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            throw new InvalidDataException("HTTP request line is invalid.");
        }

        var method = parts[0];
        var target = parts[1];
        var version = parts[2];
        var headers = ParseHeaders(headerText);
        var (host, port) = ParseTarget(method, target, headers);
        var replayable = true;
        byte[] body = [];

        if (!method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding)
                && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                replayable = false;
            }
            else if (headers.TryGetValue("Content-Length", out var contentLengthText)
                && long.TryParse(contentLengthText, out var contentLength))
            {
                if (contentLength < 0)
                {
                    throw new InvalidDataException("HTTP Content-Length is invalid.");
                }

                if (contentLength <= maxReplayBodyBytes)
                {
                    body = await ReadExactAsync(stream, (int)contentLength, cancellationToken);
                }
                else
                {
                    replayable = false;
                }
            }
        }

        return new HttpProxyRequest(method, target, version, headerText, body, replayable, host, port);
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

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        if (count > 0)
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
        }
        return buffer;
    }

    private static Dictionary<string, string> ParseHeaders(string headerText)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.None).Skip(1))
        {
            if (line.Length == 0)
            {
                break;
            }

            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
        return headers;
    }

    private static (string Host, int Port) ParseTarget(
        string method,
        string target,
        IReadOnlyDictionary<string, string> headers)
    {
        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            return ParseHostPort(target, defaultPort: 443);
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Host.IsNotEmpty())
        {
            return (uri.Host, uri.Port > 0 ? uri.Port : (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80));
        }

        if (headers.TryGetValue("Host", out var hostHeader) && hostHeader.IsNotEmpty())
        {
            return ParseHostPort(hostHeader, defaultPort: 80);
        }

        throw new InvalidDataException("HTTP target host is invalid.");
    }

    private static (string Host, int Port) ParseHostPort(string value, int defaultPort)
    {
        value = value.Trim();
        var separator = value.LastIndexOf(':');
        if (separator > 0 && int.TryParse(value[(separator + 1)..], out var port))
        {
            return (value[..separator].Trim('[', ']'), port);
        }
        return (value.Trim('[', ']'), defaultPort);
    }
}
