# 故障分组与故障转移总实施方案

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**目标：** 在 v2rayN Desktop 中新增“故障分组”和“故障队列”能力，让用户把普通分组节点复制到独立故障队列；先验证并补齐核心 fallback 的真实行为，再通过核心 fallback 能力完成第一版自动故障转移。

**架构：** 第一阶段采用“实施前门禁 + 故障分组 UI + 虚拟 fallback 入口”的路线：先用测试证明 Xray 与 sing-box 生成的 fallback 配置是否满足 P1 优先语义；满足后再由 v2rayN 负责故障分组标记、队列管理、活动故障组开关和虚拟 `PolicyGroup` 入口。后续阶段再补外层健康状态、熔断半开、恢复策略和更细粒度的“正常/故障/P*”运行时标记。

**技术栈：** .NET、Avalonia、ReactiveUI、SQLite-net、v2rayN ServiceLib、Xray/sing-box 核心配置生成。

---

## 总体目标

本功能最终要解决的问题是：用户可以建立一个或多个“故障分组”，从普通节点列表中把节点复制到这些故障分组的独立故障队列；开启故障转移后，系统按 P1、P2、P3 顺序使用队列节点，主节点故障时自动切换到下一个节点，并在 UI 上清晰显示队列优先级、当前工作节点和故障状态。

“复制到故障分组”是核心原则：普通节点的原始分组归属不被改变，故障队列只引用或复制这些节点的故障用途状态。这样不会破坏订阅分组、订阅更新和用户原有节点管理习惯。

## 参考实现结论

`cc-switch-main` 的故障转移是“队列 + 请求结果记录 + 熔断器 + UI 同步切换”。关键点包括：

- 故障转移开启后，只按队列顺序选择候选 provider。
- 失败 provider 进入熔断状态，冷却后半开探测。
- 切换成功后通知 UI 更新当前工作对象。

v2rayN 与 `cc-switch-main` 不同：v2rayN 是本地代理客户端，主要通过当前节点和核心配置重载/核心内部 selector、urltest、fallback 实现代理链路选择。因此第一版不直接做请求级路由器，而是优先复用 v2rayN 已有 `PolicyGroup` 和 `EMultipleLoad.Fallback`，降低实现风险。

但这里有一个必须先验证的事实边界：当前代码中 `EMultipleLoad.Fallback` 不一定等同于“严格按 P1、P2、P3 顺序尝试”。Xray 侧现有 `GenBalancer` 对 `Fallback` 没有专门策略映射，可能落到 `roundRobin`；sing-box 侧现有实现是 `selector + urltest`，`Fallback` 只把 `urltest.tolerance` 设置为 `5000`。所以第一阶段开始前必须先写测试确认实际生成配置，并根据测试结果补齐核心配置生成逻辑。

## 分阶段路线

### 第零阶段：核心 fallback 行为门禁

第零阶段必须先完成，不通过则不得进入第一阶段 UI 和配置接入。

目标：

- 用单元测试固定 Xray 与 sing-box 对 `EMultipleLoad.Fallback` 的生成结果。
- 明确当前实现是否能保证 P1 优先、P1 不可用后使用 P2。
- 如果当前实现不能保证，先补核心配置生成逻辑，至少保证第一阶段支持的核心类型行为真实可靠。

验收：

- `ServiceLib.Tests` 中有覆盖 Xray fallback 生成的测试。
- `ServiceLib.Tests` 中有覆盖 sing-box fallback 生成的测试。
- 测试结论必须写入本功能 `_local_changes/NNN-failover-groups/tests.md`。
- 如果只能先可靠支持 Xray 或 sing-box 其中一个核心，UI 必须在开启故障转移时对不支持核心给出明确阻止提示，不能静默退化成轮询、随机或直连。

### 第一阶段：故障分组与核心 fallback 联动

第一阶段目标是完成可用闭环：用户能创建故障分组、把普通节点复制到故障分组、维护队列顺序、设置活动故障组、开启故障转移，并让核心按 fallback 方式使用队列。

第一阶段不做精细熔断 UI，不承诺准确显示每个节点的实时“正常/故障”状态。原因是核心内部 fallback 的健康状态不一定完整暴露给 v2rayN UI，强行显示会制造不可靠状态。

