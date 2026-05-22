# 本地故障转移中继层实施计划

> **面向 agentic workers：** REQUIRED SUB-SKILL：实现本计划时使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans`。所有步骤使用复选框（`- [ ]`）跟踪执行状态。

**目标：** 为 Xray-only 普通故障转移队列实现本地请求级 relay，使 P1 当前请求连接失败后，在尚未向客户端返回失败前继续尝试 P2。

**架构：** v2rayN 进程内新增 `FailoverRelayService`，它监听本地 mixed-like 入口，解析 SOCKS5 TCP CONNECT 和 HTTP CONNECT，并按队列优先级连接 Xray 为每个候选生成的 loopback `mixed` 入站。Xray 只负责“候选入站到候选 outbound”的固定路由，不再用 `balancer + fallbackTag` 承担请求级重试。

**技术栈：** `.NET`、C#、`TcpListener` / `TcpClient`、`NetworkStream`、Xray `mixed` inbound、xUnit、现有 `ServiceLib`、现有 `CoreConfigV2rayService` 和 `CoreManager` 生命周期。

---

## 范围边界

- 第一版只启用普通故障转移模式：`Config.FailoverMode == EFailoverMode.Failover`。
- 第一版只启用 Xray-only 队列：所有候选核心类型必须是 `ECoreType.Xray`。
- 第一版只支持 SOCKS5 TCP CONNECT 和 HTTP CONNECT。
- 第一版遇到普通 HTTP 明文请求时返回代理错误，不重放请求。
- 第一版不支持 UDP、QUIC、HTTP/3、sing-box、混合核心队列、链式代理队列和 Tun 自动启用。
- Tun 模式必须在门禁中禁用 relay，直到独立验证任务通过后再打开。
- 所有候选失败时只返回代理失败，不直连。
- relay 与候选 Xray 入站都只监听 `127.0.0.1`。
- 日志只记录候选 tag、失败类型和状态变化，不记录目标完整 URL、节点密钥、订阅链接、Token、UUID、`privateKey`、`shortId`、`serverName`。

## 文件结构

### 新增生产文件

- `v2rayN/ServiceLib/Models/FailoverRelayCandidate.cs`：候选入口数据结构，包含候选 profile、Xray 入站端口、入站 tag、outbound tag、运行状态。
- `v2rayN/ServiceLib/Models/FailoverRelayOptions.cs`：relay 固定运行参数，例如候选连接超时、握手超时、最大头部大小。
- `v2rayN/ServiceLib/Models/FailoverRelayRuntime.cs`：配置生成与运行时之间传递的 relay 启动计划。
- `v2rayN/ServiceLib/Services/FailoverRelay/FailoverCandidateStateStore.cs`：维护 `Healthy`、`Failed`、`Recovering` 状态和健康检测回写入口。
- `v2rayN/ServiceLib/Services/FailoverRelay/Socks5ConnectRequest.cs`：SOCKS5 CONNECT 请求模型。
- `v2rayN/ServiceLib/Services/FailoverRelay/Socks5Handshake.cs`：SOCKS5 服务端解析和候选端握手。
- `v2rayN/ServiceLib/Services/FailoverRelay/HttpConnectRequest.cs`：HTTP CONNECT 请求模型。
- `v2rayN/ServiceLib/Services/FailoverRelay/HttpConnectHandshake.cs`：HTTP CONNECT 服务端解析和候选端握手。
- `v2rayN/ServiceLib/Services/FailoverRelay/StreamRelay.cs`：候选成功后的双向 TCP 转发。
- `v2rayN/ServiceLib/Services/FailoverRelay/FailoverRelayService.cs`：本地 relay 生命周期、监听、协议识别、候选尝试和错误响应。
- `v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayFailoverRelayConfigService.cs`：为 Xray 生成每候选 loopback inbound 和固定 routing rule。
- `v2rayN/ServiceLib/Manager/FailoverRelayManager.cs`：协调 relay 端口分配、启动门禁、停止和当前运行态。

### 修改生产文件

- `v2rayN/ServiceLib/Models/CoreConfigContext.cs`：增加 `FailoverRelayRuntime? FailoverRelayRuntime`，让配置生成服务知道是否生成候选入站。
- `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`：新增 relay 运行态解析，不再把 Xray-only 普通故障转移强制包装成旧 `VirtualPolicyGroup`。
- `v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`：在普通故障转移、Xray-only、非 Tun 时构造 relay runtime；Tun 或不支持场景保持现有路径或返回明确验证错误。
- `v2rayN/ServiceLib/Services/CoreConfig/V2ray/CoreConfigV2rayService.cs`：在 relay runtime 存在时走 `GenerateClientFailoverRelayConfigContent()` 分支。
- `v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayOutboundService.cs`：复用现有 `BuildProxyOutbound`，为每个候选生成稳定 outbound tag。
- `v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayRoutingService.cs`：relay 模式下不把用户规则转成 `balancerTag`，proxy 规则指向 relay 入口后的候选固定规则。
- `v2rayN/ServiceLib/Manager/CoreManager.cs`：核心启动后确认 Xray 进程存活，再启动 relay；relay 启动失败时停止新核心并报告失败。
- `v2rayN/ServiceLib/Handler/SysProxy/SysProxyHandler.cs`：系统代理/PAC 端口使用 relay 运行端口；无 relay 时仍使用原本 socks 端口。
- `v2rayN/ServiceLib/Services/FailoverHealthService.cs`：健康检测成功时通知候选状态进入 `Recovering`，失败时保持 `Failed`。

### 新增测试文件

- `v2rayN/ServiceLib.Tests/FailoverRelaySocks5Tests.cs`：SOCKS5 解析、候选失败后尝试 P2、全失败时不直连。
- `v2rayN/ServiceLib.Tests/FailoverRelayHttpConnectTests.cs`：HTTP CONNECT 解析、候选失败后尝试 P2、普通 HTTP 明文请求失败。
- `v2rayN/ServiceLib.Tests/FailoverRelayStateTests.cs`：候选状态机。
- `v2rayN/ServiceLib.Tests/CoreConfigV2rayFailoverRelayTests.cs`：Xray 配置结构。
- `v2rayN/ServiceLib.Tests/FailoverRelayRuntimeGateTests.cs`：Xray-only、Tun、sing-box、混合核心和空队列门禁。

---

## Task 1：定义 relay 数据结构和状态模型

**Files:**
- Create: `v2rayN/ServiceLib/Models/FailoverRelayCandidate.cs`
- Create: `v2rayN/ServiceLib/Models/FailoverRelayOptions.cs`
- Create: `v2rayN/ServiceLib/Models/FailoverRelayRuntime.cs`
- Create: `v2rayN/ServiceLib.Tests/FailoverRelayStateTests.cs`

- [ ] **Step 1：先写状态机测试**

在 `v2rayN/ServiceLib.Tests/FailoverRelayStateTests.cs` 写入：

```csharp
using ServiceLib.Models;
using ServiceLib.Services.FailoverRelay;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverRelayStateTests
{
    [Fact]
    public void MarkRequestFailure_MarksCandidateFailedAndSkipsNewRequests()
    {
        var p1 = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var p2 = new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", 21002, "P2");
        var store = new FailoverCandidateStateStore([p1, p2]);

        store.MarkRequestFailure("p1");

        Assert.Equal(FailoverRelayCandidateState.Failed, store.GetState("p1"));
        Assert.Equal(["p2"], store.GetRequestCandidates().Select(x => x.ProfileId).ToArray());
    }

    [Fact]
    public void MarkHealthRecovered_AllowsCandidateAsRecoveringThenHealthyAfterSuccess()
    {
        var p1 = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var store = new FailoverCandidateStateStore([p1]);

        store.MarkRequestFailure("p1");
        store.MarkHealthRecovered("p1");
        Assert.Equal(FailoverRelayCandidateState.Recovering, store.GetState("p1"));
        Assert.Equal(["p1"], store.GetRequestCandidates().Select(x => x.ProfileId).ToArray());

        store.MarkRequestSuccess("p1");
        Assert.Equal(FailoverRelayCandidateState.Healthy, store.GetState("p1"));
    }

    [Fact]
    public void AllFailed_ReturnsAllInPriorityOrderSoRequestCanFailWithoutDirect()
    {
        var p1 = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var p2 = new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", 21002, "P2");
        var store = new FailoverCandidateStateStore([p1, p2]);

        store.MarkRequestFailure("p1");
        store.MarkRequestFailure("p2");

        Assert.Equal(["p1", "p2"], store.GetRequestCandidates().Select(x => x.ProfileId).ToArray());
        Assert.True(store.AllCandidatesFailed);
    }
}
```

- [ ] **Step 2：运行测试并确认失败**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelayStateTests"
```

