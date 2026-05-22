# 转移分组标题展示排序实施计划

> **给 agentic workers：** 必须使用子技能 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans`，按任务逐项实施。本计划使用 checkbox (`- [ ]`) 跟踪进度。

**目标：** 让转移分组在关闭模式下支持普通标题展示排序，在开启 `故障转移` 或 `延迟转移` 时固定队列节点置顶且只排序非队列候选节点。

**架构：** 把普通分组标题排序中的列比较规则抽成可复用的纯排序方法；普通分组继续在排序后写回 `ProfileEx.Sort`，转移分组只复用同一套比较规则做 UI 内存态展示排序。转移队列段顺序仍由 `FailoverGroupManager.GetQueueEntriesForMode` 决定，标题排序不得写入 `ProfileEx.Sort` 或 `FailoverGroupItem.Sort`。

**技术栈：** .NET、C#、ReactiveUI、Avalonia DataGrid、xUnit、ServiceLib 测试项目。

---

## 文件结构

- 修改 `v2rayN/ServiceLib/Handler/ConfigHandler.cs`
  - 抽出普通标题排序使用的共享列比较方法。
  - `SortServers` 继续负责普通分组持久化排序，只把排序列表生成逻辑委托给共享方法。
  - 共享方法必须保留现有列语义：延迟和速度按数值排序，空延迟和空速度沿用普通排序的末尾处理，流量统计按可比较数值语义排序。

- 修改 `v2rayN/ServiceLib/Models/ProfileItemModel.cs`
  - 新增流量统计原始数值字段，仅供标题排序使用。
  - 展示字段 `TodayDown`、`TodayUp`、`TotalDown`、`TotalUp` 继续保存当前 UI 文本。

- 修改 `v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
  - 新增转移分组展示排序状态，按转移分组 Id 记录排序列、方向和是否由用户点击激活。
  - 新增可测试的 `ApplyTransferGroupDisplaySort` 辅助方法，用共享列比较方法排序整个转移分组或候选段。
  - 修改 `SortServer`，普通分组和转移分组分流。
  - 修改 `GetProfileItemsEx` 的最终排序逻辑，开启模式保护队列段，关闭模式只在用户点击标题后排序整个组合列表。
  - 修改 `FailoverStateChangedRequested` 订阅：切到 `Failover` 或 `LeastDelay` 时刷新并置顶队列；当前分组在开启模式下成为活动转移分组时也刷新；切回 `Off` 时不主动套用旧标题排序。

- 新增 `v2rayN/ServiceLib.Tests/TransferGroupDisplaySortTests.cs`
  - 覆盖共享列比较规则、转移分组分段排序、关闭模式排序、模式切换语义和持久化副作用。

- 新增 `_local_changes/018-transfer-group-display-sort/`
  - 记录本次功能改动目的、文件清单、重新应用步骤、验证结果和功能补丁。
  - 目录必须包含 `README.md`、`files.md`、`reapply.md`、`tests.md`、`patch.diff`。
  - `README.md` 或 `tests.md` 必须包含“代理安全影响评估”小节。
  - `patch.diff` 生成时排除 `_local_changes/` 自身，避免把迁移记录混入功能补丁。

---

### 任务 1：为共享列排序写失败测试

**文件：**
- 新建：`v2rayN/ServiceLib.Tests/TransferGroupDisplaySortTests.cs`
- 测试目标：`ConfigHandler.SortProfileDisplayItemsForHeader`

- [ ] **步骤 1：新增测试文件**

创建 `v2rayN/ServiceLib.Tests/TransferGroupDisplaySortTests.cs`，先写共享排序规则测试：

