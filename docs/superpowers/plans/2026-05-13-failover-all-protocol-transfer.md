# 全协议统一转移模式实施计划

> **给代理执行者：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 按任务逐步执行。所有步骤使用 checkbox (`- [ ]`) 跟踪。

**目标：** 让所有可单独运行的普通协议节点，在不要求核心类型一致的前提下，都支持 `故障转移` 和 `延迟转移`。

**架构：** 保留现有“同核心虚拟 fallback `PolicyGroup`”快速路径，继续利用 Xray / sing-box 的分组能力处理同核心队列；同时新增“应用层运行时目标解析器”慢路径，当活动转移队列混合 `Xray` / `sing_box`，或当前手动默认节点与转移队列核心类型不一致时，不再强制构造虚拟组，而是在 v2rayN 应用层直接挑选一个真实节点作为运行时目标并重载对应核心。`Config.IndexId` 保持“用户手动默认节点”语义，不被运行时转移接管写回。

**Tech Stack：** .NET、xUnit、Avalonia、ReactiveUI、SQLite-net、v2rayN ServiceLib、Xray、sing-box。

---

## 范围界定

- 支持对象：`VMess`、`VLESS`、`Shadowsocks`、`Trojan`、`Hysteria2`、`TUIC`、`WireGuard`、`SOCKS`、`HTTP`、`Anytls`、`Naive` 等可由 v2rayN 直接建模并单独启动的普通协议节点。
- 继续排除：`Custom`、`PolicyGroup`、`ProxyChain` 作为“转移队列目标节点”的直接支持。`PolicyGroup` / `ProxyChain` 仍可作为普通手动节点存在，但不进入统一转移队列。
- 不做的事：不新增新模式按钮，不修改现有“故障转移 / 关闭 / 延迟转移”三态 UI；不承诺跨核心切换不断流；不在这一轮新增“当前运行节点”可视化标签。
- 成功标准：
  1. 活动转移组允许混合 `Xray` 和 `sing_box` 普通协议节点。
  2. `故障转移` 模式在混合核心时能从第一个可用节点切到后续节点。
  3. `延迟转移` 模式在混合核心时能切到最低延迟可用节点。
  4. 同核心队列保持现有虚拟 `PolicyGroup` 行为，不退化。
  5. `suppressFailover: true` 的测速/健康探测隔离语义不被破坏。

## 文件结构

- 新增 `v2rayN/ServiceLib/Models/FailoverRuntimeResolveResult.cs`
  责任：定义运行时转移解析结果、解析路径类型和运行时目标 `ProfileId`。
