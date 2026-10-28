# taskbar

# Showing a small WPF widget on the Windows 11 taskbar (no deskband): verified and corrected

The verifier checked this report against primary sources on 2026-10-28:
- Cloned source of TrafficMonitor (930f175), Deskband11Lib (33ed5c3), ComposeWindowsTaskBar (110511f) and ManagedShell.
- dotnet/wpf, dotnet/runtime and Windhawk sources.
- Microsoft Learn pages.
- TrafficMonitor issues, read through the GitHub API.
- A read-only re-inspection of this machine (Win11 Pro 25H2 26200.9711).

The main corrections are marked **[CORRECTED]** and new facts **[ADDED]**. Anything not confirmed from a primary source is marked UNCONFIRMED.

## Recommendation

1. **Primary approach: embed the widget as a child window (`WS_CHILD`) of the explorer-owned `Shell_TrayWnd` (or `Shell_SecondaryTrayWnd`) with `SetParent`.** Three maintained projects do this on current Windows 11:
   - TrafficMonitor, in C++. Its 1.85.1/1.86 builds are used on 24H2/25H2.
   - Deskband11Lib.Wpf, in C# and WPF. It is MIT licensed, on NuGet as 1.4.2, and was last committed on 2026-10-23.
   - ComposeWindowsTaskBar, in Kotlin/Compose. It was last committed on 2026-08-04.

   This gives the most natural result:
   - The widget is clipped to the taskbar.
   - It stays in the taskbar's own z-order with no timer.
   - It is covered or hidden along with the taskbar for fullscreen apps and auto-hide.

   Setup:
   - WPF window settings: `AllowsTransparency=True`, `Background=Transparent`, `WindowStyle=None`.
   - **App manifest: `PerMonitorV2` *and* a Windows 10/11 `supportedOS` entry.** The `supportedOS` entry is required for layered child windows. **[CORRECTED: the original manifest omitted it.]**

   Runtime requirements:
   - A reconcile loop every ~500 ms that moves the window **only when the layout changed**.
   - Recreate the window on Explorer restart, a taskbar DPI change, or a monitor disconnect.
   - A separate hidden top-level window to receive broadcast messages.

   The fastest path is the `Deskband11Lib.Wpf` NuGet package. It requires TFM `net10.0-windows10.0.26100.0`, so the app runs only on 24H2+. Alternatively, port it: the whole library is about 2,000 lines, and the essential parts are about 1,000–1,200. **[CORRECTED: the report said about 1,000 lines.]**

