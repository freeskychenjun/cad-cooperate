# M3-E3 稳定性验收：故障注入 + 并发（三会话零丢失）+ 回归

> 验收人：audit-dev（造故障、独立判据）｜被测：core-cad 的 E3 稳定性骨架（文档级锁/排队、忙态自愈、写前重锚定、挂死检测 + `serve reset/recover`）
> 产品：ZWCAD 2025（本人固定；core-cad 用 Classic/ACAD）｜入口：`-Entry client` + **frozen 快照**（每次 chaos 自己复制 cad.ps1/helper/CadClient 到私有目录，避免队友热改代码把 hash 变掉）
> 证据：`tmp\bench\e3\{concurrency,concurrency-locked,chaos}.json` + 各 case 目录
> 判定口径（用户/lead 拍板）：**要么干净失败（明确错误码/有界），要么自愈（明确 `PROMPT_CLEARED`/`recover`/自动重启证据）；"挂死但最后看起来没事"算失败**。SKIPPED 只允许是"注入手段受限"，必须写明原因，不能混成 PASS。

---

## 1. 结论

| 维度 | 结论 |
|---|---|
| 三会话并发 | **零丢失**：3 会话同时写同一张图，最终实体数 = 基线+30（50/50），0 重复 handle、0 失败；无锁/有锁两版都零丢失，差异在延迟 |
| 并发延迟（两 shape 口径） | **A 命令级**（30 条单实体命令，core-cad 测）p50 **486 ms**；**B 批次级**（3 条命令各 10 实体 = 30 实体，本报告）无锁 p50 **3,325** / p95 11,727（每实体 **333 ms**），有锁 p50 **3,656** / p95 **6,108**（每实体 **366 ms**）。**目标口径：每实体 <400 ms 已达成（333–366）；单实体命令 p50 <400 未达（486，需 client 常驻，E4+）**——见 core-cad `M3-E3-stability.md §5.3` |
| 六类 chaos | **C3/C4/C5/C6 四类 MATCHED**；C1（CAD 忙碌）/C2（提示符污染）为 **SKIPPED = 注入手段受限**（已记录真实样本/尝试过程，非产品挂死） |
| 回归 | **本轮未跑**（按 lead 指示让 core-cad 先跑，避免两人同时触发 `DOC_AMBIGUOUS` 冲突）；待窗口错开后再补，见 §6 |

## 2. 三会话并发基准（`tmp\bench\e3\concurrency*.json`）

方法：私有夹具副本（20 实体）、3 个 `bin\CadClient.exe` **同时**各发一条 `entity -Items`；起始 `snapshot`，结束用 `read -Mode geom` 独立回读 + `diff -Since` 交叉验证。

| Shape | 版本 | 命令数 | 实体 | p50 | p95 | 每实体成本 | 说明 |
|---|---|---|---|---|---|---|---|
| **A 命令级**（core-cad 自测） | 有锁 | 30 | 30 | **486 ms** | - | 486 ms | 每条命令 1 个实体；受 daemon 单请求串行与 ~400 ms 固定开销限制 |
| **B 批次级 · 无锁**（本报告） | 17:41 快照 | 6 | 30 | **3,325 ms** | **11,727 ms** | **333 ms** | 3×2×5 实体；round2 三条 ~11.5 s 是排队把时间吃掉 |
| **B 批次级 · 有锁**（本报告） | 18:09 快照 | 3 | 30 | **3,656 ms** | **6,108 ms** | **366 ms** | 3×1×10 实体（与 lead 要求的 3×10=30 形状一致） |
| **A shape · 优化前**（本报告独立 harness） | 18:55 快照 | 30 | 30 | **751 ms** | **5,277 ms** | 751 ms/命令 | 3 会话×10 条单实体、步同步并发；同构建顺序基线 p50 **303 ms** |
| **A shape · 优化后 · ZWCAD 2025**（本次独立复跑，两轮） | 19:0x live | 30 | 30 | **363 / 458 ms**（另一轮独立复跑 439 / 457） | 3,865 / 3,923 | - | 3 会话×10 条单实体**连续**并发（`shape_a.ps1 -Mode continuous`）；顺序基线 p50 **301 ms**；零丢失 30/30、0 重复、0 错误 |
| **A shape · 优化后 · Classic 16**（对照，同 harness） | 19:0x live | 30 | 30 | **385 / 414 ms** | 564 / 644 | - | core-cad 自测（他的 harness，pins Classic 16）：**333 / 347 ms**、p95 576/451、顺序 205 ms |

