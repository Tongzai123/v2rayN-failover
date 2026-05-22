# Xray 故障转移配置与请求级行为验证计划

## 目标

先验证“故障转移”模式能否满足真实使用语义：

- `P1` 正常时，新请求优先走 `P1`。
- `P1` 请求失败时，确认当前 Xray 配置是否会由核心内部立即尝试 `P2`。
- 不等待 v2rayN 外层 45 秒健康检测轮询。
- 健康检测只更新状态标签，并在高优先级节点恢复后触发新请求回切。
- 健康检测不参与故障瞬间切换。
- 不修改 UI 文案和交互形态。

本计划只用于评审目标、边界和排查方案。当前阶段不选择备选实现方案，不引入 v2rayN 自建本地代理链，不修改 UI；先完成配置命中验证和 Xray 行为验证。

## 已确认前提

以下前提由用户确认，本计划不再重复验证：

- 当前使用的核心只考虑 Xray。
- 故障队列中的节点都已确认是 Xray 节点。
- 不考虑 sing-box。
- 不考虑混合核心队列。
- 当前问题不是混合核心类型导致的。
- 上一次提交已经单独做过核心类型检测能力。

因此，本计划聚焦于：

- Xray 故障转移配置是否生成正确。
- 当前 v2rayN 代码是否把故障队列正确写入 Xray。
- 当前生成的 Xray fallback 结构是否真的具备“P1 请求失败后尝试 P2”的行为，而不是只在路由阶段选择一个 outbound。
- 高优先级节点恢复后，如何让新请求回到高优先级节点。

## 当前问题

从发布目录和源码观察到的事实：

- 代码路径会把 Xray-only 普通“故障转移”队列构造成虚拟 `PolicyGroup`，并生成 `leastLoad + fallbackTag + costs`。
- `fallbackTag` 当前会指向第一个候选 outbound，也就是队列中的 `P1`。
- `burstObservatory` 的默认探测参数是 `interval = 5m`、`timeout = 30s`、`sampling = 2`。
- v2rayN 外层健康服务每轮探测可以更新节点标签，但这不是实时请求失败重试链路。
- 本轮评审曾看到当前工作区的 `publish/v2rayN-windows-64-desktop/binConfigs/config.json` 没有 `routing.balancers`，Google 规则仍是 `outboundTag = "proxy"`，outbounds 也只有 `proxy/direct/block`。这说明必须先确认用户测试时实际运行的配置文件是否已经进入故障队列 balancer。

用户测试现象：

- 关闭 `P1` 所在服务器后，网页无法访问 Google。
- 预期行为是 Xray 内部自动尝试 `P2`。
- 这说明至少存在两个待验证问题：实际运行配置可能没有命中 balancer；或者 `leastLoad + fallbackTag` 本身不具备请求级失败重试语义。

判断：

- 不能把问题归因于健康检测慢，因为健康检测不应参与实时切换。
- 不能把问题归因于混合核心，因为本场景已经确认都是 Xray。
- 需要先检查实际运行的 Xray 主配置，再验证 Xray fallback 队列的真实请求级行为。

## 范围

本计划覆盖：

- 普通“故障转移”模式。
- Xray-only 队列。
- `P1` 不可用时，验证新请求能否快速落到 `P2`。
- 健康标签与实时切换职责分离。
- `P1` 恢复后，新请求回到 `P1`。
- 当前 `leastLoad + fallbackTag + costs + burstObservatory` 生成结构是否正确进入实际运行配置。
- 当前 `leastLoad + fallbackTag` 是否只是路由选择机制，还是包含连接失败后的请求级重试机制。

本计划不覆盖：

- sing-box。
- 混合核心队列。
- 已建立长连接迁移。
- UI 文案调整。
- 通过缩短健康检测周期模拟实时切换。
- v2rayN 自建本地代理链。

## 核心原则

1. 健康检测只做标签。

   `LastStatus`、`LastDelay`、`FailureCount` 等字段只服务 UI 状态展示、延迟类逻辑和恢复回切判断。普通“故障转移”的故障瞬间切换如果成立，必须由 Xray 核心配置完成。

2. 只验证 Xray fallback 行为，不验证核心类型。

   核心类型已经确认。后续验证只看实际运行配置是否进入 Xray balancer，以及当前 Xray 配置是否真的能让 `P1` 失败后请求落到 `P2`。

