---
name: zwcad-cooperate
description: 在用户已经打开的中望CAD / AutoCAD 图纸里**实时**读写——agent 画图、改标注、批量改文字/图层、按图层或文字查询、出断面图（含历年套绘）、做图纸体检、导出统计，所有改动当场出现在用户眼前的绘图窗口里，默认不落盘、一步可撤销。当用户要求：在中望CAD/AutoCAD 里改图纸、在这张图上画断面/加标注/改文字、批量换图层、查图纸里有什么、图纸体检/校对、统计图纸工程量、把数据画进现有图纸、一边看着一边改图，或提到 zwcad、中望、CAD 协作、实时改图时使用。不适用：纯离线批量改 DWG 文件（不开 CAD 窗口，用 ezdxf/ODA 方案）、只看文件不碰 CAD 的场景。
version: 0.9.0
---

# zwcad-cooperate：agent 与人在 CAD 图纸里实时协作

一句话：用户开着图纸，agent 通过 COM 晚绑定 + 原生命令，在**同一份活图纸**上读写；改动当场可见、默认不保存、一批改动是一步撤销。

设计定位：这是**平台**不是功能集——用 14 条正交命令 + 一套写原语，让 agent 组合出任意任务；重点优化**稳定性**与**任务效率**（每命令约 0.1–0.4 s，而不是 4 s）。

## 适用 / 不适用

**适用**：在打开的中望CAD（2016 Classic / 2025）或 AutoCAD 2007 里改图、画图、查图、体检、统计；把 Excel/水文数据库数据画进现有图纸；批量改文字/图层/字高；断面图（单断面、多序列历年套绘）。
**不适用**：不打开 CAD、纯离线改文件（用 ezdxf / ODA）；Word/Excel/PPT（用 `wps-office-cooperate`）；只读报告不碰图纸。

## 入口（三选一，agent 默认用第一个）

```
bin\ZwcClient.exe <cmd> [flags...]          # 首选：60–100 ms 启动，直连常驻 daemon
scripts\zwc.cmd <cmd> [flags...]            # 等价薄壳（cmd → 上面那个 exe）
powershell -File scripts\zwc.ps1 <cmd> ...  # 兼容入口：复杂参数/兜底/管理的 serve 命令（~2.5 s）
```

- **stdout 永远是一段 JSON**（顶层 `ok` 是唯一判据）；退出码：`0`=ok、`2`=命令失败、`3`=门禁类失败（`VERIFY_FAILED`/`BAD_DATA`/`TOO_WIDE` 等）、`4`=引擎/输入错误。
- 中文、几何、`-Items` 大 JSON **一律走文件**（`-RequestFile`/`-Items <file>`），命令行只传 ASCII 短参数（PS 5.1 按 ANSI 读 .ps1、cmd 会吃掉 `^` `=` `%`）。
- **产品选择**：`-App zwcad|autocad`（默认 zwcad）；**强烈建议显式 `-ProgId ZWCAD.Application.16` / `ZWCAD.Application.2025` / `AutoCAD.Application.17`**。本机无版本 ProgID `ZWCAD.Application` 会被"最后启动的产品"改写；只开一个产品时不带 `-ProgId` 也能按进程自动识别，两个产品同开则返回 `MULTI_PRODUCT`（不猜）。

## 命令参考

### 会话/诊断

| 命令 | 说明 | 关键参数 / 返回 |
|---|---|---|
| `doctor` | 环境自检：产品、进程、能力探测、E_NOTIMPL 清单 | `-Deep` 另做管道/COM/窗口 ping → `CAD_HUNG` |
| `status` | 当前实例与文档列表、是否忙、产品、`mappedBy` | `docs[{name,fullName,saved,readOnly,count}]`、`cmdActive` |
| `open` | 附着/新建文档；可打"〔AI接管中〕"标题标记；`-Launch` 默认把窗口显示到前台 | `-Doc <path>`、`-Launch`、`-New`、`-ReadOnly`、`-NoHint` |
| `hint` | 用户可见提示：系统托盘「AI」图标 + 标题前缀 + 命令行横幅 | `-Text`、`-Tray`、`-Clear` |
| `show` | 把当前附着的 CAD 窗口显示/置前（用户第一反应"看不到窗口"时用） | 返回 `hwnd/visible/foreground` |
| `finish` | 收尾：关本次开的文档、只退**自己起的**实例、移除托盘图标、还原标题 | `-Quit`（必须给才会退出实例）、`-SweepHidden`（清理隐藏自动化实例）、`-TimeoutSec` |
| `serve` | daemon 生命周期 | `-Action start\|status\|stop\|reset\|recover` |

