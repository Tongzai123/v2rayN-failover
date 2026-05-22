# 主列表多选节点块拖拽实现计划

> **面向代理执行者：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 按任务执行本计划。步骤使用 checkbox（`- [ ]`）语法跟踪。

**目标：** 让 Avalonia Desktop 主界面节点列表支持多选节点作为连续块拖拽排序，非连续多选落位后合并为连续块并保持相对顺序。

**架构：** 先把排序和动画计算抽成可测试的纯逻辑，再让 ViewModel 暴露批量移动入口，最后让 `ProfilesView.axaml.cs` 在拖拽开始时捕获选中集合快照并使用批量入口提交。普通分组统一重写 `ProfileEx.Sort`；故障转移队列在队列区内批量重写 `FailoverGroupItem.Sort`，保留现有跨区保护。

**技术栈：** C#、.NET 8、Avalonia、ReactiveUI、xUnit、v2rayN `ServiceLib` / `v2rayN.Desktop`。

---

## 执行提交规则

本仓库要求任何源码、文档或项目结构实质改动都同步更新 `_local_changes/`。因此执行任务 1-6 时有两种合规方式：

- 推荐方式：先不执行任务 1-6 中的 `git commit` 命令，完成任务 7 的本地改动记录、补丁和验证后统一提交。
- 分段提交方式：每次提交业务代码时，同一个提交必须包含 `_local_changes/037-profile-multiselect-drag-block/` 和 `_local_changes/README.md` 的对应更新。

禁止提交只包含业务代码、但没有同步本地改动记录的中间状态。

## 文件结构

- 修改 `v2rayN/ServiceLib/Models/ProfileDragDropAnimation.cs`
  - 继续保留现有单行 `GetRowOffset`。
  - 新增多源块拖拽位移计算函数，供 Desktop UI 预览动画使用。

- 新增 `v2rayN/ServiceLib/Models/ProfileDragDropBlockMove.cs`
  - 放置纯排序算法：按当前展示顺序规范化源集合、移除源集合、计算目标插入位置、生成落位后的顺序。
  - 这个文件不依赖 UI、SQLite 或 ViewModel，便于单元测试。

- 修改 `v2rayN/ServiceLib/Handler/ConfigHandler.cs`
  - 新增普通节点批量排序持久化方法，例如 `MoveServers`。
  - 方法只接收当前列表和 `IndexId`，并统一写入 `ProfileExManager.SetSort`。

- 修改 `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
  - 新增故障转移队列批量移动方法，例如 `MoveQueueItems`。
  - 复用现有 `MoveQueueItem` 的排序语义，但支持多个源节点一次移动。

- 修改 `v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
  - 新增 `MoveServersTo(IReadOnlyList<string> sourceIndexIds, ProfileItemModel targetItem)`。
  - 单节点 `MoveServerTo(string?, ProfileItemModel)` 改为调用批量入口，减少重复路径。
  - 保留现有故障转移分组边界和刷新事件。

- 修改 `v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml.cs`
  - 新增拖拽源集合字段。
  - 拖拽开始时从 `SelectedProfiles` 生成源集合快照。
  - 多源拖拽时将所有源行设为透明占位，浮层按块高度移动，非源行按块高度避让。
  - 释放时调用 ViewModel 批量移动入口。

- 修改 `v2rayN/ServiceLib.Tests/ProfileDragDropSortTests.cs`
  - 覆盖普通块移动算法和块避让动画。

- 修改 `v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`
  - 覆盖故障转移队列批量移动。

- 修改 `_local_changes/037-profile-multiselect-drag-block/*`
  - 把状态从设计更新为实现中/已验证。
  - 更新 `files.md`、`tests.md`、`patch.diff`。

---

### 任务 1: 普通块排序纯逻辑

**文件：**
- 新增：`v2rayN/ServiceLib/Models/ProfileDragDropBlockMove.cs`
- 修改：`v2rayN/ServiceLib.Tests/ProfileDragDropSortTests.cs`

- [ ] **步骤 1: 写失败测试**

在 `v2rayN/ServiceLib.Tests/ProfileDragDropSortTests.cs` 追加以下测试，放在 `GetTopEdgeTargetIndex_PointerOutsideTopDropSlotOrInvalidInput_ReturnsNegativeOne` 后面：

