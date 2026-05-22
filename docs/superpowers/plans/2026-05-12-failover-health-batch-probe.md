# 批量真实延迟健康探测实施计划

> **给代理执行者：** 必须按任务顺序执行。推荐使用 `superpowers:subagent-driven-development`，也可以使用 `superpowers:executing-plans`。每个步骤使用 checkbox (`- [ ]`) 跟踪。

**目标：** 将故障队列健康探测从逐节点启动临时核心升级为复用 v2rayN 原有真连接测试的批量临时核心模式，同时把真实延迟写回主界面延迟列。

**架构：** 保留第二阶段已有状态机、UI 标签和故障转移安全边界；新增批量健康探测路径，按核心类型批量生成测速配置，一次临时核心承载多个节点端口，并发发起真实 HTTP 请求。健康探测继续使用 `suppressFailover: true`，避免活动 fallback 组掩盖单个节点故障。

**技术栈：** .NET、xUnit、SQLite-net、v2rayN ServiceLib、Xray/sing-box 临时测速核心、`HttpClient`、`CancellationToken`。

---

## 执行前门禁

- 后台健康探测必须继续验证“真实单节点链路”，不能被 `Config.FailoverEnabled` 的虚拟 fallback 入口替换。
- 每个节点保持 `2` 次 HTTP GET 并取最小耗时，沿用 v2rayN 真连接延迟语义。
- 探测成功和失败都写回普通测速延迟结果；成功写真实毫秒值，失败写 `-1`。
- 后台健康探测不执行原手动真连接测速的失败批次重试，避免周期任务变重。
- 手动测速入口行为保持不变，不新增用户可见按钮，不改变 `SpeedtestService.RunLoop(ESpeedActionType.Realping, ...)` 的调用方式。
- 实现源码前先创建最终实现目录 `_local_changes/007-failover-health-batch-probe/`。纯计划文档保留在 `docs/superpowers/plans/`，不再单独占用 `_local_changes` 编号目录。

## 文件结构

- 新增 `v2rayN/ServiceLib/Models/FailoverHealthProbeBatchResult.cs`：把 `ProfileItem.IndexId` 与单节点 `FailoverHealthProbeResult` 绑定，便于批量结果回写。
- 修改 `v2rayN/ServiceLib/Services/IFailoverHealthProbe.cs`：增加批量探测接口，保留单节点接口兼容现有测试。
- 修改 `v2rayN/ServiceLib/Handler/ConnectionHandler.cs`：新增带 `CancellationToken` 的真实延迟测试重载，仍执行两次 GET 取最小值。
- 修改 `v2rayN/ServiceLib/Handler/CoreConfigHandler.cs`：为批量测速配置生成增加 `suppressFailover` 参数。
- 修改 `v2rayN/ServiceLib/Manager/CoreManager.cs`：为 `LoadCoreConfigSpeedtest(List<ServerTestItem>)` 增加 `suppressFailover` 参数并向下透传。
- 修改 `v2rayN/ServiceLib/Services/CoreFailoverHealthProbe.cs`：改为批量临时核心探测，按核心类型分组，一组启动一个临时核心。
- 修改 `v2rayN/ServiceLib/Services/FailoverHealthService.cs`：每轮批量标记 `Probing`、批量调用探测、批量应用结果。
- 修改 `v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`：覆盖健康服务只调用一次批量探测、多个节点状态同时更新、取消不写故障。
- 新增 `v2rayN/ServiceLib.Tests/CoreConfigSpeedtestSuppressFailoverTests.cs`：覆盖批量测速配置路径会传入 `suppressFailover: true`。
- 修改 `_local_changes/007-failover-health-batch-probe/*`：记录实现、文件、验证和补丁。
- 修改 `_local_changes/README.md`：追加本地改动索引。

## 任务 0：创建本地记录

**文件：**

