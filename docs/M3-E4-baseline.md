# M3-E4 度量基线（20 条真实 CAD 任务 · E1 改造对照用）

> 2026-11-04 15:1x–15:47，ZWCAD 2025（`ZWCAD.Application.2025`，自起自退、只退自己 `launched:true` 的实例）。
> 目的：给「协作稳定性 + agent 任务效率」装仪表盘 —— **改造前（本文件）** / 改造后（E1 完成后用同一 runner 复跑对比）。
> 交付：`scripts\bench\run_bench.ps1`（runner）、`tests\bench\{tasks.json,make_fixtures.py,checkers\*}`、本文件、`tmp\bench\*`（原始数据）。
> **主数据集**：`tmp\bench\20261104-144658\`（20 条任务三次取中位数，完整可复算）；**复核数据集**：`tmp\bench\20261104-151201\`（修正后代码，18 条完成，见 §4）。

---

## 1. 基线三数字（先看这里）

| 指标 | 数值 | 说明 |
|---|---|---|
| **每命令均值（`status` ×6）** | **4,452 ms**（min 4,060 / median 4,345） | helper 自报平均仅 **167 ms** ⇒ **地板价 ≈ 4,202 ms**（≈94% 花在起 PowerShell + wrapper + JSON 往返 上） |
| **大图 read（624 实体真实水系图）** | **15.8 s**（任务总墙钟 20.7 s；响应 117,648 B ≈ 36.8k token） | 本次走 E1 新默认 `-Mode summary`；**E1 之前同图 `-Mode geom` 实测 209–224 s** ⇒ 读分级本身已带来 >13× 收益，E1 目标是 <10 s |
| **10 命令复合任务（S3 改水位重出图+体检）** | **52.4 s**（14,4658 主表，该次被判失败但墙钟有效）/ **30.9 s**（151201 修正后成功） | 10 条命令 ×~3–5 s；这是 daemon 收益最集中的形态（命令数 × 地板价） |

**成本结构一句话**：任务总墙钟 ≈ **命令数 × 4.2 s 地板价 + 少量 helper 重活**。例：`Q1` 2 命令 9.25 s（2×4.63）、`S1` 5 命令 20.8 s（5×4.17）、`S3` 10 命令 30.9–52.4 s。**daemon 把地板价打到 0.3 s 后，多命令任务的降幅是 70–90%。**

---

## 2. 方法学（可复现性）

| 项 | 做法 |
|---|---|
| 任务集 | `tests\bench\tasks.json`：20 条任务级用例（目标用自然语言写，steps 是 agent 的真实命令序列），**不是**命令微基准 |
| 入口 | `powershell -File scripts\zwc.ps1 <cmd> ...`（**PS 入口** = daemon 未启用时的现状；E1 后同一 runner 用 `-Entry client` 对比） |
| 被测代码快照 | runner `-Freeze`（默认开）把 `scripts\zwc.ps1` + `scripts\helper\*.cs` 复制到 `<Out>\frozen` 再跑，SHA256 记在 `summary.json.meta.freeze`；避免队友并行改代码把两个版本混进同一次基线 |
| 重复 | 每条 3 次取**中位数**；`Q4` 大图读只跑 1 次（单次 >200 s 量级，控时长；量级与中位数同阶） |
| 判定 | `tests\bench\checkers\check.py` 独立算（几何长度、分层计数、bbox 平移、verify 结果、退出码），输出 `{ok,kind,reasons,metrics}` |
| 指标 | 成功率 / 命令数 / 墙钟（含 PS 启动）/ 每命令 / 响应字节（**token≈字节/3.2**）/ 失败分类 / 一键撤销（undo 后实体数回 0）/ 进程卫生 |
| 产品约定 | 全程显式 `-ProgId ZWCAD.Application.2025`；`read` 需要几何/文字的任务显式 `-Mode geom`（E1 后默认变为 `summary`）；大图 `-TimeoutSec 900` |
| 环境 | 与本机其他 agent 并行（core-cad 在用 Classic 16 / AutoCAD），所以绝对地板价会随负载漂移：同一套 runner 实测过 **2.7 s / 4.2 s / 4.8 s** 三种地板价（见 §6）。比较改造前后应**同环境、看相对降幅**，或同一时段连跑 |

---

## 3. 主表：20 条任务基线（`20261104-144658`，中位数，PS 入口）

| 任务 | 类别 | 结果 | 命令数 | 中位墙钟 s | 每命令 s | 响应字节 | 近似 token | 一键撤销 |
|---|---|---|---|---|---|---|---|---|
| `Q1-layer-stats` | 读/查 | ✅ 成功 | 2 | 9.25 | 4.63 | 6,646 | 2,077 | - |
| `Q2-text-search` | 读/查 | ✅ 成功 | 2 | 8.92 | 4.46 | 6,646 | 2,077 | - |
| `Q3-open-polylines` | 读/查 | ✅ 成功 | 2 | 9.77 | 4.89 | 6,646 | 2,077 | - |
| `Q4-big-read` | 读/查 | ✅ 成功 | 2 | 20.70 | 10.35 | 117,648 | 36,765 | - |
| `W1-edit-text` | 改图 | ⚠ 能力缺口 | 1 | 2.00 | 2.00 | 169 | 53 | - |
| `W2-change-layer` | 改图 | ⚠ 能力缺口 | 1 | 2.06 | 2.06 | 169 | 53 | - |
| `W3-unify-text-height` | 改图 | ⚠ 能力缺口 | 1 | 2.07 | 2.07 | 169 | 53 | - |
| `W4-move-to-origin` | 改图 | ❌ 产品缺陷 | 7 | 30.56 | 4.37 | 21,543 | 6,732 | ✅ |
| `W5-delete-zero-lines` | 改图 | ⚠ 能力缺口 | 1 | 2.22 | 2.22 | 169 | 53 | - |
| `W6-xdata-mark` | 改图 | ⚠ 能力缺口 | 1 | 2.22 | 2.22 | 167 | 52 | - |
| `S1-single-section` | 断面 | ✅ 成功 | 5 | 20.84 | 4.17 | 42,418 | 13,256 | ✅ 4.5 s |
| `S2-overlay-4series` | 断面 | ✅ 成功¹ | 4 | 17.6¹ | 4.39¹ | 58,866¹ | 18,395¹ | ✅ |
| `S3-water-edit-composite` | 断面 | ✅ 成功¹ | 10 | 30.9¹ | 3.09¹ | 49,490¹ | 15,466¹ | ✅ |
| `S4-from-db-station` | 断面 | ✅ 成功¹ | 4 | 35.6¹ | 8.90¹ | 74,001¹ | 23,125¹ | ✅ |
| `C1-audit-clean` | 检核 | ✅ 成功¹ | 4 | 9.68¹ | 2.42¹ | 7,292¹ | 2,279¹ | - |
| `C2-audit-problem` | 检核 | ✅ 成功¹ | 4 | 11.90¹ | 2.97¹ | 14,551¹ | 4,547¹ | - |
| `C3-draw-readback` | 检核 | ✅ 成功 | 6 | 27.78 | 4.63 | 61,587 | 19,246 | ✅ 6.2 s |
| `X1-no-instance` | 稳定性 | ✅ 成功 | 1 | 4.44 | 4.44 | 356 | 111 | - |
| `X2-product-ambiguity` | 稳定性 | ✅ 成功 | 1 | 6.63 | 6.63 | 3,841 | 1,200 | - |
| `X3-bad-data-gate` | 稳定性 | ✅ 成功 | 3 | 1.22 | 0.41 | 449 | 140 | - |

¹ 144658 该条因 **我的检测脚本 bug**（audit 报告读错文件 / `{asset}` 标量被误当路径）误判失败，已用 `20261104-151201` 复核为成功，表内填复核数字。**这是测量工具 bug，不是产品问题**（修正只需改 checker，不重跑产品；W4 除外，见 §5）。

**类别汇总（144658，按中位任务成本）**

| 类别 | 任务数 | 平均命令数 | 每任务中位墙钟 | 每任务字节 | 成功/缺口/缺陷/失败 |
|---|---|---|---|---|---|
| 读/查 | 4 | 2.0 | **12.16 s** | 34,396 | 4 / 0 / 0 / 0 |
| 改图 | 6 | 2.0 | 6.86 s | 3,731 | 0 / 5 / 1 / 0 |
| 断面 | 4 | 4.8 | 20.87 s | 23,362 | 4¹ / 0 / 0 / 0 |
| 检核 | 3 | 4.7 | 21.42 s | 27,472 | 3¹ / 0 / 0 / 0 |
| 异常/稳定性 | 3 | 1.7 | 4.09 s | 1,549 | 3 / 0 / 0 / 0 |

**执行覆盖率 15/20**（5 条改图任务缺通用写原语，见 §5）。

---

## 4. 复核数据集（`20261104-151201`，修正后代码，18/20 完成）

该次运行在 `C3/rep1/sec1` 处 **CAD 卡死**（见 §7），故 C3/X2 无完整数据；其余 18 条全部符合预期：

| 任务 | 结果 | 命令数 | 中位墙钟 s | 每命令 s |
|---|---|---|---|---|
| `Q1` / `Q2` / `Q3` | ✅ | 2 / 2 / 2 | 5.92 / 5.65 / 4.86 | ~2.4–3.0 |
| `Q4-big-read`（1 次） | ✅ | 2 | **10.78**（read 单步 **7.97 s**） | 5.39 |
| `W1/W2/W3/W5/W6` | ⚠ 能力缺口 | 1 | 2.1–2.4 | - |
| `W4-move-to-origin` | ❌ 产品缺陷（origin 自检） | 7 | 17.13 | 2.45 |
| `S1` / `S2` / `S3` / `S4` | ✅ | 5 / 4 / 10 / 4 | 11.54 / 17.58 / **30.91** / 35.58 | 2.3–8.9 |
| `C1` / `C2` | ✅ | 4 / 4 | 9.68 / 11.90 | 2.4 / 3.0 |
| `X1` / `X3` | ✅ | 1 / 3 | 2.46 / 1.23 | - |

> 该次负载更轻（地板价 **2.7 s**），故同类任务比主表快 30–50%。**这正好说明：绝对数会随机器负载漂移，对比 E1 时应同环境复跑；结构结论（命令数 × 地板价 + read 重活）不受影响。**

---

## 5. 稳定性与能力边界

### 5.1 稳定性（全部符合预期，0 崩溃 / 0 残留）

| 任务 | 结论 |
|---|---|
| `X1-no-instance`（CAD 未开时 `status`） | exit 2 + `NO_INSTANCE`，4.4 s 内返回，不卡不崩 |
| `X2-product-ambiguity`（不给 `-ProgId`） | 单产品场景正确附着到运行中的 2025（不猜错、不另起）；双产品场景判 `MULTI_PRODUCT`（代码路径已由 task-8 实现） |
| `X3-bad-data-gate`（脏数据前置拦截） | 3 份坏 xlsx 全部 exit 2 + `BAD_DATA`、**不产出模型文件**、每个 0.4 s |
| 一键撤销 | 所有出图任务（S1–S4、C3、W4）`undo -Token` 后实体数回 0：**6/6 成功**，撤销墙钟 4.5–6.2 s（1 条命令，本身也吃地板价） |
| 进程卫生 | 每任务后本产品实例恒为 1；`finish -Quit` 后目标产品进程 **0**；其它产品进程（core-cad 的 Classic）只记录不动 |
| 命令面拒绝 | 未知写命令由 `zwc.ps1` 前置拒绝（**2.0–2.4 s**，未进 helper）——比正常命令便宜，因为不用起 helper |

### 5.2 能力缺口（5 条，E2 目标，不计失败）

`W1 批量改文字` / `W2 批量换层` / `W3 统一字高` / `W5 删除零长线` / `W6 XData 打标+筛选`：当前命令面只有 `doctor/status/open/info/read/section/undo/hint/finish`，**没有通用写原语**（entity add/update/delete/move + query）。tasks.json 里每条都写了 `futureSteps`（E2 接口落地后替换 steps 即可复用同一 runner）。探针确认命令被 `BAD_ARGS` 干净拒绝。

### 5.3 产品缺陷（1 条，已报给 core-cad 并**当轮修复**）

`W4-move-to-origin`：用「undo + `-Origin` 重画」模拟移动时，`section` 的**自检**报 `VERIFY_FAILED / MODEL_BBOX_INTERNAL: ground bbox [50,10,340,125] does not match W/H`，整批回滚。几何其实已正确画出（created=67 = expected=67，`expectedBbox` 与读回 `bbox` 一致），只是 W/H 校核没有扣掉非零 origin。证据：`tmp\bench\20261104-144658\W4-move-to-origin\rep1\sec2.json`。
**已修复并验证**：`tmp\bench\client-entry2`（helper SHA256 `81aa5d72…`）里 W4 **成功**，`verify.ok=true`、8 命令 **6.17 s**、一键撤销 ✅。基线主表保留"产品缺陷"是记录历史状态，不代表现状。

---

## 6. E1 改造前后对照（实测：PS 入口 vs client 入口）

**同一套 tasks.json、同一台机、同一天**，只换入口（`-Entry ps1` / `-Entry client`）。client 入口组：`tmp\bench\client-entry2\`（8 条关键任务 ×2 次，helper SHA256 `81aa5d72…`，daemon 由 client 自动带起，warm 后测）。

| 指标 | E1 前（PS 入口） | E1 后（client + daemon） | 变化 |
|---|---|---|---|
| 每命令 `status` 均值 | **4,452 ms**（min 4,060） | **219 ms**（min 170） | **−95%**（20×） |
| 每命令地板价（wall−helper） | **4,202 ms** | **71.5 ms** | **−98%** |
| helper 本体 | 167 ms | 144 ms | 持平（说明降的全是入口开销） |
| `Q1` 读 2 命令 | 9.25 s | **0.91 s** | −90% |
| `Q2` 文本检索 2 命令 | 8.92 s | **0.83 s** | −91% |
| `Q4` 大图 read（624 实体，1 次） | 20.70 s（read 单步 15.8 s） | **7.83 s**（read 单步 6.93 s） | −62%（受 read 本身限制） |
| `S3` 10 命令复合任务 | 30.9 s（轻载复核）/ 52.4 s（主表） | **7.40 s** | **−76% ~ −86%** |
| `W1` 能力缺口探针 | 2.00 s | **0.076 s** | −96% |
| `W4` 移动 workaround（8 命令） | 17.1–30.6 s（当时产品缺陷） | **6.17 s ✅成功** | 缺陷已修 |
| `X1` 无 CAD 错误 | 4.44 s | **0.159 s** | −96% |
| `X3` 脏数据闸门 3 步 | 1.22 s | **1.53 s** | 持平（python 侧主导） |
| 冷启动 `open -Launch`（2025） | 18.5–21.5 s | 13.2 s | 产品冷启动，与本改造无关 |
| 收尾 `finish -Quit` | 20.7 s | **49.7 s -> 7.86 s**（core-cad 已修：释放 daemon 持有的 `cad.App` COM 引用 + 轮询上限 45->25 s；`client-entry3` 实测 7.86 s、`procsAfter:0`） | 已修 |
| 大图 read 三档（624 实体） | 209–224 s（全属性） | 2025 实测 `summary` **6.93–7.73 s**；core-cad 在 Classic 16 warm daemon 实测 `handles` 571 ms / `summary` 1,807 ms / `geom` 1,558 ms（helper 本体 301/926/1,174 ms） | `summary` <10 s 达标 |
| `X1` 冷 daemon 首条 | 4.44 s | **1.96 s**（daemon 冷起）/ 后续 0.16–0.8 s | -56% |
| **live 源码复验**（client、`client-entry3`） | — | Q1 0.78 / Q2 0.66 / Q4 7.73 / S3 7.48 / X3 1.43 / W1 0.076 s；W4 因 daemon 文档缓存异常失败（§7 #8） | 除 W4 外一致 |

**全 20 条 client 组（repeat 2，`tmp\bench\20261104-160343`，2026-11-04 16:03–16:05）**：15 条成功 + 5 条能力缺口（E2）、**0 失败 / 0 产品缺陷**，W4 成功；每命令 `status` 均值 **139 ms**（min 123，地板价 **62 ms**）、`finish -Quit` **7.17 s**、`procsAfter:0`、总时长 **124.7 s**（20 条 ×2 次全跑完）。按类别中位任务成本：读/查 2.07 s、改图 1.09 s（含 5 条 76 ms 的缺口探针）、断面 5.72 s、检核 1.2–2.0 s、异常 1.15 s。**这组数据即 E1 后的全量对照。**

> 说明：全量 client 组曾因 **`-Freeze` 快照 + client/daemon 共享状态**出现间歇 `CLIENT_FAILED: helper binary missing`（core-cad 已让 client 自编译兜底），且**同一产品上并发跑两个 bench 会话会互相串扰**（共享 `%TEMP%\zwcad-cooperate\state.json`、scratch 文档名）。runner 现已加 **per-product 运行互斥锁**（`FileShare.None`，进程退出自动释放；第二个会话会被拒绝并提示），并发不再影响数据；如需并行请用不同 `-Product`。

## 7. 已知坑（本次实测，写进 SKILL 时必须带）

1. **E1 代码在基线前 1 分钟进树**：`read` 默认从 `geom` 改 `summary`（只回 handle/type/kind/layer/bbox），首版任务因此拿不到顶点。**对策**：基准里所有需要几何/文字的 `read` 显式 `-Mode geom`（旧 helper 忽略未知参数，所以同一 tasks.json 对 E1 前后都成立）。若再遇 read 结果“少了东西”，先看响应里的 `result.mode`。
2. **`-Launch` 只附着不独占**：目标产品已有实例时 `open -Launch` 附着，随后 `finish -Quit` 会关掉**那个实例**的所有文档。判据只有 `result.launched` / `result.app.launched`。runner 已加「目标产品已有实例 → 拒绝运行」前置闸门。**只退自己 `launched:true` 的 pid。**
3. **多文档累积会把 ZWCAD 2025 的 COM 打死（本次卡点）**：初版 runner 每个 rep 都 `open -New`，累积到 20+ 文档后，`section` 画 67 实体时 **hang 12+ 分钟**（期间检测到“中望CAD 帮助”窗口弹出，疑似模态阻塞；ZwcHelper 与 zwc.ps1 均卡在等待）。**已修**：① 写类任务共用一个 scratch 图纸（launch 创建、`{scratch}` 占位、收尾 undo 回空）→ 全程文档 ≤6；② 每条命令加 runner 级看门狗（`timeoutSec+60 s`，超时 kill 并记 `exit=-9/WATCHDOG_TIMEOUT`）。复跑请用**当前版 runner**（带看门狗），别用 144658 那版长跑。
4. **大图 read 超时口径**：helper 默认 60 s；2025 无 LISP 桥、`-Mode geom` 时 ≈0.36 s/实体 → 624 实体需 3–4 分钟。任务里 `read` 一律 `-TimeoutSec 900`；`run_bench.ps1` 的看门狗在此基础上 +60 s。
5. **机器负载会让地板价漂移 2.7–4.8 s**：本次三个 run 分别测到 2.7 / 4.2 / 4.8 s。对比 E1 前后务必**同环境**连跑，或只看相对降幅；报告里已标注每个 run 的地板价。
6. **Python 侧步骤很便宜**（prepare 0.3–1.6 s、audit 0.1 s、verify 0.1 s），**不构成瓶颈**；成本全在 CAD 命令入口。
7. **产品数据侧的 DB 查询正常**：`prepare.py --from-db <站码>`（2/4 序列、单日期）⇔ 5.9 GB `hydro_data.db` 只读查询 0.3–1.6 s。S2/S4 在 144658 的失败是**我的 runner 把站码当相对路径**（`{asset}` 标量被 `Join-Path`），已修（当前 tasks.json + runner 正常）。
8. **同一产品上并发跑两个 bench 会话会串扰（本次实测，runner 已加互斥）**：两个会话共享 `%TEMP%\zwcad-cooperate\state.json`（undo token / openedDocs）与 scratch 文档名（`Drawing2.dwg`），会看到"section 画完 67 但 read 读到 0 / 下一 rep 读到 134"这类陈旧读取（`tmp\bench\client-entry3` 的 W4 即此现象；同期 `tmp\bench\20261104-160343` 的独立全量 client 跑 W4 成功 ⇒ 非稳定复现，也非绘制/undo 逻辑）。**已修**：`run_bench.ps1` 在启动处对 `-Product` 加 `FileShare.None` 排他锁（环境实例被占用就拒绝运行；崩溃自动释放，无陈锁）；需要真正并行请用不同 `-Product`。

## 8. 交付物 / 复跑

| 文件 | 说明 |
|---|---|
| `scripts\bench\run_bench.ps1` | runner（PS 5.1 纯 ASCII+BOM）：`-Tasks all|id,id -Product zwcad16|zwcad2025|autocad -Repeat 3 -Out tmp\bench\<stamp> [-Entry ps1|client] [-Freeze]`；自起自退、冻结快照、看门狗、scratch 图纸、进程卫生 |
| `tests\bench\tasks.json` | 20 条任务（见 `tests\bench\README.md` 的覆盖表） |
| `tests\bench\make_fixtures.py` | 生成 `design-water.dxf`（23 实体，含设计中文字/开闭多段线/零长线）+ 真值 `expected.json`（含 624 实体大图的分层计数） |
| `tests\bench\checkers\check.py` / `summarize.py` | 逐任务判定 / summary.md 渲染 |
| `tmp\bench\20261104-144658\summary.{json,md}` | **主基线原始数据 + 自动报告**（PS 入口，20 条） |
| `tmp\bench\20261104-151201\*` | 复核数据（修正后代码；含卡死证据 `C3-draw-readback\rep1\`） |
| `tmp\bench\client-entry2\summary.{json,md}` | **E1 client 入口组**（8 条关键任务 ×2，daemon 自动带起） |

```powershell
# 复跑基线（PS 入口，约 20 分钟）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\bench\run_bench.ps1 `
    -Tasks all -Product zwcad2025 -Repeat 3