```csharp
using ServiceLib.Handler;
using ServiceLib.Models;
using Xunit;

namespace ServiceLib.Tests;

public class TransferGroupDisplaySortTests
{
    [Fact]
    public void SortProfileDisplayItemsForHeader_DelayAscendingKeepsMissingDelayAtEnd()
    {
        var items = new[]
        {
            CreateItem("missing", "missing", delay: 0),
            CreateItem("slow", "slow", delay: 90),
            CreateItem("fast", "fast", delay: 20),
        };

        var ordered = ConfigHandler.SortProfileDisplayItemsForHeader(items, "DelayVal", asc: true);

        Assert.Equal(["fast", "slow", "missing"], ordered.Select(item => item.IndexId).ToArray());
    }

    [Fact]
    public void SortProfileDisplayItemsForHeader_SpeedAscendingKeepsMissingSpeedAtEnd()
    {
        var items = new[]
        {
            CreateItem("missing", "missing", speed: 0),
            CreateItem("slow", "slow", speed: 10),
            CreateItem("fast", "fast", speed: 100),
        };

        var ordered = ConfigHandler.SortProfileDisplayItemsForHeader(items, "SpeedVal", asc: true);

        Assert.Equal(["slow", "fast", "missing"], ordered.Select(item => item.IndexId).ToArray());
    }

    [Fact]
    public void SortProfileDisplayItemsForHeader_TrafficColumnsUseComparableNumericText()
    {
        var items = new[]
        {
            CreateItem("large", "large", todayDownValue: 100),
            CreateItem("small", "small", todayDownValue: 2),
            CreateItem("none", "none", todayDownValue: 0),
        };

        var ordered = ConfigHandler.SortProfileDisplayItemsForHeader(items, "TodayDown", asc: true);

        Assert.Equal(["none", "small", "large"], ordered.Select(item => item.IndexId).ToArray());
    }

    [Fact]
    public void SortProfileDisplayItemsForHeader_UnsupportedColumnKeepsOriginalOrder()
    {
        var items = new[]
        {
            CreateItem("z", "zeta"),
            CreateItem("a", "alpha"),
            CreateItem("m", "middle"),
        };

        var ordered = ConfigHandler.SortProfileDisplayItemsForHeader(items, "NotAColumn", asc: true);

        Assert.Equal(["z", "a", "m"], ordered.Select(item => item.IndexId).ToArray());
    }

    private static ProfileItemModel CreateItem(
        string indexId,
        string remarks,
        int delay = 0,
        decimal speed = 0,
        long todayDownValue = 0)
    {
        return new ProfileItemModel
        {
            IndexId = indexId,
            Remarks = remarks,
            Address = $"198.51.100.{Math.Abs(indexId.GetHashCode()) % 200 + 1}",
            Port = indexId.Length,
            Network = "tcp",
            StreamSecurity = string.Empty,
            Subid = "sub-a",
            SubRemarks = "sub-a",
            Sort = indexId.Length * 10,
            Delay = delay,
            Speed = speed,
            TodayDown = $"{todayDownValue} B",
            TodayUp = "0000000000000000",
            TotalDown = "0000000000000000",
            TotalUp = "0000000000000000",
            TodayDownValue = todayDownValue,
        };
    }
}
```