- 新增：`_local_changes/007-failover-health-batch-probe/README.md`
- 新增：`_local_changes/007-failover-health-batch-probe/files.md`
- 新增：`_local_changes/007-failover-health-batch-probe/reapply.md`
- 新增：`_local_changes/007-failover-health-batch-probe/tests.md`
- 新增：`_local_changes/007-failover-health-batch-probe/patch.diff`

- [ ] **步骤 1：确认目录存在**

```powershell
New-Item -ItemType Directory -Force -Path "_local_changes\007-failover-health-batch-probe"
```

预期：命令成功，目录存在。

- [ ] **步骤 2：写入本地记录说明**

`README.md` 写入：

```markdown
# 批量真实延迟健康探测计划

## 改动目的

规划将故障队列健康探测改为复用 v2rayN 真连接测试的批量临时核心模式，减少逐节点启动临时核心造成的性能浪费，并把真实延迟同步写回主界面延迟列。

## 用户可见行为

实现后，故障转移开启时的后台健康探测仍更新 `正常`、`故障`、`探测中`、`未知` 标签；同时主界面普通延迟列会显示后台探测得到的真实连接延迟。

## 设计取舍

不直接调用完整 `SpeedtestService.RunLoop`，避免后台任务触发手动测速 UI 状态、清空延迟列或执行失败批次重试。仅复用批量临时核心和真实 HTTP 探测链路。

## 代理安全影响评估

健康探测必须通过节点对应临时核心发出，并继续传入 `suppressFailover: true`，避免 P1 故障时被 P2 fallback 兜底后误判 P1 正常。本改动不改变 Tun、系统代理、路由、DNS、TLS、订阅下载或核心更新策略。
```

`files.md` 写入本计划涉及文件清单；`reapply.md` 写入未来迁移步骤；`tests.md` 写入当前仅完成计划验证，代码验证待实现阶段执行；`patch.diff` 在任务 7 生成。

## 任务 1：扩展探测接口和结果模型

**文件：**

- 新增：`v2rayN/ServiceLib/Models/FailoverHealthProbeBatchResult.cs`
- 修改：`v2rayN/ServiceLib/Services/IFailoverHealthProbe.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`

- [ ] **步骤 1：先写失败测试**

在 `FailoverHealthServiceTests` 中新增一个 fake 批量探测器，用于证明服务层会一次传入多个节点：

```csharp
private sealed class FakeBatchProbe(IReadOnlyList<FailoverHealthProbeBatchResult> results) : IFailoverHealthProbe
{
    public int BatchCallCount { get; private set; }
    public List<string> ProfileIds { get; } = [];

    public Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken)
        => Task.FromResult(results.First(x => x.ProfileId == profile.IndexId).Result);

    public Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeBatchAsync(
        IReadOnlyList<ProfileItem> profiles,
        CancellationToken cancellationToken)
    {
        BatchCallCount++;
        ProfileIds.AddRange(profiles.Select(x => x.IndexId));
        return Task.FromResult(results);
    }
}
```

新增测试：

```csharp
[Fact]
public async Task CheckOnceAsync_UsesOneBatchProbeForMultipleDueItems()
{
    PrepareTables();
    var suffix = Utils.GetGuid(false);
    var groupId = $"failover-{suffix}";
    var p1 = $"profile-1-{suffix}";
    var p2 = $"profile-2-{suffix}";
    var config = new Config { FailoverEnabled = true, ActiveFailoverGroupId = groupId };

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
        Assert.Equal([p1, p2], probe.ProfileIds);

        var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
            .Where(item => item.GroupId == groupId)
            .OrderBy(item => item.Sort)
            .ToListAsync();
        Assert.All(stored, item => Assert.Equal(FailoverHealthStatus.Normal, item.LastStatus));
    }
    finally
    {
        await Cleanup(groupId, p1, p2);
    }
}
```

- [ ] **步骤 2：运行测试，确认失败**

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter CheckOnceAsync_UsesOneBatchProbeForMultipleDueItems
```

预期：失败，原因是 `FailoverHealthProbeBatchResult` 和 `ProbeBatchAsync` 尚不存在，或 `FailoverHealthService` 仍逐个调用 `ProbeAsync`。

- [ ] **步骤 3：新增批量结果模型**

新增 `v2rayN/ServiceLib/Models/FailoverHealthProbeBatchResult.cs`：

```csharp
namespace ServiceLib.Models;

