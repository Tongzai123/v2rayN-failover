# 延迟转移响应性实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**目标:** 让 `延迟转移` 开启后立即连接上一次实际连接节点并启动核心，同时支持 7 秒快速切换、15 秒硬截止、可立即关闭和探测间隔整体增加 15 秒。

**架构:** 将探测时间参数集中到一个健康探测时序类，避免 7 秒、15 秒、45 秒和冷却时间散落。用 `Config` 上的 `[JsonIgnore]` 运行期字段承载“首次启动目标”，让核心配置构建在 `LeastDelay` 启动期暂时使用上一次实际连接节点；探测进度仍落库，7 秒和 15 秒节点切换只从活动转移组已启用队列中选取。用 `AppManager` 记录核心实际运行的转移目标，保证“活动”标签滞后于核心重载成功。

**技术栈:** C#、.NET 8、xUnit、ReactiveUI、SQLite、Avalonia、ServiceLib。

---

## 文件结构

- 修改 `v2rayN/ServiceLib/Manager/FailoverHealthStateMachine.cs`：失败状态语义和冷却间隔。
- 新增 `v2rayN/ServiceLib/Manager/FailoverHealthTiming.cs`：集中定义 7 秒、15 秒、45 秒和冷却时间。
- 修改 `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`：最近探测间隔过滤、首次启动目标覆盖、最低延迟目标解析。
- 修改 `v2rayN/ServiceLib/Models/Config.cs`：增加 `[JsonIgnore]` 运行期首次启动目标字段。
- 修改 `v2rayN/ServiceLib/Models/CoreConfigContext.cs`：记录本次核心配置实际转移目标。
- 修改 `v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`：把运行态转移目标写入 `CoreConfigContext`。
- 修改 `v2rayN/ServiceLib/Manager/AppManager.cs`：记录当前核心实际运行的转移目标。
- 修改 `v2rayN/ServiceLib/Manager/CoreManager.cs`：核心启动成功后更新实际运行目标。
- 修改 `v2rayN/ServiceLib/Services/FailoverHealthService.cs`：非阻塞启动、7 秒快速决策、15 秒硬截止、关闭取消与清理。
- 修改 `v2rayN/ServiceLib/ViewModels/MsgViewModel.cs`：开启 `LeastDelay` 不再等待强制探测；关闭时取消当前探测并清理 `Probing`。
- 修改 `v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`：活动标签使用核心实际运行目标。
- 修改 `v2rayN/ServiceLib/Handler/ConnectionHandler.cs`：两次 HTTP 探测中只有一次成功时，超时前保留该次成功结果。
- 修改测试文件：
  - `v2rayN/ServiceLib.Tests/FailoverHealthStateMachineTests.cs`
  - `v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`
  - `v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`
  - `v2rayN/ServiceLib.Tests/MsgViewModelFailoverModeTests.cs`
  - `v2rayN/ServiceLib.Tests/FailoverHealthDisplayTests.cs`
- 新增 `_local_changes/018-failover-responsive-least-delay/`：记录本地实质改动。

---

### 任务 1：集中健康探测时序并修正失败状态语义

**文件:**
- 新增：`v2rayN/ServiceLib/Manager/FailoverHealthTiming.cs`
- 修改：`v2rayN/ServiceLib/Manager/FailoverHealthStateMachine.cs`
- 测试：`v2rayN/ServiceLib.Tests/FailoverHealthStateMachineTests.cs`

- [ ] **步骤 1：写失败测试**

在 `v2rayN/ServiceLib.Tests/FailoverHealthStateMachineTests.cs` 中替换失败和冷却测试，新增第一次失败直接进入 `Failed` 的测试：

```csharp
[Fact]
public void ApplyProbeFailure_FirstFailureEntersFailedWithCooldown()
{
    var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    var item = new FailoverGroupItem
    {
        LastStatus = FailoverHealthStatus.Probing,
        FailureCount = 0,
    };

    FailoverHealthStateMachine.ApplyProbeResult(item, FailoverHealthProbeResult.Failure("request-failed"), now);

    Assert.Equal(FailoverHealthStatus.Failed, item.LastStatus);
    Assert.Equal(1, item.FailureCount);
    Assert.Equal(now + 75_000, item.CooldownUntilTime);
    Assert.Equal(now, item.LastFailureTime);
    Assert.Equal("request-failed", item.LastFailureReason);
}

[Theory]
[InlineData(1, 75_000)]
[InlineData(2, 75_000)]
[InlineData(3, 135_000)]
[InlineData(4, 315_000)]
[InlineData(8, 315_000)]
public void GetCooldownMilliseconds_UsesBoundedBackoffPlusFifteenSeconds(int failureCount, long expected)
{
    Assert.Equal(expected, FailoverHealthStateMachine.GetCooldownMilliseconds(failureCount));
}
```

同时把现有 `ApplyProbeFailure_SecondFailureEntersFailedWithCooldown` 中的 `now + 60_000` 改成 `now + 75_000`。

- [ ] **步骤 2：运行测试确认失败**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverHealthStateMachineTests --no-restore
```

期望：失败，至少包含 `Assert.Equal() Failure`，实际冷却仍是 `60_000/120_000/300_000`，第一次失败仍可能停留在 `Probing`。

- [ ] **步骤 3：新增时序类**

新增 `v2rayN/ServiceLib/Manager/FailoverHealthTiming.cs`：

```csharp
namespace ServiceLib.Manager;

