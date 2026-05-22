# 订阅分组拖拽排序实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**目标：** 在 `v2rayN.Desktop` 主界面顶部“订阅分组”栏支持鼠标左右拖拽调整分组顺序，并在拖动过程中显示分组名称浮层和位移动画反馈。

**架构：** 排序数据继续使用已有 `SubItem.Sort`，新增可测试的订阅分组重排逻辑，再由 `ProfilesViewModel` 暴露 `MoveSubTo(sourceId, targetId)` 给 Desktop 视图调用。Desktop 端复用现有 `ProfilesView` 的指针捕获思路，但为 `lstGroup` 单独实现横向/跨行拖拽、浮动名称预览和目标项过渡效果，不改变节点列表拖放逻辑。

**技术栈：** `.NET 8`、Avalonia、ReactiveUI、SQLite、xUnit、`ServiceLib.Tests`、项目本地 `_local_changes` 记录规范。

---

## 范围与文件结构

- 修改：`v2rayN/ServiceLib/Handler/ConfigHandler.cs`
  - 新增订阅分组重排纯函数和持久化入口，负责更新真实 `SubItem.Sort`。
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
  - 新增 `MoveSubTo(string? sourceId, SubItem? targetItem)`，刷新 `SubItems` 并保持当前选中分组。
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`
  - 给分组栏增加拖拽浮层和目标项样式类需要的容器。
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml.cs`
  - 为 `lstGroup` 绑定指针事件、记录拖拽源、更新浮动名称位置、释放时调用 ViewModel 排序。
- 新增：`v2rayN/ServiceLib.Tests/SubscriptionGroupDragSortTests.cs`
  - 测试纯重排逻辑、虚拟“全部”分组保护、拖到自身不改动。
- 新增：`_local_changes/025-subscription-group-drag-sort/README.md`
- 新增：`_local_changes/025-subscription-group-drag-sort/files.md`
- 新增：`_local_changes/025-subscription-group-drag-sort/reapply.md`
- 新增：`_local_changes/025-subscription-group-drag-sort/tests.md`
- 新增：`_local_changes/025-subscription-group-drag-sort/patch.diff`

## 基线验证

- 已在新 worktree 执行：`dotnet restore v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj`
  - 结果：成功还原 `ServiceLib` 和 `ServiceLib.Tests`。
- 已在新 worktree 执行：`dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --no-restore`
  - 结果：通过，`151` 个测试通过，`0` 失败，`0` 跳过。
  - 现有警告：`ResUI.fr.resx` 重复资源名 `TbSettingsSendThroughTip`；`SQLitePCLRaw.lib.e_sqlite3` RID 使用警告。两者为基线已有警告。

---

### 任务 1：为订阅分组重排写失败测试

**文件：**
- 新增：`v2rayN/ServiceLib.Tests/SubscriptionGroupDragSortTests.cs`
- 修改：无

- [ ] **步骤 1：新增测试文件**

写入以下测试，先只引用计划中的目标 API，让测试在实现前失败：

