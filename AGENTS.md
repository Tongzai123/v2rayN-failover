# AGENTS.md

## 回复与沟通

- 默认使用简体中文回复。
- 文件名、命令、代码符号、技术名词可以保留英文，例如 `v2rayN.slnx`、`git diff`、`patch.diff`、`.NET`。
- 结论必须基于实际文件、命令输出或可验证信息；不确定时先查证。

## 项目定位

- 本仓库是 v2rayN 源代码的本地改造副本。
- 本地改动的首要目标是：未来拿到官方新版源码后，可以快速理解、复用和重新应用这些改动。
- 除非用户明确要求，不要做与当前功能无关的大规模重构。

## 本地改动记录强制规则

每次对源码、配置、脚本、文档或项目结构做任何实质改动时，必须同步更新 `_local_changes/` 目录。

### 一项功能一个目录

- 每个独立功能、修复或行为调整，都必须在 `_local_changes/` 下创建一个独立目录。
- 目录命名格式：

```text
_local_changes/NNN-short-name/
```

- `NNN` 使用三位递增编号，例如 `001-custom-routing-ui`。
- `short-name` 使用小写英文、数字和连字符，表达改动主题。
- 不同功能不要混在同一个目录里；确实强相关的一组小改动可以放在同一目录。

### 每个改动目录必须包含

```text
README.md
files.md
reapply.md
tests.md
patch.diff
```

- `README.md`：说明改动目的、用户可见行为、设计取舍和影响范围。
- `files.md`：列出新增、修改、删除的文件，并说明每个文件为什么变更。
- `reapply.md`：说明未来拿到新版源码后，如何重新应用该改动。
- `tests.md`：记录已执行的验证命令、结果和未验证风险。
- `patch.diff`：保存本改动相对改动前状态的补丁，便于未来尝试 `git apply`。

如果当前环境不是 Git 仓库，无法生成可靠 `patch.diff`，必须在 `patch.diff` 中写明原因，并在 `reapply.md` 中提供足够详细的人工迁移步骤。

## 改动流程

1. 开始改动前，先确认 `_local_changes/_template/` 是否存在。
2. 为本次改动复制或参照 `_local_changes/_template/` 创建新目录。
3. 实施代码或文档修改。
4. 更新本次改动目录中的 `README.md`、`files.md`、`reapply.md` 和 `tests.md`。
5. 如果当前目录是 Git 仓库，生成或更新 `patch.diff`。
6. 运行可用验证；无法验证时，在 `tests.md` 写清原因和残余风险。

## 生成补丁的推荐方式

如果当前项目已初始化 Git 仓库，推荐在完成某个独立改动后执行：

```powershell
git diff -- . ":(exclude)_local_changes" > _local_changes/NNN-short-name/patch.diff
```

如果改动已经分成独立提交，也可以使用：

```powershell
git show --format=fuller --stat --patch <commit-sha> > _local_changes/NNN-short-name/patch.diff
```

注意：

- `patch.diff` 应尽量只包含该功能相关改动。
- 不要把 `_local_changes/` 自身的模板或说明变更混进功能补丁。
- 不要在补丁、说明或日志中写入密钥、Token、密码、订阅链接等敏感信息。

## 未来升级源码时的复用流程

拿到官方新版源码后，按以下顺序处理：

1. 阅读 `_local_changes/README.md`，了解本地改动总览。
2. 按编号从小到大处理每个 `_local_changes/NNN-short-name/`。
3. 先读该目录的 `README.md` 和 `reapply.md`。
4. 如果条件允许，优先尝试应用 `patch.diff`：

```powershell
git apply --check _local_changes/NNN-short-name/patch.diff
git apply _local_changes/NNN-short-name/patch.diff
```

5. 如果补丁冲突，按 `reapply.md` 和 `files.md` 人工迁移。
6. 每迁移完一个改动，执行 `tests.md` 中记录的验证方式。
7. 迁移完成后，更新该改动目录中的说明和新版补丁。

## 代理安全性规则

- 所有涉及代理模式、Tun、系统代理、路由规则、DNS、订阅、核心组件、Geo/SRS 数据、更新下载、证书、网络请求和本地监听端口的改动，必须先评估是否可能导致流量绕过代理、错误直连、DNS 泄漏、订阅泄漏、证书信任扩大、核心组件降级或安全更新缺失。
- 禁止为了实现功能而绕过现有代理、路由、TLS、订阅校验或核心更新安全边界。
- 改动记录 `_local_changes/NNN-*/README.md` 或 `tests.md` 中必须包含“代理安全影响评估”小节，说明本次改动是否影响代理链路，以及已验证和未验证的风险。

## 工程纪律

- 修改前先阅读相关源码和已有记录，不要凭猜测改动。
- 优先沿用项目现有 `.NET`、WPF、Avalonia、ServiceLib 和测试项目的组织方式。
- 新增逻辑应尽量靠近所属功能模块，避免创建无归属的公共工具。
- 不要为了让编译通过而注释异常、绕过校验或隐藏错误。
- 涉及删除文件、修改 Git 历史、安装全局依赖、修改 CI/CD、发布部署等高风险操作，必须先得到用户确认。
- 改动完成后必须说明验证结果；无法运行验证时，明确说明原因。

## Git Worktree 规则

- 本项目的 worktree 根目录固定为 `C:\Users\11834\Desktop\most_V2ray\worktrees\v2rayN-7.20.4`。
- 新建 worktree 时，必须直接创建在上述目录下，例如 `C:\Users\11834\Desktop\most_V2ray\worktrees\v2rayN-7.20.4\fix-protocol-node-types`。
- 禁止在上述目录下再创建 `codex\`、`feature\`、`fix\` 等中间目录；如果分支名包含 `/`，worktree 目录名必须改成不含 `/` 的扁平名称，例如把 `codex/fix-protocol-node-types` 对应到 `fix-protocol-node-types`。
- 使用 `using-git-worktrees` skill 创建 worktree 时，必须将目标路径指向上述根目录的直接子目录，不要使用默认的 `.config/superpowers/worktrees/` 路径。

