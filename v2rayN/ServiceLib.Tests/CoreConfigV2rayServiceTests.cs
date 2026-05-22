using System.Text.Json.Nodes;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests;

public class CoreConfigV2rayServiceTests
{
    private const string SendThrough = "198.51.100.10";

    [Fact]
    public void GenerateClientConfigContent_OnlyAppliesSendThroughToRemoteProxyOutbounds()
    {
        var node = CreateProxyNode("proxy-1", "198.51.100.1", 443);
        var service = new CoreConfigV2rayService(CreateContext(node));

        var result = service.GenerateClientConfigContent();

        Assert.True(result.Success, result.Msg);

        var outbounds = GetOutbounds(result.Data?.ToString());
        var proxyOutbound = outbounds.Single(outbound => outbound["tag"]!.GetValue<string>() == Global.ProxyTag);
        var directOutbound = outbounds.Single(outbound => outbound["tag"]!.GetValue<string>() == Global.DirectTag);
        var blockOutbound = outbounds.Single(outbound => outbound["tag"]!.GetValue<string>() == Global.BlockTag);

        Assert.Equal(SendThrough, proxyOutbound["sendThrough"]?.GetValue<string>());
        Assert.Null(directOutbound["sendThrough"]);
        Assert.Null(blockOutbound["sendThrough"]);
    }

    [Fact]
    public void GenerateClientConfigContent_OnlyAppliesSendThroughToChainExitOutbounds()
    {
        var exitNode = CreateProxyNode("exit", "198.51.100.2", 443);
        var entryNode = CreateProxyNode("entry", "198.51.100.3", 443);
        var chainNode = CreateChainNode("chain", exitNode, entryNode);

        var service = new CoreConfigV2rayService(CreateContext(
            chainNode,
            allProxiesMap: new Dictionary<string, ProfileItem>
            {
                [exitNode.IndexId] = exitNode,
                [entryNode.IndexId] = entryNode,
            }));

        var result = service.GenerateClientConfigContent();

        Assert.True(result.Success, result.Msg);

        var outbounds = GetOutbounds(result.Data?.ToString())
            .Where(outbound => outbound["protocol"]?.GetValue<string>() is not ("freedom" or "blackhole" or "dns"))
            .ToList();

        var sendThroughOutbounds = outbounds
            .Where(outbound => outbound["sendThrough"]?.GetValue<string>() == SendThrough)
            .ToList();
        var chainedOutbounds = outbounds
            .Where(outbound => outbound["streamSettings"]?["sockopt"]?["dialerProxy"] is not null)
            .ToList();

        Assert.Single(sendThroughOutbounds);
        Assert.All(chainedOutbounds, outbound => Assert.Null(outbound["sendThrough"]));
    }

    [Fact]
    public void GenerateClientConfigContent_DoesNotApplySendThroughToTunRelayLoopbackOutbound()
    {
        var node = CreateProxyNode("proxy-1", "198.51.100.4", 443);
        var config = CreateConfig();
        config.TunModeItem.EnableLegacyProtect = false;

        var service = new CoreConfigV2rayService(CreateContext(
            node,
            config,
            isTunEnabled: true,
            tunProtectSsPort: 10811,
            proxyRelaySsPort: 10812));

        var result = service.GenerateClientConfigContent();

        Assert.True(result.Success, result.Msg);

        var outbounds = GetOutbounds(result.Data?.ToString());
        Assert.DoesNotContain(outbounds, outbound => outbound["sendThrough"]?.GetValue<string>() == SendThrough);
    }

    [Fact]
    public void GenerateClientConfigContent_FallbackPolicyGroupUsesPriorityAwareBalancer()
    {
        var p1 = CreateProxyNode("p1", "198.51.100.11", 443);
        var p2 = CreateProxyNode("p2", "198.51.100.12", 443);
        var group = CreatePolicyGroupNode("fallback-group", EMultipleLoad.Fallback, p1, p2);

        var service = new CoreConfigV2rayService(CreateContext(
            group,
            allProxiesMap: new Dictionary<string, ProfileItem>
            {
                [p1.IndexId] = p1,
                [p2.IndexId] = p2,
            }));

        var result = service.GenerateClientConfigContent();

        Assert.True(result.Success, result.Msg);

        var root = GetRoot(result.Data?.ToString());
        var balancer = root["routing"]?["balancers"]?.AsArray()
            .Select(node => node!.AsObject())
            .Single(item => item["tag"]!.GetValue<string>() == Global.ProxyTag + Global.BalancerTagSuffix);
        var strategy = balancer?["strategy"]?.AsObject();
        var settings = strategy?["settings"]?.AsObject();
        var costs = settings?["costs"]?.AsArray().Select(node => node!.AsObject()).ToList();

        Assert.Equal("leastLoad", strategy?["type"]?.GetValue<string>());
        Assert.Equal("proxy-1-p1", balancer?["fallbackTag"]?.GetValue<string>());
        Assert.Equal("proxy-1-p1", costs?[0]["match"]?.GetValue<string>());
        Assert.Equal("proxy-2-p2", costs?[1]["match"]?.GetValue<string>());
        Assert.True(costs?[0]["value"]?.GetValue<double>() < costs?[1]["value"]?.GetValue<double>());
    }

