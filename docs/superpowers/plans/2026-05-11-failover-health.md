# 故障队列健康状态实施计划

> **给代理执行者：** 必须按任务顺序执行。推荐使用 `superpowers:subagent-driven-development`，也可以使用 `superpowers:executing-plans`。每个步骤使用 checkbox (`- [ ]`) 跟踪。

**目标：** 为故障队列补充外层健康检测、状态机和 UI 标签，让用户能看到 `正常`、`故障`、`探测中`、`未知` 与 `P*` 优先级。

**架构：** 新增纯状态机负责状态转移，新增探测抽象负责通过临时核心检测单个节点，新增后台服务只在故障转移开启时周期扫描活动故障队列。第二阶段只更新状态和 UI，不动态重排 fallback 子节点，不强制核心回切。

**技术栈：** .NET、xUnit、Avalonia、ReactiveUI、SQLite-net、v2rayN ServiceLib、Xray/sing-box 临时测速核心。

---

## 执行前门禁

本计划必须先处理以下门禁，再进入源码实现：

- 健康探测必须验证“单节点真实链路”，不得被 `Config.FailoverEnabled` 的虚拟 fallback 入口劫持。实现时必须给测速配置构建链路增加禁用故障转移包装的参数，并在 `CoreFailoverHealthProbe` 中启用该参数。
- 健康探测取消不等同于节点故障。关闭故障转移、退出应用或取消任务时，不得把节点写成 `Failed`，也不得留下永久 `Probing` 状态。
- UI 标签样式不要依赖 Avalonia `Classes` 字符串动态绑定。默认使用多个静态 `Label` 加布尔可见性，或使用已验证可工作的 converter。
- 实现源码前先创建 `_local_changes/006-failover-health-implementation/`，之后同步维护说明、验证记录和补丁。

## 文件结构

- 新增 `v2rayN/ServiceLib/Models/FailoverHealthStatus.cs`：集中定义 `Unknown`、`Normal`、`Failed`、`Probing` 字符串常量。
- 修改 `v2rayN/ServiceLib/Models/FailoverGroupItem.cs`：补充失败次数、冷却截止、成功时间、探测时间和最近延迟。
- 新增 `v2rayN/ServiceLib/Models/FailoverHealthProbeResult.cs`：表示单次探测结果。
- 新增 `v2rayN/ServiceLib/Manager/FailoverHealthStateMachine.cs`：纯状态机，不访问数据库，不启动核心。
- 修改 `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`：提供健康状态读写、到期队列读取和状态更新方法。
- 新增 `v2rayN/ServiceLib/Services/IFailoverHealthProbe.cs`：探测接口，便于测试。
- 新增 `v2rayN/ServiceLib/Services/CoreFailoverHealthProbe.cs`：通过临时核心和 `SpeedPingTestUrl` 探测真实节点。
- 新增 `v2rayN/ServiceLib/Services/FailoverHealthService.cs`：后台扫描服务，响应故障转移开启/关闭。
- 修改 `v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`：为测速构建提供禁用故障转移包装的参数。
- 修改 `v2rayN/ServiceLib/Manager/CoreManager.cs`：为单节点测速入口透传禁用故障转移包装的参数。
- 修改 `v2rayN/ServiceLib/Events/AppEvents.cs`：新增健康状态刷新事件。
- 修改 `v2rayN/ServiceLib/Models/ProfileItemModel.cs`：新增健康标签展示字段。
- 修改 `v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`：把 `FailoverGroupItem.LastStatus` 映射到 UI 模型。
- 修改 `v2rayN/ServiceLib/ViewModels/MsgViewModel.cs`：开启/关闭故障转移时启动/停止健康服务。
- 修改 `v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`：在别名前增加健康状态标签。
- 新增或修改 `v2rayN/ServiceLib.Tests/*`：覆盖状态机、队列读取、UI 模型映射。
- 新增 `_local_changes/006-failover-health-implementation/`：实现阶段本地记录。纯设计与计划文档保留在 `docs/superpowers/`，实现记录使用连续编号的 `006`。

## 任务 0：创建实现阶段本地记录

**文件：**

- 新增：`_local_changes/006-failover-health-implementation/README.md`
- 新增：`_local_changes/006-failover-health-implementation/files.md`
- 新增：`_local_changes/006-failover-health-implementation/reapply.md`
- 新增：`_local_changes/006-failover-health-implementation/tests.md`
- 新增：`_local_changes/006-failover-health-implementation/patch.diff`

- [ ] **步骤 1：创建本地记录目录**

```powershell
New-Item -ItemType Directory -Force -Path "_local_changes\006-failover-health-implementation"
```

- [ ] **步骤 2：放入初始说明文件**

先创建五个文件，后续每完成一组源码改动就同步补充内容。

`README.md` 初始内容：

