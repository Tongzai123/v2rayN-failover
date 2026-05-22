# 故障转移快速降级 relay 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 relay 首包超时从阻塞式健康确认改为快速降级切换，缩短 P1 到 P2 的切换时间，同时保留后台确认和保底连接。

**Architecture:** 首包超时只写 `Degraded` 并启动后台健康确认，不阻塞当前请求；relay 先按 `2000ms` 扫描一轮，再按 `3000ms` 扫描第二轮，仍无首包时选取非 `Failed` 的最高优先级节点稳定保底。UI 增加 `Requesting` 蓝色标签表示当前正在请求节点，`Failed` 仍是唯一进入故障计数和冷却的状态。

**Tech Stack:** .NET、C#、xUnit、Avalonia、ServiceLib failover relay。

---

### Task 1: 首包超时策略测试

**Files:**
- Modify: `v2rayN/ServiceLib.Tests/FailoverRelaySocks5Tests.cs`
- Modify: `v2rayN/ServiceLib.Tests/FailoverRelayStateTests.cs`

- [ ] **Step 1: 写失败测试**

新增或调整测试，覆盖：

```csharp
[Fact]
public async Task RelaySocks5_FirstByteTimeoutMarksDegradedAndContinuesToSecondWithoutWaitingForProbe()
{
    // P1 首包卡住，P2 返回 payload。
    // HealthProbeAsync 被阻塞，证明 relay 不等待健康确认。
    // 预期：收到 p2-ok；P1 写 Degraded；P2 写 Requesting/Normal；failure reporter 未被调用。
}
```

新增健康确认测试：

```csharp
[Fact]
public async Task HealthConfirmation_BackgroundConfirmationsAreIsolatedPerCandidate()
{
    // 同时启动 P1/P2 后台确认。
    // 每个 profileId 独立计数，各自达到阈值后返回 Failed。
}
```

- [ ] **Step 2: 运行失败测试**

Run:

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~RelaySocks5_FirstByteTimeoutMarksDegradedAndContinuesToSecondWithoutWaitingForProbe|FullyQualifiedName~HealthConfirmation_BackgroundConfirmationsAreIsolatedPerCandidate"
```

Expected: 至少一个测试失败，原因是缺少 `Requesting` 状态、后台确认入口或 relay 仍等待/保留首个候选。

### Task 2: relay 选路实现

**Files:**
- Modify: `v2rayN/ServiceLib/Models/FailoverRelayOptions.cs`
- Modify: `v2rayN/ServiceLib/Services/FailoverRelay/FailoverRelayHealthConfirmationService.cs`
- Modify: `v2rayN/ServiceLib/Services/FailoverRelay/FailoverRelayService.cs`

- [ ] **Step 1: 调整默认参数**

将 `CandidateFirstByteTimeout` 改为 `2000ms`，将 `HealthConfirmationFailureThreshold` 改为 `2`，保留 `HealthConfirmationTimeout = 5000ms`，新增第二轮首包超时配置：

```csharp
public TimeSpan CandidateFirstByteTimeout { get; init; } = TimeSpan.FromMilliseconds(2000);
public TimeSpan CandidateSecondRoundFirstByteTimeout { get; init; } = TimeSpan.FromMilliseconds(3000);
public TimeSpan HealthConfirmationTimeout { get; init; } = TimeSpan.FromMilliseconds(5000);
public int HealthConfirmationFailureThreshold { get; init; } = 2;
```

- [ ] **Step 2: 新增后台确认入口**

在 `FailoverRelayHealthConfirmationService` 增加 `ConfirmUntilThresholdAsync`，循环执行最多 `HealthConfirmationFailureThreshold` 次确认，同一 profileId 复用已有 `InFlight`，不同 profileId 可并行。

- [ ] **Step 3: 首包超时改为降级并继续**

在 `FailoverRelayService` 中将 `first-byte-timeout` 从 `ReportCandidateFailureAsync` 的阻塞路径拆出：

```csharp
await ReportCandidateStatusAsync(candidate, protocol, target, FailoverHealthStatus.Degraded, "first-byte-timeout");
StartBackgroundHealthConfirmation(candidate, protocol, target, "first-byte-timeout");
```

然后继续尝试下一个候选。

- [ ] **Step 4: 实现两轮扫描与保底选择**

第一轮使用 `CandidateFirstByteTimeout`；第二轮使用 `CandidateSecondRoundFirstByteTimeout`，跳过已确认 `Failed` 的节点。第二轮仍全超时时，选择非 `Failed` 的最高优先级节点；如果全部 `Failed`，选择第一个节点，写 `Fallback` 并稳定 pump。

### Task 3: UI 状态与资源

**Files:**
- Modify: `v2rayN/ServiceLib/Models/FailoverHealthStatus.cs`
- Modify: `v2rayN/ServiceLib/Manager/FailoverRelayManager.cs`
- Modify: `v2rayN/ServiceLib/Models/ProfileItemModel.cs`
- Modify: `v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
- Modify: `v2rayN/ServiceLib/Resx/ResUI.resx`
- Modify: `v2rayN/ServiceLib/Resx/ResUI.zh-Hans.resx`
- Modify: `v2rayN/ServiceLib/Resx/ResUI.Designer.cs`
- Modify: `v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`
- Modify: `v2rayN/ServiceLib.Tests/FailoverHealthDisplayTests.cs`
- Modify: `v2rayN/ServiceLib.Tests/DesktopFailoverModeStyleTests.cs`

- [ ] **Step 1: 增加 `Requesting` 状态**

新增 `FailoverHealthStatus.Requesting = "Requesting"`，资源文本为英文 `Requesting`、中文 `请求中`。

- [ ] **Step 2: manager 写入规则**

`Requesting`、`Degraded`、`Fallback` 都不增加故障计数、不设置冷却时间；`Failed` 仍走原故障路径；`Normal` 清空故障信息。

- [ ] **Step 3: UI 显示**

`Requesting` 使用蓝色标签，`Degraded` 使用橙色标签，`Failed` 使用红色标签。更新显示测试和 Avalonia 标签测试。

### Task 4: 验证与本地改动记录

**Files:**
- Create: `_local_changes/056-failover-fast-degraded-relay/README.md`
- Create: `_local_changes/056-failover-fast-degraded-relay/files.md`
- Create: `_local_changes/056-failover-fast-degraded-relay/reapply.md`
- Create: `_local_changes/056-failover-fast-degraded-relay/tests.md`
- Create: `_local_changes/056-failover-fast-degraded-relay/patch.diff`

- [ ] **Step 1: 运行回归测试**

Run:

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelay|FullyQualifiedName~FailoverHealth|FullyQualifiedName~ProfilesViewModel|FullyQualifiedName~DesktopFailoverModeStyle"
```

- [ ] **Step 2: 运行完整测试与构建**

Run:

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj
dotnet build v2rayN\v2rayN.sln
git diff --check -- . ":(exclude)_local_changes"
```

- [ ] **Step 3: 写入 `_local_changes`**

记录目的、影响范围、代理安全影响评估、测试结果和重新应用步骤，并生成：

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes/056-failover-fast-degraded-relay/patch.diff
```

### 自检

- 需求覆盖：P1 快速切 P2、降级标签、请求中标签、后台并行健康确认、两轮首包超时、稳定保底、失败跳过均有任务覆盖。
- 占位符扫描：无 `TBD` / `TODO`。
- 类型一致性：新增状态名统一为 `Requesting`，新增第二轮配置名统一为 `CandidateSecondRoundFirstByteTimeout`。
