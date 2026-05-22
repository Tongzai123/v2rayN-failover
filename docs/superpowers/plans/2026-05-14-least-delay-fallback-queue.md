# 延迟转移核心兜底队列实施计划

> **给代理执行者：** 必需子技能：使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 按任务逐项实施。本计划使用复选框语法跟踪进度。

**目标：** 让 `延迟转移` 在同核心类型队列下生成按延迟排序的核心 fallback 队列，使最低延迟节点故障时核心可立即兜底到次低延迟节点。

**架构：** 在 `FailoverGroupManager` 中集中定义 `LeastDelay` 运行态排序和队列签名；`CoreConfigContextBuilder` 把运行态队列签名带到核心启动上下文；`FailoverHealthService` 用完整队列签名决定是否 reload；`ProfilesViewModel` 复用运行态队列顺序显示活动故障组。

**技术栈：** C#、.NET 8、xUnit、ReactiveUI 事件通道、SQLite-net、Avalonia/WPF 共享 `ServiceLib` ViewModel。

---

## 文件结构

- 修改 `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`：集中实现 `LeastDelay` 完整运行态队列排序、队列签名、同核心虚拟 fallback 入口选择。
- 修改 `v2rayN/ServiceLib/Models/FailoverRuntimeResolveResult.cs`：为运行态解析结果增加队列签名派生属性。
- 修改 `v2rayN/ServiceLib/Models/CoreConfigContext.cs`：保存本次核心实际使用的 failover 队列签名。
- 修改 `v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`：把解析结果的队列签名写入 `CoreConfigContext`。
- 修改 `v2rayN/ServiceLib/Manager/AppManager.cs`：新增 `RunningFailoverQueueSignature` 运行期字段。
- 修改 `v2rayN/ServiceLib/Manager/CoreManager.cs`：核心启动成功后记录运行中的队列签名；配置生成失败时保留旧运行状态，核心进程未启动成功时清空本次运行状态。
- 修改 `v2rayN/ServiceLib/Services/FailoverHealthService.cs`：比较完整运行态队列签名，决定健康探测后是否 reload。
- 修改 `v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`：延迟转移列表按运行态顺序显示，活动节点仍置顶。
- 修改测试：
  - `v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`
  - `v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`
  - `v2rayN/ServiceLib.Tests/FailoverHealthDisplayTests.cs`
  - `v2rayN/ServiceLib.Tests/CoreConfigV2rayServiceTests.cs`
  - `v2rayN/ServiceLib.Tests/CoreConfigSingboxServiceTests.cs`
- 新增 `_local_changes/024-least-delay-fallback-queue-implementation/`：记录实际功能实现，保留 `023` 作为设计记录。

---

### 任务 1：补齐 `LeastDelay` 完整运行态排序测试

**文件：**
- 修改：`v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`

- [ ] **步骤 1：写失败测试，验证成功节点按延迟排序，未知和探测中保留兜底，失败节点默认排除**

先调整两个既有测试，避免旧语义和新语义互相冲突：

1. 将 `GetQueueEntriesForMode_LeastDelaySwapsLowestDelayWithFirstEntry` 改名为 `GetQueueEntriesForMode_LeastDelayOrdersAllNormalNodesByDelay`，并把断言改为完整延迟排序：

```csharp
Assert.Equal([p2, p3, p1], entries.Select(entry => entry.Profile.IndexId).ToArray());
```

2. 将 `GetQueueEntriesForMode_LeastDelayKeepsOriginalOrderWhenNoValidDelay` 改名为 `GetQueueEntriesForMode_LeastDelayExcludesFailedWhenUnknownFallbackExists`，并把断言改为保留非失败兜底、排除 `Failed`：

```csharp
Assert.Equal([p1], entries.Select(entry => entry.Profile.IndexId).ToArray());
```

在 `GetQueueEntriesForMode_LeastDelayIgnoresDisabledLowerDelayEntry` 后添加：