- [ ] **步骤 2：运行测试确认红灯**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FullyQualifiedName~TransferGroupDisplaySortTests --logger "console;verbosity=minimal"
```

预期：编译失败，错误包含 `ConfigHandler` 没有 `SortProfileDisplayItemsForHeader` 定义；如果同时提示 `ProfileItemModel` 缺少原始统计字段，也属于本任务预期红灯。

---

### 任务 2：补充可排序的流量统计字段并抽出共享方法

**文件：**
- 修改：`v2rayN/ServiceLib/Models/ProfileItemModel.cs`
- 修改：`v2rayN/ServiceLib/Handler/ConfigHandler.cs`
- 测试：`v2rayN/ServiceLib.Tests/TransferGroupDisplaySortTests.cs`

- [ ] **步骤 1：在 `ProfileItemModel` 中添加原始统计字段**

在 `ProfileItemModel` 中添加：

```csharp
public long TodayDownValue { get; set; }
public long TodayUpValue { get; set; }
public long TotalDownValue { get; set; }
public long TotalUpValue { get; set; }
```

- [ ] **步骤 2：在 `ConfigHandler.SortServers` 投影中填充原始统计字段**

在 `SortServers` 构造 `ProfileItemModel` 时，补充：

```csharp
TodayDownValue = t22?.TodayDown ?? 0,
TodayUpValue = t22?.TodayUp ?? 0,
TotalDownValue = t22?.TotalDown ?? 0,
TotalUpValue = t22?.TotalUp ?? 0,
```

原有 `TodayDown`、`TodayUp`、`TotalDown`、`TotalUp` 可继续保留可比较字符串，避免扩大本次普通分组排序行为变更。

- [ ] **步骤 3：在 `ConfigHandler` 中添加共享排序方法**

新增公开静态方法，方法只返回排序后的新列表，不写入任何持久化字段。未知列名必须保持原顺序，避免点击异常列或未来新增列时意外按 `Sort` 重排：

```csharp
public static bool TryGetServerHeaderSortColumn(string? colName, out EServerColName name)
{
    name = EServerColName.Def;
    return colName.IsNotEmpty()
        && Enum.TryParse(colName, true, out name)
        && name != EServerColName.Def;
}

public static List<ProfileItemModel> SortProfileDisplayItemsForHeader(
    IEnumerable<ProfileItemModel> items,
    string? colName,
    bool asc)
{
    var source = items?.ToList() ?? [];
    if (source.Count <= 1 || !TryGetServerHeaderSortColumn(colName, out var name))
    {
        return source;
    }

    var ordered = asc
        ? name switch
        {
            EServerColName.ConfigType => source.OrderBy(t => t.ConfigType),
            EServerColName.Remarks => source.OrderBy(t => t.Remarks),
            EServerColName.Address => source.OrderBy(t => t.Address),
            EServerColName.Port => source.OrderBy(t => t.Port),
            EServerColName.Network => source.OrderBy(t => t.Network),
            EServerColName.StreamSecurity => source.OrderBy(t => t.StreamSecurity),
            EServerColName.DelayVal => source.OrderBy(t => t.Delay <= 0).ThenBy(t => t.Delay),
            EServerColName.SpeedVal => source.OrderBy(t => t.Speed <= 0).ThenBy(t => t.Speed),
            EServerColName.SubRemarks => source.OrderBy(t => t.Subid),
            EServerColName.TodayDown => source.OrderBy(t => t.TodayDownValue),
            EServerColName.TodayUp => source.OrderBy(t => t.TodayUpValue),
            EServerColName.TotalDown => source.OrderBy(t => t.TotalDownValue),
            EServerColName.TotalUp => source.OrderBy(t => t.TotalUpValue),
            _ => source,
        }
        : name switch
        {
            EServerColName.ConfigType => source.OrderByDescending(t => t.ConfigType),
            EServerColName.Remarks => source.OrderByDescending(t => t.Remarks),
            EServerColName.Address => source.OrderByDescending(t => t.Address),
            EServerColName.Port => source.OrderByDescending(t => t.Port),
            EServerColName.Network => source.OrderByDescending(t => t.Network),
            EServerColName.StreamSecurity => source.OrderByDescending(t => t.StreamSecurity),
            EServerColName.DelayVal => source.OrderBy(t => t.Delay <= 0).ThenByDescending(t => t.Delay),
            EServerColName.SpeedVal => source.OrderBy(t => t.Speed <= 0).ThenByDescending(t => t.Speed),
            EServerColName.SubRemarks => source.OrderByDescending(t => t.Subid),
            EServerColName.TodayDown => source.OrderByDescending(t => t.TodayDownValue),
            EServerColName.TodayUp => source.OrderByDescending(t => t.TodayUpValue),
            EServerColName.TotalDown => source.OrderByDescending(t => t.TotalDownValue),
            EServerColName.TotalUp => source.OrderByDescending(t => t.TotalUpValue),
            _ => source,
        };

    return ordered.ToList();
}
```

- [ ] **步骤 4：让 `SortServers` 复用共享方法**

在 `ConfigHandler.SortServers` 中，保留构造 `lstProfile` 的查询逻辑，删除原有 `switch` 排序块，改为：

```csharp
lstProfile = SortProfileDisplayItemsForHeader(lstProfile, colName, asc);
```

保留后续 `ProfileExManager.Instance.SetSort` 写回逻辑。延迟和速度空值末尾处理已经进入共享方法后，删除 `switch (name)` 中对 `DelayVal` / `SpeedVal` 的二次写回，避免普通分组和转移分组规则分叉。

- [ ] **步骤 5：运行共享排序测试**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FullyQualifiedName~TransferGroupDisplaySortTests --logger "console;verbosity=minimal"
```

