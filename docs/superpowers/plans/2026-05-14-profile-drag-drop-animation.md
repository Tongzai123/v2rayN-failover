# 主列表拖拽排序动画实施计划

> **给 agentic workers：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 逐任务实施本计划。步骤使用 checkbox（`- [ ]`）语法跟踪进度。

**目标：** 在 `v2rayN.Desktop` 主界面配置列表中实现整行拖拽浮层和可见行避让动画，同时保留现有排序持久化语义。

**架构：** 继续使用现有 `DataGrid` 和 `ProfilesViewModel.MoveServerTo` 作为真实排序入口，视图层只负责拖拽状态、浮层、可见行位移和清理。将“源索引、目标索引、行高到行位移”的计算抽成 `ServiceLib` 纯函数，用现有 `ServiceLib.Tests` 覆盖，避免把可测试逻辑锁死在 Avalonia 事件里。

**技术栈：** `.NET 8`、Avalonia `DataGrid`、ReactiveUI、xUnit、现有 `ServiceLib` / `v2rayN.Desktop` 项目结构。

**执行约束：** 本计划中的 `git commit` 步骤均为可选检查点，用于保持任务边界清晰；执行时如果用户未要求提交，可以只完成 `git diff` 和验证记录，不强制创建提交。

---

## 文件结构

- 新增：`v2rayN/ServiceLib/Models/ProfileDragDropAnimation.cs`
  - 负责计算某一可见行在拖拽过程中的纵向避让位移。
  - 不依赖 Avalonia，便于 `ServiceLib.Tests` 直接验证。
- 修改：`v2rayN/ServiceLib.Tests/ProfileDragDropSortTests.cs`
  - 在已有拖拽排序测试中增加避让位移计算用例。
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`
  - 用一个 `Grid` 包住 `lstProfiles` 和透明命中浮层 `profileDragOverlayLayer`。
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml.cs`
  - 用自定义指针拖拽状态替代系统 `DragDrop.DoDragDropAsync` 视觉。
  - 创建近似整行浮层、基于鼠标坐标命中目标行、对可见 `DataGridRow` 设置 `TranslateTransform`。
  - 在释放鼠标时复用 `ViewModel.MoveServerTo(sourceIndexId, targetItem)` 提交排序。
- 修改：`_local_changes/025-profile-drag-drop-animation/*`
  - 继续更新已有 `025` 目录，记录本地改动目的、文件清单、迁移步骤、验证结果和功能补丁。

---

### 任务 1：为避让位移计算写失败测试

**文件：**
- 修改：`v2rayN/ServiceLib.Tests/ProfileDragDropSortTests.cs`
- 后续实现：`v2rayN/ServiceLib/Models/ProfileDragDropAnimation.cs`

- [ ] **步骤 1：在测试文件末尾追加向下拖拽用例**

在 `ProfileDragDropSortTests` 类中追加：

```csharp
[Fact]
public void GetRowOffset_SourceAboveTarget_ShiftsRowsBetweenSourceAndTargetUp()
{
    var rowHeight = 28d;

    Assert.Equal(0, ProfileDragDropAnimation.GetRowOffset(0, 1, 3, rowHeight));
    Assert.Equal(0, ProfileDragDropAnimation.GetRowOffset(1, 1, 3, rowHeight));
    Assert.Equal(-rowHeight, ProfileDragDropAnimation.GetRowOffset(2, 1, 3, rowHeight));
    Assert.Equal(-rowHeight, ProfileDragDropAnimation.GetRowOffset(3, 1, 3, rowHeight));
    Assert.Equal(0, ProfileDragDropAnimation.GetRowOffset(4, 1, 3, rowHeight));
}
```

- [ ] **步骤 2：追加向上拖拽用例**

继续追加：

```csharp
[Fact]
public void GetRowOffset_SourceBelowTarget_ShiftsRowsBetweenTargetAndSourceDown()
{
    var rowHeight = 28d;

    Assert.Equal(0, ProfileDragDropAnimation.GetRowOffset(0, 3, 1, rowHeight));
    Assert.Equal(rowHeight, ProfileDragDropAnimation.GetRowOffset(1, 3, 1, rowHeight));
    Assert.Equal(rowHeight, ProfileDragDropAnimation.GetRowOffset(2, 3, 1, rowHeight));
    Assert.Equal(0, ProfileDragDropAnimation.GetRowOffset(3, 3, 1, rowHeight));
    Assert.Equal(0, ProfileDragDropAnimation.GetRowOffset(4, 3, 1, rowHeight));
}
```