```csharp
using ServiceLib.Handler;
using ServiceLib.Models;
using Xunit;

namespace ServiceLib.Tests;

public class SubscriptionGroupDragSortTests
{
    [Fact]
    public void ReorderSubItemsForMove_MovesSourceBeforeTargetAndNormalizesSort()
    {
        var items = new List<SubItem>
        {
            new() { Id = "a", Remarks = "A", Sort = 10 },
            new() { Id = "b", Remarks = "B", Sort = 20 },
            new() { Id = "c", Remarks = "C", Sort = 30 },
        };

        var result = ConfigHandler.ReorderSubItemsForMove(items, "c", "a");

        Assert.Collection(result,
            item =>
            {
                Assert.Equal("c", item.Id);
                Assert.Equal(1, item.Sort);
            },
            item =>
            {
                Assert.Equal("a", item.Id);
                Assert.Equal(2, item.Sort);
            },
            item =>
            {
                Assert.Equal("b", item.Id);
                Assert.Equal(3, item.Sort);
            });
    }

    [Fact]
    public void ReorderSubItemsForMove_IgnoresVirtualAllGroupWithoutId()
    {
        var items = new List<SubItem>
        {
            new() { Remarks = "All", Sort = 0 },
            new() { Id = "a", Remarks = "A", Sort = 1 },
            new() { Id = "b", Remarks = "B", Sort = 2 },
        };

        var result = ConfigHandler.ReorderSubItemsForMove(items, "", "a");

        Assert.Collection(result,
            item => Assert.Equal("a", item.Id),
            item => Assert.Equal("b", item.Id));
    }

    [Fact]
    public void ReorderSubItemsForMove_ReturnsOriginalOrderWhenSourceEqualsTarget()
    {
        var items = new List<SubItem>
        {
            new() { Id = "a", Remarks = "A", Sort = 1 },
            new() { Id = "b", Remarks = "B", Sort = 2 },
        };

        var result = ConfigHandler.ReorderSubItemsForMove(items, "a", "a");

        Assert.Equal(["a", "b"], result.Select(item => item.Id).ToArray());
        Assert.Equal([1, 2], result.Select(item => item.Sort).ToArray());
    }
}
```

- [ ] **步骤 2：运行测试并确认失败**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --no-restore --filter SubscriptionGroupDragSortTests
```

预期：编译失败，错误指出 `ConfigHandler` 不包含 `ReorderSubItemsForMove`。

- [ ] **步骤 3：提交失败测试**

```powershell
git add v2rayN\ServiceLib.Tests\SubscriptionGroupDragSortTests.cs
git commit -m "test: add subscription group drag sort coverage"
```

---

### 任务 2：实现订阅分组重排和持久化入口

**文件：**
- 修改：`v2rayN/ServiceLib/Handler/ConfigHandler.cs`
- 测试：`v2rayN/ServiceLib.Tests/SubscriptionGroupDragSortTests.cs`

- [ ] **步骤 1：在 `ConfigHandler` 的订阅区域新增纯重排方法**

放在 `AddSubItem(Config config, SubItem subItem)` 附近，保持订阅相关逻辑集中：

```csharp
public static List<SubItem> ReorderSubItemsForMove(IEnumerable<SubItem>? sourceItems, string? sourceId, string? targetId)
{
    var items = (sourceItems ?? [])
        .Where(item => item.Id.IsNotEmpty())
        .OrderBy(item => item.Sort)
        .ToList();

    if (sourceId.IsNullOrEmpty() || targetId.IsNullOrEmpty() || sourceId == targetId)
    {
        NormalizeSubItemSort(items);
        return items;
    }

    var source = items.FirstOrDefault(item => item.Id == sourceId);
    var target = items.FirstOrDefault(item => item.Id == targetId);
    if (source == null || target == null)
    {
        NormalizeSubItemSort(items);
        return items;
    }

    items.Remove(source);
    var targetIndex = items.FindIndex(item => item.Id == target.Id);
    if (targetIndex < 0)
    {
        items.Add(source);
    }
    else
    {
        items.Insert(targetIndex, source);
    }

    NormalizeSubItemSort(items);
    return items;
}

private static void NormalizeSubItemSort(IReadOnlyList<SubItem> items)
{
    for (var index = 0; index < items.Count; index++)
    {
        items[index].Sort = index + 1;
    }
}
```

- [ ] **步骤 2：新增持久化入口**

紧接纯函数后添加：

```csharp
public static async Task<int> MoveSubItemTo(string? sourceId, string? targetId)
{
    if (sourceId.IsNullOrEmpty() || targetId.IsNullOrEmpty() || sourceId == targetId)
    {
        return -1;
    }

    var currentItems = await AppManager.Instance.SubItems();
    var orderedItems = ReorderSubItemsForMove(currentItems, sourceId, targetId);
    if (!orderedItems.Any(item => item.Id == sourceId) || !orderedItems.Any(item => item.Id == targetId))
    {
        return -1;
    }

    foreach (var item in orderedItems)
    {
        await SQLiteHelper.Instance.ReplaceAsync(item);
    }

    return 0;
}
```

- [ ] **步骤 3：运行订阅分组排序测试**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --no-restore --filter SubscriptionGroupDragSortTests
```

