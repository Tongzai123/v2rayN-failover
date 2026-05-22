# v2rayN 本地故障转移中继层设计

## 背景

当前 Xray-only 普通“故障转移”验证已经确认：

- 实际运行的 `config.json` 已进入 Xray `balancer`。
- Google 规则和 final proxy 规则都使用 `balancerTag`。
- P1/P2 均进入 outbounds、selector 和 costs。
- 当 `burstObservatory` 已经发现 P1 不可用时，Xray 可以在路由阶段选择 P2。
- 当 P1 起初健康、随后在下一轮 observatory 前失效时，请求仍会选中 P1 并失败；未观察到同一请求自动重试 P2。

因此，继续调整 `leastLoad + fallbackTag + burstObservatory` 不能作为满足“P1 当前请求失败后切 P2”的可靠设计。需要一个掌握当前请求生命周期的本地中继层，在 P1 尝试失败后，能在尚未向客户端报告失败前继续尝试 P2。

## 目标

- P1 正常时，新请求优先使用 P1。
- P1 对当前请求连接失败时，同一个请求继续尝试 P2。
- P2 成功后，当前请求继续走 P2，不需要用户刷新网页。
- P1 被判定失败后，在健康检测确认恢复前，新请求跳过 P1，避免每个请求都先撞故障节点。
- P1 只能经健康检测确认恢复后重新进入候选，新请求再回到 P1。
- 所有候选都失败时，请求失败，不允许静默直连。
- relay 启动、候选 Xray 入站生成和系统代理入口切换必须作为同一个门禁处理；任一环节失败时不切到 relay 端口。
- 不记录节点密钥、订阅链接、Token、密码或真实代理鉴权字段。

## 非目标

- 不迁移已建立长连接。
- 第一版不支持 UDP、QUIC、HTTP/3。
- 第一版不支持 sing-box。
- 第一版不支持混合核心队列。
- 第一版不承诺普通 HTTP 明文请求的请求级重试。
- 第一版不把 relay 暴露到局域网，即使普通本地入站允许 LAN 连接也不继承该行为。
- 不通过缩短健康检测周期伪装实时切换。
- 不把 Xray `fallbackTag` 当作请求级重试机制。
- 不改 UI 文案和入口形态。

## 推荐方案

新增 v2rayN 本地故障转移中继层，作为用户本地代理入口和 Xray 候选节点入口之间的请求级控制层。

```text
浏览器 / 系统代理 / Tun
        |
        v
v2rayN 本地 failover relay
        |
        |-- 尝试 P1 对应的本地 Xray 入站
        |      失败：关闭本次候选尝试，继续 P2
        |
        |-- 尝试 P2 对应的本地 Xray 入站
        |      成功：把当前请求接到 P2
        |
        |-- 尝试 P3 ...
```

这个 relay 不依赖 Xray 在 outbound 失败后重试其它 outbound。它在客户端请求还没有收到失败响应前，自己按优先级尝试候选节点。

## 运行入口原则

第一版 relay 必须保持当前本地代理入口的主要语义：同一个用户侧端口至少能识别 SOCKS5 和 HTTP CONNECT。当前 v2rayN 的 Xray 本地入站使用 `mixed` 协议，系统代理、PAC、下载代理和部分内部请求都可能依赖这个统一入口；因此 relay 不能只做单一 SOCKS5 端口。

入口切换必须满足以下顺序：

1. 先生成候选 Xray loopback 入站和固定路由。
2. 再启动 relay 并确认监听成功。
3. 最后才把系统代理、PAC 或内部本地代理入口指向 relay。

如果第 1 步或第 2 步失败，保持原运行状态，不把任何入口切到无效端口，也不降级直连。

## 为什么不能只做 TCP 盲转发

如果 relay 只是把客户端 TCP 流量转发到 P1 的本地 Xray 入站，P1 失败时客户端可能已经收到 SOCKS/HTTP 失败响应，或者连接已经被关闭。此时同一请求无法再安全重放到 P2。