- 修改 `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
  责任：统一活动转移组验证、同核心快速路径判断、应用层目标节点选择和运行时重载 key 计算。
- 修改 `v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`
  责任：在真正注册节点前，把用户手动默认节点解析成“运行时有效节点”。
- 修改 `v2rayN/ServiceLib/Services/FailoverHealthService.cs`
  责任：探测前后比较运行时目标是否变化，并在应用层切换或延迟转移首节点变化时发布重载。
- 修改 `v2rayN/ServiceLib/ViewModels/MsgViewModel.cs`
  责任：继续沿用现有模式切换入口，但启用时改用新的统一校验结果，避免混合核心被误判为不支持。
- 修改 `v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`
  责任：覆盖统一解析器的快速路径、慢路径、故障优先和延迟优先规则。
- 修改 `v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`
  责任：覆盖混合核心场景下探测后重载与不重载条件。
- 修改 `v2rayN/ServiceLib.Tests/CoreConfigSpeedtestSuppressFailoverTests.cs`
  责任：保证测速和健康探测仍可绕过统一转移解析。
- 修改 `_local_changes/README.md`
  责任：执行实现时补充新的实际功能记录索引。
- 新增 `_local_changes/012-all-protocol-transfer-support/README.md`
- 新增 `_local_changes/012-all-protocol-transfer-support/files.md`
- 新增 `_local_changes/012-all-protocol-transfer-support/reapply.md`
- 新增 `_local_changes/012-all-protocol-transfer-support/tests.md`
- 新增 `_local_changes/012-all-protocol-transfer-support/patch.diff`
  责任：执行实现时记录本轮实际功能改动，方便新版源码迁移。

## 关键设计决定

- `Config.IndexId` 不改写：用户手动选择仍然落在原始默认节点上，运行时接管只在配置构建阶段发生，避免把“用户意图”和“运行时故障策略”混为一个字段。
- 同核心继续走核心内部分组：同一活动转移队列的所有节点都属于同一核心，且与当前手动默认节点核心一致时，继续生成虚拟 `PolicyGroup`，避免把已有稳定路径全部替换成更慢的应用层重载。
- 混合核心走应用层直接切换：一旦队列核心类型混合，或者当前手动默认节点核心与队列核心不一致，直接选真实节点作为运行时目标，不再报 `MsgFailoverGroupCoreTypeMixed`。
- `故障转移` 的应用层选点规则：按静态队列顺序选择第一个 `LastStatus != Failed` 的已启用节点；如果所有已启用节点都处于 `Failed`，回退到静态队列第一项，避免没有运行目标。
- `延迟转移` 的应用层选点规则：先从 `LastStatus == Normal && LastDelay > 0` 的已启用节点中选最低延迟；没有有效延迟时，回退到“故障转移”选点规则。
- 健康探测仍按真实节点分核心批量探测：现有 `CoreFailoverHealthProbe` 已经按 `ServerTestItem.CoreType` 分组启动临时核心，这一层不需要推翻。

### Task 1：冻结统一解析契约

**Files：**
- Create: `v2rayN/ServiceLib/Models/FailoverRuntimeResolveResult.cs`
- Modify: `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- Test: `v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`

- [ ] **步骤 1：先写失败测试，固定“同核心走虚拟组、混合核心走真实节点”的契约**

```csharp
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
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "mixed", IsFailoverGroup = true });
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
        Assert.StartsWith(FailoverGroupManager.VirtualPolicyGroupPrefix, result.EffectiveNode?.IndexId);
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
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "mixed", IsFailoverGroup = true });
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
        Assert.Equal(singNode.IndexId, result.EffectiveNode?.IndexId);
        Assert.Equal(singNode.IndexId, result.RuntimeTargetProfileId);
    }
    finally
    {
        await Cleanup(groupId, xrayNode.IndexId, singNode.IndexId, current.IndexId);
    }
}
```

- [ ] **步骤 2：运行单测，确认当前实现先失败**

Run:

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "TryResolveRuntimeNode"
```

Expected:

```text
FAIL 或编译失败：不存在 FailoverRuntimeKind / TryResolveRuntimeNode
```

- [ ] **步骤 3：补运行时解析结果类型和最小解析器实现**

在 `v2rayN/ServiceLib/Models/FailoverRuntimeResolveResult.cs` 写入：

```csharp
namespace ServiceLib.Models;

public enum FailoverRuntimeKind
{
    None = 0,
    VirtualPolicyGroup = 1,
    DirectProfile = 2,
}

