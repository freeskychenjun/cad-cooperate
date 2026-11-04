# M3-E2 独立验收：通用写原语让基准从 15/20 → 20/20

> 验收人：audit-dev（度量与独立判据）｜被测：core-cad 的 `entity / query / snapshot / diff`（M3-E2-A）
> 基准：`tests\bench\tasks.json`（20 条任务级用例）＋`tests\bench\negative.json`（N1–N4 负例）
> 产品：ZWCAD 2025（本人固定）＋ AutoCAD 2007（跨产品同构）
> 原始证据：`tmp\bench\e2\{full20-final, negative-final, cross-acad2, *-smoke}`
> 2026-11-04 17:0x–17:12 实测。

---

## 1. 结论（先看这里）

| 验收项 | 目标 | 实测 | 结论 |
|---|---|---|---|
| 基准成功率 | 20/20 success、0 capability_gap、0 failure | **20/20 success，0 gap，0 fail** | ✅ 达成 |
| 写原语独立判据 | W1–W6 用 `read -Mode geom` 回读断言**结果** | 6/6 通过（判据不采信 `applied`） | ✅ |
| 坏锚点 | 整批一条不改 + `ANCHOR_MISSING` + 前后指纹一致 | N1：`ANCHOR_MISSING{handle:"DEAD2", actual:"(not found)"}`、指纹 0 变化 | ✅ |
| 回读失败回滚 | `VERIFY_FAILED` + `rolledBack:true` + 指纹不变 | N2：`READBACK_HEIGHT`、`rolledBack:true`、指纹 0 变化 | ✅ |
| 一键撤销 | undo 后逐 handle 指纹恢复 | N3：`undo.verify{baseline:20, countAfter:20, ok:true}`、指纹恢复 | ✅ |
| diff 准确性 | changed=3 且 before/after 正确 | N4：`added=0, removed=0, changed=3, same=17`，字段 `text{before:"设计水位 42.30 m", after:"临时改"}` | ✅ |
| 跨产品同构 | ZWCAD 2025 与 AutoCAD 2007 上 W1–W6 一致 | **AutoCAD 2007：W1–W6 全部 success** | ✅ |
| 判据层可复算 | 离线自测 good/tamper 各一遍 | `tests\bench\selftest_checkers.py`：9/9 good PASS + 9/9 篡改被拒 | ✅ |

**剩余风险（不影响验收通过，见 §8）**：xdata 读侧在本机依赖 `%TEMP%` 镜像表（跨机器/跨会话需重打标）；AutoCAD 2007 的 `finish -Quit` 不退出进程（runner 按 pid 强停自己的实例）；被测代码仍在演进，基线必须用 `-Freeze` 快照跑。

## 2. 方法与环境

| 项 | 值 |
|---|---|
| 被测快照（`-Freeze`，SHA256 前 12） | `scripts\zwc.ps1` = `6cdfb6557ffa`；`Edits.cs` = `0fec6db13634`；`EditsQuery.cs` = `3a786d562ad6`；`ZwcHelper.cs` = 见 `full20-final\summary.json.meta.freeze` |
| 入口 | `-Entry client`（daemon + `ZwcClient.exe`），daemon 由 client 自起；对照此前 `-Entry ps1` |
| 重复 | 每条 **2 次取中位数**（20 条）；负例 1 次 |
| 夹具 | `tests\bench\fixtures\design-water.dxf`（23 实体；**DXF R2007**，见 §6 的兼容性发现）；每条写任务由 runner 复制**私有副本**再打开，源图不变、repeat 互不污染 |
| 命令纪律 | 每条 document 级命令显式 `-Doc`（DESIGN 口径，避免 `DOC_AMBIGUOUS`）；产品显式 `-ProgId/-App` |
| 产品归属 | 我固定 ZWCAD 2025（自起自退）；core-cad 固定 Classic/ACAD，互不触碰对方实例；跑前 `serve -Action status` |
| 实例与收尾 | ZWCAD：launch 19.9 s（冷启动）、`finish -Quit` 13.4 s、**procsAfter=0**；ACAD：launch 8.9 s；`finish` 详见 §6（core-cad 修后已能自己退干净，runner 仍保留按 pid 强停作保险） |

