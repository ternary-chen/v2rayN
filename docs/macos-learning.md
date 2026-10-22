# macOS Apple Silicon 测试版

适用于 M1/M2/M3/M4 等 Apple Silicon，目标架构为 `osx-arm64`。本包选择 macOS 15 或更新系统作为最低支持版本，基于 .NET 10 当前支持范围；不需要另装 .NET。Intel Mac 不适用此包。

## 安装与配置

1. 从此 fork 的 [learning.3 Release](https://github.com/ternary-chen/v2rayN/releases/tag/v7.25.6-learning.5) 下载 ARM64 测试包，核对随附的 SHA-256；解压后将 `v2rayN Learning.app` 复制到“应用程序”。不要覆盖现用客户端的配置。测试包使用本地 ad-hoc 签名，没有 Apple Developer ID 签名或公证；若系统拦截，确认来源和摘要后按系统“隐私与安全性”中的允许打开流程处理。不要全局关闭 Gatekeeper。
2. 暂时保留原代理。导入自己的订阅并选择专用节点，保持 TUN 关闭；先测试节点再切换系统代理。应用配置保存在用户数据目录，不写入 `.app` 内。
3. 使用随包 `.agents/skills/setup-v2rayn-learning/assets/default-routing.json` 新建独立路由方案，域名策略 `AsIs`，保留末尾 direct。进入“设置 → 自动分流与 Codex 代理”，启用自动检测并保存重载。默认临时 24 小时，累计 5 次独立确认转永久，缓存命中不加次数。需要 AI 专线时，在同一窗口的“策略组”页启用双策略并分别选择 AI 与默认节点；学习归默认策略，详见 [AI 与默认策略](ai-policies.md)。
4. “强制直连程序”使用实际进程名或绝对目录，例如某个 `.app/Contents/` 目录（结尾保留 `/`）。默认名单来自 Windows，不保证名称覆盖 Mac 版本；核对实际程序名、辅助进程和内核权限。不要把仅仅保存了规则当作直连已验证。
5. 在主窗口启用自动配置系统代理；需要时按 macOS 的提示授权。Codex 页可读取 `scutil --proxy` 的 HTTP/HTTPS/SOCKS/PAC 状态，状态刷新不会改动网络。自动定位失败时，选择 Codex/ChatGPT 的 `.app`，或填写桌面应用实际使用的 `codex` 可执行文件路径。
6. “启用系统代理支持”会备份并修改用户自己的 Codex `config.toml`，保留 Unix 文件权限；不写 `.env` 或认证信息。完全退出并重开 Codex，再运行 WebSocket 诊断并进行正常对话。旧版本不支持诊断时会明确提示，不能把 Windows 的验收结果套用到 Mac。
7. 验证新客户端独立可用、原客户端与 TUN 均退出后，再按用户要求调整登录启动项。macOS 的启动设置由系统“登录项”及客户端现有选项管理，不使用 Windows 注册表或计划任务。Codex 默认手动启动。

## 测试边界与恢复

自动学习只处理未匹配 TCP 80/443，HTTPS 验证独立 TLS 握手而非页面内容；明确 direct/proxy/block 规则优先。仅进入代理且可被内核识别的连接能匹配进程规则。

构建流程在 GitHub macOS runner 上运行针对性检查、发布 ARM64 程序、打包 `.app` 并验证 ad-hoc 签名。实际 GUI、M1 上的节点、系统代理、受保护程序和 Codex 长连接仍需目标机器验收；CI 通过不能替代这些测试。Windows 已安装的版本不受此测试包影响。

切换前记录原系统代理、TUN、订阅和登录项，准备在断网后仍可执行的本机回退。失败时关闭新客户端的系统代理，再恢复原客户端及其原设置；不要依赖断网后向 Codex 发送下一条命令恢复。Codex 配置可在设置页恢复；若用户后来修改过文件，自动恢复会拒绝覆盖并保留备份。

构建与签名依据：[Avalonia macOS 部署文档](https://docs.avaloniaui.net/docs/deployment/macos)；系统支持依据：[.NET 10 支持列表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。

## 定制版更新

learning.5 起使用本 fork 的定制更新入口：自动检查和下载，用户在“检查更新”页确认后才安装。首次安装应使用含 CustomUpdater 的完整包；不要用官方客户端更新覆盖此版本。详见 [更新说明](custom-updates.md)。