```csharp
[Fact]
public void MoveBlockToTarget_NonContiguousSourcesDown_CollapsesIntoContiguousBlock()
{
    var result = ProfileDragDropBlockMove.MoveToTarget(
        ["A", "B", "C", "D", "E", "F", "G"],
        ["B", "D", "E"],
        "G");

    Assert.Equal(["A", "C", "F", "B", "D", "E", "G"], result);
}

[Fact]
public void MoveBlockToTarget_NonContiguousSourcesUp_CollapsesIntoContiguousBlock()
{
    var result = ProfileDragDropBlockMove.MoveToTarget(
        ["A", "B", "C", "D", "E", "F", "G"],
        ["C", "F"],
        "A");

    Assert.Equal(["C", "F", "A", "B", "D", "E", "G"], result);
}

[Fact]
public void MoveBlockToTarget_TargetInsideSourceSpan_ReturnsOriginalOrder()
{
    var result = ProfileDragDropBlockMove.MoveToTarget(
        ["A", "B", "C", "D", "E", "F"],
        ["B", "D"],
        "C");

    Assert.Equal(["A", "B", "C", "D", "E", "F"], result);
}

[Fact]
public void MoveBlockToTarget_TargetIsSource_ReturnsOriginalOrder()
{
    var result = ProfileDragDropBlockMove.MoveToTarget(
        ["A", "B", "C", "D", "E"],
        ["B", "D"],
        "D");

    Assert.Equal(["A", "B", "C", "D", "E"], result);
}

[Fact]
public void MoveBlockToTarget_UnknownSource_ReturnsOriginalOrder()
{
    var result = ProfileDragDropBlockMove.MoveToTarget(
        ["A", "B", "C"],
        ["missing"],
        "C");

    Assert.Equal(["A", "B", "C"], result);
}

[Theory]
[InlineData(null, "A")]
[InlineData("A", null)]
public void MoveBlockToTarget_InvalidInput_ReturnsOriginalOrder(string? sourceId, string? targetId)
{
    var result = ProfileDragDropBlockMove.MoveToTarget(
        ["A", "B", "C"],
        sourceId == null ? [] : [sourceId],
        targetId);

    Assert.Equal(["A", "B", "C"], result);
}
```

- [ ] **步骤 2: 运行测试确认失败**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~ProfileDragDropSortTests"
```

预期：编译失败，错误包含 `ProfileDragDropBlockMove` 不存在。

- [ ] **步骤 3: 新增最小实现**

创建 `v2rayN/ServiceLib/Models/ProfileDragDropBlockMove.cs`：

```csharp
namespace ServiceLib.Models;

public static class ProfileDragDropBlockMove
{
    public static List<string> MoveToTarget(
        IReadOnlyList<string>? orderedIds,
        IEnumerable<string?>? sourceIds,
        string? targetId)
    {
        var ordered = orderedIds?.Where(id => id.IsNotEmpty()).ToList() ?? [];
        if (ordered.Count <= 1 || targetId.IsNullOrEmpty() || !ordered.Contains(targetId))
        {
            return ordered;
        }

        var sourceSet = (sourceIds ?? [])
            .Where(id => id.IsNotEmpty())
            .Distinct()
            .ToHashSet();
        if (sourceSet.Count == 0)
        {
            return ordered;
        }

        var sourceIndexes = ordered
            .Select((id, index) => new { id, index })
            .Where(item => sourceSet.Contains(item.id))
            .Select(item => item.index)
            .ToList();
        if (sourceIndexes.Count != sourceSet.Count)
        {
            return ordered;
        }

        var targetOriginalIndex = ordered.IndexOf(targetId);
        if (targetOriginalIndex >= sourceIndexes[0] && targetOriginalIndex <= sourceIndexes[^1])
        {
            return ordered;
        }

        var block = sourceIndexes.Select(index => ordered[index]).ToList();
        var remaining = ordered.Where(id => !sourceSet.Contains(id)).ToList();
        var targetIndex = remaining.IndexOf(targetId);
        if (targetIndex < 0)
        {
            return ordered;
        }

        remaining.InsertRange(targetIndex, block);
        return remaining;
    }
}
```

- [ ] **步骤 4: 运行测试确认通过**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~ProfileDragDropSortTests"
```

预期：`ProfileDragDropSortTests` 全部通过。

- [ ] **步骤 5: 提交**

```powershell
git add v2rayN\ServiceLib\Models\ProfileDragDropBlockMove.cs v2rayN\ServiceLib.Tests\ProfileDragDropSortTests.cs
git commit -m "test: 覆盖多选节点块排序"
```

---