- [ ] **步骤 3：追加无效输入和同位置用例**

继续追加：

```csharp
[Theory]
[InlineData(2, 2, 2, 28)]
[InlineData(2, -1, 3, 28)]
[InlineData(2, 1, -1, 28)]
[InlineData(-1, 1, 3, 28)]
[InlineData(2, 1, 3, 0)]
[InlineData(2, 1, 3, -1)]
public void GetRowOffset_InvalidOrUnchangedDrag_ReturnsZero(int rowIndex, int sourceIndex, int targetIndex, double rowHeight)
{
    Assert.Equal(0, ProfileDragDropAnimation.GetRowOffset(rowIndex, sourceIndex, targetIndex, rowHeight));
}
```

- [ ] **步骤 4：运行测试确认失败**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --no-restore --filter ProfileDragDropSortTests
```

预期结果：编译失败，错误包含 `ProfileDragDropAnimation` 不存在。

- [ ] **步骤 5：可选提交测试检查点**

```powershell
git add v2rayN\ServiceLib.Tests\ProfileDragDropSortTests.cs
git commit -m "test: cover profile drag row shift calculation"
```

---

### 任务 2：实现可测试的行避让位移计算

**文件：**
- 新增：`v2rayN/ServiceLib/Models/ProfileDragDropAnimation.cs`
- 测试：`v2rayN/ServiceLib.Tests/ProfileDragDropSortTests.cs`

- [ ] **步骤 1：新增纯函数文件**

创建 `v2rayN/ServiceLib/Models/ProfileDragDropAnimation.cs`：

```csharp
namespace ServiceLib.Models;

public static class ProfileDragDropAnimation
{
    public static double GetRowOffset(int rowIndex, int sourceIndex, int targetIndex, double rowHeight)
    {
        if (rowIndex < 0 || sourceIndex < 0 || targetIndex < 0 || rowHeight <= 0 || sourceIndex == targetIndex)
        {
            return 0;
        }

        if (sourceIndex < targetIndex)
        {
            return rowIndex > sourceIndex && rowIndex <= targetIndex ? -rowHeight : 0;
        }

        return rowIndex >= targetIndex && rowIndex < sourceIndex ? rowHeight : 0;
    }
}
```

- [ ] **步骤 2：运行单元测试确认通过**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --no-restore --filter ProfileDragDropSortTests
```

预期结果：`ProfileDragDropSortTests` 全部通过。

- [ ] **步骤 3：可选提交纯函数实现检查点**

```powershell
git add v2rayN\ServiceLib\Models\ProfileDragDropAnimation.cs v2rayN\ServiceLib.Tests\ProfileDragDropSortTests.cs
git commit -m "feat: add profile drag row shift calculation"
```

---

### 任务 3：为 `DataGrid` 增加拖拽浮层容器

**文件：**
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`

- [ ] **步骤 1：把 `lstProfiles` 包进同层 `Grid`**

将当前从第 147 行开始的 `<DataGrid x:Name="lstProfiles" ...>` 外层改成：

```xml
<Grid ClipToBounds="True">
    <DataGrid
        x:Name="lstProfiles"
        AutoGenerateColumns="False"
        BorderThickness="1"
        CanUserReorderColumns="True"
        CanUserResizeColumns="True"
        Classes.InsetContent="True"
        GridLinesVisibility="All"
        HeadersVisibility="All"
        IsReadOnly="True"
        ItemsSource="{Binding ProfileItems}">
```

保留 `DataGrid.KeyBindings`、`DataGrid.ContextMenu` 和 `DataGrid.Columns` 内部内容不变。

- [ ] **步骤 2：在 `DataGrid` 后追加透明浮层 Canvas**

在原 `</DataGrid>` 后面追加：

```xml
    <Canvas
        x:Name="profileDragOverlayLayer"
        ClipToBounds="True"
        IsHitTestVisible="False"
        ZIndex="1000" />
</Grid>
```

最终结构必须是：`DockPanel` 的最后一个子元素是这个 `Grid`，`Grid` 里第一层是 `DataGrid`，第二层是 `Canvas`。

- [ ] **步骤 3：编译 Desktop 项目确认 XAML 可解析**

运行：

```powershell
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

预期结果：构建通过，没有 `profileDragOverlayLayer` 未找到或 XAML 嵌套错误。