public static class FailoverHealthTiming
{
    public static readonly TimeSpan ProbeLoopInterval = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan LeastDelayQuickSwitchAfter = TimeSpan.FromSeconds(7);
    public static readonly TimeSpan ProbeRoundTimeout = TimeSpan.FromSeconds(15);

    public static long GetCooldownMilliseconds(int failureCount)
    {
        if (failureCount <= 2)
        {
            return 75_000;
        }
        if (failureCount == 3)
        {
            return 135_000;
        }
        return 315_000;
    }
}
```

- [ ] **步骤 4：修改状态机**

在 `v2rayN/ServiceLib/Manager/FailoverHealthStateMachine.cs` 中把失败分支改为第一次失败即写入 `Failed`，并改用集中冷却时间：

```csharp
item.FailureCount++;
item.LastFailureTime = now;
item.LastFailureReason = result.FailureReason;
item.LastStatus = FailoverHealthStatus.Failed;
item.CooldownUntilTime = now + GetCooldownMilliseconds(item.FailureCount);
```

把 `GetCooldownMilliseconds` 改成：

```csharp
public static long GetCooldownMilliseconds(int failureCount)
{
    return FailoverHealthTiming.GetCooldownMilliseconds(failureCount);
}
```

- [ ] **步骤 5：运行测试确认通过**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverHealthStateMachineTests --no-restore
```

期望：`FailoverHealthStateMachineTests` 全部通过。

- [ ] **步骤 6：提交**

```powershell
git add v2rayN/ServiceLib/Manager/FailoverHealthTiming.cs v2rayN/ServiceLib/Manager/FailoverHealthStateMachine.cs v2rayN/ServiceLib.Tests/FailoverHealthStateMachineTests.cs
git commit -m "调整转移健康探测状态语义"
```

---

### 任务 2：让正常、未知和探测中节点也遵守 45 秒探测间隔

**文件:**
- 修改：`v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- 测试：`v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`

- [ ] **步骤 1：写失败测试**

在 `FailoverGroupManagerTests` 中新增：

```csharp
[Fact]
public async Task GetDueHealthCheckEntries_SkipsRecentlyProbedItemsForAllStatuses()
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

        Assert.Equal([oldNormal, oldProbing], entries.Select(entry => entry.Profile.IndexId).ToArray());
    }
    finally
    {
        await Cleanup(groupId, recentNormal, oldNormal, recentUnknown, oldProbing);
    }
}
```

- [ ] **步骤 2：运行测试确认失败**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter GetDueHealthCheckEntries_SkipsRecentlyProbedItemsForAllStatuses --no-restore
```

期望：失败，返回结果仍包含最近 45 秒内探测过的节点。

- [ ] **步骤 3：实现间隔过滤**

在 `FailoverGroupManager.GetDueHealthCheckEntries` 中替换过滤逻辑：

```csharp
return entries
    .Where(entry => IsDueForHealthCheck(entry.Item, now))
    .ToList();
```

在同文件添加私有方法：

```csharp
private static bool IsDueForHealthCheck(FailoverGroupItem item, long now)
{
    if (item.LastProbeTime.HasValue
        && now - item.LastProbeTime.Value < (long)FailoverHealthTiming.ProbeLoopInterval.TotalMilliseconds)
    {
        return false;
    }

    if (item.LastStatus == FailoverHealthStatus.Failed
        && item.CooldownUntilTime.HasValue
        && item.CooldownUntilTime.Value > now)
    {
        return false;
    }

    return true;
}
```

- [ ] **步骤 4：运行相关测试**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "GetDueHealthCheckEntries|FailoverHealthStateMachineTests" --no-restore
```

期望：相关测试通过。

- [ ] **步骤 5：提交**

```powershell
git add v2rayN/ServiceLib/Manager/FailoverGroupManager.cs v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs
git commit -m "增加转移健康探测最小间隔"
```

---

### 任务 3：支持延迟转移首次启动目标覆盖

**文件:**
- 修改：`v2rayN/ServiceLib/Models/Config.cs`
- 修改：`v2rayN/ServiceLib/Models/CoreConfigContext.cs`
- 修改：`v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- 修改：`v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`
- 测试：`v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`

- [ ] **步骤 1：写失败测试**

在 `FailoverGroupManagerTests` 中新增测试，证明 `LeastDelay` 启动期优先使用上一次实际连接节点：

```csharp
[Fact]
public async Task CoreConfigContextBuilder_Build_LeastDelayStartupProfileKeepsPreviousNode()
{
    PrepareTables();
    var suffix = Utils.GetGuid(false);
    var groupId = $"failover-{suffix}";
    var routeId = $"route-{suffix}";
    var previous = CreateProxy($"previous-{suffix}", "source-sub", ECoreType.Xray);
    var fast = CreateProxy($"fast-{suffix}", "source-sub", ECoreType.Xray);

    try
    {
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
        await SQLiteHelper.Instance.ReplaceAsync(previous);
        await SQLiteHelper.Instance.ReplaceAsync(fast);
        var fastItem = CreateFailoverItem(groupId, fast.IndexId, 1);
        fastItem.LastStatus = FailoverHealthStatus.Normal;
        fastItem.LastDelay = 10;
        await SQLiteHelper.Instance.ReplaceAsync(fastItem);
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

        var result = await CoreConfigContextBuilder.Build(config, previous);

        Assert.True(result.Success, string.Join(" | ", result.ValidatorResult.Errors));
        Assert.Equal(previous.IndexId, result.Context.Node.IndexId);
        Assert.Equal(previous.IndexId, result.Context.FailoverRuntimeTargetProfileId);
    }
    finally
    {
        await Cleanup(groupId, previous.IndexId, fast.IndexId);
        await SQLiteHelper.Instance.ExecuteAsync($"delete from RoutingItem where Id = '{routeId}'");
    }
}
```