2. **Fallback: an owned overlay, not a 200 ms `HWND_TOPMOST` loop.**
   - Make the taskbar the owner of a normal top-level WPF window: `new WindowInteropHelper(win).Owner = taskbarHwnd`, set before `Show()`. WPF passes this to `CreateWindowEx` as `HwndSourceParameters.ParentWindow` (verified in WPF's Window.cs).
   - The documentation guarantees two things. "An owned window is always above its owner in the z-order." And "Any window ... owned by a topmost window is itself made a topmost window."
   - The researcher's local test agreed with this. The verifier did not re-run it.
   - You must handle auto-hide and fullscreen yourself.

3. **Drop the current reference design** (an unowned topmost window plus a 200 ms `SetWindowPos(HWND_TOPMOST)` loop). It works; ComposeWindowsTaskBar's overlay mode does the same thing. However, it fights the taskbar for z-order and causes needless CPU wake-ups.

4. **Not suitable for a .NET app:** Windhawk-style injection. Windhawk mods run inside `explorer.exe` (`@include explorer.exe`) and hook internal `Taskbar.View.dll` / `SystemTray.dll` / `ExplorerExtensions.dll` symbols, which move between versions. For example, from `Taskbar.View.dll` 2604.8002.200.6000 the SystemTray types moved into `SystemTray.dll`.

**Caveat for approaches 1 and 2: input queues.** A cross-thread parent/child or owner/owned relationship "implicitly attaches the input queues", and "this attachment is transitive" (Raymond Chen, 2013-04-12). Chen calls it "technically legal" but "not recommended for beginners". Never block the WPF UI thread. Do HTTP polling, file I/O and UI Automation (UIA) on background threads.

---

## 0. Ground truth from this machine (re-verified read-only, 26200.9711, 100% scale)

```
Shell_TrayWnd (explorer.exe) rect=(0,1032,1920,1080) dpi=96 exstyle=0x88 (TOPMOST|TOOLWINDOW) awareness=PER_MONITOR_AWARE_V2
  Windows.UI.Composition.DesktopWindowContentBridge  (top child) rect=(0,1032,1920,1080) exstyle=0x200000 (NOREDIRECTIONBITMAP)
    Windows.UI.Input.InputSite.WindowClass
  Windows.UI.Core.CoreWindow   hidden
  Start                        hidden, rect=(717,1032,762,1080)   <- tracks UIA StartButton (718..763) within 1 px
  TrayDummySearchControl       0-size
  TrayNotifyWnd                rect=(1630,1032,1920,1080)          <- matches UIA tray
  ReBarWindow32 > MSTaskSwWClass > MSTaskListWClass rect=(762..982) <- STALE: UIA app buttons span 763..1203
Shell_SecondaryTrayWnd [ADDED] rect=(-1920,1032,0,1080) dpi=96 (second monitor at x=-1920)
  DesktopWindowContentBridge, Start hidden (-1203..-1158), WorkerW (220 px, stale), NO TrayNotifyWnd
ABM_GETSTATE=0  ABM_GETTASKBARPOS uEdge=3 (ABE_BOTTOM) rc=(0,1032,1920,1080)  SHQueryUserNotificationState=5
HKCU\...\Explorer\Advanced: TaskbarAl absent (=centered), TaskbarDa=0 (Widgets hidden), MMTaskbarEnabled absent (=all displays)
HKCU\...\Themes\Personalize: SystemUsesLightTheme=0, AppsUseLightTheme=0, ColorPrevalence=0, EnableTransparency=1
```

UIA tree, showing `AutomationId | ClassName | ControlType | Rect`:

```
TaskbarFrame   | Taskbar.TaskbarFrameAutomationPeer   | Pane   | 0,1032,1920,48
StartButton    | ToggleButton                         | Button | 718,1032,45,48
"Appid: ..."   | Taskbar.TaskListButtonAutomationPeer | Button | 44 px each, 763..1203
SystemTrayIcon | SystemTray.NormalButton/AccentButton/OmniButtonRight/OmniButton/ShowDesktopButton | 1630..1920
NotifyItemIcon | SystemTray.NormalButton              | Button | 1662,1032,32,48
Secondary: TaskbarFrame, StartButton (-1202), app buttons, one SystemTrayIcon = Clock OmniButton (-78..-8)
```

Takeaways:
- The taskbar is 48 px high at 100%. Do not hardcode this; read the rect.
- The legacy `MSTaskSwWClass` / `MSTaskListWClass` / `ReBarWindow32` x-ranges are useless. Their vertical extent is still used by Deskband11Lib.
- The hidden `Start` HWND is cheap and accurate.
- **[CORRECTED]** `TrayNotifyWnd` can be unreadable at boot on some systems. The TrafficMonitor maintainer gives this as the cause of #2098 and #2194, so always validate it.
- UIA is the authoritative source for button geometry.

---

## 1. SetParent child embedding

### 1a. TrafficMonitor (`TaskBarDlg.cpp`, `Win11TaskbarDlg.cpp`, `TrafficMonitor.cpp`, `TrafficMonitorDlg.cpp`, `WindowsSettingHelper.cpp`, `TaskBarDlgDrawCommon.cpp`)

- **Windows 11 detection:** `FindWindowExW(FindWindow(L"Shell_TrayWnd"), 0, L"Windows.UI.Composition.DesktopWindowContentBridge", NULL) != NULL`, inside `CheckWindows11Taskbar`. It also requires `IsWindows11OrLater()`.
- **Embedding:**
  - `ModifyStyleEx(0, WS_EX_TOOLWINDOW)`, then `m_connot_insert_to_task_bar = !(::SetParent(this->m_hWnd, GetParentHwnd()));`.
  - On Windows 11, `GetParentHwnd()` returns `m_hTaskbar`.
  - **[ADDED]** The dialog keeps its resource style `WS_POPUP` and never sets `WS_CHILD`. It moves with `MoveWindow` in taskbar-relative coordinates.
- **Geometry (`AdjustTaskbarWndPos`):**
  - `m_hNotify = FindWindowEx(m_hTaskbar,0,L"TrayNotifyWnd",NULL)` and `m_hStart = FindWindowEx(m_hTaskbar,nullptr,L"Start",NULL)`.
  - The Start rect is converted to taskbar-relative coordinates.
  - Left placement:
    - Snap to Start: `m_rcStart.left - width - 2`.
    - Otherwise: `2 + DPI(taskbar_left_space_win11)` when Widgets is shown, else `2`.
  - Right placement: `notify_x_pos - width + 2`, minus `DPI(taskbar_left_space_win11)` when Widgets is shown on a left-aligned taskbar.
  - Y: `(m_rcStart.Height()-h)/2 + (m_rcTaskbar.Height()-m_rcStart.Height()) + DPI(window_offset_top)`. The comment says this fixes touch devices after build 22621.
  - When there is no notify rect:
    - Secondary taskbar: `m_rcTaskbar.Width() - DPI(88)`.
    - Primary taskbar: `m_rcTaskbar.Width() - DPI(taskbar_right_space_win11)`.
  - Defaults are `taskbar_left_space_win11=160` and `taskbar_right_space_win11=280`.
- **Registry:** `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced`.
  - `TaskbarAl`: absent means centered.
  - `TaskbarDa`: absent means Widgets shown. It counts only if the `MicrosoftWindows.Client.WebExperience` package is detected.
  - `MMTaskbarEnabled`: absent means the taskbar shows on all displays.
  - The theme comes from `Themes\Personalize` `SystemUsesLightTheme`. Absent is treated as not light. It is polled every 1 s by `MAIN_TIMER`.
- **Explorer restart:**
  - `m_WM_TASKBARCREATED = RegisterWindowMessage(_T("TaskbarCreated"))` leads to `CloseTaskBarWnd(); OpenTaskBarWnd();`.
  - If `SetParent` failed, it retries every 10 s, up to `MAX_INSERT_TO_TASKBAR_CNT = 200` attempts.
  - The wiki says "Some security software will block this behaver of software..." and "In Windows10, if the start menu is open, the taskbar window cannot embed into the taskbar."
- **Fallback when not embedded:** it uses absolute coordinates, and calls `SetWindowPos(&wndTopMost, …)` only when `GetForegroundWindow()==m_hTaskbar`. Its tooltip is always made topmost.
- **Rendering:**
  - D2D + DirectComposition, using `CreateTargetForHwnd(m_hwnd, topmost, &target)`.
  - D2D + `UpdateLayeredWindowIndirect` with `pptDst` left NULL (comment: "不更新窗口位置", "do not update window position").
  - GDI with `SetLayeredWindowAttributes(color, 0, LWA_COLORKEY)`.
  - On Windows 11 it forces color 0 to 1 "to fix the right-click menu not popping up in dark mode". Color-keyed pixels are click-through.
- **Elevation [ADDED]:** the full build is `RequireAdministrator` and Lite is not. Lite users embed fine, so elevation is not needed for `SetParent`.

### 1b. Deskband11Lib.Wpf (closest analog; `airtaxi/Deskband11Lib`, MIT)

**Packages:**
- Packages: `Deskband11Lib.Core` and `Deskband11Lib.Wpf` (1.4.2). Both target `net10.0-windows10.0.26100.0` with `TargetPlatformMinVersion 10.0.26100.0`.
- It credits `zadjii/Deskband11`, whose README says it works "by creating a transparent always-on-top WinUI3 window, and parenting it to the taskbar".

**Sample:**
- Window: `AllowsTransparency="True" Background="Transparent" WindowStyle="None"`, 360x48. App: `ShutdownMode="OnExplicitShutdown"`.
- **`app.manifest` declares PerMonitorV2 *and* `<supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>`.**
- Clickable parts use `Background="#01808080"`.

**Attach sequence (`TaskbarContentHostBase.AttachCore`):**
1. `PrepareWindowForChildHosting` sets `WindowStyle=None`, `ResizeMode=NoResize`, `ShowInTaskbar=false`, `ShowActivated=false`.
2. `EnsureHandle()`.
3. `SetParent(hwnd, taskbar)`.
4. Set `_requiresReparenting = GetParent(hwnd) != taskbar`.
5. Set the style to `(style & ~(WS_POPUP|WS_CAPTION|WS_THICKFRAME|WS_SYSMENU|WS_MINIMIZEBOX|WS_MAXIMIZEBOX)) | WS_CHILD` and apply `SWP_FRAMECHANGED`.
6. The sample calls `Show()` only after `AttachWhenLayoutReadyAsync()`.

**[CORRECTED]** Deskband11Lib sets `WS_CHILD` *after* `SetParent`, so its first `GetParent` check usually reports a mismatch: `GetParent` returns the owner for a `WS_POPUP` window, and WPF's `ShowInTaskbar=false` sets a hidden owner. The next refresh fixes it. Verify with `GetAncestor(hwnd, GA_PARENT)` instead.

**Layout:**
- The layout is recomputed every `LayoutRefreshInterval` (default 500 ms).
- **[CORRECTED]** It touches the window only when the computed snapshot changes:
  - `SetWindowRgn(NULL)`.
  - `SetWindowPos(hwnd, NULL, x, y, w, h, SWP_FRAMECHANGED|SWP_NOACTIVATE|SWP_NOZORDER)`, with x and y relative to the taskbar.
  - `SetWindowRgn(CreateRectRgn(0,0,w,h))`.
- Changes are animated by default over 500 ms with CircleOut easing.
- The content element's `MaxWidth`, `Width` and `Height` are set in DIPs as px / (`GetDpiForWindow`/96). The window's own Width/Height are not touched.
- Y = `ReBarWindow32.top - taskbar.top`. Height = min(`PreferredHeight`*scale, rebar height).
- The window is collapsed with an empty region before the first layout and whenever the layout is invalid.

**Geometry:**
- Raw COM `CUIAutomation` on a thread-pool thread with `CoInitializeEx(MTA)`, calling `ElementFromHandle(taskbar).FindAll(Descendants, TrueCondition)`.
- AutomationIds `StartButton`, `WidgetsButton`, `SystemTrayIcon` and `NotifyItemIcon` are matched. Other visible, on-screen `Button` elements in the taskbar's process are app buttons, grouped contiguously after Start with gaps of 16 px or less.
- Alignment comes from `TaskbarAl` (0 = left, 1 = center). The fallback is Start.left < 15% of the width, meaning left.
- Left gap: `[Widgets.Right or taskbar.left, Start.Left]`.
- Right gap: `[appGroup.Right (else Start.Right), notifLeft]`. `notifLeft` = the UIA tray left, else `TrayNotifyWnd.left`, else `taskbar.right`, clamped to `Widgets.Left` when Widgets lies in the gap.
- Placement values: `Auto | LeftEdge | BeforeNotificationArea | BeforeStartButton`. A left-aligned taskbar always uses `BeforeNotificationArea`.

**Other behavior:**
- **Startup race:** `AttachWhenLayoutReadyAsync` polls every 1 s until UIA reports the StartButton or app buttons. After a 15 s cap it proceeds once the taskbar exists.
- **Recreation:**
  - A 1 s `System.Threading.Timer` compares `FindWindow("Shell_TrayWnd")` (or the Nth `Shell_SecondaryTrayWnd`) with the last handle, raising `TaskbarWindowRecreated` or `TaskbarWindowDisappeared`.
  - It compares `GetDpiForWindow(taskbar)` with a baseline. On a mismatch it calls `ShowWindow(SW_HIDE)`, then `Detach()`, and raises `TaskbarDpiChanged` and `TaskbarWindowRecreationRequired`.
  - The sample disposes the host, awaits `Task.Delay(1000)`, creates a new `MainWindow`, and then closes the old one.
  - README: "When the system DPI changes, the taskbar moves to the new DPI but the hosted window can keep its old one, leaving the widget misaligned."
- **Undocumented shell behavior:** commit dc617672 says "When the Start menu or Search opens, Windows shell reparents deskband windows out of the taskbar child tree". This is the author's observation and was not independently reproduced. Do not rely on `EnumChildWindows`. Re-check the parent in the reconcile loop.
- **Not done by Deskband11Lib [ADDED]:**
  - No check that `Shell_TrayWnd` belongs to `explorer.exe`.
  - No `WS_EX_NOACTIVATE` / `WS_EX_TOOLWINDOW`.
  - No theme handling in the WPF facade. Only the WinUI facade reads `SystemUsesLightTheme`, triggered by `UISettings.ColorValuesChanged`.

**Minimal use:**

```csharp
// csproj: <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework> <UseWPF>true</UseWPF>
//         <ApplicationManifest>app.manifest</ApplicationManifest>
var w = new WidgetWindow();   // AllowsTransparency=True, Background=Transparent, WindowStyle=None
var host = new Deskband11Lib.Wpf.TaskbarContentHost(w, (FrameworkElement)w.Content,
    new Deskband11Lib.Core.TaskbarContentHostOptions { PreferredWidth = 200, PreferredHeight = 40,
        Placement = Deskband11Lib.Core.TaskbarContentPlacement.LeftEdge });
host.TaskbarWindowRecreationRequired += async (_, _) => { host.Dispose(); await Task.Delay(1000); /* new window + host */ };
await host.AttachWhenLayoutReadyAsync();
w.Show();
```

### 1c. ComposeWindowsTaskBar (`TaskBarInjector.kt`, `TaskBarDiscovery.kt`, `TaskBarEventPump.kt`)

**Styles and parenting:**
- Sets `WS_CHILD|WS_CLIPSIBLINGS` and clears `WS_POPUP` and the decorations before `SetParent`. The code comment: "WS_CHILD has to be in place *before* SetParent".
- Adds `WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW` and clears `WS_EX_TOPMOST`.
- Leaves `WS_EX_LAYERED` alone: "both adding and removing it behind AWT's back breaks rendering". WPF also enforces its layered bit (Dwayne Need; `HwndTarget` re-sets it).
- Uses `HWND_TOP` among the taskbar's children.
- "Trust GetParent, not the return value: SetParent legitimately returns null when the previous parent was the desktop." The verifier did not test this return-value claim. With `WS_CHILD` already set, `GetParent` is fine; otherwise use `GetAncestor(GA_PARENT)`.

**Coordinate drift:**
- AWT reads `GetWindowRect` (screen coordinates) and writes `SetWindowPos` (parent-relative), so the widget "walks off". The fix is to position only with `SetWindowPos` in physical pixels and to compare the *actual* rect on every tick.
- WPF equivalent (inferred): never set `Window.Left/Top/Width/Height/SizeToContent` after attaching.

**Discovery and broadcasts:**
- It does not use `FindWindow("Shell_TrayWnd")`: "Third-party taskbar replacements (YASB, Zebar, Managed Shell, …) create windows of that exact class." Every candidate is checked to be owned by `explorer.exe`.
- The `TaskbarCreated` listener "must be a real top-level window, not a message-only (HWND_MESSAGE) window". Microsoft Learn agrees: a message-only window "does not receive broadcast messages".

**Overlay mode:** re-asserts `HWND_TOPMOST` on every tick: "Windows raises the taskbar above foreign top-most windows whenever the taskbar itself is clicked".

### 1d. Does it work on 24H2/25H2? Pitfalls

- **Status:** TrafficMonitor 1.85.1/1.86 and Deskband11Lib 1.4.x run on 24H2/25H2, with open TrafficMonitor bugs noted below. The `Shell_TrayWnd` child tree on 26200.9711 is as shown in section 0. No live WPF `SetParent` render test was run on this machine, by the researcher or the verifier.
- **Z-order:** `DesktopWindowContentBridge` is the top child. Place your child with `HWND_TOP` (ComposeWindowsTaskBar), or rely on `SetParent` placement (Deskband11Lib uses `SWP_NOZORDER` and works).
- **Transparency:**
  - Microsoft Learn: "Beginning with Windows 8, WS_EX_LAYERED can be used with child windows and top-level windows."
  - **[ADDED] "In order to use layered child windows, the application has to declare itself Windows 8-aware in the manifest."** Use the `supportedOS` entry in section 5.
  - Hit testing: "areas of the window that are color-keyed or whose alpha value is zero will let the mouse messages through". Give clickable surfaces an alpha of at least 1 (`#01000000`).
  - Dwayne Need's 2008 "Windows only supports the layered feature for top-level windows" predates Windows 8.
- **DPI:**
  - SetParent's DPI table lists cross-process parenting with a different awareness mode as "Forced reset (of child window's process)".
  - Explorer's taskbar is **PerMonitorV2**, verified with `AreDpiAwarenessContextsEqual`, so declare PerMonitorV2.
  - `WM_DPICHANGED` goes only to top-level windows (WPF-Samples readme).
  - WPF's `HwndTarget` handles `WM_DPICHANGED_AFTERPARENT`. Whether that message reaches a child whose parent is in another process is UNCONFIRMED, so recreate the window on a taskbar DPI change as Deskband11Lib does.
  - The docs say: "On Windows 10, the taskbar also broadcasts this message [TaskbarCreated] when the DPI of the primary display changes."