- [ ] **步骤 4：可选提交 XAML 容器改动检查点**

```powershell
git add v2rayN\v2rayN.Desktop\Views\ProfilesView.axaml
git commit -m "feat: add profile drag overlay layer"
```

---

### 任务 4：改造 `ProfilesView` 拖拽状态机

**文件：**
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml.cs`

- [ ] **步骤 1：补充 Avalonia 动画命名空间**

在文件顶部追加：

```csharp
using Avalonia.Animation;
using Avalonia.Animation.Easings;
```

- [ ] **步骤 2：替换拖拽字段**

将当前拖拽字段：

```csharp
private const string DragFormat = "v2rayN.ProfileItemModel";
private static readonly DataFormat<string> DragDataFormat = DataFormat.CreateStringApplicationFormat(DragFormat);
private Point _dragStartPoint;
private int _dragStartIndex = -1;
private string? _dragSourceIndexId;
```

替换为：

```csharp
private const double DragActivationDistance = 4;
private const double DragSourceOpacity = 0;
private static readonly TimeSpan DragRowShiftDuration = TimeSpan.FromMilliseconds(140);
private Point _dragStartPoint;
private int _dragStartIndex = -1;
private int _dragTargetIndex = -1;
private string? _dragSourceIndexId;
private ProfileItemModel? _dragSourceItem;
private DataGridRow? _dragSourceRow;
private Border? _dragOverlay;
private double _dragRowHeight;
private bool _isRowDragActive;
private readonly Dictionary<DataGridRow, ITransform?> _originalRowTransforms = new();
private readonly Dictionary<DataGridRow, TranslateTransform> _translatedRows = new();
private readonly Dictionary<DataGridRow, double> _originalRowOpacity = new();
```

- [ ] **步骤 3：替换构造函数中的拖拽事件绑定**

将拖拽开关内的事件绑定：

```csharp
DragDrop.SetAllowDrop(lstProfiles, true);
lstProfiles.AddHandler(PointerPressedEvent, LstProfiles_PointerPressed, RoutingStrategies.Tunnel, true);
lstProfiles.AddHandler(PointerMovedEvent, LstProfiles_PointerMoved, RoutingStrategies.Tunnel, true);
lstProfiles.AddHandler(DragDrop.DragOverEvent, LstProfiles_DragOver);
lstProfiles.AddHandler(DragDrop.DropEvent, LstProfiles_Drop);
```

替换为：

```csharp
lstProfiles.AddHandler(PointerPressedEvent, LstProfiles_PointerPressed, RoutingStrategies.Tunnel, true);
lstProfiles.AddHandler(PointerMovedEvent, LstProfiles_PointerMoved, RoutingStrategies.Tunnel, true);
lstProfiles.AddHandler(PointerReleasedEvent, LstProfiles_PointerReleased, RoutingStrategies.Tunnel, true);
lstProfiles.PointerCaptureLost += LstProfiles_PointerCaptureLost;
```

- [ ] **步骤 4：替换 `LstProfiles_PointerPressed`**

用下面代码替换现有 `LstProfiles_PointerPressed`：

```csharp
private void LstProfiles_PointerPressed(object? sender, PointerPressedEventArgs e)
{
    var point = e.GetCurrentPoint(lstProfiles);
    if (!point.Properties.IsLeftButtonPressed)
    {
        return;
    }

    var row = GetDataGridRowFromEventSource(e.Source);
    var item = row?.DataContext as ProfileItemModel;
    if (row == null || item == null)
    {
        ResetDragState(false);
        return;
    }

    _dragStartPoint = e.GetPosition(lstProfiles);
    _dragStartIndex = ViewModel?.ProfileItems.IndexOf(item) ?? -1;
    _dragTargetIndex = _dragStartIndex;
    _dragSourceIndexId = item.IndexId;
    _dragSourceItem = item;
    _dragSourceRow = row;
    _dragRowHeight = Math.Max(row.Bounds.Height, 1);
    lstProfiles.SelectedItem = item;
}
```

- [ ] **步骤 5：替换 `LstProfiles_PointerMoved`**

用下面代码替换现有 `LstProfiles_PointerMoved`：

```csharp
private void LstProfiles_PointerMoved(object? sender, PointerEventArgs e)
{
    if (_dragStartIndex < 0 || _dragSourceIndexId.IsNullOrEmpty() || _dragSourceItem == null)
    {
        return;
    }

    var point = e.GetCurrentPoint(lstProfiles);
    if (!point.Properties.IsLeftButtonPressed)
    {
        ResetDragState(false);
        return;
    }

    var position = e.GetPosition(lstProfiles);
    var diff = _dragStartPoint - position;
    if (!_isRowDragActive && Math.Abs(diff.X) < DragActivationDistance && Math.Abs(diff.Y) < DragActivationDistance)
    {
        return;
    }

    if (!_isRowDragActive)
    {
        BeginRowDrag(e);
    }

    UpdateDragTarget(position);
    UpdateDragOverlay(position);
    UpdateShiftedRows();
    e.Handled = true;
}
```

- [ ] **步骤 6：新增释放和捕获丢失处理**

在 `LstProfiles_PointerMoved` 后追加：

```csharp
private async void LstProfiles_PointerReleased(object? sender, PointerReleasedEventArgs e)
{
    if (!_isRowDragActive)
    {
        ResetDragState(false);
        return;
    }

    var targetItem = GetProfileItemAtPosition(e.GetPosition(lstProfiles))
        ?? GetProfileItemByIndex(_dragTargetIndex);

    if (targetItem != null && _dragTargetIndex >= 0 && _dragTargetIndex != _dragStartIndex)
    {
        await ViewModel?.MoveServerTo(_dragSourceIndexId, targetItem);
    }

    e.Pointer.Capture(null);
    ResetDragState(false);
    e.Handled = true;
}