因此 relay 必须理解最小代理协议握手：

- SOCKS5 TCP CONNECT：解析客户端认证协商和 CONNECT 目标；对每个候选重新发起 SOCKS5 CONNECT；候选成功后再向客户端返回成功。
- HTTP CONNECT：解析 CONNECT 请求头；对每个候选重新发送 CONNECT；候选返回 2xx 后再向客户端返回成功。

第一版只处理上述两类 TCP 隧道请求。普通 HTTP 明文请求可以先返回代理错误，或在后续版本单独设计可重试语义。

普通 HTTP 明文请求不能简单重放。原因是请求体可能已经被 relay 读入或部分发送，且不同方法的幂等语义不同。第一版遇到非 CONNECT 的 HTTP 请求时，推荐返回明确的代理错误，不尝试对候选节点重放。

## 组件设计

### FailoverRelayService

职责：

- 监听本地故障转移入口端口。
- 接收 SOCKS5 TCP CONNECT 和 HTTP CONNECT 请求。
- 按优先级选择候选节点。
- 对候选本地 Xray 入站执行握手。
- 候选失败时继续尝试下一个候选。
- 候选成功后进行双向流量转发。
- 维护请求级失败产生的临时失败状态。

不负责：

- 解析真实节点密钥。
- 下载订阅。
- 修改健康检测算法。
- 承载 UI 状态展示。

### CandidateInboundManager

职责：

- 为故障转移队列中的每个候选分配本地 Xray 入站端口。
- 生成 inboundTag 到 outboundTag 的固定路由。
- 保证 P1/P2/P3 的本地入口只会路由到各自对应 outbound。
- 校验候选入站数量、端口和队列成员一致；不一致时阻止 relay 入口切换。

示例结构：

```text
127.0.0.1:21001 -> inboundTag failover-p1-in -> outboundTag proxy-1-p1
127.0.0.1:21002 -> inboundTag failover-p2-in -> outboundTag proxy-2-p2
127.0.0.1:21003 -> inboundTag failover-p3-in -> outboundTag proxy-3-p3
```

这些入站只监听 loopback，不暴露到局域网。

### RuntimeEntrySwitcher

职责：

- 在 Xray 配置生成成功、候选入站就绪、relay 监听成功后，切换用户侧入口。
- 在 relay 启动失败、Xray 配置生成失败或候选队列为空时，阻止系统代理和内部入口切到 relay。
- 关闭故障转移或队列变化时，按“先准备新入口，再切换，再释放旧入口”的顺序处理。

不负责：

- 改写节点订阅或节点密钥。
- 修改系统代理例外列表。
- 把失败链路降级到 direct。

### FailoverCandidateState

候选节点状态：

```text
Healthy
Suspect
Failed
Recovering
```

状态含义：

| 状态 | 含义 | 新请求是否可选 |
| --- | --- | --- |
| `Healthy` | 最近请求或健康检测正常 | 是 |
| `Suspect` | 当前请求失败过一次，但还未达到失败阈值 | 可以降级尝试 |
| `Failed` | 请求级失败或健康检测确认失败 | 否，直到健康检测确认恢复 |
| `Recovering` | 健康检测刚恢复，等待一次请求确认 | 是，但成功后转 `Healthy` |

第一版可以采用保守规则：

- 当前请求连接失败：立即把候选标记为 `Failed`，并从用户请求候选中排除。
- 健康检测恢复：把候选标记为 `Recovering`。
- `Recovering` 请求成功：转 `Healthy`。
- `Recovering` 请求失败：回到 `Failed`。
- 多个高优先级候选同时 `Failed` 时，新请求必须直接跳过这些候选，避免 P1 到 P4 各等待一次候选握手超时。

## 请求流程

### SOCKS5 TCP CONNECT

