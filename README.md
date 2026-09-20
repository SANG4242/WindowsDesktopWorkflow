# 桌面工作流（DesktopWorkflow）

[English](README.en.md) | 简体中文

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6.svg)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)

DesktopWorkflow 是一个面向 Windows 的开源桌面工作区启动与窗口布局工具。它把常用应用、窗口识别规则、布局和全局快捷键保存为可复用的工作流，以后可以从控制窗口、系统托盘或快捷键一次启动或复用多个窗口，并恢复预设排列。

适合开发、学习、写作、运维、数据分析等需要反复打开同一组应用的场景。程序使用 C#、.NET 10 和 WPF 开发，默认完全在本地运行，不需要账户或云服务。

## 界面预览

![DesktopWorkflow 工作流中心：开发工作区的双窗口布局](docs/images/overview.png)

![DesktopWorkflow 编辑器：应用选择、布局分配和实时预览](docs/images/editor.png)

> 截图中的应用、文件和页面均为通用演示内容，不包含真实用户数据。

## 功能

- **启动或复用窗口**：根据进程名、窗口标题和可选命令行特征查找现有窗口；没有匹配窗口时启动对应应用并等待窗口出现。
- **两类布局策略**：
  - Windows 11 原生贴靠：使用六种稳定布局档案，通过系统 `Win + Z` 界面创建原生贴靠组合，并校验实际窗口位置。
  - 自定义比例：按横向或纵向比例排列任意数量的窗口，例如 `2,1` 或 `1,1,1`。
- **可视化工作流编辑器**：从当前窗口、已安装应用或本地 `EXE` / `LNK` 添加目标；编辑启动参数和匹配条件；分配布局区域并实时预览。
- **工作流中心**：在列表中查看工作流、应用数量、有效快捷键和实时窗口缩略图，并执行、编辑、复制、导入、导出或删除工作流。
- **全局入口**：支持系统托盘、每个工作流独立的全局快捷键，以及可选的登录后启动。
- **安全执行**：排列前保存临时窗口状态；失败或取消时尽力恢复。原生贴靠失败会报告错误，不会静默切换成普通窗口定位。
- **本地声明式配置**：工作流使用 UTF-8 INI 文件保存，便于检查、备份、手工修改和版本管理。
- **多显示器与 DPI**：可选择主显示器、鼠标所在显示器或指定显示器；原生布局映射按显示环境保存。

## 下载与运行

从 [Releases](../../releases) 下载适合的发行物：

| 发行物 | 文件名 | 说明 |
| --- | --- | --- |
| 安装包 | `DesktopWorkflow-Setup-v<VERSION>-x64.exe` | 用户级安装，可选桌面快捷方式和登录后启动 |
| 便携包 | `DesktopWorkflow-v<VERSION>-Portable-x64.zip` | 解压后运行 `DesktopWorkflow.exe`，包含示例、许可证和说明 |
| 单文件版 | `DesktopWorkflow-v<VERSION>-SingleFile-x64.exe` | 自包含的单个可执行文件 |
| SHA-256 校验 | `DesktopWorkflow-v<VERSION>-SHA256SUMS.txt` | 校验下载文件完整性 |

以上发行物面向 Windows x64，均为自包含构建，无需预装 .NET Runtime。安装包默认安装到当前用户目录，不要求管理员权限。

Windows 11 22H2 或更高版本可使用原生贴靠组合。Windows 10 可使用自定义比例布局。

### 更新与卸载

三种发行形式共用 `%LOCALAPPDATA%\DesktopWorkflow` 中的工作流、设置和日志。更新程序不会主动删除这些用户数据。

- **安装包**：退出正在运行的 DesktopWorkflow，直接运行新版安装包覆盖安装。卸载时可选择保留用户数据；保留后重新安装即可继续使用原工作流。
- **便携 ZIP**：从托盘退出程序，删除旧程序目录，再把新版 ZIP 解压到新目录。不要把 `DesktopWorkflow.exe` 直接覆盖到仍在运行的目录中。
- **单文件 EXE**：从托盘退出程序，用新版 EXE 替换旧文件后重新启动。
- **切换发行形式**：退出旧版后直接启动另一种发行形式即可，用户数据会继续从同一目录加载。避免同时运行多个副本。
- **彻底卸载**：删除程序或运行卸载器后，再手动删除 `%LOCALAPPDATA%\DesktopWorkflow`；此操作会删除工作流、设置和日志，执行前请先导出需要保留的工作流。

