namespace ServiceLib.ViewModels;

public class ProfilesViewModel : MyReactiveObject
{
    #region private prop

    private List<ProfileItem> _lstProfile;
    private string _serverFilter = string.Empty;
    private Dictionary<string, bool> _dicHeaderSort = new();
    private SpeedtestService? _speedtestService;
    private string? _pendingSelectIndexId;
    private sealed record TransferGroupSortState(string Column, bool Asc, bool Active);
    private readonly Dictionary<string, TransferGroupSortState> _transferGroupSortStates = new();
    private EFailoverMode _lastFailoverMode;
    private string? _lastActiveFailoverGroupId;
    private List<SubItem>? _subDragPreviewSnapshot;

    #endregion private prop

    #region ObservableCollection

    public IObservableCollection<ProfileItemModel> ProfileItems { get; } = new ObservableCollectionExtended<ProfileItemModel>();

    public IObservableCollection<SubItem> SubItems { get; } = new ObservableCollectionExtended<SubItem>();
    public IObservableCollection<SubItem> NormalSubItems { get; } = new ObservableCollectionExtended<SubItem>();
    public IObservableCollection<SubItem> FailoverSubItems { get; } = new ObservableCollectionExtended<SubItem>();

    [Reactive]
    public ProfileItemModel SelectedProfile { get; set; }

    public IList<ProfileItemModel> SelectedProfiles { get; set; }

    [Reactive]
    public SubItem SelectedSub { get; set; }

    [Reactive]
    public SubItem SelectedMoveToGroup { get; set; }

    [Reactive]
    public string ServerFilter { get; set; }

    [Reactive]
    public bool CurrentGroupIsFailoverGroup { get; set; }

    [Reactive]
    public bool CurrentGroupIsNormalGroup { get; set; } = true;

    [Reactive]
    public bool HasFailoverGroups { get; set; }

    [Reactive]
    public bool CanSetActiveFailoverGroup { get; set; }

    [Reactive]
    public string ActiveFailoverGroupButtonText { get; set; } = ResUI.TbSetActiveFailoverGroup;

    [Reactive]
    public bool ShowSetActiveFailoverGroupIcon { get; set; } = true;

    [Reactive]
    public bool ShowActiveFailoverGroupSetIcon { get; set; }

    #endregion ObservableCollection

    #region Menu

    //servers delete
    public ReactiveCommand<Unit, Unit> EditServerCmd { get; }

    public ReactiveCommand<Unit, Unit> RemoveServerCmd { get; }
    public ReactiveCommand<Unit, Unit> RemoveDuplicateServerCmd { get; }
    public ReactiveCommand<Unit, Unit> CopyServerCmd { get; }
    public ReactiveCommand<Unit, Unit> SetDefaultServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddToFailoverQueueCmd { get; }
    public ReactiveCommand<Unit, Unit> RemoveFromFailoverQueueCmd { get; }
    public ReactiveCommand<Unit, Unit> ShareServerCmd { get; }
    public ReactiveCommand<Unit, Unit> GenGroupAllServerCmd { get; }
    public ReactiveCommand<Unit, Unit> GenGroupRegionServerCmd { get; }

    //servers move
    public ReactiveCommand<Unit, Unit> MoveTopCmd { get; }