## 3. 20/20 逐条结果（`tmp\bench\e2\full20-final`，client 入口，2 次中位）

| # | 任务 | 类别 | 结果 | 命令数 | 中位墙钟 ms | 每命令 ms | 响应字节 | token估算 | 一键撤销 |
|---|---|---|---|---|---|---|---|---|---|
| 1 | `X1-no-instance` | 稳定性 | ✅ | 1 | 371 | 371 | 363 | 113 | - |
| 2 | `X3-bad-data-gate` | 稳定性 | ✅ | 3 | 1,139 | 380 | 449 | 140 | - |
| 3 | `Q1-layer-stats` | 读/查 | ✅ | 2 | 761 | 380 | 6,498 | 2,031 | - |
| 4 | `Q2-text-search` | 读/查 | ✅ | 2 | 592 | 296 | 6,500 | 2,031 | - |
| 5 | `Q3-open-polylines` | 读/查 | ✅ | 2 | 550 | 275 | 6,500 | 2,031 | - |
| 6 | `Q4-big-read` | 读/查 | ✅ | 2 | 6,967 | 3,484 | 122,844 | 38,389 | - |
| 7 | `W1-edit-text` | 改图 | ✅ | 5 | 2,209 | 442 | 10,492 | 3,279 | ✅ |
| 8 | `W2-change-layer` | 改图 | ✅ | 5 | 3,529 | 706 | 10,316 | 3,224 | ✅ |
| 9 | `W3-unify-text-height` | 改图 | ✅ | 5 | 3,611 | 722 | 12,072 | 3,772 | ✅ |
| 10 | `W4-move-to-origin` | 改图 | ✅ | 8 | 12,318 | 1,540 | 32,610 | 10,191 | ✅ |
| 11 | `W5-delete-zero-lines` | 改图 | ✅ | 5 | 5,794 | 1,159 | 9,834 | 3,073 | ✅ |
| 12 | `W6-xdata-mark` | 改图 | ✅ | 8 | 10,049 | 1,256 | 13,704 | 4,282 | ✅ |
| 13 | `S1-single-section` | 断面 | ✅ | 5 | 8,538 | 1,708 | 42,246 | 13,202 | ✅ |
| 14 | `S2-overlay-4series` | 断面 | ✅ | 4 | 13,567 | 3,392 | 58,314 | 18,223 | ✅ |
| 15 | `S3-water-edit-composite` | 断面 | ✅ | 10 | 16,213 | 1,621 | 47,962 | 14,988 | ✅ |
| 16 | `S4-from-db-station` | 断面 | ✅ | 4 | 28,174 | 7,044 | 73,492 | 22,966 | ✅ |
| 17 | `C1-audit-clean` | 检核 | ✅ | 4 | 2,999 | 750 | 6,484 | 2,026 | - |
| 18 | `C2-audit-problem` | 检核 | ✅ | 4 | 4,699 | 1,175 | 13,744 | 4,295 | - |
| 19 | `C3-draw-readback` | 检核 | ✅ | 6 | 11,685 | 1,948 | 61,409 | 19,190 | ✅ |
| 20 | `X2-product-ambiguity` | 稳定性 | ✅ | 1 | 1,048 | 1,048 | 5,302 | 1,657 | - |

**分类汇总**：读/查 2.2 s/任务｜改图 6.3 s/任务（6/6 成功，含一键撤销）｜断面 16.6 s/任务｜检核 6.5 s/任务｜稳定性 0.85 s/任务。
**写任务命令数说明**：W1/W2/W3/W5 = 5 条、W6 = 8 条（query 定位 + 本地拼 JSON + entity + read 回读；W6 多"打标/筛选/删除/再验证"两段）。其中"拼 JSON"是**本地步骤**（`gen_entity_request.py`，0.1 s），不是 CAD 命令。

