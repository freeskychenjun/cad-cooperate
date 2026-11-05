# 优化执行记录（2026-11-05）

> 输入：`docs/optimization-plan.md`（2026-11-05 的只读分析报告，15 个问题 + 4 个批次）。
> 本文记录**实际采纳了什么、没采纳什么、为什么**，以及每项的验证证据。
> 基线：`v0.9.0`（`88b1a06`）+ 工作树未提交的 M3-E6 改动。M3-E6 工作树已先单独落库为
> `e70209f`（避免优化改动把未跟踪文件与已跟踪改动混在一起），本轮优化在其上分提交。
> **第二轮（同日，按本文 §七 的建议开工）已完成 5.3 / 4.4 / 4.2 / 5.1（第一批）/ 5.5，见 §八。**

## 一、结论摘要

| 计划项 | 决策 | 落地内容 | 关键证据 |
|---|---|---|---|
| 3.1 `\|` 转义打歪几何字段（P0） | **采纳** | 新增 `scripts/helper/LispWire.cs`：`SplitRecord/UnescapeField/TryNum/ParseGeometry/ParseFlat` 成为**唯一解析实现**；`LispGeom`/`LispFile` 只留「管道 + `LispRecord`→`JMap` 映射」 | 离线 `tests/lispwire` **99 条**断言（`E\|1A\|TEXT\|0\|A\\\|B\|1.5…` ⇒ 9 段 / Text=`A|B` / X=1.5）；真机 A/B 500/500 实体整表逐字段相等 |
| 3.2 `R\|` 半径无消费（P0） | **采纳** | `LispWire` 解析 `R` 记录 → `radius`（与 COM 路径对齐） | 离线断言 `R\|h\|2.5` ⇒ `radius=2.5`；真机样本无 CIRCLE/ARC，未观测（已记入已知限制） |
| 3.3 提交 M3-E6 工作树（P0） | **采纳（抢先）** | 原样检查点提交 `e70209f`；本轮优化另分 4 个提交 | `git log`：`e70209f` + 后续 4 个优化提交 |
| 3.4 `.gitignore` 缺 `/output/`、`*.dwl`（P0） | **采纳** | 补 `/output/`、`*.dwl`、`*.dwl2`；4 张悬空 png 改为在 `docs/M3-E6-geometry-dump.md §4.1` 引用（高程来源对照图） | `git status --ignored`：`output/`、`tests/bench/fixtures/design-water.dwl` 均被忽略；`git ls-files` 无命中 |
| 3.5 prepare 吞错 / audit 非原子写（P0） | **采纳** | `prepare.py` 新增 `warn()/warn_exc()` 走 **stderr**（6 处静默吞错留痕、同码去重），`write_json_atomic` 去掉 `except BaseException`；`audit.py` `--json/--md` 改同款原子写（不可写 → stderr + exit 2） | `stdout` 单段 JSON 协议不变（41 PASS 回归）；缺 `stations`/`daily_water_level` 表用例可区分「表坏」与「无记录」；`KeyboardInterrupt/SystemExit` 穿透且无残留临时文件 |
| 4.1 LISP 孤儿脚本 / CMDECHO 事故（P1） | **采纳** | `*error*` 处理器（还原 CMDECHO + 旧处理器）；脚本先写 `__START__` 两阶段哨兵；失败/超时**改名 `*.orphan` 留证**不删、扫描前清 >24 h 遗留；新增错误码 `LISP_NOT_STARTED`、`LISP_FILE_TIMEOUT.detail.phase` | 真机 CMDECHO 前后均 **1**；`FileSystemWatcher` 抓到 `.start`(01:12:05.953) → `.done`(01:12:07.403)；成功路径 pipeline 残留 **0**；25 h orphan 被清、fresh orphan 保留；超时分支 21 条离线断言（端到端不做，理由见 §六） |
| 4.2 合并 LispFile/LispGeom 重复（P1） | **已做（第二轮）** | 解析层 `LispWire`（第一轮）+ **注入管道 `LispScan.cs`**（第二轮，五步单一实现）；`LispFile` 240→133、`LispGeom` 269→161 行 | 见 §八.2；真机 A/B `-Limit 20/500`、read、query 与基线 0 字段差异 |
| 4.3 bin/cache 无淘汰（P1） | **采纳** | `PruneCache`（保留 10 个非空 + 当前 sha + **运行中目录**，空目录 5 min 宽限，`deleted[]/skippedRunning[]/failedDirs[]`）；`serve -Action prune`；client 拉起新 daemon 后一次性自动淘汰；编译失败回滚空目录与 `build-<pid>.exe` | **141 → 9** 目录、空目录 **33 → 0**、释放 **12.00 MB**、`failed=0`；受控实验证明运行中目录被保护；二次 `removed=0`（幂等）；现 10 目录 / 0 空 / 1.76 MB |
| 4.4 Python 性能热点（P1） | **已做（两轮 + 尾巴评估）** | 第一轮 `drop_suspect` 字典/集合索引（**1.6×/4.6×/13.6×**，逐字节等价）；第二轮**断面关联改网格分桶**：候选对 918 万→16.8 万、关联段 1.43→0.14 s（**×10.2**）、端到端 gcd −16% / dmx −26%，golden 逐单元格 0 差异。**尾巴「gcd 宽表逐 cell 写 → append」经实测评估为不值得**（宽表段 9.2→7.5 ms、占端到端 0.5%、端到端配对中位 −4.4 ms 属噪声）⇒ 保留原写法，候选 patch 留档 | 见 §八.1 / §8.8；`tests\selftest_sections.py` **85** 断言 |
| 4.5 query `-Layer` 下推（P1） | **不采纳** | 计划自身结论即"不建议为纯性能做"；本轮无"只 dump 单层"需求 | — |
| 5.1 拆 `CadHelper.cs` 巨石（P2） | **已做第一批（第二轮）** | `Json`（262 行）+ `PipeIo`（51 行）整体搬出，`CadHelper.cs` 4,371→4,100 行（diff = 0 insertions / 271 deletions，类体逐字节相等）；新增 `JsonTests` **132 断言**纳入 CI | 见 §八.3 |
| 5.2 哈希配方三（四）处（P2） | **采纳** | `cad.ps1` 三处合并为 `Get-HelperSha12`；client 新增 `hash` 子命令；`serve -Action status` 输出 `helperSha/clientSha/recipeMatch`，不一致 → `HASH_RECIPE_MISMATCH` | `recipeMatch:true`；新实现 40 次中位 **0.69 ms vs 2.15 ms**、sha 完全一致 |
| 5.3 合并 build_sections 双脚本（P2） | **已做（第二轮）** | `section_lib.py`（1,138 行）单一实现 + 两个薄入口（376→42、414→47 行）；重复度 168 行→9 行；golden 逐单元格 0 差异（34,024 + 69,987 cell） | 见 §八.1 |
| 5.4 编辑残留与死代码（P2） | **采纳（含扩展）** | `bindFailures` 重复行、`instanceError` 重复行、`ShowCmd` 重复 `return`；CadClient 退出码改结构化（不再全文搜子串）；daemon 的 ping/daemon-stop 改 `Json.Parse` 判顶层 `cmd`；`qa_yssd --block` 由死参数改为**真正生效**（默认行为不变）；硬编码 TEMP 回退改 `tempfile.gettempdir()` | 退出码 19 个假响应用例（含「`ok:true` + 文本含 BAD_DATA → **0**」，旧实现错判 3）；真机 `query -Text ping` 修复前返回 `cmd:"ping"`、修复后 `cmd:"query"` |
| 5.5 prepare.py 模块化（P2） | **已做（第二轮 + 尾巴）** | 2,090 行 → **368 行薄壳** + `prep_io`/`prep_yssd`/`prep_db`/`prep_model` 四模块；v1/v2 坐标映射/TOO_TALL/layer-prefix/字高字面量共 **54 行重复 → 24 行调用点 + 48 行单实现**；golden **21/21 全等**（stdout 字节/退出码/stderr/落盘 sha256）。**尾巴 `--compare-source`**：13 形态离线 golden（合成 DB+源表）+ 新测试 71 断言，并修掉两处真问题（见 §8.8） | 见 §八.4 / §8.8 |
| 6.1 依赖声明（P3） | **采纳** | 新增 `requirements.txt`（`xlrd>=2.0`/`openpyxl>=3.0`/`ezdxf>=1.0`，可选 matplotlib）；build 脚本延迟导入 + 明确错误码 | CI 用它装依赖；缺库时报 `NO_OPENPYXL` + exit 2（原裸 traceback） |
| 6.2 最小 CI（P3） | **采纳** | `.github/workflows/offline-ci.yml`：windows-latest + Python 3.9，跑 5 个离线入口（含 csc 编译的 `selftest_lisp.ps1`），**不跑任何在线 CAD 测试** | 五个入口本机全绿（见 §五）；CI 本身尚未在 GitHub 实跑（需首次 push，见 §七） |
| 6.3 文档一致性（P3） | **采纳** | `tmp/real/SCHEMA-v2.md` 脱敏后入库为 `docs/section-model-v2.md`（站码/站名/本机库路径/截图名替换）；`tests/README` 悬空指针改指向该文件并说明 `tmp/` 不入库；4 张 png 变为被引用；`tests/audit/README` 的"快照未随仓库分发"一节本已自洽，未动 | fresh clone 不再有悬空引用；公开仓库不新增真实站点/本机路径 |
| 6.4 可观测性（P3） | **未做** | daemon 环形请求日志、`section` timings 细分：属"建议、非必需"，本轮让位于 P0/P1 | — |