第一阶段 UI 只可靠展示“是否加入故障队列”和“队列优先级 P*”。如果尚未实现外层健康探测，禁止显示“正常/故障”这类运行时健康标签，避免让用户误以为 UI 已经感知核心内部状态。

### 第二阶段：故障队列状态与主动检测

第二阶段补充 v2rayN 外层状态表，记录故障队列节点的健康、失败次数、上次失败时间、冷却截止时间和半开探测状态。UI 开始显示“正常/故障/P*”标签。

主动检测应复用 v2rayN 原有“测试真连接延迟”的高效批量临时核心模式：按核心类型把多个故障队列节点放入同一个临时测速核心，并发访问各自本地 SOCKS 端口。每个节点执行 `2` 次 HTTP GET 并取最小耗时，探测结果同时写回故障健康状态和主界面普通“延迟”列。后台健康探测不直接调用完整手动测速 UI 流程，不执行失败批次重试，避免周期任务清空延迟列、刷屏或变重。

健康探测必须继续传入 `suppressFailover: true` 或等价保护，确保检测的是目标真实节点本身，而不是被活动 fallback 组兜底后的链路。禁止把长期方案做成“每个节点单独启动一个临时核心再顺序检测”，该方式只能作为早期验证实现或回退方案。

### 第三阶段：外层熔断与恢复策略

第三阶段实现类似 `cc-switch-main` 的熔断模型：连续失败达到阈值后熔断，冷却后半开探测，恢复后新连接优先回到更高优先级节点，但不中断已有连接。

### 第四阶段：增强体验

第四阶段优化拖拽排序、故障历史日志、手动重置故障状态、导入导出迁移和更细的提示文案。

## 数据设计

### `SubItem`

新增字段：

- `IsFailoverGroup`: `bool`，标记该订阅分组是否为故障分组。

说明：

- 该字段用于过滤“复制到故障分组”的二级菜单。
- 故障分组仍可显示在主界面分组列表中，但 UI 应与普通组区分。
- 普通订阅更新逻辑不得因为故障分组标记而删除原节点。

### 新增 `FailoverGroupItem`

建议新增 SQLite 表：

- `Id`: 主键。
- `GroupId`: 故障分组 `SubItem.Id`。
- `SourceProfileId`: 原普通节点 `ProfileItem.IndexId`。
- `FailoverProfileId`: 预留给副本模式使用；第一阶段引用模式下保持为空或等于 `SourceProfileId`，但业务逻辑不得依赖它。
- `Sort`: 队列顺序，决定 P1、P2、P3。
- `Enabled`: 是否加入故障队列。
- `LastStatus`: 预留，取值可为 `Unknown`、`Normal`、`Failed`、`Probing`。
- `LastFailureTime`: 预留。
- `LastFailureReason`: 预留。
- `Memo`: 预留。

第一阶段推荐采用引用模式：不复制完整节点配置，只记录 `SourceProfileId` 和队列顺序。生成 fallback 组时通过 `SourceProfileId` 找原节点。这样可以避免订阅节点更新后故障组副本陈旧。

引用模式必须处理悬空引用：

- 读取故障队列时，如果 `SourceProfileId` 已不存在，该项不得进入核心 fallback 子节点列表。
- 列表 UI 可以显示失效项，也可以在刷新时清理失效项；无论选择哪种，开启故障转移前必须过滤掉失效项。
- 如果过滤后队列为空，必须阻止开启故障转移。
- 后续阶段可以根据节点分享链接、地址端口、协议类型和备注生成指纹，尝试在订阅更新后重新绑定，但第一阶段不做自动猜测，避免错绑。

如果后续发现引用模式和核心配置生成冲突，再改为副本模式，但菜单语义仍然叫“复制到故障分组”，用户感知不变。

### `Config`

新增故障转移配置：

- `FailoverEnabled`: 全局故障转移开关。
- `ActiveFailoverGroupId`: 当前活动故障分组 ID。

也可以放入独立 `FailoverConfigItem`，但第一阶段放入 `Config` 更直接，便于 UI 和重载逻辑读取。

## UI 设计

### 订阅分组设置

在“订阅分组设置”窗口中，“别名”输入框右侧增加 `ToggleSwitch`：

- 左侧或旁边文本为“故障分组”。
- 默认关闭。
- 打开后保存的 `SubItem.IsFailoverGroup = true`。

