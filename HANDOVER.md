# ScreenPen 现状与待办（交接文档）

> 面向后续接手优化的 agent。本文档描述**当前代码的真实状态**、**已确认的缺陷根因**、
> **每个问题的定位坐标**，以及**验收标准**。
>
> 最后更新：本轮（**四个交互缺陷修复**：工具条加 OCR/GIF 入口、选区可移动缩放、
> GIF 结果窗口、滚轮穿透与 Esc 停止录制，见 §1.9；
> 上一轮为构建与写盘健壮性加固，见 §1.8；
> 再上一轮为文字识别 OCR + 录制 GIF 动图，见 §1.7；
> 更早为第一梯队四个功能（荧光笔 / 序号 / 箭头三样式 / 撤销重做）
> + 窗口截图 + 屏幕取色器 + 截图美化 + UI 打磨 + 高 DPI 卡片空白修复，见 §1.6 与 §5.2）
> 当前构建：`ScreenPen\dist\ScreenPen.exe`，314,880 bytes（307.5 KB）
> `sha256 856EE6F11A612E288BAD61F801D438A0D6D1E3EFA98CCD246712DD682C7E6763`
> 全套探针：`build\run_probes.ps1` → **ALL PROBES PASSED**（22/22）
> 单跑一个探针：`build\run_probes.ps1 -Only ux_fix_probe`
> 视觉诊断：`build\run_render.ps1 render_pin` → `build\shots\render\*.png`（给人眼看，不做断言）
> 跨进程验收：`verify_upgrade.py` **26/26**、`verify_hotkeys.py` **20/20**、`verify_ui.py` **81/81**、
> `verify_readme.py` **7/7**（核对 **78 键**）、`e2e_test.py` **27/27**、`check_tray.py` **6/6**、
> `verify_resize.py` **9/9**
>
> ⚠️ `verify_ui.py` 读的是 `capture_ui.py` **先前截好的 PNG**，不会自己截图。
> 只改代码不重跑 `capture_ui.py`，`verify_ui.py` 会拿旧图给你一个**毫无意义的 81/81**。
> 顺序必须是 `capture_ui.py` → `verify_ui.py`。
>
> 注：每次 csc 编译都会生成新的 MVID，所以 **sha256 每次都不同**，不要用它判断是否重新编译过；
> 用**文件体积**和**修改时间**对比 `src\` 最新文件即可。
> ⚠️ `build\build.ps1` 现在**先编译到暂存路径，成功后才替换 `dist\ScreenPen.exe`**（§1.8.2），
> 所以"编译失败留下旧 exe"这个陷阱已经被脚本堵住了。

---

## 0. 快速上手（新 agent 必读）

### 构建

```powershell
cd ScreenPen
.\build\build.ps1          # 输出 dist\ScreenPen.exe
```

- 编译器：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（**仅此一个**，无 .NET SDK、无 `dotnet`）。
- **C# 5 语法上限**（旧 csc）：禁止字符串插值 `$"..."`、表达式体成员 `=>`、`nameof`、`?.`、
  局部函数、自动属性初始化器。用 `delegate`、`string.Format`、传统属性。`var`/lambda/匿名类型/对象初始化器可用。
- 重新编译前**必须先删除**目标 exe（文件被占用会报 CS0016）。
- 源码**恰好 19 个文件**：`src\` 下的 `Shell.cs`、`Program.cs`、`Native.cs`、`Config.cs`、
  `SettingsStore.cs`、`RegionSelectForm.cs`、`PinForm.cs`、`AppContext2.cs`、`BrushCursor.cs`、
  `SettingsForm.cs`、`OverlayForm.cs`、`Beautifier.cs`、`ColorPickerForm.cs`、
  `Gif.cs`、`GifRecorder.cs`、`GifResultForm.cs`、`KeyboardHook.cs`、
  `Ocr.cs`、`OcrResultForm.cs`。**没有 `HotkeyBox.cs`**
  （`HotkeyBox` 是 `SettingsForm.cs` 内的私有嵌套类）。
  > ⚠️ `build\run_probes.ps1` 与 `build\build.ps1` 的源码列表里**已经有 `src\PinForm.cs`**；
  > 手工编译时漏掉它会在 `AppContext2.cs` 上得到 CS0246（找不到 `PinForm`）。
  > 手工编译还要带上 `src\GifResultForm.cs` 与 `src\KeyboardHook.cs`，
  > 否则 `AppContext2.cs` 会报找不到 `GifResultForm` / `KeyboardHook`。
  > ⚠️ `Gif.cs` / `GifRecorder.cs` 用了 WPF 的 `System.Windows.Media.Imaging`，
  > 手工编译**必须**加三个引用，且 `WindowsBase.dll` 在 `WPF\` 子目录、`System.Xaml.dll`
  > 在 Framework64 根目录（写反会得到 CS0006）：
  > `$fw = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319"` →
  > `\r:$fw\WPF\PresentationCore.dll`、`\r:$fw\WPF\WindowsBase.dll`、`\r:$fw\System.Xaml.dll`

### 测试

**首选：一条命令跑完全部 22 个进程内探针**（编译 + 运行 + 汇总）：

```powershell
cd ScreenPen
.\build\run_probes.ps1        # 全过 → "ALL PROBES PASSED" + exit 0
.\build\run_probes.ps1 -Only ux_fix_probe    # 只跑一个，省得在几百行里翻
```

单个探针也可以手工编译运行，模式固定为（`Program.cs` 不要一起编，它有 `Main`）：

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$fw  = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319"
$refs = '/reference:System.dll','/reference:System.Core.dll',
        '/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',
        # Gif.cs / GifRecorder.cs 需要 WPF 影像程序集（注意 WindowsBase 在 WPF\ 子目录）
        "/reference:$fw\WPF\PresentationCore.dll",
        "/reference:$fw\WPF\WindowsBase.dll",
        "/reference:$fw\System.Xaml.dll"
$srcs = 'src\Config.cs','src\SettingsStore.cs','src\Native.cs','src\OverlayForm.cs',
        'src\BrushCursor.cs','src\RegionSelectForm.cs','src\AppContext2.cs',
        'src\SettingsForm.cs','src\Shell.cs','src\PinForm.cs',
        'src\Beautifier.cs','src\ColorPickerForm.cs',
        'src\Gif.cs','src\Ocr.cs','src\GifRecorder.cs','src\OcrResultForm.cs'
& $csc /nologo /target:exe /platform:x64 /codepage:65001 `
      /out:build\obj\my_probe.exe $refs build\my_probe.cs $srcs
.\build\obj\my_probe.exe
```

跨进程的 `verify_*.py`：

```powershell
$env:PYTHONIOENCODING='utf-8'
D:\Miniconda3\envs\py312\python.exe build\<脚本>.py
```

可行性探针（**不编译程序源码**，量的是操作系统能力，失败≠程序回归）：

```powershell
.\build\run_feasibility.ps1     # 全过 → "ALL FEASIBILITY PROBES PASSED" + exit 0
```

- Python：`D:\Miniconda3\envs\py312\python.exe`（3.12.14 + Pillow + numpy）。
- 跑测试前先 `Get-Process ScreenPen | Stop-Process -Force`，否则单实例逻辑会干扰。
- ⚠️ `run_probes.ps1` 里的 FAIL 扫描**必须带 `-CaseSensitive`**。每个探针都会打印小写的
  `0 failed` 汇总行，大小写不敏感地匹配 `FAILED` 会把**全部**探针误判为失败。
- 探针完整输出留在 `build\obj\<probe>.log`，失败时会打印 `[FAIL]` 行并指向该日志。

### 测试卫生（**硬性规则**）

1. **自动化探针绝对不能弹出真实模态对话框**。`MessageBox` 窗口不在
   `Application.OpenForms` 里，无法被 WinForms `Timer` 关闭，也无法可靠地
   `EnumWindows` 掉——一旦弹出来就会留在用户桌面上（本项目真实发生过，用户看到了残留弹窗）。
   任何会 `MessageBox.Show` 的代码路径，必须走 `SettingsForm.PromptOverride` 测试缝。
2. **任何修改用户配置的脚本必须在 `finally` 里还原，并读回校验**。
   本项目真实发生过：一个诊断脚本改了 `PenKey`/`ZoomInKey` 且没有还原，
   而 `verify_hotkeys.py` 把磁盘上"当时的"内容当作 original 快照，
   于是**忠实地把污染状态保存了下来**。现在 `verify_hotkeys.py` 会读回断言。
3. 跨进程读控件文本**必须用 `SendMessageW(hwnd, WM_GETTEXT=0x000D, ...)`**，
   `GetWindowTextW` 返回的是内核缓存（对 WinForms TextBox 是过期的）。
4. **`SendMessage(BM_CLICK)` 会阻塞到点击处理函数返回**。如果该处理函数打开模态框，
   探针永远看不到弹窗，且会一直挂住。要用 `PostMessageW`。
5. **DPI 陷阱（本项目最容易误诊的地方，务必读完）**：
   - 本机是 **2880×1800 @ 192 DPI（200% 缩放）**。任何测量/移动窗口的脚本
     必须先 `SetProcessDpiAwarenessContext(-4)`。
   - **`pwsh` / 普通进程是 DPI-unaware 的**，`GetWindowRect` 返回的是
     **被虚拟化后的 96 DPI 坐标（真实值 ÷ 2）**。
     真实发生过：从 `pwsh` 里查 `dist\ScreenPen.exe` 得到 `1183×852`，
     一度被误判为"DPI 修复没生效"，实际窗口是 **2366×1704**。
     用 DPI-aware 线程（或进程内 `Control.Width`）复核才拿到真值。
   - 判据：先打印 `GetDpiForWindow(hwnd)` 和 `GetSystemMetrics(0)`。
     若 `SM_CXSCREEN` 返回 1440 而实际屏幕是 2880，说明**当前线程 unaware**，
     所有坐标都不可信。
6. **跨进程无法向托管 WinForms `TextBox` 注入按键**。`keybd_event`、`PostMessage`、
   跨进程 `SendMessage` 全都无效——即使用 ALT 技巧把 `GetForegroundWindow` 和
   `GetGUIThreadInfo.hwndFocus` 都弄成目标输入框，按键依然被忽略。
   - 想验证**按键捕获**：只能用**进程内**探针（`build\hotkey_record_probe.cs`）。
   - 想验证**保存链路**：用 `WM_SETTEXT`（0x000C）写值 + `BM_CLICK`（0x00F5）点保存，
     这两个是系统消息，能跨进程。`verify_hotkeys.py` / `verify_upgrade.py` 都这么做。
7. **测试会受到桌面前台状态影响**。`OverlayForm` 抢不到前台时，文字编辑器会被自己的
   `LostFocus` 立刻提交丢弃，于是 `tools_probe` 的文字三项全红——**看起来像产品缺陷，
   其实是环境依赖**。本轮实测：残留的"设置"UWP 窗口占着前台就复现，前台正常就全绿。
   现在代码已用 `Native.ForceForeground()` 修复，并有 `hostile_focus_probe.cs` 专门守住这个回归。
8. **别把 `e2e_test.py` 的慢当成卡死**。它要多次启动/关闭真实进程并等对话框，约 3–5 分钟。
   判断依据是 `Get-CimInstance Win32_Process` 里脚本还在、`ScreenPen.exe` 子进程还在。
9. **探针自己起的"干扰进程"必须能被自己关掉**。Win11 的记事本是 Store 应用，
   `Process.Start("notepad.exe")` 返回的是**启动器桩**，`Kill()` 关不掉真正的窗口——
   本轮真的在用户桌面上留下了一个记事本。要占用前台，就用**自己的 exe 再起一份**
   （`hostile_focus_probe.cs` 的 `--blocker` 模式），pid 完全归自己管。

---

## 1. 已完成的内容（DONE）

### 1.1 核心工具（用户最初需求，全部完成）