- [ ] **步骤 2：运行测试确认失败**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter CoreConfigContextBuilder_Build_LeastDelayStartupProfileKeepsPreviousNode --no-restore
```

期望：编译失败或断言失败，因为 `FailoverStartupProfileId` 与 `FailoverRuntimeTargetProfileId` 尚不存在。

- [ ] **步骤 3：在配置中增加运行期字段**

修改 `Config.cs`：

```csharp
[JsonIgnore]
public string? FailoverStartupProfileId { get; set; }
```

字段放在 `ActiveFailoverGroupId` 后面。`JsonIgnore` 已在 `GlobalUsings.cs` 中全局引入，确保该字段不写入 `guiNConfig.json`。

- [ ] **步骤 4：在 CoreConfigContext 记录转移目标**

修改 `CoreConfigContext.cs`：

```csharp
public string? FailoverRuntimeTargetProfileId { get; init; }
```

- [ ] **步骤 5：让运行态解析支持启动期覆盖**

在 `FailoverGroupManager.TryResolveRuntimeNode` 中，取到 `queueEntries` 后、判断 `CanUseCoreFallback` 前加入：

```csharp
if (config.FailoverMode == EFailoverMode.LeastDelay
    && config.FailoverStartupProfileId.IsNotEmpty())
{
    var startupProfile = await AppManager.Instance.GetProfileItem(config.FailoverStartupProfileId);
    if (startupProfile != null)
    {
        var startupCoreType = AppManager.Instance.GetCoreType(startupProfile, startupProfile.ConfigType);
        var startupValidation = NodeValidator.Validate(startupProfile, startupCoreType);
        if (startupValidation.Success)
        {
            return new(
                FailoverRuntimeKind.DirectProfile,
                startupProfile,
                queueEntries.Select(entry => entry.Profile).ToList(),
                startupProfile.IndexId,
                validator);
        }
    }
}
```

- [ ] **步骤 6：把解析目标写入 CoreConfigContext**

在 `CoreConfigContextBuilder.Build` 中声明局部变量：

```csharp
string? failoverRuntimeTargetProfileId = null;
```

在 `TryResolveRuntimeNode` 成功后赋值：

```csharp
failoverRuntimeTargetProfileId = resolveResult.RuntimeTargetProfileId;
```

最终返回前把 context 更新为：

```csharp
context = context with { Node = actNode, FailoverRuntimeTargetProfileId = failoverRuntimeTargetProfileId };
```

- [ ] **步骤 7：运行测试确认通过**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "CoreConfigContextBuilder_Build_LeastDelayStartupProfileKeepsPreviousNode|CoreConfigContextBuilder_Build_MixedCoreQueueUsesResolvedDirectProfile|TryResolveRuntimeNode" --no-restore
```

期望：相关测试通过。

- [ ] **步骤 8：提交**

```powershell
git add v2rayN/ServiceLib/Models/Config.cs v2rayN/ServiceLib/Models/CoreConfigContext.cs v2rayN/ServiceLib/Manager/FailoverGroupManager.cs v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs
git commit -m "支持延迟转移启动目标覆盖"
```

---

### 任务 4：核心启动成功后记录实际转移目标，活动标签只信实际运行目标

**文件:**
- 修改：`v2rayN/ServiceLib/Manager/AppManager.cs`
- 修改：`v2rayN/ServiceLib/Manager/CoreManager.cs`
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
- 测试：`v2rayN/ServiceLib.Tests/FailoverHealthDisplayTests.cs`

- [ ] **步骤 1：写失败测试**

在 `FailoverHealthDisplayTests` 中新增：

```csharp
[Fact]
public void ApplyFailoverHealthDisplay_DoesNotShowActiveWhenRuntimeTargetDoesNotMatch()
{
    var item = new ProfileItemModel
    {
        ShowFailoverHealthLabel = true,
        IsCurrentFailoverPreferred = false,
        FailoverHealthStatus = FailoverHealthStatus.Normal,
    };

    ProfilesViewModel.ApplyFailoverHealthDisplay(item);

    Assert.False(item.ShowCurrentFailoverActiveLabel);
    Assert.True(item.IsFailoverHealthNormal);
}
```

在已有 `ApplyFailoverHealthDisplay_ShowsActiveLabelForCurrentNormalFailoverNode` 测试中，保留 `IsCurrentFailoverPreferred = true`，确保实际目标匹配时仍显示活动。

- [ ] **步骤 2：运行测试确认当前行为边界**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverHealthDisplayTests --no-restore
```

期望：新增测试应通过；该测试锁定 `ApplyFailoverHealthDisplay` 的输入语义，后面的实现只改变 `IsCurrentFailoverPreferred` 的赋值来源。

- [ ] **步骤 3：记录实际运行转移目标**

在 `AppManager.cs` 的 `RunningCoreType` 附近新增：

```csharp
public string? RunningFailoverTargetProfileId { get; set; }
```

在 `CoreManager.LoadCore` 中，`AppManager.Instance.RunningCoreType = ...` 后加入：

```csharp
AppManager.Instance.RunningFailoverTargetProfileId = _processService != null
    ? mainContext.FailoverRuntimeTargetProfileId
    : null;