预期：任务 1 中新增的共享排序测试通过。

---

### 任务 3：实现转移分组展示排序纯方法

**文件：**
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
- 修改：`v2rayN/ServiceLib.Tests/TransferGroupDisplaySortTests.cs`

- [ ] **步骤 1：补充分段排序测试**

先在 `TransferGroupDisplaySortTests` 文件顶部补充引用：

```csharp
using ServiceLib.Enums;
using ServiceLib.ViewModels;
```

在 `TransferGroupDisplaySortTests` 中追加：

```csharp
[Fact]
public void ApplyTransferGroupDisplaySort_OffModeSortsWholeListByColumn()
{
    var items = new[]
    {
        CreateItem("queue-z", "zeta", isInQueue: true, failoverPriority: 1),
        CreateItem("candidate-a", "alpha", isInQueue: false),
        CreateItem("candidate-m", "middle", isInQueue: false),
    };

    var ordered = ProfilesViewModel.ApplyTransferGroupDisplaySort(
        items,
        pinQueueItems: false,
        colName: "Remarks",
        asc: true);

    Assert.Equal(["candidate-a", "candidate-m", "queue-z"], ordered.Select(item => item.IndexId).ToArray());
}

[Fact]
public void ApplyTransferGroupDisplaySort_OnModeKeepsQueueOrderAndSortsCandidates()
{
    var items = new[]
    {
        CreateItem("queue-p2", "bravo", isInQueue: true, failoverPriority: 2),
        CreateItem("candidate-z", "zulu", isInQueue: false),
        CreateItem("queue-p1", "charlie", isInQueue: true, failoverPriority: 1),
        CreateItem("candidate-a", "alpha", isInQueue: false),
    };

    var ordered = ProfilesViewModel.ApplyTransferGroupDisplaySort(
        items,
        pinQueueItems: true,
        colName: "Remarks",
        asc: true);

    Assert.Equal(["queue-p2", "queue-p1", "candidate-a", "candidate-z"], ordered.Select(item => item.IndexId).ToArray());
}
```

把测试文件中的 `CreateItem` 扩展为可设置队列字段：

```csharp
private static ProfileItemModel CreateItem(
    string indexId,
    string remarks,
    int delay = 0,
    decimal speed = 0,
    long todayDownValue = 0,
    bool isInQueue = false,
    int failoverPriority = 0)
{
    return new ProfileItemModel
    {
        IndexId = indexId,
        Remarks = remarks,
        Address = $"198.51.100.{Math.Abs(indexId.GetHashCode()) % 200 + 1}",
        Port = indexId.Length,
        Network = "tcp",
        StreamSecurity = string.Empty,
        Subid = "sub-a",
        SubRemarks = "sub-a",
        Sort = indexId.Length * 10,
        Delay = delay,
        Speed = speed,
        TodayDown = $"{todayDownValue} B",
        TodayUp = "0000000000000000",
        TotalDown = "0000000000000000",
        TotalUp = "0000000000000000",
        TodayDownValue = todayDownValue,
        IsInFailoverQueue = isInQueue,
        FailoverPriority = failoverPriority,
    };
}
```

- [ ] **步骤 2：新增 `ApplyTransferGroupDisplaySort`**

