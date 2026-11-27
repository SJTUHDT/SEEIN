# SEEIN 2.1.0

SEEIN 的首个公开版本：Windows 桌面仪表盘、耗能日历与六个可选插件。

## 下载

- 只需本体或希望按需添加：Core ZIP。
- 一次安装全部公开功能：Full ZIP。
- 已有 2.1.0 本体：按需下载六个 `.seeinplugin`，在应用设置中导入。
- 校验程序：SHA256SUMS.txt；查看实际组成：COMPONENTS.json。
- 开发及许可证：SEEIN 源码快照、修改后的原生工具源包和第三方对应源码附件。

## 六个插件

| 插件 | 功能与来源说明 |
|---|---|
| [副屏与主题](https://github.com/SJTUHDT/SEEIN/blob/v2.1.0/docs/plugins/display.md) | 串口副屏、可视化主题编辑器、视频背景、媒体和 FPS |
| [应用监控与应用库](https://github.com/SJTUHDT/SEEIN/blob/v2.1.0/docs/plugins/applications.md) | 应用时长、资源与功耗统计、进程归并、应用列表 |
| [设备控制](https://github.com/SJTUHDT/SEEIN/blob/v2.1.0/docs/plugins/devices.md) | DDC/CI、风扇、OpenRGB 集成、路由器、米家挂灯、UVC 采集播放 |
| [桌宠与大肥鱼](https://github.com/SJTUHDT/SEEIN/blob/v2.1.0/docs/plugins/companion.md) | dsh-pet 素材与行为的原生实现、本机对话桥、外部工作台 |
| [截图录屏与 OCR](https://github.com/SJTUHDT/SEEIN/blob/v2.1.0/docs/plugins/capture.md) | Snow Shot 集成、截图标注、滚动截图、录屏、OCR、翻译及 PDF |
| [效率工具](https://github.com/SJTUHDT/SEEIN/blob/v2.1.0/docs/plugins/efficiency.md) | Pane / Everything 集成、启动器、剪贴板、文件搜索、重命名、定时关机 |

Snow Shot、Pane、dsh-pet、OpenRGB 等上游作品保留来源及原许可证。桌宠为 SEEIN 原生子进程，不随包带 Electron；肥鱼后端另行部署。硬件适配以已验证型号及驱动能力为准，功耗为估算。

首次使用需配置智能设备地址及外部工作台路径。内嵌网页需要 WebView2。安装方法见 [安装说明](https://github.com/SJTUHDT/SEEIN/blob/v2.1.0/docs/INSTALL.md)，733 项自动化检查与实机验收边界见 [验收记录](https://github.com/SJTUHDT/SEEIN/blob/v2.1.0/docs/ACCEPTANCE.md)。请先完成手头的录制、导出再更新或退出。