- **Explorer restart:**
  - The taskbar destroys your child HWND, and WPF raises `Closed`.
  - Use `ShutdownMode=OnExplicitShutdown`.
  - Create a *new* `Window`.
  - Wait about 1 s and/or until UIA shows the StartButton (15 s cap).
- **Broadcasts:** a child window does not receive top-level broadcasts. Use a hidden top-level `WS_POPUP` `HwndSource`, or `Microsoft.Win32.SystemEvents`, whose window is created as `CreateWindowExW(0, cls, cls, WS_POPUP, …)`.
- **Secondary monitors:**
  - `Shell_SecondaryTrayWnd` is present when `MMTaskbarEnabled` is absent or non-zero, as TrafficMonitor assumes. The verifier did not test the 0 case.
  - Order taskbars by `GetWindowRect().left`.
  - There is no `TrayNotifyWnd`. Use UIA `SystemTrayIcon`, which is the Clock: 78 px from the right edge here, while TrafficMonitor estimates 88 px.
  - Coordinates can be negative (x=-1920 here). Always convert to parent-relative coordinates: `screenX - tbRect.Left`.
  - DPI can differ per monitor.
- **Auto-hide:** the child follows the taskbar. On 25H2 with the nonlinear hide animation (Feature ID 41356296), TrafficMonitor #2264 (open, Lite 1.85.1, 26200.8037) reports that the child hides about 0.5 s late and reappears as soon as the animation starts.
- **Boot race [ADDED detail]:**
  - TrafficMonitor #2098 and #2194 (both open) report that at boot the widget lands on the Start button.
  - The maintainer's explanation: Windows 11 sometimes cannot return `TrayNotifyWnd`'s position.
  - Users report that any tray change, such as a tray icon loading or a theme toggle, fixes it.
  - Validate rects, and re-layout whenever the Start or tray rect changes.
- **Clicks:**
  - Use `ShowActivated=false` and `WS_EX_NOACTIVATE`.
  - Open details in a separate top-level flyout.
  - WPF ToolTips, Popups and ContextMenus are layered top-level windows. From a child of the taskbar, Windows will use the topmost taskbar as their owner. This is UNTESTED, so verify that they appear above the taskbar. TrafficMonitor forces its tooltip to be topmost.