### 主节点列表右键菜单

普通分组中，右键菜单在“移至订阅分组”下方增加：

- “复制到故障分组”
- 二级菜单只列出 `IsFailoverGroup = true` 的分组。

点击二级菜单后：

- 不修改原节点 `Subid`。
- 对每个选中节点，在目标故障分组的 `FailoverGroupItem` 中创建或更新队列项。
- 如果节点已在目标故障组队列中，不重复插入，只提示“已在故障队列中”或保持幂等成功。

### 故障分组节点列表

当当前分组是故障分组时：

- 右键第一项由“设为活动 Return”改为“添加到故障队列 Return”。
- 下方增加“从故障队列移除”。
- 禁止执行普通 `SetDefaultServer`。
- Enter 执行“添加到故障队列”。
- 队列节点按 `Sort` 排在前面，未加入队列的候选项排在后面。
- 拖拽排序在故障分组中只更新 `FailoverGroupItem.Sort`，不得调用普通 `ConfigHandler.MoveServer` 去修改 `ProfileExItem.Sort`。

说明：

- 普通分组中的拖拽排序继续沿用现有节点排序。
- 故障分组中的 P1、P2 顺序完全来自故障队列排序。
- 未加入故障队列的候选项排在队列项后面，并按普通节点排序展示。

### 顶部按钮

在“一键多线程测试延迟和速度(Ctrl+E)”按钮右侧增加：

- “当前组设置为活动故障组”
- 仅当前组为故障分组时可用。
- 点击后设置 `ActiveFailoverGroupId = 当前分组 Id`。
- 按钮文本变为“已设置为活动故障组”。

### 消息区开关

在“自动滚动到末尾”右侧增加：

- 文本“故障转移”
- `ToggleSwitch`
- 右侧标签显示活动故障组名称。

开关规则：

- 未设置活动故障组时，点击不启用，右侧提示“先激活故障分组”。
- 活动故障组不存在时，自动关闭并提示。
- 活动故障组队列为空时，自动关闭并提示。
- 开启后保存 `FailoverEnabled = true`，触发核心重载。

## 核心配置策略

第一阶段不直接改请求链路，而是将活动故障队列映射成虚拟 `PolicyGroup`：

- 组类型使用 `EConfigType.PolicyGroup`。
- 多路策略使用 `EMultipleLoad.Fallback`。
- 子节点顺序按 `FailoverGroupItem.Sort` 写入 `ProtocolExtraItem.ChildItems`。
- 子节点必须是真实存在的 `ProfileItem.IndexId`，以便现有 `GroupProfileManager.GetChildProfileItems()` 和节点校验能读取数据库中的真实节点。

当 `FailoverEnabled = true` 且活动故障组有效时，主核心配置应使用该 fallback 组作为当前代理入口。实现路径有两种：

1. 在开启故障转移时，将 `config.IndexId` 临时或持久切换到自动维护的 fallback 组节点。
2. 在 `CoreConfigContextBuilder` 中检测故障转移配置，构建核心上下文时替换当前节点为活动故障组对应的 fallback 组。

推荐第一阶段采用方案 2，因为它不改变用户原本选择的普通活动节点，关闭故障转移后能自然回到原状态。

方案 2 的落地约束：

- `FailoverGroupManager` 提供 `TryBuildVirtualPolicyGroup(Config config)` 或等价方法，返回虚拟 `ProfileItem` 和过滤后的真实子节点列表。
- 虚拟 `ProfileItem.IndexId` 使用稳定前缀，例如 `inner-failover-{ActiveFailoverGroupId}`；不得写入 `ProfileItem` 表。
- 虚拟节点的 `CoreType` 必须与原当前节点或子节点可用核心保持一致。若队列内节点核心类型混用导致生成风险，第一阶段应阻止开启并提示用户统一核心类型。
- 虚拟节点写入 `ProtocolExtraItem.ChildItems` 后，仍交给 `CoreConfigContextBuilder.ResolveNodeAsync()` 走现有组节点注册、子节点校验和代理保护逻辑。
- 如果现有 `GroupProfileManager` 无法处理虚拟组，优先只修改解析逻辑以支持“虚拟组 + 数据库真实子节点”，不要创建隐藏持久 `PolicyGroup` 节点，避免污染用户节点列表和订阅更新流程。