public record FailoverRuntimeResolveResult(
    FailoverRuntimeKind Kind,
    ProfileItem? EffectiveNode,
    List<ProfileItem> QueueProfiles,
    string? RuntimeTargetProfileId,
    NodeValidatorResult ValidatorResult)
{
    public bool Success => ValidatorResult.Success;
}
```

在 `FailoverGroupManager.cs` 增加解析入口和辅助方法：

```csharp
public static async Task<FailoverRuntimeResolveResult> TryResolveRuntimeNode(Config config, ProfileItem currentNode)
{
    var validator = NodeValidatorResult.Empty();
    if (config.FailoverMode == EFailoverMode.Off)
    {
        return new(FailoverRuntimeKind.None, currentNode, [], currentNode.IndexId, validator);
    }

    var activeGroup = await GetActiveFailoverGroup(config);
    if (activeGroup == null)
    {
        validator.Errors.Add(ResUI.MsgActivateFailoverGroupFirst);
        return new(FailoverRuntimeKind.None, null, [], null, validator);
    }

    var queueEntries = await GetQueueEntriesForMode(activeGroup.Id, true, config.FailoverMode);
    if (queueEntries.Count == 0)
    {
        validator.Errors.Add(ResUI.MsgFailoverGroupQueueEmpty);
        return new(FailoverRuntimeKind.None, null, [], null, validator);
    }

    var currentCoreType = AppManager.Instance.GetCoreType(currentNode, currentNode.ConfigType);
    if (CanUseCoreFallback(currentCoreType, queueEntries))
    {
        var virtualNode = new ProfileItem
        {
            IndexId = $"{VirtualPolicyGroupPrefix}{activeGroup.Id}",
            ConfigType = EConfigType.PolicyGroup,
            CoreType = currentCoreType,
            Remarks = activeGroup.Remarks,
        };
        virtualNode.SetProtocolExtra(new ProtocolExtraItem
        {
            GroupType = EConfigType.PolicyGroup.ToString(),
            ChildItems = string.Join(",", queueEntries.Select(entry => entry.Profile.IndexId)),
            MultipleLoad = EMultipleLoad.Fallback,
        });
        return new(
            FailoverRuntimeKind.VirtualPolicyGroup,
            virtualNode,
            queueEntries.Select(entry => entry.Profile).ToList(),
            queueEntries.First().Profile.IndexId,
            validator);
    }

    var targetEntry = PickRuntimeEntryForDirectProfile(queueEntries, config.FailoverMode);
    if (targetEntry == null)
    {
        validator.Errors.Add(ResUI.MsgFailoverGroupQueueEmpty);
        return new(FailoverRuntimeKind.None, null, [], null, validator);
    }

    var targetProfile = targetEntry.Profile;
    var targetCoreType = AppManager.Instance.GetCoreType(targetProfile, targetProfile.ConfigType);
    var targetValidation = NodeValidator.Validate(targetProfile, targetCoreType);
    if (!targetValidation.Success)
    {
        return new(FailoverRuntimeKind.None, null, [], null, targetValidation);
    }

    return new(
        FailoverRuntimeKind.DirectProfile,
        targetProfile,
        queueEntries.Select(entry => entry.Profile).ToList(),
        targetProfile.IndexId,
        validator);
}
```

- [ ] **步骤 4：补最小辅助实现，让测试转绿**

在 `FailoverGroupManager.cs` 继续补齐：

```csharp
private static bool CanUseCoreFallback(ECoreType currentCoreType, IReadOnlyList<FailoverQueueEntry> queueEntries)
{
    if (currentCoreType is not (ECoreType.Xray or ECoreType.sing_box))
    {
        return false;
    }

    var queueCoreTypes = queueEntries
        .Select(entry => AppManager.Instance.GetCoreType(entry.Profile, entry.Profile.ConfigType))
        .Distinct()
        .ToList();

    return queueCoreTypes.Count == 1 && queueCoreTypes[0] == currentCoreType;
}

private static FailoverQueueEntry? PickRuntimeEntryForDirectProfile(
    IReadOnlyList<FailoverQueueEntry> queueEntries,
    EFailoverMode mode)
{
    var enabled = queueEntries.Where(entry => entry.Item.Enabled).ToList();
    if (enabled.Count == 0)
    {
        return null;
    }

    if (mode == EFailoverMode.LeastDelay)
    {
        var best = enabled
            .Where(entry => entry.Item.LastStatus == FailoverHealthStatus.Normal && entry.Item.LastDelay > 0)
            .OrderBy(entry => entry.Item.LastDelay)
            .ThenBy(entry => entry.Item.Sort)
            .FirstOrDefault();
        if (best != null)
        {
            return best;
        }
    }

    return enabled.FirstOrDefault(entry => entry.Item.LastStatus != FailoverHealthStatus.Failed)
        ?? enabled.First();
}
```

- [ ] **步骤 5：重跑测试并提交**

Run:

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "TryResolveRuntimeNode|FailoverGroupManagerTests"
```

Expected:

```text
PASS
```

Commit:

```powershell
git add v2rayN/ServiceLib/Models/FailoverRuntimeResolveResult.cs v2rayN/ServiceLib/Manager/FailoverGroupManager.cs v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs
git commit -m "feat: add all-protocol failover runtime resolver"
```

