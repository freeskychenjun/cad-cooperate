# `section.model.json` **schema v2**（多序列套绘）契约

> **来源与脱敏**：本文原为 `tmp/real/SCHEMA-v2.md`（/tmp/ 不入库，fresh clone 会丢），2026-11-05 移入 `docs/` 并把站码/站名、本机水文库路径、截图文件名替换为占位符 —— 真实取值只在生成模型的 JSON 里，契约本身与具体站点无关。

> 冻结于 2026-11-04（section-gen 数据侧 ↔ core-cad helper 消费侧）。
> 生成方：`scripts/section/prepare.py --from-db <站码>`。旧 **v1 单序列**文件（two-col / yssd）不变，helper 两端都吃。
> 示例文件：`tmp/real/db-2025-01-19.model.json`（单序列）、`tmp/real/db-overlay-<station-code>.model.json`（4 序列）、
> `tmp/real/db-vs-xls-2025.model.json`（带 `sourceCompare`）。

## 0. 与 v1 的差别（一句话）

v1 是「一个断面 + 预先算好的 `entities[]`」；**v2 是「1..N 个断面序列 + 绘制参数」，不再给 `entities[]`**，
实体由 helper 按 `params + series` 现场计算（多序列叠加、图例、标注密度都在 helper 侧）。

| | v1（`zwcad-section-model/1`） | v2（`zwcad-section-model/2`） |
|---|---|---|
| 数据形态 | 顶层 `points[[off,elev]]` + `pointsDetail` | `series[]`，每条含 `points[[off,elev]]` |
| 实体清单 | `entities[]` + `expected{total,byType,byLayer}` | **无**（helper 算） |
| 水位 | `waterLevels[{value,label,...}]`（模型级） | `series[].waterLevels[{value,label}]`（序列级） |
| 参数 | `params` 带 `noAutoScale` 等 | `params` 带 `labelEvery:null/noLegend/scaleBar` 等 |
| 兼容 | 旧 helper 用 v1 | helper 必须同时认 v1 / v2 |

## 1. 顶层

```json
{
  "ok": true,
  "schema": "zwcad-section-model/2",
  "design": "DESIGN-M1.md 4.2/4.3 + SCHEMA-v2（多序列）",

  "source": {
    "mode": "db",
    "db": "<hydro-db>/hydro_data.db",
    "table": "cross_section",
    "reader": "sqlite3 read-only URI",
    "station": "<station-code>",
    "stationName": "示例站",
    "river": "赣江",
    "stationLabel": "<station-code> 示例站",
    "selection": {"date": null, "years": "2024-2025", "last": null},
    "dates": [{"date": "2025-01-19", "pointCount": 70, "verts": [1, 70],
               "rowCount": 70, "skippedRows": 0, "conflictFlags": 0}],
    "orderedBy": "vert_num（真实测点顺序，未按起点距重排）"
  },

  "params": {
    "hScale": 4000, "vScale": 200, "paper": "A1",
    "origin": [0, 0],
    "labelEvery": null,
    "layerPrefix": "AI-SEC",
    "noAxis": false,
    "noPointNo": true,
    "pointNo": false,
    "scaleBar": false,
    "noLegend": false,
    "textHeight": 2.0,
    "waterTextHeight": 2.5
  },

  "series": [
    {
      "id": "2025-01-19",
      "label": "2025-01-19",
      "date": "2025-01-19",
      "station": "<station-code> 示例站",
      "points": [[-29.1, 28.24], [-21.3, 28.28], "..."],
      "waterLevels": [{"value": 17.84, "label": "日均水位"}],
      "style": {"color": 1, "linetype": "CONTINUOUS"},
      "color": 1,
      "linetype": "CONTINUOUS",
      "pointCount": 70
    }
  ],

  "computed": {
    "bbox": {"x0": 0.0, "y0": 10.0, "x1": 385.525, "y1": 133.5},
    "elevBase": 3.7,
    "xRange": [-29.1, 1513.0],
    "seriesCount": 1,
    "pointCounts": [70],
    "scale": {"horizontal": 4000, "vertical": 200},
    "widthMm": 385.525, "heightMm": 123.5,
    "width_mm": 385.525, "height_mm": 123.5,
    "offsetMin": -29.1, "offsetMax": 1513.0,
    "elevMin": 5.7, "elevMax": 30.4, "spanM": 1542.1,
    "autoScaled": false, "autoScaledFrom": null, "autoScaledTo": null,
    "orderReversed": false, "vertOrderPreserved": true,
    "fitWidth": true, "fitHeight": true,
    "paperUsable": {"widthMm": 821.0, "heightMm": 574.0}
  },

  "dataQuality": {
    "profile": "db",
    "source": "hydro_data.db:cross_section（只读）",
    "rules": {"ELEV_OUT_OF_RANGE": "|高程 - 本序列中位数| > 40 m"},
    "series": [{"id": "2025-01-19", "date": "2025-01-19", "pointCount": 70,
                "offsetMin": -29.1, "offsetMax": 1513.0,
                "elevMin": 5.7, "elevMax": 30.4, "medianElev": 14.0,
                "vertOrderMonotonic": true, "duplicateOffsets": 0,
                "conflictFlags": 0, "outliers": [],
                "sourceFiles": ["补充资料入库目录/…xls"], "sectionNames": []}],
    "suspectCount": 0, "dropped": 0, "suspect": [],
    "summary": [{"id": "2025-01-19", "date": "2025-01-19", "points": 70,
                 "offsetMin": -29.1, "offsetMax": 1513.0, "elevMin": 5.7, "elevMax": 30.4}]
  },

  "warnings": [{"code": "MULTIPLE_SERIES", "message": "…", "seriesCount": 4, "dates": ["…"]}]
}
```

