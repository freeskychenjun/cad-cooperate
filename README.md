# cad-cooperate

**在你已经打开的那张 CAD 图纸里，和 AI 一起改图。**

不用导出、不用转换、不用另开一份副本：agent 直接读你眼前这张图、直接往上面画和改。改动**当场出现在绘图窗口里**，默认**不落盘**，一批改动**一步撤销**。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows-0078D4)
![CAD](https://img.shields.io/badge/CAD-ZWCAD%20%7C%20AutoCAD-2E8B57)
![Python](https://img.shields.io/badge/python-3.9%2B%20(optional)-3776AB)

> **关于截图**：本仓库不随附 CAD 界面截图——早期验收截图包含内部图纸名与本机路径，已按脱敏要求移除；验收结论与实测数字保留在正文与 `docs/` 的文字记录中。

> **In short (English).** A Windows CLI + agent-skill that reads and writes the *live* drawing you have open in ZWCAD (中望CAD) or AutoCAD through COM late binding. Writes are anchor-checked and read back, every batch is one undo step, nothing is saved unless you say so, and a resident daemon keeps each command at ~0.1–0.4 s. It is a **platform of primitives**, not a fixed feature set: an agent composes the 15 orthogonal commands into whatever task the user asked for.

---

## 目录

- [它解决什么问题](#它解决什么问题)
- [能干什么](#能干什么)
- [架构](#架构)
- [快速开始](#快速开始)
- [五个真实任务示例](#五个真实任务示例)
- [命令速查](#命令速查)
- [安全模型：为什么它敢碰你的图纸](#安全模型为什么它敢碰你的图纸)
- [已验证环境与实测数据](#已验证环境与实测数据)
- [已知限制](#已知限制)
- [目录结构与二次开发](#目录结构与二次开发)
- [深入文档](#深入文档)
- [许可与声明](#许可与声明)

---

## 它解决什么问题

CAD 里的 AI 助手通常只有两条路：**离线改文件**（ezdxf/ODA：改的是副本，用户看不到过程和结果，改完还要关图重开）或**截图+坐标点击**（脆弱、慢、看不懂实体）。

还有一种更有用的做法：**用户开着图纸，agent 走 CAD 自己的自动化接口，在同一份活文档上读写**。这样人看得见每一步、随时能喊停、一步就能撤销，而 agent 读的是真实实体与几何而不是像素。

把这件事做稳有三个硬约束，也正是本项目的全部难点：

| 约束 | 说明 | 本项目的应对 |
|---|---|---|
| 接口残缺 | 中望CAD v16 的 `EvaluateLispExpression` / `app.Eval` 是**空壳**（`E_NOTIMPL`）；`.NET` 托管 API 到 2019+ 才有 ⇒ 跨版本共同面只有 **COM 晚绑定 + 原生命令 + DXF** | 全部走晚绑定 COM；唯一的 LISP 通路是 `SendCommand` + `GetLispVariable`，并自己补出"写文件 + `__DONE__` 哨兵"的大图读侧快路径 |
| 原生命令脆弱 | 单条命令串 **≈2 KB 上限，超限静默失败不报错**；一次语法错会污染命令态，吞掉后面所有命令 | 分片 + 每条后校验提示符；批量写走 `entity` 的 JSON items，不拼长命令 |
| 状态是活的 | 用户随时在动：拖拽命令、开模态框、改图、关图、切文档 | 文档级锁 + 忙态自愈 + 写前重锚定 + 挂死检测；回读校验不符就整体回滚 |

所以这是一个**平台**而不是功能集：优化目标只有两个——**协作稳定性**和**agent 完成任务效率**。新功能优先做成原语，而不是新增一个只能干一件事的命令。

## 能干什么

按用户原话就能触发的任务（详见 [命令速查](#命令速查) 与 [SKILL.md](SKILL.md)）：

**读 / 查**
- "这张图上有什么？多少实体、哪些图层、哪些块、单位是什么、范围多大？"（`info` / `read` / `query`）
- "RIVER 层一共多少实体、各类型多少、总长多少米？"（`query` + 本地算）
- "把写着「设计水位」的文字都找出来，句柄给我。"（`query -Text <正则>`）
- "这条命令改完和改之前差了什么？"（`snapshot` / `diff`，人改的也算）

**写 / 改**
- "这三处「设计水位」都改成「设计水位（复核）」，字高统一成 2.5。"（`entity update`）
- "RIVER 上的实体全移到 RIVER-2。"（`query` 取句柄 → `entity` 批量改图层）
- "删掉所有零长度线 / 给这批文字打个标记再按标记删。"（`entity delete` + `xdata`）
- "整张断面图右移 50 mm。"（`entity move` / 或 `undo` 后按新 `-Origin` 重画）
- 一步撤销上一批：`undo -Token <上一步返回的 token>`

**出图**
- "这张 30 点大断面出张断面图，含设计水位 42.30 m。"（`section`）
- "示例站最近 4 次断面套绘一张，标注别挤。"（`section` 多序列 + 图例 + 标注密度自适应）
- 数据来自 `.xlsx` / `.xls`（实测大断面成果表，含数据质量闸门）或水情数据库（`scripts/section/prepare.py`，见下）

**体检 / 统计**
- "这图体检一下，有没有空图层、关层还有实体、零长线、文字重叠？"（`scripts/audit/`，17 条规则，输出 MD/JSON，可当 CI 门禁）
- "按图层统计实体数量/长度，导成表。"（`query` 分页 + 本地汇总）

**会话**
- `doctor` 环境自检、`status` 实例/文档状态、`open` 附着或新建、`hint` 用户可见提示（托盘 AI 徽标 + 标题前缀）、`show` 把 CAD 窗口置前、`finish` 收尾（只退自己起的实例，默认不保存）

## 架构

```
                 agent / 脚本 / 人
                        │  一次性 4–5 s？不，约 0.1–0.4 s
                        ▼
        bin\CadClient.exe <cmd> [flags]          原生 60–100 ms 启动（cmd/PS 薄壳等价可用）
                        │  命名管道，一段 JSON 进 / 一段 JSON 出
                        ▼
              常驻 daemon（按版本隔离、可自愈、可 reset）
                        │  文档级锁 Local\cad-doc-<sha16(全路径)>，同图命令串行
                        ▼
        CadHelper.exe（按 helper 源码哈希编译缓存，命中只花几次 stat）
                        │  COM 晚绑定 CreateObject / GetActiveObject
                        ▼
         ZWCAD Classic 16 · ZWCAD 2025 · AutoCAD 2021 · AutoCAD 2007（注册表自动发现其它版本）
                        │
                        └─ 大图读侧快路径：LISP 写 %TEMP% 文本 + __DONE__ 哨兵 + GBK 读回
                         （目标产品没有 LISP 变量桥时自动启用，失败回退 COM）
```

- 入口三选一：`bin\CadClient.exe`（首选）/ `scripts\cad.cmd`（等价薄壳，且负责首次编译）/ `powershell -File scripts\cad.ps1`（复杂参数、`serve` 管理命令、兜底）。
- **stdout 永远是一段 JSON**，顶层 `ok` 是唯一判据。退出码：`0` 成功、`2` 命令失败、`3` 门禁类失败（`VERIFY_FAILED` / `BAD_DATA` / `TOO_WIDE`）、`4` 引擎/输入错误。
- 中文、几何、大 `-Items` 一律**走文件**（`-RequestFile` / `-Items <file>`），命令行只传 ASCII 短参数——PowerShell 5.1 会按 ANSI 读无 BOM 的 `.ps1`，`cmd` 会吃掉 `^` `=` `%`。

## 快速开始

### 环境要求

- Windows 10/11（本机实测）
- .NET Framework 4.x（首次运行用系统 `csc.exe` 编译 client 与 helper，约 1–2 s，之后走哈希缓存）
- 中望CAD 2016 Classic / 中望CAD 2025 专业版 / **AutoCAD 2021（实测）· AutoCAD 2007（实测）**；其它 AutoCAD 版本由注册表 `AutoCAD.Application.NN[.M]` 动态发现（未逐个复验）
- Python 3.9+（**可选**，只有数据侧 `prepare.py` 与体检 `audit.py` 需要；依赖见 [`requirements.txt`](requirements.txt)：`xlrd>=2.0` / `openpyxl` / `ezdxf`）

### 拉下来跑第一条命令

```powershell
git clone https://github.com/freeskychenjun/cad-cooperate.git
cd cad-cooperate

# 环境自检：没开 CAD 也应正常返回一段 JSON（ok:false + NO_INSTANCE），而不是崩或弹窗
.\scripts\cad.cmd doctor

# 首次运行会自动用 csc 编译 client 与 helper（约 2–4 s，仓库里不带任何 exe），
# 于是全新克隆的第一条命令约 7 s；之后每条命令 0.1–0.4 s。
.\scripts\cad.cmd doctor -Deep          # 再做管道 / COM / 窗口 ping
```

### 作为 agent skill 安装

本仓库同时是一个 skill 包（[SKILL.md](SKILL.md) 是权威说明书）：

```powershell
.\publish.ps1 -WhatIf    # 干跑：列出会复制哪些文件（不碰磁盘）
.\publish.ps1            # 部署到 %USERPROFILE%\.agents\skills\cad-cooperate
.\publish.ps1 -Target <临时目录>\cad-cooperate -Force   # 或任意目录
```

部署是"staging + 原子切换"，中途失败不会留下半更新的安装；目标目录必须形如 `...\skills\<name>`（脚本会清理包里不存在的文件，`-Force` 显式放行）。装好后 DSH / Claude Code 等按 skills 目录约定自动识别，`SKILL.md` 里的 `description` 决定它在什么时候被触发。

### 选择产品（重要）

**默认 `-App any`：你不需要告诉它用中望还是 AutoCAD。** 只开着一个产品时自动用它（AutoCAD 靠运行进程区分 2007/2021…）；两个产品都在跑时，`-Doc` 指向的**那份图在谁那里开着就用谁**（不会在另一个 CAD 里再开一份）；只有当两族都在跑、目标图又不在其中一边时，才报 `AMBIGUOUS_PRODUCT` 让你选。

```powershell
.\scripts\cad.cmd status                                     # 自动：单族在跑就用它
.\scripts\cad.cmd open -Doc "D:\某图.dwg"                     # 自动：谁开着这份图就附着谁
.\scripts\cad.cmd status -App zwcad                          # 强制产品族（同族多版本再给 -ProgId）
.\scripts\cad.cmd status -ProgId ZWCAD.Application.16        # 中望 Classic 16
.\scripts\cad.cmd status -ProgId ZWCAD.Application.2025      # 中望 2025
.\scripts\cad.cmd status -ProgId AutoCAD.Application.24      # AutoCAD 2021
.\scripts\cad.cmd status -ProgId AutoCAD.Application.17      # AutoCAD 2007
```

同一台机器装多个产品时，**无版本 ProgID `ZWCAD.Application` 会被"最后启动的产品"改写**，所以同族多版本时建议显式给 `-ProgId`。默认的 `-App any` 会按"谁在跑 / 谁开着这份图"自动判定；只有当**两个产品族都在跑、目标图又不在其中一边**时才返回 `AMBIGUOUS_PRODUCT`——**不猜**。详见 [docs/progid-selection.md](docs/progid-selection.md) 与 [docs/autocad-2021-acceptance.md](docs/autocad-2021-acceptance.md)。

## 五个真实任务示例

### 1. 把断面数据画进用户开着的图（一步可撤销）

```powershell
# 数据 → 模型 JSON（xlsx 两列：起点距 / 高程）
python scripts\section\prepare.py --data tests\fixtures\section-30.xlsx --out %TEMP%\m.json

# 或直接读本机水情数据库（cross_section 表；库路径 `--db` 显式指定，或环境变量 HYDRO_DB——调用期读取）
python scripts\section\prepare.py --from-db <站码> --last 4 --out %TEMP%\m.json

.\scripts\cad.cmd open  -Doc "<你的图纸>.dwg" -ProgId ZWCAD.Application.16
.\scripts\cad.cmd section -Data %TEMP%\m.json    -ProgId ZWCAD.Application.16
.\scripts\cad.cmd undo  -Token <上一步返回的 undoToken> -ProgId ZWCAD.Application.16
.\scripts\cad.cmd finish -Quit                   -ProgId ZWCAD.Application.16
```

`section` 会按图幅/比例尺建层（`AI-SEC-*`）、画地面线、水位虚线、点号与高程标注、深泓标记、可选比例尺；多序列时每序列独立图层+颜色+图例，标注密度按"点位间距 / 字高"自动降级（`labelPolicy`）。画完**回读校验**，不符整体回滚。

**数据侧输出约定**：`prepare.py` / `audit.py` 的 stdout 恒为**单段 ASCII 安全 JSON**（中文走 `\u` 转义，PS 5.1 可直接 `ConvertFrom-Json`）；**非致命内部失败写 stderr** 一行 `[prepare] warn <CODE>: ...`（如 `DB_STATION_INFO_FAILED` / `DB_WATER_LEVEL_FAILED` / `WORKBOOK_CLOSE_FAILED` / `TMP_CLEANUP_FAILED` / `CONFLICT_FLAG_INVALID`，同一告警只打一次）——「DB 表坏了」与「确实没记录」因此可区分。退出码：`0` 成功、`2` 坏输入/用法（含 `NO_INPUT`/`BAD_INPUT`/`NO_OPENPYXL`/报告路径不可写）；`audit.py -ExitCode` 命中 blocker 时为 `3`。`build_sections_*.py` 是**面向人的逐行文本**（进度 + 汇总），出错时 stderr 一行 `error <CODE>: <msg>` + exit 2。

### 2. 批量改文字 / 换图层（锚点 + 回读 + 回滚）

```jsonc
// items.json
{"items":[
  {"op":"update","handle":"1A2","expect":{"type":"AcDbText","layer":"ANNO","text":"设计水位","height":2.5},
   "set":{"text":"设计水位（复核）","height":3.0}},
  {"op":"update","handle":"1A9","expect":{"type":"AcDbText","text":"设计水位"},"set":{"layer":"ANNO-2"}}
]}
```

```powershell
.\scripts\cad.cmd query  -Text "设计水位" -ProgId ZWCAD.Application.16   # 先拿句柄
.\scripts\cad.cmd entity -Items items.json -DryRun -ProgId ZWCAD.Application.16   # 只看 plan
.\scripts\cad.cmd entity -Items items.json        -ProgId ZWCAD.Application.16   # 真正写
```

语义是**三趟**：Pass1 只要有一条 `expect` 不符 → **整批一条不改**（`ANCHOR_MISSING`，带字段级 detail）；Pass2 一个撤销组内写入；Pass3 逐条回读，不符 → `VERIFIED_FAILED` + `rolledBack:true`。锚点句柄消失但指纹唯一匹配时自动重定位（`relocated:true`）。

### 3. 图纸体检（只读，可当门禁）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\audit\run.ps1 -ProgId ZWCAD.Application.16 -ExitCode
# 也可以吃现成 JSON，完全不碰 CAD：
#   ... -InfoJson tmp\info.json -ReadJson tmp\read.json
```

17 条规则（空图层、图层命名、关/冻结层仍有实体、线型缺失、字高离群、文字重叠、零长线、重复实体、未闭合多段线、超范围、单位缺失……），唯一 blocker 是"冻结/关闭图层上还有实体"。**数据不足的规则进 `skipped[]` 并明写"不要当作已通过"**，不假装通过。干净图 0 命中 0 误报，真实水系图（624 实体）3 命中 0 误报。规则表见 [docs/audit-rules.md](docs/audit-rules.md)。

### 4. 大图读侧（几万实体也不卡）

```powershell
.\scripts\cad.cmd read  -Mode handles -ProgId ZWCAD.Application.2025   # 36,558 实体 ≈ 0.7 s
.\scripts\cad.cmd query -Type text     -ProgId ZWCAD.Application.2025   # 命中 10,293 ≈ 0.4 s
```

`read` 分三级：`handles`（句柄/类型/图层，最快）→ `summary`（默认，+包围盒）→ `geom`（+顶点/文字/半径）。实体数超过阈值且目标产品没有 LISP 变量桥时，自动改走"LISP 写 `%TEMP%` + `__DONE__` 哨兵 + 读回"的快路径（响应里 `readPath:"lisp-file"`），失败回退 COM 并给 `LISP_FILE_FALLBACK`。

### 5. 让用户看得见"AI 正在接管"

```powershell
.\scripts\cad.cmd hint -Text "正在画示例站断面图" -Tray -ProgId ZWCAD.Application.16
```

橙色 **AI** 圆标常驻通知区，悬停显示 `〔AI 接管中〕<图名> - <当前步骤>`，右键可"打开 CAD 窗口 / 复制接管信息 / 结束接管"；`hint -Clear` 与 `finish` 会移除，CAD 退出或 4 小时后自退。

> 注意：**中望CAD 2025 的标题栏是自绘的（Qt），外部写入的 `〔AI协作〕` 前缀不会重绘**（Classic 16 的 MFC 标题栏可以）。所以在 2025 上请依赖**托盘徽标 + 命令行横幅**；`hint` 会如实返回 `captionVisible:false`，不承诺"标题栏会变"。

## 命令速查

| 命令 | 作用 | 关键参数 |
|---|---|---|
| `doctor` | 环境自检：产品、进程、能力探测、`E_NOTIMPL` 清单 | `-Deep`（管道/COM/窗口 ping → `CAD_HUNG`） |
| `status` | 实例与文档列表、是否忙、产品、`mappedBy` | 返回 `docs[]`、`cmdActive` |
| `open` | 附着/新建文档 | `-Doc <path>`、`-Launch`、`-New`、`-ReadOnly`、`-NoHint` |
| `info` | 图纸概况：单位/比例/范围/图层/块/布局/计数 | 返回 `units`、`extents`、`layers[]`、`counts{}` |
| `read` | 实体读取（三级） | `-Mode handles\|summary\|geom`、`-Max`、`-Limit/-Cursor`、`-Space` |
| `query` | 过滤查询，只回命中 | `-Layer`、`-Type`、`-Text <正则>`、`-BBox`、`-XData`、分页 |
| `entity` | 写原语：`add/update/delete/move/copy` | `-Items <file>`、`-RequestFile`、`-DryRun`、`-Continue` |
| `section` | 画断面图（单序列/多年套绘） | `-Data <model.json>`、`-DryRun` |
| `snapshot` / `diff` | 实体指纹快照 / 与快照比对"这轮改了什么" | `-Out`、`-Since last\|<file>` |
| `undo` | 一步撤销一批 | `-Token <token>` 或 `-Count N` |
| `hint` | 用户可见接管提示（托盘/标题/横幅） | `-Text`、`-Tray`、`-Clear` |
| `show` | 把当前附着的 CAD 窗口显示/置前 | 返回 `hwnd/visible/foreground` |
| `finish` | 收尾：关自己开的文档、只退自己起的实例 | `-Quit`、`-SweepHidden`、`-TimeoutSec` |
| `serve` | daemon 生命周期 | `-Action start\|status\|stop\|reset\|recover` |

完整语义、全部 flag、错误码表、能力边界：[SKILL.md](SKILL.md)。故障按错误码排查（`NO_INSTANCE` / `MULTI_PRODUCT` / `DOC_AMBIGUOUS` / `DOC_LOCKED` / `ANCHOR_MISSING` / `BUSY_TIMEOUT` / `VERIFY_FAILED` / `SENDCMD_TOO_LONG` / `CAD_HUNG` …）：见 SKILL.md「故障排查」。

## 安全模型：为什么它敢碰你的图纸

1. **默认不保存。** 所有改动只在内存里，落盘由用户点头。**保存会清空撤销栈。**
2. **绝不杀用户的 CAD。** 只退本次 `open -Launch` 起、记为 `launched:true` 的进程；`finish -SweepHidden` 也只清"隐藏的自动化实例"（`MainWindowHandle=0`）。按映像名 `taskkill` 这类做法被明确禁止。
3. **写必须带锚点 + 回读。** 锚点是 `handle` + 期望字段指纹，不是"第 N 个实体"；Pass3 回读不符就**整批回滚**，不会出现"报成功其实没落上"。
4. **一批 = 一个撤销组。** 用户一次 `Ctrl+Z`（或 `undo -Token`）回到动手之前，不需要逐条拒。
5. **中间文件不落你的图纸目录。** 快照/导出/模型 JSON 一律写 `%TEMP%`（单位透明加密软件会拦截特定路径与扩展名，详见能力边界）。
6. **同图串行。** 文档级锁 `Local\cad-doc-<sha16>`；并发 agent 抢同一张图会排队并如实返回 `DOC_LOCKED{holder, waitedMs}`，不会互相踩。
7. **写前重锚定。** 两条命令之间用户改过图，会返回 `DOC_CHANGED_OUTSIDE` + `diffSummary`，而不是照旧句柄乱写。

## 已验证环境与实测数据

三套真实产品、同一套命令、同一份数据，**出图几何逐字段一致**（`tests/cross_app_diff.py` 三对全 MATCH）：

| 产品 | 版本 | 84 实体套绘（`section`） | `read` 84 实体 | 一步撤销 |
|---|---|---|---|---|
| 中望CAD Classic | 16.0.1111（32 位） | 662 ms | 217 ms | 84→0（543 ms） |
| 中望CAD 专业版 | 2025 | 2,396 ms | 1,527 ms | 84→0（336 ms） |
| AutoCAD | 2007（17.0s） | 631 ms | 303 ms | 84→0（194 ms） |

<sub>注：中望 2025 没有 LISP 变量桥 ⇒ 小图读侧纯 COM，比 Classic 慢 3–5 倍；大图会自动切 LISP 写文件快路径。</sub>

性能基线（本机实测，非承诺；环境为 WPS 在线、空闲内存仅 2.4–3.3 GB，抖动 ±100–200 ms）：

| 场景 | 数值 |
|---|---|
| `status`（warm daemon，client 入口） | **108–219 ms**（改造前 4,033 ms） |
| 单实体 `entity`（3 会话并发，wall p50） | **338 ms**；产品侧（helper）108 ms |
| `read` 624 实体真实水系图 | handles **571 ms** / summary 1.8 s / geom 1.6 s（改造前 209 s） |
| `read` 36,558 实体地形图（中望 2025） | handles **743 ms**（改造前 ≈9 分钟） |
| `query -Type text` 36,558 实体 | **425 ms** / 命中 10,293（中文正确） |
| 单实体写复杂度 | **O(1)**（图内 0→624 实体，p50 195→334 ms） |
| 任务级（20 条真实任务基准） | 读/查 0.6–0.8 s、改图 2–12 s、断面 8–28 s |

## 已知限制

| 限制 | 说明 |
|---|---|
| **XData 读侧** | 中望的 `GetXData` 是 by-ref VARIANT，.NET 晚绑定取不回 ⇒ 写入真实，但**读侧依赖同机 `%TEMP%` 镜像**；跨机器/跨会话读不到，需重新打标。 |
| **中望 2025** | 无 LISP 变量桥（`Get/SetLispVariable`、`EvaluateLispExpression` 全 `DISP_E_UNKNOWNNAME`）⇒ 小图读侧只能 COM。 |
| **中望 Classic 16** | 所有实体都不实现 `GetBoundingBox` ⇒ `read -Mode summary` 的包围盒覆盖约 97%，其余标 `BBOX_UNAVAILABLE_FOR_TYPE`。 |
| **AutoCAD 2007** | 冷启动约 **135 s**（唯一硬伤；实战靠"附着用户已开实例"规避）；`finish -Quit` 需 `-TimeoutSec 180`。 |
| **AutoCAD 2021+（SECURELOAD）** | `SECURELOAD=1` 时 LISP 文件加载（LSP/FAS/VLX/MNL）受管：未信任的 `%TEMP%` 目录会弹**模态**安全框并把 CAD 卡住。工具默认跳过注入、降级 COM 并告警 `LISP_TRUST_BLOCKED`；一次性修：`cad.cmd trust -Action addTemp`（写 TRUSTEDPATHS）。详见 [docs/autocad-2021-acceptance.md](docs/autocad-2021-acceptance.md)。 |
| **老式 2D/3D 多段线** | `AcadPolyline.Coordinates` 是 (x,y,z) 三元组（LWPOLYLINE 才是二元组）；顶点由 COM 一次调用补齐，LISP 只出 `elevation`。 |
| **表格 / 动态块** | COM 面缺 `AddTable`（v16）、动态块属性读写不可靠 ⇒ 表类任务建议 LISP 原生命令兜底或标记为人工。 |
| **只碰模型空间** | 锁定/冻结图层、外部参照内实体、只读图纸一律拒绝（`READONLY_*` / `XREF_SKIPPED`）。 |
| **不做图上批注** | 批注/标记会进打印、工程量与外部参照 ⇒ 本项目明确不做"把注释画进图里"，改用托盘徽标与命令行横幅告知。 |
| **透明加密** | 单位装了文档透明加密（DGS 类）时，**CAD 写出的 `.dxf` 可能是密文**、特定目录写文件可能超时 ⇒ 中间文件只用 `%TEMP%`，DXF 只作审计旁路。 |
| **并发** | 多 agent 同图靠文档锁串行；实测有锁后 p95 从 11.7 s 降到 6.1 s，但**不会变快**，只保证不损坏、不丢失。 |
| **chaos 覆盖** | 真实"用户正在拖拽命令/弹模态框"无法从进程外注入（中望 2025 实测），只能靠 `-FaultInject busy` 验证告警路径。 |

## 目录结构与二次开发

```
README.md                 本文件
SKILL.md                  权威说明（命令语义/铁律/错误码/能力边界/性能基线），skill frontmatter 在此
DESIGN-M1.md              M1 设计：命令行契约、JSON schema、断面图契约与验收口径
publish.ps1               打包部署（staging + 原子切换 + 干跑 + 安全门）
scripts\
  cad.ps1 / cad.cmd       入口（兼容包装 + 管理命令 + 首次编译 bootstrap）
  client\CadClient.cs     原生客户端（→ bin\CadClient.exe，命名管道）
  helper\CadHelper.cs     入口与调度（**只剩 173 行**：`Program.HandleRequestText` + `Main`；C#5，按源码哈希编译缓存）
  helper\{Core,ComInterop,Com,Procs,Cad,State,Cmd,Daemon}.cs   核心数据模型(JMap/CadError/Ctx) / OLE 消息过滤器 / COM 晚绑定 / 进程探测 / 产品选择 / 会话状态 / 命令行守卫 / 常驻 daemon
  helper\{Commands,CommandsRead,CommandsSection,CommandsVerify}.cs   **四个 partial**：共享参数 helper + 会话诊断（doctor/status/open/info/caption/cmdecho/finish） / read（Read·DescribeEntity·bbox·句柄） / section·写图·rollback·undo / 回读 verify
  helper\{Edits,EditsQuery,SectionOverlay,Stability}.cs   写原语 / 查询 / 断面绘制 / 稳定性
  helper\{LispTemplate,LispScan,LispWire,LispFile,LispGeom}.cs   LISP 快路径：模板+三重校验、注入管道单一实现、文本线解析、E5 canvas / E6 几何 dump 薄入口
  helper\{Json,PipeIo}.cs  可离线测的纯逻辑单元（自研 JSON writer/parser、命名管道 framing）
  section\prepare.py      数据侧 CLI 薄壳（实现在 prep_io / prep_yssd / prep_db / prep_model）
  section\qa_yssd.py      实测大断面成果表 QA（块清单/可疑点/两次施测对比）
  section\section_lib.py  断面提取两条路线的单一实现（from_dmx 等高线求交 / gcd 块 Z 共用）
  audit\                  只读图纸体检（17 条规则 + runner + 报告）
  tray\                   WinForms 托盘徽标（纯 ASCII 源码 + ui.json 中文文案）
  probe\                  COM 能力探针（立项期的实测脚本，保留作证据）
  bench\run_bench.ps1     20 条任务级基准 runner
tests\                    计算侧自测 / 独立核对器 / 跨产品几何比对 / DXF 往返 / LISP 解析离线测试
.github\workflows\        离线 CI（windows runner，无 CAD：数据侧自测 + 体检规则 + 端到端 dry-run + csc 解析测试）
docs\                     全部深入文档与实测截图（见下）
research\                 立项前的可行性研究（COM 探针、API 面、同类模式）
```

**构建**：无需构建系统——`scripts\cad.cmd` 首次运行会用系统 `csc.exe` 编译 client（`bin\CadClient.exe`）与 helper（`bin\cache\<sha12>\CadHelper.exe`，按"源码数 + 最大 mtime + 各文件大小"哈希），源码一改自动重编。托盘 exe 同理（`scripts\tray.ps1` 首次缺 exe 时编译）。

**回归与基准**：

```powershell
# 计算侧自测 + 端到端回归（-NoCad 表示不开 CAD，纯离线分支应全绿；跑不起来的检查项会如实记 SKIP）
python tests\selftest.py                     # 41 项：prepare/verify/夹具/负例/schema 往返
python tests\audit\test_audit.py             # 体检规则引擎 + 报告渲染
python tests\bench\selftest_checkers.py      # 基准检查器负例（必须拒错）
python tests\selftest_sections.py            # 断面提取两条路线（section_lib + 两个薄入口，85 断言）
python tests\selftest_compare_source.py      # --compare-source（DB vs 源表，合成 DB+源表夹具，84 断言）
powershell -NoProfile -ExecutionPolicy Bypass -File tests\selftest_lisp.ps1                      # C# 离线 6 套（LispWire 106 / LispTemplate 53 / Json 132 / LispScan 119 / Core 103 / ComCmd 131）
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run_m1.ps1 -DryRunOnly -NoCad -Python <你的python路径>

# 20 条任务级基准（自起独占实例，采成功率/命令数/墙钟，收尾归零进程）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\bench\run_bench.ps1 -Entry client -Product zwcad16 -Repeat 1

# 跨产品几何一致性
python tests\cross_app_diff.py --read a=tmp\compare\zwcad16\read.json --read b=tmp\compare\zwcad2025\read.json
```

以上离线层已接进 `.github\workflows\offline-ci.yml`（push/PR 自动跑；**在线 CAD 部分永远不在 CI 里跑**，领域固有约束）。**首次实跑全绿**（[run 41943999965](https://github.com/freeskychenjun/cad-cooperate/actions/runs/41943999965)），当前工作流 **12 步**：python 3.9 语法门（安装的解释器是 3.11，"3.9+" 由 `tests\check_py39_syntax.py` 的 `ast.parse(feature_version=(3,9))` 把关）→ 数据侧 41 → 体检 89·0·3 → 检查器负例 → 断面提取 85 → **compare-source 71** → 端到端 dry-run（**无 CAD 时 8·0·4**：其中 2 步按设计 SKIP；有 CAD 时 10·0·2）→ C# 6 套 644 → 证据 artifact。测试与基准的产物一律写 `tmp\`（已在 `.gitignore`，按需再生）。数据侧依赖统一见 [`requirements.txt`](requirements.txt)（本机开发环境里另有离线兜底库 `tools\py-libs`，不入库）。

## 深入文档

| 文档 | 内容 |
|---|---|
| [SKILL.md](SKILL.md) | 命令语义、10 条铁律、错误码表、能力边界、性能基线（**先读这个**） |
| [docs/M3-E2-writes.md](docs/M3-E2-writes.md) | `entity` 写原语契约：三趟校验、回滚、xdata 限制 |
| [docs/M3-E3-stability.md](docs/M3-E3-stability.md) | 文档锁 / 忙态自愈 / 写前重锚定 / 挂死恢复的语义与实测 |
| [docs/progid-selection.md](docs/progid-selection.md) | 多产品并存时如何选 ProgID（无版本 ProgID 会被改写） |
| [docs/autocad-2021-acceptance.md](docs/autocad-2021-acceptance.md) | **AutoCAD 2021 实机验收**：版本动态发现 / SECURELOAD 弹窗与 `trust` / 老式多段线顶点 / 块属性 / 性能 |
| [docs/audit-rules.md](docs/audit-rules.md) | 图纸体检 17 条规则的判据、阈值与误报边界 |
| [docs/M3-E1-perf.md](docs/M3-E1-perf.md) | 性能底座：daemon / client / read 分级的改造与实测 |
| [docs/M3-E5-lisp-file-canvas.md](docs/M3-E5-lisp-file-canvas.md) | LISP 写文件大图读侧、接管可见性与 UX 修复 |
| [docs/M3-E6-geometry-dump.md](docs/M3-E6-geometry-dump.md) | 大图**几何 dump 快路径**（坐标/顶点/等高线高程）、LISP 模板纪律、注入治理与复测（含"单次响应体上限"这条已知限制） |
| [docs/section-model-v2.md](docs/section-model-v2.md) | `section.model.json` **schema v2** 契约（多序列套绘；已脱敏入库） |
| [docs/optimization-plan.md](docs/optimization-plan.md) / [docs/optimization-executed-2026-11-05.md](docs/optimization-executed-2026-11-05.md) | 一轮只读分析报告 + 它的执行记录（采纳/未采纳、验证矩阵、遗留） |
| [docs/M3-E3-acceptance.md](docs/M3-E3-acceptance.md) / [docs/M3-E2-acceptance.md](docs/M3-E2-acceptance.md) | 验收记录：并发、chaos 六类、跨产品、负例 |
| [docs/M3-E4-baseline.md](docs/M3-E4-baseline.md) | 20 条任务基准的对照基线 |
| [docs/real-data-three-cad-comparison.md](docs/real-data-three-cad-comparison.md) | 真实断面数据在三套 CAD 上的逐字段对比 |
| [docs/overlay-and-labels.md](docs/overlay-and-labels.md) | 断面套绘、图例、标注密度策略 |
| [docs/tray-badge.md](docs/tray-badge.md) | 托盘徽标的调用链、清理规则与可见性 |
| [research/](research/) | 立项期可行性研究：`com-probe.md`（探针实测）、`api-surface.md`（API 面）、`pattern.md`（同类模式）、`feasibility-verdict.md`（结论） |

## 关于脱敏与可复跑

仓库里**不含真实图纸、真实站名/站码、本机绝对路径和界面截图**：

- 示例一律用占位符（`<站码>` / `<你的图纸>.dwg` / `%HYDRO_DB%` / `<repo>`），照着替换即可；
- 数据侧数据库路径不硬编码，改为环境变量 `HYDRO_DB`（**每次调用时读取**，`import` 后再设也生效），或 `prepare.py --db <路径>` 显式指定（显式优先；显式空串仍是 `NO_DB`）；
- 真机证据快照（`tests/audit/real/`、`tests/audit/out/`）与所有截图均未随仓库分发，可用 `tests/audit/run-real-validation.ps1` 与 `tests/` 下各脚本在本机重新生成（对应的离线自测会如实 SKIP 而不是假装通过）。

## 许可与声明

MIT License，见 [LICENSE](LICENSE)。

- 本项目是**第三方非官方工具**，与中望软件（ZWSOFT）、Autodesk 无任何隶属或背书关系；ZWCAD、AutoCAD 为其各自商标。
- 通过 CAD 公开的 COM 自动化接口操作**用户自己打开的文档**，不修改、不绕过任何许可与加密机制。
- 你的图纸是你的资产：默认不保存、写前锚点 + 回读、一步撤销，但**首次在生产图纸上使用前请先在有备份的副本上试**。