### 任务 2: 块避让动画纯逻辑

**文件：**
- 修改：`v2rayN/ServiceLib/Models/ProfileDragDropAnimation.cs`
- 修改：`v2rayN/ServiceLib.Tests/ProfileDragDropSortTests.cs`

- [ ] **步骤 1: 写失败测试**

在 `ProfileDragDropSortTests` 追加：

```csharp
[Fact]
public void GetBlockRowOffset_SourceBlockAboveTarget_ShiftsRowsUpByBlockHeight()
{
    var rowHeight = 28d;

    Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(0, [1, 3, 4], 6, rowHeight));
    Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(1, [1, 3, 4], 6, rowHeight));
    Assert.Equal(-rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(2, [1, 3, 4], 6, rowHeight));
    Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(3, [1, 3, 4], 6, rowHeight));
    Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(4, [1, 3, 4], 6, rowHeight));
    Assert.Equal(-3 * rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(5, [1, 3, 4], 6, rowHeight));
    Assert.Equal(-3 * rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(6, [1, 3, 4], 6, rowHeight));
}

[Fact]
public void GetBlockRowOffset_SourceBlockBelowTarget_ShiftsRowsDownByBlockHeight()
{
    var rowHeight = 28d;

    Assert.Equal(2 * rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(0, [2, 5], 0, rowHeight));
    Assert.Equal(2 * rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(1, [2, 5], 0, rowHeight));
    Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(2, [2, 5], 0, rowHeight));
    Assert.Equal(rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(3, [2, 5], 0, rowHeight));
    Assert.Equal(rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(4, [2, 5], 0, rowHeight));
    Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(5, [2, 5], 0, rowHeight));
}

[Theory]
[InlineData(-1, 1, 3, 28)]
[InlineData(2, -1, 3, 28)]
[InlineData(2, 1, -1, 28)]
[InlineData(2, 1, 3, 0)]
public void GetBlockRowOffset_InvalidInput_ReturnsZero(int rowIndex, int sourceIndex, int targetIndex, double rowHeight)
{
    Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(rowIndex, [sourceIndex], targetIndex, rowHeight));
}

[Fact]
public void GetBlockRowOffset_TargetInsideSourceSpan_ReturnsZero()
{
    var rowHeight = 28d;

    Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(0, [1, 3], 2, rowHeight));
    Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(1, [1, 3], 2, rowHeight));
    Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(2, [1, 3], 2, rowHeight));
    Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(3, [1, 3], 2, rowHeight));
    Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(4, [1, 3], 2, rowHeight));
}
```

- [ ] **步骤 2: 运行测试确认失败**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~ProfileDragDropSortTests"
```

预期：编译失败，错误包含 `GetBlockRowOffset` 不存在。

- [ ] **步骤 3: 实现块位移计算**

在 `ProfileDragDropAnimation` 中 `GetRowOffset` 后追加：

```csharp
public static double GetBlockRowOffset(int rowIndex, IReadOnlyCollection<int>? sourceIndexes, int targetIndex, double rowHeight)
{
    if (rowIndex < 0 || targetIndex < 0 || rowHeight <= 0 || sourceIndexes == null || sourceIndexes.Count == 0)
    {
        return 0;
    }

    var sources = sourceIndexes.Where(index => index >= 0).Distinct().OrderBy(index => index).ToList();
    if (sources.Count == 0 || sources.Contains(rowIndex))
    {
        return 0;
    }

    var firstSource = sources[0];
    var lastSource = sources[^1];
    if (targetIndex > lastSource)
    {
        var precedingSources = sources.Count(index => index < rowIndex);
        return rowIndex > firstSource && rowIndex <= targetIndex
            ? -precedingSources * rowHeight
            : 0;
    }

    if (targetIndex < firstSource)
    {
        var followingSources = sources.Count(index => index > rowIndex);
        return rowIndex >= targetIndex && rowIndex < lastSource
            ? followingSources * rowHeight
            : 0;
    }

    return 0;
}
```

- [ ] **步骤 4: 运行测试确认通过**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~ProfileDragDropSortTests"
```

预期：`ProfileDragDropSortTests` 全部通过。

- [ ] **步骤 5: 提交**

```powershell
git add v2rayN\ServiceLib\Models\ProfileDragDropAnimation.cs v2rayN\ServiceLib.Tests\ProfileDragDropSortTests.cs
git commit -m "feat: 增加多选拖拽块动画计算"
```

---

### 任务 3: 普通分组批量排序持久化

