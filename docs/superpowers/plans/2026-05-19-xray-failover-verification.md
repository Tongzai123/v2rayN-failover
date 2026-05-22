# Xray 故障转移配置与请求级行为验证实施计划

> **给代理执行者：** 必须使用 `superpowers-subagent-driven-development`（推荐）或 `superpowers-executing-plans` 按任务逐项执行。步骤使用复选框语法跟踪进度。

**目标：** 验证用户测试时实际运行的 Xray 配置是否进入故障队列 `balancer`，并验证当前 `leastLoad + fallbackTag` 是否具备 P1 连接失败后请求级重试 P2 的行为。

**架构：** 先做不含密钥的实际配置结构检查，证明请求是否经过 Xray `balancer`。如果配置确实进入 `balancer`，再用本地最小 Xray 场景隔离验证 `leastLoad + fallbackTag` 的真实行为；验证失败时停止，不展开备选实现方案。

**技术栈：** PowerShell、Xray、`curl.exe`、`.NET`、xUnit、`ServiceLib.Tests`。

---

## 文件结构

- 创建：`docs/superpowers/verification/2026-05-19-xray-failover-verification.md`
  - 记录实际运行配置路径、脱敏配置结构、最小 Xray 行为验证结果和结论。
- 修改：`v2rayN/ServiceLib.Tests/CoreConfigV2rayServiceTests.cs`
  - 增加自动化断言，确保 fallback policy group 生成的路由规则、`balancer`、P1/P2 outbounds 和 `burstObservatory` 结构完整。
- 修改：`docs/superpowers/specs/2026-05-19-core-internal-failover-plan.md`
  - 根据验证结果更新计划结论；若验证失败，只记录证据，不加入备选方案。
- 创建：`_local_changes/048-xray-failover-verification-plan/`
  - 记录实施计划文档、验证记录、自动化测试和后续结论改动。

## 执行约束

- 不读取、复制或写入节点密钥、订阅链接、Token、密码。
- 配置检查只输出 tag、`balancerTag`、`outboundTag`、`selector`、`fallbackTag`、`strategy`、`costs.match`、`burstObservatory.subjectSelector` 等非敏感字段。
- 不修改 UI，不引入 sing-box，不处理混合核心，不写 v2rayN 自建本地代理链。
- 不用健康检测 reload 替代请求级切换验证。
- 如果实际配置未进入 `balancer`，先停止 Xray 行为判断，转为定位配置生成或运行配置指向问题。
- 如果实际配置进入 `balancer` 但最小 Xray 场景不重试 P2，记录证据并停止，不展开备选方案。

---

### 任务 1：建立验证记录并定位实际运行配置

**文件：**
- 创建：`docs/superpowers/verification/2026-05-19-xray-failover-verification.md`

- [ ] **步骤 1：创建验证记录文件**

```markdown
# Xray 故障转移验证记录

## 验证目标

- 确认用户测试时实际运行的 `config.json` 是否进入 Xray `balancer`。
- 确认 `leastLoad + fallbackTag` 是否具备 P1 连接失败后请求级重试 P2 的行为。

## 实际运行配置定位

| 项目 | 结果 |
| --- | --- |
| Xray 进程是否运行 | 计划创建时尚未采集 |
| Xray 命令行配置路径 | 计划创建时尚未采集 |
| 发布目录配置路径 | `publish/v2rayN-windows-64-desktop/binConfigs/config.json` |
| 实际检查的配置路径 | 计划创建时尚未采集 |

## 脱敏配置结构检查

| 检查项 | 结果 | 证据 |
| --- | --- | --- |
| 存在 `routing.balancers` | 计划创建时尚未采集 | 计划创建时尚未采集 |
| Google 规则使用 `balancerTag` | 计划创建时尚未采集 | 计划创建时尚未采集 |
| P1/P2 outbounds 存在 | 计划创建时尚未采集 | 计划创建时尚未采集 |
| `selector` 可选中 P1/P2 | 计划创建时尚未采集 | 计划创建时尚未采集 |
| `burstObservatory.subjectSelector` 覆盖候选 tag | 计划创建时尚未采集 | 计划创建时尚未采集 |

## 最小 Xray 行为验证

| 场景 | 结果 | 证据 |
| --- | --- | --- |
| 冷启动后立即请求 | 计划创建时尚未采集 | 计划创建时尚未采集 |
| 等待 observatory 后请求 | 计划创建时尚未采集 | 计划创建时尚未采集 |
| P2 是否收到请求 | 计划创建时尚未采集 | 计划创建时尚未采集 |

## 结论

计划创建时尚未采集运行证据。

## 代理安全影响评估

本验证只检查配置结构和本地最小 Xray 行为，不扩大代理候选范围，不写入密钥，不修改系统代理、Tun、DNS、订阅、证书或核心更新逻辑。
```