### `sourceCompare`（仅 `--compare-source`，DB vs 源 xls）

```json
"sourceCompare": {
  "dbPoints": 183, "compared": 183, "same": 93, "differingCount": 90, "tolerance": 0.005,
  "byDate": [{"date": "2025-01-19", "matched": true, "xlsBlock": 1, "xlsDate": "1月19日",
              "dbPointCount": 70, "xlsEntryCount": 70,
              "xlsNumberedCount": 68, "xlsLabelRows": 2,
              "compared": 70, "same": 40, "differing": 30,
              "offsetMismatch": 0, "xlsTruncatedCells": 28,
              "dbWaterLevel": 17.84, "xlsWaterLevel": 17.85}],
  "differing": [{"date": "2025-01-19", "vert": 6, "dbOffset": 3.0, "xlsOffset": 3.0,
                 "dbElev": 28.98, "xlsElev": 98.0, "delta": -69.02,
                 "xlsRaw": "98", "xlsRawIsTruncated": true}],
  "note": "…"
}
```
`sourceCompare.differing[]` 是证据清单（xls 截断 vs DB 正确值），helper 不需要画。

## 2. 坐标映射（**多序列必须共用同一套**）

```
elevBase = min(所有序列的 elev) - 10 * vScale / 1000
x_mm = (off_m - offsetMin_all) * 1000 / hScale + origin[0]
y_mm = (elev_m - elevBase)      * 1000 / vScale  + origin[1]
bbox = { x0: min(x), y0: min(y), x1: max(x), y1: max(y) }   // 全体序列的点
widthMm = x1 - x0 ; heightMm = y1 - y0
```
- 单序列时与 v1 完全一致（v1 的 elevBase 就是该序列自身 min）。
- **不要**按序列各自算 elevBase / 各自归一化 x —— 否则叠图对不齐（v2 的 `computed.elevBase/xRange` 是全局的）。
- `xRange = [offsetMin, offsetMax]`（米，供 helper 画比例尺/刻度用）；比例尺 50 m 分档由 helper 决定。

## 3. 绘制约定（helper 侧实现，数据侧约定见 task-10）