### 用户可见提示（接管标记）

接手瞬间要让用户看得见，按产品选通道：

1. **系统托盘「AI」图标（推荐，跨版本可靠）**：橙色 AI 圆标常驻通知区，悬停显示 `〔AI 接管中〕<图名> - <当前步骤>`，右键「打开 CAD 窗口 / 复制接管信息 / 结束接管」；`hint -Tray` 启动，`hint -Clear`/`finish` 移除；CAD 退出或 4 小时后自动消失。新图标可能落进任务栏「^」溢出区（Windows 默认），拖到任务栏一次即常显。
2. **窗口标题前缀 `〔AI协作〕`**：中望 Classic 16（MFC 标题栏）可显示；**ZWCAD 2025 的标题栏是自绘的（Qt），外部 `SetWindowText` 写进去但界面不重绘** —— 所以 2025 上不能依赖标题，`hint` 会如实返回 `captionVisible:false`。
3. **命令行横幅**：`hint` 在无标题通道时向 CAD 命令区打印带分隔线的接管横幅（注入前 `CMDECHO=0`、完成后恢复，避免刷屏）。

托盘实现：`scripts/tray/ZwcTray.cs`（纯 ASCII 源码→`bin\tray\ZwcTray.exe`，WinForms `NotifyIcon`，零 COM）+ `scripts/tray.ps1`（`-Start/-Stop/-Status`）+ `scripts/tray/ui.json`（中文文案集中在此，**不要在 .ps1/.cs 里写中文**）。

### 读

| 命令 | 说明 | 关键参数 / 返回 |
|---|---|---|
| `info` | 图纸概况：单位/比例/范围/图层/块/布局/计数 | `units{insunits,dimscale,ltscale}`、`extents`、`layers[]`、`counts{}` |
| `read` | 实体读取，**三级**：`-Mode handles`（句柄/类型/图层，最快）/`summary`（默认，+bbox）/`geom`（+顶点/文字/半径） | `-Max`、`-Limit/-Cursor` 分页、`-Space`、`-Doc` |
| `query` | 过滤查询，只回命中的句柄与摘要 | `-Layer`、`-Type`、`-Text <regex>`、`-BBox`、`-Space`、`-XData "APP:VALUE"`、`-Limit/-Cursor`、`-Mode` |
| `snapshot` | 实体指纹快照（落 `%TEMP%`，绝不写用户图目录） | `-Out <file>` |
| `diff` | 与快照比："这一轮到底改了什么"（人改的也算） | `-Since last\|<file>` → `added/removed/changed[fields{before,after}]` |

### 写（核心原语）

`entity`：`add / update / delete / move / copy`，**锚点校验 → 一个撤销组 → 回读校验 → 失败整体回滚**
```
{"items":[
  {"op":"add","type":"line|polyline|text|mtext|circle","layer":"L","geom":{...},"props":{"height":2.5},"xdata":{"app":"AI_MARK","value":"x"}},
  {"op":"update","handle":"1A2","expect":{"type":"AcDbText","layer":"L","text":"旧","height":2.5},"set":{"text":"新","height":3,"layer":"M"}},
  {"op":"delete","handle":"1A2","expect":{...}},
  {"op":"move","handle":"1A2","expect":{...},"delta":[10,0]},
  {"op":"copy","handle":"1A2","delta":[0,5],"count":2}
]}
```
- 参数：`-Items <file>` 或 `-RequestFile <full request>`；`-DryRun`（只出 plan）；`-Continue`（逐条隔离，失败只回滚该条）。
- 语义：Pass1 只要有一条 `expect` 不符 → **整批一条不改**，报 `ANCHOR_MISSING`（带字段级 detail）；Pass3 回读不符 → `VERIFIED_FAILED` + `rolledBack:true`；锚点消失但指纹唯一匹配 → `relocated:true`。
- 返回：`result{applied,rolledBack,failed[],items[{i,op,ok,handle,before,after,readBack}],timings}`, `undoToken`, `warnings[]`。
- 限制：只碰模型空间；锁定/冻结图层、外部参照内实体、只读图纸一律拒绝（`READONLY_DOC`/`XREF_SKIPPED`）。

`section`：按模型 JSON 画断面图（单序列或多序列历年套绘）
- 参数：`-Data <model.json>`、`-DryRun`；模型里含 `params{scale,paper,origin,labelEvery,...}`、`series[{label,points,waterLevels,style}]`。
- 返回：`created{handles,byType,byLayer}`, `verify{ok,issues}`, `undoToken`, `labelPolicy`, `series[]`, `legend`。
- 多序列 = 每序列独立图层与颜色 + 图例；标注密度按"点位间距/字高"自适应；`>3` 条水位时标签进图例。