预期：`SubscriptionGroupDragSortTests` 全部通过。

- [ ] **步骤 4：提交服务层实现**

```powershell
git add v2rayN\ServiceLib\Handler\ConfigHandler.cs
git commit -m "feat: add subscription group reorder logic"
```

---

### 任务 3：在 `ProfilesViewModel` 接入订阅分组移动

**文件：**
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
- 测试：`v2rayN/ServiceLib.Tests/SubscriptionGroupDragSortTests.cs`

- [ ] **步骤 1：新增 ViewModel 方法**

放在 `RefreshSubscriptions()` 附近，便于和分组刷新逻辑一起维护：

```csharp
public async Task MoveSubTo(string? sourceId, SubItem? targetItem)
{
    if (sourceId.IsNullOrEmpty() || targetItem?.Id.IsNullOrEmpty() != false || sourceId == targetItem.Id)
    {
        return;
    }

    var selectedId = SelectedSub?.Id;
    if (await ConfigHandler.MoveSubItemTo(sourceId, targetItem.Id) != 0)
    {
        return;
    }

    await RefreshSubscriptions();
    SelectedSub = selectedId.IsNotEmpty()
        ? SubItems.FirstOrDefault(item => item.Id == selectedId) ?? SelectedSub
        : SelectedSub;
}
```

- [ ] **步骤 2：检查空值表达式编译**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --no-restore --filter SubscriptionGroupDragSortTests
```

预期：测试通过，且 `ProfilesViewModel.cs` 编译无空值相关错误。

- [ ] **步骤 3：提交 ViewModel 接入**

```powershell
git add v2rayN\ServiceLib\ViewModels\ProfilesViewModel.cs
git commit -m "feat: expose subscription group move in view model"
```

---

### 任务 4：实现 Desktop 分组拖拽浮层和落点排序

**文件：**
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml.cs`

- [ ] **步骤 1：给分组栏增加可覆盖浮层的容器**

把 `ProfilesView.axaml` 顶部 `DockPanel` 内的分组 `WrapPanel` 外层调整为 `Grid`，保留原有按钮和筛选框顺序。核心结构如下：

```xml
<Grid DockPanel.Dock="Top">
    <WrapPanel x:Name="panGroupTools" Margin="2">
        <ListBox
            x:Name="lstGroup"
            Margin="{StaticResource MarginLr4}"
            ItemsSource="{Binding SubItems}"
            Theme="{DynamicResource PureCardRadioGroupListBox}">
            <!-- 保留现有 ItemTemplate、ItemsPanel 和 ContextMenu -->
        </ListBox>

        <!-- 保留 btnEditSub、btnAddSub、txtServerFilter、测试按钮等现有控件 -->
    </WrapPanel>

    <Border
        x:Name="bdSubDragPreview"
        Padding="10,4"
        IsHitTestVisible="False"
        IsVisible="False"
        Opacity="0.88"
        Background="{DynamicResource ButtonDefaultBackground}"
        BorderBrush="{DynamicResource BorderColor}"
        BorderThickness="1"
        CornerRadius="4">
        <Border.RenderTransform>
            <TranslateTransform x:Name="ttSubDragPreview" />
        </Border.RenderTransform>
        <TextBlock x:Name="txtSubDragPreview" />
    </Border>
</Grid>
```

- [ ] **步骤 2：为拖拽目标增加样式类**

在 `ListBox.ItemTemplate` 根元素上加名称和类绑定入口，使用代码隐藏设置类即可，不需要新增转换器：