    [Fact]
    public void GenerateClientConfigContent_FallbackPolicyGroupRoutesProxyRulesThroughBalancer()
    {
        var p1 = CreateProxyNode("p1", "198.51.100.21", 443);
        var p2 = CreateProxyNode("p2", "198.51.100.22", 443);
        var group = CreatePolicyGroupNode("fallback-group", EMultipleLoad.Fallback, p1, p2);

        var service = new CoreConfigV2rayService(CreateContext(
            group,
            allProxiesMap: new Dictionary<string, ProfileItem>
            {
                [p1.IndexId] = p1,
                [p2.IndexId] = p2,
            },
            routingItem: CreateRoutingItem(new RulesItem
            {
                Id = "google",
                RuleType = ERuleType.Routing,
                OutboundTag = Global.ProxyTag,
                Domain = ["geosite:google"],
                Enabled = true,
            })));

        var result = service.GenerateClientConfigContent();

        Assert.True(result.Success, result.Msg);

        var root = GetRoot(result.Data?.ToString());
        var balancerTag = Global.ProxyTag + Global.BalancerTagSuffix;
        var rules = root["routing"]?["rules"]?.AsArray()
            .Select(node => node!.AsObject())
            .ToList() ?? [];

        Assert.Contains(rules, rule =>
            rule["domain"]?.AsArray().Any(domain => domain?.GetValue<string>() == "geosite:google") == true
            && rule["balancerTag"]?.GetValue<string>() == balancerTag
            && rule["outboundTag"] is null);

        Assert.Contains(rules, rule =>
            rule["network"]?.GetValue<string>() == "tcp,udp"
            && rule["balancerTag"]?.GetValue<string>() == balancerTag
            && rule["outboundTag"] is null);
    }

    [Fact]
    public void GenerateClientConfigContent_FallbackPolicyGroupObservesAllFallbackCandidates()
    {
        var p1 = CreateProxyNode("p1", "198.51.100.31", 443);
        var p2 = CreateProxyNode("p2", "198.51.100.32", 443);
        var group = CreatePolicyGroupNode("fallback-group", EMultipleLoad.Fallback, p1, p2);

        var service = new CoreConfigV2rayService(CreateContext(
            group,
            allProxiesMap: new Dictionary<string, ProfileItem>
            {
                [p1.IndexId] = p1,
                [p2.IndexId] = p2,
            }));

        var result = service.GenerateClientConfigContent();

        Assert.True(result.Success, result.Msg);

        var root = GetRoot(result.Data?.ToString());
        var outbounds = root["outbounds"]?.AsArray()
            .Select(node => node!.AsObject())
            .ToList() ?? [];
        var outboundTags = outbounds
            .Select(outbound => outbound["tag"]?.GetValue<string>())
            .Where(tag => tag is not null)
            .ToList();
        var subjectSelectors = root["burstObservatory"]?["subjectSelector"]?.AsArray()
            .Select(node => node?.GetValue<string>())
            .Where(selector => selector is not null)
            .ToList() ?? [];

        Assert.Contains("proxy-1-p1", outboundTags);
        Assert.Contains("proxy-2-p2", outboundTags);
        Assert.Contains(Global.ProxyTag, subjectSelectors);
    }

    private static CoreConfigContext CreateContext(
        ProfileItem node,
        Config? config = null,
        Dictionary<string, ProfileItem>? allProxiesMap = null,
        RoutingItem? routingItem = null,
        bool isTunEnabled = false,
        int tunProtectSsPort = 0,
        int proxyRelaySsPort = 0)
    {
        var appConfig = config ?? CreateConfig();
        SetAppManagerConfig(appConfig);

        return new CoreConfigContext
        {
            Node = node,
            RunCoreType = ECoreType.Xray,
            AppConfig = appConfig,
            AllProxiesMap = allProxiesMap ?? new(),
            RoutingItem = routingItem,
            SimpleDnsItem = new SimpleDNSItem(),
            IsTunEnabled = isTunEnabled,
            TunProtectSocksPort = tunProtectSsPort,
            ProxyRelaySocksPort = proxyRelaySsPort,
        };
    }