private void LstProfiles_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
{
    ResetDragState(false);
}
```

- [ ] **步骤 7：删除旧系统拖放方法**

删除这些方法：

```csharp
private void LstProfiles_DragOver(object? sender, DragEventArgs e)
private async void LstProfiles_Drop(object? sender, DragEventArgs e)
private void ResetDragSource()
```

保留 `GetProfileItemFromEventSource`，下一任务会补充基于鼠标坐标的命中方法。拖拽激活后不要再用 `e.Source` 计算目标行，因为指针捕获到 `lstProfiles` 后，事件源可能不再是鼠标下方的 `DataGridRow`。

- [ ] **步骤 8：编译确认事件签名正确**

运行：

```powershell
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

预期结果：如果 `PointerCaptureLostEventArgs` 命名空间或事件签名不匹配，按 Avalonia 编译错误调整到实际类型后重新运行，直到构建通过。

- [ ] **步骤 9：可选提交状态机改造检查点**

```powershell
git add v2rayN\v2rayN.Desktop\Views\ProfilesView.axaml.cs
git commit -m "feat: use pointer state for profile row drag"
```

---

### 任务 5：实现浮层、目标索引和可见行避让

**文件：**
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml.cs`
- 依赖：`v2rayN/ServiceLib/Models/ProfileDragDropAnimation.cs`

- [ ] **步骤 1：新增拖拽启动方法**

在 `#region Drag and Drop` 内追加：

```csharp
private void BeginRowDrag(PointerEventArgs e)
{
    if (_dragSourceItem == null || _dragSourceRow == null)
    {
        return;
    }

    _isRowDragActive = true;
    e.Pointer.Capture(lstProfiles);
    SetSourceRowOpacity(_dragSourceRow, DragSourceOpacity);
    _dragOverlay = CreateDragOverlay(_dragSourceItem, _dragSourceRow);
    profileDragOverlayLayer.Children.Add(_dragOverlay);
    UpdateDragOverlay(e.GetPosition(lstProfiles));
}
```

- [ ] **步骤 2：新增源行透明度方法**

继续追加：

```csharp
private void SetSourceRowOpacity(DataGridRow row, double opacity)
{
    if (!_originalRowOpacity.ContainsKey(row))
    {
        _originalRowOpacity[row] = row.Opacity;
    }

    row.Opacity = opacity;
}
```

- [ ] **步骤 3：新增浮层构造方法**

继续追加：

```csharp
private Border CreateDragOverlay(ProfileItemModel item, DataGridRow sourceRow)
{
    var rowPanel = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Height = Math.Max(sourceRow.Bounds.Height, 24)
    };

    foreach (var column in lstProfiles.Columns.Where(column => column.IsVisible).OrderBy(column => column.DisplayIndex))
    {
        rowPanel.Children.Add(new TextBlock
        {
            Text = GetDragOverlayText(item, column.Tag?.ToString()),
            Width = Math.Max(column.ActualWidth, 40),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0)
        });
    }

    return new Border
    {
        Width = Math.Max(lstProfiles.Bounds.Width, sourceRow.Bounds.Width),
        Height = Math.Max(sourceRow.Bounds.Height, 24),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Child = rowPanel,
        IsHitTestVisible = false,
        RenderTransform = new TranslateTransform()
    };
}
```