Expected: 编译失败，错误包含 `FailoverRelayCandidate` 或 `FailoverCandidateStateStore` 未定义。

- [ ] **Step 3：新增候选数据结构**

在 `v2rayN/ServiceLib/Models/FailoverRelayCandidate.cs` 写入：

```csharp
namespace ServiceLib.Models;

public enum FailoverRelayCandidateState
{
    Healthy,
    Failed,
    Recovering
}

public sealed record FailoverRelayCandidate(
    string ProfileId,
    string InboundTag,
    string OutboundTag,
    int InboundPort,
    string DisplayName);
```

- [ ] **Step 4：新增 relay 参数**

在 `v2rayN/ServiceLib/Models/FailoverRelayOptions.cs` 写入：

```csharp
namespace ServiceLib.Models;

public sealed record FailoverRelayOptions
{
    public int ListenPort { get; init; }
    public TimeSpan CandidateConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan CandidateHandshakeTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public int MaxHttpHeaderBytes { get; init; } = 16 * 1024;
}
```

- [ ] **Step 5：新增 relay runtime**

在 `v2rayN/ServiceLib/Models/FailoverRelayRuntime.cs` 写入：

```csharp
namespace ServiceLib.Models;

public sealed record FailoverRelayRuntime
{
    public int ListenPort { get; init; }
    public IReadOnlyList<FailoverRelayCandidate> Candidates { get; init; } = [];
    public bool Enabled => ListenPort > 0 && Candidates.Count > 0;
}
```

- [ ] **Step 6：新增状态存储**

在 `v2rayN/ServiceLib/Services/FailoverRelay/FailoverCandidateStateStore.cs` 写入：

```csharp
namespace ServiceLib.Services.FailoverRelay;

using ServiceLib.Models;

public sealed class FailoverCandidateStateStore
{
    private readonly object _lock = new();
    private readonly List<FailoverRelayCandidate> _candidates;
    private readonly Dictionary<string, FailoverRelayCandidateState> _states;

    public FailoverCandidateStateStore(IEnumerable<FailoverRelayCandidate> candidates)
    {
        _candidates = candidates.ToList();
        _states = _candidates.ToDictionary(x => x.ProfileId, _ => FailoverRelayCandidateState.Healthy);
    }

    public bool AllCandidatesFailed
    {
        get
        {
            lock (_lock)
            {
                return _states.Count > 0 && _states.Values.All(x => x == FailoverRelayCandidateState.Failed);
            }
        }
    }

    public FailoverRelayCandidateState GetState(string profileId)
    {
        lock (_lock)
        {
            return _states.TryGetValue(profileId, out var state) ? state : FailoverRelayCandidateState.Failed;
        }
    }

    public IReadOnlyList<FailoverRelayCandidate> GetRequestCandidates()
    {
        lock (_lock)
        {
            var available = _candidates
                .Where(x => _states.GetValueOrDefault(x.ProfileId) != FailoverRelayCandidateState.Failed)
                .ToList();
            return available.Count > 0 ? available : _candidates.ToList();
        }
    }

    public void MarkRequestFailure(string profileId)
    {
        lock (_lock)
        {
            if (_states.ContainsKey(profileId))
            {
                _states[profileId] = FailoverRelayCandidateState.Failed;
            }
        }
    }

    public void MarkRequestSuccess(string profileId)
    {
        lock (_lock)
        {
            if (_states.ContainsKey(profileId))
            {
                _states[profileId] = FailoverRelayCandidateState.Healthy;
            }
        }
    }

    public void MarkHealthRecovered(string profileId)
    {
        lock (_lock)
        {
            if (_states.ContainsKey(profileId))
            {
                _states[profileId] = FailoverRelayCandidateState.Recovering;
            }
        }
    }
}
```

- [ ] **Step 7：运行测试并确认通过**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelayStateTests"
```

Expected: `FailoverRelayStateTests` 全部通过。

---

## Task 2：实现 SOCKS5 CONNECT 解析与候选握手

**Files:**
- Create: `v2rayN/ServiceLib/Services/FailoverRelay/Socks5ConnectRequest.cs`
- Create: `v2rayN/ServiceLib/Services/FailoverRelay/Socks5Handshake.cs`
- Create: `v2rayN/ServiceLib.Tests/FailoverRelaySocks5Tests.cs`

- [ ] **Step 1：写 SOCKS5 解析测试**

在 `v2rayN/ServiceLib.Tests/FailoverRelaySocks5Tests.cs` 写入初始测试：

```csharp
using System.Net;
using System.Net.Sockets;
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
}
```

同文件底部加入测试辅助类：

```csharp
internal sealed class LoopbackStreamPair : IDisposable
{
    private readonly TcpListener _listener;
    private readonly TcpClient _clientTcp;
    private readonly TcpClient _serverTcp;

    private LoopbackStreamPair(TcpListener listener, TcpClient clientTcp, TcpClient serverTcp)
    {
        _listener = listener;
        _clientTcp = clientTcp;
        _serverTcp = serverTcp;
        Client = clientTcp.GetStream();
        Server = serverTcp.GetStream();
    }

    public NetworkStream Client { get; }
    public NetworkStream Server { get; }

    public static LoopbackStreamPair Create()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var client = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync();
        client.Connect(IPAddress.Loopback, port);
        return new LoopbackStreamPair(listener, client, acceptTask.GetAwaiter().GetResult());
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        _clientTcp.Dispose();
        _serverTcp.Dispose();
        _listener.Stop();
    }
}
```

- [ ] **Step 2：运行测试并确认失败**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelaySocks5Tests.ReadClientConnectAsync_ParsesDomainConnect"
```

Expected: 编译失败，错误包含 `Socks5Handshake` 未定义。

- [ ] **Step 3：新增请求模型**

在 `v2rayN/ServiceLib/Services/FailoverRelay/Socks5ConnectRequest.cs` 写入：