```csharp
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
```

- [ ] **步骤 2：写失败测试，验证全失败时按原始顺序放回队列**

继续添加：

```csharp
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
```

- [ ] **步骤 3：写失败测试，验证 `enabledOnly=false` 时禁用队列项不会从 UI 候选来源消失**

继续添加：

```csharp
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
```

- [ ] **步骤 4：运行测试确认失败**

运行：

```powershell
dotnet test .\v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverGroupManagerTests.GetQueueEntriesForMode_LeastDelayOrdersAllNormalNodesByDelay|FullyQualifiedName~FailoverGroupManagerTests.GetQueueEntriesForMode_LeastDelayExcludesFailedWhenUnknownFallbackExists|FullyQualifiedName~FailoverGroupManagerTests.GetQueueEntriesForMode_LeastDelayOrdersNormalNodesAndKeepsUnknownFallbacks|FullyQualifiedName~FailoverGroupManagerTests.GetQueueEntriesForMode_LeastDelayKeepsFailedEntriesOnlyWhenAllEntriesFailed|FullyQualifiedName~FailoverGroupManagerTests.GetQueueEntriesForMode_LeastDelayKeepsDisabledEntriesWhenEnabledOnlyFalse"
```

预期：至少完整排序、排除 `Failed` 或禁用项保留相关测试失败，证明当前“只交换第一节点”的实现尚未满足新语义。

- [ ] **步骤 5：提交测试**

```powershell
git add v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs
git commit -m "test: 覆盖延迟转移运行态队列排序"
```

---

### 任务 2：实现完整运行态排序和队列签名

**文件：**
- 修改：`v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`

- [ ] **步骤 1：替换 `ApplyLeastDelayRuntimeOrder`**

把 `FailoverGroupManager.cs` 中现有 `ApplyLeastDelayRuntimeOrder` 替换为：

```csharp
private static List<FailoverQueueEntry> ApplyLeastDelayRuntimeOrder(List<FailoverQueueEntry> entries)
{
    if (entries.Count <= 1)
    {
        return entries;
    }

    var enabledEntries = entries.Where(entry => entry.Item.Enabled).ToList();
    var disabledEntries = entries.Where(entry => !entry.Item.Enabled).ToList();
    var availableEntries = enabledEntries
        .Where(entry => entry.Item.LastStatus != FailoverHealthStatus.Failed)
        .ToList();
    var sortableEntries = availableEntries.Count > 0 ? availableEntries : enabledEntries;
    var sortableIds = sortableEntries.Select(entry => entry.Item.Id).ToHashSet();

    var orderedEntries = entries
        .Select((entry, index) => new { Entry = entry, OriginalIndex = index })
        .Where(x => sortableIds.Contains(x.Entry.Item.Id))
        .OrderBy(x => GetLeastDelayRuntimeOrderBucket(x.Entry.Item))
        .ThenBy(x => x.Entry.Item.LastStatus == FailoverHealthStatus.Normal && x.Entry.Item.LastDelay > 0
            ? x.Entry.Item.LastDelay
            : int.MaxValue)
        .ThenBy(x => x.Entry.Item.Sort)
        .ThenBy(x => x.OriginalIndex)
        .Select(x => x.Entry)
        .ToList();

    return orderedEntries.Concat(disabledEntries).ToList();
}

private static int GetLeastDelayRuntimeOrderBucket(FailoverGroupItem item)
{
    if (item.LastStatus == FailoverHealthStatus.Normal && item.LastDelay > 0)
    {
        return 0;
    }

    if (item.LastStatus is FailoverHealthStatus.Unknown or FailoverHealthStatus.Probing)
    {
        return 1;
    }

    return 2;
}
```

- [ ] **步骤 2：新增队列签名 API**

在 `GetLeastDelayRuntimeTargetProfileId` 后添加：

