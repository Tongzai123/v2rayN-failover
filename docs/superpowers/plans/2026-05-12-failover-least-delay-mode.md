# 故障分组延迟转移模式实施计划

> **给代理执行者：** 必须按任务顺序执行。推荐使用 `superpowers:subagent-driven-development`，也可以使用 `superpowers:executing-plans`。每个步骤使用 checkbox (`- [ ]`) 跟踪。

**目标：** 把现有故障转移二态开关升级为 `故障转移 | 关闭 | 延迟转移` 三态模式，并在延迟转移模式下基于真实延迟把最低延迟队列节点放到运行态第一位。

**架构：** 本功能建立在 005 故障分组、006 健康状态、007 批量真实延迟探测之后；不推翻现有虚拟 fallback `PolicyGroup` 入口，只把“是否启用故障转移”的布尔状态升级为三态模式。延迟转移使用运行态排序，不改写 `FailoverGroupItem.Sort`，因此关闭或切回故障转移后能自然恢复用户维护的队列顺序。

**关键正确性要求：** `LeastDelay` 只从活动故障组已启用队列节点中选择运行态第一节点；切入 `LeastDelay` 时必须强制探测一次当前活动组所有已启用节点；后台探测完成后如果运行态第一节点发生变化，必须触发核心重载，且运行态第一节点未变化时不得重复重载。

**技术栈：** .NET、xUnit、SQLite-net、Avalonia、ReactiveUI、v2rayN ServiceLib、资源文件 `.resx`、本地改动记录 `_local_changes`。

---

## 总实施方案校准结论

本计划是对既有故障分组总方案的增量扩展，不替代旧阶段：

- 005 已完成故障分组、故障队列、活动故障组和核心 fallback 联动。
- 006 已完成故障队列健康状态和主动探测。
- 007 已把健康探测升级为批量真实延迟探测，并写回普通延迟列。
- 本计划新增的是 008 图标按钮之后的运行模式扩展：`Failover` 继续表示原 P1/P2/P3 优先队列；`LeastDelay` 表示从活动故障组已启用队列节点中选择真实延迟最低节点作为运行态第一位。

语义边界：

- `LeastDelay` 只对当前活动故障组生效。
- `LeastDelay` 只对活动故障组已启用队列节点生效；未启用候选节点不得参与最低延迟选择，即使它们有更低的历史延迟。
- 非活动故障组只是静态维护视图，仍可显示 `P*`，因为它没有参与当前运行态延迟转移。
- 活动故障组在 `LeastDelay` 模式下隐藏 `P*`，避免把运行态延迟选择误解为用户手动优先级。
- 切入 `LeastDelay` 时先保存模式，再强制探测活动故障组所有已启用队列节点，然后再触发核心重载，确保初次生成核心配置时尽量使用最新延迟结果。
- 后台每轮探测结束后要比较探测前后的运行态第一节点；只有发生变化时才发布 `AppEvents.ReloadRequested`，避免每轮探测都重启核心。
- 后续“外层熔断与恢复策略”仍保留为未来阶段，不混入本计划。

## 文件结构

- 新增 `v2rayN/ServiceLib/Enums/EFailoverMode.cs`：定义 `Off / Failover / LeastDelay` 三态。
- 修改 `v2rayN/ServiceLib/Models/Config.cs`：新增 `FailoverMode`，保留 `FailoverEnabled` 兼容旧配置。
- 修改 `v2rayN/ServiceLib/Handler/ConfigHandler.cs`：集中规范化旧布尔配置和新三态模式。
- 修改 `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`：新增运行态队列排序，供核心构建、健康探测重载判断和 UI 列表复用；最低延迟候选必须限定为已启用队列节点。
- 修改 `v2rayN/ServiceLib/Manager/FailoverHealthStateMachine.cs`：让探测开始标记支持强制探测，切入 `LeastDelay` 时可忽略失败冷却并探测所有已启用节点。
- 修改 `v2rayN/ServiceLib/Services/FailoverHealthService.cs`：用三态模式判断是否启动健康探测；新增强制探测入口；探测完成后在 `LeastDelay` 模式下按运行态第一节点变化触发核心重载。
- 修改 `v2rayN/ServiceLib/ViewModels/MsgViewModel.cs`：从二态开关改为三态模式命令和按钮状态。
- 修改 `v2rayN/v2rayN.Desktop/Views/MsgView.axaml`：把 `ToggleSwitch` 替换为三段按钮。
- 修改 `v2rayN/v2rayN.Desktop/Views/MsgView.axaml.cs`：绑定三段按钮命令和状态。
- 修改 `v2rayN/v2rayN.Desktop/Assets/GlobalStyles.axaml`：新增三段按钮样式；`关闭` 激活为蓝色，`故障转移/延迟转移` 激活为绿色。
- 修改 `v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`：活动故障组在延迟转移模式下使用运行态顺序并隐藏 `P*`。
- 修改 `v2rayN/ServiceLib/Models/ProfileItemModel.cs`：新增 `ShowFailoverPriorityLabel`，避免空字符串绑定承担显示语义。
- 修改 `v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`：把 `P*` 标签可见性绑定到明确属性。
- 修改 `v2rayN/ServiceLib/Resx/ResUI.resx`、`ResUI.zh-Hans.resx`、`ResUI.Designer.cs`：新增“关闭”“延迟转移”等文案。
- 修改测试文件：`FailoverGroupManagerTests.cs`、`FailoverHealthServiceTests.cs`、`FailoverHealthDisplayTests.cs`、`FailoverHealthStateMachineTests.cs`，新增 `FailoverModeConfigTests.cs` 和 `MsgViewModelFailoverModeTests.cs`。
- 新增 `_local_changes/009-failover-least-delay-mode/`：记录实际功能改动、迁移方式、验证结果和补丁。

## 任务 0：创建本地改动记录骨架

**文件：**

- 新增：`_local_changes/009-failover-least-delay-mode/README.md`
- 新增：`_local_changes/009-failover-least-delay-mode/files.md`
- 新增：`_local_changes/009-failover-least-delay-mode/reapply.md`
- 新增：`_local_changes/009-failover-least-delay-mode/tests.md`
- 新增：`_local_changes/009-failover-least-delay-mode/patch.diff`
- 修改：`_local_changes/README.md`

- [ ] **步骤 1：创建记录目录**

运行：

```powershell
New-Item -ItemType Directory -Force -Path "_local_changes\009-failover-least-delay-mode"
```

预期：目录存在，不影响已有 `001` 到 `008` 记录。

- [ ] **步骤 2：写入 `README.md`**

写入：

```markdown
# 故障分组延迟转移模式

## 基本信息

- 编号：`009`
- 目录：`_local_changes/009-failover-least-delay-mode/`
- 创建日期：`2026-05-12`
- 适用源码版本：`v2rayN-7.20.4-code` 本地副本
- 改动类型：功能
- 状态：实施中

## 改动目的

在现有故障分组和故障转移能力上新增“延迟转移”模式。用户可以在主界面三段按钮中选择 `故障转移`、`关闭` 或 `延迟转移`；延迟转移会复用后台真实延迟探测结果，让活动故障组队列中延迟最低的已启用节点处于运行态第一位。

## 用户可见行为

- 主界面原“故障转移”二态开关改为 `故障转移 | 关闭 | 延迟转移` 三段按钮。
- `关闭` 选中时为蓝色背景；`故障转移` 和 `延迟转移` 选中时为绿色背景。
- 三段按钮右侧继续显示活动故障组名称。
- 活动故障组在延迟转移模式下不显示 `P*` 标签，但继续显示健康状态标签。
- 关闭或切回故障转移后，活动故障组列表恢复用户维护的原始队列顺序。

## 设计取舍

采用运行态重排，不改写 `FailoverGroupItem.Sort`。这样不需要保存恢复快照，也不会因为应用异常退出损坏用户维护的故障队列顺序。

## 影响范围

- UI：消息区三段按钮、故障分组列表 `P*` 显示。
- 服务逻辑：故障转移模式状态、运行态队列排序、健康探测启动条件。
- 配置文件：`Config` 新增三态模式字段，保留旧布尔字段兼容。
- 数据结构：不新增数据库表，不修改 `FailoverGroupItem.Sort` 含义。
- 平台差异：Desktop Avalonia UI 变更。
- 兼容性：旧 `FailoverEnabled=true` 迁移为 `Failover`，旧 `false` 迁移为 `Off`。

## 代理安全影响评估

本改动会改变活动故障组虚拟 `PolicyGroup` 的子节点运行态顺序，从而影响代理入口优先选择；不修改 Tun、系统代理、路由规则、DNS、TLS、订阅下载、证书或核心更新策略。健康探测继续复用已有批量真实延迟探测，并保持 `suppressFailover: true`，避免被活动 fallback 组兜底后误判单节点状态。后台探测只在运行态第一节点变化时触发核心重载，避免无意义重启核心。
```