## 4. W1–W6 判定口径（全部取 `read -Mode geom` 独立回读）

| 任务 | 写法 | 判据（回读结果） | 实测关键值 |
|---|---|---|---|
| W1 | `query -Text 设计水位` → `update set.text` | 3 handle 文字**精确等于**「设计水位（复核）」；无未改残留 | applied=3，`newTexts=3` |
| W2 | `query -Layer RIVER` → `update set.layer=RIVER-2` | RIVER 0 实体、RIVER-2 = 命中数 | moved=4 |
| W3 | `query -Layer ANNO -Type text` → `set.height=2.5` | ANNO 全部 height==2.5（1e-6） | heights={2.5} |
| W4 | undo + `-Origin` 重画 | 平移 50±0.01、实体数不变、undo 回 0 | shiftX=50，entities=67 |
| W5 | `query -Type line`（bbox 退化判零长）→ `delete` | 零长实体=0、被删 handle 不存在 | zeroFound=2，applied=2 |
| W6 | `update xdata` → `query -XData APP:VALUE` → `delete` | 打标后 total=2、删后 total=0、handle 消失 | marked=2，leftAfterDelete=0 |

## 5. 负例证据（`tmp\bench\e2\negative-final`）

| 负例 | 期望语义 | 实测 | 耗时 |
|---|---|---|---|
| **N1 坏锚点** | `ANCHOR_MISSING`、`applied=0`、前后指纹**完全一致** | ✅ `error.code=ANCHOR_MISSING`，detail `{i:1, handle:"DEAD2", field:"handle", actual:"(not found)"}`；21 个实体逐 handle 指纹 0 变化 | 1.85 s |
| **N2 回读失败回滚** | `VERIFY_FAILED`、`rolledBack:true`、指纹不变 | ✅ `error.code=VERIFY_FAILED`、issues `READBACK_HEIGHT`（`height read back as 2.5, set requested -5`）、`result.rolledBack=true`、指纹 0 变化 | 2.50 s |
| **N3 undo 回基线** | undo 后逐 handle 指纹恢复 | ✅ `undo.verify{baseline:20, countBefore:20, countAfter:20, ok:true}`；回读指纹与改前完全一致 | 2.84 s |
| **N4 snapshot/diff** | `changed=3`（handle+字段 before/after 正确）、added/removed=0 | ✅ `added=0, removed=0, changed=3, same=17`；`changed[0]={handle:"42", fields:{text:{before:"设计水位 42.30 m", after:"临时改"}}}`；snapshot 落 `%TEMP%\zwcad-cooperate\snap\snap-20261104-171201.json`（count=20） | 3.79 s |

> 三个负例都是**先证明失败语义，再证明文档没被污染**（逐 handle 指纹比对），不是只看错误码。

## 6. 跨产品同构：ZWCAD 2025 vs AutoCAD 2007（`tmp\bench\e2\cross-acad2`）

| 任务 | ZWCAD 2025（ms） | AutoCAD 2007（ms） | 一致性 |
|---|---|---|---|
| W1 改文字 | 2,209 | **1,123** | ✅ 同判据通过 |
| W2 换图层 | 3,529 | **973** | ✅ |
| W3 统一字高 | 3,611 | **937** | ✅ |
| W4 移动（undo+Origin） | 12,318 | **2,699** | ✅ |
| W5 删零长线 | 5,794 | **1,191** | ✅ |
| W6 XData 闭环 | 10,049 | **2,953** | ✅（2007 上走同一镜像回读路径） |