```csharp
public static async Task<string?> GetRuntimeQueueSignature(Config config, ProfileItem currentNode)
{
    var resolveResult = await TryResolveRuntimeNode(config, currentNode);
    return resolveResult.Success ? resolveResult.RuntimeQueueSignature : null;
}

public static string BuildRuntimeQueueSignature(IEnumerable<ProfileItem> queueProfiles)
{
    return string.Join(",", queueProfiles.Select(profile => profile.IndexId));
}
```

- [ ] **步骤 3：给 `FailoverRuntimeResolveResult` 增加派生签名属性**

修改 `v2rayN/ServiceLib/Models/FailoverRuntimeResolveResult.cs` 的 record body：

```csharp
{
    public bool Success => ValidatorResult.Success;

    public string RuntimeQueueSignature => Kind == FailoverRuntimeKind.VirtualPolicyGroup
        ? FailoverGroupManager.BuildRuntimeQueueSignature(QueueProfiles)
        : RuntimeTargetProfileId ?? string.Empty;
}
```

签名语义必须和核心实际入口一致：虚拟 fallback 入口记录完整队列；混合核心或其它 direct-profile 降级只记录实际目标，避免第二、第三候选顺序变化触发无意义 reload。

保留文件顶部已有 `using ServiceLib.Handler.Builder;`。如果编译提示缺少命名空间，添加：

```csharp
using ServiceLib.Manager;
```

- [ ] **步骤 4：运行排序测试**

运行：

```powershell
dotnet test .\v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverGroupManagerTests.GetQueueEntriesForMode_LeastDelay"
```

预期：所有 `GetQueueEntriesForMode_LeastDelay*` 测试通过。

- [ ] **步骤 5：提交实现**

```powershell
git add v2rayN/ServiceLib/Manager/FailoverGroupManager.cs v2rayN/ServiceLib/Models/FailoverRuntimeResolveResult.cs
git commit -m "feat: 实现延迟转移运行态队列排序"
```

---

### 任务 3：让同核心 `LeastDelay` 使用虚拟 fallback 入口

**文件：**
- 修改：`v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`

- [ ] **步骤 1：写失败测试，验证同核心 `LeastDelay` 生成虚拟组且队列按延迟排序**

在 `TryResolveRuntimeNode_SameCoreQueueUsesVirtualPolicyGroup` 后添加：

```csharp
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
```

- [ ] **步骤 2：写失败测试，验证没有成功延迟时仍保留启动节点直连**

继续添加：

```csharp
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
```

- [ ] **步骤 3：调整启动节点短路逻辑**

在 `TryResolveRuntimeNode` 中，把 `FailoverStartupProfileId` 分支移到 `queueEntries` 之后，并加上“尚无成功延迟结果”限制：

