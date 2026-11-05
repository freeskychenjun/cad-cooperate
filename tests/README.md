# 断面图计算侧（section-gen）交付说明

> 对应 `DESIGN-M1.md` §4（断面图契约）/ §5（验收）。**本目录与 `scripts\section\**` 全部纯计算、零 COM，绝不启动 CAD。**
> 写作用域：`scripts\section\**`、`tests\**`。CAD 侧（`scripts\cad.ps1`、`scripts\helper\**`）属 core-cad 的域，勿改。

## 1. 交付物

| 文件 | 作用 |
|---|---|
| `scripts\section\prepare.py` | xlsx/xls → `section.model.json`（DESIGN §4.1 输入、§4.2 映射、§4.3 实体清单 + `computed`；`--profile yssd` 见 §9）。**CLI 薄壳**，实现在同目录 `prep_io`（IO/解析/告警/原子写）/ `prep_yssd`（实测大断面）/ `prep_db`（hydro_data.db + schema v2）/ `prep_model`（单一坐标映射与校验） |
| `scripts\section\qa_yssd.py` | 实测大断面成果表 QA：块清单、可疑点逐条、两次施测对比 → `.qa.json` + `.qa.md`（`--block N` 选定块真正生效，默认第 1 块；跨块对比仍覆盖全部块） |
| `tests\verify_model_self.py` | 模型**自洽**核对（任意 profile，独立重算 §4.2/§4.3 后比对 entities/computed/dataQuality） |
| `tests\make_fixtures.py` | 生成 `tests\fixtures\*.xlsx`（可重复再生） |
| `tests\make_expected.py` | **独立**按 §4.2/§4.3 算出 `section-30.expected.json`（不 import prepare.py） |
| `tests\verify_section.py` | 独立核对器：吃 model.json 或 `cad.ps1 read` 响应，逐项 PASS/FAIL；`--tamper` 负例自证 |
| `tests\check_dxf.py` | ezdxf 真实 DXF 内核往返复核（第三份实现，需 `tools\py-libs` 在 `PYTHONPATH`） |
| `tests\run_m1.ps1` | 端到端 runner（纯 ASCII + UTF-8 BOM + CRLF）；`-DryRunOnly` / `-NoCad` 无 CAD 全绿 |
| `tests\selftest.py` | 计算侧全量自测（41 项：基准/负例/反序/bad 用例/升档/变体/回读 schema） |
| `tests\selftest_sections.py` | 断面提取两条路线自测（**85 项**，纯离线合成夹具，写 `tmp\selftest-sections\`，不需要 `%TEMP%` 真实数据）：`section_lib` 几何/网格索引与暴力解等价、两个薄入口逐格断言（含 **宽表整表逐格数值/顺序**）、坏输入/缺库负例 |
| `tests\selftest_compare_source.py` | `--compare-source`（DB vs 源表逐点比对）自测（**84 项**，纯离线：脚本内合成 sqlite DB〔虚构站码 99000001〕+ 合成 yssd 多列块 xlsx〔含截断高程/偏差/缺批次〕，写 `tmp\selftest-compare-source\`）：`sourceCompare` 全字段、`byDate[]`/`differing[]` 数值、容差边界、`--drop-suspect` 组合口径、负例 BAD_ARGS/BAD_DB/BAD_DATA |
| `tests\coreself\` | helper 纯逻辑离线套件（csc 编译，零 CAD）：`CoreTests.cs`（JMap/CadError/Ctx/Json 往返/State 内存态，**103 断言**）、`ComCmdTests.cs`（COM 反射晚绑定 + 重试策略 + Cmd 括号/提示符守卫，**131 断言**）——由 `tests\selftest_lisp.ps1` 统一跑 |
| `tests\fixtures\section-30.xlsx` | 30 点合成断面（起点距 0..290 步长 10 m，高程 38.55..44.30 m，深泓第 15 点/140 m），表头行「起点距/高程」，D1/D2 附设计水位 42.30（供人读，程序从参数取） |
| `tests\fixtures\section-30.expected.json` | 验收基准（实体清单 + `expected.total` + `bbox` + `computed`） |
| `tests\fixtures\section-30-reversed.xlsx` | 同行序完全反向，验自动反转 + `ORDER_REVERSED` |
| `tests\fixtures\bad-2points.xlsx` / `bad-nonmono.xlsx` / `bad-empty.xlsx` | 边界负例（<3 点 / 非单调 / 空值） |

## 2. `prepare.py` 字段契约（与 core-cad helper 冻结于 2026-11-04）

```json
{
  "ok": true,
  "schema": "zwcad-section-model/1",
  "points": [[0.0, 44.20], [10.0, 44.00], "..."],      // 主形态 [起点距_m, 高程_m]，按行序
  "pointsDetail": [{"i":1,"offset":0.0,"elev":44.2}],   // 附加：1-based 点号（helper 可忽略）
  "waterLevels": [{"value":42.3,"label":"设计水位","text":"设计水位 42.30 m","y":85,"x0":-5,"x1":295}],
  "params": {"hScale":1000,"vScale":50,"paper":"A3","origin":[0,0],"labelEvery":1,
             "noAxis":false,"noPointNo":false,"noAutoScale":false,
             "layerPrefix":"AI-SEC","textHeight":2.0,"waterTextHeight":2.5},
  "source": {"file":"...xlsx","sheet":"断面","rows":32,"headerRow":1,"firstDataRow":2,
             "lastDataRow":31,"offsetColumn":1,"elevColumn":2,"cols":null,"pointCount":30},
  "scale": {"horizontal":1000,"vertical":50},
  "paper": "A3",
  "paperSize": {"name":"A3","widthMm":420,"heightMm":297,"usableWidthMm":400,"usableHeightMm":277,"marginMm":20},
  "origin": [0,0],
  "labels": {"elevHeightMm":2.0,"pointNoHeightMm":2.0,"waterHeightMm":2.5,"labelEvery":1,
             "showPointNo":true,"showAxis":true,"elevDecimals":2,"waterTextSuffix":" m","thalwegPrefix":"河底"},
  "style": {"textStyle":"AI-SEC-TEXT","textWidthFactor":1.0,"groundColor":"BYLAYER","groundLineWeight":0.3,
            "waterDash":"DASHED","waterExtendLeftMm":5,"waterExtendRightMm":5,"waterTextDyMm":2,
            "elevTextDyMm":2,"pointNoDyMm":3,"axisLeftHeightMm":10,"thalwegCircleRadiusMm":0.6,"thalwegTextDyMm":7},
  "layerPrefix": "AI-SEC",
  "layers": {"ground":"AI-SEC-GROUND","water":"AI-SEC-WATER","anno":"AI-SEC-ANNO","axis":"AI-SEC-AXIS","text":"AI-SEC-TEXT"},
  "entities": [ "..." ],
  "expected": {"total":67,"byType":{"LWPOLYLINE":1,"LINE":3,"TEXT":62,"CIRCLE":1},
               "byKind":{...},"byLayer":{"AI-SEC-GROUND":1,"AI-SEC-WATER":1,"AI-SEC-ANNO":63,"AI-SEC-AXIS":2},
               "textCount":62},
  "computed": {"scale":{...},"widthMm":290,"heightMm":115,"width_mm":290,"height_mm":115,
               "bbox":{"x0":0,"y0":10,"x1":290,"y1":125},"extent":{...},
               "elevBase":38.05,"elevMin":38.55,"elevMax":44.3,"offsetMin":0,"offsetMax":290,"spanM":290,
               "autoScaled":true,"autoScaledFrom":500,"autoScaledTo":1000,"orderReversed":false,
               "thalwegIndex":15,"thalwegElev":38.55,"fitWidth":true,"fitHeight":true,
               "textCounts":{...},"axisLines":2},
  "warnings": [{"code":"AUTO_SCALED","message":"断面超宽，hScale 自动升档 500 -> 1000","from":"500","to":"1000"}]
}
```

### `entities[]`（helper 可按坐标直接画；`role` 供核对，可忽略）

| kind | type | 字段 |
|---|---|---|
| POLYLINE | LWPOLYLINE | `points:[[x,y],…]`、`closed:false`、`color:"BYLAYER"`、`lineWeight:0.3` |
| LINE | LINE | `points:[[x0,y0],[x1,y1]]`、`dash:"DASHED"\|null` |
| TEXT | TEXT | `text`、`pos:[x,y]`、`height`、`style`、`rotation:0`、`align:"left"` |
| CIRCLE | CIRCLE | `center:[x,y]`、`radius` |

### CLI

```
python scripts\section\prepare.py --data <xlsx> --out <json> [--sheet 名称|序号] [--cols "起点距=高程"]
    [--h-scale 500] [--v-scale 50] [--paper A3|WxH] [--origin x,y]
    [--water "42.30=设计水位"]... [--label-every 1] [--no-axis] [--no-point-no]
    [--no-auto-scale] [--layer-prefix AI-SEC]
