# tests\bench — M3-E4 度量基准集（20 条真实 CAD 任务）

> 目标：用**任务级**基准同时量化两件事 —— **协作稳定性**（成功率/失败分类/一键撤销/进程卫生）和 **agent 任务效率**（命令数/墙钟/响应字节≈token）。
> 不是"预设功能测试"：每条任务是一句用户真会说的话，步骤是 agent 真会走的命令序列，判定在独立 checker 里做算术。
> 交付文档：`docs\M3-E4-baseline.md`（E1 改造前的对照基线）。

## 1. 文件契约

| 文件 | 作用 |
|---|---|
| `tasks.json` | 20 条任务定义：`id / category / goal（自然语言）/ why（代表性）/ steps（cad 或 py）/ maxCommands / undoCheck / expect` |
| `make_fixtures.py` | 生成 `fixtures\design-water.dxf`（23 实体，含中文"设计水位"文字、开/闭/差一点闭合多段线、零长线）+ `fixtures\expected.json`（真值：各层实体数、RIVER 总长、未闭合条数、624 实体大图的分层计数） |
| `checkers\check.py` | 每条任务的**成功判定**（几何/JSON 算术），输出 `{ok, kind, reasons, metrics}`；`kind` 区分 `success / capability_gap / product_bug / failure`；含 E2 负例 `N1/N2/N3`（坏锚点整批不改、非法 set 整体回滚、undo 回基线指纹） |
| `checkers\gen_entity_request.py` | 把 `query` 结果拼成 `entity -Items` 批量请求（update-text / update-layer / update-height / delete-zero-length / mark-xdata / delete-marked）——本地步骤，不算 CAD 命令 |
| `checkers\corrupt_request.py` | 负例用：故意写坏 handle / expect / set |
| `negative.json` | E2 负例任务（`-TasksFile tests\bench\negative.json`） |
| `checkers\summarize.py` | 由 `summary.json` 渲染 `summary.md`（中文表格 + 分类均值 + "贵在哪一步"） |
| `..\..\scripts\bench\run_bench.ps1` | runner：冻结被测代码 → 自起独占实例 → 逐任务×N 次 → 采指标 → 判定 → 收尾归零 → 出 summary |

## 2. 20 条任务与覆盖

| 类别 | 任务 | 用户会怎么说 | 现状 |
|---|---|---|---|
| 读/查（4） | `Q1-layer-stats` | "RIVER 层多少实体？各类型？总长多少米？" | 可执行 |
| | `Q2-text-search` | "把写着「设计水位」的文字都找出来给我句柄" | 可执行 |
| | `Q3-open-polylines` | "哪些多段线没闭合？哪条像忘了闭合？" | 可执行 |
| | `Q4-big-read` | "打开真实水系图，报实体总数/各层数量/空图层" | 可执行（**624 实体，单次数分钟级**） |
| 改图（6） | `W1-edit-text` | "3 处「设计水位」改成「设计水位（复核）」" | 可执行（E2：query 定位 + entity update set.text） |
| | `W2-change-layer` | "RIVER 上的实体全移到 RIVER-2" | 可执行（E2：query -Layer + entity update set.layer） |
| | `W3-unify-text-height` | "注释层文字统一成字高 2.5" | 可执行（E2：query + entity update set.height） |
| | `W4-move-to-origin` | "整张断面图右移 50 mm" | 可执行（workaround：undo + `-Origin` 重画） |
| | `W5-delete-zero-lines` | "删掉所有零长度线" | 可执行（E2：query -Type line 算零长 → entity delete） |
| | `W6-xdata-mark` | "给文字打 XData 标记 → 按标记筛 → 按标记删" | 可执行（E2：entity xdata + query -XData + delete） |
| 断面（4） | `S1-single-section` | "这张 30 点大断面出张断面图，含设计水位 42.30" | 可执行 |
| | `S2-overlay-4series` | "示例站最近 4 次断面套绘一张，标注别挤" | 可执行 |
| | `S3-water-edit-composite` | "设计水位 42.30→43.00 重出，旧图撤掉，顺手体检" | 可执行（**10 条命令复合任务**） |
| | `S4-from-db-station` | "用数据库示例站 2025-09-18 那次的断面出图" | 可执行 |
| 检核（3） | `C1-audit-clean` | "干净图体检应该 0 问题（别误报）" | 可执行 |
| | `C2-audit-problem` | "问题图体检，把 blocker 挑出来" | 可执行 |
| | `C3-draw-readback` | "出图后 read 回读逐项核对，确认没画错" | 可执行 |
| 异常/稳定性（3） | `X1-no-instance` | "CAD 没开，确认能不能连上，连不上说清原因" | 可执行（需无实例时跑） |
| | `X2-product-ambiguity` | "多产品机器上不给 ProgId：别猜错产品" | 可执行（单产品分支 / 双产品分支都判） |
| | `X3-bad-data-gate` | "脏数据要当场拒绝，别画半张图" | 可执行 |

**执行覆盖率：E2 前 15/20，E2 后应 20/20**（W1/W2/W3/W5/W6 已改写成 `query` + `entity -Items` 的真实序列，checker 断言**回读结果**而不是退出码）。