3. 若 Xray 当前结构验证失败，先告知用户。

   如果发现当前 `leastLoad + fallbackTag` 结构本身无法满足目标，先报告结论和证据。本计划不继续展开其它实现方案。

4. 不允许静默直连。

   队列为空、配置生成失败、所有候选不可用时，请求可以失败，但不得绕过代理直连。

## 推荐验证方案

只保留一个验证方案：先确认实际运行配置是否进入 balancer，再验证 Xray 当前结构的真实请求级行为。

### 方案说明

第一步，确认用户测试时实际运行的 `config.json` 是否包含故障队列：

- `routing.balancers` 中存在 `proxy-balancer` 或当前 proxy tag 对应的 balancer。
- Google 访问规则最终使用 `balancerTag`，而不是仍停留在固定 `outboundTag = "proxy"`。
- `outbounds` 中存在 P1、P2 对应 tag。
- `routing.balancers[].selector` 可以选中 P1、P2 对应 outbound。
- `burstObservatory.subjectSelector` 覆盖该 balancer 候选 tag 前缀。

第二步，构造一个最小 Xray 场景，验证当前 v2rayN 生成的 Xray fallback 结构是否满足目标行为：

- `P1` 指向不可用地址或未监听端口。
- `P2` 指向可用测试服务。
- 客户端通过故障队列入口发起请求。
- 观察请求是否能成功落到 `P2`。

这里验证的是 Xray fallback 配置行为，不是节点核心类型，也不是 v2rayN 外层健康检测。

### 可能结果

结果 A：实际运行配置没有进入 balancer。

- 说明当前测试没有真正使用故障队列配置。
- 先定位 v2rayN 配置生成、运行节点解析、路由规则转换或发布目录配置更新问题。
- 不进行 Xray 行为结论判断，因为请求根本没有经过 balancer。

结果 B：实际运行配置进入 balancer，且请求成功落到 `P2`。

- 说明 Xray 具备这条路径需要的核心内部 fallback 能力。
- 问题更可能在 v2rayN 生成配置时：
  - 队列顺序写错。
  - `fallbackTag` 指向错误。
  - `selector` / `balancer` 匹配范围错误。
  - 路由规则没有命中 balancer。
  - outbound tag 生成与 balancer 选择不一致。
  - P2 没有进入实际参与 fallback 的 outbound 集合。

结果 C：实际运行配置进入 balancer，但请求没有落到 `P2`。

- 说明当前 `leastLoad + fallbackTag` 结构无法证明满足请求级失败重试，或者该结构用法错误。
- 优先判断它是否只是“路由阶段按 observatory 状态选择 outbound”，而不是“已选 outbound 连接失败后重试其它 outbound”。
- 先告知用户验证结论。
- 本计划到此停止，不展开备选方案。

## 故障与恢复策略

期望策略仍是“故障即时避开，恢复后健康检测回切”。但在 Xray 行为验证通过前，它只能作为目标语义，不能作为已实现事实。

### 故障时

当 `P1` 请求失败时：

- 不能等待 v2rayN 外层健康检测。
- 不能通过 45 秒轮询后重载来实现首次故障切换。
- 若 Xray 当前结构支持请求级重试，应由 Xray 内部快速尝试 `P2`。
- 如果高优先级存在多个故障节点，新请求不应每次都从所有故障节点逐个撞一遍，否则会明显拖慢网页访问。

因此，不采用“每个请求都从 P1 开始向下流转”的恢复/故障统一策略。

### 故障后运行态

`P1` 被判定不可用后，运行态应优先保持可用路径：

```text
原始优先级：P1 -> P2 -> P3
P1 故障后：P2 -> P3
```

此时 UI 标签可以稍后才显示 `P1` 为“故障”。只有在 Xray 行为验证通过后，才能认为标签延迟不影响请求已经走 `P2`。

### 恢复时

当 v2rayN 外层 45 秒健康检测发现 `P1` 恢复：

- 将 `P1` 状态从 `Failed` 更新为 `Normal`。
- 触发核心配置刷新或运行队列更新。
- 新请求重新按原始高优先级从 `P1` 开始。
- 已建立连接不强制迁移，继续按 Xray 连接生命周期处理。

恢复后目标状态：