- **Do not touch WPF ownership after attach [ADDED]:** WPF applies `Owner` and `ShowInTaskbar` via `SetWindowLong(GWL_HWNDPARENT)` (Window.cs `SetOwnerHandle`). Never change them once the window is `WS_CHILD`.

---

## 2. Topmost overlay done right (fallback)

```csharp
var w = new WidgetWindow { ShowActivated = false, ShowInTaskbar = false, Topmost = false };
new WindowInteropHelper(w).Owner = taskbarHwnd; // BEFORE Show(): WPF passes it as ParentWindow to CreateWindowEx
w.Show();
```

The documented semantics are quoted verbatim:
- Window Features: "An owned window is always above its owner in the z-order." "The system automatically destroys an owned window when its owner is destroyed."
- SetWindowPos: "Any window (for example, a dialog box) owned by a topmost window is itself made a topmost window, to ensure that all owned windows stay above their owner."
- SetWindowPos: "When a topmost window is made non-topmost, its owners and its owned windows are also made non-topmost windows."
- Window Features: "Hiding an owner window has no effect on the visibility state of the owned windows."

How the 25H2 shell demotes or hides the taskbar for fullscreen apps is UNCONFIRMED, so add an explicit fullscreen check. The researcher's local owner test matches the docs; the verifier did not re-run it.

**If you must stay unowned:** re-assert z-order only when needed, for example on `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` when the foreground window is the taskbar. Use `SetWindowPos(h, HWND_TOPMOST, 0,0,0,0, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_NOOWNERZORDER)`.

**Fullscreen detection (overlay mode only):**
- **`SHQueryUserNotificationState(out int s)`.**
  - Hide when the value is `QUNS_BUSY=2` ("A full-screen application is running or Presentation Settings are applied"), `QUNS_RUNNING_D3D_FULL_SCREEN=3` or `QUNS_PRESENTATION_MODE=4`.
  - Show when it is `QUNS_ACCEPTS_NOTIFICATIONS=5`.
  - Other values: `QUNS_NOT_PRESENT=1`, `QUNS_QUIET_TIME=6`, `QUNS_APP=7`.
  - **It is machine-wide, not per monitor.**
- **Appbar callback.**
  - Register with `ABM_NEW` (0) and a `uCallbackMessage`, and handle `wParam==ABN_FULLSCREENAPP (2)`. `lParam` is TRUE when an app opens and FALSE when it closes.
  - The docs add: "When a full-screen application is opening, an appbar must drop to the bottom of the z-order."
  - Never call `ABM_SETPOS`. Always call `ABM_REMOVE` (1) on exit.
  - Per-monitor behavior and side effects on 25H2 are UNCONFIRMED.
- **Per-monitor heuristic:**
  - TrafficMonitor `CCommon::IsForegroundFullscreen`: the foreground window's rect covers `rcMonitor`, excluding `GetDesktopWindow()`, class `WorkerW` and `GetShellWindow()`.
  - **[CORRECTED]** The remaining exclusions come from ManagedShell `FullScreenHelper.CanFullScreen`:
    - your own process; invisible or iconic windows;
    - windows with the `NonRudeHWND` prop; `DWMWA_CLOAKED` windows; immersive shell windows;
    - `Progman`/`WorkerW` (only before Windows 8);
    - `WS_EX_LAYERED` windows that also have `WS_EX_TRANSPARENT` or `WS_EX_NOACTIVATE`.
  - ManagedShell also requires the rect to equal the screen bounds exactly, using the client rect for captioned windows.
  - ManagedShell also uses undocumented shell-hook codes `HSHELL FULLSCREENENTER=53` / `FULLSCREENEXIT=54` ("Windows 8 and newer only") via `RegisterShellHookWindow`.

**Auto-hide (overlay mode):**
- Read `SHAppBarMessage(ABM_GETSTATE)` and test `ABS_AUTOHIDE (1)`. `ABS_ALWAYSONTOP` is 2.
- Track the taskbar rect with `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE 0x800B, …, explorerPid, taskbarThreadId, WINEVENT_OUTOFCONTEXT)`, filtering on `hwnd==taskbar && idObject==OBJID_WINDOW`.
- Hide when the taskbar rect is not fully inside `rcMonitor`.

**Other edges:**
- Windows 11 regained top, left and right taskbars in 2026:
  - TrafficMonitor #2406 reports it on 26200.9278 via ViveTool 61161244.
  - A #2338 comment on 2026-09-30 says "今天普遍推送了" ("widely pushed today").
  - TrafficMonitor works at the top and bottom but breaks on the left and right.
  - PR #2421 (open) infers orientation from the taskbar rect and was tested at 48 and 160 px on 26300.
- Read the edge from `ABM_GETTASKBARPOS` `uEdge`: `ABE_LEFT=0`, `TOP=1`, `RIGHT=2`, `BOTTOM=3`. Cross-check with the rect aspect.
- On a vertical taskbar, use a compact layout above the tray, or hide the widget.
- Whether the Start and TrayNotifyWnd HWNDs exist on vertical taskbars is UNCONFIRMED.

---

## 3. Taskbar area and the free space left of centered icons

All values are physical screen pixels, read by a PMv2 process.

1. **Find the taskbar.** `tb` = the **explorer.exe-owned** `Shell_TrayWnd` or `Shell_SecondaryTrayWnd`. Enumerate *all* windows of the class with `FindWindowEx(0, prev, cls, null)`. Read `GetWindowRect(tb)`, `GetDpiForWindow(tb)`, and the edge from `ABM_GETTASKBARPOS` or the rect aspect.
2. **Alignment.** `TaskbarAl`: absent means centered, `0` means left, `1` means centered. Fall back to Start.left < 15% of the width meaning left.
3. **Start position.**
   - Cheap: `GetWindowRect(FindWindowEx(tb, 0, "Start", null))`. The window is hidden but tracks the button within 1 px.
   - Authoritative: UIA `"StartButton"`.
4. **Widgets.** If `TaskbarDa` is not 0 (absent means on), find UIA `"WidgetsButton"`. It sits at the far left on a centered taskbar, and next to the tray on a left-aligned one.
5. **Centered taskbar.** `freeLeft = [(Widgets?.Right ?? tb.Left) + pad, StartButton.Left - pad]`. The centered group grows as apps are pinned or opened, and with the search box or Copilot. Re-measure, and hide or shrink the widget when the gap is below its minimum.
6. **Left-aligned taskbar.**
   - Left bound: the right edge of the last contiguous `Taskbar.TaskListButtonAutomationPeer`.
   - Right bound: the UIA tray left, or `TrayNotifyWnd.left` when valid, clamped to `WidgetsButton.Left`.
   - On secondary taskbars, use UIA `SystemTrayIcon` (the Clock).
7. **Vertical position.** `y = (tbHeight - h)/2`. On touch-expanded taskbars, center on the Start button's height (TrafficMonitor's formula).
8. **Refresh strategy.**
   - Re-measure every 500 ms using the cheap HWND rects. **Move the window only on change.**
   - Re-run UIA on triggers (`TaskbarCreated`, `WM_SETTINGCHANGE`, `WM_DISPLAYCHANGE`, a Start or tray rect change) or every few seconds.
   - Run UIA on a background MTA thread.

Never use the x-ranges of `MSTaskSwWClass`, `MSTaskListWClass` or `ReBarWindow32` on Windows 11. TrafficMonitor's TaskBarDlg.cpp:989 has a Chinese comment saying the MSTaskSwWClass rect is no longer the task-button area.

---

## 4. Taskbar light/dark

- **Which value:** the taskbar follows the **Windows (system) mode**, `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` `SystemUsesLightTheme` (DWORD, 0 = dark).
  - TrafficMonitor uses it (absent treated as dark), and so does Deskband11Lib's WinUI facade.
  - `AppsUseLightTheme` is the app mode and the wrong value here.
  - What to assume when the value is absent is UNCONFIRMED; Windows' default mode is light.
- **Accent and transparency:**
  - `ColorPrevalence=1` means the taskbar uses the accent color. Per Microsoft support and community guides, this is available only when the Windows mode is Dark (or Custom with Windows mode Dark).
  - `EnableTransparency=1` means a translucent taskbar, so never draw an opaque background.
