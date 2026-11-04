# DESIGN-M1：zwcad-cooperate v0.1（断面图首版）

> 2026-11-04 用户决策锁定：① 只要「用户开着图纸、agent 当场改」的实时协作；② 公司同时用中望 CAD 与 AutoCAD，**两边都有多个版本**（跨版本是硬需求，AutoCAD 放第二阶段）；③ **不做图框/会签栏**；④ 纪律 = 默认不保存、改动一步可撤销、图上不留任何 AI 标记；⑤ 首场景 = **断面图**（Excel 起点距↔高程）；⑥ 比例/图幅/水位由参数指定，默认值可覆盖。
> 实测依据全部在 `research/com-probe.md`，架构取舍见 `research/pattern.md` §1、§5；**实机验收记录见 `docs/M1-acceptance.md`**。
>
> **⚠ 环境更正（2026-11-04 验收时发现）**：本机装了**两套** ZWCAD，`ZWCAD.Application` 已被 64 位 **ZWCAD 2025 试用版**抢注（`{2F671EA1-…}`）；Classic 16 只能用 **`ZWCAD.Application.16`**（`{FB081ED6-…}`）。helper 默认 `.16` 优先、其余兜底，`-ProgId` 可覆盖。另：`zwc.ps1` 曾因参数名归一化缺失（`-ProgId` → `progid`，helper 只认 `progId`）而**静默附着到 2025**，已修（`keyMap`）。

---

## 1. 通道纪律（实测钉死，不可违反）

| 规则 | 依据 |
|---|---|
| 只用 **COM 晚绑定**（按名调用，不引用 interop），ProgID 探测 `ZWCAD.Application` → 以后 `AutoCAD.Application` | 类型库接口名 `IIcad*` 与 AutoCAD 不同，但成员名一致；早绑定绑死版本 |
| **禁止** `Document.EvaluateLispExpression` / `app.Eval` | v16 实测 `E_NOTIMPL`（空壳），调用即报错 |
| LISP 结果回传只用 `GetLispVariable`（**标量**）+ `SendCommand` | 列表回传 `E_INVALIDARG`；拼串返回字符串可行（288 KB 实测可） |
| 单条 `SendCommand` 字符串 **≤ 2 KB**，且分片后每条都要校验 `LASTPROMPT` | 1944 字符 ✅ / 3864 字符 ❌ **静默失败**；语法错会把命令态污染成 `Missing: 2) >` 吞掉后续命令 |
| 一批写操作 = 一个 `StartUndoMark`/`EndUndoMark` 组 | 实测 10 次 COM 编辑合成 1 步撤销；一条 SendCommand 天然 1 步 |
| 中间文件一律落 `%TEMP%\zwcad-cooperate\<sha>\` | DGS 按扩展名加密：`.dxf` 落盘即密文；写工作区曾 15 s 超时 |
| 用户没开 CAD 时**默认不自动起实例**（冷启动 8–9 s + 可能弹授权框）；`-Launch` 显式才起 | 实测冷启动 8.0–9.4 s |
| 本次起的实例收尾必须 `doc.Close($false)` + `app.Quit()` | 直接 `Quit()` 遇脏文档弹保存框留僵尸进程；组合调用 5.4–6.9 s、0 残留 |
| 忙闲判据：`GetVariable("CMDACTIVE")` + `LASTPROMPT` 文本 | `IsQuiescent` 不可靠（30000 实体批量时仍 True） |

**不做**（明确写死，别偷偷加）：图上 AI 标记/批注/药丸、逐页渲染、`SaveAs` 当备份、`SendCommand` 透传任何调用方给的字符串。

---

## 2. 进程拓扑（M1 故意从简）

```
用户/agent
  └─ powershell -NoProfile -ExecutionPolicy Bypass -Mta -File scripts\zwc.ps1 <命令> [参数]
        ├─ (可选) python scripts\section\prepare.py  xlsx → section.model.json   [纯计算，零 COM]
        └─ bin\ZwcHelper.exe --request <req.json> --response <resp.json>          [STA，一次性]
              └─ COM 晚绑定 → 用户已开的 CAD 实例 → 操作 ActiveDocument
