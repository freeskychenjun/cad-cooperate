# zwcad-cooperate 项目分析与优化方案

> **执行状态（2026-11-05 更新）**：本报告的建议已逐项评审并执行 —— 第一轮（P0/P1/P3 + 工程化）与**第二轮（5.3 / 4.4 / 4.2 / 5.1 第一批 / 5.5）**的采纳决策、实际改动与验证证据见
> [`docs/optimization-executed-2026-11-05.md`](optimization-executed-2026-11-05.md)（第二轮见该文 §八）。本文保持原样作为**分析底稿**（不改写结论；文中行号是分析当时的快照）。

> 生成日期：2026-11-05 ｜ 版本基线：v0.9.0（commit `88b1a06`）+ 工作树未提交改动（M3-E6）
> 性质：**只读分析报告，未执行任何优化**。所有建议均标注证据位置（文件:行号），供逐项评审后排期。
> 分析范围：`scripts/`（C# helper/client + Python 数据侧）、`tests/`、`bin/`、`publish.ps1`、`docs/`、`.gitignore`、git 工作树状态。

---

## 一、项目现状概览

### 1.1 架构（值得肯定的骨架，优化均在此之上进行）

```
用户/Agent
  │  bin\ZwcClient.exe（原生客户端，60–100 ms 启动）
  │     ├─ ① 连常驻 daemon 命名管道（zwc-<源码哈希12位>）
  │     ├─ ② 无 daemon → 跨进程互斥锁下拉起 ZwcHelper.exe --serve（隐藏窗口）
  │     └─ ③ 仍失败 → one-shot helper（临时文件进出）
  ▼
ZwcHelper.exe（csc 按源码哈希缓存编译，4354 行核心 + 6 个分文件）
  │  COM 晚绑定（无互操作程序集）→ 中望CAD 2016/2025、AutoCAD 2007
  │  读快路径：LISP ssget+entget 写 %TEMP% 文本 + __DONE__ 哨兵（36 k 实体 0.4 s vs COM 310 s）
  ▼
图纸（默认不落盘；写命令锚点校验→单撤销组→回读→失败整体回滚）

数据侧（Python，可独立）：prepare.py（Excel/DB→断面模型）· audit.py（17 条规则体检）· build_sections_*.py（几何→断面 Excel）
```

### 1.2 规模

| 组件 | 行数 | 说明 |
|---|---|---|
| `scripts/helper/ZwcHelper.cs` | 4,354 | 巨石文件：JMap/Json/Com/State/Cad/Commands/Program/Daemon 全在其中 |
| `scripts/section/prepare.py` | 2,048 | Excel 双后端 + yssd 解析 + SQLite + 建模 + CLI 混在一起 |
| `scripts/audit/audit.py` | 1,657 | 规则引擎 + 报告渲染 + CLI |
| `scripts/helper/Edits.cs` / `SectionOverlay.cs` | 1,071 / 1,033 | 写原语 / 断面绘制 |
| `scripts/client/ZwcClient.cs` | 634 | 原生客户端（本次已读全文） |
| `scripts/helper/LispFile/LispGeom/LispTemplate.cs` | 285/238/252 | E5/E6 LISP 快路径（M3-E6 新增后两者未跟踪） |
| `build_sections_from_dmx.py` / `build_sections_gcd.py` | 311 / 269 | 两脚本约 50–55% 逐行重复 |

### 1.3 亮点（优化时不要破坏）

1. **性能攻坚有完整证据链**：status 4,033 ms→108 ms、大图读取 9 min→0.4 s，全部带实测环境与文档（SKILL.md 性能基线、M3-E4/E5/E6）。
2. **安全纪律是核心资产**：写原语"锚点→撤销组→回读→回滚"；`finish` 只退 `launched:true` 的 pid；M3-E6 在 `SelectionSet.Delete()` 事故（误删 3,850 实体）后固化了 LISP 只读白名单门禁（`LispTemplate.SafetyError`）。
3. **测试分层正确**：≥139 项纯离线断言（selftest 41 + test_audit 98 + checkers），三实现互证（prepare/make_expected/check_dxf 刻意不共享常量），负例自证（--tamper）；在线测试全部可 SKIP 退化。
4. **仓库卫生基线好**：.gitignore 每条带注释，571 KB 仓库 vs 236 MB 工作区全部挡在库外。

---

## 二、问题总览（按优先级）