`undo`：`-Token <上一批返回的 token>` 一步撤销该批；或 `-Count N`。

## 配套数据侧（Python，可独立跑）

- `scripts\section\prepare.py`：把数据做成 `section` 的模型 JSON。
  - `--from-db <站码> [--date/--years/--last N]`：直连 `hydro_data.db` 的 `cross_section`（162 站 / 5575 次施测 / 1965–2025）。
  - `--data <xlsx>`（两列起点距/高程）或 `--profile yssd`（"实测大断面成果表"多列块格式，支持 `.xls`，带数据质量闸门）。
- `scripts\audit\audit.py` + `run.ps1`：只读图纸体检（17 条规则，见 `reference/audit-rules.md`），吃 `info`/`read` 的 JSON，出 MD/JSON 报告，`-ExitCode` 可当门禁。

## 铁律（违反会伤到用户）

1. **默认不保存**：`section`/`entity` 只改内存，落盘必须由用户点头或显式 `save` 语义；**保存后 undo 栈会清空**。
2. **绝不按映像名杀进程**：只退本次 `open -Launch` 起、记为 `launched:true` 的 pid；用户的 CAD 永远不碰。
3. **写必须带锚点**（`handle` + `expect`）并回读；不要"按第 N 个实体"寻址。
4. **中间文件只落 `%TEMP%`**；不把快照/导出写进用户图纸目录。CAD 落盘的 `.dxf` 可能被单位透明加密。
5. 单条原生命令串（LISP/命令）**≤2 KB**，超了会被静默截断——需要批量时用 `entity` 的 items 或循环短命令。
6. 不要用 `EvaluateLispExpression`/`app.Eval` 做业务（ZWCAD v16 是空壳、2025/ACAD 无此成员）。
7. `read` 默认档是 `summary`；要几何/文字必须显式 `-Mode geom`（老脚本迁移注意）。
8. 多文档时**必须给 `-Doc`**（写命令缺省会 `DOC_AMBIGUOUS`；只有 `-DryRun` 允许 ActiveDocument 兜底）。
9. 夹具/示例 DXF 版本 ≤ 最老目标产品（AutoCAD 2007 只能开 R2007/AC1021）。
10. 并发多 agent 同图：靠文档级锁串行（`Local\zwc-doc-<sha16>`）；不要在同一张图上并行发起写命令。

## 能力边界（已知限制，别承诺做不到的事）

| 限制 | 说明 |
|---|---|
| XData 读侧 | ZWCAD 的 `GetXData` 是 by-ref VARIANT，.NET 晚绑定取不回 ⇒ 写入真实（`SetXData` + 注册 App），但**读侧依赖同机 `%TEMP%` 镜像**；跨机器/跨会话读不到，需重新打标。 |
| 中望 2025 | **没有 LISP 变量桥**（`Get/SetLispVariable`、`EvaluateLispExpression` 都 `DISP_E_UNKNOWNNAME`）⇒ 读侧只能 COM，比 Classic 慢 3–5 倍。 |
| Classic 16 | 所有实体都不实现 `GetBoundingBox` ⇒ `read -Mode summary` 的 bbox 覆盖约 97%，其余记 `BBOX_UNAVAILABLE_FOR_TYPE`。 |
| AutoCAD 2007 | 冷启动约 **135 s**（实战靠"附着用户已开实例"规避）；`finish -Quit` 需 `-TimeoutSec 180`。 |
| 表格/动态块 | COM 面缺 `AddTable`（v16）、动态块属性读写不可靠 ⇒ 表格类任务建议 LISP 原生命令兜底或标记为人工。 |
| 性能测量 | 本机（用户 WPS 在线）空闲内存仅 2.4–3.3 GB，测量抖动 ±100–200 ms；报数字必须标环境，静默测量需过安静门禁。 |
| chaos 覆盖 | 真实"用户正在拖拽命令中/模态框"无法从进程外注入（ZWCAD 2025 实测），只能靠 `-FaultInject busy` 验证告警路径。 |

## 性能基线（本机实测，非承诺）

| 场景 | 数值 |
|---|---|
| `status`（warm daemon，client 入口） | **108–219 ms**（曾 4,033 ms）|
| 单实体 `entity`（3 会话并发，wall p50） | **338 ms**；产品侧（helper）**108 ms** |
| `read` 624 实体真实水系图 | handles **571 ms** / summary 1.8 s / geom 1.6 s（曾 209 s）|
| **`read` 36,558 实体地形图（ZWCAD 2025）** | `handles` **743 ms**（LISP 写文件快路径，曾 ≈9 分钟）|
| **`query -Type text` 36,558 实体** | **425 ms / 命中 10,293**（中文正确）|
| 单实体写复杂度 | **O(1)**（图内 0→624 实体，p50 195→334 ms）|
| 任务级（20 条真实任务基准） | 读/查 0.6–0.8 s、改图 2–12 s、断面 8–28 s |

