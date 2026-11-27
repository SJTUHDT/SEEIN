# 数据、网络和隐私

| 位置 | 内容 |
|---|---|
| `%ProgramData%\Seein\energy-v2.db` | 能耗日总量、分钟曲线和会话状态 |
| `%ProgramData%\Seein\app-usage.db` | 应用事件、聚合、归并、颜色、图标和隐藏设置 |
| `%ProgramData%\Seein\settings.json` | 传感器估算参数及已启用的设备策略 |
| `%LOCALAPPDATA%\Seein\desktop-ui.json` | 主题与导航顺序 |
| `%LOCALAPPDATA%\Seein\PetHome` | 桌宠配置、记忆与导入内容 |
| `%LOCALAPPDATA%\Seein\Companion` | 外部工作台目录和本机后台地址 |
| `%LOCALAPPDATA%\Seein\SmartHome` | 设备地址、预设、DPAPI 加密授权及路由器页面数据 |
| `%LOCALAPPDATA%\Seein\Tools` | 截图和效率工具设置、剪贴板及命名索引数据 |
| `%LOCALAPPDATA%\Seein\ThemeEditor` | 编辑器草稿与缓存 |

截图、录屏、PDF、主题工程和导出位置由用户的存储偏好决定。不要把个人数据目录提交到代码仓库；发布包从源码和固定依赖生成，不读取这些目录。

传感器、应用统计和本地 OCR 不需要把统计数据库上传。以下功能有自己的联网行为：设备局域网连接、小米授权与设备发现、用户配置的截图翻译、外部模型/语音服务、搜索站点，以及用户触发的依赖下载或上游服务功能。因此不使用“所有功能完全离线”的笼统描述。

路由器密码、挂灯授权与翻译密钥以 Windows 当前用户加密，发布包不包含真实凭据。加密不意味着可以公开数据库或整个用户目录。截图、摘要、剪贴板与网页日志均可能包含个人内容，反馈问题前请脱敏。

插件目录和下载缓存位于 `%LOCALAPPDATA%\Seein\PluginDownloads`，保存公开签名目录及按哈希命名的插件包；安装请求与结果位于同用户 incoming。目录仅在打开管理页或手动刷新时更新，无持续轮询。下载访问 SJTUHDT/SEEIN 官方 GitHub Release，不需要账号令牌。