**A shape 定义（防止后人误读）**：3 个会话各发 10 条**单实体** `entity`（不是 batch），并发在飞；"顺序基线"= 同一构建下 1 个会话顺序发 10 条单实体命令。本报告独立 harness = `tests\bench\chaos\shape_a.ps1`（frozen 快照 + 私有夹具 + `read -Mode geom` 独立断言；本表 18:55 的 751 ms 是**优化前**基线，最终两轮数字见 §8）。

**目标口径（已与 core-cad 对齐）**：`p50<400 ms` 只适用于"**单实体命令**"（A shape，当前 486 ms 未达，需 client 常驻，E4+）；对 10 实体 batch（B shape）应看"**每实体成本 <400 ms**"（当前 333–366 ms，**已达成**）。p95 有锁版比无锁版改善 48%。

- 两版都**零丢失**：说明即使无锁，daemon 单请求串行也避免了写竞争；真正的问题是延迟（round2 三条几乎同时 ~11.5 s，符合"排队把时间吃掉"）。
- **与 core-cad 自测的差异**：他报 30 条**单实体命令** p50 775 ms；本轮是 **3 条各 10 实体的大 batch**，p50 3.3–3.7 s（batch 内部 apply+回读本来就贵），两者形状不同不可混比。lead 要求我们统一到"3×10=30 条"：本轮有锁版即该形状（3 条命令）。
- **未达 p50<400 ms**：瓶颈=daemon 单请求串行 + 每条命令固定开销（core-cad 已在加 `result.stability{attachMs,busyMs,...}` 继续压；他的数据：30 条单实体命令 wall 13.9 s、p50 775 ms、30/30 落盘）。

## 3. 六类 chaos（`tmp\bench\e3\chaos.json`，合并三批结果）

| # | 故障 | 预期 | 实际 | 判定 |
|---|---|---|---|---|
| **C1** | 用户忙碌（CAD 停在待输入命令） | `BUSY`/`BUSY_TIMEOUT`（有界、带 reason）或成功+`BUSY_CMDACTIVE_IGNORED`；ESC 后重试成功 | 注入无效：后台进程持住 `SendCommand('_.LINE ' / '_.CIRCLE ' / '_.PLINE ' / '_.RECTANG ')` 期间 `CMDACTIVE` 始终 0（ZWCAD 2025 的 SendCommand 同步阻塞且命令状态不暴露给进程外客户端） | **SKIPPED（注入限制）** |
| **C2** | 提示符污染（坏 LISP） | `PROMPT_CLEARED` 后成功，或 `CMD_STATE_POLLUTED` | 注入无效：4 种坏 LISP 样本后 `LASTPROMPT` 仍是干净"命令: "（详见 §4 真实样本） | **SKIPPED（注入限制）** |
| **C3** | 写一半失败（height=-5） | `VERIFY_FAILED` + `rolledBack:true` + 指纹不变 | ✅ `exit=3 code=VERIFY_FAILED rolledBack=true fingerprintChanged=0`（两次复跑一致） | **MATCHED** |
| **C4** | 文档被关闭后写 | `DOC_GONE`（有界、不崩），重开后成功 | ✅ COM `Documents.Close()` 关全部文档 → 写 `exit=2 code=DOC_GONE bounded=true`；`open` 重开 exit 0；重试成功 count 20→21 | **MATCHED** |
| **C5** | daemon 被杀（写进行中） | 有界返回/自动重启，**无半成品** | ✅ 只杀**自己 frozen 快照的 daemon**（先验 `daemon.exe` 在 frozen 镜像内，pid 3540）→ 200 条写命令 `exit=0, wall=7,339 ms, partial=false, added=200`；随后 `status` 正常（daemon 自动重建） | **MATCHED** |
| **C6** | 双 agent 同图交替写 | 串行化、双方 diff 正确、无覆盖 | ✅ 2 会话×3 轮×3 条：count 20→38（=期望）、双方 18 条文本 0 缺失、`diff.added=18` | **MATCHED** |