在 `ProfilesViewModel` 中新增：

```csharp
public static List<ProfileItemModel> ApplyTransferGroupDisplaySort(
    IEnumerable<ProfileItemModel> items,
    bool pinQueueItems,
    string? colName,
    bool asc)
{
    var source = items?.ToList() ?? [];
    if (source.Count <= 1 || colName.IsNullOrEmpty())
    {
        return source;
    }

    if (!pinQueueItems)
    {
        return ConfigHandler.SortProfileDisplayItemsForHeader(source, colName, asc);
    }

    var queueItems = source.Where(item => item.IsInFailoverQueue).ToList();
    var candidateItems = source.Where(item => !item.IsInFailoverQueue).ToList();
    candidateItems = ConfigHandler.SortProfileDisplayItemsForHeader(candidateItems, colName, asc);

    return queueItems.Concat(candidateItems).ToList();
}
```

- [ ] **步骤 3：运行分段排序测试**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FullyQualifiedName~TransferGroupDisplaySortTests --logger "console;verbosity=minimal"
```

预期：共享排序和分段排序测试均通过。

---

### 任务 4：接入主列表标题排序分流

**文件：**
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`

- [ ] **步骤 1：新增内存态排序结构**

在 `ProfilesViewModel` 私有字段区域添加：

```csharp
private sealed record TransferGroupSortState(string Column, bool Asc, bool Active);
private readonly Dictionary<string, TransferGroupSortState> _transferGroupSortStates = new();
```

- [ ] **步骤 2：修改 `SortServer` 分流**

把 `SortServer` 修改为：

```csharp
public async Task SortServer(string colName)
{
    if (!ConfigHandler.TryGetServerHeaderSortColumn(colName, out _))
    {
        return;
    }

    _dicHeaderSort.TryAdd(colName, true);
    _dicHeaderSort.TryGetValue(colName, out var asc);

    if (CurrentGroupIsFailoverGroup && _config.SubIndexId.IsNotEmpty())
    {
        _transferGroupSortStates[_config.SubIndexId] = new TransferGroupSortState(colName, asc, true);
        _dicHeaderSort[colName] = !asc;
        await RefreshServers();
        return;
    }

    if (await ConfigHandler.SortServers(_config, _config.SubIndexId, colName, asc) != 0)
    {
        return;
    }
    _dicHeaderSort[colName] = !asc;
    await RefreshServers();
}
```

- [ ] **步骤 3：修改 `GetProfileItemsEx` 最终排序**

先在 `GetProfileItemsEx` 构造 `ProfileItemModel` 时填充原始统计值：

```csharp
TodayDownValue = t22?.TodayDown ?? 0,
TodayUpValue = t22?.TodayUp ?? 0,
TotalDownValue = t22?.TotalDown ?? 0,
TotalUpValue = t22?.TotalUp ?? 0,
```

保留现有投影后的基础排序。基础排序用于没有用户标题排序时沿用当前队列优先展示。

在 `foreach (var item in lstModel)` 前添加：

```csharp
if (isFailoverGroup
    && _transferGroupSortStates.TryGetValue(subid, out var sortState)
    && sortState.Active)
{
    var pinQueueItems = isActiveFailoverGroup && failoverMode != EFailoverMode.Off;
    lstModel = ApplyTransferGroupDisplaySort(
        lstModel,
        pinQueueItems,
        sortState.Column,
        sortState.Asc);
}
```