```

- [ ] **步骤 4：ProfilesViewModel 使用实际运行目标**

在 `ProfilesViewModel` 构造 `ProfileItemModel` 时，把 `IsCurrentFailoverPreferred` 的条件改成：

```csharp
IsCurrentFailoverPreferred = isFailoverGroup
    && _config.FailoverMode != EFailoverMode.Off
    && _config.ActiveFailoverGroupId == subid
    && AppManager.Instance.RunningFailoverTargetProfileId == t.IndexId,
```

- [ ] **步骤 5：运行显示测试和编译**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FailoverHealthDisplayTests|DesktopFailoverModeStyleTests" --no-restore
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

期望：测试通过；Desktop 编译通过。

- [ ] **步骤 6：提交**

```powershell
git add v2rayN/ServiceLib/Manager/AppManager.cs v2rayN/ServiceLib/Manager/CoreManager.cs v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs v2rayN/ServiceLib.Tests/FailoverHealthDisplayTests.cs
git commit -m "按核心实际目标显示转移活动标签"
```

---

### 任务 5：让关闭转移模式立即取消探测并清理 Probing

**文件:**
- 修改：`v2rayN/ServiceLib/Services/FailoverHealthService.cs`
- 修改：`v2rayN/ServiceLib/ViewModels/MsgViewModel.cs`
- 测试：`v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`

- [ ] **步骤 1：写失败测试**

在 `FailoverHealthServiceTests` 中新增：

```csharp
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
        FailoverMode = EFailoverMode.LeastDelay,
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
```

- [ ] **步骤 2：运行测试确认失败**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter StopAndClearProbingAsync_CancelsProbeAndMarksProbingUnknown --no-restore
```

期望：编译失败，因为 `StopAndClearProbingAsync` 尚不存在。

- [ ] **步骤 3：实现清理方法**

在 `FailoverHealthService` 中新增：

```csharp
public async Task StopAndClearProbingAsync()
{
    Stop(cancelCurrentProbe: true);
    await ResetActiveGroupProbingToUnknownAsync();
}

private async Task ResetActiveGroupProbingToUnknownAsync()
{
    if (_config.ActiveFailoverGroupId.IsNullOrEmpty())
    {
        return;
    }

    var probingItems = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
        .Where(item => item.GroupId == _config.ActiveFailoverGroupId
            && item.Enabled
            && item.LastStatus == FailoverHealthStatus.Probing)
        .ToListAsync();
    foreach (var item in probingItems)
    {
        item.LastStatus = FailoverHealthStatus.Unknown;
    }
    if (probingItems.Count > 0)
    {
        await SQLiteHelper.Instance.UpdateAllAsync(probingItems);
        AppEvents.FailoverHealthChangedRequested.Publish();
    }
}
```

在 `CheckActiveGroupOnceAsync` 的取消结果处理里，当 `_config.FailoverMode == EFailoverMode.Off` 时保持 `Unknown`，不要恢复 `PreviousStatus`：

```csharp
if (result.Cancelled)
{
    entry.Item.LastStatus = _config.FailoverMode == EFailoverMode.Off
        ? FailoverHealthStatus.Unknown
        : probeEntry.PreviousStatus;
    continue;
}
```

- [ ] **步骤 4：修改 MsgViewModel 关闭分支**

在 `MsgViewModel.ChangeFailoverMode` 的 `Off` 分支中，把：

```csharp
FailoverHealthService.Instance.Stop();
```

替换成：

```csharp
await FailoverHealthService.Instance.StopAndClearProbingAsync();
```

在 `DisableFailover()` 中做同样替换。

- [ ] **步骤 5：运行测试**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "StopAndClearProbingAsync|StopCancelCurrentProbe|MsgViewModelFailoverModeTests" --no-restore
```

期望：相关测试通过；如果旧 `StopCancelCurrentProbe_RestoresPreviousStatus` 仍断言恢复 `Normal`，将该旧测试改名为 `StopCancelCurrentProbe_KeepsPreviousStatusWhenModeStillEnabled`，并保持配置 `FailoverMode = Failover`。

- [ ] **步骤 6：提交**

```powershell
git add v2rayN/ServiceLib/Services/FailoverHealthService.cs v2rayN/ServiceLib/ViewModels/MsgViewModel.cs v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs
git commit -m "关闭转移时取消探测并清理探测中状态"
```

---

### 任务 6：开启 LeastDelay 时立即重载上一次实际连接节点并非阻塞启动探测

**文件:**
- 修改：`v2rayN/ServiceLib/ViewModels/MsgViewModel.cs`
- 修改：`v2rayN/ServiceLib/Services/FailoverHealthService.cs`
- 测试：`v2rayN/ServiceLib.Tests/MsgViewModelFailoverModeTests.cs`
- 测试：`v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`

- [ ] **步骤 1：为启动字段写静态测试**

在 `MsgViewModelFailoverModeTests` 中新增：

```csharp
[Fact]
public void PrepareLeastDelayStartup_SetsStartupProfileToCurrentIndexId()
{
    var config = new Config
    {
        IndexId = "previous-node",
        FailoverMode = EFailoverMode.Off,
        FailoverEnabled = false,
    };

    MsgViewModel.PrepareLeastDelayStartup(config);

    Assert.Equal(EFailoverMode.LeastDelay, config.FailoverMode);
    Assert.True(config.FailoverEnabled);
    Assert.Equal("previous-node", config.FailoverStartupProfileId);
}
```

- [ ] **步骤 2：运行测试确认失败**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter PrepareLeastDelayStartup_SetsStartupProfileToCurrentIndexId --no-restore
```

期望：编译失败，因为静态方法不存在。

- [ ] **步骤 3：实现启动准备方法**

