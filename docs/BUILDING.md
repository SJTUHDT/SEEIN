# 构建、源码与发布

## 本体及公开测试

使用 PowerShell 7、Git 和 .NET SDK 8.0.426。建议检出到 ASCII 路径，例如 `C:\src\SEEIN`（原生 GN 工具不支持中文工程路径）。NuGet 依赖由项目文件锁定版本，首次还原需要网络。

```powershell
pwsh -File scripts/build.ps1 -Configuration Release
dotnet run --project tests/Seein.CapturePlayback.Tests -c Release
dotnet run --project tests/Seein.ThemeEditor.Tests -c Release
dotnet run --project tests/Seein.ToolShortcuts.Tests -c Release
dotnet run --project tests/Seein.PublicRelease.Tests -c Release -- "$PWD"
python scripts/check-public-source.py
```

脚本优先使用 `SEEIN_DOTNET` 或工程内 `.build/dotnet/dotnet.exe`，否则使用 PATH。公开构建没有额外的本地源码目录依赖。测试不默认控制真实设备；主题编辑、采集及快捷键专项使用隔离数据。需要视觉预览的测试参数在各测试工程中说明。

## 构建完整插件

本体/托管模块可直接构建；完整发行还需要下面的固定第三方输入，均放在被 Git 忽略的 `.build`。它们不是用户配置或安装目录。

| 输入 | 固定版本/准备方法 |
|---|---|
| Snow Shot 与 Pane | `vendor/tools/sources.lock.json` 固定提交；`Build-IntegratedTools.ps1` 克隆并应用对应补丁和集成源文件 |
| Pane SDK | .NET 10.0.404；`SEEIN_DOTNET10` 或 `.build/tools/dotnet10/dotnet.exe` |
| C++/Windows SDK | 锁定的 MSVC 14.51.36231、SDK 10.0.28000.0；可用已配置的 x64 Native Tools Shell，或 `.build/tools/msvc`、`.build/tools/windows-sdk` 便携布局 |
| Qt | 6.12.0，安装到 `.build/tools/qt/kit`；发布的对应源码附件保留实际版本 |
| Rust / OCR | 锁定 Rust 1.97.1，OCR 子模块提交和 nightly 见 sources.lock.json；默认校验上游提供的 OCR 运行时及模型 |
| vcpkg | 克隆到 `.build/tools/vcpkg`，检出 sources.lock.json 的 baseline 并 bootstrap |
| CMake/Ninja/Python/Node | 放入 PATH；Python 3.11+ 可用 `SEEIN_PYTHON` 指定；Node 用于从固定 dsh-pet ASAR 读取素材 |
| PresentMon | 2.6.0，`Prepare-PublicDependencies.ps1` 获取官方 x64 发布程序及许可证，核对固定哈希 |
| 桌宠编解码器 | `Build-NativePetCodecs.ps1` 下载固定 FFmpeg 源码并用 MSYS2 构建，依赖版本和配置保存在 origin.json、toolchain-versions.txt |
| OpenRGB | `Prepare-PublicDependencies.ps1` 从官方固定发行获取，验证引擎哈希；对应源码另行校验分发 |

原生输入准备好后：

```powershell
$env:SEEIN_PYTHON = 'C:\Path\To\Python\python.exe'
pwsh -File scripts/Build-IntegratedTools.ps1
pwsh -File scripts/Stage-IntegratedTools.ps1
pwsh -File scripts/Build-NativePetCodecs.ps1
pwsh -File scripts/publish.ps1 -Profile PublicFull
```

发布器检查每个工具 `RUNTIME.sha256`，缺少输入或校验失败就停止，不把工具静默丢掉。已有受信任构建缓存可通过 `SEEIN_DEPENDENCY_CACHE` 提供，其下只读取 deps/tools/tool-payload 三个命名目录；在构建目录创建的链接不提交 Git。缓存仅用于提速，不是源码替代品。

## 对应源码与改动

`vendor/tools/snow.patch`、`pane.patch` 和集成文件针对锁定上游提交。发行源码附件同时提供**实际修改后的完整工具源码**；重建某次发行优先使用该对应源码附件和同批构建记录，避免误用其他版本的补丁。

`vendor/tools/export-sources.py` 导出固定工具工作树及集成源，不导出 buildtrees、运行数据或凭据。`scripts/collect-release-sources.py` 收集 Qt、FFmpeg、libvpx、OpenRGB 和原生依赖源码及哈希。Qt 6.12.0 用于截图，OpenRGB 自带 Qt 6.8.3，两个版本保持隔离。桌宠 FFmpeg 与截图 GPL FFmpeg 也分别提供来源和配置。

## 签名与发布

`scripts/publish.ps1` 支持 Core、PublicFull。输出位于 `artifacts/releases/v2.2.0`，包含展开的应用和签名插件包。`scripts/package-release.py` 生成最终 ZIP、源码快照、组件清单、许可证和 SHA-256 总清单。

签名私钥在当前 Windows 用户的 `%LOCALAPPDATA%\SeeinPublicBuild\keys` 下，经 DPAPI 加密，不进入仓库或发行附件。自建发行会创建自己的密钥、公钥和插件；修改第三方动态库时保留各自许可和可替换方式。不要索取或复制官方私钥。

普通 CI 构建托管源码并运行公开测试，不持有发布凭据。完整原生构建和最终打包由维护者显式运行，在发布 Draft 中复核后正式上线。更新时同步程序版本、插件版本、文档及对应源码。