> 大图快路径：ZWCAD 2025 / AutoCAD 2007 **没有 LISP 变量桥**时，`read`/`query` 在实体数 >2000 会自动改走「LISP 写 `%TEMP%` 文本 + `__DONE__` 哨兵 + GBK 读回」（`readPath:"lisp-file"`），失败回退 COM 并 `LISP_FILE_FALLBACK`。

## 故障排查（按错误码）

| 错误码 | 含义 | 处置 |
|---|---|---|
| `NO_INSTANCE` | 没有对应产品的 CAD 实例 | 先让用户打开图纸；或 `open -Launch`（ACAD 冷启动 ≥3 min） |
| `MULTI_PRODUCT` | 两个产品同时开 | 显式 `-ProgId` 指定 |
| `PRODUCT_MISMATCH` | 显式 ProgId 与运行产品不一致（仅告警） | 核对 `-ProgId` |
| `DOC_AMBIGUOUS` | 多文档且没给 `-Doc` | 传精确全路径或图名 |
| `DOC_NOT_FOUND` / `DOC_GONE` | 文档没开 / 已被用户关掉 | 重新 `open` |
| `DOC_LOCKED` | 另一会话正持锁 | 等待或调 `-LockTimeout`；看 `holder{pid}` 是否自己 |
| `DOC_CHANGED_OUTSIDE` | 两次命令之间图纸被人改过 | 重新 `read` 取新句柄再写 |
| `ANCHOR_MISSING` / `ANCHOR_RELOCATED` | 锚点不成立 / 已重定位 | 重取句柄；`relocated:true` 表示按指纹唯一匹配 |
| `BUSY` / `BUSY_TIMEOUT` | CAD 正在命令中/有模态框 | 请用户按 Esc；或调 `-BusyTimeout` |
| `CMD_STATE_POLLUTED` | 提示符被坏命令污染 | 发送 Esc/清场后重试 |
| `READONLY_DOC` / `READONLY_LAYER` | 只读图纸/锁定图层 | 不写，报告用户 |
| `VERIFY_FAILED` / `GEOMETRY_MISMATCH` / `TEXT_MISMATCH` | 写入后回读不符（已整体回滚） | 检查 `expect`/`set` 值 |
| `SENDCMD_TOO_LONG` | 命令串超 2 KB | 拆批 |
| `XDATA_READ_FALLBACK` | xdata 读走同机镜像 | 见能力边界 |
| `TIMEOUT` / `CAD_HUNG` | 调用超时/CAD 疑似挂死 | `doctor -Deep` 确认；`serve -Action reset`（只杀本版本 daemon） |

## 复跑与验收

```
# 环境自检（无 CAD 也应返回 ok:false + NO_INSTANCE，不崩）
bin\ZwcClient.exe doctor -ProgId ZWCAD.Application.16

# 端到端：Excel → 断面图 → 回读 → 一步撤销
python scripts\section\prepare.py --from-db <站码> --last 4 --out %TEMP%\m.json
bin\ZwcClient.exe open -Doc "<你的图纸>.dwg" -ProgId ZWCAD.Application.16
bin\ZwcClient.exe section -Data %TEMP%\m.json -ProgId ZWCAD.Application.16
bin\ZwcClient.exe undo -Token <token> -ProgId ZWCAD.Application.16
bin\ZwcClient.exe finish -Quit -ProgId ZWCAD.Application.16

# 回归（开发用，不随包分发）
powershell -File tests\run_m1.ps1 -DryRunOnly      # 离线 9/0/2
powershell -File scripts\bench\run_bench.ps1 -Entry client -Product zwcad16 -Repeat 1   # 20 条任务基准
```

## 目录

```
SKILL.md                 本文件（唯一权威说明）
README.md                简介与安装
reference\               深入文档：写原语契约 / 稳定性语义 / 体检规则 / 产品选择 / 性能
scripts\zwc.ps1|.cmd     入口（兼容 + 管理命令）
scripts\client\          原生客户端源码（编译成 bin\ZwcClient.exe）
scripts\helper\          helper 源码（csc 按源码哈希编译缓存）
scripts\section\         数据侧：xlsx/xls/hydro_data.db → section model
scripts\audit\           只读图纸体检
bin\                     运行期产物（ZwcClient.exe、helper 编译缓存）
```