在 `MsgViewModel` 中新增：

```csharp
public static void PrepareLeastDelayStartup(Config config)
{
    config.FailoverMode = EFailoverMode.LeastDelay;
    config.FailoverEnabled = true;
    config.FailoverStartupProfileId = config.IndexId;
}
```

在 `ChangeFailoverMode` 中，`mode == EFailoverMode.LeastDelay` 时用该方法；`Failover` 模式继续按原逻辑设置：

```csharp
if (mode == EFailoverMode.LeastDelay)
{
    PrepareLeastDelayStartup(_config);
}
else
{
    _config.FailoverMode = mode;
    _config.FailoverEnabled = true;
    _config.FailoverStartupProfileId = null;
}
```

- [ ] **步骤 4：把强制探测改为后台启动**

把 `ChangeFailoverMode` 中原来的：

```csharp
if (mode == EFailoverMode.LeastDelay)
{
    await FailoverHealthService.Instance.CheckActiveGroupOnceAsync(
        force: true,
        reloadOnLeastDelayChange: false);
}
```

替换成：

```csharp
if (mode == EFailoverMode.LeastDelay)
{
    FailoverHealthService.Instance.StartLeastDelayProbeRound();
}
```

确保 `NoticeManager.Instance.Enqueue`、`AppEvents.ReloadRequested.Publish()`、`AppEvents.FailoverStateChangedRequested.Publish()` 在后台探测完成前执行。

- [ ] **步骤 5：在服务中添加后台入口**

在 `FailoverHealthService` 中新增：

```csharp
public void StartLeastDelayProbeRound()
{
    Start();
    _ = Task.Run(async () =>
    {
        await RunLeastDelayProbeRoundAsync();
    });
}
```

先让 `RunLeastDelayProbeRoundAsync` 只调用：

```csharp
private async Task RunLeastDelayProbeRoundAsync()
{
    await CheckActiveGroupOnceAsync(force: true, reloadOnLeastDelayChange: false);
}
```

本任务只建立后台入口，确保 UI 命令不再等待探测；7 秒快速切换和 15 秒硬截止分别由任务 7 和任务 8 实现。

- [ ] **步骤 6：运行当前测试**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "PrepareLeastDelayStartup|MsgViewModelFailoverModeTests" --no-restore
```

期望：测试通过。

- [ ] **步骤 7：提交**

```powershell
git add v2rayN/ServiceLib/ViewModels/MsgViewModel.cs v2rayN/ServiceLib/Services/FailoverHealthService.cs v2rayN/ServiceLib.Tests/MsgViewModelFailoverModeTests.cs
git commit -m "延迟转移开启时立即使用上次连接节点"
```

---

### 任务 7：实现 7 秒快速切换

**文件:**
- 修改：`v2rayN/ServiceLib/Services/FailoverHealthService.cs`
- 修改：`v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- 测试：`v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`

- [ ] **步骤 1：写最低延迟目标解析测试**

在 `FailoverGroupManagerTests` 中新增：

```csharp
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
```

- [ ] **步骤 2：实现目标解析方法**

在 `FailoverGroupManager` 中新增：

```csharp
public static async Task<string?> GetLeastDelayRuntimeTargetProfileId(string groupId)
{
    return (await GetQueueEntries(groupId, true))
        .Where(entry => entry.Item.LastStatus == FailoverHealthStatus.Normal && entry.Item.LastDelay > 0)
        .OrderBy(entry => entry.Item.LastDelay)
        .ThenBy(entry => entry.Item.Sort)
        .Select(entry => entry.Profile.IndexId)
        .FirstOrDefault();
}
```

- [ ] **步骤 3：写 7 秒切换服务测试**

在 `FailoverHealthServiceTests` 中新增使用短时间参数的内部构造重载。先写测试：

```csharp
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
        var service = new FailoverHealthService(
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
        Assert.Null(config.FailoverStartupProfileId);

        probe.Complete();
    }
    finally
    {
        await Cleanup(groupId, p1, p2);
    }
}
```

在测试文件底部新增 fake：

```csharp
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
        return profiles.Select(profile => new FailoverHealthProbeBatchResult(profile.IndexId,
            profile.IndexId == progressProfileId ? progressResult : FailoverHealthProbeResult.Failure("request-failed"))).ToList();
    }
}
```

- [ ] **步骤 4：实现测试用构造重载和 7 秒逻辑**

给 `FailoverHealthService` 添加字段和内部构造重载：

```csharp
private readonly TimeSpan _quickSwitchAfter;
private readonly TimeSpan _probeRoundTimeout;
private readonly TimeSpan _probeLoopInterval;

internal FailoverHealthService(
    Config config,
    IFailoverHealthProbe probe,
    TimeSpan quickSwitchAfter,
    TimeSpan probeRoundTimeout,
    TimeSpan probeLoopInterval)
    : this(config, probe)
{
    _quickSwitchAfter = quickSwitchAfter;
    _probeRoundTimeout = probeRoundTimeout;
    _probeLoopInterval = probeLoopInterval;
}
```

在公开构造函数中初始化：

```csharp
_quickSwitchAfter = FailoverHealthTiming.LeastDelayQuickSwitchAfter;
_probeRoundTimeout = FailoverHealthTiming.ProbeRoundTimeout;
_probeLoopInterval = FailoverHealthTiming.ProbeLoopInterval;
```

在 `RunLeastDelayProbeRoundAsync` 中并行启动探测和快速切换：

