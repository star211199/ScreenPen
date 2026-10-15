# ScreenPen 现状与待办（交接文档）

> 面向后续接手优化的 agent。本文档描述**当前代码的真实状态**、**已确认的缺陷根因**、
> **每个问题的定位坐标**，以及**验收标准**。
>
> 最后更新：本轮（7 项升级验收 + 修复"第二次双击无反应"与"抢不到前台时文字被丢"两个真实 BUG）
> 当前构建：`ScreenPen\dist\ScreenPen.exe`，235,520 bytes（230.0 KB）
> `sha256 A459CD1B9C66EEEB10175E81CA3CF63A861CEDC68C228AF1A429E08C2336605C`
> 全套探针：`build\run_probes.ps1` → **ALL PROBES PASSED**（12/12）
> 跨进程验收：`verify_upgrade.py` **26/26**、`verify_hotkeys.py` **20/20**、`verify_ui.py` **81/81**、
> `verify_readme.py` **7/7**、`e2e_test.py` **27/27**、`check_tray.py` **6/6**
>
> 注：每次 csc 编译都会生成新的 MVID，所以 **sha256 每次都不同**，不要用它判断是否重新编译过；
> 用**文件体积**和**修改时间**对比 `src\` 最新文件即可。

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
- 源码**恰好 10 个文件**：`src\` 下的 `Shell.cs`、`Program.cs`、`Native.cs`、`Config.cs`、
  `SettingsStore.cs`、`RegionSelectForm.cs`、`AppContext2.cs`、`BrushCursor.cs`、
  `SettingsForm.cs`、`OverlayForm.cs`。**没有 `HotkeyBox.cs`**（`HotkeyBox` 是
  `SettingsForm.cs` 内的私有嵌套类）。

### 测试

**首选：一条命令跑完全部 12 个进程内探针**（编译 + 运行 + 汇总）：

```powershell
cd ScreenPen
.\build\run_probes.ps1        # 全过 → "ALL PROBES PASSED" + exit 0
```

单个探针也可以手工编译运行，模式固定为（`Program.cs` 不要一起编，它有 `Main`）：

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = '/reference:System.dll','/reference:System.Core.dll',
        '/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll'
$srcs = 'src\Config.cs','src\SettingsStore.cs','src\Native.cs','src\OverlayForm.cs',
        'src\BrushCursor.cs','src\RegionSelectForm.cs','src\AppContext2.cs',
        'src\SettingsForm.cs','src\Shell.cs'
& $csc /nologo /target:exe /platform:x64 /codepage:65001 `
      /out:build\obj\my_probe.exe $refs build\my_probe.cs $srcs
.\build\obj\my_probe.exe
```

跨进程的 `verify_*.py`：

```powershell
$env:PYTHONIOENCODING='utf-8'
D:\Miniconda3\envs\py312\python.exe build\<脚本>.py
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
| 可自定义快捷键（19 个输入框） | ✅ | `SettingsForm.cs` |
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

---

## 2. 待修复的缺陷（TODO）

> **§2.1–§2.4 已全部完成**（见 §1.4）。本节保留原始的问题描述与根因分析，
> 作为"这些问题当初为什么会出现"的历史记录，以及**验收标准的出处**。
> **当前唯一未完成的是 §2.5。**

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

1. `NewPage()`（L327–362）里 `AutoScroll = false`，并且注释明确说
   *"The window is fixed-size and every page fits, so scrolling is unnecessary."*
   —— **这个假设是错的**。「常规」和「关于」页的内容高度超过
   `DialogHeight - header(64) - footer(60)`，超出部分直接看不见。
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

### 2.5 【其他】本轮修复记录与仍然存在的陷阱

**本轮（第 5 轮）已修复的缺陷与验证结果：**

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

### 3.3 设置持久化

- 平铺 JSON，放在 EXE 同目录 `ScreenPen.settings.json`，**共 49 个键**，绿色免安装。
- 出厂默认值备份在 `build\obj\settings.backup.json`，
  另有一份只读基线 `build\obj\pristine.settings.json`。