```csharp
namespace ServiceLib.Services.FailoverRelay;

public sealed record Socks5ConnectRequest(string Host, int Port);
```

- [ ] **Step 4：实现 SOCKS5 解析和候选握手**

在 `v2rayN/ServiceLib/Services/FailoverRelay/Socks5Handshake.cs` 写入：

```csharp
using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace ServiceLib.Services.FailoverRelay;

public static class Socks5Handshake
{
    public static async Task<Socks5ConnectRequest> ReadClientConnectAsync(Stream stream, CancellationToken cancellationToken)
    {
        var head = await ReadExactAsync(stream, 2, cancellationToken);
        if (head[0] != 0x05)
        {
            throw new InvalidDataException("Unsupported SOCKS version.");
        }

        var methods = await ReadExactAsync(stream, head[1], cancellationToken);
        if (!methods.Contains((byte)0x00))
        {
            await stream.WriteAsync(new byte[] { 0x05, 0xff }, cancellationToken);
            throw new InvalidDataException("SOCKS no-auth method is not supported by client.");
        }

        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken);

        var req = await ReadExactAsync(stream, 4, cancellationToken);
        if (req[0] != 0x05 || req[1] != 0x01)
        {
            throw new InvalidDataException("Only SOCKS5 CONNECT is supported.");
        }

        var host = req[3] switch
        {
            0x01 => new IPAddress(await ReadExactAsync(stream, 4, cancellationToken)).ToString(),
            0x03 => Encoding.ASCII.GetString(await ReadExactAsync(stream, (await ReadExactAsync(stream, 1, cancellationToken))[0], cancellationToken)),
            0x04 => new IPAddress(await ReadExactAsync(stream, 16, cancellationToken)).ToString(),
            _ => throw new InvalidDataException("Unsupported SOCKS address type.")
        };
        var portBytes = await ReadExactAsync(stream, 2, cancellationToken);
        var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
        return new Socks5ConnectRequest(host, port);
    }

    public static async Task<bool> ConnectCandidateAsync(Stream stream, Socks5ConnectRequest request, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken);
        var methodResponse = await ReadExactAsync(stream, 2, cancellationToken);
        if (methodResponse[0] != 0x05 || methodResponse[1] != 0x00)
        {
            return false;
        }

        var hostBytes = Encoding.ASCII.GetBytes(request.Host);
        if (hostBytes.Length is <= 0 or > 255)
        {
            return false;
        }

        var buffer = new byte[7 + hostBytes.Length];
        buffer[0] = 0x05;
        buffer[1] = 0x01;
        buffer[2] = 0x00;
        buffer[3] = 0x03;
        buffer[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(buffer.AsSpan(5));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(5 + hostBytes.Length), (ushort)request.Port);
        await stream.WriteAsync(buffer, cancellationToken);

        var response = await ReadExactAsync(stream, 4, cancellationToken);
        if (response[0] != 0x05 || response[1] != 0x00)
        {
            return false;
        }

        var addressLength = response[3] switch
        {
            0x01 => 4,
            0x03 => (await ReadExactAsync(stream, 1, cancellationToken))[0],
            0x04 => 16,
            _ => 0
        };
        if (addressLength <= 0)
        {
            return false;
        }
        _ = await ReadExactAsync(stream, addressLength + 2, cancellationToken);
        return true;
    }

    public static Task WriteClientSuccessAsync(Stream stream, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0 }, cancellationToken).AsTask();
    }

    public static Task WriteClientFailureAsync(Stream stream, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, 0, 0 }, cancellationToken).AsTask();
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        return buffer;
    }
}
```

- [ ] **Step 5：运行 SOCKS5 测试并确认通过**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelaySocks5Tests"
```

Expected: `FailoverRelaySocks5Tests` 已有测试通过。

---

## Task 3：实现 HTTP CONNECT 解析与候选握手

**Files:**
- Create: `v2rayN/ServiceLib/Services/FailoverRelay/HttpConnectRequest.cs`
- Create: `v2rayN/ServiceLib/Services/FailoverRelay/HttpConnectHandshake.cs`
- Modify: `v2rayN/ServiceLib.Tests/FailoverRelayHttpConnectTests.cs`

- [ ] **Step 1：写 HTTP CONNECT 测试**

在 `v2rayN/ServiceLib.Tests/FailoverRelayHttpConnectTests.cs` 写入：

```csharp
using ServiceLib.Services.FailoverRelay;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverRelayHttpConnectTests
{
    [Fact]
    public async Task ReadClientConnectAsync_ParsesConnectTarget()
    {
        using var input = new MemoryStream("CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n"u8.ToArray());

        var request = await HttpConnectHandshake.ReadClientConnectAsync(input, 16 * 1024, CancellationToken.None);

        Assert.Equal("example.test", request.Host);
        Assert.Equal(443, request.Port);
        Assert.Contains("CONNECT example.test:443 HTTP/1.1", request.RawHeaderText);
    }

    [Fact]
    public async Task ReadClientConnectAsync_RejectsPlainHttpRequest()
    {
        using var input = new MemoryStream("GET http://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n"u8.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HttpConnectHandshake.ReadClientConnectAsync(input, 16 * 1024, CancellationToken.None));
    }
}
```

- [ ] **Step 2：运行测试并确认失败**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelayHttpConnectTests"
```

Expected: 编译失败，错误包含 `HttpConnectHandshake` 未定义。

- [ ] **Step 3：新增请求模型**

在 `v2rayN/ServiceLib/Services/FailoverRelay/HttpConnectRequest.cs` 写入：

```csharp
namespace ServiceLib.Services.FailoverRelay;

public sealed record HttpConnectRequest(string Host, int Port, string RawHeaderText);
```

- [ ] **Step 4：实现 HTTP CONNECT 解析和候选握手**

在 `v2rayN/ServiceLib/Services/FailoverRelay/HttpConnectHandshake.cs` 写入：

```csharp
using System.Text;

namespace ServiceLib.Services.FailoverRelay;

public static class HttpConnectHandshake
{
    public static async Task<HttpConnectRequest> ReadClientConnectAsync(Stream stream, int maxHeaderBytes, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(512);
        var buffer = new byte[1];
        while (bytes.Count < maxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }
            bytes.Add(buffer[0]);
            if (bytes.Count >= 4
                && bytes[^4] == '\r'
                && bytes[^3] == '\n'
                && bytes[^2] == '\r'
                && bytes[^1] == '\n')
            {
                break;
            }
        }

        if (bytes.Count >= maxHeaderBytes)
        {
            throw new InvalidDataException("HTTP CONNECT header is too large.");
        }

        var header = Encoding.ASCII.GetString(bytes.ToArray());
        var firstLine = header.Split("\r\n", StringSplitOptions.None).FirstOrDefault() ?? "";
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !string.Equals(parts[0], "CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only HTTP CONNECT is supported.");
        }

        var target = parts[1];
        var lastColon = target.LastIndexOf(':');
        if (lastColon <= 0 || !int.TryParse(target[(lastColon + 1)..], out var port) || port <= 0 || port > 65535)
        {
            throw new InvalidDataException("Invalid HTTP CONNECT target.");
        }

        var host = target[..lastColon].Trim('[', ']');
        if (host.IsNullOrEmpty())
        {
            throw new InvalidDataException("Invalid HTTP CONNECT host.");
        }

        return new HttpConnectRequest(host, port, header);
    }

    public static async Task<bool> ConnectCandidateAsync(Stream stream, HttpConnectRequest request, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.RawHeaderText), cancellationToken);

        var response = await ReadResponseHeaderAsync(stream, 16 * 1024, cancellationToken);
        var firstLine = response.Split("\r\n", StringSplitOptions.None).FirstOrDefault() ?? "";
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) && code >= 200 && code < 300;
    }

    public static Task WriteClientSuccessAsync(Stream stream, CancellationToken cancellationToken)
    {
        return stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), cancellationToken).AsTask();
    }

    public static Task WriteClientFailureAsync(Stream stream, CancellationToken cancellationToken)
    {
        return stream.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), cancellationToken).AsTask();
    }

    private static async Task<string> ReadResponseHeaderAsync(Stream stream, int maxHeaderBytes, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(512);
        var buffer = new byte[1];
        while (bytes.Count < maxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }
            bytes.Add(buffer[0]);
            if (bytes.Count >= 4
                && bytes[^4] == '\r'
                && bytes[^3] == '\n'
                && bytes[^2] == '\r'
                && bytes[^1] == '\n')
            {
                break;
            }
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
```

