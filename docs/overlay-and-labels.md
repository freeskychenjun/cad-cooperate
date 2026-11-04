# A-2 多序列套绘 + 图例 + 标注密度自适应（helper 侧验收）

> 2026-11-04 13:05–13:40，core-cad（task-10）。数据侧 schema v2 由 section-gen 提供（**已脱敏入库存于 `docs\section-model-v2.md`**，原 `tmp\real\SCHEMA-v2.md`），本文件只记 **helper 侧实现与实机验收**。
> 证据 JSON：`tmp\evidence-overlay\*`、`tmp\compare\*`；截图；坑台账：`tmp\core-cad-notes.md` 附录 4。（已按脱敏要求移除）
> **A-1 交付定稿（2026-11-04 13:5x，Lead 三产品复跑）**：图 1 = 示例站 <站码> 四序列套绘（84 实体）、图 2 = 2025-01-19 单序列（87 实体），三产品 `verify.ok` + `cross_app_diff` **ALL MATCH**；正式截图 6 张，明细见 `tmp\real\A1-DELIVERABLES.md §1–§2b`。（已按脱敏要求移除）

## 一、结论

**A-2 的 helper 侧全部落地并双产品实机跑通**：`section` 现在能画**多序列套绘**（同一站不同日期叠一张图）、自动图例、**自适应标注密度**、多水位线配色/标签、可选比例尺；**旧单序列模型（v1）行为零回归**（fixture 仍 67 实体、read 仍 268 PASS）；**真数据**（示例站 4 序列 92/93/70/113 点，A1 1:4000×1:200）在 ZWCAD Classic 与 AutoCAD 2007 上都是 84/84 实体、`verify.ok=true`、跨产品几何 **MATCH**。

## 二、接口（与数据侧 v2 对齐，向后兼容 v1）

| 项 | v1（旧，仍支持） | v2（新，优先） |
|---|---|---|
| 点 | 顶层 `points[]` | `series[].points[]`（按 DB vert_num 顺序，**不重排**；整段递减时自动反转，折叠/重复里程只 warning 并原样连折线） |
| 水位 | 顶层 `waterLevels[]` | 顶层 `waterLevels[]`（共享）+ `series[].waterLevels[]`，按高程去重合并 |
| 序列数 | 恒为 1 | 1..N；**N=1 时仍走"单序列口径"**（标高程+点号、无图例/比例尺），由 `params.pointNo/noPointNo` 控制点号 |
| 比例 | `params.hScale/vScale/paper/origin` | 同 v1（全局共用：`elevBase=elevMin_all-10*vScale/1000`、`x=(off-offsetMin_all)*1000/hScale`、`y=(elev-elevBase)*1000/vScale`） |

`series[]` 每项：`{id,label,date,station,points,pointCount,waterLevels[],style{color,linetype},color,linetype}`；`style` 优先于平铺同名键（两者都认）。`id` 会清洗成 `[A-Za-z0-9_-]` 用于图层名。

## 三、绘制模型（helper 侧）

### 3.1 图层与颜色

| 元素 | 单序列 | 多序列 |
|---|---|---|
| 地面线 | `AI-SEC-GROUND`（BYLAYER，色 7） | 每序列一层 `AI-SEC-GROUND-<id>`，**实体显式色** = `series.style.color` 或按调色板 `1,3,5,2,4,6,7`（避开 7 白）|
| 水位线 | `AI-SEC-WATER`（层色 5，DASHED） | 共用 `AI-SEC-WATER`；>1 条时每条**实体显式色**（调色板从序列数之后起算，保证不与地面线撞色） |
| 标注/图例/深泓 | `AI-SEC-ANNO` | 同 |
| 轴线/比例尺 | `AI-SEC-AXIS` | 同 |

### 3.2 图例（仅多序列，`-NoLegend` 关）

- 每行 = 12 mm 短线段（取该序列颜色）+ 文字（`label`/`date`）；**水位 >3 条时**水位标签也进图例（图上只标高程）。
- 位置：优先图形**右上角图内**；若与任何地面线顶点冲突（±1 mm 包围盒测试）→ **移到图形上方右对齐**（`LEGEND_RELOCATED` warning）。实测两种商业图纸都落在"上方右对齐"（地面线右岸顶到 bbox 顶）。
- 后处理：若某条水位标签会压到图例框（同 y 带且右端越界），把该标签**右对齐到图例左缘**（实测修复了首版截图里"P=1% 42.30 m"与图例重叠）。

