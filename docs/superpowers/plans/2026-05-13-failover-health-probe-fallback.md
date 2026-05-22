# 故障健康探测批量降级实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `CoreFailoverHealthProbe` 在批量真实延迟探测失败或跳过节点时，复用普通真连接测速的“缩小批次、最终单节点重试”策略，避免延迟转移一瞬间把大量可用节点误判为故障。

**Architecture:** 保留当前健康探测的 `suppressFailover: true`、按核心类型分组、临时测速核心和 `FailoverHealthProbeResult` 数据结构。新增一个服务内批量降级调度层：先按 `Global.SpeedTestPageSize` 批量探测；失败节点进入更小批次；当批次足够小时改为单节点探测。只重试可重试失败，不重试 `core-unsupported` 和取消结果。

**Tech Stack:** .NET 8、C#、xUnit、v2rayN `ServiceLib`、Xray/sing_box speedtest config。

---

## 当前证据

- 普通真连接测速入口：`v2rayN/ServiceLib/Services/SpeedtestService.cs`
  - `RunRealPingBatchAsync(...)` 批量失败后把 `pageSize` 减半。
  - 批次很小时降级到 `RunMixedTestAsync(...)`，即每个节点单独启动临时测速核心。
- 延迟转移健康探测入口：`v2rayN/ServiceLib/Services/CoreFailoverHealthProbe.cs`
  - `ProbeBatchAsync(...)` 当前按 `CoreType` 分组后一次性调用 `ProbeCoreGroupAsync(...)`。
  - `ProbeItemAsync(...)` 看到 `AllowTest=false` 会立即返回 `test-skipped`。
  - 批量配置生成没有把某节点置为 `AllowTest=true` 时，会表现为瞬间失败。
- 用户手工测试现象：启用“延迟转移”后健康探测瞬间结束，只有 1 个节点正常，其他故障；右侧延迟列的正常值来自之后手动“真连接延迟”，不是本轮健康探测结果。

## 文件结构

- 修改：`v2rayN/ServiceLib/Services/CoreFailoverHealthProbe.cs`
  - 增加批量降级调度方法。
  - 保留 `ProbeCoreGroupAsync(...)` 作为实际批量探测执行器。
  - 增加失败结果是否可重试的判断。
- 修改：`v2rayN/ServiceLib.Tests/CoreFailoverHealthProbeTests.cs`
  - 增加纯算法级测试，避免真实启动核心或真实网络。
  - 通过反射测试私有辅助方法，延续现有测试风格。
- 新增：`_local_changes/013-failover-health-probe-fallback/`
  - 记录本次行为改动、验证结果、迁移步骤和补丁。
- 修改：`_local_changes/README.md`
  - 追加 013 索引。

## 任务 1：补可重试失败分类

**Files:**
- Modify: `v2rayN/ServiceLib/Services/CoreFailoverHealthProbe.cs`
- Test: `v2rayN/ServiceLib.Tests/CoreFailoverHealthProbeTests.cs`

- [ ] **Step 1: 写失败测试**

在 `CoreFailoverHealthProbeTests` 中追加：

```csharp
[Theory]
[InlineData("test-skipped", true)]
[InlineData("core-start-failed", true)]
[InlineData("request-failed", true)]
[InlineData("probe-failed", true)]
[InlineData("core-unsupported", false)]
public void IsRetryableFailure_ClassifiesFailureReason(string reason, bool expected)
{
    var method = typeof(CoreFailoverHealthProbe).GetMethod(
        "IsRetryableFailure",
        BindingFlags.NonPublic | BindingFlags.Static);

    var result = Assert.IsType<bool>(method?.Invoke(null, [FailoverHealthProbeResult.Failure(reason)]));

    Assert.Equal(expected, result);
}

[Fact]
public void IsRetryableFailure_DoesNotRetryCancelledOrSuccessfulResult()
{
    var method = typeof(CoreFailoverHealthProbe).GetMethod(
        "IsRetryableFailure",
        BindingFlags.NonPublic | BindingFlags.Static);

    var cancelled = Assert.IsType<bool>(method?.Invoke(null, [FailoverHealthProbeResult.Cancel()]));
    var success = Assert.IsType<bool>(method?.Invoke(null, [FailoverHealthProbeResult.Success(88)]));

    Assert.False(cancelled);
    Assert.False(success);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreFailoverHealthProbeTests.IsRetryableFailure" --logger "console;verbosity=minimal"
```