```xml
<DataTemplate>
    <StackPanel Orientation="Horizontal">
        <TextBlock
            VerticalAlignment="Center"
            IsVisible="{Binding !IsFailoverGroup}"
            Text="{Binding Remarks}" />
        <Label
            Margin="{StaticResource MarginLr4}"
            Classes="Solid Green"
            Content="{Binding Remarks}"
            IsVisible="{Binding IsFailoverGroup}"
            Theme="{DynamicResource TagLabel}" />
    </StackPanel>
</DataTemplate>
```

保持模板内容不变，目标项高亮通过 `ListBoxItem.Classes` 在代码中控制。

- [ ] **步骤 3：在 `ProfilesView.axaml.cs` 增加分组拖拽字段**

放在现有节点拖拽字段旁边：

```csharp
private const string SubDragFormat = "v2rayN.SubItem";
private static readonly DataFormat<string> SubDragDataFormat = DataFormat.CreateStringApplicationFormat(SubDragFormat);
private Point _subDragStartPoint;
private string? _subDragSourceId;
private ListBoxItem? _subDragTargetContainer;
```

- [ ] **步骤 4：在构造函数中绑定 `lstGroup` 指针和拖放事件**

放在 `lstProfiles` 拖放绑定之后，订阅分组拖放不依赖 `EnableDragDropSort`，因为这是新的分组栏能力：

```csharp
DragDrop.SetAllowDrop(lstGroup, true);
lstGroup.AddHandler(PointerPressedEvent, LstGroup_PointerPressed, RoutingStrategies.Tunnel, true);
lstGroup.AddHandler(PointerMovedEvent, LstGroup_PointerMoved, RoutingStrategies.Tunnel, true);
lstGroup.AddHandler(PointerReleasedEvent, LstGroup_PointerReleased, RoutingStrategies.Tunnel, true);
lstGroup.AddHandler(DragDrop.DragOverEvent, LstGroup_DragOver);
lstGroup.AddHandler(DragDrop.DropEvent, LstGroup_Drop);
```

- [ ] **步骤 5：新增拖拽源识别和浮层更新方法**

放在现有 `#region Drag and Drop` 内，和节点拖拽方法分开命名：

```csharp
private void LstGroup_PointerPressed(object? sender, PointerPressedEventArgs e)
{
    var point = e.GetCurrentPoint(lstGroup);
    if (!point.Properties.IsLeftButtonPressed)
    {
        return;
    }

    var item = GetSubItemFromEventSource(e.Source);
    if (item?.Id.IsNullOrEmpty() != false)
    {
        return;
    }

    _subDragStartPoint = e.GetPosition(this);
    _subDragSourceId = item.Id;
    lstGroup.SelectedItem = item;
}

private async void LstGroup_PointerMoved(object? sender, PointerEventArgs e)
{
    if (_subDragSourceId.IsNullOrEmpty() || !e.GetCurrentPoint(lstGroup).Properties.IsLeftButtonPressed)
    {
        return;
    }

    var position = e.GetPosition(this);
    var diff = _subDragStartPoint - position;
    if (Math.Abs(diff.X) < 4 && Math.Abs(diff.Y) < 4)
    {
        return;
    }

    ShowSubDragPreview(e);

    var data = new DataTransfer();
    data.Add(DataTransferItem.Create(SubDragDataFormat, _subDragSourceId));
    await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    ResetSubDragSource();
}

private void ShowSubDragPreview(PointerEventArgs e)
{
    if (lstGroup.SelectedItem is not SubItem item)
    {
        return;
    }

    var position = e.GetPosition(this);
    txtSubDragPreview.Text = item.Remarks;
    ttSubDragPreview.X = position.X + 12;
    ttSubDragPreview.Y = position.Y + 12;
    bdSubDragPreview.IsVisible = true;
}
```

- [ ] **步骤 6：新增拖放目标处理**

