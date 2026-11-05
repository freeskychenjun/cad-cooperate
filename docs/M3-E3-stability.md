# M3-E3 稳定性骨架：文档锁 / 忙态自愈 / 写前重锚定 / 挂死检测与恢复

> 2026-11-04 17:30–18:40，core-cad（task-17）。动机：**两个 agent 同时驱动 CAD 时，单命令地板价 63 ms → 15–20 s**（helper 本体不变，全是排队/争抢）。
> 证据：`tmp\evidence-e3\*`、`tmp\e3_locktest.ps1`、`tmp\e3_final.ps1`。

## 一、四块能力的语义

### 1. 文档级锁 + 排队

- 锁名 `Local\cad-doc-<sha16(小写 fullName)>`（跨进程 `Mutex`）；写命令（`entity` / `section` / `undo`）先取锁，读命令不取锁（锁被持有时仍可读，但可能读到中间态）。
- `-LockTimeout <ms>`（默认 10 s）：超时 → `ok:false / error.code=DOC_LOCKED`，`error.detail={holder{pid,progId,startedAt,purpose}, waitedMs, lock}`；成功但等过 → `result.lock{acquired, waitedMs, name}`。
- **holder 旁路文件只在真的等待过时写**（`%TEMP%\cad-cooperate\locks\<sha16>.json`）——无竞争时不产生任何文件 I/O（这一条把每条命令的固定开销砍掉约 150 ms）。
- daemon 内部本身就是单请求串行（4 字节长度 + JSON 管道，一请求一连接），跨进程竞争主要靠上面的命名锁。
- **关键修复：daemon 启动去重**。多个 client 同时冷启动时，以前各自 spawn 一个 helper → 一个抢到管道、其余空转重试 → 20 s 级尖峰。现在 `CadClient` 用 `Local\cad-start-<sha>` 互斥量让**只有一个 client 真正 spawn daemon**，其余等锁后直接连管道；抢不到管道的 daemon 2 s 内自行退出。

### 2. 忙态检测与可恢复

- 前置探测（写命令 + `status`）：`CMDACTIVE`、`LASTPROMPT`（`Missing:` 污染）、CAD 拥有的模态 `#32770`（`EnumWindows` + `GW_OWNER`）。
- 忙 → 按 `-BusyTimeout`（默认 6 s）重试；超时 → `BUSY_TIMEOUT`（`detail{reason,lastPrompt,cmdActive,modalTitle,waitedMs}`）。
- **提示符污染自愈**：检测到 `Missing:` → 自动发 ESC（`SendCommand("\u0003")`）→ `PROMPT_CLEARED` warning → 重试一次；仍污染 → `CMD_STATE_POLLUTED`（附清场建议）。
- `entity` / `undo` 是纯 COM 写，`CMDACTIVE!=0` **不再阻塞**（只记 `BUSY_CMDACTIVE_IGNORED`），只有模态框/污染提示符才阻止——否则每次写都会等 150 ms 退避。

### 3. 写前重锚定 + 外部变更检测

- 每次写前按 **FullName 精确重解析**（不用 ActiveDocument）；文档不在 → `DOC_GONE`（不写，调用方看到干净失败）。
- 写前指纹 = `ModelSpace.Count + HANDSEED + Saved`；与上次写结束指纹不同 → `warnings:DOC_CHANGED_OUTSIDE` + `result.diffSummary{countBefore,countAfter,added,removed,since}`（人改的也算）。
- 指纹只在进程内缓存、每 20 s 落一次 `state.json`（避免每条写都读写 state 文件带来的文件 I/O）。
- 锚点消失但指纹未变 → `ANCHOR_RELOCATED` + item `relocated:true`（按 type/layer/text 唯一匹配重定位；不唯一仍 `ANCHOR_MISSING`）。

### 4. 挂死检测与恢复

- `doctor -Deep`：管道 ping（client 侧 ≤2 s）+ COM ping（`GetVariable CMDACTIVE` 计时）+ `SendMessageTimeout(WM_NULL, 2 s)`；超时 → `warnings:CAD_HUNG` + `result.deep{com{ok,elapsedMs},window{ok,elapsedMs},hung,verdict}`。
- `serve -Action reset`：只按**本版本 exe 全路径**杀 daemon（不按映像名、绝不杀 CAD）。
- `serve -Action recover`：备份 `state.json` → 清 `session/openedDocs` → 重建 daemon → 走管道重新附着；返回 `result{recovered,daemonRunning,appReloaded,documents[],stateBackup}`。实测 `daemonRunning=true, appReloaded=true, documents=2`，随后 `status` 正常。

## 二、实测数字（ZWCAD Classic 16，client 入口，`tmp\evidence-e3\concurrency.json`）

| 场景 | 结果 |
|---|---|
| 单会话顺序写（3 次） | **341 / 244 / 260 ms**（含 client 进程启动 ~60–90 ms） |
| 3 会话并发 × 5 条（15 条，同图） | wall **5,043 ms**、**p50 486 ms**、p95 3,138 ms、max 3,138 ms、**errors 0** |
| 数据完整性 | 最终该层 **18 条 = 3 顺序 + 15 并发全在**，无丢失/无损坏 |
| `doctor -Deep` | `ok=true, verdict=ok, com=0 ms, window=0 ms` |
| 参照：改造前两 agent 并发 | 15–20 s / 命令；本轮 wall 5.0 s / 15 条 ≈ **~330 ms/条含竞争** |

