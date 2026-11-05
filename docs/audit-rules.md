# 图纸体检 audit（规则引擎 + 报告 + 门禁）

> 只读审计层：**不碰 CAD COM**，只消费 `scripts\cad.ps1 info/read` 已经产出的 JSON，按机械规则出一份「问题清单 + 人读报告」。
> 面向场景：水利规划院既有图纸出图前体检（图层/线型/文字/重复/越界/单位），**不做图框、会签栏、标题栏规则**（用户明确不需要）。
> 交付物：`scripts\audit\audit.py`（规则引擎）、`scripts\audit\run.ps1`（串联 + 门禁）、本文件、`tests\audit\**`（夹具/自测/真机验证）。
> 2026-11-04 实测：**17 条规则**；真机（ZWCAD 2025 / Classic 16 / AutoCAD 2007 读回）验证 4 份数据，**干净图 0 误报**，真实水系图 624 实体 3 条命中全部真报；问题夹具 21 条命中里仅 1 条启发式误报（原因见 §9.4）。

---

## 1. 定位与数据流

```
cad.ps1 info ──┐                      ┌─ report.json（机器可读，含每条问题 rule/severity/handle/layer/sample/suggest）
               ├─> info.json ──┐      │
               │               ├─> audit.py ─┼─ report.md （人读：结论 / 按规则统计 / 问题清单 / 未执行项 / 阈值）
cad.ps1 read ──┘               │      │
               └─> read.json ──┘      └─ stdout    （ASCII 一行摘要 + 每规则计数，GBK 控制台不乱码）
```

三条硬边界：

1. **只读**：audit.py 不写图、不调 COM、不需要 CAD 在跑；拿到 JSON 就能跑（可离线、可在 CI 里跑）。
2. **不猜**：规则需要的字段缺失时，该规则进 `skipped[]` 并写明 `not_measured` / `no_input`，**不会假装通过**。
3. **中文只在文件里**：`.ps1` 纯 ASCII（PS 5.1 无 BOM 会按 ANSI 解析），控制台输出保持 ASCII，中文报告落在 `.md/.json`。

## 2. 快速开始

```powershell
# 一条命令：起/附着 CAD → info → read → 审计 → 出报告（推荐日常用法）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\audit\run.ps1 `
    -ProgId ZWCAD.Application.16 -Doc "<图纸目录>\某工程.dwg" -OutDir tmp\audit\某工程 -ExitCode

# 已开着图纸（不启动新实例）：同上，去掉 -Doc 之外的差别 —— 用 -Doc 精确指定图纸全路径即可
# 产品选择（本机装了三款，不显式指定会附着到最后启动的那个）：
#   -ProgId ZWCAD.Application.16     中望 Classic（走 LISP 桥，read 快）
#   -ProgId ZWCAD.Application.2025   中望 2025（**无 LISP 桥**，read 全走 COM，慢）
#   -App autocad                     AutoCAD 2007（无 LISP 桥，read 走 COM）

# 只审计已有 JSON（不起 CAD，适合回归/CI/复盘）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\audit\run.ps1 `
    -InfoJson tests\audit\real\real-map.info.json -ReadJson tests\audit\real\real-map.read.json