**跨产品过程中发现并修掉的一个测试侧问题（重要，避免后人踩）**：`design-water.dxf` 最初写成 **DXF R2010（AC1024）**，AutoCAD 2007 打不开（`OPEN_FAILED: Documents.Open failed`），导致 W1/W2/W3/W5/W6 在 ACAD 上全挂而 W4（不用该夹具）正常。改为 **DXF R2007（AC1021）** 后 6/6 通过——R2007+ DXF 是 UTF-8，中文照常保留，ZWCAD 2025/Classic 也都能读。**结论：跨产品夹具的 DXF 版本要 ≤ 最老产品**。

**ACAD 2007 收尾修复复验（2026-11-04 17:21，core-cad 修后）**：同一 runner、`-Product autocad -Entry client`、live 源码，`tmp\bench\e2\acad-finish-check`：

| 字段（`finish.json`） | 实测 | 期望 |
|---|---|---|
| `ok` / 退出码 | true / 0 | true / 0 |
| `result.processesAfter` | **0** | 0 |
| `result.forcedKill` | **false**（优雅退出，兜底未触发） | false |
| `result.processWaitMs` | 16,165 ms（本次含 2 个文档 Close+Quit） | 有界 |
| `result.docsClosed` / `ours` | 2 / true | - |
| 之后 `Get-Process acad` | **0** | 0 |
| runner `hygiene.processesAfterFinish` / 强停 | 0 / 未触发 | 0 / 未触发 |

根因与修法（core-cad）：`finish` 的进程轮询原先只数 `zwcad` 映像，`-App autocad` 数不到 AutoCAD；已改为按**产品族**计数（autocad→acad、zwcad→zwcad）+ Quit 后再等一轮 + 未退则再 Quit 一次 + 最后只对自己 `launched:true` 的 **pid** Kill（`forcedKill:true` + `QUIT_FORCED`）。**注意语义变化**：`finish.processesAfter` 现在数"本产品族"，不是全部 CAD 进程。

## 7. 性能观测（同一套 frozen 快照，client 入口）

**命令级**（全 20 条 ×2 次聚合）：

| 命令 | 次数 | 平均墙钟 | 平均 helper | 平均地板价 | 平均字节 |
|---|---|---|---|---|---|
| `section` | 16 | 5,334 ms | 5,254 ms | 80 ms | 4,082 |
| `read`（含大图） | 37 | 2,729 ms | 2,661 ms | 68 ms | 17,636 |
| `query` | 14 | 1,198 ms | 1,120 ms | 78 ms | 1,501 |
| `entity` | 12 | 809 ms | 732 ms | 77 ms | 1,977 |
| `info` | 4 | 898 ms | 834 ms | 64 ms | 1,467 |
| `open` | 33 | 636 ms | 561 ms | 75 ms | 1,397 |
| `undo` | 4 | 483 ms | 423 ms | 60 ms | 713 |
| `status` | 4 | 689 ms | 490 ms | 199 ms | 2,832 |
| python（prepare/audit/verify/gen） | 46 | 108–647 ms | - | - | - |

- 每命令 `status` 均值 **147 ms**（min 114）、地板价 **63 ms** —— E1 的 daemon+client 收益在 E2 新命令上同样成立（`query`/`entity` 平均 0.8–1.2 s，**helper 本体占 92%**，说明入口开销已经不是瓶颈）。
- **W2 提速已复验**：旧 build 42.9 s → 现在 2.2–3.5 s（2025）/ 0.97 s（2007）；core-cad 的根因是 Pass3 每条 item 都 `GetBoundingBox` + 图层全表查，已改按意图字段比对 + 图层缓存，并给 `entity` 输出 `timings` 分解。
- 最贵任务：`S4` 28.2 s（DB 查询 + 113 点断面出图 + geom 回读）、`S3` 16.2 s（10 命令复合）、`S2` 13.6 s（4 序列套绘 + 491 KB 回读）——成本在 `section`/`read` 的 helper 本体，属数据/几何量级，不是入口开销。
- **token 成本**：`Q4` 大图 read 一次 38.4k token 仍是最大单项（read 分级后已从 36.8k 量级持平，但单次 6.9 s 的 helper 时间才是重点）。

## 8. 已知限制与剩余风险