## 二、P0 正确性：两个"不报错只出错数据"的缺陷

1. **`\|` 转义**：`LispTemplate` 把文字里的 `|` 写成 `\|`；`LispFile.SplitRecord` 认这个转义，但 `LispGeom.Scan` 用裸 `line.Split('|')` ⇒ 行内字段整体右移，`x/y/z/nverts` 落到文本片段上被解析成 `0.0`（坐标静默变 0、顶点错挂）。修复后**解析只有一份实现**，`LispGeom` 里不再有 `Split('|')`。
2. **`R|` 半径**：模板一直在为 CIRCLE/ARC 写 `R|handle|radius`，但没有任何消费者 ⇒ 同一张图走 LISP 与走 COM 字段不一致。现 `radius` 与 COM 路径对齐。

**真机 A/B（只读，显式 `-Doc`，改动前基线 `tmp\baseline2\*`）**：
`-Limit 20`（20/20）、`-Limit 500`（500/500，整表 `entities` 完全相同）、默认 `read -Limit 20`、`query -Limit 10`、`doctor`、`status` 全部**字段差异 0**；两侧 `lispFile.fileBytes` 均 **4,801,512**；`counts.total=41413` 一致；`read -Mode lispfile` 两侧同为 `BAD_ARGS`（该 mode 不存在，未造通道）。

