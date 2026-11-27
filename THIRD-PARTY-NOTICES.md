# Third-party notices / 第三方来源与许可

SEEIN 自有代码采用 GPL-3.0-only。以下组件、素材与字体保留原有版权和许可证，不因集成被统一重新许可。实际解析的 NuGet 组件、版本与原生包哈希另见发行附件中的组件/来源清单。对应源码与构建脚本与同次二进制发行配套。

## Snow Shot / Snow Apps

- 作者：mg-chao 及上游贡献者。
- 源码：https://github.com/mg-chao/snow-shot/tree/7fecdb40a766e69f3a16b84c02e0fc50efc6e162
- 固定版本：v1.6.3_snow-shot；OCR 子模块 a2fc7abfec5913618c78a0d2330ae520872c5abf。
- Snow Shot、Snow Image、snow-crates、共享构建代码等为 GPL-3.0-or-later；Ant Design Qt/绘图引擎及 simd-paddle-ocr-rs 等按上游目录许可，分别包括 Apache-2.0。
- SEEIN 修改：私有管道、动作/设置/快捷键桥、存储、密钥保护、外部打开、命名与生命周期。截图和录制引擎不改称自研。
- 安装位置：Plugins/capture/tools/snow；完整许可证在该工具的 licenses 目录。实际修改源码与 SEEIN 集成配方在 ToolSources.zip。

## Pane 与 Everything

- Pane：https://github.com/LBEILC/Pane/tree/6f8be9632bd4d4e6d27240db5e18ea5923327476 ，v0.1.606，GPL-3.0。
- SEEIN 修改：进程、管道、能力、设置、快捷键、搜索站点、对齐及外部打开集成。未集成模块见效率插件说明。
- Pane 运行目录：Plugins/efficiency/tools/pane；其 .NET 10、Windows App SDK 和依赖保留微软及各原作者声明。
- Everything 1.4.1.1032：https://www.voidtools.com/ ，Copyright David Carpenter，MIT-style permission，原文见 vendor/tools/everything-LICENSE.txt 及 Plugins/efficiency/tools/everything/License.txt。上游程序仅更名，固定哈希校验。
- 原生键盘监听组件也随截图插件提供，保留 Pane 许可证与对应源码，确保截图开关不依赖安装效率插件。

## dsh-pet 与桌宠素材

- 上游：https://github.com/PC2005-cloud/dsh-pet/tree/4e589f6dacabbd3170e91e7a213b2a7afff1a09b/dsh-pet ，0.4.9，MIT，Copyright (c) 2026 PC2005-cloud。
- 素材固定发行 ZIP SHA-256：fec1d27d59fc0c9a883e77eff2facbf5676c15ffd6011b1436351cbc34f06c1e。
- 使用动画、图片、声音、字体和默认配置，并移植拖拽、菜单和行为；原许可随 Plugins/companion/pet-assets/LICENSE-dsh-pet.txt 保留。素材、字体及第三方角色的原有声明仍适用，未标为 SEEIN 自有作品。
- 当前桌宠为 SEEIN 原生 WPF 子进程，不发行原 Electron/Chromium 应用。其新实现及移植差异见桌宠文档。

## FFmpeg、libvpx、Qt 与原生依赖

