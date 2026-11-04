# M3-E6：大图几何快路径与 LISP 脚本纪律（教训固化）

> 起因：2026-11-04 用 `zwcad-cooperate` 处理用户真实图纸 `东总干渠1+300~16+500地形图20261020.dwg`
> （36,558 实体 / 17 图层 / 304 条断面线），目标是提取每条断面线的「起点距—高程」并出 Excel。
> 任务最终成功，但**耗时约 50 分钟**，其中真正取数只需 1–2 分钟。本文固化两条教训与待补能力。

## 一、实测对比：几何数据必须走 LISP 写文件，不要 COM 逐实体

| 路径 | 36,558 实体耗时 | 说明 |
|---|---|---|
| COM 逐实体（`foreach ModelSpace` + 读属性） | **310 s** | 每条记录 2–4 次 COM 属性访问；36 k 实体不可接受 |
| LISP `(ssget "X")` + `entget` + `write-line` 到 `%TEMP%` | **0.36 s** | 本文档 E5 的 lisp-file 通道，含 `__DONE__` 哨兵 |

**结论**：差 800 倍。只要任务是"要几何"，就必须一次性 dump 全部（含 `assoc 10/11/38` 与顶点），
而不是按实体 COM 读。

## 二、现状缺口（本次任务踩到）

`read`/`query` 的 lisp-file 快路径（E5 实现）当前只回传：

```
handle | type | layer | text
```

**不含坐标/顶点**，也没有等高线折线的 `Elevation` 属性（`assoc 38`）。因此本次任务被迫退回 COM 全扫 5 分钟。

### 待补（M3-E6 交付项）

1. **几何 dump 快路径**：`read -Mode geom` 在大图（实体数 > 阈值且无 LISP 变量桥）上改走 LISP：
   - 每实体一行 `E|handle|type|layer|text|x|y|z|nverts`
   - 多段线每顶点一行 `V|handle|i|x|y|z`
   - 折线（LWPOLYLINE/2D POLYLINE）额外输出 `ELEV|<handle>|<assoc-38 高程>`
   - 顶部可选只 dump 指定图层/类型（`ssget` 过滤），避免无关数据
2. **LISP 文件生成器**（helper 内部）：不再手写 .lsp，模板化生成 + 自动校验：
   - 强制 **ASCII、无 BOM**（带 BOM → `Error: 输入的列表有缺陷`）
   - **括号配平校验**（不平衡时先报错再注入，不要等 CAD 回 `Error: 语法错误`）
   - 路径统一 `/`、落 `%TEMP%`、结果 + `__DONE__` 哨兵
   - `CMDECHO` 保存/设 0/恢复，避免刷屏
3. **文档**：SKILL.md 的「铁律」与「能力边界」已写入（见 §三）。

## 三、已写入 SKILL.md 的条目（本次）

- 铁律 11：不要手写 LISP 探针（ASCII 无 BOM、括号配平、哨兵轮询、先最小骨架）
- 铁律 12：大图取几何不要 COM 全扫（36 k ≈ 5 min vs LISP 0.4 s），并标注 lisp-file 快路径缺坐标
- 能力边界新增行：大图几何快路径（坐标/顶点/Elevation 待补）
- 性能基线下方补实测对比与 `build_sections_from_dmx.py` 参考

## 四、断面提取的产出与口径（本次交付，供复用）

- 脚本：`scripts\section\build_sections_from_dmx.py`
- 输入：`%TEMP%\zwcad-cooperate\geom-com.json`（实体几何）+ `contours.json`（等高线 + Elevation）
- 识别规则：断面线 = `dmx` 层直线；桩号 = `KZD` 层 `\d+\+\d+` 文字（8 m 内 1:1 匹配）；高程点 = `ZDH` 层 POINT 的 Z（≠0）；补点 = `DGX` 等高线与断面线求交
- 起点距：面朝下游（桩号增大方向）的**左岸为 0**
- 实测：304 条断面、6,647 个高程点 + 2,803 个等高线交点 = 9,450 个剖面点；同图重跑 10 s
- 注意：9 条断面高程点 <10 个（局部缺测点），精度依赖等高线

## 五、实现（M3-E6）