**文件：**
- 修改：`v2rayN/ServiceLib/Handler/ConfigHandler.cs`
- 修改：`v2rayN/ServiceLib.Tests/ProfileDragDropSortTests.cs`

- [ ] **步骤 1: 写失败测试**

在 `ProfileDragDropSortTests` 追加：

```csharp
[Fact]
public async Task MoveServers_NonContiguousSources_RewritesSortAsContiguousBlock()
{
    var profiles = new List<ProfileItem>
    {
        new() { IndexId = "A" },
        new() { IndexId = "B" },
        new() { IndexId = "C" },
        new() { IndexId = "D" },
        new() { IndexId = "E" },
        new() { IndexId = "F" },
        new() { IndexId = "G" },
    };

    var result = await ConfigHandler.MoveServers(new Config(), profiles, ["B", "D", "E"], "G");

    Assert.Equal(0, result);
    var ordered = profiles
        .OrderBy(item => ProfileExManager.Instance.GetSort(item.IndexId))
        .Select(item => item.IndexId)
        .ToArray();
    Assert.Equal(["A", "C", "F", "B", "D", "E", "G"], ordered);
}

[Fact]
public async Task MoveServers_UnknownSource_ReturnsFailureAndKeepsSort()
{
    var profiles = new List<ProfileItem>
    {
        new() { IndexId = "A" },
        new() { IndexId = "B" },
        new() { IndexId = "C" },
    };

    var result = await ConfigHandler.MoveServers(new Config(), profiles, ["missing"], "C");

    Assert.Equal(-1, result);
}

[Fact]
public async Task MoveServers_TargetInsideSourceSpan_DoesNotRewriteSort()
{
    var profiles = new List<ProfileItem>
    {
        new() { IndexId = "span-A" },
        new() { IndexId = "span-B" },
        new() { IndexId = "span-C" },
        new() { IndexId = "span-D" },
        new() { IndexId = "span-E" },
    };

    var result = await ConfigHandler.MoveServers(new Config(), profiles, ["span-B", "span-D"], "span-C");

    Assert.Equal(0, result);
    Assert.All(profiles, item => Assert.Equal(0, ProfileExManager.Instance.GetSort(item.IndexId)));
}
```

确保文件顶部已经有：

```csharp
using ServiceLib.Handler;
using ServiceLib.Manager;
```

- [ ] **步骤 2: 运行测试确认失败**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~ProfileDragDropSortTests"
```

预期：编译失败，错误包含 `ConfigHandler.MoveServers` 不存在。

- [ ] **步骤 3: 实现批量排序**

在 `ConfigHandler.MoveServer` 后追加：

```csharp
public static async Task<int> MoveServers(
    Config config,
    List<ProfileItem> lstProfile,
    IReadOnlyList<string> sourceIndexIds,
    string? targetIndexId)
{
    var sourceIds = sourceIndexIds
        .Where(id => id.IsNotEmpty())
        .Distinct()
        .ToList();
    if (lstProfile.Count <= 1 || sourceIds.Count == 0 || targetIndexId.IsNullOrEmpty())
    {
        return -1;
    }

    var orderedIds = lstProfile.Select(item => item.IndexId).ToList();
    if (!orderedIds.Contains(targetIndexId) || !sourceIds.All(orderedIds.Contains))
    {
        return -1;
    }

    var movedIds = ProfileDragDropBlockMove.MoveToTarget(orderedIds, sourceIds, targetIndexId);
    if (movedIds.SequenceEqual(orderedIds))
    {
        return 0;
    }

    var movedSet = movedIds.ToHashSet();
    if (movedSet.Count != orderedIds.Count || !orderedIds.All(movedSet.Contains))
    {
        return -1;
    }

    for (var i = 0; i < movedIds.Count; i++)
    {
        ProfileExManager.Instance.SetSort(movedIds[i], (i + 1) * 10);
    }

    return await Task.FromResult(0);
}
```

在 `ConfigHandler.cs` 顶部确认已有 `using ServiceLib.Models;`；如果没有，添加：

```csharp
using ServiceLib.Models;
```

- [ ] **步骤 4: 运行测试确认通过**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~ProfileDragDropSortTests"
```

预期：`ProfileDragDropSortTests` 全部通过。

- [ ] **步骤 5: 提交**

```powershell
git add v2rayN\ServiceLib\Handler\ConfigHandler.cs v2rayN\ServiceLib.Tests\ProfileDragDropSortTests.cs
git commit -m "feat: 支持普通节点批量拖拽排序"
```