### 创建第一个工作流

1. 启动程序，点击“新建工作流”。
2. 填写名称，通过“当前窗口”“已安装应用”或“选择文件”添加应用。
3. 选择 Windows 原生贴靠或自定义比例，并把应用分配到对应区域。
4. 按需设置全局快捷键，保存工作流。
5. 在主窗口点击“执行排列”，或从托盘和快捷键执行。

仓库中的 [`examples/记事本单窗口.ini`](examples/记事本单窗口.ini) 可以直接导入。

## 工作流配置

默认用户数据目录：

```text
%LOCALAPPDATA%\DesktopWorkflow\
├─ workflows\                 # UTF-8 INI 工作流
├─ settings\
│  ├─ user-settings.ini       # 本机偏好和快捷键覆盖
│  ├─ native-layout-profiles.json
│  └─ active-session.json     # 仅在排列期间存在的恢复快照
└─ logs\
   └─ desktop-workflow.log
```

可通过 `--workspace-root <目录>` 指定另一套数据目录，适合便携测试或开发验证。

最小配置示例：

```ini
[workflow]
id=notepad-example
name=记事本示例
description=单窗口比例布局示例
hotkey=

[window.1]
name=记事本
exe=C:\Windows\System32\notepad.exe
args=
workdir=C:\Windows\System32
match=记事本|Notepad ahk_exe Notepad.exe
process_args_contains=
reuse=true
wait_seconds=20

[layout]
mode=proportional
orientation=horizontal
ratios=1
gap=0
monitor=primary
native_profile=wide-left
native_layout=5
native_zones=1
native_delay_ms=700
```

配置约定：

- `workflow.id` 在全部工作流中唯一。
- `window.N` 从 1 连续编号。
- `match` 可使用多个标题候选和 `ahk_exe` 进程名。
- `process_args_contains` 可区分同一程序的不同配置实例。
- `ratios` 的数量需要与窗口数量一致；程序会自动归一化比例。
- `monitor` 支持 `primary`、`mouse` 或从 1 开始的显示器编号。

导出的工作流可能包含本机程序路径、启动参数、窗口标题或浏览器配置名称。公开分享前请检查文本内容。

## 设计思路

### 声明目标，不记录固定窗口句柄

工作流描述“需要哪些窗口”和“希望如何排列”。每次执行都会重新发现或启动窗口，避免保存易失效的窗口句柄，也不会在成功后持续接管窗口。

### 分离窗口准备与布局执行

执行过程分为窗口发现、并发启动与等待、状态快照、布局应用、几何校验和提交/回滚。应用启动规则与布局策略相互独立，便于增加新的发现方式或布局实现。

### 原生贴靠与比例布局保持明确边界

普通 Win32 窗口定位可以精确设置矩形，但不能创建 Windows 原生贴靠组。DesktopWorkflow 对两种模式分别实现：原生模式走系统 Snap Layouts 交互并验证结果；比例模式直接计算显示器工作区。失败时不会用另一种模式伪装成功。

### 配置和执行采用可恢复操作

工作流保存使用临时文件、重新解析和替换/回退流程。窗口排列前写入短期会话快照，成功后立即删除；失败、取消或上次异常退出时尝试恢复原窗口状态。

### 本地优先

核心运行路径没有遥测、分析或网络回传。配置、布局映射和日志保存在用户本地目录。项目仍会在日志中记录排错所需的窗口标题、程序路径和启动参数，因此分享日志前应检查敏感信息。

## 架构

```text
src/DesktopWorkflow.App/
├─ App.xaml(.cs)                    # 生命周期、托盘、快捷键、单实例和服务编排
├─ MainWindow.xaml(.cs)             # 工作流中心
├─ WorkflowEditorWindow.xaml(.cs)   # 单页双栏工作流编辑器
├─ ApplicationDiscoveryServices.cs  # 当前窗口、已安装应用和快捷方式发现
├─ IniServices.cs                   # INI 读取、验证和用户设置
├─ WorkflowFileStore.cs             # 保存、导入和删除的补偿式事务
├─ WorkflowRunner.cs                # 窗口准备与执行流程
├─ WindowMatcher.cs                 # 窗口与进程匹配
├─ WindowLayoutService.cs           # 原生贴靠和比例布局
├─ WindowSessionManager.cs          # 临时快照与失败恢复
└─ NativeLayoutProfiles.cs          # 按显示环境保存原生布局映射
```

