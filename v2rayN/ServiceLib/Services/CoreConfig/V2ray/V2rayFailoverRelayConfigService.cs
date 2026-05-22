namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigV2rayService
{
    private RetResult GenerateClientFailoverRelayConfigContent()
    {
        var ret = new RetResult();
        try
        {
            var runtime = context.FailoverRelayRuntime;
            if (runtime is not { Enabled: true })
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }

            var result = EmbedUtils.GetEmbedText(Global.V2raySampleClient);
            if (result.IsNullOrEmpty())
            {
                ret.Msg = ResUI.FailedGetDefaultConfiguration;
                return ret;
            }

            _coreConfig = JsonUtils.Deserialize<V2rayConfig>(result);
            if (_coreConfig == null)
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }

            GenLog();
            _coreConfig.inbounds.Clear();
            _coreConfig.routing.rules.Clear();
            _coreConfig.routing.balancers?.Clear();
            _coreConfig.observatory = null;
            _coreConfig.burstObservatory = null;
            _coreConfig.outbounds.RemoveAll(outbound => outbound.tag == Global.ProxyTag);

            AddFailoverRelayPrivateDirectRules();

            foreach (var candidate in runtime.Candidates)
            {
                if (!context.AllProxiesMap.TryGetValue(candidate.ProfileId, out var profile))
                {
                    ret.Msg = ResUI.FailedGenDefaultConfiguration;
                    return ret;
                }

                _coreConfig.inbounds.Add(new Inbounds4Ray
                {
                    tag = candidate.InboundTag,
                    listen = Global.Loopback,
                    port = candidate.InboundPort,
                    protocol = EInboundProtocol.mixed.ToString(),
                    settings = new(),
                    sniffing = new(),
                });

                var outbound = new CoreConfigV2rayService(context with { Node = profile }).BuildProxyOutbound(candidate.OutboundTag);
                _coreConfig.outbounds.Insert(0, outbound);
                _coreConfig.routing.rules.Add(new RulesItem4Ray
                {
                    type = "field",
                    inboundTag = [candidate.InboundTag],
                    outboundTag = candidate.OutboundTag,
                });
            }

            GenDns();
            GenStatistic();
            ApplyOutboundSendThrough();

            ret.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
            ret.Success = true;
            ret.Data = ApplyFullConfigTemplate();
            return ret;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }
    }

    private void AddFailoverRelayPrivateDirectRules()
    {
        _coreConfig.routing.rules.Add(new RulesItem4Ray
        {
            type = "field",
            ip = ["geoip:private"],
            outboundTag = Global.DirectTag,
        });
        _coreConfig.routing.rules.Add(new RulesItem4Ray
        {
            type = "field",
            domain = ["domain:localhost", "geosite:private"],
            outboundTag = Global.DirectTag,
        });
    }
}