---

### 任务 4: 故障转移队列批量排序

**文件：**
- 修改：`v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- 修改：`v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs`

- [ ] **步骤 1: 写失败测试**

在 `FailoverGroupManagerTests` 中追加：

```csharp
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
            .Select(item => item.SourceProfileId)
            .ToArray();
        Assert.Equal([ids[0], ids[2], ids[1], ids[3], ids[4]], ordered);
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
```

- [ ] **步骤 2: 运行测试确认失败**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverGroupManagerTests"
```

预期：编译失败，错误包含 `MoveQueueItems` 不存在。

- [ ] **步骤 3: 实现队列批量移动**

在 `FailoverGroupManager.MoveQueueItem` 后追加：

```csharp
public static async Task<int> MoveQueueItems(string groupId, IReadOnlyList<string> sourceProfileIds, string? targetSourceProfileId)
{
    var sourceIds = sourceProfileIds
        .Where(id => id.IsNotEmpty())
        .Distinct()
        .ToList();
    if (groupId.IsNullOrEmpty() || sourceIds.Count == 0 || targetSourceProfileId.IsNullOrEmpty())
    {
        return -1;
    }

    var items = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
        .Where(item => item.GroupId == groupId && item.Enabled)
        .ToListAsync();
    items = items.OrderBy(item => item.Sort).ThenBy(item => item.SourceProfileId).ToList();

    var orderedIds = items.Select(item => item.SourceProfileId).ToList();
    if (!sourceIds.All(id => orderedIds.Contains(id)) || !orderedIds.Contains(targetSourceProfileId))
    {
        return -1;
    }

    var movedIds = ProfileDragDropBlockMove.MoveToTarget(orderedIds, sourceIds, targetSourceProfileId);
    if (movedIds.SequenceEqual(orderedIds))
    {
        return 0;
    }

    var itemMap = items.ToDictionary(item => item.SourceProfileId);
    for (var i = 0; i < movedIds.Count; i++)
    {
        itemMap[movedIds[i]].Sort = i + 1;
    }

    await SQLiteHelper.Instance.UpdateAllAsync(items);
    return 0;
}
```

在文件顶部确认已有：

```csharp
using ServiceLib.Models;
```

- [ ] **步骤 4: 运行测试确认通过**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverGroupManagerTests"
```

预期：`FailoverGroupManagerTests` 全部通过。

- [ ] **步骤 5: 提交**

```powershell
git add v2rayN\ServiceLib\Manager\FailoverGroupManager.cs v2rayN\ServiceLib.Tests\FailoverGroupManagerTests.cs
git commit -m "feat: 支持故障队列批量拖拽排序"
```

---

### 任务 5: ViewModel 批量移动入口

**文件：**
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
- 修改：`v2rayN/ServiceLib.Tests/ProfileDragDropSortTests.cs`

- [ ] **步骤 1: 写边界纯函数测试**

在 `ProfileDragDropSortTests` 追加：

```csharp
[Theory]
[InlineData(true, true, true)]
[InlineData(true, false, false)]
[InlineData(false, true, false)]
[InlineData(false, false, true)]
public void CanMoveTransferGroupItems_RequiresAllSourcesAndTargetInSamePinnedArea(
    bool sourceInQueue,
    bool targetInQueue,
    bool expected)
{
    var result = ProfilesViewModel.CanMoveTransferGroupItems(
        true,
        [sourceInQueue, sourceInQueue],
        targetInQueue);

    Assert.Equal(expected, result);
}

[Fact]
public void CanMoveTransferGroupItems_RejectsMixedSourcesWhenQueuePinned()
{
    var result = ProfilesViewModel.CanMoveTransferGroupItems(true, [true, false], true);

    Assert.False(result);
}

[Fact]
public void CanMoveTransferGroupItems_AllowsMixedSourcesWhenQueueNotPinned()
{
    var result = ProfilesViewModel.CanMoveTransferGroupItems(false, [true, false], false);

    Assert.True(result);
}
```

- [ ] **步骤 2: 运行测试确认失败**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~ProfileDragDropSortTests"
```

预期：编译失败，错误包含 `CanMoveTransferGroupItems` 不存在。

- [ ] **步骤 3: 增加 ViewModel 纯边界函数**

在 `CanMoveTransferGroupItem` 后追加：