- ⚠️ **校验时不要比文件哈希**：`pristine.settings.json` 是带空行分组的排版版本
  （1398 bytes / 59 个 CRLF），而程序自己写出来的是紧凑版
  （1355 bytes / 51 个 CRLF）。两者**键、值、顺序完全一致**，但字节不同。
  要按**值**逐键比对：

  ```powershell
  $a = Get-Content -Raw dist\ScreenPen.settings.json   | ConvertFrom-Json
  $b = Get-Content -Raw build\obj\pristine.settings.json | ConvertFrom-Json
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

---

## 4. 测试资产清单

| 文件 | 类型 | 上次结果 | 说明 |
|---|---|---|---|
| **`build\run_probes.ps1`** | **总入口** | **ALL PROBES PASSED (12/12)** | 编译并运行下面 12 个进程内探针，逐个打印 PASS/FAIL |
| `build\hotkey_conflict_probe.cs` | 进程内 | **29/29 PASS** | 快捷键冲突 UX（含 `PromptOverride`） |
| `build\region_probe.cs` | 进程内 | **PASS** | 区域截图、裁剪、剪贴板、保存、取消 |
| `build\follow_probe.cs` | 进程内 | **24/24 PASS** | 滚动跟随（算法层 + F 集成层 + 滚轮路由） |
| `build\tools_probe.cs` | 进程内 | **PASS** | 工具、修饰键、渲染、点数断言 |
| `build\brush_probe.cs` | 进程内 | **PASS** | 画笔光标 SVG |
| `build\smooth_probe.cs` | 进程内 | **14/14 PASS** | 画笔平滑：折线 vs 样条面数、`Densify` 间隙 |
| `build\footer_probe.cs` | 进程内 | **12/12 PASS** | 页脚按钮不重叠、状态栏不被压 |
| `build\layout_probe.cs` | 进程内 | **4/4 PASS** | 每页内容可容纳/可滚动、卡片子控件、**0 个 Label 被裁** |
| `build\measure_ui.cs` | 进程内 | **ALL PASS** | 缩放后真实几何：窗口、卡片宽度、按钮/标签高度、头部 |
| `build\hotkey_record_probe.cs` | 进程内 | **8/8 PASS** | **快捷键捕获的唯一可靠证据**：真 `WM_KEYDOWN` + 真修饰键态 |
| `build\text_focus_probe.cs` | 进程内 | **ALL PASS** | 前台正常时文字编辑器存活并落成笔画 |
| `build\hostile_focus_probe.cs` | 进程内 | **ALL PASS** | **别的进程占着前台时**，overlay 夺回前台、编辑器存活 |
| `build\render_ui.cs` | 诊断 | — | 逐页渲染 PNG → `build\shots\render\`，**供人眼看** |
| `build\verify_hotkeys.py` | 跨进程 E2E | **20/20 PASS** | 快捷键重绑 + 冲突保存 + 读回校验 |
| `build\verify_brush.py` | 跨进程 | **17/17 PASS** | 画笔光标 |
| `build\verify_ui.py` | 跨进程 | **81/81 PASS** | UI 结构（**平铺 glob `build\shots\*.png`，别在里面留临时图**） |
| `build\e2e_test.py` | 跨进程 | **27/27 PASS** | 端到端（跑得慢，约 3–5 分钟，不要误判为卡死） |
| `build\check_tray.py` | 跨进程 | **6/6 PASS, exit 0** | 托盘图标注册（`Shell_NotifyIconGetRect` + 负向对照） |
| `build\verify_readme.py` | 跨进程 | **7/7 PASS** | README 一致性（**会改写 `dist\ScreenPen.settings.json`，前后要快照/还原**） |
| `build\verify_upgrade.py` | 跨进程 | **26/26 PASS** | 本轮 7 项需求的综合验收 |
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

### 5.0 本次会话（7 项需求验收 + 两个真实 BUG 修复）

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

### 5.1 此前会话（UI 遮挡 / 画笔平滑 / F 跟随）

| 文件 | 变更 |
|---|---|
| `src\SettingsForm.cs` | **新增 `ApplyDpiScale()` / `SystemDpiScale()` / `RefreshScrollExtents()` / `LayoutHeader()` / `PagePadY`**；构造函数末尾调用 `ApplyDpiScale()`；新增 `_header` / `_headerLogo` / `_headerTitle` / `_headerSub` 字段；`AddNav` 去 emoji + 自绘强调条 + `NavButtonWidth=192`；导航栏 `Width` 210→212；`NewPage` 开 `AutoScroll`；`FinishPage` 设 `AutoScrollMinSize`；新增 `LayoutFooter()` / `ButtonWidthFor()` / `FitButton()` / `LayoutButtonRow()` / `_paletteLabelFont` |
| `src\OverlayForm.cs` | 新增 `DrawPolyline()` / `Densify()` / `CatmullRom()` / `SmoothTension`；`DrawStroke` 仅对 `Tool.Pen` 渲染时加密；`_followMisses` + `FollowIdleFrames` 自动退出 + `ScrollFollowIdled` 事件；`OnMouseWheel` 改为 `WheelScrollsDocument \|\| _scrollFollow`；`ResumeAfterCapture` 无条件 `StopScrollFollow()`；新增测试钩子 `WheelGoesToDocumentForTest` / `FollowMissesForTest` / `FollowUnavailableReason` |
| `src\AppContext2.cs` | F 键先读 `FollowUnavailableReason()` 再 `ToggleScrollFollow()`，据此给气球提示 |
| `build\run_probes.ps1` | **新增**，一条命令跑完全部探针；FAIL 扫描用 `-CaseSensitive`；完整日志落 `build\obj\<probe>.log` |
| `build\smooth_probe.cs` | **新增**，14 项 |
| `build\follow_probe.cs` | 从 18 项扩到 **24 项**（F 集成层、滚轮路由、空闲退出） |
| `build\footer_probe.cs` | **新增**，12 项 |
| `build\layout_probe.cs` | **新增**，4 项（含 0 个 Label 被裁） |
| `build\measure_ui.cs` | **新增**，缩放后真实几何断言 |
| `build\render_ui.cs` | **新增**，逐页渲染 PNG 供人眼确认 |

### 5.2 更早会话（快捷键冲突 UX）

| 文件 | 变更 |
|---|---|
| `src\SettingsForm.cs` | 新增 `_bindings` / `_readouts` / `_lastTouched` / `_loading`、`RefreshReadouts()`、`RefreshStatusLine()`、`PromptOverride` + `Prompt()`、`KeyBinding` 嵌套类、`HotkeyBox AddKeyRow(...)`；重写 `OnSave()`、`LoadDraftIntoControls()`、`AddHotkeyRow`/`AddToolKeyRow` |
| `build\hotkey_conflict_probe.cs` | **新增**，29 项断言 |
| `build\diag_prompt.py` | **新增**，验证确认框可观测（含 `finally` 还原） |
| `build\verify_hotkeys.py` | 改用 `PostMessageW`、新增"确认框说明冲突"断言、新增"设置已精确还原"断言、新增 `WM_COMMAND`/`IDYES` |
| `build\obj\pristine.settings.json` | **新增**，出厂基线快照 |

---

## 6. 建议的工作顺序

**§2.1–§2.4 已完成。剩下的是 §2.5 的收尾工作**，建议顺序：

1. **重写 `README.md`** —— 覆盖区域截图、复制、文字/马赛克/聚光灯、修饰键、
   画笔光标、跟随滚动；然后跑 `verify_readme.py`。
2. **修 `e2e_test.py`** —— 它仍在断言 `Ctrl+S` 打开"另存为"对话框，现在 `Ctrl+S` 是区域截图。
3. **重写或删除 `verify_upgrade.py`** —— 目前完全不可用。
4. **查清 `check_tray.py` 为什么没有汇总行** —— 是脚本问题还是托盘逻辑问题。
5. **补断言**：马赛克/聚光灯/文字是否真的烙进保存的 PNG
   （`OverlayForm.RenderComposite`），而不只是画在 overlay 上。
6. **重跑 `verify_ui.py` / `capture_ui.py`** —— UI 改动后必须重跑。

**可选（价值中等）**：让 `ApplyDpiScale()` 响应 `DpiChanged`，
即把窗口拖到不同 DPI 的显示器时重新缩放。当前只在构造时缩放一次，
单显示器（本机情形）完全够用；多显示器且 DPI 不同时会偏。
**注意**：`Scale()` 不是幂等的，重复调用会**再乘一次**比例，
所以 `DpiChanged` 的实现必须按"设计尺寸 → 新比例"重算，
不能直接再调一次 `ApplyDpiScale()`。

### 每一步完成后

- 重新构建（**先删旧 exe** → `build.ps1`）；
- 跑 `build\run_probes.ps1`（全套 12 个探针）；
- 若动了 UI，再跑 `render_ui.cs` 并**逐页看图**；
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