- [ ] **Step 5：运行 HTTP CONNECT 测试并确认通过**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelayHttpConnectTests"
```

Expected: `FailoverRelayHttpConnectTests` 全部通过。

---

## Task 4：实现 relay 服务的候选尝试与双向转发

**Files:**
- Create: `v2rayN/ServiceLib/Services/FailoverRelay/StreamRelay.cs`
- Create: `v2rayN/ServiceLib/Services/FailoverRelay/FailoverRelayService.cs`
- Modify: `v2rayN/ServiceLib.Tests/FailoverRelaySocks5Tests.cs`
- Modify: `v2rayN/ServiceLib.Tests/FailoverRelayHttpConnectTests.cs`

- [ ] **Step 1：写集成式 relay 测试**

在 `FailoverRelaySocks5Tests` 增加测试：

```csharp
[Fact]
public async Task RelaySocks5_P1Closed_CurrentRequestFallsBackToP2()
{
    await using var p2 = await FakeSocksHttpServer.StartAsync("p2-ok");
    var p1Port = GetUnusedLoopbackPort();
    var candidates = new[]
    {
        new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", p1Port, "P1"),
        new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", p2.Port, "P2"),
    };
    var relay = new FailoverRelayService(new FailoverRelayOptions { ListenPort = GetUnusedLoopbackPort() }, candidates);
    await relay.StartAsync(CancellationToken.None);

    var body = await SocksClientGetAsync(relay.ListenPort, "example.test", 80);

    await relay.StopAsync();
    Assert.Contains("p2-ok", body);
}
```

在 `FailoverRelayHttpConnectTests` 增加测试：

```csharp
[Fact]
public async Task RelayHttpConnect_P1Closed_CurrentRequestFallsBackToP2()
{
    await using var p2 = await FakeSocksHttpServer.StartAsync("p2-ok");
    var p1Port = GetUnusedLoopbackPort();
    var candidates = new[]
    {
        new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", p1Port, "P1"),
        new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", p2.Port, "P2"),
    };
    var relay = new FailoverRelayService(new FailoverRelayOptions { ListenPort = GetUnusedLoopbackPort() }, candidates);
    await relay.StartAsync(CancellationToken.None);

    var body = await HttpConnectClientGetAsync(relay.ListenPort, "example.test", 80);

    await relay.StopAsync();
    Assert.Contains("p2-ok", body);
}
```

同测试文件增加共享测试辅助类 `FakeSocksHttpServer`、`SocksClientGetAsync`、`HttpConnectClientGetAsync`、`GetUnusedLoopbackPort`。辅助类只监听 loopback，返回固定 body，不访问外网。

- [ ] **Step 2：运行测试并确认失败**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~RelaySocks5|FullyQualifiedName~RelayHttpConnect"
```

Expected: 编译失败，错误包含 `FailoverRelayService` 或 `FakeSocksHttpServer` 未定义。

- [ ] **Step 3：实现双向转发工具**

在 `v2rayN/ServiceLib/Services/FailoverRelay/StreamRelay.cs` 写入：

```csharp
namespace ServiceLib.Services.FailoverRelay;

public static class StreamRelay
{
    public static async Task CopyBothWaysAsync(Stream left, Stream right, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leftToRight = CopyOneWayAsync(left, right, cts.Token);
        var rightToLeft = CopyOneWayAsync(right, left, cts.Token);
        await Task.WhenAny(leftToRight, rightToLeft);
        await cts.CancelAsync();
        await Task.WhenAll(SwallowAsync(leftToRight), SwallowAsync(rightToLeft));
    }

    private static async Task CopyOneWayAsync(Stream source, Stream target, CancellationToken cancellationToken)
    {
        await source.CopyToAsync(target, cancellationToken);
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task;
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
```

- [ ] **Step 4：实现 relay 服务**

在 `v2rayN/ServiceLib/Services/FailoverRelay/FailoverRelayService.cs` 写入：