```markdown
# 故障队列健康状态实现

## 改动目的

实现第二阶段故障队列健康状态、主动检测和恢复可见性。

## 代理安全影响评估

健康探测必须通过目标节点对应的临时核心链路发出，并且单节点探测必须禁用故障转移包装，避免被活动 fallback 组掩盖真实节点故障。探测失败不得导致主代理入口静默直连。
```

`files.md` 初始内容：

```markdown
# 文件清单

实现过程中按实际新增和修改文件补充。
```

`reapply.md` 初始内容：

```markdown
# 重新应用说明

未来迁移新版源码时，先应用本目录 `patch.diff`；如补丁冲突，按 `files.md` 中的文件清单逐项迁移。
```

`tests.md` 初始内容：

```markdown
# 验证记录

实现过程中按实际执行命令和手工验证结果补充。

## 代理安全影响评估

待实现完成后记录单节点探测、开关关闭和取消探测的验证结果。
```

`patch.diff` 初始内容：

```text
实现完成后生成。
```

## 任务 1：状态常量和数据字段

**文件：**

- 新增：`v2rayN/ServiceLib/Models/FailoverHealthStatus.cs`
- 修改：`v2rayN/ServiceLib/Models/FailoverGroupItem.cs`
- 新增：`v2rayN/ServiceLib.Tests/FailoverHealthStateMachineTests.cs`

- [ ] **步骤 1：写失败测试**

在 `v2rayN/ServiceLib.Tests/FailoverHealthStateMachineTests.cs` 新增：

```csharp
using ServiceLib.Manager;
using ServiceLib.Models;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverHealthStateMachineTests
{
    [Fact]
    public void ApplyProbeSuccess_ResetsFailureState()
    {
        var now = new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var item = new FailoverGroupItem
        {
            LastStatus = FailoverHealthStatus.Failed,
            FailureCount = 2,
            CooldownUntilTime = now + 60_000,
            LastFailureReason = "timeout",
        };

        FailoverHealthStateMachine.ApplyProbeResult(item, FailoverHealthProbeResult.Success(123), now);

        Assert.Equal(FailoverHealthStatus.Normal, item.LastStatus);
        Assert.Equal(0, item.FailureCount);
        Assert.Null(item.CooldownUntilTime);
        Assert.Null(item.LastFailureReason);
        Assert.Equal(now, item.LastSuccessTime);
        Assert.Equal(123, item.LastDelay);
    }
}
```

- [ ] **步骤 2：运行测试，确认失败**

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverHealthStateMachineTests
```

预期：失败，原因是 `FailoverHealthStatus`、`FailoverHealthProbeResult`、`FailoverHealthStateMachine` 尚不存在。

- [ ] **步骤 3：新增状态常量和数据字段**

新增 `v2rayN/ServiceLib/Models/FailoverHealthStatus.cs`：

```csharp
namespace ServiceLib.Models;

public static class FailoverHealthStatus
{
    public const string Unknown = "Unknown";
    public const string Normal = "Normal";
    public const string Failed = "Failed";
    public const string Probing = "Probing";
}
```

修改 `v2rayN/ServiceLib/Models/FailoverGroupItem.cs`，在现有字段后补充：

```csharp
public int FailureCount { get; set; }

public long? CooldownUntilTime { get; set; }

public long? LastSuccessTime { get; set; }

public long? LastProbeTime { get; set; }

public int LastDelay { get; set; }
```

- [ ] **步骤 4：新增探测结果模型和最小状态机**

新增 `v2rayN/ServiceLib/Models/FailoverHealthProbeResult.cs`。`Cancelled` 用于表达服务停止或应用退出，不参与失败计数：

```csharp
namespace ServiceLib.Models;

public record FailoverHealthProbeResult(bool Success, int Delay, string? FailureReason, bool Cancelled = false)
{
    public static FailoverHealthProbeResult Success(int delay)
        => new(true, delay, null, false);

    public static FailoverHealthProbeResult Failure(string reason)
        => new(false, 0, reason, false);

    public static FailoverHealthProbeResult Cancel()
        => new(false, 0, null, true);
}
```

新增 `v2rayN/ServiceLib/Manager/FailoverHealthStateMachine.cs`：

```csharp
namespace ServiceLib.Manager;

public static class FailoverHealthStateMachine
{
    public static void ApplyProbeResult(FailoverGroupItem item, FailoverHealthProbeResult result, long now)
    {
        if (result.Cancelled)
        {
            return;
        }

        item.LastProbeTime = now;
        if (result.Success)
        {
            item.LastStatus = FailoverHealthStatus.Normal;
            item.FailureCount = 0;
            item.CooldownUntilTime = null;
            item.LastFailureReason = null;
            item.LastSuccessTime = now;
            item.LastDelay = result.Delay;
            return;
        }

        item.FailureCount++;
        item.LastFailureTime = now;
        item.LastFailureReason = result.FailureReason;
        if (item.FailureCount >= 2)
        {
            item.LastStatus = FailoverHealthStatus.Failed;
            item.CooldownUntilTime = now + GetCooldownMilliseconds(item.FailureCount);
        }
    }