```text
P1 恢复前：P2 -> P3
P1 恢复后：P1 -> P2 -> P3
```

这意味着健康检测不参与故障瞬间切换，但参与恢复回切。这个职责划分是有意设计：

- 故障切换要求快，必须在核心内部完成。
- 恢复切回不要求毫秒级，可以由 45 秒健康检测确认后触发。
- 用户可以通过标签从“故障”变为“正常/活动”理解当前已恢复高优先级路径。

## 排查重点

### 1. 实际运行配置是否正确命中 balancer

这是第一优先级。需要确认用户测试时实际运行的配置文件，而不是只看源码推测。Google 相关路由必须真正进入 `proxy-balancer` 或当前 proxy tag 对应的 balancer，而不是落到单独 outbound。

检查点：

- `routing.rules` 中访问 Google 的规则是否设置 `balancerTag`。
- 是否存在更早的规则把请求导向 `direct`、`block` 或固定 outbound。
- 系统代理入口是否实际走当前 `config.json`。
- `publish/v2rayN-windows-64-desktop/binConfigs/config.json`、应用实际工作目录下的 `binConfigs/config.json`、当前运行核心命令行参数指向的配置文件是否一致。

### 2. fallback 队列是否包含 P2

需要确认 `P2` 不只是显示在 UI 队列中，而是真的进入 Xray outbounds 和 balancer 选择范围。

检查点：

- `outbounds` 中是否存在 P1、P2 对应 tag。
- tag 是否符合 balancer 的 `selector` 匹配规则。
- `costs` 是否按 P1、P2 顺序生成。
- `fallbackTag` 是否误指向不该兜底的节点。

### 3. `fallbackTag` 语义是否被代码误用

当前配置中 `fallbackTag` 指向 `P1`。需要确认 Xray 对 `fallbackTag` 的语义到底是不是“首选节点”，还是“策略无法选出 outbound 时的兜底节点”。

如果它不是首选节点语义，那么当前代码把 `fallbackTag` 设为 P1 不能被当作“P1 优先、失败后 P2”的证据。

### 4. `burstObservatory` 是否影响实时失败判断

当前 `burstObservatory` 间隔是 `5m`。如果 Xray 的 `leastLoad` 依赖 observatory 状态，而不是连接失败即时重试，那么关闭 `P1` 后短时间仍选中 `P1` 就符合现象。

需要确认：

- Xray 是否在单次连接失败时重新选择其他 outbound。
- 还是只根据 observatory 最近状态选择 outbound。
- `burstObservatory` 未更新前，是否会持续选中 P1。

### 5. P1 恢复后是否能回到高优先级

需要确认健康探测从 `Failed` 更新为 `Normal` 后，v2rayN 是否会触发核心刷新或运行队列恢复。

检查点：

- `FailoverHealthStateMachine.ApplyProbeResult` 将 `P1` 恢复为 `Normal` 后，是否有事件触发核心重载。
- 普通“故障转移”模式下，运行队列签名是否会因为 `Failed -> Normal` 变化而改变。
- 如果签名只包含节点 ID，不包含健康状态，则恢复后可能不会重载。
- 需要设计一个恢复回切触发条件：高优先级节点从 `Failed` 恢复为 `Normal`，且它排序高于当前运行入口时，触发核心刷新。

### 6. v2rayN 生成逻辑是否和 Xray 期望结构不匹配

重点文件：

- `v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`
- `v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayBalancerService.cs`
- `v2rayN/ServiceLib/Services/CoreConfig/V2ray/CoreConfigV2rayService.cs`
- `v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayRoutingService.cs`
- `v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayConfigTemplateService.cs`

## 用户可见行为

验证通过并进入实现后，用户可见行为目标是：

- UI 仍然是当前“故障转移”入口，不改名。
- 健康标签仍按周期更新。
- 关闭 `P1` 服务器后，如果 Xray 当前结构验证通过，刷新 Google 时请求应快速使用 `P2`。
- `P1` 恢复并被健康检测确认为正常后，新的请求重新优先使用 `P1`。
- 如果实际运行配置没有命中 balancer，先修正配置生成或运行配置指向问题。
- 如果实际运行配置命中 balancer 但 Xray 当前结构验证失败，先把证据和结论告知用户；本计划不展开备选实现方案。

## 错误处理