- **Change notification:**
  - Handle `WM_SETTINGCHANGE` with lParam = "ImmersiveColorSet", which Chromium, Blender and Zed use. It is not in the Microsoft Learn theme page.
  - Guard against a NULL lParam, compare case-insensitively, and expect several deliveries; handle them idempotently.
  - `SystemEvents.UserPreferenceChanged` reports this string as `UserPreferenceCategory.General`, because only "Policy" and "intl" are special-cased. Its listener is a `WS_POPUP` top-level window.
  - Keep a 1–2 s registry poll as a fallback; TrafficMonitor polls every 1 s.
  - Whether `UISettings.ColorValuesChanged` fires for system-mode-only changes is UNCONFIRMED.
- **Do not sample pixels.**
- **Text colors:** white in dark mode, black in light mode, with about 0.6–0.7 opacity for secondary text.

---

## 5. Per-monitor DPI v2 and `AllowsTransparency` in WPF (.NET 10)

**Manifest [CORRECTED: the `compatibility` block is required for layered child windows]**

Reference it with `<ApplicationManifest>app.manifest</ApplicationManifest>`. This matches Deskband11Lib's working WPF sample:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="AITokenUsage.app"/>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10/11. "In order to use layered child windows, the application has to declare itself Windows 8-aware in the manifest." -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

**DPI:**
- "WPF apps are System DPI aware by default" (WPF-Samples readme, which only describes PerMonitor v1).
- Size the HWND in pixels with `SetWindowPos`, and size the content in DIPs: px / (`GetDpiForWindow(tb)`/96). Set `UseLayoutRounding=True`.
- Child mode: recreate the window on a taskbar DPI change.
- Overlay mode: handle `Window.DpiChanged` and re-apply your position.

**AllowsTransparency:**
- It requires `WindowStyle=None`; otherwise WPF throws `InvalidOperationException`. It is fixed at construction.
- WPF uses only the application-provided layered mode (`UpdateLayeredWindow`):
  - No child HWNDs (HwndHost, WebView2, WindowsFormsHost) are rendered inside.
  - Hit testing is per pixel, and you need at least one partially opaque pixel to receive the mouse.
  - The content is copied out of the GPU on every update, so cost scales with area times update rate. Dwayne Need: "reduce the amount of updates to the window, in either frequency or area."
- For a 200x48 widget that updates every few seconds, the cost is negligible. Avoid continuous animations.
- `HwndTarget.RenderMode = RenderMode.SoftwareOnly` is an optional experiment. That it is cheaper is UNCONFIRMED; measure it.
- ClearType: "WPF disables ClearType when it detects that the buffer into which text is drawn could have a transparent background." Grayscale anti-aliasing is acceptable.
- Whether WPF's render thread passes a position in `UpdateLayeredWindow` for `WS_CHILD` layered windows is UNCONFIRMED. Deskband11Lib moves its window only on layout change and still works, which suggests WPF does not move it. Still, compare the actual rect each tick.

---

## 6. C# P/Invoke (.NET 10, x64). The csproj needs `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` for `LibraryImport` **[ADDED]**

```csharp
using System.Runtime.InteropServices;

internal static partial class Native
{
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
    public const long WS_CHILD = 0x40000000, WS_POPUP = 0x80000000, WS_CLIPSIBLINGS = 0x04000000,
        WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
        WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    public const long WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
        WS_EX_APPWINDOW = 0x40000, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    public static readonly nint HWND_TOP = 0, HWND_BOTTOM = 1, HWND_TOPMOST = -1, HWND_NOTOPMOST = -2;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
        SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, SWP_NOOWNERZORDER = 0x200,
        SWP_ASYNCWINDOWPOS = 0x4000;
    public const uint GA_PARENT = 1;                                   // [ADDED]
    public const int WM_SETTINGCHANGE = 0x001A, WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0,
        WM_DPICHANGED_BEFOREPARENT = 0x02E2, WM_DPICHANGED_AFTERPARENT = 0x02E3;
    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_GETSTATE = 4, ABM_GETTASKBARPOS = 5;
    public const uint ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;
    public const int ABN_STATECHANGE = 0, ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
    public const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4, QUNS_ACCEPTS_NOTIFICATIONS = 5;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_REORDER = 0x8004, EVENT_OBJECT_LOCATIONCHANGE = 0x800B,
        WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    public const int OBJID_WINDOW = 0, SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const uint MSGFLT_ALLOW = 1;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public uint cbSize; public nint hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public nint lParam; } // 48 bytes on x64

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindow(string? cls, string? title);
    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowEx(nint parent, nint childAfter, string? cls, string? title);
    [LibraryImport("user32.dll", SetLastError = true)] public static partial nint SetParent(nint child, nint newParent);
    [LibraryImport("user32.dll")] public static partial nint GetParent(nint hwnd);
    [LibraryImport("user32.dll")] public static partial nint GetAncestor(nint hwnd, uint flags);   // [ADDED] use GA_PARENT to verify
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static partial nint GetWindowLongPtr(nint hwnd, int index); // x64 only; x86 => GetWindowLongW
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string name);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint msg, uint action, nint changeFilterStruct); // only if running elevated
    [LibraryImport("user32.dll")] public static partial int SetWindowRgn(nint hwnd, nint hrgn, [MarshalAs(UnmanagedType.Bool)] bool redraw); // system owns hrgn afterwards
    [LibraryImport("gdi32.dll")] public static partial nint CreateRectRgn(int l, int t, int r, int b);
    [LibraryImport("shell32.dll")] public static partial nuint SHAppBarMessage(uint msg, ref APPBARDATA data);
    [LibraryImport("shell32.dll")] public static partial int SHQueryUserNotificationState(out int state);

    public delegate void WinEventProc(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern nint SetWinEventHook(uint min, uint max, nint hmod, WinEventProc proc, uint pid, uint tid, uint flags); // keep delegate alive!
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnhookWinEvent(nint hook);
}
```

### Child-embedding core (WPF), corrected