    public static long GetCooldownMilliseconds(int failureCount)
    {
        if (failureCount <= 2)
        {
            return 60_000;
        }
        if (failureCount == 3)
        {
            return 120_000;
        }
        return 300_000;
    }
}
```

- [ ] **步骤 5：运行测试，确认通过**

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverHealthStateMachineTests
```

预期：通过。

## 任务 2：补齐失败、冷却和半开探测状态机

**文件：**

- 修改：`v2rayN/ServiceLib/Manager/FailoverHealthStateMachine.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverHealthStateMachineTests.cs`

- [ ] **步骤 1：写失败测试**

追加测试：

```csharp
[Fact]
public void ApplyProbeFailure_SecondFailureEntersFailedWithCooldown()
{
    var now = new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    var item = new FailoverGroupItem
    {
        LastStatus = FailoverHealthStatus.Normal,
        FailureCount = 1,
    };

    FailoverHealthStateMachine.ApplyProbeResult(item, FailoverHealthProbeResult.Failure("timeout"), now);

    Assert.Equal(FailoverHealthStatus.Failed, item.LastStatus);
    Assert.Equal(2, item.FailureCount);
    Assert.Equal(now + 60_000, item.CooldownUntilTime);
    Assert.Equal(now, item.LastFailureTime);
    Assert.Equal("timeout", item.LastFailureReason);
}

[Theory]
[InlineData(2, 60_000)]
[InlineData(3, 120_000)]
[InlineData(4, 300_000)]
[InlineData(8, 300_000)]
public void GetCooldownMilliseconds_UsesBoundedBackoff(int failureCount, long expected)
{
    Assert.Equal(expected, FailoverHealthStateMachine.GetCooldownMilliseconds(failureCount));
}

[Fact]
public void MarkProbeStarting_FailedAfterCooldownBecomesProbing()
{
    var now = new DateTimeOffset(2026, 5, 11, 12, 2, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    var item = new FailoverGroupItem
    {
        LastStatus = FailoverHealthStatus.Failed,
        FailureCount = 2,
        CooldownUntilTime = now - 1,
    };

    var canProbe = FailoverHealthStateMachine.TryMarkProbeStarting(item, now);

    Assert.True(canProbe);
    Assert.Equal(FailoverHealthStatus.Probing, item.LastStatus);
    Assert.Equal(now, item.LastProbeTime);
}

[Fact]
public void MarkProbeStarting_FailedBeforeCooldownIsSkipped()
{
    var now = new DateTimeOffset(2026, 5, 11, 12, 2, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    var item = new FailoverGroupItem
    {
        LastStatus = FailoverHealthStatus.Failed,
        FailureCount = 2,
        CooldownUntilTime = now + 1,
    };

    var canProbe = FailoverHealthStateMachine.TryMarkProbeStarting(item, now);

    Assert.False(canProbe);
    Assert.Equal(FailoverHealthStatus.Failed, item.LastStatus);
}
```

- [ ] **步骤 2：运行测试，确认失败**

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverHealthStateMachineTests
```

预期：失败，原因是 `TryMarkProbeStarting` 尚不存在，失败转移逻辑还不完整。

- [ ] **步骤 3：补状态机方法**

在 `FailoverHealthStateMachine` 中增加：

```csharp
public static bool TryMarkProbeStarting(FailoverGroupItem item, long now)
{
    if (item.LastStatus == FailoverHealthStatus.Failed
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

确认 `ApplyProbeResult` 的失败分支在 `FailureCount >= 2` 时写入 `Failed` 和冷却截止时间。

- [ ] **步骤 4：运行测试，确认通过**

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverHealthStateMachineTests
```

预期：通过。

## 任务 3：故障队列健康读写方法

**文件：**

- 修改：`v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`

- [ ] **步骤 1：写失败测试**

在 `FailoverGroupManagerTests` 中追加：

```csharp
[Fact]
public async Task GetDueHealthCheckEntries_SkipsMissingProfilesAndCooldownItems()
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

        Assert.Single(entries);
        Assert.Equal(due, entries[0].Profile.IndexId);
    }
    finally
    {
        await Cleanup(groupId, due, cooling, missing);
    }
}
```

- [ ] **步骤 2：运行测试，确认失败**

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter GetDueHealthCheckEntries
```

预期：失败，原因是 `GetDueHealthCheckEntries` 尚不存在。

- [ ] **步骤 3：实现队列读取方法**

在 `FailoverGroupManager` 增加：

```csharp
public static async Task<List<FailoverQueueEntry>> GetDueHealthCheckEntries(string groupId, long now)
{
    var entries = await GetQueueEntries(groupId, true);
    return entries
        .Where(entry => entry.Item.LastStatus != FailoverHealthStatus.Failed
            || entry.Item.CooldownUntilTime is null
            || entry.Item.CooldownUntilTime <= now)
        .ToList();
}
```

