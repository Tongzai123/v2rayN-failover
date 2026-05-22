using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ServiceLib.Models;
using ServiceLib.Services.FailoverRelay;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverRelaySocks5Tests
{
    [Fact]
    public async Task ReadClientConnectAsync_ParsesDomainConnect()
    {
        using var pair = LoopbackStreamPair.Create();
        var clientTask = Task.Run(async () =>
        {
            await pair.Client.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
            var greetingResponse = new byte[2];
            await pair.Client.ReadExactlyAsync(greetingResponse);
            await pair.Client.WriteAsync(new byte[]
            {
                0x05, 0x01, 0x00, 0x03, 0x0c,
                (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
                (byte)'.', (byte)'t', (byte)'e', (byte)'s', (byte)'t',
                0x01, 0xbb
            });
        });

        var request = await Socks5Handshake.ReadClientConnectAsync(pair.Server, CancellationToken.None);

        await clientTask;
        Assert.Equal("example.test", request.Host);
        Assert.Equal(443, request.Port);
    }

    [Fact]
    public async Task RelaySocks5_FirstCandidateClosedFallsBackToSecond()
    {
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p2 = await FailoverRelayTestHelpers.StartSocksCandidateAsync(p2Port, "example.test", 443);
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", FailoverRelayTestHelpers.GetUnusedLoopbackPort(), "P1"),
            new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", p2Port, "P2"),
        };
        var relay = new FailoverRelayService(new FailoverRelayOptions { ListenPort = 0 }, candidates);
        await relay.StartAsync(CancellationToken.None);

        var response = await FailoverRelayTestHelpers.SocksClientConnectRawAsync(relay.ListenPort, "example.test", 443);

        await relay.StopAsync();
        Assert.Equal(0x00, response.ReplyCode);
    }

    [Fact]
    public async Task RelaySocks5_FirstCandidateConnectSucceedsButPayloadStallsFallsBackToSecond()
    {
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p1 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p1Port,
            "p1-ok"u8.ToArray(),
            stallAfterConnect: true);
        using var p2 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p2Port,
            "p2-ok"u8.ToArray());
        var recentSuccessTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", p1Port, "P1", FailoverHealthStatus.Normal, recentSuccessTime),
            new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", p2Port, "P2", FailoverHealthStatus.Normal, recentSuccessTime),
        };
        var relay = new FailoverRelayService(new FailoverRelayOptions
        {
            ListenPort = 0,
            CandidateHandshakeTimeout = TimeSpan.FromMilliseconds(200),
            CandidateFirstByteTimeout = TimeSpan.FromMilliseconds(100),
        }, candidates);
        await relay.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.ListenPort);
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

        await stream.ReadExactlyAsync(payloadResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        await relay.StopAsync();
        Assert.Equal("p2-ok"u8.ToArray(), payloadResponse);
    }

    [Fact]
    public async Task RelaySocks5_DefaultSequentialMode_WaitsForFirstCandidateBeforeTryingSecond()
    {
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p1 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p1Port,
            "p1-ok"u8.ToArray(),
            stallAfterConnect: true);
        using var p2 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p2Port,
            "p2-ok"u8.ToArray());
        var recentSuccessTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", p1Port, "P1", FailoverHealthStatus.Normal, recentSuccessTime),
            new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", p2Port, "P2", FailoverHealthStatus.Normal, recentSuccessTime),
        };
        var relay = new FailoverRelayService(new FailoverRelayOptions
        {
            ListenPort = 0,
            CandidateFirstByteTimeout = TimeSpan.FromMilliseconds(300),
        }, candidates);
        await relay.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.ListenPort);
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
        var stopwatch = Stopwatch.StartNew();
        await stream.WriteAsync("client-hello"u8.ToArray());
        var payloadResponse = new byte[5];

        await stream.ReadExactlyAsync(payloadResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        await relay.StopAsync();
        Assert.Equal("p2-ok"u8.ToArray(), payloadResponse);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(250), $"Elapsed: {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task RelaySocks5_DefaultSequentialMode_ConnectsSecondOnlyAfterFirstTimesOut()
    {
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p1 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p1Port,
            "p1-ok"u8.ToArray(),
            stallAfterConnect: true);
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", p1Port, "P1"),
            new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", p2Port, "P2"),
        };
        var relay = new FailoverRelayService(new FailoverRelayOptions
        {
            ListenPort = 0,
            CandidateFirstByteTimeout = TimeSpan.FromMilliseconds(200),
        }, candidates);
        await relay.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.ListenPort);
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
        var p2StartTask = Task.Run(async () =>
        {
            await Task.Delay(80);
            return await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
                p2Port,
                "p2-ok"u8.ToArray());
        });
        await stream.WriteAsync("client-hello"u8.ToArray());
        var payloadResponse = new byte[5];

        await stream.ReadExactlyAsync(payloadResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        using var p2 = await p2StartTask;

        await relay.StopAsync();
        Assert.Equal("p2-ok"u8.ToArray(), payloadResponse);
    }

    [Fact]
    public async Task RelaySocks5_CandidateFailureAndSelection_AreReported()
    {
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p1 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p1Port,
            "p1-ok"u8.ToArray(),
            stallAfterConnect: true);
        using var p2 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p2Port,
            "p2-ok"u8.ToArray());
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", p1Port, "P1"),
            new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", p2Port, "P2"),
        };
        var failed = new List<string>();
        var selected = new List<string>();
        var statuses = new List<string>();
        var relay = new FailoverRelayService(new FailoverRelayOptions
        {
            ListenPort = 0,
            CandidateFirstByteTimeout = TimeSpan.FromMilliseconds(100),
            CandidateFailureReporterAsync = (candidate, protocol, target, reason) =>
            {
                failed.Add($"{candidate.ProfileId}:{reason}");
                return Task.FromResult(failed.Count);
            },
            CandidateSelectedReporterAsync = (candidate, protocol, target) =>
            {
                selected.Add(candidate.ProfileId);
                return Task.CompletedTask;
            },
            CandidateStatusReporterAsync = (candidate, protocol, target, status, reason) =>
            {
                statuses.Add($"{candidate.ProfileId}:{status}:{reason}");
                return Task.CompletedTask;
            },
        }, candidates);
        await relay.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.ListenPort);
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

        await stream.ReadExactlyAsync(payloadResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        await relay.StopAsync();
        Assert.Empty(failed);
        Assert.Equal(["p2"], selected);
        Assert.Contains("p1:Degraded:first-byte-timeout", statuses);
    }

    [Fact]
    public async Task RelaySocks5_FirstByteTimeoutMarksDegradedAndContinuesToSecondWithoutWaitingForProbe()
    {
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p1 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p1Port,
            "p1-ok"u8.ToArray(),
            stallAfterConnect: true);
        using var p2 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p2Port,
            "p2-ok"u8.ToArray());
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", p1Port, "P1"),
            new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", p2Port, "P2"),
        };
        var failed = new List<string>();
        var selected = new List<string>();
        var statuses = new List<string>();
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var relay = new FailoverRelayService(new FailoverRelayOptions
        {
            ListenPort = 0,
            CandidateFirstByteTimeout = TimeSpan.FromMilliseconds(100),
            HealthConfirmationTimeout = TimeSpan.FromSeconds(5),
            SpeedPingTestUrl = "https://probe.test/generate_204",
            HealthProbeAsync = async (candidate, probeUrl, timeout, token) =>
            {
                probeStarted.TrySetResult();
                await releaseProbe.Task.WaitAsync(token);
                return new FailoverRelayHealthProbeResult(true, "ok", 32);
            },
            CandidateFailureReporterAsync = (candidate, protocol, target, reason) =>
            {
                failed.Add($"{candidate.ProfileId}:{reason}");
                return Task.FromResult(failed.Count);
            },
            CandidateSelectedReporterAsync = (candidate, protocol, target) =>
            {
                selected.Add(candidate.ProfileId);
                return Task.CompletedTask;
            },
            CandidateStatusReporterAsync = (candidate, protocol, target, status, reason) =>
            {
                statuses.Add($"{candidate.ProfileId}:{status}:{reason}");
                return Task.CompletedTask;
            },
        }, candidates);
        await relay.StartAsync(CancellationToken.None);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, relay.ListenPort);
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

            await stream.ReadExactlyAsync(payloadResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal("p2-ok"u8.ToArray(), payloadResponse);
            Assert.Empty(failed);
            Assert.Equal(["p2"], selected);
            Assert.Contains("p1:Degraded:first-byte-timeout", statuses);
            Assert.Contains("p2:Requesting:selected", statuses);
        }
        finally
        {
            releaseProbe.TrySetResult();
            await relay.StopAsync();
        }
    }

    [Fact]
    public async Task RelaySocks5_FirstByteTimeoutWithFailedProbeAtThreshold_FallsBackAndWritesExternalFailure()
    {
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p3Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p1 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p1Port,
            "p1-ok"u8.ToArray(),
            stallAfterConnect: true);
        using var p2 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p2Port,
            "p2-ok"u8.ToArray(),
            stallAfterConnect: true);
        using var p3 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p3Port,
            "p3-ok"u8.ToArray());
        var recentSuccessTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", p1Port, "P1", FailoverHealthStatus.Normal, recentSuccessTime),
            new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", p2Port, "P2", FailoverHealthStatus.Normal, recentSuccessTime),
            new FailoverRelayCandidate("p3", "failover-p3-in", "proxy-3-p3", p3Port, "P3", FailoverHealthStatus.Normal, recentSuccessTime),
        };
        var failed = new List<string>();
        var selected = new List<string>();
        var statuses = new List<string>();
        var relay = new FailoverRelayService(new FailoverRelayOptions
        {
            ListenPort = 0,
            CandidateFirstByteTimeout = TimeSpan.FromMilliseconds(100),
            SpeedPingTestUrl = "https://probe.test/generate_204",
            HealthConfirmationFailureThreshold = 1,
            HealthProbeAsync = (candidate, probeUrl, timeout, token) =>
                Task.FromResult(new FailoverRelayHealthProbeResult(false, "timeout")),
            CandidateFailureReporterAsync = (candidate, protocol, target, reason) =>
            {
                failed.Add($"{candidate.ProfileId}:{reason}");
                return Task.FromResult(failed.Count);
            },
            CandidateSelectedReporterAsync = (candidate, protocol, target) =>
            {
                selected.Add(candidate.ProfileId);
                return Task.CompletedTask;
            },
            CandidateStatusReporterAsync = (candidate, protocol, target, status, reason) =>
            {
                statuses.Add($"{candidate.ProfileId}:{status}:{reason}");
                return Task.CompletedTask;
            },
        }, candidates);
        await relay.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.ListenPort);
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

        await stream.ReadExactlyAsync(payloadResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        await relay.StopAsync();
        Assert.Equal("p3-ok"u8.ToArray(), payloadResponse);
        Assert.Equal(["p1:all-probes-failed timeout:1", "p2:all-probes-failed timeout:1"], failed);
        Assert.Equal(["p3"], selected);
    }

    [Fact]
    public async Task RelaySocks5_ConcurrentFailedConfirmationsWriteExternalFailureOnce()
    {
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", p1Port, "P1"),
        };
        var failureReports = 0;
        var probeCount = 0;
        var relay = new FailoverRelayService(new FailoverRelayOptions
        {
            ListenPort = 0,
            CandidateConnectTimeout = TimeSpan.FromMilliseconds(50),
            HealthConfirmationProbeUrls = ["https://probe.test/a", "https://probe.test/b"],
            HealthConfirmationFailureThreshold = 1,
            HealthProbeAsync = async (_, _, _, token) =>
            {
                Interlocked.Increment(ref probeCount);
                await Task.Delay(100, token);
                return new FailoverRelayHealthProbeResult(false, "timeout");
            },
            CandidateFailureReporterAsync = (_, _, _, _) =>
            {
                var count = Interlocked.Increment(ref failureReports);
                return Task.FromResult(count);
            },
        }, candidates);
        await relay.StartAsync(CancellationToken.None);

        try
        {
            var results = await Task.WhenAll(
                    Enumerable.Range(0, 30)
                        .Select(_ => FailoverRelayTestHelpers.SocksClientConnectRawAsync(relay.ListenPort, "example.test", 443)))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.All(results, result => Assert.NotEqual(0x00, result.ReplyCode));
            Assert.Equal(2, probeCount);
            Assert.Equal(1, failureReports);
        }
        finally
        {
            await relay.StopAsync();
        }
    }

    [Fact]
    public async Task RelaySocks5_FirstByteTimeoutWithSuspectProbe_TriesSecondAndKeepsFirstDegraded()
    {
        var p1Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        var p2Port = FailoverRelayTestHelpers.GetUnusedLoopbackPort();
        using var p1 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p1Port,
            "p1-ok"u8.ToArray(),
            stallAfterConnect: true);
        using var p2 = await FailoverRelayTestHelpers.StartSocksCandidateWithFirstPayloadResponseAsync(
            p2Port,
            "p2-ok"u8.ToArray());
        var recentSuccessTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", p1Port, "P1", FailoverHealthStatus.Normal, recentSuccessTime),
            new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", p2Port, "P2", FailoverHealthStatus.Normal, recentSuccessTime),
        };
        var failed = new List<string>();
        var selected = new List<string>();
        var statuses = new List<string>();
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var relay = new FailoverRelayService(new FailoverRelayOptions
        {
            ListenPort = 0,
            CandidateFirstByteTimeout = TimeSpan.FromMilliseconds(100),
            SpeedPingTestUrl = "https://probe.test/generate_204",
            HealthConfirmationFailureThreshold = 3,
            HealthProbeAsync = async (candidate, probeUrl, timeout, token) =>
            {
                await releaseProbe.Task.WaitAsync(token);
                return new FailoverRelayHealthProbeResult(false, "timeout");
            },
            CandidateFailureReporterAsync = (candidate, protocol, target, reason) =>
            {
                failed.Add($"{candidate.ProfileId}:{reason}");
                return Task.FromResult(failed.Count);
            },
            CandidateSelectedReporterAsync = (candidate, protocol, target) =>
            {
                selected.Add(candidate.ProfileId);
                return Task.CompletedTask;
            },
            CandidateStatusReporterAsync = (candidate, protocol, target, status, reason) =>
            {
                statuses.Add($"{candidate.ProfileId}:{status}:{reason}");
                return Task.CompletedTask;
            },
        }, candidates);
        await relay.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.ListenPort);
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

        await stream.ReadExactlyAsync(payloadResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        releaseProbe.TrySetResult();
        await relay.StopAsync();
        Assert.Equal("p2-ok"u8.ToArray(), payloadResponse);
        Assert.Equal(["p2"], selected);
        Assert.Contains("p1:Degraded:first-byte-timeout", statuses);
    }

    [Fact]
    public async Task RelaySocks5_AllCandidatesClosed_ReturnsSocksFailure()
    {
        var candidates = new[]
        {
            new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", FailoverRelayTestHelpers.GetUnusedLoopbackPort(), "P1"),
            new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", FailoverRelayTestHelpers.GetUnusedLoopbackPort(), "P2"),
        };
        var relay = new FailoverRelayService(new FailoverRelayOptions { ListenPort = 0 }, candidates);
        await relay.StartAsync(CancellationToken.None);

        var response = await FailoverRelayTestHelpers.SocksClientConnectRawAsync(relay.ListenPort, "example.test", 80);

        await relay.StopAsync();
        Assert.Equal(0x01, response.ReplyCode);
    }
}