| 组件 | 内容 |
|---|---|
| `scripts/helper/LispTemplate.cs` | **生成器**：`BuildDump(geom, txt, done, ssFilter)` 生成纯 ASCII 脚本；`WriteScript()` 依次过 **ASCII 校验 → 括号配平（忽略字符串/注释）→ 只读安全白名单**，任一不过抛 `LISP_TEMPLATE_NOT_ASCII` / `LISP_TEMPLATE_UNBALANCED` / `LISP_TEMPLATE_UNSAFE` 且**不注入**；`CMDECHO` 保存/设 0/恢复；哨兵 `__DONE__|<n>`。全脚本只用基础 AutoLISP（无 `vl-*`，2D 点缺 Z 用 nil-safe 补 0.0）。 |
| `scripts/helper/LispGeom.cs` | **几何 dump 通道**：`E/V/P2/ELEV/H/CL` 行解析 → 实体 map（x/y/z、point、p2、vertices/points、elevation、height、closed），GBK936 解码，失败抛错由 read 回退；生成同样的 `meta{lispInjectMs,lispSentinelMs,parseMs,totalMs,fileBytes,decodedAs}`。 |
| `ZwcHelper.Read` | `-Mode geom` 在"无 LISP 桥 + 实体数 > `-LispThreshold`（默认 2000）"时自动走 LispGeom，响应 `readPath:"lisp-file-geom"`；失败回退 COM + `LISP_GEOM_FALLBACK`（对 geom 而言 COM 很慢，故仅在 LISP 不可用时发生）。 |
| `lispgen` debug 命令 | 不 attach CAD，只生成 `.lsp` 到文件并回 `{path,bytes,ascii,balance}`，供测试/审查（本轮用它验证生成器）。 |
| `Queries` | E5 的 `query` 也改为调用同一生成器（ASCII/括号/安全校验），`-Type` 仍下推 ssget。 |

### 5.1 只读安全约束（SelectionSet.Delete 事故后新增，硬约束）

1. **禁止 `SelectionSets` 相关 API**（尤其 `SelectionSet.Delete()`）；按层筛选只用 LISP `(ssget "X" (list (cons 8 layer)))`。
2. **LISP 模板只读白名单**：`ssget/entget/assoc/princ/write-line/open/close/itoa/rtos/getvar/strcat/strlen/substr/chr/nth/car/cdr/cons/list/length/reverse/foreach/while/if/progn/setq/…`；**禁止** `command/entdel/entmod/entmake/entmakex/erase/Delete/SelectionSets/vla-/vlax-/vlr-/vl-load/undo/_.U/saveas/qsave/-layer/-insert/…`。生成器在 `WriteScript()` 里逐 token 校验（忽略字符串/注释），命中即报 `LISP_TEMPLATE_UNSAFE`。
3. `setvar` **只允许 CMDECHO**（保存/设 0/恢复），其他 setvar 一律拒绝。
4. **用户正在编辑的图纸 = 只读，任何注入先征得用户/lead 同意**；自测一律在自己的 `open -Launch` 自动化实例/夹具图上做。

### 5.2 实测（小筛选验证，用户真实图，只读）

| 测试 | 结果 |
|---|---|
| 生成脚本 | `lispgen -Geom` → 3,396 B，`ascii:true`，`balance:null`，通过安全门 |
| `(ssget "X" '((0 . "LINE,LWPOLYLINE")))` 手动注入 | **2,995 条记录 / 334 ms**，E/P2 行坐标正确（例：`E|1079A6|LINE|JMD||420218.215874|3124393.412191|0` + `V|…` + `P2|…`），`CMDECHO` 恢复 1 |
| `(ssget "X" '((0 . "LINE")))` | **1,500 条 / 237 ms**，E/V/P2 各 1,500 行 |
| `(ssget "X" '((0 . "CIRCLE")))` | 0 条（该图无圆）→ 哨兵 `__DONE__|0`，脚本正常结束 |
| 全量 36,558 `read -Mode geom` | **未完成**：生成器早期版本有缺陷（2D 点 Z 为 nil、vl-string-translate 不可用）导致 LISP 报错、两次注入超时（helper 之一回退 one-shot 后 180 s 超时）；修复后仅用上述小筛选验证语法/逻辑，**全量 dump 待 lead 给窗口、允许在用户实例上再跑一次**。 |

> 注：全量 dump 期间用户图纸 `CMDECHO` 一度被我方脚本设为 0（脚本未走到恢复行），随后已 `(setvar "CMDECHO" 1)` 恢复；未做任何改图操作。