核心行为门禁结论会决定实现分支：

- 如果 Xray 与 sing-box 都能生成符合 P1 优先的 fallback 配置，第一阶段同时支持两者。
- 如果只有 sing-box 能可靠表达 fallback，第一阶段仅允许 sing-box 故障转移，并在 Xray 当前节点下阻止开启。
- 如果只有 Xray 能可靠表达 fallback，第一阶段仅允许 Xray 故障转移，并在 sing-box 当前节点下阻止开启。
- 如果两者都不能可靠表达 P1 优先，必须先补核心配置生成逻辑，再实现 UI 开关。

## 恢复策略总目标

第二、三阶段采用熔断思路：

- 默认连续失败 2 次判定故障。
- 初始冷却时间默认 60 秒。
- 连续失败后指数退避：`60s -> 120s -> 300s`，上限 300 秒。
- 冷却后只允许一次半开探测。
- 半开探测成功后标记正常。
- 恢复后不强行中断已有连接；新连接或下一次重载优先回到 P1。
- 第二阶段健康探测只负责可见性和恢复确认，不动态剔除或重排 fallback 子节点；第三阶段再决定是否根据健康状态驱动外层熔断控制。

第一阶段仅在文档和数据字段中预留这些状态，不强行显示不可靠的实时故障标签。

## 代理安全影响评估

本功能会影响代理链路选择，因此必须避免以下风险：

- 不得绕过现有系统代理、Tun、路由、DNS 和 TLS 校验逻辑。
- 不得在故障转移开启时静默直连。
- 活动故障组为空或无有效节点时，必须关闭故障转移或阻止开启。
- 核心配置生成失败时，不得保留半成品配置。
- 使用核心 fallback 时，仍应走现有 `CoreConfigContextBuilder`、节点校验和 `CoreConfigHandler`。
- 第二阶段批量健康探测必须通过节点对应临时核心发起真实请求，不得为了提速改成直连测速目标 URL。
- 批量健康探测生成测速配置时必须禁用故障转移包装，避免 P1 故障时因 fallback 到 P2 而把 P1 误判为正常。

第一阶段的安全边界是：只改变“选择哪个节点或组作为代理入口”，不改变路由规则、DNS、证书、订阅下载和核心更新策略。

## 实施任务

### 任务 0：核心 fallback 行为门禁测试

**文件：**

- 修改：`v2rayN/ServiceLib.Tests/CoreConfigV2rayServiceTests.cs`
- 新增或修改：`v2rayN/ServiceLib.Tests/CoreConfigSingboxServiceTests.cs`
- 可能修改：`v2rayN/ServiceLib/Services/CoreConfig/V2ray/V2rayBalancerService.cs`
- 可能修改：`v2rayN/ServiceLib/Services/CoreConfig/Singbox/SingboxOutboundService.cs`
- 记录：`_local_changes/NNN-failover-groups/tests.md`

- [ ] 写 Xray 测试：构造 `PolicyGroup + EMultipleLoad.Fallback + ChildItems=[P1,P2]`，生成配置后断言路由入口不是 `roundRobin` 轮询语义；如果当前核心只能生成 `roundRobin`，测试应先失败。
- [ ] 写 sing-box 测试：构造同样的 `PolicyGroup`，生成配置后断言最终入口、`selector`、`urltest`、子 outbound 顺序和 `interrupt_exist_connections=false` 符合预期。
- [ ] 如果测试暴露当前实现不能表达 P1 优先，先补核心生成逻辑或将不支持核心标记为不可开启。
- [ ] 运行 `dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj`。
- [ ] 把测试结论写入本功能 `_local_changes/NNN-failover-groups/tests.md`，作为第一阶段是否继续的门禁。

### 任务 1：新增数据模型和配置字段

**文件：**

- 修改：`v2rayN/ServiceLib/Models/SubItem.cs`
- 修改：`v2rayN/ServiceLib/Models/Config.cs`
- 新增：`v2rayN/ServiceLib/Models/FailoverGroupItem.cs`
- 修改：`v2rayN/ServiceLib/Manager/AppManager.cs`

