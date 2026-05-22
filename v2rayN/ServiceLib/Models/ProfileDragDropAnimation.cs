namespace ServiceLib.Models;

public record ProfileDragOverlayColumnMeasurement(
    string ColumnTag,
    double Left,
    double Width,
    double? ContentLeft = null,
    double? ContentWidth = null);

public record ProfileDragOverlayColumnSlot(string ColumnTag, double Left, double Width, double ContentLeft, double ContentWidth);

public static class ProfileDragDropAnimation
{
    public static int GetTopEdgeTargetIndex(double pointerY, double rowHeight, int itemCount)
    {
        if (rowHeight <= 0 || itemCount <= 0)
        {
            return -1;
        }

        return pointerY < rowHeight ? 0 : -1;
    }

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

    public static List<ProfileDragOverlayColumnSlot> BuildOverlayColumnSlots(
        IReadOnlyCollection<ProfileDragOverlayColumnMeasurement>? measurements,
        double contentPadding)
    {
        if (measurements == null || measurements.Count == 0)
        {
            return [];
        }

        var padding = Math.Max(0, contentPadding);
        return measurements
            .Where(measurement => measurement.Width > 0)
            .OrderBy(measurement => measurement.Left)
            .Select(measurement =>
            {
                var contentLeft = measurement.ContentLeft.HasValue
                    ? Math.Clamp(measurement.ContentLeft.Value, measurement.Left, measurement.Left + measurement.Width)
                    : measurement.Left + padding;
                var contentWidth = measurement.ContentWidth is > 0
                    ? Math.Min(measurement.ContentWidth.Value, Math.Max(0, measurement.Left + measurement.Width - contentLeft))
                    : Math.Max(0, measurement.Width - 2 * padding);
                return new ProfileDragOverlayColumnSlot(
                    measurement.ColumnTag,
                    measurement.Left,
                    measurement.Width,
                    contentLeft,
                    contentWidth);
            })
            .ToList();
    }
}
