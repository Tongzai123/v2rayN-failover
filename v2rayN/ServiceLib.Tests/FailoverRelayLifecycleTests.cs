using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Helper;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverRelayLifecycleTests
{
    [Fact]
    public async Task StartAsync_ValidRuntimeStartsRelayAndExposesPort()
    {
        var runtime = new FailoverRelayRuntime
        {
            ListenPort = 0,
            Candidates = [new("p1", "failover-p1-in", "proxy-1-p1", FailoverRelayTestHelpers.GetUnusedLoopbackPort(), "P1")]
        };

        var result = await FailoverRelayManager.Instance.StartAsync(runtime);
        var currentListenPort = FailoverRelayManager.Instance.CurrentListenPort;

        await FailoverRelayManager.Instance.StopAsync();
        Assert.True(result.Success, result.Msg);
        Assert.True(currentListenPort > 0);
    }

    [Fact]
    public async Task StopAsync_ClearsCurrentPort()
    {
        await FailoverRelayManager.Instance.StopAsync();

        Assert.Equal(0, FailoverRelayManager.Instance.CurrentListenPort);
    }

    [Fact]
    public async Task RelaySelection_UpdateExternalFailoverLabels()
    {
        var groupId = $"relay-status-{Guid.NewGuid():N}";
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        EnsureTables();
        EnsureAppConfig();
        AppManager.Instance.Config.ActiveFailoverGroupId = groupId;
        await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
        {
            Id = $"{groupId}-p1",
            GroupId = groupId,
            SourceProfileId = "p1",
            FailoverProfileId = "p1",
            Sort = 1,
            Enabled = true,
            LastStatus = FailoverHealthStatus.Normal,
        });
        await SQLiteHelper.Instance.ReplaceAsync(new FailoverGroupItem
        {
            Id = $"{groupId}-p2",
            GroupId = groupId,
            SourceProfileId = "p2",
            FailoverProfileId = "p2",
            Sort = 2,
            Enabled = true,
            LastStatus = FailoverHealthStatus.Unknown,
        });
        using var p1 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p1Port,
            "p1-ok"u8.ToArray());
        using var p2 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p2Port,
            "p2-ok"u8.ToArray());
        var recentSuccessTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var runtime = new FailoverRelayRuntime
        {
            ListenPort = 0,
            Candidates =
            [
                new("p1", "failover-p1-in", "proxy-1-p1", p1Port, "P1", FailoverHealthStatus.Normal, recentSuccessTime),
                new("p2", "failover-p2-in", "proxy-2-p2", p2Port, "P2", FailoverHealthStatus.Normal, recentSuccessTime),
            ],
        };
        var result = await FailoverRelayManager.Instance.StartAsync(runtime);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, FailoverRelayManager.Instance.CurrentListenPort);
        await using var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var greetingResponse = new byte[2];
        await stream.ReadExactlyAsync(greetingResponse);
        await stream.WriteAsync(new byte[]
        {
            0x05, 0x01, 0x00, 0x03, 0x0c,
            (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            (byte)'.', (byte)'t', (byte)'e', (byte)'s', (byte)'t',
            0x01, 0xbb
        });
        var connectResponse = new byte[10];
        await stream.ReadExactlyAsync(connectResponse);
        await stream.WriteAsync("client-hello"u8.ToArray());
        var payloadResponse = new byte[5];
        await stream.ReadExactlyAsync(payloadResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        var activeProfileId = FailoverRelayManager.Instance.CurrentActiveProfileId;

        await FailoverRelayManager.Instance.StopAsync();
        var stored = await SQLiteHelper.Instance.TableAsync<FailoverGroupItem>()
            .Where(item => item.GroupId == groupId)
            .ToListAsync();
        await SQLiteHelper.Instance.ExecuteAsync($"delete from FailoverGroupItem where GroupId = '{groupId}'");

        Assert.True(result.Success, result.Msg);
        Assert.Equal("p1-ok"u8.ToArray(), payloadResponse);
        Assert.Equal("p1", activeProfileId);
        Assert.Equal(FailoverHealthStatus.Normal, stored.Single(item => item.SourceProfileId == "p1").LastStatus);
        Assert.Equal(FailoverHealthStatus.Unknown, stored.Single(item => item.SourceProfileId == "p2").LastStatus);
        Assert.Equal(0, stored.Single(item => item.SourceProfileId == "p1").FailureCount);
    }

    private static void EnsureTables()
    {
        SQLiteHelper.Instance.CreateTable<FailoverGroupItem>();
    }

    private static void EnsureAppConfig()
    {
        if (AppManager.Instance.Config is not null)
        {
            return;
        }

        var field = typeof(AppManager).GetField("_config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AppManager._config field was not found");
        field.SetValue(AppManager.Instance, new Config { ActiveFailoverGroupId = string.Empty });
    }
}
