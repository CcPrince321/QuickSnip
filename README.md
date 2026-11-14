# QuickSnip

一个 60 MB 单文件、双击即用的 Windows 截图工具。按下热键 → 全屏变暗 → 框选 → 进剪贴板。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)

> **当前版本 v0.1 —— 只完成了第 1 轮（截图地基）**：全局热键、多屏混合 DPI 精确框选、复制到剪贴板。
> 标注、贴图、OCR、滚动截图都还没做，见 [路线图](#路线图)。

---

## 它解决什么问题

大多数截图脚本在**混合 DPI 的多显示器**上都会错位：框的是 200×200，截出来是 133×133，
或者副屏在主屏左侧时整体偏移。QuickSnip 的核心目标就是把这条坐标链彻底做对。

顺带避开了两个常见坑：

- **不做 hide → sleep(200ms) → capture**。那个模型是竞态，遮罩还可能漏进截图。
- **不用 `AllowsTransparency=True` 做遮罩**。它会强制 WPF 走软件渲染，拖拽掉帧。

---

## 特性

- **全局热键**：默认 `Ctrl+Alt+A`，可在托盘菜单里改，退出时必定 `UnregisterHotKey`
- **多屏混合 DPI 正确**：主屏 150% + 副屏 100%，副屏在主屏左侧/上方也正确
- **跨屏框选连续**：从主屏按下拖到副屏松开，选区不断裂、不偏移
- **冻结帧框选**：按下热键时抓一张整个虚拟桌面的位图，拖拽期间零截屏、无卡顿、边缘不闪
- **单击取消 / 双击全屏 / Ctrl+A 全屏 / Esc 取消**
- **零安装**：自包含单文件 exe，机器上不需要装 .NET 运行时
- **自带诊断**：物理坐标自检工具 + 冒烟自检 + 运行日志，偏移问题可定位到具体环节

## 快速开始

### 方式一：下载即用（推荐）

从 [Releases](../../releases) 下载 `QuickSnip.exe`，双击运行。

不需要安装 .NET，不需要管理员权限。启动后没有窗口，只有右下角托盘一个红色取景框图标
（可能被折叠进 `^`）。

### 方式二：从源码构建

```bat
git clone https://github.com/CcPrince321/QuickSnip.git
cd QuickSnip
dotnet build QuickSnip.sln -c Debug
dotnet run --project src\QuickSnip.csproj
```

发布单文件：

```bat
dotnet publish src\QuickSnip.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

> ⚠️ **不要直接双击 `src\bin\Debug\...\QuickSnip.exe`**。那是依赖框架的 apphost，会去系统里找
> 全局 .NET；如果 .NET 只装在用户目录（`%LOCALAPPDATA%\Microsoft\dotnet`），它会弹
> `You must install .NET` 然后退出。开发期用 `dotnet run`，交付期用 `dist\QuickSnip.exe`。

## 操作

| 操作 | 行为 |
| --- | --- |
| `Ctrl+Alt+A` | 开始截图（可在托盘菜单「热键设置…」里改） |
| 拖拽 | 框选；跨屏拖拽也连续 |
| 松手 | 裁切并复制到剪贴板 |
| 单击（不拖） | 取消（Snipaste 行为） |
| 双击 | 全屏：鼠标所在显示器 |
| `Ctrl+A` | 同上（全屏） |
| `Esc` / 右键 | 取消 |

配置文件：`%APPDATA%\QuickSnip\settings.json`；日志：`%APPDATA%\QuickSnip\logs\app.log`。

---

## 架构

### 先截屏，再上遮罩

时序很关键：快门在**我们自己的任何窗口出现之前**按下。

```
WM_HOTKEY
  → BitBlt(GetDC(NULL)) 抓整个虚拟桌面            ← 唯一一次截屏
  → EnumDisplayMonitors 枚举每块屏，每块屏开一个遮罩窗口
  → 拖拽期间只改一个 VSP 矩形变量并重绘矢量层      ← 零截屏
  → 松开：从冻结帧内存里 CROP
```

好处：天然不会把遮罩拍进截图，也不存在「睡多久」的竞态。

### 每块显示器一个遮罩窗口

一个横跨所有屏幕的窗口，只能按**某一块屏**的 DPI 渲染，另一块屏由 DWM 位图拉伸，文字发虚，
跨屏拖拽时命中测试还会抖 1px。所以 N 块屏 = N 个**不透明**窗口，窗口内画暗层 + 用
`CombinedGeometry(Exclude)` 挖出选区洞，全程硬件合成。

### 只有一套坐标系：VSP

**虚拟桌面物理像素（Virtual Screen Physical）**是唯一真值，原点 `(SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN)`
（副屏在左或上时为负）。其余都是它的派生量：

```
位图坐标  bmp = vsp − (vx0, vy0)
窗口坐标  mwp = vsp − rcMonitor.TopLeft
DIP       dip = mwp ÷ (dpi / 96)
裁剪      crop = (sel.X − vx0, sel.Y − vy0, sel.W, sel.H)   ← 永远在 VSP 里做
```

几条硬约束：

- `PerMonitorV2` 写在 `app.manifest`，并在启动时 `SetProcessDpiAwarenessContext` 兜底。
  非感知进程拿到的全是系统虚拟化后的值，后面全错。
- 鼠标坐标不用 WPF 的 `e.GetPosition`（DIP，且 PerMonitorV2 下有额外换算），一律 `GetCursorPos`。
- 窗口定位用 `SetWindowPos` 给物理像素，不用 `Window.Left/Top`（DIP，会有 ±1px 抖动）。
- 剪贴板位图的 `DpiX/DpiY` 元数据写 96，不写屏幕 DPI，否则粘进 Word/微信会被二次缩放。
- 会话期间收到 `WM_DISPLAYCHANGE` 直接放弃本次会话，不中途重排窗口。

## 目录

```
src/                     应用
  Native/                P/Invoke、PixelRect、屏幕枚举
  Services/              抓帧、裁剪、剪贴板、热键、托盘、设置、日志
  Overlay/               遮罩窗口 + 会话协调器
  Views/                 热键设置窗口
tools/CoordProbe/        无界面自检（验证取数、抓帧、裁剪尺寸）
```

## 排查

```bat
:: 冒烟自检：走真实抓帧 + 建遮罩 + 关闭，1.5 秒后自动退出，不复制不落盘，结果看日志
dist\QuickSnip.exe --selftest

:: 坐标链路自检：无界面，只打印虚拟桌面矩形、每屏 DPI、裁剪尺寸
dotnet run --project tools\CoordProbe\CoordProbe.csproj
```

正常时日志里应该能看到这几行（4K@150% 单屏）：

```
frame captured: 3840x2160 dpi=96 vsp=(0,0)
OverlayWindow hook attached: rect=(0,0)-(3840,2160) 3840x2160
```

`3840×2160` 而不是 `2560×1440`，说明 PerMonitorV2 真的生效了。

## 已知限制

- **独占全屏 DirectX（全屏游戏）下热键不生效**。`WM_HOTKEY` 依赖前台窗口的消息循环，
  独占全屏时不投递。计划用 `WH_KEYBOARD_LL` 兜底。
- **BitBlt 抓不到硬件覆盖层**（部分视频播放器、部分游戏会黑屏），也抓不到 UAC 安全桌面。
  升级路径是 `Windows.Graphics.Capture`（Win10 1803+，拿到的是 DWM 合成后帧）。
- 为支持「双击 = 全屏」，**单击取消有约 500ms 延迟**（系统双击时间）。
  不想要就把 `settings.json` 里 `doubleClickFullScreen` 设为 `false`，单击立刻取消。
- 只在 Windows 10/11 x64 上测过；ARM64 没测。

## 路线图

- [x] **第 1 轮** 全局热键 + 全屏遮罩框选 + 多屏混合 DPI 坐标正确
- [ ] **第 2 轮** 标注编辑：矩形、箭头、画笔、文本、马赛克（策略模式）；撤销/重做；`Ctrl+S` 存 PNG
- [ ] **第 3 轮** 贴图：置顶窗口、可拖动缩放、无边框不占任务栏
- [ ] **第 4 轮** OCR：优先 `Windows.Media.Ocr`（系统原生，中文精度最好），后台线程，失败给可见提示
- [ ] **第 5 轮** 滚动截图（低优先级）：UI Automation `ScrollPattern` 驱动 + 行匹配拼接

## 为什么是 WPF + .NET 8

全局热键、多屏坐标、BitBlt、全屏遮罩全是 Win32 互操作，C# P/Invoke 最直接。

Electron 的问题是 `transparent:true` + `setIgnoreMouseEvents` 这组组合在 Windows 上历来不稳，
且 Chromium 的 `deviceScaleFactor` 是 per-window 的，混合 DPI 下得在 JS 里再实现一遍换算，
任何一次误差都表现为「框和结果差几像素」。Tauri 更绕：热键、坐标、BitBlt 全在 Rust 侧，
UI 在 WebView2 里，框选矩形要跨 IPC 往返。体积优势不值得付这些代价。

## 许可

[MIT](LICENSE)