该浮层是第一阶段的近似整行视觉：按当前可见列和 `DisplayIndex` 显示文本，保留列宽关系，但不复制 `DataGridTemplateColumn` 中的所有标签和图标，也不承诺横向滚动后的逐像素对齐。

- [ ] **步骤 4：新增浮层文本映射**

继续追加：

```csharp
private static string GetDragOverlayText(ProfileItemModel item, string? columnTag)
{
    return columnTag switch
    {
        "ConfigType" => item.ConfigType.ToString(),
        "Remarks" => item.Remarks,
        "Address" => item.Address,
        "Port" => item.Port.ToString(CultureInfo.InvariantCulture),
        "Network" => item.Network,
        "StreamSecurity" => item.StreamSecurity,
        "SubRemarks" => item.SubRemarks,
        "DelayVal" => item.DelayVal,
        "SpeedVal" => item.SpeedVal,
        "TodayUp" => item.TodayUp,
        "TodayDown" => item.TodayDown,
        "TotalUp" => item.TotalUp,
        "TotalDown" => item.TotalDown,
        _ => string.Empty
    };
}
```

- [ ] **步骤 5：新增浮层位置更新方法**

继续追加：

```csharp
private void UpdateDragOverlay(Point position)
{
    if (_dragOverlay?.RenderTransform is not TranslateTransform transform)
    {
        return;
    }

    var top = Math.Clamp(position.Y - _dragRowHeight / 2, 0, Math.Max(0, lstProfiles.Bounds.Height - _dragRowHeight));
    transform.X = 0;
    transform.Y = top;
}
```

- [ ] **步骤 6：新增基于鼠标位置的目标索引更新方法**

继续追加：

```csharp
private void UpdateDragTarget(Point position)
{
    var item = GetProfileItemAtPosition(position);
    if (item == null)
    {
        return;
    }

    var index = ViewModel?.ProfileItems.IndexOf(item) ?? -1;
    if (index >= 0)
    {
        _dragTargetIndex = index;
    }
}
```

- [ ] **步骤 7：新增可见行位移更新方法**

继续追加：

```csharp
private void UpdateShiftedRows()
{
    foreach (var row in lstProfiles.GetVisualDescendants().OfType<DataGridRow>())
    {
        var item = row.DataContext as ProfileItemModel;
        var rowIndex = item == null ? -1 : ViewModel?.ProfileItems.IndexOf(item) ?? -1;
        var offset = ProfileDragDropAnimation.GetRowOffset(rowIndex, _dragStartIndex, _dragTargetIndex, _dragRowHeight);

        var transform = EnsureRowTranslateTransform(row);
        transform.Y = offset;
    }
}
```

- [ ] **步骤 8：新增行 transform 管理方法**

继续追加：

```csharp
private TranslateTransform EnsureRowTranslateTransform(DataGridRow row)
{
    if (_translatedRows.TryGetValue(row, out var transform))
    {
        return transform;
    }

    if (!_originalRowTransforms.ContainsKey(row))
    {
        _originalRowTransforms[row] = row.RenderTransform;
    }

    transform = new TranslateTransform
    {
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = DragRowShiftDuration,
                Easing = new CubicEaseOut()
            }
        }
    };
    row.RenderTransform = transform;
    _translatedRows[row] = transform;
    return transform;
}

private void RestoreRowTransform(DataGridRow row)
{
    if (!_translatedRows.ContainsKey(row))
    {
        return;
    }

    row.RenderTransform = _originalRowTransforms.TryGetValue(row, out var originalTransform)
        ? originalTransform
        : null;
    _translatedRows.Remove(row);
    _originalRowTransforms.Remove(row);
}
```

如果编译显示 `TranslateTransform.Transitions` 不可用，则按 Avalonia 11 实际 API 做等价调整，但必须保持同一语义：拖拽过程中不要在偏移为 0 时立即移除 transform；只把 `Y` 动画回 0，最终由 `ResetDragState` 统一恢复原始状态。

- [ ] **步骤 9：补充事件源和坐标命中辅助方法**