### Task 2：把运行时解析接入核心构建与启用校验

**Files：**
- Modify: `v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`
- Modify: `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- Modify: `v2rayN/ServiceLib/ViewModels/MsgViewModel.cs`
- Test: `v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`
- Test: `v2rayN/ServiceLib.Tests/CoreConfigSpeedtestSuppressFailoverTests.cs`

- [ ] **步骤 1：先写失败测试，固定“混合核心启用成功、构建时替换为真实节点”**

```csharp
[Fact]
public async Task CoreConfigContextBuilder_Build_MixedCoreQueueUsesResolvedDirectProfile()
{
    PrepareTables();
    var suffix = Utils.GetGuid(false);
    var groupId = $"failover-{suffix}";
    var current = CreateProxy($"current-{suffix}", "source-sub", ECoreType.Xray);
    var fallback = CreateProxy($"fallback-{suffix}", "source-sub", ECoreType.sing_box);

    try
    {
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "mixed", IsFailoverGroup = true });
        await SQLiteHelper.Instance.ReplaceAsync(current);
        await SQLiteHelper.Instance.ReplaceAsync(fallback);
        var first = CreateFailoverItem(groupId, current.IndexId, 1);
        first.LastStatus = FailoverHealthStatus.Failed;
        var second = CreateFailoverItem(groupId, fallback.IndexId, 2);
        second.LastStatus = FailoverHealthStatus.Normal;
        second.LastDelay = 25;
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
        var result = await CoreConfigContextBuilder.Build(config, current);

        Assert.True(result.Success, string.Join(" | ", result.ValidatorResult.Errors));
        Assert.Equal(fallback.IndexId, result.Context.Node.IndexId);
    }
    finally
    {
        await Cleanup(groupId, current.IndexId, fallback.IndexId);
    }
}
```

- [ ] **步骤 2：运行相关测试，确认当前实现会在混合核心时失败**

Run:

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "CoreConfigContextBuilder_Build_MixedCoreQueueUsesResolvedDirectProfile|CoreConfigHandler_BatchSpeedtestSuppressFailoverKeepsRequestedNode"
```

Expected:

```text
FAIL：混合核心仍返回 MsgFailoverGroupCoreTypeMixed
```

- [ ] **步骤 3：把 `CoreConfigContextBuilder.Build()` 改为使用统一解析器**

把 `CoreConfigContextBuilder.Build()` 中旧逻辑：

```csharp
var failoverResult = await FailoverGroupManager.TryBuildVirtualPolicyGroup(config, node);
```

替换为：

```csharp
var resolveResult = await FailoverGroupManager.TryResolveRuntimeNode(config, node);
if (!resolveResult.Success)
{
    return new CoreConfigContextBuilderResult(context, resolveResult.ValidatorResult);
}

if (resolveResult.EffectiveNode != null)
{
    node = resolveResult.EffectiveNode;
    context = context with { Node = node };
}
```

同时把 `ValidateActiveFailoverGroup()` 改成走新解析器：

```csharp
public static async Task<NodeValidatorResult> ValidateActiveFailoverGroup(Config config, ProfileItem currentNode)
{
    return (await TryResolveRuntimeNode(config, currentNode)).ValidatorResult;
}
```

- [ ] **步骤 4：调整 `MsgViewModel` 的启用时序，统一先探测再重载**

把 `MsgViewModel.ChangeFailoverMode()` 中启用非 `Off` 模式后的尾段改成：

```csharp
var currentNode = await AppManager.Instance.GetProfileItem(_config.IndexId);
if (currentNode == null)
{
    NoticeManager.Instance.Enqueue(ResUI.PleaseSelectServer);
    await DisableFailover();
    return;
}

var validator = await FailoverGroupManager.ValidateActiveFailoverGroup(_config, currentNode);
if (!validator.Success)
{
    NoticeManager.Instance.NotifyValidatorResult(validator);
    await DisableFailover();
    return;
}

await ConfigHandler.SaveConfig(_config);
FailoverHealthService.Instance.Start();
await FailoverHealthService.Instance.CheckActiveGroupOnceAsync(
    force: true,
    reloadOnRuntimeTargetChange: false);
NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
AppEvents.ReloadRequested.Publish();
AppEvents.FailoverStateChangedRequested.Publish();
await RefreshFailoverState();
```

