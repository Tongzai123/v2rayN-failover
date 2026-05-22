using System.Net;
using System.Net.Sockets;
using System.Text;
using ServiceLib.Handler;
using Xunit;

namespace ServiceLib.Tests;

public class ConnectionHandlerTests
{
    [Fact]
    public async Task GetRealPingTime_CancelledTokenReturnsNegative()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var delay = await ConnectionHandler.GetRealPingTime("https://example.com", null, 10, cts.Token);

        Assert.Equal(-1, delay);
    }

    [Fact]
    public async Task GetRealPingTime_DoesNotDiscardCompletedMeasurementsWhenCancelledAfterSecondGet()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();

        var serverTask = ServeTwoResponsesAsync(listener, cts);

        var delay = await ConnectionHandler.GetRealPingTime($"http://127.0.0.1:{port}/", null, 10, cts.Token);

        Assert.True(delay > 0);
        await serverTask;
    }

    private static async Task ServeTwoResponsesAsync(TcpListener listener, CancellationTokenSource cts)
    {
        for (var i = 0; i < 2; i++)
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await Task.Delay(5);
            var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await stream.WriteAsync(response);

            if (i == 1)
            {
                cts.CancelAfter(10);
            }
        }
    }
}
