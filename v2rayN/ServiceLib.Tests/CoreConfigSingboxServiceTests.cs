using System.Text.Json.Nodes;
using ServiceLib;
using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests;

public class CoreConfigSingboxServiceTests
{
    [Fact]
    public void GenerateClientConfigContent_FallbackPolicyGroupBuildsSelectorUrltestInQueueOrder()
    {
        var p1 = CreateProxyNode("p1", "198.51.100.21", 443);
        var p2 = CreateProxyNode("p2", "198.51.100.22", 443);
        var group = CreatePolicyGroupNode("fallback-group", EMultipleLoad.Fallback, p1, p2);

        var service = new CoreConfigSingboxService(CreateContext(
            group,
            allProxiesMap: new Dictionary<string, ProfileItem>
            {
                [p1.IndexId] = p1,
                [p2.IndexId] = p2,
            }));

        var result = service.GenerateClientConfigContent();

        Assert.True(result.Success, result.Msg);

        var outbounds = GetOutbounds(result.Data?.ToString());
        var selector = outbounds.Single(outbound => outbound["tag"]!.GetValue<string>() == Global.ProxyTag);
        var urltest = outbounds.Single(outbound => outbound["tag"]!.GetValue<string>() == $"{Global.ProxyTag}-auto");

        Assert.Equal("selector", selector["type"]?.GetValue<string>());
        Assert.False(selector["interrupt_exist_connections"]?.GetValue<bool>());
        Assert.Equal(["proxy-auto", "proxy-1-p1", "proxy-2-p2"],
            selector["outbounds"]?.AsArray().Select(node => node!.GetValue<string>()).ToArray());

        Assert.Equal("urltest", urltest["type"]?.GetValue<string>());
        Assert.False(urltest["interrupt_exist_connections"]?.GetValue<bool>());
        Assert.Equal(["proxy-1-p1", "proxy-2-p2"],
            urltest["outbounds"]?.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.Equal(5000, urltest["tolerance"]?.GetValue<int>());
    }

    private static CoreConfigContext CreateContext(
        ProfileItem node,
        Dictionary<string, ProfileItem>? allProxiesMap = null)
    {
        var config = CreateConfig();
        SetAppManagerConfig(config);

        return new CoreConfigContext
        {
            Node = node,
            RunCoreType = ECoreType.sing_box,
            AppConfig = config,
            AllProxiesMap = allProxiesMap ?? new(),
            RoutingItem = new()
            {
                Id = "routing",
                Remarks = "routing",
                RuleSet = "[]",
                DomainStrategy = string.Empty,
                DomainStrategy4Singbox = string.Empty,
            },
            SimpleDnsItem = new SimpleDNSItem(),
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
                SendThrough = string.Empty,
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
            Mux4RayItem = new(),
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
            CoreType = ECoreType.sing_box,
            Address = address,
            Port = port,
        };
    }

    private static ProfileItem CreatePolicyGroupNode(string indexId, EMultipleLoad multipleLoad, params ProfileItem[] nodes)
    {
        var groupNode = new ProfileItem
        {
            IndexId = indexId,
            Remarks = indexId,
            ConfigType = EConfigType.PolicyGroup,
            CoreType = ECoreType.sing_box,
        };
        groupNode.SetProtocolExtra(new ProtocolExtraItem
        {
            ChildItems = string.Join(',', nodes.Select(node => node.IndexId)),
            MultipleLoad = multipleLoad,
        });
        return groupNode;
    }

    private static List<JsonObject> GetOutbounds(string? json)
    {
        var root = JsonNode.Parse(json ?? throw new InvalidOperationException("Config JSON is missing"))?.AsObject()
            ?? throw new InvalidOperationException("Failed to parse config JSON");
        return root["outbounds"]?.AsArray().Select(node => node!.AsObject()).ToList()
            ?? throw new InvalidOperationException("Config JSON does not contain outbounds");
    }
}