```

- 退出码：`0` 成功（落盘 + stdout 单段 ASCII 安全 JSON 摘要）；`2` 任何失败（stdout `{"ok":false,"error":{...}}`，**不产出文件**，先写临时文件再 `os.replace`）。
- 错误码：`BAD_DATA`（<3 点 / 非单调 / 空值 / 非数字 / 高程全等）、`TOO_WIDE`（`--no-auto-scale` 超宽或升档阶梯用尽）、`BAD_ARGS`、`BAD_SHEET`、`NO_FILE`、`BAD_FILE`、`INTERNAL`。
- stdout 摘要为纯 ASCII（中文走 `\u` 转义）⇒ PS 5.1 `ConvertFrom-Json` 不会踩编码坑；落盘 JSON 为 UTF-8 无 BOM 中文原文。

## 3. 口径（与 core-cad 逐条对过，2026-11-04）

1. **可用区**：A3 `420-20=400 × 297-20=277`（A4/A2/A1/A0 同规则 `幅面-20`，也支持 `WxH`）。
2. **自动升档**：候选 = `[请求 hScale] + {500,1000,2000 中 > 请求值}` 升序，取第一个满足 `(offMax-offMin)*1000/h <= 可用宽`；并发 `warnings:AUTO_SCALED{from,to}`。`--no-auto-scale` 或阶梯用尽 ⇒ `TOO_WIDE`。
   - 30 点 fixture（跨 290 m）在默认 1:500 下宽 **580 mm > 400** ⇒ **必升档到 hScale=1000，widthMm=290**。
3. **映射**（§4.2 逐字）：`elevBase = elevMin - 10*vScale/1000`；`x=(off-offMin)*1000/h`；`y=(elev-elevBase)*1000/v`；模型空间 1 单位 = 1 mm。
4. **bbox / widthMm / heightMm** = **地面线 bbox**（`x:0..W`、`y:10..125`，**不含**水位线左右各 5 mm 外延）：`widthMm=290`、`heightMm=115`。另给 `extent`（含基线 y=0 的实际绘图范围）。
5. **元素**（§4.3 #1–#7，默认 8 项共 67 实体）：地面线 1 + 水位线 1 + 水位文字 1 + 点高程 30 + 点号 30 + 基线 1 + 左竖线 1 + 深泓圆 1 + 深泓文字 1 = **67**。
6. **DESIGN 未固定的位置，本实现自定并已固化在 `style`/`labels` 中**（三份实现同一套常量，改口径要三处同改）：
   - 水位文字：贴水位线**右端**（`bbox.x1+5`）上方 2 mm，字高 2.5，左对齐；
   - 点高程：点上 2 mm，字高 2.0；点号：点下 3 mm，字高 2.0；
   - 深泓文字 `河底 38.55 m`：最低点**下方 7 mm**（避开点号 3+2 mm），字高 2.0；
   - 深泓圆：最低点圆心，r=0.6；
   - 基线 `(0,0)-(W,0)`，左竖线 `(0,0)-(0,10)`；
   - 高程/字数固定 `%.2f`。
7. **§4.3 #8 起点距刻度（`-Axis` 开）本版未实现**（默认关闭），模型不含刻度实体；`--no-axis` 只去掉基线+左竖线。

## 4. `verify_section.py`

```
python tests\verify_section.py --model <model.json 或 read.json> --expected <expected.json> [--tamper] [--tol 1e-6]
```

- 输入两种形态自动识别：`prepare.py` 的 model（含 `entities[].role`）或 `cad.ps1 read` 响应（`result.entities[].type/vertices|start,end|insertion|center`，AcDb* 名会归一）。read 形态无 `role` 时按「文字内容 + 最近位置」贪婪匹配；非 `layerPrefix` 图层实体自动过滤。
- 断言：实体总数 / 分类型 / 分图层 / 图层名 / 地面线顶点逐点（<1e-6）/ 水位线 y 与端点与线型 / 每处文字内容+位置+字高+图层+文字样式 / 基线+左竖线端点 / 深泓圆心半径 / bbox（<0.01 mm，§4.4）/ `computed` 各字段 / `points` 与 `pointsDetail` / `warnings`。
- 退出码 `0` 全绿、`1` 有 FAIL。
- `--tamper`：把地面线顶点#1 的 y +1.0 mm 后跑同一套断言，**抓到 FAIL 才算校验有效**（抓到→退出码 0；抓不到→1）。

## 5. `run_m1.ps1`

```
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run_m1.ps1 -DryRunOnly   # 无 CAD 全绿
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run_m1.ps1 -NoCad        # 同上
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run_m1.ps1 [-Doc <dwg>]  # 真画（需调用方已开图纸）
```

步骤：`prepare.py` → `verify` → `check_dxf`（ezdxf 往返）→ **`lisp.wire`（C# 离线 6 套，调 `tests\selftest_lisp.ps1`）** → `verify --tamper` → 3 个 bad 用例 → `cad.ps1 section -DryRun`（并与基准交叉核对 `result.expected`）→ 真画（`-DryRunOnly/-NoCad` 时跳过）→ `cad.ps1 read` → `verify`。

- **绝不启动 CAD**：不传 `-Launch`，只 attach 已开实例。真画/回读在无 CAD 或 CAD 未开图时判 `SKIP` 而非 FAIL（`NO_INSTANCE`/`NO_CAD`/`NO_DOC` 等错误码）。
- `--water` 由 runner 从 `expected.json` 运行时拼出（保证 `.ps1` 纯 ASCII）。
- 退出码：0 无 FAIL；1 有 FAIL。
- 编码纪律：`tests\run_m1.ps1` 必须是 **纯 ASCII + UTF-8 BOM + CRLF**（PS 5.1 按 ANSI 读含中文的 .ps1 会语法错）。改动后请重跑规范化：
  `BOM=EF BB BF`、无 >127 字节、`Parser::ParseFile` 无错。

## 6. `cad.ps1 read` 对 verify 的接口期待（已与 core-cad 对齐）

```
{"ok":true,"cmd":"read","result":{"readPath":"com","counts":{...},"truncated":false,"entities":[
  {"handle":"2F","type":"AcDbPolyline","kind":"LWPOLYLINE","layer":"AI-SEC-GROUND","vertices":[[x,y],...],"closed":false},
  {"handle":"30","type":"AcDbLine","kind":"LINE","layer":"AI-SEC-WATER","start":[x0,y0],"end":[x1,y1]},
  {"handle":"31","type":"AcDbText","kind":"TEXT","layer":"AI-SEC-ANNO","text":"...","insertion":[x,y],"height":2.5,"style":"AI-SEC-TEXT"},
  {"handle":"32","type":"AcDbCircle","kind":"CIRCLE","layer":"AI-SEC-ANNO","center":[x,y],"radius":0.6}]}}