```csharp
public static bool CanMoveTransferGroupItems(bool pinFailoverQueue, IReadOnlyCollection<bool> sourceInQueueStates, bool targetInQueue)
{
    if (!pinFailoverQueue)
    {
        return true;
    }

    if (sourceInQueueStates.Count == 0 || sourceInQueueStates.Distinct().Count() != 1)
    {
        return false;
    }

    return sourceInQueueStates.First() == targetInQueue;
}
```

- [ ] **步骤 4: 新增批量移动入口**

把 `MoveServerTo(string? sourceIndexId, ProfileItemModel targetItem)` 保留为兼容入口，并在其后追加批量入口：

```csharp
public async Task MoveServersTo(IReadOnlyList<string> sourceIndexIds, ProfileItemModel targetItem)
{
    var sourceIds = sourceIndexIds
        .Where(id => id.IsNotEmpty())
        .Distinct()
        .ToList();
    if (sourceIds.Count == 0 || targetItem == null)
    {
        return;
    }

    if (sourceIds.Count == 1)
    {
        await MoveServerTo(sourceIds[0], targetItem);
        return;
    }

    var displayIds = ProfileItems.Select(item => item.IndexId).ToList();
    if (ProfileDragDropBlockMove.MoveToTarget(displayIds, sourceIds, targetItem.IndexId).SequenceEqual(displayIds))
    {
        return;
    }

    if (CurrentGroupIsFailoverGroup)
    {
        var sourceItems = ProfileItems.Where(item => sourceIds.Contains(item.IndexId)).ToList();
        if (sourceItems.Count != sourceIds.Count)
        {
            return;
        }

        var pinFailoverQueue = ShouldPinTransferGroupQueue(
            _config.ActiveFailoverGroupId == _config.SubIndexId,
            _config.FailoverMode);
        if (!CanMoveTransferGroupItems(pinFailoverQueue, sourceItems.Select(item => item.IsInFailoverQueue).ToList(), targetItem.IsInFailoverQueue))
        {
            return;
        }

        if (pinFailoverQueue && targetItem.IsInFailoverQueue)
        {
            if (await FailoverGroupManager.MoveQueueItems(_config.SubIndexId, sourceIds, targetItem.IndexId) == 0)
            {
                await PromoteFailoverQueueFirstAfterDrag();
                await RefreshServers();
                AppEvents.FailoverStateChangedRequested.Publish();
            }
            return;
        }
    }

    if (await ConfigHandler.MoveServers(_config, _lstProfile, sourceIds, targetItem.IndexId) == 0)
    {
        await RefreshServers();
    }
}
```

然后把单节点入口改成调用批量入口时要避免递归。推荐保持现有单节点 `MoveServerTo` 代码不动，批量入口在 `sourceIds.Count == 1` 时调用它。

- [ ] **步骤 5: 运行测试确认通过**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~ProfileDragDropSortTests"
```

预期：`ProfileDragDropSortTests` 全部通过。

- [ ] **步骤 6: 提交**

```powershell
git add v2rayN\ServiceLib\ViewModels\ProfilesViewModel.cs v2rayN\ServiceLib.Tests\ProfileDragDropSortTests.cs
git commit -m "feat: 增加节点批量拖拽移动入口"
```

---

### 任务 6: Desktop UI 接入多选拖拽

**文件：**
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml.cs`

- [ ] **步骤 1: 增加拖拽源集合字段**

在 `_dragSourceIndexId` 后追加：

```csharp
private List<string> _dragSourceIndexIds = [];
private List<int> _dragSourceIndexes = [];
```

- [ ] **步骤 2: 增加源集合快照函数**

在 `BeginRowDrag` 前追加：

```csharp
private void PrepareDragSourceSnapshot()
{
    _dragSourceIndexIds.Clear();
    _dragSourceIndexes.Clear();

    if (_dragSourceItem == null || ViewModel?.ProfileItems == null)
    {
        return;
    }

    var selectedIds = (ViewModel.SelectedProfiles ?? [])
        .Where(item => item?.IndexId.IsNotEmpty() == true)
        .Select(item => item.IndexId)
        .Distinct()
        .ToHashSet();

    if (selectedIds.Count <= 1 || !selectedIds.Contains(_dragSourceItem.IndexId))
    {
        selectedIds.Clear();
        selectedIds.Add(_dragSourceItem.IndexId);
    }

    for (var i = 0; i < ViewModel.ProfileItems.Count; i++)
    {
        var item = ViewModel.ProfileItems[i];
        if (!selectedIds.Contains(item.IndexId))
        {
            continue;
        }

        _dragSourceIndexIds.Add(item.IndexId);
        _dragSourceIndexes.Add(i);
    }
}
```

