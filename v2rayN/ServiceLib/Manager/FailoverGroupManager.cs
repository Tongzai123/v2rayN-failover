namespace ServiceLib.Manager;

public record FailoverQueueEntry(FailoverGroupItem Item, ProfileItem Profile);

public record FailoverBuildResult(ProfileItem? Node, List<ProfileItem> ChildProfiles, NodeValidatorResult ValidatorResult)
{
    public bool Success => ValidatorResult.Success;
}

public static class FailoverGroupManager
{
    public const string VirtualPolicyGroupPrefix = "inner-failover-";

    public static async Task<FailoverRuntimeResolveResult> TryResolveRuntimeNode(Config config, ProfileItem currentNode)
    {
        var validator = NodeValidatorResult.Empty();
        if (config.FailoverMode == EFailoverMode.Off)
        {
            return new(FailoverRuntimeKind.None, currentNode, [], currentNode.IndexId, validator);
        }

        var activeGroup = await GetActiveFailoverGroup(config);
        if (activeGroup == null)
        {
            validator.Errors.Add(ResUI.MsgActivateFailoverGroupFirst);
            return new(FailoverRuntimeKind.None, null, [], null, validator);
        }

        var queueEntries = await GetQueueEntriesForMode(activeGroup.Id, true, config.FailoverMode);
        if (queueEntries.Count == 0)
        {
            validator.Errors.Add(ResUI.MsgFailoverGroupQueueEmpty);
            return new(FailoverRuntimeKind.None, null, [], null, validator);
        }

        var hasLeastDelayResult = config.FailoverMode == EFailoverMode.LeastDelay
            && queueEntries.Any(entry => entry.Item.LastStatus == FailoverHealthStatus.Normal && entry.Item.LastDelay > 0);

        if (config.FailoverMode == EFailoverMode.LeastDelay
            && !hasLeastDelayResult
            && config.FailoverStartupProfileId.IsNotEmpty())
        {
            var startupProfile = await AppManager.Instance.GetProfileItem(config.FailoverStartupProfileId);
            if (startupProfile != null)
            {
                var startupCoreType = AppManager.Instance.GetCoreType(startupProfile, startupProfile.ConfigType);
                var startupValidation = NodeValidator.Validate(startupProfile, startupCoreType);
                if (startupValidation.Success)
                {
                    return new(
                        FailoverRuntimeKind.DirectProfile,
                        startupProfile,
                        queueEntries.Select(entry => entry.Profile).ToList(),
                        startupProfile.IndexId,
                        validator);
                }
            }
        }

        var currentCoreType = AppManager.Instance.GetCoreType(currentNode, currentNode.ConfigType);
        if (CanUseCoreFallback(currentCoreType, queueEntries))
        {
            var result = await TryBuildVirtualPolicyGroup(config, currentNode);
            return new(
                FailoverRuntimeKind.VirtualPolicyGroup,
                result.Node,
                result.ChildProfiles,
                result.ChildProfiles.FirstOrDefault()?.IndexId,
                result.ValidatorResult);
        }

        var targetEntry = PickRuntimeEntryForDirectProfile(queueEntries, config.FailoverMode);
        if (targetEntry == null)
        {
            validator.Errors.Add(ResUI.MsgFailoverGroupQueueEmpty);
            return new(FailoverRuntimeKind.None, null, [], null, validator);
        }

        var targetProfile = targetEntry.Profile;
        var targetCoreType = AppManager.Instance.GetCoreType(targetProfile, targetProfile.ConfigType);
        var targetValidation = NodeValidator.Validate(targetProfile, targetCoreType);
        if (!targetValidation.Success)
        {
            return new(FailoverRuntimeKind.None, null, [], null, targetValidation);
        }

        return new(
            FailoverRuntimeKind.DirectProfile,
            targetProfile,
            queueEntries.Select(entry => entry.Profile).ToList(),
            targetProfile.IndexId,
            validator);
    }

    public static async Task<List<SubItem>> GetFailoverGroups()
    {
        return (await AppManager.Instance.SubItems())?
            .Where(item => item.IsFailoverGroup)
            .OrderBy(item => item.Sort)
            .ToList() ?? [];
    }