如果 `LastStatus` 为空，读取后按 `Unknown` 处理；可在 `GetQueueEntries` 构造结果前补：

```csharp
foreach (var item in items)
{
    if (item.LastStatus.IsNullOrEmpty())
    {
        item.LastStatus = FailoverHealthStatus.Unknown;
    }
}
```

- [ ] **步骤 4：运行测试，确认通过**

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter GetDueHealthCheckEntries
```

预期：通过。

## 任务 4：节点探测抽象和真实探测实现

**文件：**

- 新增：`v2rayN/ServiceLib/Services/IFailoverHealthProbe.cs`
- 新增：`v2rayN/ServiceLib/Services/CoreFailoverHealthProbe.cs`
- 修改：`v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`
- 修改：`v2rayN/ServiceLib/Manager/CoreManager.cs`

- [ ] **步骤 1：新增探测接口**

```csharp
namespace ServiceLib.Services;

public interface IFailoverHealthProbe
{
    Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken);
}
```

- [ ] **步骤 2：写单节点探测不被故障转移包装的失败测试**

在 `v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs` 增加命名空间引用并追加测试。该测试用于防止健康探测目标被活动 fallback 组掩盖：

```csharp
using ServiceLib.Handler.Builder;
```

```csharp
[Fact]
public async Task CoreConfigContextBuilder_BuildSuppressFailoverKeepsRequestedNode()
{
    PrepareTables();
    var suffix = Utils.GetGuid(false);
    var groupId = $"failover-{suffix}";
    var p1 = $"p1-{suffix}";
    var p2 = $"p2-{suffix}";
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

        var config = new Config
        {
            FailoverEnabled = true,
            ActiveFailoverGroupId = groupId,
        };

        var result = await CoreConfigContextBuilder.Build(config, source, suppressFailover: true);

        Assert.True(result.Success);
        Assert.Equal(source.IndexId, result.Context.Node.IndexId);
        Assert.DoesNotContain(FailoverGroupManager.VirtualPolicyGroupPrefix, result.Context.Node.IndexId);
    }
    finally
    {
        await Cleanup(groupId, p1, p2, source.IndexId);
    }
}
```

- [ ] **步骤 3：运行测试，确认失败**

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter CoreConfigContextBuilder_BuildSuppressFailoverKeepsRequestedNode
```

预期：失败，原因是 `CoreConfigContextBuilder.Build` 还没有 `suppressFailover` 参数。

- [ ] **步骤 4：给核心上下文构建增加禁用故障转移包装参数**

修改 `CoreConfigContextBuilder.Build` 签名，并只在未禁用时接入虚拟 fallback 组：

```csharp
public static async Task<CoreConfigContextBuilderResult> Build(
    Config config,
    ProfileItem node,
    bool suppressFailover = false)
{
    var runCoreType = AppManager.Instance.GetCoreType(node, node.ConfigType);
    var coreType = runCoreType == ECoreType.sing_box ? ECoreType.sing_box : ECoreType.Xray;
    var context = new CoreConfigContext()
    {
        Node = node,
        RunCoreType = runCoreType,
        AllProxiesMap = [],
        AppConfig = config,
        FullConfigTemplate = await AppManager.Instance.GetFullConfigTemplateItem(coreType),
        IsTunEnabled = config.TunModeItem.EnableTun,
        SimpleDnsItem = config.SimpleDNSItem,
        ProtectDomainList = [],
        TunProtectSocksPort = 0,
        ProxyRelaySocksPort = 0,
        RawDnsItem = await AppManager.Instance.GetDNSItem(coreType),
        RoutingItem = await ConfigHandler.GetDefaultRouting(config),
    };
    var validatorResult = NodeValidatorResult.Empty();

    if (config.FailoverEnabled && !suppressFailover)
    {
        var failoverResult = await FailoverGroupManager.TryBuildVirtualPolicyGroup(config, node);
        if (!failoverResult.Success)
        {
            return new CoreConfigContextBuilderResult(context, failoverResult.ValidatorResult);
        }
        if (failoverResult.Node != null)
        {
            node = failoverResult.Node;
            context = context with { Node = node };
        }
    }

    var (actNode, nodeValidatorResult) = await ResolveNodeAsync(context, node);
    // 保留该方法后续现有逻辑不变。
}
```

在同文件中修改 `BuildPreSocksIfNeeded` 内部调用，明确保持默认行为：

```csharp
var preSocksResult = await Build(nodeContext.AppConfig, preSocksItem);
```

不要给主运行路径传 `suppressFailover: true`，该参数只供健康探测这种“单节点验证”使用。

- [ ] **步骤 5：给单节点测速入口透传禁用故障转移包装参数**

