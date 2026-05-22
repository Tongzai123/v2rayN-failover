namespace ServiceLib.Models;

[Serializable]
public class FailoverGroupItem
{
    [PrimaryKey]
    public string Id { get; set; } = string.Empty;

    public string GroupId { get; set; } = string.Empty;

    public string SourceProfileId { get; set; } = string.Empty;

    public string? FailoverProfileId { get; set; }

    public int Sort { get; set; }

    public bool Enabled { get; set; }

    public string LastStatus { get; set; } = "Unknown";

    public long? LastFailureTime { get; set; }

    public string? LastFailureReason { get; set; }

    public int FailureCount { get; set; }

    public long? CooldownUntilTime { get; set; }

    public long? LastSuccessTime { get; set; }

    public long? LastProbeTime { get; set; }

    public int LastDelay { get; set; }

    public string? Memo { get; set; }
}