| 项 | 约定 |
|---|---|
| 地面线 | 每序列一条 `LWPOLYLINE`（不闭合），图层 `${layerPrefix}-GROUND-${series.id}`，颜色 `series.style.color`（1..7 ACI），线宽 0.3 |
| 序列 id | 只用 `[A-Za-z0-9_-]`（数据侧已保证，如 `2025-01-19`） |
| 颜色 | 数据侧按 `(1,3,5,2,4,6)` 循环分配（避开 7=白）；helper 可用 `style.color`/`color`，缺省自己循环 |
| 水位线 | 每序列各自 `AddLine`（`${layerPrefix}-WATER`，DASHED，y=(value-elevBase)*ky），文字给第 1 条/或进图例 |
| 高程标注 | 只标 `series[0]`（primary）；`params.labelEvery=null` ⇒ helper 按间距自动算 |
| 点号 | 多序列默认关（`params.pointNo=false`）；单序列 `pointNo=true` 保持 v1 口径 |
| 图例 | 仅 `seriesCount>1` 且 `noLegend=false` 时画；每行=短线段+`series.label` |
| 比例尺 | `scaleBar=false` 默认关（开了画在基线下方 50 m 分档） |
| 轴线 | `noAxis=false` 时画基线 + 左竖线（与 v1 相同） |

## 4. `warnings[].code` 一览（v2）

`MULTIPLE_SERIES` / `SERIES_MANY` / `WATER_FROM_DB` / `WATER_EXPLICIT` / `NO_WATER` /
`SERIES_DIST_NON_MONOTONIC` / `DUPLICATE_OFFSET` / `SUSPECT_DATA` / `SUSPECT_DROPPED` /
`AUTO_SCALED` / `TOO_TALL` / `SOURCE_COMPARE_DIFF` / `SOURCE_COMPARE_SAME` / **`SOURCE_COMPARE_UNMATCHED`**
（后者：所选施测日期在源表里没有对应块、`byDate[].matched=false`，不应被当成「一致」）。

helper 建议：`MULTIPLE_SERIES` / `SUSPECT_DATA` / `SOURCE_COMPARE_DIFF` 原样透传到响应 `warnings`；
`AUTO_SCALED` 与自身选档不一致时按 v1 规则报 `warnings:MODEL_COMPUTED_MISMATCH`。

## 5. 与 v1 的兼容规则

1. 顶层有 `series`（非空数组）⇒ 走 v2 多序列路径；
2. 只有顶层 `points` ⇒ 走 v1 单序列路径（`entities[]` 若存在可直接用，也可按 v1 规则重算）；
3. v2 单序列（`seriesCount==1`）**保留 helper 的自适应标注密度**（2026-11-04 定稿，实际见 §7）：
   70 点 → `labelEvery=2`、40 标注 + 40 点号、无图例/比例尺；要字面「全标」就在 params 里显式给
   `labelEvery:1`。**v1 单序列文件（`points` + `entities[]`）仍按老口径**（全标高程+点号，30 点 fixture = 67 实体），不受此影响；
4. `computed` 任何字段不一致：只 warning 不阻断（v1 已约定）。

## 6. 已知数据事实（示例站站 <station-code>）

- `cross_section` 里 2025-01-19 = 70 点（含左右岸行，vert 1 = `-29.1/28.24`，vert 70 = `1513.0/27.42`）；
  2025-09-18 = 113 点；2024-01-30 = 92；2024-08-09 = 93。可选日期 103 次（1988-03-09 … 2025-09-18）。
- DB 的 `elevation` 是**正确值**；源 `.xls` 同名单元格被截断成文本（`98`=28.98、`45`=28.45、`28`=28.28…）。
  逐点证据见 `tmp/real/DB-VS-XLS.md`。
- DB 无「测时水位」字段 ⇒ 水位取 `daily_water_level` 日均值（label `日均水位`；2025-01-19 = 17.84 vs 源表测时 17.85）。

## 7. 渲染实测对账（2026-11-04，core-cad 真机回报）

helper 已全部消费 v2 模型（**ZWCAD Classic** 与 **AutoCAD 2007** 双环境真跑，`cross_app_diff = ALL MATCH`）：