## 三、稳定性与运行时工程化

- **4.1**：见摘要表。关键设计：脚本先 `(setq _cad_olderr *error*)` 再 `(defun *error* …)`，处理器关闭打开的文件、还原 CMDECHO、还原旧处理器；正常收尾同样还原；`SafetyError` 白名单为此新增 `defun` 但**跳过形参表**，`forbidden` 表与只读语义不变（`defun` 体内 `command` 仍被拒，有负例）。
- **4.3**：见摘要表；`PruneCache` 只动 `bin\cache\<12位小写hex>`，散落文件/非 hex 目录/`bin\CadClient.exe`/`bin\tray` 一律不碰。
- **5.2 / 5.4**：见摘要表。daemon 的 `{"cmd":"daemon-stop"}` 与 `{"cmd":"ping"}` 响应报文**逐字节未改**；解析失败按原样交给正常分派（不因解析失败拒合法请求）。

## 四、Python 数据侧

- `prepare.py`：stderr 告警（`DB_STATION_INFO_FAILED`/`DB_WATER_LEVEL_FAILED`/`DB_CLOSE_FAILED`/`WORKBOOK_CLOSE_FAILED`/`TMP_CLEANUP_FAILED`/`CONFLICT_FLAG_INVALID`，同码去重）、原子写去 `BaseException`、`drop_suspect` 线性化。
- `audit.py`：`--json/--md` 原子写，不可写 → stderr + exit 2。
- `qa_yssd.py`：`--block N` **真正生效**（默认第 1 块 = 原行为；跨块对比仍覆盖全部块），新增 `--title`，去掉 md 里的硬编码标题。
- `build_sections_from_dmx.py` / `_gcd.py`：`tempfile.gettempdir()` 回退、输出名/桩号参数化（默认值不变）、openpyxl 延迟导入、坏输入 `NO_INPUT/BAD_INPUT/NO_OPENPYXL` + exit 2。
- 行为变化（**均为"更明确"，无静默语义改变**）：多出 stderr 行；`audit` 输出不可写由裸 traceback 变 exit 2；`qa_yssd --block` 从无效变有效。

## 五、本轮提交前的验证矩阵（本机实测）

| 项 | 命令/方式 | 结果 |
|---|---|---|
| helper 编译 | `csc` 9 个 `.cs` | **0 error**（仅 3 条历史 warning） |
| LISP 解析/模板离线 | `tests\selftest_lisp.ps1` | **LispWireTests 99 PASS / LispTemplateTests 53 PASS / 0 FAIL** |
| 数据侧自测 | `python tests\selftest.py` | **41 PASS / 0 FAIL**（与改前一致） |
| 体检规则 | `python tests\audit\test_audit.py` | **PASS 89 / FAIL 0 / SKIP 3**（与改前一致） |
| 基准检查器负例 | `python tests\bench\selftest_checkers.py` | **SELFTEST PASS** |
| 端到端 dry-run | `tests\run_m1.ps1 -DryRunOnly -NoCad` | **PASS 10 / FAIL 0 / SKIP 2**（改前 9/0/2，新增 `lisp.wire` 步） |
| 退出码抽取 | `bin\CadClient.exe --selftest-exitcode` | 19 个假响应全对（含旧实现错判用例） |
| cache 淘汰 | `cad.ps1 serve -Action prune` | 141→9 目录、33→0 空目录、释放 12.00 MB、幂等 `removed=0` |
| 哈希自检 | `cad.ps1 serve -Action status` | `recipeMatch:true`（`helperSha==clientSha`） |
| 真机回归（只读） | `tmp\evidence-runtime\real-regression.ps1` | **ALL PASS**：pre/post 与 `baseline2` 两组字段级 **ALL MATCH**；`serve stop` 后自动重启；CAD 保持 **1 实例 / 41413 实体** |
| 收尾现场 | — | `bin\cache` 10 目录 / 0 空 / 1.76 MB；`%TEMP%\cad-cooperate` 无本轮 `geom-*/scan-*` 残留 |

## 六、未采纳 / 降级项与理由

> **第二轮更新**：下列第 1–3 项（5.1、5.5、5.3、4.2）已在同日第二轮补齐（见 §八）；本节保留为**当时的取舍理由**，用于解释"为什么当时先不做、后来怎么做的"。