    private static RoutingItem CreateRoutingItem(params RulesItem[] rules)
    {
        return new RoutingItem
        {
            Id = "routing",
            Remarks = "routing",
            RuleSet = JsonUtils.Serialize(rules),
            DomainStrategy = Global.DomainStrategies.First(),
            DomainStrategy4Singbox = Global.DomainStrategies4Sbox.First(),
        };
    }

    private static Config CreateConfig()
    {
        return new Config
        {
            IndexId = string.Empty,
            SubIndexId = string.Empty,
            CoreBasicItem = new()
            {
                LogEnabled = false,
                Loglevel = "warning",
                MuxEnabled = false,
                DefAllowInsecure = false,
                DefFingerprint = Global.Fingerprints.First(),
                DefUserAgent = string.Empty,
                SendThrough = SendThrough,
                EnableFragment = false,
                EnableCacheFile4Sbox = true,
            },
            TunModeItem = new()
            {
                EnableTun = false,
                AutoRoute = true,
                StrictRoute = true,
                Stack = string.Empty,
                Mtu = 9000,
                EnableIPv6Address = false,
                IcmpRouting = Global.TunIcmpRoutingPolicies.First(),
                EnableLegacyProtect = false,
            },
            KcpItem = new(),
            GrpcItem = new(),
            RoutingBasicItem = new()
            {
                DomainStrategy = Global.DomainStrategies.First(),
                DomainStrategy4Singbox = Global.DomainStrategies4Sbox.First(),
                RoutingIndexId = string.Empty,
            },
            GuiItem = new(),
            MsgUIItem = new(),
            UiItem = new()
            {
                CurrentLanguage = "en",
                CurrentFontFamily = string.Empty,
                MainColumnItem = [],
                WindowSizeItem = [],
            },
            ConstItem = new(),
            SpeedTestItem = new(),
            Mux4RayItem = new()
            {
                Concurrency = 8,
                XudpConcurrency = 8,
                XudpProxyUDP443 = "reject",
            },
            Mux4SboxItem = new()
            {
                Protocol = string.Empty,
            },
            HysteriaItem = new(),
            ClashUIItem = new()
            {
                ConnectionsColumnItem = [],
            },
            SystemProxyItem = new(),
            WebDavItem = new(),
            CheckUpdateItem = new(),
            Fragment4RayItem = null,
            Inbound = [new InItem
            {
                Protocol = EInboundProtocol.socks.ToString(),
                LocalPort = 10808,
                UdpEnabled = true,
                SniffingEnabled = true,
                RouteOnly = false,
            }],
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

    private static ProfileItem CreateProxyNode(string indexId, string address, int port)
    {
        return new ProfileItem
        {
            IndexId = indexId,
            Remarks = indexId,
            ConfigType = EConfigType.SOCKS,
            CoreType = ECoreType.Xray,
            Address = address,
            Port = port,
        };
    }

    private static ProfileItem CreateChainNode(string indexId, params ProfileItem[] nodes)
    {
        var chainNode = new ProfileItem
        {
            IndexId = indexId,
            Remarks = indexId,
            ConfigType = EConfigType.ProxyChain,
            CoreType = ECoreType.Xray,
        };
        chainNode.SetProtocolExtra(new ProtocolExtraItem
        {
            ChildItems = string.Join(',', nodes.Select(node => node.IndexId)),
        });
        return chainNode;
    }

    private static ProfileItem CreatePolicyGroupNode(string indexId, EMultipleLoad multipleLoad, params ProfileItem[] nodes)
    {
        var groupNode = new ProfileItem
        {
            IndexId = indexId,
            Remarks = indexId,
            ConfigType = EConfigType.PolicyGroup,
            CoreType = ECoreType.Xray,
        };
        groupNode.SetProtocolExtra(new ProtocolExtraItem
        {
            ChildItems = string.Join(',', nodes.Select(node => node.IndexId)),
            MultipleLoad = multipleLoad,
        });
        return groupNode;
    }

    private static JsonObject GetRoot(string? json)
    {
        return JsonNode.Parse(json ?? throw new InvalidOperationException("Config JSON is missing"))?.AsObject()
            ?? throw new InvalidOperationException("Failed to parse config JSON");
    }

    private static List<JsonObject> GetOutbounds(string? json)
    {
        var root = GetRoot(json);
        return root["outbounds"]?.AsArray().Select(node => node!.AsObject()).ToList()
            ?? throw new InvalidOperationException("Config JSON does not contain outbounds");
    }
}