# 直接调引擎
python scripts\audit\audit.py --info info.json --read read.json --md report.md --json report.json --exit-code
python scripts\audit\audit.py --list-rules                  # 规则清单
python scripts\audit\audit.py --read read.json --rules duplicate_entity,text_empty   # 只跑指定规则
```

`run.ps1` 参数：`-ProgId/-App/-Doc/-Max(20000)/-Scope(model)/-Launch/-InfoJson/-ReadJson/-Rules/-Config/-OutDir(tmp\audit)/-Json/-Md/-Python/-TimeoutSec(180)/-ExitCode/-Quiet`。

**退出码语义**（两个入口一致，便于当门禁用）：

| 退出码 | 含义 |
|---|---|
| 0 | 审计跑完且**无 blocker**（有 major/minor 仍是 0，只是报告里有清单） |
| 2 | 输入/流程失败：JSON 缺失、`ok:false`、`cad.ps1` 返回非 0（含 read 超时） |
| 3 | **存在 blocker**，且调用时带了 `-ExitCode` / `--exit-code`（门禁失败） |
| 4 | `audit.py` 自身失败（run.ps1 包装层；直接调 audit.py 时是参数错 2 / 内部错 1） |

> 默认只有 `layer_off_but_used`（冻结/关闭图层上仍有实体）是 **blocker**：它会让内容在打印/交付时**静默丢失**。
> `units_missing`、`text_overlap` 等默认是 major/minor —— 本院单比例图纸 INSUNITS=0 极为常见，若直接判 blocker 会让门禁永远红。
> 要调整门禁，改 `--config` 里的 `severity`（见 §7），例如 `{"severity":{"units_missing":"blocker"}}`。

## 3. 输入契约（用到了 info/read 的哪些字段）

| 来源 | 字段 | 用途 |
|---|---|---|
| info | `units.{insunits,dimscale,ltscale,textsize}` | `units_missing` / `linetype_scale` / `dim_scale_invalid` |
| info | `extents.{min,max}` | `outside_extents`（退化时改用实体分布近似，见 §6.11） |
| info | `layers[].{name,color,frozen,locked,linetype}` | `layer_empty` / `layer_naming` / `layer_off_but_used` / `linetype_missing` |
| info | `textStyles[].{name,height}` | 文字字高兜底（`height=0` 时取样式高度）、`text_style_missing` |
| info | `counts.{model,paper,blocks}` | 范围提示（paper>0 → 空图层可能误判；blocks>2 → 块内实体未读取） |
| read | `entities[].{handle,kind,layer,space,bbox,text,height,insertion/pos,points/vertices,closed,start,end,center,radius,linetype}` | 全部实体类规则 |
| read | `counts.{model,paper,returned}` / `truncated` / `scope` | 报告元信息；`truncated:true` 会出 `READ_TRUNCATED` 警告 |

**info 不带 linetypes 表**（本机 helper 的 `info` 没有该字段，只有 `layers[].linetype`）：`linetype_missing` 因此有两条路径 ——
配置里给了 `linetype_missing.loaded` 就严格比对；没给就退化为「标准线型名白名单」启发式，并且**只给 minor + 明确 warning**。

## 4. 规则清单（17 条 = 14 条必备 + 3 条增强）

| # | rule | 默认级别 | 判据一句话 | 主要误报边界 |
|---|---|---|---|---|
| 1 | `units_missing` | major | `INSUNITS` 缺失 / =0 / 不在 1~20 | 本院图常为 0；只提示不改图 |
| 2 | `layer_off_but_used` | **blocker** | `layers[].frozen=true` 的图层上仍有实体 | `frozen` 来自 `Layer.Freeze`；若某版本回落到 `LayerOn` 会极性相反（见 §11.6） |
| 3 | `duplicate_entity` | major | 同 kind+图层+空间+几何（容差内）完全重复 | 容差默认 1e-6；镜像/旋转后的等价不算 |
| 4 | `zero_length_line` | major | LINE 起点=终点，或多段线总长≈0 | 极小构件（<1e-6 图纸单位）才会命中 |
| 5 | `unclosed_polyline` | major | 多段线**几乎闭合**（缺口 ≤ 周长 2%）却没闭合 | 真正画的开放折线（河岸线/地面线）不报，见 §6.5 |
| 6 | `linetype_missing` | major | 实体/图层引用的线型未加载 | info 无线型表时退化为白名单启发式 → 自定义线型会误报（§9.4） |
| 7 | `linetype_scale` | major | `LTSCALE<=0` 或超出 [1e-6, 1000] | CAD 通常会把 0 自动修成 1，所以真机少命中 |
| 8 | `dim_scale_invalid` | minor | `DIMSCALE<=0` | 注释性图纸 0 是合法写法，故只给 minor |
| 9 | `text_empty` | major | TEXT/MTEXT 去格式后为空串 | 全是空格的文字也会命中（本来就是脏数据） |
| 10 | `text_overlap` | major/minor | 同图层同空间两文字包围盒重叠 ≥30%（≥60% 记 major） | 旋转/右对齐/居中对齐文字按「插入点+估算宽度」算框，可能误判（§11.5） |
| 11 | `outside_extents` | major/minor | 实体包围盒超出 `EXTMIN/EXTMAX`；远离 2× 图幅记 major | EXTMIN/EXTMAX 是**上次保存值、CAD 不重算**；退化时走分位数近似（§6.11） |
| 12 | `dim_no_measurement` | major | 标注缺测量值/文字被覆盖为空 | **read 不返回标注测量值** → 本版恒为 `not_measured`（§10） |
| 13 | `text_height_outlier` | minor | 字高 <0.4× 或 >3× 同层中位数（同层样本 ≥8 才用层中位数） | 图名/标题字号本来就大 → 默认豁免全图最大的 1 个字号（§6.13） |
| 14 | `layer_empty` | minor | info 里的图层在 read 范围内无任何实体 | 只被布局/块参照使用的图层会误判（paper>0 或 blocks>2 时给 warning） |
| 15 | `layer_naming` | minor | 含空格 / 含非法字符 / `>31` 字符 / 与已有层仅大小写不同 | **中文图层名默认不算问题**（院内常态），`allow_cjk=false` 才检查 |
| 16 | `layer_unknown` | minor | 实体所在图层不在 `info.layers` 里 | 通常是 info 与 read 不是同一图纸/同一时刻 |
| 17 | `text_style_missing` | minor | 文字样式不在 `textStyles` 里 | 只在 info 提供 textStyles 时执行 |

每条问题固定输出：`{rule, severity(blocker|major|minor), handle, layer, space, kind, sample, suggest}`。
`handle` 可直接在 CAD 里 `(handent "1EC")` 定位；文件级问题（单位/全局比例/汇总越界）`handle=null`。

## 5. 缺数据时的降级（不会假装通过）

| 情况 | 结果 |
|---|---|
| 只给 `--read`，没有 `--info` | 图纸级规则进 `skipped`：`units_missing`/`layer_empty`/`layer_naming`/`layer_off_but_used`/`linetype_scale`/`dim_scale_invalid`/`layer_unknown`/`text_style_missing`；实体级规则照常 |
| `info` 没有 `extents` 或 min≥max | `outside_extents` 改用实体分布 5%/95% 分位数近似，并出 `EXTENTS_STALE` 警告 |
| `info` 没有 `textStyles` | `text_style_missing` → `not_measured` |
| 文字 `height=0` 且样式高度也为 0 | 该文字排除在字高统计外，出 `TEXT_HEIGHT_UNKNOWN` 警告 |
| 文字 < 10 个（或同层 < 8 个） | `text_height_outlier` → `not_measured`（中位数不可信） |
| 无 DIMENSION 实体 | `dim_no_measurement` → `no_dimensions`（正常） |
| 有 DIMENSION 但 read 无测量值 | `dim_no_measurement` → `not_measured`（本版已知限制） |
| `read` 被 `-Max` 截断 | `READ_TRUNCATED` 警告；报告头部标注「已截断」 |
| 命中数 > `max_issues_per_rule` | 只列前 N 条，`byRule.suppressed` 记录被折叠的条数，总数不丢 |

## 6. 逐条判据与阈值

> 所有阈值都可用 `--config` 覆盖，默认值见 §7。

**6.1 `units_missing`**：`info.units.insunits` 缺失或 0 → major；不在 1~20 → minor。`SUGGEST` 指向 UNITS 命令取值（4=毫米、6=米）。

**6.2 `layer_off_but_used`**：`layers[].frozen is True` 且该层上有实体（按层汇总一条，`sample` 列前 3 个 handle）。关闭/冻结层不被打印，交付即丢内容 → **唯一的默认 blocker**。

**6.3 `duplicate_entity`**：几何签名 = `(kind, layer, space, 归一化几何)`；LINE 端点排序后比较、多段线顶点正反向取字典序较小者、TEXT 比 `(文本, 插入点, 字高)`、CIRCLE/ARC 比圆心半径、其它退化到 bbox 签名。容差 `tol` 默认 1e-6（以 `round(v, ceil(-log10(tol)))` 量化）。

**6.4 `zero_length_line`**：LINE 两端距离 ≤ `tol`；多段线所有相邻段长度之和 ≤ `tol`。

**6.5 `unclosed_polyline`**（默认 `mode=near_miss`）：
```
gap       = |首点 - 末点|
perimeter = 折线（不含首末缺口）的长度
ratio     = gap / perimeter
命中条件  = points>=3 且 closed!=true 且 gap>tol 且 ratio <= max_gap_ratio(0.02)
```
设计理由：图纸上「本应闭合的边界」漏点闭合时，缺口相对周长极小（千分之几）；而河岸线、地面线这类**本来开放**的折线 gap≈周长，不会命中。要查所有开放折线用 `{"unclosed_polyline":{"mode":"any"}}`（此时 severity 降为 minor）。

**6.6 `linetype_missing`**：收集实体引用的线型（`linetype` 为 `ByLayer` 时取图层线型），跳过 `BYLAYER/BYBLOCK/CONTINUOUS`。
- 有已加载集合（`info.linetypes` 或 config `loaded`）→ 不在集合里 → major；
- 无已加载集合（本机现状）→ 不在标准白名单（CONTINUOUS/DASHED/HIDDEN/CENTER/DASHDOT/PHANTOM/BORDER/DIVIDE 及 `ACAD_ISO*W100` 等）→ minor + warning。**自定义线型会被误报**，把名称加进 `linetype_missing.standard` 或给 `loaded` 即可消除。

**6.7 `linetype_scale`**：`units.ltscale <= 0` → 稿；超出 `[min,max]` → minor。报告里给的是全局 LTSCALE —— **实体级 `LinetypeScale` 在 read 里没有**，本版不查（§10）。

**6.8 `dim_scale_invalid`**：`units.dimscale <= 0` → minor（注释性图纸 DIMSCALE=0 合法）。

**6.9 `text_empty`**：TEXT/MTEXT 原文本经 MTEXT 格式清理（`\P`→换行、`\fSimSun|b0;` 之类格式码、花括号）后 `strip()` 为空。注意 `"<>"`、`"%<>%"` 这类是标注/字段占位，**属于正常**，不在本规则范围（那是 DIMENSION 的文本覆盖）。

**6.10 `text_overlap`**：同 `(layer, space)` 内两两比较包围盒，`overlap_area / min(areaA, areaB) >= 0.30` 命中；`>= 0.60` 记 major。包围盒估算：
```
宽 = Σ 每字宽度系数 × 字高 （CJK/全角=1.0，拉丁=0.62）
高 = 行数 × 字高 × 1.2     （MTEXT 按 \P 分行）
锚点 = 插入点（左下角），忽略旋转/对齐方式
```
两段**文本+位置+字高完全相同**的重叠不重复报（那是 `duplicate_entity` 的活）。

**6.11 `outside_extents`**：
- 正常路径：实体 bbox 超出 `[EXTMIN/EXTMAX]` 且超出量 > `tol_ratio(2%) × 对角线` → 命中；离图中心 > `far_factor(2) × 图幅跨度` → major。
- 退化路径（`EXTMIN/EXTMAX` 缺失或 min≥max，**真机很常见**：CAD 打开时不重算，只返回上次保存值）：取所有模型空间实体 bbox 角点的 5%/95% 分位数当「图面范围」，整体落在范围外的实体命中，`sample` 前缀标注「（按实体分布分位数近似）」，并出 `EXTENTS_STALE` 警告。`fallback:"off"` 可关闭近似改为 `not_measured`。
- 命中数 > `flood_threshold(20)` 时合并成 1 条汇总问题（避免"整张图都越界"的刷屏，通常是 extents 过时）。

**6.12 `dim_no_measurement`**：read 里 DIMENSION 实体没有 `measurement/value/...` 字段（本机 helper 现状）→ 有标注时恒为 `not_measured`；若将来 read 补了测量值，则 `测量值缺失且文本覆盖为空` → major。

**6.13 `text_height_outlier`**：
```
med = 同层样本 >= 8 时用层中位数，否则用全图中位数
命中 = h/med < 0.4 或 h/med > 3.0
图名豁免：全图最大的 title_skip_largest(1) 个字号，若 h/med <= title_max_ratio(8) 则从「偏高」侧豁免
```
真机实测：水系图图名 18mm vs 正文 4mm（4.5×）——**这是合法图名，不该报**，故加豁免；豁免生效时出 `TEXT_TITLE_EXEMPTED` 警告，透明可查。荒谬字号（>8×）仍会报。

**6.14 `layer_empty`**：info 里有、read 里没有实体的图层；默认忽略 `0/Defpoints` 与 `$0$*/*` 前缀。`info.counts.paper>0` → 额外 warning（布局内实体未读取）；`counts.blocks>2` 且有空层命中 → `BLOCKS_NOT_READ` warning（块内实体未读取）。

**6.15 `layer_naming`**：命中任一即报：含空白、含 `< > / \ " : ; ? * | = \` ,` 之一、长度 > 31、与前面某层仅大小写不同。**默认 `allow_cjk=true`**（中文图层名是院内常态，不判死；设 false 才把非 ASCII 当问题，仍是 minor）。

**6.16 `layer_unknown`**：实体图层不在 `info.layers`。>5 个则合并 1 条。通常意味着 info 与 read 不是同一图纸/同一时刻（`-Doc` 漂移、两次读取之间删了图层）。

**6.17 `text_style_missing`**：文字 `style` 不在 `textStyles`（按层/样式汇总）。CAD 会静默换成默认样式，字体/字宽可能变。

## 7. 配置（`--config <json 文件或内联 JSON>`）

只写想覆盖的键即可，其余走默认（深度合并）。完整默认值：

```json
{
  "max_issues_per_rule": 200,
  "rules_disabled": [],
  "severity": {
    "units_missing": "major", "layer_off_but_used": "blocker", "duplicate_entity": "major",
    "zero_length_line": "major", "unclosed_polyline": "major", "linetype_missing": "major",
    "linetype_scale": "major", "dim_scale_invalid": "minor", "text_empty": "major",
    "text_overlap": "major", "outside_extents": "major", "dim_no_measurement": "major",
    "text_height_outlier": "minor", "layer_empty": "minor", "layer_naming": "minor",
    "layer_unknown": "minor", "text_style_missing": "minor"
  },
  "layer_empty":       { "ignore": ["0", "Defpoints", "DEFPOINTS"], "ignore_prefix": ["$0$", "*"] },
  "layer_naming":      { "max_len": 31, "allow_cjk": true, "forbid_space": true,
                         "forbidden_chars": "<>/\\\":;?*|=`,", "exempt": ["0", "Defpoints"] },
  "layer_off_but_used":{ "report_entities_per_layer": 3 },
  "linetype_missing":  { "loaded": null, "heuristic": true, "standard": ["BYLAYER","BYBLOCK","CONTINUOUS","DASHED","HIDDEN","CENTER","DASHDOT","DOT","BORDER","DIVIDE","PHANTOM","ACAD_ISO02W100","ACAD_ISO10W100", "..."] },
  "linetype_scale":    { "min": 1e-6, "max": 1000.0 },
  "dim_no_measurement":{ "measurement_keys": ["measurement", "value", "dimensionValue", "measurementValue"] },
  "text_height_outlier": { "ratio_low": 0.4, "ratio_high": 3.0, "min_samples_global": 10,
                           "min_samples_per_layer": 8, "title_skip_largest": 1, "title_max_ratio": 8.0 },
  "text_overlap":      { "overlap_ratio": 0.30, "major_ratio": 0.60,
                         "cjk_width": 1.0, "latin_width": 0.62, "max_pairs": 400000 },
  "zero_length_line":  { "tol": 1e-6 },
  "duplicate_entity":  { "tol": 1e-6 },
  "unclosed_polyline": { "tol": 1e-6, "mode": "near_miss", "max_gap_ratio": 0.02, "min_perimeter": 0.0 },
  "outside_extents":   { "tol_ratio": 0.02, "far_factor": 2.0, "flood_threshold": 20, "fallback": "auto" }
}
```

常用配方：

```json
// 1) 院规：图层必须 ASCII 命名
{ "layer_naming": { "allow_cjk": false } }
// 2) 门禁更严：单位、文字重叠一起算 blocker
{ "severity": { "units_missing": "blocker", "text_overlap": "blocker" } }
// 3) 自定义线型多、不想看到启发式提示
{ "linetype_missing": { "loaded": ["CONTINUOUS","WATER_DASH","分水界","CAD_ISO02W100"] } }
// 4) 只关心会丢内容的项
{ "rules_disabled": ["layer_empty","layer_naming","text_height_outlier","dim_scale_invalid","layer_unknown","text_style_missing"] }
// 5) 图框层/示意层豁免空图层
{ "layer_empty": { "ignore": ["0","Defpoints","TK","图框","视口"] } }
```

## 8. 报告

### 8.1 `report.json`（机器可读）

```json
{
  "ok": true, "tool": "cad-cooperate/audit", "schema": "cad-audit/1", "generatedAt": "...",
  "inputs": {"info": "...", "read": "..."},
  "source": {"progId": "ZWCAD.Application.2025", "product": "ZWCAD 2025", "pid": 50424,
             "doc": "...dwg", "scope": "model", "readPath": "com", "truncated": false,
             "returned": 45, "model": 45, "paper": 0},
  "counts": {"entities": 45, "layers": 8, "texts": 21, "polylines": 2, "lines": 19, "dimensions": 1, "other": 2},
  "summary": {
    "issues": 21,
    "bySeverity": {"blocker": 1, "major": 11, "minor": 9},
    "byRule": {"layer_off_but_used": {"count": 1, "severity": "blocker", "reported": 1, "suppressed": 0, "label": "..."} },
    "rulesEnabled": ["..."], "rulesWithHits": 13,
    "skipped": [{"rule": "dim_no_measurement", "reason": "not_measured", "detail": "..."}],
    "warnings": [{"code": "LINETYPE_SET_UNKNOWN", "message": "..."}]
  },
  "gate": {"blockers": 1, "pass": false, "exitCode": 3},
  "issues": [{"rule": "...", "severity": "...", "handle": "...", "layer": "...", "space": "model",
              "kind": "...", "sample": "...", "suggest": "..."}],
  "config": {"...": "本次生效的阈值快照，便于复现"}
}
```

### 8.2 `report.md`（人读，节选自重跑产物 `tests\audit\out\problem-rich.report.md`）

```markdown
# 图纸体检报告
| 项 | 值 |
|---|---|
| CAD 产品 | ZWCAD 2025（ZWCAD.Application.2025，pid 50424） |
| 实体数 | 45（文字 21 / 多段线 2 / 线 19 / 标注 1 / 其他 2） |
| 图层数 | 8 |

## 一、结论
| 指标 | 数量 |
| 命中问题总数 | 21 |
| 阻断 blocker | 1 |
| 重要 major | 11 |
| 提示 minor | 9 |

结论：**不通过**（存在 1 个阻断问题）（门禁：blocker==0 时通过）

## 三、问题清单
### 阻断（blocker，1 条）
| # | 规则 | handle | 图层 | 问题 | 建议 |
| 1 | `layer_off_but_used` | 4C | FROZEN-L | 图层 FROZEN-L 已关闭/冻结，但仍有 2 个实体（handle: 4C, 4D） | 解冻该层或把实体移到可见层：冻结层上的内容不会被打印，交付时等于丢失 |

### 重要（major，11 条）
| 2 | `duplicate_entity` | 47 | BODY | LINE 与 46 几何完全重复（图层 BODY，容差 1e-06） | 删除重复实体（OVERKILL 命令可批量清理）… |
| … | `zero_length_line` / `unclosed_polyline` / `linetype_scale` / `text_empty` / `text_overlap` / `dim_scale_invalid` | | | | |

## 四、未执行的检查（数据不足，不等于通过）
| 规则 | 原因 | 说明 |
| `dim_no_measurement` | not_measured | 图上有 1 个标注实体，但 read 不返回标注测量值… |

## 五、数据源警告
- **LINETYPE_SET_UNKNOWN**：info 未返回已加载线型表，linetype_missing 退化为标准线型名启发式判断…
- **TEXT_TITLE_EXEMPTED**：2 个字号被视为图名/标题…
- **BLOCKS_NOT_READ**：read 只覆盖模型空间，info 报告 4 个块定义…
```

## 9. 验收记录（真机，2026-11-04）

### 9.1 环境与方法

- 产品：**ZWCAD 2025**（`ZWCAD.Application.2025`，每轮 `launched:true` 的独占实例，本轮 49344 / 50424 / 55344 三轮均自起自退）+ 复用既有真机读数（`tmp\compare-real\zwcad16|zwcad2025|autocad2007`，三款产品 info/read schema 一致，M1 已交叉核对）。
- 流程：`open -Doc`（只读）→ `run.ps1`（info + read + audit.py）→ `finish -Quit` 只退自己 pid → `Get-Process zwcad*,acad*` **归零**。
- 原始 JSON 快照归档在 `tests\audit\real\`，报告在 `tests\audit\out\`，机器断言在 `tests\audit\test_audit.py`（**98 项全 PASS**）；最近一次真机验证的完整日志/摘要：`tests\audit\out\real-validation.log`、`real-validation-summary.json`（`validation exit=0`，两个用例 `issues=21/blockers=1` 与 `issues=0/blockers=0` 均符合预算）。
- 复跑真机：`powershell -File tests\audit\run-real-validation.ps1`（自带「目标产品已有实例就拒绝运行」的前置闸门，防止 `-Launch` 附着到别人的实例）。

### 9.2 四份数据的命中汇总

| 数据集 | 来源 | 实体 | 命中 | blocker/major/minor | 未执行项 | 判定 |
|---|---|---|---|---|---|---|
| `problem-rich.dxf` | 真机 ZWCAD 2025 读回 | 45 | **21** | 1 / 11 / 9 | dim_no_measurement（not_measured） | 设计命中，见 §9.4 的 1 条启发式误报 |
| `clean.dxf` | 真机 ZWCAD 2025 读回 | 14 | **0** | 0 / 0 / 0 | dim_no_measurement（no_dimensions） | **误报 0** |
| 示例流域水系分布图（2011 规划修编）.dwg | 真机 ZWCAD 2025 读回 | **624** | **3** | 0 / 0 / 3 | dim_no_measurement（no_dimensions） | **误报 0**（3 条全真，见 §9.3） |
| 断面图（真机 read，`tmp\compare-real\zwcad16`） | 复用既有真机读数 | 87 | **2** | 0 / 1 / 1 | 9 条 no_input（无 info） | **误报 0**（文字压字，截图证实） |

### 9.3 真实图纸逐条判定（误报分析）

**示例流域水系分布图（624 实体 / 9 图层 / insunits=4 / 无标注）—— 3 条命中，全部真报：**

| # | 规则 | 级别 | 内容 | 判定 | 说明 |
|---|---|---|---|---|---|
| 1 | `layer_empty` | minor | 图层 `电站` 在模型空间无实体 | **真报**（价值低） | 该层确实空；但 read 不含块内实体，已随报 `BLOCKS_NOT_READ` 提示需人工核对块内使用 |
| 2 | `layer_empty` | minor | 图层 `补充` 在模型空间无实体 | **真报**（价值低） | 同上 |
| 3 | `outside_extents` | minor | handle `E6C`（图层1）的 MTEXT 包围盒 y 到 867，超出保存的 `EXTMAX.y=754`（容差 19.2） | **真报** | 该实体是竖排图名（13 行 × 18mm），确实在已存 extents 之外 → 说明 `EXTMIN/EXTMAX` 是旧值；工程师视角属"extents 该重算"的低危项 |

**修复前后对比**：加「图名豁免」前，该图还会多报 1 条 `text_height_outlier`（图名 18mm vs 中位数 4mm = 4.5×）——**这是设计上唯一修掉的真实误报**，现由 `title_skip_largest` 豁免并随报 `TEXT_TITLE_EXEMPTED`。
另外 `linetype_missing` 对 `分水界/县界` 图层使用的 `ACAD_ISO02W100 / ACAD_ISO10W100`（ISO 线型）**未误报**（已在标准白名单内）；中文图层名 `分水界/公路/河流/水库/电站/县界/图层1/补充` **未误报**（`allow_cjk=true` 默认）。

**断面图（87 实体，无 info）—— 2 条命中，均为真报：**

| # | 规则 | 级别 | 内容 | 判定 |
|---|---|---|---|---|
| 1 | `text_overlap` | major | 高程 `23.13` 与点号 `36` 重叠 ~88% | **真报**：截图 右半段 35~40 号点一带高程与点号确实压在一起（与 M1 验收记录的"标注过密"问题一致）（已按脱敏要求移除） |
| 2 | `text_overlap` | minor | 高程 `21.80` 与点号 `35` 重叠 ~31% | **真报**（轻度贴边） |

该数据集**没有 info**，所以 9 条图纸级规则是 `no_input`，`outside_extents` 走分位数近似（`EXTENTS_STALE` 警告）——报告明确标注"未执行 ≠ 通过"。

### 9.4 问题夹具（`problem-rich.dxf`）的 21 条命中与唯一误报

夹具按「每条规则至少一个实例」构造（`tests\audit\fixtures\problem-rich.dxf`，ezdxf 生成后由真机 CAD 读回）：

| 规则 | 命中 | 是否设计内 |
|---|---|---|
| `layer_off_but_used`（blocker） | 1 | ✔ 冻结层 `FROZEN-L` 上 2 条线 |
| `units_missing` / `layer_empty`×3 / `layer_naming`×2 | 6 | ✔ `INSUNITS=0`、3 个空层、`" bad name"`、32 字符层名 |
| `duplicate_entity`×3 / `zero_length_line` / `unclosed_polyline` | 5 | ✔ 重复线/圆/文字、零长线、缺口 0.28% 的方框 |
| `linetype_scale` / `dim_scale_invalid` / `text_empty` | 3 | ✔ LTSCALE=0、DIMSCALE=0、空文字 |
| `text_overlap`×3 / `text_height_outlier`×2 | 5 | ✔ 同层压字、0.3mm 与 8mm 离群字（12mm 那个按图名豁免） |
| `linetype_missing` | 1 | **⚠ 启发式误报** |

**唯一误报（1/21 = 4.8%）**：`linetype_missing` 报了自定义线型 `WATER_DASH`。
- 原因：本机 `info` 不返回"已加载线型表"，规则退化为标准线型名白名单启发式，任何自定义线型都会被点名（只给 minor + `LINETYPE_SET_UNKNOWN` 警告）。
- 消除办法：`--config {"linetype_missing":{"standard":[…,"WATER_DASH"]}}` 或给 `loaded` 列表；helper 将来在 `info` 里补 `linetypes[]` 后该规则自动变严格（代码已支持）。
- 之所以保留启发式而不是默认 `not_measured`：自定义线型缺失在真机上也表现为"显示成实线"，值得提示；但**级别压到 minor**，不进 blocker。

### 9.5 误报率结论

| 数据 | 真报 | 误报 | 误报率 |
|---|---|---|---|
| 干净图（clean.dxf，真机） | 0 | 0 | 0% |
| 真实水系图（624 实体） | 3 | 0 | **0%** |
| 真实断面图（87 实体） | 2 | 0 | **0%** |
| 构造问题图（45 实体） | 20 | 1 | 4.8%（已知启发式边界，可配掉） |

## 10. 本版不支持（明确不做）

1. **图框 / 会签栏 / 标题栏规则** —— 用户明确不需要，本层不实现。
2. **标注测量值校验**（"标注数字是否等于实际长度"）：`read` 不返回 DIMENSION 的 `Measurement`，`dim_no_measurement` 恒为 `not_measured`。需要 helper 扩字段后才能做（规则里预留了 `measurement_keys`）。
3. **实体级线型比例**：`read` 不返回 `LinetypeScale`，只查全局 `LTSCALE`。
4. **外部参照 / 块内实体**：只审 `read` 返回的模型空间（或 `-Scope paper`）实体；块定义内部、XREF 内部不审（会出 `BLOCKS_NOT_READ` 提示）。
5. **文字旋转/对齐**：按"插入点 + 估算字宽"算包围盒；旋转文字、右/居中对齐文字的重叠判定可能偏保守或误判（见 §11.5）。
6. **几何压盖/线穿越文字、跨图层视觉冲突**：只做同层文字两两重叠，不做线穿字。
7. **符号库/字体缺失、打印样式（CTB）、图层颜色合规**：不在规则范围（颜色有 `layers[].color` 但本版不判）。
8. **自动修复**：审计层只出清单与建议，不改图（改图属 core-cad 的 `section/undo` 域）。

## 11. 已知限制与本机实测坑

**11.1 `-Launch` 在已有实例时只附着（重要）**
`open -Launch` 并不是"新起一个进程"，有同产品实例在跑时它会**附着**上去；随后 `finish -Quit` 会把那个实例**所有文档关掉并退出**。判据只有响应里的 `result.launched:true`（说明是本进程起的）。
纪律：**只 `finish -Quit` 自己 `launched:true` 的实例；对着别人的 pid 只用 `status`/`info`/`read`，收尾用不带 `-Quit` 的 `finish` 或干脆不动**。`tests\audit\run-real-validation.ps1` 已把「目标产品已有实例 → 拒绝运行」写成前置闸门。
`run.ps1` 自己也做了容错：带 `-Doc` 但该图纸没打开时，`info` 会拿到 `DOC_NOT_FOUND`，此时它会自动补一次 `open -Doc -ReadOnly` 再重试（冷启动再加 `-Launch`）。

**11.2 大图 read 的耗时与超时（必看）**
`cad.ps1 read` 的 helper 默认超时 60 s，按 **≈100 ms/实体**（Classic 16，COM 路径）估算，600 实体就撞墙。
实测：624 实体在 **ZWCAD 2025（无 LISP 桥，全 COM）** 耗时 **224 s（≈0.36 s/实体，比 Classic 的 COM 路径更慢）**；Classic 16 走 LISP 快速路径时只回句柄（不做几何审计），AutoCAD 2007 读数见 `tmp\compare-real`（87 实体，约 0.2~1.6 s）。
规避：`-TimeoutSec` 显式放大（run.ps1 已透传给 info/read 两个调用）—— **>500 实体给 300，走 ZWCAD 2025 给 600**；再大用 `-Max` 分批（报告会标 `READ_TRUNCATED`，结论只覆盖前 N 个实体）。默认 60 s 不够时会拿到 `ok:false / TIMEOUT`（run.ps1 退出码 2）。
`run.ps1` 的 `-TimeoutSec` 默认 180 —— 大图务必显式给。

**11.3 `EXTMIN/EXTMAX` 是上次保存值，CAD 打开时不重算**
真机验证：我们刻意把 DXF 头写成 `EXTMIN=(0,0)/EXTMAX=(100,60)`，CAD 读回来仍是这对旧值（`outside_extents` 因此退化到分位数近似）。工程含义：想用「越界」规则前应先在 CAD 里 `ZOOM Extents` 或 `AUDIT` 让边界重算并保存，否则报告里的坐标范围只是历史值。已实现近似兜底 + `EXTENTS_STALE` 警告。

**11.4 `info` 没有 `linetypes[]`**
只有 `layers[].linetype`，实体线型也只在 LINE 上返回。所以 `linetype_missing` 默认走白名单启发式（§6.6），自定义线型会误报（唯一已知误报源）。helper 若在 `info` 增加 `linetypes` 数组，本规则自动升级为严格判定，无需改调用方。

**11.5 文字包围盒是估算**
`read` 不返回文字旋转角、对齐方式、真实字宽因子。`TEXT` 的插入点默认为基线左端（本机读数里 `TEXT` 也没给 bbox），所以：
- 旋转 90° 的文字，估算框会横过来；
- 右/居中/`Fit` 对齐的文字，估算框会偏一侧；
- 字宽按 CJK=1.0×字高、拉丁=0.62×字高近似（真机核对基本吻合，但中西混排会有几个百分点误差）。
结论：`text_overlap` 适合发现"明显压字"，不适合做精确排版核对；阈值 30%/60% 就是为此留的余量。

**11.6 `layers[].frozen` 的极性风险**
helper 优先取 `Layer.Freeze`（True=冻结，正确）；若某产品不提供 `Freeze` 而回落到 `Layer.LayerOn`（True=打开），极性会相反 —— 那种情况下会整版误报"冻结层上有实体"。**判别方法**：看 info 里是不是几乎所有图层都 `frozen:true`（真机 Classic 16/2025 实测 `Freeze` 可用、极性正确；`FROZEN-L` 夹具读回 `frozen:true` 与关闭状态一致）。

**11.7 `-ProgId` 必须显式给**
本机装了三款产品（ZWCAD Classic 16 / ZWCAD 2025 / AutoCAD 2007）。不带 `-ProgId`/`-App` 时 helper 按默认顺序找，可能附着到最后启动的产品。`run.ps1` 默认 `-ProgId ZWCAD.Application.16`，跨产品验证请显式传。

**11.8 控制台编码**
`.ps1` 必须纯 ASCII（PS 5.1 无 BOM 按 ANSI 解析，中文字面量会变乱码并报莫名的语法错）；`run.ps1` 的控制台摘要刻意保持 ASCII（规则 id + 计数 + 路径）；中文只在 `.md/.json` 里。`audit.py` 的 stdout 也是 ASCII（`--quiet` 只有一行），避免 GBK 控制台把中文打成问号。

## 12. 复跑与自测

```powershell
# 离线全量自测（98 项断言：规则目录/夹具/严格配置/退出码/上限/无 info 降级/报告渲染/真机快照回归）
python tests\audit\test_audit.py

# 重新生成夹具（ezdxf 需要 tools\py-libs 在 PYTHONPATH）
$env:PYTHONPATH='<repo>\tools\py-libs'
python tests\audit\make_fixtures.py ; python tests\audit\make_clean_dxf.py

# 真机复跑（自起自退，自带"不做别人的实例"闸门）
powershell -NoProfile -ExecutionPolicy Bypass -File tests\audit\run-real-validation.ps1
```

| 文件 | 作用 |
|---|---|
| `scripts\audit\audit.py` | 规则引擎（17 条 + JSON/MD 报告 + 退出码） |
| `scripts\audit\run.ps1` | 串联 cad.ps1 info/read → audit.py，打印摘要，`-ExitCode` 门禁 |
| `tests\audit\make_fixtures.py` | 生成 `problem-rich.dxf` + 三套 JSON 夹具（离线确定性） |
| `tests\audit\make_clean_dxf.py` | 生成"零问题"干净图 `clean.dxf` |
| `tests\audit\test_audit.py` | 98 项离线断言（含真机快照回归预算） |
| `tests\audit\run-real-validation.ps1` | 真机验证（独占实例 + `launched:true` 判据 + 收尾归零） |
| `tests\audit\real\*.json` | 真机 info/read 快照（含 624 实体真实水系图、87 实体断面图） |
| `tests\audit\out\*.md/.json` | 验收报告样例 |

## 13. 变更记录

- 2026-11-04 v1.0：17 条规则、`run.ps1` 门禁、真机 4 份数据验证（干净图/真实水系图/真实断面图误报 0）、`text_height_outlier` 图名豁免与 `outside_extents` 分位数兜底两处针对真机误报的修正。