```text
1. 客户端连接 failover relay。
2. relay 读取 SOCKS5 greeting。
3. relay 返回 no-auth。
4. relay 读取 CONNECT 请求，得到目标地址和端口。
5. relay 按优先级遍历可用候选。
6. relay 连接候选本地 Xray 入站。
7. relay 对候选入站执行 SOCKS5 greeting 和 CONNECT。
8. 候选返回成功：
   - relay 向客户端返回 SOCKS5 success。
   - relay 开始双向转发。
9. 候选连接失败、超时或返回失败：
   - relay 关闭候选连接。
   - 标记候选失败。
   - 尝试下一个候选。
10. 所有候选失败：
   - relay 向客户端返回 SOCKS5 failure。
```

### HTTP CONNECT

```text
1. 客户端连接 failover relay。
2. relay 读取完整 CONNECT 请求头。
3. relay 按优先级遍历可用候选。
4. relay 连接候选本地 Xray 入站。
5. relay 把 CONNECT 请求发送给候选入站。
6. 候选返回 2xx：
   - relay 把 2xx 返回给客户端。
   - relay 开始双向转发。
7. 候选连接失败、超时或返回非 2xx：
   - relay 关闭候选连接。
   - 标记候选失败。
   - 尝试下一个候选。
8. 所有候选失败：
   - relay 返回 `502 Bad Gateway`。
```

## 超时策略

第一版使用固定内部参数，不暴露 UI：

| 项目 | 建议值 | 目的 |
| --- | --- | --- |
| 连接候选本地入站超时 | 300ms | 本地 loopback 连接不应长时间等待 |
| 候选 CONNECT 握手超时 | 500ms | 限制 P1 故障时用户感知卡顿 |
| 单请求候选最大尝试数 | 队列长度 | 保持用户配置的完整优先级语义 |
| 恢复确认窗口 | 1 次成功请求 | 避免健康检测偶发成功导致立即回切抖动 |

后续可以把这些参数移到隐藏高级配置，但第一版不需要 UI。

relay 不维护独立恢复计时。P1 被标记为 `Failed` 后，普通用户请求会跳过 P1；只有健康检测确认 P1 恢复，relay 才把 P1 放回可选候选。这样在 P1 到 P4 都故障、P5 可用时，用户请求会直接走 P5，不会依次为 P1、P2、P3、P4 各等待一次 `500ms`。

## 与健康检测的关系

职责划分：

| 能力 | 负责组件 |
| --- | --- |
| 当前请求 P1 失败后尝试 P2 | `FailoverRelayService` |
| 节点标签、延迟、失败次数展示 | 现有健康检测 |
| P1 恢复后新请求回到 P1 | 健康检测确认恢复后更新 relay 状态 |
| 队列成员变化后重建候选入口 | 配置生成和 relay 生命周期管理 |

健康检测不参与故障瞬间切换。它负责恢复回切和状态展示。失败节点不会由用户请求重新试探；健康检测的轮询、退避和恢复判定继续复用现有健康检测服务。

## 与现有故障队列运行态的关系

当前普通“故障转移”在 Xray-only 队列中会解析为虚拟 `PolicyGroup`，再由 Xray `balancer`、`leastLoad`、`fallbackTag` 和 `burstObservatory` 处理。relay 方案落地后，普通 Xray-only 故障转移不应再把请求级重试责任交给该虚拟 `PolicyGroup`。

推荐运行态拆分：

| 模式 | 运行态 |
| --- | --- |
| 普通 Xray-only 故障转移 | 使用本地 relay 和每候选 Xray loopback 入站 |
| 最小延迟、负载均衡、手动选择等策略 | 继续使用现有策略，不启用 relay |
| sing-box 或混合核心队列 | 第一版继续阻止或走现有受限逻辑，不启用 relay |

这样可以保留现有故障分组、队列排序、健康检测和 UI 展示能力，同时把“当前请求失败后重试下一个候选”的职责放到 v2rayN 本地层。

## Xray 配置生成要求