这里不再新增 `CoreTypeMixed` 特判；混合核心场景应由解析器返回成功。强制探测放在首次重载前执行，目的是让混合核心 `故障转移` 和 `延迟转移` 都能尽量基于最新健康结果决定第一次运行时目标。

- [ ] **步骤 5：重跑测试并提交**

Run:

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "CoreConfigContextBuilder_Build_MixedCoreQueueUsesResolvedDirectProfile|CoreConfigHandler_BatchSpeedtestSuppressFailoverKeepsRequestedNode|FailoverGroupManagerTests"
```

Expected:

```text
PASS
```

Commit:

```powershell
git add v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs v2rayN/ServiceLib/Manager/FailoverGroupManager.cs v2rayN/ServiceLib/ViewModels/MsgViewModel.cs v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs v2rayN/ServiceLib.Tests/CoreConfigSpeedtestSuppressFailoverTests.cs
git commit -m "feat: wire all-protocol transfer resolution into core build"
```

### Task 3：让健康探测驱动应用层切换重载

**Files：**
- Modify: `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- Modify: `v2rayN/ServiceLib/Services/FailoverHealthService.cs`
- Test: `v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`

- [ ] **步骤 1：先写失败测试，覆盖混合核心故障转移和跨核心延迟转移**

```csharp
[Fact]
public async Task CheckActiveGroupOnceAsync_MixedCoreFailoverReloadsWhenRuntimeTargetChanges()
{
    PrepareTables();
    var suffix = Utils.GetGuid(false);
    var groupId = $"failover-{suffix}";
    var current = CreateProxy($"current-{suffix}", "source-sub", ECoreType.Xray);
    var backup = CreateProxy($"backup-{suffix}", "source-sub", ECoreType.sing_box);
    var config = new Config
    {
        IndexId = current.IndexId,
        FailoverEnabled = true,
        FailoverMode = EFailoverMode.Failover,
        ActiveFailoverGroupId = groupId,
    };
    var reloadCount = 0;

    using var subscription = AppEvents.ReloadRequested.AsObservable().Subscribe(_ => reloadCount++);

    try
    {
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "mixed", IsFailoverGroup = true });
        await SQLiteHelper.Instance.ReplaceAsync(current);
        await SQLiteHelper.Instance.ReplaceAsync(backup);
        await SQLiteHelper.Instance.InsertAllAsync(new[]
        {
            CreateFailoverItem(groupId, current.IndexId, 1),
            CreateFailoverItem(groupId, backup.IndexId, 2),
        });

        var probe = new FakeBatchProbe(new[]
        {
            new FailoverHealthProbeBatchResult(current.IndexId, FailoverHealthProbeResult.Failure("request-failed")),
            new FailoverHealthProbeBatchResult(backup.IndexId, FailoverHealthProbeResult.Success(18)),
        });

        var service = new FailoverHealthService(config, probe);
        await service.CheckActiveGroupOnceAsync(force: false, reloadOnRuntimeTargetChange: true);

        Assert.Equal(1, reloadCount);
    }
    finally
    {
        await Cleanup(groupId, current.IndexId, backup.IndexId);
    }
}
```

- [ ] **步骤 2：运行测试，确认当前实现只会处理 `LeastDelay` 的首节点变化**

