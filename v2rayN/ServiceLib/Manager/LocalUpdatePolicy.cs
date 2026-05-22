namespace ServiceLib.Manager;

public static class LocalUpdatePolicy
{
    public static bool ShouldDisableAppUpdate => true;

    public static bool ShouldDisableGeoUpdate => true;

    public static bool ShouldKeepSubscriptionUpdate => true;

    public static bool IsCoreUpdateDisabled(ECoreType coreType)
    {
        return coreType is ECoreType.Xray
            or ECoreType.mihomo
            or ECoreType.sing_box;
    }
}
