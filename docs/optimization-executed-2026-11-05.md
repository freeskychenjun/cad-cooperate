# 优化执行记录（2026-11-05）

> 输入：`docs/optimization-plan.md`（2026-11-05 的只读分析报告，15 个问题 + 4 个批次）。
> 本文记录**实际采纳了什么、没采纳什么、为什么**，以及每项的验证证据。
> 基线：`v0.9.0`（`88b1a06`）+ 工作树未提交的 M3-E6 改动。M3-E6 工作树已先单独落库为
> `e70209f`（避免优化改动把未跟踪文件与已跟踪改动混在一起），本轮优化在其上分提交。

## 一、结论摘要

| 计划项 | 决策 | 落地内容 | 关键证据 |
|---|---|---|---|
| 3.1 `\|` 转义打歪几何字段（P0） | **采纳** | 新增 `scripts/helper/LispWire.cs`：`SplitRecord/UnescapeField/TryNum/ParseGeometry/ParseFlat` 成为**唯一解析实现**；`LispGeom`/`LispFile` 只留「管道 + `LispRecord`→`JMap` 映射」 | 离线 `tests/lispwire` **99 条**断言（`E\|1A\|TEXT\|0\|A\\\|B\|1.5…` ⇒ 9 段 / Text=`A|B` / X=1.5）；真机 A/B 500/500 实体整表逐字段相等 |
| 3.2 `R\|` 半径无消费（P0） | **采纳** | `LispWire` 解析 `R` 记录 → `radius`（与 COM 路径对齐） | 离线断言 `R\|h\|2.5` ⇒ `radius=2.5`；真机样本无 CIRCLE/ARC，未观测（已记入已知限制） |
| 3.3 提交 M3-E6 工作树（P0） | **采纳（抢先）** | 原样检查点提交 `e70209f`；本轮优化另分 4 个提交 | `git log`：`e70209f` + 后续 4 个优化提交 |
| 3.4 `.gitignore` 缺 `/output/`、`*.dwl`（P0） | **采纳** | 补 `/output/`、`*.dwl`、`*.dwl2`；4 张悬空 png 改为在 `docs/M3-E6-geometry-dump.md §4.1` 引用（高程来源对照图） | `git status --ignored`：`output/`、`tests/bench/fixtures/design-water.dwl` 均被忽略；`git ls-files` 无命中 |
| 3.5 prepare 吞错 / audit 非原子写（P0） | **采纳** | `prepare.py` 新增 `warn()/warn_exc()` 走 **stderr**（6 处静默吞错留痕、同码去重），`write_json_atomic` 去掉 `except BaseException`；`audit.py` `--json/--md` 改同款原子写（不可写 → stderr + exit 2） | `stdout` 单段 JSON 协议不变（41 PASS 回归）；缺 `stations`/`daily_water_level` 表用例可区分「表坏」与「无记录」；`KeyboardInterrupt/SystemExit` 穿透且无残留临时文件 |
| 4.1 LISP 孤儿脚本 / CMDECHO 事故（P1） | **采纳** | `*error*` 处理器（还原 CMDECHO + 旧处理器）；脚本先写 `__START__` 两阶段哨兵；失败/超时**改名 `*.orphan` 留证**不删、扫描前清 >24 h 遗留；新增错误码 `LISP_NOT_STARTED`、`LISP_FILE_TIMEOUT.detail.phase` | 真机 CMDECHO 前后均 **1**；`FileSystemWatcher` 抓到 `.start`(01:12:05.953) → `.done`(01:12:07.403)；成功路径 pipeline 残留 **0**；25 h orphan 被清、fresh orphan 保留；超时分支 21 条离线断言（端到端不做，理由见 §六） |
| 4.2 合并 LispFile/LispGeom 重复（P1） | **部分采纳** | **解析层**合并为 `LispWire`（消除两份切分/解码回调），并顺带修 3.1/3.2；**注入管道**（守卫→注入→哨兵→清理）本轮**未**抽 `LispScan.cs` | 两文件合计 −约 500 行、解析零重复；注入管道仍有约 120 行同构（见 §六 理由） |
| 4.3 bin/cache 无淘汰（P1） | **采纳** | `PruneCache`（保留 10 个非空 + 当前 sha + **运行中目录**，空目录 5 min 宽限，`deleted[]/skippedRunning[]/failedDirs[]`）；`serve -Action prune`；client 拉起新 daemon 后一次性自动淘汰；编译失败回滚空目录与 `build-<pid>.exe` | **141 → 9** 目录、空目录 **33 → 0**、释放 **12.00 MB**、`failed=0`；受控实验证明运行中目录被保护；二次 `removed=0`（幂等）；现 10 目录 / 0 空 / 1.76 MB |
| 4.4 Python 性能热点（P1） | **部分采纳** | `drop_suspect` 改字典/集合索引（O(p×o×s) → O(p)），**model JSON 逐字节等价** | N=2000/8000/20000：**1.6× / 4.6× / 13.6×**（7.669 s → 0.563 s），4 组模型 JSON `identical` |
| 4.5 query `-Layer` 下推（P1） | **不采纳** | 计划自身结论即"不建议为纯性能做"；本轮无"只 dump 单层"需求 | — |
| 5.1 拆 `ZwcHelper.cs` 巨石（P2） | **未做** | 见 §六：纯搬移 4.3k 行 × 每类回归，风险/收益在本轮窗口内不划算 | — |
| 5.2 哈希配方三（四）处（P2） | **采纳** | `zwc.ps1` 三处合并为 `Get-HelperSha12`；client 新增 `hash` 子命令；`serve -Action status` 输出 `helperSha/clientSha/recipeMatch`，不一致 → `HASH_RECIPE_MISMATCH` | `recipeMatch:true`；新实现 40 次中位 **0.69 ms vs 2.15 ms**、sha 完全一致 |
| 5.3 合并 build_sections 双脚本（P2） | **未做（只做安全侧）** | 未抽 `section_lib.py` 单实现；但补了输入校验（坏 JSON/缺键 → `NO_INPUT/BAD_INPUT` + exit 2）、openpyxl 延迟导入（`NO_OPENPYXL`）、TEMP/输出名/桩号参数化（默认值不变） | 端到端合成数据 21 项断言 PASS；见 §六 理由 |
| 5.4 编辑残留与死代码（P2） | **采纳（含扩展）** | `bindFailures` 重复行、`instanceError` 重复行、`ShowCmd` 重复 `return`；ZwcClient 退出码改结构化（不再全文搜子串）；daemon 的 ping/daemon-stop 改 `Json.Parse` 判顶层 `cmd`；`qa_yssd --block` 由死参数改为**真正生效**（默认行为不变）；硬编码 TEMP 回退改 `tempfile.gettempdir()` | 退出码 19 个假响应用例（含「`ok:true` + 文本含 BAD_DATA → **0**」，旧实现错判 3）；真机 `query -Text ping` 修复前返回 `cmd:"ping"`、修复后 `cmd:"query"` |
| 5.5 prepare.py 模块化（P2） | **未做** | 见 §六：2–3 人日且需逐字节协议保持，本轮窗口不足 | — |
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