| # | 优先级 | 问题 | 一句话影响 | 证据 |
|---|---|---|---|---|
| 1 | **P0** | `LispGeom` 解析不处理 `\|` 转义 | 文字含 `\|` 时坐标字段静默错位 | LispGeom.cs:115 vs LispFile.cs:238-251 |
| 2 | **P0** | `R\|`（圆/弧半径）记录生成但从不解析 | 半径静默丢失，与 COM 路径不一致 | LispTemplate.cs:237 → LispGeom.cs:143-166 无 "R" 分支 |
| 3 | **P0** | M3-E6 代码全部未提交（单 commit 仓库） | 误操作即丢整个 E6 功能 | git status：2 个未跟踪 .cs + 7 个已改未提交 |
| 4 | **P0** | `.gitignore` 缺 `/output/`、`*.dwl` | 一条 `git add -A` 就提交真实工程数据+机器信息 | output/ 1.1 MB 成品；design-water.dwl 含用户名/主机名 |
| 5 | **P1** | LISP 超时后脚本可能仍在 CAD 内运行 | %TEMP% 残留、CMDECHO 可能未恢复（已有一次真实事故） | M3-E6 §5.2；LispFile/LispGeom 的 finally 直接删文件 |
| 6 | **P1** | `LispFile.Scan` 与 `LispGeom.Scan` 约 60% 重复 | 双份守卫/轮询/解码逻辑，改一处漏一处 | 两文件 39-99 行近乎逐字相同 |
| 7 | **P1** | bin/cache 无淘汰（105 exe/13 MB/33 空目录） | 日级增速无限膨胀 | 见 §4.3；zwc.ps1 全文仅 1 处 Remove-Item |
| 8 | **P1** | Python 侧多处方/嵌套循环 + 静默吞错 | 数据量增大后平方级变慢；查询失败与无记录不可区分 | prepare.py:1137-1141；build_sections_from_dmx.py:139-157 等 |
| 9 | **P2** | ZwcHelper.cs 4,354 行巨石 | 每次改动触发全量重编译缓存失效 + 定位困难 | §1.2 |
| 10 | **P2** | 哈希配方三处独立实现 | 三者不一致时 client/daemon/缓存互不匹配 | ZwcClient.cs:53-80、zwc.ps1 两处 |
| 11 | **P2** | build_sections 两脚本 50-55% 重复 | 缺陷修一份漏一份 | difflib 实测 |
| 12 | **P2** | 编辑残留与死代码 | ZwcHelper.cs 重复行×2、qa_yssd 死参数 | ZwcHelper.cs:4162-4163, 2070-2071；qa_yssd.py:358 |
| 13 | **P3** | 无 requirements.txt；py-libs 兜底缺 openpyxl | 离线环境必挂；依赖只活在 README 散文里 | README.md:112,326 |
| 14 | **P3** | 无 CI | 139 项离线断言只能手工跑 | 仓库无 .github/ |
| 15 | **P3** | 文档悬空引用 | 4 张 png 无引用；tmp/real 契约文档被 ignore | docs/section-*.png；tests/README §9 |

---

## 三、P0：正确性与安全（建议立即处理，合计 ≈ 1–2 人日）

### 3.1 LispGeom 用裸 `Split('|')`，破坏 `\|` 转义协议

- **证据**：`LispTemplate.BuildDump` 生成的转义规则是文字内 `|` 写成 `\|`（LispTemplate.cs:193）；`LispFile.Scan` 的 `SplitRecord` 正确处理了该转义（LispFile.cs:238-251）；但 `LispGeom.Scan` 用 `line.Split('|')`（LispGeom.cs:115）。
- **影响**：几何 dump 模式（`read -Mode geom` 大图快路径）下，任何 TEXT/MTEXT/属性含 `|` 的实体，其 `x|y|z|nverts` 字段整体右移——`Num()` 对文本片段返回 0.0（LispGeom.cs:231-236），**坐标静默变 0**，顶点记录错挂到错误偏移。这正是"读路径最危险的一类缺陷：不报错、只出错数据"。
- **方案**：把 `LispFile.SplitRecord`/`UnescapeField` 提升为共享方法（如挪入 `LispTemplate` 或新建 `LispWire` 静态类），`LispGeom.Scan` 改用它；补两个回归用例（text 含 `|`、含 `\|`）到 `tests/selftest.py` 同级的离线解析测试。
- **验证**：构造含 `A|B` 文字的夹具行，断言 E 记录 9 字段各归其位；真机上用 `lispgen` + 小筛选复跑 M3-E6 §5.2 的验证表。
- **工作量**：0.5 人日。**风险**：极低（纯解析侧，不碰注入）。

