namespace ServiceLib.Handler;

public static class TunModeBehavior
{
    public static int GetNotifyIconIndex(ESysProxyType sysProxyType, bool isTunEnabled)
    {
        return isTunEnabled ? (int)ESysProxyType.Pac : (int)sysProxyType;
    }

    public static ESysProxyType NormalizeProxyForTun(ESysProxyType sysProxyType, bool isTunEnabled)
    {
        return isTunEnabled && sysProxyType == ESysProxyType.ForcedChange
            ? ESysProxyType.ForcedClear
            : sysProxyType;
    }

    public static bool ShouldDisableTunForSystemProxy(ESysProxyType sysProxyType)
    {
        return sysProxyType == ESysProxyType.ForcedChange;
    }
}
