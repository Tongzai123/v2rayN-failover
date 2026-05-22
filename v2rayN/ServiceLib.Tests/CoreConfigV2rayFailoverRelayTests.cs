using System.Text.Json.Nodes;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests;

public class CoreConfigV2rayFailoverRelayTests
{
    [Fact]
    public void GenerateClientConfigContent_FailoverRelayRuntimeCreatesLoopbackMixedInboundsAndFixedRules()
    {
        var p1 = FailoverRelayTestHelpers.CreateProxyNode("p1");
        var p2 = FailoverRelayTestHelpers.CreateProxyNode("p2");
        var runtime = new FailoverRelayRuntime
        {
            ListenPort = 31000,
            Candidates =
            [
                new(p1.IndexId, "failover-p1-in", "proxy-1-p1", 31001, "P1"),
                new(p2.IndexId, "failover-p2-in", "proxy-2-p2", 31002, "P2"),
            ],
        };
        var service = new CoreConfigV2rayService(CreateContext(runtime, p1, p2));

        var result = service.GenerateClientConfigContent();

        Assert.True(result.Success, result.Msg);
        var root = GetRoot(result.Data?.ToString());
        var inbounds = root["inbounds"]!.AsArray().Select(x => x!.AsObject()).ToList();
        var rules = root["routing"]!["rules"]!.AsArray().Select(x => x!.AsObject()).ToList();
        var balancers = root["routing"]!["balancers"]?.AsArray();

        Assert.Contains(inbounds, inbound =>
            inbound["tag"]?.GetValue<string>() == "failover-p1-in"
            && inbound["listen"]?.GetValue<string>() == Global.Loopback
            && inbound["protocol"]?.GetValue<string>() == "mixed"
            && inbound["port"]?.GetValue<int>() == 31001);
        Assert.Contains(inbounds, inbound =>
            inbound["tag"]?.GetValue<string>() == "failover-p2-in"
            && inbound["listen"]?.GetValue<string>() == Global.Loopback
            && inbound["protocol"]?.GetValue<string>() == "mixed"
            && inbound["port"]?.GetValue<int>() == 31002);
        Assert.Contains(rules, rule =>
            rule["inboundTag"]?.AsArray().Any(x => x?.GetValue<string>() == "failover-p1-in") == true
            && rule["outboundTag"]?.GetValue<string>() == "proxy-1-p1");
        Assert.Contains(rules, rule =>
            rule["inboundTag"]?.AsArray().Any(x => x?.GetValue<string>() == "failover-p2-in") == true
            && rule["outboundTag"]?.GetValue<string>() == "proxy-2-p2");
        var directPrivateRuleIndex = rules.FindIndex(rule =>
            rule["outboundTag"]?.GetValue<string>() == Global.DirectTag
            && rule["ip"]?.AsArray().Any(x => x?.GetValue<string>() == "geoip:private") == true);
        var firstCandidateRuleIndex = rules.FindIndex(rule =>
            rule["inboundTag"]?.AsArray().Any(x => x?.GetValue<string>() == "failover-p1-in") == true
            && rule["outboundTag"]?.GetValue<string>() == "proxy-1-p1");
        Assert.True(directPrivateRuleIndex >= 0, "Failover relay config must keep private/loopback addresses direct.");
        Assert.True(directPrivateRuleIndex < firstCandidateRuleIndex, "Private direct rule must run before candidate fixed routing rules.");
        Assert.True(balancers is null || balancers.Count == 0);
    }

    private static CoreConfigContext CreateContext(FailoverRelayRuntime runtime, params ProfileItem[] profiles)
    {
        var config = CreateConfig();
        SetAppManagerConfig(config);

        return new CoreConfigContext
        {
            Node = profiles[0],
            RunCoreType = ECoreType.Xray,
            AppConfig = config,
            AllProxiesMap = profiles.ToDictionary(profile => profile.IndexId),
            RoutingItem = new RoutingItem { RuleSet = "[]", DomainStrategy = Global.DomainStrategies.First() },
            SimpleDnsItem = new SimpleDNSItem(),
            FailoverRelayRuntime = runtime,
        };
    }

    private static Config CreateConfig()
    {
        return new Config
        {
            CoreBasicItem = new()
            {
                LogEnabled = false,
                Loglevel = "warning",
                MuxEnabled = false,
                DefAllowInsecure = false,
                DefFingerprint = Global.Fingerprints.First(),
                EnableFragment = false,
            },
            TunModeItem = new(),
            KcpItem = new(),
            GrpcItem = new(),
            RoutingBasicItem = new() { DomainStrategy = Global.DomainStrategies.First() },
            GuiItem = new(),
            MsgUIItem = new(),
            UiItem = new(),
            ConstItem = new(),
            SpeedTestItem = new(),
            Mux4RayItem = new(),
            Mux4SboxItem = new(),
            HysteriaItem = new(),
            ClashUIItem = new(),
            SystemProxyItem = new(),
            WebDavItem = new(),
            CheckUpdateItem = new(),
            Inbound = [],
            GlobalHotkeys = [],
            CoreTypeItem = [],
            SimpleDNSItem = new(),
        };
    }

    private static void SetAppManagerConfig(Config config)
    {
        var field = typeof(AppManager).GetField("_config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AppManager._config field was not found");
        field.SetValue(AppManager.Instance, config);
    }

    private static JsonObject GetRoot(string? json)
    {
        return JsonNode.Parse(json ?? throw new InvalidOperationException("Config JSON is missing"))?.AsObject()
            ?? throw new InvalidOperationException("Failed to parse config JSON");
    }
}