- [ ] **步骤 3: 在开始拖拽时使用快照并设置所有源行透明**

修改 `BeginRowDrag`：

```csharp
private void BeginRowDrag(PointerEventArgs e)
{
    if (_dragSourceItem == null || _dragSourceRow == null)
    {
        return;
    }

    PrepareDragSourceSnapshot();
    if (_dragSourceIndexIds.Count == 0)
    {
        return;
    }

    _isRowDragActive = true;
    e.Pointer.Capture(lstProfiles);
    foreach (var row in lstProfiles.GetVisualDescendants().OfType<DataGridRow>())
    {
        if (row.DataContext is ProfileItemModel item && _dragSourceIndexIds.Contains(item.IndexId))
        {
            SetSourceRowOpacity(row, DragSourceOpacity);
        }
    }
    _dragOverlay = CreateDragOverlay(_dragSourceItem, _dragSourceRow);
    profileDragOverlayLayer.Children.Add(_dragOverlay);
    UpdateDragOverlay(e.GetPosition(lstProfiles));
}
```

- [ ] **步骤 4: 释放时调用批量入口**

修改 `LstProfiles_PointerReleased` 中的提交逻辑：

```csharp
var sourceIds = _dragSourceIndexIds.Count > 0
    ? _dragSourceIndexIds.ToList()
    : _dragSourceIndexId.IsNotEmpty()
        ? new List<string> { _dragSourceIndexId }
        : [];
if (targetItem != null
    && _dragTargetIndex >= 0
    && sourceIds.Count > 0
    && !sourceIds.Contains(targetItem.IndexId))
{
    await ViewModel?.MoveServersTo(sourceIds, targetItem);
}
```

- [ ] **步骤 5: 使用块动画偏移**

修改 `UpdateShiftedRows` 中 offset 计算：

```csharp
var offset = _dragSourceIndexes.Count > 1
    ? ProfileDragDropAnimation.GetBlockRowOffset(rowIndex, _dragSourceIndexes, _dragTargetIndex, _dragRowHeight)
    : ProfileDragDropAnimation.GetRowOffset(rowIndex, _dragStartIndex, _dragTargetIndex, _dragRowHeight);
```

- [ ] **步骤 6: 调整浮层高度和移动范围**

修改 `UpdateDragOverlay`：

```csharp
var overlayHeight = Math.Max(_dragOverlay.Bounds.Height, _dragRowHeight);
var top = Math.Clamp(position.Y - overlayHeight / 2, 0, Math.Max(0, lstProfiles.Bounds.Height - overlayHeight));
transform.X = 0;
transform.Y = top;
```

如果暂不实现多行浮层，保留单行浮层也可以，但必须保证多源行透明占位和排序结果正确。若实现多行浮层，把 `CreateDragOverlay` 改为接收源集合并按最多 5 行渲染，超出时在最后一行显示数量提示。

- [ ] **步骤 7: 重置拖拽状态时清理集合**

在 `ResetDragState` 末尾追加：

```csharp
if (!keepSource)
{
    _dragSourceIndexIds.Clear();
    _dragSourceIndexes.Clear();
}
```

- [ ] **步骤 8: 编译 Desktop 项目**

运行：

```powershell
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj
```

预期：build 成功。允许出现仓库既有资源重复名或 RID warning，不允许新增 error。

- [ ] **步骤 9: 提交**

```powershell
git add v2rayN\v2rayN.Desktop\Views\ProfilesView.axaml.cs
git commit -m "feat: 接入主列表多选块拖拽"
```

---

### 任务 7: 回归验证与本地改动记录

**文件：**
- 修改：`_local_changes/037-profile-multiselect-drag-block/README.md`
- 修改：`_local_changes/037-profile-multiselect-drag-block/files.md`
- 修改：`_local_changes/037-profile-multiselect-drag-block/reapply.md`
- 修改：`_local_changes/037-profile-multiselect-drag-block/tests.md`
- 修改：`_local_changes/037-profile-multiselect-drag-block/patch.diff`
- 修改：`_local_changes/README.md`