```csharp
private void LstGroup_DragOver(object? sender, DragEventArgs e)
{
    if (!e.DataTransfer.Formats.Contains(SubDragDataFormat))
    {
        e.DragEffects = DragDropEffects.None;
        return;
    }

    e.DragEffects = DragDropEffects.Move;
    HighlightSubDragTarget(e.Source);
}

private async void LstGroup_Drop(object? sender, DragEventArgs e)
{
    if (!e.DataTransfer.Formats.Contains(SubDragDataFormat))
    {
        e.DragEffects = DragDropEffects.None;
        return;
    }

    var targetItem = GetSubItemFromEventSource(e.Source);
    if (targetItem?.Id.IsNullOrEmpty() != false)
    {
        e.DragEffects = DragDropEffects.None;
        ResetSubDragSource();
        return;
    }

    e.DragEffects = DragDropEffects.Move;
    await ViewModel?.MoveSubTo(_subDragSourceId, targetItem);
    ResetSubDragSource();
}

private void LstGroup_PointerReleased(object? sender, PointerReleasedEventArgs e)
{
    ResetSubDragSource();
}
```

- [ ] **步骤 7：新增目标高亮和清理方法**

```csharp
private void HighlightSubDragTarget(object? source)
{
    var container = (source as Visual)?.FindAncestorOfType<ListBoxItem>();
    if (_subDragTargetContainer == container)
    {
        return;
    }

    _subDragTargetContainer?.Classes.Remove("SubDragTarget");
    _subDragTargetContainer = container;
    _subDragTargetContainer?.Classes.Add("SubDragTarget");
}

private void ResetSubDragSource()
{
    _subDragSourceId = null;
    bdSubDragPreview.IsVisible = false;
    _subDragTargetContainer?.Classes.Remove("SubDragTarget");
    _subDragTargetContainer = null;
}

private static SubItem? GetSubItemFromEventSource(object? source)
{
    return (source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext as SubItem;
}
```

- [ ] **步骤 8：补充目标项动画样式**

在 `ProfilesView.axaml` 的 `UserControl.Resources` 中加入局部样式，避免影响其他 `ListBox`：

```xml
<Style Selector="ListBoxItem.SubDragTarget">
    <Setter Property="Opacity" Value="0.72" />
    <Setter Property="RenderTransform">
        <TranslateTransform X="4" />
    </Setter>
</Style>
```

- [ ] **步骤 9：编译 Desktop 项目**

运行：

```powershell
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

预期：编译通过；若 Avalonia 样式属性类型报错，优先把动画效果收敛为 `Opacity` 高亮和浮层移动，保留拖动名称显示。

- [ ] **步骤 10：提交 Desktop 交互实现**

```powershell
git add v2rayN\v2rayN.Desktop\Views\ProfilesView.axaml v2rayN\v2rayN.Desktop\Views\ProfilesView.axaml.cs
git commit -m "feat: add subscription group drag preview"
```

---

### 任务 5：补充验证并更新本地改动记录

**文件：**
- 新增：`_local_changes/025-subscription-group-drag-sort/README.md`
- 新增：`_local_changes/025-subscription-group-drag-sort/files.md`
- 新增：`_local_changes/025-subscription-group-drag-sort/reapply.md`
- 新增：`_local_changes/025-subscription-group-drag-sort/tests.md`
- 新增：`_local_changes/025-subscription-group-drag-sort/patch.diff`

- [ ] **步骤 1：创建本地改动目录**

```powershell
Copy-Item -Recurse _local_changes\_template _local_changes\025-subscription-group-drag-sort
```

- [ ] **步骤 2：填写 `README.md`**

内容必须包含以下小节：

```markdown
# 订阅分组拖拽排序

## 基本信息

- 编号：`025`
- 目录：`_local_changes/025-subscription-group-drag-sort/`
- 创建日期：`2026-05-14`
- 适用源码版本：`v2rayN 7.20.4` 本地改造副本
- 改动类型：功能
- 状态：已验证

## 改动目的

主界面顶部“订阅分组”只能通过编辑排序值调整位置，用户无法像拖动列表标题一样直接调整分组顺序。本改动让真实订阅分组支持鼠标拖拽排序，并在拖动时显示分组名称预览。