public record FailoverHealthProbeBatchResult(string ProfileId, FailoverHealthProbeResult Result);
```

- [ ] **步骤 4：扩展接口**

修改 `v2rayN/ServiceLib/Services/IFailoverHealthProbe.cs`：

```csharp
namespace ServiceLib.Services;

public interface IFailoverHealthProbe
{
    Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken);

    Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeBatchAsync(
        IReadOnlyList<ProfileItem> profiles,
        CancellationToken cancellationToken);
}
```

- [ ] **步骤 5：补齐测试辅助方法**

在 `FailoverHealthServiceTests` 中增加：

```csharp
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
```

- [ ] **步骤 6：再次运行测试**

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter CheckOnceAsync_UsesOneBatchProbeForMultipleDueItems
```

预期：仍失败，失败点应集中在 `FailoverHealthService` 尚未调用批量接口。

## 任务 2：为真实延迟测试增加取消支持

**文件：**

- 修改：`v2rayN/ServiceLib/Handler/ConnectionHandler.cs`
- 新增：`v2rayN/ServiceLib.Tests/ConnectionHandlerTests.cs`

- [ ] **步骤 1：写失败测试**

新增测试文件 `v2rayN/ServiceLib.Tests/ConnectionHandlerTests.cs`：

```csharp
using ServiceLib.Handler;
using Xunit;

namespace ServiceLib.Tests;

public class ConnectionHandlerTests
{
    [Fact]
    public async Task GetRealPingTime_CancelledTokenReturnsNegative()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var delay = await ConnectionHandler.GetRealPingTime("https://example.com", null, 10, cts.Token);

        Assert.Equal(-1, delay);
    }
}
```

- [ ] **步骤 2：运行测试，确认失败**

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter GetRealPingTime_CancelledTokenReturnsNegative
```

预期：失败，原因是带 `CancellationToken` 的重载不存在。

- [ ] **步骤 3：实现取消重载**

在 `ConnectionHandler` 中保留现有方法签名，并新增重载：

```csharp
public static Task<int> GetRealPingTime(string url, IWebProxy? webProxy, int downloadTimeout)
    => GetRealPingTime(url, webProxy, downloadTimeout, CancellationToken.None);

public static async Task<int> GetRealPingTime(
    string url,
    IWebProxy? webProxy,
    int downloadTimeout,
    CancellationToken cancellationToken)
{
    if (url.IsNullOrEmpty())
    {
        return -1;
    }

    try
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(downloadTimeout));
        using var client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = webProxy,
            UseProxy = webProxy != null
        });

        List<int> oneTime = [];
        for (var i = 0; i < 2; i++)
        {
            var timer = Stopwatch.StartNew();
            await client.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
            timer.Stop();
            oneTime.Add((int)timer.Elapsed.TotalMilliseconds);
            await Task.Delay(100, timeoutCts.Token).ConfigureAwait(false);
        }

        return oneTime.Where(x => x > 0).OrderBy(x => x).FirstOrDefault(-1);
    }
    catch
    {
        return -1;
    }
}
```

- [ ] **步骤 4：运行测试**

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter ConnectionHandlerTests
```

预期：通过。

## 任务 3：让批量测速配置支持 `suppressFailover`

**文件：**

- 修改：`v2rayN/ServiceLib/Handler/CoreConfigHandler.cs`
- 修改：`v2rayN/ServiceLib/Manager/CoreManager.cs`
- 新增：`v2rayN/ServiceLib.Tests/CoreConfigSpeedtestSuppressFailoverTests.cs`

- [ ] **步骤 1：写失败测试**

新增 `v2rayN/ServiceLib.Tests/CoreConfigSpeedtestSuppressFailoverTests.cs`，验证当 `Config.FailoverEnabled = true` 时，批量测速配置传入 `suppressFailover: true` 后不得把目标节点替换为虚拟 fallback 组：