| 功能 | 状态 | 位置 |
|---|---|---|
| 双击即可运行的绿色 EXE | ✅ | `dist\ScreenPen.exe` |
| 开机自动启动（HKCU Run 项） | ✅ | `AppContext2.cs` + `SettingsForm.cs` |
| 可自定义快捷键（**19 个输入框** = 6 全局 + 8 工具 + 5 其它） | ✅ | `SettingsForm.cs`（`AddHotkeyRow` ×6 建前两组，`AddToolKeyRow` ×13 建后 8+5 行，两者都转调 `AddKeyRow`） |
| 笔宽 `[` / `]` 调节 | ✅ | `OverlayForm.AdjustPenWidth` |
| 颜色切换（1–8 / 调色板） | ✅ | `OverlayForm.SelectColor` |
| 工具切换 `P/L/R/O/A` | ✅ | `Config.ToolForKey` |
| `E` 清空、`Ctrl+Z` 撤销 | ✅ | `AppContext2.OverlayKeyDown` |
| 实时缩放（滚轮缩放 + 边缘平移） | ✅ | `OverlayForm` |
| 区域截图 + 保存位置对话框 | ✅ | `RegionSelectForm.cs` |
| 可视化设置页面（workbuddy2api 风格） | ✅ | `SettingsForm.cs` |
| 图标取自 `image\` | ✅ | `Shell.cs` / `IconLibrary` |

### 1.2 第二轮升级（全部完成，有测试）

| 功能 | 状态 | 测试 |
|---|---|---|
| 区域截图（框选，非全屏） | ✅ | `region_probe.cs` 17/17 |
| 截图复制到剪贴板 | ✅ | `region_probe.cs` |
| 文字标注（T） | ✅ | `tools_probe.cs` |
| 马赛克（M） | ✅ | `tools_probe.cs` |
| 聚光灯（K） | ✅ | `tools_probe.cs` |
| 画笔光标显示 `画笔.svg` | ✅ | `verify_brush.py` 17/17 |
| Ctrl=矩形 / Tab=椭圆 / Shift=直线 / Ctrl+Shift=箭头 | ✅ | `tools_probe.cs` 31 checks |
| 滚轮滚动文档 + 标注跟随（F 模式） | ✅ **已修复，见 §2.4** | `follow_probe.cs` 24/24 |

### 1.3 快捷键冲突 UX 修复（已完成）

**用户报告的问题**：设置里把「画笔」也设成 `E`（与「清空标注」冲突）后，
保存被**硬性拒绝**，并且**丢弃了全部 49 项设置**，而实时读数是 `✓ 单键可用`，
用户直到点保存才知道有问题。

**已修复**：

1. 新增 `_bindings` 列表 + `_readouts` 字典 + `_lastTouched`，
   `RefreshReadouts()` 实时逐行比对，重复项显示
   `⚠ 与其它项重复`（用户刚改的那一行）/ `✗ 已被占用`（另一行）。
2. 新增 `RefreshStatusLine()`，底部状态栏显示
   `有 N 个快捷键重复，重复的键只有其中一项能用。`
3. `OnSave()` 重写：不再硬编码 19 项 `pairs` 数组，改为遍历 `_bindings`。
   **重复键 → 弹确认框 → 用户点"是"则正常保存**；
   真正损坏的项（无法解析 / 全局键为空 / 全局键是裸字母）仍然阻止保存。
4. **一组冲突不再丢弃其余 48 项编辑**。
5. 新增测试缝 `public Func<string,string,MessageBoxButtons,DialogResult> PromptOverride`
   + `Prompt(...)`，所有 `MessageBox.Show` 都改走它，探针不再弹真实对话框。

**验证**：
- `hotkey_conflict_probe.cs` — **29/29 PASS**（进程内驱动真实 `SettingsForm`）。
- `verify_hotkeys.py` — **20/20 PASS**（跨进程端到端，含读回校验还原）。

### 1.4 第三轮升级：UI 遮挡 / 画笔平滑 / F 跟随（已完成）

用户反馈的三条缺陷**全部修复**。最关键的一条是 §1.4.1 的 DPI 根因——
它才是"文字被遮挡"反复复发的真正原因，前两轮的修法都只是局部缓解。

#### 1.4.1 【UI】DPI 缩放（**根本原因**）

**真正的根因**：本机是 **192 DPI（200%）**。`SettingsForm` 刻意用
`AutoScaleMode.None` + 96 DPI 手工像素 `Bounds`。但 WinForms 把**磅值换算成像素时用的是
显示器 DPI**，所以 9pt 字在 192 DPI 下实测 **31px 高**，而按钮却仍是 96 DPI 时代的 **30–32px**。
**字比框还高 → 从中间被裁掉**。这就是用户反复报的"文字被遮挡"。

**为什么不能交给 `AutoScaleMode`**（实测结论，别再试了）：
在 192 DPI 下把 `AutoScaleMode` 设成 `Dpi` 或 `Font`，`CurrentAutoScaleDimensions`
虽然报告 `{192,192}` / `{12,24}`，但 `ClientSize`、控件 `Bounds`、字体大小**全都不变**
（`AutoScaleMode.None` 抑制了自动缩放）。**显式缩放是唯一可行路径。**

**最终解法**（`src\SettingsForm.cs`）：

| 成员 | 作用 |
|---|---|
| `ApplyDpiScale()` | 构造函数**最后一步**调用；`s <= 1.01f` 直接返回（96 DPI 机器不受影响） |
| `SystemDpiScale()` | `Graphics.FromHwnd(IntPtr.Zero).DpiX / 96f`，失败返回 `1f` |
| `LayoutHeader()` | 按**实测文本高度**推导头部高度/标题 Y/副标题 Y，缩放后重跑 |
| `RefreshScrollExtents()` | 从子控件重新算 `AutoScrollMinSize` |
| `PagePadY` | 页面底部留白常量 |

`ApplyDpiScale()` 的执行顺序（**顺序本身是正确性的一部分**）：

1. **先把每个页面从 `_content` 上摘下来**；
2. `Scale(new SizeF(s, s))` —— 只缩放窗体自身（含导航栏、页脚、头部）；
3. 对每个页面单独 `p.Scale(new SizeF(s, s))`；
4. `ShowPage(_currentPage)` 重新挂回；
5. `LayoutHeader()` → `LayoutFooter()` → `RefreshScrollExtents()`；
6. 若窗口超出工作区，夹到 `WorkingArea` 并重跑 `LayoutFooter()`。

> ⚠️ **为什么必须先摘页面**：`Control.Scale()` **只遍历已挂到父控件上的子树**。
> `ShowPage` 故意把非当前页从 `_content` 移除（多个 `Dock=Fill` 兄弟页会互相覆盖），
> 所以这些页面**不会**被 `Scale()` 波及。真实踩坑：只缩放窗体时，
> `画笔` 页的卡片仍是 `780px` 宽，躺在一个 `1880px` 宽的页面里。
> 必须**先全部摘掉 → 缩放窗体 → 再逐个手工缩放 → 最后挂回**，
> 否则当前显示的那一页会被**缩放两次**。

**关键性质**：`Control.Scale()` 会缩放 `Bounds`/`MinimumSize`/`MaximumSize`/`Padding`/`Margin`
并递归子控件，但**绝不改 `Font`**。这正是它在这里安全的原因——
字体已经按显示器 DPI 渲染成正确的物理尺寸了，只需要把**框**撑大。

#### 1.4.2 【UI】导航栏截断 + 页面滚动

- 导航按钮**去掉 emoji 前缀**（emoji 的 fallback 字体 advance width 被 GDI+ 严重高估，
  是截断元凶），改为纯文字 + 自绘 3px 强调条（`NavButtonPaint`，
  选中时画在 `(0, 6, 3, Height-12)`）。
- 按钮 `Bounds = (10, 44 + index*46, 192, 40)`，`Padding = (22,0,0,0)`，字体 `10f`。
- 导航栏 `Width` 190 → **212**。
- `NewPage()` 打开 `AutoScroll = true`；`FinishPage()` 设 `page.AutoScrollMinSize = new Size(0, y)`；
  外层 `_content` 保持 `AutoScroll = false`（`Dock=Fill` 的页面自己滚）。
  > WinForms 细节：`Dock=Fill` 的面板必须用 **`AutoScrollMinSize`**（不是 `Size`）定义滚动范围。

#### 1.4.3 【画笔】快速画圆的折线

- **根因**：`SmoothLines` 之前**只切换抗锯齿**（`SmoothingMode`），
  完全没有平滑几何；`g.DrawLines` 把采样点直线相连，鼠标快移时采样间隔几十像素，圆就变多边形。
- **修复**（`src\OverlayForm.cs`）：
  - `static void DrawPolyline(Graphics, Pen, PointF[], bool smooth)`：
    `pts.Length >= 3` 时用 `g.DrawCurve(pen, pts, SmoothTension)`（Cardinal spline，`SmoothTension = 0.5f`）。
  - `static List<PointF> Densify(List<PointF> src, float maxStep)`：Catmull-Rom 插值补点。
  - `DrawStroke` 里**仅对 `Tool.Pen`** 在**渲染时**调用 `Densify(..., 3f)`。
  - ⚠️ **只在渲染时加密，绝不写回 `s.Points`**。否则 `tools_probe.cs` 的点数断言会挂
    （`Rectangle` 必须恰好 2 点、`Pen` 保持原始采样点数）。
  - ⚠️ `DrawCurve` 对 `pts.Length < 3` 会抛异常，必须保留 1 点 / 2 点分支。
  - ⚠️ Catmull-Rom 的**弦比弧短**，按 `maxStep` 算出的步数会偏少。
    实现里有**步数翻倍重试**（最多 8 次，上限 64），实测把 51.76px 的最大间隙压到 2.97px。

#### 1.4.4 【跟随模式】F 键的 bug

- **根因**：`F` 只切换 `_scrollFollow`，但**滚轮是否穿透给下层文档**却取决于另一个无关设置
  `WheelScrollsDocument`（默认 **false**）。于是 F 提示"已开启跟随滚动"，
  滚轮却仍在改笔宽 → 文档根本不滚 → 什么都跟不上。
- **修复**：`bool passToDocument = _cfg.WheelScrollsDocument || _scrollFollow;`
- 附带修好两点：
  - `_followMisses` 之前**只增不读** → 现在连续 `FollowIdleFrames = 20` 帧无位移就自动退出
    并触发 `ScrollFollowIdled` 事件（避免空转时每 140ms 一次全屏 `CaptureGray` 白吃 CPU）。
  - **被拒绝的切换**之前会谎报"已关闭跟随滚动" → 现在 `AppContext2` 先读
    `FollowUnavailableReason()`，据此提示"无法开启跟随滚动"+原因（实时缩放模式下不可用 /
    设置里已关闭 / 光标下没有可滚动的窗口）。
  - `ResumeAfterCapture()` 之前会重新开启跟随，与 `ActivateOverlay` 的关闭相矛盾 → 改为无条件 `StopScrollFollow()`。

**验证**：
- `layout_probe.cs` — **4/4 PASS**，0 个被裁的 Label，所有溢出页面都能滚到。
- `measure_ui.cs` — **ALL PASS**，实测窗口 `2360×1653`、卡片 `1560px`（= 780×2）。
- `smooth_probe.cs` — **14/14 PASS**，折线 3.00px → 样条 1.00px。
- `follow_probe.cs` — **24/24 PASS**。
- `footer_probe.cs` — **12/12 PASS**。
- `render_ui.cs` 逐页渲染 PNG 人工确认：`build\shots\render\`。

### 1.5 第四轮升级：贴图 / 设置窗口可调大小 / 输入框抢键 / 安全加固（已完成）

用户本轮提了四条：**① 设置面板不可以手动调整大小；② 按 `T` 打字时敲到 `L`/`P`/`O`/`E`/`A`
就无法继续输入；③ 截图可以钉在桌面上；④ 按审计报告做安全与稳定性加固**。
四条全部完成，下面按条记录根因、改法与证据。

#### 1.5.1 【新功能】贴图：把截图钉在桌面（`src\PinForm.cs`，**新文件**）

**入口**（三条，都复用同一个框选界面）：

| 入口 | 代码位置 |
|---|---|
| 托盘菜单 **区域截图并钉在桌面** | `AppContext2.BuildTray()`（`RegionAction.PinToDesktop`） |
| 框选时按裸键 `P` | `RegionSelectForm.OnKeyDown` |
| 框选时点工具条 **贴图 (P)** / 在选区内双击（当默认动作是贴图时） | `RegionSelectForm.OnMouseDown` / `OnMouseDoubleClick` |

**`RegionAction` 枚举新增成员**（`src\RegionSelectForm.cs` L10–17）：

```csharp
public enum RegionAction
{
    Cancelled = 0,
    SaveToFile = 1,
    CopyToClipboard = 2,
    /// <summary>把选区作为一张置顶贴图钉在桌面上（Snipaste 风格）。</summary>
    PinToDesktop = 3
}
```

> ⚠️ **`0/1/2` 的数值必须保持稳定**：这三个值既进过探针断言，也是
> `RegionAction.Cancelled` 作为"默认值 / 归一化目标"的语义基础
> （`_defaultAction = defaultAction == RegionAction.Cancelled ? SaveToFile : defaultAction`，
> 以及 `Finish()` 里 `if (action != Cancelled && (!_hasSelection || Width < 1)) action = Cancelled;`）。
> 新动作只能**追加**到末尾，不要重排、不要插队。

**为什么裸 `P` 放在 Ctrl 分支之后**（`OnKeyDown`，注释原文在代码里）：
选择器自己只占用 `Esc` / `Enter` / `Ctrl+C` / `Ctrl+A` / 方向键，裸 `P` 是空的；
但把它写在 `if (e.Control)` 之前就会**永久遮蔽未来的 `Ctrl+P`**。现在的顺序是
`Esc → Enter → Ctrl+C → 裸 P（且要求 !Control && !Alt）→ 方向键 → Ctrl+A`。

**`PinForm` 的设计约束（改这个文件前务必读完）**：

| 约束 | 说明 |
|---|---|
| **窗口是独立顶层窗口**，不属于任何父窗口 | `FormBorderStyle.None` + `ShowInTaskbar = false` + `TopMost = true` + `KeyPreview = true`（`Esc`/`Ctrl+C` 必须由窗体自己收到）。构造完立刻 `Activate()`（`OnShown`），无边框窗口 `Show()` 后未必拿到焦点 |
| **缩放只重算 `ClientSize`，绝不用 `Form.Scale`** | `ScaledSizeFor(scale, workArea)` 每次都用**原始** `_baseSize` 重算（`_baseSize * _scale`），所以缩放是精确、可逆、不累积误差的。`Form.Scale` 会把比例累乘到窗体和子控件上，重复调用因取整逐次漂移（与 §1.4.1 的 `Scale()` 不幂等是同一个坑） |
| **拖动用光标屏幕坐标增量，不用 `e.Location`** | 窗口自己在动，用相对按下点的差值会中途归零 → 拖动卡死。只有 `DragBy(dx,dy)` 一个移动实现（`dx==0 && dy==0` 直接返回） |
| **滚轮等比缩放** | `factor = Math.Pow(1.12, e.Delta / 120.0)`，锚点是**光标下的图像比例保持不变**（贴边被夹回工作区时略有偏差，可接受） |
| **比例夹在 `[MinScale=0.1, MaxScale=8.0]`，窗口夹在工作区** | `ClampLocation` 保证**至少 40px（`MinVisible`）留在屏幕内**，拖不丢；`ApplyScale` 里比例未变（`< 0.0005f`）直接早退，不做无谓重排 |
| **关闭按钮随窗口缩小** | `CloseSize = 22` / `CloseMargin = 2`，`CloseButtonBounds` 在小窗口里向内收缩；探针断言它**永不超出缩小的窗口**。只在鼠标悬停（`_mouseInside`）时绘制，拖动中被捕获离开窗体不算移出（否则按钮会闪） |
| **右键直接关闭**，且必须放在左键拖动分支之前 | 否则会被拖动分支吃掉 |
| **`RoundedRect` 是本地实现，故意不复用 `OverlayForm.RoundRect`** | `PinForm` 要能被单文件探针独立编译引用，不多引一层依赖 |
| **不做模态对话框** | `SaveFileDialog`（用户主动发起）可以模态；`Ctrl+C` 失败**只用 `ToolTip` 提示 1800ms**，绝不 `MessageBox`（无边框窗口弹模态会把整个程序卡住，也违反 §0 测试卫生） |
| **`PinForm` 拥有位图** | 见下面的所有权契约 |
| **测试缝** | `TestScale` / `TestBaseSize` / `TestImage`（**探针只读，不得释放**）/ `TestIsClosed` / `IsClosed` / `TestSetScale` / `TestWheelZoom` / `TestDragBy` |

**位图所有权契约（最容易写出双重释放的地方）**：

1. 构造函数拿走所有权：`PinForm(Bitmap image, Rectangle screenRect)`，`Dispose` 时释放 `_image`。
2. 调用方（`AppContext2.PinScreenshot`，L688）在把位图交出去之后**立刻把本地引用置 `null`**：
   ```csharp
   Bitmap toPin = cropped;
   cropped = null;          // 交出去之前先清本地
   PinScreenshot(toPin, region);
   ```
   否则外层 `finally { cropped.Dispose(); }` 会释放**贴图正在显示的那张位图**。
3. `PinScreenshot` 里再把本地也置 `null`（`bmp = null`），失败路径**恰好释放一次**：
   构造函数抛异常 → `bmp.Dispose()`；`Show()` 抛异常 → 释放窗体（窗体释放位图）。
4. `PinForm.Dispose(bool)` **只在 `disposing == true` 时**释放 `_image`，因此 Dispose 是
   唯一的释放点：若构造函数中途抛异常，所有权仍在调用方手上，而 `Component` 的终结器会用
   `disposing == false` 再走一遍本方法——那时释放就会和调用方**重复释放同一张位图**。
   重复调用 `Dispose()` 也安全（第二遍 `_image` 已是 `null`）。
5. 构造函数接受 `image == null`（探针会这么用），此时 `_baseSize` 回退 `240×160`，
   **绝不能把空引用异常抛出构造函数之外**。
6. `ApplyScale` / `OnPaint` / `OnMouseDown` 里的一切窗口操作都包在 `try/catch` 里，
   异常不得抛进输入事件处理链。

**所有贴图必须随程序退出一起关闭**（`AppContext2.CloseAllPins()`，L758）：

- `Quit()`（L980）与 `Dispose(bool)` 都调用。**没有这一步，贴图会在托盘进程退出后继续浮在桌面上。**
- 遍历的是 `_pins.ToArray()` 快照而不是列表本身：`Close()` 会泵消息、可能重入，
  **枚举中改列表是硬崩溃**。
- 逐个 `FormClosed -= PinFormClosed` → `Close()` → `Dispose()`，每步各自 `try/catch`。
- `PinFormClosed`（L739）负责从 `_pins` 移除并释放窗口。

#### 1.5.2 【BUG】按 `T` 打字时，单键命令抢走按键（真实产品 BUG）

**用户报告**（原文）：*"我按下 T，进行打字的时候，按到其他的有特殊意义的按键会触发，
比如：L，P，O，E，A 等按键，然后打字就无法输入了"*。

**根因**：`OverlayForm` 设了 `KeyPreview = true`，所以 **overlay 的 `KeyDown` 先于聚焦的
`TextBox` 执行**。`AppContext2.OverlayKeyDown`（L314 起）里的单键分支各自 `e.Handled = true`，
字符**根本没送到输入框**：

| 按键 | 被抢走的动作 |
|---|---|
| `L` | 切直线工具 |
| `P` | 切画笔 |
| `O` | 切椭圆 |
| `A` | 切箭头 |
| `E` | 清空全部标注 |
| `B` | 循环颜色 |
| `1`–`8` | 直接选调色板颜色 |

**修法**：在 `OverlayKeyDown` 的**最前面**（`Keys code = e.KeyCode;` 之后、`Esc` 分支之前）加一道闸：

```csharp
// While the inline text editor is open, every key belongs to it.
// ...
// Returning without touching e.Handled hands the key straight back to
// the editor. Esc and Enter still work: TextEditorKeyDown handles
// both (Esc discards this label, Enter commits it).
if (_overlay.IsEditingText) return;      // AppContext2.cs L331
```

- `OverlayForm.IsEditingText`（L2020）= `_textEditor != null`。**不设 `e.Handled`** 是关键：
  WinForms 会继续把该键派发给聚焦控件。
- `Esc` / `Enter` 不受影响：它们由 `TextEditorKeyDown` 自己处理（`Esc` 丢弃本次标注，
  `Enter` 提交）。
- **`Tab` 有第二条路径**：`Form` 在**消息层**（早于 `IsInputKey`）就把 `Tab` 当导航键处理，
  所以 `OverlayForm.ProcessCmdKey`（L469）里也要放行：
  ```csharp
  if ((keyData & Keys.KeyCode) == Keys.Tab)
  {
      if (_textEditor != null) return base.ProcessCmdKey(ref msg, keyData);
      SetTabHeld(true);
      return true;      // 非打字状态：吞掉，绝不让 Tab 把焦点移出 overlay
  }
  ```
  不放行的后果不是"打不出字"，而是 **`_tabHeld` 卡在 `true`**——提交文字后的下一次拖动
  会**悄悄变成椭圆**。

**验证**：

- `build\typing_probe.cs`（**新增**）—— 命令键清单
  `Keys.L, P, O, E, A, B, D1, M, R, T, F, D0`；断言打字时**没有任何命令键被吞**、
  工具未变、颜色未变、没有产生笔画、`Tab` 不会把 `_tabHeld` 闩住，
  并用真实 `WM_CHAR` 把 `lpoeab1` 送进编辑器、断言 `CommitTextEditing(true)` 后标注还在。
  → 日志 `build\obj\typing_probe.log`：**19 `[PASS]`，0 `[FAIL]`**。
- 顺带加严的邻近约束：`OverlayForm.OnMouseMove`（L1757–1778）里 `if (_textEditor != null) return;`
  —— **打字时绝不干扰绘图状态**。

#### 1.5.3 【UI】设置窗口现在可以手动调整大小

**用户报告**（原文）：*"设置面板不可以自己手动调整大小"*。

**改法**（`src\SettingsForm.cs`）：

| 位置 | 变更 |
|---|---|
| L126 | `FormBorderStyle = FormBorderStyle.FixedSingle` → **`Sizable`** |
| L127–128 | `MaximizeBox = true`（原来随 `FixedSingle` 无意义）、`MinimizeBox = true` |
| L197 `ApplyMinimumSize()` | **新增**。`MinClientWidth = 560` / `MinClientHeight = 460` 是**作者在 96 DPI 下写的最小客户区**，经 `ScaleSize()` 换算到当前 DPI，再各自加上**非客户区边框**（`Width - ClientSize.Width` / `Height - ClientSize.Height`）才是 `MinimumSize` |
| L210 `ScaleSize(Size)` | `_dpiScale <= 1.01f` 时原样返回（96 DPI 机器不受影响） |
| L235 `OnResize` | `if (!_ready) return; LayoutFooter(); RefreshScrollExtents();` |

**三条硬约束（都写进了代码注释，别"顺手优化"）**：

1. **`MinimumSize` 必须在 `ClientSize` 之后设置**（`ApplyMinimumSize()` 是构造函数最后一步，
   L163）。`MinimumSize` 会**夹住 `Size`**，提前设会把刚排好的（可能已按 DPI 放大的）
   布局压小。
2. **`AutoScaleMode.None` 是刻意保留的**（L134）：所有控件都用显式像素 `Bounds`，
   `Font` 模式的自动缩放会把它们**再缩放一次**（整体变小 + 中文被裁）。
3. **窗口**刻意**不 reflow**：拉大只多出背景，卡片列、头部、每个测量过的子控件都保持
   DPI 那一次给出的尺寸。真正随宽度变化的只有两样东西：**锚定的页脚按钮**（`LayoutFooter()`）
   和**每页的滚动范围**（`RefreshScrollExtents()`）。
   > ⚠️ **`OnResize` 里绝对不能重新调用 `Scale()` / `ApplyDpiScale()`**：
   > `Scale()` **不幂等**（重复调用会再乘一次比例），而且卡片宽度是被断言过等于
   > `780 × DPI 因子` 的（`measure_ui.cs`）。

**验证**：`build\verify_resize.py`（**新增**，跨进程，9 项）——直接对 `dist\ScreenPen.exe`
的真实窗口断言：样式含 `WS_THICKFRAME`；样式含 `WS_MAXIMIZEBOX`；拉大后客户区确实变大；
往小里拖被下限夹住；**放大后**页脚三个按钮仍在窗口内、**最小时**仍在窗口内、且没有按钮
溢出窗口；把窗口调回设计尺寸后能恢复（±2px）。
→ 前置参考图：`build\featshots\20_settings_min.png` / `21_settings_wide.png`
（**不要**输出到 `build\shots\`：`verify_ui.py` 会把该目录下所有 PNG 两两做尺寸比对，
尺寸不同就直接判 FAIL，实测因此产生 15 条假失败）。

#### 1.5.4 安全 / 稳定性加固（审计 12 项）

下面 12 条是审计报告的全部整改项。**行号会随代码变动，所以每条同时给出方法名——
以方法名为准定位，行号只是当轮快照。**

| # | 问题 | 位置（方法名 + 当轮行号） | 修法 |
|---|---|---|---|
| 1 | **画笔笔画点数无上限**：一条不断的笔画以 100–200 采样/秒累积，画几分钟后内存与每帧开销无界 | `OverlayForm`：`MaxStrokePoints` 常量 L40；`OnMouseMove` L1757–1778 | 点数上限 `20000`，到顶后**停止收录新点**（现有几何保持不变） |
| 2 | **同一位置的重复采样**：鼠标移动事件远比指针实际位移密，慢画时攒下大量近乎重合的点，只增加内存与每帧 densify 开销 | `OverlayForm.MinSampleDist` L54；`OnMouseMove` L1757–1778 | 与上一点距离 `< 1.5f` 的采样**丢弃**。存下来的仍是**原始鼠标采样**（从不插值），且 1.5px 远低于可见抖动；所有测试钩子每步 ≥ 10px，**探针点数断言不变** |
| 3 | **每帧重复 densify**：`DrawStroke` 每帧为每条平滑笔画重建 `Densify(new List<PointF>(pts), 3f)`，静止的笔画一秒被细分 60+ 次 | `OverlayForm.DensifiedFor()` L99 + `DenseEntry` L72 + `_denseCache` L79 + `InvalidateDensifyCache()` L131 | 按 **`Stroke` 引用**（`Stroke` 是普通 sealed 类，无 `Equals`/`GetHashCode` 重写，默认引用相等正是"同一笔画"该有的语义）+ **点数** + **缩放**做缓存。缓存放在**屏幕坐标**（`Densify` 只看点差，纯平移不会失效）。`InvalidateDensifyCache()` 在几何变化处全部调用：`ShiftAnnotations` L950、`Undo` L2051、`Clear` L2061、`ClearForTest` L2157、`Dispose` L2295。**缓存只影响渲染，绝不写回 `s.Points`**（§1.4.3 的老规矩） |
| 4 | **换颜色泄漏 GDI 句柄**：每次改色都泄漏 1 个 HICON + 2 个 DIB，撑到 10000 句柄上限就画不出东西 | `OverlayForm.ReleaseBrushPointer()` L263 + `ApplyCursorForMode()` L273 | 装新光标前**先摘再放**（`Cursor = Cursors.Default` 之后再 `DestroyIcon`），然后装新的 |
| 5 | **图标加载泄漏 HICON**：`Icon.FromHandle` 用完不销毁 | `Shell.IconFromBytes()` L155（`DestroyIcon` 在 L171–172 的 `finally`）、`Shell.GenerateFallback()` L197（同上 L216–217） | `(Icon)Icon.FromHandle(h).Clone()` + `finally { DestroyIcon(h); }` |
| 6 | **托盘提示的复制路径泄漏位图**：`CopyToClipboard` 每次调用泄漏一个 32bpp 暂存位图（本机 `2860×1800` 约 **20.6 MB/次**） | `AppContext2.CopyToClipboard()` L885，暂存位图在 L902 | 暂存位图用 `using` 作用域包起来；`SafeSetClipboardImage` 的 `CF_BITMAP` 路径**复制像素**，所以随即释放是正确的 |
| 7 | **框选截图的马赛克/聚光灯位图可能不释放**：`DrawMosaic` 里中途抛异常就漏掉一个全屏位图 | `OverlayForm.DrawMosaic`：`Bitmap raw` 上提到 L1437，`finally` 在 L1470 | `raw` 提升到 `try` 之外，`finally` 里无条件 `Dispose` 并置 `null`（`tile` 在 `catch` 里释放） |
| 8 | **配置写入非原子**：旧实现先 `Delete` 再 `Move`，两步之间崩溃/断电就**一份配置都不剩**，下次启动静默回到出厂值 | `SettingsStore.Save()` L96，`File.Replace` / `File.Move` 在 L182–183 | 先写 `path + ".tmp"`，再 `File.Replace(tmp, path, null)`（无文件时 `File.Move`）。传 `null` 备份是**故意**的：不在目录里留 `.bak` 垃圾。失败时删掉 tmp 并返回 `false`，**从不抛异常** |
| 9 | **保存失败静默**：装目录只读 / 磁盘满时，用户以为存上了，重启才发现全丢 | `AppContext2.OpenSettings()` L927，`bool saved = SettingsStore.Save(_cfg)` 在 L951 | 保存返回 `bool`；`false` 时气球提示 **"设置保存失败"** + *"配置未能写入磁盘，本次改动仅对当前运行有效。"* |
| 10 | **热键注册失败静默**：某个全局热键被别的软件占用时，用户只看到"按了没反应" | `AppContext2.ReportHotkeyProblems()` L77、`TryRegister()` L170、`_failedHotkeys` L38/184、检查点 L964–968 | `TryRegister` 把失败的 `显示名 = 键` 记进 `_failedHotkeys`；启动与打开设置时据此提示（注释原文："Registration failures used to be silent"） |
| 11 | **截图格式未校验就拼进文件名**：`"png/../../x"` 之类会让 `Path.Combine` 归一化到截图目录**之外** | `Config.Clamp()` L215，格式白名单 L227–233 | 去空白、去前导 `.`、转小写；`jpeg → jpg`；**只允许 `png` / `jpg` / `bmp`**，其它一律回退 `png` |
| 12 | **崩溃提示被置顶窗口挡住**：overlay/贴图是 `TopMost`，`MessageBox` 弹在它们**后面**，用户只看到程序"卡住" | `Program.ReportCrash()` L83，取消置顶 L107，`MessageBoxOptions.DefaultDesktopOnly` L122 | 写 `.error.log` → 遍历 `Application.OpenForms` 逐个 `TopMost = false` → 再用 `DefaultDesktopOnly` 弹框 |

> 另有两处顺带收紧、可视为上述条目的配套：
> - `OverlayForm.StartScrollFollow()` L645：`SetExcludedFromCapture` 的返回值**不再忽略**，
>   失败时置 `_followRefusal = "系统不支持抓屏排除，跟随滚动可能不准"`。
> - `Config.Clamp()` L244–256：单键字段为空白时从 `new Settings()`（出厂默认）**补回**该键，
>   而不是静默让那个工具不可达（白名单外的非法赋值同样被挡在 L227–233）。

**验证**：`build\run_probes.ps1` 全绿（**15/15**）。与本节直接相关的三个新探针：
`pin_probe.cs` → `build\obj\pin_probe.log` **57 `[PASS]`，0 `[FAIL]`**；
`typing_probe.cs` → `build\obj\typing_probe.log` **19 `[PASS]`，0 `[FAIL]`**；
`measure_toolbar.cs` → `build\obj\measure_toolbar.log` **ALL TOOLBAR MEASUREMENTS PASSED**
（这条是工具条标签被裁切的回归防线，见 §1.5.5）。
`pin_probe.cs` 覆盖：选择器工具条 4 个按钮 + `PinToDesktop` 裁切；`PinForm` 几何
（1.0×/2.0×、下限夹取、上限夹取、40 次往返无漂移、每格 1.12×、光标锚点缩放偏移为 `(0,0)`、
拖动增量、零距离拖动、关闭按钮尺寸/位于窗内/永不超出缩小后的窗口、`Esc` 关闭、关闭释放位图）；
位图所有权（打开期间存活、`Dispose` 清空引用、释放调用方位图、幂等、`null` 图不抛、
`null` 图尺寸为 `{240,160}`、干净释放）；多张贴图互不影响；控制器集成
（新控制器 0 张贴图、`PinScreenshot` 被跟踪、移交后的位图存活、贴图拥有位图、始终置顶）。

> ⚠️ 探针断言条数以 `build\obj\*.log` 里的 `[PASS]` 行数为准（`pin_probe` 57 条、
> `typing_probe` 19 条）。**不要**把这里的数字当成固定不变量——加断言时它就会变。

#### 1.5.5 【BUG】框选工具条在高 DPI 下把按钮标签裁掉（验证阶段发现的真实缺陷）

**不是**用户报的四条之一，是给上面四条做可视化验证时**新发现的既有缺陷**，
而且是**从加第 4 个按钮（贴图）之前就存在**的——第 4 个按钮只是让它更明显。

**症状**（本机 200% 缩放，即 192 DPI）：框选时工具条上四个按钮只显示
`保存` / `复制 (` / `贴图 (` / `取消 (E`——除裸 `保存` 外**全部被截断**。

**根因**：`RegionSelectForm.LayoutToolbar()` 用的是**硬编码像素常量**
`const int BtnW = 84, BtnH = 32, BtnPad = 8;`，而字体是**固定点数**
`new Font("Microsoft YaHei UI", 8.6f, ...)`。点数会随 DPI 放大，像素常量不会，
于是 192 DPI 下 8.6pt 的中文字高约 30px、`复制 (Ctrl+C)` 实measure **153px**，
塞进 84px 的按钮必然溢出。这与 `SettingsForm` 形成对比——后者有显式的
`ApplyDpiScale()` / `ScaleSize()` 路径。

**为什么老探针没抓到**：`pin_probe.cs` 与 `region_probe.cs` 只断言了
「按钮非零、互不重叠、在窗口内」——**从来没有断言过文字装得下**。
几何全对，标签全被裁掉，两者并不矛盾。

**修法**（`src\RegionSelectForm.cs`）：
- 新增 `DpiScale()`（`CreateGraphics().DpiX / 96f`，夹在 `0.1..8`），**按窗口实测 DPI**，
  不依赖进程 awareness 上下文是否设过；
- `BtnPad` / `BtnH` 乘 `DpiScale()`；
- **每个按钮宽度由自己的标签量出来**：`MeasureLabel()` =
  `TextRenderer.MeasureText(text, font, ..., TextFormatFlags.NoPadding).Width + BtnInnerPad*2`，
  下限 `BtnMinWidth = 46`；
- 三级降级：① 整行装不下 → 换**短标签**（`保存` / `复制` / `贴图` / `取消`）；
  ② 还装不下 → 按最宽按钮**等比例整体收窄**；③ 极端小窗 → 四个等宽，
  保证算术一定落在窗内；
- 绘制端 `DrawToolbarButton()` 把绘制区**裁到按钮矩形**并用
  `StringTrimming.EllipsisCharacter` + `NoWrap` 兜底，绝不外溢到邻居；
- **单一事实来源**：`LayoutToolbar()` 把量过的标签存进 `_btnLabels`，
  `DrawToolbar()` 就画这组（不再是各自调 `SaveLabel()` 各算一遍），
  绘制与测量不可能再分歧；
- 字体对象缓存到 `_fontBtn` / `_fontBtnBold`（原来每帧 `new Font` 一次），
  新增 `Dispose(bool)` 释放；
- 新增测试接缝 `ToolbarLabelsForTest` / `ToolbarShrunkForTest` / `*LabelForTest`。

**证据**：
- `build\measure_toolbar.cs`（**新增**，155 行）→ 5 种桌面宽度（2880/1920/1280/900/640）
  × 3 种默认动作 = 15 组，逐标签量 `TextRenderer.MeasureText` 并和真实按钮宽度比。
  修前 **10 FAIL**（`保存 (Enter)` 需 140px vs 84px 等）；修后
  **ALL TOOLBAR MEASUREMENTS PASSED**。它还断言「≥1280px 的桌面**永远不收缩**行」，
  这条正是本次缺陷的回归防线。
- **已加入 `build\run_probes.ps1`**，所以探针总数 14 → **15**，以后自动跑。
- 人眼确认：`build\obj\render_pin.exe` 重渲染 `build\shots\render\35_selector_2880.png`，
  裁 `(1660,615)-(2350,745)` 放大 2× —— 四个标签完整显示，
  工具条从 376×48 变成 636×96。
- `pin_probe` / `region_probe` 修改后仍然全绿。

#### 1.5.6 顺带修掉的测试假失败（`verify_upgrade.py` 马赛克检查不稳定）

`build\verify_upgrade.py` 的马赛克断言**硬编码**屏幕点 `(1300, 700)`，并在注释里写
"Measured texture at (1300,700) is 0.96"——**这个数字是错的/早已过期**。马赛克把每个
块换成平均值，所以**在平坦区域上输出与输入逐像素相同**（这是设计如此），
像素差就是 `0.00%`，于是「马赛克坏了」是假失败。实测同一坐标两次跑分别是
texture `0.023` 和 `0.052`、diff `0.00%`，**取决于当时那个位置恰好是哪个窗口**。

**修法**：新增 `pick_textured_region(grab, w, h, min_texture=0.15)`，
运行时扫 160px 网格挑最有纹理的区域；纹理不够就**明确打印跳过原因**而不是报假失败。
现在稳定拿到 texture `0.988`、diff `6.51%`，连跑两次结果一致。

### 1.6 第五轮升级：功能增强第一梯队 + 界面打磨（已完成）

需求原文：「帮我先做第一梯队，然后再帮我优化一下界面，让他看起来更好看，更顺眼，
可以参考一些其他开源的一些项目」。

依据是仓库根目录的调研报告 `ScreenPen功能增强调研报告.md`（581 行）与
`ui-design-research.md`（780 行）。第一梯队 = 调研里"投入产出比最高、且现有代码结构能自然容纳"的四项。

#### 1.6.1 荧光笔 `Highlighter`（`Tool.Highlighter = 8`）

半透明宽笔画，**不遮住底下的文字**——这是 Snipaste / 微信截图都有的标注刚需。
Snipaste 的关键设计是「荧光笔拥有一份**独立的 alpha**，其它笔共用一个」，
本实现照搬了这个思路。

- `Stroke.HighlightAlpha` 默认 90（16~220），与调色板颜色**解耦**：
  换颜色不会让荧光笔突然变得不透明。
- 绘制路径 `DrawHighlighter()`：**不能直接往屏幕上画半透明笔迹**，
  否则同一笔重叠处会越描越深（Google 的 `over` 混合在自重叠时必然加深）。
  做法是先画进一张 `Format32bppArgb` 的 scratch 位图，
  再用 `ImageAttributes` + `ColorMatrix` 的 `Matrix33` 一次性把整体 alpha 乘上去，
  最后 `DrawImage` 合成——这样一笔之内无论自重叠多少次，透明度都是均匀的。
- scratch 位图有 `(long)bw*bh > 32L*1024*1024` 上限，超了直接跳过而不是 OOM。
- `OnMouseMove` 的样本采集守卫是 `Pen || Highlighter`（漏掉后者会让荧光笔断线）。

#### 1.6.2 序号标记 `StepNumber`（`Tool.StepNumber = 9`）

每点一下画一个带数字的圆点，数字自动递增。

**这里最费心思的是编号语义**，因为它直接决定导出的图对不对：

- 数字存在**笔画自己身上**（`Stroke.StepIndex`），**绝不按列表位置重算**。
  这是从 ShareX 的历史里学到的教训：ShareX 的形状历史与画布历史是**两套独立的 memento**，
  撤销时两者不同步，序号就会错乱。
- `RecomputeStepCounter()` **只扫 `_strokes`，不扫 `_redo`**。
  早期版本扫了两个栈，导致「撤销掉最后一个序号后数字不能回收」——
  导出的图里会出现 1、2、4 这种跳号。
- 重做栈**不预留**编号：撤销会释放数字，重做会重新分配。
- 一张图内保证「无重号、无跳号」：删掉中间某个序号，其余会重新编号。
- `StepNumberPersistent`（默认 true）决定**跨截图**是否连续，
  `_persistentStepCounter` 只在 `ActivateOverlay` 里播种一次，
  这样退出再进画笔模式能接着上一张的最大数字往下走，适合做多张连续步骤图。

#### 1.6.3 箭头三样式（`ArrowStyle` / `ArrowStyleIndex`）

| 值 | 名称 | 实现 |
| --- | --- | --- |
| 0 | 直线箭头 | 默认，`DrawArrowHead` 直接画在 (a→b) 上 |
| 1 | 弧线箭头 | 二次贝塞尔，`bow = Math.Min(len * 0.28f, 120f)`，箭头方向取**端点切线**而不是弦方向 |
| 2 | 折线箭头 | 沿主轴先走到拐点再转向目标 |

弧线箭头的箭头方向必须用**曲线在端点的切线**，用弦方向会让箭头"歪着长在曲线上"。

#### 1.6.4 撤销 / 重做（`Ctrl+Z` / `Ctrl+Y`）

ShareX 花了 **4.5 年**才加上撤销重做（issue #4079），本项一次做对。

- 两个栈各自上限 `MaxRedoDepth = 200`。
- **重做的判定必须排在撤销之前**：`OverlayKeyDown` 的 Ctrl 分支先测
  `if (Matches(redo, code) || (code == Keys.Z && e.Shift))`，再测
  `if (Matches(undo, code) || code == Keys.Z)`。
  反过来的话裸 `code == Keys.Z` 会把 `Ctrl+Shift+Z` 一起吞掉，重做永远触发不了。
- **`Pinned` 笔画不进重做栈**：它自己持有一张 `Bitmap`，撤销语义不明确，
  强行入栈会引入位图生命周期问题。想删贴图就用它的关闭按钮。
- `Clear()` 会**丢掉重做栈**（清空之后"重做"回到一个已被清掉的画面没有意义）。
- `NoteEdit()` 必须在**每一个**提交路径里调用，否则撤销栈会漏记录。
- `ClearForTest()` **委托给 `Clear()`**，不要各写一遍——两条路径分叉正是上面那些 bug 的来源。

#### 1.6.5 窗口截图 `Ctrl+Alt+W`（`Native.WindowBounds`）

**本次最容易踩坑的一项。**截光标下的顶层窗口，不框选。

- 边界**必须**用 `DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS = 9, ...)`，
  不能用 `GetWindowRect`。实测：`GetWindowRect` 返回的是 **DPI 虚拟化后**的坐标，
  而且包含窗口周围一圈**不可见的拉伸边框**——在 200% DPI 下会截歪、截多。
  这是**实测数据**，不是推测。
- `AppContext2.WindowScreenshot()` 在显示任何东西**之前**先解析
  `Native.RootWindowAt(Cursor.Position)`，并用 `OwnWindowHandles()` 排除自己，
  否则会截出"截图里的截图"。
- `OwnWindowHandles()` 里 `_msgWindow.Handle` 要 `try/catch` 包起来。
- 最小化 / 不可见的窗口用 `IsIconic` / `IsWindowVisible` 跳过，
  继续往下找可截的窗口；`finally` 里 `ResumeAfterCapture()`。

#### 1.6.6 屏幕取色器 `Ctrl+Alt+P`（`src\ColorPickerForm.cs`，新增）

- 全屏无边框窗口 + 冻结底图 + 放大镜（最近邻放大，边缘自动翻转避免出界）。
- 读出框用 `Beautifier.FormatColor()` 给 `#RRGGBB`，带双层描边色块。
- 取到之后：**替换调色板当前选中格**（`OverlayForm.SetColorFromPicker`）
  + 色号**复制到剪贴板**。
- 所有权：`_ownsFrozen` 为 true 时 `Dispose(bool)` 才释放 `_frozen`；
  `AppContext2.StartColorPicker()` 用 `using` 并把 `ownsFrozen: true` 传进去。

#### 1.6.7 美化截图（`src\Beautifier.cs`，新增）

渐变背景 + 圆角 + 柔和阴影，**只作用于存盘文件**。

- **剪贴板和贴图不受影响**，这是刻意的：美化后尺寸会变大，
  如果连剪贴板一起改，粘进文档时尺寸对不上。
- `Beautifier.Render()` 是**纯函数**：null 输入 / 空图 / 超大图
  （`(long)outW*outH > 80L*1024*1024`）返回 null，不抛异常。
- `radius` 夹到 `Math.Min(src.Width, src.Height) / 2`，半径大于短边一半会画出怪异形状。
- **GDI+ 没有模糊原语**，柔和阴影只能靠分层圆角矩形叠出来：
  `Layers = 6`、`Spread = 26`、`OffsetY = 10`、alpha `70*(1-t)*(1-t)`（二次衰减，外圈更淡）。
- `TryParseColor()` 自己解析 hex（`NumberStyles.HexNumber` + `InvariantCulture`），
  **不用 `ColorTranslator.FromHtml`**——它对非法输入的行为不可靠。

#### 1.6.8 设置界面接线（`src\SettingsForm.cs`）

新增 6 个可重绑快捷键 + 17 个配置项。**注意 `SettingsStore.Load/Save` 是逐字段手写的，
不是反射**，所以每个新字段都要在 **Load 和 Save 两处**都加一行，
漏掉一处会表现为"设置保存了但重启后丢失"。

- 快捷键页三张卡都长高了以容纳新行（功能快捷键 420→496、
  绘制中的工具快捷键 330→420、其它单键操作 240→280）。
- 画笔页末尾新增 `BuildNewToolsCard()`：卡片「荧光笔与序号」
  （透明度 / 箭头样式 / 序号圆点大小 / 序号跨图连续）。
- 截图页末尾新增 `BuildBeautifyCard()` + `BuildPickerCard()`。
- `PickColorFor(Button)` 给美化卡片挑渐变色：冻结屏幕 → `ColorPickerForm` → 写回按钮文字。
- `ResetDefaults()` 不需要改——它调用 `LoadDraftIntoControls()`。

#### 1.6.9 界面打磨

依据 `ui-design-research.md` 的 token。**该文档每条结论都标了 (A) 已验证 / (B) 推荐**，
只有 (A) 才可作为依据。

**HUD（`OverlayForm`）**

- 抽出**共享**的 `OverlayForm.BuildPanelShadow(w, h, radius)`：
  10 层圆角矩形、`PanelShadowSpread = 14`、`PanelShadowOffsetY = 5`、
  alpha `64*(1-t)^2`、最外层先画。返回**调用方拥有**的位图。
- `DrawHudPanel()` 用它，并按 `(w, h, radius, PanelShadowSpread)` **缓存**——
  绝不能每帧重建，那会直接把帧率打下去。
- `DrawDrawHud`（半径 10）与 `DrawZoomHud`（半径 12）都改用它，视觉统一了。
- 文字渲染提示必须是 `AntiAliasGridFit`：**GDI 的 ClearType 无法混进带 alpha 的表面**，
  在 HUD 上会出彩边。绘制原点一律 `Math.Round` 取整（**永远不要 `/2f`**），
  否则抗锯齿会把半像素糊成灰边。
- 色块改成**双层描边**（外黑 `ARGB(190,0,0,0)` 1.4f + 内白 `ARGB(180,255,255,255)` 1.2f）。
  原因：8 个调色板颜色里有 4 个对至少一种底色达不到 3:1 对比度，只描一层必然有看不清的组合。
- 新增 `HudEdge` 发丝线（`ARGB(38,255,255,255)`）勾出面板轮廓。

**区域选择工具条（`RegionSelectForm`）**

- 新增 `DrawToolbarShadow()`，同样复用 `OverlayForm.BuildPanelShadow`（半径 9），
  按 `(Width, Height, radius)` 缓存，`Dispose(bool)` 里一起释放。

**修掉一个真实的文字重叠缺陷（`SettingsForm` 画笔页）**

`_chkSmooth`（"平滑线条（抗锯齿）"）在 `(16, sy)`，
而它的说明文字 `AddHint(..., 34, sy + 2, ...)` 放在**同一个 y**、x=34 —— 
正好压在自己的复选框标签上。用 `render_scroll.exe` 渲染画笔页截到 y=900 才看出来，
纯算术探针完全测不到。修法：`sy += _chkSmooth.PreferredSize.Height + 4;` 让说明文字排到下一行。

（注意 `CheckBox` **没有** `PreferredHeight` 属性，用 `PreferredSize.Height`。）

**修掉高 DPI 下卡片的大片空白（重要）**

现象：设置页每张卡片标题底下都有一条明显的空白带（`快捷键` 页最大 211px）。

根因链条：

1. `FinishPage()` 在 `BuildPages()` 期间跑，**那时 `ApplyDpiScale()` 还没执行**；
2. 那一刻说明文字的 `MaximumSize` 宽度还是**未缩放的 96 DPI 值**，
   而文字本身**已经按真实 DPI 渲染**了（`Scale()` 不碰 `Font`）；
3. 于是这段文字在测量时被折成**远多于实际**的行数，
   `FinishPage()` 忠实地为这些行预留了高度；
4. `ApplyDpiScale()` 随后把这个偏大的高度**原样乘以 2**，
   而加宽后的标签又塌回了真实行数——差值就变成卡片底部的空白带。

修法：新增 `ReflowCardHeights()`，在 `ApplyDpiScale()` 里、
`p.Scale()` 之后 `ShowPage()` 之前调用。它按 `Top` 排序还原叠放次序，
用**缩放后**的几何重新量每张卡，并重写 `AutoScrollMinSize`。
作者的 authoured 高度也按 `_dpiScale` 一起缩放（它是 96 DPI 的数字）。

同时把两处自己写得过大的 authored 值降回去
（`功能快捷键` 496→120、`美化截图` 340→120、`启动与后台` 210→120、
`全局快捷键` 110→60）——测量才是权威，authored 只是给"还没布局"的卡兜底。

**效果（实测，`build\obj\dump_cards.exe`）**：每页内容高度
`快捷键 3342→2801`、`画笔 2568→2261`、`截图 2784→2463`、`常规 1968→1562`，
共去掉约 2000px 空白；每张卡的 `dead`（高度 − 内容底 − 内边距）从最大 211 降到 ≤45。

**证据**：`build\layout_cards.cs`（**新增**，加入 `run_probes.ps1`，探针总数 16→**17**）
断言三件事——① 每个控件都完整落在自己的卡内（含 `Bottom/Right/Top/Left` 四面）、
② 卡片之间不重叠、③ 没有卡片在内容下方留 >40px 空白。
另加"超出视口的页必须能把 `AutoScrollMinSize` 撑到最后一个卡"。
修前 **2 FAIL**，修后 **21 PASS / 0 FAIL**。

> 这个探针的价值在于它测的是 PNG 里**看不出来但确实错**的性质：
> 一张截图能看出"这里好像空了点"，但只有算术能断言"没有任何控件越出卡片"。

#### 1.6.10 本轮的验证结果

| 项目 | 结果 |
| --- | --- |
| `build\run_probes.ps1` | **17/17 PASS**（`tier1_probe` 78 PASS/0 FAIL、`layout_cards` 21/0、`hotkey_conflict_probe` 30/0） |
| `verify_ui.py` | **81/81 断言通过** |
| `verify_resize.py` | 全部通过，设计尺寸 **2386×1704 仍可精确还原** |
| `verify_upgrade.py` | **26/26 通过** |
| `verify_readme.py` | **7/7 通过**，README 的 66 键与程序 **逐一一致**且可回环 |
| 发布构建 | `dist\ScreenPen.exe` 271,360 B，sha256 `F6B479FF…` |
| 配置回环 | 17 个新键全部写出（共 66 键） |

> `hotkey_conflict_probe` 里原来是 `Check(..., boxes.Count == 19)` 的**硬编码行数**，
> 每加一个键就会假失败。已改成和窗体自己的 `_bindings` 集合比数量
> （保留 `>= 19` 的下限），这样"界面里建了行但没进 `_bindings`"这类真 bug 才会被抓住。

---

### 1.7 第六轮升级：文字识别（OCR）+ 录制 GIF（已完成）

本轮新增两个功能，共 **4 个新源文件、4 个新探针、12 个新配置键**。

#### 1.7.1 为什么工具栏没有变成 5 个按钮

> **⚠️ 本节结论已在 §1.8 被用户明确推翻**，保留在此仅作历史记录。
> 用户的原话是「OCR 和 gif 都可以在截图那个增加要给按钮」——工具条入口本来就是
> 他想要的东西。工具条现在按**数量**而不是四个具名矩形来布局，`BtnCount = 6`，
> 所以再加按钮不再需要动布局代码。下面的"三处探针在断言恰好 4 个"已经全部
> 改写成"遍历全部按钮"。

区域选择界面的工具条**曾经必须保持恰好 4 个按钮**——`pin_probe.cs`、`measure_toolbar.cs`
（`labels.Length == 4`）、`region_probe.cs` 三处都在断言这件事，因为那个宽度是在
小桌面上还能收得下的上限。

所以 OCR / GIF 走的是**别的入口**，三个都有：

1. **区域选择界面里的单键** `O`（识别）/ `G`（录制）——和原有的 `P`（贴图）同一个位置、
   同一个写法，**只在没有 Ctrl/Alt 时生效**，所以 `Ctrl+O`/`Ctrl+G` 仍然空闲（有断言）。
2. **两个新的全局热键** `Ctrl+Alt+O` / `Ctrl+Alt+G`。
3. **托盘菜单**新增两项「识别选区文字…」「录制选区为 GIF…」。

`RegionAction` 追加为 `Ocr = 4`、`RecordGif = 5`，**0/1/2/3 的取值一个都没动**
（存档和既有探针依赖它们）。`ocr_gif_wiring_probe` 对这一点有显式断言。

#### 1.7.2 GIF 编码：为什么必须自己回填字节

这是本轮最硬的一个坑，**实测结论**：

- WPF 的 `GifBitmapEncoder` **会静默丢掉每帧延时**——写进去 `5,40,15`，
  再读出来是 `0,0,0`；而且**根本不写 `NETSCAPE2.0` 循环块**。
- 所以 `Gif.cs` 的做法是：`GifBitmapEncoder` 出图 → **在原字节里就地回填延时** →
  **再插入一个循环块**。
- ⚠️ **关键偏移量：第一个块在字节 `13` 开始**（6 字节签名 + 7 字节逻辑屏幕描述符），
  **不是 6**。这里写错 `PatchDelays` 会返回"未识别"，GIF 整个功能假成功——
  `gif_delay_probe`（0/2）就是这个 bug 的保留回归样本。
- 每帧延时的注入方式：GDI+ `Bitmap` → PNG 字节 →
  `BitmapDecoder.Create(ms, PreservePixelFormat, OnLoad)` → `Frames[0]` →
  `BitmapMetadata md = new BitmapMetadata("gif"); md.SetQuery("/grctlext/Delay", (ushort)cs);`
  → `enc.Frames.Add(BitmapFrame.Create(bf, bf.Thumbnail, md, bf.ColorContexts))`。

#### 1.7.3 OCR：不装 SDK 也能用系统引擎

- 运行时是 **.NET Framework 4.8**，所以可以**反射**绑定 WinRT 的 `Windows.Media.Ocr`：
  `Type.GetType("Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime")`。
- WinRT 的异步方法用 `MethodInfo.Invoke` 拿回来的是**裸 `System.__ComObject`**，
  必须**轮询 `Status`**（1=Completed / 2=Cancelled / 3=Error），再调 `GetResults()`。
  `System.Runtime.WindowsRuntime`（`AsTask`/`GetAwaiter`）**要 .NET 4.5 起**，
  所以 ShareX 那种 `await` 写法在本项目里用不了。
- `OcrEngine.RecognizerLanguage` 是 `Windows.Globalization.Language` **对象**，
  要读它的 `.LanguageTag`。`.Lines` 的声明类型是 `IReadOnlyList<OcrLine>`，
  用 `as IEnumerable` 枚举才稳。
- 本机实测：引擎可用，识别语言 1 个 = `zh-Hans-CN`，`"ScreenPen 12345"` 原样识别出来。

#### 1.7.4 "OCR 返回空"的真凶是测试自己（重要）

有一轮 5 条 OCR 断言全红，看着像产品坏了，**实际是探针的 bug**：

- 探针用**点值字号**建字体（`new Font("Segoe UI", 40f)`），点值要乘
  `Graphics.DpiY`，而 `DpiY` 跟**进程 DPI 感知**走。
- 本机是 **200% DPI**，同一个测试文本量出 **867×155**，画布只有 **760×130**
  —— **右边和下边都被裁掉**（墨迹盒 `(45,64)-(759,129)`），引擎当然什么都找不到。
- 取消 DPI 感知（96 DPI）时同一份代码墨迹盒是 `(32,45)-(428,83)`，识别正常。
- **修法**：探针**先钉住 DPI**（`SetProcessDpiAwarenessContext(new IntPtr(-4))`，
  放在 `Main` 第一行），并且**所有测试字体改用 `GraphicsUnit.Pixel`**（像素单位不受 `DpiY` 影响）。
- 同时加了一条**防裁剪断言**：扫出最右/最下的墨迹像素，断言没贴到画布边缘。
  把 `PinDpi()` 注释掉后它会立刻报 `(ink reaches 759x129 of 760x130)`，
  证明这条断言真的能抓住回归。

> 顺手保留下来的三条健壮性改进（不是探针 hack，是产品改进）：
> `OcrResult.RawText`（区分"引擎没找到"和"解析失败"）、
> `ReadLines` 失败时**设置 `Error` 而不是静默返回 0 行**、
> 上面那条防裁剪断言。

#### 1.7.5 录制 GIF 的几个必须遵守的点

- **录制框不能录进画面**：用 `Native.SetExcludedFromCapture`（`WDA_EXCLUDEFROMCAPTURE`）
  把红框窗口排除掉；**这个调用失败时直接把红框隐藏**，绝不冒险把 UI 烤进帧里。
- **采样用 `System.Windows.Forms.Timer`**，且**必须写全名**——裸 `Timer` 会被绑定到
  `System.Timers.Timer`，得到 `CS1502/CS1503`。
- `GifCountdownSeconds` 默认 **3**：探针测"时长上限"时**必须显式设成 0**，
  否则 2 秒的等待全耗在倒计时里（这个坑踩过一次）。
- 抓帧失败**要计数**（`GrabErrors`/`FirstGrabError`），不能 `catch { }` 吞掉——
  吞掉之后"抓帧一直失败"和"画面一直没变"表现完全一样。
- `FrameCount` 在 `Stop`/`Cancel` 之后**仍然可读**（`_storedFrames`），
  否则探针会在结束后读到 0。

#### 1.7.6 本轮的验证结果

| 项目 | 结果 |
| --- | --- |
| `build\run_probes.ps1` | **21/21 PASS**（新增 4 个：`gif_delta_probe` 6/0、`gif_ocr_probe` 47/0、`gif_recorder_probe` 29/0、`ocr_gif_wiring_probe` 56/0） |
| `build\run_feasibility.ps1` | **4/4 PASS**（38 条断言） |
| `verify_readme.py` | **7/7 通过**，README 的 **78 键**与程序逐一一致且可回环（连跑 3 次稳定） |
| `layout_cards` | **21/0**，新增的 OCR / GIF 卡片无越界、无空白带 |
| 发布构建 | `dist\ScreenPen.exe` **301,568 B**，sha256 `9B27348C…` |
| 独立解码验证 | 用 Pillow 解码录制产物：**9 帧、240×160、延时 `[100×8, 500]`、`loop=0`**，8/8 帧与首帧不同（真的在动） |

> ⚠️ **踩过的坑（务必记住）**：`verify_readme.py` 有一轮报 12 个新键"回环失败"，
> 查下来是 **`dist\ScreenPen.exe` 还是旧的**（时间戳比源码早一天，且二进制里搜不到
> `GifFps`）。**跨进程验收脚本跑的是构建产物，不是源码**——
> 改完源码**必须重新 build 再跑**，否则会误判成产品 bug。
> 这与"编译失败会留下旧 exe"是同一类陷阱。

---

### 1.8 第七轮：构建与写盘健壮性加固（已完成）

本轮**没有改任何用户可见行为**，只修两处"失败时会静默留下错误状态"的地方。

#### 1.8.1 GIF 写盘不再是"先截断再写"（`src\Gif.cs`）

**问题**：`Gif.Encode` 原来是
```csharp
using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
    fs.Write(bytes, 0, bytes.Length);
```
`FileMode.Create` 会**先把目标文件截断成 0 字节**再写。整个 GIF 在内存里已经编码好了，
所以唯一的失败窗口就是这一步写盘（磁盘满、目录只读、目标被别的程序占用），
但一旦在这里失败，磁盘上就留下一个 **0 字节或写了一半的 GIF**——
而用户在"图片\ScreenPen"里看到的是一个有名字的文件，会以为录成功了。

**改法**：先写同目录的临时文件，全部写完再原子替换。
```csharp
tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    fs.Write(bytes, 0, bytes.Length);

if (File.Exists(path)) File.Replace(tmp, path, null);
else File.Move(tmp, path);
tmp = null;
```
`finally` 里删除残留的 tmp（成功路径已经把 `tmp` 置 `null`，所以不会误删刚发布的文件）。

**为什么临时文件要和目标同目录**：`File.Replace` / `File.Move` 只在同一卷内是原子的，
写到 `%TEMP%` 再跨卷搬过去就退化成"复制 + 删除"，等于没修。

**这与 `SettingsStore.Save()` 是同一套模式**（§3.3、§1.5.4 #8），
现在程序里两处"用户以为存上了"的写盘都已经是原子的。

#### 1.8.2 构建脚本不再可能"留下旧 exe 冒充新构建"（`build\build.ps1`）

**问题**：`csc` 原来直接 `/out:dist\ScreenPen.exe`。§1.7.6 已经记录过这个陷阱的代价：
一次跨进程验收脚本报 12 个键"回环失败"，查下来是 `dist\ScreenPen.exe` **还是旧的**。
脚本本身有 `$LASTEXITCODE` 检查，但**手工编译**（HANDOVER §0 里给的那条命令）
和"编译中途失败"这两种情况下，`dist\ScreenPen.exe` 依然可能是上一次的产物。

**改法**：编译到暂存路径，**只有编译成功才发布**。
- `/out:build\obj\ScreenPen.stage.exe`
- 成功后才 `Move-Item -LiteralPath $StageExe -Destination $OutExe -Force`
- 编译失败 → 清掉暂存文件，抛出的消息里**明确写"`$OutExe` was left untouched"**
- 发布时目标被占用（`ScreenPen.exe` 还在运行）→ `Move-Item` 抛错，
  消息提示"is ScreenPen.exe still running?"，**不会**静默发布失败

**顺带修掉的一个自伤**：第一版实现在编译前加了一句
`Remove-Item $StageExe -ErrorAction SilentlyContinue`，本意是清掉上次的残留。
实测它会在**暂存文件不存在时**（首次构建的正常情况）抛出终止性错误，
**导致脚本在调用编译器之前就死掉**——即"失败路径"根本没被走到，
却因为 `dist\ScreenPen.exe` 恰好没变而看起来像是验证通过了。
现已删除该预删除（`csc` 本来就会覆盖自己的输出），并把所有清理集中到
`Remove-StageQuietly`：**清理永远不允许成为让构建失败的原因**。

#### 1.8.3 `run_probes.ps1` 不再硬编码编译器路径

`run_probes.ps1` 原来写死
`$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"`，
而 `build.ps1` 是**动态查找**的。两个脚本会用不同工具链编译，
且只有 32 位 Framework 的机器上会以一句含糊的"命令不存在"失败。
现改为与 `build.ps1` 相同的查找顺序（Framework64 → Framework），
找不到就抛 `No C# compiler (csc.exe) found. Install .NET Framework 4.x.`，
并打印一行 `compiler : <路径>` 便于比对。

#### 1.8.4 本轮的验证结果

| 项目 | 结果 |
| --- | --- |
| `build\run_probes.ps1` | **21/21 PASS**；`gif_ocr_probe` 由 47 项增至 **49/49**（新增 2 条原子写入断言） |
| 新增断言 | 目标路径被目录占用时 `Encode` 返回错误、**目标本身未被破坏**、且**不残留 `.tmp` 文件** |
| 构建成功路径 | `sources: 17`，`dist\ScreenPen.exe` **302,080 B**，无暂存残留 |
| 构建失败路径（实测） | 故意放一个语法错误的 `src\zz_break_test.cs` → 编译器 **exit 1** → 脚本抛出预期消息 → **`dist\ScreenPen.exe` 的 sha256 与 LastWriteTime 均未变化**，且无暂存残留 |

> ⚠️ 构建失败路径是**实测**的，不是推断的——这一点很重要：
> 1.8.2 里那个自伤（预删除先抛异常）在"只看结果"的验证下**完全看不出来**，
> 因为原 exe 同样没有变化。**验证失败路径时，必须确认失败真的发生在你打算验证的那一步。**
> 本次的判据是脚本抛出的消息必须是 `compilation failed (exit 1)`，
> 而不是任何其他异常。

> `dist\ScreenPen.exe` 的体积和 sha256 每次编译都不同（新的 MVID），
> 这是 §0 里已经说明过的，不要拿哈希判断是否重新编译过。
> 本轮体积 **301,568 → 302,080 B**（+512 B），来自 `Gif.Encode` 的临时文件逻辑。

---

### 1.9 第八轮：四个交互缺陷修复（已完成）

用户一次性报了五个问题，原文：

> 文字识别OCR功能交互方式不友好，跟随滚动功能有bug(我想做的是在标注的时候可以使用鼠标滚轮上下滑动页面，
> 这个是否可以实现，标注跟不跟着移动都无所谓)，还有gif动图功能有bug,交互方式也不友好，
> 生成的动图保存路径也不显示，复制按钮也不显示，不知道生成的动图在哪里，
> OCR和gif都可以在截图那个增加要给按钮，还有截图的时候，框选了区域之后，就不可以移动候选框了，
> 我想有的是还可以自动调节框选的区域的，帮我优化一下这些存在的问题

本轮**新增 2 个源文件、1 个探针**，改写了 `RegionSelectForm` 的工具条与选区几何，
并推翻了 §1.7.1 里"工具条必须恰好 4 个按钮"的设计约束。

#### 1.9.1 工具条 4 → 6 个按钮，且以后再加不用动布局

**问题**：OCR / GIF 只能靠快捷键触发（§1.7.1 的设计），工具条上没有任何入口，
所以用户根本不知道有这两个功能。

**改法**：`RegionSelectForm` 里原来是一串具名成员（`_btnSave` / `_btnCopy` /
`_btnPin` / `_btnCancel`）加一堆散落的下标。现在换成**数量驱动**：

```csharp
const int BtnCount = 6;
const int BtnSave = 0, BtnCopy = 1, BtnPin = 2, BtnOcr = 3, BtnGif = 4, BtnCancel = 5;
Rectangle[] _btn = new Rectangle[BtnCount];
string[] _btnLabels = new string[BtnCount];
```

`LayoutToolbar()` 只遍历 `BtnCount`，**原来的四级降级顺序一步没少**：
自然标签 → 短标签（`保存/复制/贴图/识别/GIF/取消`）→ 按最宽按钮等比收窄 → 极端小窗等宽兜底。
`ActionForButton(int)` 是下标到 `RegionAction` 的唯一映射，`PrimaryIndex()` 决定哪个按钮高亮
（现在 `Ocr` / `RecordGif` 也会高亮自己那一个）。

> 关键点：**新的按钮走的是和快捷键完全相同的 `Finish(ActionForButton(i))`**，
> 不存在"按钮能用但快捷键不能用"或者反过来的分叉。

#### 1.9.2 框选完成后可以移动和缩放（`RegionSelectForm`）

**问题**：`OnMouseDown` 里"有选区"这一分支只有两件事——命中工具条就 `Finish`，
否则**一律 `BeginGesture(SelDrag.New)` 重新开一个框**。所以框完就定型了，
手抖多框了两像素也只能重画。

**改法**：加了 `enum SelDrag { None, New, Move, N, S, W, E, NW, NE, SW, SE }`，
以及 `HandleRect(int)` 定义的 **8 个把手**（顺序 `0=NW 1=W 2=SW 3=N 4=NE 5=E 6=SE 7=S`，
把手矩形**以边框线为中心**，所以有一半在选区外——这是有意的，方便从外侧抓）。
`HandleLeftDown` 的优先级变成：

1. 工具条按钮 → `Finish`
2. **把手** → 缩放（`HitHandle`，**角优先于边**，因为小选区上把手会重叠，瞄角就该拿到角）
3. **选区内部** → 整体移动
4. 其余 → 新框选

`ResizeFrom(origin, kind, dx, dy, client)` **逐条边单独钳制**而不是钳制最终矩形，
这样"用户没抓的那条边"不会跟着动。移动走 `ClampToClient`，保证不把选区拖出屏幕。
光标由 `CursorForDrag` / `UpdateSelectionCursor` 给（`SizeNWSE` / `SizeNESW` / `SizeNS` /
`SizeWE` / `SizeAll`）。

把手在**小于 2 倍把手边长**的选区上会隐藏（`HandlesVisible()`）——否则把手会盖满整个选区，
连"拖动内部移动"都没地方下手了。

鼠标逻辑被拆成 `HandleLeftDown` / `HandleMouseMove` / `HandleLeftUp`，
`OnMouseDown/Move/Up` 只是转发，**探针因此能驱动真实手势路径**而不是另写一套。

#### 1.9.3 GIF 录完之后，必须看得见文件在哪（`src\GifResultForm.cs`，新增）

**问题**：录制结束时只 `Balloon("GIF 已保存", ...)`。气泡 2 秒就消失、不能选中、
不能点，所以用户的原话是"不知道生成的动图在哪里"。

**改法**：新增结果窗口，显示

- `已保存：<文件名>`
- `42 帧   7 秒   2.0 MB`
- `保存位置（可直接选中复制）：` + 一个**只读 `TextBox`**（可选中、可 `Ctrl+C`、路径深了能滚动）
- 四个按钮：**复制路径** / **在文件夹中显示** / **打开动图** / **关闭**

`在文件夹中显示` 用 `explorer.exe /select,"<path>"`，会**在资源管理器里高亮这个文件本身**，
而不只是打开所在目录。

**布局刻意不用 `Dock`**：WinForms 里 docked 子控件是按**反 z-order** 解析的，
"谁最后压在上面"取决于 `Controls.Add` 的调用顺序。全部改用显式 `SetBounds`
（`LayoutChildren()` 在构造和 `OnResize` 里调），在任何 DPI 和任何宽度下表现一致。

> **实测抓到的自伤**：`ClientSize = ...` 写在构造函数里，会**先触发 `OnResize` → `LayoutChildren()`**，
> 而此时子控件字段还全是 `null` → `NullReferenceException`，构造函数直接崩。
> `ux_fix_probe` 一跑就复现了。`LayoutChildren()` 现在开头有一句
> `if (_head == null || _close == null) return;`。
> **在一个"专门用来告诉用户文件在哪"的窗口上崩掉，比原来那个气泡还糟。**

**`AppContext2.RecordGif` 的接线**：`rec.Finished` 里改成
`GifResultForm.ShowResult(file, frames, seconds)`。注意两点：

- `frames` / `seconds` 必须在 `rec.Dispose()` **之前**读出来；
- `Quit()` 里新增 `_quitting` 标志。`StopRecordingIfAny()` 会同步触发 `Finished`，
  如果在关机路径里弹一个模态窗口，进程就**永远退不出去**。

#### 1.9.4 GIF 的"怎么停下来"以前根本不存在（`src\KeyboardHook.cs`，新增）

README 一直写着"按 `Esc` 或再次触发录制快捷键即可结束并保存"，但：

- 录制红框 `RecorderChrome` 是 `WS_EX_NOACTIVATE` 且**从不获取焦点**，收不到任何按键；
- `AppContext2` 里**没有任何全局键盘钩子**，`Esc` 会直接落到前台程序；
- `RegionScreenshot(RecordGif)` 也没有"已在录制就停止"的分支。

结论：**唯一能结束录制的方式是等满 30 秒上限**。这是本轮发现的最严重的一个 bug，
而且它是**文档承诺了、代码里不存在**。

**改法**（三条路都补上）：

1. `src\KeyboardHook.cs`：录制期间临时挂 `WH_KEYBOARD_LL`，只认 `Esc`，
   录完立刻卸载（`StartRecordingKeyWatch` / `StopRecordingKeyWatch`）。
   两个实现细节是必须的：
   - **回调里不做实事**，用 `SynchronizationContext.Post` 把工作丢回消息队列。
     Windows 对超过 `LowLevelHooksTimeout`（约 300ms）的低层钩子会**静默摘掉**，
     而"停止录制"要把整个 GIF 编码写盘——在回调里做，第一次 `Esc` 之后钩子就没了。
   - **吞掉这个 `Esc`**（返回 1 而不是 `CallNextHookEx`），录制期间 `Esc` 属于录制，
     不该漏给前台程序。
2. `RegionScreenshot` 开头：`if (defaultAction == RecordGif && StopGifRecording()) return;`
   —— 第二次触发 = 停止，而不是"再开一个框选"。
3. 托盘新增 **停止 GIF 录制**，**只在录制期间可用**（`_stopGifItem.Enabled`），
   这样托盘本身就能告诉用户"现在正在录"。

> `KeyboardHook` 的委托**必须存在字段里**：GC 不追踪只被 native 代码引用的委托，
> 存在局部变量里会被回收，下一次按键就跳进已释放的内存。

#### 1.9.5 滚轮为什么滚不动下面的页面（`OverlayForm` + `Native`）

这是"跟随滚动有 bug"的真正根因，和相关性算法一点关系都没有。

**问题链**：

1. 标注层是一张**全屏置顶**的窗口；
2. `SendWheel` 用 `SendInput` 注入滚轮，而 `SendInput` 投递到**光标下的窗口**——
   也就是**标注层自己**；
3. 于是自己的 `OnMouseWheel` 又被调一次，`passToDocument` 仍为 true，
   **再注入一次**……下面的网页**一次都没滚**；
4. 更早一层：`Native.IsScrollableWindowUnder` 用 `WindowFromPoint`，
   在标注层不穿透时**返回的永远是标注层自己**，类名不在黑名单里 → 判定"可滚动"。
   所以旧代码是"确认了一个根本没确认过的页面可滚动，然后把滚轮扔给自己"。

**改法**（`OverlayForm.ForwardWheel`）：

```csharp
if (Environment.TickCount - _wheelForwardTick < WheelPassThroughMs) return true; // 吃掉自己的回声
BeginWheelPassThrough();                       // WS_EX_TRANSPARENT，命中测试跳过我们
bool scrollable = Native.IsScrollableWindowUnder(screen);   // 现在才真的看到文档
if (!scrollable) { EndWheelPassThrough(); return false; }
_wheelForwardTick = Environment.TickCount;
Native.SendWheel(delta, false);
_wheelRestoreTimer.Stop(); _wheelRestoreTimer.Start();      // 180ms 后撤销
```

- 穿透窗口期内**标注层收不到鼠标**，所以注入的滚轮回声根本进不来——这才是打破死循环的关键，
  tick 判断只是双保险；
- 连续滚动会**不断续期**这个 180ms，所以整段滚动期间保持穿透，不会在两个刻度之间闪断；
- `Native.IsScrollableWindowUnder` 增加 **PID 判定**：窗口（或其 root）属于本进程就返回 false。
  标注层万一没穿透成功，也不会再谎报"可滚动"。

**顺带修掉 `Native.SetClickThrough` 的两个问题**：

- 原来 `enable` 时 `|= WS_EX_LAYERED`，`disable` 时**不清**——每调用一次就把窗口
  永久变成 layered，一个不可见的渲染改动会活过整个功能寿命。现在完全不碰 `LAYERED`，
  命中测试穿透本来就只需要 `WS_EX_TRANSPARENT`；
- 加了 `SetWindowPos(..., SWP_FRAMECHANGED)`，否则 ex-style 可能"读得到、命中测试不生效"；
- 加了 `next == v` 短路，状态没变就不写。

#### 1.9.6 `ScrollFollowClickThrough` 从死开关变成真开关（含一次键名迁移）

这个设置**写盘、有 UI 复选框、有 README 说明，但 `src/` 里没有任何一处读它**——
勾了等于没勾。

现在它按字面实现了：**跟随模式期间**标注层保持穿透（可以滚动、点链接）。
但代价必须说清楚——穿透是**整个窗口**的属性，所以那时**画不了新标注**。
默认值因此从 `true` 改成 `false`（默认应该是"还能继续标注"的那个）。

**键名迁移**：老配置里已经存着一个 `"ScrollFollowClickThrough": true`
（用户机器上实测确实有），而它从来没有产生过任何可观察的效果。
直接沿用这个键名，等于把一个"用户从没见过的 true"变成"一按 F 就画不了字"。
所以读写都换到 **`ScrollFollowClickThroughV2`**：老键被忽略，老用户拿到安全的默认值
`false`，下次保存时老键自然消失。

#### 1.9.7 本轮的验证结果

| 项目 | 结果 |
| --- | --- |
| `build\run_probes.ps1` | **22/22 PASS** |
| `ux_fix_probe`（新增） | 全部通过，覆盖上述五个问题 + 键名迁移 |
| `ocr_gif_wiring_probe` | 47 → **64/64**（工具条 6 按钮、点击落点、按数量遍历） |
| `pin_probe` | 改成遍历 `ToolbarButtonCountForTest`，不再只查四个具名矩形 |
| `measure_toolbar` | 5 种桌面宽度 × **5 种默认动作**（新增 `Ocr` / `RecordGif`）× 6 个按钮的真实字宽 |
| `verify_upgrade.py` | **26/26** |
| `verify_hotkeys.py` | **20/20** |
| `verify_ui.py` | **81/81**（**先重跑 `capture_ui.py`**；修掉 §1.9.8 的两个坑之后才是真的） |
| `verify_readme.py` | **7/7**（78 键；并验证跑完会还原用户配置） |
| `e2e_test.py` | **27/27** |
| `check_tray.py` | **6/6** |
| `verify_resize.py` | **9/9** |
| 构建 | `sources: 19`，`dist\ScreenPen.exe` **314,880 B**，`sha256 856EE6F1…` |

`ux_fix_probe` 里两条值得单独说的断言：

- **`Esc` 钩子是用真按键验的**：`Native.SendEscape()` 走 `keybd_event` 注入真实按键，
  再泵消息，断言 `EscapePressed` 真的被触发（`callbacks=1`）。
  只调测试缝的话，验的是自己的转发代码，不是钩子本身。
- **穿透是真读窗口样式验的**：`Native.ExStyleOf(ov.Handle)` 在穿透前后各读一次，
  断言 `WS_EX_TRANSPARENT` 被置上、又被清掉，且**前后 ex-style 逐位相同**
  （`0x10008 -> 0x10008`）——这条同时是"不再残留 `WS_EX_LAYERED`"的回归守卫。

> 顺带修的一个探针自身的 bug：`ux_fix_probe` 一开始断言"把手中心落在选区边上"，
> 用的却是 `Right - 1`。把手是**以边框线为中心**的，中心正好是 `Right`。
> 是**探针错了，不是代码错了**——这类断言要先想清楚"正确的定义是什么"再写。
> 另外 `run_probes.ps1` 现在支持 `-Only <探针名>`，单跑一个探针时不用再在几百行输出里找。

#### 1.9.8 三个"测试在骗自己"的坑（本轮踩到并修掉）

跑跨进程验收时发现 `verify_ui.py` 报 81/81，但那个 81/81 **是假的**。顺着查出来三个
**验证工具自己**的问题，都不是产品缺陷，但每一个都会让人得出错误结论：

1. **`verify_ui.py` 不会截图，它读 `capture_ui.py` 先前的产物。**
   我改了设置页文案之后直接跑 `verify_ui.py`，它拿的是 **10 月 15 日的旧 PNG**，
   于是给我一个漂亮的 81/81。**只改代码不重跑 `capture_ui.py`，这个脚本毫无意义。**
   （修法：在 HANDOVER 头部和 README 里把"必须按 `capture_ui.py` → `verify_ui.py` 的顺序"
   写成硬性说明。）

2. **`capture_ui.py` 截的是整窗，而 `verify_ui.py` 的常量是客户端坐标。**
   `PrintWindow` 会把**非客户区**（标题栏）也渲染进去，本机 200% DPI 下那就是顶部
   **58px 的浅色标题栏**。而 `NAV_W = 190` / `HEADER_H = 64` / `FOOTER_H = 60` 全是按
   客户端算的——于是 `header = a[:64, 190:]` 量的其实是**操作系统的标题栏**，
   难怪"header band is a card"掉到 8%。
   **为什么以前能过**：标题栏的深色标题文字正好落在 `TEXTMAIN` 上，给每一页
   白送了约 3000 个"主文字"像素，把 `bright > 8000` 这条阈值撑过去了；
   裁掉标题栏之后，最稀疏的 `关于` 页只有 5166 px 就暴露了。
   修法：`capture_ui.py` 新增 `client_crop()`，用 `GetClientRect` + `ClientToScreen`
   算出客户区偏移并裁掉非客户区；`verify_ui.py` 的主文字阈值按真实页面重新标定
   （8000 → 4000，注释里写清"这条是在防白页，不是在量页面有多满"）。

3. **`verify_readme.py` 会把用户的真实配置覆盖掉。**
   它必须把 README 里的 JSON 当配置喂给程序，但 `dist\ScreenPen.settings.json`
   就是用户的**在用的**配置——跑一次就等于把人家设置重置成文档样例。
   修法：跑之前字节备份，最后在报告之前恢复（放在最后，这样即使中途断言失败
   也不会把用户配置留在被替换的状态）。

> 这三条的共同点：**验证工具本身没有被验证过**。
> 判据不是"它报了 PASS"，而是"它这一轮真的读到了我刚改的代码/刚生成的产物"。
> 以后看到某个验收脚本全绿，先问一句：**它的输入是这一轮生成的吗？**

`verify_readme.py` 另外还要知道一处**有意的键名不一致**：`Config.cs` 的字段叫
`ScrollFollowClickThrough`，落盘键叫 `ScrollFollowClickThroughV2`（§1.9.6）。
脚本里加了 `JSON_KEY_OVERRIDES` 显式声明这件事，否则它会把这个**故意的**别名
报成"文档写了程序不存在的键"。

---

## 2. 待修复的缺陷（TODO）


> **§2.1–§2.4 已全部完成**（见 §1.4）。本节保留原始的问题描述与根因分析，
> 作为"这些问题当初为什么会出现"的历史记录，以及**验收标准的出处**。
> **§2.1–§2.5 现已全部完成**；本节保留的"修复方向 / 验收"是**当时的计划**，
> 实际落地方式见 §1.4 / §1.5，两者若有出入以代码为准。

### 2.1 【UI】导航栏文字被截断成一个字 —— ✅ 已修复（§1.4.2）

**现象**：左侧导航按钮只显示 `快`、`画`、`缩`、`截`、`常`、`关`，
而不是 `快捷键`、`画笔`、`缩放`、`截图`、`常规`、`关于`。

**根因**（`src\SettingsForm.cs` L263–285 `AddNav`）：

```csharp
Text = "   " + glyph + "    " + name,   // glyph 是 emoji：⌨ 🖌 🔍 📷 ⚙ ℹ
TextAlign = ContentAlignment.MiddleLeft,
Bounds = new Rectangle(12, 44 + index * 42, 166, 38),   // 宽度 166px
```

emoji 走的是 fallback 字体（Segoe UI Emoji），其 advance width 在 GDI+ 下被严重高估，
加上 `"   "` + `"    "` 共 7 个空格的前导缩进，`166px` 里塞不下，
`Button` 的文本渲染直接把剩余部分裁掉，于是只剩第一个汉字。

**修复方向**（任选其一，推荐 A）：

- **A（推荐）**：不用 emoji 做前缀。改成自绘：`OwnerDraw` 或 `Paint` 事件里
  先 `TextRenderer.DrawText` 画文字，再用 `IconLibrary` 画一个小的矢量/位图图标。
  图标与文字分开定位，宽度可控。
- **B**：把 emoji 换成单色几何符号（`▸`、`●` 等 ASCII/BMP 范围内的字符），
  去掉多余空格，并把按钮宽度加大到 `≥ 180px`、导航栏 `Width` 从 190 加到 210。
- **C**：干脆去掉图标，只保留文字，按钮宽度 180px。

**注意**：如果改成自绘，`ShowPage()`（L287–316）里靠 `b.BackColor` / `b.ForeColor` /
`b.Font` 切换选中态的做法会失效（自绘不读这些），需要改成记录 `_currentPage`
然后在 `Paint` 里按名字判断高亮。

**验收**：`dump_settings_tree.py` 能列出 6 个导航按钮，每个按钮的文本**完整**
（`快捷键`/`画笔`/`缩放`/`截图`/`常规`/`关于`）；用 `TextRenderer.MeasureText`
量出的宽度 ≤ 按钮可用宽度。

---

### 2.2 【UI】卡片内文字被遮挡 / 显示不全 —— ✅ 已修复（§1.4.1 + §1.4.2）

**现象**：截图中多处文字被下方元素压住或截断。典型：

- 「快捷键」页顶部卡片：说明文字第 2 行与卡片下边界重叠
  （截图里 `屏幕画笔 · 实时缩放 · 截图` 与卡片文字叠在一起）。
- 「画笔」页：`3 px` 与滑块、`文字大小 (px)` 行都紧贴/越界。
- 「截图」页：「保存位置与格式」卡片的路径输入框与「浏览」按钮错位。
- 「常规」页：「跟随滚动」卡片最后一行文字被底部状态栏切掉。
- 「关于」页：底部内容超出可见区域。

**根因**：

1. `NewPage()`（L327–362，**当时的行号**）里 `AutoScroll = false`，并且注释明确说
   *"The window is fixed-size and every page fits, so scrolling is unnecessary."*
   —— **这个假设是错的**。「常规」和「关于」页的内容高度超过
   `DialogHeight - header(64) - footer(60)`，超出部分直接看不见。
   > 📌 上面引的是**当时那一版**的注释。修好之后 `NewPage()` 改为 `AutoScroll = true`
   > （`FinishPage()` 用 `AutoScrollMinSize` 定虚拟高度），这段旧注释也一并被改写了。
   > 另外窗口**现在是可以调整大小的**（见 §1.5.3），所以"fixed-size"这个前提本身也不成立了——
   > 这也是为什么页脚与滚动范围必须随 `OnResize` 重算。
2. `FinishPage()`（L398–420）会测量卡片子控件并把卡片**加高**，
   于是卡片总高度进一步超过可视区，但没有滚动条兜底。
3. 卡片内的说明文字用 `MaximumSize = new Size(330, 0)` 限制宽度
   （`HotkeyBox AddKeyRow` 里），中文换行后行数变多，
   但**同行右侧的控件是按固定 Y 放置的**，没有跟随文字高度下移。

**修复方向**：

- **A（推荐，两步）**：
  1. 把 `_content`（L252）的 `AutoScroll = true` 打开。
     ⚠️ 注意 `NewPage()` 的注释警告过 `AutoScroll` 会让页面的
     `Dock=Fill` 子面板出问题。正确做法是：**页面不 `Dock=Fill`**，
     改成显式 `Size = new Size(CardWidth + PagePadX*2, FinishPage() 之后的 _cardCursorY)`，
     并设 `page.AutoScroll = true`，由外层 `_content` 承载滚动。
     即"页面高度 = 内容真实高度"，滚动交给页面自身。
  2. 每次 `FinishPage()` 后，`page.Height = _cardCursorY`（内容总高），
     `page.Width = CardWidth + PagePadX*2`，`AutoScroll = true`。
     外层 `_content` 保持 `Dock=Fill`。
- **B（更简单）**：把 `DialogWidth` 从 1180 加到 1280，
  `DialogHeight` 从 830 加到 900，并逐个页面核对内容高度是否装得下。
  **但这只是缓解，内容继续增加就会复发**，不建议单独使用。
- **A + B 一起做**最稳。

**同时要修**：`_cardContentTop` 的用法。所有页面构建器里，
卡片内控件的 Y 坐标目前是"作者手写"的（如 `int y = _cardContentTop;` 然后 `y += 34;`），
一旦某行说明文字换行变高，后续控件不会自动下移。
需要把这些手写递进改成**基于 `TextRenderer.MeasureText` 的累积**，
或在每个可能换行的文本后重新计算 `_cardContentTop`。

**验收**：写一个 `layout_probe.cs`（进程内）遍历每个页面，
断言：**每个卡片的 `Bottom` ≤ 页面内容高度**；
**每个子控件的 `Bottom` ≤ 所属卡片高度 − CardBottomPad**；
**每个 Label 的实测文本高度 ≤ 它的 `Height`**（这是"文字被遮挡"的机器可判定形式）。
再配合 `capture_ui.py` 逐页截图人工确认。

---

### 2.3 【画笔】快速画圆有明显折线，`SmoothLines` 没有真正平滑 —— ✅ 已修复（§1.4.3）

**现象**：用户开了「平滑线条」，快速画圆仍是多边形折线。

**根因**（`src\OverlayForm.cs`）：

```csharp
// L800 / L1699
g.SmoothingMode = _cfg.SmoothLines ? SmoothingMode.AntiAlias : SmoothingMode.None;
```

```csharp
// L938–941  DrawStroke
PointF[] pts = new PointF[s.Points.Count];
for (int i = 0; i < s.Points.Count; i++)
    pts[i] = ScreenToClientF(s.Points[i]);
g.DrawLines(pen, pts);          // ← 直线段连接，几何上就是折线
```

**`SmoothLines` 只切换了抗锯齿（像素边缘柔化），完全没有平滑几何**。
`Graphics.DrawLines` 把相邻采样点用直线连起来；鼠标快速移动时
`OnMouseMove` 的采样间隔可能几十像素，于是每两个点之间是一条直边，
圆就变成多边形。

**修复方向**（推荐组合 A+B）：

- **A. 曲线拟合替代折线**：把 `DrawLines` 换成
  `g.DrawCurve(pen, pts)`（Cardinal spline，`Graphics.DrawCurve` 有 tension 重载），
  或 `GraphicsPath.AddCurve`。这是**最小改动、效果最明显**的一步。
  注意：`DrawCurve` 对 `pts.Length < 3` 会抛异常，需保留 `Count == 1` / `Count == 2` 的分支。
- **B. 落笔时插值补点**：在 `OnMouseMove`（L1284）/ `TestExtendStroke`（L1577）
  里，新点与上一点距离超过阈值（如 4px）时，用 **Catmull-Rom / 二次贝塞尔**
  在两点间插值若干中间点，再存入 `Points`。
  这样既平滑了渲染，也让 `StrokeRect` / 马赛克 / 橡皮的命中判定更准。
- **C. 渲染时做移动平均**（Chaikin 或 3 点加权平均）：
  `pts[i] = (p[i-1] + 2*p[i] + p[i+1]) / 4`，对已存点做一次轻量平滑再画。
  实现最简单，但会轻微缩短笔画两端，需对首尾点做保留处理。

**必须保留的行为**：
- 直线/矩形/椭圆/箭头工具**不能**被平滑影响（它们本来就该是精确直线/形状）。
  只对 `Tool.Pen` 生效。`DrawStroke` 里要按 `s.Tool` 分支。
- 马赛克（`DrawMosaic`）和聚光灯（`DrawSpotlight`）的几何不要改。
- 撤销/重做、`FirstStrokePoints()`、`LastStrokePointCountForTest()`
  这些测试钩子的语义要保持（插值会改变点数，注意 `tools_probe.cs` 里
  有依赖点数的断言，需要同步更新）。

**验收**：新增 `smooth_probe.cs`（进程内）：
1. 构造一条"快速画圆"的稀疏折线（如 16 个点构成的圆），
   调用 `RenderComposite` 渲染，检查**渲染路径确实走了曲线**（可用 `GraphicsPath` 的
   `PointCount` 或直接断言 `DrawCurve` 被调用的次数 > 0 —— 建议把渲染抽出一个
   `static void DrawPolyline(Graphics, Pen, PointF[], bool smooth)` 便于单测）。
2. 断言平滑后的曲线在**弦中点处**的最大偏差小于折线版本的偏差
   （即量化"更圆了"）。
3. 断言 `Tool.Line`/`Rectangle`/`Ellipse`/`Arrow` 的渲染路径**没有**被平滑。
4. 回归 `tools_probe.cs` 全部通过。

---

### 2.4 【跟随模式】按 F 切换的跟随模式有 bug —— ✅ 已修复（§1.4.4）

**现象**：用户报告 F 跟随模式有 bug。算法层 `follow_probe.cs` 18/18 通过，
说明**相关性算法本身是对的**，bug 在**集成层**。

**已定位的可疑点**：

1. **`_followMisses` 只增不读**（`OverlayForm.cs` L74、L668、L671）。
   字段被 `++` 和 `= 0`，但**没有任何地方读取它**。
   原意显然是"连续 N 帧检测不到滚动就自动退出跟随"，但这个逻辑**没写**。
   → 后果：文档滚到底 / 切到不滚动的窗口后，跟随模式一直空转，
   每 140ms 一次全屏 `CaptureGray`，**白白吃 CPU**，且用户没有反馈。
2. **`_followSawMotion` 的抖动抑制可能吃掉第一帧**（L616–624）：
   指针移动时 `return`，且用 `_followSawMotion` 做奇偶交替。
   用户滚轮滚动时指针通常**不动**，所以这条大概率不触发；
   但"滚一下马上移动鼠标"的场景会让跟随延迟一帧。
   需要实测确认是否导致"滚了但标注没跟上"。
3. **`StartScrollFollow` 在 `_liveMode` 下直接 `return`**（L507）。
   实时缩放模式下按 F 会**静默无效**，用户没有任何提示。
   → 应该至少在状态栏/HUD 给出"缩放模式下不可用"的反馈。
4. **`SetExcludedFromCapture(Handle, true)` 后若异常退出没有还原**。
   `StopScrollFollow` 里有 `try/catch` 还原，但如果进程被 `taskkill`，
   `WDA_EXCLUDEFROMCAPTURE` 随窗口销毁自动失效，这个应该没问题；
   但**切换页面 / 最小化 / 重新激活 overlay** 时是否成对调用需要审计。
5. **`ShiftAnnotations` 会平移 `_current`（正在画的那一笔）**（L747），
   但 `FollowTick` 在 `_drawing` 时直接 `return`（L610），
   所以这段代码在正常路径下**不可达**——可能是死代码，也可能是
   通过 `TestExtendStroke` 之类的钩子走到。需要确认，避免留下隐藏状态不一致。
6. **滚轮穿透**：`OnMouseWheel`（L1337）里用
   `Native.IsScrollableWindowUnder(screen)` 判断是否把滚轮转发给下层窗口。
   如果判断失败（比如 Chrome 的渲染窗口层级较深），滚轮会被自己消费掉，
   表现为"滚了没反应"。需要实测 + 可能要多探测几层父窗口。

**修复方向**：

- **A. 实现 `_followMisses` 的退出逻辑**：连续 `N`（建议 15–20）帧无位移
  且无滚轮输入时，自动 `StopScrollFollow()` 并给出状态提示；
  任何滚轮事件（`NoteManualScroll`）清零计数。
- **B. 给 F 切换加可见反馈**：overlay 上画一个短暂的 HUD 提示
  （"跟随滚动：开 / 关"），并在 `_liveMode` 下提示不可用。
- **C. 审计 `SetExcludedFromCapture` 的成对调用**，
  在 `DeactivateOverlay` / 窗口销毁 / 页面切换处确保还原。
- **D. 清理或修正 `ShiftAnnotations` 对 `_current` 的平移**（L747）。
- **E. 实测滚轮穿透**：用真实的 Chrome / 记事本窗口验证
  `IsScrollableWindowUnder` 的判定，必要时扩大父窗口探测层数。

**验收**：扩展 `follow_probe.cs`（或新增 `follow_integration_probe.cs`）：
1. 连续喂 N 帧无位移 → 断言 `ScrollFollowActive == false`（自动退出）。
2. 喂一帧滚轮输入 → 断言计数清零、模式保持。
3. `_liveMode` 下调用 `StartScrollFollow` → 断言 `ScrollFollowActive == false`
   **且**产生了用户可见提示（可通过事件或测试钩子断言）。
4. `StartScrollFollow` / `StopScrollFollow` 成对调用后，
   `SetExcludedFromCapture` 的最终状态为"未排除"（需要给 `Native` 加一个
   可查询的测试钩子，或统计调用次数）。
5. 现有 18 项全部保持通过。

---

### 2.5 【其他】上一轮的修复记录与仍然存在的陷阱

**上一轮（第 5 轮）已修复的缺陷与验证结果：**

| 项目 | 状态 | 证据 |
|---|---|---|
| `README.md` 内容陈旧 | ✅ 已重写 | 覆盖区域截图/复制、文字/马赛克/聚光灯、修饰键、画笔光标、跟随滚动；`verify_readme.py` **7/7 PASS** |
| `e2e_test.py` 断言过期 | ✅ 无需修改 | `Ctrl+S` 已按新语义断言，**27/27 PASS** |
| `verify_upgrade.py` 不可用 | ✅ 已修复 | 从 22/24 修到 **26/26 PASS**（4 处均为测试脚本缺陷，见下） |
| `check_tray.py` 无汇总行 | ✅ 已重写 | 6/6 PASS 且 **exit 0**，新增负向对照 |
| 马赛克/聚光灯/文字烙进保存的截图 | ✅ 已覆盖 | `tools_probe.cs` 断言 `RenderComposite` 输出，Mosaic `diff=1.41%`、Spotlight `92.90%` |
| `verify_ui.py` / `capture_ui.py` 重跑 | ✅ 已重跑 | `verify_ui.py` **81/81 PASS** |
| 第二次双击 EXE 无反应（**真实产品 BUG**） | ✅ 已修复 | `RegisterWindowMessage` 方案，见 2.5.1 |
| 抢不到前台时文字工具静默丢弃（**真实产品 BUG**） | ✅ 已修复 | `ForceForeground()`，见 2.5.2 |

**2.5.1 激活消息被系统丢弃（真实产品 BUG）**

原实现把 `WM_SCREENPEN_ACTIVATE = WM_APP + 0x51` 广播给 `HWND_BROADCAST`，
`PostMessage` 返回 `True` 但**消息被静默丢弃**，导致"程序已在运行时再双击
EXE 没反应"。根因用 `build\broadcast_probe.cs` 证实：

- 广播**只投递 `< WM_USER (0x400)` 的消息**。实测 `0x3FF` → 3 个顶层窗口全收到；
  `0x400`（`WM_USER`）与 `0x8051`（`WM_APP+0x51`）→ **一个都没收到**。
- 但 `RegisterWindowMessage` 返回的 id（0xC000–0xFFFF）**可以**正常广播（实测 `0xC2EA` 成功）。

修复：`Native.SCREENPEN_ACTIVATE_MESSAGE = "ScreenPen.Activate.9F2A7C14"` +
`RegisterWindowMessage`，`Shell.cs` 广播给 `Native.HWND_BROADCAST`。
**结论：永远不要把 `>= WM_USER` 的消息广播给 `HWND_BROADCAST`，改用 `RegisterWindowMessage`。**

**2.5.2 前台被别的进程占用时，文字标注会被自己丢掉（真实产品 BUG）**

`BeginTextEditing()` 创建 `TextBox` 后调用 `Focus()`。若 overlay 没能成为前台窗口
（Windows 前台锁会拒绝 `SetForegroundWindow`），`Focus()` 失败 → 立刻触发
`LostFocus` → `CommitTextEditing(true)` → 空文本被丢弃 → 编辑器当场消失。

这不是理论问题：跑 `run_probes.ps1` 时，只要桌面上有别的窗口占着前台
（本轮实测是残留的"设置"UWP 窗口 / 任务栏），`tools_probe` 的
`text editor opens`、`editor accepts typed content`、`text became a stroke`
就三项全红；前台正常时全绿。**症状会伪装成产品缺陷，实际是环境依赖。**

修复：`Native.ForceForeground(hwnd)` —— 先 `AttachThreadInput` 把自己的输入队列
挂到当前前台线程上，再 `BringWindowToTop` + `SetForegroundWindow` + `SetActiveWindow`，
最后解挂；`ActivateOverlay` / `ResumeAfterCapture` / `BeginTextEditing` 都改用它。
另外 `TextEditorLostFocus` 增加自愈：空文本 + 前台属于别的进程时，重新抢前台并
保留编辑器，而不是直接提交丢弃。

验证：`build\hostile_focus_probe.cs` 先起一个别的进程占住前台，再断言 overlay
夺回前台、编辑器存活、文字成为笔画 → **ALL PASS**。

**仍然存在的陷阱（写测试前必读）：**

| 陷阱 | 说明 |
|---|---|
| `GetWindowTextW` 跨进程返回**陈旧缓存** | 它不发 `WM_GETTEXT`，只回内核缓存的标题。实测 `WM_SETTEXT` 成 `F2` 后，`GetWindowTextW` 仍回 `'Ctrl+Alt+D'`，而 `WM_GETTEXT` 回 `'F2'`。**跨进程读控件文本必须用 `WM_GETTEXT`（0x000D），先 `WM_GETTEXTLENGTH`（0x000E）。** |
| 跨进程**无法**向托管 `TextBox` 注入按键 | `keybd_event`、`PostMessage`、跨进程 `SendMessage` 全部无效（即使用 ALT 技巧让 `GetForegroundWindow` 与 `GetGUIThreadInfo.hwndFocus` 都正确指向该框）。**按键捕获只能在进程内验证**（`build\hotkey_record_probe.cs`），保存链路用 `WM_SETTEXT` + `BM_CLICK`。 |
| 标量 `diff()` **不是形状指纹** | 实测碰撞：`pen=0.008721719457013574` vs `shift->line=0.008653846153846154`。要用 bbox 宽高 / 填充率 / 长宽比 / 墨迹像素数。 |
| **直线拖拽**会让画笔和 Shift 直线几何全等 | 两者都是 `175x89, fill≈0.048, aspect=1.97`。这是**测试脚本假象**，不是产品缺陷。修法：让拖拽走**弧线**（沿弦的垂直方向鼓起 `amp=46`），直线/矩形/椭圆/箭头仍由两端点推导，bbox 不变。 |
| 马赛克是**均值色块**滤波 | 在平坦区域上像素完全一致，`diff == 0.00%` 是**正确**的，不是缺陷。测马赛克必须选有纹理的区域（本轮用 `(1300,700)`，纹理 `0.96–1.000`，`diff=7.30%`）。 |
| `GetFocus()` 跨进程永远返回 0 | 用 `GetGUIThreadInfo(threadId, &GUITHREADINFO)` 读 `hwndFocus`。 |
| WinForms 子控件类名被**改写** | 实际是 `WindowsForms10.EDIT.app.0.34f5582_r6_ad1` 之类，**不是** `Edit`/`Static`/`Button`。按 `".EDIT."` 这类**子串**匹配，精确匹配会静默返回 0 个子控件。 |
| `verify_ui.py` 平铺 glob `build/shots/*.png` | 任何临时 PNG 留在那里都会让 `verify_ui.py` 报 `size mismatch`。 |
| 高 DPI 下测量窗口必须声明感知 | 不声明（如普通 `pwsh`）时 `GetWindowRect` 会被虚拟化，真实 2366x1704 的窗口回报 1183x852。先 `SetThreadDpiAwarenessContext(-4)`。 |
| `SettingsForm` 用 `AutoScaleMode.None` + 硬编码像素 `Bounds` | 本机 200% DPI（192），9pt 字高 **31px** 而按钮只有 30–32px，必然裁字。`AutoScaleMode.Dpi/Font` **不会**缩放硬编码 `Bounds`；**只有 `Form.Scale(new SizeF(s,s))` 有效**，且它只遍历**已挂到父控件**的控件树、**不幂等**、**不碰 `Font`**。 |
| 截图/断言用的进程要区分 | 探针**绝不能**驱动真实模态对话框，用 `PromptOverride`（**3 个参数**）；`SendMessage(BM_CLICK)` 会阻塞，后面可能弹模态时改用 `PostMessageW`。 |
| 任何 ctypes 返回 HANDLE/指针的 P/Invoke | 必须声明 `restype = ctypes.c_void_p` 和 `argtypes`，否则 ctypes 按 `c_int` 截断 64 位句柄 → `access violation writing 0x0000000000000000`。 |
| 修改用户配置的脚本 | 必须在 `finally` 里恢复**并回读校验**；比较配置用**逐键值**，不要比 `Get-FileHash`。 |
| `run_probes.ps1` 的 FAIL 扫描 | `Select-String` 默认不区分大小写，会把 `0 failed` 也算命中，必须加 `-CaseSensitive`。 |
| 托盘图标不能用 `ToolbarWindow32` 查 | Win11 托盘是 XAML 的（`Shell_TrayWnd` 下只有 `TrayNotifyWnd`/`ReBar`/`DesktopWindowXamlSource`，**没有** `ToolbarWindow32`）。改用 `Shell_NotifyIconGetRect`（`check_tray.py` 已用，`uID=1`）。 |

---

## 3. 关键设计约束（改代码前务必理解）

### 3.1 坐标系统

- 所有笔画点存在**屏幕（虚拟桌面）坐标系**，不是客户区坐标。
- 渲染时通过 `ScreenToClientF()` 变换到客户区。
- `_screenBounds = Native.VirtualScreen()`，多显示器时要保证 overlay 覆盖全部屏幕。
- 跟随滚动平移的是**屏幕坐标**下的点（`ShiftAnnotations`）。

### 3.2 快捷键的分层（重要，容易改错）

- **只有"动作类"快捷键**（开关画笔、实时缩放、截图、复制、放大、缩小）
  走 `RegisterHotKey`，是**全局**的。
- **单键字母**（`P/L/R/O/A/T/M/K`、`E`、`F`、`[`、`]`、数字键）
  是 overlay 的本地 `KeyDown`，**故意不注册全局**，否则会抢占全系统打字输入。
- 因此 `E`（清空标注）与 `E`（画笔）的冲突是**本地 vs 本地**冲突，
  两者都在 `seenLocal` 分组里校验。
- 重复键在运行时的解析顺序是**确定的**：`ToolForKey` 依次检查
  `PenKey → LineKey → RectKey → EllipseKey → ArrowKey → TextKey → MosaicKey → SpotlightKey`，
  返回第一个匹配；`ClearKey` 在 `OverlayKeyDown` 的 `default:` 分支里**最后**检查，
  所以工具键永远压制清空键。**改这个顺序会改变用户已保存配置的行为语义。**
- ⚠️ **本地键必须先让位给正在打字的文字编辑器**（§1.5.2 的真实 BUG）。
  `OverlayForm` 设了 `KeyPreview = true`，所以 `AppContext2.OverlayKeyDown` **先于**
  聚焦的 `TextBox` 收到按键；不加守卫时 `L/P/O/E/A/B/1`–`8` 会被当成命令吞掉，
  用户"打字打到一半就打不进去了"。
  现在 `OverlayKeyDown` 的第一句就是：
  ```csharp
  if (_overlay.IsEditingText) return;   // 不设 e.Handled，把键原样还给输入框
  ```
  **将来往 `OverlayKeyDown` 里加任何单键命令，都必须加在这句之后**，
  并且**绝不能设 `e.Handled`**（否则等于把用户的字符吃掉）。
  `Tab` 更早一层：它在 `OverlayForm.ProcessCmdKey`（消息层，比 `KeyDown` 更早）里就必须放行，
  否则 `_tabHeld` 会闩在 `true`，用户提交文字后的**下一次拖动会变成椭圆**。
- **框选选择器（`RegionSelectForm`）自己的键也是本地键**：`Esc` / `Enter` / `Ctrl+C` /
  `Ctrl+A` / 方向键 / 裸 `P`。裸 `P` 的 `if` **必须写在 `Ctrl` 分支之后**——
  写在前面就会永久遮蔽将来可能加的 `Ctrl+P`。
- **`RegionAction` 的数值是契约**：`Cancelled = 0` / `SaveToFile = 1` /
  `CopyToClipboard = 2` / `PinToDesktop = 3` / `Ocr = 4` / `RecordGif = 5`。`0` 同时被当作
  "未指定"的哨兵（构造时 `defaultAction == Cancelled` 会归一化成 `SaveToFile`；
  `Finish()` 里选区为空也会退化成 `Cancelled`）。**新动作只能追加，不要插队或重排。**
- **工具条按数量驱动，不要退回具名成员**。`RegionSelectForm` 里布局的唯一来源是
  `BtnCount` / `_btn[]` / `_btnLabels[]`，下标到动作的唯一映射是 `ActionForButton(int)`。
  **加按钮只需要动 `BtnCount` 和这几张表**；`LayoutToolbar()` 的四级降级
  （自然标签 → 短标签 → 等比收窄 → 等宽兜底）会自动覆盖新按钮。
  ⚠️ 不要再写 `_btnSave` / `_btnCopy` 这种成员——§1.7.1 就是因为这个才把
  OCR/GIF 挤出了工具条。探针也必须用 `ToolbarButtonCountForTest` **遍历**，
  写死"恰好 4 个"会让新按钮无声地跑到窗口外。
- **选区的移动/缩放必须"逐条边钳制"**。`ResizeFrom` 单独夹住每条边，
  而不是先算出矩形再整体 `ClampToClient`——后者会让用户**没抓的那条边也动**，
  看起来就像选区在"自己跳"。移动用 `ClampToClient`（只改位置不改尺寸）。
- **`WS_EX_TRANSPARENT` 有两个来源，必须走 `ApplyClickThrough()` 合并**：
  ① 正在转发滚轮的 180ms 瞬态（`_wheelPassThrough`）；② 跟随模式期间的
  `ScrollFollowClickThrough`（`_followClickThrough`）。**任何一处直接调
  `Native.SetClickThrough` 都会把另一处的状态抹掉**，而且窗口样式写漏一次就
  等于标注层永久失去鼠标。`SetClickThrough` 本身**不碰 `WS_EX_LAYERED`**——
  旧版本 enable 时加上、disable 时不清，每调用一次就永久 layered。
- **`AppContext2._quitting` 是必须的**：`StopRecordingIfAny()` 会**同步**触发
  `GifRecorder.Finished`，而那个处理器会 `ShowDialog` 一个结果窗口。
  关机路径里弹模态框 = 进程永远退不出去。

### 3.3 设置持久化

- 平铺 JSON，放在 EXE 同目录 `ScreenPen.settings.json`，**共 49 个键**，绿色免安装。
- ⚠️ **`build\obj\` 整个目录都不入版本库**（`.gitignore`）且常被清理，里面**没有**长期保存的
  出厂基线：`settings.backup.json` / `pristine.settings.json` 在当前工作区**已不存在**。
  现在的实际基线就是 **`dist\ScreenPen.settings.json` 本身**（1355 bytes / 51 个 CRLF / 49 个键），
  以及脚本自己抓的快照 `build\obj\pre_suite.json`、`build\obj\e2e_pre.json`（各 1366 bytes）。
  需要长期基线时**现场重新生成**：删掉 `dist\ScreenPen.settings.json` → 启动一次 EXE → 拷出来。
- ⚠️ **校验时不要比文件哈希**：格式化过的 JSON 与程序写出来的紧凑版**键、值、顺序完全一致、
  字节却不同**。要按**值**逐键比对（下面的脚本用 `ConvertFrom-Json` 逐键比，不碰哈希）：

  ```powershell
  $a = Get-Content -Raw dist\ScreenPen.settings.json   | ConvertFrom-Json
  $b = Get-Content -Raw build\obj\pre_suite.json       | ConvertFrom-Json   # 你自己抓的快照
  $diff = 0
  foreach ($p in $b.PSObject.Properties) {
    $av = $a.PSObject.Properties[$p.Name]
    if (-not $av -or "$($p.Value)" -ne "$($av.Value)") { Write-Host "DIFF $($p.Name)"; $diff++ }
  }
  Write-Host "diffs = $diff"   # 期望 0
  ```
- 默认值：`ToggleKey=Ctrl+Alt+D`、`LiveZoomKey=Ctrl+Alt+Z`、
  `ScreenshotKey=Ctrl+Alt+S`、`CopyShotKey=Ctrl+Alt+C`、
  `ClearKey=E`、`UndoKey=Ctrl+Z`、`SaveKey=Ctrl+S`、`CopyKey=Ctrl+C`、
  `ZoomInKey=Ctrl+Up`、`ZoomOutKey=Ctrl+Down`、
  工具键 `P/L/R/O/A/T/M/K`、`FollowKey=F`、
  `PenWidth=3`、`MinPenWidth=1`、`MaxPenWidth=20`、
  `ZoomStepPercent=25`、`MinZoom=0.5`、`MaxZoom=8`。
- 调色板默认 8 色：`#FF3B30 #FF9500 #FFCC00 #34C759 #007AFF #AF52DE #FFFFFF #000000`。
- **本轮加固后的两条写入侧约束**（见 §1.5.4 #8/#11）：
  - `SettingsStore.Save()` 是**原子**的（写 `.tmp` → `File.Replace`），
    并且**返回 `bool`**——调用方必须检查，`false` 要提示用户（`AppContext2.OpenSettings`）。
  - `Config.Clamp()` 会对 `ScreenshotFormat` 做**白名单**（`png`/`jpg`/`bmp`，其它回退 `png`），
    因为该值会被拼进文件名；改动格式相关的代码时**不要绕过 `Clamp()`**。

### 3.4 单实例

- `Mutex("Local\ScreenPen.SingleInstance")`。
- **第二次启动只会广播激活消息，不会打开设置窗口**。
  要拿设置界面：先 `taskkill /F /IM ScreenPen.exe`，再**不带 `--tray`** 启动。

### 3.5 视觉规范

```
Bg       = RGB(24, 26, 32)
Card     = RGB(34, 37, 45)
CardAlt  = RGB(41, 45, 54)
Border   = RGB(56, 61, 72)
TextMain = RGB(236, 238, 242)
TextSub  = RGB(148, 156, 172)
Accent   = RGB(232, 160, 0)
字体      = Microsoft YaHei UI
所有 UI 文案用中文
```

`AutoScaleMode.None` 必须保留（否则显式像素 `Bounds` 会被缩放破坏）。

### 3.6 已踩过的坑（别重蹈覆辙）

| 坑 | 症状 | 解法 |
|---|---|---|
| `TextBox.BackColor` 传 `A < 255` 的颜色 | `ArgumentException: 控件不支持透明的背景色。` | 用不透明色 |
| `GetWindowTextW` 跨进程读 TextBox | 拿到过期文本 | `SendMessageW(WM_GETTEXT)` |
| WinForms 控件类名 | 不是 `"Edit"` | 匹配 `WindowsForms10.EDIT` 子串，忽略大小写 |
| `GraphicsPath` 填充 | 默认 `FillMode.Alternate` | 需要非零环绕时显式设 `FillMode.Winding` |
| 图片缩放插值 | 无 `InterpolationMode.None` | 用 `NearestNeighbor` + `PixelOffsetMode.Half` |
| `ICONINFO.fIcon` 用于光标 | Windows 把热点重定到中心，丢掉笔尖 | 必须 `fIcon = false` |
| 旧 csc 编译 | `error CS1513` 等 | 删除目标 exe 后重编；严格 C# 5 |
| overlay 里同步全屏 `CaptureGray` | 绘图卡顿、掉点、所有工具画不出东西 | `ActivateOverlay` 里**总是**先 `StopScrollFollow()`，跟随改为 `F` 显式开启 |
| `CommitTextEditing` 重入 | `NullReferenceException` | `_committingText` 守卫 + 先摘事件再 `Controls.Remove` |
| `Dock=Fill` 的多页面 | 后添加的页面盖住选中的 | `ShowPage` 里把非目标页面 `Controls.Remove` 掉 |
| `Control.Scale()` 只遍历**已挂载**的子树 | 非当前页保持 96 DPI（卡片 780px 躺在 1880px 页面里） | `ApplyDpiScale` 先**全部摘掉** → 缩放窗体 → 逐个 `p.Scale` → 再挂回 |
| 缩放当前页两次 | 当前页几何是别人的 2 倍 | 同上：**先摘再缩放**，避免 `Scale()` 和手工 `p.Scale` 叠加 |
| `AutoScaleMode.Dpi/.Font` 想当然能自动缩放 | 192 DPI 下 `ClientSize`/`Bounds`/字体**全都不变** | 显式 `Form.Scale(SizeF)`；`AutoScaleMode.None` 必须保留 |
| 用 `pwsh` 量真实窗口尺寸 | 拿到减半的 `1183×852`，误判"修复没生效" | 先 `SetThreadDpiAwarenessContext(-4)`；用 `SM_CXSCREEN` 自检 |
| `Dock=Fill` 面板想用 `Size` 定滚动范围 | 滚不动 | 用 `AutoScrollMinSize` |
| `Select-String -Pattern 'FAILED'` | 每个探针都打印小写 `0 failed`，**全部误判为失败** | 加 `-CaseSensitive` |
| `KeyPreview = true` 的窗体 + 正在输入的 `TextBox` | 用户按 `L`/`P`/`O`/`E`/`A` 打字时工具被切换、清空被触发，**字符进不去**（真实 BUG，§1.5.2） | 命令分发前先 `if (_overlay.IsEditingText) return;`——**不设 `e.Handled`**，让键继续派发给聚焦控件 |
| `Tab` 在打字时被 overlay 吞掉 | `_tabHeld` 闩在 `true`，提交文字后**下一次拖动变椭圆** | `ProcessCmdKey` 里 `_textEditor != null` 时 `return base.ProcessCmdKey(...)`（消息层比 `KeyDown` 更早，只有一个放行点） |
| 用 `Form.Scale()` 实现"滚轮缩放贴图" | 比例累乘、取整逐帧漂移，缩不回原尺寸 | 保存 `_baseSize`，每次**从原始尺寸重算** `Size = _baseSize * _scale`（§1.5.1） |
| 贴图窗口拖动时用 `e.Location` 算增量 | 窗口自己在动，相对位移反复归零，**拖动一开始就卡死** | 用 `Cursor.Position`（屏幕坐标）的增量，`_dragCursorScreen` 记起点 |
| 把位图交给 `PinForm` 后忘了把本地引用置 `null` | 外层 `finally { cropped.Dispose(); }` 释放**贴图正在显示**的位图 → 贴图变黑/崩溃 | 交出所有权时立即 `cropped = null;`（`AppContext2.cs` L615–616），`PinScreenshot` 里同样 `bmp = null;`（L701） |
| `PinForm.Dispose(bool)` 无条件释放 `_image` | 构造函数抛异常时调用方仍持有位图，终结器路径**二次释放**同一张位图 | 只在 `disposing == true` 时释放；`disposing == false`（终结器）什么都不做 |
| 托盘退出时不关贴图窗口 | 进程退出后贴图**继续浮在桌面上** | `Quit()` 与 `Dispose(bool)` 都调用 `CloseAllPins()`（§1.5.1） |
| 遍历 `_pins` 的同时 `Close()` | `Close()` 会泵消息、可能重入，**枚举中改列表是硬崩溃** | 先 `PinForm[] snapshot = _pins.ToArray(); _pins.Clear();` 再遍历快照 |
| 在 `OnResize` 里重新跑 `Scale()` / `ApplyDpiScale()` | 比例再乘一遍，卡片宽度不再等于 `780 × DPI 因子`（`measure_ui.cs` 会红） | 窗口**刻意不 reflow**；`OnResize` 只重跑 `LayoutFooter()` + `RefreshScrollExtents()`，用 `_ready` 门控构造函数期间的伪 resize |
| 在 `ClientSize` 之前设 `MinimumSize` | `MinimumSize` 会夹住 `Size`，刚排好的（已按 DPI 放大的）布局被压小 | `ApplyMinimumSize()` 必须是构造函数**最后一步**；最小值 = 96 DPI 设计值 × DPI + 非客户区边框（`Width - ClientSize.Width`） |
| 设置写入用 `Delete` + `Move` 两步 | 两步之间崩溃/断电 → **一份配置都不剩**，静默回出厂值 | 写 `.tmp` → `File.Replace(tmp, path, null)`（无文件时 `File.Move`）；失败删 tmp 返回 `false`。`Save` 的返回值**必须检查**，否则"保存失败静默"（§1.5.4 #8/#9） |
| `TopMost` 窗口下弹 `MessageBox` | 崩溃提示被 overlay/贴图**盖住**，用户只看到"卡住" | 弹框前把 `Application.OpenForms` 逐个 `TopMost = false`，并用 `MessageBoxOptions.DefaultDesktopOnly` |
| `Bitmap raw` 在 `try` 内部声明 | 中途抛异常时**全屏位图泄漏**（几十 MB/次） | 声明移到 `try` 之外，`finally` 里无条件 `Dispose` 并置 `null` |

---

## 4. 测试资产清单

| 文件 | 类型 | 上次结果 | 说明 |
|---|---|---|---|
| **`build\run_probes.ps1`** | **总入口** | **ALL PROBES PASSED (22/22)** | 编译并运行下面 22 个进程内探针，逐个打印 PASS/FAIL。**源码列表已含 18 个 `.cs`**，并且会自动追加 3 个 WPF 引用（`Gif.cs`/`GifRecorder.cs` 需要）。支持 `-Only <名字>` 单跑 |
| `build\ux_fix_probe.cs` | 进程内 | **ALL UX FIX CHECKS PASSED** | **本轮新增**：四个反馈问题的回归。① 工具条 6 按钮 / 顺序 / 点击落点；② 选区移动 + 8 把手缩放 + 逐边锚定 + 出屏钳制 + 小选区隐藏把手；③ `GifResultForm` 路径可见 / 帧数时长体积 / 复制按钮 / 各种宽度下路径框不出窗 / 空路径不崩；④ 滚轮穿透决策 + `WS_EX_TRANSPARENT` 真置真撤 + 前后 ex-style 逐位相同 + `ScrollFollowClickThrough` 跟随期穿透与退出恢复；⑤ **真实按键**验证 `Esc` 钩子（`keybd_event` 注入 + 泵消息） |
| `build\gif_delta_probe.cs` | 进程内 | **6/6 PASS** | **本轮新增**：GIF 字节级回填。**第一块的偏移量是 13**（6 字节签名 + 7 字节逻辑屏幕描述符），延时要能被独立解码器读回 |
| `build\gif_ocr_probe.cs` | 进程内 | **49/49 PASS** | **第七轮新增**：GIF（延时 / 循环块 / 合并帧 / 上限 / 原子写盘）+ OCR（识别 / 放大 / 坐标换算 / 语言标签 / 工作线程超时 / `RawText` 兜底）。**探针必须先 `SetProcessDpiAwarenessContext(-4)` 并把测试字体用 `GraphicsUnit.Pixel` 建**，否则 200% DPI 下文字被裁、OCR 必然空手而归 |
| `build\gif_recorder_probe.cs` | 进程内 | **29/29 PASS** | **第七轮新增**：录制真的在采样（驱动一个会动的 `Form`，静止桌面会合并成 1 帧而假失败）、写出的帧数与解码数一致、帧尺寸等于选区、时长上限、取消、倒计时、极小区域拒绝、**红框没被烤进画面** |
| `build\ocr_gif_wiring_probe.cs` | 进程内 | **64/64 PASS** | **第七轮新增，第八轮扩写**：接线正确性。`RegionAction` 0/1/2/3 **取值不变**、工具条有**全部 6 个按钮**（按 `ToolbarButtonCountForTest` 遍历，不再写死四个具名矩形）、点 `识别`/`GIF` 按钮落到与快捷键相同的动作、`O`/`G` 选中新动作而 **`Ctrl+O`/`Ctrl+G` 仍空闲**、原有 `P`/`Ctrl+C`/`Enter`/`Esc` 都没坏、10 个默认热键互不重复、`OcrResultForm` 显示文本、设置窗口读写 + 落盘回环、越界值 `Clamp` |

| `build\tier1_probe.cs` | 进程内 | **78 PASS / 0 FAIL** | **本轮新增**：第一梯队四项功能。9 组：枚举与笔画、撤销重做（多级 + 序号回收）、序号徽标编号、荧光笔（**alpha 独立性**：改透明度不影响其它笔画）、箭头三样式（**按像素差**断言三种样式几何确实不同）、窗口几何（DWM 物理边界）、`PixelAt`、美化器（圆角不泄漏内容用 `BeautifyRadius=0` 差分验证）、设置回环 |
| `build\layout_cards.cs` | 进程内 | **21 PASS / 0 FAIL** | **本轮新增**：卡片布局健全性。① 每个控件四面都在自己的卡内；② 卡片互不重叠；③ 卡片内容下方无 >40px 空白；④ 溢出页的 `AutoScrollMinSize` 能滚到最后一卡。**这是"卡片底部大片空白"的回归防线** |
| `build\hotkey_conflict_probe.cs` | 进程内 | **30/30 PASS** | 快捷键冲突 UX（含 `PromptOverride`）。**本轮改法**：键行数不再硬编码，改为与窗体自己的 `_bindings` 集合比数量 |
| `build\region_probe.cs` | 进程内 | **PASS** | 区域截图、裁剪、剪贴板、保存、取消 |
| `build\follow_probe.cs` | 进程内 | **26 `[PASS]` / 0 `[FAIL]`** | 滚动跟随（算法层 + F 集成层 + 滚轮路由 + 空闲退出 + 拒绝理由） |
| `build\tools_probe.cs` | 进程内 | **PASS** | 工具、修饰键、渲染、点数断言 |
| `build\brush_probe.cs` | 进程内 | **PASS** | 画笔光标 SVG |
| `build\smooth_probe.cs` | 进程内 | **14/14 PASS** | 画笔平滑：折线 vs 样条面数、`Densify` 间隙 |
| `build\footer_probe.cs` | 进程内 | **12/12 PASS** | 页脚按钮不重叠、状态栏不被压 |
| `build\layout_probe.cs` | 进程内 | **4/4 PASS** | 每页内容可容纳/可滚动、卡片子控件、**0 个 Label 被裁** |
| `build\measure_ui.cs` | 进程内 | **ALL PASS** | 缩放后真实几何：窗口、卡片宽度、按钮/标签高度、头部 |
| `build\hotkey_record_probe.cs` | 进程内 | **8/8 PASS** | **快捷键捕获的唯一可靠证据**：真 `WM_KEYDOWN` + 真修饰键态 |
| `build\text_focus_probe.cs` | 进程内 | **ALL PASS** | 前台正常时文字编辑器存活并落成笔画 |
| `build\hostile_focus_probe.cs` | 进程内 | **ALL PASS** | **别的进程占着前台时**，overlay 夺回前台、编辑器存活 |
| `build\pin_probe.cs` | 进程内 | **57 `[PASS]` / 0 `[FAIL]`** | **第五轮新增，第八轮扩写**：贴图。选择器工具条（现在遍历全部按钮查尺寸/在窗内/不重叠）+ `PinToDesktop` 裁切；`PinForm` 几何（缩放上下限、往返无漂移、滚轮 1.12×、光标锚点、拖动、关闭按钮随窗缩、`Esc` 关闭、释放位图）；位图所有权（存活/清引用/幂等/`null` 图）；多贴图互不影响；控制器集成（`PinScreenshot` 跟踪、始终置顶） |
| `build\typing_probe.cs` | 进程内 | **19 `[PASS]` / 0 `[FAIL]`** | **本轮新增**：打字时单键命令不抢键。命令键清单 `L/P/O/E/A/B/1/M/R/T/F/0`；`Tab` 不闩 `_tabHeld`；`WM_CHAR` 送入后文字成笔画 |
| `build\measure_toolbar.cs` | 进程内 | **ALL TOOLBAR MEASUREMENTS PASSED** | **第五轮新增，第八轮扩写**：5 种桌面宽度（2880/1920/1280/900/640）× **5 种默认动作**（新增 `Ocr` / `RecordGif`）× **6 个按钮**，用 `TextRenderer.MeasureText` 量**每个按钮自己的标签**是否真的装得下。这是 §1.5.5 工具条标签被裁切的**回归防线**——老探针只查"不重叠、在窗内"，几何全对而文字全被裁 |
| `build\render_ui.cs` | 诊断 | — | 逐页渲染 PNG → `build\shots\render\`，**供人眼看** |
| `build\render_scroll.cs` | 诊断 | — | **本轮新增**：按 `scrollY` 渲染设置页的**真实视口**（`render_ui.cs` 渲染的是整页全高）。正是它截到了 `_chkSmooth` 与说明文字重叠——纯算术探针测不到这种缺陷 |
| **`build\run_feasibility.ps1`** | **可行性入口** | **ALL FEASIBILITY PROBES PASSED (4 个 / 38 条)** | **本轮新增**：跑下面 4 个**独立**探针（不编译程序源码）。它们量的是**操作系统能力**而不是程序行为，所以失败的含义是"本机/本 OS 版本没有这个 API"，不是"程序回归了"，因此刻意**不放进 `run_probes.ps1`**。`gif_probe` 需要额外的 WPF 引用（`WindowsBase.dll` 在 **`WPF\` 子目录**而不是 Framework64 根目录；`System.Xaml.dll` 必须取 Framework64 的，GAC 那份不是文件，会 `error CS0006`） |
| `build\ocr_probe2.cs` | 可行性 | **9 passed / 0 failed** | 反射绑定 WinRT `Windows.Media.Ocr`。**注意运行时要 .NET 4.8**（编译器虽是 C# 5，但运行时是 4.8.09221）；`System.Runtime.WindowsRuntime`（`AsTask`/`GetAwaiter`）从 .NET 4.5 才有，所以 ShareX 那种 `await` 写法不可用；winrt 异步的返回值是 `__ComObject` 且 `GetInterfaces()` 为空，必须用泛型 `CastTo<T>` + 轮询 `Status` 再 `GetResults()`。**v1（`ocr_probe.cs`）已删除**——它在第一个异步调用就失败，v2 文件头记录了这个原因 |
| `build\feature_probe.cs` | 可行性 | **10 passed / 0 failed** | 证明系统**没有**内置位图条码解码器 → 要加就得引 QRCoder(MIT) / ZXing.Net(Apache-2.0)，两者都是纯 C#，许可证干净 |
| `build\roadmap_probe.cs` | 可行性 | **12 passed / 0 failed** | 用合成滚动帧独立复现行/列剖面相关算法，证明滚动拼接截图在本项目的算法基础上可行 |
| `build\gif_probe.cs` | 可行性 | **7 passed / 0 failed** | 用 WPF 编码器写出 30 帧 800×600 动画 GIF（61 KB，约 2 KB/帧），**零外部依赖**——不需要 ffmpeg（省掉 ~70 MB 和 LGPL/GPL 许可问题；`gifski` 是 AGPL-3.0-or-later，MP4 路线已排除） |
| `build\verify_hotkeys.py` | 跨进程 E2E | **20/20 PASS** | 快捷键重绑 + 冲突保存 + 读回校验 |
| `build\verify_brush.py` | 跨进程 | **17/17 PASS** | 画笔光标 |
| `build\verify_ui.py` | 跨进程 | **81/81 PASS** | UI 结构（**平铺 glob `build\shots\*.png`，别在里面留临时图**） |
| `build\e2e_test.py` | 跨进程 | **27/27 PASS** | 端到端（跑得慢，约 3–5 分钟，不要误判为卡死） |
| `build\check_tray.py` | 跨进程 | **6/6 PASS, exit 0** | 托盘图标注册（`Shell_NotifyIconGetRect` + 负向对照） |
| `build\verify_readme.py` | 跨进程 | **7/7 PASS** | README 一致性（**会改写 `dist\ScreenPen.settings.json`，前后要快照/还原**） |
| `build\verify_upgrade.py` | 跨进程 | **26/26 PASS** | 上上轮 7 项需求的综合验收 |
| `build\verify_resize.py` | 跨进程 | **ALL RESIZE CHECKS PASSED**（9 项） | **本轮新增**：设置窗口可调整大小。对真实 `dist\ScreenPen.exe` 断言 `WS_THICKFRAME` / `WS_MAXIMIZEBOX`、放大生效、缩小被下限夹住、页脚按钮在放大后/最小时都在窗内且不溢出、设计尺寸可复原（±2px） |
| `build\diag_save_pipeline.py` | 诊断 | PASS | 证明 `WM_SETTEXT` + `BM_CLICK` 真的驱动保存链路 |
| `build\diag_gettext.py` | 诊断 | PASS | 证明 `GetWindowTextW` 跨进程是陈旧缓存、`WM_GETTEXT` 才是实时值 |
| `build\diag_texture_scan.py` | 诊断 | — | 扫描桌面找有纹理的区域（测马赛克用） |
| `build\diag_tray_rect.py` / `diag_tray_deep.py` | 诊断 | — | 证明 Win11 托盘是 XAML、只能用 `Shell_NotifyIconGetRect` |
| `build\broadcast_probe.cs` | 诊断 | PASS | 证明广播丢弃 `>= WM_USER` 的消息（激活 BUG 的根因） |
| `build\dump_settings_tree.py` | 诊断 | — | 枚举设置窗口子控件树（**绝对屏幕坐标，不能当溢出证据**） |
| `build\diag_prompt.py` | 诊断 | PASS | 验证冲突确认框真的出现 |
| `build\capture_ui.py` | 诊断 | — | 逐页截图 |
| `build\analyse_ui.py` | 诊断 | — | UI 分析 |

**运行顺序建议**：先跑 `run_probes.ps1`（快、可靠），
再跑跨进程 `verify_*.py`（慢、受环境影响）。

**UI 改动的验收流程**（缺一不可）：
1. `run_probes.ps1` —— 机器化断言布局数值；
2. `render_ui.cs` 生成 PNG 后**逐页用眼睛看**（模型可以读图）。
   > 真实教训：程序化探针报 PASS，但用户仍能看到遮挡——**只看数值会漏**。
   > 反过来，`dump_settings_tree.py` 的绝对屏幕坐标**不能**当作溢出证据。

---

## 5. 历次会话新增/修改的文件

> 按**倒序**排列：`5.0` 是本轮，越往后越早。

### 5.0 本轮（四个交互缺陷修复）

| 文件 | 变更 |
|---|---|
| `src\RegionSelectForm.cs` | 工具条由**四个具名成员**改为**数量驱动**（`BtnCount = 6` + `_btn[]` + `_btnLabels[]` + `ActionForButton`），新增 **`Ocr = 3` / `Gif = 4`** 两个按钮位（`BtnCancel` 变 5），`PrimaryIndex()` 支持新默认动作；新增选区编辑：`enum SelDrag`、`HandleRect` / `HitHandle`（角优先）/ `HandlesVisible` / `ResizeFrom`（**逐边钳制**）/ `ClampToClient` / `CursorForDrag` / `UpdateSelectionCursor`；鼠标逻辑拆成 `HandleLeftDown` / `HandleMouseMove` / `HandleLeftUp` 供探针驱动真实手势；测试缝新增 `ToolbarButtonCountForTest` / `ToolbarButtonBoundsForTest` / `OcrButtonBounds` / `GifButtonBounds` / `OcrLabelForTest` / `GifLabelForTest` / `HandleCountForTest` / `HandleBoundsForTest` / `HandlesVisibleForTest` / `CursorAtForTest` / `ClickToolbarButtonForTest` / `SimulatePressAndDrag` / `SimulateRelease`（§1.9.1、§1.9.2） |
| `src\GifResultForm.cs` | **新增（第 18 个源文件）**。GIF 结果窗口：文件名 + `N 帧 N 秒 体积` + **只读可选中的完整路径** + `复制路径` / `在文件夹中显示`（`explorer /select` 高亮文件）/ `打开动图` / `关闭`。**布局全用显式 `SetBounds`**（`Dock` 的反 z-order 解析不可控）；`LayoutChildren()` 开头有 `if (_head == null || _close == null) return;`，因为构造里设 `ClientSize` 会**先触发 `OnResize`**（§1.9.3） |
| `src\KeyboardHook.cs` | **新增（第 19 个源文件）**。录制期间临时挂 `WH_KEYBOARD_LL` 只认 `Esc`；**回调里不做事**（`SynchronizationContext.Post` 转投，否则超过 `LowLevelHooksTimeout` 会被系统摘钩）；**吞掉该 `Esc`**；委托存字段防 GC（§1.9.4） |
| `src\AppContext2.cs` | `rec.Finished` 里的气泡换成 `GifResultForm.ShowResult(...)`（`frames`/`seconds` 在 `Dispose` 前读出）；新增 `_quitting` 标志（关机路径不能弹模态框）；`StartRecordingKeyWatch` / `StopRecordingKeyWatch` / `IsRecordingGif` / `StopGifRecording`；托盘新增 **停止 GIF 录制**（`_stopGifItem`，仅录制期可用）；`RegionScreenshot` 开头：**第二次触发 GIF = 停止**（§1.9.3、§1.9.4） |
| `src\OverlayForm.cs` | 新增 `ForwardWheel` / `BeginWheelPassThrough` / `EndWheelPassThrough` / `ApplyClickThrough` + `_wheelPassThrough` / `_followClickThrough` / `_clickThroughApplied` / `_wheelForwardTick` / `_wheelRestoreTimer`（180ms）；`OnMouseWheel` 改走 `ForwardWheel`；`StartScrollFollow` / `StopScrollFollow` 接上 `ScrollFollowClickThrough`（**此前是死开关**）；测试缝 `ForwardWheelForTest` / `BeginWheelPassThroughForTest` / `EndWheelPassThroughForTest` / `ClickThroughForTest` / `FollowClickThroughForTest`（§1.9.5、§1.9.6） |
| `src\Native.cs` | `SetClickThrough` **不再碰 `WS_EX_LAYERED`**（原来 enable 时加、disable 时不清，等于永久 layered），加 `SWP_FRAMECHANGED` 让 ex-style 真正生效，状态未变则短路；`IsScrollableWindowUnder` 增加**本进程 PID 判定**（含 root 窗口），不再把自己的窗口当成"可滚动文档"；新增 `SendEscape()`（`keybd_event` 注入真实 `Esc`，供探针验证钩子）；补齐 `SWP_NOZORDER` / `SWP_FRAMECHANGED` 常量（§1.9.5） |
| `src\Config.cs` | `ScrollFollowClickThrough` 默认 `true` → **`false`**，注释说明"穿透会让跟随期间画不了新标注，默认应该保留标注能力"（§1.9.6） |
| `src\SettingsStore.cs` | 该键读写迁到 **`ScrollFollowClickThroughV2`**：老配置里那个从未生效过的 `true` 会被忽略，老用户回到安全默认值（§1.9.6） |
| `src\SettingsForm.cs` | 复选框文案改为 **"跟随期间让鼠标点击穿透到下方窗口（可滚动、点链接；此时无法再画新标注）"**，把代价写进 UI |
| `build\ux_fix_probe.cs` | **新增**。五个小节覆盖四个反馈问题 + `Esc` 钩子（§1.9.7） |
| `build\ocr_gif_wiring_probe.cs` | 47 → **64** 项。工具条 6 按钮、按数量遍历、点击 `识别`/`GIF` 按钮的落点、`ButtonIndexAtForTest` 边界 |
| `build\pin_probe.cs` | 工具条检查从四个具名矩形改为**遍历 `ToolbarButtonCountForTest`**（尺寸/在窗内/两两不重叠） |
| `build\measure_toolbar.cs` | 默认动作 3 → **5** 种（加 `Ocr` / `RecordGif`），按钮 4 → **6** 个，粗体主按钮改为 `PrimaryIndexFor(act)` |
| `build\run_probes.ps1` | `$srcs` 加 `GifResultForm.cs` / `KeyboardHook.cs`；`$probes` 加 `ux_fix_probe`；新增 **`-Only <名字>`** 参数单跑探针 |
| `build\run_render.ps1` | **新增**。把 `render_pin` / `render_ui` / `render_scroll` / `render_gifresult` 的编译运行固定下来（**排除 `Program.cs`**，加 3 个 WPF 引用），产物落在 `build\shots\render\`。这些渲染器不在 `$probes` 里，以前只能手敲编译命令 |
| `build\render_gifresult.cs` | **新增**。把新的 GIF 结果窗口渲染成 PNG（`40_gif_result.png` / `41_gif_result_longpath.png`），后者用一个很深的目录验证路径框会滚动到文件名的位置。几何断言看不出"路径到底看不看得清" |
| `build\capture_ui.py` | 新增 `client_crop()`：用 `GetClientRect` + `ClientToScreen` 裁掉非客户区（200% DPI 下是顶部 58px 的标题栏），让 PNG 与 `verify_ui.py` 的客户端常量一致；`capture()` 改为返回图像对象；翻页后**校验页面真的变了**，没变就重试（第一次合成点击有时被激活吞掉，曾把 `01_brush.png` 存成快捷键页）（§1.9.8） |
| `build\verify_ui.py` | 主文字阈值 8000 → **4000**。旧阈值是被标题栏白送的像素撑住的，裁掉标题栏后 `关于` 页只有 5166 px（§1.9.8） |
| `build\verify_readme.py` | 跑之前**字节备份**用户配置、报告前恢复（原来跑一次就等于重置用户设置）；新增 `JSON_KEY_OVERRIDES` 声明 `ScrollFollowClickThrough` → `...V2` 这处**有意的**键名不一致（§1.9.6、§1.9.8） |
| `README.md` | 工具条 4 → 6 按钮（含"框选后仍可移动/缩放"小节）；新增**"滚轮是怎么穿到下层窗口的"**说明；GIF 章节补"怎么停下来"四条 + 结果窗口截图式说明；OCR 章节说明工具条入口；配置样例键名改 `V2`；源码表加 2 个新文件（17 → **19**）；探针表加 `ux_fix_probe` |
| `HANDOVER.md` | **§1.9（本轮五个根因 + 验证表）**、§1.7.1 加"已被用户推翻"标记、§0 源码数 17 → 19 / 探针数 21 → 22、§4 测试资产表、本节、头部版本/体积/sha256 更新 |
| `dist\ScreenPen.exe` | 302,080 → **314,880 B**（重新构建，非手改） |

### 5.1 上一轮（构建与写盘健壮性加固）

| 文件 | 变更 |
|---|---|
| `src\Gif.cs` | **新增（第 14 个源文件）**，约 430 行。`public static class Gif` + `public sealed class GifBuilder`。核心是**字节级回填**：`GifBitmapEncoder` 会静默丢帧延时且不写循环块，所以 `Encode(IList<byte[]>, IList<int>, string, bool)` 先用编码器出图，再 `PatchDelays` 就地改延时、`PatchLoop` 插入 `NETSCAPE2.0`。⚠️ **`int p = 13;` / `int at = 13;`——第一个块在字节 13 开始**（6 签名 + 7 逻辑屏幕描述符），写成 6 会返回"无法写入帧延时（GIF 结构未识别）"而假成功。另有 `ToPngBytes`、`FramesIdentical`（`LockBits` + `Marshal.Copy` 分块比较）、`SkipSubBlocks`。`GifBuilder`：`Add(Bitmap)` / `Save(path, loop)` / `Delays` / `IsFull` / `MaxFrameDelayCs = 1000` / `MaxStoredFrames = 4000` |
| `src\GifRecorder.cs` | **新增（第 15 个源文件）**，约 430 行。`GifRecorder : IDisposable` + `RecorderChrome : Form`。**采样计时器必须写全 `System.Windows.Forms.Timer`**（裸 `Timer` 绑到 `System.Timers.Timer` → CS1502/CS1503）；`GifCountdownSeconds` 用第二个 1 秒计时器。`FrameCount`/`CollapsedFrames` 经 `_storedFrames`/`_collapsedFrames` **在 `Stop`/`Cancel` 之后仍可读**；抓帧失败计入 `GrabErrors`/`FirstGrabError`（**不能 `catch { }` 吞掉**——吞掉后"一直失败"和"画面没变"表现一样）。`RecorderChrome` 用 `TransparencyKey` + `WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE`，**`SetExcludedFromCapture` 失败就永久隐藏红框**，绝不让 UI 进画面 |
| `src\Ocr.cs` | **新增（第 16 个源文件）**，约 700 行。`OcrWord` / `OcrLine` / `OcrResult`（含 **`RawText`**，用来区分"引擎没找到文字"和"解析失败"）+ `public static class Ocr`。反射绑定 WinRT `Windows.Media.Ocr`；异步返回的是**裸 `__ComObject`**，必须**轮询 `Status`**（1/2/3）再 `GetResults()`——`System.Runtime.WindowsRuntime` 的 `AsTask`/`GetAwaiter` 要 .NET 4.5+，**用不了**。`RecognizerLanguage` 是 `Language` **对象**要读 `.LanguageTag`。`MinUsefulDimension = 100` / `AbsoluteMinDimension = 40`；`RecognizeOnWorker` 跑独立 STA 线程（默认 30 秒超时）；`ReadLines` 失败时**设置 `Error` 而不是静默返回 0 行** |
| `src\OcrResultForm.cs` | **新增（第 17 个源文件）**。只读多行 `TextBox`（可选中、可再次复制）+ 复制/关闭按钮；`ShowResult(text, langTag, copied)` 用 `using` + `ShowDialog` 管生命周期；测试缝 `TextForTest` / `CreateForTest` |
| `src\Config.cs` | `OcrKey = "Ctrl+Alt+O"` / `GifKey = "Ctrl+Alt+G"`；**4 个 OCR + 6 个 GIF 字段**（`OcrCopyToClipboard` / `OcrKeepLineBreaks` / `OcrUpscaleSmall` / `OcrLanguage`、`GifFps` / `GifMaxSeconds` / `GifLoop` / `GifSkipIdenticalFrames` / `GifShowBorder` / `GifCountdownSeconds`）；`Clamp()` 补范围（`OcrUpscaleSmall` 0~8、`GifFps` 2~25、`GifMaxSeconds` 1~300、`GifCountdownSeconds` 0~10、`OcrLanguage` null→`""`）；空白键回退清单加两个新键 |
| `src\SettingsStore.cs` | `Load` / `Save` **各加 12 行**（逐字段手写，非反射）。**另修一个既有 bug**：`S("FollowKey", …)` 重复写了一次（JSON 里出现重复键），删掉 `BrushCursorSize` 旁那一行 |
| `src\RegionSelectForm.cs` | `RegionAction` 追加 **`Ocr = 4` / `RecordGif = 5`**（**0/1/2/3 一个都没动**）；`OnKeyDown` 增加裸 `O` / `G`，**放在 `Ctrl` 分支之后**（与裸 `P` 同位置），所以 `Ctrl+O`/`Ctrl+G` 仍空闲。**工具条刻意保持 4 个按钮**——三处探针在断言这件事 |
| `src\AppContext2.cs` | `HK_OCR = 9` / `HK_GIF = 10`；新增 `OcrText(Bitmap)` / `RecordGif(Rectangle)` / `StopRecordingIfAny()` / `SafeSetClipboardText()`（5 次重试）；`RegionScreenshot` 在贴图分支之后加两个**提前返回**分支——OCR 用裁剪图，**GIF 必须丢掉 `cropped`/`frozen` 用实时屏幕**；托盘加两项；`OwnsHotkey` 的 `mine[]` 补齐（原来漏了 `WindowShotKey`/`ColorPickerKey`）；`Quit()` 调 `StopRecordingIfAny()` |
| `src\SettingsForm.cs` | 新增两个 `HotkeyBox`（`_hkOcr` / `_hkGif`）挂在「功能快捷键」卡上（共 10 行）；**新增 `BuildOcrCard()`**（复制开关 / 保留换行 / 小图放大 / 识别语言下拉，会实际查 `Ocr.AvailableLanguages()` 并把引擎状态写进说明文字）与 **`BuildGifCard()`**（帧率 / 最长时长 / 倒计时 / 红框开关 / 循环 / 合并帧）；`LoadDraftIntoControls` / `OnSave` 各补 12 项；新增 `SelectOcrLanguage` / `SelectedOcrLanguage`（语言被卸载时回退到「跟随系统」）；新增一组 `internal ...ForTest` 测试缝 |
| `build\gif_delta_probe.cs` | **新增**。字节级验证延时回填与循环块（`int p = 13`），6/0 |
| `build\gif_ocr_probe.cs` | **新增**，47/0。`Main` 第一句 `PinDpi()`（`SetProcessDpiAwarenessContext(-4)`），**测试字体一律 `GraphicsUnit.Pixel`**；含防裁剪断言（扫最右/最下墨迹像素） |
| `build\gif_recorder_probe.cs` | **新增**，29/0。驱动一个会动的 `Form`（静止桌面会合并成 1 帧）+ `Pump()` 泵消息；断言帧数=解码数、帧尺寸=选区、**红框没被烤进画面** |
| `build\ocr_gif_wiring_probe.cs` | **新增**，56/0。断言 `RegionAction` 取值稳定、**工具条仍是 4 个按钮**、`O`/`G` 生效而 `Ctrl+O`/`Ctrl+G` 空闲、原有按键没坏、10 个默认热键不重复、设置窗口读写 + 落盘回环、越界值 Clamp |
| `build\run_probes.ps1` | 探针数 17 → **21**；源码列表 12 → **16**；**新增 WPF 引用**（`$fwDir\WPF\PresentationCore.dll`、`WPF\WindowsBase.dll`、`System.Xaml.dll`），缺文件只警告不失败 |
| `build\build.ps1` | 自动从 `csc.exe` 所在目录推导 `$fwDir` 并追加同样的 3 个 WPF 引用（缺文件警告不失败），这样 `Gif.cs` 能进发布构建 |
| `README.md` | 特性行 + 体积 265→**294 KB** + 配置键数 66→**78**；新增 **六、文字识别（OCR）** 与 **七、录制 GIF 动图** 两节；快捷键表补 `Ctrl+Alt+O`/`Ctrl+Alt+G`；配置 JSON 块补 12 个键；源码表 13→**17** 行；探针数 17→**21**；章节重新编号为 一~十五 |
| `HANDOVER.md` | **§1.7（OCR + GIF：工具条为何不加按钮、GIF 字节回填、OCR 反射、**"OCR 返回空"其实是探针 DPI bug**、录制注意事项、验证表）** + §0/§4/§5 计数更新 + 本节 |

> 本轮**没有**改动的源文件：`src\Program.cs`、`src\Shell.cs`、`src\BrushCursor.cs`、`src\PinForm.cs`、
> `src\Beautifier.cs`、`src\ColorPickerForm.cs`、`src\Native.cs`（复用了既有的
> `CaptureRegion` / `SetExcludedFromCapture`）。

> ⚠️ **本轮最大的陷阱**：`verify_readme.py` 一度报 12 个新键"回环失败"，
> 差点被当成产品 bug。实际是 **`dist\ScreenPen.exe` 还是旧的**——
> 跨进程验收脚本跑的是**构建产物**，改完源码必须先 `build.ps1` 再跑。

### 5.2 上两轮（第一梯队四个功能 + UI 打磨 + 卡片空白修复）

| 文件 | 变更 |
|---|---|
| `src\Beautifier.cs` | **新增（第 12 个源文件）**，212 行。`public static class Beautifier`：`Render(Bitmap, Settings)` 纯函数（null / 空图 / `(long)outW*outH > 80L*1024*1024` 一律返回 null）、45° `LinearGradientBrush`、圆角夹到短边一半、`Layers=6` / `Spread=26` / `OffsetY=10` / alpha `70*(1-t)^2` 的分层柔光阴影；`TryParseColor`（自己解 hex，**不用** `ColorTranslator.FromHtml`）；`FormatColor` → `#RRGGBB` 大写；`ShadowRoom = 60` |
| `src\ColorPickerForm.cs` | **新增（第 13 个源文件）**。`ColorPickerForm(Settings, Rectangle, Bitmap, bool ownsFrozen)`：`FormBorderStyle.None` + `Bounds = desktop` + `TopMost` + `KeyPreview`；放大镜 2~32×、loupe 60~400；`SampleAt` 先读冻结帧再退到 `Native.PixelAt`；左键取色 → `DialogResult.OK`，右键/中键/`Esc` 取消；`OnShown` 里 `Native.ForceForeground`；`Dispose(bool)` 只在 `ownsFrozen` 时释放 `_frozen` |
| `src\Config.cs` | `enum Tool` 追加 **`Highlighter = 8` / `StepNumber = 9`**；新增 **`enum ArrowStyle { Straight, Curved, Elbow }`**；`Stroke` 新增 `Arrow` / `HighlightAlpha = 90` / `StepIndex`；`Settings` 新增 `HighlighterKey="H"` / `StepKey="N"` / `RedoKey="Ctrl+Y"` / `ColorPickerKey="Ctrl+Alt+P"` / `WindowShotKey="Ctrl+Alt+W"` / `HighlighterAlpha` / `ArrowStyleIndex` / `StepBadgeSize` / `StepNumberPersistent` / `Beautify*` 六项 / `PickerLoupeSize` / `PickerZoom`；`Clamp()` 补全部新字段的取值范围与 `TryParseColor` 校验回退；`ToolForKey()` 加 `H` / `N` |
| `src\SettingsStore.cs` | `Load` / `Save` **各加一行**承接全部 17 个新键（逐字段手写，非反射——**漏一处就会"存了但重启丢失"**） |
| `src\Native.cs` | 新增 `DWMWA_EXTENDED_FRAME_BOUNDS = 9` / `DwmGetWindowAttribute` / `IsIconic` / `IsWindowVisible` / `WindowBounds(IntPtr)` / `FromRect(RECT)` / `RootWindowAt(Point)` / `IsCapturableWindow(IntPtr, IntPtr[])` / `PixelAt(Point)` / `CLR_INVALID` |
| `src\OverlayForm.cs` | 新增 `Tool.Highlighter` / `Tool.StepNumber` 的绘制与命中；**`DrawHighlighter()`**（`Format32bppArgb` scratch + `ImageAttributes`/`ColorMatrix` 的 `Matrix33` 整体乘 alpha，解决自重叠加深；`(long)bw*bh > 32L*1024*1024` 上限）；`DrawStepBadge()`（`StepBadgeSize*_zoom/2`，`Microsoft YaHei UI` Bold）；`DrawArrow(g, pen, a, b, ArrowStyle)` 三样式（Curved 用二次贝塞尔且箭头取**端点切线**，`bow = Math.Min(len*0.28f, 120f)`；Elbow 走主轴）；撤销/重做双栈（`_redo` / `MaxRedoDepth = 200` / `_stepCounter` / `static _persistentStepCounter` / `Undo` 跳过 `Pinned` / `Redo` / `CanUndo` / `CanRedo` / `NoteEdit`）；`RecomputeStepCounter()` **只扫 `_strokes`**；`ClearForTest()` 委托 `Clear()`；`SetColorFromPicker`；`MakeStroke` / `PlaceStepBadge`；`ToolName` 加"荧光笔"/"序号"；HUD 提示更新为 `P/H/N/Ctrl+Y` 版本 |
| `src\OverlayForm.cs`（UI 打磨） | **新增共享 `public static Bitmap BuildPanelShadow(int, int, float)`**（`PanelShadowSpread = 14` / `PanelShadowOffsetY = 5` / 10 层 / alpha `64*(1-t)^2` / 最外层先画）+ `public const` 两项 + 缓存字段；**新增 `DrawHudPanel(Graphics, RectangleF, float)`**（按 `(w,h,radius,spread)` 缓存 + `DrawImageUnscaled` + `RoundRect` 填充 + `HudEdge` 发丝线）；`DrawDrawHud`（半径 10）/ `DrawZoomHud`（半径 12）改用它并统一 `AntiAliasGridFit` + `Math.Round` 取整；色块改**双层描边**；`_hudShadowCache` 的释放**折进已有的唯一那个 `Dispose(bool)`**（**绝不能新增第二个 override**） |
| `src\RegionSelectForm.cs` | 新增 `DrawToolbarShadow(Graphics, Rectangle, float)` —— 复用 `OverlayForm.BuildPanelShadow`（半径 9），按 `(Width, Height, radius)` 缓存；`DrawToolbar` 在 `Inflate` 之后、`RoundRect` 填充之前调用；`Dispose(bool)` 一起释放 `_toolbarShadow` |
| `src\SettingsForm.cs` | 新增 6 个可重绑 `HotkeyBox`（`_hkHighlighter` / `_hkStep` / `_hkRedo` / `_hkWindowShot` / `_hkPicker`）；**`BuildNewToolsCard()`**（画笔页，荧光笔透明度 / 箭头样式 / 序号圆点 / 序号跨图连续）；**`BuildBeautifyCard()`** + **`BuildPickerCard()`**（截图页）；`PickColorFor(Button)` 给渐变色开取色器；`LoadDraftIntoControls` / `OnSave` 各补 17 项；三张快捷键卡长高以容纳新行。**另修两处缺陷**：① 画笔页 `_chkSmooth` 与其说明文字同 y 重叠（`sy += _chkSmooth.PreferredSize.Height + 4`，注意 `CheckBox` 无 `PreferredHeight`）；② **新增 `ReflowCardHeights()`** 并在 `ApplyDpiScale()` 里 `p.Scale()` 之后、`ShowPage()` 之前调用，修掉高 DPI 下卡片底部的大片空白（根因见 §1.6.9）；四处 authored 高度降回兜底值 |
| `src\AppContext2.cs` | `HK_WINDOWSHOT = 7` / `HK_PICKER = 8`；**`WindowScreenshot()`**（先 `RootWindowAt` 再显示任何东西，`OwnWindowHandles()` 排除自身，`Native.WindowBounds`，`finally { ResumeAfterCapture(); }`）；**`StartColorPicker()`**（冻结屏幕 → 隐藏 overlay → `using ColorPickerForm(..., ownsFrozen: true)` → `SetColorFromPicker` + 剪贴板色号）；`SaveBitmap` 在 `_cfg.BeautifyEnabled` 时走 `Beautifier.Render`，否则退到 `static SaveBitmapRaw`；`OverlayKeyDown` 的 Ctrl 分支**先判重做再判撤销**；`OwnWindowHandles()` 包 `try/catch` |
| `build\tier1_probe.cs` | **新增**，约 800 行 / 40,550 bytes，9 组 78 条断言（枚举与笔画、撤销重做、序号徽标、荧光笔、箭头三样式、窗口几何、`PixelAt`、美化器、设置回环）。`Main` 第一句就是 `EnableDpiAwareness()` |
| `build\layout_cards.cs` | **新增**，加入 `run_probes.ps1`。断言"控件不越出卡片 / 卡片不重叠 / 卡片底部无 >40px 空白 / 溢出页能滚到最后一卡"，**21 PASS / 0 FAIL** |
| `build\run_probes.ps1` | 探针数 16 → **17**（加入 `layout_cards`） |
| `build\hotkey_conflict_probe.cs` | 把 `boxes.Count == 19` 的**硬编码行数**改成与窗体自己的 `_bindings` 集合比数量（保留 `>= 19` 下限）——每加一个键就假失败的断言没有价值，只有"界面上建了行却没进 `_bindings`"才是真 bug |
| `build\render_scroll.cs` | **新增**。`render_ui.cs` 渲染的是整页全高，比屏幕还高；这个按 `scrollY` 渲染**真实视口**，为的是看折线以下的卡片（正是它抓到了 `_chkSmooth` 的重叠） |
| `build\run_feasibility.ps1` | **新增**。一条命令跑 4 个**独立**可行性探针（`ocr_probe2` / `feature_probe` / `roadmap_probe` / `gif_probe`，共 38 条断言）。它们不编译程序源码，量的是 OS 能力，所以刻意不进 `run_probes.ps1`。文件里记了 `gif_probe` 的 WPF 引用坑（`WindowsBase.dll` 在 `WPF\` 子目录、`System.Xaml.dll` 取 Framework64 的） |
| `build\ocr_probe.cs` | **删除**（v1）。它在第一个 WinRT 异步调用就失败，`ocr_probe2.cs` 的文件头记录了这个原因和修法，留着 v1 只会误导 |
| `README.md` | 标题特性行 + EXE 体积 243→**265 KB** + 配置键数 49→**66**；新增 **荧光笔 / 序号 / 箭头样式 / 撤销重做 / 窗口截图 / 屏幕取色器 / 美化截图** 七节；快捷键两张表补齐 6 个新键；配置 JSON 块补全 17 个键（`verify_readme.py` 逐键核对）；源码表 11→**13** 行；探针数 15→**17** |
| `HANDOVER.md` | **§1.6（本轮四个功能 + UI 打磨 + 卡片空白根因链 + 验证表）** + 本节 + §5 倒序重排 |

> 本轮**没有**改动的源文件：`src\Program.cs`、`src\Shell.cs`、`src\BrushCursor.cs`、`src\PinForm.cs`。

### 5.3 上三轮（贴图 + 设置窗口可调大小 + 打字抢键 BUG + 12 项加固）

| 文件 | 变更 |
|---|---|
| `src\PinForm.cs` | **新增（第 11 个源文件）**，629 行。无边框置顶贴图窗口：拖动、滚轮等比缩放（锚点跟随光标）、悬停关闭按钮、右键关闭、`Esc` 关闭、`Ctrl+C` 复制、`Ctrl+S` 另存（PNG/JPG/BMP，JPG/BMP 在 `Color.Black` 上拍平成 `Format24bppRgb`）；`MinScale=0.1f` / `MaxScale=8.0f` / `MinVisible=40`；`_baseSize` 原始尺寸 + `_scale`，`ScaledSizeFor` 每次从原始尺寸重算（**绝不用 `Form.Scale`**）；`Dispose(bool)` 只在 `disposing == true` 时释放 `_image`；`ShowTip` 用 `ToolTip` 代替 `MessageBox`；测试缝 `TestScale`/`TestBaseSize`/`TestImage`/`TestIsClosed`/`IsClosed`/`TestSetScale`/`TestWheelZoom`/`TestDragBy` |
| `src\RegionSelectForm.cs` | `enum RegionAction` 追加 **`PinToDesktop = 3`**（`0/1/2` 不变）；工具条 3 按钮 → **4 按钮**（`_btnPin`），标签 `贴图 (Enter)` / `贴图 (P)`；`OnKeyDown` 增加裸 `P`（**放在 `Ctrl` 分支之后**，避免遮蔽将来的 `Ctrl+P`）；`OnMouseDown` 命中 `_btnPin`；`DrawToolbar` 的强调色跟随 `_defaultAction`。另含遗留的 DPI 化改造：`DpiScale()` / `BtnFont()` / `BtnInnerPad` / `BtnMinWidth` / `LayoutToolbar()` 三步收缩（长标签 → 短标签 → 等比缩小 → 等宽兜底）、`_btnLabels` / `_btnShrunk` 供探针量真实字串 |
| `src\AppContext2.cs` | 托盘新增 **区域截图并钉在桌面**（`RegionScreenshot(RegionAction.PinToDesktop)`）；`_pins` 列表 + `PinScreenshot()` + `PinFormClosed()` + **`CloseAllPins()`**（`ToArray()` 快照遍历，`Quit()` 与 `Dispose(bool)` 都调用）；**`OverlayKeyDown` 最前面加 `if (_overlay.IsEditingText) return;`**（打字抢键 BUG 的修复点）；`ReportHotkeyProblems()` + `_failedHotkeys`；`SettingsStore.Save` 返回值检查 → 气球提示"设置保存失败"；`CopyToClipboard` 的暂存位图改用 `using` |
| `src\OverlayForm.cs` | `MaxStrokePoints = 20000` 上限 + `MinSampleDist = 1.5f` 抖动去重；`DenseEntry` / `_denseCache` / `DensifiedFor()` / `InvalidateDensifyCache()` 渲染期缓存；`ReleaseBrushPointer()` 修 GDI 泄漏；`ProcessCmdKey` 里 `_textEditor != null` 时放行 `Tab`（否则 `_tabHeld` 闩死，下一次拖动变椭圆）；`DrawMosaic` 的 `raw` 位图提升到 `finally` 释放；`StartScrollFollow` 检查 `SetExcludedFromCapture` 返回值 |
| `src\SettingsForm.cs` | `FormBorderStyle` → **`Sizable`** + `MaximizeBox` / `MinimizeBox`；**新增 `ApplyMinimumSize()`**（`MinClientWidth = 560` / `MinClientHeight = 460` × DPI + 非客户区边框）；`ScaleSize()`；`OnResize` → `LayoutFooter()` + `RefreshScrollExtents()`（`_ready` 门控）；`DialogWidth` 1180 / `DialogHeight` 830 |
| `src\SettingsStore.cs` | `Save()` 改为**原子写**：先写 `.tmp`，再 `File.Replace(tmp, path, null)`（无文件时 `File.Move`），失败删 tmp 并返回 `false` |
| `src\Config.cs` | `Clamp()` 里 **`ScreenshotFormat` 白名单**（去空白 / 去前导 `.` / 小写 / `jpeg → jpg` / 只允许 `png`-`jpg`-`bmp`，否则回退 `png`）；空白单键字段用 `new Settings()` 的出厂值补回 |
| `src\Shell.cs` | `IconFromBytes` / `GenerateFallback` 里的 `Icon.FromHandle(...)` 改为 `Clone()` + `finally { DestroyIcon(h); }` |
| `src\Program.cs` | `ReportCrash()` 在弹框前遍历 `Application.OpenForms` 逐个 `TopMost = false`，并用 `MessageBoxOptions.DefaultDesktopOnly` |
| `build\run_probes.ps1` | 探针数 12 → **15**（加入 `pin_probe` / `typing_probe` / `measure_toolbar`）；`$srcs` 加入 `src\PinForm.cs` |
| `build\pin_probe.cs` | **新增**，444 行 / 19,850 bytes。覆盖面见 §1.5.4 末（57 条断言） |
| `build\typing_probe.cs` | **新增**，262 行 / 11,435 bytes。驱动 `Keys.L/P/O/E/A/B/D1/M/R/T/F/D0` + `Tab` + 真 `WM_CHAR` |
| `build\verify_resize.py` | **新增**，206 行 / 6,596 bytes，跨进程 9 项断言真实 EXE 的窗口样式与页脚布局 |
| `build\measure_toolbar.cs` | **新增**，155 行。量框选工具条 4 个按钮的真实标签/宽度/是否收缩，**已被加入 `run_probes.ps1`**（跨 5 种桌面宽度 × 3 种默认动作） |
| `build\render_pin.cs` | **新增**，179 行。渲染贴图窗口 PNG → `build\shots\render\30_pin_idle.png` … `35_selector_*.png`，**供人眼确认** |
| `build\capture_new_features.py` | **新增**，337 行。跨进程截图本轮的新界面：`build\featshots\20_settings_min.png` / `21_settings_wide.png` / `22_pin_window.png`。**不要**输出到 `build\shots\`：`verify_ui.py` 会对该目录下所有 PNG 两两做尺寸比对，尺寸不同即判 FAIL（实测产生 15 条假失败） |
| `README.md` | EXE 体积 230 KB → **243 KB**；新增 **贴图** 章节 + 托盘项 + 设置页"窗口大小可以自己调" + 4 条 FAQ；源码结构表 10 → **11** 行；探针数 9 → **15**；注明打字时单键命令会让位给输入框 |
| `HANDOVER.md` | **本节 + §1.5（四条改动的根因/改法/证据 + 12 项审计表）+ §3.2/§3.3/§3.6 约束补充 + §4 测试清单刷新 + §5 倒序重排 + §6 收尾 + 头部版本信息** |

> 本轮**没有**改动的源文件：`src\BrushCursor.cs`、`src\Native.cs`（`ForceForeground` 等是上一轮的）。

### 5.4 上四轮（7 项需求验收 + 两个真实 BUG 修复）

| 文件 | 变更 |
|---|---|
| `src\Native.cs` | 新增 `GetForegroundWindow` / `GetWindowThreadProcessId`（含 `out uint` 重载）/ `GetCurrentThreadId` / `AttachThreadInput` / `BringWindowToTop` / `SetActiveWindow`；**新增 `ForceForeground(IntPtr)`** —— 挂输入队列后抢前台，绕开 Windows 前台锁。此前已把激活消息改成 `RegisterWindowMessage`（`SCREENPEN_ACTIVATE_MESSAGE`）并新增 `HWND_BROADCAST` 常量 |
| `src\OverlayForm.cs` | `ActivateOverlay` / `ResumeAfterCapture` 改用 `Native.ForceForeground`；`BeginTextEditing` 抢前台后再 `Focus()`；`TextEditorLostFocus` 增加自愈（空文本 + 前台属别的进程 → 重新抢前台并保留编辑器）；新增 `ForegroundIsForeign()` |
| `build\hostile_focus_probe.cs` | **新增**，起一个别的进程占住前台，断言 overlay 夺回前台 + 编辑器存活 + 文字成笔画。**前台占用者改为自己以 `--blocker` 再起一份**：Win11 记事本是 Store 应用，`Process.Start("notepad.exe")` 返回的是启动器桩，`Kill()` 关不掉真窗口，**曾在用户桌面上留下一个记事本** |
| `build\text_focus_probe.cs` | **新增**，前台正常时的对照探针 |
| `build\hotkey_record_probe.cs` | Ctrl+F2 用例改用真 `keybd_event` + 泵消息（`ModifierKeys` 读的是**物理**键态，`SendMessage` 满足不了）→ **8/8** |
| `build\run_probes.ps1` | 探针数 9 → **12**（加入 `hotkey_record_probe` / `text_focus_probe` / `hostile_focus_probe`） |
| `build\check_tray.py` | **重写**：改用 `Shell_NotifyIconGetRect`（Win11 托盘是 XAML，没有 `ToolbarWindow32`），加断言、负向对照、汇总行、非零退出码 → **6/6 PASS, exit 0** |
| `build\verify_hotkeys.py` | 跨进程按键注入段改用 `WM_SETTEXT` + `BM_CLICK`（跨进程无法向托管 TextBox 注入按键）→ 从 4 FAIL 恢复到 **20/20 PASS** |
| `build\verify_upgrade.py` | 修 4 处脚本缺陷 → **26/26 PASS** |
| `README.md` | EXE 体积 229 KB → **230 KB**；新增"程序已在运行时再双击 EXE 会弹出设置窗口"；修正文字标注的按键说明（`Enter` 确认 / `Esc` 取消，原文错写成 `Esc` 确认） |
| `HANDOVER.md` | 本节 + §2.5 陷阱表 + §4 测试资产清单刷新 |

### 5.5 上五轮（UI 遮挡 / 画笔平滑 / F 跟随）

| 文件 | 变更 |
|---|---|
| `src\SettingsForm.cs` | **新增 `ApplyDpiScale()` / `SystemDpiScale()` / `RefreshScrollExtents()` / `LayoutHeader()` / `PagePadY`**；构造函数末尾调用 `ApplyDpiScale()`；新增 `_header` / `_headerLogo` / `_headerTitle` / `_headerSub` 字段；`AddNav` 去 emoji + 自绘强调条 + `NavButtonWidth=192`；导航栏 `Width` 210→212；`NewPage` 开 `AutoScroll`；`FinishPage` 设 `AutoScrollMinSize`；新增 `LayoutFooter()` / `ButtonWidthFor()` / `FitButton()` / `LayoutButtonRow()` / `_paletteLabelFont` |
| `src\OverlayForm.cs` | 新增 `DrawPolyline()` / `Densify()` / `CatmullRom()` / `SmoothTension`；`DrawStroke` 仅对 `Tool.Pen` 渲染时加密；`_followMisses` + `FollowIdleFrames` 自动退出 + `ScrollFollowIdled` 事件；`OnMouseWheel` 改为 `WheelScrollsDocument \|\| _scrollFollow`；`ResumeAfterCapture` 无条件 `StopScrollFollow()`；新增测试钩子 `WheelGoesToDocumentForTest` / `FollowMissesForTest` / `FollowUnavailableReason` |
| `src\AppContext2.cs` | F 键先读 `FollowUnavailableReason()` 再 `ToggleScrollFollow()`，据此给气球提示 |
| `build\run_probes.ps1` | **新增**，一条命令跑完全部探针；FAIL 扫描用 `-CaseSensitive`；完整日志落 `build\obj\<probe>.log` |
| `build\smooth_probe.cs` | **新增**，14 项 |
| `build\follow_probe.cs` | 从 18 项扩到 **24 项**（F 集成层、滚轮路由、空闲退出）。后续又加了 2 项拒绝理由断言，现为 **26 项** |
| `build\footer_probe.cs` | **新增**，12 项 |
| `build\layout_probe.cs` | **新增**，4 项（含 0 个 Label 被裁） |
| `build\measure_ui.cs` | **新增**，缩放后真实几何断言 |
| `build\render_ui.cs` | **新增**，逐页渲染 PNG 供人眼确认 |

### 5.6 更早（快捷键冲突 UX）

| 文件 | 变更 |
|---|---|
| `src\SettingsForm.cs` | 新增 `_bindings` / `_readouts` / `_lastTouched` / `_loading`、`RefreshReadouts()`、`RefreshStatusLine()`、`PromptOverride` + `Prompt()`、`KeyBinding` 嵌套类、`HotkeyBox AddKeyRow(...)`；重写 `OnSave()`、`LoadDraftIntoControls()`、`AddHotkeyRow`/`AddToolKeyRow` |
| `build\hotkey_conflict_probe.cs` | **新增**，29 项断言 |
| `build\diag_prompt.py` | **新增**，验证确认框可观测（含 `finally` 还原） |
| `build\verify_hotkeys.py` | 改用 `PostMessageW`、新增"确认框说明冲突"断言、新增"设置已精确还原"断言、新增 `WM_COMMAND`/`IDYES` |
| `build\obj\pristine.settings.json` | **新增**，出厂基线快照（⚠️ `build\obj\` 已在 `.gitignore` 内，该文件**现在已不存在**，见 §3.3） |

---

## 6. 建议的工作顺序

**§2.1–§2.4 已完成，§2.5 的收尾工作也已完成**（`README.md` 重写、`e2e_test.py`、
`verify_upgrade.py`、`check_tray.py` 全部转绿，见 §2.5 与 §1.5）。
本节保留下来的是**当时的建议顺序**，作为历史记录；当前**没有已知的未完成缺陷**。

历史建议顺序（均已执行）：

1. **重写 `README.md`** —— 覆盖区域截图、复制、文字/马赛克/聚光灯、修饰键、
   画笔光标、跟随滚动、**贴图**；然后跑 `verify_readme.py`。→ 已完成，**7/7 PASS**。
2. **修 `e2e_test.py`** —— 它当时仍在断言 `Ctrl+S` 打开"另存为"对话框，现在 `Ctrl+S` 是区域截图。
   → 已按新语义断言，**27/27 PASS**。
3. **重写或删除 `verify_upgrade.py`** —— 当时完全不可用。→ 已修复，**26/26 PASS**。
4. **查清 `check_tray.py` 为什么没有汇总行** —— 是脚本问题。→ 已重写，**6/6 PASS, exit 0**。
5. **补断言**：马赛克/聚光灯/文字是否真的烙进保存的 PNG
   （`OverlayForm.RenderComposite`），而不只是画在 overlay 上。→ 已覆盖。
6. **重跑 `verify_ui.py` / `capture_ui.py`** —— UI 改动后必须重跑。→ 已重跑，**81/81 PASS**。

**下一轮的候选（都不是缺陷，按价值排序）**：

- **贴图的进一步能力**：目前只有整张贴图，没有"复制贴图文字/取色/再次框选"。
  若要做，注意 §1.5.1 里"`PinForm` 拥有位图 + `ApplyScale` 从 `_baseSize` 重算"两条契约。
- **设置窗口的 reflow**：现在拉大只多背景。要做真正的响应式布局，
  必须先把"卡片宽度 = `780 × DPI`"的断言改掉，并且**绝不能**在 `OnResize` 里调 `Scale()`。
- **多显示器不同 DPI**：见下面的 `DpiChanged` 说明。

**可选（价值中等）**：让 `ApplyDpiScale()` 响应 `DpiChanged`，
即把窗口拖到不同 DPI 的显示器时重新缩放。当前只在构造时缩放一次，
单显示器（本机情形）完全够用；多显示器且 DPI 不同时会偏。
**注意**：`Scale()` 不是幂等的，重复调用会**再乘一次**比例，
所以 `DpiChanged` 的实现必须按"设计尺寸 → 新比例"重算，
不能直接再调一次 `ApplyDpiScale()`。

### 每一步完成后

- 重新构建（**先删旧 exe** → `build.ps1`）；
- 跑 `build\run_probes.ps1`（全套 **15** 个探针）；
- 若动了 UI，再跑 `render_ui.cs` / `render_pin.cs` 并**逐页看图**；
- 若动了设置窗口的窗口样式或页脚，再跑 `verify_resize.py`；
- 确认 `dist\ScreenPen.settings.json` 仍是 49 键出厂值
  （对照 `build\obj\pristine.settings.json`）。

---

## 7. 给接手 agent 的提醒

- **不要相信"应该没问题"**。本项目所有已修复的 bug，都是靠**进程内探针**发现的；
  OS 级合成输入在这台机器上对 overlay 不可靠。
- **UI 布局必须机器化断言 + 人眼看图，两者缺一不可**。
  真实教训：探针全 PASS 时用户仍能看到遮挡；反过来，
  `dump_settings_tree.py` 的绝对屏幕坐标**不能**当作溢出证据。
  用 `layout_probe.cs` / `measure_ui.cs` 断言数值，再用 `render_ui.cs` 出图**亲眼确认**。
  注意假阳性来源：按钮要量**原始文本高度**（`GetPreferredSize` 会虚报），
  标签优先用 `PreferredSize`（`MaximumSize` 会换行）。
- **这台机器是 192 DPI**，任何几何相关的改动都要在 200% 缩放下验证，
  而不是在 96 DPI 的直觉下写死像素。
- **量窗口尺寸前先确认当前线程是 DPI-aware**，否则数字会减半，导致误诊。
- **改任何写用户配置的脚本，都要 `finally` 还原 + 读回校验**。
- **不要用真实 `MessageBox` 做测试**，走 `PromptOverride`。
- **单键快捷键是本地键，不是全局热键**——这是刻意设计，不要"顺手修掉"。
- **`Scale()` 不幂等**，重复调用会重复相乘；`Densify` 只在渲染时做，不要写回 `s.Points`。