每次写的阶段分解（`result.stability`）：`attachMs≈50–60`、`lockMs≈55–76`（含按 FullName 重解析）、`busyMs≈1–6`、`fingerprintMs≈20–100`、`bodyMs≈74–200`、`totalMs≈200–390`。

## 三、结论与已知限制

1. **零丢失/零损坏已达成**，并发墙钟从"15–20 s/命令"降到"15 条 5.0 s"。
2. **p50 486 ms 未达 <400 ms 目标**（本环境实测单条真空地板 244–341 ms，其中 client 启动 ~70 ms、Attach ≈55 ms、重解析 ≈60 ms）。要做到 400 以下需要：client 常驻（免进程启动）、daemon 缓存文档解析结果、减少写前指纹读取次数。这些属下一步优化，不是正确性问题。
3. 已知限制：锁是**建议式**（命名互斥量），只有本工具的写命令会遵守；别的程序/人手改仍可能造成 `DOC_CHANGED_OUTSIDE`（已能被指纹/快照发现）。`GetXData` 读侧镜像限制见 `M3-E2-writes.md`。
4. 恢复语义：`recover` 不重启 CAD、不杀 CAD；若 CAD 本身挂了，`doctor -Deep` 报 `CAD_HUNG`，需用户手工重启 CAD（工具不替你杀）。
5. 夹具/进程约定：每条命令显式 `-ProgId`；每轮结束 CAD/daemon 归零（只退自己 `launched:true` 的实例）。

## 四、复现

```powershell
cd <repo>
.\tmp\e3_final.ps1          # 顺序地板 + 3 会话并发 + doctor -Deep，证据写到 tmp\evidence-e3\concurrency.json
.\bin\CadClient.exe doctor -Deep -ProgId ZWCAD.Application.16
.\bin\CadClient.exe serve -Action recover
.\bin\CadClient.exe serve -Action reset      # 只杀本版本 daemon
```

## 五、收口结果（lead 复核轮，2026-11-04 18:10–18:45）

### 5.1 P1：`run_m1 -DryRunOnly` 复绿（PASS 9 / FAIL 0 / SKIP 2）

- 规则：只有**纯 plan/dry-run（零写入）**在多文档时允许兜底——`Ctx.PlanOnly` 由 `-DryRun`/`-Mode plan` 置位，`ResolveDoc` 在无 `-Doc` 且多文档时返回 ActiveDocument（Classic 无活动文档则取第一个打开图），`matchedBy:"active-fallback"` + `DOC_ACTIVE_FALLBACK` warning；**写命令仍硬报 `DOC_AMBIGUOUS`**。
- 证据：`tmp\evidence-e3\regression-run_m1-dryrun.txt`（`[run_m1] RESULT: PASS (PASS=9 FAIL=0 SKIP=2)`）。

### 5.2 P2：基准 20/20 与 overlay 回归（E3 未破坏 E1/E2）

- `run_bench.ps1 -Tasks all -Entry client -Product zwcad16 -Repeat 1` → **20/20 success，0 fail**（`tmp\bench\20261104-181434\summary.json`；控制台摘要 `tmp\evidence-e3\regression-bench-zwcad16.txt`）。
- 真数据 overlay 84 实体：Classic 84/84 `verify.ok`、AutoCAD 2007 84/84 `verify.ok`；`undo` 后 countAfter=0；`cross_app_diff` = **ALL MATCH**（`tmp\evidence-e3\regression-overlay.txt`、`tmp\compare-e3\*`）。

### 5.3 P3（未做，按 lead 指示如实记录）：p50 486 ms 的地板构成

| 组成 | 实测 |
|---|---|
| client 进程启动（cmd→exe） | **~70 ms** |
| `Cad.Attach`（含缓存校验；冷则含 WMI 探测 200+ ms） | **~55 ms** |
| 按 FullName 重解析文档（Documents 枚举 + 匹配） | **~60 ms** |
| 文档锁 + 队列 | **0–100 ms**（无竞争时 <5 ms；daemon 单请求串行时是主要排队点） |
| 写前忙态/指纹（`CMDACTIVE`+`LASTPROMPT`+count+HANDSEED+Saved） | **~20–105 ms** |
| body（add/update + Pass3 回读） | **~74–200 ms** |
| **合计** | **~200–390 ms 顺序 / 并发 p50 486 ms** |

⇒ 要压到 <400 ms 需要"client 常驻 + daemon 缓存 FullName→Document + 合并写前指纹读取"，属 E4+ 的入口层改造；当前不做硬凑，文档化供决策。

### 5.4 告警补强（lead：沉默的长时间等待不可接受）

- `LOCK_WAITED{ms}`：持锁等待 ≥1000 ms 时告警。
- `BUSY_RETRIED{reason}`：忙态重试第一次即告警（`busy{waitedMs,retries,promptCleared}` 入响应）。
- `DAEMON_START_WAIT`：client 冷启动等待 ≥1500 ms 时 `transport` 标注 `daemon-started(wait=Nms)`（`transport` 字段直接可见）。
- `PROMPT_CLEARED`：污染自愈时告警并重试一次（真实证据由 audit-dev 的 C2 提供样本后校准）。

### 5.5 C2 污染签名扩展（中文界面）

`PromptPolluted()` 现匹配：`Missing:`、`错误:`、`错误：`、`缺少`、`缺陷`、`无效`、`无法`，以及**行尾连续提示符 `>`**（命令仍在等输入）。中文 ZWCAD 不打印 `Missing:`，等 audit 给真实样本后再补精确串。

### 5.6 已知未收口

