using System.Text.Json.Nodes;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Resx;
using Xunit;

namespace ServiceLib.Tests;

public class CoreConfigSpeedtestSuppressFailoverTests
{
    [Fact]
    public async Task CoreConfigHandler_BatchSpeedtestSuppressFailoverKeepsRequestedNode()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = CreateProxy($"p1-{suffix}", "198.51.100.30", ECoreType.Xray);
        var p2 = CreateProxy($"p2-{suffix}", "198.51.100.31", ECoreType.sing_box);
        var failoverFileName = Path.Combine(Path.GetTempPath(), $"speedtest-failover-{suffix}.json");
        var suppressedFileName = Path.Combine(Path.GetTempPath(), $"speedtest-suppressed-{suffix}.json");
        var config = CreateConfig(groupId);
        var previousConfig = SetAppManagerConfig(config);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(p1);
            await SQLiteHelper.Instance.ReplaceAsync(p2);
            await FailoverGroupManager.AddToQueue(groupId, [p2.IndexId]);

            var item = new ServerTestItem
            {
                IndexId = p1.IndexId,
                Profile = p1,
                ConfigType = p1.ConfigType,
                CoreType = ECoreType.Xray,
                Address = p1.Address,
                Port = p1.Port,
                AllowTest = true,
            };

            var failoverResult = await CoreConfigHandler.GenerateClientSpeedtestConfig(
                config,
                failoverFileName,
                [item],
                ECoreType.Xray,
                suppressFailover: false);

            Assert.True(failoverResult.Success, failoverResult.Msg);
            Assert.DoesNotContain(ResUI.MsgFailoverGroupCoreTypeMixed, failoverResult.Msg);

            var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(
                config,
                suppressedFileName,
                [item],
                ECoreType.Xray,
                suppressFailover: true);

            Assert.True(result.Success, result.Msg);
            var json = await File.ReadAllTextAsync(suppressedFileName);
            var serverAddresses = GetSocksServerAddresses(json);
            Assert.Contains(p1.Address, serverAddresses);
            Assert.DoesNotContain(p2.Address, serverAddresses);
            Assert.DoesNotContain(FailoverGroupManager.VirtualPolicyGroupPrefix, json);
        }
        finally
        {
            RestoreAppManagerConfig(previousConfig);
            if (File.Exists(failoverFileName))
            {
                File.Delete(failoverFileName);
            }
            if (File.Exists(suppressedFileName))
            {
                File.Delete(suppressedFileName);
            }
            await Cleanup(groupId, p1.IndexId, p2.IndexId);
        }
    }

    private static void PrepareTables()
    {
        SQLiteHelper.Instance.CreateTable<SubItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<FailoverGroupItem>();
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        SQLiteHelper.Instance.CreateTable<DNSItem>();
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
    }

    private static Config CreateConfig(string groupId)
    {
        return new Config
        {
            IndexId = string.Empty,
            SubIndexId = string.Empty,
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.Failover,
            ActiveFailoverGroupId = groupId,
            CoreBasicItem = new()
            {
                LogEnabled = false,
                Loglevel = "warning",
                MuxEnabled = false,
                DefAllowInsecure = false,
                DefFingerprint = Global.Fingerprints.First(),
                DefUserAgent = string.Empty,
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

    private static Config? SetAppManagerConfig(Config config)
    {
        var field = GetAppManagerConfigField();
        var previousConfig = (Config?)field.GetValue(AppManager.Instance);
        field.SetValue(AppManager.Instance, config);
        return previousConfig;
    }

    private static void RestoreAppManagerConfig(Config? config)
    {
        GetAppManagerConfigField().SetValue(AppManager.Instance, config);
    }

    private static System.Reflection.FieldInfo GetAppManagerConfigField()
    {
        return typeof(AppManager).GetField("_config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AppManager._config field was not found");
    }

    private static List<string> GetSocksServerAddresses(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Config JSON is missing");
        var outbounds = root["outbounds"]?.AsArray()
            ?? throw new InvalidOperationException("Config JSON does not contain outbounds");

        return outbounds
            .Select(outbound => outbound?["settings"]?["servers"]?.AsArray())
            .Where(servers => servers is not null)
            .SelectMany(servers => servers!)
            .Select(server => server?["address"]?.GetValue<string>())
            .Where(address => address.IsNotEmpty())
            .ToList()!;
    }

    private static ProfileItem CreateProxy(string indexId, string address, ECoreType coreType)
    {
        return new ProfileItem
        {
            IndexId = indexId,
            Remarks = indexId,
            ConfigType = EConfigType.SOCKS,
            CoreType = coreType,
            Address = address,
            Port = 443,
        };
    }

    private static async Task Cleanup(string groupId, params string[] profileIds)
    {
        await SQLiteHelper.Instance.ExecuteAsync($"delete from FailoverGroupItem where GroupId = '{groupId}'");
        await SQLiteHelper.Instance.ExecuteAsync($"delete from SubItem where Id = '{groupId}'");
        foreach (var profileId in profileIds)
        {
            await SQLiteHelper.Instance.ExecuteAsync($"delete from ProfileItem where IndexId = '{profileId}'");
        }
    }
}