```csharp
using System.Net;
using System.Net.Sockets;
using ServiceLib.Models;

namespace ServiceLib.Services.FailoverRelay;

public sealed class FailoverRelayService
{
    private readonly FailoverRelayOptions _options;
    private readonly FailoverCandidateStateStore _stateStore;
    private readonly CancellationTokenSource _stopCts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public FailoverRelayService(FailoverRelayOptions options, IEnumerable<FailoverRelayCandidate> candidates)
    {
        _options = options;
        _stateStore = new FailoverCandidateStateStore(candidates);
    }

    public int ListenPort { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new TcpListener(IPAddress.Loopback, _options.ListenPort);
        _listener.Start();
        ListenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stopCts.Token), cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        await _stopCts.CancelAsync();
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var _ = client.ConfigureAwait(false);
        client.NoDelay = true;
        var clientStream = client.GetStream();

        var firstByte = await ReadFirstByteAsync(clientStream, cancellationToken);
        if (firstByte == 0x05)
        {
            await HandleSocks5ClientAsync(clientStream, cancellationToken);
            return;
        }

        if (IsHttpMethodStart(firstByte))
        {
            await HandleHttpClientAsync(firstByte, clientStream, cancellationToken);
            return;
        }
    }

    private async Task HandleSocks5ClientAsync(Stream clientStream, CancellationToken cancellationToken)
    {
        using var replay = new PrefixReplayStream(0x05, clientStream);
        var request = await Socks5Handshake.ReadClientConnectAsync(replay, cancellationToken);
        var result = await TryConnectCandidateAsync(
            request.Host,
            request.Port,
            candidateStream => Socks5Handshake.ConnectCandidateAsync(candidateStream, request, cancellationToken),
            cancellationToken);

        if (result.Stream is null)
        {
            await Socks5Handshake.WriteClientFailureAsync(clientStream, cancellationToken);
            return;
        }

        await Socks5Handshake.WriteClientSuccessAsync(clientStream, cancellationToken);
        await using (result.Client)
        {
            await StreamRelay.CopyBothWaysAsync(clientStream, result.Stream, cancellationToken);
        }
    }

    private async Task HandleHttpClientAsync(byte firstByte, Stream clientStream, CancellationToken cancellationToken)
    {
        using var replay = new PrefixReplayStream(firstByte, clientStream);
        HttpConnectRequest request;
        try
        {
            request = await HttpConnectHandshake.ReadClientConnectAsync(replay, _options.MaxHttpHeaderBytes, cancellationToken);
        }
        catch (InvalidDataException)
        {
            await HttpConnectHandshake.WriteClientFailureAsync(clientStream, cancellationToken);
            return;
        }

        var result = await TryConnectCandidateAsync(
            request.Host,
            request.Port,
            candidateStream => HttpConnectHandshake.ConnectCandidateAsync(candidateStream, request, cancellationToken),
            cancellationToken);

        if (result.Stream is null)
        {
            await HttpConnectHandshake.WriteClientFailureAsync(clientStream, cancellationToken);
            return;
        }

        await HttpConnectHandshake.WriteClientSuccessAsync(clientStream, cancellationToken);
        await using (result.Client)
        {
            await StreamRelay.CopyBothWaysAsync(clientStream, result.Stream, cancellationToken);
        }
    }

    private async Task<(TcpClient? Client, Stream? Stream)> TryConnectCandidateAsync(
        string host,
        int port,
        Func<Stream, Task<bool>> handshake,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in _stateStore.GetRequestCandidates())
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_options.CandidateHandshakeTimeout);

                var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(IPAddress.Loopback, candidate.InboundPort, timeoutCts.Token);
                var stream = tcp.GetStream();
                if (await handshake(stream))
                {
                    _stateStore.MarkRequestSuccess(candidate.ProfileId);
                    return (tcp, stream);
                }

                tcp.Dispose();
                _stateStore.MarkRequestFailure(candidate.ProfileId);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                _stateStore.MarkRequestFailure(candidate.ProfileId);
            }
        }

        return (null, null);
    }

    private static async Task<byte> ReadFirstByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        return buffer[0];
    }

    private static bool IsHttpMethodStart(byte value)
    {
        return value is (byte)'C' or (byte)'G' or (byte)'P' or (byte)'H' or (byte)'D' or (byte)'O' or (byte)'T';
    }
}
```

同文件底部加入 `PrefixReplayStream`，用于把协议识别时读出的首字节放回解析流：

```csharp
internal sealed class PrefixReplayStream : Stream
{
    private readonly byte _prefix;
    private readonly Stream _inner;
    private bool _prefixRead;

    public PrefixReplayStream(byte prefix, Stream inner)
    {
        _prefix = prefix;
        _inner = inner;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!_prefixRead && count > 0)
        {
            buffer[offset] = _prefix;
            _prefixRead = true;
            return 1;
        }
        return _inner.Read(buffer, offset, count);
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_prefixRead && buffer.Length > 0)
        {
            buffer.Span[0] = _prefix;
            _prefixRead = true;
            return 1;
        }
        return await _inner.ReadAsync(buffer, cancellationToken);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
```

- [ ] **Step 5：运行 relay 行为测试**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelaySocks5Tests|FullyQualifiedName~FailoverRelayHttpConnectTests"
```

Expected: SOCKS5 与 HTTP CONNECT 的 P1 失败转 P2 测试通过；普通 HTTP 明文请求返回失败。

---

## Task 5：生成 Xray 候选 loopback 入站和固定路由

**Files:**
- Create: `v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayFailoverRelayConfigService.cs`
- Modify: `v2rayN/ServiceLib/Models/CoreConfigContext.cs`
- Modify: `v2rayN/ServiceLib/Services/CoreConfig/V2ray/CoreConfigV2rayService.cs`
- Modify: `v2rayN/ServiceLib.Tests/CoreConfigV2rayFailoverRelayTests.cs`

- [ ] **Step 1：写配置结构测试**

在 `v2rayN/ServiceLib.Tests/CoreConfigV2rayFailoverRelayTests.cs` 写入：

```csharp
using System.Text.Json.Nodes;
using ServiceLib;
using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests;

public class CoreConfigV2rayFailoverRelayTests
{
    [Fact]
    public void GenerateClientConfigContent_RelayRuntimeBuildsPerCandidateInboundsAndRules()
    {
        var p1 = CreateProxyNode("p1", "198.51.100.11", 443);
        var p2 = CreateProxyNode("p2", "198.51.100.12", 443);
        var group = CreatePolicyGroupNode("fallback-group", EMultipleLoad.Fallback, p1, p2);
        var runtime = new FailoverRelayRuntime
        {
            ListenPort = 20808,
            Candidates =
            [
                new("p1", "failover-p1-in", "proxy-1-p1", 21001, "p1"),
                new("p2", "failover-p2-in", "proxy-2-p2", 21002, "p2"),
            ]
        };

        var service = new CoreConfigV2rayService(CreateContext(group, runtime, new Dictionary<string, ProfileItem>
        {
            [p1.IndexId] = p1,
            [p2.IndexId] = p2,
        }));

        var result = service.GenerateClientConfigContent();

        Assert.True(result.Success, result.Msg);
        var root = JsonNode.Parse(result.Data!.ToString())!.AsObject();
        var inbounds = root["inbounds"]!.AsArray().Select(x => x!.AsObject()).ToList();
        var rules = root["routing"]!["rules"]!.AsArray().Select(x => x!.AsObject()).ToList();
        var outbounds = root["outbounds"]!.AsArray().Select(x => x!.AsObject()).ToList();

        Assert.Contains(inbounds, x => x["tag"]!.GetValue<string>() == "failover-p1-in"
            && x["listen"]!.GetValue<string>() == Global.Loopback
            && x["port"]!.GetValue<int>() == 21001
            && x["protocol"]!.GetValue<string>() == "mixed");
        Assert.Contains(inbounds, x => x["tag"]!.GetValue<string>() == "failover-p2-in"
            && x["listen"]!.GetValue<string>() == Global.Loopback
            && x["port"]!.GetValue<int>() == 21002
            && x["protocol"]!.GetValue<string>() == "mixed");
        Assert.Contains(rules, x => x["inboundTag"]!.AsArray().Any(tag => tag!.GetValue<string>() == "failover-p1-in")
            && x["outboundTag"]!.GetValue<string>() == "proxy-1-p1");
        Assert.Contains(rules, x => x["inboundTag"]!.AsArray().Any(tag => tag!.GetValue<string>() == "failover-p2-in")
            && x["outboundTag"]!.GetValue<string>() == "proxy-2-p2");
        Assert.DoesNotContain(root["routing"]?["balancers"]?.AsArray() ?? [], x => x?["tag"]?.GetValue<string>() == Global.ProxyTag + Global.BalancerTagSuffix);
        Assert.Contains(outbounds, x => x["tag"]!.GetValue<string>() == "proxy-1-p1");
        Assert.Contains(outbounds, x => x["tag"]!.GetValue<string>() == "proxy-2-p2");
    }
}
```

同文件复用 `CoreConfigV2rayServiceTests` 中的 `CreateConfig`、`CreateProxyNode`、`CreatePolicyGroupNode` 模式；如果 helper 是 private，则复制必要 helper 到本测试文件。

- [ ] **Step 2：运行测试并确认失败**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreConfigV2rayFailoverRelayTests"
```

