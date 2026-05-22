namespace ServiceLib.Services.FailoverRelay;

public static class StreamRelay
{
    public static async Task PumpAsync(Stream left, Stream right, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leftToRight = CopyAsync(left, right, linkedCts.Token);
        var rightToLeft = CopyAsync(right, left, linkedCts.Token);
        await Task.WhenAny(leftToRight, rightToLeft);
        linkedCts.Cancel();
    }

    private static async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
