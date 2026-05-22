# 本地故障转移 relay 手工验证记录

## 自动化前置

- `dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~FailoverRelay"`
- `dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~CoreConfigV2ray"`
- `dotnet test v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj --filter "FullyQualifiedName~Failover"`

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
- 日志只记录候选 outbound tag 和异常类型，不记录目标 URL、节点密钥、订阅链接、Token、UUID、`privateKey`、`shortId`、`serverName`。
