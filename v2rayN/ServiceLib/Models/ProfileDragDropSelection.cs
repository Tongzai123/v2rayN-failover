namespace ServiceLib.Models;

public static class ProfileDragDropSelection
{
    public static bool ShouldSelectSourceItem(bool control, bool shift, bool meta)
    {
        return !control && !shift && !meta;
    }

    public static List<string> GetDragSourceIds(
        IReadOnlyList<string>? displayIds,
        IEnumerable<string?>? selectedIds,
        string? sourceId)
    {
        if (sourceId.IsNullOrEmpty())
        {
            return [];
        }

        var selectedSet = (selectedIds ?? [])
            .Where(id => id.IsNotEmpty())
            .Distinct()
            .ToHashSet();
        if (selectedSet.Count <= 1 || !selectedSet.Contains(sourceId))
        {
            return [sourceId];
        }

        var ordered = (displayIds ?? [])
            .Where(id => id.IsNotEmpty() && selectedSet.Contains(id))
            .Distinct()
            .ToList();
        return ordered.Count > 1 ? ordered : [sourceId];
    }
}
