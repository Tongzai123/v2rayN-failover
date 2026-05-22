namespace ServiceLib.Models;

[Serializable]
public class ProfileItemModel : ReactiveObject
{
    public bool IsActive { get; set; }
    public string IndexId { get; set; }
    public EConfigType ConfigType { get; set; }
    public string Remarks { get; set; }
    public string Address { get; set; }
    public int Port { get; set; }
    public string Network { get; set; }
    public string StreamSecurity { get; set; }
    public string Subid { get; set; }
    public string SubRemarks { get; set; }
    public int Sort { get; set; }
    public bool IsFailoverGroupNode { get; set; }
    public bool IsInFailoverQueue { get; set; }
    public int FailoverPriority { get; set; }
    public string FailoverPriorityLabel { get; set; }
    public bool ShowFailoverPriorityLabel { get; set; }
    public bool IsCurrentFailoverPreferred { get; set; }
    public string FailoverHealthStatus { get; set; }
    public string FailoverHealthLabel { get; set; }
    public bool ShowFailoverHealthLabel { get; set; }
    public bool ShowCurrentFailoverActiveLabel { get; set; }
    public bool ShowPendingActiveLabel { get; set; }
    public bool IsFailoverHealthNormal { get; set; }
    public bool IsFailoverHealthFailed { get; set; }
    public bool IsFailoverHealthProbing { get; set; }
    public bool IsFailoverHealthRequesting { get; set; }
    public bool IsFailoverHealthDegraded { get; set; }
    public bool IsFailoverHealthFallback { get; set; }
    public bool IsFailoverHealthUnknown { get; set; }

    [Reactive]
    public int Delay { get; set; }

    public decimal Speed { get; set; }

    [Reactive]
    public string DelayVal { get; set; }

    [Reactive]
    public string SpeedVal { get; set; }

    [Reactive]
    public string TodayUp { get; set; }

    [Reactive]
    public string TodayDown { get; set; }

    [Reactive]
    public string TotalUp { get; set; }

    [Reactive]
    public string TotalDown { get; set; }

    public long TodayDownValue { get; set; }
    public long TodayUpValue { get; set; }
    public long TotalDownValue { get; set; }
    public long TotalUpValue { get; set; }

    public string GetSummary()
    {
        var summary = $"[{ConfigType}] {Remarks}";
        if (!ConfigType.IsComplexType())
        {
            summary += $"({Address}:{Port})";
        }

        return summary;
    }
}