- [ ] **步骤 2：查找当前 Xray 进程命令行**

运行：

```powershell
Get-CimInstance Win32_Process |
  Where-Object { $_.Name -match '^(xray|wv2ray|v2ray)\\.exe$' -or $_.CommandLine -match 'xray' } |
  Select-Object ProcessId, Name, CommandLine |
  Format-List
```

期望：

- 如果 Xray 正在运行，输出中应能看到 `-config` 或包含 `config.json` 的命令行。
- 如果没有 Xray 进程，记录“Xray 进程未运行”，然后只检查发布目录配置，不做实际运行结论。

- [ ] **步骤 3：从进程命令行提取配置路径**

运行：

```powershell
$proc = Get-CimInstance Win32_Process |
  Where-Object { $_.Name -match '^(xray|wv2ray|v2ray)\\.exe$' -or $_.CommandLine -match 'xray' } |
  Select-Object -First 1

if ($null -eq $proc) {
  "NO_XRAY_PROCESS"
} else {
  $cmd = $proc.CommandLine
  if ($cmd -match '-config\\s+\"([^\"]+)\"') {
    $Matches[1]
  } elseif ($cmd -match '-config\\s+([^\\s]+)') {
    $Matches[1]
  } elseif ($cmd -match '([^\\s\"]*config\\.json)') {
    $Matches[1]
  } else {
    "CONFIG_PATH_NOT_FOUND_IN_COMMAND_LINE"
  }
}
```

期望：

- 成功时输出一个 `config.json` 的绝对路径。
- 输出 `NO_XRAY_PROCESS` 时，验证记录只能写“未捕获到运行配置路径”。
- 输出 `CONFIG_PATH_NOT_FOUND_IN_COMMAND_LINE` 时，记录完整进程名和 PID，但不要记录节点地址、密钥或订阅内容。

- [ ] **步骤 4：更新验证记录**

把步骤 2 和步骤 3 的结果写入 `docs/superpowers/verification/2026-05-19-xray-failover-verification.md` 的“实际运行配置定位”表。

- [ ] **步骤 5：提交检查点**

```powershell
git add docs/superpowers/verification/2026-05-19-xray-failover-verification.md
git commit -m "docs: add xray failover verification record"
```

如果当前会话不允许提交，记录为“未提交，等待用户确认”。

---

### 任务 2：脱敏检查实际配置是否进入 balancer

**文件：**
- 修改：`docs/superpowers/verification/2026-05-19-xray-failover-verification.md`

- [ ] **步骤 1：设置待检查配置路径**

运行以下命令。提示输入任务 1 提取到的实际运行 `config.json` 路径；如果没有运行路径，直接回车，会自动使用发布目录配置：

```powershell
$configPath = Read-Host "输入实际运行 config.json 绝对路径；没有则直接回车"
if ([string]::IsNullOrWhiteSpace($configPath)) {
  $configPath = "publish\v2rayN-windows-64-desktop\binConfigs\config.json"
}
$configPath
```

- [ ] **步骤 2：输出脱敏配置结构**

运行：

```powershell
$json = Get-Content -Raw $configPath | ConvertFrom-Json

"CONFIG_PATH=$configPath"
"BALANCERS"
$json.routing.balancers | ConvertTo-Json -Depth 12

"RULES_PROXY_OR_BALANCER"
$json.routing.rules |
  Where-Object {
    $_.balancerTag -or
    $_.outboundTag -eq "proxy" -or
    ($_.domain -and ($_.domain -contains "geosite:google"))
  } |
  Select-Object type, domain, ip, network, outboundTag, balancerTag |
  ConvertTo-Json -Depth 8

"OUTBOUND_TAGS"
$json.outbounds | Select-Object tag, protocol | ConvertTo-Json -Depth 4

"BURST_OBSERVATORY"
$json.burstObservatory | ConvertTo-Json -Depth 12
```

期望：