- [ ] **步骤 1: 运行完整测试**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj
```

预期：全部测试通过，失败数为 0。

- [ ] **步骤 2: 编译 Desktop**

运行：

```powershell
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj
```

预期：build 成功，失败数为 0。

- [ ] **步骤 3: 更新 README 状态**

把 `_local_changes/037-profile-multiselect-drag-block/README.md` 中：

```markdown
- 改动类型：设计 / 功能
- 状态：设计中
```

改为：

```markdown
- 改动类型：功能
- 状态：已验证自动化与编译
```

并在“用户可见行为”保留已经确认的连续块规则。

- [ ] **步骤 4: 更新 files.md**

在 `_local_changes/037-profile-multiselect-drag-block/files.md` 的“修改文件”中加入以下行：

```markdown
| `v2rayN/ServiceLib/Models/ProfileDragDropAnimation.cs` | 新增多源块拖拽行位移计算。 | 支持多选拖拽时可见行按块高度避让。 |
| `v2rayN/ServiceLib/Handler/ConfigHandler.cs` | 新增普通节点批量排序持久化方法。 | 避免循环单节点移动导致索引漂移。 |
| `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs` | 新增故障转移队列批量排序方法。 | 支持队列区内多选块移动。 |
| `v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs` | 新增批量拖拽移动入口和故障转移边界校验。 | 连接 UI 源集合与底层排序持久化。 |
| `v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml.cs` | 拖拽开始时捕获多选源集合，释放时提交批量移动。 | 实现主列表多选节点块拖拽。 |
| `v2rayN/ServiceLib.Tests/ProfileDragDropSortTests.cs` | 增加普通块排序、块动画、故障转移边界测试。 | 锁定核心排序规则。 |
| `v2rayN/ServiceLib.Tests/FailoverGroupManagerTests.cs` | 增加故障转移队列批量移动测试。 | 锁定队列排序持久化。 |
```

- [ ] **步骤 5: 更新 tests.md**

把实际命令和结果写入 `_local_changes/037-profile-multiselect-drag-block/tests.md`。自动化验证表至少包含：

```markdown
| `dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj` | 通过 | 记录通过数和 warning。 |
| `dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj` | 通过 | 记录 warning 和失败数。 |
```

- [ ] **步骤 6: 更新总览状态**

把 `_local_changes/README.md` 中 `035` 行的状态从：

```markdown
设计中
```

改为：

```markdown
已验证自动化与编译
```

- [ ] **步骤 7: 重新生成补丁**

运行：

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes\037-profile-multiselect-drag-block\patch.diff
```

如果执行后 `git diff --check` 因 `patch.diff` 中的补丁上下文空白行报 trailing whitespace，则改用：

```powershell
git diff -U0 -- . ":(exclude)_local_changes" > _local_changes\037-profile-multiselect-drag-block\patch.diff
```

使用 `-U0` 生成的补丁未来检查时需要加 `--unidiff-zero`：

```powershell
git apply --check --unidiff-zero _local_changes\037-profile-multiselect-drag-block\patch.diff
```

预期：`patch.diff` 非空，包含设计文档和业务代码改动，不包含 `_local_changes` 自身内容。

- [ ] **步骤 8: 检查补丁和文档**

运行：

```powershell
git diff --check
rg -n "TB[D]|TO[D]O|待[定]|填[写]|path/[t]o|YYY[Y]" docs\superpowers\plans\2026-05-18-profile-multiselect-drag-block.md docs\superpowers\specs\2026-05-18-profile-multiselect-drag-block-design.md _local_changes\README.md _local_changes\037-profile-multiselect-drag-block\README.md _local_changes\037-profile-multiselect-drag-block\files.md _local_changes\037-profile-multiselect-drag-block\reapply.md
```

预期：`git diff --check` 无 error；`rg` 无命中。

- [ ] **步骤 9: 提交记录**

```powershell
git add _local_changes\README.md _local_changes\037-profile-multiselect-drag-block
git commit -m "docs: 更新多选块拖拽本地改动记录"
```

---

## 最终验收清单

- [ ] `dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj` 通过，失败数为 0。
- [ ] `dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj` 通过，失败数为 0。
- [ ] 普通分组多选非连续节点后，拖拽结果合并为连续块。
- [ ] 批量移动保持源节点相对顺序。
- [ ] 从未选中节点开始拖拽仍只移动该节点。
- [ ] 故障转移队列置顶时，批量源集合不能混合队列和非队列节点。
- [ ] `_local_changes/037-profile-multiselect-drag-block/patch.diff` 非空且不包含 `_local_changes` 自身内容。
- [ ] `_local_changes/037-profile-multiselect-drag-block/tests.md` 记录实际验证结果。