1. **5.1 拆 `CadHelper.cs`、5.5 `prepare.py` 模块化**：纯行为保持的大规模搬移（各 2–3 人日），必须逐类/逐函数跑在线+离线回归；本轮窗口内做不完，硬做只会把"未验证的大 diff"塞进历史。建议单独开一个批次，前提是先用 6.2 的 CI 把离线网兜住。
2. **5.3 合并 build_sections 双脚本**：计划估计 1–1.5 人日且需重跑真实数据；本轮只做了**风险侧**（输入校验、延迟导入、参数化），去重留给下一次（4.4 的空间索引优化也应等这次合并后再做，避免改两遍）。
3. **4.2 的注入管道合并**：解析层已合并（收益最大且可离线测）；注入管道（守卫→注入→哨兵→清理，约 120 行同构）与 4.1 改的是同一片代码，本轮先保证 4.1 的正确性并做真机验证，管道抽象留待下一轮（届时 4.1 的修复只需改一处）。
4. **4.4 其余热点**（断面×等高线空间索引、yssd 归一化缓存、audit 文字重叠网格、gcd 宽表 append）：当前数据量 10 s 可接受，属防御性优化；且 5.3 合并后代码会搬动，先优化会重复劳动。
5. **4.1 的超时端到端真机验证**：`Read` 在 LISP 失败后会 `LISP_FILE_FALLBACK` 回退 COM 全扫（41k 实体 ≈10 min），会在用户会话里长时间霸占 CAD。改为**纯函数离线单测**（`LispSentinelPhase.Classify/ErrorCodeFor`、`LispOrphans.EvidenceAction/Preserve/IsStale/CleanStale` 共 21 条断言），并在文档里写明取舍。
6. **6.4 可观测性**：非必需项，让位于 P0/P1。

## 七、遗留与下一步建议