```

- **M1 不引入常驻 daemon**：附着只要 191 ms、命令本体 ms 级，一次性进程足够；daemon 留到 M3（跨命令会话状态/快照缓存）。
- helper 编译：`csc.exe` 按源码哈希缓存到 `bin\cache\<sha12>\ZwcHelper.exe`（照 WPS 做法，避免每条命令 300 ms 编译）。
- **所有中文/长文本/几何参数走 JSON 文件**，命令行只传 ASCII（PS 5.1 按 ANSI 读 .ps1、cmd 吞 `^`/`=`/`%`）。
- 输出：**单段 JSON**（唯一判据看顶层 `ok`）；退出码 0/2（`ok:false`）/3（门禁失败）。

### 请求/响应信封

```json
// 请求（helper 侧）
{ "cmd": "section", "doc": "<图纸目录>\\a.dwg", "args": { ... } }
// 响应
{ "ok": true, "cmd": "section",
  "doc": { "path": "...", "fullName": "...", "saved": false, "readOnly": false },
  "result": { ... }, "warnings": [ {"code":"...","message":"..."} ],
  "undoToken": "u-20261104-1030-1a2b", "elapsedMs": 812,
  "app": { "progId": "ZWCAD.Application", "version": "2015.11.10(17516)", "hwnd": 118827746 } }
