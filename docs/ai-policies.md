# AI 与默认策略

Windows WPF 和 macOS Avalonia 的“设置 → 自动分流与 Codex 代理 → 策略组”提供两个手动节点选择。原生 v2rayN 的自动测速/故障转移策略组仍保留；本功能使用两个固定选择的标准 Xray 节点，暂不接受负载均衡组、链式订阅、自定义核心或 TUN。

- **AI 策略**：独立保存专线节点 ID，不跟随主列表的节点切换。
- **默认策略**：复用客户端当前选中节点。可从带订阅名称的列表里选择 onani 的其他节点；在主列表切换节点也只改变默认策略。
- **自动学习**：探测代理与实际转发都使用默认出口 `proxy`。按默认节点保存历史；更换 AI 节点不影响默认节点的计数，更换默认节点仍遵循原本的每节点独立历史。
- **优先级**：受保护程序直连 → AI 厂商规则 → 原有明确网站规则 → 未匹配 TCP 80/443 学习 → 其余 direct。内部学习探测入站强制使用默认出口，避免再次落入 AI 规则或学习循环。
- 首次升级不自动启用双策略，也不替用户选择免费节点。AI 节点被删除或配置无效时预检报错，不偷偷改走默认节点；现有核心在配置预检失败时保持运行。网络连通性仍需实际测试。

## 可追踪的社区来源

主来源选择 MIT 许可的 [v2fly/domain-list-community](https://github.com/v2fly/domain-list-community)，使用厂商分类名称，不复制 GLaDOS 的整份混合规则组。该项目把 `data/` 中的文件编译成 geosite 分类；v2rayN 当前通过 [Loyalsoldier/v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat) 获取构建后的 geosite.dat。

| 范围 | 社区文件/分类 |
| --- | --- |
| OpenAI / ChatGPT / Codex / Sora | [openai](https://github.com/v2fly/domain-list-community/blob/master/data/openai) |
| Anthropic / Claude / Claude Code | [anthropic](https://github.com/v2fly/domain-list-community/blob/master/data/anthropic) |
| Gemini、AI Studio、Code Assist、NotebookLM、Jules 等 Google AI 产品 | [google-deepmind](https://github.com/v2fly/domain-list-community/blob/master/data/google-deepmind) |
| GitHub Copilot | [github-copilot](https://github.com/v2fly/domain-list-community/blob/master/data/github-copilot) |
| Cursor | [cursor](https://github.com/v2fly/domain-list-community/blob/master/data/cursor) |
| Windsurf / Codeium | [windsurf](https://github.com/v2fly/domain-list-community/blob/master/data/windsurf) |
| Perplexity | [perplexity](https://github.com/v2fly/domain-list-community/blob/master/data/perplexity) |
| xAI / Grok | [xai](https://github.com/v2fly/domain-list-community/blob/master/data/xai) |
| Poe | [poe](https://github.com/v2fly/domain-list-community/blob/master/data/poe) |
| DeepSeek | [deepseek](https://github.com/v2fly/domain-list-community/blob/master/data/deepseek) |

2026-10-21 核对的上游提交为 `e8f6d1e5c4a01cda943c0784ffa834340eeab5ae`。这里只说明选取时的依据；实际匹配内容由本机 geosite.dat 决定。社区规则是社区维护的产品归类，并不表示每条域名都经过厂商认证。

核对发现 `google-gemini` 已成为 `google-deepmind` 的兼容别名；`category-ai-chat-!cn` 已成为 `category-ai-!cn` 的兼容别名。因此使用明确的厂商列表，避免一个历史分类名称后续变成覆盖大量工具的总集合。`google-deepmind` 包含其他 Google AI 产品，但不等于整个 Google 域名组。

另外保留 CSL `ai_route_catalog_2026_08_18_v3` 梳理的九家厂商、96 条域名事实作为补充，包括 Microsoft Copilot、Mistral、精确登录/CDN/实时通信辅助目标。只复用域名事实，未复制 CSL 的 Rust 实现，也未改动 CSL 工作区。本地补充以 `full:` / `domain:` 区分匹配，不使用域名关键词或整个 Google/Microsoft/云平台根域。精确的第三方辅助域仍可能被其他产品共用，这是域名分流的边界。

GLaDOS 的 `Research + AI` 共 324 条规则（含重复项），同时覆盖通用 Google、学术网站等；它用于交叉发现遗漏，不直接整组导入。普通 Google、arXiv、Nature 等不因这次新增 AI 策略而改走专线。CSL 补充是合并项，并非对上游分类的自动删减过滤器。

## 更新方式

通过客户端“检查更新”中的 Geo 数据更新入口更新 geosite.dat，再重载配置。现有厂商分类中的域名更新随数据文件生效，不需要重新选择节点或重新打包客户端。此功能没有增加后台轮询，也不自动新增厂商类别。

跟踪某厂商更新时，查看上表对应文件的提交历史；核对本机数据的来源及更新时间。维护者新增厂商或调整补充时，修改 `v2rayN/AdaptiveRouting/ai-catalog.json` 并提升目录版本。该文件记录来源仓库、核对提交、分类名称和补充列表，便于 diff 与回滚。

[MetaCubeX/meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat) 提供 Mihomo/Sing-box 等分发格式，可作为交叉检查；[blackmatrix7/ios_rule_script](https://github.com/blackmatrix7/ios_rule_script/tree/master/rule/Clash) 有 OpenAI、Claude、Gemini、Copilot 等独立目录，可辅助发现遗漏。为避免多个来源的范围冲突，本版不把三套列表直接全量叠加，也不引用整个 Google、Microsoft 或 Research 组。

## 验证

定向检查覆盖 AI 优先级、受保护进程、默认学习出口、出口缺失/重复失败、目录范围以及真实 geosite 分类的 Xray 配置校验。编译验证同时覆盖 Windows WPF 与 Avalonia。实机切换前仍需备份、保留原代理和本机回退；构建通过不代表具体专线、免费节点或 Mac 系统代理已经验收。

本批轻量验证命令：

```powershell
dotnet run --project v2rayN/AdaptiveRouting.SmokeTests/AdaptiveRouting.SmokeTests.csproj
dotnet run --project v2rayN/PolicyIntegration.SmokeTests/PolicyIntegration.SmokeTests.csproj
dotnet build v2rayN/v2rayN/v2rayN.csproj -c Debug
dotnet build v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj -c Debug
```

真实 geosite 校验通过测试进程的 `TEST_XRAY_EXE` 与 `TEST_XRAY_GEOSITE` 指向内核和数据目录启用；只运行内核配置检查，不启动代理。集成检查在自己的构建目录创建合成节点与数据库，不调用核心启动、系统代理或应用退出流程。