- **4.1**：见摘要表。关键设计：脚本先 `(setq _zwc_olderr *error*)` 再 `(defun *error* …)`，处理器关闭打开的文件、还原 CMDECHO、还原旧处理器；正常收尾同样还原；`SafetyError` 白名单为此新增 `defun` 但**跳过形参表**，`forbidden` 表与只读语义不变（`defun` 体内 `command` 仍被拒，有负例）。
- **4.3**：见摘要表；`PruneCache` 只动 `bin\cache\<12位小写hex>`，散落文件/非 hex 目录/`bin\ZwcClient.exe`/`bin\tray` 一律不碰。
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
| 退出码抽取 | `bin\ZwcClient.exe --selftest-exitcode` | 19 个假响应全对（含旧实现错判用例） |
| cache 淘汰 | `zwc.ps1 serve -Action prune` | 141→9 目录、33→0 空目录、释放 12.00 MB、幂等 `removed=0` |
| 哈希自检 | `zwc.ps1 serve -Action status` | `recipeMatch:true`（`helperSha==clientSha`） |
| 真机回归（只读） | `tmp\evidence-runtime\real-regression.ps1` | **ALL PASS**：pre/post 与 `baseline2` 两组字段级 **ALL MATCH**；`serve stop` 后自动重启；CAD 保持 **1 实例 / 41413 实体** |
| 收尾现场 | — | `bin\cache` 10 目录 / 0 空 / 1.76 MB；`%TEMP%\zwcad-cooperate` 无本轮 `geom-*/scan-*` 残留 |