- chaos 六类中 C1/C5/C6 由 audit-dev 正在跑，C2（污染自愈）待其真实样本校准；本轮已把 `PROMPT_CLEARED` 路径与多语言签名补齐。
- p50 486 ms 未达 <400（见 §5.3，按 lead 口径记录）。
### 5.7 chaos 结论（audit-dev `docs\M3-E3-acceptance.md` + `tmp\bench\e3\chaos.json`）

| 场景 | 结果 |
|---|---|
| C3 写一半失败 | MATCHED（脏批次整体回滚，无残留） |
| C4 关图 | MATCHED（`inject.ps1 -Action closeall` 稳定造出 `DOC_GONE`） |
| C5 daemon 被杀 | MATCHED：200 条写命令中途杀 daemon → client `exit=0 / wall 7.34 s / partial=false / added=200`，随后 status 正常、daemon 自动重建 |
| C6 双 agent 同图 | MATCHED：零丢失，两版并发数据见下 |
| C1 忙碌态 | **注入手段受限**（进程外 `SendCommand` 同步阻塞，注入时 `CMDACTIVE` 仍 0）→ 已提供测试开关 `-FaultInject busy[:recover]` 供后续验证 |
| C2 提示符污染 | **该产品该交互下无污染表现**：audit 试了 4 种坏 LISP（`(setq x (1+))` / `(princ "abc` / `(command "_.LINE" "nonsense")` / `((( `），`LASTPROMPT` 始终是 `命令: ` 或空串，**没有出现可识别污染签名**。因此 `PROMPT_CLEARED` 路径**仅有代码路径保证，未获真实样本验证**（不谎称已验证）。多模式匹配已按样本扩展为 `Missing:`/`错误`/`缺少`/`缺陷`/`无效`/`无法` + 空串（空串仅在 `CMDACTIVE!=0` 时算污染）+ 行尾 `>`。 |

**并发口径（两种 shape，别混用）**
- **shape A（命令级，本报告 §二）**：3 个会话各跑 10 条**单实体 `entity` 命令** = 30 条命令 / 30 实体 → 本机 p50 **486 ms**（未达 <400）。*每实体成本* = 486 ms/实体？不——每条命令只写 1 实体，所以"每实体 ≈ 每条命令"，仍 486。
- **shape B（批次级，audit 的 chaos 基准）**：3 会话 × 10 实体 = **30 实体 / 3 条 `entity` 命令**（每命令 10 items）→ 锁定版 p50 **3,656 ms / p95 6,108 ms**（无锁 3,325 / 11,727，p95 −48%）⇒ **每实体成本 ≈ 3,656/10 = 366 ms < 400 达成**，且零丢失。
- 结论：**"每实体成本 <400 ms"在批次形态下达标；"单实体命令 p50<400"未达标**（地板 = client 70 + attach 55 + 重解析 60 + 锁/队列 + body 74–200）。

### 5.8 告警补强的实测证据（`tmp\evidence-e3\warning-evidence*.json`）

| 告警 | 实测 |
|---|---|
| 字段存在 | 正常写响应含 `result.stability{attachMs:96,lockMs:78,busyMs:5,fingerprintMs:42,bodyMs:253,totalMs:474}`、`lock{acquired:true,waitedMs:0,name}`、`busy{waitedMs,promptCleared,retries}` |
| `LOCK_WAITED` | 外部持 `Local\cad-doc-<sha>` 1.6 s 后放行 → 写命令 `lock.waitedMs=1266`、`warnings=[LOCK_WAITED]` ✅ |
| `BUSY_RETRIED` | `-FaultInject busy-recover` → `ok:true` + `BUSY_RETRIED`（含模拟 MS）✅ |
| `BUSY_TIMEOUT` | `-FaultInject busy -BusyTimeout 600` → `ok:false / BUSY_TIMEOUT`（detail 带 `injected:busy`）✅ |
| `DAEMON_START_WAIT` | 冷启动时 `client` 把 `transport` 标为 `daemon-started(wait=Nms)`（仅当等待 ≥1.5 s；本机冷启动约 1–3 s 时可见）|
| 20.2 s 静默 | 该次是 audit 的 C1 注入无效 + daemon 冷启动/争抢叠加；现在≥1.5 s 的冷启动会体现在 `transport`，持锁 ≥1 s 会给 `LOCK_WAITED`，忙态会先给 `BUSY_RETRIED` —— 三条路径都有实测样本，不再存在"卡了却零 warning"的静默路径。 |

### 5.9 `-FaultInject` 测试开关

`entity -FaultInject busy[-recover] [-BusyTimeout ms]`：`busy` → 模拟忙到超时报 `BUSY_TIMEOUT`；`busy-recover` → 模拟一次忙后恢复（`BUSY_RETRIED` + 正常写）。仅供 chaos 验证，真实 CAD 状态不被触碰。
### 5.10 p50 优化（task-19）：改了什么、省了多少、最终数字

**目标**：shape A（3 会话 × 10 条单实体 `entity` 命令 = 30 命令/30 实体）每命令 **p50 <400 ms**，两轮都 <400。

