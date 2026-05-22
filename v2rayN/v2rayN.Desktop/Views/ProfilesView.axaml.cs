using DialogHostAvalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Layout;
using Avalonia.VisualTree;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class ProfilesView : ReactiveUserControl<ProfilesViewModel>
{
    private static Config _config;
    private Window? _window;
    private static readonly string _tag = "ProfilesView";
    private const double DragActivationDistance = 4;
    private const double DragSourceOpacity = 0;
    private static readonly TimeSpan DragRowShiftDuration = TimeSpan.FromMilliseconds(140);
    private Point _dragStartPoint;
    private int _dragStartIndex = -1;
    private int _dragTargetIndex = -1;
    private string? _dragSourceIndexId;
    private List<string> _dragSourceIndexIds = [];
    private List<int> _dragSourceIndexes = [];
    private List<string> _dragPressedSelectedIndexIds = [];
    private ProfileItemModel? _dragSourceItem;
    private DataGridRow? _dragSourceRow;
    private Border? _dragOverlay;
    private double _dragRowHeight;
    private bool _isRowDragActive;
    private bool _dragReleaseCleanupPending;
    private int _dragReleaseCleanupVersion;
    private readonly Dictionary<DataGridRow, ITransform?> _originalRowTransforms = new();
    private readonly Dictionary<DataGridRow, TranslateTransform> _translatedRows = new();
    private readonly Dictionary<DataGridRow, double> _originalRowOpacity = new();
    private Point _subDragStartPoint;
    private string? _subDragSourceId;
    private SubItem? _subDragSourceItem;
    private bool _subDragPreviewActive;
    private int _subDragStartIndex = -1;
    private int _subDragTargetIndex = -1;
    private double _subDragItemWidth;
    private double _subDragOverlayY;
    private Border? _subDragOverlay;
    private ListBoxItem? _subDragPlaceholderContainer;
    private readonly Dictionary<ListBoxItem, ITransform?> _originalSubItemTransforms = new();
    private readonly Dictionary<ListBoxItem, TranslateTransform> _translatedSubItems = new();

    public ProfilesView()
    {
        InitializeComponent();
    }

    public ProfilesView(Window window)
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;
        _window = window;

        menuSelectAll.Click += menuSelectAll_Click;
        btnAutofitColumnWidth.Click += BtnAutofitColumnWidth_Click;
        txtServerFilter.KeyDown += TxtServerFilter_KeyDown;
        lstProfiles.KeyDown += LstProfiles_KeyDown;
        lstProfiles.SelectionChanged += lstProfiles_SelectionChanged;
        lstProfiles.DoubleTapped += LstProfiles_DoubleTapped;
        lstProfiles.LoadingRow += LstProfiles_LoadingRow;
        lstProfiles.Sorting += LstProfiles_Sorting;
        if (_config.UiItem.EnableDragDropSort)
        {
            lstProfiles.AddHandler(PointerPressedEvent, LstProfiles_PointerPressed, RoutingStrategies.Tunnel, true);
            lstProfiles.AddHandler(PointerMovedEvent, LstProfiles_PointerMoved, RoutingStrategies.Tunnel, true);
            lstProfiles.AddHandler(PointerReleasedEvent, LstProfiles_PointerReleased, RoutingStrategies.Tunnel, true);
            lstProfiles.PointerCaptureLost += LstProfiles_PointerCaptureLost;
        }
        DragDrop.SetAllowDrop(lstGroup, true);
        lstGroup.AddHandler(PointerPressedEvent, LstGroup_PointerPressed, RoutingStrategies.Tunnel, true);
        lstGroup.AddHandler(PointerMovedEvent, LstGroup_PointerMoved, RoutingStrategies.Tunnel, true);
        lstGroup.AddHandler(PointerReleasedEvent, LstGroup_PointerReleased, RoutingStrategies.Tunnel, true);
        lstGroup.PointerCaptureLost += LstGroup_PointerCaptureLost;

        ViewModel = new ProfilesViewModel(UpdateViewHandler);

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.ProfileItems, v => v.lstProfiles.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedProfile, v => v.lstProfiles.SelectedItem).DisposeWith(disposables);

            // this.OneWayBind(ViewModel, vm => vm.SubItems, v => v.lstGroup.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSub, v => v.lstGroup.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ServerFilter, v => v.txtServerFilter.Text).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddSubCmd, v => v.btnAddSub).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.EditSubCmd, v => v.btnEditSub).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.EditSubCmd, v => v.menuSubEdit).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddSubCmd, v => v.menuSubAdd).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.DeleteSubCmd, v => v.menuSubDelete).DisposeWith(disposables);

            //servers delete
            this.BindCommand(ViewModel, vm => vm.EditServerCmd, v => v.menuEditServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RemoveServerCmd, v => v.menuRemoveServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RemoveDuplicateServerCmd, v => v.menuRemoveDuplicateServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CopyServerCmd, v => v.menuCopyServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SetDefaultServerCmd, v => v.menuSetDefaultServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddToFailoverQueueCmd, v => v.menuAddToFailoverQueue).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RemoveFromFailoverQueueCmd, v => v.menuRemoveFromFailoverQueue).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ShareServerCmd, v => v.menuShareServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.GenGroupAllServerCmd, v => v.menuGenGroupAllServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.GenGroupRegionServerCmd, v => v.menuGenGroupRegionServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SetActiveFailoverGroupCmd, v => v.btnSetActiveFailoverGroup).DisposeWith(disposables);

            //servers move
            //this.OneWayBind(ViewModel, vm => vm.SubItems, v => v.cmbMoveToGroup.ItemsSource).DisposeWith(disposables);
            //this.Bind(ViewModel, vm => vm.SelectedMoveToGroup, v => v.cmbMoveToGroup.SelectedItem).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.MoveTopCmd, v => v.menuMoveTop).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveUpCmd, v => v.menuMoveUp).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveDownCmd, v => v.menuMoveDown).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveBottomCmd, v => v.menuMoveBottom).DisposeWith(disposables);

            //servers ping
            this.BindCommand(ViewModel, vm => vm.MixedTestServerCmd, v => v.menuMixedTestServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.TcpingServerCmd, v => v.menuTcpingServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RealPingServerCmd, v => v.menuRealPingServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SpeedServerCmd, v => v.menuSpeedServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SortServerResultCmd, v => v.menuSortServerResult).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RemoveInvalidServerResultCmd, v => v.menuRemoveInvalidServerResult).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.FastRealPingCmd, v => v.btnFastRealPing).DisposeWith(disposables);

            //servers export
            this.BindCommand(ViewModel, vm => vm.Export2ClientConfigCmd, v => v.menuExport2ClientConfig).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.Export2ClientConfigClipboardCmd, v => v.menuExport2ClientConfigClipboard).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.Export2ShareUrlCmd, v => v.menuExport2ShareUrl).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.Export2ShareUrlBase64Cmd, v => v.menuExport2ShareUrlBase64).DisposeWith(disposables);

            AppEvents.AppExitRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(_ => StorageUI())
              .DisposeWith(disposables);

            AppEvents.AdjustMainLvColWidthRequested
                .AsObservable()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => AutofitColumnWidth())
                .DisposeWith(disposables);
        });

        RestoreUI();
    }

    private async void LstProfiles_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;

        if (ViewModel != null && e.Column?.Tag?.ToString() != null)
        {
            await ViewModel.SortServer(e.Column.Tag.ToString());
        }

        e.Handled = false;
    }

    #region Event

    private async Task<bool> UpdateViewHandler(EViewAction action, object? obj)
    {
        switch (action)
        {
            case EViewAction.SetClipboardData:
                if (obj is null)
                {
                    return false;
                }

                await AvaUtils.SetClipboardData(this, (string)obj);
                break;

            case EViewAction.ProfilesFocus:
                lstProfiles.Focus();
                break;

            case EViewAction.ShowYesNo:
                if (await UI.ShowYesNo(_window, ResUI.RemoveServer) != ButtonResult.Yes)
                {
                    return false;
                }
                break;

            case EViewAction.SaveFileDialog:
                if (obj is null)
                {
                    return false;
                }

                var fileName = await UI.SaveFileDialog(_window, "");
                if (fileName.IsNullOrEmpty())
                {
                    return false;
                }
                ViewModel?.Export2ClientConfigResult(fileName, (ProfileItem)obj);
                break;

            case EViewAction.AddServerWindow:
                if (obj is null)
                {
                    return false;
                }

                return await new AddServerWindow((ProfileItem)obj).ShowDialog<bool>(_window);

            case EViewAction.AddServer2Window:
                if (obj is null)
                {
                    return false;
                }

                return await new AddServer2Window((ProfileItem)obj).ShowDialog<bool>(_window);

            case EViewAction.AddGroupServerWindow:
                if (obj is null)
                {
                    return false;
                }

                return await new AddGroupServerWindow((ProfileItem)obj).ShowDialog<bool>(_window);

            case EViewAction.ShareServer:
                if (obj is null)
                {
                    return false;
                }

                await ShareServer((string)obj);
                break;

            case EViewAction.SubEditWindow:
                if (obj is null)
                {
                    return false;
                }

                return await new SubEditWindow((SubItem)obj).ShowDialog<bool>(_window);

            case EViewAction.DispatcherRefreshServersBiz:
                Dispatcher.UIThread.Post(RefreshServersBiz, DispatcherPriority.Default);
                break;
        }

        return await Task.FromResult(true);
    }

    public async Task ShareServer(string url)
    {
        if (url.IsNullOrEmpty())
        {
            return;
        }

        var dialog = new QrcodeView(url);
        await DialogHost.Show(dialog);
    }

    public void RefreshServersBiz()
    {
        if (lstProfiles.SelectedIndex >= 0)
        {
            lstProfiles.ScrollIntoView(lstProfiles.SelectedItem, null);
        }
    }

    private void lstProfiles_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.SelectedProfiles = lstProfiles.SelectedItems.Cast<ProfileItemModel>().ToList();
        }
    }

    private void LstProfiles_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        var source = e.Source as Border;
        if (source?.Name == "HeaderBackground")
        {
            return;
        }

        if (_config.UiItem.DoubleClick2Activate)
        {
            ViewModel?.SetDefaultServer();
        }
        else
        {
            ViewModel?.EditServerAsync();
        }
    }

    private void LstProfiles_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        e.Row.Header = $" {e.Row.Index + 1}";
    }

    //private void LstProfiles_ColumnHeader_Click(object? sender, RoutedEventArgs e)
    //{
    //    var colHeader = sender as DataGridColumnHeader;
    //    if (colHeader == null || colHeader.TabIndex < 0 || colHeader.Column == null)
    //    {
    //        return;
    //    }

    //    var colName = ((MyDGTextColumn)colHeader.Column).ExName;
    //    ViewModel?.SortServer(colName);
    //}

    private void menuSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        lstProfiles.SelectAll();
    }

    private void LstProfiles_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta)
        {
            switch (e.Key)
            {
                case Key.A:
                    menuSelectAll_Click(null, null);
                    break;

                case Key.C:
                    ViewModel?.Export2ShareUrlAsync(false);
                    break;

                case Key.D:
                    ViewModel?.EditServerAsync();
                    break;

                case Key.F:
                    ViewModel?.ShareServerAsync();
                    break;

                case Key.O:
                    ViewModel?.ServerSpeedtest(ESpeedActionType.Tcping);
                    break;

                case Key.R:
                    ViewModel?.ServerSpeedtest(ESpeedActionType.Realping);
                    break;

                case Key.T:
                    ViewModel?.ServerSpeedtest(ESpeedActionType.Speedtest);
                    break;

                case Key.E:
                    ViewModel?.ServerSpeedtest(ESpeedActionType.Mixedtest);
                    break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Enter:
                    //case Key.Return:
                    ViewModel?.SetDefaultServer();
                    break;

                case Key.Delete:
                case Key.Back:
                    ViewModel?.RemoveServerAsync();
                    break;

                case Key.T:
                    ViewModel?.MoveServer(EMove.Top);
                    break;

                case Key.U:
                    ViewModel?.MoveServer(EMove.Up);
                    break;

                case Key.D:
                    ViewModel?.MoveServer(EMove.Down);
                    break;

                case Key.B:
                    ViewModel?.MoveServer(EMove.Bottom);
                    break;

                case Key.Escape:
                    ViewModel?.ServerSpeedtestStop();
                    break;
            }
        }
    }

    private void BtnAutofitColumnWidth_Click(object? sender, RoutedEventArgs e)
    {
        AutofitColumnWidth();
    }

    private void AutofitColumnWidth()
    {
        try
        {
            //First scroll horizontally to the initial position to avoid the control crash bug
            if (lstProfiles.SelectedIndex >= 0)
            {
                lstProfiles.ScrollIntoView(lstProfiles.SelectedItem, lstProfiles.Columns[0]);
            }
            else
            {
                var model = lstProfiles.ItemsSource.Cast<ProfileItemModel>();
                if (model.Any())
                {
                    lstProfiles.ScrollIntoView(model.First(), lstProfiles.Columns[0]);
                }
                else
                {
                    return;
                }
            }

            foreach (var it in lstProfiles.Columns)
            {
                it.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private void TxtServerFilter_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            ViewModel?.RefreshServers();
        }
    }

    #endregion Event

    #region UI

    private void RestoreUI()
    {
        try
        {
            var lvColumnItem = _config.UiItem.MainColumnItem.OrderBy(t => t.Index).ToList();
            var displayIndex = 0;
            foreach (var item in lvColumnItem)
            {
                foreach (var item2 in lstProfiles.Columns)
                {
                    if (item2.Tag == null)
                    {
                        continue;
                    }
                    if (item2.Tag.Equals(item.Name))
                    {
                        if (item.Width < 0)
                        {
                            item2.IsVisible = false;
                        }
                        else
                        {
                            item2.Width = new DataGridLength(item.Width, DataGridLengthUnitType.Pixel);
                            item2.DisplayIndex = displayIndex++;
                        }
                        if (item.Name.ToLower().StartsWith("to"))
                        {
                            item2.IsVisible = _config.GuiItem.EnableStatistics;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private void StorageUI()
    {
        try
        {
            List<ColumnItem> lvColumnItem = new();
            foreach (var item2 in lstProfiles.Columns)
            {
                if (item2.Tag == null)
                {
                    continue;
                }
                lvColumnItem.Add(new()
                {
                    Name = (string)item2.Tag,
                    Width = (int)(item2.IsVisible == true ? item2.ActualWidth : -1),
                    Index = item2.DisplayIndex
                });
            }
            _config.UiItem.MainColumnItem = lvColumnItem;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    #endregion UI

    #region Drag and Drop

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

        _subDragStartPoint = e.GetPosition(gridGroupTools);
        _subDragSourceId = item.Id;
        _subDragSourceItem = item;
        lstGroup.SelectedItem = item;
    }

    private async void LstGroup_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_subDragSourceId.IsNullOrEmpty() || !e.GetCurrentPoint(lstGroup).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = e.GetPosition(gridGroupTools);
        var diff = _subDragStartPoint - position;
        if (!_subDragPreviewActive && Math.Abs(diff.X) < DragActivationDistance && Math.Abs(diff.Y) < DragActivationDistance)
        {
            return;
        }

        if (!_subDragPreviewActive)
        {
            if (!BeginSubDrag(e))
            {
                ResetSubDragSource();
                return;
            }
        }

        UpdateSubDragTarget(e.GetPosition(lstGroup));
        UpdateSubDragOverlay(position);
        UpdateShiftedSubItems();
        e.Handled = true;
        await Task.CompletedTask;
    }

    private async void LstGroup_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_subDragPreviewActive)
        {
            ResetSubDragSource(false);
            return;
        }

        if (ViewModel != null)
        {
            if (_subDragTargetIndex >= 1 && _subDragTargetIndex != _subDragStartIndex)
            {
                ViewModel.MoveSubDragPreviewTo(_subDragSourceId, _subDragTargetIndex);
                await ViewModel.CommitSubDragPreview();
            }
            else
            {
                ViewModel.CancelSubDragPreview();
            }
        }
        e.Pointer.Capture(null);
        ResetSubDragSource(false);
        e.Handled = true;
    }

    private void LstGroup_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ResetSubDragSource();
    }

    private bool BeginSubDrag(PointerEventArgs e)
    {
        if (ViewModel?.BeginSubDragPreview(_subDragSourceId) != true || _subDragSourceItem == null)
        {
            return false;
        }

        _subDragPlaceholderContainer = GetSubContainer(_subDragSourceId);
        if (_subDragPlaceholderContainer == null)
        {
            ViewModel.CancelSubDragPreview();
            return false;
        }

        _subDragStartIndex = ViewModel.SubItems.IndexOf(_subDragSourceItem);
        _subDragTargetIndex = _subDragStartIndex;
        _subDragItemWidth = Math.Max(_subDragPlaceholderContainer.Bounds.Width, 1);
        _subDragOverlayY = _subDragPlaceholderContainer.TranslatePoint(new Point(0, 0), gridGroupTools)?.Y ?? 0;
        _subDragPreviewActive = true;
        e.Pointer.Capture(lstGroup);
        _subDragPlaceholderContainer.Classes.Add("SubDragPlaceholder");
        _subDragOverlay = CreateSubDragOverlay(_subDragSourceItem, _subDragPlaceholderContainer);
        subDragOverlayLayer.Children.Add(_subDragOverlay);
        UpdateSubDragOverlay(e.GetPosition(gridGroupTools));
        return true;
    }

    private Border CreateSubDragOverlay(SubItem item, ListBoxItem sourceContainer)
    {
        var child = CreateSubDragOverlayContent(item);

        return new Border
        {
            Width = Math.Max(sourceContainer.Bounds.Width, 1),
            Height = Math.Max(sourceContainer.Bounds.Height, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Child = child,
            IsHitTestVisible = false,
            RenderTransform = new TranslateTransform()
        };
    }

    private Control CreateSubDragOverlayContent(SubItem item)
    {
        if (item.IsFailoverGroup)
        {
            var label = new Label
            {
                Content = item.Remarks,
                Margin = new Thickness(4, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            label.Classes.Add("Solid");
            label.Classes.Add("Green");
            if (Application.Current?.TryGetResource("TagLabel", ActualThemeVariant, out var resource) == true
                && resource is ControlTheme theme)
            {
                label.Theme = theme;
            }
            return label;
        }

        return new TextBlock
        {
            Text = item.Remarks,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
    }

    private void UpdateSubDragOverlay(Point position)
    {
        if (_subDragOverlay?.RenderTransform is not TranslateTransform transform)
        {
            return;
        }

        var left = Math.Clamp(position.X - _subDragItemWidth / 2, 0, Math.Max(0, gridGroupTools.Bounds.Width - _subDragItemWidth));
        transform.X = left;
        transform.Y = _subDragOverlayY;
    }

    private void UpdateSubDragTarget(Point position)
    {
        if (ViewModel == null)
        {
            return;
        }

        var insertIndex = GetSubInsertIndex(position);
        var targetIndex = insertIndex > _subDragStartIndex ? insertIndex - 1 : insertIndex;
        _subDragTargetIndex = Math.Clamp(targetIndex, 1, Math.Max(1, ViewModel.SubItems.Count - 1));
    }

    private int GetSubInsertIndex(Point position)
    {
        if (ViewModel == null)
        {
            return 1;
        }

        var containers = lstGroup
            .GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Select(container => new
            {
                Container = container,
                Item = container.DataContext as SubItem,
                TopLeft = container.TranslatePoint(new Point(0, 0), lstGroup),
            })
            .Where(entry => entry.Item?.Id.IsNotEmpty() == true && entry.TopLeft.HasValue)
            .Select(entry => new
            {
                entry.Container,
                entry.Item,
                CenterX = entry.TopLeft!.Value.X + entry.Container.Bounds.Width / 2,
            })
            .OrderBy(entry => ViewModel.SubItems.IndexOf(entry.Item!))
            .ToList();

        if (containers.Count == 0)
        {
            return 1;
        }

        foreach (var entry in containers)
        {
            if (position.X < entry.CenterX)
            {
                return ViewModel.SubItems.IndexOf(entry.Item!);
            }
        }

        return ViewModel.SubItems.Count;
    }

    private void UpdateShiftedSubItems()
    {
        foreach (var container in lstGroup.GetVisualDescendants().OfType<ListBoxItem>())
        {
            var item = container.DataContext as SubItem;
            var itemIndex = item == null ? -1 : ViewModel?.SubItems.IndexOf(item) ?? -1;
            var offset = SubscriptionGroupDragDropAnimation.GetItemOffset(
                itemIndex,
                _subDragStartIndex,
                _subDragTargetIndex,
                _subDragItemWidth);

            var transform = EnsureSubItemTranslateTransform(container);
            transform.X = offset;
            transform.Y = 0;
        }
    }

    private TranslateTransform EnsureSubItemTranslateTransform(ListBoxItem item)
    {
        if (_translatedSubItems.TryGetValue(item, out var transform))
        {
            return transform;
        }

        if (!_originalSubItemTransforms.ContainsKey(item))
        {
            _originalSubItemTransforms[item] = item.RenderTransform;
        }

        transform = new TranslateTransform
        {
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = TranslateTransform.XProperty,
                    Duration = DragRowShiftDuration,
                    Easing = new CubicEaseOut()
                }
            }
        };
        item.RenderTransform = transform;
        _translatedSubItems[item] = transform;
        return transform;
    }

    private void RestoreSubItemTransform(ListBoxItem item)
    {
        if (!_translatedSubItems.ContainsKey(item))
        {
            return;
        }

        item.RenderTransform = _originalSubItemTransforms.TryGetValue(item, out var originalTransform)
            ? originalTransform
            : null;
        _translatedSubItems.Remove(item);
        _originalSubItemTransforms.Remove(item);
    }

    private ListBoxItem? GetSubContainer(string? subId)
    {
        if (subId.IsNullOrEmpty())
        {
            return null;
        }

        return lstGroup
            .GetVisualDescendants()
            .OfType<ListBoxItem>()
            .FirstOrDefault(container => (container.DataContext as SubItem)?.Id == subId);
    }

    private void ResetSubDragSource(bool cancelPreview = true)
    {
        if (cancelPreview)
        {
            ViewModel?.CancelSubDragPreview();
        }

        if (_subDragOverlay != null)
        {
            subDragOverlayLayer.Children.Remove(_subDragOverlay);
        }

        foreach (var item in _translatedSubItems.Keys.ToList())
        {
            RestoreSubItemTransform(item);
        }

        _subDragSourceId = null;
        _subDragSourceItem = null;
        _subDragPreviewActive = false;
        _subDragPlaceholderContainer?.Classes.Remove("SubDragPlaceholder");
        _subDragPlaceholderContainer = null;
        _subDragOverlay = null;
        _subDragStartIndex = -1;
        _subDragTargetIndex = -1;
        _subDragItemWidth = 0;
        _subDragOverlayY = 0;
        _originalSubItemTransforms.Clear();
        _translatedSubItems.Clear();
    }

    private static SubItem? GetSubItemFromEventSource(object? source)
    {
        return (source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext as SubItem;
    }

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
        _dragPressedSelectedIndexIds = (ViewModel?.SelectedProfiles ?? [])
            .Where(profile => profile?.IndexId.IsNotEmpty() == true)
            .Select(profile => profile.IndexId)
            .Distinct()
            .ToList();

        if (ProfileDragDropSelection.ShouldSelectSourceItem(
            e.KeyModifiers.HasFlag(KeyModifiers.Control),
            e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            lstProfiles.SelectedItem = item;
        }
    }

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

    private async void LstProfiles_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isRowDragActive)
        {
            ResetDragState(false);
            return;
        }

        var targetItem = GetProfileItemAtPosition(e.GetPosition(lstProfiles))
            ?? GetProfileItemByIndex(_dragTargetIndex);

        var sourceIds = _dragSourceIndexIds.Count > 0
            ? _dragSourceIndexIds.ToList()
            : _dragSourceIndexId.IsNotEmpty()
                ? new List<string> { _dragSourceIndexId }
                : [];
        var moveCommitted = false;
        if (targetItem != null
            && _dragTargetIndex >= 0
            && sourceIds.Count > 0
            && !sourceIds.Contains(targetItem.IndexId))
        {
            moveCommitted = await (ViewModel?.MoveServersTo(sourceIds, targetItem) ?? Task.FromResult(false));
        }

        if (moveCommitted)
        {
            BeginCommittedDragCleanup();
            e.Pointer.Capture(null);
        }
        else
        {
            e.Pointer.Capture(null);
            ResetDragState(false);
        }
        e.Handled = true;
    }

    private void LstProfiles_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragReleaseCleanupPending)
        {
            return;
        }

        ResetDragState(false);
    }

    private void PrepareDragSourceSnapshot()
    {
        _dragSourceIndexIds.Clear();
        _dragSourceIndexes.Clear();

        if (_dragSourceItem == null || ViewModel?.ProfileItems == null)
        {
            return;
        }

        var sourceIds = ProfileDragDropSelection.GetDragSourceIds(
            ViewModel.ProfileItems.Select(item => item.IndexId).ToList(),
            _dragPressedSelectedIndexIds,
            _dragSourceItem.IndexId).ToHashSet();

        for (var i = 0; i < ViewModel.ProfileItems.Count; i++)
        {
            var item = ViewModel.ProfileItems[i];
            if (!sourceIds.Contains(item.IndexId))
            {
                continue;
            }

            _dragSourceIndexIds.Add(item.IndexId);
            _dragSourceIndexes.Add(i);
        }
    }

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

    private void SetSourceRowOpacity(DataGridRow row, double opacity)
    {
        if (!_originalRowOpacity.ContainsKey(row))
        {
            _originalRowOpacity[row] = row.Opacity;
        }

        row.Opacity = opacity;
    }

    private Border CreateDragOverlay(ProfileItemModel item, DataGridRow sourceRow)
    {
        const double textPadding = 8;
        var measurements = GetDragOverlayColumnMeasurements(item, sourceRow);
        var slots = ProfileDragDropAnimation.BuildOverlayColumnSlots(measurements, textPadding);
        var rowCanvas = new Canvas
        {
            Width = Math.Max(lstProfiles.Bounds.Width, sourceRow.Bounds.Width),
            Height = Math.Max(sourceRow.Bounds.Height, 24)
        };

        foreach (var slot in slots)
        {
            var textBlock = new TextBlock
            {
                Text = GetDragOverlayText(item, slot.ColumnTag),
                Width = slot.ContentWidth,
                Height = rowCanvas.Height,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Canvas.SetLeft(textBlock, slot.ContentLeft);
            Canvas.SetTop(textBlock, 0);
            rowCanvas.Children.Add(textBlock);
        }

        return new Border
        {
            Width = rowCanvas.Width,
            Height = rowCanvas.Height,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Child = rowCanvas,
            IsHitTestVisible = false,
            RenderTransform = new TranslateTransform()
        };
    }

    private IReadOnlyCollection<ProfileDragOverlayColumnMeasurement> GetDragOverlayColumnMeasurements(ProfileItemModel item, DataGridRow sourceRow)
    {
        var visibleColumns = lstProfiles.Columns
            .Where(column => column.IsVisible)
            .OrderBy(column => column.DisplayIndex)
            .ToList();
        if (visibleColumns.Count == 0)
        {
            return [];
        }

        var cells = sourceRow
            .GetVisualDescendants()
            .OfType<DataGridCell>()
            .Select(cell => new
            {
                Cell = cell,
                Position = cell.TranslatePoint(new Point(0, 0), profileDragOverlayLayer)
            })
            .Where(item => item.Position.HasValue && item.Cell.Bounds.Width > 0)
            .OrderBy(item => item.Position!.Value.X)
            .ToList();

        if (cells.Count >= visibleColumns.Count)
        {
            return cells
                .Take(visibleColumns.Count)
                .Select((cellInfo, index) =>
                {
                    var columnTag = visibleColumns[index].Tag?.ToString() ?? string.Empty;
                    var contentBounds = GetDragOverlayTextBounds(item, columnTag, cellInfo.Cell);
                    return new ProfileDragOverlayColumnMeasurement(
                        columnTag,
                        cellInfo.Position!.Value.X,
                        cellInfo.Cell.Bounds.Width,
                        contentBounds?.Left,
                        contentBounds?.Width);
                })
                .ToList();
        }

        var left = 0d;
        var measurements = new List<ProfileDragOverlayColumnMeasurement>();
        foreach (var column in visibleColumns)
        {
            var width = Math.Max(column.ActualWidth, 40);
            measurements.Add(new ProfileDragOverlayColumnMeasurement(column.Tag?.ToString() ?? string.Empty, left, width));
            left += width;
        }
        return measurements;
    }

    private (double Left, double Width)? GetDragOverlayTextBounds(ProfileItemModel item, string columnTag, DataGridCell cell)
    {
        var expectedText = GetDragOverlayText(item, columnTag);
        var textBlocks = cell
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(textBlock => textBlock.Text.IsNotEmpty())
            .ToList();
        if (textBlocks.Count == 0)
        {
            return null;
        }

        var textBlock = textBlocks.FirstOrDefault(textBlock => textBlock.Text == expectedText)
            ?? textBlocks.FirstOrDefault();
        var position = textBlock?.TranslatePoint(new Point(0, 0), profileDragOverlayLayer);
        if (textBlock == null || !position.HasValue || textBlock.Bounds.Width <= 0)
        {
            return null;
        }

        return (position.Value.X, textBlock.Bounds.Width);
    }

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

    private void UpdateDragOverlay(Point position)
    {
        if (_dragOverlay?.RenderTransform is not TranslateTransform transform)
        {
            return;
        }

        var overlayHeight = Math.Max(_dragOverlay.Bounds.Height, _dragRowHeight);
        var top = Math.Clamp(position.Y - overlayHeight / 2, 0, Math.Max(0, lstProfiles.Bounds.Height - overlayHeight));
        transform.X = 0;
        transform.Y = top;
    }

    private void UpdateDragTarget(Point position)
    {
        var item = GetProfileItemAtPosition(position);
        if (item == null)
        {
            var topEdgeTargetIndex = ProfileDragDropAnimation.GetTopEdgeTargetIndex(
                position.Y,
                _dragRowHeight,
                ViewModel?.ProfileItems.Count ?? 0);
            if (topEdgeTargetIndex >= 0)
            {
                _dragTargetIndex = topEdgeTargetIndex;
            }
            return;
        }

        var index = ViewModel?.ProfileItems.IndexOf(item) ?? -1;
        if (index >= 0)
        {
            _dragTargetIndex = index;
        }
    }

    private void UpdateShiftedRows()
    {
        foreach (var row in lstProfiles.GetVisualDescendants().OfType<DataGridRow>())
        {
            var item = row.DataContext as ProfileItemModel;
            var rowIndex = item == null ? -1 : ViewModel?.ProfileItems.IndexOf(item) ?? -1;
            var offset = _dragSourceIndexes.Count > 1
                ? ProfileDragDropAnimation.GetBlockRowOffset(rowIndex, _dragSourceIndexes, _dragTargetIndex, _dragRowHeight)
                : ProfileDragDropAnimation.GetRowOffset(rowIndex, _dragStartIndex, _dragTargetIndex, _dragRowHeight);

            var transform = EnsureRowTranslateTransform(row);
            transform.Y = offset;
        }
    }

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

    private ProfileItemModel? GetProfileItemByIndex(int index)
    {
        return ViewModel?.ProfileItems != null && index >= 0 && index < ViewModel.ProfileItems.Count
            ? ViewModel.ProfileItems[index]
            : null;
    }

    private void BeginCommittedDragCleanup()
    {
        _dragReleaseCleanupPending = true;
        _isRowDragActive = false;
        var cleanupVersion = ++_dragReleaseCleanupVersion;
        ResetCommittedDragStateAfterLayout(cleanupVersion);
    }

    private async void ResetCommittedDragStateAfterLayout(int cleanupVersion)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (cleanupVersion == _dragReleaseCleanupVersion)
            {
                ResetDragState(false, restoreOnlyCurrentProfileRows: true);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ResetDragState(false);
        }
        finally
        {
            if (cleanupVersion == _dragReleaseCleanupVersion)
            {
                _dragReleaseCleanupPending = false;
            }
        }
    }

    private void ResetDragState(bool keepSource, bool restoreOnlyCurrentProfileRows = false)
    {
        if (_dragOverlay != null)
        {
            profileDragOverlayLayer.Children.Remove(_dragOverlay);
        }

        foreach (var row in _translatedRows.Keys.ToList())
        {
            RestoreRowTransform(row);
        }

        var rowOpacitySnapshot = _originalRowOpacity.ToList();
        var currentProfiles = restoreOnlyCurrentProfileRows
            ? ViewModel?.ProfileItems
                .ToHashSet()
            : null;
        foreach (var (row, opacity) in rowOpacitySnapshot)
        {
            if (currentProfiles != null
                && (row.DataContext is not ProfileItemModel item || !currentProfiles.Contains(item)))
            {
                continue;
            }

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
        if (!keepSource)
        {
            _dragSourceIndexIds.Clear();
            _dragSourceIndexes.Clear();
            _dragPressedSelectedIndexIds.Clear();
        }
    }

    #endregion Drag and Drop
}