### 3.3 标注密度（`labelPolicy`）

```
labelW  = max(字高×3, 估算宽("88.88"))           // 保守，实测 6.2 mm
fits    = max(2, floor(图宽mm / labelW))
every   = ceil(点数 / fits)                       // 显式 -LabelEvery N 优先（reason:"explicit"）
labelled = {i | i % every == 0} ∪ {末点} ∪ mandatory
mandatory = {首点, 末点, 深泓点, 左岸顶, 右岸顶} ∪ {每条水位线交点的两侧最近点}
```
- 响应 `result.labelPolicy{labelEvery, reason, mandatory[], labelled, pointCount, labelWidthMm, pointNumbers}`。
- 多序列**只标 primary 序列**（= `series[0]`，与 section-gen 约定一致）；点号默认关（`-PointNo` 才标）。
- 单序列保持"标高程 + 点号"，但同样按 `every` 减密（30 点 fixture 上 `every=1`，**实体数与老口径完全一致**；70 点真模型 A1 上 `every=2`、40 标注）。

### 3.4 比例尺（`-ScaleBar`，默认关）

基线下方 6 mm 画 50/20/10 m 分档刻度（按跨度自动选档）+ 里程数字 + `H 1:xxxx  V 1:yy` 注记；默认关，老场景不变。

### 3.5 回读校验扩展（失败仍整体回滚）

新增断言：`SERIES_MISSING`（序列地面线句柄/实体）、`SERIES_VERTEX_COUNT_MISMATCH`、`SERIES_ENDPOINT_MISMATCH`（首/末顶点）、`SERIES_LAYER_MISMATCH`、`LEGEND_MISSING`（图例行数 vs 实际图例文字数）、`LABEL_POLICY_MISMATCH`（标注数/点号一致性）；`verify.seriesChecked / legendRows / labels` 一并返回。

## 四、实机验收（真跑）

| 场景 | 产品 | 结果 |
|---|---|---|
| **3 序列手工套绘**（41/35/47 点，2 条水位） | ZWCAD Classic 16 | **40 实体**、`verify.ok=true`、seriesChecked=3、legendRows=3、labels=23、helper **635 ms**；截图（已按脱敏要求移除） |
| 同上 | AutoCAD 2007 | **40 实体**、`verify.ok=true`、seriesChecked=3、legendRows=3、labels=23、helper **471 ms**；截图（已按脱敏要求移除） |
| **跨产品几何一致性** | `python tests\cross_app_diff.py`（两产品 read） | **RESULT: ALL MATCH**（40 vs 40 实体，归一化后零差异） |
| **真数据 4 序列**（示例站 <站码>，92/93/70/113 点，A1 1:4000×1:200，4 条日均水位） | ZWCAD Classic 16 | **84 实体 = expected**、`verify.ok=true`、seriesChecked=4、legendRows=8（4 序列 + 4 水位）、`labelEvery=2`、labelled 52/92、helper **2 595 ms** |
| 同上 | AutoCAD 2007 | **84 实体 = expected**、`verify.ok=true`、seriesChecked=4、legendRows=8、helper **897 ms** |
| 单序列 v1 fixture（30 点）回归 | ZWCAD Classic 16 | **67 实体 / `verify.ok`**、read → `verify_section.py` **268 PASS**；`run_m1.ps1 -DryRunOnly` **PASS 9 / FAIL 0 / SKIP 2** |
| 单序列 v2 真数据（`db-2025-01-19`，70 点） | **ZWCAD Classic 16 / ZWCAD 2025 / AutoCAD 2007（Lead 三产品各跑一次）** | **87/87/87 实体、三者 `verify.ok`**；`labelEvery=2` 自动（40 标注 + 40 点号）、无图例/比例尺；section 569 / 2281 / 516 ms，read 234 / 1552 / 251 ms，undo 均 87→0；`cross_app_diff` 三对 **ALL MATCH**；截图（已按脱敏要求移除） |
| 4 序列真数据（图 1 定稿） | 同上三产品 | **84/84/84 实体 `verify.ok`**，三对 **ALL MATCH**；截图（已按脱敏要求移除） |
| `undo` 回退 | 两产品 | 真数据 84 → 40 → 0、套绘 40 → 0，均一次 `undoGroups` 内完成；`finish -Quit` 后 `Get-Process zwcad*,acad*` = 0 |