Expected: 编译失败或测试失败，原因是 `IsRetryableFailure` 不存在。

- [ ] **Step 3: 实现最小分类函数**

在 `CoreFailoverHealthProbe` 中加入：

```csharp
private static bool IsRetryableFailure(FailoverHealthProbeResult result)
{
    if (result.IsSuccess || result.Cancelled)
    {
        return false;
    }

    return result.FailureReason is
        "test-skipped"
        or "core-start-failed"
        or "request-failed"
        or "probe-failed";
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreFailoverHealthProbeTests.IsRetryableFailure" --logger "console;verbosity=minimal"
```

Expected: 2 个测试通过。

## 任务 2：提取批次切分和失败节点选择

**Files:**
- Modify: `v2rayN/ServiceLib/Services/CoreFailoverHealthProbe.cs`
- Test: `v2rayN/ServiceLib.Tests/CoreFailoverHealthProbeTests.cs`

- [ ] **Step 1: 写失败测试**

在 `CoreFailoverHealthProbeTests` 中追加：

```csharp
[Fact]
public void GetRetryableFailedItems_ReturnsOnlyRetryableFailedItems()
{
    var items = new List<ServerTestItem>
    {
        new() { IndexId = "ok" },
        new() { IndexId = "skip" },
        new() { IndexId = "unsupported" },
        new() { IndexId = "missing" },
    };
    var results = new Dictionary<string, FailoverHealthProbeResult>
    {
        ["ok"] = FailoverHealthProbeResult.Success(88),
        ["skip"] = FailoverHealthProbeResult.Failure("test-skipped"),
        ["unsupported"] = FailoverHealthProbeResult.Failure("core-unsupported"),
    };
    var method = typeof(CoreFailoverHealthProbe).GetMethod(
        "GetRetryableFailedItems",
        BindingFlags.NonPublic | BindingFlags.Static);

    var retryItems = Assert.IsAssignableFrom<List<ServerTestItem>>(method?.Invoke(null, [items, results]));

    Assert.Equal(["skip", "missing"], retryItems.Select(item => item.IndexId).ToArray());
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FullyQualifiedName~CoreFailoverHealthProbeTests.GetRetryableFailedItems_ReturnsOnlyRetryableFailedItems --logger "console;verbosity=minimal"
```

Expected: 失败，原因是 `GetRetryableFailedItems` 不存在。

- [ ] **Step 3: 实现辅助函数**

在 `CoreFailoverHealthProbe` 中加入：

```csharp
private static List<ServerTestItem> GetRetryableFailedItems(
    IReadOnlyList<ServerTestItem> items,
    IReadOnlyDictionary<string, FailoverHealthProbeResult> results)
{
    return items
        .Where(item => item.IndexId.IsNotEmpty())
        .Where(item => !results.TryGetValue(item.IndexId, out var result) || IsRetryableFailure(result))
        .ToList();
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FullyQualifiedName~CoreFailoverHealthProbeTests.GetRetryableFailedItems_ReturnsOnlyRetryableFailedItems --logger "console;verbosity=minimal"
```

Expected: 通过。

## 任务 3：实现批量降级调度

**Files:**
- Modify: `v2rayN/ServiceLib/Services/CoreFailoverHealthProbe.cs`
- Test: `v2rayN/ServiceLib.Tests/CoreFailoverHealthProbeTests.cs`