**进程卫生**：每个 case 用完 `finish -Quit` 后目标产品族进程 = 0；C5 只杀自己 frozen daemon 的 pid（先做归属证明，否则拒绝执行）。
**文档完整性**：C3/C4/C6 都用"改前/改后指纹 + 实体数"双证；C5 用"要么 0 条要么 200 条"判无半成品。

## 4. 供 core-cad 修复/对齐的输入

### 4.1 C2：ZWCAD 2025 真实 prompt 样本（4 种坏 LISP，全部未产生污染）
| 注入命令 | `LASTPROMPT` 实测 |
|---|---|
| `(setq x (1+))` | `命令: ` |
| `(princ "abc` | `命令: ` |
| `(command "_.LINE" "nonsense")` | ``（空） |
| `((( ` + Enter | ``（空） |
| 单独 Enter | ``（空） |

→ 结论：中文界面的污染签名**不是** `Missing:`；建议 core-cad 按 `错误:`/`缺少`/`缺陷`/`无效` 多模式匹配，且 LASTPROMPT 可能是空串。本机这 4 种都没能制造出可识别的污染态，因此 `PROMPT_CLEARED` 路径本轮**无法验证**（不是通过也不是失败）。

### 4.2 C1：一个必须定性的"20.2 s 成功但 0 warning"现象
首轮 C1 尝试（注入进程被 kill 后其实没进命令态、属无效注入）里，一次 `entity` 写**成功**但 `wall=20,214 ms`，响应**没有任何 warning**、`warnings[]` 为空。按"挂了但最后看起来没事=失败"的口径，这需要 core-cad 补时间构成与告警：
- 请在 `result.stability{attachMs,lockMs,busyMs,fingerprintMs,bodyMs,totalMs}` 之外，**对超过阈值的时间给出 `warnings`**，例如 `LOCK_WAITED{ms}`（>3 s 必须报）、`BUSY_RETRIED{reason,ms}`；
- 我复现时会同时抓 `transport / elapsedMs / lock.waitedMs / stability.*` 一起发。
（该次原始 JSON 已被后续批次覆盖，日志见会话记录。）

**复现结论（18:47，`tests\bench\chaos\probe.ps1 -Case latency`，证据 `tmp\bench\e3\probe.json`）**：同一形状（后台 injector 持住 `SendCommand('_.LINE ')` → 1.3 s 后 client `entity` 写 1 条）**未能复现 20.2 s**：本次 `wall=412 ms`、`warnings=[]`、`stability={attachMs:60, lockMs:35, busyMs:7, fingerprintMs:33, bodyMs:165, totalMs:300}`、`lock={acquired:true, waitedMs:0}`、`busy={waitedMs:3,promptCleared:false}`。判断：20.2 s 是**旧构建的 busy 退避循环**（core-cad 已移除 150 ms/次退避）；新构建已把时间拆成 `stability.*`，`LOCK_WAITED` 仅在锁等待 ≥1 s 告警（正常写不触发属预期）。**结论：旧构建现象，新构建无法复现且已可解释**；验证阈值告警需人为制造 >1 s 锁等待（后续）。

### 4.3 C4 的注入方式（供其它人复用）
进程外 COM 直接 `Document.Close` 在本机不稳定（常拿回 String/被文件占用）；可用的是 **`app.Documents.Close()`（closeall）**：关掉全部文档后写 → 稳定得到 `DOC_GONE`。脚本：`tests\bench\chaos\inject.ps1 -Action closeall`。

