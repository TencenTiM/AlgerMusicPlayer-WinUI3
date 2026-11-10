<div align="center">
  <img src="./AlgerMusicPlayer.WinUI/Assets/icon.png" width="108" height="108" alt="AlgerMusicPlayer Logo" />
  <h1>AlgerMusicPlayer · WinUI 3</h1>
  <p>基于 C# / .NET 9 与 WinUI 3 原生重构的 Windows 高解析度音乐播放器</p>

  <p>
    <strong>简体中文</strong> | <a href="./README_EN.md">English</a>
  </p>

  <p>
    <a href="https://github.com/TencenTiM/AlgerMusicPlayer-WinUI3/stargazers"><img src="https://img.shields.io/github/stars/TencenTiM/AlgerMusicPlayer-WinUI3?style=flat-square&logo=github&color=22C55E" alt="GitHub Stars" /></a>
    <a href="https://github.com/TencenTiM/AlgerMusicPlayer-WinUI3/releases"><img src="https://img.shields.io/github/v/release/TencenTiM/AlgerMusicPlayer-WinUI3?style=flat-square&logo=github&color=0078D4" alt="GitHub Release" /></a>
    <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 9.0" />
    <img src="https://img.shields.io/badge/WinUI-3.0-0078D4?style=flat-square&logo=windows11&logoColor=white" alt="WinUI 3" />
    <img src="https://img.shields.io/badge/Windows%20App%20SDK-1.6-00A4EF?style=flat-square&logo=windows&logoColor=white" alt="Windows App SDK 1.6" />
    <img src="https://img.shields.io/badge/Audio-WASAPI%20%2F%20mpv%205.1-10B981?style=flat-square" alt="WASAPI & mpv 5.1" />
    <a href="./LICENSE"><img src="https://img.shields.io/badge/License-MIT-22C55E?style=flat-square" alt="MIT License" /></a>
  </p>
</div>

---

## 📖 项目简介