- 桌宠 FFmpeg n8.1：a65b3bfe9dacc3b20597ef199d0afdd8bc8128e2，禁用 GPL/nonfree 的动态 LGPL-2.1-or-later 构建。配置、许可证及哈希在 Plugins/companion/pet-codecs，源包和配方在发行 sources 附件。
- libvpx 1.17.0：82615ff0733cd4063838c352bc2e2ab225f37ade，BSD-style license 与专利许可；LICENSE/PATENTS/AUTHORS 随 DLL 保留。winpthreads 保留其 MIT/BSD 声明。
- FFmpeg.AutoGen 8.1.0：https://github.com/Ruslan-B/FFmpeg.AutoGen ，MIT。
- 截图 FFmpeg 9.0 构建包含 GPL 组件（包括 x264），按该构建的 GPL 与原生依赖声明提供完整对应源码。它与桌宠精简 LGPL 库隔离，不混用 ABI。
- 截图 Qt 6.12.0 与 OpenRGB 自带 Qt 6.8.3：动态链接，按适用 LGPL/GPL 及第三方许可证分发，保留运行目录的许可文本。DLL 可替换，用户可按构建说明调试修改；对应 Qt 模块源包随发行提供。
- vcpkg baseline、原生工具链及 OCR 模型/运行时来源由 sources.lock.json、模型 manifest、NativeDependencySources 和源码哈希记录。

## OpenRGB 与硬件接口

- OpenRGB 1.1：https://codeberg.org/OpenRGB/OpenRGB ，提交 8e16de27858cf2be6af54a33ce003df74cb9fd3e，GPL-2.0-or-later。
- 官方引擎仅更名为 Plugins/devices/rgb-engine/seergb.exe，SHA-256 e26ae71162795a8abcfb90c05c1572a00ba06ca63b886aca57392a0138963b1e；许可证随目录保留，对应 OpenRGB-1.1-source.zip 随发行提供。
- SEEIN 使用本机回环 SDK，并实现已验证设备的场景、原生效果及软件控制集成；不是 OpenRGB 官方发行的替代品牌。
- AMD ADLX 绑定参考 GPUOpen SDK 头文件提交 32b5a740d42295c5dfe9026b9f52683da0f3af91，只动态使用用户驱动自带 amdadlx64.dll，不发行 AMD 驱动二进制。
- PresentMon 2.6.0：Intel/GameTechDev 项目，官方程序位于 Plugins/display/tools/seefps.exe，原许可证同目录保留，对应来源在组件清单中列出。

## 采集播放

FlashCap/FlashCap.Core 1.13.0（Apache-2.0，Kouji Matsui，7e087ca8725e29f869caebdb5205b5b5068a80fb）；Vortice.Windows 3.6.2（MIT，Amer Koleci，cd916a03f206165bec67982ed501e88820d4182b）；SharpGen.Runtime/COM 2.2.0-beta、Vortice.Mathematics 1.9.2（MIT）；NAudio 2.2.1（MIT，Mark Heath）。完整许可原文保存在 Devices 模块的 CapturePlayback/THIRD-PARTY-NOTICES.txt。

SEEIN 实现界面、会话、队列及音视频协调，不附带 OBS、另一个上游播放器或新增 FFmpeg 播放器进程。

## 小米设备授权

参考 https://github.com/PiotrMachowski/Xiaomi-cloud-tokens-extractor/tree/c4db715dace9806e905153c2977608873e8ab7c9 ，MIT，Piotr Machowski。原许可见 vendor/xiaomi-cloud/LICENSE.txt，安装包保留对应许可。SEEIN 为原生 C# 实现，不附带授权用 Python 解释器。

## 其他托管组件、字体与外部服务

.NET Runtime、Windows App SDK、WebView2 SDK、LibreHardwareMonitor、Microsoft.Data.Sqlite、SkiaSharp、OpenCvSharp、NAudio 等保留各自的 NuGet/运行时许可，详见发行组件清单和许可证附件。网页使用已安装 WebView2 Evergreen Runtime，SEEIN 不重新分发浏览器用户资料。

主题内修改后的 Rajdhani/RX78 Readout 字体采用 SIL OFL 1.1，保留原许可、改名和子集修改说明。桌宠随上游附带的字体按其原有声明处理，不标为 SEEIN 自制字体。

AstrBot、SnowLuma、QQ、大肥鱼工作台和模型/语音服务是用户另行安装或选择的外部服务，不包含在插件程序中，仍适用各服务自身许可及条款。SEEIN 包不带任何账号、授权、密钥、剪贴板或个人配置。