    public static async Task<SubItem?> GetActiveFailoverGroup(Config config)
    {
        if (config.ActiveFailoverGroupId.IsNullOrEmpty())
        {
            return null;
        }

        var group = await AppManager.Instance.GetSubItem(config.ActiveFailoverGroupId);
        return group is { IsFailoverGroup: true } ? group : null;
    }

    public static async Task<int> CopyToFailoverGroup(string groupId, IEnumerable<ProfileItem> profiles)
    {
        var group = await AppManager.Instance.GetSubItem(groupId);
        if (group is not { IsFailoverGroup: true })
        {
            return -1;
        }

        var profileList = profiles
            .Where(profile => profile.IndexId.IsNotEmpty() && !profile.ConfigType.IsComplexType())
            .GroupBy(profile => profile.IndexId)
            .Select(grouping => grouping.First())
            .ToList();
        if (profileList.Count == 0)
        {
            return -1;
        }

        var existing = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
            .Where(item => item.GroupId == groupId)
            .ToListAsync();
        var maxSort = existing.Count == 0 ? 0 : existing.Max(item => item.Sort);
        var additions = new List<FailoverGroupItem>();

        foreach (var profile in profileList)
        {
            if (existing.Any(item => item.SourceProfileId == profile.IndexId))
            {
                continue;
            }

            additions.Add(new FailoverGroupItem
            {
                Id = Utils.GetGuid(false),
                GroupId = groupId,
                SourceProfileId = profile.IndexId,
                FailoverProfileId = profile.IndexId,
                Sort = ++maxSort,
                Enabled = false,
                LastStatus = FailoverHealthStatus.Unknown,
            });
        }

        if (additions.Count == 0)
        {
            return 0;
        }

        return await SQLiteHelper.Instance.InsertAllAsync(additions) > 0 ? 0 : -1;
    }

    public static async Task<int> AddToQueue(string groupId, IEnumerable<string> sourceProfileIds)
    {
        var idList = sourceProfileIds.Where(id => id.IsNotEmpty()).Distinct().ToList();
        if (groupId.IsNullOrEmpty() || idList.Count == 0)
        {
            return -1;
        }

        var items = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
            .Where(item => item.GroupId == groupId)
            .ToListAsync();
        var itemMap = items.ToDictionary(item => item.SourceProfileId);
        var maxSort = items.Where(item => item.Enabled).Select(item => item.Sort).DefaultIfEmpty(0).Max();
        var changed = new List<FailoverGroupItem>();

        foreach (var sourceProfileId in idList)
        {
            if (!itemMap.TryGetValue(sourceProfileId, out var item))
            {
                item = new FailoverGroupItem
                {
                    Id = Utils.GetGuid(false),
                    GroupId = groupId,
                    SourceProfileId = sourceProfileId,
                    FailoverProfileId = sourceProfileId,
                    Sort = ++maxSort,
                    LastStatus = FailoverHealthStatus.Unknown,
                };
            }
            else if (!item.Enabled)
            {
                item.Sort = ++maxSort;
            }

            item.Enabled = true;
            changed.Add(item);
        }

        await NormalizeQueueSort(groupId, changed);
        return 0;
    }

    public static async Task<int> RemoveFromQueue(string groupId, IEnumerable<string> sourceProfileIds)
    {
        var idList = sourceProfileIds.Where(id => id.IsNotEmpty()).Distinct().ToList();
        if (groupId.IsNullOrEmpty() || idList.Count == 0)
        {
            return -1;
        }

        var items = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
            .Where(item => item.GroupId == groupId && idList.Contains(item.SourceProfileId))
            .ToListAsync();
        foreach (var item in items)
        {
            item.Enabled = false;
        }

        if (items.Count > 0)
        {
            await SQLiteHelper.Instance.UpdateAllAsync(items);
        }
        await NormalizeQueueSort(groupId);
        return 0;
    }