**AlgerMusicPlayer (WinUI 3 Edition)** 是对开源音乐播放器 **[algerkong/AlgerMusicPlayer](https://github.com/algerkong/AlgerMusicPlayer)** 的 **100% 纯原生 C# / WinUI 3** 桌面端重构版本。

本项目彻底移除了 Electron（Chromium + Node.js）运行时依赖，使用 **.NET 9 + Windows App SDK 1.6** 从底层重写了全部网络协议栈、音频播放管线与 Fluent 界面交互。在 1:1 还原原版核心功能与设计美学的基础上，带来了毫秒级原生冷启动、极低的内存/CPU 占用、原生 Windows 11 云母（Mica）材质，以及发烧级 **24bit 超清母带流媒体** 与 **5.1/7.1 多声道无下混原生直通输出** 能力。

---

## 📸 界面预览

| 深色云母主题（Dark Mica） | 浅色主题（Light Theme） |
| :---: | :---: |
| <img src="./docs/AlgerMusicPlayer%20WinUI%203%202.png" alt="深色云母主题" width="100%" /> | <img src="./docs/AlgerMusicPlayer%20WinUI%203%201.png" alt="浅色主题" width="100%" /> |
| **超清母带与多音源解灰设置** | **自研单文件安装程序** |
| <img src="./docs/AlgerMusicPlayer%20WinUI%203%203.png" alt="音源与音质设置" width="100%" /> | <img src="./docs/AlgerMusicPlayer%20WinUI%203%200.png" alt="自研安装程序" width="88%" /> |

---

## ✨ 核心特性

- **🪟 纯原生 Windows 11 桌面体验**
  - 基于 WinUI 3 与 Windows App SDK 1.6 构建，支持原生 Mica 云母半透明背景、深浅色主题无缝切换、圆角悬浮迷你播放器（Mini Mode）、系统托盘驻留及 Windows 原生媒体控制中心（SMTC）集成。
  - 全局自定义快捷键支持，进程重启自动记忆上次播放曲目、精确播放秒数与所在本地文件夹。

- **🎧 发烧级音频引擎与真 5.1 环绕声直通**
  - 双引擎音频管线（`NAudio` + 内置原生 `mpv` WASAPI 通道）：支持 **5.1 / 7.1 多声道音频无矩阵下混（`--ad-lavc-downmix=no`）原生直通输出**。
  - 内置 10 段参量均衡器（EQ）与多款预设音效、无级倍速播放、定时停止播放（Sleep Timer）。

- **🎼 全曲库在线服务与 24bit 超清母带音源**
  - **纯 C# 原生协议直连**：内置网易云音乐 WEAPI / EAPI 加密客户端，无需额外部署本地 Node.js API 服务。
  - 支持扫码登录、UID 免密登录与 Cookie 登录；完整支持每日推荐、私人 FM、心动模式、歌单广场、新碟上架、排行榜单、电台播客、听歌排行与评论互动。
  - **高解析度音质与多源自动解灰**：支持在线实时显示与切换音质档位（标准 / 极高 / 无损 FLAC / Hi-Res / **超清母带 `jymaster`**），内置多平台高码率音源自动提档与单曲手动换源，支持自定义解析接口与落雪（LX Music）音源配置。
  - **跨平台生态融合**：支持 **QQ 音乐** 账号登录与歌单同步播放、**Bilibili（B 站）** BV 号 / 关键词视频分 P 音频流直连播放、**MV 视频** 六大地区筛选与 1080P/720P 全屏连播。
  - **多线程无损下载**：支持批量高速下载音乐并自动通过 `TagLibSharp` 内嵌高清专辑封面、歌手专辑元数据与 LRC 歌词标签。

- **📂 本地音乐与 NAS / SMB 网络共享曲库**
  - 支持添加多个本地音乐目录及局域网 **SMB / UNC 网络共享路径（`\\server\share`）** 高码率无损流式直通播放。
  - 提供 **文件夹树形层级视图（Folder Tree）** 与全量平铺列表双视图切换，支持曲目搜索、排序与定位本地文件。

- **🎤 沉浸式全屏歌词与独立透明桌面歌词**
  - 点击左下角封面展开沉浸式播放详情页：支持封面主色调动态提取流光渐变背景、3D 悬浮封面交互、LRC 逐行 / YRC 逐字 / 翻译 / 罗马音同步滚动及歌词时间轴偏移微调。
  - 独立 Win32 分层透明桌面歌词窗口：支持置顶显示、鼠标穿透锁定、自定义字体字号与描边配色。

- **📦 自研单文件安装包**
  - 内置零外部依赖的自研现代安装器（`AlgerMusicPlayer.Installer`），一键打包生成自包含单文件安装程序 `AlgerMusicPlayer-Setup-x64.exe` 与绿色便携压缩包，完美兼容中文系统与中文安装路径，支持桌面/开始菜单快捷方式创建与一键干净卸载。

---

## 🗂️ 项目结构

```text
AlgerMusicPlayer-WinUI3/
├── AlgerMusicPlayer.WinUI/             # WinUI 3 主程序工程 (.NET 9 / Windows App SDK 1.6)
│   ├── Assets/                         # 应用图标与静态资源
│   ├── Helpers/                        # Win32 P/Invoke 互操作、窗口与托盘底层封装
│   ├── Models/                         # 音乐、歌词、配置与音质数据模型
│   ├── Services/
│   │   ├── Api/                        # 纯 C# 网易云 WEAPI/EAPI、QQ 音乐、B 站接口服务
│   │   ├── Audio/                      # NAudio + mpv 5.1 原生音频引擎、10 段 EQ、SMTC
│   │   ├── Download/                   # 异步下载队列与音乐内嵌标签写入服务
│   │   ├── Local/                      # 本地与 SMB 目录扫描及文件夹树构建服务
│   │   ├── Lyric/                      # LRC / YRC 逐字 / 翻译 / 罗马音解析器
│   │   ├── System/                     # 持久化配置、全局热键、多语言服务
│   │   └── Unblock/                    # 24bit 母带/无损多音源匹配与解灰服务
│   ├── ViewModels/                     # MVVM 主视图模型 (CommunityToolkit.Mvvm)
│   ├── Views/                          # 主窗口、沉浸歌词、迷你播放器、桌面歌词窗口
│   └── mpv/win-x64/                    # 内置原生多声道音频后端 (Git LFS 托管)
├── AlgerMusicPlayer.Installer/         # 自研单文件现代安装/卸载程序工程
├── build_and_run.ps1                   # 开发环境一键编译并运行脚本
├── build-installer.ps1                 # 一键发布自包含安装包与绿色版脚本
└── AlgerMusicPlayer.WinUI.sln          # Visual Studio 解决方案
```

---

## 🚀 快速开始

### 环境要求

- **操作系统**：Windows 10（1809 / Build 17763 及以上）或 Windows 11（x64）
- **开发工具**：[.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)（或 Visual Studio 2022 17.12+，勾选“.NET 桌面开发”与“Windows 应用 SDK C# 模板”）
- **Git LFS**：仓库内置的 `mpv.exe` 使用 Git LFS 托管，克隆前请确保已安装 `git lfs`

### 1. 克隆仓库

```powershell
git lfs install
git clone https://github.com/TencenTiM/AlgerMusicPlayer-WinUI3.git
cd AlgerMusicPlayer-WinUI3
```

### 2. 开发编译与运行

使用 Visual Studio 打开 `AlgerMusicPlayer.WinUI.sln` 选择 `x64` 直接运行，或在 PowerShell 中执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build_and_run.ps1
```

### 3. 一键构建安装包与绿色便携版

```powershell
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

构建完成后，产物将输出至 `dist/` 目录：
- `dist/AlgerMusicPlayer-Setup-x64.exe`：标准自包含单文件安装程序（免安装 .NET 运行时，开箱即用）
- `dist/AlgerMusicPlayer-Portable-x64.zip`：绿色免安装压缩包

---

## 🙏 致谢（Acknowledgements）

本项目能够诞生，首先要特别感谢原作者 **[Alger (@algerkong)](https://github.com/algerkong)** 以及原版开源项目 **[algerkong/AlgerMusicPlayer](https://github.com/algerkong/AlgerMusicPlayer)**！

原作者倾注大量心血打磨出了功能全面、交互优雅、完全开源免费的跨平台音乐播放器，为社区树立了极佳的标杆。本仓库的界面布局、功能交互与产品设计均源自并致敬原版项目，在此向原作者 **[@algerkong](https://github.com/algerkong)** 及所有原项目贡献者致以最诚挚的敬意与感谢！欢迎大家前往原项目点亮 Star ⭐️ 支持原作者：
- 原项目仓库：[https://github.com/algerkong/AlgerMusicPlayer](https://github.com/algerkong/AlgerMusicPlayer)

同时感谢以下优秀的开源项目与技术社区：
- [Microsoft Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- [NAudio](https://github.com/naudio/NAudio)
- [mpv media player](https://github.com/mpv-player/mpv)
- [TagLibSharp](https://github.com/mono/taglib-sharp)
- [QRCoder](https://github.com/codebude/QRCoder)
- [Binaryify/NeteaseCloudMusicApi](https://github.com/Binaryify/NeteaseCloudMusicApi)

---

## ⚠️ 免责声明

1. 本项目仅供 C# / .NET / WinUI 3 桌面端开发技术学习与个人非商业交流使用，**严禁用于任何商业用途**。
2. 本项目不存储、不分发任何受版权保护的音频或视频文件，所有在线音频数据均来自各平台公开接口实时解析，版权归原平台及权利人所有。
3. 请尊重音乐人与版权方劳动成果，支持并购买官方正版音乐会员服务；请在下载体验后 24 小时内删除相关缓存内容。

---

## 📄 开源协议

本项目延续原项目采用 **[MIT License](./LICENSE)** 开源协议（内置的 `mpv` 组件遵循其各自的上游开源协议，详见 `AlgerMusicPlayer.WinUI/mpv/win-x64/LICENSE.GPL`）。