Expected: 编译失败或测试失败，原因是 `CoreConfigContext.FailoverRelayRuntime` 或配置生成分支不存在。

- [ ] **Step 3：扩展 `CoreConfigContext`**

在 `v2rayN/ServiceLib/Models/CoreConfigContext.cs` 的 failover 运行态字段附近加入：

```csharp
public FailoverRelayRuntime? FailoverRelayRuntime { get; init; }
```

- [ ] **Step 4：新增 Xray relay 配置生成分支**

在 `CoreConfigV2rayService.GenerateClientConfigContent()` 中，在普通 `_node` 校验之后、`GenInbounds()` 之前加入分支：

```csharp
if (context.FailoverRelayRuntime is { Enabled: true })
{
    return GenerateClientFailoverRelayConfigContent();
}
```

在 `v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayFailoverRelayConfigService.cs` 写入：

```csharp
namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigV2rayService
{
    private RetResult GenerateClientFailoverRelayConfigContent()
    {
        var ret = new RetResult();
        var result = EmbedUtils.GetEmbedText(Global.V2raySampleClient);
        if (result.IsNullOrEmpty())
        {
            ret.Msg = ResUI.FailedGetDefaultConfiguration;
            return ret;
        }

        _coreConfig = JsonUtils.Deserialize<V2rayConfig>(result);
        if (_coreConfig == null || context.FailoverRelayRuntime is not { Enabled: true } runtime)
        {
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }

        GenLog();
        _coreConfig.inbounds = [];
        _coreConfig.outbounds.Clear();
        _coreConfig.routing.rules.Clear();
        _coreConfig.routing.balancers = null;

        foreach (var candidate in runtime.Candidates)
        {
            if (!context.AllProxiesMap.TryGetValue(candidate.ProfileId, out var profile))
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }

            _coreConfig.inbounds.Add(new Inbounds4Ray
            {
                tag = candidate.InboundTag,
                listen = Global.Loopback,
                port = candidate.InboundPort,
                protocol = EInboundProtocol.mixed.ToString(),
                settings = new(),
                sniffing = new()
            });

            var outbound = new CoreConfigV2rayService(context with { Node = profile }).BuildProxyOutbound(candidate.OutboundTag);
            _coreConfig.outbounds.Insert(0, outbound);
            _coreConfig.routing.rules.Add(new RulesItem4Ray
            {
                type = "field",
                inboundTag = [candidate.InboundTag],
                outboundTag = candidate.OutboundTag,
            });
        }

        GenDns();
        GenStatistic();
        ApplyOutboundSendThrough();
        ret.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
        ret.Success = true;
        ret.Data = ApplyFullConfigTemplate();
        return ret;
    }
}
```

- [ ] **Step 5：运行配置测试并确认通过**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreConfigV2rayFailoverRelayTests"
```

Expected: 新配置测试通过，旧 `FallbackPolicyGroupUsesPriorityAwareBalancer` 仍失败，下一任务改造旧测试预期。

---

## Task 6：改造运行态解析和门禁

**Files:**
- Modify: `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- Modify: `v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`
- Create: `v2rayN/ServiceLib.Tests/FailoverRelayRuntimeGateTests.cs`
- Modify: `v2rayN/ServiceLib.Tests/CoreConfigV2rayServiceTests.cs`

- [ ] **Step 1：写门禁测试**

在 `v2rayN/ServiceLib.Tests/FailoverRelayRuntimeGateTests.cs` 写入测试覆盖：

```csharp
using ServiceLib.Enums;
using ServiceLib.Handler.Builder;
using ServiceLib.Manager;
using ServiceLib.Models;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverRelayRuntimeGateTests
{
    [Fact]
    public async Task Build_XrayFailoverNonTun_CreatesRelayRuntime()
    {
        var config = TestConfigFactory.CreateFailoverConfig(enableTun: false, mode: EFailoverMode.Failover);
        var current = TestConfigFactory.CreateProxyNode("current", ECoreType.Xray);
        await TestConfigFactory.InsertFailoverQueueAsync(config, ECoreType.Xray, "p1", "p2");

        var result = await CoreConfigContextBuilder.Build(config, current);

        Assert.True(result.Success, string.Join("\n", result.ValidatorResult.Errors));
        Assert.NotNull(result.Context.FailoverRelayRuntime);
        Assert.Equal(2, result.Context.FailoverRelayRuntime!.Candidates.Count);
    }

    [Fact]
    public async Task Build_XrayFailoverTun_DoesNotCreateRelayRuntime()
    {
        var config = TestConfigFactory.CreateFailoverConfig(enableTun: true, mode: EFailoverMode.Failover);
        var current = TestConfigFactory.CreateProxyNode("current", ECoreType.Xray);
        await TestConfigFactory.InsertFailoverQueueAsync(config, ECoreType.Xray, "p1", "p2");

        var result = await CoreConfigContextBuilder.Build(config, current);

        Assert.True(result.Success, string.Join("\n", result.ValidatorResult.Errors));
        Assert.Null(result.Context.FailoverRelayRuntime);
    }

    [Fact]
    public async Task Build_LeastDelay_DoesNotCreateRelayRuntime()
    {
        var config = TestConfigFactory.CreateFailoverConfig(enableTun: false, mode: EFailoverMode.LeastDelay);
        var current = TestConfigFactory.CreateProxyNode("current", ECoreType.Xray);
        await TestConfigFactory.InsertFailoverQueueAsync(config, ECoreType.Xray, "p1", "p2");

        var result = await CoreConfigContextBuilder.Build(config, current);

        Assert.True(result.Success, string.Join("\n", result.ValidatorResult.Errors));
        Assert.Null(result.Context.FailoverRelayRuntime);
    }
}
```

如果项目没有 `TestConfigFactory`，在本测试文件内新增 private helper，直接复用 `FailoverGroupManagerTests` 中已有的 SQLite 初始化、`SubItem` 和 `FailoverGroupItem` 插入方式。