### 3.2 `R|`（半径）记录有生成、无消费

- **证据**：模板为 CIRCLE/ARC 输出 `R|handle|radius`（LispTemplate.cs:237）；`LispFile.IsAuxRecord` 把 R 列为"忽略"（LispFile.cs:278）；`LispGeom.Scan` 的记录分派只有 V/P2/ELEV/H/CL（LispGeom.cs:143-166）；而 COM 路径返回 `radius` 字段（ZwcHelper.cs:2730）。
- **影响**：同一张图走 `lisp-file-geom` 与走 COM 得到的圆/弧实体字段不一致——下游若依赖 radius（如审计规则、几何计算）会静默缺失。
- **方案**：`LispGeom` 增加 `else if (kind == "R")` 分支写入 `m.Add("radius", Num(p[2]))`；`LispFile` 的 IsAuxRecord 保持不变。同时在 SKILL.md「能力边界」表更新该行为。
- **工作量**：0.25 人日。

### 3.3 提交 M3-E6 工作树（流程风险，最高优先的"零代码"动作）

- **证据**：仓库仅 1 个 commit；`LispGeom.cs`、`LispTemplate.cs`、`build_sections_*.py`、`docs/M3-E6-geometry-dump.md` 全部未跟踪，另有 7 个文件已改未提交（`publish.ps1` 的 manifest 已引用 M3-E6 文档）。任何 `git reset`/`checkout`/磁盘故障都会丢掉整个 E6 功能。
- **方案**：按现有 conventional 风格提交（如 `feat: M3-E6 大图几何 dump 快路径与 LISP 模板门禁`）。**建议先做 3.1/3.2 修复再一起提交**，避免把已知解析缺陷固化进历史。
- **工作量**：0.25 人日。

### 3.4 .gitignore 补两条规则

- **证据**：`output/`（1.1 MB：真实渠名 xlsx/csv + preview png，均可再生）与 `tests/bench/fixtures/design-water.dwl`（51 B，内容含 `Administrator / WIN-4KRLTLRR4L5 / 2026-11-04`——CAD 锁文件泄漏用户名与主机名）均未忽略。
- **方案**：
  ```gitignore
  /output/          # 可再生的任务成品，不入库
  *.dwl             # CAD 打开图纸的锁文件（含用户名/主机名，瞬态）
  *.dwl2
  ```
  顺带处理 4 张 `docs/section-*.png`（约 205 KB，全仓 md 无引用）：要么在 M3-E5/E6 文档里加引用作为效果展示，要么删除。
- **工作量**：0.25 人日。

### 3.5 prepare.py 静默吞错（信息丢失，非崩溃风险）

- **证据**：`except Exception: pass/return None` 至少 5 处——`db_station_info`（prepare.py:888-891）、`db_water_level`（prepare.py:1002-1004）、close 两处（311-315、1894-1897）；`audit.py` 的 `--json/--md` 输出非原子写（audit.py:1643-1649，对比 prepare.py 已有 `write_json_atomic`）。
- **影响**：DB 表损坏与"确实无记录"对下游完全不可区分，排障时只能靠猜。
- **方案**：吞错处至少 `ctx.warn`/stderr 一行（保持 stdout 纯 JSON 协议不变）；`except BaseException`（prepare.py:171）收窄为 `Exception`；audit 输出复用 `write_json_atomic` 同款原子写。
- **工作量**：0.5 人日。

---

## 四、P1：稳定性与性能（建议 1–2 周内排期）

### 4.1 LISP 注入超时后的"孤儿脚本"治理（有真实事故史）