- 不输出 `settings.vnext`、`settings.servers.password`、`users.id`、`realitySettings`、`tlsSettings` 等敏感字段。
- 若存在故障队列，`BALANCERS` 应包含 `proxy-balancer` 或当前 proxy tag 对应的 balancer。
- Google 规则应显示 `balancerTag`，而不是固定 `outboundTag = "proxy"`。
- `OUTBOUND_TAGS` 应能看到 P1、P2 对应 tag。
- `BURST_OBSERVATORY` 应包含覆盖候选 tag 的 `subjectSelector`。

- [ ] **步骤 3：判定配置命中结果**

按以下规则更新验证记录：

```text
结果 A：没有 routing.balancers，或 Google 规则仍是 outboundTag = "proxy"，或 outbounds 只有 proxy/direct/block。
结论：实际运行配置没有进入故障队列 balancer，停止 Xray 请求级行为判断。

结果 B：存在 balancer，Google 规则使用 balancerTag，P1/P2 outbounds 存在，subjectSelector 覆盖候选 tag。
结论：实际运行配置已进入 balancer，可以继续任务 4 的最小 Xray 行为验证。
```

- [ ] **步骤 4：如果配置未进入 balancer，记录定位方向**

在验证记录中写入：

```markdown
配置没有进入 balancer 时，下一步只定位以下路径，不判断 Xray 请求级 fallback 能力：

- `v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`
- `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- `v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayOutboundService.cs`
- `v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayRoutingService.cs`
- `v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayBalancerService.cs`
- 实际运行进程加载的 `config.json` 是否和发布目录 `config.json` 一致。
```

- [ ] **步骤 5：提交检查点**

```powershell
git add docs/superpowers/verification/2026-05-19-xray-failover-verification.md
git commit -m "docs: record actual xray failover config shape"
```

如果当前会话不允许提交，记录为“未提交，等待用户确认”。

---

### 任务 3：补充自动化配置生成测试

**文件：**
- 修改：`v2rayN/ServiceLib.Tests/CoreConfigV2rayServiceTests.cs`

- [ ] **步骤 1：新增失败测试**

在 `GenerateClientConfigContent_FallbackPolicyGroupUsesPriorityAwareBalancer` 后面新增测试：

```csharp
[Fact]
public void GenerateClientConfigContent_FallbackPolicyGroupRoutesProxyRulesThroughBalancer()
{
    var p1 = CreateProxyNode("p1", "198.51.100.21", 443);
    var p2 = CreateProxyNode("p2", "198.51.100.22", 443);
    var group = CreatePolicyGroupNode("fallback-group", EMultipleLoad.Fallback, p1, p2);

    var service = new CoreConfigV2rayService(CreateContext(
        group,
        allProxiesMap: new Dictionary<string, ProfileItem>
        {
            [p1.IndexId] = p1,
            [p2.IndexId] = p2,
        }));

    var result = service.GenerateClientConfigContent();

    Assert.True(result.Success, result.Msg);

    var root = GetRoot(result.Data?.ToString());
    var balancerTag = Global.ProxyTag + Global.BalancerTagSuffix;
    var rules = root["routing"]?["rules"]?.AsArray()
        .Select(node => node!.AsObject())
        .ToList() ?? [];

    Assert.Contains(rules, rule =>
        rule["domain"]?.AsArray().Any(domain => domain?.GetValue<string>() == "geosite:google") == true
        && rule["balancerTag"]?.GetValue<string>() == balancerTag
        && rule["outboundTag"] is null);

    Assert.Contains(rules, rule =>
        rule["network"]?.GetValue<string>() == "tcp,udp"
        && rule["balancerTag"]?.GetValue<string>() == balancerTag
        && rule["outboundTag"] is null);
}