- [ ] **Step 2：运行门禁测试并确认失败**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelayRuntimeGateTests"
```

Expected: 编译失败或断言失败，原因是 runtime 未生成。

- [ ] **Step 3：在构建上下文中创建 relay runtime**

在 `CoreConfigContextBuilder.Build()` 中，`FailoverGroupManager.TryResolveRuntimeNode` 后增加逻辑：

```csharp
var relayRuntime = await FailoverGroupManager.TryBuildRelayRuntime(config, node, context);
if (relayRuntime is not null)
{
    context = context with
    {
        FailoverRelayRuntime = relayRuntime,
        FailoverRuntimeTargetProfileId = relayRuntime.Candidates.FirstOrDefault()?.ProfileId,
        FailoverRuntimeQueueSignature = FailoverGroupManager.BuildRuntimeQueueSignature(
            relayRuntime.Candidates.Select(candidate => context.AllProxiesMap[candidate.ProfileId]))
    };
    return new CoreConfigContextBuilderResult(context, validatorResult);
}
```

新增 `FailoverGroupManager.TryBuildRelayRuntime(Config config, ProfileItem currentNode, CoreConfigContext context)`：

```csharp
public static async Task<FailoverRelayRuntime?> TryBuildRelayRuntime(Config config, ProfileItem currentNode, CoreConfigContext context)
{
    if (config.FailoverMode != EFailoverMode.Failover || config.TunModeItem.EnableTun)
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
    var basePort = Utils.GetFreePort();
    for (var i = 0; i < entries.Count; i++)
    {
        var profile = entries[i].Profile;
        context.AllProxiesMap[profile.IndexId] = profile;
        candidates.Add(new FailoverRelayCandidate(
            profile.IndexId,
            $"failover-p{i + 1}-in",
            $"{Global.ProxyTag}-{i + 1}-{profile.Remarks}",
            basePort + i,
            profile.Remarks));
    }

    return new FailoverRelayRuntime
    {
        ListenPort = Utils.GetFreePort(basePort + entries.Count + 1),
        Candidates = candidates
    };
}
```

- [ ] **Step 4：更新旧 balancer 测试**

将 `CoreConfigV2rayServiceTests` 中三条旧 fallback balancer 测试保留为“非 relay 的普通 PolicyGroup 仍可用”，并新增说明性测试：

```csharp
[Fact]
public void GenerateClientConfigContent_FallbackRelayRuntimeDoesNotCreateBalancer()
{
    // 使用 CoreConfigV2rayFailoverRelayTests 中的 runtime 方式断言 routing.balancers 为空。
}
```

不要删除旧测试的覆盖意图；把旧测试改成显式不传 `FailoverRelayRuntime` 的 PolicyGroup 配置，避免普通 PolicyGroup 退化。

- [ ] **Step 5：运行门禁和配置测试**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelayRuntimeGateTests|FullyQualifiedName~CoreConfigV2ray"
```

Expected: relay runtime 门禁测试通过；旧普通 PolicyGroup balancer 测试仍通过；relay runtime 配置测试通过。

---

## Task 7：接入核心生命周期和入口切换门禁

**Files:**
- Create: `v2rayN/ServiceLib/Manager/FailoverRelayManager.cs`
- Modify: `v2rayN/ServiceLib/Manager/CoreManager.cs`
- Modify: `v2rayN/ServiceLib/Handler/SysProxy/SysProxyHandler.cs`
- Modify: `v2rayN/ServiceLib/Manager/PacManager.cs`
- Create: `v2rayN/ServiceLib.Tests/FailoverRelayLifecycleTests.cs`

- [ ] **Step 1：写生命周期测试**

在 `FailoverRelayLifecycleTests.cs` 写：

```csharp
using ServiceLib.Manager;
using ServiceLib.Models;
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
            Candidates = [new("p1", "failover-p1-in", "proxy-1-p1", GetUnusedLoopbackPort(), "P1")]
        };

        var result = await FailoverRelayManager.Instance.StartAsync(runtime);

        await FailoverRelayManager.Instance.StopAsync();
        Assert.True(result.Success, result.Msg);
        Assert.True(FailoverRelayManager.Instance.CurrentListenPort > 0);
    }

    [Fact]
    public async Task StopAsync_ClearsCurrentPort()
    {
        await FailoverRelayManager.Instance.StopAsync();

        Assert.Equal(0, FailoverRelayManager.Instance.CurrentListenPort);
    }
}
```

- [ ] **Step 2：运行测试并确认失败**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelayLifecycleTests"
```

Expected: 编译失败，错误包含 `FailoverRelayManager` 未定义。

- [ ] **Step 3：实现 `FailoverRelayManager`**

在 `v2rayN/ServiceLib/Manager/FailoverRelayManager.cs` 写入：

```csharp
using ServiceLib.Models;
using ServiceLib.Services.FailoverRelay;

namespace ServiceLib.Manager;

public sealed class FailoverRelayManager
{
    private static readonly Lazy<FailoverRelayManager> _instance = new(() => new FailoverRelayManager());
    public static FailoverRelayManager Instance => _instance.Value;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private FailoverRelayService? _service;

    public int CurrentListenPort { get; private set; }

    public async Task<RetResult> StartAsync(FailoverRelayRuntime runtime)
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            if (!runtime.Enabled)
            {
                return new RetResult(false, ResUI.FailedGenDefaultConfiguration);
            }

            _service = new FailoverRelayService(new FailoverRelayOptions { ListenPort = runtime.ListenPort }, runtime.Candidates);
            await _service.StartAsync(CancellationToken.None);
            CurrentListenPort = _service.ListenPort;
            return new RetResult(true, string.Empty);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(nameof(FailoverRelayManager), ex);
            await StopCoreAsync();
            return new RetResult(false, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync();
            _service = null;
        }
        CurrentListenPort = 0;
    }
}
```

- [ ] **Step 4：修改 `CoreManager` 启停顺序**

在 `CoreManager.CoreStop()` 开头或停止核心后调用：

```csharp
await FailoverRelayManager.Instance.StopAsync();
```

在 `CoreManager.LoadCore()` 中 `await CoreStart(mainContext);` 后、`await CoreStartPreService(preContext);` 前加入：

```csharp
if (_processService != null && mainContext.FailoverRelayRuntime is { Enabled: true } runtime)
{
    var relayResult = await FailoverRelayManager.Instance.StartAsync(runtime);
    if (relayResult.Success != true)
    {
        await UpdateFunc(true, relayResult.Msg);
        await CoreStop();
        return;
    }
}
```

- [ ] **Step 5：系统代理和 PAC 使用 relay 端口**

在 `SysProxyHandler.UpdateSysProxy()` 中将：

```csharp
var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
```

替换为：

```csharp
var relayPort = FailoverRelayManager.Instance.CurrentListenPort;
var port = relayPort > 0 ? relayPort : AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
```

`PacManager.StartAsync(httpPort, pacPort)` 的 `httpPort` 不变由调用方传入，所以 `SysProxyHandler.SetWindowsProxyPac(port)` 会自动使用 relay 端口生成 PAC。

- [ ] **Step 6：运行生命周期测试**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelayLifecycleTests"
```

Expected: 生命周期测试通过。

---

## Task 8：接入健康检测恢复回切

**Files:**
- Modify: `v2rayN/ServiceLib/Manager/FailoverRelayManager.cs`
- Modify: `v2rayN/ServiceLib/Services/FailoverRelay/FailoverRelayService.cs`
- Modify: `v2rayN/ServiceLib/Services/FailoverHealthService.cs`
- Modify: `v2rayN/ServiceLib.Tests/FailoverRelayStateTests.cs`

- [ ] **Step 1：扩展 relay manager 状态通知测试**

在 `FailoverRelayStateTests` 增加：

```csharp
[Fact]
public void MarkHealthRecovered_FailedCandidateBecomesRecovering()
{
    var p1 = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
    var store = new FailoverCandidateStateStore([p1]);

    store.MarkRequestFailure("p1");
    store.MarkHealthRecovered("p1");

    Assert.Equal(FailoverRelayCandidateState.Recovering, store.GetState("p1"));
}
```

- [ ] **Step 2：给 `FailoverRelayService` 暴露恢复通知**

增加方法：

```csharp
public void MarkHealthRecovered(string profileId)
{
    _stateStore.MarkHealthRecovered(profileId);
}
```

- [ ] **Step 3：给 `FailoverRelayManager` 暴露恢复通知**

增加方法：

```csharp
public void MarkHealthRecovered(string profileId)
{
    _service?.MarkHealthRecovered(profileId);
}
```

- [ ] **Step 4：健康检测成功时通知 relay**