- [ ] **步骤 4：运行相关测试**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~TransferGroupDisplaySortTests|FullyQualifiedName~FailoverHealthDisplayTests" --logger "console;verbosity=minimal"
```

预期：测试全部通过。

---

### 任务 5：处理转移状态变化刷新语义

**文件：**
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
- 修改：`v2rayN/ServiceLib.Tests/TransferGroupDisplaySortTests.cs`

- [ ] **步骤 1：新增转移状态变化判定纯函数**

在 `ProfilesViewModel` 中新增可测试方法：

```csharp
public static bool ShouldRefreshTransferGroupForStateChange(
    EFailoverMode oldMode,
    EFailoverMode newMode,
    string? oldActiveGroupId,
    string? newActiveGroupId,
    string? currentGroupId)
{
    if (currentGroupId.IsNullOrEmpty())
    {
        return false;
    }

    if (oldMode == EFailoverMode.Off && newMode != EFailoverMode.Off && newActiveGroupId == currentGroupId)
    {
        return true;
    }

    return newMode != EFailoverMode.Off
        && oldActiveGroupId != newActiveGroupId
        && newActiveGroupId == currentGroupId;
}

public static bool ShouldDeactivateTransferGroupSortForStateChange(
    EFailoverMode oldMode,
    EFailoverMode newMode,
    string? currentGroupId)
{
    return currentGroupId.IsNotEmpty()
        && oldMode != EFailoverMode.Off
        && newMode == EFailoverMode.Off;
}
```

在 `TransferGroupDisplaySortTests` 中追加：

```csharp
[Fact]
public void ShouldRefreshTransferGroupForStateChange_RefreshesWhenSwitchingOnCurrentActiveGroup()
{
    Assert.True(ProfilesViewModel.ShouldRefreshTransferGroupForStateChange(
        EFailoverMode.Off,
        EFailoverMode.Failover,
        oldActiveGroupId: "group-a",
        newActiveGroupId: "group-a",
        currentGroupId: "group-a"));
}

[Fact]
public void ShouldRefreshTransferGroupForStateChange_RefreshesWhenCurrentGroupBecomesActive()
{
    Assert.True(ProfilesViewModel.ShouldRefreshTransferGroupForStateChange(
        EFailoverMode.Failover,
        EFailoverMode.Failover,
        oldActiveGroupId: "group-b",
        newActiveGroupId: "group-a",
        currentGroupId: "group-a"));
}

[Fact]
public void ShouldRefreshTransferGroupForStateChange_DoesNotRefreshWhenSwitchingOff()
{
    Assert.False(ProfilesViewModel.ShouldRefreshTransferGroupForStateChange(
        EFailoverMode.Failover,
        EFailoverMode.Off,
        oldActiveGroupId: "group-a",
        newActiveGroupId: "group-a",
        currentGroupId: "group-a"));
}

[Fact]
public void ShouldDeactivateTransferGroupSortForStateChange_DeactivatesWhenSwitchingOff()
{
    Assert.True(ProfilesViewModel.ShouldDeactivateTransferGroupSortForStateChange(
        EFailoverMode.LeastDelay,
        EFailoverMode.Off,
        currentGroupId: "group-a"));
}
```

- [ ] **步骤 2：修改事件订阅**

给 `ProfilesViewModel` 添加字段：

```csharp
private EFailoverMode _lastFailoverMode;
private string? _lastActiveFailoverGroupId;
```

构造函数初始化配置加载后，确保 `_lastFailoverMode` 和 `_lastActiveFailoverGroupId` 记录当前状态。把 `FailoverStateChangedRequested` 订阅改为：

```csharp
AppEvents.FailoverStateChangedRequested
    .AsObservable()
    .ObserveOn(RxSchedulers.MainThreadScheduler)
    .Subscribe(async _ =>
    {
        var oldMode = _lastFailoverMode;
        var newMode = _config.FailoverMode;
        var oldActiveGroupId = _lastActiveFailoverGroupId;
        var newActiveGroupId = _config.ActiveFailoverGroupId;
        _lastFailoverMode = newMode;
        _lastActiveFailoverGroupId = newActiveGroupId;

        UpdateFailoverGroupState();
        if (CurrentGroupIsFailoverGroup
            && ShouldDeactivateTransferGroupSortForStateChange(
                oldMode,
                newMode,
                _config.SubIndexId)
            && _transferGroupSortStates.TryGetValue(_config.SubIndexId, out var sortState))
        {
            _transferGroupSortStates[_config.SubIndexId] = sortState with { Active = false };
        }

        if (CurrentGroupIsFailoverGroup
            && ShouldRefreshTransferGroupForStateChange(
                oldMode,
                newMode,
                oldActiveGroupId,
                newActiveGroupId,
                _config.SubIndexId))
        {
            await RefreshServersBiz();
        }
    });
