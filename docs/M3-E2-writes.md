# M3-E2-A 通用写原语：entity / query / snapshot / diff

> 2026-11-04 16:15–17:00，core-cad（task-15）。目标：把基准集里 5 条 `capability_gap`（W1 改文字 / W2 换图层 / W3 统一字高 / W5 删零长线 / W6 XData 标记）用通用原语补上。
> 证据：`tmp\evidence-e2\*`；自测脚本：`tmp\e2_selftest.ps1`。

## 一、命令契约

### 1. `entity`（写原语）

```
zwc entity -ProgId X -Doc D -Items <items.json> [-Mode plan|-DryRun] [-Continue] [-Tol 1e-6]
```
`items.json` = `{"items":[{op:"add|update|delete|move|copy", ...}]}`；也支持 `-RequestFile <完整请求>`。

| op | 字段 |
|---|---|
| `add` | `type`(line/polyline/text/mtext/circle) + `layer` + `geom`（line: start/end；polyline: points/closed；text/mtext: text/insert/height/width；circle: center/radius）+ `props` + `xdata` |
| `update` | `handle` + `expect{type,layer,text,height,bbox}` + `set{text,height,layer,color,linetype,style,rotation}` + `xdata` |
| `delete` | `handle` + `expect`（可选）|
| `move` | `handle` + `delta[x,y]` + `expect` |
| `copy` | `handle` + `delta` + `count` |

三趟语义（与 WPS 版同一套纪律）：
1. **Pass1 全校验**：handle 存在、类型/层/文字/高度/bbox 与 `expect` 相符（`-Tol` 默认 1e-6）→ 任一条不过：`ok:false / ANCHOR_MISSING`（`error.detail[]` 逐条 `{i,op,handle,field,expect,actual,relocatable}`），**整批一条不改**；`-Continue` 则逐条隔离。
2. **Pass2 应用**：一个 undo 组内执行（`BeginUndoMark`/`StartUndoMark` 双名）。
3. **Pass3 回读**：逐 handle 重新读回并按**调用方意图**（`set`/`xdata`）比对；失败 → **整体回滚** + `ok:false / VERIFY_FAILED`。
- 拒绝：只读文档 `READONLY_DOC`、锁定/冻结/关闭图层 `ANCHOR_MISSING(field=layer)`；只碰模型空间。
- 响应：`result{applied, plan?, rolledBack, failed[], items[{i,op,ok,handle,before,after}], verify{ok,issues,checked}, timings{pass1,pass2,readback,items}}`、`undoToken`。

### 2. `query`（查询原语）

```
zwc query -ProgId X -Doc D [-Layer L] [-Type text,line,polyline,circle,...] [-Text <regex>] \
          [-BBox x0,y0,x1,y1] [-Space model|paper|all] [-XData "APP:VALUE"|APP] [-Limit N] [-Cursor C] \
          [-Mode handles|summary|geom]
→ result{items[{handle,type,kind,layer,space,text?,bbox?,xdata?,xdataPresent?}], total, returned, nextCursor, truncated, mode, readMode}
```
- 默认 `-Mode summary`（含 bbox；**TEXT/MTEXT 也带 `text`**，供 `entity` 的 `expect.text` 直接复用）；`-Mode geom` 才回顶点/高度/样式；`-Mode handles` 最省（3 属性/LISP 拼串）。
- `-Text` 是 .NET 正则（忽略大小写）；`-XData` 支持 `APP:VALUE`（value 精确匹配）或仅 `APP`（存在即命中）。
- 内部走 E1 分级读，624 实体实测：handles ≈0.6 s / summary ≈1.8 s（Classic client 入口）。

### 3. `snapshot` / `diff`

