# M3-E5 LISP→临时文件快路径（大图全图扫描 9 分钟 → 亚秒级）

> 2026-11-04 21:00–21:40，core-cad（task-23）。用户实测缺口：**ZWCAD 2025 上 36,558 实体的地形图**，接管（attach+info+标题）100–400 ms 成功，但全图扫描 ≈**9 分钟**（2025 无 `Get/SetLispVariable` 桥，只能 COM 逐实体 ~15 ms/个），被 4 分钟调用超时截断。

## 一、方案

```
helper                                     CAD (LISP)                          helper
  │ 写 scan-<guid>.lsp  ──────────────►  (load "…/scan-<guid>.lsp")
  │ SendCommand("(load \"…\")")  ───────►  顶层 (progn …)：
  │                                          (ssget "X" <filter>) 遍历
  │                                          write-line "handle|type|layer|text"
  │                                          close + 写 scan-<guid>.done 的 __DONE__
  │ 轮询 .done 出现 __DONE__  ◄──────────
  │ 读 .txt，按 GBK(936) 解码，解析  ──► 返回现有 read 结构
  │ 删除 .lsp/.txt/.done
```

- `.lsp` 顶层直接 `(progn …)`（不用 defun）；`(load "…")` 是短命令（<100 字符），不受 2 KB 限制。
- 文件只落 `%TEMP%\zwcad-cooperate\`；**只读**：仅 `ssget/entget/open/write-line`，无 `setvar`/`command`/改图调用。
- 哨兵只认文件内容 `__DONE__`（不依赖 SendCommand 返回值）。
- 注入前 guard：`CMDACTIVE == 0`，`LASTPROMPT` 无 `Error`/`undefined`/`错误`；否则不注入。

## 二、编码（必须 GBK/936）

LISP 的 `write-line` 用**本地 ANSI 码页**写文件；实测该图中文注记为 GBK。helper 用 `Encoding.GetEncoding(936)` 读，UTF-8 读会得到乱码。
真实样本（用户图上第一条 TEXT）：`1079FC|TEXT|DLSS|砼加固` —— GBK 解码正确；同字节按 UTF-8 解码为乱码。

## 三、接入与降级

| 命令 | 触发条件 | 响应 |
|---|---|---|
| `read -Mode handles/summary` | 无 LISP 桥 **且** 模型空间实体数 > `-LispThreshold`（默认 2000） | `readPath:"lisp-file"` + `result.lispFile{lispInjectMs,lispSentinelMs,parseMs,totalMs,fileBytes,decodedAs:"GBK/936"}` |
| `query` | 同上，且 `-Space model`、无 `-BBox`/`-XData` 过滤 | `readPath:"lisp-file"`；`-Type` 下推为 `(ssget "X" '((0 . "TEXT,MTEXT,…")))`，`-Layer`/`-Text` 在客户端过滤（保持精确语义） |

- 失败（LISP 报错 / 超时 / 编码异常 / guard 拒绝）→ **回退 COM 扫描**，`warnings:LISP_FILE_FALLBACK`，响应 `readPath:"com"`。
- `-LispTimeoutSec`（默认 60）控制哨兵等待；`-Limit/-Cursor` 从 dump 文件分页（不重扫）。
- `-Mode geom` 仍走 COM（顶点量大、文件大）；**summary 的 bbox 坐标字段也不在 lisp-file 里**（只用 handle/type/layer/text），summary 走此路径时 bbox 不提供——A/B/回归时按字段说明使用。

## 四、实测（用户真实图，只读；机器有 WPS 负载，属 `environment:"load"`，仍远低于目标）

图：`%USERPROFILE%\Desktop\<示例图纸>.dwg`，**36,558 模型实体 / 17 图层 / 30 块 / 2 布局**。

| 操作 | wall（PowerShell 捕获） | wall（输出重定向到文件） | helper | lispFile |
|---|---|---|---|---|
| `read -Mode handles` | 4,595 ms | **743 ms** | **594 ms** | inject 344 / sentinel 344 / parse 154 / total 498 / **708,732 B** / GBK936 |
| `read -Mode handles -Limit 500` | — | 651 ms | — | 分页命中 500、`nextCursor=500` |
| `query -Type text` | 260 ms（10 条）/ **425 ms**（10,293 条） | — | — | total **10,293**、中文正确 |
| `query -Type line` | 145 ms | — | — | total **1,500** |
| `query -Type insert` | 367 ms | — | — | total **13,013** |
| `query -Layer DLSS` | 773 ms | — | — | total **492** |

类型分布自校验（与 lead 探针一致）：INSERT 13,013 + TEXT 10,293 + POINT 9,423 + LINE 1,500 + LWPOLYLINE 1,495 + POLYLINE 810 + PROXY 22 + HATCH 2 = **36,558 = ModelSpace.Count**。

> 第一次测量（4.6 s）是 PowerShell 捕获 3 MB JSON 的开销；重定向到文件后为 **743 ms**，达到 `<5 s` 目标（helper 侧 594 ms）。
> 目标 2–3 s 以内、且远低于原 9 分钟；`readPath:"lisp-file"` 与 `lispFile` 字段已落响应。

## 五、坑与注意

1. **`(setq x (1+))` 等坏 LISP 不会留下 `Missing:`**（2025 中文界面 `LASTPROMPT` 只剩 `命令:`）——guard 改用 `Error`/`undefined`/`错误` 关键字 + `CMDACTIVE`。
2. 空文本不能 `strcat nil`（会报错）→ LISP 里 `(if (= _zwc_x nil) (setq _zwc_x ""))`。
3. `|` 分隔：文本可能含 `|`/换行 → C# 端按前 3 个 `|` 切分，其余归 text；CR/LF 归一为空格。
4. `.lsp` 与输出路径都用 `/`（AutoLISP 转义）；文件只落 `%TEMP%`。
5. **用户图纸有未保存改动**：全程只读、不保存；**绝不 `finish -Quit` 用户实例**；只停自己的 daemon。
6. 大图 JSON 3 MB：agent 侧建议 `-Limit` 分页；`readPath` 可判别数据来源。

## 六、未完成（等 2025 可测信号）

- **A/B 200 条逐字段**：需在 2025 实例上抽样，与 COM `read -Scope handle:<h>` 逐条比对 handle/type/layer/text（含中文）——用户实例中途关闭，待窗口。
- `query -Mode summary` 的 bbox 字段在 lisp-file 路径下为空（设计限制，已写第三节）。
- 证据目录 `tmp\evidence-lispfile\*` 已存：`read-handles-36558.json`（3.0 MB）、`query-text-classic16.json`、`read-handles-classic16.json`、`query-text-2025.json`；A/B 汇总脚本 `tmp\e5_ab.ps1` 就绪待跑。

## 七、相关 UX 修复（同批，lead 21:26 紧急项，代码已落盘）

用户"看不到窗口"事故（COM `/Automation -Embedding` 隐藏实例）：
1. `open -Launch` 默认 **reveal**：`app.Visible=true` + `ShowWindow(SW_RESTORE/SW_SHOW)` + `SetForegroundWindow`，并**重试 ≤6 s** 等 HWND 出现；响应 `app.windowVisible`、`result.reveal{hwnd,visible,shown,foreground,title}`；`-NoReveal` 供 headless。
2. 新增 **`show`** 命令：显示/置前当前实例，回 `hwnd/visible/shown/foreground/title`；`open -Reveal` 同义。
3. 附着到隐藏实例时 `warnings:HIDDEN_INSTANCE_REVEALED`（不再把 `MainWindowHandle=0` 当"用户实例"优先）。
4. **`finish -SweepHidden`**：WMI 列隐藏 `/Automation|-Embedding` 实例（仅本产品），`hiddenInstances[]` 总在响应里；`-SweepHidden` 只杀这些隐藏实例，并回 `sweptHidden[]`。
5. 标题标记**每命令幂等补标**（`〔AI协作〕` 前缀 + hint 文本，state 记 `captions`/`hintTexts`），CAD 重置标题后下一条命令自动写回。
6. 托盘集成：`hint -Tray` → `scripts\tray.ps1 -Start …`（hwnd/pid/doc 从响应取），`hint -Clear`/`finish` → `-Stop`（finish 无 `-Doc` 时逐个 tray 状态文件 stop）；`-Tray` 已进 client/zwc.ps1 开关表，UI 文案仍走 `scripts\tray\ui.json`。

第一轮可见性证据（Classic，测试实例已按 lead 要求关闭）：launched=true、`windowVisible=true`、`reveal{visible:true,shown:true,foreground:true}`、进程 `MainWindowHandle≠0`、hint 后任意命令标题仍带前缀。用户 2025 实例的 `show` 验证等 lead 信号（**当前机器 CAD 归 lead**）。

## 八、A/B 正确性（只读窗口 21:41–21:52，用户 2025 pid 50268 原地，未注入写命令）

- **类型归一化（必须）**：LISP `(assoc 0)` 给的是 DXF 名（TEXT/INSERT/POINT/LWPOLYLINE/POLYLINE/ACAD_PROXY_ENTITY…），COM `ObjectName` 是 AcDb 名。helper 在解析时把 DXF 名归一化为 AcDb 名（`TEXT→AcDbText`、`INSERT→AcDbBlockReference`、`LWPOLYLINE→AcDbPolyline`、`POINT→AcDbPoint`、`ACAD_PROXY_ENTITY→AcDbProxyEntity` 等），**保证 read/query 响应两种路径同构**；首次 A/B 因未归一化 200/200 type 全判不一致，归一化后 PASS。
- **测试方式**：一次 lisp-file dump（36,558 行）+ 随机 **200 条**与 COM `read -Mode summary -Scope handle:<h>` 逐字段比对（handle/type/layer/text，含中文）。
- **结果**：`checked=200 missing=0 diff=0` → **PASS**（`tmp\evidence-lispfile\ab-summary-2025.json`；回读耗时 27.3 s，200 次单 handle COM 查询，仅用于 A/B，不是快路径）。
- 类型分布（dump 归一化后）：AcDbText 10,293 / AcDbBlockReference 13,013 / AcDbPoint 9,423 / AcDbLine 1,500 / AcDbPolyline 1,495 / AcDb2dPolyline 810 / AcDbProxyEntity 22 / AcDbHatch 2 = **36,558**。
- **CMDECHO**：dump 的 LISP 首尾 `(setq _zwc_echo (getvar "CMDECHO")) → (setvar "CMDECHO" 0) … (setvar "CMDECHO" _zwc_echo)`，用户命令区不刷屏；两次 dump 间隔 ≥2 s。
- 本轮（含 CMDECHO 包装）`read -Mode handles` wall **961 ms**（helper 571 ms；lispFile inject 446 / sentinel 446 / parse 125 / 708,732 B）；文本查询 461 ms / 10,293。

## 九、托盘"AI"徽标集成（`hint -Tray`）

调用链：`zwc.cmd → ZwcClient`（解析 `-Tray`）→ 成功后 `powershell -File scripts\tray.ps1 -Start -Doc <doc> -Step <hint text> -Hwnd <app.hwnd> -CadPid <app.pid>` → `scripts\tray.ps1` 写 `%TEMP%\zwcad-cooperate\tray-<sha8(doc)>.json`（UTF-8）→ 启动 `bin\tray\ZwcTray.exe --state <json>`（`/target:winexe`，托盘图标 + 菜单）。
- `-Hwnd/-CadPid` 从 hint 响应 `app.hwnd/pid` 取，**不需要为托盘再起一次 CAD 或再 attach**。
- 清理：`hint -Clear` 与 `finish` 调 `tray.ps1 -Stop -Doc <doc>`；`finish` 无 `-Doc` 时客户端枚举 `tray-*.json` 逐个 stop（每图一个状态文件），避免"结束接管但图标残留"。
- UI 文案：`scripts/tray/ui.json`（UTF-8：prefix/balloonTitle/open/copy/end）；C# 与 .ps1 源码保持纯 ASCII（csc 按 ANSI 读无 BOM 文件会炸）。
- 生命周期：tray exe 在 state 文件消失 / CAD pid 退出 / 4 小时后自行退出；`tray.ps1 -Status` 可查（本轮冒烟：status 正常列出 lead 演示中的 state，`-Stop -Doc ux-smoke` 未动他人状态）。
- Windows 溢出区：图标可能在托盘"^"溢出区，首次 balloon 提示"AI 已接管你的图纸"，用户点"打开 CAD 窗口"可置前（菜单项 open 使用 state.hwnd）。