```csharp
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models;
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
    var p1 = CreateProxy($"p1-{suffix}", ECoreType.Xray);
    var p2 = CreateProxy($"p2-{suffix}", ECoreType.Xray);
    var config = new Config
    {
        FailoverEnabled = true,
        ActiveFailoverGroupId = groupId,
        SpeedTestItem = new()
    };

    try
    {
        await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = groupId, Remarks = "failover", IsFailoverGroup = true });
        await SQLiteHelper.Instance.ReplaceAsync(p1);
        await SQLiteHelper.Instance.ReplaceAsync(p2);
        await FailoverGroupManager.AddToQueue(groupId, [p2.IndexId]);

        var fileName = Path.Combine(Path.GetTempPath(), $"speedtest-{suffix}.json");
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

        var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(
            config,
            fileName,
            [item],
            ECoreType.Xray,
            suppressFailover: true);

        Assert.True(result.Success);
        var json = await File.ReadAllTextAsync(fileName);
        Assert.Contains(p1.Address, json);
        Assert.DoesNotContain(FailoverGroupManager.VirtualPolicyGroupPrefix, json);
    }
    finally
    {
        await Cleanup(groupId, p1.IndexId, p2.IndexId);
    }
}

private static void PrepareTables()
{
    SQLiteHelper.Instance.CreateTable<SubItem>();
    SQLiteHelper.Instance.CreateTable<ProfileItem>();
    SQLiteHelper.Instance.CreateTable<FailoverGroupItem>();
}

private static ProfileItem CreateProxy(string indexId, ECoreType coreType)
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
```

