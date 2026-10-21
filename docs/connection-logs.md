# 连接日志

Windows WPF 与 macOS Avalonia 共用连接日志增强。适用于本定制版的标准 Xray 非 TUN 配置；原始内核日志保留，访问日志后追加进程、策略、节点和状态。测速核心、自定义内核与其他核心保持其原始日志。

示例（名称与 PID 仅为示意）：

```text
... accepted //api.openai.com:443 [socks -> ai-policy] [进程:codex.exe (PID 1234) | 策略:AI 策略 | 节点:美国专线 | 状态:路由选择]
... accepted //mtalk.google.com:5228 [socks -> proxy] [进程:browser.exe (PID 5678) | 策略:默认策略 | 节点:日常香港 | 状态:路由选择]
... [socks -> direct] [进程:NTEWebBooster.exe (PID 9012) | 策略:直连 | 节点:无（直连） | 状态:路由选择]
```

“路由选择”表示核心为连接选中的实际出站，不等于远端握手或网页请求成功。节点名称取自本次核心启动的配置快照，不读取界面后来切换的高亮行。只显示节点备注，不添加服务器地址、订阅链接、密码或密钥。无法将自定义出口、负载均衡或链式出口映射为单个节点时标为未知。

## 进程识别

Windows 使用系统 TCP 所属 PID 表，覆盖 IPv4/IPv6；Mac 使用系统自带 `/usr/sbin/lsof` 的机器可读输出。只匹配本机客户端的源地址、源端口和对应代理入口端口，不按目标域名、相邻日志、单一端口或上次缓存猜测进程。不会请求 sudo 或改变权限。

连接已结束、系统限制、UDP、局域网其他设备的请求、查询超时或匹配有歧义时，明确显示未知及原因。浏览器连接只能定位到浏览器/辅助进程，不能据此确定具体标签页。没有经过本代理的连接不会出现在这里。

查询在有界后台队列中批量进行，避免阻塞核心输出和转发。Mac 请求高峰时合并调用 lsof。查询失败或队列繁忙仍显示原始连接与策略/节点信息，进程标为未知。

## 自动学习

进入 `limited-learning` 时显示原程序（若可识别）、“自动检测”和“待检测，尚未选择出口”。学习模块另以 `学习连接#编号` 记录开始检测、出口已选择/正在建连、隧道已建立或失败，并明确显示最终直连或默认策略的节点。

Xray 向学习模块发起的是一条新连接，现有 SOCKS 转发不携带原应用身份。因此最终学习日志明确显示“未知（内部转发未携带原进程）”；不能将 Xray/v2rayN 自己当成原程序，也不能把同域名的并发连接拼成一条。编号仅关联同一次学习连接的阶段，不跨核心重启作为全局编号。

`limited-learning-proxy` 标为“专用代理入口，固定使用默认策略”。学习模块和显式配置该端口的其他工具（例如 gh）都可以使用它，不能仅凭入口名称断言这是学习内部流量；进程仍按实际连接表识别。普通学习诊断中的“默认候选节点”不是已经走了代理的证明；以连接状态行区分。

## 本地记录与验证

增强后的连接日志和学习连接状态显示在主窗口，同时写到应用数据目录的 `guiLogs/connections.txt`。约 10 MiB 轮转，最多保留 3 个归档；磁盘写入使用有界异步队列，极端繁忙时可能丢弃磁盘记录，不阻塞联网。其他应用日志仍使用原日志文件。记录包含访问目标、进程名和用户设置的节点备注，分享前按需脱敏。

轻量检查：

```text
dotnet run --project v2rayN/ConnectionLogging.SmokeTests/ConnectionLogging.SmokeTests.csproj
dotnet run --project v2rayN/AdaptiveRouting.SmokeTests/AdaptiveRouting.SmokeTests.csproj
```

专用检查覆盖真实本机 IPv4/IPv6 进程归属、Mac lsof 格式、双策略节点标注、直连/阻止/未知出口、快照、并发歧义、自动学习状态、超时与队列压力。CI 已接入 Mac 原生进程归属检查；是否实际运行通过应以对应构建记录为准。

实现依据：[Windows TCP PID 表](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable)、[lsof 字段输出](https://github.com/lsof-org/lsof/blob/master/docs/manpage.md)、[Xray 访问日志格式](https://github.com/XTLS/Xray-core/blob/main/common/log/access.go)。