- [ ] **Step 1: 写可单元测试的页大小函数**

在 `CoreFailoverHealthProbeTests` 中追加：

```csharp
[Theory]
[InlineData(0, 0)]
[InlineData(1, 1)]
[InlineData(5, 5)]
[InlineData(100, 100)]
public void GetInitialProbePageSize_NeverExceedsItemCount(int count, int expectedUpperBound)
{
    var method = typeof(CoreFailoverHealthProbe).GetMethod(
        "GetInitialProbePageSize",
        BindingFlags.NonPublic | BindingFlags.Static);

    var result = Assert.IsType<int>(method?.Invoke(null, [count]));

    Assert.True(result >= 0);
    Assert.True(result <= expectedUpperBound);
    if (count > 0)
    {
        Assert.True(result > 0);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FullyQualifiedName~CoreFailoverHealthProbeTests.GetInitialProbePageSize_NeverExceedsItemCount --logger "console;verbosity=minimal"
```

Expected: 失败，原因是 `GetInitialProbePageSize` 不存在。

- [ ] **Step 3: 实现页大小函数**

在 `CoreFailoverHealthProbe` 中加入：

```csharp
private static int GetInitialProbePageSize(int itemCount)
{
    if (itemCount <= 0)
    {
        return 0;
    }

    return Math.Min(itemCount, Global.SpeedTestPageSize);
}
```

- [ ] **Step 4: 增加调度方法**

把 `ProbeBatchAsync(...)` 中原来的：

```csharp
var groupItems = group.ToList();
var groupResults = await ProbeCoreGroupAsync(groupItems, cancellationToken, updateFunc);
for (var i = 0; i < groupItems.Count; i++)
{
    results[groupItems[i].QueueNum] = i < groupResults.Count
        ? groupResults[i]
        : await MarkItemFailed(groupItems[i], "request-failed", updateFunc);
}
```

替换为：

```csharp
var groupItems = group.ToList();
var groupResults = await ProbeCoreGroupWithFallbackAsync(groupItems, cancellationToken, updateFunc);
for (var i = 0; i < groupItems.Count; i++)
{
    results[groupItems[i].QueueNum] = groupResults.TryGetValue(groupItems[i].IndexId, out var result)
        ? new FailoverHealthProbeBatchResult(groupItems[i].IndexId, result)
        : await MarkItemFailed(groupItems[i], "request-failed", updateFunc);
}
```

在 `CoreFailoverHealthProbe` 中加入：

```csharp
private async Task<Dictionary<string, FailoverHealthProbeResult>> ProbeCoreGroupWithFallbackAsync(
    List<ServerTestItem> testItems,
    CancellationToken cancellationToken,
    Func<FailoverHealthProbeProgress, Task>? updateFunc)
{
    var results = new Dictionary<string, FailoverHealthProbeResult>();
    var pageSize = GetInitialProbePageSize(testItems.Count);
    if (pageSize <= 0)
    {
        return results;
    }

    await ProbePagedAsync(testItems, pageSize, results, cancellationToken, updateFunc);
    return results;
}

private async Task ProbePagedAsync(
    List<ServerTestItem> testItems,
    int pageSize,
    Dictionary<string, FailoverHealthProbeResult> results,
    CancellationToken cancellationToken,
    Func<FailoverHealthProbeProgress, Task>? updateFunc)
{
    foreach (var batch in testItems.Chunk(pageSize).Select(chunk => chunk.ToList()))
    {
        if (cancellationToken.IsCancellationRequested)
        {
            foreach (var item in batch)
            {
                results[item.IndexId] = FailoverHealthProbeResult.Cancel();
            }
            return;
        }

        var batchResults = await ProbeCoreGroupAsync(batch, cancellationToken, updateFunc);
        foreach (var result in batchResults)
        {
            results[result.ProfileId] = result.Result;
        }

        var retryItems = GetRetryableFailedItems(batch, results);
        if (retryItems.Count == 0 || pageSize <= 1)
        {
            continue;
        }

        var nextPageSize = Math.Max(1, pageSize / 2);
        await ProbePagedAsync(retryItems, nextPageSize, results, cancellationToken, updateFunc);
    }
}
```