1. **真机 `radius` 未观测**：本轮样本图前 500 实体不含 CIRCLE/ARC。建议下次在一张有圆/弧的图上跑 `read -Mode geom -Limit 50` 确认（离线断言与模板已覆盖生成/消费两端）。
2. **全量 geom 响应体上限（既有边界，未改协议）**：41k 实体全量 `read -Mode geom` 时，LISP 扫描只需 ≈1.5 s 且 dump 与基线逐字节相同（4.8 MB），但"41k 实体 → JSON 响应"产生不出应答（client 180 s 超时、stdout 0 字节；直连 one-shot helper 也不写 response）。已写入 `SKILL.md` 能力边界与 `docs/M3-E6-geometry-dump.md §6.3`；**可用做法**是 `-Limit` 分页或直接消费 `%TEMP%` 的 dump 文件。若要做"一次拿全量"，需要改协议（分块/流式/落盘引用），属独立课题。
3. ~~**CI 未在 GitHub 实跑**~~ → **已完成（2026-11-05 08:48Z）**：推 `main` 后 `offline-ci` 首跑即暴露两个真问题，修完第二跑 11 步全绿（run [41943999965](https://github.com/freeskychenjun/cad-cooperate/actions/runs/41943999965)，1m14s）：
   - `run_m1.ps1` 对参数做 `Test-Path` 预检 ⇒ workflow 里裸 `python` 必然 `missing: python`，改为 `-Python (Get-Command python).Source`；
   - windows runner 控制台是 **cp1252**，`tests/selftest.py` 打印中文明细抛 `UnicodeEncodeError` ⇒ job 级 `PYTHONUTF8=1`/`PYTHONIOENCODING=utf-8` + 三个测试入口加 `reconfigure(encoding="utf-8", errors="replace")` 自保；
   - 顺手把 `setup-python` 从 3.9 改 3.11（3.9 已 EOL、镜像可能缺失），"支持 3.9"改由新增的 `tests/check_py39_syntax.py`（`ast.parse(feature_version=(3,9))` 校验 34 个 .py）把关。
   证据：`gh run view 41943999965`；本机也按 CI 同编码（`PYTHONIOENCODING=cp1252`、清空 `PYTHONPATH`）预演过全部步骤。
4. **下一轮可做**（按建议顺序；**前四项已于第二轮完成，见 §八**）：~~5.3 合并 build_sections~~ → ~~4.4 空间索引~~ → ~~4.2 注入管道抽象~~ → ~~5.1 第一批（Json/PipeIo）~~ → 剩余：`DEFAULT_DB_PATH` 延迟求值（按「先别改语义」保留导入期求值）；`docs/section-model-v2.md` 里 helper 侧对 `SOURCE_COMPARE_UNMATCHED` 的透传建议（未验证，非阻塞）。**已完成**：5.1 第二/三批（CadHelper 拆 24 个 .cs）、4.4 宽表 append（评估后决定不改）、5.5 `--compare-source` golden。
5. **本机现场**：`bin\cache` 已从 140 目录/13.39 MB 收敛到 10 目录/1.76 MB；`%TEMP%\cad-cooperate` 里的 `g3.*`、`gtest*`、`contours.json`、`sections-src.json` 等是 **2026-11-04 M3-E6 试验遗留**，非本轮产物，可随时手工删除（不在仓库内）。

## 八、第二轮执行（2026-11-05，按 §七 建议开工）

> 四个写作用域互不重叠的重构并行完成；每项都以"离线全绿 + 等价性证明"收口，提交见本文件末尾。

### 8.1 5.3 + 4.4 数据侧：`section_lib.py` 单一实现 + 网格分桶空间索引
- **合并**：`build_sections_gcd.py` 376 → **42 行**、`build_sections_from_dmx.py` 414 → **47 行**；新增 `scripts/section/section_lib.py` **1,138 行**（常量、几何工具、输入校验/错误码、网格索引、标签 1:1 关联、方向判定、去重/`MIN_PTS`、Excel 五表、预览 PNG、CLI/stdout 契约、可选计时）。两条路线的差异收敛为 `VARIANTS` 声明式数据。重复代码实测：旧两脚本 168 行逐行重复（50–54%），新两入口仅 **9 行**共享。
- **等价性**：真实数据 golden —— gcd 4 sheet / **34,024 cell**、dmx 5 sheet / **69,987 cell**，**差异 0**（各掩码 1 个「生成时间」）；另 stdout 逐行相同、stderr 空、`--help` 逐字符相同、PNG/CSV **sha256 逐字节相同**、xlsx 内部 zip 条目全同。
- **空间索引**：候选对 **9,182,928 → 168,353（1.8%）**；关联段 **1.43 s → 0.14 s（×10.2）**；端到端真实数据 gcd **1.84→1.55 s (−16%)**、dmx **2.58→1.92 s (−26%)**（剩余为 openpyxl 保存与 matplotlib 三图，未变）。桶尺寸自适应（断面线 240 m 网格 31×36；等高线 66.4 m 网格 111×129），并与暴力解**逐结果相等**（最近线段含 t、等距取低序号、0.5 m 成员集合、2,845 个交点、标签 304/304、点 2,897、块分配 2,896/未分配 42 全一致）。
- **测试**：新增 `tests/selftest_sections.py`（**82 断言**，纯离线合成夹具，覆盖几何/网格等价/两入口逐格断言/`NO_INPUT`/`BAD_INPUT`×4/`NO_OPENPYXL`×2/缺 matplotlib 仍 exit 0），已接入 CI。
- **唯一内部修复**（真实数据观测不到，由合成夹具捕获）：dmx「说明」表 `合计` 行原用 `len(secs)`，改为 `len(rows)`。
- **尾巴（已评估）**：4.4 里「gcd 宽表逐 cell 写 → append」在真实 304 断面数据上做过候选实现与逐格/样式/产物 sha256 等价验证（34,024 cell 差异 0），但收益仅 ≈0.1%（宽表段 9.2→7.5 ms，占端到端 0.5%，端到端配对中位 −4.4 ms 在噪声内）⇒ **判定不值得、保留原写法**（候选 patch 留档 `tmp/py-section2/wide-append.patch`，将来数据量级变化可直接套用）；顺带把宽表**整表逐格**断言补进 `tests\selftest_sections.py`（82→85）。

### 8.2 4.2 LISP 注入管道抽象：`LispScan.cs`
- 新增 `scripts/helper/LispScan.cs`（304 行）：CMDACTIVE/LASTPROMPT 守卫 → ssget 过滤串 → 生成/写盘/`SendCommand` → 两阶段哨兵 → GBK 读回 → finally CMDECHO 还原 + 成功删档/失败 `*.orphan`，**五步唯一实现**；纯函数可离线测。
- `LispFile.cs` 240→**133 行**、`LispGeom.cs` 269→**161 行**：只剩 request + `LispWire.Parse*` + `LispRecord→JMap` 映射。**签名、`result`/`meta` 字段集合与顺序、10 处错误码/文案/hint/detail 逐字不变**，`CadHelper.cs`/`EditsQuery.cs` 调用点零改动。
- **真机 A/B**（显式 `-Doc`、只读）：`-Mode geom -Limit 20/500`、`read -Limit 20`、`query -Limit 10` 与 `tmp\baseline2` **0 字段差异**（`diff_ab.py` problems=0）；`%TEMP%` 残留 0、无 orphan、**CMDECHO=1 / CMDACTIVE=0**。
- 新增 `tests/lispwire/LispScanTests.cs`（**119 断言**，含 COM 桩端到端：成功删档+GBK 读回、started 超时→`LISP_FILE_TIMEOUT`+`.orphan`、not-started→`LISP_NOT_STARTED`、BUSY/PROMPT_POLLUTED 文案渲染断言）。

### 8.3 5.1 第一批：`Json.cs` / `PipeIo.cs` 可离线测
- `CadHelper.cs` **4,371 → 4,100 行**；`git diff --numstat` = **0 insertions / 271 deletions**；两个类体逐字节相等（独立字符串感知 walker 复核：Json 9,552 字符、PipeIo 1,087 字符 IDENTICAL）。协议（4 字节小端长度 + UTF-8 JSON）、数字格式、错误文案全不变。
- 新增 `tests/jsonself/JsonTests.cs`（**132 断言**）+ `JMapStub.cs`（仅测试用最小桩，不进 `scripts\helper`）：转义/数字边界/嵌套/键序/中文往返/50 层嵌套/15 个非法输入/`Write→Parse→Write` 字节相等。**收益：JSON 层从此能在无 CAD 的 CI 上回归。**
- 编码约定（查证结论）：request 写侧统一 UTF-8 无 BOM；读侧接受 UTF-8（含/不含 BOM）甚至带 BOM 的 UTF-16LE/BE，但 **GBK/ANSI 不报错、静默变 U+FFFD 乱码** ⇒ 调用方不得用 ANSI 写 request。已写入 `docs/M3-E1-perf.md §10.5`。

### 8.4 5.5 `prepare.py` 模块化
- **2,090 行 → 368 行薄壳** + 四模块：`prep_io`(343) / `prep_yssd`(458) / `prep_db`(535) / `prep_model`(538)；`prepare.py` 仍是 CLI 入口且 `import prepare` 仍可用（`qa_yssd.py` 一行未改）。
- **重复映射合并**：v1 `build_model` 与 v2 `build_db_model` 的坐标映射/bbox（17+11 行）、`TOO_TALL`（7+5）、`--layer-prefix`（3+3）、字高字面量（4+2+2）共 **54 行重复 → 24 行调用点 + 48 行单实现**（`CoordMapper`/`measure_bbox`/`resolve_layer_prefix`/`append_too_tall_warning`）。
- **等价性**：golden **21/21 全等**（stdout 原始字节、退出码、stderr、落盘 sha256；覆盖 two-col/yssd block1-2/`--drop-suspect`/`--no-axis --label-every`/`TOO_WIDE` 失败例/from-db 单+多序列 v2/反转夹具/`.xls`→xlrd/`--list-dates`/`--summary`/4 个负例），另 `28/28` 补充差分全等；机械证明「只搬不改」：69 个顶层节点逐字节相同，仅 `build_model`/`build_db_model` 按显式替换表改动。
- 迁移中 golden 抓到并修掉 1 个回归（v1 水位线 y 依赖的 `ky` 在合并后丢失 → NameError）——**这正是先抓 golden 的价值**。
- **尾巴（已做，见 §8.8）**：`--compare-source` 的离线 golden（用**合成** sqlite DB + 合成 yssd 源表，13 种形态）+ 正式测试 `tests\selftest_compare_source.py`（71 断言），并修掉两处真问题（0 点比对照报一致 → 新增 `SOURCE_COMPARE_UNMATCHED`；`--drop-suspect` 与比对下标错位 → 比对改用删点前原始序列）。**未做**：`DEFAULT_DB_PATH` 仍导入期求值（按"先别改语义"）。

### 8.5 第二轮验证矩阵（合并提交前）

| 项 | 结果 |
|---|---|
| helper 编译 | 第二轮中途 12 个 `.cs` → 5.1 第二批后 **22 个 `.cs`，0 error**（3 条历史 warning 不变） |
| `tests\selftest.py` / `test_audit.py` / `selftest_checkers.py` | 41 PASS / 89·0·3 / SELFTEST PASS（与第一轮一致） |
| `tests\selftest_sections.py`（新） | **82 PASS / 0 FAIL** |
| `tests\selftest_lisp.ps1`（现 6 套） | **644 PASS / 0 FAIL**（LispWire 106 + LispTemplate 53 + Json 132 + LispScan 119 + Core 103 + ComCmd 131） |
| `tests\run_m1.ps1 -DryRunOnly -NoCad` | **PASS 10 / FAIL 0 / SKIP 2**（`lisp.wire` 步 PASS lines 291→410） |
| 四个 golden 对比 | 数据侧 34,024+69,987 cell 0 差异；prepare 21/21 全等；LISP 真机 0 字段差异 |
| 真机只读冒烟（5.1 第二批后） | status / read geom 20 / read 20 / query 10 / doctor 全 ok，`readPath` 仍 lisp-file-geom / lisp-file；CMDECHO=1、CMDACTIVE=0、%TEMP% 残留 0 |
| **GitHub Actions `offline-ci`** | **首次实跑 11 步全绿**（run [41943999965](https://github.com/freeskychenjun/cad-cooperate/actions/runs/41943999965)，1m14s）：语法门 → selftest 41 → audit 89·0·3 → checkers → sections 82 → run_m1 10·0·2 → selftest_lisp 6 套 644 → 证据 artifact |

### 8.6 5.1 第二批：`CadHelper.cs` 逐类拆完（4,100 → 173 行）
- 13 个类型按 6 步搬出：`Core.cs`(130) `ComInterop.cs`(64) `Com.cs`(269) `Procs.cs`(124) `Cad.cs`(864) `State.cs`(218) `Cmd.cs`(126) `Daemon.cs`(108)，`Commands`（2,135 行 / 46 成员）拆成 **双 partial**：`Commands.cs`(810，共享 helper+会话诊断) + `CommandsRead.cs`(1,382，read/section/写图/verify/rollback)。`CadHelper.cs` 只剩 `Program`（173 行）；`scripts\helper` 现 22 个 `.cs`，**csc 通配、`cad.ps1`、run_m1 源清单零改动**。
- **纯搬移自证**：8 个搬移块在 baseline 字节流中各唯一出现、互不重叠；`baseline − 块 − 随块空行 == 搬移后文件`（198,878→118,438 B 逐字节相同，零夹带）；第 6 步成员级：baseline 类体 2,100 成员行**每行恰好归属一个成员一次**、46 成员在两文件中**各出现且仅一次**、顺序为 baseline 序子序列。
- **Lead 独立复核**（不复用对方脚本）：按成员名+成员体比对 → missing/extra/跨文件重复/成员体差异全 none；`HEAD − Commands 块` 与当前文件 diff **只剩 −9 行 using/空行、+0 行**。
- 3 处非搬移偏差（已批准）：两文件类声明插入 `partial `；原 12 处相邻成员间补单个空行；`CadHelper.cs` 删 8 条无用 `using`（编译器可证最小集）。
- 收益：入口文件 4,100→173 行；`Core`/`Com`/`State`/`Cmd` 成为可独立离线单测的单元；read/section 家族与会话诊断家族不再混在一个 2,135 行的类里。**未做**（按纯搬移纪律原样保留）：`Doctor` 的 `deepMatched` 死变量等，留待专门清理（`JMap`/`Json` 两处过时注释已随本批修正）。
- **配套离线测试（5.1 的收益兑现）**：新增 `tests/coreself/CoreTests.cs`（**103 断言**：JMap 覆盖写/键序/大小写/缺键/类型不匹配、CadError/Ctx、Json 往返、State 的内存态 session/token 函数）与 `ComCmdTests.cs`（**131 断言**：POCO 假 COM 对象走 `Com.Get/TryGet/Call/Set/Str/Dbl/I32/Bool/Pt/Flat/Hr`、**永久失败不重试**这个 M3-E1 策略（`ReadRetries=5` 时实际调用仍 1 次）、`Cmd.Balanced`/`PromptPolluted`/`Send` 四类守卫）。**零桩**：直接编译真的 `Core.cs`/`Com.cs`/`Cmd.cs`/`State.cs`；`tests/jsonself/JMapStub.cs` 删除，JsonTests 改用**真 JMap**（断言数不变 132）。
  C# 离线套件从 4 套/410 断言长到 **6 套/644 断言**（LispWire 106 + LispTemplate 53 + Json 132 + LispScan 119 + Core 103 + ComCmd 131），`tests\selftest_lisp.ps1` 一次跑完，CI 无需改动。

### 8.7 5.1 第三批：`CommandsRead.cs` 再拆 read / section / verify 三文件
- `CommandsRead.cs` **1,382 行 / 25 成员** → `CommandsRead.cs`（**456 行 / 7 成员**：read 家族）、新建 `CommandsSection.cs`（**670 行 / 14 成员**：section + plan meta/roles/waters/types/layers + 写原语 + Rollback/WaitIdle/UndoCmd）、新建 `CommandsVerify.cs`（**287 行 / 4 成员**）。`scripts\helper` 现 **24 个 `.cs`**；`Commands` 家族 = **四个 partial** 文件，仍是零调用点改动。
- 分配**由 crossref 实测决定**而非按名字猜：`AddPlanMeta/ByRole/WatersOut/CountTypes/CountLayers/CreateEntity/EnsureLayer/EnsureTextStyle/EnsureLinetype/Rollback` 的引用者只有 `SectionCmd`；`FirstExistingFont` 只被 `EnsureTextStyle` 引用；`WaitIdle` 被 `Rollback/VerifySection/UndoCmd` 共用（partial 跨文件可见）；read 7 个与 verify 4 个各自闭合成簇。
- 自证：25/25 成员三文件**各出现且仅一次**、顺序为 baseline 子序列、成员体 **sha 相等**；`baseline − 搬出成员块 == 三文件体` 逐字节重建；全仓恰 4 个 `partial Commands` 声明；Lead 独立复核（自写**行深度感知**成员切分器，避免上一版把方法体局部变量误判成成员）→ missing/extra/跨文件重复/成员体差异全 none。
- 非搬移偏差仅"文件装配层"：三个文件头重写（原头描述的是拆分后已不存在的全集）、using 按文件重分配、成员间空行 24→22（两个文件边界各少 1）。
- 验证：csc **24 源 0 error / 3 warning（均为基线）**；`selftest_lisp` 6 套 644 ALL PASS；python 五套（34 文件 3.9 语法门 / 41 / 89·0·3 / 82 / checkers）不变；`run_m1` 10/0/2；真机只读冒烟在**用户当前打开的** 8,511 实体图上 `read -Mode geom`→`lisp-file-geom`、`read`/`query`→`lisp-file`、`doctor` ok，CMDECHO=1、CMDACTIVE=0、%TEMP% 残留 0。

### 8.8 数据侧尾巴收口（4.4 宽表 append 评估 / 5.5 `--compare-source` golden + 两处修复）

**(a) 4.4 尾巴：gcd 宽表「逐 cell 写 → openpyxl append」= 评估后不改**
先在真实 304 断面 / 2,938 块数据上做了候选实现（append 的 dict 形式，只创建有值 cell）并验证等价：
4 sheet / **34,024 cell 值差异 0**、**全格样式指纹差异 0**、列宽与 freeze_panes 全同、CSV 与 3 张 PNG
**sha256 逐字节相同**。但计时显示它**不是热点**：宽表写出阶段 **9.16 → 7.47 ms（−18%）**，只占端到端
**0.5%**；端到端 13 对交替配对 **1.476 → 1.414 s（配对中位 −4.4 ms / −0.3%，7/13 胜）**，与噪声不可区分。
真正耗时是 `openpyxl` 首次 import（325–372 ms）、写全本+save（≈190 ms，其中 save 139.5 ms）、
`matplotlib.pyplot` import（474–590 ms）+ 3 张 PNG。
⇒ **判定不值得、保留原写法**（候选 patch 留档 `tmp/py-section2/wide-append.patch`）；顺带补了宽表
**整表逐格**数值/顺序断言（`tests/selftest_sections.py` 82 → **85**，含稀疏断面留空规则）。

**(b) 5.5 尾巴：`--compare-source` 的离线 golden + 正式测试**
仓库里没有匹配的真实源表，改用**合成夹具**：照 `hydro_data.db` 的真实 schema 建 sqlite（虚构站码
`99000001`）+ 合成 yssd 多列块 xlsx（表头 ×3 组、块元信息、左/右岸标签行、刻意截断高程 `"88"`/`"75"`、
0.03 偏差、起点距不符、源表缺批次）。13 种形态抓 golden（stdout 原始字节 / 退出码 / stderr / 落盘 sha256）：
12/13 逐字节全等，唯一差异是下面修掉的真 bug。新增 `tests/selftest_compare_source.py`（**71 断言**、
纯离线、自建夹具、py3.9 语法门 35 files 覆盖），已接入 CI（工作流 **12 步**）。

**(c) 顺带修掉两个真问题（都是"静默错数据/误报"级别）**
1. **0 点比对却报「两者一致」**：所选施测日期在源表里没有对应块时（`byDate[].matched=false`、`compared=0`），
   旧行为输出 `SOURCE_COMPARE_SAME` +「比对 0 点，0 处不同（两者一致）」——下游只看 code 会把"源表缺日期"
   当成"DB 与源表一致"。修法：`differingCount>0`→`DIFF`（文案不动）、`==0 且 unmatched>0`→**新码
   `SOURCE_COMPARE_UNMATCHED`**、否则 `SAME`（文案不动）。`compare_db_vs_excel()` 数据层一行未改。
2. **`--drop-suspect` 与 `--compare-source` 下标错位**：旧实现把"已被 drop 过的 `series[].points`"与源表条目
   按下标 1:1 对齐 ⇒ 删点后下标前移，`sourceCompare` 的 compared/same/differingCount/byDate 全部失真（静默错数据）。
   修法：只在「`--drop-suspect` + `--compare-source`」组合下，比对侧用 `_db_compare_series()` **重取删点前原始序列**
   （普通路径零额外开销、模型输出不变）。语义澄清：**比对=数据审计（以原始数据为准），模型=成图数据**。
   复现证据：合成用例 `2021-03-15`（2 个 >40 m 离群点、源表与 DB 原始值逐点一致）修前 `4/4/1/3`（且
   `offsetMismatch=3`，伪差异如 `vert=2: dbOffset=20 vs xlsOffset=5`）→ 修后 **6/6/6/0**，与不带 `--drop-suspect`
   的 `sourceCompare` 深度相等；多序列 `--last 5` 从 `28/23/16/7` → **30/25/21/4**（不删点正确值）。
   既有 13 形态 golden **13/13 逐字节不变**；`15-drop-nocompare`（只 drop 的模型输出）与
   `16-nodrop-compare`（正常路径）逐字节不变。

**已知限制（记录，不做猜测性修复）**：源表表头只有「1月19日」这类**月-日**，DB 日期 `YYYY-MM-DD` 归一到
`M-D` 匹配；同一份源表含跨年同月日、或一次比对选中不同年份的同月日批次时，会命中源表第一个同月日块
（其余无法区分），相关结论需人工复核（源表信息不足的固有歧义）。

**验证**：`selftest_compare_source` 71 / `selftest_sections` 85 / `selftest` 41 / `audit` 89·0·3 /
`checkers` PASS / `py39` 35 files；`run_m1 -DryRunOnly` **无 CAD 时 8·0·4**（两个 CAD 步骤按设计 SKIP，
CI 一直是这个口径；有 CAD 时 10·0·2）；C# 6 套 644 不变。

**(c) `DEFAULT_DB_PATH` 从导入期求值改为调用期求值（计划 5.5 的最后一条尾巴）**
旧实现 `prep_db.py` 在**导入时**快照 `os.environ["HYDRO_DB"]`，`prepare.py` 又把它当 argparse `--db` 的默认值
⇒ "先 import、后设环境变量"永远不生效（对测试/嵌入式调用尤其反直觉）。
改法：`prep_db.default_db_path()` **调用期**读 env；`prep_db` 与 `prepare` 各加 PEP 562 模块级 `__getattr__`，
保留 `DEFAULT_DB_PATH` / `from prep_db import DEFAULT_DB_PATH` 的兼容访问（返回访问那一刻的值）；
`--db` 的 argparse 默认改为 `None`，`main()` 统一 `args.db = args.db if args.db is not None else default_db_path()`
——**`is not None`** 确保显式 `--db ""` 仍是 `NO_DB` 而不是"回退 env"（旧行为一致）。
**验证**：10 种形态 golden（显式 `--db` / 纯 `HYDRO_DB` env / `--list-dates` 两种 / `--summary` /
`--compare-source` / 缺 DB→`NO_DB` / **显式 `--db ""` + env→仍 `NO_DB`** / 显式空串无 env /
显式不存在路径优先于 env）**10/10 逐字节全等**（stdout/退出码/stderr/落盘 sha256）；task-12 的 13 例与
task-13 的 18 例在新代码下仍然全等；`import prepare` 的 85 个公开名/常量/`--help` 兼容检查 PASS。
新增 13 条断言（`selftest_compare_source` 71 → **84**），含"进程内 import 后改 env 立即生效"与"再改一次仍生效"
（证明是每次调用都读、而非缓存一次）。

> 本轮另外发现（**并发会话在改 C# 侧，本文件不记录其结论**）：`scripts/helper/*.cs` 当时正处于另一个会话的
> 在途改动中（`Trust.cs`/`ProductRegistry.cs` + Cad/Cmd/CommandsRead/Core/LispScan 等），因此本轮只提交
> **数据侧 + 文档 + .gitignore**，不触碰、不提交那些 C# 改动；C# 侧的验证由 CI 在**已提交版本**上完成。