    public static async Task<List<FailoverQueueEntry>> GetQueueEntries(string groupId, bool enabledOnly)
    {
        if (groupId.IsNullOrEmpty())
        {
            return [];
        }

        var items = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
            .Where(item => item.GroupId == groupId)
            .ToListAsync();
        if (enabledOnly)
        {
            items = items.Where(item => item.Enabled).ToList();
        }
        foreach (var item in items)
        {
            if (item.LastStatus.IsNullOrEmpty())
            {
                item.LastStatus = FailoverHealthStatus.Unknown;
            }
        }

        items = items
            .OrderByDescending(item => item.Enabled)
            .ThenBy(item => item.Sort)
            .ThenBy(item => item.SourceProfileId)
            .ToList();
        var profileMap = await AppManager.Instance.GetProfileItemsByIndexIdsAsMap(items.Select(item => item.SourceProfileId));

        return items
            .Where(item => profileMap.ContainsKey(item.SourceProfileId))
            .Select(item => new FailoverQueueEntry(item, profileMap[item.SourceProfileId]))
            .ToList();
    }

    public static async Task<List<FailoverQueueEntry>> GetQueueEntriesForMode(string groupId, bool enabledOnly, EFailoverMode mode)
    {
        var entries = await GetQueueEntries(groupId, enabledOnly);
        return mode == EFailoverMode.LeastDelay
            ? ApplyLeastDelayRuntimeOrder(entries)
            : entries;
    }

    public static async Task<EFailoverQueueCoreTypeCheckResult> CheckFailoverQueueCoreType(string groupId)
    {
        var entries = await GetQueueEntries(groupId, true);
        if (entries.Count == 0)
        {
            return EFailoverQueueCoreTypeCheckResult.Empty;
        }

        var coreTypes = entries
            .Select(entry => AppManager.Instance.GetCoreType(entry.Profile, entry.Profile.ConfigType))
            .Distinct()
            .ToList();

        return coreTypes.Count == 1
            ? coreTypes[0] switch
            {
                ECoreType.Xray => EFailoverQueueCoreTypeCheckResult.SameXray,
                ECoreType.sing_box => EFailoverQueueCoreTypeCheckResult.SameSingBox,
                _ => EFailoverQueueCoreTypeCheckResult.Mixed,
            }
            : EFailoverQueueCoreTypeCheckResult.Mixed;
    }

    public static async Task<string?> GetFirstQueueProfileIdForMode(string groupId, EFailoverMode mode)
    {
        return (await GetQueueEntriesForMode(groupId, true, mode))
            .FirstOrDefault()
            ?.Profile
            .IndexId;
    }

    public static async Task<string?> GetLeastDelayRuntimeTargetProfileId(string groupId)
    {
        return (await GetQueueEntries(groupId, true))
            .Where(entry => entry.Item.LastStatus == FailoverHealthStatus.Normal && entry.Item.LastDelay > 0)
            .OrderBy(entry => entry.Item.LastDelay)
            .ThenBy(entry => entry.Item.Sort)
            .Select(entry => entry.Profile.IndexId)
            .FirstOrDefault();
    }

    public static async Task<string?> GetRuntimeQueueSignature(Config config, ProfileItem currentNode)
    {
        var resolveResult = await TryResolveRuntimeNode(config, currentNode);
        return resolveResult.Success ? resolveResult.RuntimeQueueSignature : null;
    }