- **证据**：`LispFile/LispGeom.Scan` 超时抛 `LISP_FILE_TIMEOUT` 后，finally 立即删三个临时文件（LispFile.cs:228-234）。但 CAD 内的 LISP 仍在执行：它随后 `open` 同名路径会**重建 txt/done 文件**（%TEMP% 垃圾越积越多），且若脚本中途出错，`(setvar "CMDECHO" _zwc_echo)` 恢复行不会执行——M3-E6 §5.2 记录了用户图纸 CMDECHO 被留 0 的真实事故（人工 `(setvar "CMDECHO 1)` 才恢复）。
- **方案**（按代价从低到高，可只做前两条）：
  1. 模板注册 AutoLISP 错误处理器：脚本开头 `(defun *error* (msg) (setvar "CMDECHO" _zwc_echo) (princ))`，LISP 出错也恢复 CMDECHO（需在白名单增加 `defun`、`*error*`，仍保持只读语义）。
  2. 超时路径不删 txt/lsp（只删 helper 侧可确定性拥有的部分），改为重命名留档（如 `<guid>.orphan`），并在报错 hint 里告知"检查 %TEMP%\zwcad-cooperate"；下次 scan 启动时清理超过 24 h 的孤儿文件。
  3. 哨兵分两阶段：脚本第一行先写 `__START__`，超时时若只见 `__START__` 无 `__DONE__`，错误信息明确区分"注入未执行"（管道/命令态问题）与"执行中卡死"（图太大/超时太短）。
- **验证**：`-FaultInject` 体系加一条"故意生成除零的 LISP"（仅测试模式绕过白名单）验证 CMDECHO 恢复；离线单测 `LispTemplate.BalanceError` 覆盖 *error* 定义后的括号配平。
- **工作量**：1–1.5 人日。**风险**：中——改的是注入模板，必须过 lispgen + 小筛选真机回归。

### 4.2 合并 LispFile/LispGeom 重复的管道逻辑

- **证据**：两文件 `Scan` 的预检守卫（CMDACTIVE/LASTPROMPT 检查，LispFile.cs:104-117 vs LispGeom.cs:41-54）、ssget 过滤器拼装（LispFile.cs:121-130 vs LispGeom.cs:56-66）、哨兵轮询循环、GBK 解码、finally 清理**逐字级重复**，仅解析段不同；两文件合计约 520 行，重复约 200+ 行。
- **方案**：抽 `LispScan` 基础类/静态类承载"守卫→生成→注入→等哨兵→GBK 读回"五步，解析用回调或返回原始行由调用方消费（`LispGeom` 天然按行流式解析也更省内存——36 k 实体 × 多顶点时当前实现先 `File.ReadAllBytes` 再 `Split`，全量字符串驻留）。
- **收益**：E5/E6 行为差异只剩解析差异；4.1 的修复只改一处。
- **工作量**：1 人日。**前置**：先完成 3.1/3.2，避免重构搬移 bug。

### 4.3 bin/cache 编译缓存淘汰

- **证据**：138 个 sha12 目录、105 个 ZwcHelper.exe（avg 127 KB，共 13.0 MB）、**33 个空目录**（编译失败残留：`EnsureHelper` 先 `Directory.CreateDirectory` 后编译，失败不回滚，ZwcClient.cs:368-394）；zwc.ps1 全文仅 1 处 Remove-Item；无任何 LRU/上限。源码每改一次永久 +127 KB。
- **方案**：
  1. `EnsureHelper`/zwc.ps1 编译失败时删除刚建的空 cacheDir；
  2. daemon 启动与 `serve` 命令处顺带做轻量维护：按目录 mtime 保留最近 N（如 10）份，删除更旧目录与空目录（守护进程持有当前 exe 的目录天然不会被删——运行中的 exe 文件被锁，删除失败静默跳过即可）；
  3. `serve -Action` 增加子命令或 `zwc.ps1 cache -Clean` 供手动清理。
- **工作量**：0.5 人日。**风险**：低（删除失败一律吞掉，不影响主流程）。

### 4.4 Python 侧性能热点

| 热点 | 证据 | 方案 | 收益 |
|---|---|---|---|
| `drop_suspect` 逐点 `next()` 线性查找 + `any()` 全离群扫描 | prepare.py:1137-1141 | 先建 `{id: series}` 字典与离群集合，整体降为 O(points) | 大批量断面制备从 O(p×o×s) 降为线性 |
| 断面×等高线/高程点双重循环无空间索引 | build_sections_from_dmx.py:139-157；build_sections_gcd.py:92-101（2,938×304≈89 万次距离计算） | 网格分桶（断面线 bbox 分桶后只与桶内对象求交）；不引 shapely 也可先按 X 坐标排序 + 双指针 | 现有数据 10 s 可接受，但同图断面数×3 后保护性收益明显 |
| yssd 全表单元格归一化重复 ≥3 次 | prepare.py:454-473, 512-513, 532 | 一次性生成归一化矩阵缓存，各阶段复用 | 大 `.xls` 解析减少 2/3 正则开销 |
| audit 文字重叠 O(n²)（已有 40 万对熔断） | audit.py:866-905 | 保持熔断，增加网格空间划分降候选对 | 万级文字图体检不再触熔断 |
| gcd 宽表逐 cell 写 | build_sections_gcd.py:187-199 | 改 openpyxl `append` 批量（from_dmx 已是此写法） | 对齐已有快路径 |