修改 `CoreManager.LoadCoreConfigSpeedtest(ServerTestItem testItem)` 签名和构建调用：

```csharp
public async Task<ProcessService?> LoadCoreConfigSpeedtest(
    ServerTestItem testItem,
    bool suppressFailover = false)
{
    var node = await AppManager.Instance.GetProfileItem(testItem.IndexId);
    if (node is null)
    {
        return null;
    }

    var fileName = string.Format(Global.CoreSpeedtestConfigFileName, Utils.GetGuid(false));
    var configPath = Utils.GetBinConfigPath(fileName);
    var (context, _) = await CoreConfigContextBuilder.Build(_config, node, suppressFailover);
    var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(_config, context, testItem, configPath);
    if (result.Success != true)
    {
        return null;
    }

    var coreType = context.RunCoreType;
    var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
    return await RunProcess(coreInfo, fileName, true, false);
}
```

保留 `SpeedtestService` 原调用不变，让用户手动测速继续走现有行为；只有健康探测显式传 `suppressFailover: true`。

- [ ] **步骤 6：新增真实探测实现**

```csharp
namespace ServiceLib.Services;

public class CoreFailoverHealthProbe(Config config) : IFailoverHealthProbe
{
    public async Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken)
    {
        ProcessService? processService = null;
        try
        {
            var testItem = new ServerTestItem
            {
                IndexId = profile.IndexId,
                Address = profile.Address,
                Port = profile.Port,
                ConfigType = profile.ConfigType,
                Profile = profile,
                CoreType = AppManager.Instance.GetCoreType(profile, profile.ConfigType),
            };

            processService = await CoreManager.Instance.LoadCoreConfigSpeedtest(testItem, suppressFailover: true);
            if (processService is null)
            {
                return FailoverHealthProbeResult.Failure("core-start-failed");
            }

            await Task.Delay(1000, cancellationToken);
            var webProxy = new WebProxy($"socks5://{Global.Loopback}:{testItem.Port}");
            var delay = await ConnectionHandler.GetRealPingTime(config.SpeedTestItem.SpeedPingTestUrl, webProxy, 10);
            return delay > 0
                ? FailoverHealthProbeResult.Success(delay)
                : FailoverHealthProbeResult.Failure("request-failed");
        }
        catch (OperationCanceledException)
        {
            return FailoverHealthProbeResult.Cancel();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(nameof(CoreFailoverHealthProbe), ex);
            return FailoverHealthProbeResult.Failure("probe-failed");
        }
        finally
        {
            if (processService != null)
            {
                await processService.StopAsync();
            }
        }
    }
}
```

- [ ] **步骤 7：运行测试和编译**

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter CoreConfigContextBuilder_BuildSuppressFailoverKeepsRequestedNode
dotnet build v2rayN\ServiceLib\ServiceLib.csproj --no-restore
```

预期：测试通过，编译通过。若缺少命名空间，按项目现有 global using 或显式 using 补齐。

## 任务 5：后台健康服务

**文件：**

- 新增：`v2rayN/ServiceLib/Services/FailoverHealthService.cs`
- 修改：`v2rayN/ServiceLib/Events/AppEvents.cs`
- 新增：`v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`

- [ ] **步骤 1：写服务单轮测试**

新增 `v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`：

```csharp
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverHealthServiceTests
{
    [Fact]
    public async Task CheckOnceAsync_UpdatesQueueItemStatus()
    {
        SQLiteHelper.Instance.CreateTable<SubItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<FailoverGroupItem>();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config { FailoverEnabled = true, ActiveFailoverGroupId = groupId };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(new ProfileItem
            {
                IndexId = profileId,
                Remarks = profileId,
                ConfigType = EConfigType.SOCKS,
                CoreType = ECoreType.Xray,
                Address = "198.51.100.30",
                Port = 443,
            });
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
            await SQLiteHelper.Instance.ExecuteAsync($"delete from FailoverGroupItem where GroupId = '{groupId}'");
            await SQLiteHelper.Instance.ExecuteAsync($"delete from SubItem where Id = '{groupId}'");
            await SQLiteHelper.Instance.ExecuteAsync($"delete from ProfileItem where IndexId = '{profileId}'");
        }
    }

    [Fact]
    public async Task CheckOnceAsync_CancelledProbeDoesNotMarkFailed()
    {
        SQLiteHelper.Instance.CreateTable<SubItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<FailoverGroupItem>();
        var suffix = Utils.GetGuid(false);
        var groupId = $"failover-{suffix}";
        var profileId = $"profile-{suffix}";
        var config = new Config { FailoverEnabled = true, ActiveFailoverGroupId = groupId };

        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
            await SQLiteHelper.Instance.ReplaceAsync(new ProfileItem
            {
                IndexId = profileId,
                Remarks = profileId,
                ConfigType = EConfigType.SOCKS,
                CoreType = ECoreType.Xray,
                Address = "198.51.100.30",
                Port = 443,
            });
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
            await SQLiteHelper.Instance.ExecuteAsync($"delete from FailoverGroupItem where GroupId = '{groupId}'");
            await SQLiteHelper.Instance.ExecuteAsync($"delete from SubItem where Id = '{groupId}'");
            await SQLiteHelper.Instance.ExecuteAsync($"delete from ProfileItem where IndexId = '{profileId}'");
        }
    }

    private sealed class FakeProbe(FailoverHealthProbeResult result) : IFailoverHealthProbe
    {
        public Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken)
            => Task.FromResult(result);
    }
}
```

- [ ] **步骤 2：运行测试，确认失败**

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverHealthServiceTests
```