```

说明：切到 `Failover` 或 `LeastDelay` 且当前分组是活动分组时刷新并把队列置顶；模式已开启且当前分组刚成为活动分组时也刷新；切回 `Off` 时把旧标题排序状态标为未激活，避免后续普通刷新自动套用旧排序。用户在 `Off` 状态再次点击标题时，`SortServer` 会重新激活关闭模式下的整表排序。

- [ ] **步骤 3：运行模式切换测试**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~TransferGroupDisplaySortTests|FullyQualifiedName~MsgViewModelFailoverModeTests|FullyQualifiedName~FailoverModeConfigTests" --logger "console;verbosity=minimal"
```

预期：测试全部通过。

---

### 任务 6：验证无持久化副作用

**文件：**
- 修改：`v2rayN/ServiceLib.Tests/TransferGroupDisplaySortTests.cs`

- [ ] **步骤 1：补充对象级副作用测试**

在 `TransferGroupDisplaySortTests` 中追加：

```csharp
[Fact]
public void ApplyTransferGroupDisplaySort_DoesNotModifyQueueFieldsOrSortFields()
{
    var queue = CreateItem("queue", "zeta", isInQueue: true, failoverPriority: 7);
    var candidate = CreateItem("candidate", "alpha", isInQueue: false);
    var originalQueueSort = queue.Sort;
    var originalCandidateSort = candidate.Sort;

    _ = ProfilesViewModel.ApplyTransferGroupDisplaySort(
        [candidate, queue],
        pinQueueItems: true,
        colName: "Remarks",
        asc: true);

    Assert.Equal(7, queue.FailoverPriority);
    Assert.True(queue.IsInFailoverQueue);
    Assert.Equal(originalQueueSort, queue.Sort);
    Assert.Equal(originalCandidateSort, candidate.Sort);
}
```

- [ ] **步骤 2：手工核对 `SortServer` 分流**

确认 `CurrentGroupIsFailoverGroup == true` 分支只写 `_transferGroupSortStates` 并调用 `RefreshServers()`，不调用：

```csharp
ConfigHandler.SortServers(...)
ProfileExManager.Instance.SetSort(...)
FailoverGroupManager.MoveQueueItem(...)
SQLiteHelper.Instance.ReplaceAsync(... FailoverGroupItem ...)
```

- [ ] **步骤 3：运行目标测试**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FullyQualifiedName~TransferGroupDisplaySortTests --logger "console;verbosity=minimal"
```

预期：`TransferGroupDisplaySortTests` 全部通过。

---

### 任务 7：完整验证

**文件：**
- 修改：`_local_changes/018-transfer-group-display-sort/tests.md`

- [ ] **步骤 1：运行转移分组排序测试**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter FullyQualifiedName~TransferGroupDisplaySortTests --logger "console;verbosity=minimal"
```

预期：`TransferGroupDisplaySortTests` 全部通过。

