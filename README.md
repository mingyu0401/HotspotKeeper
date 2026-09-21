# HotspotKeeper · 热点守护

一个轻量的 Windows 11 桌面小工具：自动开启并保持「个人热点」在线。当热点长时间没有设备连接时，自动关闭并重新开启热点，让设备更容易回连。

> ⚠️ **免责声明：本软件未经完整测试，不保证在所有 Windows 设备上可用。**
> 热点开关、设备检测等核心功能仅在作者一台电脑上通过脚本层验证；
> 「有线网 ⇄ WiFi + Clash」一键切换功能**从未实测**（测试会直接切断作者的网络），
> 请在确认有备用上网方式后再使用该功能，风险自负。

## 功能

- **系统托盘**：点窗口 × 不会退出，软件带托盘图标继续在后台守护；双击托盘图标恢复窗口，右键托盘图标 →「退出 HotspotKeeper」才会完全关闭。
- **热点守护**：打开软件后自动打开 WiFi 网卡与 Windows「个人热点」，之后每 60 秒检测一次热点连接设备数；设备数为 0 时自动「关闭热点 → 等 3 秒 → 重新开启」，然后继续每分钟检测。
- **一键切换：WiFi + Clash**：关闭有线网卡 → 打开 WiFi → 启动 Clash for Windows（守护自动暂停，避免干扰）。
- **一键恢复**：关闭 Clash for Windows → 重新启用有线网卡。
- **开机自启动**（可选）：通过任务计划程序（最高权限）实现，登录时自动运行。
- **深色模式**：跟随系统 / 浅色 / 深色 三种主题。

## 使用

1. 下载 `HotspotKeeper.exe`（自包含单文件，无需安装 .NET）。
2. 右键「以管理员身份运行」（软件需要管理员权限来开关网卡与热点）。
3. 首次使用建议在「设置」里确认 Clash for Windows 的安装路径（留空则自动在常见位置查找）。

## 实现方式

- C# / WPF (.NET 8) 负责界面与定时守护逻辑。
- 网络操作通过内嵌的 PowerShell 脚本完成：
  - 热点开关与设备数：WinRT `NetworkOperatorTetheringManager`（`TetheringOperationalState` / `ClientCount`），失败时回退到 `192.168.137.1` 网卡启发式检测。
  - 网卡开关：`Enable-NetAdapter` / `Disable-NetAdapter`。
  - Clash：`Start-Process` / `Stop-Process`。

## 从源码构建

```powershell
# 需要 .NET 8 SDK（含 Windows Desktop）
dotnet publish -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish
```

产物在 `publish\HotspotKeeper.exe`。

## 测试状态（截至发布时）

| 功能 | 状态 |
| --- | --- |
| 热点状态/连接数检测 | ✅ 已在本机验证 |
| 热点关闭 / 开启 | ✅ 已在本机验证（脚本层） |
| 守护自动恢复（热点被关后自动重开） | ✅ 已在本机验证 |
| 关闭窗口 → 隐藏到托盘（进程存活） | ✅ 已在本机验证 |
| 托盘双击恢复 / 右键退出 | ⚠️ 逻辑已实现，未逐项实测 |
| WiFi / 有线网卡识别 | ✅ 已在本机验证（只读） |
| 有线断网 + Clash 一键切换 | ❌ 未测试 |
| 开机自启 | ❌ 未测试 |
| 界面完整交互 | ⚠️ 未逐项测试 |

## 作者

**明雨（MingYu）** · GitHub: [mingyu0401](https://github.com/mingyu0401)

## License

仅供学习交流使用。