- [ ] **步骤 3：写入 `files.md`**

写入本计划“文件结构”中的文件清单，并为每个文件说明职责。必须明确：`FailoverGroupItem.Sort` 不改变语义，运行态顺序由 `FailoverGroupManager` 计算。

- [ ] **步骤 4：写入 `reapply.md`**

写入：

```markdown
# 重新应用步骤

1. 先确认新版源码中是否已有故障分组、健康探测和批量真实延迟探测能力。
2. 如果新版仍使用 `FailoverEnabled` 布尔开关，先迁移 `EFailoverMode` 和 `Config.FailoverMode` 兼容逻辑。
3. 迁移 `FailoverGroupManager` 的运行态排序方法，并确认 `TryBuildVirtualPolicyGroup` 使用运行态顺序。
4. 迁移 `MsgViewModel` 与 `MsgView.axaml` 的三段按钮。
5. 迁移 `ProfilesViewModel` 的活动故障组延迟模式排序和 `P*` 隐藏逻辑。
6. 迁移资源文案和样式。
7. 运行 `tests.md` 中记录的自动化测试和 Desktop 编译验证。
```

- [ ] **步骤 5：写入 `tests.md` 初始内容**

写入：

```markdown
# 验证记录

## 计划阶段

- 已检查 `docs/superpowers/specs/2026-05-12-failover-least-delay-mode-design.md`。
- 已确认本计划是 005/006/007 之后的增量，不修改 Tun、系统代理、路由、DNS、TLS、订阅下载、证书或核心更新策略。

## 实施阶段待执行

- `dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FailoverMode|FailoverGroupManager|FailoverHealth|MsgViewModel"`
- `dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "LeastDelay|FailoverHealthStateMachine|FailoverHealthService"`
- `dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj`
- `dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj`
- 手工检查三段按钮颜色和活动故障组延迟模式排序。
```

- [ ] **步骤 6：更新 `_local_changes/README.md`**

在改动索引表中追加：

```markdown
| 009 | `009-failover-least-delay-mode/` | 实施中 | 故障分组三态模式按钮与延迟最低运行态转移 |
```

- [ ] **步骤 7：提交记录骨架**

运行：

```powershell
git add _local_changes/009-failover-least-delay-mode _local_changes/README.md
git commit -m "docs: 记录故障分组延迟转移模式改动"
```

预期：提交只包含 `_local_changes` 记录文件。

## 任务 1：新增三态模式配置并兼容旧布尔语义

**文件：**

- 新增：`v2rayN/ServiceLib/Enums/EFailoverMode.cs`
- 修改：`v2rayN/ServiceLib/Models/Config.cs`
- 修改：`v2rayN/ServiceLib/Handler/ConfigHandler.cs`
- 新增：`v2rayN/ServiceLib.Tests/FailoverModeConfigTests.cs`

- [ ] **步骤 1：写失败测试**

新增 `v2rayN/ServiceLib.Tests/FailoverModeConfigTests.cs`：

```csharp
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Models;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverModeConfigTests
{
    [Fact]
    public void NormalizeFailoverMode_MigratesLegacyEnabledToFailover()
    {
        var config = new Config
        {
            FailoverEnabled = true,
            ActiveFailoverGroupId = "group-a",
        };

        ConfigHandler.NormalizeFailoverMode(config);

        Assert.Equal(EFailoverMode.Failover, config.FailoverMode);
        Assert.True(config.FailoverEnabled);
    }

    [Fact]
    public void NormalizeFailoverMode_MigratesLegacyDisabledToOff()
    {
        var config = new Config
        {
            FailoverEnabled = false,
            ActiveFailoverGroupId = "group-a",
        };

        ConfigHandler.NormalizeFailoverMode(config);

        Assert.Equal(EFailoverMode.Off, config.FailoverMode);
        Assert.False(config.FailoverEnabled);
    }

    [Fact]
    public void NormalizeFailoverMode_ClearsModeWhenActiveGroupMissing()
    {
        var config = new Config
        {
            FailoverMode = EFailoverMode.LeastDelay,
            FailoverEnabled = true,
            ActiveFailoverGroupId = "",
        };

        ConfigHandler.NormalizeFailoverMode(config);

        Assert.Equal(EFailoverMode.Off, config.FailoverMode);
        Assert.False(config.FailoverEnabled);
    }

    [Fact]
    public void NormalizeFailoverMode_KeepsExplicitLeastDelay()
    {
        var config = new Config
        {
            FailoverMode = EFailoverMode.LeastDelay,
            FailoverEnabled = false,
            ActiveFailoverGroupId = "group-a",
        };

        ConfigHandler.NormalizeFailoverMode(config);

        Assert.Equal(EFailoverMode.LeastDelay, config.FailoverMode);
        Assert.True(config.FailoverEnabled);
    }
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverModeConfigTests
```

预期：编译失败，提示 `EFailoverMode` 或 `NormalizeFailoverMode` 不存在。

- [ ] **步骤 3：新增枚举**

新增 `v2rayN/ServiceLib/Enums/EFailoverMode.cs`：

```csharp
namespace ServiceLib.Enums;

public enum EFailoverMode
{
    Off = 0,
    Failover = 1,
    LeastDelay = 2,
}
```

- [ ] **步骤 4：扩展配置模型**

在 `v2rayN/ServiceLib/Models/Config.cs` 的 `FailoverEnabled` 附近加入：

```csharp
public EFailoverMode FailoverMode { get; set; } = EFailoverMode.Off;
```

保持：

```csharp
public bool FailoverEnabled { get; set; }
public string ActiveFailoverGroupId { get; set; }
```

旧字段暂不删除，因为已有配置、测试和旧逻辑仍依赖它。

- [ ] **步骤 5：集中规范化配置**

在 `v2rayN/ServiceLib/Handler/ConfigHandler.cs` 中新增 public static 方法：

```csharp
public static void NormalizeFailoverMode(Config config)
{
    config.ActiveFailoverGroupId ??= string.Empty;

    if (config.FailoverMode == EFailoverMode.Off && config.FailoverEnabled)
    {
        config.FailoverMode = EFailoverMode.Failover;
    }

    if (config.ActiveFailoverGroupId.IsNullOrEmpty())
    {
        config.FailoverMode = EFailoverMode.Off;
    }

    config.FailoverEnabled = config.FailoverMode is EFailoverMode.Failover or EFailoverMode.LeastDelay;
}
```

把 `LoadConfig()` 中现有：

```csharp
config.ActiveFailoverGroupId ??= string.Empty;
config.FailoverEnabled = config.FailoverEnabled && config.ActiveFailoverGroupId.IsNotEmpty();
```

替换为：

```csharp
NormalizeFailoverMode(config);
```

- [ ] **步骤 6：运行测试确认通过**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverModeConfigTests
```

预期：`FailoverModeConfigTests` 全部通过。

- [ ] **步骤 7：提交**

运行：

```powershell
git add v2rayN/ServiceLib/Enums/EFailoverMode.cs v2rayN/ServiceLib/Models/Config.cs v2rayN/ServiceLib/Handler/ConfigHandler.cs v2rayN/ServiceLib.Tests/FailoverModeConfigTests.cs
git commit -m "feat: 新增故障转移三态模式配置"
```

## 任务 2：实现运行态延迟排序，不改写原始队列顺序

**文件：**

- 修改：`v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`

- [ ] **步骤 1：写最低延迟互换测试**

在 `FailoverGroupManagerTests` 中新增：

```csharp
[Fact]
public async Task GetQueueEntriesForMode_LeastDelaySwapsLowestDelayWithFirstEntry()
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

        Assert.Equal([p2, p1, p3], entries.Select(entry => entry.Profile.IndexId).ToArray());

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
```

- [ ] **步骤 2：写无有效延迟保持原顺序测试**

新增：

```csharp
[Fact]
public async Task GetQueueEntriesForMode_LeastDelayKeepsOriginalOrderWhenNoValidDelay()
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

        Assert.Equal([p1, p2], entries.Select(entry => entry.Profile.IndexId).ToArray());
    }
    finally
    {
        await Cleanup(groupId, p1, p2);
    }
}
```

- [ ] **步骤 3：写未启用节点不参与最低延迟测试**

新增：

```csharp
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

        var entries = await FailoverGroupManager.GetQueueEntriesForMode(groupId, false, EFailoverMode.LeastDelay);

        Assert.Equal([p3, p1, p2], entries.Select(entry => entry.Profile.IndexId).ToArray());
    }
    finally
    {
        await Cleanup(groupId, p1, p2, p3);
    }
}
```

这个测试故意传入 `enabledOnly: false`，用于防止 UI 维护视图把未启用候选节点误纳入 `LeastDelay` 运行态排序。

- [ ] **步骤 4：运行测试确认失败**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "GetQueueEntriesForMode"
```

预期：编译失败，提示 `GetQueueEntriesForMode` 不存在。

- [ ] **步骤 5：新增运行态排序方法**

在 `FailoverGroupManager` 中新增：

```csharp
public static async Task<List<FailoverQueueEntry>> GetQueueEntriesForMode(
    string groupId,
    bool enabledOnly,
    EFailoverMode mode)
{
    var entries = await GetQueueEntries(groupId, enabledOnly);
    return ApplyRuntimeOrder(entries, mode);
}

public static List<FailoverQueueEntry> ApplyRuntimeOrder(
    IReadOnlyList<FailoverQueueEntry> entries,
    EFailoverMode mode)
{
    var ordered = entries.ToList();
    if (mode != EFailoverMode.LeastDelay || ordered.Count <= 1)
    {
        return ordered;
    }

    var best = ordered
        .Select((entry, index) => new { Entry = entry, Index = index })
        .Where(x => x.Entry.Item.Enabled
            && x.Entry.Item.LastStatus == FailoverHealthStatus.Normal
            && x.Entry.Item.LastDelay > 0)
        .OrderBy(x => x.Entry.Item.LastDelay)
        .ThenBy(x => x.Index)
        .FirstOrDefault();
    if (best == null || best.Index == 0)
    {
        return ordered;
    }

    (ordered[0], ordered[best.Index]) = (ordered[best.Index], ordered[0]);
    return ordered;
}
```

- [ ] **步骤 6：让虚拟组构建使用运行态顺序**

在 `TryBuildVirtualPolicyGroup` 中把：

```csharp
if (!config.FailoverEnabled)
```

替换为：

```csharp
if (config.FailoverMode == EFailoverMode.Off)
```

把：

```csharp
var queueEntries = await GetQueueEntries(activeGroup.Id, true);
```

替换为：

```csharp
var queueEntries = await GetQueueEntriesForMode(activeGroup.Id, true, config.FailoverMode);
```

保留 `MultipleLoad = EMultipleLoad.Fallback`，不要改成核心内置 `LeastPing`。

- [ ] **步骤 7：调整旧测试配置**

在 `FailoverGroupManagerTests` 中所有需要开启故障转移的 `Config` 增加：

```csharp
FailoverMode = EFailoverMode.Failover,
```

新增一个构建测试确认 `LeastDelay` 会影响核心子节点顺序：

```csharp
[Fact]
public async Task TryBuildVirtualPolicyGroup_LeastDelayUsesRuntimeOrder()
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
        first.LastStatus = FailoverHealthStatus.Normal;
        first.LastDelay = 90;
        var second = CreateFailoverItem(groupId, p2, 2);
        second.LastStatus = FailoverHealthStatus.Normal;
        second.LastDelay = 20;
        await SQLiteHelper.Instance.InsertAllAsync(new[] { first, second });

        var config = new Config
        {
            FailoverMode = EFailoverMode.LeastDelay,
            FailoverEnabled = true,
            ActiveFailoverGroupId = groupId,
            TunModeItem = new(),
            SimpleDNSItem = new(),
            RoutingBasicItem = new(),
        };

        var result = await FailoverGroupManager.TryBuildVirtualPolicyGroup(config, CreateProxy("current", "source-sub", ECoreType.Xray));

        Assert.True(result.Success);
        Assert.Equal($"{p2},{p1}", result.Node!.GetProtocolExtra().ChildItems);
    }
    finally
    {
        await Cleanup(groupId, p1, p2);
    }
}
```

- [ ] **步骤 8：运行测试确认通过**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FailoverGroupManagerTests"
```