```csharp
var hasLeastDelayResult = config.FailoverMode == EFailoverMode.LeastDelay
    && queueEntries.Any(entry => entry.Item.LastStatus == FailoverHealthStatus.Normal && entry.Item.LastDelay > 0);

if (config.FailoverMode == EFailoverMode.LeastDelay
    && !hasLeastDelayResult
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

保留 `CanUseCoreFallback(currentCoreType, queueEntries)` 分支在其后。这样已有成功延迟结果时同核心 `LeastDelay` 会进入虚拟 fallback。

- [ ] **步骤 4：同步调整既有启动节点测试**

把 `CoreConfigContextBuilder_Build_LeastDelayStartupProfileKeepsPreviousNode` 改名为 `CoreConfigContextBuilder_Build_LeastDelayStartupProfileKeepsPreviousNodeUntilDelayResultExists`，并把测试中的队列项改为尚无成功延迟：

```csharp
fastItem.LastStatus = FailoverHealthStatus.Unknown;
fastItem.LastDelay = 0;
```

保留断言：

```csharp
Assert.Equal(previous.IndexId, result.Context.Node.IndexId);
Assert.Equal(previous.IndexId, result.Context.FailoverRuntimeTargetProfileId);
```

该测试只覆盖“没有成功延迟结果时保留启动节点”；已有成功延迟结果时由步骤 1 的 `TryResolveRuntimeNode_LeastDelaySameCoreUsesVirtualPolicyGroupOrderedByDelay` 覆盖虚拟 fallback 路径。

- [ ] **步骤 5：运行解析测试**

运行：

```powershell
dotnet test .\v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverGroupManagerTests.TryResolveRuntimeNode_LeastDelay|FullyQualifiedName~FailoverGroupManagerTests.CoreConfigContextBuilder_Build_LeastDelayStartupProfileKeepsPreviousNodeUntilDelayResultExists"
```

预期：新增测试通过；调整后的启动节点测试通过。

- [ ] **步骤 6：提交解析调整**

```powershell
git add v2rayN/ServiceLib/Manager/FailoverGroupManager.cs v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs
git commit -m "feat: 让延迟转移使用核心 fallback 队列"
```

---

### 任务 4：记录核心实际运行的 fallback 队列签名

**文件：**
- 修改：`v2rayN/ServiceLib/Models/CoreConfigContext.cs`
- 修改：`v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`
- 修改：`v2rayN/ServiceLib/Manager/AppManager.cs`
- 修改：`v2rayN/ServiceLib/Manager/CoreManager.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`

- [ ] **步骤 1：写失败测试，验证构建上下文带出队列签名**

在 `CoreConfigContextBuilder_Build_LeastDelayStartupProfileKeepsPreviousNodeUntilDelayResultExists` 后添加：

```csharp
[Fact]
public async Task CoreConfigContextBuilder_Build_LeastDelayFallbackQueueSetsRuntimeQueueSignature()
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
        slowItem.LastDelay = 70;
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
            FailoverStartupProfileId = fast.IndexId,
            TunModeItem = new(),
            SimpleDNSItem = new(),
            RoutingBasicItem = new(),
        };

        var result = await CoreConfigContextBuilder.Build(config, current);

        Assert.True(result.Success, string.Join(" | ", result.ValidatorResult.Errors));
        Assert.Equal(fast.IndexId, result.Context.FailoverRuntimeTargetProfileId);
        Assert.Equal($"{fast.IndexId},{slow.IndexId}", result.Context.FailoverRuntimeQueueSignature);
    }
    finally
    {
        await Cleanup(groupId, current.IndexId, slow.IndexId, fast.IndexId);
        await SQLiteHelper.Instance.ExecuteAsync($"delete from RoutingItem where Id = '{routeId}'");
    }
}
```

- [ ] **步骤 2：扩展 `CoreConfigContext`**

在 `FailoverRuntimeTargetProfileId` 后添加：

```csharp
public string? FailoverRuntimeQueueSignature { get; init; }
```

- [ ] **步骤 3：扩展 `CoreConfigContextBuilder.Build`**

在 `failoverRuntimeTargetProfileId` 变量旁添加：

```csharp
string? failoverRuntimeQueueSignature = null;
```

在设置 `failoverRuntimeTargetProfileId = resolveResult.RuntimeTargetProfileId;` 后添加：

```csharp
failoverRuntimeQueueSignature = resolveResult.RuntimeQueueSignature;
```

把最终 context 设置改成：

```csharp
context = context with
{
    Node = actNode,
    FailoverRuntimeTargetProfileId = failoverRuntimeTargetProfileId,
    FailoverRuntimeQueueSignature = failoverRuntimeQueueSignature,
};
```

- [ ] **步骤 4：扩展 `AppManager` 与 `CoreManager`**

在 `v2rayN/ServiceLib/Manager/AppManager.cs` 中 `RunningFailoverTargetProfileId` 后添加：

```csharp
public string? RunningFailoverQueueSignature { get; set; }
```

在 `CoreManager.LoadCore` 中设置运行目标后添加：

```csharp
AppManager.Instance.RunningFailoverQueueSignature = _processService != null
    ? mainContext.FailoverRuntimeQueueSignature
    : null;