    public static async Task<FailoverRelayRuntime?> TryBuildRelayRuntime(Config config, ProfileItem currentNode, CoreConfigContext context)
    {
        if (config.FailoverMode != EFailoverMode.Failover || config.TunModeItem.EnableTun)
        {
            return null;
        }

        var currentCoreType = AppManager.Instance.GetCoreType(currentNode, currentNode.ConfigType);
        if (currentCoreType != ECoreType.Xray)
        {
            return null;
        }

        var activeGroup = await GetActiveFailoverGroup(config);
        if (activeGroup == null)
        {
            return null;
        }

        var entries = await GetQueueEntriesForMode(activeGroup.Id, true, config.FailoverMode);
        if (entries.Count == 0)
        {
            return null;
        }

        var coreTypes = entries
            .Select(entry => AppManager.Instance.GetCoreType(entry.Profile, entry.Profile.ConfigType))
            .Distinct()
            .ToList();
        if (coreTypes.Count != 1 || coreTypes[0] != ECoreType.Xray)
        {
            return null;
        }

        var candidates = new List<FailoverRelayCandidate>();
        var relayListenPort = GetConfiguredSocksPort(config);
        var allocatedCandidatePorts = new HashSet<int> { relayListenPort };
        var nextCandidatePort = relayListenPort < 65535 ? relayListenPort + 1 : 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var profile = entries[i].Profile;
            var inboundPort = AllocateCandidatePort();
            context.AllProxiesMap[profile.IndexId] = profile;
            candidates.Add(new FailoverRelayCandidate(
                profile.IndexId,
                $"failover-p{i + 1}-in",
                $"{Global.ProxyTag}-{i + 1}-{profile.Remarks}",
                inboundPort,
                profile.Remarks,
                entries[i].Item.LastStatus,
                entries[i].Item.LastSuccessTime,
                entries[i].Item.LastFailureReason,
                entries[i].Item.Sort));
        }

        return new FailoverRelayRuntime
        {
            ListenPort = relayListenPort,
            Candidates = candidates,
        };