## 用户可见行为

- 用户按住真实订阅分组名称拖动，可以把分组移动到目标分组前方。
- 拖动过程中显示当前分组名称浮层。
- 目标分组出现轻微视觉反馈。
- 虚拟“全部”分组不参与拖拽排序。
- 排序写回 `SubItem.Sort`，重启后保持新顺序。

## 设计取舍

- 复用已有 `SubItem.Sort`，不新增数据库字段。
- 服务层提供纯重排函数，方便单元测试。
- Desktop 自定义分组拖拽交互，因为 `DataGrid` 列拖动是内建能力，无法直接复用于 `ListBox`。
- 不引入第三方排序控件，降低未来迁移官方新版源码时的成本。

## 影响范围

- UI：主界面订阅分组栏增加拖拽浮层和目标反馈。
- 服务逻辑：新增订阅分组排序重排和保存入口。
- 配置文件：不修改配置结构。
- 数据结构：不新增表或字段，继续使用 `SubItem.Sort`。
- 平台差异：只实现 `v2rayN.Desktop`；WPF 版不变。
- 兼容性：已有分组按原 `Sort` 读取；拖拽后重新归一为从 `1` 开始的连续排序值。

## 代理安全影响评估

本次改动只影响订阅分组在 UI 中的展示顺序和 `SubItem.Sort` 持久化，不修改代理模式、Tun、系统代理、路由规则、DNS、订阅下载、核心组件、Geo/SRS 数据、更新下载、证书、网络请求或本地监听端口。不会导致流量绕过代理、错误直连、DNS 泄漏、订阅泄漏、证书信任扩大、核心组件降级或安全更新缺失。

## 注意事项

- 拖拽排序只处理真实分组，`Id` 为空的虚拟“全部”分组必须忽略。
- 如果未来官方改用可排序分组控件，迁移时优先复用官方控件，再保留本地 `SubItem.Sort` 语义。
```

- [ ] **步骤 3：填写 `files.md`**

列出每个修改文件和原因：

```markdown
# 文件清单

- 修改：`v2rayN/ServiceLib/Handler/ConfigHandler.cs`
  - 新增订阅分组重排和保存入口。
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
  - 暴露主界面分组拖拽排序调用方法，并在排序后刷新分组集合。
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`
  - 增加拖拽名称浮层和目标反馈样式。
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml.cs`
  - 处理分组栏指针拖拽、浮层移动和落点排序。
- 新增：`v2rayN/ServiceLib.Tests/SubscriptionGroupDragSortTests.cs`
  - 覆盖订阅分组重排规则。
- 新增：`_local_changes/025-subscription-group-drag-sort/*`
  - 记录本地改动目的、迁移方式、验证结果和补丁。
```

- [ ] **步骤 4：填写 `reapply.md`**

```markdown
# 重新应用说明

1. 检查新版源码中 `SubItem` 是否仍包含 `Sort` 字段，`AppManager.Instance.SubItems()` 是否仍按 `Sort` 读取。
2. 如果 `ProfilesView.axaml` 仍使用 `lstGroup` 展示订阅分组，把拖拽浮层和目标反馈样式迁移到该视图。
3. 将 `ConfigHandler.ReorderSubItemsForMove` 和 `ConfigHandler.MoveSubItemTo` 迁移到新版订阅分组管理位置。
4. 将 `ProfilesViewModel.MoveSubTo` 接入新版主界面 ViewModel。
5. 将 `ProfilesView.axaml.cs` 中 `lstGroup` 的指针事件和拖放处理迁移到新版 Desktop 视图。
6. 迁移 `SubscriptionGroupDragSortTests` 并运行测试。
7. 如果新版官方已经提供分组排序功能，保留官方 UI，重点确认它是否写回 `SubItem.Sort` 并忽略虚拟“全部”分组。
```

- [ ] **步骤 5：填写 `tests.md`**

记录实际执行的命令和结果：

```markdown
# 验证记录