| 模型 | 序列/点数 | helper expected | 双环境真机 | 截图 |
|---|---|---|---|---|
| `db-2025-01-19`（单序列） | 1 序列 / 70 点 | **87** = 1 折线 + 3 线 + 82 文 + 1 圆；`labelEvery=2`(自动) → 40 标注 + 40 点号；无图例/比例尺 | — | — |
| `db-overlay-<station-code>`（4 序列） | 4 序列 / 92+93+70+113 | **84** = 4 折线 + 14 线 + 65 文 + 1 圆；legend **8 行**（4 序列 + 4 水位）；`labelEvery=2`、52/92；W=385.5 H=124.3（A1 1:4000×1:200，不升档） | ZWCAD Classic **84/84 verify.ok**（helper 2 595 ms）；ACAD 2007 **84/84 verify.ok**（helper 897 ms） | （截图已按脱敏要求移出仓库） |
| `db-vs-xls-2025` / `db-last2`（各 2 序列） | 2 序列 / 70+113、70+183 | **54** 各 | — | — |

**定稿的渲染口径**（数据侧确认，2026-11-04）：

1. **单序列 v2 保留自适应减密**（70 点 → `every=2`、40 标注 + 40 点号），因为「真实断面 40–113 点太挤」
   正是 A-断面图产品化要解决的问题；30 点 v1 fixture 仍 `every=1`、**老口径 67 实体不变**。
   要字面全标 ⇒ 模型 `params.labelEvery=1`（显式优先）。
2. **水位 >3 条**时图上只标高程、水位标签进图例（overlay 的 4 条日均水位 = 12.53/18.77/17.84/17.32，
   legend 共 8 行）；数据侧认可，避免 4 条水位标签互相打架。
3. `series[].points` 按数组原样连折线；helper 只在「整段递减」时反转，折叠/重复里程只报 warning 不改数据。
   数据侧说明：x 坐标由 offset 映射得出，反转只改变折线方向、不改几何形状；重复里程的
   `DUPLICATE_OFFSET` / `SERIES_DIST_NON_MONOTONIC` 由数据侧 warning 透传。
4. 数据侧**不参与**实体数公式：v2 的实体数由 helper 的标注密度/图例策略决定（≠ v1 的 `2N+7`）；
   数据侧只保证 `series/points/elevBase/xRange/bbox/pointCounts` 正确（`tests/verify_model_self.py` v2 分支，34–36 项全 PASS）。

### 7.1 `byRole` 对账表（core-cad 已落进响应，数据侧已逐项复核 ✅）

| 模型 | byRole | Σ |
|---|---|---|
| `db-overlay-<station-code>`（4 序列） | `ground:4, water-line:4, water-text:4, elev:52, axis:2, thalweg:1, thalweg-text:1, legend:8, legend-text:8` | **84** |
| `db-2025-01-19`（单序列 70 点） | `ground:1, water-line:1, water-text:1, elev:40, pointno:40, axis:2, thalweg:1, thalweg-text:1` | **87** |
| v1 fixture（30 点，老口径） | `ground:1, water-line:1, water-text:1, elev:30, pointno:30, axis:2, thalweg:1, thalweg-text:1` | **67** |
| 手工 3 序列（41 点，非本次交付） | `ground:3, water-line:2, water-text:2, elev:23, axis:2, thalweg:1, thalweg-text:1, legend:3, legend-text:3` | **40** |

- **之前对不上的「65 条文字 = 61 + 余 4」已对上**：余 4 = `water-text`（水位标签）。
  多序列里每条水位线**仍各画 1 条文字**，只是 `>3` 条时文字内容只写高程（`12.53`），名字进图例。
  ⇒ `65 = 52 elev + 8 legend-text + 1 thalweg-text + 4 water-text`；
  折线 4；线 `14 = 4 water-line + 2 axis + 8 legend`（图例每行一段 12 mm 短线段）；
  圆 `1 = thalweg`。`4+14+65+1 = 84` ✓
- `db-vs-xls-2025` / `db-last2` 各 **54**（2 序列 + 2 水位，byRole 同构）。

### 7.2 `water-text` 插入点实测（**helper 当前实现的副作用，不是契约**）