- 队列为空：不允许开启故障转移。
- P2 未进入实际配置：视为配置生成错误。
- Xray 配置生成失败：保留原运行状态，不切直连。
- P1、P2 都不可用：请求失败，健康标签稍后反映。
- Xray fallback 结构验证失败：停止继续假设，先告知用户，不在本计划内展开其它实现路径。

## 健康检测定位

健康检测职责：

- 周期性探测队列节点。
- 更新健康标签。
- 写入延迟、失败次数和失败原因。
- 发现高优先级故障节点恢复后，触发新请求回切高优先级节点。

健康检测不负责：

- 捕获用户当前请求失败。
- 在请求失败时触发切换。
- 作为普通“故障转移”的实时切换依据。

职责边界：

- `P1 -> P2` 的故障瞬间切换：如果当前结构验证通过，由 Xray 内部完成。
- `P2 -> P1` 的恢复回切：由健康检测确认恢复后触发核心刷新。

健康探测中的并发写库问题可以作为独立修复项处理，但不能把它当成本次核心内部切换失败的根因。

## 代理安全影响评估

本计划只检查和修正 Xray 故障队列配置，不扩大代理候选范围。

不允许以下行为：

- P1/P2 都不可用时静默直连。
- 为了保证网页可打开而绕过代理、DNS、TLS 或路由规则。
- 把未加入故障队列的节点纳入候选。
- 在验证当前结构前，用健康检测 reload 替代核心内部实时切换。
- 每次请求都反复撞已知故障的高优先级节点。

本计划验证通过后的后续实现可能影响：

- Xray 核心配置生成。
- Xray balancer / fallback 相关逻辑。
- 故障队列运行态解析。

本计划不影响：

- UI 文案和交互。
- sing-box。
- 混合核心。
- Tun 开关语义。
- 系统代理开关。
- 订阅下载。
- 证书信任。
- 核心组件更新策略。

## 已确认评审结论

- 同意“健康检测只做标签，不参与实时切换”作为硬边界。
- 同意“健康检测可参与恢复回切，但不参与故障瞬间切换”。
- 不再验证节点是否为 Xray；用户已确认所有节点都是 Xray。
- 不验证 sing-box。
- 不考虑混合核心场景。
- 如果 Xray 当前结构验证失败，先告知用户。
- 不改 UI。
- 验证结论：实际运行配置已经进入 Xray `balancer`；但最小 Xray 场景显示，当 P1 起初健康、随后在 observatory 下一轮探测前失效时，请求仍选中 P1 并失败，未观察到同一请求自动重试 P2。本计划停止，不展开备选方案。

## 验收标准

本计划阶段的验收标准：

- 明确用户测试时实际运行的 `config.json` 路径。
- 确认 Google 访问规则实际是否命中 Xray balancer。
- 确认 P1、P2 是否存在于实际 Xray outbounds 和 balancer 选择范围内。
- 确认 `burstObservatory.subjectSelector` 是否覆盖故障队列候选 tag。
- 用最小 Xray 场景验证 `leastLoad + fallbackTag` 是否会在 P1 连接失败后让请求落到 P2。
- 验证结论必须区分“配置没有进入 balancer”和“进入 balancer 但 Xray 不重试 P2”。
- 验证过程中无可用候选时不会直连。
- 不引入 sing-box 或混合核心处理。
- 不修改 UI。
- 不展开备选实现方案。

## 下一步

用户确认本计划后，先写验证实施计划。

验证实施计划应只围绕 Xray 展开：

1. 找到用户测试时核心实际加载的 `config.json`。
2. 检查实际 `config.json` 是否包含 `routing.balancers`、Google 规则 `balancerTag`、P1/P2 outbounds 和 `burstObservatory`。
3. 如果实际配置没有进入 balancer，定位 v2rayN 运行节点解析、故障队列虚拟节点生成、路由规则转换或配置文件写入问题。
4. 如果实际配置已进入 balancer，构造 Xray-only 最小复现场景。
5. 用 P1 不可用、P2 可用的场景验证 `leastLoad + fallbackTag` 是否具备请求级失败重试。
6. 记录验证证据：核心日志、访问结果、P2 是否收到请求、最终配置片段。
7. 根据验证结果更新计划结论；若验证失败，本计划停止，不展开备选实现方案。
8. 补充 `_local_changes/` 实际改动记录。
