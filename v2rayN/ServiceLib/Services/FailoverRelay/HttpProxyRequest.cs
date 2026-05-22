namespace ServiceLib.Services.FailoverRelay;

public sealed record HttpProxyRequest(
    string Method,
    string Target,
    string Version,
    string RawHeaderText,
    byte[] Body,
    bool Replayable,
    string Host,
    int Port)
{
    public bool IsConnect => Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);

    public byte[] HeaderBytes => Encoding.ASCII.GetBytes(RawHeaderText);

    public byte[] ToReplayBytes()
    {
        var header = HeaderBytes;
        if (Body.Length == 0)
        {
            return header;
        }

        var bytes = new byte[header.Length + Body.Length];
        Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
        Buffer.BlockCopy(Body, 0, bytes, header.Length, Body.Length);
        return bytes;
    }

    public byte[] ToDirectReplayBytes()
    {
        var header = DirectHeaderBytes;
        if (Body.Length == 0)
        {
            return header;
        }

        var bytes = new byte[header.Length + Body.Length];
        Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
        Buffer.BlockCopy(Body, 0, bytes, header.Length, Body.Length);
        return bytes;
    }

    public byte[] DirectHeaderBytes
    {
        get
        {
            var firstLineEnd = RawHeaderText.IndexOf("\r\n", StringComparison.Ordinal);
            if (firstLineEnd < 0)
            {
                return HeaderBytes;
            }

            var target = Target;
            if (Uri.TryCreate(Target, UriKind.Absolute, out var uri))
            {
                target = uri.PathAndQuery.IsNotEmpty() ? uri.PathAndQuery : "/";
            }

            var headerLines = RawHeaderText[(firstLineEnd + 2)..]
                .Split("\r\n", StringSplitOptions.None);
            var builder = new StringBuilder();
            builder.Append(Method).Append(' ').Append(target).Append(' ').Append(Version).Append("\r\n");
            var wroteConnection = false;
            foreach (var line in headerLines)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    builder.Append(line).Append("\r\n");
                    continue;
                }

                var name = line[..separator].Trim();
                if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Proxy-Trailers", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Proxy-Require", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                {
                    if (!wroteConnection)
                    {
                        builder.Append("Connection: close\r\n");
                        wroteConnection = true;
                    }
                    continue;
                }

                builder.Append(line).Append("\r\n");
            }

            if (!wroteConnection)
            {
                builder.Append("Connection: close\r\n");
            }
            builder.Append("\r\n");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }
    }
}
