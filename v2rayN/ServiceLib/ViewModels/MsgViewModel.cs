namespace ServiceLib.ViewModels;

public class MsgViewModel : MyReactiveObject
{
    public sealed record FailoverModeDisplayState(bool IsOff, bool IsFailover, bool IsLeastDelay);

    public static FailoverModeDisplayState GetFailoverModeDisplayState(EFailoverMode mode)
    {
        return new FailoverModeDisplayState(
            mode != EFailoverMode.Failover,
            mode == EFailoverMode.Failover,
            false);
    }

    public static bool GetShowActiveFailoverGroupTag(bool hasActiveGroup, EFailoverMode mode)
    {
        return hasActiveGroup && mode == EFailoverMode.Failover;
    }

    public static void PrepareLeastDelayStartup(Config config, string? runningFailoverTargetProfileId)
    {
        config.FailoverMode = EFailoverMode.LeastDelay;
        config.FailoverEnabled = true;
        config.FailoverStartupProfileId = runningFailoverTargetProfileId.IsNotEmpty()
            ? runningFailoverTargetProfileId
            : config.IndexId;
    }

    public static void PrepareFailoverStartup(Config config, string? firstQueueProfileId)
    {
        config.FailoverMode = EFailoverMode.Failover;
        config.FailoverEnabled = true;
        config.FailoverStartupProfileId = firstQueueProfileId.IsNotEmpty()
            ? firstQueueProfileId
            : null;
    }

    private readonly ConcurrentQueue<string> _queueMsg = new();
    private volatile bool _lastMsgFilterNotAvailable;
    private int _showLock = 0; // 0 = unlocked, 1 = locked
    public int NumMaxMsg { get; } = 500;

    [Reactive]
    public string MsgFilter { get; set; }

    [Reactive]
    public bool AutoRefresh { get; set; }

    [Reactive]
    public EFailoverMode FailoverMode { get; set; }

    [Reactive]
    public bool IsFailoverModeOff { get; set; }

    [Reactive]
    public bool IsFailoverModeFailover { get; set; }

    [Reactive]
    public bool IsFailoverModeLeastDelay { get; set; }

    [Reactive]
    public string ActiveFailoverGroupName { get; set; }

    [Reactive]
    public bool ShowActiveFailoverGroupTag { get; set; }

    [Reactive]
    public string FailoverCoreCheckResultText { get; set; }

    [Reactive]
    public bool ShowFailoverCoreCheckResult { get; set; }

    public ReactiveCommand<EFailoverMode, Unit> ChangeFailoverModeCmd { get; }

    private bool _suppressFailoverModeChange;