- **工作量**：合计 1.5–2 人日。**注意**：这些是防御性优化（当前 10 s 重跑可接受），优先级低于 4.1–4.3；若做，先加计时断言再改。

### 4.5 query 的 `-Layer` 下推（可选，收益有限）

- **证据**：query 的 lisp 快路径刻意把 layer/text 留在客户端过滤以保持语义（EditsQuery.cs:87-98 注释）；`read` 的 lisp 路径同样不传 layer（ZwcHelper.cs:2517-2518）。
- **现状评估**：36 k 实体全量 dump 仅 0.4 s，客户端过滤开销可忽略——**不建议为纯性能做**。仅当未来支持"只 dump 单层几何"（省网络/内存）时，对**精确层名**（无通配、大小写不敏感匹配一次）下推 `(cons 8 layer)`，并在响应里保留 `layerPushed:true` 便于回归比对。
- **工作量**：如做，0.5 人日。

---

## 五、P2：架构与可维护性（建议在 P0/P1 后的稳定窗口做，2–3 周节奏）

### 5.1 拆分 ZwcHelper.cs 巨石文件

- **证据**：单文件 4,354 行含 10+ 个类（JMap/Json/Com/Coll/State/Cad/Cmd/Procs/Commands/PipeIo/Daemon/Program，ZwcHelper.cs:52-4215）。已拆出的 Edits/EditsQuery/SectionOverlay/Stability/LispFile/LispGeom/LispTemplate 证明拆分模式可行。
- **连带成本**：任何一行改动 → 源码哈希变 → daemon 重启 + 新 127 KB 缓存副本。文件越巨，这条链触发越频繁（也放大 4.3 的缓存问题）。
- **方案**：按既有先例继续抽 `Json.cs`（自研 writer/parser 独立可测）、`Cad.cs`（ProgID 解析/进程识别，纯逻辑离线可测）、`State.cs`、`Daemon.cs+PipeIo.cs`、`Commands.Read` 独立成 `Read.cs`（Read+ReadState 约 400 行）。**每次只搬一个类，搬完立即跑离线自测 + `run_m1.ps1 -DryRunOnly`**。
- **收益**：编译单元变小（增量感知友好）、纯逻辑部分（Json/BalanceError/哈希配方）可进离线测试与未来 CI。
- **工作量**：2–3 人日（含回归）。**风险**：中——纯搬移不改逻辑，靠哈希缓存机制天然隔离新旧版本（旧 daemon 继续跑旧缓存）。

### 5.2 哈希配方收敛为单一事实源

- **证据**：同一"文件数|最新mtime|排序sizes→SHA256前12位"配方在 ZwcClient.cs:53-80（C#）、zwc.ps1 委托分支（约 55-70 行）、zwc.ps1 编译分支三处独立实现，靠注释约定同步。
- **方案**：zwc.ps1 内两处至少合并为一个函数；C# 侧与 .ps1 侧的同步改为**双向自检**——`doctor` 输出两侧算出的哈希，不一致时报 `HASH_RECIPE_MISMATCH`（比"约定"可靠，防止未来改动只改一边导致 client 连不上 daemon 且编译重复）。
- **工作量**：0.5 人日。

### 5.3 合并 build_sections 双脚本为"数据源策略"单实现

- **证据**：difflib 实测逐行重复 46.9%、归一化后 53.1%；`seg_dist_t`/`chainage_to_m`/`head` 逐字相同，`associate` 仅键名不同，main 骨架同构；常量（LABEL_R=8.0/DEDUP_D=0.05/MIN_PTS=3）两份。
- **方案**：抽 `scripts/section/section_lib.py`（几何工具 + 标签关联 + Excel 写出 + 预览），两个入口变薄壳（差异仅：高程来源——等高线求交 vs GCD 块 Z、宽表列、输出命名）。同时补上当前缺失的输入校验（坏 JSON/缺键目前直接裸 traceback，build_sections_from_dmx.py:76-78）与 `wc[0]` 空匹配 IndexError（:118-120）。
- **收益**：删约 250 行重复；4.4 的空间索引优化只需做一次。
- **工作量**：1–1.5 人日。