对于故障转移队列，Xray 配置应包含：

- 每个候选节点一个真实 outbound。
- 每个候选节点一个 loopback inbound。
- 每个候选 inbound 一条固定 routing rule。
- 不再依赖 Xray `balancer` 实现请求级失败重试。

示意：

```json
{
  "inbounds": [
    {
      "tag": "failover-p1-in",
      "listen": "127.0.0.1",
      "port": 21001,
      "protocol": "mixed"
    },
    {
      "tag": "failover-p2-in",
      "listen": "127.0.0.1",
      "port": 21002,
      "protocol": "mixed"
    }
  ],
  "routing": {
    "rules": [
      {
        "type": "field",
        "inboundTag": ["failover-p1-in"],
        "outboundTag": "proxy-1-p1"
      },
      {
        "type": "field",
        "inboundTag": ["failover-p2-in"],
        "outboundTag": "proxy-2-p2"
      }
    ]
  }
}
```

实际实现时仍要保留 direct、block、DNS、统计等现有基础配置；上面只展示故障转移相关结构。

候选入站使用 `mixed` 协议是为了同时接收 relay 发起的 SOCKS5 CONNECT 和 HTTP CONNECT。它们仍然只监听 `127.0.0.1`，不继承用户普通入站的 `AllowLANConn`。

## Tun 兼容要求

Tun 场景必须单独验证，不能假设普通系统代理路径验证通过就等价可用。实现时需要保证：

- Tun 转入代理流量时，入口仍然先到 relay，再由 relay 选择候选 Xray 入站。
- 现有 Tun 保护链路、pre-socks 链路和 `ProtectDomainList` 仍然生效，避免核心连接自身代理地址或节点域名解析路径被破坏。
- relay 连接候选 loopback 入站的本地连接不得被 Tun 捕获后再次送回 relay。
- Tun 场景验证失败时，Xray-only relay 不能默认在 Tun 模式下启用。

## 代理安全边界

- relay 只监听 `127.0.0.1`。
- 候选本地 Xray 入站只监听 `127.0.0.1`。
- 所有候选失败时返回代理错误，不直连。
- 队列为空时不启动 relay。
- 候选节点解析失败时不把请求交给 direct。
- relay 监听失败、候选入站缺失或端口冲突时，不切换系统代理、PAC、Tun 或内部代理入口。
- relay 不继承普通入站的 LAN 监听设置。
- 日志默认只记录候选 tag、失败类型和状态变化。
- 不记录目标完整 URL、节点密钥、订阅链接、Token、密码、UUID、`privateKey`、`shortId`、`serverName`。
- 不把失败请求降级到系统代理外的网络路径。

## 错误处理

| 场景 | 行为 |
| --- | --- |
| P1 本地入站连接失败 | 标记 P1 失败，尝试 P2 |
| P1 CONNECT 握手超时 | 标记 P1 失败，尝试 P2 |
| P1 返回代理失败 | 标记 P1 失败，尝试 P2 |
| P2 成功 | 当前请求使用 P2，并开始双向转发 |
| P1 到 P4 均为 Failed，P5 可用 | 新请求跳过 P1 到 P4，直接尝试 P5 |
| 健康检测确认 P1 恢复 | P1 进入 `Recovering`，新请求可回到 P1 |
| 所有候选失败 | SOCKS5 返回 failure，HTTP CONNECT 返回 `502 Bad Gateway` |
| 普通 HTTP 明文请求 | 第一版返回代理错误，不做候选重放 |
| relay 启动失败 | 保留原运行状态，不切直连 |
| Xray 配置生成失败 | 不启动 relay，不修改系统代理到无效端口 |

## 测试策略

### 单元测试

