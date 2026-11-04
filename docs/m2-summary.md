# M2 交付总结：A（断面图产品化）+ C（图纸体检）

> 2026-11-04 13:0x–13:5x。用户选定 A+C 两块并行推进，三位执行者（数据侧 / CAD 侧 / 审计侧）并行交付，Lead 做了跨产品集成验收。
> 相关证据：`tmp\real\`（数据）、`tmp\evidence-overlay\`（绘图）、`tmp\compare-overlay\`（三产品对比）、`tests\audit\out\`（审计报告）。

---

## A. 断面图产品化

### A-1 数据侧：主数据源改为水文数据库（`scripts\section\prepare.py`）

- 新命令：`--from-db <站码> [--date/--years/--last N] [--list-dates] [--summary] [--compare-source --from-excel <xls>]`，只读 URI 打开 `%HYDRO_DB%`（5.6 GB）。
- 覆盖：`cross_section` 表 **452,996 行 / 162 站 / 5,575 次施测 / 1965–2025**；按站查询 ~100 ms（无索引也够快）。
- **DB vs Excel 逐点对比**（示例站 2025 两次施测，183 比对点）：**88 处**是源 `.xls` 单元格被截断成无小数点短文本（`5.73→73`、`28.45→45`、`28.98→98`），DB 存完整值；另 **2 处**待人工核对（vert25/vert51，差 1–2 m，非截断模式）。结论：**DB 作为主数据源，xls 解析保留兜底**。
- schema v2（`tmp\real\SCHEMA-v2.md`，已冻结）：`params{}` + `series[{id,label,date,station,points,waterLevels,style}]` + `computed{}` + `dataQuality{}`；多序列共用全局 `elevBase/xRange`；DB 路径按 `vert_num` 顺序保留（不重排）、xls 路径仍按起点距排序。
- 现成模型：`tmp\real\db-overlay-<站码>.model.json`（4 序列 92/93/70/113 点，A1 1:4000×1:200）、`db-2025-01-19.model.json`（单序列 70 点）、`db-list-dates.json`（103 个可选日期）。

### A-2 绘图侧：多序列套绘 + 图例 + 标注密度（`scripts\helper\SectionOverlay.cs`）

| 能力 | 行为 |
|---|---|
| 多序列套绘 | 每序列一条 LWPOLYLINE，图层 `AI-SEC-GROUND-<id>` + 调色板显式色（1,3,5,2,4,6,7） |
| 图例 | 仅多序列；右上图内，冲突则上移右对齐；短线+日期；>3 条水位时水位标签入图例 |
| 标注密度自适应 | `labelW=max(3×字高, "88.88"宽)`、`every=ceil(n/fits)`；强制标首末/深泓/左右岸顶/水位交点近点；显式 `-LabelEvery` 优先 |
| 多水位 | 共享+序列级按高程去重；>1 条显式配色；≤3 条标 `label+elev`，>3 条只标高程 |
| 比例尺 | `-ScaleBar`（50/20/10 m 自动分档 + 里程 + `H 1:x V 1:y`），默认关 |
| 回读校验 | 新增 `SERIES_*` / `LEGEND_MISSING` / `LABEL_POLICY_MISMATCH`，失败整体回滚 |

### A 的实机验收（Lead 自己跑的三产品对比，非队友自报）

| 产品 | 实体数 | verify | section 耗时 | read 耗时 | undo |
|---|---|---|---|---|---|
| 中望 Classic 16 | 84 | ✅ | 662 ms | 217 ms | 84→0（543 ms）|
| 中望 2025 | 84 | ✅ | 2396 ms | 1527 ms | 84→0（336 ms）|
| AutoCAD 2007 | 84 | ✅ | 631 ms | 303 ms | 84→0（194 ms）|

**跨产品几何比对（`tests\cross_app_diff.py`）：三对全部 MATCH**（84 实体 × 3 组合）。
截图。（已按脱敏要求移除）

---

## C. 图纸体检 audit（`scripts\audit\`）

- **17 条规则**（14 必备 + 3 增强）：空图层、图层命名、关闭/冻结图层仍有实体（唯一 blocker）、线型缺失/比例、文字高度离群、文字重叠、空文字、零长线、重复实体、未闭合多段线、超出图形范围、单位缺失、标注比例异常等。
- **不假装通过**：数据不足的规则进 `skipped[]`（`not_measured`/`no_input`/`no_dimensions`），报告里明写"不要当作已通过"。
- 报告：Markdown + JSON（逐条 `rule/severity/handle/layer/sample/suggest`，handle 可回 CAD 用 `(handent ...)` 定位）；退出码 0 无 blocker / 2 输入失败 / 3 有 blocker 且 `-ExitCode` / 4 引擎失败。
- **真机误报率**（ZWCAD 2025 独占实例，自起自退）：

| 数据 | 实体 | 命中 | blocker | 误报 |
|---|---|---|---|---|
| 干净图 | 14 | 0 | 0 | **0** |
| 真实示例水系图 | 624 | 3 | 0 | **0**（2 空图层 + 1 竖排图名超出旧 extents） |
| 真实断面图 | 87 | 2 | 0 | **0**（标注压字，已截图核实） |
| 构造问题图 | 45 | 21 | 1 | 1（自定义线型启发式，已压 minor + warning） |

- 离线自测 **98 项全 PASS**；规则表/阈值/误报边界见 [audit-rules.md](audit-rules.md)；示例报告 `tests\audit\out\*.report.md`。

---

## 已知限制（写进后续待办）

1. **中望 2025 大图 read 慢**：624 实体实测 **224 s**（无 LISP 桥，≈0.36 s/实体）⇒ `-TimeoutSec` 需 300（2025 给 600），更大图用 `-Max` 分批。
2. **水位交点符号 / 水面宽 / 面积湿周** 尚未画到图上（A-3 候选）。
3. `-ScaleBar` 已实现但未进截图；图例无边框/标题。
4. AutoCAD 2007 冷启动 134 s（唯一硬伤，实战靠"附着已开实例"规避）。
5. audit 只覆盖模型空间；块定义内实体、外部参照内部未审计（报告已声明）。
