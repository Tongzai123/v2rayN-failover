using ServiceLib.Handler;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests;

public class ProfileDragDropSortTests
{
    [Fact]
    public void FindProfileMoveIndexByIndexId_ReturnsSourceRowIndex()
    {
        var profiles = new List<ProfileItem>
        {
            new() { IndexId = "first" },
            new() { IndexId = "dragged" },
            new() { IndexId = "last" },
        };

        var index = ProfilesViewModel.FindProfileMoveIndexByIndexId(profiles, "dragged");

        Assert.Equal(1, index);
    }

    [Fact]
    public void FindProfileMoveIndexByIndexId_ReturnsNegativeOneForUnknownSource()
    {
        var profiles = new List<ProfileItem>
        {
            new() { IndexId = "first" },
            new() { IndexId = "last" },
        };

        var index = ProfilesViewModel.FindProfileMoveIndexByIndexId(profiles, "missing");

        Assert.Equal(-1, index);
    }

    [Fact]
    public void UIItem_EnableDragDropSort_DefaultsToEnabled()
    {
        var item = new UIItem();

        Assert.True(item.EnableDragDropSort);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void ShouldSelectSourceItem_PreservesMultiSelectionModifierGestures(
        bool control,
        bool shift,
        bool meta,
        bool expected)
    {
        Assert.Equal(expected, ProfileDragDropSelection.ShouldSelectSourceItem(control, shift, meta));
    }

    [Fact]
    public void GetDragSourceIds_SourceInMultiSelection_ReturnsSelectedIdsInDisplayOrder()
    {
        var result = ProfileDragDropSelection.GetDragSourceIds(
            ["A", "B", "C", "D", "E"],
            ["D", "B"],
            "D");

        Assert.Equal(["B", "D"], result);
    }

    [Fact]
    public void GetDragSourceIds_SourceOutsideSelection_ReturnsOnlySource()
    {
        var result = ProfileDragDropSelection.GetDragSourceIds(
            ["A", "B", "C", "D"],
            ["B", "C"],
            "D");

        Assert.Equal(["D"], result);
    }

    [Fact]
    public void GetDragSourceIds_SingleSelection_ReturnsOnlySource()
    {
        var result = ProfileDragDropSelection.GetDragSourceIds(
            ["A", "B", "C"],
            ["B"],
            "B");

        Assert.Equal(["B"], result);
    }

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

    [Fact]
    public void GetTopEdgeTargetIndex_PointerInTopDropSlot_ReturnsFirstIndex()
    {
        var targetIndex = ProfileDragDropAnimation.GetTopEdgeTargetIndex(20, 28, 3);

        Assert.Equal(0, targetIndex);
    }

    [Theory]
    [InlineData(28, 28, 3)]
    [InlineData(8, 0, 3)]
    [InlineData(8, 28, 0)]
    public void GetTopEdgeTargetIndex_PointerOutsideTopDropSlotOrInvalidInput_ReturnsNegativeOne(
        double pointerY,
        double rowHeight,
        int itemCount)
    {
        Assert.Equal(-1, ProfileDragDropAnimation.GetTopEdgeTargetIndex(pointerY, rowHeight, itemCount));
    }

    [Fact]
    public void MoveBlockToTarget_NonContiguousSourcesDown_CollapsesIntoContiguousBlock()
    {
        var result = ProfileDragDropBlockMove.MoveToTarget(
            ["A", "B", "C", "D", "E", "F", "G"],
            ["B", "D", "E"],
            "G");

        Assert.Equal(["A", "C", "F", "G", "B", "D", "E"], result);
    }

    [Fact]
    public void MoveBlockToTarget_SingleSourceDown_InsertsAfterTarget()
    {
        var result = ProfileDragDropBlockMove.MoveToTarget(
            ["A", "B", "C", "D"],
            ["B"],
            "D");

        Assert.Equal(["A", "C", "D", "B"], result);
    }

    [Fact]
    public void MoveBlockToTarget_SingleSourceUp_InsertsBeforeTarget()
    {
        var result = ProfileDragDropBlockMove.MoveToTarget(
            ["A", "B", "C", "D"],
            ["D"],
            "B");

        Assert.Equal(["A", "D", "B", "C"], result);
    }

    [Fact]
    public void MoveBlockToTarget_NonContiguousSourcesUp_CollapsesIntoContiguousBlock()
    {
        var result = ProfileDragDropBlockMove.MoveToTarget(
            ["A", "B", "C", "D", "E", "F", "G"],
            ["C", "F"],
            "A");

        Assert.Equal(["C", "F", "A", "B", "D", "E", "G"], result);
    }

    [Fact]
    public void MoveBlockToTarget_TargetInsideSourceSpan_ReturnsOriginalOrder()
    {
        var result = ProfileDragDropBlockMove.MoveToTarget(
            ["A", "B", "C", "D", "E", "F"],
            ["B", "D"],
            "C");

        Assert.Equal(["A", "B", "C", "D", "E", "F"], result);
    }

    [Fact]
    public void MoveBlockToTarget_TargetIsSource_ReturnsOriginalOrder()
    {
        var result = ProfileDragDropBlockMove.MoveToTarget(
            ["A", "B", "C", "D", "E"],
            ["B", "D"],
            "D");

        Assert.Equal(["A", "B", "C", "D", "E"], result);
    }

    [Fact]
    public void MoveBlockToTarget_UnknownSource_ReturnsOriginalOrder()
    {
        var result = ProfileDragDropBlockMove.MoveToTarget(
            ["A", "B", "C"],
            ["missing"],
            "C");

        Assert.Equal(["A", "B", "C"], result);
    }

    [Theory]
    [InlineData(null, "A")]
    [InlineData("A", null)]
    public void MoveBlockToTarget_InvalidInput_ReturnsOriginalOrder(string? sourceId, string? targetId)
    {
        var result = ProfileDragDropBlockMove.MoveToTarget(
            ["A", "B", "C"],
            sourceId == null ? [] : [sourceId],
            targetId);

        Assert.Equal(["A", "B", "C"], result);
    }

    [Fact]
    public void GetBlockRowOffset_SourceBlockAboveTarget_ShiftsRowsUpByBlockHeight()
    {
        var rowHeight = 28d;

        Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(0, [1, 3, 4], 6, rowHeight));
        Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(1, [1, 3, 4], 6, rowHeight));
        Assert.Equal(-rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(2, [1, 3, 4], 6, rowHeight));
        Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(3, [1, 3, 4], 6, rowHeight));
        Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(4, [1, 3, 4], 6, rowHeight));
        Assert.Equal(-3 * rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(5, [1, 3, 4], 6, rowHeight));
        Assert.Equal(-3 * rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(6, [1, 3, 4], 6, rowHeight));
    }

    [Fact]
    public void GetBlockRowOffset_SourceBlockBelowTarget_ShiftsRowsDownByBlockHeight()
    {
        var rowHeight = 28d;

        Assert.Equal(2 * rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(0, [2, 5], 0, rowHeight));
        Assert.Equal(2 * rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(1, [2, 5], 0, rowHeight));
        Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(2, [2, 5], 0, rowHeight));
        Assert.Equal(rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(3, [2, 5], 0, rowHeight));
        Assert.Equal(rowHeight, ProfileDragDropAnimation.GetBlockRowOffset(4, [2, 5], 0, rowHeight));
        Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(5, [2, 5], 0, rowHeight));
    }

    [Theory]
    [InlineData(-1, 1, 3, 28)]
    [InlineData(2, -1, 3, 28)]
    [InlineData(2, 1, -1, 28)]
    [InlineData(2, 1, 3, 0)]
    public void GetBlockRowOffset_InvalidInput_ReturnsZero(int rowIndex, int sourceIndex, int targetIndex, double rowHeight)
    {
        Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(rowIndex, [sourceIndex], targetIndex, rowHeight));
    }

    [Fact]
    public void GetBlockRowOffset_TargetInsideSourceSpan_ReturnsZero()
    {
        var rowHeight = 28d;

        Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(0, [1, 3], 2, rowHeight));
        Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(1, [1, 3], 2, rowHeight));
        Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(2, [1, 3], 2, rowHeight));
        Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(3, [1, 3], 2, rowHeight));
        Assert.Equal(0, ProfileDragDropAnimation.GetBlockRowOffset(4, [1, 3], 2, rowHeight));
    }

    [Fact]
    public void BuildOverlayColumnSlots_PreservesMeasuredCellBoundsAndUsesInnerPadding()
    {
        var slots = ProfileDragDropAnimation.BuildOverlayColumnSlots(
            [
                new ProfileDragOverlayColumnMeasurement("ConfigType", 0, 80),
                new ProfileDragOverlayColumnMeasurement("Remarks", 80, 260),
                new ProfileDragOverlayColumnMeasurement("Address", 340, 120),
            ],
            8);

        Assert.Equal(3, slots.Count);
        Assert.Equal(0, slots[0].Left);
        Assert.Equal(80, slots[0].Width);
        Assert.Equal(8, slots[0].ContentLeft);
        Assert.Equal(64, slots[0].ContentWidth);
        Assert.Equal(80, slots[1].Left);
        Assert.Equal(260, slots[1].Width);
        Assert.Equal(88, slots[1].ContentLeft);
        Assert.Equal(244, slots[1].ContentWidth);
        Assert.Equal(340, slots[2].Left);
        Assert.Equal(120, slots[2].Width);
        Assert.Equal(348, slots[2].ContentLeft);
        Assert.Equal(104, slots[2].ContentWidth);
    }

    [Fact]
    public void BuildOverlayColumnSlots_UsesMeasuredContentBoundsWhenAvailable()
    {
        var slots = ProfileDragDropAnimation.BuildOverlayColumnSlots(
            [
                new ProfileDragOverlayColumnMeasurement("Address", 340, 120, 356, 76),
            ],
            8);

        Assert.Single(slots);
        Assert.Equal(340, slots[0].Left);
        Assert.Equal(120, slots[0].Width);
        Assert.Equal(356, slots[0].ContentLeft);
        Assert.Equal(76, slots[0].ContentWidth);
    }

    [Fact]
    public async Task MoveServers_NonContiguousSources_RewritesSortAsContiguousBlock()
    {
        var profiles = new List<ProfileItem>
        {
            new() { IndexId = "A" },
            new() { IndexId = "B" },
            new() { IndexId = "C" },
            new() { IndexId = "D" },
            new() { IndexId = "E" },
            new() { IndexId = "F" },
            new() { IndexId = "G" },
        };

        var result = await ConfigHandler.MoveServers(new Config(), profiles, ["B", "D", "E"], "G");

        Assert.Equal(0, result);
        var ordered = profiles
            .OrderBy(item => ProfileExManager.Instance.GetSort(item.IndexId))
            .Select(item => item.IndexId)
            .ToArray();
        Assert.Equal(["A", "C", "F", "G", "B", "D", "E"], ordered);
    }

    [Fact]
    public async Task MoveServers_SingleSourceDown_RewritesSortAfterTarget()
    {
        var profiles = new List<ProfileItem>
        {
            new() { IndexId = "A" },
            new() { IndexId = "B" },
            new() { IndexId = "C" },
            new() { IndexId = "D" },
        };

        var result = await ConfigHandler.MoveServers(new Config(), profiles, ["B"], "D");

        Assert.Equal(0, result);
        var ordered = profiles
            .OrderBy(item => ProfileExManager.Instance.GetSort(item.IndexId))
            .Select(item => item.IndexId)
            .ToArray();
        Assert.Equal(["A", "C", "D", "B"], ordered);
    }

    [Fact]
    public async Task MoveServers_UnknownSource_ReturnsFailureAndKeepsSort()
    {
        var profiles = new List<ProfileItem>
        {
            new() { IndexId = "A" },
            new() { IndexId = "B" },
            new() { IndexId = "C" },
        };

        var result = await ConfigHandler.MoveServers(new Config(), profiles, ["missing"], "C");

        Assert.Equal(-1, result);
    }

    [Fact]
    public async Task MoveServers_TargetInsideSourceSpan_DoesNotRewriteSort()
    {
        var profiles = new List<ProfileItem>
        {
            new() { IndexId = "span-A" },
            new() { IndexId = "span-B" },
            new() { IndexId = "span-C" },
            new() { IndexId = "span-D" },
            new() { IndexId = "span-E" },
        };

        var result = await ConfigHandler.MoveServers(new Config(), profiles, ["span-B", "span-D"], "span-C");

        Assert.Equal(0, result);
        Assert.All(profiles, item => Assert.Equal(0, ProfileExManager.Instance.GetSort(item.IndexId)));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void CanMoveTransferGroupItems_RequiresAllSourcesAndTargetInSamePinnedArea(
        bool sourceInQueue,
        bool targetInQueue,
        bool expected)
    {
        var result = ProfilesViewModel.CanMoveTransferGroupItems(
            true,
            [sourceInQueue, sourceInQueue],
            targetInQueue);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CanMoveTransferGroupItems_RejectsMixedSourcesWhenQueuePinned()
    {
        var result = ProfilesViewModel.CanMoveTransferGroupItems(true, [true, false], true);

        Assert.False(result);
    }

    [Fact]
    public void CanMoveTransferGroupItems_RejectsMixedSourcesEvenWhenQueueNotPinned()
    {
        var result = ProfilesViewModel.CanMoveTransferGroupItems(false, [true, false], false);

        Assert.False(result);
    }
}
