using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler.Builder;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverRelayRuntimeGateTests
{
    [Fact]
    public async Task Build_XrayFailoverNonTun_CreatesRelayRuntime()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var current = FailoverRelayTestHelpers.CreateProxyNode($"current-{suffix}");
        var p1 = FailoverRelayTestHelpers.CreateProxyNode($"p1-{suffix}");
        var p2 = FailoverRelayTestHelpers.CreateProxyNode($"p2-{suffix}");
        var config = CreateFailoverConfig(groupId, current.IndexId, enableTun: false, EFailoverMode.Failover);

        try
        {
            await InsertFailoverQueueAsync(groupId, p1, p2);

            var result = await CoreConfigContextBuilder.Build(config, current);

            Assert.True(result.Success, string.Join("\n", result.ValidatorResult.Errors));
            Assert.NotNull(result.Context.FailoverRelayRuntime);
            Assert.Equal(2, result.Context.FailoverRelayRuntime!.Candidates.Count);
            Assert.Equal(p1.IndexId, result.Context.FailoverRelayRuntime.Candidates[0].ProfileId);
            Assert.Equal($"{p1.IndexId},{p2.IndexId}", result.Context.FailoverRuntimeQueueSignature);
        }
        finally
        {
            await Cleanup(groupId, current.IndexId, p1.IndexId, p2.IndexId);
        }
    }

    [Fact]
    public async Task Build_XrayFailoverNonTun_UsesConfiguredLocalPortForRelayEntry()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var current = FailoverRelayTestHelpers.CreateProxyNode($"current-{suffix}");
        var p1 = FailoverRelayTestHelpers.CreateProxyNode($"p1-{suffix}");
        var p2 = FailoverRelayTestHelpers.CreateProxyNode($"p2-{suffix}");
        var localPort = Utils.GetFreePort(28080);
        var config = CreateFailoverConfig(groupId, current.IndexId, enableTun: false, EFailoverMode.Failover, localPort);

        try
        {
            await InsertFailoverQueueAsync(groupId, p1, p2);

            var result = await CoreConfigContextBuilder.Build(config, current);

            Assert.True(result.Success, string.Join("\n", result.ValidatorResult.Errors));
            Assert.NotNull(result.Context.FailoverRelayRuntime);
            Assert.Equal(localPort, result.Context.FailoverRelayRuntime!.ListenPort);
            Assert.DoesNotContain(
                result.Context.FailoverRelayRuntime.Candidates,
                candidate => candidate.InboundPort == localPort);
        }
        finally
        {
            await Cleanup(groupId, current.IndexId, p1.IndexId, p2.IndexId);
        }
    }

    [Fact]
    public async Task Build_XrayFailoverNonTun_BuildsRelayRuntimeCandidates()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var current = FailoverRelayTestHelpers.CreateProxyNode($"current-{suffix}");
        var p1 = FailoverRelayTestHelpers.CreateProxyNode($"p1-{suffix}");
        var p2 = FailoverRelayTestHelpers.CreateProxyNode($"p2-{suffix}");
        var config = CreateFailoverConfig(groupId, current.IndexId, enableTun: false, EFailoverMode.Failover);

        try
        {
            await InsertFailoverQueueAsync(groupId, p1, p2);

            var result = await CoreConfigContextBuilder.Build(config, current);

            Assert.True(result.Success, string.Join("\n", result.ValidatorResult.Errors));
            Assert.NotNull(result.Context.FailoverRelayRuntime);
            Assert.Equal([p1.IndexId, p2.IndexId], result.Context.FailoverRelayRuntime!.Candidates.Select(candidate => candidate.ProfileId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, current.IndexId, p1.IndexId, p2.IndexId);
        }
    }

    [Fact]
    public async Task Build_XrayFailoverNonTun_CopiesExternalHealthSnapshotIntoRelayCandidates()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var current = FailoverRelayTestHelpers.CreateProxyNode($"current-{suffix}");
        var p1 = FailoverRelayTestHelpers.CreateProxyNode($"p1-{suffix}");
        var p2 = FailoverRelayTestHelpers.CreateProxyNode($"p2-{suffix}");
        var config = CreateFailoverConfig(groupId, current.IndexId, enableTun: false, EFailoverMode.Failover);
        var lastSuccessTime = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();

        try
        {
            await InsertFailoverQueueAsync(groupId, p1, p2);
            var items = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .Where(item => item.GroupId == groupId)
                .ToListAsync();
            var p1Item = items.Single(item => item.SourceProfileId == p1.IndexId);
            p1Item.LastStatus = FailoverHealthStatus.Failed;
            p1Item.LastFailureReason = "probe-timeout";
            var p2Item = items.Single(item => item.SourceProfileId == p2.IndexId);
            p2Item.LastStatus = FailoverHealthStatus.Normal;
            p2Item.LastSuccessTime = lastSuccessTime;
            await SQLiteHelper.Instance.UpdateAllAsync(items);

            var result = await CoreConfigContextBuilder.Build(config, current);

            Assert.True(result.Success, string.Join("\n", result.ValidatorResult.Errors));
            Assert.NotNull(result.Context.FailoverRelayRuntime);
            var candidates = result.Context.FailoverRelayRuntime!.Candidates;
            var p1Candidate = candidates.Single(candidate => candidate.ProfileId == p1.IndexId);
            var p2Candidate = candidates.Single(candidate => candidate.ProfileId == p2.IndexId);
            Assert.Equal(FailoverHealthStatus.Failed, p1Candidate.LastStatus);
            Assert.Equal("probe-timeout", p1Candidate.LastFailureReason);
            Assert.Equal(FailoverHealthStatus.Normal, p2Candidate.LastStatus);
            Assert.Equal(lastSuccessTime, p2Candidate.LastSuccessTime);
            Assert.Equal(2, p2Candidate.Sort);
        }
        finally
        {
            await Cleanup(groupId, current.IndexId, p1.IndexId, p2.IndexId);
        }
    }

    [Fact]
    public async Task Build_XrayFailoverTun_DoesNotCreateRelayRuntime()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var current = FailoverRelayTestHelpers.CreateProxyNode($"current-{suffix}");
        var p1 = FailoverRelayTestHelpers.CreateProxyNode($"p1-{suffix}");
        var p2 = FailoverRelayTestHelpers.CreateProxyNode($"p2-{suffix}");
        var config = CreateFailoverConfig(groupId, current.IndexId, enableTun: true, EFailoverMode.Failover);

        try
        {
            await InsertFailoverQueueAsync(groupId, p1, p2);

            var result = await CoreConfigContextBuilder.Build(config, current);

            Assert.True(result.Success, string.Join("\n", result.ValidatorResult.Errors));
            Assert.Null(result.Context.FailoverRelayRuntime);
        }
        finally
        {
            await Cleanup(groupId, current.IndexId, p1.IndexId, p2.IndexId);
        }
    }

    [Fact]
    public async Task Build_LeastDelay_DoesNotCreateRelayRuntime()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var current = FailoverRelayTestHelpers.CreateProxyNode($"current-{suffix}");
        var p1 = FailoverRelayTestHelpers.CreateProxyNode($"p1-{suffix}");
        var p2 = FailoverRelayTestHelpers.CreateProxyNode($"p2-{suffix}");
        var config = CreateFailoverConfig(groupId, current.IndexId, enableTun: false, EFailoverMode.LeastDelay);

        try
        {
            await InsertFailoverQueueAsync(groupId, p1, p2);

            var result = await CoreConfigContextBuilder.Build(config, current);

            Assert.True(result.Success, string.Join("\n", result.ValidatorResult.Errors));
            Assert.Null(result.Context.FailoverRelayRuntime);
        }
        finally
        {
            await Cleanup(groupId, current.IndexId, p1.IndexId, p2.IndexId);
        }
    }

    private static Config CreateFailoverConfig(string groupId, string currentId, bool enableTun, EFailoverMode mode, int localPort = 10808)
    {
        var config = new Config
        {
            IndexId = currentId,
            FailoverEnabled = true,
            FailoverMode = mode,
            ActiveFailoverGroupId = groupId,
            TunModeItem = new() { EnableTun = enableTun },
            SimpleDNSItem = new(),
            RoutingBasicItem = new() { DomainStrategy = Global.DomainStrategies.First() },
            CoreBasicItem = new(),
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
            Inbound =
            [
                new InItem
                {
                    Protocol = EInboundProtocol.socks.ToString(),
                    LocalPort = localPort,
                    UdpEnabled = true,
                    SniffingEnabled = true,
                },
            ],
            GlobalHotkeys = [],
            CoreTypeItem = [],
        };
        SetAppManagerConfig(config);
        return config;
    }

    private static async Task InsertFailoverQueueAsync(string groupId, params ProfileItem[] profiles)
    {
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem
        {
            Id = groupId,
            Remarks = "failover",
            IsFailoverGroup = true,
        });
        for (var i = 0; i < profiles.Length; i++)
        {
            await SQLiteHelper.Instance.ReplaceAsync(profiles[i]);
            await SQLiteHelper.Instance.ReplaceAsync(FailoverRelayTestHelpers.CreateFailoverItem(groupId, profiles[i].IndexId, i + 1));
        }
        await SQLiteHelper.Instance.ReplaceAsync(new RoutingItem { Id = $"route-{groupId}", Remarks = groupId, RuleSet = "[]", IsActive = true });
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

    private static async Task Cleanup(string groupId, params string[] profileIds)
    {
        await SQLiteHelper.Instance.ExecuteAsync($"delete from FailoverGroupItem where GroupId = '{groupId}'");
        await SQLiteHelper.Instance.ExecuteAsync($"delete from SubItem where Id = '{groupId}'");
        await SQLiteHelper.Instance.ExecuteAsync($"delete from RoutingItem where Remarks = '{groupId}'");
        foreach (var profileId in profileIds)
        {
            await SQLiteHelper.Instance.ExecuteAsync($"delete from ProfileItem where IndexId = '{profileId}'");
        }
    }

    private static void SetAppManagerConfig(Config config)
    {
        var field = typeof(AppManager).GetField("_config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AppManager._config field was not found");
        field.SetValue(AppManager.Instance, config);
    }
}