```

注意：`CoreConfigHandler.GenerateClientConfig` 失败时 `LoadCore` 会提前返回，不会执行这里的赋值，因此旧运行目标和旧队列签名会保留。只有配置生成成功但核心进程未启动成功时，才把本次运行状态清空为 `null`。

- [ ] **步骤 5：运行上下文测试**

运行：

```powershell
dotnet test .\v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverGroupManagerTests.CoreConfigContextBuilder_Build_LeastDelayFallbackQueueSetsRuntimeQueueSignature"
```

预期：新增测试通过。

- [ ] **步骤 6：提交运行签名支持**

```powershell
git add v2rayN/ServiceLib/Models/CoreConfigContext.cs v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs v2rayN/ServiceLib/Manager/AppManager.cs v2rayN/ServiceLib/Manager/CoreManager.cs v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs
git commit -m "feat: 记录延迟转移运行队列签名"
```

---

### 任务 5：按完整队列签名触发 reload

**文件：**
- 修改：`v2rayN/ServiceLib/Services/FailoverHealthService.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`

- [ ] **步骤 1：写失败测试，验证第一节点不变但备用顺序变化也 reload**

在 `CheckActiveGroupOnceAsync_LeastDelayReloadsOnlyWhenFirstEntryChanges` 后添加：

```csharp
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
```

- [ ] **步骤 2：调整 `CheckActiveGroupOnceAsync` 的 before/after 比较**

同时添加 direct-profile 降级保护测试，验证混合核心队列只因实际目标变化 reload，不因备用顺序变化 reload：

```csharp
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
```

把 `runtimeTargetBefore` 改成：

```csharp
var runtimeSignatureBefore = currentNode != null
    ? await FailoverGroupManager.GetRuntimeQueueSignature(_config, currentNode)
    : null;
```

把 reload 判断改成：

```csharp
var runtimeSignatureAfter = await FailoverGroupManager.GetRuntimeQueueSignature(_config, currentNode);
if (runtimeSignatureAfter.IsNotEmpty() && runtimeSignatureAfter != runtimeSignatureBefore)
{
    AppEvents.ReloadRequested.Publish();
}
```

- [ ] **步骤 3：调整快速切换逻辑比较运行队列签名**

在 `ReloadLeastDelayBestTargetIfAvailable` 中，取 target 后添加：

```csharp
var currentNode = await AppManager.Instance.GetProfileItem(_config.IndexId);
var queueSignature = currentNode != null
    ? await FailoverGroupManager.GetRuntimeQueueSignature(_config, currentNode)
    : null;
```

把“已在运行时不 reload”的判断改成：

```csharp
if (AppManager.Instance.RunningFailoverTargetProfileId == target
    && AppManager.Instance.RunningFailoverQueueSignature == queueSignature)
{
    _pendingLeastDelayReloadTargetProfileId = null;
    return;
}
```

保留 `_pendingLeastDelayReloadTargetProfileId` 去重判断；如果需要避免同目标不同签名被误跳过，将判断改成：

```csharp
if (_config.FailoverStartupProfileId == target
    && _pendingLeastDelayReloadTargetProfileId == target
    && AppManager.Instance.RunningFailoverQueueSignature == queueSignature)
{
    return;
}
```

- [ ] **步骤 4：运行健康服务测试**

运行：

```powershell
dotnet test .\v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverHealthServiceTests.CheckActiveGroupOnceAsync_LeastDelay|FullyQualifiedName~FailoverHealthServiceTests.StartLeastDelayProbeRound"
```

预期：新增和既有 `LeastDelay` 健康服务测试通过。

- [ ] **步骤 5：提交 reload 策略**

```powershell
git add v2rayN/ServiceLib/Services/FailoverHealthService.cs v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs
git commit -m "feat: 按延迟转移队列签名触发重载"
```

---

### 任务 6：调整 UI 展示排序测试和实现

**文件：**
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverHealthDisplayTests.cs`