主执行链：

```text
工作流入口
  → 加载并验证配置
  → 查找或启动全部目标窗口
  → 保存窗口状态快照
  → 应用原生贴靠或比例布局
  → 校验结果
  → 提交并删除快照 / 失败时回滚
```

## 二次开发

### 环境

- Windows 10/11 x64
- .NET 10 SDK
- 可选：Inno Setup 6，用于生成安装包
- PowerShell 7，用于仓库脚本

### 构建与运行测试

```powershell
git clone <repository-url>
cd DesktopWorkflow

dotnet build src/DesktopWorkflow.App/DesktopWorkflow.App.csproj -c Release
dotnet run --project src/DesktopWorkflow.Tests/DesktopWorkflow.Tests.csproj -c Release
```

`DesktopWorkflow.Tests` 是无第三方测试框架的控制台测试程序，当前覆盖配置往返、比例区域、原生布局目录与约束，以及同一应用的多目标生成。

### 本地发布与验证

```powershell
# 生成供本机运行的框架依赖版 app/ 目录
./scripts/发布.ps1

# 验证 Release 构建、测试、工作流解析，并比较 app/ 完整文件哈希
./scripts/验证.ps1

# 生成便携包、单文件版和安装包
./scripts/打包.ps1 -Version 1.0.0
```

`发布.ps1` 运行前需要从托盘退出当前仓库的 DesktopWorkflow 实例。完整打包需要安装 Inno Setup 6。未安装时可用 `./scripts/打包.ps1 -Version 1.0.0 -OutputTypes portable,singlefile` 只生成便携包和单文件版；安装包可由 GitHub Actions 在版本标签上构建。

### GitHub Actions 发布

CI 会在 `main`、`master` 和 Pull Request 上执行构建与打包。推送形如 `v1.0.0` 的标签时，会创建 GitHub Release 并上传 `dist/` 中的 EXE 和 ZIP：

```powershell
git tag v1.0.0
git push origin v1.0.0
```

发布前建议在可见桌面中人工验证：

- 当前窗口、已安装应用和 `EXE` / `LNK` 三类添加入口；
- 原生贴靠组合及失败回滚；
- 比例布局、多显示器和 DPI 缩放；
- 快捷键冲突、托盘执行和开机启动；
- 导入、导出、覆盖、复制和删除流程；
- 安装、升级、卸载与用户数据保留选项。

## 扩展方向

新增能力时，优先保持现有边界：

- 新的应用发现来源应转换为统一的窗口目标模型。
- 新布局策略应实现独立布局服务，并保留校验与回滚。
- 配置字段变更应同时更新解析、序列化、验证和往返测试。
- 涉及真实窗口移动的测试应与无 GUI 自动化测试分开。
- 用户文件删除应继续进入回收站，并保留失败补偿。

欢迎通过 Issue 报告可复现问题，或通过 Pull Request 提交小范围、可验证的改动。问题报告建议包含 Windows 版本、显示器布局、DPI、工作流配置（脱敏后）、复现步骤和相关日志片段。

## 已知限制

- Windows 没有公开、稳定的 API 可直接创建 Snap Group。原生模式依赖 `Win + Z` 系统界面，菜单顺序可能随 Windows 版本、语言和显示环境变化。
- 原生贴靠需要前台窗口交互；远程桌面、虚拟显示器或输入焦点竞争可能导致校验失败。
- 窗口标题和命令行规则取决于目标应用，应用升级后可能需要调整。
- 自定义比例布局不会形成 Windows 原生贴靠组。
- 当前自动化测试不替代真实桌面的原生贴靠、安装和多显示器验收。

## 隐私与安全

- 程序默认不联网，不包含遥测和账户系统。
- 工作流、设置、会话快照和日志保存在 `%LOCALAPPDATA%\DesktopWorkflow`。
- 日志可能包含窗口标题、程序路径和启动参数；会话快照可能暂存窗口及进程信息。
- 工作流导入后可能启动其中指定的程序。只导入可信来源的配置，并先检查 `exe`、`args` 和 `workdir`。

安全问题请避免直接公开包含个人路径、窗口标题或启动参数的完整日志；先提供最小化、脱敏后的复现信息。

## 许可证

本项目采用 [MIT License](LICENSE)。