## 3. 度量口径

| 指标 | 口径 |
|---|---|
| 成功率 | checker.ok 的 rep 比例；`capability_gap` 不计失败（单独统计） |
| 命令数 | 任务 steps 数（CAD 命令 + python 步骤都算，agent 都要付启动/IO） |
| 墙钟 | 每次 rep 的 stopwatch 总耗时；三次取**中位数** |
| 每命令 | 中位墙钟 / 命令数 |
| 响应字节 | 所有步骤 stdout 的 UTF-8 字节之和；**近似 token = 字节 / 3.2** |
| 地板价 | `status` ×6：`wallMs - helper.elapsedMs` 的中位数（≈ PS 启动 + wrapper 开销） |
| 失败分类 | 由 `error.code` 归：`TIMEOUT / BUSY / VERIFY_FAILED / BAD_DATA / NO_INSTANCE / MULTI_PRODUCT / DOC_NOT_FOUND / OTHER` |
| 一键撤销 | 写类任务结束后 `undo -Token` + 回读，实体数回 0 记 ✅（墙钟单独记，不算任务成本） |
| 进程 | 每任务后**本产品**实例数（应恒为 1）；finish 后应归 0；其它产品进程只记录不动 |

## 3.5 E2 写任务（W1–W6）的约定

- 每条 W 任务先 `setup.copyFixture`：runner 把 `fixtures\design-water.dxf` **复制到本次 rep 目录**再 `open -Doc {workdoc}`（源夹具不动，三次 repeat 互不污染）。
- 序列统一是 `open → query（定位 handle）→ gen（本地拼 JSON）→ entity -Items（批量写，带 expect 锚点）→ read -Mode geom`；W6 多一段 `query -XData`。
- **每条命令显式 `-Doc`**（DESIGN 口径：多文档且不给 `-Doc` 会 `DOC_AMBIGUOUS`）。
- `entity` 响应只看 `result.applied / verify.ok / rolledBack / failed`，**真正判定靠最后 `read -Mode geom` 独立回读**。
- 负例：`-TasksFile tests\bench\negative.json`（N1 坏锚点 / N2 回滚 / N3 undo / N4 diff）。

## 4. 跑法

```powershell
# 完整基线（自起自退 ZWCAD 2025；约 20~25 分钟，含 1 次 624 实体大图读）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\bench\run_bench.ps1 `
    -Tasks all -Product zwcad2025 -Repeat 3

# 只跑几条（冒烟/回归）
... -Tasks Q1-layer-stats,S1-single-section -Repeat 1 -Out tmp\bench\smoke
# E1 之后用同一 runner 对比（daemon + ZwcClient 就绪后）
... -Entry client -Repeat 3
# 不冻结代码（默认会冻结；-NoFreeze 关闭。注意 `powershell -File` 下不要写 -Freeze:$false，布尔参数会被当字符串）
... -NoFreeze
```

输出目录：`tmp\bench\<stamp>\{bench.log, launch.json, summary.json, summary.md, frozen\*, <taskid>\rep<N>\{*.raw.txt,*.json,*.meta.json,verdict.json,context.json,work-*.dxf}}`。
E2 负例：`powershell ... -File scripts\bench\run_bench.ps1 -TasksFile tests\bench\negative.json -Tasks all -Repeat 1 -Entry client -ProgId ZWCAD.Application.2025`。

## 5. CAD 纪律（踩过坑，别改坏）

1. **`-Launch` 只附着不独占**：目标产品已有实例时 `open -Launch` 会附上去，随后 `finish -Quit` 会关别人的文档。唯一判据 `result.launched` / `result.app.launched`；runner 在 launch 前先查目标产品进程，**有就直接拒绝运行**。
2. **只退自己 pid**：`finish -Quit` 只对本实例；其它产品（core-cad 的 Classic、lead 的 AutoCAD）**只记录不动**。
3. **大图 read 必须放大超时**：ZWCAD 2025 无 LISP 桥，624 实体 ≈ 0.36 s/实体 → 任务里 `read` 给 `-TimeoutSec 900`。
4. 每条命令显式 `-ProgId`（本机三产品共存），唯一例外是 `X2` 故意不带（那是被测点）。
5. runner 的 `-Freeze` 默认开：把 `scripts\zwc.ps1` + `scripts\helper\*.cs` 复制到输出目录再跑，避免队友并行改代码把两个版本混进同一次基线；快照 SHA256 记在 `summary.json.meta.freeze`。

## 6. 加任务 / 改判定

1. `tasks.json` 加一条：`steps` 里 CAD 命令用 `{"kind":"cad","cmd":"read","args":[...]}`，python 用 `{"kind":"py","script":"scripts/...","args":[...]}`；占位符 `{repo} {rundir} {fixture:NAME} {asset:NAME} {ref:STEP.PATH} {stepfile:STEP}`。
2. `checkers\check.py` 加 `chk_<ID>` 并注册到 `CHECKS`；只读步骤输出（`*.meta.json` 里的 exit/wallMs/bytes + `*.json` 响应），输出 `{ok,kind,reasons,metrics}`。
3. 先 `-Tasks <新id> -Repeat 1` 单跑，再进全套。