[Fact]
public void GenerateClientConfigContent_FallbackPolicyGroupObservesAllFallbackCandidates()
{
    var p1 = CreateProxyNode("p1", "198.51.100.31", 443);
    var p2 = CreateProxyNode("p2", "198.51.100.32", 443);
    var group = CreatePolicyGroupNode("fallback-group", EMultipleLoad.Fallback, p1, p2);

    var service = new CoreConfigV2rayService(CreateContext(
        group,
        allProxiesMap: new Dictionary<string, ProfileItem>
        {
            [p1.IndexId] = p1,
            [p2.IndexId] = p2,
        }));

    var result = service.GenerateClientConfigContent();

    Assert.True(result.Success, result.Msg);

    var root = GetRoot(result.Data?.ToString());
    var outbounds = root["outbounds"]?.AsArray()
        .Select(node => node!.AsObject())
        .ToList() ?? [];
    var outboundTags = outbounds
        .Select(outbound => outbound["tag"]?.GetValue<string>())
        .Where(tag => tag is not null)
        .ToList();
    var subjectSelectors = root["burstObservatory"]?["subjectSelector"]?.AsArray()
        .Select(node => node?.GetValue<string>())
        .Where(selector => selector is not null)
        .ToList() ?? [];

    Assert.Contains("proxy-1-p1", outboundTags);
    Assert.Contains("proxy-2-p2", outboundTags);
    Assert.Contains(Global.ProxyTag, subjectSelectors);
}
```

- [ ] **步骤 2：运行目标测试**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreConfigV2rayServiceTests" --no-restore
```

期望：

- 如果测试失败，失败应指向当前生成配置没有把 Google/final proxy 规则改成 `balancerTag`，或 `burstObservatory.subjectSelector` 没覆盖候选 tag。
- 如果测试通过，说明自动化层面生成结构完整；仍不能证明 Xray 请求级重试行为。

- [ ] **步骤 3：按失败结果做最小修正**

如果 `GenerateClientConfigContent_FallbackPolicyGroupRoutesProxyRulesThroughBalancer` 失败，优先检查：

```text
v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayRoutingService.cs
- GenRouting() 是否在生成 final rule 前把用户规则中的 proxy outboundTag 转成 balancerTag。
- BuildFinalRule() 是否在存在 proxy balancer 时设置 balancerTag 并清空 outboundTag。
```

如果 `GenerateClientConfigContent_FallbackPolicyGroupObservesAllFallbackCandidates` 失败，优先检查：

```text
v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayOutboundService.cs
- BuildOutboundsList() 是否生成 P1/P2 outbounds。

v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayBalancerService.cs
- GenObservatory() 是否为 Fallback 创建 burstObservatory。
- GenBalancer() 的 selector 是否能覆盖 P1/P2 tag。
```

本任务只允许修正配置生成错误，不允许改 UI，不允许加入备选故障转移方案。

- [ ] **步骤 4：再次运行目标测试**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreConfigV2rayServiceTests" --no-restore
```

期望：全部通过。

- [ ] **步骤 5：提交检查点**

```powershell
git add v2rayN\ServiceLib.Tests\CoreConfigV2rayServiceTests.cs v2rayN\ServiceLib\Services\CoreConfig\V2ray
git commit -m "test: cover xray failover balancer config shape"
```

如果当前会话不允许提交，记录为“未提交，等待用户确认”。

---

### 任务 4：构造 Xray-only 最小行为验证

**文件：**
- 修改：`docs/superpowers/verification/2026-05-19-xray-failover-verification.md`

- [ ] **步骤 1：确认本地 Xray 可执行文件**

运行：

```powershell
$xray = "publish\v2rayN-windows-64-desktop\bin\xray\xray.exe"
if (Test-Path $xray) {
  & $xray version
} else {
  "XRAY_NOT_FOUND"
}
```

期望：

- 输出 Xray 版本信息。
- 如果输出 `XRAY_NOT_FOUND`，停止本任务，并在验证记录中写明无法执行最小 Xray 行为验证。

- [ ] **步骤 2：启动本地 HTTP 观测服务**

运行：

```powershell
$httpPort = 18080
$httpJob = Start-Job -ScriptBlock {
  param($port)
  $listener = [System.Net.HttpListener]::new()
  $listener.Prefixes.Add("http://127.0.0.1:$port/")
  $listener.Start()
  while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $body = [System.Text.Encoding]::UTF8.GetBytes("p2-ok")
    $ctx.Response.StatusCode = 200
    $ctx.Response.OutputStream.Write($body, 0, $body.Length)
    $ctx.Response.Close()
  }
} -ArgumentList $httpPort

Start-Sleep -Milliseconds 500
curl.exe --max-time 3 "http://127.0.0.1:$httpPort/"
```

期望：输出 `p2-ok`。

- [ ] **步骤 3：生成最小 Xray 配置**

运行：

```powershell
$work = Join-Path $env:TEMP "xray-failover-verification"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$config = Join-Path $work "xray-failover-minimal.json"
$socksPort = 18081
$deadSocksPort = 65530
$httpPort = 18080