`elevBase=3.54, vScale=200 ⇒ ky=5, bbox.x1=385.525`；实测插入点 **`x = bbox.x1 + 5 + 5*(i-1)`、`y = line_y + 2`**：

| series | 水位值 | 线 y | 文字 y | 文字 x | core-cad 实测 |
|---|---|---|---|---|---|
| 2024-01-30 | 12.53 | 44.95 | 46.95 | 390.525 | `12.53@[390.525,46.95]` ✓ |
| 2024-08-09 | 18.77 | 76.15 | 78.15 | 395.525 | `18.77@[395.525,78.15]` ✓ |
| 2025-01-19 | 17.84 | 71.50 | 73.50 | 400.525 | `17.84@[400.525,73.5]` ✓ |
| 2025-09-18 | 17.32 | 68.90 | 70.90 | 405.525 | `17.32@[405.525,70.9]` ✓ |

> **⚠️ 该 `+5 mm` 级联是副作用，不是设计（2026-11-04 core-cad 更正）**：
> helper 在水位循环里 `wl.P2 = xMax + 5` 之后又 `xMax = wl.Bmax[0]`，于是**第 i 条水位线的右端比第
> i-1 条再长 5 mm**，标签跟着右移。后果：**每条水位线的 x 跨度并不相等**（4 条时最右一条超出
> `bbox.x1` 达 20 mm），与 DESIGN-M1 §4.3「x 从 bbox 左延 5 mm 到右延 5 mm」**不再严格一致**。
> 只碰巧降低了标签压字概率，视觉上"像设计"。
>
> - 这是 v2 多序列才有的偏差；**单序列 v2 与 v1 都只有 1 条水位线，无级联**（仍 `bbox.x1+5`，符合 DESIGN）。
> - core-cad 已在 `SectionOverlay.cs` 就地注释标注为「已记录副作用」，并记入 `tmp\core-cad-notes.md`
>   附录 4 增补之三；task-10 已验收，**暂不改**。
> - 若后续要求「所有水位线等长 / 严格符合 DESIGN」：把 `wspan = xMax + 5` 提到循环外即可（一行），
>   但必须**重跑 overlay 双产品截图 + `cross_app_diff`**。
> - ⇒ 数据侧文档**不把这个 x 坐标当契约**；模型侧只保证 `series[].waterLevels[].value/label` 正确，
>   文字插入点是 helper 渲染细节，可随实现调整。

### 7.3 `labelPolicy.mandatory[]`（逐条索引）与标注数复算 ✅

| 模型 | 点数 | `every` | stride 数 | mandatory（去 stride 后） | 标注数 |
|---|---|---|---|---|---|
| overlay primary（2024-01-30） | 92 | 2 | 46 | `1,15,17,73,75,91` | **52** |
| `db-2025-01-19` | 70 | 2 | 35 | `19,33,51,67,69` | **40** |
| v1 fixture | 30 | 1 | 30 | `1,10,22,29`（全标） | 30 |
| 手工 3 序列（41 点） | 41 | 2 | 21 | `1,37` | **23** |

强制点 = 首末点 / 深泓 / 左右岸顶 / 各水位交点两侧最近点。数据侧按「stride ∪ mandatory」复算，
与 core-cad 的 `labelPolicy.labelled` 完全一致（我是 39/49 的粗算漏了水位交点的 2×2 与末点，已对上）。

> helper 响应新增：`result.expected.byRole` / `result.created.byRole`（角色分组对账）、
> `result.waterLevels[{value,label,y,color,text,insertion}]`、`result.labelPolicy{labelEvery,reason,mandatory[],labelled,pointCount,labelWidthMm,pointNumbers}`、
> `result.series[{id,label,handle,points,color,layer,linetype,first,last}]`、`result.legend{enabled,anchor,size,rows[],handles[],texts[]}`；
> `verify{seriesChecked,legendRows,labels}` 与 `SERIES_*` / `LEGEND_MISSING` / `LABEL_POLICY_MISMATCH` 断言。
> 数据侧文档即以此为准；模型侧字段无改动（v2 冻结）。