- [ ] **Step 5: 运行核心测试**

Run:

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FullyQualifiedName~CoreFailoverHealthProbeTests --logger "console;verbosity=minimal"
```

Expected: `CoreFailoverHealthProbeTests` 全部通过。

## 任务 4：防止降级时覆盖已经成功的结果

**Files:**
- Modify: `v2rayN/ServiceLib/Services/CoreFailoverHealthProbe.cs`
- Test: `v2rayN/ServiceLib.Tests/CoreFailoverHealthProbeTests.cs`

- [ ] **Step 1: 写失败测试**

在 `CoreFailoverHealthProbeTests` 中追加：

```csharp
[Fact]
public void GetRetryableFailedItems_DoesNotRetrySuccessfulItems()
{
    var items = new List<ServerTestItem>
    {
        new() { IndexId = "normal" },
        new() { IndexId = "failed" },
    };
    var results = new Dictionary<string, FailoverHealthProbeResult>
    {
        ["normal"] = FailoverHealthProbeResult.Success(100),
        ["failed"] = FailoverHealthProbeResult.Failure("request-failed"),
    };
    var method = typeof(CoreFailoverHealthProbe).GetMethod(
        "GetRetryableFailedItems",
        BindingFlags.NonPublic | BindingFlags.Static);

    var retryItems = Assert.IsAssignableFrom<List<ServerTestItem>>(method?.Invoke(null, [items, results]));

    Assert.Equal(["failed"], retryItems.Select(item => item.IndexId).ToArray());
}
```

- [ ] **Step 2: 运行测试确认通过或按需修正**

Run:

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FullyQualifiedName~CoreFailoverHealthProbeTests.GetRetryableFailedItems_DoesNotRetrySuccessfulItems --logger "console;verbosity=minimal"
```

Expected: 通过。若失败，修正 `GetRetryableFailedItems`，确保成功项不会进入重试列表。

## 任务 5：更新本地改动记录

**Files:**
- Create: `_local_changes/013-failover-health-probe-fallback/README.md`
- Create: `_local_changes/013-failover-health-probe-fallback/files.md`
- Create: `_local_changes/013-failover-health-probe-fallback/reapply.md`
- Create: `_local_changes/013-failover-health-probe-fallback/tests.md`
- Create: `_local_changes/013-failover-health-probe-fallback/patch.diff`
- Modify: `_local_changes/README.md`

- [ ] **Step 1: 创建目录和说明文件**

创建 `_local_changes/013-failover-health-probe-fallback/README.md`：

```markdown
# 故障健康探测批量降级

## 改动目的

让延迟转移和故障转移健康探测在批量真实延迟探测失败时，像普通真连接测速一样缩小批次并最终单节点重试，减少可用节点被瞬间误判为故障的情况。

## 用户可见行为

- 开启延迟转移时，健康探测不再因为某个大批次配置或启动异常而直接把大量节点标记为故障。
- 批量失败节点会被自动重试，最终仍失败才显示故障。
- 右侧普通延迟列和左侧健康标签仍是两套显示，但健康标签更接近普通真连接测速结果。

## 代理安全影响评估

本改动不改变真实代理链路、路由、Tun、系统代理、DNS、证书或订阅逻辑。健康探测仍使用 `suppressFailover: true`，避免通过活动故障组兜底后误判真实节点状态。新增降级重试会增加临时测速核心启动次数，但不会扩大代理边界。
```

- [ ] **Step 2: 创建文件清单**

创建 `_local_changes/013-failover-health-probe-fallback/files.md`：