- [ ] **步骤 1：写失败测试，验证活动节点置顶后按运行态队列顺序显示**

在 `FailoverHealthDisplayTests` 中 `GetFailoverPriorityDisplayState_HidesPriorityOnlyForActiveLeastDelay` 附近添加：

```csharp
[Fact]
public void OrderFailoverProfileModelsForDisplay_ActiveLeastDelayKeepsActiveFirstThenRuntimeOrder()
{
    var active = new ProfileItemModel
    {
        IndexId = "active",
        IsInFailoverQueue = true,
        FailoverPriority = 2,
        Sort = 20,
    };
    var best = new ProfileItemModel
    {
        IndexId = "best",
        IsInFailoverQueue = true,
        FailoverPriority = 1,
        Sort = 10,
    };
    var fallback = new ProfileItemModel
    {
        IndexId = "fallback",
        IsInFailoverQueue = true,
        FailoverPriority = 3,
        Sort = 30,
    };
    var candidate = new ProfileItemModel
    {
        IndexId = "candidate",
        IsInFailoverQueue = false,
        Sort = 1,
    };

    var ordered = ProfilesViewModel.OrderFailoverProfileModelsForDisplay(
        [candidate, fallback, active, best],
        isFailoverGroup: true,
        isActiveFailoverGroup: true,
        activeFailoverTargetProfileId: "active");

    Assert.Equal(["active", "best", "fallback", "candidate"], ordered.Select(item => item.IndexId).ToArray());
}
```

- [ ] **步骤 2：保留现有排序实现并确认含义**

当前 `OrderFailoverProfileModelsForDisplay` 已按：

```csharp
.OrderBy(t => isActiveFailoverGroup && activeFailoverTargetProfileId == t.IndexId ? 0 : 1)
.ThenBy(t => isFailoverGroup && t.IsInFailoverQueue ? 0 : 1)
.ThenBy(t => isFailoverGroup && t.IsInFailoverQueue ? t.FailoverPriority : t.Sort)
```

排序。由于 `FailoverPriority` 在 `LeastDelay` 下来自 `GetQueuePriorityMap(subid, failoverMode)`，任务 2 的运行态排序会自动反映到 UI。若步骤 1 测试已通过，不改实现，只提交测试。若测试失败，保持上述排序结构，不引入新字段。

- [ ] **步骤 3：运行 UI 显示测试**

运行：

```powershell
dotnet test .\v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverHealthDisplayTests.OrderFailoverProfileModelsForDisplay_ActiveLeastDelayKeepsActiveFirstThenRuntimeOrder|FullyQualifiedName~FailoverHealthDisplayTests.GetFailoverPriorityDisplayState_HidesPriorityOnlyForActiveLeastDelay"
```

预期：新增测试通过，`LeastDelay` 下 `P*` 标签仍隐藏。

- [ ] **步骤 4：提交 UI 测试或修正**

```powershell
git add v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs v2rayN/ServiceLib.Tests/FailoverHealthDisplayTests.cs
git commit -m "test: 固定延迟转移列表展示顺序"
```

---

### 任务 7：验证 Xray 和 sing-box 生成的 fallback 队列

**文件：**
- 修改：`v2rayN/ServiceLib.Tests/CoreConfigV2rayServiceTests.cs`
- 修改：`v2rayN/ServiceLib.Tests/CoreConfigSingboxServiceTests.cs`

- [ ] **步骤 1：复用现有配置生成测试确认队列顺序来源**

现有测试已经覆盖：

- `CoreConfigV2rayServiceTests.GenerateClientConfigContent_FallbackPolicyGroupUsesPriorityAwareBalancer`
- `CoreConfigSingboxServiceTests.GenerateClientConfigContent_FallbackPolicyGroupBuildsSelectorUrltestInQueueOrder`

