namespace ServiceLib.Services.FailoverRelay;

public sealed record HttpConnectRequest(string Host, int Port, string RawHeaderText);