预期：失败，原因是 `FailoverHealthService` 尚不存在。

- [ ] **步骤 3：新增事件**

在 `AppEvents.cs` 增加：

```csharp
public static readonly EventChannel<Unit> FailoverHealthChangedRequested = new();
```

- [ ] **步骤 4：实现后台服务**

新增 `v2rayN/ServiceLib/Services/FailoverHealthService.cs`：

```csharp
namespace ServiceLib.Services;

public class FailoverHealthService
{
    private readonly Config _config;
    private readonly IFailoverHealthProbe _probe;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public static FailoverHealthService Instance { get; } =
        new(AppManager.Instance.Config, new CoreFailoverHealthProbe(AppManager.Instance.Config));

    public FailoverHealthService(Config config, IFailoverHealthProbe probe)
    {
        _config = config;
        _probe = probe;
    }

    public void Start()
    {
        if (_loopTask?.IsCompleted == false)
        {
            return;
        }
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    public async Task CheckOnceAsync()
    {
        if (!_config.FailoverEnabled || _config.ActiveFailoverGroupId.IsNullOrEmpty())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = await FailoverGroupManager.GetDueHealthCheckEntries(_config.ActiveFailoverGroupId, now);
        foreach (var entry in entries)
        {
            var previousStatus = entry.Item.LastStatus;
            if (!FailoverHealthStateMachine.TryMarkProbeStarting(entry.Item, now))
            {
                continue;
            }
            await SQLiteHelper.Instance.UpdateAsync(entry.Item);
            AppEvents.FailoverHealthChangedRequested.Publish();

            var result = await _probe.ProbeAsync(entry.Profile, _cts?.Token ?? CancellationToken.None);
            if (result.Cancelled)
            {
                entry.Item.LastStatus = previousStatus;
                await SQLiteHelper.Instance.UpdateAsync(entry.Item);
                AppEvents.FailoverHealthChangedRequested.Publish();
                continue;
            }

            FailoverHealthStateMachine.ApplyProbeResult(entry.Item, result, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await SQLiteHelper.Instance.UpdateAsync(entry.Item);
            AppEvents.FailoverHealthChangedRequested.Publish();
        }
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await CheckOnceAsync();
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
}
```

- [ ] **步骤 5：运行测试，确认通过**

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverHealthServiceTests
```

预期：通过。

## 任务 6：故障转移开关接入健康服务

**文件：**

- 修改：`v2rayN/ServiceLib/ViewModels/MsgViewModel.cs`
- 修改：`v2rayN/v2rayN.Desktop/Views/MainWindow.axaml.cs`

- [ ] **步骤 1：开启故障转移时启动服务**

在 `MsgViewModel.ToggleFailover` 成功开启分支中，发布重载前后增加：

```csharp
FailoverHealthService.Instance.Start();
```

在 `ToggleFailover(false)` 分支和 `DisableFailover()` 中都增加停止逻辑，避免用户关闭开关后后台探测继续启动临时核心：

```csharp
FailoverHealthService.Instance.Stop();
```

- [ ] **步骤 2：应用退出时停止服务**

在 Desktop 主窗口已有 `AppEvents.AppExitRequested` 订阅附近增加：

```csharp
AppEvents.AppExitRequested
    .AsObservable()
    .Subscribe(_ => FailoverHealthService.Instance.Stop())
    .DisposeWith(disposables);
```

如果 `MainWindow.axaml.cs` 中没有合适位置，也可以放在 `MsgViewModel` 构造函数的事件订阅区域。

- [ ] **步骤 3：运行 Desktop 编译**

```powershell
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

预期：通过。

## 任务 7：UI 模型和标签映射

**文件：**