- [ ] 增加 `SubItem.IsFailoverGroup`。
- [ ] 增加 `Config.FailoverEnabled` 和 `Config.ActiveFailoverGroupId`。
- [ ] 新建 `FailoverGroupItem`。
- [ ] 在应用初始化时创建 `FailoverGroupItem` 表。
- [ ] 验证旧数据库可自动补列。
- [ ] 在 `ConfigHandler.LoadConfig()` 中为新增配置字段提供安全默认值：`FailoverEnabled = false`，`ActiveFailoverGroupId = string.Empty`。

### 任务 2：订阅分组设置增加“故障分组”

**文件：**

- 修改：`v2rayN/v2rayN.Desktop/Views/SubEditWindow.axaml`
- 修改：`v2rayN/v2rayN.Desktop/Views/SubEditWindow.axaml.cs`
- 修改：`v2rayN/ServiceLib/ViewModels/SubEditViewModel.cs`
- 修改：`v2rayN/ServiceLib/Resx/ResUI.resx`
- 修改：`v2rayN/ServiceLib/Resx/ResUI.zh-Hans.resx`

- [ ] 在别名输入框右侧加入 `ToggleSwitch`。
- [ ] 绑定到 `SelectedSource.IsFailoverGroup`。
- [ ] 保存时保持现有订阅 URL 校验逻辑不变。

### 任务 3：实现故障队列服务

**文件：**

- 新增：`v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`

- [ ] 提供获取所有故障分组方法。
- [ ] 提供复制节点到故障分组方法。
- [ ] 提供添加/移除故障队列方法。
- [ ] 提供按 `Sort` 读取活动故障队列方法。
- [ ] 所有写入操作保持幂等，避免重复队列项。
- [ ] 读取活动故障队列时过滤已经不存在的 `SourceProfileId`。
- [ ] 提供更新故障队列排序方法，专供故障分组拖拽调用。
- [ ] 提供构建虚拟 `PolicyGroup` 的方法，不写入 `ProfileItem` 表。

### 任务 4：右键菜单增加“复制到故障分组”

**文件：**

- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml.cs`
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
- 修改：`v2rayN/ServiceLib/Resx/ResUI.resx`
- 修改：`v2rayN/ServiceLib/Resx/ResUI.zh-Hans.resx`

- [ ] 在“移至订阅分组”下方新增“复制到故障分组”。
- [ ] 二级菜单只显示故障分组。
- [ ] 点击后调用 `FailoverGroupManager.CopyToFailoverGroup`。
- [ ] 普通节点 `Subid` 不变。
- [ ] 当前分组本身是故障分组时，隐藏或禁用“复制到故障分组”，避免把故障组候选重复加入其它故障组造成语义混乱。

### 任务 5：故障分组内右键行为替换

**文件：**

- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml.cs`
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`

- [ ] 当前组是故障分组时隐藏或禁用“设为活动”。
- [ ] 显示“添加到故障队列 Return”。
- [ ] 显示“从故障队列移除”。
- [ ] Enter 在故障分组中执行添加到故障队列。
- [ ] 防止故障分组节点被设置为普通活动节点。
- [ ] 故障分组内拖拽排序调用 `FailoverGroupManager.UpdateSort`，不调用普通 `MoveServer`。

### 任务 6：活动故障组按钮和故障转移开关

**文件：**

- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
- 修改：`v2rayN/v2rayN.Desktop/Views/MsgView.axaml`
- 修改：`v2rayN/v2rayN.Desktop/Views/MsgView.axaml.cs`
- 修改：`v2rayN/ServiceLib/ViewModels/MsgViewModel.cs`

- [ ] 在顶部测试按钮右侧增加活动故障组按钮。
- [ ] 在消息区“自动滚动到末尾”右侧增加故障转移开关和活动组名称。
- [ ] 未激活故障组时阻止开启。
- [ ] 活动组队列为空时阻止开启。
- [ ] 开启前校验活动故障组至少存在一个有效真实子节点。
- [ ] 开启前校验当前核心类型在任务 0 门禁中被标记为支持。
- [ ] 开关变化后保存配置并触发 `AppEvents.ReloadRequested`。

### 任务 7：核心配置构建接入 fallback 组

**文件：**

- 修改：`v2rayN/ServiceLib/Handler/Builder/CoreConfigContextBuilder.cs`
- 修改：`v2rayN/ServiceLib/Manager/FailoverGroupManager.cs`

- [ ] 在构建主节点上下文前检测 `FailoverEnabled`。
- [ ] 如果活动故障队列有效，生成虚拟 `PolicyGroup` 节点。
- [ ] 设置 `ProtocolExtraItem.MultipleLoad = EMultipleLoad.Fallback`。
- [ ] 按故障队列顺序写入 `ChildItems`。
- [ ] 虚拟组的 `ChildItems` 只包含数据库中仍存在且节点校验通过的真实节点。
- [ ] 复用现有节点校验和核心配置生成路径。
- [ ] 无有效节点时返回校验错误，不静默直连。
- [ ] 保持 `config.IndexId` 不变；关闭故障转移后继续使用用户原来的普通活动节点。

### 任务 8：第一阶段 UI 状态标签

**文件：**

- 修改：`v2rayN/ServiceLib/Models/ProfileItemModel.cs`
- 修改：`v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs`
- 修改：`v2rayN/v2rayN.Desktop/Views/ProfilesView.axaml`

- [ ] 增加只读展示字段：是否故障组节点、是否在队列、队列优先级。
- [ ] 故障分组中对队列项显示绿色 `P*` 标签。
- [ ] 第一阶段不显示“故障”标签，避免表达不可靠状态。
- [ ] 活动故障组开启时，只把 P1 行设置为“当前优先入口”提示；不要声称它一定是当前真实工作节点。

### 任务 9：测试和验证

**文件：**

- 修改或新增：`v2rayN/ServiceLib.Tests/*`

- [ ] 测试 `SubItem.IsFailoverGroup` 保存读取。
- [ ] 测试复制到故障分组不会改变原节点 `Subid`。
- [ ] 测试重复复制保持幂等。
- [ ] 测试活动故障组为空时阻止开启。
- [ ] 测试 fallback 组生成顺序符合 `Sort`。
- [ ] 测试故障分组拖拽只更新 `FailoverGroupItem.Sort`，不修改普通节点 `ProfileExItem.Sort`。
- [ ] 测试失效 `SourceProfileId` 不进入虚拟 fallback 组。
- [ ] 运行 `dotnet test v2rayN/v2rayN.slnx` 或项目可用测试命令。

### 任务 10：本地改动记录

**文件：**

- 新增：`_local_changes/NNN-failover-groups/README.md`
- 新增：`_local_changes/NNN-failover-groups/files.md`
- 新增：`_local_changes/NNN-failover-groups/reapply.md`
- 新增：`_local_changes/NNN-failover-groups/tests.md`
- 新增：`_local_changes/NNN-failover-groups/patch.diff`
- 修改：`_local_changes/README.md`

- [ ] 记录功能目的、影响范围和代理安全影响评估。
- [ ] 记录所有新增和修改文件。
- [ ] 记录未来官方新版源码迁移步骤。
- [ ] 记录验证命令和结果。
- [ ] 生成不包含 `_local_changes/` 自身的功能补丁。

## 第一阶段验收标准

- 任务 0 已证明当前支持核心能表达 P1 优先的故障转移语义；未通过的核心不能开启故障转移。
- 用户可创建故障分组。
- 普通节点可通过“复制到故障分组”加入故障组，不改变原分组。
- 用户可设置活动故障组。
- 故障转移开关有有效性校验。
- 开启故障转移后，核心配置使用按队列顺序构建的虚拟 fallback 组。
- 关闭故障转移后，恢复原普通活动节点行为。
- 故障分组内排序只影响 P1、P2 队列顺序，不影响普通分组排序。
- 队列中失效节点不会进入核心配置，也不会导致静默直连。
- 文档清楚标明第二、三阶段要补的健康状态和熔断策略。

## 设计取舍

第一阶段不直接做完整请求级熔断，是因为 v2rayN 当前架构更接近“核心配置驱动的本地代理客户端”，不是 `cc-switch-main` 那类请求级反向代理。先复用经过门禁验证的核心 fallback 可以更快形成可用闭环，也更不容易破坏 Tun、系统代理、路由和 DNS 现有安全边界。

“故障”标签和自动恢复 UI 放到第二、三阶段，是因为第一阶段不能可靠读取核心内部每个子节点的真实故障状态。宁可少显示，也不显示不可信状态。