- [ ] **步骤 2：运行相关回归测试**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~TransferGroupDisplaySortTests|FullyQualifiedName~FailoverHealthDisplayTests|FullyQualifiedName~MsgViewModelFailoverModeTests|FullyQualifiedName~FailoverModeConfigTests|FullyQualifiedName~FailoverGroupManagerTests" --logger "console;verbosity=minimal"
```

预期：相关测试全部通过。

- [ ] **步骤 3：构建 ServiceLib**

运行：

```powershell
dotnet build v2rayN\ServiceLib\ServiceLib.csproj --no-restore
```

预期：构建通过。若出现既有警告，只记录实际输出，不隐藏或绕过。

- [ ] **步骤 4：手工验证主界面行为**

手工验证以下状态：

- 普通订阅分组：点击备注、地址、延迟、速度、流量列，行为与改动前一致。
- 转移分组且 `Off`：点击标题后整个组合列表按普通展示规则排序，队列节点可以移动到候选节点下方。
- 活动转移分组且 `Failover`：队列节点固定在顶部并保持 `FailoverGroupItem.Sort` 顺序，候选节点按标题排序。
- 活动转移分组且 `LeastDelay`：队列节点固定在顶部并保持运行态顺序，候选节点按标题排序。
- 从 `Off` 切到开启模式：队列节点立即置顶。
- 从开启模式切回 `Off`：不恢复旧快照，也不主动套用旧标题排序；再次点击标题后，整个转移分组按普通展示规则排序。

---

### 任务 8：更新本地改动记录

**文件：**
- 新建或修改：`_local_changes/018-transfer-group-display-sort/README.md`
- 新建或修改：`_local_changes/018-transfer-group-display-sort/files.md`
- 新建或修改：`_local_changes/018-transfer-group-display-sort/reapply.md`
- 新建或修改：`_local_changes/018-transfer-group-display-sort/tests.md`
- 新建或修改：`_local_changes/018-transfer-group-display-sort/patch.diff`

- [ ] **步骤 1：确认改动记录目录存在**

运行：

```powershell
Test-Path _local_changes\018-transfer-group-display-sort
```

预期：输出 `True`。如果输出 `False`，从 `_local_changes/_template/` 复制或参照模板创建该目录，并补齐 5 个必需文件。

- [ ] **步骤 2：更新改动说明**

在 `README.md` 记录：

- 改动目的：转移分组标题排序只影响 UI 展示，不污染普通节点排序或真实转移队列顺序。
- 用户可见行为：关闭模式整表排序；开启模式队列段置顶且候选段排序；切回 `Off` 后暂停旧标题排序。
- 设计取舍：复用普通标题排序比较规则，但转移分组不调用 `ProfileExManager.Instance.SetSort`。
- 代理安全影响评估：本次改动不修改代理模式、Tun、系统代理、路由、DNS、订阅、证书、核心更新和本地监听端口；真实转移运行顺序仍由 `FailoverGroupManager` 和 `FailoverGroupItem.Sort` 控制。

- [ ] **步骤 3：更新文件清单和重新应用步骤**

在 `files.md` 列出本计划中所有源码、测试和文档记录文件；在 `reapply.md` 写明新版迁移时先尝试应用 `patch.diff`，冲突时按共享排序方法、`ProfilesViewModel` 分流、模式切换状态处理和测试文件逐项人工迁移。

- [ ] **步骤 4：记录验证结果**

把任务 7 的自动化验证和手工验证结果写入 `tests.md`。如果某些手工验证未执行，必须说明未验证风险。

- [ ] **步骤 5：生成功能补丁**

运行：

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes/018-transfer-group-display-sort/patch.diff
```

预期：`patch.diff` 包含本功能相关源码、测试和计划文档变更，不包含 `_local_changes/` 自身内容。

---

## 自检记录

- 设计覆盖：本计划覆盖普通分组保持原行为、转移分组关闭模式普通展示排序、开启模式队列置顶、候选段排序、模式切换不恢复快照、标题排序不修改真实队列优先级。
- 普通排序一致性：计划要求抽出共享列比较方法，避免转移分组复制一份近似但不一致的排序逻辑。
- 持久化边界：转移分组标题排序只写 ViewModel 内存态，不调用普通分组持久化排序入口，不写 `ProfileEx.Sort` 或 `FailoverGroupItem.Sort`。
- 本地记录边界：本计划要求新增并维护 `_local_changes/018-transfer-group-display-sort/` 改动记录，符合本仓库本地改动记录规则。
- 代理安全：计划只改 UI 展示排序和相关测试，不改核心配置构建、健康探测、路由、DNS、Tun、系统代理、订阅、证书或更新逻辑。