### 5.4 清理编辑残留与死代码

| 项 | 证据 | 动作 |
|---|---|---|
| `bindFailures = 0;` 连续两行 | ZwcHelper.cs:4162-4163 | 删一行 |
| `r.Add("instanceError", ...)` 连续两行（JMap.Add 是覆盖语义，不崩溃但冗余） | ZwcHelper.cs:2070-2071 | 删一行 |
| `qa_yssd.py` 的 `--block` 参数传入 `build_report(block_sel=...)` 但函数体从未使用（用户以为可选块，实际无效） | qa_yssd.py:32,358,373 | 实现或删除参数 |
| 硬编码输出名/预选桩号 `16+475`、TEMP 回退硬编码 `C:\Users\Administrator\...` | build_sections_from_dmx.py:191-192, 69；build_sections_gcd.py:142-143, 46 | 参数化；TEMP 回退改 `tempfile.gettempdir()` |
| ZwcClient 退出码靠在响应文本里搜 `"VERIFY_FAILED"` 等子串 | ZwcClient.cs:215-221 | 若 helper 在 message/detail 中合法提及这些词会误判为门禁码；建议解析顶层 `ok` 与 `error.code` 字段（哪怕仍用手写 JSON 提取，取 `"code":"..."` 第一个出现即可） |
| daemon-stop/ping 靠 `reqJson.IndexOf("\"ping\"")` 匹配整个报文 | ZwcHelper.cs:4182,4191 | 请求体恰好含 `"ping"` 字符串时会误入 ping 分支；改为先 Json.Parse 再判 cmd（该路径不在热路径上） |

- **工作量**：合计 0.5–1 人日，可随其他改动顺手做。

### 5.5 prepare.py 模块化（最大的一项，可与 5.3 合并排期）

- **证据**：2,048 行六类职责；`build_model` 与 `build_db_model` 坐标映射/elev_base/TOO_TALL/params 逐字重复（prepare.py:1464-1479 vs 1075-1085、1765-1771 vs 1184-1188、1711-1713 vs 1202-1203）；`_series_stats` 与 `_series_summary` 重叠（1015-1035 vs 1875-1882）；`DEFAULT_DB_PATH` 在导入期求值（:855）。
- **方案**：按 `io_excel.py / yssd.py / db.py / model.py / cli.py` 拆包；v1/v2 两套建模合并为"序列来源不同、坐标映射单一实现"；DB 路径延迟到 main 里读环境变量。**保持 stdout JSON 协议与退出码语义逐字节不变**（tests/run_m1.ps1 全链是现成回归）。
- **工作量**：2–3 人日。**风险**：中——纯行为保持重构，靠 41 项 selftest + check_dxf 三方互证兜底。

---

## 六、P3：工程化补课（持续做，每项独立可交付）

### 6.1 依赖声明（requirements.txt）

- **证据**：全仓无 requirements.txt/pyproject；依赖散在 README（README.md:112,326）；vendor 的 `tools/py-libs` 不含 openpyxl，而 3 个脚本硬 import openpyxl、build 脚本可选 matplotlib——离线兜底不完整，缺库时 build 脚本顶层 import 直接裸 traceback（对比 prepare.py:243-248 的延迟 import + SectionError 转译是正确做法）。
- **方案**：最小 requirements.txt（`xlrd>=2.0`、`openpyxl`、`ezdxf`，可选段 matplotlib）；build 脚本统一"延迟导入 + 明确错误码"；文档写明 py-libs 覆盖范围或在 vendor 中补齐 openpyxl。
- **工作量**：0.5 人日。

### 6.2 最小 CI（只跑离线层）

- **证据**：无 .github/workflows；而 selftest.py(41)/test_audit.py(98)/selftest_checkers.py/check_dxf.py 全部零 CAD 依赖、windows runner 可跑。
- **方案**：一个 workflow：windows-latest + Python 3.9，pip install xlrd openpyxl ezdxf，依次跑四个离线入口 + `run_m1.ps1 -DryRunOnly`；main 分支 push/PR 触发。**不追求在线 CAD 测试**（领域固有约束）。
- **收益**：P2 各重构项的安全网；5.x 的任何拆分失误在 PR 即暴露。
- **工作量**：0.5 人日。