- SOCKS5 greeting 和 CONNECT 解析。
- HTTP CONNECT 请求头解析。
- 同一 relay 入口区分 SOCKS5 和 HTTP CONNECT。
- 非 CONNECT HTTP 请求返回代理错误，不触发候选重放。
- 候选排序和失败跳过。
- `Healthy -> Failed -> Recovering -> Healthy` 状态转换。
- `Failed` 候选不会被用户请求选中，直到健康检测确认恢复。
- 多个高优先级候选 `Failed` 时，新请求直接跳到第一个可用候选。
- 所有候选失败时不返回 direct。
- relay 启动失败时入口切换被阻止。

### 集成测试

使用本地假服务：

```text
P1 local inbound -> 故意拒绝或断开
P2 local inbound -> 返回 p2-ok
客户端 -> failover relay
```

验收：

- P1 失败时，同一 SOCKS5 CONNECT 请求成功落到 P2。
- P1 失败时，同一 HTTP CONNECT 请求成功落到 P2。
- P1 被标记 `Failed` 后，新请求直接尝试 P2。
- P1 到 P4 均被标记 `Failed` 时，新请求直接尝试 P5，不依次等待 P1 到 P4 的 `500ms` 候选握手超时。
- 健康检测未确认 P1 恢复时，新请求仍不尝试 P1。
- P1 恢复并进入 `Recovering` 后，新请求可回到 P1。
- P1/P2 都失败时，请求失败且不直连。
- 系统代理或 PAC 指向 relay 后，HTTPS CONNECT 请求仍可走 P1/P2。
- Tun 开启时验证 relay 不造成回环、直连或 DNS/节点域名保护失效；如果未通过，第一版在 Tun 模式下禁用 relay。

### 回归测试

- 保留现有 `CoreConfigV2rayServiceTests` 中对 Xray 配置结构的测试。
- 新增 relay 行为测试，避免再次把 Xray balancer 误认为请求级重试。

## 迁移与兼容

第一版只在普通“故障转移”队列且候选核心均为 Xray 时启用 relay。其它模式继续走现有逻辑：

- 普通单节点：不启用 relay。
- 链式代理：不启用 relay。
- 负载均衡、最小延迟、测速策略：不启用 relay。
- sing-box：不启用 relay。
- 混合核心队列：不启用 relay。
- Tun：必须通过单独验证后才启用 relay；未验证前保持现有逻辑。

如果 relay 启动失败，应阻止切换到 relay 入口，避免用户流量进入无效端口。

## 验收标准

- 关闭 P1 服务器后，已发起的新 TCP CONNECT 请求能在失败窗口内尝试 P2。
- P1 被标记失败后，新请求不再先撞 P1。
- 多个高优先级节点失败后，新请求跳过所有 `Failed` 候选，不把用户请求用于恢复探测。
- `Failed` 候选只有在健康检测确认恢复后才重新进入候选。
- P1 恢复并通过健康检测后，新请求回到 P1。
- 所有候选失败时请求失败，不直连。
- relay 启动失败、候选入站缺失或端口冲突时，系统代理/PAC/Tun 不会切到无效 relay 入口。
- 普通 HTTP 明文请求不会被错误重放到多个候选。
- Tun 模式要么通过单独验证，要么第一版明确禁用 relay。
- `.NET` 单元测试覆盖 SOCKS5、HTTP CONNECT、状态机和安全失败。
- 本地集成测试证明 P1 断开后同一请求落到 P2。
- 验证记录不包含节点密钥、订阅链接、Token、密码或真实代理鉴权字段。

## 推荐实施顺序

1. 先实现 relay 的 SOCKS5 TCP CONNECT 最小闭环。
2. 再生成每候选本地 Xray 入站和固定路由，验证 P1 失败同一请求落到 P2。
3. 再实现 HTTP CONNECT 和 mixed-like 入口识别。
4. 再接入候选状态机和入口切换门禁。
5. 再补系统代理、PAC 和 Tun 兼容验证。
6. 最后接入健康检测恢复回切。

这个顺序可以最早验证核心目标：P1 当前请求失败后，同一请求是否能切到 P2。