## 5. 交付物

| 文件 | 内容 |
|---|---|
| `tests\bench\chaos\concurrency.ps1` | 三会话并发基准（frozen 快照、异步多客户端、独立回读断言、p50/p95/waitedMs/失败数） |
| `tests\bench\chaos\chaos.ps1` | 六类故障注入（`-Cases C1,C5,C6 -AllowDaemonKill`；SKIPPED 不计 PASS） |
| `tests\bench\chaos\inject.ps1` | 进程外 COM 注入器（sendcmd escape close closeall poll，带 `-HoldSec` 让命令态保持） |
| `tests\bench\chaos\lib.ps1` | 共享库（frozen 快照、bounded 进程、读数/指纹/百分位、实例自起自退、只退自己 `launched:true`） |
| `tests\bench\chaos\gen_bulk_add.py` | 批量 add 请求生成（每会话互不相同的文本） |
| `tmp\bench\e3\concurrency.json` | 无锁版并发原始数据 |
| `tmp\bench\e3\concurrency-locked.json` | 有锁版并发原始数据 |
| `tests\bench\chaos\probe.ps1` | 回归探针：overlay-84 画图核对 + silent-latency 复现（`-Case overlay,latency`） |
| `tmp\bench\e3\chaos.json` | 六类 chaos 合并结果（含 SKIPPED 原因、C2 样本、C5 归属证明） |
| `tmp\bench\e3\probe.json` | overlay 84 / latency 复现原始数据 |
| `tmp\bench\e3\regression3\` | 20/20 回归全绿证据（frozen 快照） |

## 6. 回归（2026-11-04 18:2x–18:47，窗口与 core-cad 错开）

| 回归项 | 命令 | 结果 |
|---|---|---|
| **20/20 基准** | `run_bench.ps1 -Tasks all -Product zwcad2025 -Repeat 2 -Entry client`（frozen 快照） | ✅ **20/20 success**（`tmp\bench\e3\regression3`，18:38，含 E3 全部新代码） |
| **M1 离线回归 · 有 CAD**（自己 `-Launch` 一个 2025 后跑） | `tests\run_m1.ps1 -DryRunOnly` | ✅ **PASS 9 / FAIL 0 / SKIP 2**（期望值；18:53 实测） |
| **M1 离线回归 · 无 CAD**（刻意不启实例） | 同上 | ⚠️ **PASS 7 / FAIL 0 / SKIP 4**（18:56 实测）：多出的 2 个 SKIP = `cad.expected.crosscheck`（拿不到 `result.expected`）与 `cad.section.dryrun`（`-DryRun` 仍先附 CAD → `NO_INSTANCE`）。**"有 CAD 9/0/2、无 CAD 7/0/4" 是预期差异，不是回归**；无 CAD 下若出现 FAIL 才是问题 |
| **overlay 84 + cross_app_diff** | `tests\bench\chaos\probe.ps1 -Case overlay` + `python tests\cross_app_diff.py` | ✅ overlay：`section exit=0, expected.total=84, verify.ok=true, readCount=84`；cross_app_diff：归档三产品两两 **ALL MATCH** + 本次新画 2025 vs 归档 zwcad16 **MATCH** |

**窗口绕行记录**：回归首跑/次跑成批"首步失败"根因分别是 core-cad 归零时杀掉了我的实例、以及 E3 严格 `-Doc` FullName 解析 + 残留实例导致 `{scratch}` `DOC_NOT_FOUND`；恢复窗口后 20/20 全绿，非产品回归。教训：跑回归前必须确认目标产品族 0 残留。

## 7. 剩余风险 / 下一步

1. **C1/C2 注入手段受限**：需要 core-cad 提供"可注入的忙碌/污染构造方法"（例如 `entity` 自带 `-FaultInject busy|pollute` 测试开关，或给出 ZWCAD 2025 上能真正进入命令态/污染态的操作序列）。在此之前两类只能记 SKIPPED。
2. **p50 <400 ms 未达**：见 §2；真正的优化点是 daemon 单请求串行与每命令固定开销（core-cad 正在拆 `stability` 分解）。
3. **20.2 s 零告警**（§4.2）：需要产品侧补阈值告警，否则"卡住但成功"仍会伤体验。
4. **C4 之后的产品语义**：`DOC_GONE` 已按预期返回，但"文档被关后重新 open + 重写"是否要自动恢复由产品定义（当前由 agent 显式 open，行为正确）。

## 8. E3 目标达成情况（最终验收口径）

| 目标块 | 结果 |
|---|---|
| 文档级锁 / 排队 | ✅ 跨进程命名互斥量生效；并发 3 会话零丢失（B shape 50/50；A shape 30/30） |
| 忙态自愈 | ✅ 能力已具备（`BUSY_TIMEOUT`/`CMD_STATE_POLLUTED`/`PROMPT_CLEARED` + `stability` 分解）；**C1/C2 因注入手段受限未能实测**（见 §4，如实不计 PASS） |
| 写前重锚定 / 外部变更 | ✅ `DOC_GONE`（C4 实测）、`DOC_CHANGED_OUTSIDE`、`ANCHOR_RELOCATED`（core-cad 自测 + 契约） |
| 挂死检测 / 恢复 | ✅ `doctor -Deep` + `serve -Action recover/reset`（core-cad 自测）；**C5 daemon 被杀本报告实测自愈** |
| 六类 chaos | **4/6 MATCHED**（C3/C4/C5/C6）+ **2 SKIPPED**（C1/C2 注入限制，不计 PASS） |
| 并发零丢失 | ✅ A 30/30、B 50/50、C6 18/18、C5 200/200（无半成品） |
| 并发每命令 p50 <400 ms（shape A） | **❌ 未稳定达标（独立复跑结论：否）**。ZWCAD 2025：两次独立复跑两轮 = (363, 458) / (439, 457) ms —— round2 均 >400；p95 仍 3.9 s（长尾未压掉）；顺序基线 301 ms（说明并发没有比顺序更慢，不是假优化）。Classic 16 对照：我的 harness 385/414（1 轮过 1 轮超），core-cad 自己的 harness 333/347（两轮过）。**优化本身有效**（751 → 363–458，约 40–50%），但"两轮都 <400"在 2025 上未达成；core-cad 的 333/347 是 Classic 16 + 他的 harness，不是 2025 上的结论 |
| 并发 p50 · fresh 3 次中位（最终口径） | **audit-dev 独立 harness：2025 453 / Classic 424 → 均 >400（未达标）**；core-cad 同口径：2025 380.5 / Classic 326（达标）。两套 harness 有 +70~90 ms 系统差，需先对齐测量口径；详见 §8.1a |

### 8.1a 统一 warmup 协议 + fresh 实例复验（task-22，最终口径）

**协议（lead 定稿）**：每产品 **3 次独立 fresh 跑**；每次跑 = 每轮新 CAD 实例 + 新私有文档 + 新 daemon；每会话 **1 条 warmup + 10 条采样**（warmup 不计）；3 会话并发；判 **hot p50 中位数 <400 ms**；必须报 min/max/逐次值。

| 产品 | 来源 | 逐次 hot p50 | 中位 | min / max | 判定 |
|---|---|---|---|---|---|
| **ZWCAD 2025** | **audit-dev 独立 harness**（3 次 fresh，每次 1 轮 30 采样） | **355 / 453 / 531** | **453** | 355 / 531 | **❌ 未达标** |
| ZWCAD 2025 | core-cad 同口径（3 次 fresh × 每轮取 hot） | 319/295、395/389、372/455 | 380.5（口径B 392） | 295 / 455 | ✅（他的 harness） |
| **ZWCAD Classic 16** | **audit-dev 独立 harness** | **403 / 445**（第 3 次因他人进程干扰无效，已重跑丢 1 次） | **424** | 403 / 445 | **❌ 未达标** |
| ZWCAD Classic 16 | core-cad 同口径 | 303/419、306/337、315/381 | 326（口径B 348） | 303 / 419 | ✅（他的 harness） |

**两套 harness 存在 +70~90 ms 系统差**（2025：我 453 中位 vs 他 380；Classic：我 424 vs 他 326），且我的 p95 明显更高（2025 p95 4.6–4.9 s，他 0.5–0.7 s）。已知差异点：① 我的 worker 是独立 `powershell` 进程、每条命令都经 `Invoke-Cad` 全包装（含 JSON 解析/写盘）；② 他的会话脚本内直接 `& CadClient.exe`；③ 跑次间机器负载/其他 agent 干扰（我 2025 的第 3 次 435、Classic 第 1 次 seq 全错均为干扰迹象）。
**结论（不粉饰）**：按我这套独立 harness，"每产品 3 次 fresh 中位 <400" **未达成**（2025 453、Classic 424，均超 400）；core-cad 的正式协议数字为 2025 380.5 / Classic 326（达标）。**两套 harness 必须先对齐测量口径（同一会话脚本/同一计时点）才能给出唯一结论**——在未对齐前，E3 的 p50 项口径应记为"**未稳定达标（独立 harness 复跑 453；对方 harness 380）**"。

**实体数无关性（O(1) 证据，本机 2025 冻结快照）**：顺序 1 warmup + 10 采样单实体 `entity` 写入，不同实体数的图：

| 图内实体数 | p50 (ms) | p95 (ms) | helper 平均 (ms) | bodyMs 平均 |
|---|---|---|---|---|
| 0 | 195 | 243 | 140.7 | 114.1 |
| 20 | 180 | 201 | 135.6 | 111 |
| 50 | 200 | 254 | 160.1 | 131.1 |
| 100 | 212 | 243 | 170.5 | 143.7 |
| 200 | 226 | 262 | 177.9 | 148.4 |
| 500 | 245 | 284 | 201.3 | 169.2 |
| **624（真实示例水系图，只读副本）** | **334** | 457 | 271.6 | 219.7 |

→ N=0→500 的 p50 只从 195 涨到 245 ms（**O(1)，非 O(N)**）；**624 实体真实示例水系图 p50=334 ms**（仍 <400），`attachMs=0`、`fingerprintMs=0`（缓存生效）。结论：**真实大图（数百~千级实体）单命令仍在可接受量级，E3 的 O(N) 担忧不成立**；大图慢的是 `read/section` 的几何量，不是单实体写。

### 8.1b 安静窗口门禁 + 两套 harness 同场对照（task-22 收口，进行中）

**为什么加门禁**：本机噪声源已定性为**真实用户负载**（用户正在 WPS 里编辑 Excel `八字脑、塔城库容曲线.xls`；一次孤立的 `ZwHelp` 进程吃过 94 s CPU），叠加其它 agent 的 CAD/daemon → 单命令 p50 抖动 ±100–200 ms、p95 偶发 3.6–4.9 s。**带噪声跑出的延迟数字不是证据**。

**门禁**（`tests/bench/chaos/quiet-gate.ps1`，库函数 `Get-QuietWindowState`/`Assert-QuietWindow`，已接入 `shape_a.ps1`、`scaling.ps1`、`compare-harnesses.ps1`）：
- ① 无其它 CAD/SolidWorks 等 CAD 实例 / helper daemon（自己的除外）；② **无 `wps`/`et`/`winword`/`excel`/`wpp` 进程（硬禁止，不因阈值放宽而放行）**；③ CPU <25% 且空闲内存 **>2 GB**（lead 2026-11-04 决定从 4 GB 放宽：本机 15.7 GB 总内存被 WPS 常态占 12–13 GB，4 GB 在本机等于永久拒绝；2.4 GB 空闲跑 200 MB CAD 基准不会分页）；
- 任一不满足 → 打印 `QUIET_WINDOW_REQUIRED`（JSON 含 reasons/metrics）并 **exit 4，拒绝测量**；兜底 `-ForceLoadNote` 模式可强制跑一次但证据标 `environment:"load"`、**只作参考不进达标判定**。
- 实测（20:18，用户正在用 WPS）：`{"verdict":"QUIET_WINDOW_REQUIRED","reasons":["office app running: wps#14816,wps#16712,wps#36344","free RAM 2.9 GB < 4 GB"],"metrics":{"cpuPercent":8.8,"freeMemGB":2.9}}` → 门禁按设计拦下。

**两套 harness 同场对照**（`tests/bench/chaos/compare-harnesses.ps1`）：同一安静窗口内，每产品各跑 3 次 fresh：
- A = core-cad 计时点：`frozen-task21\tmp\shapeA_v2.ps1 -Product <p> -Rounds 2 -Sessions 3 -Writes 10 -Fresh`（他每轮 1 warmup+10 采样、每轮 fresh）；
- B = audit 计时点：`tests\bench\chaos\shape_a.ps1 -Runs 1 -Mode continuous -ProgId <p>`（每次调用 = 一次 fresh 测量，1 warmup+10 采样）。
- 输出：每次每轮 hot p50/p95、两套的中位/min/max、**固定差 deltaMs**；判定：**两套中位都 <400 → 达标；只有一套 → "口径敏感，未稳定达标"**（不挑好轮）。

| 产品 | A（core-cad harness）中位 [min,max] | B（audit harness）中位 [min,max] | 差 delta | 两套都 <400 |
|---|---|---|---|---|
| ZWCAD 2025 | 待填 | 待填 | 待填 | 待填 |
| ZWCAD Classic 16 | 待填 | 待填 | 待填 | 待填 |

**测量卫生规则（后人必读）**：测量期间禁止改 `scripts\helper\*.cs` / `scripts\cad.ps1` / `bin\CadClient.exe`（helper hash 变 → `CLIENT_FAILED: helper binary missing`，整轮作废）；用 **frozen 快照**（复制 scripts/helper/client 到私有目录 + 对副本 ngen）；**他人进程/daemon 干扰会直接毁掉一次测量**（本任务 2025/Classic 各浪费 1 轮），跑前确认产品窗口空闲、`serve -Action status`。

### 8.1 shape A 独立复跑明细（task-20）

| 项 | ZWCAD 2025（audit-dev 独立） | Classic 16（对照） |
|---|---|---|
| harness | `tests\bench\chaos\shape_a.ps1 -Mode continuous`（3 个独立会话进程、每会话 10 条单实体 `entity`，私有夹具副本） | 同 harness（`-ProgId ZWCAD.Application.16`） |
| 两轮 p50 | 363 / **458**（另一独立复跑 439 / **457**） | 385 / **414**；core-cad harness 333 / 347 |
| 两轮 p95 | 3,865 / 3,923 | 564 / 644；core-cad harness 576 / 451 |
| errors / 零丢失 / 重复 handle | 0 / 30/30（两轮均）/ 0 | 0 / 30/30 / 0 |
| 顺序基线 p50 | **301 ms** | 197 ms；core-cad 205 ms |
| 结论 | **未达标（否）**：两轮都 <400 未达成；p95 长尾仍在 ~3.9 s | 边界：1 轮过 1 轮超；core-cad 自测两轮过 |

**判读**：① 优化真实（751 → 363–458，顺序 303 → 301/197）；② 2025 比 Classic 16 单命令仍慢约 60–100 ms（与 §2 中 2025/Classic 的 section 5× 差异同一量级），且 2025 的 p95 长尾明显（~3.9 s vs ~0.6 s），是"两轮都 <400"未达成的主因；③ 若口径必须是 **2025** 上的 <400，还需再省 ~60–160 ms 并压 long tail（E4+ client 常驻/去 PowerShell 会话）；若接受 **Classic 16** 为该口径载体，则 core-cad 的 333/347 可判达标，但需在文档里写明产品。证据：`tmp\bench\e3\latency-opt\{independent-run.json,zwcad2025\*,zwcad16\*}`。
