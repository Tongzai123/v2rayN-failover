namespace ServiceLib.Models;

public static class FailoverHealthStatus
{
    public const string Unknown = "Unknown";
    public const string Normal = "Normal";
    public const string Failed = "Failed";
    public const string Probing = "Probing";
    public const string Requesting = "Requesting";
    public const string Degraded = "Degraded";
    public const string Fallback = "Fallback";
}