# E1 后（client 入口；cold-start 未修好时先 `serve -Action start` 一次跑 warm daemon）
... -Entry client -Repeat 3
# 冒烟
... -Tasks Q1-layer-stats,S1-single-section -Repeat 1 -Out tmp\bench\smoke
```

## 9. 下一步

- ✅ **client 入口组已补**（§6，`tmp\bench\client-entry2`）：Q1/Q2/Q4/S3/X1/X3/W1/W4 全跑通，W4 因 core-cad 修复 origin 自检而转绿。
- ✅ **全 20 条 client 组已跑通**（`tmp\bench\20261104-160343`，repeat 2）：15 成功 + 5 能力缺口、0 失败，W4 成功；每命令 139 ms、finish 7.17 s、总时长 124.7 s。见 §6。
- ✅ `finish -Quit` 已修并复验 7.86 s；⚠ 新发现 daemon 文档缓存异常（§7 #8，W4 在 live daemon 下偶发失败）→ 等 core-cad 修复后复跑 W4 确认；大图 read 仍是最大单项（6.9 s），继续分页/摘要优先。
- **E1 正式收尾后**：用当前 runner 同环境连跑 `-Entry ps1` 与 `-Entry client` 各一次（~25 min + ~8 min），把本文件 §6 更新为最终对比并冻结数字。
- **E2 落地后**：5 条能力缺口任务的 `futureSteps` 换成真命令（`entity/query`），执行覆盖率应从 15/20 → 20/20。