@"
{
  "log": {
    "loglevel": "debug"
  },
  "inbounds": [
    {
      "listen": "127.0.0.1",
      "port": $socksPort,
      "protocol": "socks",
      "settings": {
        "auth": "noauth",
        "udp": true
      },
      "tag": "socks-in"
    }
  ],
  "outbounds": [
    {
      "tag": "proxy-1-p1",
      "protocol": "socks",
      "settings": {
        "servers": [
          {
            "address": "127.0.0.1",
            "port": $deadSocksPort
          }
        ]
      }
    },
    {
      "tag": "proxy-2-p2",
      "protocol": "freedom"
    },
    {
      "tag": "direct",
      "protocol": "freedom"
    },
    {
      "tag": "block",
      "protocol": "blackhole"
    }
  ],
  "routing": {
    "domainStrategy": "AsIs",
    "rules": [
      {
        "type": "field",
        "network": "tcp,udp",
        "balancerTag": "proxy-balancer"
      }
    ],
    "balancers": [
      {
        "tag": "proxy-balancer",
        "selector": [
          "proxy"
        ],
        "fallbackTag": "proxy-1-p1",
        "strategy": {
          "type": "leastLoad",
          "settings": {
            "expected": 1,
            "costs": [
              {
                "regexp": false,
                "match": "proxy-1-p1",
                "value": 1
              },
              {
                "regexp": false,
                "match": "proxy-2-p2",
                "value": 1000001
              }
            ]
          }
        }
      }
    ]
  },
  "burstObservatory": {
    "subjectSelector": [
      "proxy"
    ],
    "pingConfig": {
      "destination": "http://127.0.0.1:$httpPort/",
      "interval": "2s",
      "timeout": "1s",
      "sampling": 1
    }
  }
}
"@ | Set-Content -Encoding UTF8 $config

$config
```

期望：输出最小配置路径。配置不含任何真实节点、密钥或订阅。

- [ ] **步骤 4：启动 Xray**

运行：

```powershell
$xray = "publish\v2rayN-windows-64-desktop\bin\xray\xray.exe"
$work = Join-Path $env:TEMP "xray-failover-verification"
$config = Join-Path $work "xray-failover-minimal.json"
$stdout = Join-Path $work "xray.stdout.log"
$stderr = Join-Path $work "xray.stderr.log"

$xrayProcess = Start-Process -FilePath $xray `
  -ArgumentList "run -config `"$config`"" `
  -NoNewWindow `
  -PassThru `
  -RedirectStandardOutput $stdout `
  -RedirectStandardError $stderr

Start-Sleep -Seconds 1
$xrayProcess.Id
```

期望：输出 Xray 进程 ID。

- [ ] **步骤 5：冷启动后立即请求**

运行：

```powershell
$socksPort = 18081
$httpPort = 18080
curl.exe --socks5-hostname "127.0.0.1:$socksPort" --max-time 5 "http://127.0.0.1:$httpPort/google"
```

记录：

- 如果输出 `p2-ok`，说明冷启动下请求成功落到 P2，需要继续查日志确认是否先选 P1 后重试 P2。
- 如果超时或连接失败，说明冷启动下没有请求级自动重试到 P2。

- [ ] **步骤 6：等待 observatory 后再次请求**

运行：

```powershell
Start-Sleep -Seconds 5
curl.exe --socks5-hostname "127.0.0.1:18081" --max-time 5 "http://127.0.0.1:18080/google"
```

记录：

- 如果等待后成功，倾向说明 `leastLoad` 根据 observatory 状态选择了 P2。
- 如果等待后仍失败，说明当前最小结构既没有请求级重试，也没有通过 observatory 选出 P2。

- [ ] **步骤 7：读取 Xray 日志中的路由证据**

运行：

```powershell
$work = Join-Path $env:TEMP "xray-failover-verification"
Get-Content (Join-Path $work "xray.stderr.log") -Tail 200
Get-Content (Join-Path $work "xray.stdout.log") -Tail 200
```

期望：

- 记录是否出现 `proxy-1-p1`、`proxy-2-p2`、`fallback to`、`least load`、`balancing` 等关键字。
- 不把日志中的真实节点信息写入验证记录；本最小配置不应包含真实节点信息。

- [ ] **步骤 8：停止本地验证进程**

运行：

