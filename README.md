# SteamCN Translator

面向 Windows Steam 客户端的简体中文翻译工具。打开创意工坊、商店或社区页面后，程序识别可见的原文段落，把译文显示在对应位置。常用操作集中在主窗口和悬浮球菜单里。

[下载与版本记录](https://github.com/CHUNAI45/SteamCNTranslator/releases) · [反馈问题](https://github.com/CHUNAI45/SteamCNTranslator/issues)

## 能翻译哪些内容

| 页面 | 主要内容 |
| --- | --- |
| 创意工坊 | 模组标题、说明、评论，以及「已订阅项目」列表的条目标题 |
| Steam 商店 | 商品标题、说明和可识别的正文 |
| 游戏评测 | 可见的评测正文 |
| 社区讨论和指南 | 主题标题、帖子及指南正文 |

程序会过滤导航、计数、订阅时间等界面文字。换页或滚动时，已经离开可视区的译文会清理。Steam 最小化、切到其他应用，或定位无法核实时，覆盖层会隐藏。

## 下载和使用

1. 在 Releases 下载 `SteamCN-Translator-v1.1.0-Windows-x64.zip`，解压到一个目录。
2. 打开 Steam 客户端，再运行解压后的 `SteamCN.App.exe`。首次使用时，在主窗口选择翻译引擎。
3. 切回 Steam 并打开要看的页面。在线译文会逐段出现。需要重试当前页面时，点主窗口或悬浮球菜单里的「重新扫描」。

当前发布包面向 Windows x64。在线翻译需要网络连接。画面中的原文能否被识别，取决于页面内容、可访问文本，以及 Windows 已安装的 OCR 语言组件。

## 显示方式

- **双语显示**保留原文和译文，适合对照阅读。
- **仅中文**在能够可靠定位的区域显示中文译文。
- **仅原文**临时查看 Steam 原页面。

在「显示」页签可以切换旁边、覆盖或自适应布局，并调整透明度、字号、译文宽度和双语间距。「范围」页签可以单独启用或停用不同类型的 Steam 页面。

## 翻译引擎和语种

| 引擎 | 使用方式 |
| --- | --- |
| Google、Bing 网页翻译 | 无需 API 密钥。网页接口可能限频或暂时不可用 |
| 本地离线翻译 | 通过 Argos Translate 运行，需要安装本地引擎和对应语言包 |
| LibreTranslate | 配置服务地址，按服务要求填写密钥 |
| Azure Translator | 使用自己的 API 密钥和服务区域 |

开启自动回退后，失败的段落可以交给备用引擎，已返回的译文会继续显示。使用在线引擎时，待翻译的原文会发送给所选服务，触发回退时也可能发送给备用服务。缓存和诊断保存在本机。

在线引擎可以处理英语、日语、韩语、俄语和多种欧洲语言。能否从 Steam 画面读到对应原文是另一个环节。例如，韩文页面如果需要走截图识别，Windows 必须有韩语 OCR 组件。离线覆盖范围则取决于已安装的翻译语言包。

## 常见问题

- **切到浏览器或桌面后看不到译文**。切回前台 Steam 客户端，程序会重新核验页面。
- **某个页面完全没出字**。先检查「范围」里对应页面类型是否启用，再用「重新扫描」。展开「实时诊断」，查看 OCR 文本数、当前引擎和译文覆盖层状态。
- **只有部分标题或段落被翻译**。被截断、识别结果不一致或位置不可靠的原文会保留。滚动到内容完整可见的位置后，可以重新核验。
- **在线翻译慢或失败**。在「引擎」页签测试连接，查看主窗口提示，也可以切换已配置的引擎。

反馈时请附程序版本、Steam 页面类型、复现步骤，以及去掉个人信息后的截图或诊断信息。请勿贴 API 密钥、登录信息或私人页面内容。

## 源码与许可状态

当前测试版是从已有 Windows 程序恢复并修改后构建的。现有本地工程分为桌面界面、窗口与网页区域核验、OCR、段落处理和翻译引擎等模块。发布准备仍包括核实原始源码与第三方资源的许可、整理依赖，以及验证其他人能在干净的 Windows 环境中从源码构建。

项目采用 [MIT 许可证](LICENSE)。目录中的代码由项目原有 Windows 发布版恢复，并整理为可独立构建的 .NET 解决方案。第三方依赖通过 NuGet 还原，适用各自的许可证。

## 从源码构建

在 Windows x64 上安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)，然后在仓库根目录运行：

```powershell
dotnet build SteamCNTranslator.sln -c Release
dotnet run --project src/SteamCN.App/SteamCN.App.csproj -c Release
```

要生成可分发的 Windows x64 发布目录，可以运行：

```powershell
dotnet publish src/SteamCN.App/SteamCN.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/win-x64
```

| 目录 | 职责 |
| --- | --- |
| `src/SteamCN.App` | WPF 主窗口、悬浮菜单、设置和启动逻辑 |
| `src/SteamCN.Windows` | Steam 窗口绑定、网页区域核验和译文覆盖层 |
| `src/SteamCN.Ocr` | Windows OCR 接入 |
| `src/SteamCN.Core` | 语言检测、段落处理、翻译调度及共享接口 |
| `src/SteamCN.Translation` | 翻译引擎、逐段返回与自动回退 |
| `src/SteamCN.Storage` | 设置、术语词典和翻译缓存 |
| `src/SteamCN.Compatibility` | 页面分类和兼容性诊断 |

本项目与 Valve 或 Steam 无隶属关系。