    public ReactiveCommand<Unit, Unit> MoveUpCmd { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCmd { get; }
    public ReactiveCommand<Unit, Unit> MoveBottomCmd { get; }
    public ReactiveCommand<SubItem, Unit> MoveToGroupCmd { get; }
    public ReactiveCommand<SubItem, Unit> CopyToFailoverGroupCmd { get; }

    //servers ping
    public ReactiveCommand<Unit, Unit> MixedTestServerCmd { get; }

    public ReactiveCommand<Unit, Unit> TcpingServerCmd { get; }
    public ReactiveCommand<Unit, Unit> RealPingServerCmd { get; }
    public ReactiveCommand<Unit, Unit> SpeedServerCmd { get; }
    public ReactiveCommand<Unit, Unit> SortServerResultCmd { get; }
    public ReactiveCommand<Unit, Unit> RemoveInvalidServerResultCmd { get; }
    public ReactiveCommand<Unit, Unit> FastRealPingCmd { get; }

    //servers export
    public ReactiveCommand<Unit, Unit> Export2ClientConfigCmd { get; }

    public ReactiveCommand<Unit, Unit> Export2ClientConfigClipboardCmd { get; }
    public ReactiveCommand<Unit, Unit> Export2ShareUrlCmd { get; }
    public ReactiveCommand<Unit, Unit> Export2ShareUrlBase64Cmd { get; }

    public ReactiveCommand<Unit, Unit> AddSubCmd { get; }
    public ReactiveCommand<Unit, Unit> EditSubCmd { get; }
    public ReactiveCommand<Unit, Unit> DeleteSubCmd { get; }
    public ReactiveCommand<Unit, Unit> SetActiveFailoverGroupCmd { get; }

    #endregion Menu

    #region Init

    public ProfilesViewModel(Func<EViewAction, object?, Task<bool>>? updateView)
    {
        _config = AppManager.Instance.Config;
        _updateView = updateView;
        _lastFailoverMode = _config.FailoverMode;
        _lastActiveFailoverGroupId = _config.ActiveFailoverGroupId;

        #region WhenAnyValue && ReactiveCommand

        var canEditRemove = this.WhenAnyValue(
           x => x.SelectedProfile,
           selectedSource => selectedSource != null && !selectedSource.IndexId.IsNullOrEmpty());

        this.WhenAnyValue(
            x => x.SelectedSub,
            y => y != null && !y.Remarks.IsNullOrEmpty() && _config.SubIndexId != y.Id)
                .Subscribe(async c => await SubSelectedChangedAsync(c));
        this.WhenAnyValue(
             x => x.SelectedMoveToGroup,
             y => y != null && !y.Remarks.IsNullOrEmpty())
                 .Subscribe(async c => await MoveToGroup(c));

        this.WhenAnyValue(
          x => x.ServerFilter,
          y => y != null && _serverFilter != y)
              .Subscribe(async c => await ServerFilterChanged(c));

        //servers delete
        EditServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await EditServerAsync();
        }, canEditRemove);
        RemoveServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RemoveServerAsync();
        }, canEditRemove);
        RemoveDuplicateServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RemoveDuplicateServer();
        });
        CopyServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await CopyServer();
        }, canEditRemove);
        SetDefaultServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SetDefaultServer();
        }, canEditRemove);
        AddToFailoverQueueCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddSelectedToFailoverQueue();
        }, canEditRemove);
        RemoveFromFailoverQueueCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RemoveSelectedFromFailoverQueue();
        }, canEditRemove);
        ShareServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ShareServerAsync();
        }, canEditRemove);
        GenGroupAllServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await GenGroupAllServer();
        }, canEditRemove);
        GenGroupRegionServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await GenGroupRegionServer();
        }, canEditRemove);

        //servers move
        MoveTopCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await MoveServer(EMove.Top);
        }, canEditRemove);
        MoveUpCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await MoveServer(EMove.Up);
        }, canEditRemove);
        MoveDownCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await MoveServer(EMove.Down);
        }, canEditRemove);
        MoveBottomCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await MoveServer(EMove.Bottom);
        }, canEditRemove);
        MoveToGroupCmd = ReactiveCommand.CreateFromTask<SubItem>(async sub =>
        {
            SelectedMoveToGroup = sub;
        });
        CopyToFailoverGroupCmd = ReactiveCommand.CreateFromTask<SubItem>(async sub =>
        {
            await CopySelectedToFailoverGroup(sub);
        });

        //servers ping
        FastRealPingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ServerSpeedtest(ESpeedActionType.FastRealping);
        });
        MixedTestServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ServerSpeedtest(ESpeedActionType.Mixedtest);
        });
        TcpingServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ServerSpeedtest(ESpeedActionType.Tcping);
        }, canEditRemove);
        RealPingServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ServerSpeedtest(ESpeedActionType.Realping);
        }, canEditRemove);
        SpeedServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ServerSpeedtest(ESpeedActionType.Speedtest);
        }, canEditRemove);
        SortServerResultCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SortServer(EServerColName.DelayVal.ToString());
        });
        RemoveInvalidServerResultCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RemoveInvalidServerResult();
        });
        //servers export
        Export2ClientConfigCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await Export2ClientConfigAsync(false);
        }, canEditRemove);
        Export2ClientConfigClipboardCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await Export2ClientConfigAsync(true);
        }, canEditRemove);
        Export2ShareUrlCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await Export2ShareUrlAsync(false);
        }, canEditRemove);
        Export2ShareUrlBase64Cmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await Export2ShareUrlAsync(true);
        }, canEditRemove);

        //Subscription
        AddSubCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await EditSubAsync(true);
        });
        EditSubCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await EditSubAsync(false);
        });
        DeleteSubCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await DeleteSubAsync();
        });
        SetActiveFailoverGroupCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SetActiveFailoverGroup();
        });

        #endregion WhenAnyValue && ReactiveCommand

        #region AppEvents

        AppEvents.ProfilesRefreshRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await RefreshServersBiz());

        AppEvents.SubscriptionsRefreshRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                await RefreshSubscriptions();
                await RefreshServersBiz();
            });

        AppEvents.DispatcherStatisticsRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async result => await UpdateStatistics(result));

        AppEvents.SpeedTestResultRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async result => await SetSpeedTestResult(result));

        AppEvents.SetDefaultServerRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async indexId => await SetDefaultServer(indexId));

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
                else if (CurrentGroupIsNormalGroup
                    && ShouldRefreshNormalGroupForPendingActiveStateChange(oldMode, newMode))
                {
                    await RefreshServersBiz();
                }
            });

        AppEvents.FailoverHealthChangedRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await RefreshServersBiz());

        #endregion AppEvents

        _ = Init();
    }

    private async Task Init()
    {
        SelectedProfile = new();
        SelectedSub = new();
        SelectedMoveToGroup = new();

        await RefreshSubscriptions();
        //await RefreshServers();
    }

    #endregion Init

    #region Actions

    private void Reload()
    {
        AppEvents.ReloadRequested.Publish();
    }

    public async Task SetSpeedTestResult(SpeedTestResult result)
    {
        if (result.IndexId.IsNullOrEmpty())
        {
            NoticeManager.Instance.SendMessageEx(result.Delay);
            NoticeManager.Instance.Enqueue(result.Delay);
            return;
        }
        var item = ProfileItems.FirstOrDefault(it => it.IndexId == result.IndexId);
        if (item == null)
        {
            return;
        }

        if (result.Delay.IsNotEmpty())
        {
            item.Delay = result.Delay.ToInt();
            item.DelayVal = result.Delay ?? string.Empty;
            ApplyFailoverCompletedDelayDisplay(item);
        }
        if (result.Speed.IsNotEmpty())
        {
            item.SpeedVal = result.Speed ?? string.Empty;
        }
        await Task.CompletedTask;
    }

    public async Task UpdateStatistics(ServerSpeedItem update)
    {
        if (!_config.GuiItem.EnableStatistics
            || (update.ProxyUp + update.ProxyDown) <= 0
            || DateTime.Now.Second % 3 != 0)
        {
            return;
        }

        try
        {
            var item = ProfileItems.FirstOrDefault(it => it.IndexId == update.IndexId);
            if (item != null)
            {
                item.TodayDown = Utils.HumanFy(update.TodayDown);
                item.TodayUp = Utils.HumanFy(update.TodayUp);
                item.TotalDown = Utils.HumanFy(update.TotalDown);
                item.TotalUp = Utils.HumanFy(update.TotalUp);
            }
        }
        catch
        {
        }
        await Task.CompletedTask;
    }

    #endregion Actions

    #region Servers && Groups

    private async Task SubSelectedChangedAsync(bool c)
    {
        if (!c)
        {
            return;
        }
        _config.SubIndexId = SelectedSub?.Id;
        UpdateFailoverGroupState();

        await RefreshServers();

        await _updateView?.Invoke(EViewAction.ProfilesFocus, null);
    }

    private async Task ServerFilterChanged(bool c)
    {
        if (!c)
        {
            return;
        }
        _serverFilter = ServerFilter;
        if (_serverFilter.IsNullOrEmpty())
        {
            await RefreshServers();
        }
    }

    public async Task RefreshServers()
    {
        AppEvents.ProfilesRefreshRequested.Publish();

        await Task.Delay(200);
    }

    private async Task RefreshServersBiz()
    {
        var lstModel = await GetProfileItemsEx(_config.SubIndexId, _serverFilter);
        _lstProfile = JsonUtils.Deserialize<List<ProfileItem>>(JsonUtils.Serialize(lstModel)) ?? [];

        ProfileItems.Clear();
        ProfileItems.AddRange(lstModel);
        if (lstModel.Count > 0)
        {
            ProfileItemModel? selected = null;
            if (!_pendingSelectIndexId.IsNullOrEmpty())
            {
                selected = lstModel.FirstOrDefault(t => t.IndexId == _pendingSelectIndexId);
                _pendingSelectIndexId = null;
            }
            if (CurrentGroupIsFailoverGroup
                && _config.FailoverMode != EFailoverMode.Off
                && _config.ActiveFailoverGroupId == _config.SubIndexId)
            {
                selected ??= lstModel.FirstOrDefault(t => t.FailoverPriority == 1);
            }
            selected ??= lstModel.FirstOrDefault(t => t.IndexId == _config.IndexId);
            SelectedProfile = selected ?? lstModel.First();
        }

        await _updateView?.Invoke(EViewAction.DispatcherRefreshServersBiz, null);
    }

    private async Task RefreshSubscriptions()
    {
        SubItems.Clear();
        NormalSubItems.Clear();
        FailoverSubItems.Clear();

        var allGroup = new SubItem { Remarks = ResUI.AllGroupServers };
        SubItems.Add(allGroup);
        NormalSubItems.Add(allGroup);

        foreach (var item in await AppManager.Instance.SubItems())
        {
            SubItems.Add(item);
            if (item.IsFailoverGroup)
            {
                FailoverSubItems.Add(item);
            }
            else
            {
                NormalSubItems.Add(item);
            }
        }
        HasFailoverGroups = FailoverSubItems.Count > 0;
        SelectedSub = (_config.SubIndexId.IsNotEmpty()
                        ? SubItems.FirstOrDefault(t => t.Id == _config.SubIndexId)
                        : null) ?? SubItems.LastOrDefault();
        UpdateFailoverGroupState();
    }

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

    public bool BeginSubDragPreview(string? sourceId)
    {
        if (sourceId.IsNullOrEmpty() || SubItems.All(item => item.Id != sourceId))
        {
            return false;
        }

        _subDragPreviewSnapshot = SubItems.ToList();
        return true;
    }

    public void MoveSubDragPreviewTo(string? sourceId, int insertIndex)
    {
        if (sourceId.IsNullOrEmpty())
        {
            return;
        }

        var sourceItem = SubItems.FirstOrDefault(item => item.Id == sourceId);
        if (sourceItem == null)
        {
            return;
        }

        SubItems.Remove(sourceItem);
        var clampedIndex = Math.Clamp(insertIndex, 1, SubItems.Count);
        SubItems.Insert(clampedIndex, sourceItem);
    }

    public void CancelSubDragPreview()
    {
        if (_subDragPreviewSnapshot == null)
        {
            return;
        }

        SubItems.Clear();
        SubItems.AddRange(_subDragPreviewSnapshot);
        _subDragPreviewSnapshot = null;
    }

    public async Task CommitSubDragPreview()
    {
        if (_subDragPreviewSnapshot == null)
        {
            return;
        }

        var selectedId = SelectedSub?.Id;
        var orderedIds = SubItems
            .Where(item => item.Id.IsNotEmpty())
            .Select(item => item.Id)
            .ToList();
        _subDragPreviewSnapshot = null;

        if (await ConfigHandler.SaveSubItemOrder(orderedIds) == 0)
        {
            await RefreshSubscriptions();
            SelectedSub = selectedId.IsNotEmpty()
                ? SubItems.FirstOrDefault(item => item.Id == selectedId) ?? SelectedSub
                : SelectedSub;
        }
    }

    private async Task<List<ProfileItemModel>?> GetProfileItemsEx(string subid, string filter)
    {
        var isFailoverGroup = SelectedSub?.IsFailoverGroup == true;
        var isActiveFailoverGroup = isFailoverGroup && _config.ActiveFailoverGroupId == subid;
        var failoverMode = isActiveFailoverGroup ? _config.FailoverMode : EFailoverMode.Failover;
        var isActiveLeastDelay = isActiveFailoverGroup && failoverMode == EFailoverMode.LeastDelay;
        var lstModel = isFailoverGroup
            ? await GetFailoverProfileModels(subid, failoverMode)
            : await AppManager.Instance.ProfileModels(_config.SubIndexId, filter);

        if (!isFailoverGroup)
        {
            await ConfigHandler.SetDefaultServer(_config, lstModel);
        }

        var lstServerStat = (_config.GuiItem.EnableStatistics ? StatisticsManager.Instance.ServerStat : null) ?? [];
        var lstProfileExs = await ProfileExManager.Instance.GetProfileExs();
        var priorityMap = isFailoverGroup ? await FailoverGroupManager.GetQueuePriorityMap(subid, failoverMode) : [];
        var failoverEntries = isFailoverGroup
            ? await FailoverGroupManager.GetQueueEntries(subid, false)
            : [];
        var failoverEntryMap = failoverEntries.ToDictionary(entry => entry.Profile.IndexId, entry => entry.Item);
        var relayActiveProfileId = FailoverRelayManager.Instance.CurrentActiveProfileId;
        var activeFailoverTargetProfileId = isActiveFailoverGroup
            ? ResolveFailoverDisplayActiveProfileId(
                relayActiveProfileId.IsNotEmpty()
                    ? relayActiveProfileId
                    : GetFailoverDisplayTargetProfileId(_config, AppManager.Instance.RunningFailoverTargetProfileId),
                failoverEntries)
            : null;
        lstModel = (from t in lstModel
                    join t2 in lstServerStat on t.IndexId equals t2.IndexId into t2b
                    from t22 in t2b.DefaultIfEmpty()
                    join t3 in lstProfileExs on t.IndexId equals t3.IndexId into t3b
                    from t33 in t3b.DefaultIfEmpty()
                    let isOriginalActive = !isFailoverGroup && t.IndexId == _config.IndexId
                    let showPendingActive = ShouldShowPendingActiveLabel(
                        isFailoverGroup,
                        isOriginalActive,
                        _config.FailoverMode)
                    select new ProfileItemModel
                    {
                        IndexId = t.IndexId,
                        ConfigType = t.ConfigType,
                        Remarks = t.Remarks,
                        Address = t.Address,
                        Port = t.Port,
                        //Security = t.Security,
                        Network = t.Network,
                        StreamSecurity = t.StreamSecurity,
                        Subid = t.Subid,
                        SubRemarks = t.SubRemarks,
                        IsActive = isOriginalActive && !showPendingActive,
                        ShowPendingActiveLabel = showPendingActive,
                        Sort = t33?.Sort ?? 0,
                        Delay = t33?.Delay ?? 0,
                        Speed = t33?.Speed ?? 0,
                        DelayVal = t33?.Delay != 0 ? $"{t33?.Delay}" : string.Empty,
                        SpeedVal = t33?.Speed > 0 ? $"{t33?.Speed}" : t33?.Message ?? string.Empty,
                        TodayDown = t22 == null ? "" : Utils.HumanFy(t22.TodayDown),
                        TodayUp = t22 == null ? "" : Utils.HumanFy(t22.TodayUp),
                        TotalDown = t22 == null ? "" : Utils.HumanFy(t22.TotalDown),
                        TotalUp = t22 == null ? "" : Utils.HumanFy(t22.TotalUp),
                        TodayDownValue = t22?.TodayDown ?? 0,
                        TodayUpValue = t22?.TodayUp ?? 0,
                        TotalDownValue = t22?.TotalDown ?? 0,
                        TotalUpValue = t22?.TotalUp ?? 0,
                        IsFailoverGroupNode = isFailoverGroup,
                        IsInFailoverQueue = priorityMap.ContainsKey(t.IndexId),
                        FailoverPriority = priorityMap.GetValueOrDefault(t.IndexId),
                        FailoverPriorityLabel = priorityMap.TryGetValue(t.IndexId, out var priority) ? $"P{priority}" : string.Empty,
                        ShowFailoverPriorityLabel = ShouldShowFailoverPriorityLabel(
                            priorityMap.ContainsKey(t.IndexId),
                            isActiveLeastDelay),
                        FailoverHealthStatus = failoverEntryMap.TryGetValue(t.IndexId, out var healthItem)
                            ? NormalizeFailoverHealthStatus(healthItem.LastStatus)
                            : FailoverHealthStatus.Unknown,
                        ShowFailoverHealthLabel = isFailoverGroup && priorityMap.ContainsKey(t.IndexId),
                        IsCurrentFailoverPreferred = isFailoverGroup
                            && _config.FailoverMode != EFailoverMode.Off
                            && _config.ActiveFailoverGroupId == subid
                            && activeFailoverTargetProfileId == t.IndexId,
                    })
                    .ToList();
        var transferGroupSortActive = isFailoverGroup
            && _transferGroupSortStates.TryGetValue(subid, out var activeSortState)
            && activeSortState.Active;
        var pinFailoverQueue = ShouldPinTransferGroupPriorityQueue(
            isFailoverGroup,
            isActiveFailoverGroup,
            failoverMode,
            isActiveLeastDelay,
            transferGroupSortActive);
        lstModel = OrderFailoverProfileModelsForDisplay(
            lstModel,
            isFailoverGroup,
            isActiveFailoverGroup,
            activeFailoverTargetProfileId,
            pinFailoverQueue);

        if (isFailoverGroup
            && _transferGroupSortStates.TryGetValue(subid, out var sortState)
            && sortState.Active)
        {
            lstModel = ApplyTransferGroupDisplaySort(
                lstModel,
                pinFailoverQueue,
                sortState.Column,
                sortState.Asc);
        }

        foreach (var item in lstModel)
        {
            ApplyFailoverHealthDisplay(item);
            ApplyFailoverSpeedTestDisplay(item);
        }

        return lstModel;
    }

    public static List<ProfileItemModel> ApplyTransferGroupDisplaySort(
        IEnumerable<ProfileItemModel>? items,
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
        queueItems = ConfigHandler.SortProfileDisplayItemsForHeader(queueItems, colName, asc);
        var candidateItems = source.Where(item => !item.IsInFailoverQueue).ToList();
        candidateItems = ConfigHandler.SortProfileDisplayItemsForHeader(candidateItems, colName, asc);

        return queueItems.Concat(candidateItems).ToList();
    }

    public static bool ShouldPinTransferGroupPriorityQueue(
        bool isFailoverGroup,
        bool isActiveFailoverGroup,
        EFailoverMode failoverMode,
        bool isActiveLeastDelay,
        bool sortActive)
    {
        return isFailoverGroup
            && !isActiveLeastDelay
            && (sortActive || !isActiveFailoverGroup || failoverMode != EFailoverMode.Off);
    }

    public static bool ShouldPinTransferGroupQueue(bool isActiveFailoverGroup, EFailoverMode failoverMode)
    {
        return isActiveFailoverGroup && failoverMode != EFailoverMode.Off;
    }

    public static bool CanMoveTransferGroupItem(bool pinFailoverQueue, bool sourceInQueue, bool targetInQueue)
    {
        return sourceInQueue == targetInQueue;
    }

    public static bool CanMoveTransferGroupItems(bool pinFailoverQueue, IReadOnlyCollection<bool> sourceInQueueStates, bool targetInQueue)
    {
        if (sourceInQueueStates.Count == 0 || sourceInQueueStates.Distinct().Count() != 1)
        {
            return false;
        }

        return sourceInQueueStates.First() == targetInQueue;
    }

    public static bool ShouldPromoteFailoverQueueFirstAfterDrag(Config config, string? currentGroupId, string? firstQueueProfileId)
    {
        return config.FailoverMode == EFailoverMode.Failover
            && currentGroupId.IsNotEmpty()
            && config.ActiveFailoverGroupId == currentGroupId
            && firstQueueProfileId.IsNotEmpty();
    }

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

    public static void ApplyFailoverCompletedDelayDisplay(ProfileItemModel item)
    {
        if (!item.ShowFailoverHealthLabel
            || item.FailoverHealthStatus != FailoverHealthStatus.Probing
            || item.Delay <= 0)
        {
            return;
        }

        item.FailoverHealthStatus = FailoverHealthStatus.Normal;
        ApplyFailoverHealthDisplay(item);
    }

    public static void ApplyFailoverSpeedTestDisplay(ProfileItemModel item)
    {
        if (!item.ShowFailoverHealthLabel || item.FailoverHealthStatus != FailoverHealthStatus.Probing)
        {
            return;
        }

        item.Delay = 0;
        item.DelayVal = ResUI.Speedtesting;
    }

    public static void ApplyFailoverHealthDisplay(ProfileItemModel item)
    {
        item.FailoverHealthStatus = NormalizeFailoverHealthStatus(item.FailoverHealthStatus);
        item.FailoverHealthLabel = item.FailoverHealthStatus switch
        {
            FailoverHealthStatus.Normal => ResUI.TbFailoverHealthNormal,
            FailoverHealthStatus.Failed => ResUI.TbFailoverHealthFailed,
            FailoverHealthStatus.Probing => ResUI.TbFailoverHealthProbing,
            FailoverHealthStatus.Requesting => ResUI.TbFailoverHealthRequesting,
            FailoverHealthStatus.Degraded => ResUI.TbFailoverHealthDegraded,
            FailoverHealthStatus.Fallback => ResUI.TbFailoverHealthFallback,
            _ => ResUI.TbFailoverHealthUnknown,
        };
        item.ShowCurrentFailoverActiveLabel = item.ShowFailoverHealthLabel
            && item.IsCurrentFailoverPreferred
            && item.FailoverHealthStatus != FailoverHealthStatus.Failed
            && item.FailoverHealthStatus != FailoverHealthStatus.Requesting
            && item.FailoverHealthStatus != FailoverHealthStatus.Degraded;
        item.IsFailoverHealthNormal = item.ShowFailoverHealthLabel
            && item.FailoverHealthStatus == FailoverHealthStatus.Normal
            && !item.ShowCurrentFailoverActiveLabel;
        item.IsFailoverHealthFailed = item.ShowFailoverHealthLabel
            && item.FailoverHealthStatus == FailoverHealthStatus.Failed
            && !item.ShowCurrentFailoverActiveLabel;
        item.IsFailoverHealthProbing = item.ShowFailoverHealthLabel
            && item.FailoverHealthStatus == FailoverHealthStatus.Probing
            && !item.ShowCurrentFailoverActiveLabel;
        item.IsFailoverHealthRequesting = item.ShowFailoverHealthLabel
            && item.FailoverHealthStatus == FailoverHealthStatus.Requesting
            && !item.ShowCurrentFailoverActiveLabel;
        item.IsFailoverHealthDegraded = item.ShowFailoverHealthLabel
            && item.FailoverHealthStatus == FailoverHealthStatus.Degraded
            && !item.ShowCurrentFailoverActiveLabel;
        item.IsFailoverHealthFallback = item.ShowFailoverHealthLabel
            && item.FailoverHealthStatus == FailoverHealthStatus.Fallback
            && !item.ShowCurrentFailoverActiveLabel;
        item.IsFailoverHealthUnknown = item.ShowFailoverHealthLabel
            && item.FailoverHealthStatus == FailoverHealthStatus.Unknown
            && !item.ShowCurrentFailoverActiveLabel;
    }

    public static bool ShouldShowFailoverPriorityLabel(bool isInQueue, bool isActiveLeastDelay)
    {
        return isInQueue && !isActiveLeastDelay;
    }

    public static bool ShouldShowPendingActiveLabel(
        bool isFailoverGroup,
        bool isOriginalActive,
        EFailoverMode failoverMode)
    {
        return !isFailoverGroup
            && isOriginalActive
            && failoverMode != EFailoverMode.Off;
    }

    public static bool ShouldRefreshNormalGroupForPendingActiveStateChange(
        EFailoverMode oldMode,
        EFailoverMode newMode)
    {
        return (oldMode == EFailoverMode.Off) != (newMode == EFailoverMode.Off);
    }

    public static List<ProfileItemModel> OrderFailoverProfileModelsForDisplay(
        IEnumerable<ProfileItemModel> items,
        bool isFailoverGroup,
        bool isActiveFailoverGroup,
        string? activeFailoverTargetProfileId,
        bool pinFailoverQueue = true)
    {
        var source = items.ToList();
        if (!isFailoverGroup || !pinFailoverQueue)
        {
            if (isFailoverGroup && isActiveFailoverGroup && activeFailoverTargetProfileId.IsNotEmpty())
            {
                return source
                    .OrderBy(t => activeFailoverTargetProfileId == t.IndexId ? 0 : 1)
                    .ThenBy(t => t.IsInFailoverQueue ? 0 : 1)
                    .ThenBy(t => t.Sort)
                    .ToList();
            }

            return source
                .OrderBy(t => isActiveFailoverGroup && activeFailoverTargetProfileId == t.IndexId ? 0 : 1)
                .ThenBy(t => t.Sort)
                .ToList();
        }

        return source
            .OrderBy(t => t.IsInFailoverQueue ? 0 : 1)
            .ThenBy(t => t.IsInFailoverQueue ? t.FailoverPriority : 0)
            .ThenBy(t => !t.IsInFailoverQueue && isActiveFailoverGroup && activeFailoverTargetProfileId == t.IndexId ? 0 : 1)
            .ThenBy(t => t.Sort)
            .ToList();
    }

    public static string? GetFailoverDisplayTargetProfileId(Config config, string? runningFailoverTargetProfileId)
    {
        if (config.FailoverMode == EFailoverMode.Off || config.ActiveFailoverGroupId.IsNullOrEmpty())
        {
            return null;
        }

        if (config.FailoverStartupProfileId.IsNotEmpty())
        {
            return config.FailoverStartupProfileId;
        }

        return runningFailoverTargetProfileId;
    }

    public static string? ResolveFailoverDisplayActiveProfileId(
        string? currentTargetProfileId,
        IEnumerable<FailoverQueueEntry>? queueEntries)
    {
        if (currentTargetProfileId.IsNullOrEmpty())
        {
            return null;
        }

        var entries = queueEntries?
            .Where(entry => entry.Item.Enabled)
            .ToList() ?? [];
        var currentTarget = entries.FirstOrDefault(entry => entry.Profile.IndexId == currentTargetProfileId);
        if (currentTarget?.Item.LastStatus != FailoverHealthStatus.Failed)
        {
            return currentTargetProfileId;
        }

        return entries
            .FirstOrDefault(entry => entry.Item.LastStatus == FailoverHealthStatus.Normal)
            ?.Profile
            .IndexId;
    }

    public static bool IsSelectedActiveFailoverGroup(SubItem? selectedSub, string? activeFailoverGroupId)
    {
        return selectedSub?.IsFailoverGroup == true
            && !string.IsNullOrEmpty(selectedSub.Id)
            && selectedSub.Id == activeFailoverGroupId;
    }

    private static string NormalizeFailoverHealthStatus(string? status)
    {
        return status switch
        {
            FailoverHealthStatus.Normal => FailoverHealthStatus.Normal,
            FailoverHealthStatus.Failed => FailoverHealthStatus.Failed,
            FailoverHealthStatus.Probing => FailoverHealthStatus.Probing,
            FailoverHealthStatus.Requesting => FailoverHealthStatus.Requesting,
            FailoverHealthStatus.Degraded => FailoverHealthStatus.Degraded,
            FailoverHealthStatus.Fallback => FailoverHealthStatus.Fallback,
            _ => FailoverHealthStatus.Unknown,
        };
    }

    private async Task<List<ProfileItemModel>> GetFailoverProfileModels(string subid, EFailoverMode mode)
    {
        var profiles = await FailoverGroupManager.GetDisplayProfiles(subid, mode);
        var subMap = SubItems
            .Where(item => item.Id.IsNotEmpty())
            .ToDictionary(item => item.Id, item => item.Remarks);

        return profiles.Select(profile => new ProfileItemModel
        {
            IndexId = profile.IndexId,
            ConfigType = profile.ConfigType,
            Remarks = profile.Remarks,
            Address = profile.Address,
            Port = profile.Port,
            Network = profile.Network,
            StreamSecurity = profile.StreamSecurity,
            Subid = profile.Subid,
            SubRemarks = subMap.GetValueOrDefault(profile.Subid, profile.Subid),
        }).ToList();
    }

    private void UpdateFailoverGroupState()
    {
        CurrentGroupIsFailoverGroup = SelectedSub?.IsFailoverGroup == true;
        CurrentGroupIsNormalGroup = !CurrentGroupIsFailoverGroup;
        CanSetActiveFailoverGroup = CurrentGroupIsFailoverGroup;
        ShowActiveFailoverGroupSetIcon = IsSelectedActiveFailoverGroup(SelectedSub, _config.ActiveFailoverGroupId);
        ShowSetActiveFailoverGroupIcon = !ShowActiveFailoverGroupSetIcon;
        ActiveFailoverGroupButtonText = ShowActiveFailoverGroupSetIcon
            ? ResUI.TbActiveFailoverGroupSet
            : ResUI.TbSetActiveFailoverGroup;
    }

    #endregion Servers && Groups

    #region Add Servers

    private async Task<List<ProfileItem>?> GetProfileItems(bool latest)
    {
        var lstSelected = new List<ProfileItem>();
        if (SelectedProfiles == null || SelectedProfiles.Count <= 0)
        {
            return null;
        }

        var orderProfiles = SelectedProfiles?.OrderBy(t => t.Sort);
        if (latest)
        {
            lstSelected.AddRange(await AppManager.Instance.GetProfileItemsOrderedByIndexIds(orderProfiles.Select(sp => sp?.IndexId)));
        }
        else
        {
            lstSelected = JsonUtils.Deserialize<List<ProfileItem>>(JsonUtils.Serialize(orderProfiles));
        }

        return lstSelected;
    }

    public async Task EditServerAsync()
    {
        if (string.IsNullOrEmpty(SelectedProfile?.IndexId))
        {
            return;
        }
        var item = await AppManager.Instance.GetProfileItem(SelectedProfile.IndexId);
        if (item is null)
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectServer);
            return;
        }
        var eConfigType = item.ConfigType;

        bool? ret = false;
        if (eConfigType == EConfigType.Custom)
        {
            ret = await _updateView?.Invoke(EViewAction.AddServer2Window, item);
        }
        else if (eConfigType.IsGroupType())
        {
            ret = await _updateView?.Invoke(EViewAction.AddGroupServerWindow, item);
        }
        else
        {
            ret = await _updateView?.Invoke(EViewAction.AddServerWindow, item);
        }
        if (ret == true)
        {
            await RefreshServers();
            if (item.IndexId == _config.IndexId)
            {
                Reload();
            }
        }
    }

    public async Task RemoveServerAsync()
    {
        var lstSelected = await GetProfileItems(true);
        if (lstSelected == null)
        {
            return;
        }
        if (await _updateView?.Invoke(EViewAction.ShowYesNo, null) == false)
        {
            return;
        }
        var exists = lstSelected.Exists(t => t.IndexId == _config.IndexId);

        await ConfigHandler.RemoveServers(_config, lstSelected);
        NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
        if (lstSelected.Count == ProfileItems.Count)
        {
            ProfileItems.Clear();
        }
        await RefreshServers();
        if (exists)
        {
            Reload();
        }
    }

    private async Task RemoveDuplicateServer()
    {
        if (await _updateView?.Invoke(EViewAction.ShowYesNo, null) == false)
        {
            return;
        }

        var tuple = await ConfigHandler.DedupServerList(_config, _config.SubIndexId);
        if (tuple.Item1 > 0 || tuple.Item2 > 0)
        {
            await RefreshServers();
            Reload();
        }
        NoticeManager.Instance.Enqueue(string.Format(ResUI.RemoveDuplicateServerResult, tuple.Item1, tuple.Item2));
    }

    private async Task CopyServer()
    {
        var lstSelected = await GetProfileItems(false);
        if (lstSelected == null)
        {
            return;
        }
        if (await ConfigHandler.CopyServer(_config, lstSelected) == 0)
        {
            await RefreshServers();
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
        }
    }

    private async Task CopySelectedToFailoverGroup(SubItem sub)
    {
        if (sub is not { IsFailoverGroup: true } || CurrentGroupIsFailoverGroup)
        {
            return;
        }

        var lstSelected = await GetProfileItems(true);
        if (lstSelected == null)
        {
            return;
        }

        if (await FailoverGroupManager.CopyToFailoverGroup(sub.Id, lstSelected) == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
            AppEvents.FailoverStateChangedRequested.Publish();
        }
        else
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
        }
    }

    private async Task AddSelectedToFailoverQueue()
    {
        if (!CurrentGroupIsFailoverGroup || _config.SubIndexId.IsNullOrEmpty())
        {
            return;
        }

        var ids = (SelectedProfiles?.Count > 0 ? SelectedProfiles : [SelectedProfile])
            .Where(item => item?.IndexId.IsNotEmpty() == true)
            .Select(item => item.IndexId)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        if (await FailoverGroupManager.AddToQueue(_config.SubIndexId, ids) == 0)
        {
            var orderedQueueIds = GetQueueOrderForAddToFailoverQueue(ProfileItems, ids);
            if (orderedQueueIds.Count > 0)
            {
                await FailoverGroupManager.ReorderQueueItems(_config.SubIndexId, orderedQueueIds);
            }

            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
            await RefreshServers();
            AppEvents.FailoverStateChangedRequested.Publish();
        }
    }

    public static List<string> GetQueueOrderForAddToFailoverQueue(
        IEnumerable<ProfileItemModel>? displayItems,
        IEnumerable<string>? addingProfileIds)
    {
        var addingIds = (addingProfileIds ?? [])
            .Where(id => id.IsNotEmpty())
            .Distinct()
            .ToHashSet();
        if (addingIds.Count == 0)
        {
            return [];
        }

        return (displayItems ?? [])
            .Where(item => item?.IndexId.IsNotEmpty() == true
                && (item.IsInFailoverQueue || addingIds.Contains(item.IndexId)))
            .Select(item => item.IndexId)
            .Distinct()
            .ToList();
    }

    private async Task RemoveSelectedFromFailoverQueue()
    {
        if (!CurrentGroupIsFailoverGroup || _config.SubIndexId.IsNullOrEmpty())
        {
            return;
        }

        var ids = (SelectedProfiles?.Count > 0 ? SelectedProfiles : [SelectedProfile])
            .Where(item => item?.IndexId.IsNotEmpty() == true)
            .Select(item => item.IndexId)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        if (await FailoverGroupManager.RemoveFromQueue(_config.SubIndexId, ids) == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
            await RefreshServers();
            AppEvents.FailoverStateChangedRequested.Publish();
        }
    }

    public async Task SetDefaultServer()
    {
        if (CurrentGroupIsFailoverGroup)
        {
            await AddSelectedToFailoverQueue();
            return;
        }

        if (string.IsNullOrEmpty(SelectedProfile?.IndexId))
        {
            return;
        }
        await SetDefaultServer(SelectedProfile.IndexId);
    }

    private async Task SetDefaultServer(string? indexId)
    {
        if (CurrentGroupIsFailoverGroup)
        {
            return;
        }

        if (indexId.IsNullOrEmpty())
        {
            return;
        }
        if (indexId == _config.IndexId)
        {
            return;
        }
        var item = await AppManager.Instance.GetProfileItem(indexId);
        if (item is null)
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectServer);
            return;
        }

        if (await ConfigHandler.SetDefaultServerIndex(_config, indexId) == 0)
        {
            await RefreshServers();
            Reload();
        }
    }

    private async Task SetActiveFailoverGroup()
    {
        if (!CurrentGroupIsFailoverGroup || SelectedSub?.Id.IsNullOrEmpty() != false)
        {
            NoticeManager.Instance.Enqueue(ResUI.MsgActivateFailoverGroupFirst);
            return;
        }

        _config.ActiveFailoverGroupId = SelectedSub.Id;
        await ConfigHandler.SaveConfig(_config);
        UpdateFailoverGroupState();
        NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
        AppEvents.FailoverStateChangedRequested.Publish();
    }

    public async Task ShareServerAsync()
    {
        var item = await AppManager.Instance.GetProfileItem(SelectedProfile.IndexId);
        if (item is null)
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectServer);
            return;
        }
        var url = FmtHandler.GetShareUri(item);
        if (url.IsNullOrEmpty())
        {
            return;
        }

        await _updateView?.Invoke(EViewAction.ShareServer, url);
    }

    private async Task GenGroupAllServer()
    {
        var ret = await ConfigHandler.AddGroupAllServer(_config, SelectedSub);
        if (ret.Success != true)
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            return;
        }
        _pendingSelectIndexId = ret.Data?.ToString();
        await RefreshServers();
    }

    private async Task GenGroupRegionServer()
    {
        var ret = await ConfigHandler.AddGroupRegionServer(_config, SelectedSub);
        if (ret.Success != true)
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            return;
        }
        var indexIdList = ret.Data as List<string>;
        _pendingSelectIndexId = indexIdList?.FirstOrDefault();
        await RefreshServers();
    }

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
            var isActiveLeastDelay = _config.ActiveFailoverGroupId == _config.SubIndexId
                && _config.FailoverMode == EFailoverMode.LeastDelay;
            if (isActiveLeastDelay)
            {
                _transferGroupSortStates[_config.SubIndexId] = new TransferGroupSortState(colName, asc, true);
                _dicHeaderSort[colName] = !asc;
                await RefreshServers();
                return;
            }

            if (await SortTransferGroupByHeader(_config.SubIndexId, colName, asc) != 0)
            {
                return;
            }

            _transferGroupSortStates[_config.SubIndexId] = new TransferGroupSortState(colName, asc, true);
            _dicHeaderSort[colName] = !asc;
            await PromoteFailoverQueueFirstAfterDrag();
            AppEvents.FailoverStateChangedRequested.Publish();
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

    private async Task<int> SortTransferGroupByHeader(string groupId, string colName, bool asc)
    {
        var source = ProfileItems.ToList();
        if (source.Count == 0)
        {
            source = await GetProfileItemsEx(groupId, string.Empty) ?? [];
        }

        var queueItems = source.Where(item => item.IsInFailoverQueue).ToList();
        var candidateItems = source.Where(item => !item.IsInFailoverQueue).ToList();
        var orderedQueueIds = ConfigHandler.SortProfileDisplayItemsForHeader(queueItems, colName, asc)
            .Select(item => item.IndexId)
            .ToList();
        var orderedCandidateIds = ConfigHandler.SortProfileDisplayItemsForHeader(candidateItems, colName, asc)
            .Select(item => item.IndexId)
            .ToList();

        if (orderedQueueIds.Count > 0 && await FailoverGroupManager.ReorderQueueItems(groupId, orderedQueueIds) != 0)
        {
            return -1;
        }

        var orderedIds = orderedQueueIds.Concat(orderedCandidateIds).ToList();
        if (orderedIds.Count > 0 && await ConfigHandler.SaveProfileDisplayOrder(orderedIds) != 0)
        {
            return -1;
        }

        return 0;
    }

    public async Task RemoveInvalidServerResult()
    {
        var count = await ConfigHandler.RemoveInvalidServerResult(_config, _config.SubIndexId);
        await RefreshServers();
        NoticeManager.Instance.Enqueue(string.Format(ResUI.RemoveInvalidServerResultTip, count));
    }

    //move server
    private async Task MoveToGroup(bool c)
    {
        if (!c)
        {
            return;
        }

        var lstSelected = await GetProfileItems(true);
        if (lstSelected == null)
        {
            return;
        }
        if (SelectedMoveToGroup?.IsFailoverGroup == true)
        {
            await CopySelectedToFailoverGroup(SelectedMoveToGroup);
            SelectedMoveToGroup = null;
            SelectedMoveToGroup = new();
            return;
        }

        await ConfigHandler.MoveToGroup(_config, lstSelected, SelectedMoveToGroup.Id);
        NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);

        await RefreshServers();
        SelectedMoveToGroup = null;
        SelectedMoveToGroup = new();
    }

    public async Task MoveServer(EMove eMove)
    {
        if (CurrentGroupIsFailoverGroup)
        {
            if (SelectedProfile == null)
            {
                return;
            }

            if (SelectedProfile.IsInFailoverQueue)
            {
                if (await FailoverGroupManager.MoveQueueItem(_config.SubIndexId, SelectedProfile.IndexId, eMove) == 0)
                {
                    await PromoteFailoverQueueFirstAfterDrag();
                    await RefreshServers();
                    AppEvents.FailoverStateChangedRequested.Publish();
                }
                return;
            }
        }

        var item = _lstProfile.FirstOrDefault(t => t.IndexId == SelectedProfile.IndexId);
        if (item is null)
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectServer);
            return;
        }

        var index = _lstProfile.IndexOf(item);
        if (index < 0)
        {
            return;
        }
        if (await ConfigHandler.MoveServer(_config, _lstProfile, index, eMove) == 0)
        {
            await RefreshServers();
        }
    }

    public async Task MoveServerTo(int startIndex, ProfileItemModel targetItem)
    {
        var sourceIndexId = startIndex >= 0 && startIndex < _lstProfile.Count
            ? _lstProfile[startIndex].IndexId
            : null;
        await MoveServerTo(sourceIndexId, targetItem);
    }

    public async Task MoveServerTo(string? sourceIndexId, ProfileItemModel targetItem)
    {
        if (CurrentGroupIsFailoverGroup)
        {
            if (sourceIndexId.IsNullOrEmpty() || targetItem == null)
            {
                return;
            }

            var sourceItem = ProfileItems.FirstOrDefault(item => item.IndexId == sourceIndexId);
            if (sourceItem == null)
            {
                return;
            }

            if (sourceItem.IsInFailoverQueue)
            {
                if (!targetItem.IsInFailoverQueue)
                {
                    return;
                }

                if (await FailoverGroupManager.MoveQueueItem(_config.SubIndexId, sourceIndexId, EMove.Position, targetItem.IndexId) == 0)
                {
                    await PromoteFailoverQueueFirstAfterDrag();
                    await RefreshServers();
                    AppEvents.FailoverStateChangedRequested.Publish();
                }
                return;
            }

            if (targetItem.IsInFailoverQueue)
            {
                return;
            }

            var displayStartIndex = FindProfileMoveIndexByIndexId(_lstProfile, sourceIndexId);
            var displayTargetIndex = FindProfileMoveIndexByIndexId(_lstProfile, targetItem.IndexId);
            if (displayStartIndex >= 0 && displayTargetIndex >= 0 && displayStartIndex != displayTargetIndex)
            {
                if (await ConfigHandler.MoveServer(_config, _lstProfile, displayStartIndex, EMove.Position, displayTargetIndex) == 0)
                {
                    await RefreshServers();
                }
            }
            return;
        }

        var startIndex = FindProfileMoveIndexByIndexId(_lstProfile, sourceIndexId);
        var targetIndex = FindProfileMoveIndexByIndexId(_lstProfile, targetItem?.IndexId);
        if (startIndex >= 0 && targetIndex >= 0 && startIndex != targetIndex)
        {
            if (await ConfigHandler.MoveServer(_config, _lstProfile, startIndex, EMove.Position, targetIndex) == 0)
            {
                await RefreshServers();
            }
        }
    }

    public async Task<bool> MoveServersTo(IReadOnlyList<string> sourceIndexIds, ProfileItemModel targetItem)
    {
        var sourceIds = sourceIndexIds
            .Where(id => id.IsNotEmpty())
            .Distinct()
            .ToList();
        if (sourceIds.Count == 0 || targetItem == null)
        {
            return false;
        }

        var displayIds = ProfileItems.Select(item => item.IndexId).ToList();
        if (ProfileDragDropBlockMove.MoveToTarget(displayIds, sourceIds, targetItem.IndexId).SequenceEqual(displayIds))
        {
            return false;
        }

        if (CurrentGroupIsFailoverGroup)
        {
            var sourceItems = ProfileItems.Where(item => sourceIds.Contains(item.IndexId)).ToList();
            if (sourceItems.Count != sourceIds.Count)
            {
                return false;
            }

            var sourceQueueStates = sourceItems.Select(item => item.IsInFailoverQueue).ToList();
            var restrictQueueBoundary = ShouldRestrictTransferGroupDragBoundary(
                _config,
                _config.SubIndexId,
                CurrentGroupIsFailoverGroup);
            if (restrictQueueBoundary && sourceQueueStates.Distinct().Count() != 1)
            {
                return false;
            }
            if (restrictQueueBoundary && sourceQueueStates.First() != targetItem.IsInFailoverQueue)
            {
                return false;
            }

            if (sourceQueueStates.Distinct().Count() == 1 && sourceQueueStates.First() && targetItem.IsInFailoverQueue)
            {
                if (await FailoverGroupManager.MoveQueueItems(_config.SubIndexId, sourceIds, targetItem.IndexId) == 0)
                {
                    _pendingSelectIndexId = sourceIds.FirstOrDefault();
                    await PromoteFailoverQueueFirstAfterDrag();
                    DeactivateTransferGroupSortState();
                    await RefreshServersBiz();
                    AppEvents.FailoverStateChangedRequested.Publish();
                    return true;
                }
                return false;
            }

        }

        if (await ConfigHandler.MoveServers(_config, _lstProfile, sourceIds, targetItem.IndexId) == 0)
        {
            _pendingSelectIndexId = sourceIds.FirstOrDefault();
            DeactivateTransferGroupSortState();
            await RefreshServersBiz();
            return true;
        }

        return false;
    }

    public static bool ShouldRestrictTransferGroupDragBoundary(Config config, string? currentGroupId, bool currentGroupIsFailoverGroup)
    {
        return currentGroupIsFailoverGroup
            && currentGroupId.IsNotEmpty()
            && config.ActiveFailoverGroupId == currentGroupId
            && config.FailoverMode != EFailoverMode.Off;
    }

    private void DeactivateTransferGroupSortState()
    {
        if (!CurrentGroupIsFailoverGroup
            || _config.SubIndexId.IsNullOrEmpty()
            || !_transferGroupSortStates.TryGetValue(_config.SubIndexId, out var sortState))
        {
            return;
        }

        _transferGroupSortStates[_config.SubIndexId] = sortState with { Active = false };
    }

    private async Task PromoteFailoverQueueFirstAfterDrag()
    {
        var firstQueueProfileId = await FailoverGroupManager.GetFirstQueueProfileIdForMode(
            _config.SubIndexId,
            EFailoverMode.Failover);
        if (!ShouldPromoteFailoverQueueFirstAfterDrag(_config, _config.SubIndexId, firstQueueProfileId))
        {
            return;
        }

        _config.FailoverStartupProfileId = firstQueueProfileId;
        await ConfigHandler.SaveConfig(_config);
        Reload();
    }

    public static int FindProfileMoveIndexByIndexId(IReadOnlyList<ProfileItem>? profiles, string? indexId)
    {
        if (profiles == null || indexId.IsNullOrEmpty())
        {
            return -1;
        }

        for (var i = 0; i < profiles.Count; i++)
        {
            if (profiles[i].IndexId == indexId)
            {
                return i;
            }
        }

        return -1;
    }

    public async Task ServerSpeedtest(ESpeedActionType actionType)
    {
        List<ProfileItem>? lstSelected;
        if (actionType is ESpeedActionType.Mixedtest or ESpeedActionType.FastRealping)
        {
            if (actionType == ESpeedActionType.FastRealping)
            {
                actionType = ESpeedActionType.Realping;
            }

            lstSelected = JsonUtils.Deserialize<List<ProfileItem>>(JsonUtils.Serialize(ProfileItems?.OrderBy(t => t.Sort)));
        }
        else
        {
            lstSelected = await GetProfileItems(false);
        }

        if (lstSelected is null || lstSelected.Count <= 0)
        {
            return;
        }

        _speedtestService ??= new SpeedtestService(_config, async (SpeedTestResult result) =>
        {
            RxSchedulers.MainThreadScheduler.Schedule(result, (scheduler, result) =>
            {
                _ = SetSpeedTestResult(result);
                return Disposable.Empty;
            });
            await Task.CompletedTask;
        });
        _speedtestService?.RunLoop(actionType, lstSelected);
    }

    public void ServerSpeedtestStop()
    {
        _speedtestService?.ExitLoop();
    }

    private async Task Export2ClientConfigAsync(bool blClipboard)
    {
        var item = await AppManager.Instance.GetProfileItem(SelectedProfile.IndexId);
        if (item is null)
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectServer);
            return;
        }

        var (context, validatorResult) = await CoreConfigContextBuilder.Build(_config, item);
        if (NoticeManager.Instance.NotifyValidatorResult(validatorResult) && !validatorResult.Success)
        {
            return;
        }

        if (blClipboard)
        {
            var result = await CoreConfigHandler.GenerateClientConfig(context, null);
            if (result.Success != true)
            {
                NoticeManager.Instance.Enqueue(result.Msg);
            }
            else
            {
                await _updateView?.Invoke(EViewAction.SetClipboardData, result.Data);
                NoticeManager.Instance.SendMessage(ResUI.OperationSuccess);
            }
        }
        else
        {
            await _updateView?.Invoke(EViewAction.SaveFileDialog, item);
        }
    }

    public async Task Export2ClientConfigResult(string fileName, ProfileItem item)
    {
        if (fileName.IsNullOrEmpty())
        {
            return;
        }
        var (context, validatorResult) = await CoreConfigContextBuilder.Build(_config, item);
        if (NoticeManager.Instance.NotifyValidatorResult(validatorResult) && !validatorResult.Success)
        {
            return;
        }
        var result = await CoreConfigHandler.GenerateClientConfig(context, fileName);
        if (result.Success != true)
        {
            NoticeManager.Instance.Enqueue(result.Msg);
        }
        else
        {
            NoticeManager.Instance.SendMessageAndEnqueue(string.Format(ResUI.SaveClientConfigurationIn, fileName));
        }
    }

    public async Task Export2ShareUrlAsync(bool blEncode)
    {
        var lstSelected = await GetProfileItems(true);
        if (lstSelected == null)
        {
            return;
        }

        StringBuilder sb = new();
        foreach (var it in lstSelected)
        {
            var url = FmtHandler.GetShareUri(it);
            if (url.IsNullOrEmpty())
            {
                continue;
            }
            sb.Append(url);
            sb.AppendLine();
        }
        if (sb.Length > 0)
        {
            if (blEncode)
            {
                await _updateView?.Invoke(EViewAction.SetClipboardData, Utils.Base64Encode(sb.ToString()));
            }
            else
            {
                await _updateView?.Invoke(EViewAction.SetClipboardData, sb.ToString());
            }
            NoticeManager.Instance.SendMessage(ResUI.BatchExportURLSuccessfully);
        }
    }

    #endregion Add Servers

    #region Subscription

    private async Task EditSubAsync(bool blNew)
    {
        SubItem item;
        if (blNew)
        {
            item = new();
        }
        else
        {
            item = await AppManager.Instance.GetSubItem(_config.SubIndexId);
            if (item is null)
            {
                return;
            }
        }
        if (await _updateView?.Invoke(EViewAction.SubEditWindow, item) == true)
        {
            await RefreshSubscriptions();
            await SubSelectedChangedAsync(true);
        }
    }

    private async Task DeleteSubAsync()
    {
        var item = await AppManager.Instance.GetSubItem(_config.SubIndexId);
        if (item is null)
        {
            return;
        }

        if (await _updateView?.Invoke(EViewAction.ShowYesNo, null) == false)
        {
            return;
        }
        await ConfigHandler.DeleteSubItem(_config, item.Id);

        await RefreshSubscriptions();
        await SubSelectedChangedAsync(true);
    }

    #endregion Subscription
}