如果任务 3 已保证 `LeastDelay` 生成虚拟 `PolicyGroup` 且 `ChildItems` 已排序，这两类核心配置测试无需新增同构测试。

- [ ] **步骤 2：运行核心配置生成测试**

运行：

```powershell
dotnet test .\v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreConfigV2rayServiceTests.GenerateClientConfigContent_FallbackPolicyGroupUsesPriorityAwareBalancer|FullyQualifiedName~CoreConfigSingboxServiceTests.GenerateClientConfigContent_FallbackPolicyGroupBuildsSelectorUrltestInQueueOrder"
```

预期：两个测试通过，确认 `Fallback` 仍生成 Xray `leastLoad + fallbackTag + costs` 和 sing-box `selector + urltest`。

- [ ] **步骤 3：提交验证记录**

如未修改测试文件，不提交代码；在任务 9 的 `_local_changes/023-*/tests.md` 中记录此验证。若必须调整测试断言，提交：

```powershell
git add v2rayN/ServiceLib.Tests/CoreConfigV2rayServiceTests.cs v2rayN/ServiceLib.Tests/CoreConfigSingboxServiceTests.cs
git commit -m "test: 验证延迟转移 fallback 核心配置"
```

---

### 任务 8：补充本地改动记录

**文件：**
- 新增：`_local_changes/024-least-delay-fallback-queue-implementation/README.md`
- 新增：`_local_changes/024-least-delay-fallback-queue-implementation/files.md`
- 新增：`_local_changes/024-least-delay-fallback-queue-implementation/reapply.md`
- 新增：`_local_changes/024-least-delay-fallback-queue-implementation/tests.md`
- 新增：`_local_changes/024-least-delay-fallback-queue-implementation/patch.diff`
- 修改：`_local_changes/README.md`

- [ ] **步骤 1：创建目录和说明文件**

创建目录：

```powershell
New-Item -ItemType Directory -Force -Path _local_changes\024-least-delay-fallback-queue-implementation
```

`README.md` 写入：

```markdown
# 延迟转移核心兜底队列实现

## 基本信息

- 编号：`024`
- 目录：`_local_changes/024-least-delay-fallback-queue-implementation/`
- 创建日期：`2026-05-14`
- 适用源码版本：本地 `v2rayN-7.20.4` 改造副本
- 改动类型：功能 / 行为修复 / 代理安全
- 状态：实施中

## 改动目的

让 `延迟转移` 在同核心类型队列下生成按延迟排序的核心 fallback 队列。当前最低延迟节点故障时，核心可以立即尝试次低延迟节点，后台健康探测继续刷新排序并触发必要重载。

## 用户可见行为

- `延迟转移` 正常优先连接最低延迟节点。
- 同核心类型队列中，最低延迟节点故障时，新连接可由核心 fallback 到后续节点。
- 活动故障组列表继续隐藏 `P*`，并按运行态延迟兜底顺序显示。

## 设计取舍

同核心类型优先使用虚拟 `PolicyGroup + Fallback`，混合核心类型保留 direct-profile 降级。`Failed` 节点默认排除；全失败时按原始顺序放回，避免空入口。

## 代理安全影响评估

候选节点仍限制在活动故障组已启用队列中。健康探测继续使用 `suppressFailover: true`，避免被 fallback 队列掩盖真实故障。无有效候选时不静默直连，不修改 Tun、系统代理、DNS、路由、TLS、订阅、证书或核心更新策略。
```

- [ ] **步骤 2：补齐 `files.md`**

`files.md` 写入实际修改文件和原因。至少包含：