- [ ] **步骤 2：运行测试，确认失败**

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter CoreConfigHandler_BatchSpeedtestSuppressFailoverKeepsRequestedNode
```

预期：失败，原因是 `GenerateClientSpeedtestConfig` 批量重载没有 `suppressFailover` 参数。

- [ ] **步骤 3：修改批量配置生成签名**

修改 `CoreConfigHandler`：

```csharp
public static async Task<RetResult> GenerateClientSpeedtestConfig(
    Config config,
    string fileName,
    List<ServerTestItem> selecteds,
    ECoreType coreType,
    bool suppressFailover = false)
{
    var result = new RetResult();
    var dummyNode = new ProfileItem
    {
        CoreType = coreType
    };
    var builderResult = await CoreConfigContextBuilder.Build(config, dummyNode, suppressFailover);
    var context = builderResult.Context;
    foreach (var testItem in selecteds)
    {
        var node = testItem.Profile;
        var (actNode, _) = await CoreConfigContextBuilder.ResolveNodeAsync(context, node, true);
        if (node.IndexId == actNode.IndexId)
        {
            continue;
        }
        context.ServerTestItemMap[node.IndexId] = actNode.IndexId;
    }

    if (coreType == ECoreType.sing_box)
    {
        result = new CoreConfigSingboxService(context).GenerateClientSpeedtestConfig(selecteds);
    }
    else if (coreType == ECoreType.Xray)
    {
        result = new CoreConfigV2rayService(context).GenerateClientSpeedtestConfig(selecteds);
    }
    if (result.Success != true)
    {
        return result;
    }
    await File.WriteAllTextAsync(fileName, result.Data.ToString());
    return result;
}
```

修改 `CoreManager`：

```csharp
public async Task<ProcessService?> LoadCoreConfigSpeedtest(
    List<ServerTestItem> selecteds,
    bool suppressFailover = false)
{
    var coreType = selecteds.FirstOrDefault()?.CoreType == ECoreType.sing_box ? ECoreType.sing_box : ECoreType.Xray;
    var fileName = string.Format(Global.CoreSpeedtestConfigFileName, Utils.GetGuid(false));
    var configPath = Utils.GetBinConfigPath(fileName);
    var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(
        _config,
        configPath,
        selecteds,
        coreType,
        suppressFailover);
    await UpdateFunc(false, result.Msg);
    if (result.Success != true)
    {
        return null;
    }

    await UpdateFunc(false, string.Format(ResUI.StartService, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")));
    await UpdateFunc(false, configPath);

    var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
    return await RunProcess(coreInfo, fileName, true, false);
}
```

- [ ] **步骤 4：运行相关测试**

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "CoreConfigHandler_BatchSpeedtestSuppressFailoverKeepsRequestedNode|CoreConfigContextBuilder_BuildSuppressFailoverKeepsRequestedNode"
```

预期：通过。

## 任务 4：实现批量健康探测

**文件：**

- 修改：`v2rayN/ServiceLib/Services/CoreFailoverHealthProbe.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`

- [ ] **步骤 1：实现单节点接口委托批量接口**

在 `CoreFailoverHealthProbe` 中保留 `ProbeAsync`，改为调用 `ProbeBatchAsync`：

```csharp
public async Task<FailoverHealthProbeResult> ProbeAsync(ProfileItem profile, CancellationToken cancellationToken)
{
    var results = await ProbeBatchAsync([profile], cancellationToken);
    return results.FirstOrDefault(x => x.ProfileId == profile.IndexId)?.Result
        ?? FailoverHealthProbeResult.Failure("request-failed");
}
```

- [ ] **步骤 2：实现批量探测入口**

在 `CoreFailoverHealthProbe` 中新增：

```csharp
public async Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeBatchAsync(
    IReadOnlyList<ProfileItem> profiles,
    CancellationToken cancellationToken)
{
    if (profiles.Count == 0)
    {
        return [];
    }

    var results = new ConcurrentBag<FailoverHealthProbeBatchResult>();
    var testItems = profiles
        .Where(profile => profile.IndexId.IsNotEmpty())
        .Select((profile, index) => new ServerTestItem
        {
            IndexId = profile.IndexId,
            Address = profile.Address,
            Port = profile.Port,
            ConfigType = profile.ConfigType,
            AllowTest = true,
            QueueNum = index,
            Profile = profile,
            CoreType = AppManager.Instance.GetCoreType(profile, profile.ConfigType),
        })
        .Where(item => item.CoreType is ECoreType.Xray or ECoreType.sing_box)
        .ToList();

    foreach (var group in testItems.GroupBy(item => item.CoreType))
    {
        var groupResults = await ProbeCoreGroupAsync(group.ToList(), cancellationToken);
        foreach (var result in groupResults)
        {
            results.Add(result);
        }
    }

    var known = results.Select(x => x.ProfileId).ToHashSet();
    foreach (var profile in profiles.Where(profile => !known.Contains(profile.IndexId)))
    {
        ProfileExManager.Instance.SetTestDelay(profile.IndexId, -1);
        results.Add(new(profile.IndexId, FailoverHealthProbeResult.Failure("core-unsupported")));
    }

    return results.ToList();
}
```

- [ ] **步骤 3：实现同核心类型批量探测**

在 `CoreFailoverHealthProbe` 中新增：

```csharp
private async Task<IReadOnlyList<FailoverHealthProbeBatchResult>> ProbeCoreGroupAsync(
    List<ServerTestItem> testItems,
    CancellationToken cancellationToken)
{
    ProcessService? processService = null;
    try
    {
        processService = await CoreManager.Instance.LoadCoreConfigSpeedtest(testItems, suppressFailover: true);
        cancellationToken.ThrowIfCancellationRequested();
        if (processService is null)
        {
            foreach (var item in testItems)
            {
                ProfileExManager.Instance.SetTestDelay(item.IndexId, -1);
            }
            return testItems
                .Select(item => new FailoverHealthProbeBatchResult(item.IndexId, FailoverHealthProbeResult.Failure("core-start-failed")))
                .ToList();
        }

        await Task.Delay(1000, cancellationToken);

        var tasks = testItems.Select(item => ProbeItemAsync(item, cancellationToken));
        return await Task.WhenAll(tasks);
    }
    catch (OperationCanceledException)
    {
        return testItems
            .Select(item => new FailoverHealthProbeBatchResult(item.IndexId, FailoverHealthProbeResult.Cancel()))
            .ToList();
    }
    catch (Exception ex)
    {
        Logging.SaveLog(nameof(CoreFailoverHealthProbe), ex);
        foreach (var item in testItems)
        {
            ProfileExManager.Instance.SetTestDelay(item.IndexId, -1);
        }
        return testItems
            .Select(item => new FailoverHealthProbeBatchResult(item.IndexId, FailoverHealthProbeResult.Failure("probe-failed")))
            .ToList();
    }
    finally
    {
        if (processService != null)
        {
            await processService.StopAsync();
        }
    }
}
```

- [ ] **步骤 4：实现单端口真实请求和普通延迟列写回**

在 `CoreFailoverHealthProbe` 中新增：

```csharp
private async Task<FailoverHealthProbeBatchResult> ProbeItemAsync(
    ServerTestItem item,
    CancellationToken cancellationToken)
{
    try
    {
        if (!item.AllowTest)
        {
            ProfileExManager.Instance.SetTestDelay(item.IndexId, -1);
            return new(item.IndexId, FailoverHealthProbeResult.Failure("test-skipped"));
        }

        var webProxy = new WebProxy($"socks5://{Global.Loopback}:{item.Port}");
        var delay = await ConnectionHandler.GetRealPingTime(
            _config.SpeedTestItem.SpeedPingTestUrl,
            webProxy,
            10,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        ProfileExManager.Instance.SetTestDelay(item.IndexId, delay);

        return delay > 0
            ? new(item.IndexId, FailoverHealthProbeResult.Success(delay))
            : new(item.IndexId, FailoverHealthProbeResult.Failure("request-failed"));
    }
    catch (OperationCanceledException)
    {
        return new(item.IndexId, FailoverHealthProbeResult.Cancel());
    }
    catch (Exception ex)
    {
        Logging.SaveLog(nameof(CoreFailoverHealthProbe), ex);
        ProfileExManager.Instance.SetTestDelay(item.IndexId, -1);
        return new(item.IndexId, FailoverHealthProbeResult.Failure("request-failed"));
    }
}
```

- [ ] **步骤 5：运行已有健康探测测试**

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverHealthServiceTests
```

预期：当前仍可能失败，因为服务层尚未改成调用 `ProbeBatchAsync`。

## 任务 5：健康服务改为批量调度

**文件：**

- 修改：`v2rayN/ServiceLib/Services/FailoverHealthService.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverHealthServiceTests.cs`

- [ ] **步骤 1：批量标记 `Probing`**

将 `CheckOnceAsync(CancellationToken)` 的逐项 `foreach` 调用改为先收集可探测项：

```csharp
var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var entries = await FailoverGroupManager.GetDueHealthCheckEntries(_config.ActiveFailoverGroupId, now);
var probingEntries = new List<FailoverQueueEntry>();
var previousStatuses = new Dictionary<string, string>();

foreach (var entry in entries)
{
    previousStatuses[entry.Item.SourceProfileId] = entry.Item.LastStatus;
    if (!FailoverHealthStateMachine.TryMarkProbeStarting(entry.Item, now))
    {
        continue;
    }
    probingEntries.Add(entry);
}

if (probingEntries.Count == 0)
{
    return;
}

await SQLiteHelper.Instance.UpdateAllAsync(probingEntries.Select(entry => entry.Item));
AppEvents.FailoverHealthChangedRequested.Publish();
```

- [ ] **步骤 2：批量调用探测器并应用结果**

继续在 `CheckOnceAsync` 中追加：

```csharp
var probeResults = await _probe.ProbeBatchAsync(
    probingEntries.Select(entry => entry.Profile).ToList(),
    cancellationToken);
var resultMap = probeResults.ToDictionary(result => result.ProfileId, result => result.Result);

foreach (var entry in probingEntries)
{
    var result = resultMap.GetValueOrDefault(entry.Profile.IndexId)
        ?? FailoverHealthProbeResult.Failure("request-failed");

    if (result.Cancelled)
    {
        entry.Item.LastStatus = previousStatuses.GetValueOrDefault(entry.Item.SourceProfileId, FailoverHealthStatus.Unknown);
        continue;
    }

    FailoverHealthStateMachine.ApplyProbeResult(
        entry.Item,
        result,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}

await SQLiteHelper.Instance.UpdateAllAsync(probingEntries.Select(entry => entry.Item));
AppEvents.FailoverHealthChangedRequested.Publish();
```

- [ ] **步骤 3：保留取消语义**

确认 `ProbeAsync(ProfileItem, CancellationToken)` 仍被测试 fake 支持；若旧测试只实现单节点接口，改为实现 `ProbeBatchAsync`。取消结果必须让节点恢复探测前状态，不增加失败次数。

- [ ] **步骤 4：运行健康服务测试**

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FailoverHealthServiceTests
```

预期：通过。

## 任务 6：完整验证与手工验证

**文件：**

- 修改：`_local_changes/007-failover-health-batch-probe/tests.md`

- [ ] **步骤 1：运行目标单元测试**

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FailoverHealthServiceTests|ConnectionHandlerTests|CoreConfigHandler_BatchSpeedtestSuppressFailoverKeepsRequestedNode"
```

预期：通过。

- [ ] **步骤 2：运行完整 ServiceLib 测试**

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj
```

预期：通过。

- [ ] **步骤 3：构建 Desktop 项目**

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

预期：通过，0 错误。

- [ ] **步骤 4：手工验证批量效率**

操作步骤：

1. 准备一个活动故障组，加入至少 `20` 个队列节点。
2. 开启故障转移，观察健康标签从 `未知` 或历史状态变为 `正常` / `故障`。
3. 观察主界面普通延迟列同步出现真实延迟或失败值。
4. 对比任务管理器内存和核心进程启动次数，确认不再每个节点启动一次临时核心。
5. 让 `P1` 故障、`P2` 正常，确认 `P1` 不会被 fallback 误判为 `正常`。

预期结果：

- 同核心类型的一批节点只启动一个临时测速核心。
- 主界面延迟列有真实结果。
- `P1` 故障时仍显示 `故障 P1`，fallback 请求可落到 `P2`。
- 关闭故障转移后不再持续启动临时核心。

## 任务 7：更新记录和补丁

**文件：**

- 修改：`_local_changes/007-failover-health-batch-probe/README.md`
- 修改：`_local_changes/007-failover-health-batch-probe/files.md`
- 修改：`_local_changes/007-failover-health-batch-probe/reapply.md`
- 修改：`_local_changes/007-failover-health-batch-probe/tests.md`
- 修改：`_local_changes/007-failover-health-batch-probe/patch.diff`
- 修改：`_local_changes/README.md`

- [ ] **步骤 1：补齐文件清单**

`files.md` 必须列出所有新增和修改文件，并说明每个文件的职责。

- [ ] **步骤 2：补齐验证记录**

`tests.md` 必须记录任务 6 的自动化和手工验证结果；如果无法执行手工验证，写清原因和残余风险。

- [ ] **步骤 3：生成补丁**

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes\007-failover-health-batch-probe\patch.diff
```

预期：`patch.diff` 只包含本功能相关源码和文档改动，不包含 `_local_changes/` 自身。

- [ ] **步骤 4：检查空白错误**

```powershell
git diff --check -- . ":(exclude)_local_changes"
```

预期：无空白错误。

## 自检清单

- [ ] 批量健康探测传入 `suppressFailover: true`。
- [ ] 手动真连接测速行为未改变。
- [ ] 每个节点仍执行 `2` 次 HTTP GET 并取最小值。
- [ ] 健康探测成功和失败都会写回普通延迟列。
- [ ] 取消探测不会把节点写成 `Failed`。
- [ ] 后台健康探测不做失败批次重试。
- [ ] `_local_changes` 记录包含代理安全影响评估。