将当前 `GetProfileItemFromEventSource` 替换为三个方法：

```csharp
private static DataGridRow? GetDataGridRowFromEventSource(object? source)
{
    return (source as Visual)?.FindAncestorOfType<DataGridRow>();
}

private ProfileItemModel? GetProfileItemAtPosition(Point position)
{
    var visual = lstProfiles.InputHitTest(position) as Visual
        ?? lstProfiles.GetVisualAt(position);
    return GetDataGridRowFromEventSource(visual)?.DataContext as ProfileItemModel;
}

private static ProfileItemModel? GetProfileItemFromEventSource(object? source)
{
    return GetDataGridRowFromEventSource(source)?.DataContext as ProfileItemModel;
}
```

`GetProfileItemFromEventSource` 只用于按下时识别源行；拖拽激活后的目标行必须通过 `GetProfileItemAtPosition` 计算，避免指针捕获导致 `e.Source` 不再代表鼠标下方行。

- [ ] **步骤 10：新增按索引回取目标项方法**

继续追加：

```csharp
private ProfileItemModel? GetProfileItemByIndex(int index)
{
    return ViewModel?.ProfileItems != null && index >= 0 && index < ViewModel.ProfileItems.Count
        ? ViewModel.ProfileItems[index]
        : null;
}
```

- [ ] **步骤 11：新增完整清理方法**

继续追加：

```csharp
private void ResetDragState(bool keepSource)
{
    if (_dragOverlay != null)
    {
        profileDragOverlayLayer.Children.Remove(_dragOverlay);
    }

    foreach (var row in _translatedRows.Keys.ToList())
    {
        RestoreRowTransform(row);
    }

    foreach (var (row, opacity) in _originalRowOpacity.ToList())
    {
        row.Opacity = opacity;
    }

    _originalRowOpacity.Clear();
    _originalRowTransforms.Clear();
    _translatedRows.Clear();
    _dragOverlay = null;
    _dragTargetIndex = keepSource ? _dragTargetIndex : -1;
    _dragSourceRow = keepSource ? _dragSourceRow : null;
    _dragSourceItem = keepSource ? _dragSourceItem : null;
    _dragSourceIndexId = keepSource ? _dragSourceIndexId : null;
    _dragStartIndex = keepSource ? _dragStartIndex : -1;
    _dragRowHeight = keepSource ? _dragRowHeight : 0;
    _isRowDragActive = false;
}
```

- [ ] **步骤 12：编译并处理 Avalonia API 差异**

运行：

```powershell
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

预期结果：构建通过。若 `BoxShadows.Parse`、`TranslateTransform.Transitions` 或 `Math.Clamp` 类型推断产生编译错误，按 Avalonia 11 / .NET 8 实际 API 做等价替换，并保持功能语义不变。

- [ ] **步骤 13：可选提交浮层和避让动画检查点**

```powershell
git add v2rayN\v2rayN.Desktop\Views\ProfilesView.axaml.cs
git commit -m "feat: animate profile row drag sorting"
```

---

### 任务 6：补齐本地改动记录

**文件：**
- 新增或修改：`_local_changes/025-profile-drag-drop-animation/README.md`
- 新增或修改：`_local_changes/025-profile-drag-drop-animation/files.md`
- 新增或修改：`_local_changes/025-profile-drag-drop-animation/reapply.md`
- 新增或修改：`_local_changes/025-profile-drag-drop-animation/tests.md`
- 新增或修改：`_local_changes/025-profile-drag-drop-animation/patch.diff`

- [ ] **步骤 1：继续使用已有改动目录**

```powershell
New-Item -ItemType Directory -Force -Path _local_changes\025-profile-drag-drop-animation
```

该目录当前已经用于记录本功能计划。实施代码时继续更新 `025-profile-drag-drop-animation`，把状态从“计划文档”推进到“功能实现记录”，不要另建 `026` 或其他无关编号。

- [ ] **步骤 2：写入 `README.md`**

内容必须覆盖：改动目的、用户可见行为、设计取舍、影响范围、代理安全影响评估。代理安全结论写明：该功能只影响 Desktop UI 排序动画和排序提交，不修改代理模式、Tun、系统代理、路由、DNS、订阅、证书、核心组件或本地监听端口。

- [ ] **步骤 3：写入 `files.md`**

列出这些文件：

```text
v2rayN/ServiceLib/Models/ProfileDragDropAnimation.cs
v2rayN/ServiceLib.Tests/ProfileDragDropSortTests.cs
v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml
v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml.cs
```

说明每个文件的新增或修改原因；如果 `docs/superpowers/specs/2026-05-14-profile-drag-drop-animation-design.md` 和 `docs/superpowers/plans/2026-05-14-profile-drag-drop-animation.md` 仍作为迁移依据，也要列入文件清单。

- [ ] **步骤 4：写入 `reapply.md`**

迁移步骤写清：先检查新版是否已有官方拖拽动画；若无，先迁移纯函数和测试，再迁移 XAML 浮层容器，最后迁移 `ProfilesView.axaml.cs` 指针状态机。目标行计算必须按鼠标坐标命中测试，不要退回依赖 `e.Source`；排序提交优先使用 `MoveServerTo(string? sourceIndexId, ProfileItemModel targetItem)`。

- [ ] **步骤 5：写入 `tests.md`**

记录自动化验证命令：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --no-restore --filter ProfileDragDropSortTests
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

记录手工验证场景：向下拖、向上拖、取消拖、横向滚动后拖、调整列顺序后拖、故障转移分组内拖。横向滚动场景第一阶段只要求浮层不遮挡操作、不残留、不影响排序提交；如果未实现逐像素对齐，要在 `tests.md` 中明确记录为后续增强。

- [ ] **步骤 6：生成功能补丁**

运行：

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes\025-profile-drag-drop-animation\patch.diff
```