```csharp
private async Task RunLeastDelayProbeRoundAsync()
{
    using var timeoutCts = new CancellationTokenSource(_probeRoundTimeout);
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
        _probeCts?.Token ?? CancellationToken.None,
        timeoutCts.Token);

    var probeTask = CheckActiveGroupOnceAsync(force: true, reloadOnLeastDelayChange: false, linkedCts.Token);
    var quickTask = Task.Run(async () =>
    {
        await Task.Delay(_quickSwitchAfter, linkedCts.Token);
        await ReloadLeastDelayBestTargetIfAvailable();
    }, linkedCts.Token);

    await Task.WhenAny(probeTask, quickTask);
    await probeTask;
    await ReloadLeastDelayBestTargetIfAvailable();
}
```

新增：

```csharp
private async Task ReloadLeastDelayBestTargetIfAvailable()
{
    if (_config.FailoverMode != EFailoverMode.LeastDelay || _config.ActiveFailoverGroupId.IsNullOrEmpty())
    {
        return;
    }

    var target = await FailoverGroupManager.GetLeastDelayRuntimeTargetProfileId(_config.ActiveFailoverGroupId);
    if (target.IsNullOrEmpty())
    {
        return;
    }

    if (_config.FailoverStartupProfileId == target)
    {
        return;
    }

    _config.FailoverStartupProfileId = null;
    AppEvents.ReloadRequested.Publish();
}
```

- [ ] **步骤 5：运行测试**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "GetLeastDelayRuntimeTargetProfileId|StartLeastDelayProbeRound_ReloadsAfterQuickSwitchWhenBestResultExists" --no-restore
```

期望：测试通过。

- [ ] **步骤 6：提交**

```powershell
git add v2rayN/ServiceLib/Services/FailoverHealthService.cs v2rayN/ServiceLib/Manager/FailoverGroupManager.cs v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs
git commit -m "实现延迟转移七秒快速切换"
```

---

### 任务 8：实现 15 秒硬截止和超时失败

**文件:**
- 修改：`v2rayN/ServiceLib/Services/FailoverHealthService.cs`
- 修改：`v2rayN/ServiceLib/Handler/ConnectionHandler.cs`
- 测试：`v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`

- [ ] **步骤 1：写硬截止测试**

在 `FailoverHealthServiceTests` 中新增：

```csharp
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

    try
    {
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
        await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
        await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, p1, 1));

        var probe = new NeverCompletingBatchProbe();
        var service = new FailoverHealthService(
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
        await Cleanup(groupId, p1);
    }
}
```

新增 fake：

```csharp
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
```

- [ ] **步骤 2：运行测试确认失败**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter StartLeastDelayProbeRound_MarksUnfinishedItemsFailedAfterRoundTimeout --no-restore
```

期望：失败，取消结果不会写入 `probe-timeout`。

- [ ] **步骤 3：让超时取消转成失败**

给 `CheckActiveGroupOnceAsync` 增加可选参数：

```csharp
bool cancellationMeansTimeout = false
```

在 `ProbeAsync` 增加同名参数。`OperationCanceledException` 分支改成：

```csharp
return profiles
    .GroupBy(x => x.IndexId)
    .ToDictionary(
        group => group.Key,
        _ => cancellationMeansTimeout
            ? FailoverHealthProbeResult.Failure("probe-timeout")
            : FailoverHealthProbeResult.Cancel());
```

`RunLeastDelayProbeRoundAsync` 在 `timeoutCts` 触发后调用：

```csharp
await CheckActiveGroupOnceAsync(
    force: true,
    reloadOnLeastDelayChange: false,
    linkedCts.Token,
    cancellationMeansTimeout: timeoutCts.IsCancellationRequested
        && !(_probeCts?.IsCancellationRequested ?? false));
```

在 `RunLeastDelayProbeRoundAsync` 中固定使用局部函数判断本轮取消是否来自硬截止：

```csharp
bool IsRoundTimeout() => timeoutCts.IsCancellationRequested && !(_probeCts?.IsCancellationRequested ?? false);
```

- [ ] **步骤 4：保留一次成功 HTTP 结果**

在 `ConnectionHandler.GetRealPingTime` 中把两次请求循环改为逐次捕获取消。核心规则：如果第一轮已成功，第二轮因超时或取消失败，返回第一轮成功值；如果没有任何成功，则返回 `-1`。

替换循环为：

```csharp
List<int> oneTime = new();
for (var i = 0; i < 2; i++)
{
    try
    {
        var timer = Stopwatch.StartNew();
        await client.GetAsync(url, cts.Token).ConfigureAwait(false);
        timer.Stop();
        oneTime.Add((int)timer.Elapsed.TotalMilliseconds);
        if (i < 1)
        {
            await Task.Delay(100, cts.Token);
        }
    }
    catch (OperationCanceledException) when (oneTime.Count > 0)
    {
        break;
    }
}
var elapsed = oneTime.Where(x => x > 0).OrderBy(x => x).FirstOrDefault();
responseTime = elapsed == 0 ? -1 : elapsed;
```

