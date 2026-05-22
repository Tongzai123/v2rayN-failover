using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Events;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Resx;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverHealthServiceTests
{
    [Fact]
    public void DefaultProbeLoopInterval_IsSixtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), FailoverHealthTiming.ProbeLoopInterval);
    }

    [Fact]
    public async Task CheckOnceAsync_UpdatesQueueItemStatus()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config { FailoverEnabled = true, FailoverMode = EFailoverMode.Failover, ActiveFailoverGroupId = groupId };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = profileId,
                FailoverProfileId = profileId,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Unknown,
            });

            var service = new FailoverHealthService(config, new FakeProbe(FailoverHealthProbeResult.Success(88)));
            await service.CheckOnceAsync();

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .FirstAsync(item => item.GroupId == groupId && item.SourceProfileId == profileId);
            Assert.Equal(FailoverHealthStatus.Normal, stored.LastStatus);
            Assert.Equal(88, stored.LastDelay);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task CheckOnceAsync_CancelledProbeDoesNotMarkFailed()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config { FailoverEnabled = true, FailoverMode = EFailoverMode.Failover, ActiveFailoverGroupId = groupId };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = profileId,
                FailoverProfileId = profileId,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
            });

            var service = new FailoverHealthService(config, new FakeProbe(FailoverHealthProbeResult.Cancel()));
            await service.CheckOnceAsync();

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .FirstAsync(item => item.GroupId == groupId && item.SourceProfileId == profileId);
            Assert.Equal(FailoverHealthStatus.Normal, stored.LastStatus);
            Assert.Equal(0, stored.FailureCount);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task CheckOnceAsync_UsesOneBatchProbeForMultipleDueItems()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var p2 = $"profile-2-{suffix}";
        var config = new Config { FailoverEnabled = true, FailoverMode = EFailoverMode.Failover, ActiveFailoverGroupId = groupId };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p1, 1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p2, 2));

            var probe = new FakeBatchProbe([
                new(p1, FailoverHealthProbeResult.Success(81)),
                new(p2, FailoverHealthProbeResult.Success(92)),
            ]);

            var service = new FailoverHealthService(config, probe);
            await service.CheckOnceAsync();

            Assert.Equal(1, probe.BatchCallCount);
            Assert.Equal(0, probe.ProbeCallCount);
            Assert.Equal([p1, p2], probe.ProfileIds);

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .Where(item => item.GroupId == groupId)
                .OrderBy(item => item.Sort)
                .ToListAsync();
            Assert.All(stored, item => Assert.Equal(FailoverHealthStatus.Normal, item.LastStatus));
            Assert.Equal(81, stored[0].LastDelay);
            Assert.Equal(92, stored[1].LastDelay);
        }
        finally
        {
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task CheckOnceAsync_PublishesHealthChangedWhenBatchStartsAndItemsComplete()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var p2 = $"profile-2-{suffix}";
        var config = new Config { FailoverEnabled = true, FailoverMode = EFailoverMode.Failover, ActiveFailoverGroupId = groupId };
        var publishCount = 0;
        using var subscription = AppEvents.FailoverHealthChangedRequested
            .AsObservable()
            .Subscribe(_ => publishCount++);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p1, 1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p2, 2));

            var probe = new FakeBatchProbe([
                new(p1, FailoverHealthProbeResult.Success(81)),
                new(p2, FailoverHealthProbeResult.Success(92)),
            ]);

            var service = new FailoverHealthService(config, probe);
            await service.CheckOnceAsync();

            Assert.Equal(4, publishCount);
        }
        finally
        {
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task CheckOnceAsync_KeepsFirstBatchResultWhenProfileIdIsDuplicated()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var p2 = $"profile-2-{suffix}";
        var config = new Config { FailoverEnabled = true, FailoverMode = EFailoverMode.Failover, ActiveFailoverGroupId = groupId };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p1, 1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p2, 2));

            var probe = new FakeBatchProbe([
                new(p1, FailoverHealthProbeResult.Success(81)),
                new(p1, FailoverHealthProbeResult.Success(82)),
                new(p2, FailoverHealthProbeResult.Success(92)),
            ]);

            var service = new FailoverHealthService(config, probe);
            await service.CheckOnceAsync();

            Assert.Equal(1, probe.BatchCallCount);
            Assert.Equal(0, probe.ProbeCallCount);

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .Where(item => item.GroupId == groupId)
                .OrderBy(item => item.Sort)
                .ToListAsync();

            Assert.All(stored, item => Assert.Equal(FailoverHealthStatus.Normal, item.LastStatus));
            Assert.Equal(81, stored[0].LastDelay);
            Assert.Equal(92, stored[1].LastDelay);
        }
        finally
        {
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task CheckOnceAsync_CancelledBatchWithDuplicateProfilesRestoresPreviousStatus()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config { FailoverEnabled = true, FailoverMode = EFailoverMode.Failover, ActiveFailoverGroupId = groupId };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = profileId,
                FailoverProfileId = profileId,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
            });
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = profileId,
                FailoverProfileId = profileId,
                Sort = 2,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
            });

            var service = new FailoverHealthService(config, new FakeCancellingBatchProbe());
            await service.CheckOnceAsync();

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .Where(item => item.GroupId == groupId)
                .OrderBy(item => item.Sort)
                .ToListAsync();

            Assert.Equal(2, stored.Count);
            Assert.All(stored, item =>
            {
                Assert.Equal(FailoverHealthStatus.Normal, item.LastStatus);
                Assert.Equal(0, item.FailureCount);
            });
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task CheckOnceAsync_PublishesSpeedTestProgressForDueItems()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var p2 = $"profile-2-{suffix}";
        var config = new Config { FailoverEnabled = true, FailoverMode = EFailoverMode.Failover, ActiveFailoverGroupId = groupId };
        var observed = new List<(string? IndexId, string? Delay)>();
        using var subscription = AppEvents.SpeedTestResultRequested
            .AsObservable()
            .Subscribe(result => observed.Add((result.IndexId, result.Delay)));

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p1, 1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p2, 2));

            var probe = new FakeBatchProbe([
                new(p1, FailoverHealthProbeResult.Success(81)),
                new(p2, FailoverHealthProbeResult.Failure("request-failed")),
            ]);

            var service = new FailoverHealthService(config, probe);
            await service.CheckOnceAsync();

            Assert.Equal([
                (p1, ResUI.Speedtesting),
                (p2, ResUI.Speedtesting),
                (p1, "81"),
                (p2, "-1"),
            ], observed);
        }
        finally
        {
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task CheckOnceAsync_AppliesHealthStatusBeforePublishingCompletedDelay()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config { FailoverEnabled = true, FailoverMode = EFailoverMode.Failover, ActiveFailoverGroupId = groupId };
        var statusWhenDelayCompleted = string.Empty;
        using var subscription = AppEvents.SpeedTestResultRequested
            .AsObservable()
            .Subscribe(result =>
            {
                if (result.IndexId == profileId && result.Delay == "88")
                {
                    statusWhenDelayCompleted = SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                        .FirstAsync(item => item.GroupId == groupId && item.SourceProfileId == profileId)
                        .GetAwaiter()
                        .GetResult()
                        .LastStatus;
                }
            });

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, profileId, 1));

            var service = new FailoverHealthService(config, new FakeBatchProbe([
                new(profileId, FailoverHealthProbeResult.Success(88)),
            ]));
            await service.CheckOnceAsync();

            Assert.Equal(FailoverHealthStatus.Normal, statusWhenDelayCompleted);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task CheckActiveGroupOnceAsync_ForcePublishesSpeedTestProgressForCoolingItems()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var config = new Config
        {
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.Failover,
            ActiveFailoverGroupId = groupId,
        };
        var observed = new List<(string? IndexId, string? Delay)>();
        using var subscription = AppEvents.SpeedTestResultRequested
            .AsObservable()
            .Subscribe(result => observed.Add((result.IndexId, result.Delay)));

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = profileId,
                FailoverProfileId = profileId,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Failed,
                CooldownUntilTime = now + 60_000,
            });

            var probe = new FakeBatchProbe([new(profileId, FailoverHealthProbeResult.Success(66))]);
            var service = new FailoverHealthService(config, probe);
            await service.CheckActiveGroupOnceAsync(force: true, reloadOnLeastDelayChange: false);

            Assert.Equal([
                (profileId, ResUI.Speedtesting),
                (profileId, "66"),
            ], observed);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task CheckOnceAsync_OffModeSkipsProbe()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config
        {
            FailoverEnabled = false,
            FailoverMode = EFailoverMode.Off,
            ActiveFailoverGroupId = groupId,
        };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, profileId, 1));

            var probe = new FakeBatchProbe([new(profileId, FailoverHealthProbeResult.Success(81))]);
            var service = new FailoverHealthService(config, probe);
            await service.CheckOnceAsync();

            Assert.Equal(0, probe.BatchCallCount);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task CheckOnceAsync_RetrySuccessMarksNodeNormal()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config { FailoverEnabled = true, FailoverMode = EFailoverMode.LeastDelay, ActiveFailoverGroupId = groupId };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, profileId, 1));

            var service = new FailoverHealthService(config, new RetrySuccessBatchProbe());
            await service.CheckOnceAsync();

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .FirstAsync(item => item.GroupId == groupId && item.SourceProfileId == profileId);

            Assert.Equal(FailoverHealthStatus.Normal, stored.LastStatus);
            Assert.Equal(88, stored.LastDelay);
            Assert.Equal(0, stored.FailureCount);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task CheckActiveGroupOnceAsync_SkipsWhenProbeAlreadyRunning()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config
        {
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.Failover,
            ActiveFailoverGroupId = groupId,
        };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, profileId, 1));

            var probe = new BlockingBatchProbe(FailoverHealthProbeResult.Success(77));
            var service = new FailoverHealthService(config, probe);
            var firstProbeTask = service.CheckActiveGroupOnceAsync(force: false, reloadOnLeastDelayChange: false);

            await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));
            var overlappingProbeTask = service.CheckActiveGroupOnceAsync(force: true, reloadOnLeastDelayChange: false);
            await Task.Delay(200);

            Assert.Equal(1, probe.BatchCallCount);

            probe.Complete();
            await Task.WhenAll(firstProbeTask, overlappingProbeTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task Stop_DoesNotCancelInFlightProbe()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config
        {
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.Failover,
            ActiveFailoverGroupId = groupId,
        };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, profileId, 1));

            var probe = new BlockingBatchProbe(FailoverHealthProbeResult.Success(77));
            var service = new FailoverHealthService(config, probe);
            service.Start();

            await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));
            config.FailoverMode = EFailoverMode.Off;
            config.FailoverEnabled = false;
            service.Stop();
            probe.Complete();

            var loopTask = GetLoopTask(service);
            Assert.NotNull(loopTask);
            await loopTask.WaitAsync(TimeSpan.FromSeconds(5));

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .FirstAsync(item => item.GroupId == groupId && item.SourceProfileId == profileId);
            Assert.Equal(FailoverHealthStatus.Normal, stored.LastStatus);
            Assert.Equal(77, stored.LastDelay);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task StopCancelCurrentProbe_RestoresPreviousStatus()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config
        {
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.Failover,
            ActiveFailoverGroupId = groupId,
        };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = profileId,
                FailoverProfileId = profileId,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
                LastDelay = 45,
            });

            var probe = new BlockingBatchProbe(FailoverHealthProbeResult.Success(77));
            var service = new FailoverHealthService(config, probe);
            service.Start();

            await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));
            service.Stop(cancelCurrentProbe: true);

            var loopTask = GetLoopTask(service);
            Assert.NotNull(loopTask);
            await loopTask.WaitAsync(TimeSpan.FromSeconds(5));

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .FirstAsync(item => item.GroupId == groupId && item.SourceProfileId == profileId);
            Assert.Equal(FailoverHealthStatus.Normal, stored.LastStatus);
            Assert.Equal(45, stored.LastDelay);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task StopAndClearProbingAsync_CancelsProbeAndMarksProbingUnknown()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config
        {
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.Failover,
            ActiveFailoverGroupId = groupId,
        };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, profileId, 1));

            var probe = new BlockingBatchProbe(FailoverHealthProbeResult.Success(77));
            var service = new FailoverHealthService(config, probe);
            service.Start();

            await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));
            config.FailoverMode = EFailoverMode.Off;
            config.FailoverEnabled = false;
            await service.StopAndClearProbingAsync();

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .FirstAsync(item => item.GroupId == groupId && item.SourceProfileId == profileId);
            Assert.Equal(FailoverHealthStatus.Unknown, stored.LastStatus);
            Assert.Equal(0, stored.FailureCount);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task StopAndRestoreProbingAsync_RestoresNormalStatusFromBeforeProbe()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config
        {
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.Failover,
            ActiveFailoverGroupId = groupId,
        };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = profileId,
                FailoverProfileId = profileId,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
                LastDelay = 45,
            });

            var probe = new BlockingBatchProbe(FailoverHealthProbeResult.Success(77));
            var service = new FailoverHealthService(config, probe);
            service.Start();

            await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));
            config.FailoverMode = EFailoverMode.Off;
            config.FailoverEnabled = false;
            await service.StopAndRestoreProbingAsync();
            var loopTask = GetLoopTask(service);
            Assert.NotNull(loopTask);
            await loopTask.WaitAsync(TimeSpan.FromSeconds(5));

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .FirstAsync(item => item.GroupId == groupId && item.SourceProfileId == profileId);
            Assert.Equal(FailoverHealthStatus.Normal, stored.LastStatus);
            Assert.Equal(45, stored.LastDelay);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task StopAndRestoreProbingAsync_RestoresFailedStatusFromBeforeProbe()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var config = new Config
        {
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.Failover,
            ActiveFailoverGroupId = groupId,
        };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = profileId,
                FailoverProfileId = profileId,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Failed,
                FailureCount = 2,
                LastFailureReason = "timeout",
                CooldownUntilTime = now - 1,
            });

            var probe = new BlockingBatchProbe(FailoverHealthProbeResult.Success(77));
            var service = new FailoverHealthService(config, probe);
            service.Start();

            await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));
            config.FailoverMode = EFailoverMode.Off;
            config.FailoverEnabled = false;
            await service.StopAndRestoreProbingAsync();
            var loopTask = GetLoopTask(service);
            Assert.NotNull(loopTask);
            await loopTask.WaitAsync(TimeSpan.FromSeconds(5));

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .FirstAsync(item => item.GroupId == groupId && item.SourceProfileId == profileId);
            Assert.Equal(FailoverHealthStatus.Failed, stored.LastStatus);
            Assert.Equal(2, stored.FailureCount);
            Assert.Equal("timeout", stored.LastFailureReason);
            Assert.Equal(now - 1, stored.CooldownUntilTime);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task StopAndRestoreProbingAsync_MarksHistoricalProbingUnknownWhenPreviousStatusMissing()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config
        {
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.Failover,
            ActiveFailoverGroupId = groupId,
        };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = profileId,
                FailoverProfileId = profileId,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Probing,
            });

            var service = new FailoverHealthService(config, new FakeProbe(FailoverHealthProbeResult.Success(77)));
            await service.StopAndRestoreProbingAsync();

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .FirstAsync(item => item.GroupId == groupId && item.SourceProfileId == profileId);
            Assert.Equal(FailoverHealthStatus.Unknown, stored.LastStatus);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public void Start_OffModeDoesNotCreateLoopTask()
    {
        var config = new Config
        {
            FailoverEnabled = false,
            FailoverMode = EFailoverMode.Off,
            ActiveFailoverGroupId = "failover-off",
        };
        var service = new FailoverHealthService(config, new FakeProbe(FailoverHealthProbeResult.Success(88)));

        try
        {
            service.Start();

            Assert.Null(GetLoopTask(service));
        }
        finally
        {
            service.Stop();
        }
    }

    [Fact]
    public async Task CheckActiveGroupOnceAsync_ForceProbesCoolingItems()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var config = new Config
        {
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.LeastDelay,
            ActiveFailoverGroupId = groupId,
        };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = profileId,
                FailoverProfileId = profileId,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Failed,
                CooldownUntilTime = now + 60_000,
            });

            var probe = new FakeBatchProbe([new(profileId, FailoverHealthProbeResult.Success(81))]);
            var service = new FailoverHealthService(config, probe);
            await service.CheckActiveGroupOnceAsync(force: true, reloadOnLeastDelayChange: false);

            Assert.Equal(1, probe.BatchCallCount);
            Assert.Equal([profileId], probe.ProfileIds);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task CheckActiveGroupOnceAsync_LeastDelayReloadsOnlyWhenFirstEntryChanges()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var p2 = $"profile-2-{suffix}";
        var config = new Config
        {
            IndexId = p1,
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.LeastDelay,
            ActiveFailoverGroupId = groupId,
        };
        var reloadCount = 0;
        using var subscription = AppEvents.ReloadRequested
            .AsObservable()
            .Subscribe(_ => reloadCount++);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = p1,
                FailoverProfileId = p1,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
                LastDelay = 100,
            });
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = p2,
                FailoverProfileId = p2,
                Sort = 2,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
                LastDelay = 120,
            });

            var probe = new FakeBatchProbe([
                new(p1, FailoverHealthProbeResult.Success(100)),
                new(p2, FailoverHealthProbeResult.Success(30)),
            ]);
            var service = new FailoverHealthService(config, probe);
            await service.CheckActiveGroupOnceAsync(force: false, reloadOnLeastDelayChange: true);

            Assert.Equal(1, reloadCount);

            var stableProbe = new FakeBatchProbe([
                new(p1, FailoverHealthProbeResult.Success(90)),
                new(p2, FailoverHealthProbeResult.Success(30)),
            ]);
            var stableService = new FailoverHealthService(config, stableProbe);
            await stableService.CheckActiveGroupOnceAsync(force: false, reloadOnLeastDelayChange: true);

            Assert.Equal(1, reloadCount);
        }
        finally
        {
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task ApplyManualRealPingResultsAsync_LeastDelayUpdatesQueueAndReloadsWhenBestChanges()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var p2 = $"profile-2-{suffix}";
        var outsider = $"profile-outsider-{suffix}";
        var config = new Config
        {
            IndexId = p1,
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.LeastDelay,
            ActiveFailoverGroupId = groupId,
        };
        var previousRunningTarget = AppManager.Instance.RunningFailoverTargetProfileId;
        var previousRunningSignature = AppManager.Instance.RunningFailoverQueueSignature;
        AppManager.Instance.RunningFailoverTargetProfileId = p1;
        AppManager.Instance.RunningFailoverQueueSignature = $"{p1},{p2}";
        var reloadCount = 0;
        using var subscription = AppEvents.ReloadRequested.AsObservable().Subscribe(_ => reloadCount++);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(outsider));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = p1,
                FailoverProfileId = p1,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
                LastDelay = 100,
            });
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = p2,
                FailoverProfileId = p2,
                Sort = 2,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Unknown,
            });

            var service = new FailoverHealthService(config, new FakeProbe(FailoverHealthProbeResult.Success(88)));
            await service.ApplyManualRealPingResultsAsync([
                RealPingProbeResult.Success(p2, 30),
                RealPingProbeResult.Success(outsider, 5),
            ]);

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .Where(item => item.GroupId == groupId)
                .OrderBy(item => item.Sort)
                .ToListAsync();

            Assert.Equal(FailoverHealthStatus.Normal, stored[0].LastStatus);
            Assert.Equal(100, stored[0].LastDelay);
            Assert.Equal(FailoverHealthStatus.Normal, stored[1].LastStatus);
            Assert.Equal(30, stored[1].LastDelay);
            Assert.Equal(1, reloadCount);
            Assert.Equal(p2, config.FailoverStartupProfileId);
        }
        finally
        {
            AppManager.Instance.RunningFailoverTargetProfileId = previousRunningTarget;
            AppManager.Instance.RunningFailoverQueueSignature = previousRunningSignature;
            await Cleanup(groupId, p1, p2, outsider);
        }
    }

    [Fact]
    public async Task ApplyManualRealPingResultsAsync_LeastDelayDoesNotReloadWhenBestTargetAlreadyRunning()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var p2 = $"profile-2-{suffix}";
        var config = new Config
        {
            IndexId = p1,
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.LeastDelay,
            ActiveFailoverGroupId = groupId,
        };
        var previousRunningTarget = AppManager.Instance.RunningFailoverTargetProfileId;
        var previousRunningSignature = AppManager.Instance.RunningFailoverQueueSignature;
        AppManager.Instance.RunningFailoverTargetProfileId = p1;
        AppManager.Instance.RunningFailoverQueueSignature = $"{p1},{p2}";
        var reloadCount = 0;
        using var subscription = AppEvents.ReloadRequested.AsObservable().Subscribe(_ => reloadCount++);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p1, 1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p2, 2));

            var service = new FailoverHealthService(config, new FakeProbe(FailoverHealthProbeResult.Success(88)));
            await service.ApplyManualRealPingResultsAsync([
                RealPingProbeResult.Success(p1, 20),
                RealPingProbeResult.Success(p2, 80),
            ]);

            Assert.Equal(0, reloadCount);
            Assert.Null(config.FailoverStartupProfileId);
        }
        finally
        {
            AppManager.Instance.RunningFailoverTargetProfileId = previousRunningTarget;
            AppManager.Instance.RunningFailoverQueueSignature = previousRunningSignature;
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task ApplyManualRealPingResultsAsync_FailoverModeUpdatesQueueHealth()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config
        {
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.Failover,
            ActiveFailoverGroupId = groupId,
        };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = profileId,
                FailoverProfileId = profileId,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Failed,
                FailureCount = 2,
                CooldownUntilTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000,
                LastFailureReason = "request-failed",
            });

            var service = new FailoverHealthService(config, new FakeProbe(FailoverHealthProbeResult.Success(88)));
            await service.ApplyManualRealPingResultsAsync([RealPingProbeResult.Success(profileId, 20)]);

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .FirstAsync(item => item.GroupId == groupId && item.SourceProfileId == profileId);
            Assert.Equal(FailoverHealthStatus.Normal, stored.LastStatus);
            Assert.Equal(20, stored.LastDelay);
            Assert.Equal(0, stored.FailureCount);
            Assert.Null(stored.CooldownUntilTime);
            Assert.Null(stored.LastFailureReason);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task ApplyManualRealPingResultsAsync_FailoverModeFailureUpdatesQueueHealthWithoutCooldown()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config
        {
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.Failover,
            ActiveFailoverGroupId = groupId,
        };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, profileId, 1));

            var service = new FailoverHealthService(config, new FakeProbe(FailoverHealthProbeResult.Success(88)));
            await service.ApplyManualRealPingResultsAsync([RealPingProbeResult.Failure(profileId, "request-failed")]);

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .FirstAsync(item => item.GroupId == groupId && item.SourceProfileId == profileId);
            Assert.Equal(FailoverHealthStatus.Failed, stored.LastStatus);
            Assert.Equal(1, stored.FailureCount);
            Assert.Equal("request-failed", stored.LastFailureReason);
            Assert.Null(stored.CooldownUntilTime);
        }
        finally
        {
            await Cleanup(groupId, profileId);
        }
    }

    [Fact]
    public async Task CheckActiveGroupOnceAsync_LeastDelayReloadsWhenFallbackQueueOrderChanges()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var p2 = $"profile-2-{suffix}";
        var p3 = $"profile-3-{suffix}";
        var config = new Config
        {
            IndexId = p1,
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.LeastDelay,
            ActiveFailoverGroupId = groupId,
        };
        var previousRunningTarget = AppManager.Instance.RunningFailoverTargetProfileId;
        var previousRunningSignature = AppManager.Instance.RunningFailoverQueueSignature;
        AppManager.Instance.RunningFailoverTargetProfileId = p1;
        AppManager.Instance.RunningFailoverQueueSignature = $"{p1},{p2},{p3}";
        var reloadCount = 0;
        using var subscription = AppEvents.ReloadRequested.AsObservable().Subscribe(_ => reloadCount++);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p3));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = p1,
                FailoverProfileId = p1,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
                LastDelay = 20,
            });
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = p2,
                FailoverProfileId = p2,
                Sort = 2,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
                LastDelay = 60,
            });
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = p3,
                FailoverProfileId = p3,
                Sort = 3,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
                LastDelay = 90,
            });

            var probe = new FakeBatchProbe([
                new(p1, FailoverHealthProbeResult.Success(20)),
                new(p2, FailoverHealthProbeResult.Success(100)),
                new(p3, FailoverHealthProbeResult.Success(30)),
            ]);
            var service = new FailoverHealthService(config, probe);

            await service.CheckActiveGroupOnceAsync(force: false, reloadOnLeastDelayChange: true);

            Assert.Equal(1, reloadCount);
        }
        finally
        {
            AppManager.Instance.RunningFailoverTargetProfileId = previousRunningTarget;
            AppManager.Instance.RunningFailoverQueueSignature = previousRunningSignature;
            await Cleanup(groupId, p1, p2, p3);
        }
    }

    [Fact]
    public async Task CheckActiveGroupOnceAsync_LeastDelayMixedCoreDoesNotReloadWhenOnlyFallbackOrderChanges()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var p2 = $"profile-2-{suffix}";
        var p3 = $"profile-3-{suffix}";
        var config = new Config
        {
            IndexId = p1,
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.LeastDelay,
            ActiveFailoverGroupId = groupId,
        };
        var reloadCount = 0;
        using var subscription = AppEvents.ReloadRequested.AsObservable().Subscribe(_ => reloadCount++);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1, ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2, ECoreType.sing_box));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p3, ECoreType.sing_box));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = p1,
                FailoverProfileId = p1,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
                LastDelay = 20,
            });
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = p2,
                FailoverProfileId = p2,
                Sort = 2,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
                LastDelay = 60,
            });
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = p3,
                FailoverProfileId = p3,
                Sort = 3,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
                LastDelay = 90,
            });

            var probe = new FakeBatchProbe([
                new(p1, FailoverHealthProbeResult.Success(20)),
                new(p2, FailoverHealthProbeResult.Success(100)),
                new(p3, FailoverHealthProbeResult.Success(30)),
            ]);
            var service = new FailoverHealthService(config, probe);

            await service.CheckActiveGroupOnceAsync(force: false, reloadOnLeastDelayChange: true);

            Assert.Equal(0, reloadCount);
        }
        finally
        {
            await Cleanup(groupId, p1, p2, p3);
        }
    }

    [Fact]
    public async Task CheckActiveGroupOnceAsync_FailoverReloadsWhenRuntimeTargetChanges()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var current = $"profile-current-{suffix}";
        var backup = $"profile-backup-{suffix}";
        var config = new Config
        {
            IndexId = current,
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.Failover,
            ActiveFailoverGroupId = groupId,
        };
        var reloadCount = 0;
        using var subscription = AppEvents.ReloadRequested
            .AsObservable()
            .Subscribe(_ => reloadCount++);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(current, ECoreType.Xray));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(backup, ECoreType.sing_box));
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = current,
                FailoverProfileId = current,
                Sort = 1,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
                LastDelay = 100,
                FailureCount = 1,
            });
            await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = backup,
                FailoverProfileId = backup,
                Sort = 2,
                Enabled = true,
                LastStatus = FailoverHealthStatus.Normal,
                LastDelay = 120,
            });

            var probe = new FakeBatchProbe([
                new(current, FailoverHealthProbeResult.Failure("request-failed")),
                new(backup, FailoverHealthProbeResult.Success(18)),
            ]);
            var service = new FailoverHealthService(config, probe);
            await service.CheckActiveGroupOnceAsync(force: false, reloadOnLeastDelayChange: true);

            Assert.Equal(1, reloadCount);
        }
        finally
        {
            await Cleanup(groupId, current, backup);
        }
    }

    [Fact]
    public async Task StartLeastDelayProbeRound_ReloadsAfterQuickSwitchWhenBestResultExists()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var p2 = $"profile-2-{suffix}";
        var config = new Config
        {
            IndexId = p1,
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.LeastDelay,
            ActiveFailoverGroupId = groupId,
            FailoverStartupProfileId = p1,
        };
        var previousRunningTarget = AppManager.Instance.RunningFailoverTargetProfileId;
        var previousRunningSignature = AppManager.Instance.RunningFailoverQueueSignature;
        AppManager.Instance.RunningFailoverTargetProfileId = null;
        AppManager.Instance.RunningFailoverQueueSignature = null;
        FailoverHealthService? service = null;
        var reloadCount = 0;
        using var subscription = AppEvents.ReloadRequested.AsObservable().Subscribe(_ => reloadCount++);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p1, 1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p2, 2));

            var probe = new BlockingProgressBatchProbe(p2, FailoverHealthProbeResult.Success(22));
            service = new FailoverHealthService(
                config,
                probe,
                quickSwitchAfter: TimeSpan.FromMilliseconds(50),
                probeRoundTimeout: TimeSpan.FromSeconds(5),
                probeLoopInterval: TimeSpan.FromSeconds(45));

            service.StartLeastDelayProbeRound();
            await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));
            await probe.PublishProgress();
            await Task.Delay(200);

            Assert.True(reloadCount >= 1);
            Assert.Equal(p2, config.FailoverStartupProfileId);

            probe.Complete();
        }
        finally
        {
            config.FailoverMode = EFailoverMode.Off;
            config.FailoverEnabled = false;
            service?.Stop(cancelCurrentProbe: true);
            await Task.Delay(100);
            AppManager.Instance.RunningFailoverTargetProfileId = previousRunningTarget;
            AppManager.Instance.RunningFailoverQueueSignature = previousRunningSignature;
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task StartLeastDelayProbeRound_ReloadsImmediatelyWhenProbeCompletesBeforeQuickSwitch()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var p2 = $"profile-2-{suffix}";
        var config = new Config
        {
            IndexId = p1,
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.LeastDelay,
            ActiveFailoverGroupId = groupId,
            FailoverStartupProfileId = p1,
        };
        var previousRunningTarget = AppManager.Instance.RunningFailoverTargetProfileId;
        var previousRunningSignature = AppManager.Instance.RunningFailoverQueueSignature;
        AppManager.Instance.RunningFailoverTargetProfileId = null;
        AppManager.Instance.RunningFailoverQueueSignature = null;
        FailoverHealthService? service = null;
        var reloadCount = 0;
        using var subscription = AppEvents.ReloadRequested.AsObservable().Subscribe(_ => reloadCount++);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p1, 1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p2, 2));

            var probe = new BlockingProgressBatchProbe(p2, FailoverHealthProbeResult.Success(18));
            service = new FailoverHealthService(
                config,
                probe,
                quickSwitchAfter: TimeSpan.FromMilliseconds(500),
                probeRoundTimeout: TimeSpan.FromSeconds(5),
                probeLoopInterval: TimeSpan.FromSeconds(45));

            service.StartLeastDelayProbeRound();
            await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));
            probe.Complete();
            await Task.Delay(150);

            Assert.True(reloadCount >= 1);
            Assert.Equal(p2, config.FailoverStartupProfileId);
        }
        finally
        {
            config.FailoverMode = EFailoverMode.Off;
            config.FailoverEnabled = false;
            service?.Stop(cancelCurrentProbe: true);
            await Task.Delay(100);
            AppManager.Instance.RunningFailoverTargetProfileId = previousRunningTarget;
            AppManager.Instance.RunningFailoverQueueSignature = previousRunningSignature;
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task StartLeastDelayProbeRound_ReloadsWhenProbeCompletesAfterQuickSwitchBeforeTimeout()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var p2 = $"profile-2-{suffix}";
        var config = new Config
        {
            IndexId = p1,
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.LeastDelay,
            ActiveFailoverGroupId = groupId,
            FailoverStartupProfileId = p1,
        };
        var previousRunningTarget = AppManager.Instance.RunningFailoverTargetProfileId;
        var previousRunningSignature = AppManager.Instance.RunningFailoverQueueSignature;
        AppManager.Instance.RunningFailoverTargetProfileId = null;
        AppManager.Instance.RunningFailoverQueueSignature = null;
        FailoverHealthService? service = null;
        var reloadCount = 0;
        using var subscription = AppEvents.ReloadRequested.AsObservable().Subscribe(_ => reloadCount++);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p1, 1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p2, 2));

            var probe = new BlockingProgressBatchProbe(p2, FailoverHealthProbeResult.Success(18));
            service = new FailoverHealthService(
                config,
                probe,
                quickSwitchAfter: TimeSpan.FromMilliseconds(20),
                probeRoundTimeout: TimeSpan.FromMilliseconds(500),
                probeLoopInterval: TimeSpan.FromSeconds(45));

            service.StartLeastDelayProbeRound();
            await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(80);
            probe.Complete();
            await Task.Delay(150);

            Assert.True(reloadCount >= 1);
            Assert.Equal(p2, config.FailoverStartupProfileId);
        }
        finally
        {
            config.FailoverMode = EFailoverMode.Off;
            config.FailoverEnabled = false;
            service?.Stop(cancelCurrentProbe: true);
            await Task.Delay(100);
            AppManager.Instance.RunningFailoverTargetProfileId = previousRunningTarget;
            AppManager.Instance.RunningFailoverQueueSignature = previousRunningSignature;
            await Cleanup(groupId, p1, p2);
        }
    }

    [Fact]
    public async Task StartLeastDelayProbeRound_DoesNotReloadWhenBestTargetAlreadyRunning()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var config = new Config
        {
            IndexId = p1,
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.LeastDelay,
            ActiveFailoverGroupId = groupId,
            FailoverStartupProfileId = p1,
        };
        var previousRunningTarget = AppManager.Instance.RunningFailoverTargetProfileId;
        var previousRunningSignature = AppManager.Instance.RunningFailoverQueueSignature;
        AppManager.Instance.RunningFailoverTargetProfileId = p1;
        AppManager.Instance.RunningFailoverQueueSignature = p1;
        FailoverHealthService? service = null;
        var reloadCount = 0;
        using var subscription = AppEvents.ReloadRequested.AsObservable().Subscribe(_ => reloadCount++);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p1, 1));

            var probe = new BlockingProgressBatchProbe(p1, FailoverHealthProbeResult.Success(22));
            service = new FailoverHealthService(
                config,
                probe,
                quickSwitchAfter: TimeSpan.FromMilliseconds(50),
                probeRoundTimeout: TimeSpan.FromSeconds(5),
                probeLoopInterval: TimeSpan.FromSeconds(45));

            service.StartLeastDelayProbeRound();
            await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));
            await probe.PublishProgress();
            await Task.Delay(200);

            Assert.Equal(0, reloadCount);

            probe.Complete();
        }
        finally
        {
            config.FailoverMode = EFailoverMode.Off;
            config.FailoverEnabled = false;
            service?.Stop(cancelCurrentProbe: true);
            await Task.Delay(100);
            AppManager.Instance.RunningFailoverTargetProfileId = previousRunningTarget;
            AppManager.Instance.RunningFailoverQueueSignature = previousRunningSignature;
            await Cleanup(groupId, p1);
        }
    }

    [Fact]
    public async Task StartLeastDelayProbeRound_MarksUnfinishedItemsFailedAfterRoundTimeout()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var config = new Config
        {
            IndexId = p1,
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.LeastDelay,
            ActiveFailoverGroupId = groupId,
            FailoverStartupProfileId = p1,
        };
        FailoverHealthService? service = null;

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p1, 1));

            var probe = new NeverCompletingBatchProbe();
            service = new FailoverHealthService(
                config,
                probe,
                quickSwitchAfter: TimeSpan.FromMilliseconds(20),
                probeRoundTimeout: TimeSpan.FromMilliseconds(80),
                probeLoopInterval: TimeSpan.FromSeconds(45));

            service.StartLeastDelayProbeRound();
            await Task.Delay(400);

            var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
                .FirstAsync(item => item.GroupId == groupId && item.SourceProfileId == p1);
            Assert.Equal(FailoverHealthStatus.Failed, stored.LastStatus);
            Assert.Equal("probe-timeout", stored.LastFailureReason);
        }
        finally
        {
            config.FailoverMode = EFailoverMode.Off;
            config.FailoverEnabled = false;
            service?.Stop(cancelCurrentProbe: true);
            await Task.Delay(100);
            await Cleanup(groupId, p1);
        }
    }

    [Fact]
    public async Task StartLeastDelayProbeRound_ReloadsAtTimeoutWhenProbeDoesNotReturn()
    {
        PrepareTables();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var p1 = $"profile-1-{suffix}";
        var p2 = $"profile-2-{suffix}";
        var config = new Config
        {
            IndexId = p1,
            FailoverEnabled = true,
            FailoverMode = EFailoverMode.LeastDelay,
            ActiveFailoverGroupId = groupId,
            FailoverStartupProfileId = p1,
        };
        var previousRunningTarget = AppManager.Instance.RunningFailoverTargetProfileId;
        var previousRunningSignature = AppManager.Instance.RunningFailoverQueueSignature;
        AppManager.Instance.RunningFailoverTargetProfileId = null;
        AppManager.Instance.RunningFailoverQueueSignature = null;
        FailoverHealthService? service = null;
        var reloadCount = 0;
        using var subscription = AppEvents.ReloadRequested.AsObservable().Subscribe(_ => reloadCount++);

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p1, 1));
            await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p2, 2));

            var probe = new UnresponsiveProgressBatchProbe(p2, FailoverHealthProbeResult.Success(18));
            service = new FailoverHealthService(
                config,
                probe,
                quickSwitchAfter: TimeSpan.FromMilliseconds(20),
                probeRoundTimeout: TimeSpan.FromMilliseconds(100),
                probeLoopInterval: TimeSpan.FromSeconds(45));

            service.StartLeastDelayProbeRound();
            await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(60);
            await probe.PublishProgress();
            await Task.Delay(200);

            Assert.True(reloadCount >= 1);
            Assert.Equal(p2, config.FailoverStartupProfileId);
        }
        finally
        {
            config.FailoverMode = EFailoverMode.Off;
            config.FailoverEnabled = false;
            service?.Stop(cancelCurrentProbe: true);
            await Task.Delay(100);
            AppManager.Instance.RunningFailoverTargetProfileId = previousRunningTarget;
            AppManager.Instance.RunningFailoverQueueSignature = previousRunningSignature;
            await Cleanup(groupId, p1, p2);
        }
    }

    private static void PrepareTables()
    {
        SQLiteHelper.Instance.CreateTable<SubItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<FailoverGroupItem>();
    }

    private static ProfileItem CreateProxy(string indexId, ECoreType coreType = ECoreType.Xray)
    {
        return new ProfileItem
        {
            IndexId = indexId,
            Remarks = indexId,
            ConfigType = EConfigType.SOCKS,
            CoreType = coreType,
            Address = "198.51.100.30",
            Port = 443,
        };
    }

    private static FailoverGroupItem CreateFailoverItem(string groupId, string profileId, int sort)
    {
        return new FailoverGroupItem
        {
            Id = Utils.GetGuid(false),
            GroupId = groupId,
            SourceProfileId = profileId,
            FailoverProfileId = profileId,
            Sort = sort,
            Enabled = true,
            LastStatus = FailoverHealthStatus.Unknown,
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

    private static Task? GetLoopTask(FailoverHealthService service)
    {
        return (Task?)typeof(FailoverHealthService)
            .GetField("_loopTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(service);
    }

    private sealed class FakeProbe(FailoverHealthProbeResult result) : IFailoverHealthProbe
    {
        public Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken)
            => Task.FromResult(result);

        public async Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeBatchAsync(
            IReadOnlyList<ProfileItem> profiles,
            CancellationToken cancellationToken,
            Func<FailoverHealthProbeProgress, Task>? updateFunc = null)
        {
            if (updateFunc is not null)
            {
                foreach (var profile in profiles)
                {
                    if (!result.Cancelled)
                    {
                        await updateFunc(new FailoverHealthProbeProgress(profile.IndexId, result));
                    }
                }
            }
            return profiles.Select(profile => new FailoverHealthProbeBatchResult(profile.IndexId, result)).ToList();
        }
    }

    private sealed class FakeBatchProbe(IReadOnlyList<FailoverHealthProbeBatchResult> results) : IFailoverHealthProbe
    {
        public int BatchCallCount { get; private set; }
        public int ProbeCallCount { get; private set; }
        public List<string> ProfileIds { get; } = [];

        public Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken)
        {
            ProbeCallCount++;
            return Task.FromResult(results.First(x => x.ProfileId == profile.IndexId).Result);
        }

        public async Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeBatchAsync(
            IReadOnlyList<ProfileItem> profiles,
            CancellationToken cancellationToken,
            Func<FailoverHealthProbeProgress, Task>? updateFunc = null)
        {
            BatchCallCount++;
            ProfileIds.AddRange(profiles.Select(x => x.IndexId));
            if (updateFunc is not null)
            {
                foreach (var result in results)
                {
                    if (!result.Result.Cancelled)
                    {
                        await updateFunc(new FailoverHealthProbeProgress(result.ProfileId, result.Result));
                    }
                }
            }
            return results;
        }
    }

    private sealed class FakeCancellingBatchProbe : IFailoverHealthProbe
    {
        public Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken)
            => Task.FromResult(FailoverHealthProbeResult.Cancel());

        public Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeBatchAsync(
            IReadOnlyList<ProfileItem> profiles,
            CancellationToken cancellationToken,
            Func<FailoverHealthProbeProgress, Task>? updateFunc = null)
            => throw new OperationCanceledException();
    }

    private sealed class RetrySuccessBatchProbe : IFailoverHealthProbe
    {
        public Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken)
            => Task.FromResult(FailoverHealthProbeResult.Success(88));

        public async Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeBatchAsync(
            IReadOnlyList<ProfileItem> profiles,
            CancellationToken cancellationToken,
            Func<FailoverHealthProbeProgress, Task>? updateFunc = null)
        {
            var profile = profiles.Single();
            await updateFunc!(new FailoverHealthProbeProgress(profile.IndexId, FailoverHealthProbeResult.Success(88)));
            return [new FailoverHealthProbeBatchResult(profile.IndexId, FailoverHealthProbeResult.Success(88))];
        }
    }

    private sealed class BlockingBatchProbe(FailoverHealthProbeResult result) : IFailoverHealthProbe
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _batchCallCount;

        public Task Started => _started.Task;
        public int BatchCallCount => Volatile.Read(ref _batchCallCount);

        public void Complete()
        {
            _complete.TrySetResult();
        }

        public Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken)
            => Task.FromResult(result);

        public async Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeBatchAsync(
            IReadOnlyList<ProfileItem> profiles,
            CancellationToken cancellationToken,
            Func<FailoverHealthProbeProgress, Task>? updateFunc = null)
        {
            Interlocked.Increment(ref _batchCallCount);
            _started.TrySetResult();
            try
            {
                await _complete.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return profiles
                    .Select(profile => new FailoverHealthProbeBatchResult(profile.IndexId, FailoverHealthProbeResult.Cancel()))
                    .ToList();
            }

            return profiles
                .Select(profile => new FailoverHealthProbeBatchResult(profile.IndexId, result))
                .ToList();
        }
    }

    private sealed class BlockingProgressBatchProbe(string progressProfileId, FailoverHealthProbeResult progressResult) : IFailoverHealthProbe
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<FailoverHealthProbeProgress, Task>? _updateFunc;

        public Task Started => _started.Task;

        public async Task PublishProgress()
        {
            if (_updateFunc is not null)
            {
                await _updateFunc(new FailoverHealthProbeProgress(progressProfileId, progressResult));
            }
        }

        public void Complete() => _complete.TrySetResult();

        public Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken)
            => Task.FromResult(progressResult);

        public async Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeBatchAsync(
            IReadOnlyList<ProfileItem> profiles,
            CancellationToken cancellationToken,
            Func<FailoverHealthProbeProgress, Task>? updateFunc = null)
        {
            _updateFunc = updateFunc;
            _started.TrySetResult();
            await _complete.Task.WaitAsync(cancellationToken);
            return profiles.Select(profile => new FailoverHealthProbeBatchResult(
                profile.IndexId,
                profile.IndexId == progressProfileId
                    ? progressResult
                    : FailoverHealthProbeResult.Failure("request-failed"))).ToList();
        }
    }

    private sealed class NeverCompletingBatchProbe : IFailoverHealthProbe
    {
        public Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken)
            => Task.FromResult(FailoverHealthProbeResult.Cancel());

        public async Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeBatchAsync(
            IReadOnlyList<ProfileItem> profiles,
            CancellationToken cancellationToken,
            Func<FailoverHealthProbeProgress, Task>? updateFunc = null)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }

    private sealed class UnresponsiveProgressBatchProbe(string progressProfileId, FailoverHealthProbeResult progressResult) : IFailoverHealthProbe
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<FailoverHealthProbeProgress, Task>? _updateFunc;

        public Task Started => _started.Task;

        public async Task PublishProgress()
        {
            if (_updateFunc is not null)
            {
                await _updateFunc(new FailoverHealthProbeProgress(progressProfileId, progressResult));
            }
        }

        public Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken)
            => Task.FromResult(FailoverHealthProbeResult.Cancel());

        public async Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeBatchAsync(
            IReadOnlyList<ProfileItem> profiles,
            CancellationToken cancellationToken,
            Func<FailoverHealthProbeProgress, Task>? updateFunc = null)
        {
            _updateFunc = updateFunc;
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return [];
        }
    }
}