```powershell
if ($xrayProcess -and !$xrayProcess.HasExited) {
  Stop-Process -Id $xrayProcess.Id
}
if ($httpJob) {
  Stop-Job $httpJob
}
```

期望：本地 Xray 和 HTTP job 停止。不要删除临时目录，便于复核日志。

- [ ] **步骤 9：更新验证记录**

按以下格式写入 `docs/superpowers/verification/2026-05-19-xray-failover-verification.md`：

```markdown
## 最小 Xray 行为验证结论

- 冷启动后立即请求：成功 / 失败。
- 等待 observatory 后请求：成功 / 失败。
- P2 是否收到请求：是 / 否。
- 关键日志证据：记录脱敏日志摘要。

结论：

- 如果冷启动失败、等待后成功：当前结构更像 observatory 驱动的路由选择，不是 P1 连接失败后的请求级重试。
- 如果冷启动成功且日志证明先尝试 P1 再尝试 P2：当前结构具备请求级重试证据。
- 如果两次都失败：当前结构不能满足目标。
```

- [ ] **步骤 10：提交检查点**

```powershell
git add docs/superpowers/verification/2026-05-19-xray-failover-verification.md
git commit -m "docs: record xray failover runtime behavior"
```

如果当前会话不允许提交，记录为“未提交，等待用户确认”。

---

### 任务 5：根据验证结果更新计划结论

**文件：**
- 修改：`docs/superpowers/specs/2026-05-19-core-internal-failover-plan.md`
- 修改：`docs/superpowers/verification/2026-05-19-xray-failover-verification.md`

- [ ] **步骤 1：选择结论分支**

根据任务 2 和任务 4 的结果，只选择以下一个结论：

```text
结论 A：实际运行配置没有进入 balancer。
处理：更新计划为“先修正配置生成或实际配置加载问题”，不判断 Xray 请求级行为。

结论 B：实际运行配置进入 balancer，最小 Xray 场景证明请求级重试 P2。
处理：更新计划为“继续定位 v2rayN 生成配置与实际运行配置差异”，后续可写修复计划。

结论 C：实际运行配置进入 balancer，最小 Xray 场景不能证明请求级重试 P2。
处理：更新计划为“当前 leastLoad + fallbackTag 结构不满足目标或证据不足”，本计划停止，不展开备选实现方案。
```

- [ ] **步骤 2：更新计划文档的“已确认评审结论”**

追加一条对应结论：

```markdown
- 验证结论：实际运行配置未进入 balancer，需先定位配置生成或加载路径。
```

或：

```markdown
- 验证结论：实际运行配置进入 balancer，且最小 Xray 场景证明当前结构具备请求级重试证据。
```

或：

```markdown
- 验证结论：实际运行配置进入 balancer，但最小 Xray 场景未证明当前结构具备请求级重试；本计划停止，不展开备选方案。
```

- [ ] **步骤 3：运行文档关键词检查**

运行：

```powershell
rg -n "sing-box|混合核心|自建本地代理链|备选方案|健康检测 reload 替代|直接改 UI" docs\superpowers\specs\2026-05-19-core-internal-failover-plan.md docs\superpowers\verification\2026-05-19-xray-failover-verification.md
```

期望：

- 只允许出现“本计划不覆盖”或“禁止”语境。
- 不允许出现把这些内容作为实施路径的语句。

- [ ] **步骤 4：提交检查点**

```powershell
git add docs/superpowers/specs/2026-05-19-core-internal-failover-plan.md docs/superpowers/verification/2026-05-19-xray-failover-verification.md
git commit -m "docs: update xray failover verification conclusion"
```

如果当前会话不允许提交，记录为“未提交，等待用户确认”。

---

### 任务 6：补充本地改动记录并做最终验证

**文件：**
- 创建或修改：`_local_changes/048-xray-failover-verification-plan/README.md`
- 创建或修改：`_local_changes/048-xray-failover-verification-plan/files.md`
- 创建或修改：`_local_changes/048-xray-failover-verification-plan/reapply.md`
- 创建或修改：`_local_changes/048-xray-failover-verification-plan/tests.md`
- 创建或修改：`_local_changes/048-xray-failover-verification-plan/patch.diff`

- [ ] **步骤 1：创建本地改动记录**

```powershell
New-Item -ItemType Directory -Force _local_changes\048-xray-failover-verification-plan | Out-Null
```

写入以下文件：

`README.md`：

