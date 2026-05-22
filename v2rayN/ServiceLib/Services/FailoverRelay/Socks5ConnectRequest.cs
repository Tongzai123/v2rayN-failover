namespace ServiceLib.Services.FailoverRelay;

public sealed record Socks5ConnectRequest(string Host, int Port);