- [ ] **步骤 7：可选提交本地改动记录检查点**

```powershell
git add _local_changes\025-profile-drag-drop-animation
git commit -m "docs: document profile drag animation local change"
```

---

### 任务 7：执行最终验证

**文件：**
- 修改：`_local_changes/025-profile-drag-drop-animation/tests.md`

- [ ] **步骤 1：运行拖拽排序相关测试**

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --no-restore --filter ProfileDragDropSortTests
```

预期结果：通过。

- [ ] **步骤 2：运行 Desktop 编译**

```powershell
dotnet build v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj --no-restore
```

预期结果：通过。

- [ ] **步骤 3：执行手工验证**

手工验证清单：

- 启用“配置拖放排序”并重启 Desktop 客户端。
- 从上往下拖动一行，浮层跟随鼠标，中间可见行上移避让，释放后顺序保存。
- 从下往上拖动一行，浮层跟随鼠标，中间可见行下移避让，释放后顺序保存。
- 按住后移动未超过阈值再释放，不应触发排序或残留半透明行。
- 拖动时移出行区域再释放，无有效目标时恢复原状。
- 横向滚动后拖动，浮层宽度和可见列文本不遮挡主列表操作；如果未实现和当前横向滚动位置逐像素对齐，记录为已知限制。
- 调整列顺序后拖动，浮层按当前列显示顺序展示文本。
- 在故障转移分组内拖动队列项，释放后优先级刷新，不破坏当前活动节点标记。

- [ ] **步骤 4：更新验证记录和补丁**

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes\025-profile-drag-drop-animation\patch.diff
```

把实际执行结果写入 `_local_changes/025-profile-drag-drop-animation/tests.md`。

- [ ] **步骤 5：可选最终提交**

```powershell
git add v2rayN\ServiceLib\Models\ProfileDragDropAnimation.cs v2rayN\ServiceLib.Tests\ProfileDragDropSortTests.cs v2rayN\v2rayN.Desktop\Views\ProfilesView.axaml v2rayN\v2rayN.Desktop\Views\ProfilesView.axaml.cs _local_changes\025-profile-drag-drop-animation
git commit -m "feat: animate profile drag sorting"
```

---

## 自检结果

- 规格覆盖：计划覆盖设计文档中的源行抓起、浮层跟随、可见行上下避让、释放后复用现有排序、清理状态、横向滚动第一阶段限制、列顺序风险记录、故障转移分组验证、代理安全评估。
- 占位符扫描：没有保留未定义的待补内容；需要根据编译错误调整的点均限定为 Avalonia API 等价替换。
- 类型一致性：纯函数使用 `double` 行高；视图状态使用 `ProfileItemModel`、`DataGridRow`、`Border`、`TranslateTransform`；目标命中使用 `Point`；排序提交使用现有 `MoveServerTo(string?, ProfileItemModel)`。
- 范围控制：不替换 `DataGrid`，不新增第三方依赖，不改配置结构、数据库结构或代理链路。