```markdown
# 文件变更

- `v2rayN/ServiceLib/Services/CoreFailoverHealthProbe.cs`
  - 新增健康探测批量降级调度。
  - 新增可重试失败分类。
  - 保留 `core-unsupported` 和取消结果不重试。

- `v2rayN/ServiceLib.Tests/CoreFailoverHealthProbeTests.cs`
  - 新增失败分类、失败节点选择和页大小辅助方法测试。

- `_local_changes/README.md`
  - 追加 013 改动索引。
```

- [ ] **Step 3: 创建迁移说明**

创建 `_local_changes/013-failover-health-probe-fallback/reapply.md`：

```markdown
# 重新应用步骤

1. 先确认 `CoreFailoverHealthProbe` 仍负责延迟转移健康探测。
2. 在该类中添加 `IsRetryableFailure`、`GetRetryableFailedItems`、`GetInitialProbePageSize`。
3. 把按核心类型分组后的直接 `ProbeCoreGroupAsync` 调用替换为 `ProbeCoreGroupWithFallbackAsync`。
4. 保持 `suppressFailover: true` 不变。
5. 迁移 `CoreFailoverHealthProbeTests` 中新增测试。
6. 运行 `CoreFailoverHealthProbeTests`、`FailoverHealthServiceTests` 和故障转移相关测试。
```

- [ ] **Step 4: 创建验证记录**

创建 `_local_changes/013-failover-health-probe-fallback/tests.md`，记录实际执行的红绿测试命令、结果，以及是否做了真实手工测试。

- [ ] **Step 5: 更新索引**

在 `_local_changes/README.md` 改动索引表追加：

```markdown
| 013 | `013-failover-health-probe-fallback/` | 已验证自动化 | 故障健康探测批量失败后缩小批次并单节点重试，减少延迟转移误判故障 |
```

- [ ] **Step 6: 生成补丁**

Run:

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes\013-failover-health-probe-fallback\patch.diff
```

Expected: `patch.diff` 只包含 `CoreFailoverHealthProbe.cs` 和 `CoreFailoverHealthProbeTests.cs` 的源码测试改动。

## 任务 6：最终验证

**Files:**
- Test only.

- [ ] **Step 1: 运行健康探测测试**

Run:

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FullyQualifiedName~CoreFailoverHealthProbeTests --logger "console;verbosity=minimal"
```

Expected: 全部通过。

- [ ] **Step 2: 运行故障转移相关测试**

Run:

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreFailoverHealthProbeTests|FullyQualifiedName~FailoverHealthServiceTests|FullyQualifiedName~FailoverHealthDisplayTests|FullyQualifiedName~FailoverModeConfigTests|FullyQualifiedName~MsgViewModelFailoverModeTests|FullyQualifiedName~FailoverGroupManagerTests" --logger "console;verbosity=minimal"
```

Expected: 全部通过。若整套 `ServiceLib.Tests` 出现 SQLite `savePoint is not valid`，按既有共享数据库并发问题记录，不把它误判为本改动失败。

- [ ] **Step 3: 编译 Desktop 项目**

Run:

```powershell
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj
```

Expected: 0 个错误。若出现既有 nullable 或 RID 警告，记录到 `tests.md`。

## 手工验收建议

1. 准备包含多个 VLESS Reality 节点的故障分组。
2. 手动清空或忽略普通延迟列的历史结果。
3. 开启“延迟转移”。
4. 观察健康标签不应一瞬间把大多数节点判为故障；失败节点应有更长探测过程。
5. 再执行 v2rayN 自带“真连接延迟”，对比健康标签和普通延迟列是否明显更一致。

## 风险和边界

- 降级重试会增加临时测速核心启动次数，开启延迟转移的首轮探测可能比现在慢。
- 不建议直接用普通延迟列历史值覆盖健康状态，因为延迟转移需要本轮可用性，而不是历史缓存。
- 不改变 `FailoverHealthStateMachine` 的失败次数和冷却语义；最终失败仍按现有规则进入故障状态。