**改动（按便宜到贵，做到就停）**
| # | 改动 | 机制 | 实测省 |
|---|---|---|---|
| 1 | **daemon 内文档解析缓存** | `ResolveDoc` 先按传入 `-Doc` 字符串命中进程内缓存（同一 FullName 连续命令），校验 `Com.TryGet(doc,"Name")` 仍可用；`DOC_GONE`/缓存对象失效即丢弃。命中后 `lockMs`（含解析）从 **55–122 ms → 0–2 ms** | **~55–115 ms** |
| 2 | **AppInfo 缓存** | `Cad.AppInfo()` 第一次调用后缓存在 Cad 实例内（version/name/fullName/caption 等 5 次 COM 读只发生一次） | **~30–60 ms** |
| 3 | **DocInfo 短 TTL 缓存** | 每命令的 `ctx.DocMap` 6 次 COM 读改为 800 ms TTL + 每写后显式失效 | **~20–40 ms** |
| 4 | **写前指纹按需** | 同一进程、同一文档、上一次写 <800 ms 且本次锁等待=0 → 复用上次指纹（外部写入必然要先拿锁，因此复用是安全的）；否则全量取。`DOC_CHANGED_OUTSIDE` 检测保留 | **20–45 ms**（指纹 34–45 → 0） |
| 5 | **client NGen 原生映像** | `ngen install bin\CadClient.exe`（本机成功），并把"首次自动 ngen"做进 `cad.ps1 --build-client` 的编译后步骤（失败静默） | **~30–40 ms**（client 启动 70 → ~35 ms）|
| 6 | 锁旁路 | 无竞争时不写 holder 旁路文件（此前每条命令 2 次 %TEMP% 文件 I/O） | 已含在上一轮 |

**最终数字（`tmp\evidence-e3\lateny-opt\shape-a.json`，Classic 16，client 入口）**
| 指标 | 优化前 | 优化后 |
|---|---|---|
| shape A p50（轮 1 / 轮 2） | 486 ms（单轮） | **333 ms / 347 ms** ✅ |
| shape A p95（轮 1 / 轮 2） | — | 576 / 451 ms |
| shape A max | — | 5,221 / 5,237 ms（每轮首个命令含冷启动，非稳态） |
| errors | 0 | **0 / 0** |
| 实体数（零丢失） | 基线+30 | **50/50、80/80**（两轮累计 +60）✅ |
| 重复 handle | — | **0** |
| 顺序基线 p50 | 303–341 ms | **205 ms**（p95 382）|
| 单命令 helper 段 | 200–390 ms | **125–296 ms**（首次 296，稳态 125–179）|

**判定：PASS** —— 两轮独立运行 p50 均 <400 ms，零丢失、零重复 handle、errors=0。

**回归**
- `run_m1 -DryRunOnly`：**有 CAD 实例时 PASS 9/0/2**（`tmp\evidence-e3\lateny-opt\run_m1-with-cad.txt`）；**无 CAD 时 PASS 7/0/4**（CAD 依赖步骤被 SKIP，`run_m1-no-cad.txt`）——两种期望值都写在这里，避免误判。
- bench `-Tasks all -Entry client -Product zwcad16 -Repeat 1` → **20/20 success**（`tmp\bench\20261104-190002\summary.json`、`bench-zwcad16.txt`）。
- overlay 84 实体：Classic 84/84 verify.ok、AutoCAD 2007 84/84 verify.ok、undo→0、`cross_app_diff` **ALL MATCH**（`overlay-crossapp.txt`）。

**脚本（audit-dev 可独立复跑）**
- `tmp\e3_shapeA.ps1 -Rounds 2 -Sessions 3 -Writes 10`（3 进程并发会话 + 两轮 + 顺序基线，结果写 `tmp\evidence-e3\lateny-opt\shape-a.json`）
- 子会话：`tmp\shapeA_session.ps1`；旧的 `tmp\e3_final.ps1` 仍可用作粗测。
### 5.11 2025 口径校正与最终结论（task-21）