- 修改：`v2rayN/ServiceLib/Models/ProfileItemModel.cs`
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`
- 修改：`v2rayN/ServiceLib/Resx/ResUI.resx`
- 修改：`v2rayN/ServiceLib/Resx/ResUI.zh-Hans.resx`
- 修改：`v2rayN/ServiceLib/Resx/ResUI.Designer.cs`

- [ ] **步骤 1：增加模型字段**

在 `ProfileItemModel` 增加：

```csharp
public string FailoverHealthStatus { get; set; }
public string FailoverHealthLabel { get; set; }
public bool ShowFailoverHealthLabel { get; set; }
public bool IsFailoverHealthNormal { get; set; }
public bool IsFailoverHealthFailed { get; set; }
public bool IsFailoverHealthProbing { get; set; }
public bool IsFailoverHealthUnknown { get; set; }
```

- [ ] **步骤 2：增加资源文案**

在 `ResUI.zh-Hans.resx` 增加：

```xml
<data name="TbFailoverHealthNormal" xml:space="preserve">
  <value>正常</value>
</data>
<data name="TbFailoverHealthFailed" xml:space="preserve">
  <value>故障</value>
</data>
<data name="TbFailoverHealthProbing" xml:space="preserve">
  <value>探测中</value>
</data>
<data name="TbFailoverHealthUnknown" xml:space="preserve">
  <value>未知</value>
</data>
```

在 `ResUI.resx` 增加对应英文值：

```xml
<data name="TbFailoverHealthNormal" xml:space="preserve">
  <value>Normal</value>
</data>
<data name="TbFailoverHealthFailed" xml:space="preserve">
  <value>Failed</value>
</data>
<data name="TbFailoverHealthProbing" xml:space="preserve">
  <value>Probing</value>
</data>
<data name="TbFailoverHealthUnknown" xml:space="preserve">
  <value>Unknown</value>
</data>
```

更新 `ResUI.Designer.cs`，按项目现有资源生成方式生成或手动补同名属性。

- [ ] **步骤 3：映射队列状态**

在 `ProfilesViewModel.GetProfileItemsEx` 读取 `priorityMap` 附近，增加健康状态映射。推荐新增私有方法：

```csharp
private static string NormalizeFailoverHealthStatus(string? status)
{
    return status switch
    {
        FailoverHealthStatus.Normal => FailoverHealthStatus.Normal,
        FailoverHealthStatus.Failed => FailoverHealthStatus.Failed,
        FailoverHealthStatus.Probing => FailoverHealthStatus.Probing,
        _ => FailoverHealthStatus.Unknown,
    };
}
```

读取故障组队列条目时，建立状态字典：

```csharp
var failoverEntryMap = isFailoverGroup
    ? (await FailoverGroupManager.GetQueueEntries(subid, false))
        .ToDictionary(entry => entry.Profile.IndexId, entry => entry.Item)
    : [];
```

在 `select new ProfileItemModel` 中填充：

```csharp
FailoverHealthStatus = failoverEntryMap.TryGetValue(t.IndexId, out var healthItem)
    ? NormalizeFailoverHealthStatus(healthItem.LastStatus)
    : FailoverHealthStatus.Unknown,
ShowFailoverHealthLabel = isFailoverGroup && priorityMap.ContainsKey(t.IndexId),
```

然后用后处理设置标签文案和布尔样式字段：

```csharp
foreach (var item in lstModel)
{
    item.FailoverHealthStatus = NormalizeFailoverHealthStatus(item.FailoverHealthStatus);
    item.FailoverHealthLabel = item.FailoverHealthStatus switch
    {
        FailoverHealthStatus.Normal => ResUI.TbFailoverHealthNormal,
        FailoverHealthStatus.Failed => ResUI.TbFailoverHealthFailed,
        FailoverHealthStatus.Probing => ResUI.TbFailoverHealthProbing,
        _ => ResUI.TbFailoverHealthUnknown,
    };
    item.IsFailoverHealthNormal = item.ShowFailoverHealthLabel && item.FailoverHealthStatus == FailoverHealthStatus.Normal;
    item.IsFailoverHealthFailed = item.ShowFailoverHealthLabel && item.FailoverHealthStatus == FailoverHealthStatus.Failed;
    item.IsFailoverHealthProbing = item.ShowFailoverHealthLabel && item.FailoverHealthStatus == FailoverHealthStatus.Probing;
    item.IsFailoverHealthUnknown = item.ShowFailoverHealthLabel && item.FailoverHealthStatus == FailoverHealthStatus.Unknown;
}
```

- [ ] **步骤 4：健康状态变化时刷新列表**

在 `ProfilesViewModel` 构造函数的事件订阅区域增加：

```csharp
AppEvents.FailoverHealthChangedRequested
    .AsObservable()
    .ObserveOn(RxSchedulers.MainThreadScheduler)
    .Subscribe(async _ => await RefreshServersBiz());
```

- [ ] **步骤 5：更新 XAML 标签**

在 `ProfilesView.axaml` 别名列中，`P*` 标签前增加：

```xml
<Label
    Margin="{StaticResource MarginLr4}"
    Classes="Solid Green"
    Content="{x:Static resx:ResUI.TbFailoverHealthNormal}"
    IsVisible="{Binding IsFailoverHealthNormal}"
    Theme="{DynamicResource TagLabel}" />