```markdown
# 文件变更清单

## 修改文件

| 文件 | 修改内容 | 原因 |
| --- | --- | --- |
| `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs` | 完整延迟排序、队列签名、同核心 fallback 入口 | 支持延迟转移即时兜底 |
| `v2rayN/ServiceLib/Models/FailoverRuntimeResolveResult.cs` | 增加运行态队列签名 | 供重载判断复用 |
| `v2rayN/ServiceLib/Models/CoreConfigContext.cs` | 保存核心运行队列签名 | 记录当前核心实际使用队列 |
| `v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs` | 写入队列签名 | 向核心管理层传递运行态队列 |
| `v2rayN/ServiceLib/Manager/AppManager.cs` | 新增运行期队列签名字段 | 供健康服务比较 |
| `v2rayN/ServiceLib/Manager/CoreManager.cs` | 核心启动后记录队列签名 | 保持运行状态可信 |
| `v2rayN/ServiceLib/Services/FailoverHealthService.cs` | 按完整队列签名触发 reload | 备用顺序变化也更新核心 |
| `v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs` | 如需修改，记录展示排序调整 | UI 反映运行态队列 |
```

- [ ] **步骤 3：补齐 `reapply.md` 和 `tests.md`**

`reapply.md` 必须说明优先应用 `patch.diff`，冲突时按任务 1 到任务 7 的顺序人工迁移。

`tests.md` 记录所有实际执行命令，至少包括：

```powershell
dotnet test .\v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverGroupManagerTests|FullyQualifiedName~FailoverHealthServiceTests|FullyQualifiedName~FailoverHealthDisplayTests|FullyQualifiedName~CoreConfigV2rayServiceTests|FullyQualifiedName~CoreConfigSingboxServiceTests"
```

- [ ] **步骤 4：更新总览索引**

在 `_local_changes/README.md` 的索引表追加：

```markdown
| 024 | `024-least-delay-fallback-queue-implementation/` | 已验证自动化 | 延迟转移按延迟排序生成核心 fallback 队列，支持当前节点故障时核心即时兜底 |
```

- [ ] **步骤 5：生成实现补丁**

运行：

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes/024-least-delay-fallback-queue-implementation/patch.diff
```

- [ ] **步骤 6：提交本地记录**

```powershell
git add _local_changes/README.md _local_changes/024-least-delay-fallback-queue-implementation
git commit -m "docs: 记录延迟转移核心兜底队列实现"
```

---

### 任务 9：最终验证

**文件：**
- 修改：`_local_changes/024-least-delay-fallback-queue-implementation/tests.md`

- [ ] **步骤 1：运行相关自动化测试**

运行：

```powershell
dotnet test .\v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverGroupManagerTests|FullyQualifiedName~FailoverHealthServiceTests|FullyQualifiedName~FailoverHealthDisplayTests|FullyQualifiedName~CoreConfigV2rayServiceTests|FullyQualifiedName~CoreConfigSingboxServiceTests"
```

预期：相关测试全部通过，失败数为 `0`。

- [ ] **步骤 2：运行 diff 检查**

运行：

```powershell
git diff --check
git status --short
```

预期：`git diff --check` 无错误；`git status --short` 只显示预期未提交文件，或为空。

- [ ] **步骤 3：更新测试记录并提交**

把实际测试结果写入 `_local_changes/024-least-delay-fallback-queue-implementation/tests.md`。如果有新记录，提交：

```powershell
git add _local_changes/024-least-delay-fallback-queue-implementation/tests.md _local_changes/024-least-delay-fallback-queue-implementation/patch.diff
git commit -m "docs: 更新延迟转移兜底队列验证记录"
```

---

## 自检结果

- 设计目标覆盖：任务 1、2 覆盖运行态排序；任务 3、7 覆盖同核心 fallback；任务 5 覆盖完整队列签名 reload；任务 6 覆盖 UI 展示；任务 8 覆盖 `_local_changes`。
- 边界覆盖：任务 2 覆盖 `Failed` 默认排除和全失败放回；任务 3 覆盖启动节点直连保留和同核心虚拟组；任务 5 覆盖第一节点不变但备用顺序变化。
- 代理安全覆盖：任务 8 和任务 9 要求记录候选范围、`suppressFailover: true` 和无静默直连边界。