```csharp
// App.xaml: ShutdownMode="OnExplicitShutdown"
// Widget XAML: WindowStyle="None" AllowsTransparency="True" Background="Transparent" ShowInTaskbar="False"
//              ShowActivated="False" ResizeMode="NoResize" Topmost="False" UseLayoutRounding="True"
//              root Border Background="#01000000" (hit-testable). After attach never touch Left/Top/Width/Height/
//              SizeToContent/Owner/ShowInTaskbar.

// [CORRECTED] enumerate ALL windows of the class; a 3rd-party bar may own the first one
static nint FindExplorerTaskbar(string cls = "Shell_TrayWnd")
{
    for (nint h = Native.FindWindowEx(0, 0, cls, null); h != 0; h = Native.FindWindowEx(0, h, cls, null))
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid);
              if (p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return h; }
        catch { }
    }
    return 0;
}

static bool AttachAsChild(Window w, nint tb)
{
    nint h = new WindowInteropHelper(w).EnsureHandle();          // before Show()
    long st = Native.GetWindowLongPtr(h, Native.GWL_STYLE);
    st = (st & ~(Native.WS_POPUP | Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU |
                 Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX)) | Native.WS_CHILD | Native.WS_CLIPSIBLINGS;
    Native.SetWindowLongPtr(h, Native.GWL_STYLE, (nint)st);       // WS_CHILD before SetParent (SetParent docs)
    long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
    ex = (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW;
    Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, (nint)ex);     // do NOT touch WS_EX_LAYERED (WPF owns it)
    Native.SetWindowRgn(h, Native.CreateRectRgn(0, 0, 0, 0), true); // collapsed until first layout
    Native.SetParent(h, tb);
    if (Native.GetAncestor(h, Native.GA_PARENT) != tb) return false; // [CORRECTED] GA_PARENT, not GetParent; else fall back to owned overlay
    Native.SetWindowPos(h, 0, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                        Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    w.Show();
    return true;
}

// Reconcile every ~500 ms (DispatcherTimer) and on TaskbarCreated / WM_DISPLAYCHANGE / WM_SETTINGCHANGE
void Reconcile()
{
    if (!Native.IsWindow(_tb) || !Native.IsWindow(_h)) { RequestRecreate(); return; }
    if (Native.GetAncestor(_h, Native.GA_PARENT) != _tb) Native.SetParent(_h, _tb);   // shell may reparent (Start/Search)
    uint dpi = Native.GetDpiForWindow(_tb);
    if (dpi != _baselineDpi) { Native.ShowWindow(_h, Native.SW_HIDE); RequestRecreate(); return; }
    Native.GetWindowRect(_tb, out var tbr);
    double s = dpi / 96.0;
    var gap = _geometry.FreeLeftGap(tbr);            // validated Start/Widgets rects (HWND + background UIA)
    int wPx = (int)Math.Round(_desiredWidthDip * s), hPx = Math.Min(tbr.Height, (int)Math.Round(40 * s));
    if (gap.Width < wPx) { if (_shown) { Native.ShowWindow(_h, Native.SW_HIDE); _shown = false; } return; }
    int x = gap.Left - tbr.Left, y = (tbr.Height - hPx) / 2;      // parent-relative physical px (tbr.Left may be negative)
    Native.GetWindowRect(_h, out var cur);                         // screen coords
    bool misplaced = cur.Left != tbr.Left + x || cur.Top != tbr.Top + y || cur.Width != wPx || cur.Height != hPx;
    if (!misplaced && _shown) return;                              // [CORRECTED] no per-tick SetWindowPos/SetWindowRgn churn
    _root.Width = wPx / s; _root.Height = hPx / s;
    Native.SetWindowPos(_h, Native.HWND_TOP, x, y, wPx, hPx, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    Native.SetWindowRgn(_h, Native.CreateRectRgn(0, 0, wPx, hPx), true);
    _shown = true;
}
// RequestRecreate(): dispose old window, await Task.Delay(1000) (+ wait for UIA StartButton, <=15 s), new WidgetWindow(), AttachAsChild.
```

### Broadcast listener (must be top-level, not HWND_MESSAGE)

```csharp
var src = new HwndSource(new HwndSourceParameters("TaskbarWatcher") { WindowStyle = unchecked((int)0x80000000) /*WS_POPUP, hidden*/, ParentWindow = 0, Width = 0, Height = 0 });
uint WM_TASKBARCREATED = Native.RegisterWindowMessage("TaskbarCreated");
// if elevated: Native.ChangeWindowMessageFilterEx(src.Handle, WM_TASKBARCREATED, Native.MSGFLT_ALLOW, 0);
src.AddHook((nint hwnd, int msg, nint w, nint l, ref bool handled) =>
{
    if (msg == (int)WM_TASKBARCREATED) RequestRecreate();      // Explorer restart OR primary-display DPI change
    else if (msg is Native.WM_DISPLAYCHANGE or Native.WM_DPICHANGED) ScheduleReconcile();
    else if (msg == Native.WM_SETTINGCHANGE && l != 0 &&
             string.Equals(Marshal.PtrToStringUni(l), "ImmersiveColorSet", StringComparison.OrdinalIgnoreCase)) ApplyTheme();
    return 0;
});
```

### Theme

```csharp
static bool TaskbarIsLight()
{
    using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
    return k?.GetValue("SystemUsesLightTheme") is int v && v != 0;   // absent => treated as dark (TrafficMonitor behaviour)
}
```

### Geometry via UIA (background MTA thread)

```csharp
// using System.Windows.Automation;  (UIAutomationClient is in the WPF shared framework)
var root = AutomationElement.FromHandle(tb);
var start = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
var widgets = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton"));
// BoundingRectangle is physical screen px for a PMv2 caller (identical at 100% here; >100% UNCONFIRMED)
```

---

## 7. Comparison