<Label
    Margin="{StaticResource MarginLr4}"
    Classes="Solid Red"
    Content="{x:Static resx:ResUI.TbFailoverHealthFailed}"
    IsVisible="{Binding IsFailoverHealthFailed}"
    Theme="{DynamicResource TagLabel}" />
<Label
    Margin="{StaticResource MarginLr4}"
    Classes="Solid Green"
    Content="{x:Static resx:ResUI.TbFailoverHealthProbing}"
    IsVisible="{Binding IsFailoverHealthProbing}"
    Theme="{DynamicResource TagLabel}" />
<Label
    Margin="{StaticResource MarginLr4}"
    Classes="Solid Green"
    Content="{x:Static resx:ResUI.TbFailoverHealthUnknown}"
    IsVisible="{Binding IsFailoverHealthUnknown}"
    Theme="{DynamicResource TagLabel}" />
```

这里使用静态 `Classes` 和布尔可见性，避免动态绑定 `Classes` 在 Avalonia 中表现不确定。`探测中` 和 `未知` 暂用绿色标签以复用现有样式；如果后续确认存在可用的中性色样式，再单独调整视觉，不阻塞功能验收。

- [ ] **步骤 6：运行 Desktop 编译**

```powershell
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

预期：通过。

## 任务 8：测试补强和全量验证

**文件：**

- 修改或新增：`v2rayN/ServiceLib.Tests/*`
- 修改：`_local_changes/006-failover-health-implementation/tests.md`

- [ ] **步骤 1：运行服务层测试**

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj
```

预期：全部通过。

- [ ] **步骤 2：运行 Desktop 编译**

```powershell
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

预期：0 错误。

- [ ] **步骤 3：运行空白检查**

```powershell
git diff --check -- . ":(exclude)_local_changes"
```

预期：无空白错误。

- [ ] **步骤 4：手工验证**

场景：

- 开启故障转移后，活动故障组队列节点从 `未知` 变为 `正常` 或 `故障`。
- 让 `P1` 故障，确认 UI 显示 `故障 P1`，请求仍可落到 `P2`。
- 在 `P1` 故障且 `P2` 正常时，确认健康探测不会因为 fallback 落到 `P2` 而把 `P1` 误判为 `正常`。
- 恢复 `P1`，等待冷却和探测后，确认 UI 显示 `正常 P1`。
- 关闭故障转移后，观察不再持续启动临时核心。
- 关闭故障转移或退出应用时，确认正在探测的节点不会被写成 `故障`，也不会长期停留在 `探测中`。

## 任务 9：本地改动记录

**文件：**

- 新增：`_local_changes/006-failover-health-implementation/README.md`
- 新增：`_local_changes/006-failover-health-implementation/files.md`
- 新增：`_local_changes/006-failover-health-implementation/reapply.md`
- 新增：`_local_changes/006-failover-health-implementation/tests.md`
- 新增：`_local_changes/006-failover-health-implementation/patch.diff`
- 修改：`_local_changes/README.md`

- [ ] **步骤 1：确认本地记录目录已存在**

```powershell
Test-Path "_local_changes\006-failover-health-implementation"
```

预期：输出 `True`。该目录应已在任务 0 创建。

- [ ] **步骤 2：记录说明**

`README.md` 必须说明：

- 二阶段实现目的。
- 用户可见的健康状态标签。
- 第二阶段不动态重排 fallback 队列。
- 代理安全影响评估。

- [ ] **步骤 3：记录文件清单**

`files.md` 必须列出所有新增和修改源码、测试、资源文件。

- [ ] **步骤 4：记录验证结果**

`tests.md` 必须记录任务 8 的自动化和手工验证结果。

- [ ] **步骤 5：生成补丁**

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes/006-failover-health-implementation/patch.diff
```

- [ ] **步骤 6：更新总览**

在 `_local_changes/README.md` 追加：

```markdown
| 006 | `006-failover-health-implementation/` | 已实现 | 故障队列健康状态、主动检测和恢复可见性实现 |
```

## 执行顺序

1. 先完成任务 0，建立实现阶段本地记录目录。
2. 再完成任务 1 到任务 3，保证状态机和数据库队列读写可测。
3. 再完成任务 4 和任务 5，接入真实单节点探测和后台服务；任务 4 必须确认健康探测不会被 `FailoverEnabled` 的虚拟 fallback 入口劫持。
4. 然后完成任务 6 和任务 7，接入开关与 UI。
5. 最后完成任务 8 和任务 9，验证并记录。

## 暂不实施内容

- 不根据健康状态动态删除、重排或跳过 fallback 子节点。
- 不显示“核心当前真实工作节点”强语义标签。
- 不新增拖拽排序设置入口。
- 不改变 Tun、系统代理、路由、DNS、TLS、订阅下载或核心更新逻辑。