## 验证环境

- 操作系统：Windows
- .NET SDK：以 `dotnet --version` 输出为准
- v2rayN 源码版本：`v2rayN 7.20.4` 本地改造副本
- 验证日期：`2026-05-14`

## 自动化验证

| 命令 | 结果 | 备注 |
| --- | --- | --- |
| `dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --no-restore --filter SubscriptionGroupDragSortTests` | 执行后记录退出码、通过数量和失败数量 | 验证分组重排规则 |
| `dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore` | 执行后记录退出码和编译警告 | 验证 Desktop 编译 |

## 手工验证

| 场景 | 步骤 | 预期结果 | 实际结果 |
| --- | --- | --- | --- |
| 分组拖拽排序 | 启动 Desktop，拖动一个真实订阅分组到另一个分组上 | 拖动时显示名称浮层，释放后分组移动到目标前方 | 执行后记录观察到的浮层、目标反馈和最终顺序 |
| 虚拟全部分组保护 | 尝试拖动“全部”分组 | 不开始排序，不写入数据库 | 执行后记录是否触发拖拽和数据库是否变化 |
| 重启保持顺序 | 完成拖拽后重启 Desktop | 分组顺序保持不变 | 执行后记录重启前后顺序 |

## 代理安全影响评估

本次改动不进入代理链路，只调整订阅分组 UI 排序和 `SubItem.Sort` 保存。未修改 Tun、系统代理、路由规则、DNS、订阅下载、核心组件、更新下载、证书或本地监听端口。

## 未验证内容与风险

- 如果没有运行 Desktop 手工界面验证，需要说明浮层位置和跨行拖动体验仍有残余风险。
```

- [ ] **步骤 6：生成补丁**

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes\025-subscription-group-drag-sort\patch.diff
```

- [ ] **步骤 7：提交本地改动记录**

```powershell
git add _local_changes\025-subscription-group-drag-sort
git commit -m "docs: record subscription group drag sort"
```

---

### 任务 6：最终验证

**文件：**
- 修改：按前述任务实际改动

- [ ] **步骤 1：运行服务层测试**

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --no-restore
```

预期：所有测试通过；允许保留基线已有资源重复名和 RID 警告。

- [ ] **步骤 2：运行 Desktop 编译**

```powershell
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

预期：编译通过；如果出现 Avalonia 样式相关错误，回到任务 4 收敛样式实现。

- [ ] **步骤 3：检查补丁不包含 `_local_changes` 自身**

```powershell
Select-String -Path _local_changes\025-subscription-group-drag-sort\patch.diff -Pattern "_local_changes"
```

预期：无匹配输出。

- [ ] **步骤 4：查看工作区状态**

```powershell
git status --short
```

预期：没有未提交源码改动；如果保留计划文档未提交，状态中只应出现计划文档。

---

## 代理安全影响评估

本功能只调整 Desktop 主界面订阅分组排序交互，并持久化 `SubItem.Sort`。它不修改代理模式、Tun、系统代理、路由规则、DNS、订阅 URL、订阅下载流程、核心组件、Geo/SRS 数据、更新下载、证书、网络请求或本地监听端口。

唯一间接影响是：分组展示顺序变化可能改变用户浏览和选择分组的便利性；不会改变节点配置内容、当前活动节点、故障转移队列、核心出站配置或流量路径。

## 计划自检

- 需求覆盖：已覆盖拖拽分组名称、左右/跨行位置变更、拖动名称浮层、目标反馈、持久化排序、虚拟“全部”分组保护。
- 文件边界：排序规则在 `ServiceLib`，交互在 `v2rayN.Desktop`，本地迁移记录在 `_local_changes/025-subscription-group-drag-sort`。
- 测试覆盖：计划包含纯逻辑单元测试、Desktop 编译验证和手工 UI 验证。
- 未纳入范围：不实现 WPF 版分组拖拽；不新增配置开关；不改变节点列表拖拽排序开关语义。