Run:

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "CheckActiveGroupOnceAsync_MixedCoreFailoverReloadsWhenRuntimeTargetChanges|FailoverHealthServiceTests"
```

Expected:

```text
FAIL：reloadCount 仍为 0，或签名中不存在 reloadOnRuntimeTargetChange
```

- [ ] **步骤 3：在 `FailoverGroupManager` 暴露运行时重载 key**

新增统一辅助方法：

```csharp
public static async Task<string?> GetRuntimeTargetProfileId(Config config, ProfileItem currentNode)
{
    var resolveResult = await TryResolveRuntimeNode(config, currentNode);
    return resolveResult.Success ? resolveResult.RuntimeTargetProfileId : null;
}
```

这里不要直接返回 `EffectiveNode?.IndexId`，因为虚拟 `PolicyGroup` 场景下需要返回“运行态第一真实节点”的 `ProfileId`，供 `LeastDelay` 判断是否需要重载。

- [ ] **步骤 4：把健康探测重载判断从“仅延迟转移”泛化为“运行时目标变化”**

把 `FailoverHealthService.CheckActiveGroupOnceAsync()` 的前后比较改成：

```csharp
var currentNode = await AppManager.Instance.GetProfileItem(_config.IndexId);
var runtimeTargetBefore = currentNode == null
    ? null
    : await FailoverGroupManager.GetRuntimeTargetProfileId(_config, currentNode);

// ... 执行探测并应用结果 ...

var runtimeTargetAfter = currentNode == null
    ? null
    : await FailoverGroupManager.GetRuntimeTargetProfileId(_config, currentNode);

if (reloadOnRuntimeTargetChange
    && runtimeTargetAfter.IsNotEmpty()
    && runtimeTargetAfter != runtimeTargetBefore)
{
    AppEvents.ReloadRequested.Publish();
}
```

同时把方法签名统一改名，避免保留 `reloadOnLeastDelayChange` 这种过窄语义：

```csharp
public Task CheckActiveGroupOnceAsync(bool force, bool reloadOnRuntimeTargetChange)
```

- [ ] **步骤 5：重跑测试并提交**

Run:

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FailoverHealthServiceTests|TryResolveRuntimeNode"
```

Expected:

```text
PASS
```

Commit:

```powershell
git add v2rayN/ServiceLib/Manager/FailoverGroupManager.cs v2rayN/ServiceLib/Services/FailoverHealthService.cs v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs
git commit -m "feat: reload runtime target for mixed-core transfer modes"
```

### Task 4：补实现记录和迁移材料

**Files：**
- Modify: `_local_changes/README.md`
- Create: `_local_changes/012-all-protocol-transfer-support/README.md`
- Create: `_local_changes/012-all-protocol-transfer-support/files.md`
- Create: `_local_changes/012-all-protocol-transfer-support/reapply.md`
- Create: `_local_changes/012-all-protocol-transfer-support/tests.md`
- Create: `_local_changes/012-all-protocol-transfer-support/patch.diff`

- [ ] **步骤 1：创建记录目录并更新总览索引**

Run:

```powershell
New-Item -ItemType Directory -Force -Path "_local_changes\012-all-protocol-transfer-support"
```

在 `_local_changes/README.md` 追加：

```markdown
| 012 | `012-all-protocol-transfer-support/` | 已验证自动化 | 混合 Xray / sing_box 普通协议节点统一支持故障转移与延迟转移 |
```

- [ ] **步骤 2：写 `README.md`，明确快速路径和慢路径**

写入：

```markdown
# 全协议统一转移模式支持

## 基本信息

- 编号：`012`
- 目录：`_local_changes/012-all-protocol-transfer-support/`
- 创建日期：`2026-05-13`
- 适用源码版本：本地 `v2rayN-7.20.4` 改造副本
- 改动类型：功能
- 状态：已验证自动化

## 改动目的

让普通协议节点在活动转移组中不再因为核心类型不同而无法启用故障转移或延迟转移。

## 设计取舍

- 同核心保持虚拟 `PolicyGroup` 快速路径。
- 混合核心改为应用层解析真实节点并重载核心。
- 不改写 `Config.IndexId`，保留用户手动默认节点语义。
```

- [ ] **步骤 3：写 `files.md`、`reapply.md`、`tests.md`**

`files.md` 至少包含：

```markdown
| `v2rayN/ServiceLib/Models/FailoverRuntimeResolveResult.cs` | 新增运行时解析结果模型。 |
| `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs` | 新增统一解析器、应用层选点规则和运行时重载 key。 |
| `v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs` | 核心构建入口改为消费统一解析结果。 |
| `v2rayN/ServiceLib/Services/FailoverHealthService.cs` | 探测完成后按运行时目标变化决定是否重载。 |
```

