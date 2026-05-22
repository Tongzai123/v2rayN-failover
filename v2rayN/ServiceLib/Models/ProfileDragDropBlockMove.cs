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

        var movingDown = targetOriginalIndex > sourceIndexes[^1];
        var block = sourceIndexes.Select(index => ordered[index]).ToList();
        var remaining = ordered.Where(id => !sourceSet.Contains(id)).ToList();
        var targetIndex = remaining.IndexOf(targetId);
        if (targetIndex < 0)
        {
            return ordered;
        }
        if (movingDown)
        {
            targetIndex++;
        }

        remaining.InsertRange(targetIndex, block);
        return remaining;
    }
}
