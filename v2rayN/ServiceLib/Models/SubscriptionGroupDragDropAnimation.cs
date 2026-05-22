namespace ServiceLib.Models;

public static class SubscriptionGroupDragDropAnimation
{
    public static double GetItemOffset(int itemIndex, int sourceIndex, int targetIndex, double itemWidth)
    {
        if (itemIndex < 0 || sourceIndex < 0 || targetIndex < 0 || itemWidth <= 0 || sourceIndex == targetIndex)
        {
            return 0;
        }

        if (sourceIndex < targetIndex)
        {
            return itemIndex > sourceIndex && itemIndex <= targetIndex ? -itemWidth : 0;
        }

        return itemIndex >= targetIndex && itemIndex < sourceIndex ? itemWidth : 0;
    }
}
