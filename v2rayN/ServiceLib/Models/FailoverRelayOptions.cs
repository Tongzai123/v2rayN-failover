namespace ServiceLib.Models;

public sealed record FailoverRelayOptions
{
    public static IReadOnlyList<string> DefaultHealthConfirmationProbeUrls { get; } =
    [
        "https://www.gstatic.com/generate_204",
        "https://connectivitycheck.gstatic.com/generate_204",
        "https://www.google.com/generate_204",
        "https://cp.cloudflare.com/generate_204",
        "http://detectportal.firefox.com/canonical.html",
        "http://www.msftconnecttest.com/connecttest.txt",
        "https://www.wikipedia.org/",
        "https://www.youtube.com/",
    ];

    public static IReadOnlyList<int> DefaultHealthConfirmationSuccessStatusCodes { get; } = [200, 204, 301, 302, 304];
    public const int DefaultFirstByteTimeoutMs = 4000;
    public const int MinFirstByteTimeoutMs = 1500;
    public const int SecondRoundFirstByteTimeoutExtraMs = 1000;

    public int ListenPort { get; init; }
    public TimeSpan CandidateConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan CandidateHandshakeTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan CandidateFirstByteTimeout { get; init; } = TimeSpan.FromMilliseconds(DefaultFirstByteTimeoutMs);
    public TimeSpan CandidateSecondRoundFirstByteTimeout { get; init; } = TimeSpan.FromMilliseconds(DefaultFirstByteTimeoutMs + SecondRoundFirstByteTimeoutExtraMs);
    public TimeSpan HealthConfirmationTimeout { get; init; } = TimeSpan.FromMilliseconds(5000);
    public TimeSpan HealthConfirmationFailureWindowDelay { get; init; } = TimeSpan.FromMilliseconds(1000);
    public TimeSpan HealthSuccessCacheDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HealthRecentSuccessDuration { get; init; } = TimeSpan.FromMinutes(5);
    public int HealthConfirmationFailureThreshold { get; init; } = 2;
    public IReadOnlyList<string> HealthConfirmationProbeUrls { get; init; } = [];
    public IReadOnlyList<int> HealthConfirmationSuccessStatusCodes { get; init; } = DefaultHealthConfirmationSuccessStatusCodes;
    public string SpeedPingTestUrl { get; init; } = string.Empty;
    public int MaxHttpHeaderBytes { get; init; } = 16 * 1024;
    public int MaxPlainHttpReplayBodyBytes { get; init; } = 1024 * 1024;
    public Func<FailoverRelayCandidate, string, TimeSpan, CancellationToken, Task<FailoverRelayHealthProbeResult>>? HealthProbeAsync { get; init; }
    public Func<FailoverRelayCandidate, string, string, string, Task<int>>? CandidateFailureReporterAsync { get; init; }
    public Func<FailoverRelayCandidate, string, string, Task>? CandidateSelectedReporterAsync { get; init; }
    public Func<FailoverRelayCandidate, string, string, string, string?, Task>? CandidateStatusReporterAsync { get; init; }

    public static int NormalizeFirstByteTimeoutMs(int timeoutMs)
    {
        return timeoutMs >= MinFirstByteTimeoutMs
            ? timeoutMs
            : DefaultFirstByteTimeoutMs;
    }
}