在 `FailoverHealthService` 写入健康检测成功结果的位置，找到设置 `LastStatus = FailoverHealthStatus.Normal` 的分支，加入：

```csharp
FailoverRelayManager.Instance.MarkHealthRecovered(entry.Profile.IndexId);
```

失败分支不需要主动通知 relay；请求级失败由 relay 标记，健康检测失败继续维持不可用状态。

- [ ] **Step 5：运行状态与健康相关测试**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelayStateTests|FullyQualifiedName~FailoverHealth"
```

Expected: relay 状态测试通过，既有健康检测测试不回归。

---

## Task 9：安全失败、日志脱敏和错误响应

**Files:**
- Modify: `v2rayN/ServiceLib/Services/FailoverRelay/FailoverRelayService.cs`
- Modify: `v2rayN/ServiceLib.Tests/FailoverRelaySocks5Tests.cs`
- Modify: `v2rayN/ServiceLib.Tests/FailoverRelayHttpConnectTests.cs`

- [ ] **Step 1：写全失败测试**

SOCKS5：

```csharp
[Fact]
public async Task RelaySocks5_AllCandidatesClosed_ReturnsSocksFailure()
{
    var candidates = new[]
    {
        new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", GetUnusedLoopbackPort(), "P1"),
        new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", GetUnusedLoopbackPort(), "P2"),
    };
    var relay = new FailoverRelayService(new FailoverRelayOptions { ListenPort = GetUnusedLoopbackPort() }, candidates);
    await relay.StartAsync(CancellationToken.None);

    var response = await SocksClientConnectRawAsync(relay.ListenPort, "example.test", 80);

    await relay.StopAsync();
    Assert.Equal(0x01, response.ReplyCode);
}
```

HTTP CONNECT：

```csharp
[Fact]
public async Task RelayHttpConnect_AllCandidatesClosed_Returns502()
{
    var candidates = new[]
    {
        new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", GetUnusedLoopbackPort(), "P1"),
        new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", GetUnusedLoopbackPort(), "P2"),
    };
    var relay = new FailoverRelayService(new FailoverRelayOptions { ListenPort = GetUnusedLoopbackPort() }, candidates);
    await relay.StartAsync(CancellationToken.None);

    var header = await HttpConnectClientRawAsync(relay.ListenPort, "example.test", 80);

    await relay.StopAsync();
    Assert.StartsWith("HTTP/1.1 502", header);
}
```

- [ ] **Step 2：运行测试并确认失败或通过**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~AllCandidatesClosed"
```

Expected: 如果 Task 4 已覆盖全失败则通过；否则失败并继续 Step 3。

- [ ] **Step 3：补齐错误响应和日志**

在 `FailoverRelayService.TryConnectCandidateAsync()` 的失败 catch 中加入脱敏日志：

```csharp
Logging.SaveLog($"failover relay candidate failed: tag={candidate.OutboundTag}, type={ex.GetType().Name}");
```

禁止记录：

```csharp
host
request.RawHeaderText
profile.Password
profile.Sni
profile.PublicKey
profile.ShortId
```

- [ ] **Step 4：运行全失败测试**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~AllCandidatesClosed"
```

Expected: SOCKS5 全失败返回 failure；HTTP CONNECT 全失败返回 `502 Bad Gateway`。

---

## Task 10：Tun 禁用门禁和手工验证清单

**Files:**
- Modify: `v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`
- Modify: `v2rayN/ServiceLib.Tests/FailoverRelayRuntimeGateTests.cs`
- Create: `docs/superpowers/verification/2026-05-20-local-failover-relay-manual-verification.md`

- [ ] **Step 1：确认 Tun 模式不启用 relay**

在 `FailoverRelayRuntimeGateTests` 保留 `Build_XrayFailoverTun_DoesNotCreateRelayRuntime`，并断言：

```csharp
Assert.Null(result.Context.FailoverRelayRuntime);
```

- [ ] **Step 2：为 Tun 写明确验证文档**

新增 `docs/superpowers/verification/2026-05-20-local-failover-relay-manual-verification.md`：

```markdown
# 本地故障转移 relay 手工验证记录

## 自动化前置

- `dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelay"`
- `dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreConfigV2ray"`

## 非 Tun 验证

1. 开启普通系统代理。
2. 激活 Xray-only 普通故障转移队列，候选顺序为 P1、P2。
3. 确认 Xray 配置中存在 `failover-p1-in` 和 `failover-p2-in`。
4. 关闭 P1 服务器。
5. 打开 HTTPS 网站。
6. 预期：当前请求不需要用户刷新即可落到 P2。
7. 预期：P1 被 relay 标记失败后，新请求直接走 P2。
8. 预期：P1 和 P2 都失败时浏览器收到代理错误，不直连。

## Tun 验证

1. 开启 Tun。
2. 激活同一故障转移队列。
3. 预期：第一版不启用 relay，仍走现有运行路径。
4. 检查日志中不应出现 relay 监听端口。
5. 检查 Xray 配置中不应生成 `failover-p*-in` 候选入站。

## 代理安全影响评估

- relay 只监听 `127.0.0.1`。
- 候选 Xray 入站只监听 `127.0.0.1`。
- 所有候选失败时返回代理错误，不直连。
- Tun 第一版不启用 relay，避免 loopback 回环、DNS 泄漏和核心保护链路破坏。
```

- [ ] **Step 3：运行门禁测试**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelayRuntimeGateTests"
```

Expected: Tun 门禁测试通过。

---

## Task 11：端到端验证

**Files:**
- Modify: `_local_changes/NNN-local-failover-relay-implementation/tests.md`

- [ ] **Step 1：运行 relay 单元与集成测试**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelay"
```

Expected: `FailoverRelay*` 测试全部通过。

- [ ] **Step 2：运行 Xray 配置测试**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreConfigV2ray"
```

Expected: Xray 配置测试全部通过。

- [ ] **Step 3：运行故障转移相关测试**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~Failover"
```

Expected: 故障转移相关测试全部通过。

- [ ] **Step 4：运行完整 ServiceLib 测试**

Run:

```powershell
dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj
```

Expected: `ServiceLib.Tests` 全部通过。

- [ ] **Step 5：生成本地改动记录**

创建 `_local_changes/NNN-local-failover-relay-implementation/`，按项目规则写入：

```text
README.md
files.md
reapply.md
tests.md
patch.diff
```

`tests.md` 必须包含：

- 自动化测试命令和结果。
- 手工非 Tun 验证结果。
- Tun 禁用验证结果。
- 代理安全影响评估。

---

## 自检结果

- 设计文档中的 SOCKS5 TCP CONNECT、HTTP CONNECT、候选状态、全失败不直连、loopback 入站、固定路由、入口切换门禁、Tun 禁用门禁均有对应任务。
- 第一版不支持 UDP、QUIC、HTTP/3、普通 HTTP 明文重放、sing-box、混合核心队列和 Tun relay，这些限制已写入范围边界和门禁任务。
- `FailoverRelayCandidate`、`FailoverRelayRuntime`、`FailoverRelayService`、`FailoverRelayManager`、`CoreConfigContext.FailoverRelayRuntime` 的命名在各任务中保持一致。
- 计划没有要求修改 `.env`、密钥、Token、CI/CD、数据库 schema 或 Git 历史。
