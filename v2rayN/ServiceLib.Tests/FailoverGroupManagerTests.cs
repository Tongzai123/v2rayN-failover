using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Builder;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverGroupManagerTests
{
    [Fact]
    public async Task CopyToFailoverGroup_DoesNotChangeSourceSubidAndIsIdempotent()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem
            {
                Id = groupId,
                Remarks = "failover",
                IsFailoverGroup = true,
            });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId, "source-sub", ECoreType.Xray));

            var profile = await SQLiteHelper.Instance.TableAsync<ProfileItem>()
                .FirstAsync(item => item.IndexId == profileId);

            Assert.Equal(0, await FailoverGroupManager.CopyToFailoverGroup(groupId, [profile]));
            Assert.Equal(0, await FailoverGroupManager.CopyToFailoverGroup(groupId, [profile]));

            var storedProfile = await SQLiteHelper.Instance.TableAsync<ProfileItem>()
                .FirstAsync(item => item.IndexId == profileId);
            var failoverItems = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .Where(item => item.GroupId == groupId && item.SourceProfileId == profileId)
                .ToListAsync();

            Assert.Equal("source-sub", storedProfile.Subid);
            Assert.Single(failoverItems);
            Assert.False(failoverItems[0].Enabled);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task GetDisplayProfiles_FailoverModeKeepsQueueAndDirectProfiles()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";
        var pasted = $"pasted-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem
            {
                Id = groupId,
                Remarks = "failover",
                IsFailoverGroup = true,
            });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(pasted, groupId, ECoreType.Xray));
            await SQLiteHelper.Instance.InsertAllAsync(new[]
            {
                CreateFailoverItem(groupId, p1, 1),
                CreateFailoverItem(groupId, p2, 2),
            });

            var profiles = await FailoverGroupManager.GetDisplayProfiles(groupId, EFailoverMode.Failover);

            Assert.Equal([p1, p2, pasted], profiles.Select(profile => profile.IndexId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, p1, p2, pasted);
        }
    }

    [Fact]
    public async Task AddSubItem_DisablingActiveFailoverGroupConvertsReferencesToNormalProfiles()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var queued = $"queued-{suffix}";
        var candidate = $"candidate-{suffix}";
        var direct = $"direct-{suffix}";
        var config = new Config
        {
            ActiveFailoverGroupId = groupId,
            FailoverMode = EFailoverMode.Failover,
            FailoverEnabled = true,
            FailoverStartupProfileId = queued,
        };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem
            {
                Id = groupId,
                Remarks = "failover",
                IsFailoverGroup = true,
            });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(queued, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(candidate, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(direct, groupId, ECoreType.Xray));
            var disabled = CreateFailoverItem(groupId, candidate, 2);
            disabled.Enabled = false;
            await SQLiteHelper.Instance.InsertAllAsync(new[]
            {
                CreateFailoverItem(groupId, queued, 1),
                disabled,
            });

            var result = await ConfigHandler.AddSubItem(config, new SubItem
            {
                Id = groupId,
                Remarks = "normal",
                IsFailoverGroup = false,
            });

            var group = await AppManager.Instance.GetSubItem(groupId);
            var failoverItems = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .Where(item => item.GroupId == groupId)
                .ToListAsync();
            var sourceProfiles = await AppManager.Instance.GetProfileItemsOrderedByIndexIds([queued, candidate]);
            var normalProfiles = await SQLiteHelper.Instance.TableAsync<ProfileItem>()
                .Where(profile => profile.Subid == groupId)
                .ToListAsync();

            Assert.Equal(0, result);
            Assert.NotNull(group);
            Assert.False(group.IsFailoverGroup);
            Assert.Empty(failoverItems);
            Assert.Equal(["source-sub", "source-sub"], sourceProfiles.Select(profile => profile.Subid).ToArray());
            Assert.Equal(3, normalProfiles.Count);
            Assert.Contains(normalProfiles, profile => profile.IndexId == direct);
            Assert.Equal(EFailoverMode.Off, config.FailoverMode);
            Assert.False(config.FailoverEnabled);
            Assert.Equal(string.Empty, config.ActiveFailoverGroupId);
            Assert.Null(config.FailoverStartupProfileId);
        }
        finally
        {
            await Cleanup(groupId, queued, candidate, direct);
        }
    }

    [Fact]
    public async Task TryBuildVirtualPolicyGroup_FiltersMissingProfilesAndPreservesSort()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";
        var missing = $"missing-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem
            {
                Id = groupId,
                Remarks = "failover",
                IsFailoverGroup = true,
            });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.InsertAllAsync(new[]
            {
                CreateFailoverItem(groupId, p1, 2),
                CreateFailoverItem(groupId, p2, 1),
                CreateFailoverItem(groupId, missing, 0),
            });

            var config = new Config
            {
                FailoverEnabled = true,
                FailoverMode = EFailoverMode.Failover,
                ActiveFailoverGroupId = groupId,
                TunModeItem = new(),
                SimpleDNSItem = new(),
                RoutingBasicItem = new(),
            };

            var result = await FailoverGroupManager.TryBuildVirtualPolicyGroup(config, CreateProxy("current", "source-sub", ECoreType.Xray));

            Assert.True(result.Success);
            Assert.NotNull(result.Node);
            Assert.Equal([p2, p1], result.ChildProfiles.Select(profile => profile.IndexId).ToArray());
            Assert.Equal(EMultipleLoad.Fallback, result.Node.GetProtocolExtra().MultipleLoad);
            Assert.Equal($"{p2},{p1}", result.Node.GetProtocolExtra().ChildItems);
        }
        finally
        {
            await Cleanup(groupId, p1, p2, missing);
        }
    }

    [Fact]
    public async Task TryResolveRuntimeNode_SameCoreQueueUsesVirtualPolicyGroup()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = CreateProxy($"p1-{suffix}", "source-sub", ECoreType.Xray);
        var p2 = CreateProxy($"p2-{suffix}", "source-sub", ECoreType.Xray);
        var current = CreateProxy($"current-{suffix}", "source-sub", ECoreType.Xray);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem
            {
                Id = groupId,
                Remarks = "failover",
                IsFailoverGroup = true,
            });
            await SQLiteHelper.Instance.ReplaceAsync(p1);
            await SQLiteHelper.Instance.ReplaceAsync(p2);
            await SQLiteHelper.Instance.InsertAllAsync(new[]
            {
                CreateFailoverItem(groupId, p1.IndexId, 1),
                CreateFailoverItem(groupId, p2.IndexId, 2),
            });

            var config = new Config
            {
                IndexId = current.IndexId,
                FailoverEnabled = true,
                FailoverMode = EFailoverMode.Failover,
                ActiveFailoverGroupId = groupId,
                TunModeItem = new(),
                SimpleDNSItem = new(),
                RoutingBasicItem = new(),
            };

            var result = await FailoverGroupManager.TryResolveRuntimeNode(config, current);

            Assert.True(result.Success);
            Assert.Equal(FailoverRuntimeKind.VirtualPolicyGroup, result.Kind);
            Assert.NotNull(result.EffectiveNode);
            Assert.StartsWith(FailoverGroupManager.VirtualPolicyGroupPrefix, result.EffectiveNode.IndexId);
            Assert.Equal(p1.IndexId, result.RuntimeTargetProfileId);
        }
        finally
        {
            await Cleanup(groupId, p1.IndexId, p2.IndexId, current.IndexId);
        }
    }

    [Fact]
    public async Task TryResolveRuntimeNode_MixedCoreQueueUsesDirectProfile()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var xrayNode = CreateProxy($"xray-{suffix}", "source-sub", ECoreType.Xray);
        var singNode = CreateProxy($"sing-{suffix}", "source-sub", ECoreType.sing_box);
        var current = CreateProxy($"current-{suffix}", "source-sub", ECoreType.Xray);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem
            {
                Id = groupId,
                Remarks = "failover",
                IsFailoverGroup = true,
            });
            await SQLiteHelper.Instance.ReplaceAsync(xrayNode);
            await SQLiteHelper.Instance.ReplaceAsync(singNode);
            var first = CreateFailoverItem(groupId, xrayNode.IndexId, 1);
            first.LastStatus = FailoverHealthStatus.Failed;
            var second = CreateFailoverItem(groupId, singNode.IndexId, 2);
            second.LastStatus = FailoverHealthStatus.Normal;
            second.LastDelay = 42;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { first, second });

            var config = new Config
            {
                IndexId = current.IndexId,
                FailoverEnabled = true,
                FailoverMode = EFailoverMode.Failover,
                ActiveFailoverGroupId = groupId,
                TunModeItem = new(),
                SimpleDNSItem = new(),
                RoutingBasicItem = new(),
            };

            var result = await FailoverGroupManager.TryResolveRuntimeNode(config, current);

            Assert.True(result.Success);
            Assert.Equal(FailoverRuntimeKind.DirectProfile, result.Kind);
            Assert.NotNull(result.EffectiveNode);
            Assert.Equal(singNode.IndexId, result.EffectiveNode.IndexId);
            Assert.Equal(singNode.IndexId, result.RuntimeTargetProfileId);
        }
        finally
        {
            await Cleanup(groupId, xrayNode.IndexId, singNode.IndexId, current.IndexId);
        }
    }

    [Fact]
    public async Task GetQueueEntriesForMode_LeastDelayOrdersAllNormalNodesByDelay()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";
        var p3 = $"p3-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p3, "source-sub", ECoreType.Xray));
            var first = CreateFailoverItem(groupId, p1, 1);
            first.LastStatus = FailoverHealthStatus.Normal;
            first.LastDelay = 100;
            var second = CreateFailoverItem(groupId, p2, 2);
            second.LastStatus = FailoverHealthStatus.Normal;
            second.LastDelay = 30;
            var third = CreateFailoverItem(groupId, p3, 3);
            third.LastStatus = FailoverHealthStatus.Normal;
            third.LastDelay = 60;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { first, second, third });

            var entries = await FailoverGroupManager.GetQueueEntriesForMode(groupId, true, EFailoverMode.LeastDelay);

            Assert.Equal([p2, p3, p1], entries.Select(entry => entry.Profile.IndexId).ToArray());

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .Where(item => item.GroupId == groupId)
                .OrderBy(item => item.Sort)
                .ToListAsync();
            Assert.Equal([p1, p2, p3], stored.Select(item => item.SourceProfileId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, p1, p2, p3);
        }
    }

    [Fact]
    public async Task GetQueueEntriesForMode_LeastDelayExcludesFailedWhenUnknownFallbackExists()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            var first = CreateFailoverItem(groupId, p1, 1);
            first.LastStatus = FailoverHealthStatus.Unknown;
            first.LastDelay = 0;
            var second = CreateFailoverItem(groupId, p2, 2);
            second.LastStatus = FailoverHealthStatus.Failed;
            second.LastDelay = 10;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { first, second });

            var entries = await FailoverGroupManager.GetQueueEntriesForMode(groupId, true, EFailoverMode.LeastDelay);

            Assert.Equal([p1], entries.Select(entry => entry.Profile.IndexId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task GetQueueEntriesForMode_LeastDelayIgnoresDisabledLowerDelayEntry()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";
        var p3 = $"p3-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p3, "source-sub", ECoreType.Xray));
            var first = CreateFailoverItem(groupId, p1, 1);
            first.LastStatus = FailoverHealthStatus.Normal;
            first.LastDelay = 80;
            var second = CreateFailoverItem(groupId, p2, 2);
            second.LastStatus = FailoverHealthStatus.Normal;
            second.LastDelay = 20;
            second.Enabled = false;
            var third = CreateFailoverItem(groupId, p3, 3);
            third.LastStatus = FailoverHealthStatus.Normal;
            third.LastDelay = 40;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { first, second, third });

            var entries = await FailoverGroupManager.GetQueueEntriesForMode(groupId, true, EFailoverMode.LeastDelay);

            Assert.Equal([p3, p1], entries.Select(entry => entry.Profile.IndexId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, p1, p2, p3);
        }
    }

    [Fact]
    public async Task TryResolveRuntimeNode_LeastDelaySameCoreUsesVirtualPolicyGroupOrderedByDelay()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var current = CreateProxy($"current-{suffix}", "source-sub", ECoreType.Xray);
        var slow = CreateProxy($"slow-{suffix}", "source-sub", ECoreType.Xray);
        var fast = CreateProxy($"fast-{suffix}", "source-sub", ECoreType.Xray);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(current);
            await SQLiteHelper.Instance.ReplaceAsync(slow);
            await SQLiteHelper.Instance.ReplaceAsync(fast);
            var slowItem = CreateFailoverItem(groupId, slow.IndexId, 1);
            slowItem.LastStatus = FailoverHealthStatus.Normal;
            slowItem.LastDelay = 90;
            var fastItem = CreateFailoverItem(groupId, fast.IndexId, 2);
            fastItem.LastStatus = FailoverHealthStatus.Normal;
            fastItem.LastDelay = 20;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { slowItem, fastItem });

            var config = new Config
            {
                IndexId = current.IndexId,
                FailoverEnabled = true,
                FailoverMode = EFailoverMode.LeastDelay,
                ActiveFailoverGroupId = groupId,
                FailoverStartupProfileId = fast.IndexId,
                TunModeItem = new(),
                SimpleDNSItem = new(),
                RoutingBasicItem = new(),
            };

            var result = await FailoverGroupManager.TryResolveRuntimeNode(config, current);

            Assert.True(result.Success);
            Assert.Equal(FailoverRuntimeKind.VirtualPolicyGroup, result.Kind);
            Assert.NotNull(result.EffectiveNode);
            Assert.StartsWith(FailoverGroupManager.VirtualPolicyGroupPrefix, result.EffectiveNode.IndexId);
            Assert.Equal(fast.IndexId, result.RuntimeTargetProfileId);
            Assert.Equal([fast.IndexId, slow.IndexId], result.QueueProfiles.Select(profile => profile.IndexId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, current.IndexId, slow.IndexId, fast.IndexId);
        }
    }

    [Fact]
    public async Task TryResolveRuntimeNode_LeastDelayStartupProfileKeepsDirectProfileUntilDelayResultExists()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var previous = CreateProxy($"previous-{suffix}", "source-sub", ECoreType.Xray);
        var queued = CreateProxy($"queued-{suffix}", "source-sub", ECoreType.Xray);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(previous);
            await SQLiteHelper.Instance.ReplaceAsync(queued);
            var item = CreateFailoverItem(groupId, queued.IndexId, 1);
            item.LastStatus = FailoverHealthStatus.Unknown;
            item.LastDelay = 0;
            await SQLiteHelper.Instance.ReplaceAsync(item);

            var config = new Config
            {
                IndexId = previous.IndexId,
                FailoverEnabled = true,
                FailoverMode = EFailoverMode.LeastDelay,
                ActiveFailoverGroupId = groupId,
                FailoverStartupProfileId = previous.IndexId,
                TunModeItem = new(),
                SimpleDNSItem = new(),
                RoutingBasicItem = new(),
            };

            var result = await FailoverGroupManager.TryResolveRuntimeNode(config, previous);

            Assert.True(result.Success);
            Assert.Equal(FailoverRuntimeKind.DirectProfile, result.Kind);
            Assert.Equal(previous.IndexId, result.EffectiveNode?.IndexId);
            Assert.Equal(previous.IndexId, result.RuntimeTargetProfileId);
        }
        finally
        {
            await Cleanup(groupId, previous.IndexId, queued.IndexId);
        }
    }

    [Fact]
    public async Task GetQueueEntriesForMode_LeastDelayOrdersNormalNodesAndKeepsUnknownFallbacks()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";
        var p3 = $"p3-{suffix}";
        var p4 = $"p4-{suffix}";
        var p5 = $"p5-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p3, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p4, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p5, "source-sub", ECoreType.Xray));

            var first = CreateFailoverItem(groupId, p1, 1);
            first.LastStatus = FailoverHealthStatus.Normal;
            first.LastDelay = 80;
            var second = CreateFailoverItem(groupId, p2, 2);
            second.LastStatus = FailoverHealthStatus.Normal;
            second.LastDelay = 30;
            var third = CreateFailoverItem(groupId, p3, 3);
            third.LastStatus = FailoverHealthStatus.Unknown;
            third.LastDelay = 0;
            var fourth = CreateFailoverItem(groupId, p4, 4);
            fourth.LastStatus = FailoverHealthStatus.Probing;
            fourth.LastDelay = 0;
            var failed = CreateFailoverItem(groupId, p5, 5);
            failed.LastStatus = FailoverHealthStatus.Failed;
            failed.LastDelay = 10;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { first, second, third, fourth, failed });

            var entries = await FailoverGroupManager.GetQueueEntriesForMode(groupId, true, EFailoverMode.LeastDelay);

            Assert.Equal([p2, p1, p3, p4], entries.Select(entry => entry.Profile.IndexId).ToArray());
            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .Where(item => item.GroupId == groupId)
                .OrderBy(item => item.Sort)
                .ToListAsync();
            Assert.Equal([p1, p2, p3, p4, p5], stored.Select(item => item.SourceProfileId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, p1, p2, p3, p4, p5);
        }
    }

    [Fact]
    public async Task GetQueueEntriesForMode_LeastDelayKeepsFailedEntriesOnlyWhenAllEntriesFailed()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            var first = CreateFailoverItem(groupId, p1, 1);
            first.LastStatus = FailoverHealthStatus.Failed;
            first.LastDelay = 20;
            var second = CreateFailoverItem(groupId, p2, 2);
            second.LastStatus = FailoverHealthStatus.Failed;
            second.LastDelay = 10;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { first, second });

            var entries = await FailoverGroupManager.GetQueueEntriesForMode(groupId, true, EFailoverMode.LeastDelay);

            Assert.Equal([p1, p2], entries.Select(entry => entry.Profile.IndexId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task GetQueueEntriesForMode_LeastDelayKeepsDisabledEntriesWhenEnabledOnlyFalse()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";
        var p3 = $"p3-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p3, "source-sub", ECoreType.Xray));
            var first = CreateFailoverItem(groupId, p1, 1);
            first.LastStatus = FailoverHealthStatus.Normal;
            first.LastDelay = 80;
            var disabled = CreateFailoverItem(groupId, p2, 2);
            disabled.LastStatus = FailoverHealthStatus.Normal;
            disabled.LastDelay = 20;
            disabled.Enabled = false;
            var third = CreateFailoverItem(groupId, p3, 3);
            third.LastStatus = FailoverHealthStatus.Normal;
            third.LastDelay = 40;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { first, disabled, third });

            var entries = await FailoverGroupManager.GetQueueEntriesForMode(groupId, false, EFailoverMode.LeastDelay);

            Assert.Equal([p3, p1, p2], entries.Select(entry => entry.Profile.IndexId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, p1, p2, p3);
        }
    }

    [Fact]
    public async Task GetCandidateProfiles_LeastDelayKeepsFailedEntriesForDisplay()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";
        var p3 = $"p3-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p3, "source-sub", ECoreType.Xray));
            var slow = CreateFailoverItem(groupId, p1, 1);
            slow.LastStatus = FailoverHealthStatus.Normal;
            slow.LastDelay = 80;
            var failed = CreateFailoverItem(groupId, p2, 2);
            failed.LastStatus = FailoverHealthStatus.Failed;
            failed.LastDelay = 10;
            var fast = CreateFailoverItem(groupId, p3, 3);
            fast.LastStatus = FailoverHealthStatus.Normal;
            fast.LastDelay = 30;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { slow, failed, fast });

            var profiles = await FailoverGroupManager.GetCandidateProfiles(groupId, EFailoverMode.LeastDelay);

            Assert.Equal([p3, p1, p2], profiles.Select(profile => profile.IndexId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, p1, p2, p3);
        }
    }

    [Fact]
    public async Task GetQueuePriorityMap_LeastDelayIncludesFailedEntriesForHealthLabelDisplay()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            var normal = CreateFailoverItem(groupId, p1, 1);
            normal.LastStatus = FailoverHealthStatus.Normal;
            normal.LastDelay = 40;
            var failed = CreateFailoverItem(groupId, p2, 2);
            failed.LastStatus = FailoverHealthStatus.Failed;
            failed.LastDelay = 0;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { normal, failed });

            var priorityMap = await FailoverGroupManager.GetQueuePriorityMap(groupId, EFailoverMode.LeastDelay);

            Assert.True(priorityMap.ContainsKey(p1));
            Assert.True(priorityMap.ContainsKey(p2));
            Assert.Equal(2, priorityMap[p2]);
        }
        finally
        {
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task GetQueuePriorityMap_LeastDelayUsesQueueSortNotDelayOrder()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var slow = $"slow-{suffix}";
        var fast = $"fast-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(slow, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(fast, "source-sub", ECoreType.Xray));
            var slowItem = CreateFailoverItem(groupId, slow, 1);
            slowItem.LastStatus = FailoverHealthStatus.Normal;
            slowItem.LastDelay = 200;
            var fastItem = CreateFailoverItem(groupId, fast, 2);
            fastItem.LastStatus = FailoverHealthStatus.Normal;
            fastItem.LastDelay = 20;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { slowItem, fastItem });

            var priorityMap = await FailoverGroupManager.GetQueuePriorityMap(groupId, EFailoverMode.LeastDelay);

            Assert.Equal(1, priorityMap[slow]);
            Assert.Equal(2, priorityMap[fast]);
        }
        finally
        {
            await Cleanup(groupId, slow, fast);
        }
    }

    [Fact]
    public async Task GetQueuePriorityMap_DoesNotIncludeDisabledCandidate()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var queued = $"queued-{suffix}";
        var candidate = $"candidate-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(queued, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(candidate, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, queued, 1));
            var disabled = CreateFailoverItem(groupId, candidate, 2);
            disabled.Enabled = false;
            await SQLiteHelper.Instance.ReplaceAsync(disabled);

            var priorityMap = await FailoverGroupManager.GetQueuePriorityMap(groupId);

            Assert.True(priorityMap.ContainsKey(queued));
            Assert.False(priorityMap.ContainsKey(candidate));
        }
        finally
        {
            await Cleanup(groupId, queued, candidate);
        }
    }

    [Fact]
    public async Task GetLeastDelayRuntimeTargetProfileId_ReturnsLowestNormalEnabledQueueItem()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            var item1 = CreateFailoverItem(groupId, p1, 1);
            item1.LastStatus = FailoverHealthStatus.Normal;
            item1.LastDelay = 80;
            var item2 = CreateFailoverItem(groupId, p2, 2);
            item2.LastStatus = FailoverHealthStatus.Normal;
            item2.LastDelay = 20;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { item1, item2 });

            var target = await FailoverGroupManager.GetLeastDelayRuntimeTargetProfileId(groupId);

            Assert.Equal(p2, target);
        }
        finally
        {
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task ProfilesViewModel_ActiveLeastDelayTargetIsDisplayedFirstEvenWhenDelayIsNotLowest()
    {
        await Task.CompletedTask;
        var active = new ProfileItemModel
        {
            IndexId = "active",
            IsInFailoverQueue = true,
            FailoverPriority = 2,
            IsCurrentFailoverPreferred = true,
        };
        var fastest = new ProfileItemModel
        {
            IndexId = "fastest",
            IsInFailoverQueue = true,
            FailoverPriority = 1,
        };

        var models = ProfilesViewModel.OrderFailoverProfileModelsForDisplay(
            [fastest, active],
            isFailoverGroup: true,
            isActiveFailoverGroup: true,
            activeFailoverTargetProfileId: active.IndexId,
            pinFailoverQueue: false);

        Assert.Equal(active.IndexId, models[0].IndexId);
        Assert.Equal(fastest.IndexId, models[1].IndexId);
    }

    [Fact]
    public async Task GetDueHealthCheckEntries_IncludesCooldownItemsAndSkipsMissingProfiles()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var due = $"due-{suffix}";
        var cooling = $"cooling-{suffix}";
        var missing = $"missing-{suffix}";
        var now = new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(due, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(cooling, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.InsertAllAsync(new[]
            {
                CreateFailoverItem(groupId, due, 1),
                new FailoverGroupItem
                {
                    Id = Utils.GetGuid(false),
                    GroupId = groupId,
                    SourceProfileId = cooling,
                    FailoverProfileId = cooling,
                    Sort = 2,
                    Enabled = true,
                    LastStatus = FailoverHealthStatus.Failed,
                    CooldownUntilTime = now + 60_000,
                },
                CreateFailoverItem(groupId, missing, 3),
            });

            var entries = await FailoverGroupManager.GetDueHealthCheckEntries(groupId, now);

            Assert.Equal([due, cooling], entries.Select(entry => entry.Profile.IndexId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, due, cooling, missing);
        }
    }

    [Fact]
    public async Task GetDueHealthCheckEntries_IncludesRecentlyProbedItemsForAllStatuses()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var recentNormal = $"recent-normal-{suffix}";
        var oldNormal = $"old-normal-{suffix}";
        var recentUnknown = $"recent-unknown-{suffix}";
        var oldProbing = $"old-probing-{suffix}";
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(recentNormal, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(oldNormal, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(recentUnknown, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(oldProbing, "source-sub", ECoreType.Xray));

            var item1 = CreateFailoverItem(groupId, recentNormal, 1);
            item1.LastStatus = FailoverHealthStatus.Normal;
            item1.LastProbeTime = now - 44_000;
            var item2 = CreateFailoverItem(groupId, oldNormal, 2);
            item2.LastStatus = FailoverHealthStatus.Normal;
            item2.LastProbeTime = now - 45_001;
            var item3 = CreateFailoverItem(groupId, recentUnknown, 3);
            item3.LastStatus = FailoverHealthStatus.Unknown;
            item3.LastProbeTime = now - 10_000;
            var item4 = CreateFailoverItem(groupId, oldProbing, 4);
            item4.LastStatus = FailoverHealthStatus.Probing;
            item4.LastProbeTime = now - 45_001;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { item1, item2, item3, item4 });

            var entries = await FailoverGroupManager.GetDueHealthCheckEntries(groupId, now);

            Assert.Equal([recentNormal, oldNormal, recentUnknown, oldProbing], entries.Select(entry => entry.Profile.IndexId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, recentNormal, oldNormal, recentUnknown, oldProbing);
        }
    }

    [Fact]
    public async Task CoreConfigContextBuilder_BuildSuppressFailoverKeepsRequestedNode()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";
        var routeId = $"route-{suffix}";
        var source = CreateProxy($"source-{suffix}", "source-sub", ECoreType.Xray);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem
            {
                Id = groupId,
                Remarks = "failover",
                IsFailoverGroup = true,
            });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.InsertAllAsync(new[]
            {
                CreateFailoverItem(groupId, p1, 1),
                CreateFailoverItem(groupId, p2, 2),
            });
            await SQLiteHelper.Instance.ReplaceAsync(new RoutingItem
            {
                Id = routeId,
                Remarks = routeId,
                RuleSet = "[]",
                IsActive = true,
            });

            var config = new Config
            {
                FailoverEnabled = true,
                FailoverMode = EFailoverMode.Failover,
                ActiveFailoverGroupId = groupId,
                TunModeItem = new(),
                SimpleDNSItem = new(),
                RoutingBasicItem = new(),
            };

            var result = await CoreConfigContextBuilder.Build(config, source, suppressFailover: true);

            Assert.True(result.Success);
            Assert.Equal(source.IndexId, result.Context.Node.IndexId);
            Assert.DoesNotContain(FailoverGroupManager.VirtualPolicyGroupPrefix, result.Context.Node.IndexId);
        }
        finally
        {
            await Cleanup(groupId, p1, p2, source.IndexId);
            await SQLiteHelper.Instance.ExecuteAsync($"delete from RoutingItem where Id = '{routeId}'");
        }
    }

    [Fact]
    public async Task CoreConfigContextBuilder_BuildNormalizesLegacyFailoverEnabled()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";
        var routeId = $"route-{suffix}";
        var source = CreateProxy($"source-{suffix}", "source-sub", ECoreType.Xray);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem
            {
                Id = groupId,
                Remarks = "failover",
                IsFailoverGroup = true,
            });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p1, 1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p2, 2));
            await SQLiteHelper.Instance.ReplaceAsync(new RoutingItem
            {
                Id = routeId,
                Remarks = routeId,
                RuleSet = "[]",
                IsActive = true,
            });

            var config = new Config
            {
                FailoverEnabled = true,
                FailoverMode = EFailoverMode.Off,
                ActiveFailoverGroupId = groupId,
                TunModeItem = new(),
                SimpleDNSItem = new(),
                RoutingBasicItem = new(),
            };

            var result = await CoreConfigContextBuilder.Build(config, source);

            Assert.True(result.Success);
            Assert.Equal(EFailoverMode.Failover, config.FailoverMode);
            Assert.Equal(source.IndexId, result.Context.Node.IndexId);
            Assert.NotNull(result.Context.FailoverRelayRuntime);
            Assert.Equal([p1, p2], result.Context.FailoverRelayRuntime!.Candidates.Select(candidate => candidate.ProfileId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, p1, p2, source.IndexId);
            await SQLiteHelper.Instance.ExecuteAsync($"delete from RoutingItem where Id = '{routeId}'");
        }
    }

    [Fact]
    public async Task CoreConfigContextBuilder_Build_MixedCoreQueueUsesResolvedDirectProfile()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var routeId = $"route-{suffix}";
        var current = CreateProxy($"current-{suffix}", "source-sub", ECoreType.Xray);
        var fallback = CreateProxy($"fallback-{suffix}", "source-sub", ECoreType.sing_box);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem
            {
                Id = groupId,
                Remarks = "failover",
                IsFailoverGroup = true,
            });
            await SQLiteHelper.Instance.ReplaceAsync(current);
            await SQLiteHelper.Instance.ReplaceAsync(fallback);
            var first = CreateFailoverItem(groupId, current.IndexId, 1);
            first.LastStatus = FailoverHealthStatus.Failed;
            var second = CreateFailoverItem(groupId, fallback.IndexId, 2);
            second.LastStatus = FailoverHealthStatus.Normal;
            second.LastDelay = 25;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { first, second });
            await SQLiteHelper.Instance.ReplaceAsync(new RoutingItem
            {
                Id = routeId,
                Remarks = routeId,
                RuleSet = "[]",
                IsActive = true,
            });

            var config = new Config
            {
                IndexId = current.IndexId,
                FailoverEnabled = true,
                FailoverMode = EFailoverMode.Failover,
                ActiveFailoverGroupId = groupId,
                TunModeItem = new(),
                SimpleDNSItem = new(),
                RoutingBasicItem = new(),
            };

            var result = await CoreConfigContextBuilder.Build(config, current);

            Assert.True(result.Success, string.Join(" | ", result.ValidatorResult.Errors));
            Assert.Equal(fallback.IndexId, result.Context.Node.IndexId);
        }
        finally
        {
            await Cleanup(groupId, current.IndexId, fallback.IndexId);
            await SQLiteHelper.Instance.ExecuteAsync($"delete from RoutingItem where Id = '{routeId}'");
        }
    }

    [Fact]
    public async Task TryResolveRuntimeNode_LeastDelayStartupProfileKeepsPreviousNodeUntilDelayResultExists()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var routeId = $"route-{suffix}";
        var previous = CreateProxy($"previous-{suffix}", "source-sub", ECoreType.Xray);
        var queued = CreateProxy($"queued-{suffix}", "source-sub", ECoreType.Xray);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(previous);
            await SQLiteHelper.Instance.ReplaceAsync(queued);
            var queuedItem = CreateFailoverItem(groupId, queued.IndexId, 1);
            queuedItem.LastStatus = FailoverHealthStatus.Unknown;
            queuedItem.LastDelay = 0;
            await SQLiteHelper.Instance.ReplaceAsync(queuedItem);
            await SQLiteHelper.Instance.ReplaceAsync(new RoutingItem { Id = routeId, Remarks = routeId, RuleSet = "[]", IsActive = true });

            var config = new Config
            {
                IndexId = previous.IndexId,
                FailoverEnabled = true,
                FailoverMode = EFailoverMode.LeastDelay,
                ActiveFailoverGroupId = groupId,
                FailoverStartupProfileId = previous.IndexId,
                TunModeItem = new(),
                SimpleDNSItem = new(),
                RoutingBasicItem = new(),
            };

            var result = await FailoverGroupManager.TryResolveRuntimeNode(config, previous);

            Assert.True(result.Success, string.Join(" | ", result.ValidatorResult.Errors));
            Assert.Equal(FailoverRuntimeKind.DirectProfile, result.Kind);
            Assert.Equal(previous.IndexId, result.EffectiveNode?.IndexId);
            Assert.Equal(previous.IndexId, result.RuntimeTargetProfileId);
        }
        finally
        {
            await Cleanup(groupId, previous.IndexId, queued.IndexId);
            await SQLiteHelper.Instance.ExecuteAsync($"delete from RoutingItem where Id = '{routeId}'");
        }
    }

    [Fact]
    public async Task TryResolveRuntimeNode_LeastDelayVirtualGroupStoresRuntimeQueueSignature()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var routeId = $"route-{suffix}";
        var current = CreateProxy($"current-{suffix}", "source-sub", ECoreType.Xray);
        var slow = CreateProxy($"slow-{suffix}", "source-sub", ECoreType.Xray);
        var fast = CreateProxy($"fast-{suffix}", "source-sub", ECoreType.Xray);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(current);
            await SQLiteHelper.Instance.ReplaceAsync(slow);
            await SQLiteHelper.Instance.ReplaceAsync(fast);
            var slowItem = CreateFailoverItem(groupId, slow.IndexId, 1);
            slowItem.LastStatus = FailoverHealthStatus.Normal;
            slowItem.LastDelay = 90;
            var fastItem = CreateFailoverItem(groupId, fast.IndexId, 2);
            fastItem.LastStatus = FailoverHealthStatus.Normal;
            fastItem.LastDelay = 20;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { slowItem, fastItem });
            await SQLiteHelper.Instance.ReplaceAsync(new RoutingItem { Id = routeId, Remarks = routeId, RuleSet = "[]", IsActive = true });

            var config = new Config
            {
                IndexId = current.IndexId,
                FailoverEnabled = true,
                FailoverMode = EFailoverMode.LeastDelay,
                ActiveFailoverGroupId = groupId,
                TunModeItem = new(),
                SimpleDNSItem = new(),
                RoutingBasicItem = new(),
            };

            var result = await FailoverGroupManager.TryResolveRuntimeNode(config, current);

            Assert.True(result.Success, string.Join(" | ", result.ValidatorResult.Errors));
            Assert.Equal(FailoverRuntimeKind.VirtualPolicyGroup, result.Kind);
            Assert.StartsWith(FailoverGroupManager.VirtualPolicyGroupPrefix, result.EffectiveNode?.IndexId);
            Assert.Equal(fast.IndexId, result.RuntimeTargetProfileId);
            Assert.Equal($"{fast.IndexId},{slow.IndexId}", result.RuntimeQueueSignature);
        }
        finally
        {
            await Cleanup(groupId, current.IndexId, slow.IndexId, fast.IndexId);
            await SQLiteHelper.Instance.ExecuteAsync($"delete from RoutingItem where Id = '{routeId}'");
        }
    }

    [Fact]
    public async Task CoreConfigContextBuilder_Build_MixedCoreAnytlsUpdatesRunCoreType()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var routeId = $"route-{suffix}";
        var current = CreateProxy($"current-{suffix}", "source-sub", ECoreType.Xray);
        var fallback = new ProfileItem
        {
            IndexId = $"anytls-{suffix}",
            Remarks = $"anytls-{suffix}",
            Subid = "source-sub",
            ConfigType = EConfigType.Anytls,
            CoreType = ECoreType.sing_box,
            Address = "198.51.100.31",
            Port = 443,
            Password = "secret",
            StreamSecurity = Global.StreamSecurity,
        };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem
            {
                Id = groupId,
                Remarks = "failover",
                IsFailoverGroup = true,
            });
            await SQLiteHelper.Instance.ReplaceAsync(current);
            await SQLiteHelper.Instance.ReplaceAsync(fallback);
            var first = CreateFailoverItem(groupId, current.IndexId, 1);
            first.LastStatus = FailoverHealthStatus.Failed;
            var second = CreateFailoverItem(groupId, fallback.IndexId, 2);
            second.LastStatus = FailoverHealthStatus.Normal;
            second.LastDelay = 25;
            await SQLiteHelper.Instance.InsertAllAsync(new[] { first, second });
            await SQLiteHelper.Instance.ReplaceAsync(new RoutingItem
            {
                Id = routeId,
                Remarks = routeId,
                RuleSet = "[]",
                IsActive = true,
            });

            var config = new Config
            {
                IndexId = current.IndexId,
                FailoverEnabled = true,
                FailoverMode = EFailoverMode.Failover,
                ActiveFailoverGroupId = groupId,
                TunModeItem = new(),
                SimpleDNSItem = new(),
                RoutingBasicItem = new(),
            };

            var result = await CoreConfigContextBuilder.Build(config, current);

            Assert.True(result.Success, string.Join(" | ", result.ValidatorResult.Errors));
            Assert.Equal(fallback.IndexId, result.Context.Node.IndexId);
            Assert.Equal(ECoreType.sing_box, result.Context.RunCoreType);
        }
        finally
        {
            await Cleanup(groupId, current.IndexId, fallback.IndexId);
            await SQLiteHelper.Instance.ExecuteAsync($"delete from RoutingItem where Id = '{routeId}'");
        }
    }

    [Fact]
    public async Task MoveQueueItems_NonContiguousSources_RewritesQueueAsContiguousBlock()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var ids = new[] { $"A-{suffix}", $"B-{suffix}", $"C-{suffix}", $"D-{suffix}", $"E-{suffix}" };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.InsertAllAsync(ids.Select((id, index) => CreateFailoverItem(groupId, id, index + 1)));

            var result = await FailoverGroupManager.MoveQueueItems(groupId, [ids[1], ids[3]], ids[4]);

            Assert.Equal(0, result);
            var ordered = (await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                    .Where(item => item.GroupId == groupId && item.Enabled)
                    .ToListAsync())
                .OrderBy(item => item.Sort)
                .ToList();
            Assert.Equal([1, 2, 3, 4, 5], ordered.Select(item => item.Sort).ToArray());
            Assert.Equal([ids[0], ids[2], ids[4], ids[1], ids[3]], ordered.Select(item => item.SourceProfileId).ToArray());
        }
        finally
        {
            await Cleanup(groupId, ids);
        }
    }

    [Fact]
    public async Task MoveQueueItem_PositionRewritesPriorityFromRows()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var ids = Enumerable.Range(1, 5).Select(index => $"P{index}-{suffix}").ToArray();

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.InsertAllAsync(ids.Select((id, index) => CreateFailoverItem(groupId, id, index + 1)));

            var result = await FailoverGroupManager.MoveQueueItem(groupId, ids[4], EMove.Position, ids[2]);

            Assert.Equal(0, result);
            var ordered = (await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                    .Where(item => item.GroupId == groupId && item.Enabled)
                    .ToListAsync())
                .OrderBy(item => item.Sort)
                .ToList();

            Assert.Equal([1, 2, 3, 4, 5], ordered.Select(item => item.Sort).ToArray());
            Assert.Equal([ids[0], ids[1], ids[4], ids[2], ids[3]], ordered.Select(item => item.SourceProfileId).ToArray());
            Assert.Equal(3, ordered.Single(item => item.SourceProfileId == ids[4]).Sort);
        }
        finally
        {
            await Cleanup(groupId, ids);
        }
    }

    [Fact]
    public async Task ReorderQueueItems_HeaderSortedIds_RewritesPriorityMap()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var ids = new[] { $"slow-{suffix}", $"fast-{suffix}", $"middle-{suffix}" };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.InsertAllAsync(ids.Select(id => CreateProxy(id, groupId, ECoreType.Xray)));
            await SQLiteHelper.Instance.InsertAllAsync(ids.Select((id, index) => CreateFailoverItem(groupId, id, index + 1)));

            var result = await FailoverGroupManager.ReorderQueueItems(groupId, [ids[1], ids[2], ids[0]]);

            Assert.Equal(0, result);
            var ordered = (await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                    .Where(item => item.GroupId == groupId && item.Enabled)
                    .ToListAsync())
                .OrderBy(item => item.Sort)
                .ToList();
            Assert.Equal([1, 2, 3], ordered.Select(item => item.Sort).ToArray());
            Assert.Equal([ids[1], ids[2], ids[0]], ordered.Select(item => item.SourceProfileId).ToArray());

            var priorityMap = await FailoverGroupManager.GetQueuePriorityMap(groupId, EFailoverMode.Failover);
            Assert.Equal(1, priorityMap[ids[1]]);
            Assert.Equal(2, priorityMap[ids[2]]);
            Assert.Equal(3, priorityMap[ids[0]]);
        }
        finally
        {
            await Cleanup(groupId, ids);
        }
    }

    [Fact]
    public async Task MoveQueueItems_UnknownSource_ReturnsFailure()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var ids = new[] { $"A-{suffix}", $"B-{suffix}" };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.InsertAllAsync(ids.Select((id, index) => CreateFailoverItem(groupId, id, index + 1)));

            var result = await FailoverGroupManager.MoveQueueItems(groupId, [$"missing-{suffix}"], ids[1]);

            Assert.Equal(-1, result);
        }
        finally
        {
            await Cleanup(groupId, ids);
        }
    }

    [Fact]
    public async Task MoveQueueItems_TargetInsideSourceSpan_DoesNotRewriteQueue()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var ids = new[] { $"A-{suffix}", $"B-{suffix}", $"C-{suffix}", $"D-{suffix}", $"E-{suffix}" };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.InsertAllAsync(ids.Select((id, index) => CreateFailoverItem(groupId, id, index + 1)));

            var result = await FailoverGroupManager.MoveQueueItems(groupId, [ids[1], ids[3]], ids[2]);

            Assert.Equal(0, result);
            var ordered = (await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                    .Where(item => item.GroupId == groupId && item.Enabled)
                    .ToListAsync())
                .OrderBy(item => item.Sort)
                .Select(item => item.SourceProfileId)
                .ToArray();
            Assert.Equal(ids, ordered);
        }
        finally
        {
            await Cleanup(groupId, ids);
        }
    }

    [Fact]
    public async Task CheckFailoverQueueCoreType_ReturnsSameXrayForAllXrayQueueNodes()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.InsertAllAsync(new[]
            {
                CreateFailoverItem(groupId, p1, 1),
                CreateFailoverItem(groupId, p2, 2),
            });

            var result = await FailoverGroupManager.CheckFailoverQueueCoreType(groupId);

            Assert.Equal(EFailoverQueueCoreTypeCheckResult.SameXray, result);
        }
        finally
        {
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task CheckFailoverQueueCoreType_ReturnsSameSingBoxForAllSingBoxQueueNodes()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.sing_box));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.sing_box));
            await SQLiteHelper.Instance.InsertAllAsync(new[]
            {
                CreateFailoverItem(groupId, p1, 1),
                CreateFailoverItem(groupId, p2, 2),
            });

            var result = await FailoverGroupManager.CheckFailoverQueueCoreType(groupId);

            Assert.Equal(EFailoverQueueCoreTypeCheckResult.SameSingBox, result);
        }
        finally
        {
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task CheckFailoverQueueCoreType_ReturnsMixedForMixedQueueNodes()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"p1-{suffix}";
        var p2 = $"p2-{suffix}";

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, "source-sub", ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, "source-sub", ECoreType.sing_box));
            await SQLiteHelper.Instance.InsertAllAsync(new[]
            {
                CreateFailoverItem(groupId, p1, 1),
                CreateFailoverItem(groupId, p2, 2),
            });

            var result = await FailoverGroupManager.CheckFailoverQueueCoreType(groupId);

            Assert.Equal(EFailoverQueueCoreTypeCheckResult.Mixed, result);
        }
        finally
        {
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task CheckFailoverQueueCoreType_ReturnsEmptyForMissingQueue()
    {
        PrepareTables();

        var result = await FailoverGroupManager.CheckFailoverQueueCoreType("missing-group");

        Assert.Equal(EFailoverQueueCoreTypeCheckResult.Empty, result);
    }

    private static void PrepareTables()
    {
        SQLiteHelper.Instance.CreateTable<SubItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<ProfileExItem>();
        SQLiteHelper.Instance.CreateTable<FailoverGroupItem>();
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        SQLiteHelper.Instance.CreateTable<DNSItem>();
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
    }

    private static ProfileItem CreateProxy(string indexId, string subid, ECoreType coreType)
    {
        return new ProfileItem
        {
            IndexId = indexId,
            Remarks = indexId,
            Subid = subid,
            ConfigType = EConfigType.SOCKS,
            CoreType = coreType,
            Address = "198.51.100.30",
            Port = 443,
        };
    }

    private static FailoverGroupItem CreateFailoverItem(string groupId, string sourceProfileId, int sort)
    {
        return new FailoverGroupItem
        {
            Id = Utils.GetGuid(false),
            GroupId = groupId,
            SourceProfileId = sourceProfileId,
            FailoverProfileId = sourceProfileId,
            Sort = sort,
            Enabled = true,
        };
    }

    private static async Task Cleanup(string groupId, params string[] profileIds)
    {
        await SQLiteHelper.Instance.ExecuteAsync($"delete from FailoverGroupItem where GroupId = '{groupId}'");
        await SQLiteHelper.Instance.ExecuteAsync($"delete from SubItem where Id = '{groupId}'");
        await SQLiteHelper.Instance.ExecuteAsync($"delete from ProfileItem where Subid = '{groupId}'");
        foreach (var profileId in profileIds)
        {
            await SQLiteHelper.Instance.ExecuteAsync($"delete from ProfileItem where IndexId = '{profileId}'");
        }
    }
}