1. **XData 读侧依赖同机镜像表（平台限制，非使用问题）**：`GetXData(AppName, out types, out vals)` 的 by-ref VARIANT 在 .NET 晚绑定下取不回（core-cad 用 `xprobe` 实测 params 保持 null，3 参/2 参分别报 TargetParameterCount）。实现改为：注册 `RegisteredApplications` → 真实 `SetXData` → 回读优先 `GetXData`，取不到则回退 `%TEMP%\zwcad-cooperate` 的 xdata 镜像表并给 `XDATA_READ_FALLBACK` warning；`query -XData` 同样先 GetXData 再镜像，`entity delete` 清镜像。**含义**：跨机器/跨会话读 xdata 需要重新打标（或 Classic 走 LISP 通道）。`APP:VALUE` 为精确串匹配，只给 `APP` 表示"有该 app 任意值"。
2. ~~AutoCAD 2007 的 `finish -Quit` 不退出进程~~ → **core-cad 已修并复验**（2026-11-04 17:21）：`processesAfter:0 / forcedKill:false / exit 0`，此后 `Get-Process acad`=0。根因是 finish 的进程轮询只数 `zwcad` 映像（`-App autocad` 数不到 acad）；现按产品族计数 + 二次 Quit + 仅对自己 `launched:true` 的 pid 兜底 Kill。runner 的"按 pid 强停"保留作保险（本次未触发）。**语义变化**：`finish.processesAfter` 数本产品族，不是全部 CAD。
3. **被测代码仍在演进 ⇒ 基线必须冻结**：E2 实现期 core-cad 多次热改 helper（16:26 一次 `COMPILE_FAILED`、16:47 一次 hash 变更导致 client `helper binary missing`），不冻结的跑法会混版本。**口径**：验收/对比一律 `-Freeze`（本次快照哈希见 §2），E1/E2 完成后再用 live 源码复跑一次。
4. **大图读取**（624 实体）仍是单次最贵操作（~6.9 s helper）；如需更快需要 E1 的 `handles` 档（Classic 571 ms / 2025 预计 1–2 s）。
5. **W6 的命令数 8**：为完整证明"打标→筛选→删除→再验证"闭环，比 lead 建议的 ≤3 多；其中 2 步是本地 gen（拼 JSON），实 CAD 命令 6 条。

## 9. 复跑命令

```powershell
# 20 条基准（client 入口，冻结快照，2 次中位）→ 目标 20/20
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\bench\run_bench.ps1 `
    -Tasks all -Product zwcad2025 -Repeat 2 -Entry client
# 负例 N1–N4（坏锚点/回滚/undo/diff）
... -TasksFile tests\bench\negative.json -Tasks all -Repeat 1 -Entry client
# 跨产品 AutoCAD 2007（W1–W6）
... -Product autocad -Tasks W1-edit-text,W2-change-layer,W3-unify-text-height,W4-move-to-origin,W5-delete-zero-lines,W6-xdata-mark -Repeat 1 -Entry client -NoFreeze
# 判据层离线自测（无 CAD）
python tests\bench\selftest_checkers.py
```

## 10. 证据文件索引

| 路径 | 内容 |
|---|---|
| `tmp\bench\e2\full20-final\summary.{json,md}` | 20/20 原始数据（含每步 meta、freeze 哈希） |
| `tmp\bench\e2\negative-final\` | N1–N4 全步骤 JSON（含 `diff1.json`、`entity1.json` 错误 detail） |
| `tmp\bench\e2\cross-acad2\` | AutoCAD 2007 W1–W6 结果与收尾记录 |
| `tmp\bench\e2\w2w6-check\`、`w6-check2\` | W2 提速对比、W6 xdata 修复前后 |
| `tests\bench\selftest_checkers.py` | 判定层离线自测（9 good + 9 tamper） |
| `tests\bench\fixtures\design-water.dxf`（R2007, 23 实体） | 写任务夹具 |