```
缺几何字段时 verify 会降级（只做能做的项并标 SKIP，不误报 FAIL）。

## 7. 自测与当前结果（2026-11-05 更新）

```
python tests\selftest.py                        # 41 项 PASS
python tests\selftest_sections.py               # 85 项 PASS（断面提取两条路线）
python tests\selftest_compare_source.py         # 84 项 PASS（--compare-source，合成 DB + 源表）
python tests\audit\test_audit.py                # PASS 89 / FAIL 0 / SKIP 3
python tests\bench\selftest_checkers.py         # SELFTEST PASS
powershell -File tests\selftest_lisp.ps1        # C# 6 套：106+53+132+119+103+131 = 644 PASS
powershell -File tests\run_m1.ps1 -DryRunOnly -Python <python全路径>   # PASS 10 / FAIL 0 / SKIP 2（CAD 步按设计跳过）
```

以上全部已接进 `.github\workflows\offline-ci.yml`（push/PR，windows runner，零 CAD）：首次实跑 11 步全绿（[run 41943999965](https://github.com/freeskychenjun/cad-cooperate/actions/runs/41943999965)）。跑 CI 等价环境时注意两件事：`run_m1.ps1` 的 `-Python` 要传**解析后的路径**（它对参数做 `Test-Path` 预检）；runner 控制台默认 **cp1252**，本目录脚本已各自 `reconfigure(encoding="utf-8", errors="replace")` 自保。

- 30 点 fixture → **67 实体**：`LWPOLYLINE 1 / LINE 3 / TEXT 62 / CIRCLE 1`；`AI-SEC-GROUND 1 / WATER 1 / ANNO 63 / AXIS 2`。
- `bbox={x0:0,y0:10,x1:290,y1:125}`、`widthMm=290`、`heightMm=115`、`elevBase=38.05`、`scale=1000/50`、`autoScaled=true`。
- 变体计数：`--no-axis` 65、`--no-point-no` 37、`--label-every 2` 52、三者叠加 20、双水位 69。
- 三份独立实现（`prepare.py` / `make_expected.py` / `check_dxf.py`）互证一致（DXF audit 0 errors）。
- 与 core-cad helper 的 `-DryRun` 交叉核对：`result.expected` 的 `total/byType/byLayer` 与 `model.bbox/hScale` 全部一致；**唯一口径差异**：helper 的 `model.heightMm=125`（含 10 mm 底边距的绘图范围）vs 基准 bbox 高度 115（helper 自称只做 warning，不阻断）。

## 8. 已知待办 / 接口议题

1. helper `heightMm` 与 `widthMm` 语义不一致（建议 `heightMm` 取 bbox 高度 115，另给 `extentHeightMm=125`）。
2. 真画/回读全链需在 core-cad helper 稳定后由 lead 在已开图纸上跑 `run_m1.ps1`（本侧未启动过 CAD）。
3. §4.3 #8 起点距刻度未实现（默认关闭）。

## 9. 实测大断面成果表（`--profile yssd`，2026-11-04 增补）

真实数据：示例站 2025 实测大断面成果表（`.xls`）。**现有两列格式行为完全不变**（`selftest.py` 41 项回归验证）。
逐条证据与复现命令见本机 `tmp\real\README.md`（`/tmp/` 不入库，fresh clone 没有它）；**契约类文档已移入
`docs\section-model-v2.md`**（fresh clone 可读），本节 9.1–9.3 即是契约摘要。

### 9.1 识别与 CLI

- `--profile auto`（默认）：表头行里「起点距」「高程」各出现 ≥2 次即判为 `yssd`，否则走两列；
  `--profile yssd|two-col` 可强制。
- `.xls` 走 **xlrd**（`PYTHONPATH=tools\py-libs`；缺库报 `NO_XLRD`），`.xlsx/.xlsm` 仍走 openpyxl。
- 新增参数：`--block N` / `--date 9-18`（选施测块，默认第 1 块）、`--drop-suspect`、
  `--fail-on-suspect`、`--keep-label-rows`（默认过滤 左岸/右岸 标签行）、
  `--no-auto-water` / `--water-label`（yssd 默认把源表「测时水位」当水位线）。
- 解析流程：找表头 → 按 3 列一组切块（点号/起点距/高程）→ **逐组自上而下**收集 → 过滤
  `附注/备注/说明` 元信息行 → 标签行可留可弃 → **按起点距排序** → 质量闸门 → 复用 §4.2/§4.3 建模。
- 新增 warnings：`MULTIPLE_BLOCKS`、`WATER_FROM_FILE`、`DUPLICATE_OFFSET`、`NON_MONOTONIC`、
  `LABEL_ROWS_DROPPED`、`SUSPECT_DATA`、`SUSPECT_DROPPED`。
- 模型新增：`source.profile/block/blocks/ignoredRows/labelRowsDropped`、`dataQuality{...}`、
  `pointsDetail[{point,tag,offset,elev,offsetRaw,elevRaw,row,group,suspect,reasons}]`、`computed.profile/pointCount/suspectKept/suspectDropped`。

### 9.2 质量闸门（`dataQuality`，绝不静默丢数据）

| 判据 | 含义 |
|---|---|
| `ELEV_TEXT_TRUNCATED` | 高程单元格是文本、无小数点、长度 ≤2 —— 源表导出/录入被截断（实证 `73`←`5.73`、`03`←`6.03`、`28`←`28.24`） |
| `ELEV_OUT_OF_RANGE` | \|高程 − 本块中位数\| > 40 m |
| `CROSS_BLOCK_DIFF` | 与另一次施测同起点距高程差 > 10 m（**仅当对方原始值也带小数点/为真数值**才作判据，避免用坏值反诬好值） |

默认**保留但告警**（`suspect[]` 逐条含 点号/起点距/高程/原文/源行/列组/判据）；
`--drop-suspect` 剔除；`--fail-on-suspect` → `BAD_DATA` 退出码 2 不产出文件。

### 9.3 真实数据结果（示例站 2025）

```powershell
$env:PYTHONPATH='<repo>\tools\py-libs'
python tmp\real\build_all.py     # 4 模型 + QA 报告 + 边界/分支用例，输出 ALL = PASS
```

| 模型 | 块 | 点数 | 实体数 | 可疑点(留/剔) | 宽×高 (mm) |
|---|---|---|---|---|---|
| `tmp\real\sample-station-2025.model.json` | 1（1-19，水位 17.85） | 68 | 143 | 28/0 | 378.8×461.5 |
| `tmp\real\sample-station-2025.v2-dropped.model.json` | 1 | 40 | 87 | 0/28 | 375.4×123.5 |
| `tmp\real\sample-station-2025.block2.model.json` | 2（9-18，水位 17.33） | 111 | 229 | 60/0 | 378.8×475.0 |
| `tmp\real\sample-station-2025.block2-dropped.model.json` | 2 | 51 | 109 | 0/60 | 375.4×123.8 |

- 实体数公式（1 水位线、标签/点号/轴线全开）：`total = 2N + 7`（N=40→87，68→143，111→229）。
- 参数统一 `--paper A1 --h-scale 4000 --v-scale 200`：真实断面跨 1515 m，DESIGN 的 500→1000→2000 阶梯在
  A3（400 mm）下仍 771 mm → `TOO_WIDE`（已实测）；A1 可用区 821×574 才容得下含 98 m 尖峰的版本。
- 源表质量：块 1 有 28/68（41.2%）、块 2 有 60/111（54.1%）高程被截断，**文件内无法恢复**，
  已全部列进 `dataQuality.suspect[]`；两次施测同起点距匹配 40 点，差 >1 m 的 2 处两侧都不可信
（不构成真实河床变化证据）。
- `tests\verify_model_self.py` 对 4 个真实模型各 45/45 PASS（独立重算 §4.2/§4.3 后比对）。
- 边界：文件不存在 `NO_FILE`、表头失败/点数<3/`--fail-on-suspect` 均 `BAD_DATA`，全部 exit 2 且不产出文件。

## 10. 从水文数据库出断面 + schema v2 多序列（2026-11-04 增补，task-11）

> 结论：**`.xls` 的高程截断是源文件本身的问题**（单元格就是文本 `28`/`45`/`98`），而
> `hydro_data.db` 里存的是正确值（28.28/28.45/28.98）⇒ 主数据源改为 **DB**，xls 解析保留兜底。
> 契约见 `docs\section-model-v2.md`（**已脱敏入库**，原 `tmp\real\SCHEMA-v2.md`）；逐点证据见本机
> `tmp\real\DB-VS-XLS.md`（含真实站点数据，按脱敏要求不入库）。

### 10.1 新 CLI

```powershell
# 单序列（v2）
python scripts\section\prepare.py --from-db <站码> --date 2025-01-19 ^
    --out tmp\real\db-2025-01-19.model.json --paper A1 --h-scale 4000 --v-scale 200