### 6.3 文档一致性

| 项 | 证据 | 动作 |
|---|---|---|
| 4 张悬空 png | docs/section-*.png 全仓无引用 | 在 M3-E5/E6 文档引用，或删除 |
| tests/audit/README 声称的 `real\*.json`、`out\*.report` 目录不存在 | tests/audit/README.md | 修文档或补目录约定 |
| tests/README §9 引用的 `tmp\real\README.md`、`SCHEMA-v2.md` 被 /tmp/ 忽略，fresh clone 即失效 | tests/README.md §9 | 契约类文档移入 docs/（保持 tmp 只放瞬态数据） |
| audit.py 新增规则要改 4 处（_rule/规则函数/RULES/RULE_LABELS） | audit.py:1255-1301 | 装饰器注册收敛为 1 处（顺带补类型注解） |

### 6.4 可观测性小增强（建议，非必需）

- daemon 无请求日志（Daemon.Run 仅异常回 DAEMON_ERROR，ZwcHelper.cs:4147-4213），排障只有 stdout；建议 daemon 把最近 N（如 100）条请求摘要+耗时环形写入 `%TEMP%\zwcad-cooperate\daemon.log`，`doctor` 附带回报最后几条。
- `section` 命令返回缺 timings 细分（`entity` 已有 timings，SKILL.md:78）；任务级 8–28 s 的大头（数据校验/绘制/标注/回读）目前不可见，加细分有助于下一次性能攻坚定位。

---

## 七、实施路线图建议

```
第 1 批（本周，≈2 人日）—— P0 正确性 + 流程安全
  3.1 LispGeom 转义解析修复 + 回归用例
  3.2 R| 半径记录解析
  3.3 修复后一并提交 M3-E6 全部工作树
  3.4 .gitignore 补 /output/、*.dwl、处置悬空 png
  （3.5 prepare 吞错收敛可顺延到第 2 批）

第 2 批（1–2 周，≈4 人日）—— P1 稳定性
  4.1 LISP 孤儿脚本治理（*error* 恢复 + 超时留档 + __START__ 两阶段哨兵）
  4.2 合并 LispFile/LispGeom 管道逻辑
  4.3 bin/cache 淘汰 + 空目录回滚
  6.2 顺手把 CI 搭起来（后续批次的安全网）

第 3 批（P1/P2 之间，≈2 人日）—— Python 数据侧
  4.4 性能热点（drop_suspect 优先）+ 3.5 吞错收敛
  5.3 build_sections 合并 + 输入校验
  6.1 requirements.txt

第 4 批（稳定窗口，≈5–7 人日，可拆散）—— P2 架构
  5.1 ZwcHelper.cs 逐类拆分（每搬一类跑一次离线回归）
  5.2 哈希配方自检
  5.5 prepare.py 模块化
  5.4 残留清理（随时穿插）

依赖关系：3.1/3.2 → 3.3（提交顺序）→ 4.2（重构在修复之上）
         6.2 CI 尽早做，是第 4 批全部重构的前置安全网
不建议并行：4.1 与 4.2 同改 Lisp*，串行做
```

---

## 八、附录：分析方法与未覆盖项

- **逐文件人工精读**：ZwcClient.cs（全文）、LispFile.cs / LispGeom.cs / LispTemplate.cs（全文）、ZwcHelper.cs（结构 outline + Daemon/Program/Read/Doctor 关键段）、zwc.ps1（前 120 行）、M3-E6 文档、SKILL.md。
- **子任务委派分析**：Python 五文件（prepare/qa_yssd/build_sections×2/audit）代码质量报告；tests/bench/publish/bin-cache/.gitignore/git 体积报告。
- **关键结论均经二次验证**：如 LispGeom 的 `Split('|')` 与 `R|` 记录缺失、ZwcHelper.cs 重复行（含 JMap.Add 覆盖语义确认，故不构成运行时 bug）。
- **未深入**（本次范围外，如需可另开分析）：Edits.cs / SectionOverlay.cs / Stability.cs 的逐行审查；tray 子系统；`compare-three-cads.ps1`；`run_bench.ps1` 内部（45 KB）的拆分价值评估；真实 CAD 上的性能复测。

> 本文档仅为分析结论与建议，**未对任何源码、配置或文档执行修改**。
