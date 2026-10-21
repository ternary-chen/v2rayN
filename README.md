# v2rayN

## 定制版配置与 Codex skill

本分支新增了 [AI 与默认策略](docs/ai-policies.md)：AI 专线单独选择，默认节点由用户切换，学习记录归默认策略；厂商域名使用可更新的社区 geosite 分类。Windows 与 macOS ARM64 的 `learning.3` 发布包均包含此功能。

本分支提供 Windows WPF 与 macOS Avalonia 的自动分流、累计学习记录和 Codex 系统代理配置。定制功能说明见 [自动分流与 Codex 系统代理](docs/limited-learning.md)。下方保留的是上游项目介绍及下载入口，官方安装包不包含这些定制功能。

**Windows x64 定制版下载：[v7.25.6-learning.3](https://github.com/ternary-chen/v2rayN/releases/tag/v7.25.6-learning.3)。** 客户端为便携压缩包，解压到独立目录后运行 `v2rayN.exe`；Release 同时提供配置 skill 和 SHA-256 校验文件。无需自行编译。

**macOS Apple Silicon：已移植设置界面，提供 M1/M2/M3/M4 的 ARM64 测试版。** [Mac 使用与验收说明](docs/macos-learning.md)；[Mac 测试版 Release](https://github.com/ternary-chen/v2rayN/releases/tag/v7.25.6-learning.3)。要求 macOS 15 或更新版本；测试包采用 ad-hoc 签名，未经 Apple 公证，实际代理与 Codex 会话需要目标 Mac 验收。Windows 包不能用于 Mac。

下载此分支，在仓库目录中打开 Codex，然后发送：

```text
请使用 $setup-v2rayn-learning，配置这个定制版 v2rayN，使用随附的默认规则和自动学习，替换我当前使用的 Clash。如果不知道订阅链接，请向我要；验证新代理能独立工作后再关闭旧代理。
```

也可以直接让 Codex 阅读 [.agents/skills/setup-v2rayn-learning/SKILL.md](.agents/skills/setup-v2rayn-learning/SKILL.md)。技能使用用户自己的订阅或节点，不包含 VPS 服务端搭建，也不包含作者的节点信息。默认规则随 skill 提供，自动学习需要在客户端启用；不会默认套用下载者原来的 Clash 规则。

仓库级 skill 的发现方式见 [OpenAI 官方技能文档](https://learn.chatgpt.com/docs/build-skills)。learning.3 客户端压缩包随附 `.agents/skills` 与文档；也可下载独立 skill 包或本分支源码，在解压目录打开 Codex。

---

### A GUI client for Windows, Linux and macOS. Support [Xray](https://github.com/XTLS/Xray-core) and [sing-box](https://github.com/SagerNet/sing-box) and [others](https://github.com/2dust/v2rayN/wiki/List-of-supported-cores)

[![CodeFactor](https://www.codefactor.io/repository/github/2dust/v2rayn/badge)](https://www.codefactor.io/repository/github/2dust/v2rayn)
[![Release](https://img.shields.io/github/v/release/2dust/v2rayN?logo=github&label=Release)](https://github.com/2dust/v2rayN/releases)
[![Downloads](https://img.shields.io/github/downloads/2dust/v2rayN/latest/total?logo=github&label=Downloads)](https://github.com/2dust/v2rayN/releases)
[![Telegram](https://img.shields.io/badge/Telegram-Chat-26A5E4?logo=telegram)](https://t.me/v2rayn)
 
[![Windows](https://img.shields.io/badge/Windows-supported-0078D6?logo=windows)](https://github.com/2dust/v2rayN) 
[![Linux](https://img.shields.io/badge/Linux-supported-FCC624?logo=linux&logoColor=000)](https://github.com/2dust/v2rayN) 
[![macOS](https://img.shields.io/badge/macOS-supported-000000?logo=apple)](https://github.com/2dust/v2rayN) 
[![GPG Signed](https://img.shields.io/badge/GPG-signed-4B32C3?logo=gnuprivacyguard)](https://github.com/2dust/v2rayN)


---

## Download / 下载

Download the latest release here:

在这里下载最新版本：

[https://github.com/2dust/v2rayN/releases](https://github.com/2dust/v2rayN/releases)


> [!TIP]
> v2rayN is the desktop version. For the mobile version, please visit the v2rayNG \
> v2rayN 是电脑版，手机版请访问 v2rayNG
>
> https://github.com/2dust/v2rayNG

---

## Documentation / 使用文档

Read the Wiki for usage guides and configuration details.

请阅读 Wiki 获取使用说明和配置教程。

[https://github.com/2dust/v2rayN/wiki](https://github.com/2dust/v2rayN/wiki)

---

## Supported Platforms / 支持平台

| Platform / 平台 | x64 | x86 | arm64 | riscv64 | loong64 |
| --- | --- | --- | --- | --- | --- |
| Windows | ✅ | ✅ | ✅ | - | - |
| Linux | ✅ | - | ✅ | ✅ | ✅ |
| macOS | ✅ | - | ✅ | - | - |

Minimum OS requirements: [Release files introduction](https://github.com/2dust/v2rayN/wiki/Release-files-introduction) / 最低系统要求：[发布文件介绍](https://github.com/2dust/v2rayN/wiki/Release-files-introduction)

---

## GPG Verification / GPG 签名校验

Release files are signed with GPG to verify authenticity and integrity, helping prevent mirror, ISP, or CDN hijacking.

发布文件已使用 GPG 签名，可用于校验文件真实性与完整性，预防镜像站、运营商或 CDN 劫持。

### Fingerprint / 公钥指纹

```text
7694 5E9F 3E9A 168F 8070 F195 805D 661C
134D FAF6 8903 C199 463C 31E5 AE90 3AE0
```

---

## Community / 社区

Telegram Group / Telegram 群组：

[https://t.me/v2rayN](https://t.me/v2rayN)

Telegram Channel / Telegram 频道：

[https://t.me/github_2dust](https://t.me/github_2dust)