    public MsgViewModel(Func<EViewAction, object?, Task<bool>>? updateView)
    {
        _config = AppManager.Instance.Config;
        _updateView = updateView;
        MsgFilter = _config.MsgUIItem.MainMsgFilter ?? string.Empty;
        AutoRefresh = _config.MsgUIItem.AutoRefresh ?? true;
        FailoverMode = _config.FailoverMode;
        ApplyFailoverModeDisplay(FailoverMode);
        ActiveFailoverGroupName = ResUI.MsgActivateFailoverGroupFirst;
        FailoverCoreCheckResultText = string.Empty;
        ChangeFailoverModeCmd = ReactiveCommand.CreateFromTask<EFailoverMode>(ChangeFailoverMode);

        this.WhenAnyValue(
           x => x.MsgFilter)
               .Subscribe(c => DoMsgFilter());

        this.WhenAnyValue(
          x => x.AutoRefresh,
          y => y == true)
              .Subscribe(c => _config.MsgUIItem.AutoRefresh = AutoRefresh);

        AppEvents.SendMsgViewRequested
         .AsObservable()
         //.ObserveOn(RxSchedulers.MainThreadScheduler)
         .Subscribe(content => _ = AppendQueueMsg(content));

        AppEvents.FailoverStateChangedRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                var ignored = RefreshFailoverState();
            });

        AppEvents.FailoverCoreCheckRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                var ignored = CheckFailoverCoreType();
            });

        _ = RefreshFailoverState();
    }

    private async Task RefreshFailoverState()
    {
        var activeGroup = await FailoverGroupManager.GetActiveFailoverGroup(_config);
        ActiveFailoverGroupName = activeGroup?.Remarks ?? ResUI.MsgActivateFailoverGroupFirst;
        ShowActiveFailoverGroupTag = GetShowActiveFailoverGroupTag(activeGroup != null, _config.FailoverMode);
        if (activeGroup == null && _config.FailoverMode != EFailoverMode.Off)
        {
            _config.FailoverMode = EFailoverMode.Off;
            _config.FailoverEnabled = false;
            await FailoverHealthService.Instance.StopAndRestoreProbingAsync();
            await ConfigHandler.SaveConfig(_config);
            ShowActiveFailoverGroupTag = false;
        }

        if (_config.FailoverMode != EFailoverMode.Off
            && (_config.FailoverMode != EFailoverMode.LeastDelay || _config.FailoverStartupProfileId.IsNullOrEmpty()))
        {
            FailoverHealthService.Instance.Start();
        }
        else
        {
            FailoverHealthService.Instance.Stop();
        }

        _suppressFailoverModeChange = true;
        FailoverMode = _config.FailoverMode;
        ApplyFailoverModeDisplay(FailoverMode);
        _suppressFailoverModeChange = false;
    }

    private void ApplyFailoverModeDisplay(EFailoverMode mode)
    {
        var state = GetFailoverModeDisplayState(mode);
        IsFailoverModeOff = state.IsOff;
        IsFailoverModeFailover = state.IsFailover;
        IsFailoverModeLeastDelay = state.IsLeastDelay;
    }

    private async Task CheckFailoverCoreType()
    {
        ShowFailoverCoreCheckResult = true;
        FailoverCoreCheckResultText = ResUI.TbFailoverCoreChecking;

        var result = await FailoverGroupManager.CheckFailoverQueueCoreType(_config.ActiveFailoverGroupId);
        FailoverCoreCheckResultText = result switch
        {
            EFailoverQueueCoreTypeCheckResult.SameXray => ResUI.TbFailoverCoreSameXray,
            EFailoverQueueCoreTypeCheckResult.SameSingBox => ResUI.TbFailoverCoreSameSingBox,
            EFailoverQueueCoreTypeCheckResult.Mixed => ResUI.TbFailoverCoreMixed,
            _ => ResUI.TbFailoverCoreEmpty,
        };
    }

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
            await FailoverHealthService.Instance.StopAndClearProbingAsync();
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

        if (mode == EFailoverMode.LeastDelay)
        {
            PrepareLeastDelayStartup(_config, AppManager.Instance.RunningFailoverTargetProfileId);
        }
        else
        {
            var firstQueueProfileId = await FailoverGroupManager.GetFirstQueueProfileIdForMode(
                _config.ActiveFailoverGroupId,
                EFailoverMode.Failover);
            PrepareFailoverStartup(_config, firstQueueProfileId);
        }
        var validator = await FailoverGroupManager.ValidateActiveFailoverGroup(_config, currentNode);
        if (!validator.Success)
        {
            NoticeManager.Instance.NotifyValidatorResult(validator);
            await DisableFailover();
            return;
        }

        await ConfigHandler.SaveConfig(_config);
        if (mode == EFailoverMode.LeastDelay)
        {
            AppEvents.ReloadRequested.Publish();
            AppEvents.FailoverStateChangedRequested.Publish();
            FailoverHealthService.Instance.StartLeastDelayProbeRound();
        }
        else
        {
            FailoverHealthService.Instance.Start();
            AppEvents.ReloadRequested.Publish();
            AppEvents.FailoverStateChangedRequested.Publish();
        }

        NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
        await RefreshFailoverState();
    }

    private async Task DisableFailover()
    {
        _config.FailoverMode = EFailoverMode.Off;
        _config.FailoverEnabled = false;
        await FailoverHealthService.Instance.StopAndClearProbingAsync();
        await ConfigHandler.SaveConfig(_config);
        _suppressFailoverModeChange = true;
        FailoverMode = EFailoverMode.Off;
        ApplyFailoverModeDisplay(FailoverMode);
        _suppressFailoverModeChange = false;
        AppEvents.FailoverStateChangedRequested.Publish();
        await RefreshFailoverState();
    }

    private async Task AppendQueueMsg(string msg)
    {
        if (AutoRefresh == false)
        {
            return;
        }

        EnqueueQueueMsg(msg);

        if (!AppManager.Instance.ShowInTaskbar)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _showLock, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await Task.Delay(500).ConfigureAwait(false);

            var sb = new StringBuilder();
            while (_queueMsg.TryDequeue(out var line))
            {
                sb.Append(line);
            }

            await _updateView?.Invoke(EViewAction.DispatcherShowMsg, sb.ToString());
        }
        finally
        {
            Interlocked.Exchange(ref _showLock, 0);
        }
    }

    private void EnqueueQueueMsg(string msg)
    {
        //filter msg
        if (MsgFilter.IsNotEmpty() && !_lastMsgFilterNotAvailable)
        {
            try
            {
                if (!Regex.IsMatch(msg, MsgFilter))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                EnqueueWithLimit(ex.Message);
                _lastMsgFilterNotAvailable = true;
            }
        }

        EnqueueWithLimit(msg);
        if (!msg.EndsWith(Environment.NewLine))
        {
            EnqueueWithLimit(Environment.NewLine);
        }
    }

    private void EnqueueWithLimit(string item)
    {
        _queueMsg.Enqueue(item);

        while (_queueMsg.Count > NumMaxMsg)
        {
            _queueMsg.TryDequeue(out _);
        }
    }

    //public void ClearMsg()
    //{
    //    _queueMsg.Clear();
    //}

    private void DoMsgFilter()
    {
        _config.MsgUIItem.MainMsgFilter = MsgFilter;
        _lastMsgFilterNotAvailable = false;
    }
}