```markdown
# Xray 故障转移验证实施计划

## 改动目的

记录 Xray-only 普通“故障转移”的验证实施计划和验证证据，先确认实际运行配置是否进入 `balancer`，再确认 `leastLoad + fallbackTag` 是否具备请求级失败重试语义。

## 用户可见行为

本改动自身不改变用户可见行为。后续实现前先形成验证证据。

## 设计取舍

- 不选择备选实现方案。
- 不改 UI。
- 不引入 sing-box、混合核心或 v2rayN 自建本地代理链。
- 验证失败时停止并记录证据。

## 代理安全影响评估

本改动只新增文档、测试和验证记录，不改变代理链路、DNS、Tun、系统代理、订阅、证书或核心更新逻辑。验证命令必须使用脱敏输出，不写入真实节点密钥或订阅内容。
```

`files.md`：

```markdown
# 文件变更

| 文件 | 变更原因 |
| --- | --- |
| `docs/superpowers/plans/2026-05-19-xray-failover-verification.md` | 新增 Xray 故障转移配置与请求级行为验证实施计划。 |
| `docs/superpowers/verification/2026-05-19-xray-failover-verification.md` | 执行计划时记录实际配置和最小 Xray 行为验证证据。 |
| `v2rayN/ServiceLib.Tests/CoreConfigV2rayServiceTests.cs` | 执行计划时补充 fallback policy group 配置生成断言。 |
| `docs/superpowers/specs/2026-05-19-core-internal-failover-plan.md` | 执行计划后记录验证结论。 |
| `_local_changes/048-xray-failover-verification-plan/*` | 记录本地改动说明、迁移方式、验证结果和补丁。 |
```

`reapply.md`：

```markdown
# 重新应用说明

未来升级源码后，如果仍需验证 Xray-only 普通“故障转移”的请求级行为：

1. 先应用 `patch.diff`。
2. 运行 `dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreConfigV2rayServiceTests" --no-restore`。
3. 按 `docs/superpowers/plans/2026-05-19-xray-failover-verification.md` 定位实际运行配置并执行最小 Xray 行为验证。
4. 更新 `docs/superpowers/verification/2026-05-19-xray-failover-verification.md` 中的证据和结论。
```

`tests.md`：

```markdown
# 验证记录

## 已执行验证

- `dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreConfigV2rayServiceTests" --no-restore`
  - 结果：执行本计划时写入实际命令输出。
- 最小 Xray 行为验证
  - 结果：执行本计划时写入冷启动请求、等待 observatory 后请求和 P2 收到请求的实际结果。
- `git diff --check -- . ":(exclude)_local_changes"`
  - 结果：执行本计划时写入实际命令输出。

## 代理安全影响评估

验证过程只允许写入脱敏配置结构和本地最小 Xray 日志，不得写入真实节点密钥、订阅链接、Token 或密码。
```

- [ ] **步骤 2：生成补丁**

运行：

```powershell
git diff -- . ":(exclude)_local_changes" | Out-File -FilePath _local_changes\048-xray-failover-verification-plan\patch.diff -Encoding utf8
```

- [ ] **步骤 3：最终验证**

运行：

```powershell
dotnet test v2rayN\ServiceLib.Tests\ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreConfigV2rayServiceTests" --no-restore
git diff --check -- . ":(exclude)_local_changes"
```

期望：

- `dotnet test` 通过。
- `git diff --check` 无空白错误。

- [ ] **步骤 4：提交检查点**

```powershell
git add docs/superpowers/plans/2026-05-19-xray-failover-verification.md docs/superpowers/verification/2026-05-19-xray-failover-verification.md v2rayN\ServiceLib.Tests\CoreConfigV2rayServiceTests.cs docs/superpowers/specs/2026-05-19-core-internal-failover-plan.md _local_changes\048-xray-failover-verification-plan
git commit -m "docs: plan xray failover verification"
```

如果当前会话不允许提交，记录为“未提交，等待用户确认”。

---

## 自检清单

- 本计划只覆盖 Xray-only。
- 本计划先确认实际运行配置是否命中 `balancer`，再做 Xray 行为验证。
- 本计划没有选择备选方案。
- 本计划没有引入 v2rayN 自建本地代理链。
- 本计划没有把健康检测 reload 作为故障瞬间切换方案。
- 本计划要求脱敏输出，不记录节点密钥、订阅链接、Token 或密码。