        int AllocateCandidatePort()
        {
            while (true)
            {
                var port = Utils.GetFreePort(nextCandidatePort);
                nextCandidatePort = port < 65535 ? port + 1 : 0;
                if (allocatedCandidatePorts.Add(port))
                {
                    return port;
                }
            }
        }
    }

    private static int GetConfiguredSocksPort(Config config)
    {
        return config.Inbound?
            .FirstOrDefault(t => t.Protocol == nameof(EInboundProtocol.socks))
            ?.LocalPort ?? 10808;
    }

    public static string BuildRuntimeQueueSignature(IEnumerable<ProfileItem> queueProfiles)
    {
        return string.Join(",", queueProfiles.Select(profile => profile.IndexId));
    }

    public static async Task<List<FailoverQueueEntry>> GetDueHealthCheckEntries(string groupId, long now)
    {
        return await GetQueueEntries(groupId, true);
    }

    public static async Task<List<ProfileItem>> GetCandidateProfiles(string groupId)
    {
        return (await GetQueueEntries(groupId, false))
            .Select(entry => entry.Profile)
            .ToList();
    }

    public static async Task<List<ProfileItem>> GetCandidateProfiles(string groupId, EFailoverMode mode)
    {
        return (await GetQueueEntriesForDisplayMode(groupId, false, mode))
            .Select(entry => entry.Profile)
            .ToList();
    }

    public static async Task<List<ProfileItem>> GetDisplayProfiles(string groupId, EFailoverMode mode)
    {
        var profiles = await GetCandidateProfiles(groupId, mode);
        var directProfiles = await AppManager.Instance.ProfileItems(groupId) ?? [];
        var existingIds = profiles.Select(profile => profile.IndexId).ToHashSet();
        profiles.AddRange(directProfiles.Where(profile => existingIds.Add(profile.IndexId)));
        return profiles;
    }

    public static async Task<int> ConvertToNormalGroup(string groupId)
    {
        if (groupId.IsNullOrEmpty())
        {
            return -1;
        }

        var entries = await GetQueueEntries(groupId, false);
        var directProfiles = await AppManager.Instance.ProfileItems(groupId) ?? [];
        var directProfileIds = directProfiles.Select(profile => profile.IndexId).ToHashSet();
        var sourceProfiles = entries
            .Select(entry => entry.Profile)
            .Where(profile => !directProfileIds.Contains(profile.IndexId))
            .GroupBy(profile => profile.IndexId)
            .Select(grouping => grouping.First())
            .ToList();

        var sort = ProfileExManager.Instance.GetMaxSort();
        foreach (var sourceProfile in sourceProfiles)
        {
            var profile = JsonUtils.DeepCopy(sourceProfile);
            profile.IndexId = Utils.GetGuid(false);
            profile.Subid = groupId;
            profile.IsSub = false;
            profile.SetProtocolExtra(profile.GetProtocolExtra());

            await SQLiteHelper.Instance.ReplaceAsync(profile);
            ProfileExManager.Instance.SetSort(profile.IndexId, ++sort);
        }

        var failoverItems = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
            .Where(item => item.GroupId == groupId)
            .ToListAsync();
        foreach (var item in failoverItems)
        {
            await SQLiteHelper.Instance.DeleteAsync(item);
        }

        return 0;
    }

    public static async Task<Dictionary<string, int>> GetQueuePriorityMap(string groupId)
    {
        return await GetQueuePriorityMap(groupId, EFailoverMode.Failover);
    }

    public static async Task<Dictionary<string, int>> GetQueuePriorityMap(string groupId, EFailoverMode mode)
    {
        var entries = await GetQueueEntries(groupId, true);
        var result = new Dictionary<string, int>();
        for (var i = 0; i < entries.Count; i++)
        {
            result[entries[i].Profile.IndexId] = i + 1;
        }
        return result;
    }

    public static async Task<List<FailoverQueueEntry>> GetQueueEntriesForDisplayMode(string groupId, bool enabledOnly, EFailoverMode mode)
    {
        var entries = await GetQueueEntries(groupId, enabledOnly);
        return mode == EFailoverMode.LeastDelay
            ? ApplyLeastDelayDisplayOrder(entries)
            : entries;
    }

    public static async Task<int> MoveQueueItem(string groupId, string sourceProfileId, EMove move, string? targetSourceProfileId = null)
    {
        var items = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
            .Where(item => item.GroupId == groupId && item.Enabled)
            .ToListAsync();
        items = items.OrderBy(item => item.Sort).ToList();
        var index = items.FindIndex(item => item.SourceProfileId == sourceProfileId);
        if (index < 0)
        {
            return -1;
        }

        var item = items[index];
        items.RemoveAt(index);
        var targetIndex = move switch
        {
            EMove.Top => 0,
            EMove.Up => Math.Max(0, index - 1),
            EMove.Down => Math.Min(items.Count, index + 1),
            EMove.Bottom => items.Count,
            EMove.Position => targetSourceProfileId.IsNotEmpty()
                ? Math.Max(0, items.FindIndex(x => x.SourceProfileId == targetSourceProfileId))
                : index,
            _ => index,
        };
        if (targetIndex < 0)
        {
            targetIndex = items.Count;
        }
        items.Insert(targetIndex, item);

        for (var i = 0; i < items.Count; i++)
        {
            items[i].Sort = i + 1;
        }
        await SQLiteHelper.Instance.UpdateAllAsync(items);
        return 0;
    }

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

    public static async Task<int> ReorderQueueItems(string groupId, IReadOnlyList<string> orderedProfileIds)
    {
        var orderedIds = orderedProfileIds
            .Where(id => id.IsNotEmpty())
            .Distinct()
            .ToList();
        if (groupId.IsNullOrEmpty() || orderedIds.Count == 0)
        {
            return -1;
        }

        var items = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
            .Where(item => item.GroupId == groupId && item.Enabled)
            .ToListAsync();
        items = items.OrderBy(item => item.Sort).ThenBy(item => item.SourceProfileId).ToList();
        var itemMap = items.ToDictionary(item => item.SourceProfileId);
        if (!orderedIds.All(itemMap.ContainsKey))
        {
            return -1;
        }

        var remainingIds = items
            .Select(item => item.SourceProfileId)
            .Where(id => !orderedIds.Contains(id))
            .ToList();
        var reorderedIds = orderedIds.Concat(remainingIds).ToList();
        for (var i = 0; i < reorderedIds.Count; i++)
        {
            itemMap[reorderedIds[i]].Sort = i + 1;
        }

        await SQLiteHelper.Instance.UpdateAllAsync(items);
        return 0;
    }

    public static async Task ClearGroup(string groupId)
    {
        if (groupId.IsNullOrEmpty())
        {
            return;
        }

        await SQLiteHelper.Instance.ExecuteAsync($"delete from FailoverGroupItem where GroupId = '{groupId.Replace("'", "''")}'");
    }

    public static async Task<FailoverBuildResult> TryBuildVirtualPolicyGroup(Config config, ProfileItem currentNode)
    {
        var result = NodeValidatorResult.Empty();
        if (config.FailoverMode == EFailoverMode.Off)
        {
            return new FailoverBuildResult(null, [], result);
        }

        var activeGroup = await GetActiveFailoverGroup(config);
        if (activeGroup == null)
        {
            result.Errors.Add(ResUI.MsgActivateFailoverGroupFirst);
            return new FailoverBuildResult(null, [], result);
        }

        var queueEntries = await GetQueueEntriesForMode(activeGroup.Id, true, config.FailoverMode);
        if (queueEntries.Count == 0)
        {
            result.Errors.Add(ResUI.MsgFailoverGroupQueueEmpty);
            return new FailoverBuildResult(null, [], result);
        }

        var currentCoreType = AppManager.Instance.GetCoreType(currentNode, currentNode.ConfigType);
        if (currentCoreType is not (ECoreType.Xray or ECoreType.sing_box))
        {
            result.Errors.Add(string.Format(ResUI.MsgFailoverCoreUnsupported, currentCoreType));
            return new FailoverBuildResult(null, [], result);
        }

        var mismatched = queueEntries
            .Select(entry => new
            {
                entry.Profile,
                CoreType = AppManager.Instance.GetCoreType(entry.Profile, entry.Profile.ConfigType)
            })
            .Where(entry => entry.CoreType != currentCoreType)
            .ToList();
        if (mismatched.Count > 0)
        {
            result.Errors.Add(ResUI.MsgFailoverGroupCoreTypeMixed);
            return new FailoverBuildResult(null, [], result);
        }

        var virtualNode = new ProfileItem
        {
            IndexId = $"{VirtualPolicyGroupPrefix}{activeGroup.Id}",
            ConfigType = EConfigType.PolicyGroup,
            CoreType = currentCoreType,
            Remarks = activeGroup.Remarks,
        };
        virtualNode.SetProtocolExtra(new ProtocolExtraItem
        {
            GroupType = EConfigType.PolicyGroup.ToString(),
            ChildItems = string.Join(",", queueEntries.Select(entry => entry.Profile.IndexId)),
            MultipleLoad = EMultipleLoad.Fallback,
        });

        return new FailoverBuildResult(virtualNode, queueEntries.Select(entry => entry.Profile).ToList(), result);
    }

    public static async Task<NodeValidatorResult> ValidateActiveFailoverGroup(Config config, ProfileItem currentNode)
    {
        return (await TryResolveRuntimeNode(config, currentNode)).ValidatorResult;
    }

    public static async Task<string?> GetRuntimeTargetProfileId(Config config, ProfileItem currentNode)
    {
        var resolveResult = await TryResolveRuntimeNode(config, currentNode);
        return resolveResult.Success ? resolveResult.RuntimeTargetProfileId : null;
    }

    private static bool CanUseCoreFallback(ECoreType currentCoreType, IReadOnlyList<FailoverQueueEntry> queueEntries)
    {
        if (currentCoreType is not (ECoreType.Xray or ECoreType.sing_box))
        {
            return false;
        }

        var queueCoreTypes = queueEntries
            .Select(entry => AppManager.Instance.GetCoreType(entry.Profile, entry.Profile.ConfigType))
            .Distinct()
            .ToList();

        return queueCoreTypes.Count == 1 && queueCoreTypes[0] == currentCoreType;
    }

    private static FailoverQueueEntry? PickRuntimeEntryForDirectProfile(
        IReadOnlyList<FailoverQueueEntry> queueEntries,
        EFailoverMode mode)
    {
        var enabledEntries = queueEntries.Where(entry => entry.Item.Enabled).ToList();
        if (enabledEntries.Count == 0)
        {
            return null;
        }

        if (mode == EFailoverMode.LeastDelay)
        {
            var bestEntry = enabledEntries
                .Where(entry => entry.Item.LastStatus == FailoverHealthStatus.Normal && entry.Item.LastDelay > 0)
                .OrderBy(entry => entry.Item.LastDelay)
                .ThenBy(entry => entry.Item.Sort)
                .FirstOrDefault();
            if (bestEntry != null)
            {
                return bestEntry;
            }
        }

        return enabledEntries.FirstOrDefault(entry => entry.Item.LastStatus != FailoverHealthStatus.Failed)
            ?? enabledEntries.First();
    }

    private static async Task NormalizeQueueSort(string groupId, List<FailoverGroupItem>? changedItems = null)
    {
        var enabledItems = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
            .Where(item => item.GroupId == groupId && item.Enabled)
            .ToListAsync();
        enabledItems = enabledItems.OrderBy(item => item.Sort).ThenBy(item => item.SourceProfileId).ToList();
        for (var i = 0; i < enabledItems.Count; i++)
        {
            enabledItems[i].Sort = i + 1;
        }

        var updateItems = new Dictionary<string, FailoverGroupItem>();
        foreach (var item in enabledItems)
        {
            updateItems[item.Id] = item;
        }
        foreach (var item in changedItems ?? [])
        {
            updateItems[item.Id] = item;
        }

        var inserts = updateItems.Values.Where(item => item.Id.IsNotEmpty()).ToList();
        foreach (var item in inserts)
        {
            await SQLiteHelper.Instance.ReplaceAsync(item);
        }
    }

    private static List<FailoverQueueEntry> ApplyLeastDelayRuntimeOrder(List<FailoverQueueEntry> entries)
    {
        if (entries.Count <= 1)
        {
            return entries;
        }

        var enabledEntries = entries.Where(entry => entry.Item.Enabled).ToList();
        var disabledEntries = entries.Where(entry => !entry.Item.Enabled).ToList();
        var availableEntries = enabledEntries
            .Where(entry => entry.Item.LastStatus != FailoverHealthStatus.Failed)
            .ToList();
        var sortableEntries = availableEntries.Count > 0 ? availableEntries : enabledEntries;
        var sortableIds = sortableEntries.Select(entry => entry.Item.Id).ToHashSet();

        var orderedEntries = entries
            .Select((entry, index) => new { Entry = entry, OriginalIndex = index })
            .Where(x => sortableIds.Contains(x.Entry.Item.Id))
            .OrderBy(x => GetLeastDelayRuntimeOrderBucket(x.Entry.Item))
            .ThenBy(x => x.Entry.Item.LastStatus == FailoverHealthStatus.Normal && x.Entry.Item.LastDelay > 0
                ? x.Entry.Item.LastDelay
                : int.MaxValue)
            .ThenBy(x => x.Entry.Item.Sort)
            .ThenBy(x => x.OriginalIndex)
            .Select(x => x.Entry)
            .ToList();

        return orderedEntries.Concat(disabledEntries).ToList();
    }

    private static List<FailoverQueueEntry> ApplyLeastDelayDisplayOrder(List<FailoverQueueEntry> entries)
    {
        if (entries.Count <= 1)
        {
            return entries;
        }

        var enabledEntries = entries.Where(entry => entry.Item.Enabled).ToList();
        var disabledEntries = entries.Where(entry => !entry.Item.Enabled).ToList();

        return enabledEntries
            .Select((entry, index) => new { Entry = entry, OriginalIndex = index })
            .OrderBy(x => GetLeastDelayRuntimeOrderBucket(x.Entry.Item))
            .ThenBy(x => x.Entry.Item.LastStatus == FailoverHealthStatus.Normal && x.Entry.Item.LastDelay > 0
                ? x.Entry.Item.LastDelay
                : int.MaxValue)
            .ThenBy(x => x.Entry.Item.Sort)
            .ThenBy(x => x.OriginalIndex)
            .Select(x => x.Entry)
            .Concat(disabledEntries)
            .ToList();
    }

    private static int GetLeastDelayRuntimeOrderBucket(FailoverGroupItem item)
    {
        if (item.LastStatus == FailoverHealthStatus.Normal && item.LastDelay > 0)
        {
            return 0;
        }

        if (item.LastStatus is FailoverHealthStatus.Unknown or FailoverHealthStatus.Probing)
        {
            return 1;
        }

        return 2;
    }
}