预期：全部通过。

- [ ] **步骤 9：提交**

运行：

```powershell
git add v2rayN/ServiceLib/Manager/FailoverGroupManager.cs v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs
git commit -m "feat: 按延迟计算故障组运行态顺序"
```

## 任务 3：让健康探测和核心上下文统一使用三态模式

**文件：**

- 修改：`v2rayN/ServiceLib/Manager/FailoverHealthStateMachine.cs`
- 修改：`v2rayN/ServiceLib/Services/FailoverHealthService.cs`
- 修改：`v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverHealthStateMachineTests.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`

- [ ] **步骤 1：写健康探测三态测试**

在 `FailoverHealthServiceTests` 中新增：

```csharp
[Fact]
public async Task CheckOnceAsync_RunsWhenModeIsLeastDelay()
{
    PrepareTables();
    var suffix = Utils.GetGuid(false);
    var groupId = $"failover-{suffix}";
    var profileId = $"profile-{suffix}";
    var config = new Config
    {
        FailoverMode = EFailoverMode.LeastDelay,
        FailoverEnabled = true,
        ActiveFailoverGroupId = groupId,
    };

    try
    {
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
        await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
        await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, profileId, 1));

        var probe = new FakeBatchProbe([new(profileId, FailoverHealthProbeResult.Success(45))]);
        var service = new FailoverHealthService(config, probe);
        await service.CheckOnceAsync();

        Assert.Equal(1, probe.BatchCallCount);
    }
    finally
    {
        await Cleanup(groupId, profileId);
    }
}

[Fact]
public async Task CheckOnceAsync_DoesNotRunWhenModeIsOff()
{
    PrepareTables();
    var suffix = Utils.GetGuid(false);
    var groupId = $"failover-{suffix}";
    var profileId = $"profile-{suffix}";
    var config = new Config
    {
        FailoverMode = EFailoverMode.Off,
        FailoverEnabled = false,
        ActiveFailoverGroupId = groupId,
    };

    try
    {
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
        await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
        await SQLiteHelper.Instance.ReplaceAsync(CreateFailoverItem(groupId, profileId, 1));

        var probe = new FakeBatchProbe([new(profileId, FailoverHealthProbeResult.Success(45))]);
        var service = new FailoverHealthService(config, probe);
        await service.CheckOnceAsync();

        Assert.Equal(0, probe.BatchCallCount);
    }
    finally
    {
        await Cleanup(groupId, profileId);
    }
}
```