> 截图观察：三条地面线红/绿/蓝清晰分层；右上图例三行日期与短线颜色一致；水位线青/黄两条虚线，蓝色 `P=1% 设计水位 42.30 m`、黄色 `日均水位 40.85 m`，标签已避让图例；高程标注从 41 个减到 23 个（首末/深泓/岸顶/水位交点必标），中文渲染正常。

## 五、耗时基线

| 场景 | helper | 说明 |
|---|---|---|
| 3 序列套绘（40 实体） | 635 ms（ZWCAD Classic）/ 471 ms（ACAD 2007） | 含建层/字体/线型/回读 verify |
| 4 序列真数据（84 实体，A1） | 2 595 ms（Classic）/ 897 ms（ACAD 2007） | Classic 的 COM 回读明显慢（Read 逐实体） |
| 单序列 67 实体 | 400–700 ms | 与 M1 一致 |
| `read` 40 实体 | 约 0.8–0.9 s（Classic）/ 0.17 s（ACAD） | 大图请给 `-TimeoutSec 300`（audit-dev 实测 624 实体 ≈100 ms/实体） |

## 六、坑与纪律（本轮新增）

1. **多队友并行会触发 `MULTI_PRODUCT`**：audit-dev 一开 ZWCAD 2025，我所有**不带 `-ProgId`** 的命令立刻 `MULTI_PRODUCT`（这是 task-8 的正确行为）。⇒ 并行期**每条命令都显式 `-ProgId`**（`.16`/`.2025`）。
2. **`-Launch` 不保证独占实例**（已有实例时只附着）⇒ 只有 `result.launched:true` 的实例才允许 `finish -Quit`；`launched:false` 时只 `finish`（不带 `-Quit`）。
3. **截图前必须激活自己的图纸**：加了 `tmp\capture-section.ps1 -Doc <名字|全路径>`（`Documents.Item(i).Activate()` 后再 `ZoomExtents`）。不激活时抓到的是别人/别的图纸（本轮第一版就抓到了 audit 的 `problem-rich.dxf` 标签页）。
4. **wedged 实例的两种症状**：`Linetype` 设成 DASHED 却回读 `ByLayer` + `_.U` 完全不生效。已加：① `section` 前置 **BUSY 检查**（`CMDACTIVE!=0` 或 `LASTPROMPT` 脏则拒绝画）；② 水位线 linetype 设置**重试一次**并 `LINETYPE_SET_FAILED` warning；③ 回滚 `_.U` 重试一轮（`WaitIdle` 4 s）。换一个干净实例后同一 build 立刻全绿——即"实例状态"问题，不是代码问题。
5. 图例/水位标签的**避让顺序**：先右上图内 → 冲突则上方右对齐；再对水位标签做右对齐避让（两个坑都是先看截图才发现的）。

## 七、未做 / 待定

1. ~~ZWCAD 2025 的套绘实机跑未做~~ → **已由 Lead 补跑完成**（2026-11-04 13:5x，`scripts\compare-three-cads.ps1`）：图 1 套绘 84 实体、图 2 单序列 87 实体，**三产品（Classic 16 / 2025 / AutoCAD 2007）全部 `verify.ok` 且 `cross_app_diff` 三对 ALL MATCH**，截图已归档，结果见 `tmp\real\A1-DELIVERABLES.md §2b`。（已按脱敏要求移除）
2. **水位交点**目前只把它"就近点纳入必标集合"，没有画交点符号/水平距离标注（真实报告常用"水面宽"，属后续 A-3）。
3. 图例只有短线+文字，无边框/标题；`-ScaleBar` 已实现但默认关（本轮截图未开）。
4. 多序列点号默认关（`-PointNo` 可开）；若报告需要"逐点编号"再讨论密度规则。
5. `read` 大图（>2000 实体）走 COM 会慢，`-TimeoutSec` 需放宽（口径见上）。
