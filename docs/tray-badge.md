# 托盘「AI」徽标（tray badge）

> 2026-11-04，lead 演示 + core-cad 集成（task-23 附带 UX 项）。目的：用户在任何时候都能看到"AI 正在接管这张图"，并一键回到 CAD 窗口/结束接管。

## 调用链

```
zwc.cmd  →  bin\ZwcClient.exe  hint -Doc <图> -Text "<步骤>" -Tray
                                  │ 成功 (ok:true) 后读取响应 app.hwnd / app.pid / doc.name
                                  └─► powershell -File scripts\tray.ps1 -Start -Doc <doc> -Step <步骤>
                                          -Hwnd <hwnd> -CadPid <pid>
                                          │ 写 %TEMP%\zwcad-cooperate\tray-<sha8(doc)>.json (UTF-8)
                                          └─► bin\tray\ZwcTray.exe --state <json>   (WinForms NotifyIcon)
```

- `scripts\tray\ZwcTray.cs`：纯 ASCII、`/target:winexe`、不引用 CAD；图标/菜单文字全部来自 `scripts\tray\ui.json`（UTF-8）。
- `scripts\tray.ps1`：纯 ASCII wrapper（`-Start|-Stop|-Status`），首次缺 exe 时用 csc 编译；state 文件按 `sha8(文档名)` 命名，**一图一 state**。
- `scripts\tray\ui.json`：`prefix`（〔AI 接管中〕）、`balloonTitle`、`open/copy/end` 菜单文案。

## 清理规则

| 触发 | 动作 |
|---|---|
| `hint -Clear` | client 调 `tray.ps1 -Stop -Doc <doc>`（移除该图 state；tray exe 见 state 消失后自退） |
| `finish` | 同 `-Stop`；**未带 `-Doc`** 时 client 扫 `%TEMP%\zwcad-cooperate\tray-*.json` 逐个 stop，避免"结束接管但图标残留" |
| 自动 | tray exe 检测 state 文件消失、CAD pid 退出或 4 小时到期后自退 |

## 可见性 / Windows 溢出区

- 图标可能在任务栏右下角"^"溢出区：首次启动弹 balloon「AI 已接管你的图纸」，用户可点开。
- 菜单「打开 CAD 窗口」用 state.hwnd 调 `ShowWindow(SW_RESTORE)+SetForegroundWindow`（与 `show` 命令同一套 Win32 调用），解决"看不到窗口"。
- `tray.ps1 -Status` 返回所有 state（doc/step/pid/pidAlive）与运行中的 tray 进程，便于排查。

## 相关 UX 修复（同批）

`open -Launch` 默认 reveal（`app.Visible=true` + `ShowWindow` + `SetForegroundWindow`，6 s 重试等 HWND）、`show` 命令、`finish -SweepHidden`（只清隐藏 `/Automation -Embedding` 实例）、标题每命令幂等补标。详见 `docs\M3-E5-lisp-file-canvas.md` §七。