- [ ] **步骤 2：写强制探测和运行态第一节点变化重载测试**

在 `FailoverHealthStateMachineTests` 中新增：

```csharp
[Fact]
public void TryMarkProbeStarting_ForceIgnoresCooldown()
{
    var item = new FailoverGroupItem
    {
        LastStatus = FailoverHealthStatus.Failed,
        CooldownUntilTime = 2000,
    };

    var result = FailoverHealthStateMachine.TryMarkProbeStarting(item, now: 1000, force: true);

    Assert.True(result);
    Assert.Equal(FailoverHealthStatus.Probing, item.LastStatus);
    Assert.Equal(1000, item.LastProbeTime);
}
```

在 `FailoverHealthServiceTests` 中新增：

```csharp
[Fact]
public async Task CheckActiveGroupOnceAsync_ForceProbesCoolingFailedEntry()
{
    PrepareTables();
    var suffix = Utils.GetGuid(false);
    var groupId = $"failover-{suffix}";
    var profileId = $"profile-{suffix}";
    var config = new Config
    {
        FailoverMode = EFailoverMode.LeastDelay,
        FailoverEnabled = true,
        ActiveFailoverGroupId = groupId,
    };

    try
    {
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
        await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(profileId));
        var item = CreateFailoverItem(groupId, profileId, 1);
        item.LastStatus = FailoverHealthStatus.Failed;
        item.CooldownUntilTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 300_000;
        await SQLiteHelper.Instance.ReplaceAsync(item);

        var probe = new FakeBatchProbe([new(profileId, FailoverHealthProbeResult.Success(45))]);
        var service = new FailoverHealthService(config, probe);
        await service.CheckActiveGroupOnceAsync(force: true, reloadOnLeastDelayChange: false);

        Assert.Equal(1, probe.BatchCallCount);
    }
    finally
    {
        await Cleanup(groupId, profileId);
    }
}

[Fact]
public async Task CheckOnceAsync_LeastDelayPublishesReloadWhenRuntimeFirstChanges()
{
    PrepareTables();
    var suffix = Utils.GetGuid(false);
    var groupId = $"failover-{suffix}";
    var p1 = $"p1-{suffix}";
    var p2 = $"p2-{suffix}";
    var config = new Config
    {
        FailoverMode = EFailoverMode.LeastDelay,
        FailoverEnabled = true,
        ActiveFailoverGroupId = groupId,
    };
    var reloadCount = 0;
    using var subscription = AppEvents.ReloadRequested.AsObservable().Subscribe(_ => reloadCount++);

    try
    {
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
        await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p1));
        await SQLiteHelper.Instance.ReplaceAsync(CreateProxy(p2));
        var first = CreateFailoverItem(groupId, p1, 1);
        first.LastStatus = FailoverHealthStatus.Normal;
        first.LastDelay = 80;
        var second = CreateFailoverItem(groupId, p2, 2);
        second.LastStatus = FailoverHealthStatus.Normal;
        second.LastDelay = 120;
        await SQLiteHelper.Instance.InsertAllAsync(new[] { first, second });

        var probe = new FakeBatchProbe([
            new(p1, FailoverHealthProbeResult.Success(90)),
            new(p2, FailoverHealthProbeResult.Success(20)),
        ]);
        var service = new FailoverHealthService(config, probe);
        await service.CheckOnceAsync();

        Assert.Equal(1, reloadCount);
    }
    finally
    {
        await Cleanup(groupId, p1, p2);
    }
}
```

- [ ] **步骤 3：运行测试确认失败或旧逻辑不完整**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "CheckOnceAsync_RunsWhenModeIsLeastDelay|CheckOnceAsync_DoesNotRunWhenModeIsOff|TryMarkProbeStarting_ForceIgnoresCooldown|CheckActiveGroupOnceAsync_ForceProbesCoolingFailedEntry|CheckOnceAsync_LeastDelayPublishesReloadWhenRuntimeFirstChanges"
```

预期：至少 `LeastDelay`、强制探测或重载测试失败，说明服务仍只依赖旧 `FailoverEnabled`，且还没有强制探测和运行态第一节点变化重载逻辑。

- [ ] **步骤 4：让状态机支持强制探测**

把 `FailoverHealthStateMachine.TryMarkProbeStarting` 签名改为：

```csharp
public static bool TryMarkProbeStarting(FailoverGroupItem item, long now, bool force = false)
{
    if (!force
        && item.LastStatus == FailoverHealthStatus.Failed
        && item.CooldownUntilTime.HasValue
        && item.CooldownUntilTime.Value > now)
    {
        return false;
    }

    item.LastStatus = FailoverHealthStatus.Probing;
    item.LastProbeTime = now;
    return true;
}
```

- [ ] **步骤 5：修改健康探测入口、强制探测入口和重载触发**

在 `FailoverHealthService` 中把公开入口改为：

```csharp
public Task CheckOnceAsync()
    => CheckActiveGroupOnceAsync(force: false, reloadOnLeastDelayChange: true);

public Task CheckOnceAsync(CancellationToken cancellationToken)
    => CheckActiveGroupOnceAsync(cancellationToken, force: false, reloadOnLeastDelayChange: true);

public Task CheckActiveGroupOnceAsync(bool force = false, bool reloadOnLeastDelayChange = true)
    => CheckActiveGroupOnceAsync(_cts?.Token ?? CancellationToken.None, force, reloadOnLeastDelayChange);