| | A: SetParent child (recommended) | B: Owned overlay (fallback) | C: Topmost + 200 ms loop (current) | D: Explorer injection (Windhawk) |
|---|---|---|---|---|
| Z-order vs taskbar | inherent | inherent per docs (owned by topmost ⇒ topmost) | fights; possible flicker | native |
| Fullscreen apps | covered with the taskbar | demoted with owner if shell makes the taskbar non-topmost (UNCONFIRMED); add a QUNS check | must detect | native |
| Auto-hide | follows (25H2 animation lag, #2264) | must track | must track | native |
| Explorer restart | HWND destroyed, so recreate | destroyed with owner, so recreate | just reposition | mod reload |
| DPI | recreate on taskbar DPI change | normal WPF PMv2 | normal | n/a |
| Manifest needs | PMv2 **+ supportedOS Win10** (layered child) | PMv2 | PMv2 | n/a |
| WPF friendliness | hacky (no Left/Top/Owner/ShowInTaskbar after attach; broadcasts need a separate window) | normal | normal | not WPF |
| Input coupling with Explorer | attached queues | attached queues | none | in-process |
| Other risks | security software may block; shell may reparent (Start/Search); boot race | stays visible if the shell *hides* the taskbar | CPU wake-ups | AV flags; breaks on Taskbar.View.dll/SystemTray.dll changes |
| Look | most natural | near-native | OK | perfect |

---

## 8. Pitfall checklist

1. **Manifest:** use PerMonitorV2 **and** `<supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>`. Without `supportedOS`, layered child windows are unsupported.
2. **Wait for the taskbar layout before the first attach.** Wait until UIA shows the StartButton (15 s cap). Validate `TrayNotifyWnd` and Start rects, which can be invalid at boot (#2098, #2194).
3. **Use the right taskbar.** Enumerate all `Shell_TrayWnd` windows and pick the `explorer.exe`-owned one. Without a `DesktopWindowContentBridge` child, it is not the Windows 11 XAML taskbar (ExplorerPatcher or a classic taskbar).
4. **Verify the parent with `GetAncestor(GA_PARENT)`.** Re-check it every tick and call `SetParent` again if needed.
5. **Plan for recreation.** Set `ShutdownMode=OnExplicitShutdown`. On `Closed` or `IsWindow()==false`, create a **new** Window.
6. **Never block the UI thread.** Its input queue is attached to Explorer's.
7. **Make clickable areas hit-testable.** Use alpha of at least 1; fully transparent or color-keyed pixels pass clicks through to the taskbar.
8. **Don't use WPF window geometry or ownership after reparenting.** That means `Window.Left/Top/Width/Height/SizeToContent/Owner/ShowInTaskbar`. Use `SetWindowPos` in parent-relative physical pixels, compare with the actual rect, and set the content size in DIPs.
9. **Take the scale from `GetDpiForWindow(taskbar)`.** Recreate the window in child mode when the taskbar DPI changes.
10. **Handle `uEdge` for top, left and right taskbars (2026 builds).** Use a compact layout or hide on vertical taskbars.
11. **Handle Widgets.** It is on the left in centered mode and on the right in left-aligned mode. The centered group grows, so hide or shrink the widget when the gap is too small.
12. **Secondary taskbars:**
    - There is no `TrayNotifyWnd`; use UIA `SystemTrayIcon` (the Clock).
    - Coordinates can be negative.
    - They exist only if `MMTaskbarEnabled` is absent or non-zero.
13. **Elevation.** If the app runs elevated, allow `TaskbarCreated` through UIPI with `ChangeWindowMessageFilterEx(..., MSGFLT_ALLOW)`. Better: don't run elevated; non-elevated embedding works, as TrafficMonitor Lite shows.
14. **Keep updates small and rare.** Layered WPF windows copy content from the GPU on every update.
15. **Set `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`** for `LibraryImport`. Deskband11Lib also requires TFM `net10.0-windows10.0.26100.0`.

---

## Open questions (still UNCONFIRMED)

- No live test has been run here of a per-pixel-alpha WPF window parented as a child of `Shell_TrayWnd`; the researcher's attempt was denied permission and the verifier did not run one. Deskband11Lib's published WPF sample suggests it works. The test script is at C:\Users\kyung\AppData\Local\Temp\claude\d--AITokenUsage-WorkSpace-AITokenUsage\b0f14b69-2945-43cd-89fe-a14ff4f6c1e2\scratchpad\exp\childtest.ps1 and should be run only with the user's consent.
- Does `WM_DPICHANGED_AFTERPARENT` reach a WPF child whose parent is in explorer.exe? Recreating the window is the safe choice.
- Does WPF's render thread pass `pptDst` to `UpdateLayeredWindow` for `WS_CHILD` layered windows? The evidence (Deskband11Lib moves its window only on change) suggests it does not move the window.
- How does 24H2/25H2 hide or demote the taskbar for fullscreen ("rude") apps? This decides whether owned overlays hide automatically.
- On vertical taskbars, what does `ABM_GETTASKBARPOS` report, and do the Start and TrayNotifyWnd HWNDs exist?
- Is `ABN_FULLSCREENAPP` sent per monitor? What are the side effects of a dummy appbar on 25H2?
- Does the shell really reparent hosted windows when Start or Search opens? The source is the Deskband11Lib commit message plus the TrafficMonitor wiki note on Windows 10. Re-checking the parent each tick mitigates it either way.
- Does `UISettings.ColorValuesChanged` fire for system-mode-only changes?
- Which security products block cross-process `SetParent`? None are named anywhere.
- What is the taskbar height with "show smaller taskbar buttons"? Read the rect; never hardcode it.

---

## Sources

- **TrafficMonitor** (master 930f175): https://github.com/zhongyang219/TrafficMonitor
  - Source files: `TrafficMonitor/TaskBarDlg.cpp`, `Win11TaskbarDlg.cpp`, `TrafficMonitor.cpp`, `TrafficMonitorDlg.cpp`, `WindowsSettingHelper.cpp`, `TaskBarDlgDrawCommon.cpp`, `Common.cpp`, `TaskbarHelper.cpp`, `stdafx.h`, `TrafficMonitor.vcxproj`, `TrafficMonitor.rc`.
  - Issues #2098, #2194, #2264, #2338, #2406 and PR #2421.
  - Wiki: https://github.com/zhongyang219/TrafficMonitor/wiki/Taskbar-Window
- **Deskband11Lib** (master 33ed5c3, NuGet 1.4.2): https://github.com/airtaxi/Deskband11Lib
  - Source files: `TaskbarContentHostBase.cs`, `TaskbarContentHostOptions.cs`, `Internal/TaskbarWindowMonitorService.cs`, `TaskbarWindowLocator.cs`, `TaskbarButtonReader.cs`, `TaskbarLayoutCalculator.cs`, `TaskbarAlignmentDetector.cs`, `Wpf/TaskbarHostPlatformAdapter.cs`, `Wpf.Sample/*`, `WinUI/TaskbarHostPlatformAdapter.cs`.
  - Commits dc617672, a3cd68c9, b4997f45, 21bbb0bf.
- **Deskband11:** https://github.com/zadjii/Deskband11 (README)
- **ComposeWindowsTaskBar** (main 110511f): https://github.com/LionZXY/ComposeWindowsTaskBar (`TaskBarInjector.kt`, `TaskBarEventPump.kt`, `TaskBarDiscovery.kt`, `WindowsTaskBar.kt`)
- **ManagedShell:** https://github.com/cairoshell/ManagedShell (`FullScreenHelper.cs`, `TasksService.cs`, `NativeMethods.Shell32.cs`, `NativeMethods.User32.cs`)
- **RoundedTB:** https://github.com/RoundedTB/RoundedTB (`Taskbar.cs`, `IAppVisibility.cs`). It applies `SetWindowRgn` on the taskbar itself and does not host widgets.
- **TaskbarXI:** https://github.com/ChrisAnd1998/TaskbarXI (`Taskbar11/Taskbar11.cpp`). It applies `SetWindowRgn` on the taskbar.
- **Windhawk:** https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-clock-customization.wh.cpp (v1.8.1)
- **dotnet/wpf:**
  - `HwndTarget.cs`: https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/InterOp/HwndTarget.cs
  - `Window.cs`: https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Window.cs
- **dotnet/runtime `SystemEvents.cs`:** https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Win32.SystemEvents/src/Microsoft/Win32/SystemEvents.cs
- **Microsoft Learn:**
  - SetParent: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent
  - GetParent: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getparent
  - SetWindowPos: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos
  - Window Features: https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features
  - Using Windows (layered child windows need a Windows 8+ manifest): https://learn.microsoft.com/en-us/windows/win32/winmsg/using-windows
  - The Taskbar: https://learn.microsoft.com/en-us/windows/win32/shell/taskbar
  - ABN_FULLSCREENAPP: https://learn.microsoft.com/en-us/windows/win32/shell/abn-fullscreenapp
  - SHAppBarMessage: https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shappbarmessage
  - APPBARDATA: https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-appbardata
  - QUERY_USER_NOTIFICATION_STATE: https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state
  - Window.AllowsTransparency: https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.allowstransparency
  - RenderOptions.ClearTypeHint: https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.renderoptions.cleartypehint
  - Support Dark and Light themes in Win32 apps: https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-windows-themes
- **WPF-Samples PerMonitorDPI readme:** https://github.com/microsoft/WPF-Samples/blob/main/PerMonitorDPI/readme.md
- **Raymond Chen** (2013-04-12): https://devblogs.microsoft.com/oldnewthing/20130412-00/?p=4683
- **Dwayne Need, "Transparent Windows in WPF"** (2008-09-08): https://learn.microsoft.com/en-us/archive/blogs/dwayneneed/transparent-windows-in-wpf
- **Read-only re-inspection on this machine** (Windows 11 Pro 25H2 26200.9711, two 1920x1080 monitors at 96 DPI) using Win32 calls and UIA.


## Verifier: missing_but_important
- Layered CHILD windows need a Windows 8+ compatibility manifest. 'Using Windows' says: 'In order to use layered child windows, the application has to declare itself Windows 8-aware in the manifest.' Add <compatibility><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/></application></compatibility> next to the PerMonitorV2 dpiAwareness. The report's manifest omits it. Deskband11Lib's working WPF sample includes it.
- Verify reparenting with GetAncestor(hwnd, GA_PARENT=1), not GetParent. GetParent returns the owner for WS_POPUP windows, and WPF's ShowInTaskbar=false gives the window a hidden owner through GWL_HWNDPARENT (dotnet/wpf Window.cs SetTaskbarStatus/SetOwnerHandle).
- After attaching, never change Window.Owner, WindowInteropHelper.Owner or ShowInTaskbar. WPF applies them with SetWindowLong(GWL_HWNDPARENT), which must not be used on a WS_CHILD window. This precaution is inferred from the WPF source and was not tested.
- On some Windows 11 boots, TrayNotifyWnd's rect cannot be read: the maintainer's explanation on TrafficMonitor #2098 is the root cause of the 'widget lands on Start' bugs. Validate HWND rects, wait for UIA StartButton or app buttons (Deskband11Lib uses a 15 s cap), and re-layout when the tray or Start rect changes.
- Deskband11Lib.Wpf 1.4.2 has TargetPlatformMinVersion 10.0.26100.0. The app must target net10.0-windows10.0.26100.0 or higher, so it runs only on 24H2+. It does not check explorer.exe ownership and does not add WS_EX_NOACTIVATE or WS_EX_TOOLWINDOW. It recomputes every 500 ms but moves the window only when the computed layout changes. By default it animates moves (500 ms) and supports multiple widgets through a shared-memory slot registry.
- LibraryImport source generation requires <AllowUnsafeBlocks>true</AllowUnsafeBlocks> in the csproj.
- This machine has TWO monitors. Shell_SecondaryTrayWnd sits at x=-1920 with negative screen coordinates, so a child window must be positioned with parent-relative coordinates (screenX - taskbarRect.Left). The secondary taskbar has no TrayNotifyWnd; its clock (UIA SystemTrayIcon, OmniButton) spans 78 px from the right edge, close to TrafficMonitor's 88 px estimate.
- SHQueryUserNotificationState is machine-wide, per its docs ('state of the machine for the current user'), so a fullscreen app on one monitor hides the widget on all monitors. Use a per-monitor foreground-rect check if that matters.
- Explorer's Shell_TrayWnd is DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 (verified live with AreDpiAwarenessContextsEqual). Matching PMv2 avoids the 'Forced reset' row in the SetParent DPI table.
- TrafficMonitor embeds a WS_POPUP dialog with SetParent and never sets WS_CHILD, and it works. WS_CHILD-before-SetParent is the documented order but not strictly what working projects do. Deskband11Lib sets it after SetParent, and ComposeWindowsTaskBar before.
- RoundedTB and TaskbarXI do not host widgets: both reshape Explorer's own taskbar with SetWindowRgn(Shell_TrayWnd, ...) from outside the process. RoundedTB uses IAppVisibility::IsLauncherVisible (CLSID 7E5FE3D9-985F-4908-91F9-EE19F9FD1514) to detect the Start menu. ModernFlyouts replaces flyouts and does not apply here.
- ABN_FULLSCREENAPP docs say an appbar 'must drop to the bottom of the z-order' when a fullscreen app opens. Registering a dummy appbar only to get this notification carries that contract. Its per-monitor behavior is UNCONFIRMED.
- WPF ToolTips, Popups and ContextMenus are themselves layered top-level windows (Dwayne Need). When opened from a child of the taskbar, Windows will use the top-level ancestor (the topmost taskbar) as owner, so they should appear above the taskbar. This is UNTESTED; verify it.

## Verifier: checked claims (non-confirmed only)
- [corrected] TrafficMonitor IsForegroundFullscreen / ManagedShell FullScreenHelper exclude the desktop (WorkerW/Progman/GetShellWindow), cloaked windows, NonRudeHWND and your own process.
  -> The report merges two implementations. TrafficMonitor (Common.cpp:730) excludes only GetDesktopWindow(), class "WorkerW" and GetShellWindow(), and treats the foreground window as fullscreen if its rect covers rcMonitor. The other exclusions belong to ManagedShell's CanFullScreen: own process, invisible or iconic windows, the NonRudeHWND prop, DWMWA_CLOAKED, immersive shell windows, Progman/WorkerW (only before Windows 8), and WS_EX_LAYERED windows that also have WS_EX_TRANSPARENT or WS_EX_NOACTIVATE. ManagedShell also requires the rect to equal the screen bounds exactly, and uses the client rect for captioned or thick-frame windows.
- [corrected] Every LayoutRefreshInterval (500 ms), Deskband11Lib calls SetWindowPos(hwnd, NULL, x, y, w, h, SWP_FRAMECHANGED|SWP_NOACTIVATE|SWP_NOZORDER) followed by SetWindowRgn(CreateRectRgn(0,0,w,h)).
  -> It RECALCULATES every 500 ms (LayoutRefreshInterval default = 500 ms) but calls SetWindowRgn(NULL), SetWindowPos and SetWindowRgn(rect) only when the computed snapshot changes (AreLayoutSnapshotsClose early-return). By default it also animates changes (500 ms CircleOut, high-refresh-rate timer). Y = ReBarWindow32.top - taskbar.top. Height = min(PreferredHeight*scale, rebar height). Content Width/Height/MaxWidth are set in DIPs on the content element, not on the Window.
- [corrected] Deskband11Lib geometry: UIA FindAll(Descendants, TrueCondition) on a thread-pool MTA thread. It matches AutomationIds StartButton, WidgetsButton, SystemTrayIcon and NotifyItemIcon, and counts other visible Buttons in Explorer's process as app buttons. Alignment comes from TaskbarAl (0 left, 1 center), with a fallback of Start < 15% of the width meaning left. Left gap is [Widgets.Right or taskbar.left, Start.Left]. Right gap is [app buttons Right, min(TrayNotifyWnd.left, Widgets.Left)].
  -> Mostly confirmed (TaskbarButtonReader.cs, TaskbarAlignmentDetector.cs, TaskbarLayoutCalculator.cs). It uses raw COM CUIAutomation (CLSID ff48dba4-...) with CoInitializeEx(MTA), not System.Windows.Automation. Right-gap detail: right = the UIA notification-area left (min of SystemTrayIcon/NotifyItemIcon), else TrayNotifyWnd.left, else taskbar.right, clamped to WidgetsButton.Left if Widgets lies inside the gap. Left = app-button group right, else Start.Right. The app-button group is contiguous buttons after Start with gaps of 16 px or less. Placement enum: Auto | LeftEdge | BeforeNotificationArea | BeforeStartButton. Left-aligned taskbars always fall back to BeforeNotificationArea.
- [corrected] Deskband11Lib uses SystemUsesLightTheme for the taskbar theme.
  -> Only the WinUI facade does this (Deskband11Lib.WinUI/TaskbarHostPlatformAdapter.cs:129). It subscribes to UISettings.ColorValuesChanged and then reads SystemUsesLightTheme (commit b4997f45). The WPF facade has no theme handling.
- [corrected] The fastest path is to port Deskband11Lib's roughly 1,000 lines.
  -> Core plus the Wpf facade is about 2,040 lines of C#, plus a 171-line generated UIA COM interop. The cross-process slot registry (~300 lines) and the animation are optional. The essential attach, layout, UIA and monitor code is roughly 1,000–1,200 lines. Consuming the NuGet package (1.4.2) is simpler than porting.
- [corrected] Verifying the parent with GetParent() is reliable.
  -> GetParent returns the OWNER for a top-level WS_POPUP window. WPF's ShowInTaskbar=false sets a hidden owner via GWL_HWNDPARENT (Window.cs SetTaskbarStatus/SetOwnerHandle). Verify with GetAncestor(hwnd, GA_PARENT=1), which the GetParent docs recommend ('To obtain the parent window and not the owner ... use GetAncestor with the GA_PARENT flag').
- [corrected] Since Windows 8, WS_EX_LAYERED can be used on child windows, and color-keyed or zero-alpha pixels let mouse messages through.
  -> Both sentences are verbatim in Window Features. MISSING REQUIREMENT: 'Using Windows' says 'In order to use layered child windows, the application has to declare itself Windows 8-aware in the manifest' and shows <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>. The report's manifest omits this compatibility section. A WPF AllowsTransparency window (WS_EX_LAYERED) reparented as WS_CHILD needs it. Deskband11Lib's sample manifest includes it.
- [could-not-verify] Local experiment on 26200.9711: a window owned by Shell_TrayWnd automatically got WS_EX_TOPMOST and stayed above the taskbar after SetWindowPos(taskbar, HWND_TOP). An unowned HWND_TOPMOST window fell below.
  -> The verifier did not re-run this, to avoid creating cross-process windows without user consent. The outcome is what the SetWindowPos docs predict. Behavior on real taskbar clicks and on fullscreen demotion remains untested.
- [corrected] The hidden Start child HWND and TrayNotifyWnd are cheap and accurate.
  -> They were accurate at inspection time, but the TrafficMonitor maintainer says on #2098 that on some Win11 systems the TrayNotifyWnd position sometimes cannot be obtained. This is the cause of the boot-time 'widget lands on Start' bugs #2098 and #2194. Validate the rects (non-zero, inside the taskbar) and wait for UIA geometry before the first attach.
- [could-not-verify] UISettings/ColorValuesChanged reflects the app color mode and may not fire for SystemUsesLightTheme changes alone.
  -> The Microsoft Learn page documents only UISettings.GetColorValue(Foreground) and ColorValuesChanged, and does not say which mode they track. Deskband11Lib's WinUI facade uses ColorValuesChanged as its trigger for re-reading SystemUsesLightTheme, which suggests it does fire. Both modes are dark on this machine, so the claim could not be tested without changing user settings. Use WM_SETTINGCHANGE 'ImmersiveColorSet' plus a registry read.