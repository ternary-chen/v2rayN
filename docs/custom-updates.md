# 定制版自动更新

客户端只从 `ternary-chen/v2rayN` 的 GitHub Releases 获取定制版更新。Windows x64 便携版和 macOS Apple Silicon 应用包使用同一套检查、下载和确认安装流程。上游官方客户端不参与此更新通道，Xray 等内核和 Geo 数据仍从原来源更新。

## 使用

在“检查更新”页面顶部查看“定制版客户端更新”。默认开启自动检查和下载：启动约一分钟后检查，此后每六小时检查；失败一小时后重试。后台下载不会停止代理。也可以点击“检查并下载”立即重试。

下载后校验大小、SHA-256 和包结构，显示版本说明和“安装并重启”按钮。**只有点击这个按钮才会关闭客户端并安装**，后台任务和下载完成事件都不会自动安装。已下载的包在重启客户端后重新校验，可在离线时确认安装。网络下载失败不启用不完整文件。

首次需要手动安装包含 `CustomUpdater` 的完整启动包。之后发布新版本时，客户端按独立递增 Build 号判断更新，避免 `7.25.6` 与 `learning.N` 的版本比较冲突。界面中的定制版本号来自编译时嵌入的 `custom-build.json`。

## 安装与回退

独立更新程序被复制到用户更新缓存中运行，校验安装包、构建版本和提交，完成解包及目标目录写入检查后，才通知当前客户端退出。Windows 保持安装目录不变，备份并替换程序文件，不覆盖 `bin` 内独立更新的内核和数据文件。Mac 先验证签名，再以同一目录下的候选 `.app` 替换完整应用包；用户配置仍保存在包外。

退出后备份配置目录（包括 SQLite 主库及可能残留的 WAL 文件）。新程序需要报告预期版本和本次安装标识，并恢复此前工作的 SOCKS/学习端口。超时或退出会触发程序与配置回退，再启动旧版。代理端口恢复不代表所有外部网站都可达；不会因为某个远端网站临时故障而误判应用升级失败。

回退记录和备份保存在当前用户 LocalApplicationData 下的 `v2rayN-learning-updates/install-*`，Mac 的旧应用包保留在原 `.app` 相邻的隐藏备份目录。下次启动会提示失败记录。目录权限不足会在退出旧程序前报错，不自动提权或关闭系统安全策略。Mac 暂只支持可写的普通 `.app` 安装，不支持 App Translocation 临时路径、软件商店或其他包管理器托管安装。

更新缓存和备份可能包含个人配置，请不要提交到仓库或公开分享。程序不把订阅、凭据和学习历史上传到 GitHub。SHA-256 用于传输与安装完整性，更新来源信任你的 GitHub 仓库和 HTTPS；这不是独立开发者签名机制。Mac 包仍使用 ad-hoc 签名而非 Apple 公证。

## 发布新版本

1. 增加 `v2rayN/AdaptiveRouting/custom-build.json` 的 Build，更新 Version 为对应的 `v7.x.y-learning.N`。Build 必须一直递增，即使上游版本变化也不能重置。
2. 同一提交分别构建客户端与 `CustomUpdater`。Windows helper 使用 `win-x64`，Mac helper 使用 `osx-arm64`，均为 self-contained 单文件；Windows 打包用 `scripts/package-learning-windows.py`，Mac 打包用 `scripts/package-learning-macos.py`。Mac CI 已包含 helper 构建、签名及针对性检查。
3. 两端安装包验证后生成清单：

```text
python scripts/create-custom-update-manifest.py --windows WINDOWS.zip --macos MACOS.zip --notes release-notes.md --commit FULL_COMMIT --output custom-update.json
```

4. 创建与 Version 一致的 Release 草稿，上传两个包、校验文件与 `custom-update.json`。验证上传后的摘要，再公开发布。客户端只读取公开发布且带清单的 `learning.N` 版本；本定制通道包含标记为 prerelease 的定制发布，忽略旧的无清单版本、草稿和官方版本。

没有清单或没有当前架构包的发布不会被当作可安装的完整更新。无需另建更新服务器，也不要把 GitHub 管理令牌打进客户端。

## 针对性验证

`CustomUpdate.SmokeTests` 检查来源约束、版本选择、缓存/下载校验、路径穿越、用户数据隔离和 Mac 链接约束。使用 `CustomUpdate.Fixture` 的生命周期测试仅操作临时目录中的无网络假客户端，验证确认交接前不替换、健康检查、失败后恢复程序与配置。Mac CI 用本机架构假应用测试签名及 `.app` 替换，正式包交叉构建 ARM64；实际 M1 用户环境仍需验收。