```
zwc snapshot -ProgId X -Doc D [-Out <file>]      → result{path,count,items[]}
zwc diff -ProgId X -Doc D [-Since <file>|last]   → result{added[],removed[],changed[{handle,type,layer,fields{before,after}}],same,counts}
```
- snapshot **强制写 `%TEMP%\zwcad-cooperate\snap\`**（给图纸目录会被重定向并 warning）；同时记 `state.lastSnapshot`，所以 `-Since last` 即可。
- diff 按 handle 对齐，比较 type/layer/text/bbox（1e-6）；人改的和 agent 改的都看得见。

## 二、本轮修掉的问题（都在冒烟里暴露过）

1. **`entity` 每条 item 2–10 s 的固定开销**（W2 曾 42.9 s）：根因是 `after` 回读里带 **每实体 `GetBoundingBox`**（在 ZWCAD 2025 上触发几何计算）+ 图层确保每次全表查。
   修法：① `after` 不再带 bbox（只做 type/layer/text/height 意图比对）；② 图层确保加**进程内缓存**，一个批次只查一次；③ 响应加 `timings{pass1,pass2,readback}` 便于回归。
   **实测（Classic 16，client）**：2 条 xdata update = pass1 8 ms / pass2 168 ms / readback 146 ms；2 条 delete = pass2 249 ms；audit 在 2025 复测 W2 从 **42.9 s → 2.02 s**。
2. **XData 静默不落**（W6）：两个根因
   - `SetXData` 前**必须先注册 app**（`doc.RegisteredApplications.Add`），否则 ZWCAD 静默 no-op；
   - 回读 `GetXData(AppName, out types, out values)` 的 by-ref VARIANT 在 .NET 晚绑定下**无法取回**（`xprobe` 实测参数保持 null，3 参调用抛 `TargetParameterCount`）。
   修法：注册 regapp → 真实 `SetXData` → 回读优先 `GetXData`，取不到时用 **`%TEMP%` 里的 xdata 镜像表**（按 文档键|handle|app 存 value）并在响应里给 `XDATA_READ_FALLBACK` warning；`query -XData` 同样先 `GetXData`、再镜像；`entity delete` 会清理镜像。
   实测 W6 闭环：mark 2 → `query -XData` total=2 → delete 2 → total=0；`verify.checked=2`（xdata 计入回读校验，失败会 `XDATA_VERIFY_FAILED` + 整体回滚）。
   ⚠️ **已知限制**：该 CAD 的 `GetXData` 晚绑定不可读 ⇒ xdata 的"读侧真相"在本机取自镜像；镜像与实体同机同会话，跨机器/跨会话需要重新打标（或改用 LISP 通道，Classic 可行、2025 无 LISP 桥）。
3. **`state.json` 死 pid**（lead 的 bench 被它挡过）：`State.Load()` 现在按 pid 存活过滤 `session.instances`、清空无存活实例的 session、写 `schemaVersion:2`；`open` 实测"人为写死 pid → 自动清理且 `launched:true`"。
4. **ADOC_AMBIGUOUS**：多文档且没给 `-Doc` 时硬报错（列候选），不再猜 ActiveDocument——这正是 audit W4 那次"read 读到空 Drawing4"的根因；bench 的每条命令已显式 `-Doc`。
5. 顺手：daemon 拉起改用 ShellExecute 分离句柄（P0 cold hang）、daemon 争抢管道时最多重试 5 次即退出（避免 helper 僵尸堆积）。

## 三、自测结果（Classic 16，逐项）

| 用例 | 结果 |
|---|---|
| W1 改文字（3 条 update） | `applied=3 verify.ok=true`，回读 3 条文字 = 新值 |
| W2 换图层（4 条） | `applied=4 verify.ok=true`，回读 RIVER 空 / RIVER-2=4 |
| W3 统一字高（7 条） | `applied=7 verify.ok=true`，回读 ANNO 全部 2.5 |
| W5 删零长线（2 条） | `applied=2 verify.ok=true`，回读 ZERO=0 |
| W6 XData（打标→筛→删） | mark `applied=2 verify.ok=true checked=2` → query total=2 → delete `applied=2` → query total=0 |
| 负例：错 handle | `ANCHOR_MISSING`，整批未改（count 不变） |
| snapshot + 3 update + diff | `changed=3, same=15`，`fields.before/after` 正确 |
| 回归 | `run_m1 -DryRunOnly` PASS 9/0/2；v1 section 67 + 268 PASS；overlay 84 + cross_app_diff ALL MATCH（前几轮同 build 路径未变） |

## 四、未做 / 已知限制

1. `GetXData` 晚绑定不可读 → xdata 读侧走 `%TEMP%` 镜像（见 §二.2）；`xprobe` 命令保留在 helper 里作诊断（非公开命令）。
2. `entity` 只写模型空间；`copy` 只做平移复制（`count` 次），不支持旋转/缩放矩阵。
3. `query -BBox` 需要实体有 bbox（summary/geom 有；handles 档没有 → 该过滤下自动升级 readMode）。
4. `snapshot` 全量指纹对超大图（>5k）会慢，后续可加 `-Layer` 过滤（当前用 `-Limit` 分页读更省）。
5. 26/20 条基准的最终判定由 audit-dev 的 runner 给出（本轮我提供原语与自测证据）；ZWCAD 2025 / AutoCAD 2007 复跑由其执行。
## 五、终验结果（audit-dev `tmp\bench\e2\full20-final`，frozen + client + 2025 + Repeat 2）

- **20/20 success，0 capability_gap，0 fail**；W1 2.2 s / W2 2.2–3.5 s / W3 3.6 s / W4 12.3 s / W5 5.8 s / W6 10.0 s。
- 负例 N1–N4 全过：N1 `ANCHOR_MISSING{handle:"DEAD2"}` + 21 实体指纹 0 变化；N2 `VERIFY_FAILED/READBACK_HEIGHT` + `rolledBack:true`；N3 `undo.verify{baseline:20,countAfter:20,ok:true}`；N4 `diff{changed:3,same:17}` 且 before/after 正确。
- **AutoCAD 2007 跨产品 W1–W6 全绿**（W2 0.97 s / W6 2.95 s）。

### 5.1 `finish -Quit` 在 AutoCAD 2007 上不退进程（终验后修掉）

**现象**：AutoCAD 2007 下 `finish -Quit` 返回 exit 0，但进程仍在（调用方只能按 pid 强停）。
**根因**：`finish` 的进程轮询用 `Procs.ZwcadCount()`（**只数 zwcad 映像**）——对 `-App autocad` 完全数错对象，所以既不会等 AutoCAD 退出，也不会发现它没退。
**修法**：① 新增 `Procs.Family(kind)` / `FamilyCount(kind)`（autocad→`acad`，zwcad→`zwcad`），`finish` 按产品族计数；② Quit 后再等一轮，仍未退出则**再 Quit 一次**；③ 最后兜底：只对**我们自己 `launched:true` 的那个 pid** 做 `Kill()`（`forcedKill:true` + `QUIT_FORCED` warning），绝不动别人的实例。
**实测（AutoCAD 2007，client 入口）**：`open` 134.0 s（冷启动）→ `finish -Quit` **7.2 s，`processesAfter:0`，`forcedKill:false`**（优雅退出，没用到兜底）；后续 `Get-Process acad` = 0。

### 5.2 其它来自终验的注意点

- **夹具 DXF 版本必须 ≤ 最老产品**：`design-water.dxf` 原 R2010 在 AutoCAD 2007 上 `OPEN_FAILED`，改 R2007 UTF-8 后 6/6 通过（audit-dev 已写入 `docs\M3-E2-acceptance.md §6`，我这边记录为"夹具约定"）。
- xdata 读侧镜像限制（§二.2）已写进 audit 的验收文档 §8：跨机器/跨会话需重打标；`APP:VALUE` 为 value 精确匹配。
#### 5.1.1 ACAD 收尾修复的第三方复验（audit-dev `tmp\bench\e2\acad-finish-check\finish.json`）

| 断言 | 实测 | 结论 |
|---|---|---|
| `finish` ok / runner 退出码 | `true` / 0 | ✅ |
| `result.processesAfter` | **0** | ✅ 本产品族残留 0 |
| `result.forcedKill` | **false** | ✅ 优雅退出，兜底未触发 |
| `result.processWaitMs` | 16,165 ms（docsClosed=2，比单文档 7.16 s 长，量级正常） | ✅ 有界 |
| `result.ours` / `docsClosed` | true / 2 | ✅ 收的是自家实例 |
| 之后 `Get-Process acad` | **0** | ✅ |
| runner `hygiene.processesAfterFinish` / 强停 | 0 / 未触发 | ✅ |

> 口径提醒（写进验收文档 §8）：**`finish.processesAfter` = 本产品族计数**（`-App autocad` 数 `acad`、ZWCAD 数 `zwcad`），不是"全部 CAD 进程"；按旧口径断言的脚本要改。