# 多序列（4 次施测套绘，v2）
python scripts\section\prepare.py --from-db <站码> --years 2024-2025 ^
    --out tmp\real\db-overlay-<站码>.model.json --paper A1 --h-scale 4000 --v-scale 200
# 最近 N 次 / 按年份 / 列可选日期 / 每序列统计
python scripts\section\prepare.py --from-db <站码> --last 2 --out ...
python scripts\section\prepare.py --from-db <站码> --list-dates
python scripts\section\prepare.py --from-db <站码> --date 2025-01-19 --summary --out ...
# DB vs 源 xls 逐点比对（证明 xls 截断）
python scripts\section\prepare.py --from-db <站码> --years 2025 --compare-source ^
    --from-excel <xls> --out tmp\real\db-vs-xls-2025.model.json
```

- DB 只读 URI 打开（`file:...?mode=ro`）；库路径解析：`--db` 显式 > 环境变量 `HYDRO_DB`（**调用期读取**，
  `import` 之后再设也生效）；显式 `--db ""` 仍是 `NO_DB`、**不会**回退 env；
- `--date` 兼容 `YYYY-MM-DD` / `YYYY`（只有年份时取该年全部）/ DB 老记录 `'1965'` 形态（前缀匹配）；
  yssd 路径仍用 `9-18`/`9月18日`（同一参数按场景解释）。
- DB 路径**按 `vert_num` 顺序保留测点，不按起点距重排**（重排会破坏断面形态；非单调时报警
  `SERIES_DIST_NON_MONOTONIC`）；xls 路径仍按起点距排序 —— 两套口径分别写清。
- **schema v2**：`{schema:"zwcad-section-model/2", params{}, series[{id,label,date,station,points,
  waterLevels,style{color,linetype},color,linetype,pointCount}], computed{bbox,elevBase,xRange,
  seriesCount,pointCounts,...}, dataQuality{}, warnings[]}`；**不含 `entities[]`/`expected`**；
  多序列**共用** `elevBase`/`xRange`（取全体 min/max）。
- 水位：DB 无「测时水位」⇒ 取 `daily_water_level` 日均（label `日均水位`，2025-01-19=17.84 vs 源表测时 17.85）；
  `--water` 覆盖 / `--no-water-from-db` 关。
- `--compare-source` → `sourceCompare{dbPoints,compared,same,differingCount,tolerance,byDate[],differing[]}`
  + `warnings:SOURCE_COMPARE_DIFF`（有差异）/ `SOURCE_COMPARE_SAME`（全部一致）/
  **`SOURCE_COMPARE_UNMATCHED`**（所选日期在源表里没有对应施测块、`byDate[].matched=false`；修前这种
  情形会误报 `SOURCE_COMPARE_SAME`「比对 0 点…两者一致」）。
- **比对口径**：始终以 **DB 原始序列（删点前）** 为准，`--drop-suspect` 只影响出图模型 ⇒ 该组合下
  `sourceCompare.dbPoints/compared` 可能大于 `series[].pointCount`（比对=数据审计，模型=成图数据）。
- **已知限制**：源表表头只记「1月19日」这类**月-日**，DB 日期 `YYYY-MM-DD` 归一到 `M-D` 匹配；同一份源表
  含跨年同月日、或一次比对选中不同年份的同月日批次时，会命中源表第一个同月日块（其余无法区分），需人工复核
  （源表信息不足的固有歧义，不做猜测性修复）。离线覆盖：`tests\selftest_compare_source.py`（84 断言）。

### 10.2 产物与结果

| 文件 | 内容 |
|---|---|
| `tmp\real\db-overlay-<站码>.model.json` | **4 序列**：2024-01-30(92) / 2024-08-09(93) / 2025-01-19(70) / 2025-09-18(113)，共用 1:4000×1:200、A1、bbox 385.5×124.3 mm |
| `tmp\real\db-2025-01-19.model.json` | 单序列 70 点（含左右岸行），385.5×123.5 mm |
| `tmp\real\db-last2.model.json` / `db-list-dates.json` | `--last 2`（70/113）/ 103 个可选日期 |
| `tmp\real\db-vs-xls-2025.model.json` | 带 `sourceCompare`：183 比对点，93 一致、**90 不同**（88 处 xls 截断 + 2 处待人工核对） |
| `docs\section-model-v2.md` | v2 契约（已脱敏入库，供 helper 侧实现；原 `tmp\real\SCHEMA-v2.md`） |
| `tmp\real\DB-VS-XLS.md` | DB vs xls 逐点对比报告（向用户证明用 DB 的理由） |
| `tmp\real\build_db.py` | 一键重建以上全部 + 边界/分支用例（`ALL = PASS`） |

- `tests\verify_model_self.py` **已支持 v2**（独立重算共用映射 → 比对 bbox/elevBase/xRange/seriesCount/
  pointCounts/dataQuality/sourceCompare，并断言 v2 不含 entities）：4 个 v2 模型 34–36 项全 PASS。
- **helper 真机消费（core-cad 回报，2026-11-04）**：`db-2025-01-19` expected 87、`db-overlay` expected 84
（4 折线 + 14 线 + 65 文 + 1 圆，legend 8 行，`labelEvery=2` 自动、52/92 标注）；
  **ZWCAD Classic 84/84 verify.ok**、**ACAD 2007 84/84 verify.ok**、`cross_app_diff = ALL MATCH`；
  截图（已按脱敏要求移除）。数据侧核账：
  `tmp\real\probe_crosscheck_v2.py`（总数/线数与我的 computed 自洽）。
- **`byRole` 逐项对账已完成**（core-cad 已回 `result.expected.byRole`）：overlay
  `{ground:4, water-line:4, water-text:4, elev:52, axis:2, thalweg:1, thalweg-text:1, legend:8, legend-text:8}` = 84；
  `db-2025-01-19` `{ground:1, water-line:1, water-text:1, elev:40, pointno:40, axis:2, thalweg:1, thalweg-text:1}` = 87；
  v1 fixture 仍 67。之前悬着的「65 文 = 61 + 4」= **4 条 `water-text`**（>3 条水位只标高程、名字进图例）。
  标注数复算 `|stride(every=2) ∪ mandatory|` = 52/40/30/23，与 `labelPolicy.labelled` 全一致
（脚本 `tmp\real\probe_labelpolicy.py`，ALL=PASS）。详见 `tmp\real\SCHEMA-v2.md` §7.1–7.3。
- v1 回归未坏：`selftest.py` 41/41、`run_m1.ps1 -DryRunOnly` PASS9/0/2、`build_all.py` ALL=PASS。
- 边界：站码无数据/日期不存在/点数<3 → `BAD_DATA`；`--from-db` 与 `--data` 混用、`--compare-source`
  缺 `--from-excel`、`--last 0`、`--list-dates` 缺 `--from-db` → `BAD_ARGS`；全部 exit 2 且不产出文件。
