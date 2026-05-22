# 延迟转移核心兜底队列设计

## 目标

`延迟转移` 当前以外层健康探测结果选择最低延迟节点，并通过重载核心切换运行目标。这个模式能实现“按延迟选择”，但当前运行节点故障时，不能像 `故障转移` 一样由核心内部马上兜底。

本设计的目标是把 `延迟转移` 改为“延迟排序 + 核心 fallback”：正常情况下优先连接当前最低延迟节点；最低延迟节点故障时，核心可以立即从参与转移的节点中落到次低延迟节点；后台健康探测继续刷新延迟排序，并在排序变化时重载核心。

## 当前依据

现有 `故障转移` 的即时切换来自核心 fallback 队列，而不是 v2rayN 外层即时 reload：

- `FailoverGroupManager.TryResolveRuntimeNode` 在同核心类型队列下会生成虚拟 `PolicyGroup`。
- `TryBuildVirtualPolicyGroup` 会把队列写入 `ChildItems`，并设置 `MultipleLoad = EMultipleLoad.Fallback`。
- Xray 配置生成会把 `Fallback` 转成 `leastLoad + fallbackTag + costs`。
- sing-box 配置生成会把 `Fallback` 转成 `selector + urltest`，并保持 outbounds 顺序。

现有 `延迟转移` 已具备后台真实延迟探测、7 秒快速决策、15 秒硬截止、活动节点显示和运行期启动目标字段。缺口是：延迟排序结果没有形成完整 fallback 队列，导致当前目标故障时不能完全复用核心内部的即时兜底能力。

## 推荐方案

采用“运行态延迟排序 fallback 队列”。

在 `EFailoverMode.LeastDelay` 下，读取活动故障组已启用队列后，不再只把最低延迟节点放到第一位或只选择单个 direct profile，而是生成完整的运行态队列顺序：

1. `Normal` 且 `LastDelay > 0` 的节点按 `LastDelay` 升序排列。
2. 同延迟时按用户原始 `Sort` 稳定排序。
3. `Unknown` 和 `Probing` 节点排在成功延迟节点之后，并按 `Sort` 保留兜底机会。
4. `Failed` 节点默认不参与运行态队列。
5. 如果除了 `Failed` 以外没有任何可用队列项，则把 `Failed` 节点按 `Sort` 放回队列末尾，避免生成空入口。

同核心类型时，`LeastDelay` 应和 `Failover` 一样优先生成虚拟 `PolicyGroup`，但 `ChildItems` 使用上述延迟排序后的运行态队列。这样核心内部可以在当前最低延迟节点故障时，马上尝试次低延迟或后续节点。

混合核心类型时，仍保持现有 direct-profile 降级路径。原因是 Xray 和 sing-box 不能放进同一个核心 fallback 队列；这种情况下只能依赖外层健康探测和 reload。

## 运行态排序语义

运行态排序只影响本次核心配置生成和主列表展示，不修改 `FailoverGroupItem.Sort`。用户维护的故障队列优先级仍保存在数据库中，切回 `故障转移` 或 `关闭` 后应恢复原始顺序。

延迟排序的输入只来自活动故障组中 `Enabled = true` 的队列项，不纳入未加入队列的候选节点。

排序示例：

| 节点 | 原始顺序 | 状态 | 延迟 | 运行态顺序 |
| --- | ---: | --- | ---: | ---: |
| A | 1 | Normal | 80 | 2 |
| B | 2 | Normal | 30 | 1 |
| C | 3 | Unknown | 0 | 3 |
| D | 4 | Failed | 15 | 不参与，除非无其它可用项 |

## 核心配置生成

### Xray

当活动队列同为 Xray 可运行类型时，`LeastDelay` 生成虚拟 `PolicyGroup`：

- `ChildItems` 按运行态延迟顺序写入。
- `MultipleLoad` 继续使用 `Fallback`。
- 现有 `V2rayBalancerService` 会生成 `leastLoad` balancer、`fallbackTag` 和优先级 `costs`。

这样当前最低延迟节点不可用时，Xray 内部会根据 fallback 配置尝试后续节点。

### sing-box

当活动队列同为 sing-box 可运行类型时，`LeastDelay` 生成虚拟 `PolicyGroup`：