- [ ] **步骤 5：运行测试**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "StartLeastDelayProbeRound_MarksUnfinishedItemsFailedAfterRoundTimeout|CheckOnceAsync_CancelledProbeDoesNotMarkFailed" --no-restore
```

期望：超时写 `Failed/probe-timeout`，用户取消仍不写失败。

- [ ] **步骤 6：提交**

```powershell
git add v2rayN/ServiceLib/Services/FailoverHealthService.cs v2rayN/ServiceLib/Handler/ConnectionHandler.cs v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs
git commit -m "实现延迟转移十五秒探测截止"
```

---

### 任务 9：用 45 秒循环间隔替换后台固定 30 秒

**文件:**
- 修改：`v2rayN/ServiceLib/Services/FailoverHealthService.cs`
- 测试：`v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`

- [ ] **步骤 1：写可测试间隔断言**

在 `FailoverHealthServiceTests` 中新增：

```csharp
[Fact]
public void DefaultProbeLoopInterval_IsFortyFiveSeconds()
{
    Assert.Equal(TimeSpan.FromSeconds(45), FailoverHealthTiming.ProbeLoopInterval);
}
```

- [ ] **步骤 2：运行测试确认常量存在并通过**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter DefaultProbeLoopInterval_IsFortyFiveSeconds --no-restore
```

期望：通过。

- [ ] **步骤 3：替换后台循环延迟**

在 `FailoverHealthService.RunLoopAsync` 中把：

```csharp
await Task.Delay(TimeSpan.FromSeconds(30), token);
```

替换为：

```csharp
await Task.Delay(_probeLoopInterval, token);
```

- [ ] **步骤 4：运行服务测试**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverHealthServiceTests --no-restore
```

期望：服务测试通过。

- [ ] **步骤 5：提交**

```powershell
git add v2rayN/ServiceLib/Services/FailoverHealthService.cs v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs
git commit -m "调整转移后台探测循环间隔"
```

---

### 任务 10：补本地改动记录

**文件:**
- 新增：`_local_changes/018-failover-responsive-least-delay/README.md`
- 新增：`_local_changes/018-failover-responsive-least-delay/files.md`
- 新增：`_local_changes/018-failover-responsive-least-delay/reapply.md`
- 新增：`_local_changes/018-failover-responsive-least-delay/tests.md`
- 新增：`_local_changes/018-failover-responsive-least-delay/patch.diff`
- 修改：`_local_changes/README.md`

- [ ] **步骤 1：创建记录目录**

```powershell
Copy-Item -Recurse -LiteralPath '_local_changes\_template' -Destination '_local_changes\018-failover-responsive-least-delay'
```

- [ ] **步骤 2：填写 README.md**

写入以下结构：

```markdown
# 延迟转移响应性与探测超时

## 基本信息

- 编号：`018`
- 目录：`_local_changes/018-failover-responsive-least-delay/`
- 创建日期：`2026-05-14`
- 适用源码版本：本地 `v2rayN-7.20.4` 改造副本
- 改动类型：行为修复 / 用户体验 / 代理安全
- 状态：已实现，待验证

## 改动目的

开启 `延迟转移` 后立即连接上一次实际连接节点并启动核心，避免按钮和代理服务被整轮探测阻塞；同时增加 7 秒快速切换、15 秒硬截止、关闭即取消探测和探测间隔整体增加 15 秒。

## 用户可见行为

- `延迟转移` 按钮立即变绿。
- 代理服务立即连接到上一次实际连接节点。
- 探测过程中可以关闭，关闭后残留 `探测中` 变为 `未知`。
- 7 秒左右按已成功探测结果切换到最低延迟节点。
- 15 秒后未出结果的节点判定失败。
- 第一轮结束后不会马上开启第二轮。

## 代理安全影响评估

本改动影响转移模式核心重载时机和活动节点选择。初始目标必须是进入 `延迟转移` 前已可用节点或可验证的队列兜底节点；7 秒和 15 秒切换目标必须来自活动转移组已启用队列。无有效候选时回退关闭，不允许静默直连。探测继续使用 `suppressFailover: true`，不扩大 DNS、TLS、证书、订阅或更新安全边界。
```

- [ ] **步骤 3：填写 files.md、reapply.md、tests.md**

`files.md` 写入：

```markdown
# 文件变更

## 修改文件

| 文件 | 说明 |
| --- | --- |
| `v2rayN/ServiceLib/Manager/FailoverHealthStateMachine.cs` | 调整失败状态语义，非取消失败立即进入 `Failed` 并使用增加 15 秒后的冷却时间。 |
| `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs` | 增加最近探测间隔过滤、延迟转移最低延迟目标解析和启动目标覆盖支持。 |
| `v2rayN/ServiceLib/Models/Config.cs` | 增加 `[JsonIgnore]` 运行期首次启动目标字段。 |
| `v2rayN/ServiceLib/Models/CoreConfigContext.cs` | 记录核心配置实际转移目标。 |
| `v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs` | 将转移运行态目标写入配置上下文。 |
| `v2rayN/ServiceLib/Manager/AppManager.cs` | 记录当前核心实际运行的转移目标。 |
| `v2rayN/ServiceLib/Manager/CoreManager.cs` | 核心启动成功后更新实际运行目标。 |
| `v2rayN/ServiceLib/Services/FailoverHealthService.cs` | 实现非阻塞开启、7 秒快速切换、15 秒硬截止、关闭取消和探测状态清理。 |
| `v2rayN/ServiceLib/ViewModels/MsgViewModel.cs` | 开启 `LeastDelay` 时立即保存模式、设置启动目标并后台探测；关闭时取消探测。 |
| `v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs` | 活动标签改为依据核心实际运行目标显示。 |
| `v2rayN/ServiceLib/Handler/ConnectionHandler.cs` | 两次 HTTP 探测中只完成一次成功时保留该次结果。 |
| `v2rayN/ServiceLib.Tests/*.cs` | 增加和调整转移响应性、超时、间隔与标签显示测试。 |
| `_local_changes/README.md` | 追加 `018` 改动索引。 |

## 新增文件

