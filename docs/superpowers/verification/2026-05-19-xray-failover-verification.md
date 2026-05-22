# Xray 故障转移验证记录

## 验证目标

- 确认用户测试时实际运行的 `config.json` 是否进入 Xray `balancer`。
- 确认 `leastLoad + fallbackTag` 是否具备 P1 连接失败后请求级重试 P2 的行为。

## 实际运行配置定位

| 项目 | 结果 |
| --- | --- |
| Xray 进程是否运行 | 已运行。主进程 PID 为 `26140`，另有 `configTest*.json` 测试进程。 |
| Xray 命令行配置路径 | 主进程命令行为 `run -c config.json`。 |
| 发布目录配置路径 | `C:\Users\11834\Desktop\most_V2ray\v2rayN-7.20.4\publish\v2rayN-windows-64-desktop\binConfigs\config.json` |
| 实际检查的配置路径 | `C:\Users\11834\Desktop\most_V2ray\v2rayN-7.20.4\publish\v2rayN-windows-64-desktop\binConfigs\config.json` |

## 脱敏配置结构检查

| 检查项 | 结果 | 证据 |
| --- | --- | --- |
| 存在 `routing.balancers` | 是 | 存在 `tag = proxy-round`，`selector = ["proxy"]`，`strategy.type = leastLoad`。 |
| Google 规则使用 `balancerTag` | 是 | `geosite:google` 规则的 `outboundTag = null`，`balancerTag = proxy-round`。 |
| P1/P2 outbounds 存在 | 是 | outbounds 中存在 `proxy-1-*` 和 `proxy-2-*`，协议均为代理协议，不是 `direct`。 |
| `selector` 可选中 P1/P2 | 是 | `selector = ["proxy"]`，P1/P2 tag 均以 `proxy-` 开头。 |
| `burstObservatory.subjectSelector` 覆盖候选 tag | 是 | `subjectSelector = ["proxy"]`，`interval = 5m`，`timeout = 30s`，`sampling = 2`。 |

实际运行配置已经进入 Xray `balancer`。本轮继续执行最小 Xray 行为验证。

## 最小 Xray 行为验证

| 场景 | 结果 | 证据 |
| --- | --- | --- |
| P1 从启动起不可用，P2 可用 | 成功落到 P2 | 本地最小配置中，`burstObservatory` 启动后发现 P1 不可用，请求日志显示 `socks-in -> proxy-2-p2`，`curl` 返回 `p2-ok`。 |
| P1/P2 初始都可用，随后停止 P1 | 请求失败 | 停止 P1 helper 后、下一轮 observatory 间隔前立即请求，`curl` 返回 `Recv failure: Connection was reset`，退出码 `56`。 |
| P1 失败后是否同一请求重试 P2 | 未观察到 | 日志显示请求 `socks-in -> proxy-1-p1`，随后 `proxy/socks` 连接失败；该请求未出现切到 `proxy-2-p2` 的证据。 |

## 结论

本轮已确认用户实际运行的 `config.json` 进入了 Xray `balancer`，Google 规则和 final proxy 规则都使用 `balancerTag = proxy-round`，P1/P2 也进入 outbounds、`selector` 和 `costs`。

最小 Xray 行为验证显示：

- 当 observatory 已经发现 P1 不可用时，`leastLoad` 可以在路由阶段选择 P2，请求成功。
- 当 P1 起初健康、随后在 observatory 下一轮探测前失效时，请求仍会选中 P1 并失败；未观察到同一请求在 P1 连接失败后自动重试 P2。

因此，当前 `leastLoad + fallbackTag + burstObservatory` 结构不能证明满足“P1 请求失败后同一请求自动切到 P2”的目标。它更像是基于 observatory 状态做路由阶段选择；在 observatory 状态尚未更新时，P1 故障会导致请求失败。这与用户关闭 P1 服务器后网页无法访问 Google 的现象一致。

## 代理安全影响评估

本验证只记录进程状态、配置路径状态和脱敏结构检查结论；未写入真实节点密钥、订阅链接、Token 或密码；未修改系统代理、Tun、DNS、订阅、证书、核心更新逻辑或代理候选范围。