- `ChildItems` 按运行态延迟顺序写入。
- `MultipleLoad` 继续使用 `Fallback`。
- 现有 `SingboxOutboundService` 会生成 `selector + urltest`，并保持 outbounds 顺序。

由于当前实现设置 `interrupt_exist_connections = false`，本设计不承诺迁移已建立连接；主要保证新连接和核心内部可切换路径。

## 健康探测与重载策略

后台健康探测继续使用现有真实延迟探测链路，并保持 `suppressFailover: true`，避免被活动 fallback 队列掩盖单节点真实状态。

探测结果写回后，如果同核心 `LeastDelay` 的虚拟 fallback 运行态队列顺序发生变化，应触发 `ReloadRequested`。判断不应只看第一节点变化，还应比较核心实际使用的运行入口签名。虚拟 fallback 入口的签名是完整运行态队列，因为第一节点不变但第二、第三节点顺序变化时，也会影响“当前节点故障后的即时兜底顺序”。混合核心 direct-profile 降级路径的签名只代表实际运行目标；此路径没有核心内部 fallback 队列，不应因为备用候选顺序变化触发无意义 reload。

7 秒快速决策和 15 秒硬截止继续保留。快速决策时如果已有成功延迟结果，可以先生成部分延迟排序队列；未完成节点按 `Unknown/Probing` 排在成功节点后，避免等待慢节点阻塞可用 fallback。

## UI 表示

活动故障组列表在 `延迟转移` 模式下继续隐藏 `P*` 优先级标签，避免把运行态延迟排序误读为用户手动优先级。

列表排序应反映运行态队列：当前核心入口队列的第一节点置顶，后续节点按延迟兜底顺序显示。当前实际活动标签仍以 `RunningFailoverTargetProfileId` 或待启动 `FailoverStartupProfileId` 为准。

UI 不承诺显示核心内部当前落到的子节点。核心 fallback 可能在连接级别切换，而 v2rayN 当前没有统一读取 Xray/sing-box 内部 fallback 状态的接口。

## 边界与失败处理

- 活动故障组为空时，不允许开启 `LeastDelay`。
- 队列无有效真实节点时，不生成空 fallback，沿用现有校验错误。
- 同核心类型队列优先生成核心 fallback。
- 混合核心类型队列继续 direct-profile 降级，不承诺即时切换。
- `Failed` 节点默认从运行态队列排除；只有所有节点都为 `Failed` 时才放回，避免入口为空。
- 核心重载失败时，不应清空已有运行目标；UI 应继续保持已有活动显示或待启动目标。

## 代理安全影响评估

本设计会改变 `LeastDelay` 模式下的核心配置入口，从单目标或第一节点优先模式调整为按延迟排序的 fallback 队列。所有候选仍限制在活动故障组已启用队列中，不扩大到未加入队列的节点。

探测继续使用单节点真实探测，并保持 `suppressFailover: true`，避免误判。设计不修改 Tun、系统代理、DNS、路由规则、TLS、订阅下载、证书信任或核心更新策略。无有效候选时不得静默直连，不得退化到系统默认出口。

## 验收标准

- `LeastDelay` 下同核心类型队列生成虚拟 `PolicyGroup`，且 `ChildItems` 按运行态延迟顺序排列。
- 最低延迟节点故障时，核心配置中仍包含次低延迟节点作为后续 fallback。
- `Failed` 节点默认不出现在运行态 fallback 队列；全失败时按原始顺序放回，避免空入口。
- 完整运行态队列顺序变化时触发 reload，不只比较第一节点。
- 混合核心类型队列保持 direct-profile 降级行为，并在说明中明确不承诺即时切换。
- `FailoverGroupItem.Sort` 不被改写。
- 自动化测试覆盖 Xray、sing-box、混合核心、全失败兜底、排序变化 reload、UI 排序和优先级标签隐藏。

## 后续实施计划边界

实施计划应拆成以下几块：

1. 提取或调整 `LeastDelay` 运行态队列排序方法。
2. 调整 `TryResolveRuntimeNode`，让同核心 `LeastDelay` 使用虚拟 fallback 入口。
3. 增加完整队列签名比较，决定是否 reload。
4. 更新 UI 展示排序与活动标签逻辑。
5. 补充自动化测试和 `_local_changes` 实际功能记录。

本设计阶段不修改业务代码；实施计划需在用户审阅本 spec 后另行编写。