```

新增私有实现：

```csharp
private async Task CheckActiveGroupOnceAsync(
    CancellationToken cancellationToken,
    bool force,
    bool reloadOnLeastDelayChange)
{
    if (_config.FailoverMode == EFailoverMode.Off || _config.ActiveFailoverGroupId.IsNullOrEmpty())
    {
        return;
    }

    var previousRuntimeFirst = reloadOnLeastDelayChange
        ? await GetLeastDelayRuntimeFirstProfileId()
        : null;
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var entries = force
        ? await FailoverGroupManager.GetQueueEntries(_config.ActiveFailoverGroupId, true)
        : await FailoverGroupManager.GetDueHealthCheckEntries(_config.ActiveFailoverGroupId, now);
    var probeEntries = new List<(FailoverQueueEntry Entry, string PreviousStatus)>();
    foreach (var entry in entries)
    {
        var previousStatus = entry.Item.LastStatus;
        if (!FailoverHealthStateMachine.TryMarkProbeStarting(entry.Item, now, force))
        {
            continue;
        }

        probeEntries.Add((entry, previousStatus));
    }

    if (probeEntries.Count == 0)
    {
        return;
    }

    await SQLiteHelper.Instance.UpdateAllAsync(probeEntries.Select(x => x.Entry.Item));
    AppEvents.FailoverHealthChangedRequested.Publish();

    var results = await ProbeAsync(probeEntries.Select(x => x.Entry.Profile).ToList(), cancellationToken);
    foreach (var probeEntry in probeEntries)
    {
        var entry = probeEntry.Entry;
        var result = results.TryGetValue(entry.Profile.IndexId, out var found)
            ? found
            : FailoverHealthProbeResult.Failure("request-failed");
        if (result.Cancelled)
        {
            entry.Item.LastStatus = probeEntry.PreviousStatus;
            continue;
        }

        FailoverHealthStateMachine.ApplyProbeResult(entry.Item, result, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    await SQLiteHelper.Instance.UpdateAllAsync(probeEntries.Select(x => x.Entry.Item));
    AppEvents.FailoverHealthChangedRequested.Publish();
    if (reloadOnLeastDelayChange)
    {
        await PublishLeastDelayReloadIfRuntimeFirstChanged(previousRuntimeFirst);
    }
}
```

新增辅助方法：

```csharp
private async Task<string?> GetLeastDelayRuntimeFirstProfileId()
{
    if (_config.FailoverMode != EFailoverMode.LeastDelay || _config.ActiveFailoverGroupId.IsNullOrEmpty())
    {
        return null;
    }

    var entries = await FailoverGroupManager.GetQueueEntriesForMode(
        _config.ActiveFailoverGroupId,
        true,
        EFailoverMode.LeastDelay);
    return entries.FirstOrDefault()?.Profile.IndexId;
}

private async Task PublishLeastDelayReloadIfRuntimeFirstChanged(string? previousRuntimeFirst)
{
    if (_config.FailoverMode != EFailoverMode.LeastDelay)
    {
        return;
    }

    var currentRuntimeFirst = await GetLeastDelayRuntimeFirstProfileId();
    if (currentRuntimeFirst != previousRuntimeFirst)
    {
        AppEvents.ReloadRequested.Publish();
    }
}
```

旧的 `CheckOnceAsync(CancellationToken cancellationToken)` 中这段判断必须移入上面的私有实现：

```csharp
if (!_config.FailoverEnabled || _config.ActiveFailoverGroupId.IsNullOrEmpty())
```

并替换为：

```csharp
if (_config.FailoverMode == EFailoverMode.Off || _config.ActiveFailoverGroupId.IsNullOrEmpty())
```

- [ ] **步骤 6：修改核心上下文入口**

在 `CoreConfigContextBuilder.Build(...)` 中把：

```csharp
if (config.FailoverEnabled && !suppressFailover)
```

替换为：

```csharp
if (config.FailoverMode != EFailoverMode.Off && !suppressFailover)
```

保持 `suppressFailover: true` 保护语义不变。

- [ ] **步骤 7：运行相关测试**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FailoverHealthServiceTests|FailoverHealthStateMachineTests|CoreConfigContextBuilder_BuildSuppressFailoverKeepsRequestedNode"
```

预期：全部通过。

- [ ] **步骤 8：提交**

运行：

```powershell
git add v2rayN/ServiceLib/Manager/FailoverHealthStateMachine.cs v2rayN/ServiceLib/Services/FailoverHealthService.cs v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs v2rayN/ServiceLib.Tests/FailoverHealthStateMachineTests.cs v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs
git commit -m "feat: 使用三态模式驱动故障探测入口"
```

## 任务 4：调整故障分组列表的运行态顺序和 `P*` 显示

**文件：**

- 修改：`v2rayN/ServiceLib/Models/ProfileItemModel.cs`
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`
- 修改：`v2rayN/ServiceLib.Tests/FailoverHealthDisplayTests.cs`

- [ ] **步骤 1：写 `P*` 显示规则测试**

在 `FailoverHealthDisplayTests` 中新增：

```csharp
[Theory]
[InlineData(EFailoverMode.Off, true)]
[InlineData(EFailoverMode.Failover, true)]
[InlineData(EFailoverMode.LeastDelay, false)]
public void ShouldShowFailoverPriorityLabel_HidesOnlyActiveGroupInLeastDelay(
    EFailoverMode mode,
    bool expected)
{
    var result = ProfilesViewModel.ShouldShowFailoverPriorityLabel(
        isFailoverGroup: true,
        isInQueue: true,
        mode,
        activeFailoverGroupId: "active",
        currentSubId: "active");

    Assert.Equal(expected, result);
}

[Fact]
public void ShouldShowFailoverPriorityLabel_KeepsInactiveGroupPriorityInLeastDelay()
{
    var result = ProfilesViewModel.ShouldShowFailoverPriorityLabel(
        isFailoverGroup: true,
        isInQueue: true,
        EFailoverMode.LeastDelay,
        activeFailoverGroupId: "active",
        currentSubId: "other");

    Assert.True(result);
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter ShouldShowFailoverPriorityLabel
```

预期：编译失败，提示方法不存在。

- [ ] **步骤 3：扩展列表模型**

在 `ProfileItemModel.cs` 增加：

```csharp
public bool ShowFailoverPriorityLabel { get; set; }
```

- [ ] **步骤 4：新增显示规则方法**

在 `ProfilesViewModel` 中新增 public static 方法：

```csharp
public static bool ShouldShowFailoverPriorityLabel(
    bool isFailoverGroup,
    bool isInQueue,
    EFailoverMode mode,
    string? activeFailoverGroupId,
    string? currentSubId)
{
    if (!isFailoverGroup || !isInQueue)
    {
        return false;
    }

    return mode != EFailoverMode.LeastDelay
        || activeFailoverGroupId.IsNullOrEmpty()
        || currentSubId.IsNullOrEmpty()
        || activeFailoverGroupId != currentSubId;
}
```

- [ ] **步骤 5：让列表使用运行态顺序**

在 `GetProfileItemsEx` 中把优先级来源从：

```csharp
var priorityMap = isFailoverGroup ? await FailoverGroupManager.GetQueuePriorityMap(subid) : [];
var failoverEntryMap = isFailoverGroup
    ? (await FailoverGroupManager.GetQueueEntries(subid, false))
```

调整为：

```csharp
var runtimeQueueEntries = isFailoverGroup
    ? await FailoverGroupManager.GetQueueEntriesForMode(
        subid,
        true,
        _config.ActiveFailoverGroupId == subid ? _config.FailoverMode : EFailoverMode.Failover)
    : [];
var allQueueEntries = isFailoverGroup
    ? await FailoverGroupManager.GetQueueEntries(subid, false)
    : [];
var queueEntries = runtimeQueueEntries
    .Concat(allQueueEntries.Where(entry => !entry.Item.Enabled))
    .ToList();
var priorityMap = new Dictionary<string, int>();
for (var i = 0; i < runtimeQueueEntries.Count; i++)
{
    priorityMap[runtimeQueueEntries[i].Profile.IndexId] = i + 1;
}
var failoverEntryMap = isFailoverGroup
    ? queueEntries.ToDictionary(entry => entry.Profile.IndexId, entry => entry.Item)
```

说明：`runtimeQueueEntries` 始终只取已启用队列，确保未启用候选节点不参与 `LeastDelay` 排序；`allQueueEntries` 只用于把未启用候选节点补回维护视图。非活动故障组按 `Failover` 静态顺序展示，避免全局运行模式影响非活动组维护视图。

- [ ] **步骤 6：设置 `ShowFailoverPriorityLabel`**

在构造 `ProfileItemModel` 时，把：

```csharp
FailoverPriorityLabel = priorityMap.TryGetValue(t.IndexId, out var priority) ? $"P{priority}" : string.Empty,
```

扩展为：

```csharp
FailoverPriorityLabel = priorityMap.TryGetValue(t.IndexId, out var priority) ? $"P{priority}" : string.Empty,
ShowFailoverPriorityLabel = ShouldShowFailoverPriorityLabel(
    isFailoverGroup,
    priorityMap.ContainsKey(t.IndexId),
    _config.FailoverMode,
    _config.ActiveFailoverGroupId,
    subid),
```

并把：

```csharp
IsCurrentFailoverPreferred = isFailoverGroup
    && _config.FailoverEnabled
    && _config.ActiveFailoverGroupId == subid
    && priorityMap.GetValueOrDefault(t.IndexId) == 1,
```

替换为：

```csharp
IsCurrentFailoverPreferred = isFailoverGroup
    && _config.FailoverMode != EFailoverMode.Off
    && _config.ActiveFailoverGroupId == subid
    && priorityMap.GetValueOrDefault(t.IndexId) == 1,
```

- [ ] **步骤 7：调整选中逻辑**

在 `RefreshServersBiz` 中把：

```csharp
if (CurrentGroupIsFailoverGroup && _config.FailoverEnabled && _config.ActiveFailoverGroupId == _config.SubIndexId)
```

替换为：

```csharp
if (CurrentGroupIsFailoverGroup && _config.FailoverMode != EFailoverMode.Off && _config.ActiveFailoverGroupId == _config.SubIndexId)
```

- [ ] **步骤 8：调整 XAML 可见性**

在 `ProfilesView.axaml` 中找到 `Content="{Binding FailoverPriorityLabel}"` 的标签，把可见性从：

```xml
IsVisible="{Binding IsInFailoverQueue}"
```

改为：

```xml
IsVisible="{Binding ShowFailoverPriorityLabel}"
```

- [ ] **步骤 9：运行测试**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FailoverHealthDisplayTests|FailoverGroupManagerTests"
```

预期：全部通过。

- [ ] **步骤 10：提交**

运行：

```powershell
git add v2rayN/ServiceLib/Models/ProfileItemModel.cs v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml v2rayN/ServiceLib.Tests/FailoverHealthDisplayTests.cs
git commit -m "feat: 延迟转移模式隐藏活动组优先级标签"
```

## 任务 5：实现主界面三段模式按钮的 ViewModel 行为

**文件：**

- 修改：`v2rayN/ServiceLib/ViewModels/MsgViewModel.cs`
- 新增：`v2rayN/ServiceLib.Tests/MsgViewModelFailoverModeTests.cs`

- [ ] **步骤 1：写纯逻辑测试**

新增 `MsgViewModelFailoverModeTests.cs`：

```csharp
using ServiceLib.Enums;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests;

public class MsgViewModelFailoverModeTests
{
    [Theory]
    [InlineData(EFailoverMode.Off, true, false, false)]
    [InlineData(EFailoverMode.Failover, false, true, false)]
    [InlineData(EFailoverMode.LeastDelay, false, false, true)]
    public void ApplyFailoverModeDisplay_SetsExactlyOneModeActive(
        EFailoverMode mode,
        bool off,
        bool failover,
        bool leastDelay)
    {
        var state = MsgViewModel.GetFailoverModeDisplayState(mode);

        Assert.Equal(off, state.IsOff);
        Assert.Equal(failover, state.IsFailover);
        Assert.Equal(leastDelay, state.IsLeastDelay);
        Assert.Single(new[] { state.IsOff, state.IsFailover, state.IsLeastDelay }, x => x);
    }
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter MsgViewModelFailoverModeTests
```

预期：编译失败，提示 `GetFailoverModeDisplayState` 不存在。

- [ ] **步骤 3：新增显示状态 record**

在 `MsgViewModel` 类内部新增：

```csharp
public sealed record FailoverModeDisplayState(bool IsOff, bool IsFailover, bool IsLeastDelay);

public static FailoverModeDisplayState GetFailoverModeDisplayState(EFailoverMode mode)
{
    return new FailoverModeDisplayState(
        mode == EFailoverMode.Off,
        mode == EFailoverMode.Failover,
        mode == EFailoverMode.LeastDelay);
}
```

- [ ] **步骤 4：替换二态属性**

保留旧 `FailoverEnabled` 的 ViewModel 属性会造成 UI 语义混乱。把：

```csharp
[Reactive]
public bool FailoverEnabled { get; set; }
```

替换为：

```csharp
[Reactive]
public EFailoverMode FailoverMode { get; set; }

[Reactive]
public bool IsFailoverModeOff { get; set; }

[Reactive]
public bool IsFailoverModeFailover { get; set; }

[Reactive]
public bool IsFailoverModeLeastDelay { get; set; }

public ReactiveCommand<EFailoverMode, Unit> ChangeFailoverModeCmd { get; }
```

把 `_suppressFailoverToggle` 重命名为：

```csharp
private bool _suppressFailoverModeChange;
```

- [ ] **步骤 5：初始化三态命令**

构造函数中把：

```csharp
FailoverEnabled = _config.FailoverEnabled;
```

替换为：

```csharp
FailoverMode = _config.FailoverMode;
ApplyFailoverModeDisplay(FailoverMode);
ChangeFailoverModeCmd = ReactiveCommand.CreateFromTask<EFailoverMode>(ChangeFailoverMode);
```

删除旧的：

```csharp
this.WhenAnyValue(x => x.FailoverEnabled)
    .Skip(1)
    .Subscribe(enabled => _ = ToggleFailover(enabled));
```

- [ ] **步骤 6：新增显示同步方法**

在 `MsgViewModel` 中新增：

```csharp
private void ApplyFailoverModeDisplay(EFailoverMode mode)
{
    var state = GetFailoverModeDisplayState(mode);
    IsFailoverModeOff = state.IsOff;
    IsFailoverModeFailover = state.IsFailover;
    IsFailoverModeLeastDelay = state.IsLeastDelay;
}
```

- [ ] **步骤 7：替换模式切换方法**

用下面方法替换 `ToggleFailover(bool enabled)`：

```csharp
private async Task ChangeFailoverMode(EFailoverMode mode)
{
    if (_suppressFailoverModeChange || mode == _config.FailoverMode)
    {
        return;
    }

    if (mode == EFailoverMode.Off)
    {
        _config.FailoverMode = EFailoverMode.Off;
        _config.FailoverEnabled = false;
        FailoverHealthService.Instance.Stop();
        await ConfigHandler.SaveConfig(_config);
        AppEvents.ReloadRequested.Publish();
        AppEvents.FailoverStateChangedRequested.Publish();
        await RefreshFailoverState();
        return;
    }

    var currentNode = await AppManager.Instance.GetProfileItem(_config.IndexId);
    if (currentNode == null)
    {
        NoticeManager.Instance.Enqueue(ResUI.PleaseSelectServer);
        await DisableFailover();
        return;
    }

    _config.FailoverMode = mode;
    _config.FailoverEnabled = true;
    var validator = await FailoverGroupManager.ValidateActiveFailoverGroup(_config, currentNode);
    if (!validator.Success)
    {
        NoticeManager.Instance.NotifyValidatorResult(validator);
        await DisableFailover();
        return;
    }

    await ConfigHandler.SaveConfig(_config);
    FailoverHealthService.Instance.Start();
    if (mode == EFailoverMode.LeastDelay)
    {
        await FailoverHealthService.Instance.CheckActiveGroupOnceAsync(
            force: true,
            reloadOnLeastDelayChange: false);
    }

    NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
    AppEvents.ReloadRequested.Publish();
    AppEvents.FailoverStateChangedRequested.Publish();
    await RefreshFailoverState();
}
```

说明：切到 `LeastDelay` 时，强制探测使用 `reloadOnLeastDelayChange: false`，然后由后面的 `AppEvents.ReloadRequested.Publish()` 统一触发一次核心重载，避免切换过程中重复重载。后台定时探测仍使用 `reloadOnLeastDelayChange: true`，只在运行态第一节点变化时重载。

- [ ] **步骤 8：更新刷新和关闭逻辑**

在 `RefreshFailoverState` 中：

```csharp
if (activeGroup == null && _config.FailoverEnabled)
```

替换为：

```csharp
if (activeGroup == null && _config.FailoverMode != EFailoverMode.Off)
```

失效时设置：

```csharp
_config.FailoverMode = EFailoverMode.Off;
_config.FailoverEnabled = false;
```

启动判断改为：

```csharp
if (_config.FailoverMode != EFailoverMode.Off)
```

最后同步 UI：

```csharp
_suppressFailoverModeChange = true;
FailoverMode = _config.FailoverMode;
ApplyFailoverModeDisplay(FailoverMode);
_suppressFailoverModeChange = false;
```

在 `DisableFailover` 中同样设置：

```csharp
_config.FailoverMode = EFailoverMode.Off;
_config.FailoverEnabled = false;
```

- [ ] **步骤 9：运行测试**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter MsgViewModelFailoverModeTests
```

预期：通过。

- [ ] **步骤 10：提交**

运行：

```powershell
git add v2rayN/ServiceLib/ViewModels/MsgViewModel.cs v2rayN/ServiceLib.Tests/MsgViewModelFailoverModeTests.cs
git commit -m "feat: 支持主界面故障组三态模式"
```

## 任务 6：实现三段按钮 UI、颜色和文案

**文件：**

- 修改：`v2rayN/v2rayN.Desktop/Views/MsgView.axaml`
- 修改：`v2rayN/v2rayN.Desktop/Views/MsgView.axaml.cs`
- 修改：`v2rayN/v2rayN.Desktop/Assets/GlobalStyles.axaml`
- 修改：`v2rayN/ServiceLib/Resx/ResUI.resx`
- 修改：`v2rayN/ServiceLib/Resx/ResUI.zh-Hans.resx`
- 修改：`v2rayN/ServiceLib/Resx/ResUI.Designer.cs`

- [ ] **步骤 1：新增资源文案**

在 `ResUI.resx` 中新增：

```xml
  <data name="TbFailoverModeOff" xml:space="preserve">
    <value>Off</value>
  </data>
  <data name="TbFailoverModeLeastDelay" xml:space="preserve">
    <value>Least delay</value>
  </data>
```

在 `ResUI.zh-Hans.resx` 中新增：

```xml
  <data name="TbFailoverModeOff" xml:space="preserve">
    <value>关闭</value>
  </data>
  <data name="TbFailoverModeLeastDelay" xml:space="preserve">
    <value>延迟转移</value>
  </data>
```

已有 `TbFailover` 可继续作为“故障转移”按钮文案。

- [ ] **步骤 2：更新 `ResUI.Designer.cs`**

按现有 `ResUI.Designer.cs` 模式新增：

```csharp
public static string TbFailoverModeOff {
    get {
        return ResourceManager.GetString("TbFailoverModeOff", resourceCulture);
    }
}

public static string TbFailoverModeLeastDelay {
    get {
        return ResourceManager.GetString("TbFailoverModeLeastDelay", resourceCulture);
    }
}
```

- [ ] **步骤 3：新增三段按钮样式**

在 `GlobalStyles.axaml` 中加入：

```xml
    <Style Selector="StackPanel.FailoverModeSwitch">
        <Setter Property="Orientation" Value="Horizontal" />
    </Style>

    <Style Selector="Button.FailoverModeSegment">
        <Setter Property="MinWidth" Value="72" />
        <Setter Property="Height" Value="28" />
        <Setter Property="Padding" Value="10,0" />
        <Setter Property="Margin" Value="0" />
        <Setter Property="CornerRadius" Value="0" />
        <Setter Property="Theme" Value="{DynamicResource BorderlessButton}" />
    </Style>

    <Style Selector="Button.FailoverModeSegment.First">
        <Setter Property="CornerRadius" Value="6,0,0,6" />
    </Style>

    <Style Selector="Button.FailoverModeSegment.Last">
        <Setter Property="CornerRadius" Value="0,6,6,0" />
    </Style>

    <Style Selector="Button.FailoverModeSegment.OffMode.active">
        <Setter Property="Background" Value="#2878D7" />
        <Setter Property="Foreground" Value="White" />
    </Style>

    <Style Selector="Button.FailoverModeSegment.ActiveMode.active">
        <Setter Property="Background" Value="#18A672" />
        <Setter Property="Foreground" Value="White" />
    </Style>
```

如果项目主题覆盖 `Background`，执行时改用项目已存在的成功/主色资源，但必须保持“关闭蓝色、故障转移和延迟转移绿色”的视觉结果。

- [ ] **步骤 4：替换 `MsgView.axaml` 二态开关**

删除：

```xml
<TextBlock
    Margin="{StaticResource MarginLr8}"
    VerticalAlignment="Center"
    Text="{x:Static resx:ResUI.TbFailover}" />
<ToggleSwitch
    x:Name="togFailover"
    Margin="{StaticResource MarginLr8}"
    HorizontalAlignment="Left"
    Theme="{DynamicResource SimpleToggleSwitch}" />
```

替换为：

```xml
<StackPanel
    Margin="{StaticResource MarginLr8}"
    Classes="FailoverModeSwitch">
    <Button
        x:Name="btnFailoverModeFailover"
        Classes="FailoverModeSegment First ActiveMode"
        Classes.active="{Binding IsFailoverModeFailover}"
        Content="{x:Static resx:ResUI.TbFailover}" />
    <Button
        x:Name="btnFailoverModeOff"
        Classes="FailoverModeSegment OffMode"
        Classes.active="{Binding IsFailoverModeOff}"
        Content="{x:Static resx:ResUI.TbFailoverModeOff}" />
    <Button
        x:Name="btnFailoverModeLeastDelay"
        Classes="FailoverModeSegment Last ActiveMode"
        Classes.active="{Binding IsFailoverModeLeastDelay}"
        Content="{x:Static resx:ResUI.TbFailoverModeLeastDelay}" />
</StackPanel>
```

- [ ] **步骤 5：绑定按钮命令**

在 `MsgView.axaml.cs` 的 `WhenActivated` 中删除：

```csharp
this.Bind(ViewModel, vm => vm.FailoverEnabled, v => v.togFailover.IsChecked).DisposeWith(disposables);
```

新增：

```csharp
btnFailoverModeFailover.Command = ViewModel?.ChangeFailoverModeCmd;
btnFailoverModeFailover.CommandParameter = EFailoverMode.Failover;
btnFailoverModeOff.Command = ViewModel?.ChangeFailoverModeCmd;
btnFailoverModeOff.CommandParameter = EFailoverMode.Off;
btnFailoverModeLeastDelay.Command = ViewModel?.ChangeFailoverModeCmd;
btnFailoverModeLeastDelay.CommandParameter = EFailoverMode.LeastDelay;
```

如果直接设置 `Command` 导致激活生命周期问题，则改用 Avalonia 点击事件并在构造函数中绑定一次：

```csharp
btnFailoverModeFailover.Click += (_, _) => ViewModel?.ChangeFailoverModeCmd.Execute(EFailoverMode.Failover).Subscribe();
btnFailoverModeOff.Click += (_, _) => ViewModel?.ChangeFailoverModeCmd.Execute(EFailoverMode.Off).Subscribe();
btnFailoverModeLeastDelay.Click += (_, _) => ViewModel?.ChangeFailoverModeCmd.Execute(EFailoverMode.LeastDelay).Subscribe();
```

优先使用 `Command`，避免重复订阅。

- [ ] **步骤 6：编译 Desktop**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj
```

预期：编译通过；如果 `Classes.active` 绑定语法不被当前 Avalonia 版本接受，改为在 code-behind 监听 `WhenAnyValue` 并手动维护 `Classes`。

- [ ] **步骤 7：提交**

运行：

```powershell
git add v2rayN/v2rayN.Desktop/Views/MsgView.axaml v2rayN/v2rayN.Desktop/Views/MsgView.axaml.cs v2rayN/v2rayN.Desktop/Assets/GlobalStyles.axaml v2rayN/ServiceLib/Resx/ResUI.resx v2rayN/ServiceLib/Resx/ResUI.zh-Hans.resx v2rayN/ServiceLib/Resx/ResUI.Designer.cs
git commit -m "feat: 新增故障组三段模式按钮"
```

## 任务 7：补齐兼容引用和全量测试

**文件：**

- 修改：所有仍引用 `_config.FailoverEnabled` 决定运行状态的文件
- 修改：`v2rayN/ServiceLib.Tests/*Failover*.cs`

- [ ] **步骤 1：搜索旧布尔运行态引用**

运行：

```powershell
rg -n "FailoverEnabled" v2rayN\ServiceLib v2rayN\ServiceLib.Tests v2rayN\v2rayN.Desktop
```

预期：允许保留的位置：

- `Config.FailoverEnabled` 字段本身。
- `ConfigHandler.NormalizeFailoverMode` 兼容桥接。
- 测试旧配置迁移的断言。

其它运行态判断应改为 `FailoverMode != EFailoverMode.Off` 或具体模式判断。

- [ ] **步骤 2：修正发现的旧语义**

如果搜索发现类似：

```csharp
if (_config.FailoverEnabled)
```

用于决定运行状态，替换为：

```csharp
if (_config.FailoverMode != EFailoverMode.Off)
```

如果用于兼容保存旧字段，则必须和 `FailoverMode` 同步，不单独作为真相来源。

- [ ] **步骤 3：运行故障相关测试**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "Failover"
```

预期：所有 Failover 相关测试通过。

- [ ] **步骤 4：运行全量 ServiceLib 测试**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj
```

预期：全部通过。

- [ ] **步骤 5：编译 Desktop**

运行：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj
```

预期：编译通过。

- [ ] **步骤 6：提交兼容修正**

如果步骤 2 有改动，运行：

```powershell
git add v2rayN/ServiceLib v2rayN/ServiceLib.Tests v2rayN/v2rayN.Desktop
git commit -m "fix: 统一故障转移三态运行语义"
```

如果没有改动，记录“无需提交”。

## 任务 8：更新本地记录、生成补丁并做手工验收

**文件：**

- 修改：`_local_changes/009-failover-least-delay-mode/README.md`
- 修改：`_local_changes/009-failover-least-delay-mode/files.md`
- 修改：`_local_changes/009-failover-least-delay-mode/reapply.md`
- 修改：`_local_changes/009-failover-least-delay-mode/tests.md`
- 修改：`_local_changes/009-failover-least-delay-mode/patch.diff`
- 修改：`_local_changes/README.md`

- [ ] **步骤 1：更新状态**

把 `_local_changes/009-failover-least-delay-mode/README.md` 中状态从 `实施中` 改为 `已验证自动化` 或 `已验证自动化与手工`，取决于手工 UI 是否完成。

- [ ] **步骤 2：补齐 `files.md`**

确保每个实际修改文件都有说明，例如：

```markdown
- `v2rayN/ServiceLib/Enums/EFailoverMode.cs`：新增故障转移三态模式。
- `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`：新增延迟转移运行态排序，核心虚拟组按运行态顺序生成。
- `v2rayN/ServiceLib/Manager/FailoverHealthStateMachine.cs`：支持强制探测，切入延迟转移时可探测冷却中的已启用节点。
- `v2rayN/ServiceLib/Services/FailoverHealthService.cs`：切入延迟转移可强制探测；后台探测后仅在运行态第一节点变化时触发核心重载。
- `v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`：活动故障组延迟模式隐藏 `P*`，并按运行态顺序显示。
```

- [ ] **步骤 3：补齐 `tests.md`**

记录实际执行命令和结果：

```markdown
## 自动化验证

- `dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "Failover"`：通过。
- `dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj`：通过。
- `dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj`：通过。

## 手工验证

- 主界面三段按钮显示 `故障转移 | 关闭 | 延迟转移`。
- `关闭` 选中为蓝色。
- `故障转移` 和 `延迟转移` 选中为绿色。
- 延迟转移开启后，活动故障组探测完成时最低延迟节点显示在第一位。
- 延迟转移开启后，当前活动故障组所有已启用节点都会进行一次强制探测。
- 后台探测导致运行态第一节点变化时核心会重载；运行态第一节点不变时不会重复重载。
- 切回故障转移或关闭后，活动故障组恢复原队列顺序。

## 代理安全影响评估

本改动只影响活动故障组虚拟 `PolicyGroup` 的子节点运行态顺序，不修改 Tun、系统代理、路由、DNS、TLS、订阅下载、证书或核心更新策略。健康探测继续使用 `suppressFailover: true`。后台探测只在运行态第一节点变化时触发核心重载。
```

- [ ] **步骤 4：生成补丁**

运行：

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes/009-failover-least-delay-mode/patch.diff
```

如果功能已分成多个提交，也可以改用：

```powershell
git show --format=fuller --stat --patch <first-commit>^..<last-commit> -- . ":(exclude)_local_changes" > _local_changes/009-failover-least-delay-mode/patch.diff
```

预期：`patch.diff` 只包含本功能相关源码、资源和测试改动，不包含 `_local_changes/` 自身。

- [ ] **步骤 5：更新总览状态**

把 `_local_changes/README.md` 中 009 行状态更新为实际状态。

- [ ] **步骤 6：提交本地记录**

运行：

```powershell
git add _local_changes/009-failover-least-delay-mode _local_changes/README.md
git commit -m "docs: 更新故障分组延迟转移记录"
```

## 任务 9：最终验证与偏差检查

**文件：**

- 只读检查：`docs/superpowers/specs/2026-05-12-failover-least-delay-mode-design.md`
- 只读检查：`docs/superpowers/plans/2026-05-11-failover-groups.md`
- 只读检查：`docs/superpowers/plans/2026-05-12-failover-health-batch-probe.md`
- 只读检查：`_local_changes/005-failover-groups/README.md`
- 只读检查：`_local_changes/006-failover-health-implementation/README.md`
- 只读检查：`_local_changes/007-failover-health-batch-probe/README.md`
- 只读检查：`_local_changes/009-failover-least-delay-mode/README.md`

- [ ] **步骤 1：确认没有语义漂移**

逐项确认：

- 005 的故障转移仍按队列优先级生成 fallback。
- 006 的健康状态仍只表达探测状态，不声称核心内部真实连接状态。
- 007 的批量探测仍使用 `suppressFailover: true`。
- 009 的延迟转移只改变运行态顺序，不改 `Sort`。
- 009 的延迟转移最低延迟候选只包含已启用队列节点，不包含未启用候选节点。
- 切入 `LeastDelay` 时会强制探测所有已启用队列节点；后台探测后只有运行态第一节点变化才触发核心重载。
- `Failover` 与 `LeastDelay` 都仍使用虚拟 fallback `PolicyGroup`，没有改成核心内置 `LeastPing`。
- `Off` 不启动健康探测，不生成虚拟组。

- [ ] **步骤 2：搜索危险语义**

运行：

```powershell
rg -n "LeastPing|FailoverEnabled|P\\*|LastDelay|suppressFailover|Sort|ReloadRequested|CheckActiveGroupOnceAsync|force" v2rayN\ServiceLib v2rayN\ServiceLib.Tests v2rayN\v2rayN.Desktop _local_changes\009-failover-least-delay-mode
```

预期：

- 不应出现把 `LeastDelay` 实现为 `EMultipleLoad.LeastPing` 的代码。
- 不应出现延迟转移写回 `FailoverGroupItem.Sort` 的代码。
- 不应删除或绕过 `suppressFailover: true`。
- 不应出现后台探测无条件发布 `ReloadRequested` 的代码；必须以运行态第一节点变化为条件。

- [ ] **步骤 3：最终工作区检查**

运行：

```powershell
git status --short
```

预期：只剩用户明确允许保留的未提交文件；如果本功能已经全部提交，应为空。

## 执行顺序建议

1. 任务 0 先做，保证 `_local_changes` 记录从第一步开始存在。
2. 任务 1 到任务 3 先完成服务层状态和排序，避免 UI 先行后语义不稳。
3. 任务 4 完成列表显示语义。
4. 任务 5 和任务 6 完成主界面三段按钮。
5. 任务 7 到任务 9 做全量验证和记录闭环。

每个任务完成后都提交一次，方便未来迁移时按功能边界复用。