| 文件 | 说明 |
| --- | --- |
| `v2rayN/ServiceLib/Manager/FailoverHealthTiming.cs` | 集中定义故障健康探测时间参数。 |
| `_local_changes/018-failover-responsive-least-delay/README.md` | 记录改动目的、行为和代理安全评估。 |
| `_local_changes/018-failover-responsive-least-delay/files.md` | 记录涉及文件。 |
| `_local_changes/018-failover-responsive-least-delay/reapply.md` | 记录未来迁移步骤。 |
| `_local_changes/018-failover-responsive-least-delay/tests.md` | 记录验证命令和结果。 |
| `_local_changes/018-failover-responsive-least-delay/patch.diff` | 保存功能补丁。 |

## 删除文件

无。
```

`reapply.md` 写入：

```markdown
# 重新应用步骤

1. 阅读新版 `FailoverHealthStateMachine` 和 `FailoverHealthService`，确认健康状态、冷却和后台循环仍使用同名模型。
2. 先迁移 `FailoverHealthTiming`，再把状态机失败语义改为非取消失败立即 `Failed`。
3. 迁移 `FailoverGroupManager` 的 45 秒最小探测间隔、最低延迟目标解析和启动目标覆盖。
4. 迁移 `Config.FailoverStartupProfileId`、`CoreConfigContext.FailoverRuntimeTargetProfileId` 和 `CoreConfigContextBuilder` 的目标写入。
5. 迁移 `MsgViewModel` 的非阻塞 `LeastDelay` 开启流程和关闭取消流程。
6. 迁移 `FailoverHealthService` 的 7 秒快速切换、15 秒硬截止和 `Probing` 清理。
7. 迁移 `AppManager`、`CoreManager` 和 `ProfilesViewModel` 的核心实际目标记录与活动标签显示。
8. 迁移 `ConnectionHandler` 中保留一次成功 HTTP 探测结果的逻辑。
9. 迁移测试并运行 `tests.md` 中的验证命令。
10. 重新生成新版 `patch.diff`。
```

`tests.md` 先写入待执行命令，实施时把“待执行”改为实际结果：

```markdown
# 验证记录

## 自动化验证

| 命令 | 结果 | 说明 |
| --- | --- | --- |
| `$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "Failover" --no-restore` | 待执行 | 覆盖转移相关服务、状态机、运行态解析和 UI 显示测试。 |
| `$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --no-restore` | 待执行 | ServiceLib 测试项目全量验证。 |
| `dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore` | 待执行 | Desktop 项目编译验证。 |

## 手工验证

- 待执行：开启 `延迟转移` 后按钮立即变绿，代理立即连接上一次实际连接节点。
- 待执行：7 秒左右按本轮成功结果切换到最低延迟节点。
- 待执行：15 秒后无结果节点标记为失败，不残留 `探测中`。
- 待执行：探测中关闭会取消探测并把残留 `探测中` 改为 `未知`。
- 待执行：第一轮结束后不会马上开启第二轮。

## 代理安全影响评估

本改动会改变转移模式启动和切换时机。验证重点是无有效候选时必须关闭或提示，不能静默直连；7 秒和 15 秒切换目标必须来自活动转移组已启用队列；探测仍使用 `suppressFailover: true`。
```

- [ ] **步骤 4：更新本地改动总览**

在 `_local_changes/README.md` 表格末尾追加：

```markdown
| 018 | `018-failover-responsive-least-delay/` | 已实现 | 延迟转移开启立即连接上一次节点、7 秒快速切换、15 秒硬截止、关闭取消探测和探测间隔增加 15 秒 |
```

- [ ] **步骤 5：生成 patch.diff**

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes/018-failover-responsive-least-delay/patch.diff
```

- [ ] **步骤 6：提交**

```powershell
git add _local_changes/README.md _local_changes/018-failover-responsive-least-delay
git commit -m "记录延迟转移响应性本地改动"
```

---

### 任务 11：完整验证

**文件:**
- 不修改文件，验证当前分支。

- [ ] **步骤 1：运行转移相关测试**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "Failover" --no-restore
```

期望：所有 `Failover*` 测试通过。

- [ ] **步骤 2：运行 ServiceLib.Tests 全量测试**

```powershell
$env:DOTNET_ROLL_FORWARD='Major'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --no-restore
```

期望：测试通过。如果本机运行时缺失导致测试宿主无法启动，在 `_local_changes/018-failover-responsive-least-delay/tests.md` 记录具体错误和残余风险。

- [ ] **步骤 3：编译 Desktop 项目**

```powershell
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

期望：编译通过。

- [ ] **步骤 4：手工验证**

在本地发布目录运行程序，准备一个含大量已启用队列节点的活动转移组：

1. 开启 `延迟转移`，观察三态按钮立即变绿。
2. 立刻访问网页，确认代理服务使用上一次实际连接节点可用。
3. 观察 7 秒左右是否按已成功结果切换到最低延迟节点。
4. 观察 15 秒后未完成节点是否标记为 `Failed`，不再残留 `Probing`。
5. 探测中点击 `关闭`，确认探测停止且残留 `Probing` 变 `Unknown`。
6. 第一轮结束后等待短时间，确认不会马上开启第二轮。

- [ ] **步骤 5：更新 tests.md 和 patch.diff**

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes/018-failover-responsive-least-delay/patch.diff
git add _local_changes/018-failover-responsive-least-delay/tests.md _local_changes/018-failover-responsive-least-delay/patch.diff
git commit -m "补充延迟转移响应性验证记录"
```