`reapply.md` 至少包含：

```markdown
1. 先确认新版是否已原生支持混合核心转移。
2. 如果没有，先迁移 `FailoverRuntimeResolveResult` 和 `FailoverGroupManager.TryResolveRuntimeNode(...)`。
3. 再迁移 `CoreConfigContextBuilder.Build()` 的接入点。
4. 最后迁移 `FailoverHealthService` 的运行时目标变化重载逻辑。
```

`tests.md` 至少包含：

```markdown
| `$env:DOTNET_ROLL_FORWARD='LatestMajor'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "TryResolveRuntimeNode|FailoverHealthServiceTests|CoreConfigSpeedtestSuppressFailoverTests"` | 通过 | 覆盖统一解析器、混合核心重载和测速绕过保护。 |
```

- [ ] **步骤 4：生成补丁**

Run:

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes/012-all-protocol-transfer-support/patch.diff
```

Expected:

```text
patch.diff 只包含本轮功能相关源码与文档变更，不包含 `_local_changes` 自身说明文件
```

- [ ] **步骤 5：提交文档记录**

```powershell
git add _local_changes/README.md _local_changes/012-all-protocol-transfer-support
git commit -m "docs: record all-protocol transfer support change"
```

### Task 5：执行最终验证

**Files：**
- Test: `v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`
- Test: `v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`
- Test: `v2rayN/ServiceLib.Tests/CoreConfigSpeedtestSuppressFailoverTests.cs`
- Test: `v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj`
- Docs: `_local_changes/012-all-protocol-transfer-support/tests.md`

- [ ] **步骤 1：跑服务层自动化测试**

Run:

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'; dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "TryResolveRuntimeNode|CoreConfigContextBuilder_Build_MixedCoreQueueUsesResolvedDirectProfile|FailoverHealthServiceTests|CoreConfigSpeedtestSuppressFailoverTests"
```

Expected:

```text
PASS：统一解析、混合核心构建、运行时重载和 suppressFailover 保护全部通过
```

- [ ] **步骤 2：编译 Desktop 项目，防止签名改动破坏 UI 层**

Run:

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'; dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

Expected:

```text
Build succeeded.
```

- [ ] **步骤 3：手工验证混合核心故障转移**

场景：

```text
当前手动默认节点：Xray SOCKS
活动转移组：P1 = Xray SOCKS，P2 = sing_box TUIC
模式：故障转移
```

验证步骤：

```text
1. 开启故障转移。
2. 让 P1 探测失败，P2 探测成功。
3. 等待一轮健康探测结束。
```

预期：

```text
核心被重载到 P2 对应的 sing_box 配置，消息区和日志显示 P2 摘要；不再提示核心类型混用不支持。
```

- [ ] **步骤 4：手工验证跨核心延迟转移**

场景：

```text
当前手动默认节点：Xray VMess
活动转移组：P1 = Xray VMess，P2 = sing_box TUIC，P3 = sing_box Naive
模式：延迟转移
```

验证步骤：

```text
1. 开启延迟转移。
2. 让 P1 = 180ms，P2 = 35ms，P3 = 60ms。
3. 等待一轮健康探测完成。
4. 再让 P2 失败，P3 = 40ms。
```

预期：

```text
第一次重载切到 P2；第二次重载切到 P3；手动默认节点 `Config.IndexId` 不被改写。
```

- [ ] **步骤 5：回填测试记录并收尾提交**

在 `_local_changes/012-all-protocol-transfer-support/tests.md` 中记录：

```markdown
| 混合核心故障转移 | P1 失败、P2 成功后等待一轮探测 | 自动重载到 P2 对应核心 | 待执行 |
| 跨核心延迟转移 | P2 最低延迟后再失败，P3 次低延迟 | 先切 P2，再切 P3 | 待执行 |
```

Commit:

```powershell
git add _local_changes/012-all-protocol-transfer-support/tests.md
git commit -m "test: verify all-protocol transfer support"
```