```

---

## 3. M1 命令面（9 条）

| 命令 | 参数 | 返回 result 要点 |
|---|---|---|
| `doctor` | — | `app{progId,version,hwnd,found}`、`capabilities{com,lisp,sendCommand,undoMark,saveasDxf,plot,addLine,…}`、`eNotImpl[]`、`tempDir`、`csc` |
| `status` | — | `documents[{name,fullName,saved,readOnly,active,count}]`、`active`、`cmdActive`、`lastPrompt` |
| `open` | `-Doc <路径>` `[-Launch]` `[-ReadOnly]` `[-NoHint]` | `doc{...}`、`app{...}`、`hint{captionSet}`、`launched` |
| `info` | `-Doc` | `units{insunits,dimscale,ltscale,textsize}`、`extents{min,max}`、`layers[{name,color,frozen,locked}]`、`counts{model,paper,blocks,layouts}`、`textStyles[]` |
| `read` | `-Doc` `[-Scope model\|handle:<h>]` `[-Handles]` `[-Max N]` | `entities[{handle,type,layer,bbox,text?}]`、`counts{}`、`readPath:"lisp\|com"`、`truncated` |
| `section` | `-Doc` `-Data <xlsx>` `[-Sheet]` `[-Cols "起点距=高程"]` `[-HScale 500]` `[-VScale 50]` `[-Paper A3]` `[-Origin x,y]` `[-WaterLevels "42.3=设计水位"\|-WaterFile]` `[-LabelEvery 1]` `[-NoAxis]` `[-DryRun]` | `model{points,extents}`、`created{handles[],byType{}}`、`expected{}`、`verify{ok,issues[]}`、`undoToken`、`layers[]` |
| `undo` | `-Token <undoToken>` 或 `-Count N` | `removed`、`countBefore/After`、`verify{ok}` |
| `hint` | `-Doc` `-Text "正在画断面图…"` `[-Clear]` | `captionSet`、`statusBar` |
| `finish` | `[-Doc]` `[-Quit]` | `docsLeft`、`processes{}`、`hintCleared` |

> `undo` 语义：M1 只用**本次启动的 undo 组**（helper 内 `_.U` 次数可控），不做跨进程 journal；`undoToken` 记录"本次命令压入的 undo 组数"。

---

## 4. 断面图契约（`section`）

### 4.1 输入（Excel）

- 默认：**第 1 列 = 起点距（m）**，**第 2 列 = 高程（m）**；首行若为表头自动跳过（非数字即跳过）。
- 兼容表头识别：`-Cols "起点距=高程"` 指定列名；列名支持 `起点距/桩号/距离/offset` 与 `高程/河底高程/elevation`。
- 点序：按行序；起点距若递减则自动反转并 `warnings:ORDER_REVERSED`。
- 校验：≥3 点、起点距单调递增（容差 0）、高程范围合理（非全等）、无空值 → 否则 `BAD_DATA` 且**不画任何东西**。

### 4.2 坐标映射（**纸面 mm 坐标系，模型空间 1 单位 = 1 mm**）

```
x_mm = (offset_m - offset_min) * 1000 / hScale          // hScale=500 ⇒ 1 m 实际 = 2 mm 纸面
y_mm = (elev_m   - elev_base) * 1000 / vScale           // vScale=50  ⇒ 1 m 实际 = 20 mm 纸面
elev_base = elev_min - (10 * vScale / 1000)             // 最低点上方留 10 mm 底边距
origin    = [0,0] 平移；最终 bbox 宽度 = (offset_max-offset_min)*1000/hScale
```
- 默认 `hScale=500 / vScale=50`（垂直放大 10 倍）；`-HScale 200 -VScale 20` 等由参数覆盖。
- A3 可用绘图区 ≈ 400×277 mm；若 bbox 超宽/超高 → 自动把 `hScale` 按整档放大（500→1000→2000）并 `warnings:AUTO_SCALED{from,to}`（`-NoAutoScale` 关闭则 `TOO_WIDE` 报错）。

### 4.3 绘制元素（全部进新图层，前缀 `-LayerPrefix`，默认 `AI-SEC`）

| # | 元素 | 类型 | 图层 | 规格 |
|---|---|---|---|---|
| 1 | 地面线 | `AddLightWeightPolyline`（闭合=false） | `AI-SEC-GROUND` | 颜色 BYLAYER(7)，线宽 0.3，随层 |
| 2 | 水位线 ×N | `AddLine` | `AI-SEC-WATER` | 虚线 `DASHED`；y=水位；x 从 bbox 左延 5 mm 到右延 5 mm |
| 3 | 水位文字 | `AddText` | `AI-SEC-ANNO` | `设计水位 42.30 m`，贴在右端上方 2 mm，字高 2.5 |
| 4 | 点高程 | `AddText` | `AI-SEC-ANNO` | 每 `-LabelEvery` 个点一个；点上方 2 mm，字高 2.0，`42.30` |
| 5 | 点号 | `AddText` | `AI-SEC-ANNO` | 点下方 3 mm，字高 2.0，`1`、`2`…（`-NoPointNo` 关） |
| 6 | 基线+左竖线 | `AddLine` | `AI-SEC-AXIS` | 底部 y=0 横线（x=0..W），左端竖线 0→10 mm；`-NoAxis` 关 |
| 7 | 深泓点标记 | `AddCircle`(r=0.6) + `AddText` | `AI-SEC-ANNO` | 最低点处，文字 `河底 38.55 m` |
| 8 | 起点距刻度（可选，`-Axis` 开） | `AddLine`+`AddText` | `AI-SEC-AXIS` | 每 50 m 一个刻度 2 mm，字高 2.0 |

- **文字样式**：新建 `AI-SEC-TEXT`（字体用当前默认，宽度因子 1）；不覆盖用户已有样式。
- **不写任何"AI/工具名"字样**（用户要求图上不留标记）；图层名是唯一痕迹，且 `finish` 不改名不删除。
- 全部在**一个 undo 组**内；`-DryRun` 只算不画（返回 `expected`）。

### 4.4 写后校验（`verify`）

1. 回读：每个 created handle 用 `HandleToObject` 取 `ObjectName/Layer`，与预期逐项比对；
2. 计数：`ModelSpace.Count` 增量 == `expected.total`；
3. 几何：地面线各点 y 与模型计算值误差 < 1e-6；bbox 宽高 == 预期（容差 0.01 mm）；
4. 失败 → 整体 `undo`（本次组）并 `ok:false` / `VERIFY_FAILED`，绝不"报成功其实没画上"。

---

## 5. 验收标准（M1 完成定义）

- **A** `doctor` 在"无 CAD / 有 CAD"两种状态下都能给出可读结论（不崩溃、不弹框）。
- **B** `section` 用 `tests\fixtures\section-30.xlsx`（30 点合成数据，含 1 个水位）在 CAD 里画出：实体数 = expected、图层正确、bbox 正确。
- **C** 回读校验 `verify.ok=true`；故意改坏一个点时 `verify` 能抓出来（负例）。
- **D** `undo` 一次性撤销整批：`ModelSpace.Count` 回到画前基线。
- **E** 全流程 CAD 内存里改动"一步可撤销"，**不落盘**；`finish` 后 `Get-Process zwcad*` 为 0（若本次起的实例）。
- **F** 同一套命令在**中望 CAD 2016**（本机）跑通；AutoCAD 侧留 `-App autocad` 抽象位，M4 再验。
- **G** 视觉：`hint` 标题出现 `〔AI协作〕`；可选 `render` 出 PNG（M2）。

---

## 6. 分工与写作用域（并行，互不重叠）

| 角色 | 写作用域 | 交付 |
|---|---|---|
| **core-cad** | `scripts\zwc.ps1`、`scripts\zwc.cmd`、`scripts\lib\*.ps1`、`scripts\helper\**`（C#）、`bin\`（编译缓存，仅运行时生成） | 可用的 9 条命令 + 编译缓存 + 忙/错处理；`doctor/section` 必须真跑通 |
| **section-gen** | `scripts\section\*.py`、`tests\fixtures\*`、`tests\verify_section.py`、`tests\run_m1.ps1` | xlsx→section.model.json（含比例自动选档、校验、负例用例）、ezdxf 独立复核脚本、验收 runner |
| **Lead** | `DESIGN-m1.md`、`SKILL.md`（M1 之后）、集成、实机验收 | 裁决与最终验收 |

**接口冻结**：`prepare.py` 输出 `section.model.json`（字段见 §4.2/4.3），`zwc.ps1 section -Data` 调它；helper 只吃 JSON，不解析 xlsx（Python 侧用 openpyxl，已装 3.1.5）。