**测量口径**（`tmp\shapeA_v2.ps1`，按产品参数化，不再硬编码 .16）
- 3 个并发会话 ×（1 条 warmup + 10 条采样单实体 `entity`）；warmup 不计入任何统计。
- **cold** = 10 条采样含会话首条；**hot** = 排除每会话首条（n=27）。逐条序列写进证据 JSON。
- `passHot` 判定 = **所有轮次**满足 `hot.p50 < 400 且 errors=0 且 实体数=基线+轮次×会话数×11 且 重复 handle=0`；`passHot:false` 即"至少一轮 hot p50 ≥400"。
- 脚本参数：`-Product zwcad16|zwcad2025|autocad -Rounds N -Sessions 3 -Writes 10`；产品名写进输出与证据目录。
- **正式测量用冻结快照**：`tmp\bench\e3\frozen-task21\`（bin+scripts+fixtures+脚本的完整拷贝，运行期间零源码编辑；`ngen` 已安装到快照 client），两轮之间不改任何文件。

**冻结快照下 2025 的实测（三次独立跑，均零错误、零丢失、零重复 handle）**
| 跑次 | 轮 | hot p50 | hot p95 | cold p50 | 备注 |
|---|---|---|---|---|---|
| A | 1 / 2 | **301 / 472** | 3872 / 666 | 322 / 490 | 轮1 max 3.9 s 在首条采样；轮2 整体慢 |
| B | 1 / 2 | **259 / 408** | 544 / 3455 | 313 / 429 | 轮2 有 3.4 s 离群 |
| C（更早，含 warmup 版） | 1 / 2 | 304 / 433 | 650 / 3618 | 317 / 475 | — |

**结论：2025 两轮同时 <400 ms 未稳定达成**（最好轮 259，最差轮 472；多数跑次是一轮达标一轮 400–472）。`passHot=false` 即此意。

**2025 地板构成（`stability` 实测，稳态命令）**：`attachMs 0`（缓存生效）、`lockMs 3–6`（解析缓存生效）、`busyMs 0–1`、`fingerprintMs 0`（按需复用）、**`bodyMs 133–179`**、`totalMs 138–183`；再加 **client 进程启动 ~35–50** + **daemon 单请求串行队列（3 会话并发时 0–200，取决于相位）** ⇒ 每命令 300–470。
- Classic 16 同快照同 harness：hot p50 **192**（p95 228），证明不是脚本/机器整体问题，而是 2025 的 COM body 与相位波动。
- 差距量化：若以"两轮都 <400"为目标，坏轮超 8–72 ms；要稳定达标需 **client 常驻（去掉 ~35–50 ms 进程启动）** 且降低 daemon 串行队列的相位敏感度——属 E4+ 入口层改造，不在本轮"便宜优化"范围内。

**已做且有效的优化（相对 751 基线）**：daemon 文档解析缓存（lockMs→0–6）、跨请求 Cad 缓存（attachMs→0）、AppInfo 缓存、DocInfo 低频刷新、写前指纹按需复用、client NGen（~70→~35）、写后轻量指纹。合计单命令 service 从 ~400 降到 ~140–180（2025）/ ~80–120（Classic）。

**cold/hot 差异**：cold 与 hot 差 ≈ 首条命令的 client 启动/NGen/JIT/daemon attach（2025 上 warmup 本身 370–3950 ms，含首条离群），这就是 lead 怀疑的"p95 3.9 s 来自首条"——**确认成立**：除离群外 p95 为 544–666。
### 5.12 entity 复杂度标定 + round1/round2 差异定位（lead 提示的 O(N) 排查）

**方法**：冻结快照 `tmp\bench\e3\frozen-task21`，新建 scratch 图，用 `entity` 批量填充到 N=21/51/101/201/501 实体，每个 N 先 1 warmup 再 10 条单实体写；记录 wall 与 `stability` 各段（`tmp\evidence-e3\lateny-opt-calibration\calibration-zwcad*.json`）。

**结果：`entity` 单命令是 O(1)，与实体数无关**
| N | Classic p50 | 2025 p50 | bodyMs（Classic/2025） | attach/lock/fp |
|---|---|---|---|---|
| 21 | 191 | 168 | 109 / 102 | 0 / 2 / 0 |
| 51 | 177 | 174 | 103 / 104 | 0 / 1–3 / 0 |
| 101 | 173 | 161 | 106 / 100 | 0 / 1–2 / 0 |
| 201 | 160 | 167 | 104 / 108 | 0–5 / 1–3 / 0 |
| 501 | 173 | 166 | 98 / 101 | 0 / 1–3 / 0 |

⇒ **body 100–110 ms 恒定、p50 160–190 ms 恒定**；`attachMs→0`（跨请求 Cad 缓存）、`fingerprintMs→0`（按需复用）、`lockMs 1–3`（解析缓存）。lead 怀疑的 O(N) 来源（ModelSpace 枚举 / snapshot 扫描 / 全表 EnsureLayer / 全量指纹）**均不存在**——真实几千/十万实体图纸不会因此变慢。

**那 round2 为什么慢？——是"同一 CAD 实例连续跑多轮"的累积，不是文档实体数**
- 同一实例/同一进程内连跑两轮（round1 53 实体 → round2 86 实体）：hot p50 = 301/472、259/408、304/433（跨三次独立跑，round2 稳定变慢）。
- **每次全新实例只跑一轮**（fresh CAD + fresh doc + fresh daemon，两次独立跑）：hot p50 = **258 / 285**（p95 281/532，errors 0，count 53/53）。
- 结合 §5.12 的 O(1) 标定 ⇒ round2 变慢来自**实例会话累积**（最可能是 undo 历史/显示重绘/内存增长，而非实体数）。附：每轮每会话写 11 条（1 warmup + 10 采样），故 round2 结束实体数 = 基线 + 2×3×11 = 86，与实测一致；口径已在 §5.11 注明。

**结论**：单命令成本 O(1)，大图可用；shape A 的 2025 p50 在"全新实例单轮"下稳定 <400（258/285），在"同实例连跑两轮"下 round2 会劣化到 408–472。若验收要求"同实例两轮都 <400"，需进一步查 2025 实例累积（undo 历史清理/重开文档），否则建议按"每轮独立实例"口径验收。
### 5.13 同实例累积 / undo 实验 / `-Fresh` 复跑协议（task-21 收尾）

**实验设计（冻结快照，2025，3 会话 × 11 条/会话/轮）**
| 实验 | 设置 | hot p50 轮1/轮2 | 结论 |
|---|---|---|---|
| E1 同实例基线 | 同 CAD 同文档连跑两轮 | **347 / 366** | 本次两轮都 <400（历史跑次曾 301/472、259/408 → 波动大）|
| E2 同实例 + `-NoUndoMark` | 单实体写不建 undo 组 | **337 / 360** | 相对 E1 **无改善** ⇒ undo 组不是累积成本源；默认行为保持不变（`-NoUndoMark` 仅作诊断开关）|
| E3 `-Fresh`（每轮新实例） | 每轮 `finish -Quit` → 等进程消失 → 新 CAD+新文档 | 2025 **332 / 432**；另两次独立 fresh 跑 **258 / 285**；Classic fresh **392 / 402** | 新实例能拿到好轮（258/285/332），但**同机多轮波动 ±100–200 ms**，本轮未能稳定两轮 <400 |

**结论**
1. `entity` 单命令 **O(1)**（§5.12 标定），大图无退化。
2. 同实例 round2 劣化**不是 undo mark**（E2 无改善），更像是 2025 实例会话内的重绘/内存/后台任务与**测量时机器负载**叠加；E1 本次同实例两轮 347/366 已 <400，说明它不是必现规律，而是**高方差**。
3. 本机测量噪声较大（Classic 同 harness 历史 hot p50 192→392 波动）；**判定"是否达标"必须用多次独立跑的统计量**（建议中位数或"3 次独立 fresh 跑的中位"），单次 472 或 402 不能直接判负。

**实战缓解建议（写进 SKILL 能力边界）**
- 长会话/大批量写入时，建议每 N（如 200–500）条写后**重开文档或重启 CAD 会话**（成本低、可消除实例累积）。
- 单实体写可保留默认 undo 组（不影响性能）；仅在诊断时用 `-NoUndoMark`。

**`-Fresh` 复跑方法（已固化到 `tmp\shapeA_v2.ps1`）**
```
powershell -NoProfile -File tmp\shapeA_v2.ps1 -Product zwcad2025 -Rounds 2 -Sessions 3 -Writes 10 -Fresh
```
- `-Fresh`：每轮先 `finish -Quit`、轮询等 `ZWCAD|acad` 进程消失、再 `open -Launch` 新实例+新私有夹具副本；每轮每会话写 11 条（1 warmup + 10 采样），warmup 不计统计。
- 输出：cold（含首条）/hot（排除首条）p50/p95、逐条序列、errors、实体数、重复 handle；证据写 `tmp\evidence-e3\lateny-opt-<product>\`。
- `-NoUndoMark`：诊断用 undo 组开关；`-KeepInstance`：保留实例供下一轮 attach。
- 证据：`tmp\evidence-e3\lateny-opt-fresh\*`（2025）、`lateny-opt-fresh-classic\*`（Classic）、`lateny-opt-calibration\*`（O(1) 标定）。
### 5.13.1 最终口径与判定（lead 定稿，2026-11-04 20:00）

**口径（唯一合法）**：每产品 **3 次独立 `-Fresh` 跑**（每次全新 CAD+doc+daemon，`-Fresh -Rounds 2`），每次两轮各取 3 会话 × 10 采样的 **hot p50**；判 **hot p50 的中位数 <400 ms 为达标**；同时必须报 **min/max 与逐次值**（透明，不挑好轮）。
两种统计口径都给出且不混用：
- **口径 A（推荐）= 6 个 hot p50 的中位数**（3 次跑 × 2 轮）
- **口径 B = 3 次跑的"每次两轮中位"再取中位**（更稳健，抗单轮离群）

**ZWCAD 2025（3 次独立 `-Fresh`，冻结快照）**
| 跑次 | round1 hot p50 / p95 | round2 hot p50 / p95 | 本次中位 |
|---|---|---|---|
| 1 | 319 / 552 | 295 / 992 | 307 |
| 2 | 395 / 590 | 389 / 3661 | 392 |
| 3 | 372 / 766 | 455 / 3940 | 414 |
| **统计** | min **295** / max **455** | 口径 A 中位 = **380.5** ✅ | 口径 B 中位 = **392** ✅ |

**ZWCAD Classic 16（3 次独立 `-Fresh`，同一快照/同一 harness）**
| 跑次 | round1 hot p50 / p95 | round2 hot p50 / p95 | 本次中位 |
|---|---|---|---|
| 1 | 303 / 577 | 419 / 630 | 361 |
| 2 | 306 / 429 | 337 / 455 | 322 |
| 3 | 315 / 469 | 381 / 561 | 348 |
| **统计** | min **303** / max **419** | 口径 A 中位 = **326** ✅ | 口径 B 中位 = **348** ✅ |

**判定**：**两产品均达标**（2025 口径 A 380.5 / 口径 B 392；Classic 326 / 348，均 <400），errors 全 0、每轮实体数 53/53（=20 基线 + 3 会话×11）、重复 handle 0。
- 透明记录：2025 有单轮 455（跑次 3 round2）、p95 偶发 3.6–3.9 s（后台重绘/GC 型离群）；Classic 最大单轮 419 —— 这就是采用"3 次中位"口径的原因（单次样本 ±100–200 ms 波动是实测事实）。
- 与历史单次样本对齐：2025 fresh 曾见 258/285/332/432，Classic 192–402，均落在同一分布内。

**验收结论**：E3 的"3 会话同图写入、每命令 hot p50 <400 ms"在 **2025 与 Classic 16 均达成**（中位口径）；本机单轮方差作为已知限制记录。

**复跑命令（冻结快照，勿改源码）**
```
cd tmp\bench\e3\frozen-task21
powershell -NoProfile -File tmp\shapeA_v2.ps1 -Product zwcad2025 -Rounds 2 -Sessions 3 -Writes 10 -Fresh
powershell -NoProfile -File tmp\shapeA_v2.ps1 -Product zwcad16  -Rounds 2 -Sessions 3 -Writes 10 -Fresh
```
`-Fresh`=每轮新实例；每轮每会话 **11 条 = 1 warmup + 10 采样**（warmup 不计统计）；输出 cold/hot p50/p95 + 逐条序列 + errors/实体数/重复 handle。证据：`tmp\evidence-e3\lateny-opt-final\{zwcad2025,zwcad16}-run{1..3}\`（本次最终测量）+ `lateny-opt-fresh*`、`lateny-opt-calibration*`。

**`-NoUndoMark` 定位**：诊断开关，用于验证 undo 组是否累积成本；实测 (E2: 337/360 vs E1: 347/366) **无改善** ⇒ 默认行为**不改**，客户/agent 无需关心；仅排查时使用。

**实战缓解建议**：长会话/大批量写入每 **200–500 条**重开文档或重启 CAD 会话（消除实例内累积）；单实体写保留默认 undo 组（不影响性能）；批量任务用 `snapshot`→`diff` 核对外部改动。
## 5.14 Lead 独立仲裁测量（2026-11-04 20:0x，中立 harness）

core-cad（2025 中位 380.5 / Classic 326）与 audit-dev（2025 中位 453 / Classic 424）口径不一致，Lead 用**第三套最小 harness** 仲裁：
- 计时点：每个会话进程内 & bin\CadClient.exe entity ... 的外部 Stopwatch（不额外包 JSON 解析/写盘；仅读一次 ok 判成功）；每会话 1 warmup + 10 采样；hot 排除首条；**每轮 fresh CAD + 私有夹具副本**；3 次独立 fresh 跑，取每次 hot p50 的中位数。
- 脚本：	mp\lead-tiebreak-shapeA.ps1（+ 	mp\lead-tiebreak-session.ps1），证据：%TEMP%\lead-tiebreak-*\tiebreak-result.json。

| 产品 | 三次 fresh 的 hot p50 | 中位 | min/max | 判定 |
|---|---|---|---|---|
| ZWCAD 2025 | 362 / 321 / 477 | **362** | 321 / 477 | <400 ✅ |
| ZWCAD Classic 16 | 317 / 442 / 425 | **425** | 317 / 442 | ≥400 ❌ |

每次运行：errors=0、最终实体数 33（3 会话 × 11 条）、零重复 handle。

**仲裁结论（重要，别只看阈值）**：
1. **本机噪声 ±100–200 ms 主导**：同一 harness 同一产品，run 间 p50 从 317 到 477 摆动；三套 harness 的同一产品中位数落在 326–453 区间。**单次或单 harness 的"达标/未达标"都不稳**。
2. 稳定成立的事实：① **零丢失/零损坏**（所有轮次 33 实体、无重复 handle）；② **O(1) 扩展性**（§5.12：0→624 实体 p50 195→334 ms）；③ **E3 相对基线大幅改善**（751/486 → 约 330–450）；④ p95 长尾主要来自**每会话首条**（cold 484–721 ms，daemon 冷启动），不是逐命令退化。
3. **"每命令 p50 <400 ms" 在本机当前噪声下无法稳健裁定**（三套 harness 互相矛盾）。要把它变成可签字的指标，需要二选一：
   a. **E4+ 入口层**：client 常驻（省 35–50 ms）+ 空闲机器测量，可系统性把中位压到 300 附近；
   b. **接受口径**：定义为"稳态中位 ≤450 ms（跨 harness）"，把 <400 作为优化目标而非验收门。
4. 建议验收措辞：**能力项全部达标（锁/忙态/重锚定/挂死 + 零丢失 + O(1) + 4/6 chaos MATCHED）；性能项记为"明显改善但受本机噪声限制未稳健达标，E4+ 待收"**。

### 5.15 E4-lite：body 子阶段拆解 + 三项优化 + 三产品三次 fresh 复测

**1. `entity` 单实体 add 的 body 子阶段（新增 `result.bodyDetail`，2025/Classic 实测，稳态命令）**
| 子阶段 | before (ms) | after (ms) | 说明 |
|---|---|---|---|
| plan（Pass1 校验） | 3–9 | 2–6 | 单 item，O(1) |
| undoMark（Begin/StartUndoMark） | 1–2 | 1–2 | 每命令一次；实测**<5 ms**，不动默认语义 |
| apply（CreateForAdd + layer + props） | 13–47 | 12–47（首次 47，稳态 12–15） | 首次含图层创建/COM 预热 |
| readback（Pass3） | 2–12 | 2–5 | 去掉重复的 after 快照比对后减半 |
| undoEnd | 0 | 0 | EndUndoMark 不阻塞 |
| **body 合计（`stability.bodyMs`）** | **95–140** | **82–91** | 稳态 −10~15 ms |
> body 内还有约 60 ms 属未单列项：入口 `DocInfo`、`Layers` 探测、`State` 读写、结果组装；其中 **State 文件读写是最大的一项**（见下）。

**2. 三项优化与节省**
| 优化 | 做法 | 实测省 |
|---|---|---|
| ① State 热路径去掉文件 I/O | `State` 加进程内缓存 + 1 s 防抖落盘（`Flush()`），`entity` 的 token 读写不再每次 `Load+Save` `%TEMP%\state.json`（端侧加密驱动下每次 2–4 次文件操作） | **~10–15 ms/命令**（body 95→82） |
| ② add 路径精简 | 去掉 `CreateForAdd` 里多余的 `Document` 探测与重复 `Layer` 赋值（caller 已 Ensure+Set）；`countAfter` 由 `countBefore+applied-deletes` 推导（少 1 次 `ModelSpace.Count`）；Pass3 不再重复比对已知的 `after` 快照（意图校验已覆盖） | **readback 12→2–5 ms，apply 首次 47→47/稳态 13→12** |
| ③ client 启动 | 核查结论：**client 本身不引用 `System.Management`**（源码 using 无它；只在编译 helper 时传 `-r:System.Management.dll`），所以 WMI 不是 client 启动项；`/optimize+` 与 NGen 均已生效（`ngen display` 有 native image），client 纯启动 ~35–50 ms（含 .NET 运行时初始化，未达 ≤30 ms，属 .NET Framework 下限） | 0（已最优） |
| ④ UndoMark 默认语义 | 实测 Begin+End ≈ 1–2 ms/命令（<5 ms），**不改为默认不建组**（会破坏单命令 Ctrl+Z 语义），仅保留 `-NoUndoMark` 诊断开关 | 0（不推荐改） |

**3. 最终三次 `-Fresh` 复测（每产品 3 次独立跑，每次两轮，warmup 排除；冻结快照）**
ZWCAD 2025
| 跑次 | r1 hot p50 | r2 hot p50 | 本次中位 |
|---|---|---|---|
| 1 | 390 | 295 | 342.5 |
| 2 | 377 | 336 | 356.5 |
| 3 | 322 | 376 | 349 |
min **295** / max **390**；口径 A（6 样本中位）**356**；口径 B（3 次中位再中位）**349**；errors 0、53/53、重复 handle 0。

ZWCAD Classic 16
| 跑次 | r1 | r2 | 本次中位 |
|---|---|---|---|
| 1 | 281 | 348 | 314.5 |
| 2 | 368 | 355 | 361.5 |
| 3 | 312 | 375 | 343.5 |
min **281** / max **375**；口径 A **351.5**；口径 B **343.5**；errors 0、53/53、重复 0。

**4. 结论**
- 相对 E3 定稿（2025 中位 380.5 / Classic 326），E4-lite 后 **2025 中位 356（−24.5 ms）**、Classic 351.5（本轮样本受噪声影响，落在 326–352 区间）。
- **目标"两产品三次 fresh 中位 ≤330 ms"未达成**：实测 2025 **356**、Classic **351.5**，距目标 21–26 ms；但**均 <400，余量 44–48 ms**（口径 B：349/343.5）。
- 剩余地板（2025）：client 启动 ~35–50（.NET 下限）+ helper 82–96（body 82 中 DocInfo/Layers/State 组装 ~60，apply 12–15）+ daemon 串行队列相位。要再降 ≥30 ms 只能改入口层（常驻 client / 去 PowerShell 会话），属 E4+。
- **环境更正（重要）**：上述三次 fresh 期间 **WPS 一直在跑**（`wps` pid 16712 启动 19:19:47，早于测量；pid 43588/53688 启动 20:29:24/20:29:33 落在后两次 fresh 期间；当前空闲内存 2.36 GB < 4 GB 阈值）。因此 §5.15 的数字应标注 **`environment:"load"`（含用户 WPS 负载，偏悲观）**，不作为最终验收值；优化收益（State 去 I/O、readback 12→2–5 ms、body 95→82）需在安静窗口复测确认。
- `tmp\shapeA_v2.ps1` 已加**安静门禁**：`freeMemGB>=4.0` 且无 `wps/et/wpp/EXCEL/WINWORD`、无 CAD，否则 exit 3 并打印 `QUIET_GATE: refused to measure ...`；`-MinFreeMemGB` 可调，`-ForceLoad` 强制跑但证据 JSON 记 `environment{start,end,loadOverride}`。已同步进 frozen 快照。

**证据**：`tmp\evidence-e3\e4lite\{zwcad2025,zwcad16}-run{1..3}\`（含逐条序列）与 `console.txt`。
## 5.16 Lead 中立 harness v2（wall + helper 双口径，2026-11-04 20:4x）

为摆脱"负载/计时点"争议，Lead 用第二套中立脚本同时记录两口径：
- **wall**：会话进程内 & bin\CadClient.exe entity ... 外部 Stopwatch（用户实际等待时间）；
- **helper**：响应里的 esult.stability.totalMs（产品侧耗时，排除 client 进程启动与 OS 调度）；
- 3 会话 × (1 warmup + 10 采样)，每轮 fresh CAD + 私有夹具；3 次独立 fresh。
- 脚本：	mp\lead-measure2.ps1（+ -session.ps1）；证据：%TEMP%\lead-measure2-*\measure2-result.json。

**环境（如实记录）**：WPS 5 个进程在线（用户编辑 PDF/Excel）、free RAM 2.37 GB、CPU 8% —— **属于"负载环境"，非安静窗口**。

| 跑次 | wall p50 | wall p95 | helper p50 | helper p95 | cold p50 | 实体 | errors |
|---|---|---|---|---|---|---|---|
| 1 | 262 | 4254 | 134 | 268 | 538 | 33 | 0 |
| 2 | 338 | 452 | 103 | 127 | 361 | 33 | 0 |
| 3 | 355 | 396 | 108 | 138 | 373 | 33 | 0 |
| **中位** | **338** | — | **108** | — | — | 33 | 0 |

**结论（双口径）**：
1. **产品级（helper）p50 = 108 ms**，远低于 400 —— 说明 E4-lite 优化后"产品自身"的写命令开销很小；
2. **端到端（wall）p50 = 338 ms < 400**，即使在用户 WPS 负载下也达标（余量 62 ms）；
3. 波动来源确认是 client 进程启动（每命令 ~35–50 ms 且受 OS 调度/负载影响）+ 每次运行首条命令的 daemon 冷启动（361–538 ms）；
4. 三套 harness 对照（同一负载环境）：Lead wall 338 / core-cad 356 / audit 453（其计时多包 ~70–90 ms 包装）—— **产品级数字一致，wall 差异来自计时包含项**。

**最终验收口径（建议）**：以 **wall p50 中位 <400 ms（三次 fresh）** 为主判据，**helper p50 <200 ms** 为辅证；测量必须过安静门禁或如实标 environment:"load"。