## 六、未采纳 / 降级项与理由

1. **5.1 拆 `ZwcHelper.cs`、5.5 `prepare.py` 模块化**：纯行为保持的大规模搬移（各 2–3 人日），必须逐类/逐函数跑在线+离线回归；本轮窗口内做不完，硬做只会把"未验证的大 diff"塞进历史。建议单独开一个批次，前提是先用 6.2 的 CI 把离线网兜住。
2. **5.3 合并 build_sections 双脚本**：计划估计 1–1.5 人日且需重跑真实数据；本轮只做了**风险侧**（输入校验、延迟导入、参数化），去重留给下一次（4.4 的空间索引优化也应等这次合并后再做，避免改两遍）。
3. **4.2 的注入管道合并**：解析层已合并（收益最大且可离线测）；注入管道（守卫→注入→哨兵→清理，约 120 行同构）与 4.1 改的是同一片代码，本轮先保证 4.1 的正确性并做真机验证，管道抽象留待下一轮（届时 4.1 的修复只需改一处）。
4. **4.4 其余热点**（断面×等高线空间索引、yssd 归一化缓存、audit 文字重叠网格、gcd 宽表 append）：当前数据量 10 s 可接受，属防御性优化；且 5.3 合并后代码会搬动，先优化会重复劳动。
5. **4.1 的超时端到端真机验证**：`Read` 在 LISP 失败后会 `LISP_FILE_FALLBACK` 回退 COM 全扫（41k 实体 ≈10 min），会在用户会话里长时间霸占 CAD。改为**纯函数离线单测**（`LispSentinelPhase.Classify/ErrorCodeFor`、`LispOrphans.EvidenceAction/Preserve/IsStale/CleanStale` 共 21 条断言），并在文档里写明取舍。
6. **6.4 可观测性**：非必需项，让位于 P0/P1。

## 七、遗留与下一步建议

1. **真机 `radius` 未观测**：本轮样本图前 500 实体不含 CIRCLE/ARC。建议下次在一张有圆/弧的图上跑 `read -Mode geom -Limit 50` 确认（离线断言与模板已覆盖生成/消费两端）。
2. **全量 geom 响应体上限（既有边界，未改协议）**：41k 实体全量 `read -Mode geom` 时，LISP 扫描只需 ≈1.5 s 且 dump 与基线逐字节相同（4.8 MB），但"41k 实体 → JSON 响应"产生不出应答（client 180 s 超时、stdout 0 字节；直连 one-shot helper 也不写 response）。已写入 `SKILL.md` 能力边界与 `docs/M3-E6-geometry-dump.md §6.3`；**可用做法**是 `-Limit` 分页或直接消费 `%TEMP%` 的 dump 文件。若要做"一次拿全量"，需要改协议（分块/流式/落盘引用），属独立课题。
3. **CI 未在 GitHub 实跑**：`offline-ci.yml` 的 5 个入口在本机已全绿，但 Actions 需要首次 push 后确认（特别是 windows runner 上 `csc.exe` 可用性与 Python 3.9 依赖安装）。首次 push 后请看一眼 `offline-ci` 工作流。
4. **下一轮可做**（按建议顺序）：5.3 合并 build_sections → 4.4 空间索引 → 4.2 注入管道抽象 → 5.1/5.5 模块化；每步在 CI 绿的基础上做。
5. **本机现场**：`bin\cache` 已从 140 目录/13.39 MB 收敛到 10 目录/1.76 MB；`%TEMP%\zwcad-cooperate` 里的 `g3.*`、`gtest*`、`contours.json`、`sections-src.json` 等是 **2026-11-04 M3-E6 试验遗留**，非本轮产物，可随时手工删除（不在仓库内）。
