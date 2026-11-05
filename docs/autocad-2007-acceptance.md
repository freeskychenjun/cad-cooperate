# AutoCAD 2007 实机验收记录（跨版本协作通路，M4 提前验一次）

> 2026-11-04 11:21–11:30，core-cad 在本机 **AutoCAD 2007**（`17.0s (LMS Tech)`，`ACADVER=17.0s`，pid 52220，`C:\Program Files (x86)\AutoCAD 2007\acad.exe`）上跑通与中望**完全相同**的验收链。
> 设计基线：[DESIGN-M1.md](../DESIGN-M1.md)；对称记录：[M1-acceptance.md](M1-acceptance.md)；证据 JSON：`tmp\evidence-acad\*`；坑台账：`tmp\core-cad-notes.md`。
> 结论：**AutoCAD 2007 通路成立（A–F 全过），无需 32 位 helper，无需用户做任何激活操作。**

## 一、验收结论：跨版本通路闭环

| 项 | 判据 | 实测（最终 build） |
|---|---|---|
| A `doctor` 双态 | 无 CAD 不崩、有 CAD 给能力表 | 无 CAD → `ok:false / NO_INSTANCE / 0x800401E3`（wall 4.3 s，不崩不挂）；有 CAD → `app{name:AutoCAD, version:17.0s (LMS Tech)}`、`ACADVER=17.0s`、helper 50 ms |
| B `section` 绘图 | 67 实体 + `verify.ok` | **created.total=67**、`byType{LWPOLYLINE:1,LINE:3,TEXT:62,CIRCLE:1}`、`byLayer{GROUND:1,WATER:1,ANNO:63,AXIS:2}` 与 expected 全等；`verify.ok=true`、`bbox[-5,0,295,125]`；helper **336 ms** / wall 4.4 s；`AUTO_SCALED 500→1000` 与中望同口径 |
| C 回读 + 独立复核 | `verify_section.py` 全绿 | `read` → `readPath:"com"`、`counts{model:67,returned:67}`、helper 179 ms；`python tests\verify_section.py` → **268 PASS / 0 FAIL / 0 SKIP**（与中望一致） |
| D 一步撤销 | Count 回基线 | `undo -Token` → `removed:67, countBefore:67, countAfter:0, undoGroups:1, verify.ok:true (baseline 0)`，helper 230 ms |
| E 不落盘 + 零残留 | `saved:false`；进程归零 | 全程 `saved:false`；`finish -Quit` → `ours:true, docsClosed:2, processesAfter:0, processWaitMs:14`；`Get-Process acad*` = **0** |
| F 用户可见提示 | 标题出现〔AI协作〕 | `hint.captionSet:true`，实测标题 `〔AI协作〕 AutoCAD 2007 - [Drawing2.dwg]`（SetWindowText 直打 `app.HWND` 成功，无需 WindowTitle） |
| 中望回归 | `run_m1.ps1 -DryRunOnly` + 一次真画 | 离线 **PASS 9 / FAIL 0 / SKIP 2**；真画：section 67 + `verify.ok`（helper 668 ms）→ read → **268 PASS** → undo `removed:67, after:0` |
| 跨版本物料 | SQL/DXF/几何口径 | `read` 的 `ObjectName` 是 `AcDbPolyline`，python 验收器自动归一，268 断言全过 |

## 二、中望 vs AutoCAD 2007 差异表（实测）

| 维度 | 中望 Classic 16 / 2025 | AutoCAD 2007 | 处理 |
|---|---|---|---|
| ProgID | `ZWCAD.Application.16`（Classic）/ `.Application`（2025 抢注） | `AutoCAD.Application.17` 已注册；**`AutoCAD.Application` 在 64/32 位视图都未注册** | helper 候选顺序 `AutoCAD.Application.17 → .Application`，`.17` 命中 |
| 位宽与 COM 注册 | CLSID 注册在 64 位视图（2025） | **`LocalServer32` 只在 32 位视图（WOW6432Node）**，64 位视图为空 | **64 位 helper 实测可直接 CreateObject 成功**（CLSID 映射在 64 位视图存在，LocalServer32 走 WOW64 回退）；⇒ **不需要 x86 构建**，cad.ps1 未加第二份 exe |
| 版本字符串 | `2015.11.10(17516)` / `2025` | `17.0s (LMS Tech)`；`ACADVER=17.0s` | 只作诊断输出，不参与分支 |
| 集合下标 | **0 基**（`ModelSpace.Item(0)`=首实体） | **1 基** | `Coll.DetectBase()` 逐集合探测（先试 `Item(0)`）：AutoCAD 上 read 回读 67 实体含首条地面线，证明 1 基路径正确 |
| 撤销组打开方法 | `StartUndoMark` | **`BeginUndoMark`** | helper 改 `TryInvoke("BeginUndoMark") \|\| TryInvoke("StartUndoMark")`；实测 AutoCAD `undoGroups:1` 一次 `_.U` 回退 67 |
| 轻量多段线 ObjectName | `AcDbLightWeightPolyline` | **`AcDbPolyline`** | 类型归一用 `EndsWith("Polyline"/"Line"/"Text"/"Circle")`，两端都过 |
| DASHED 线型 | 新图可 `Load("DASHED","acad.lin")` | 新图默认未加载；`Layer.Linetype` 直设会失败 | `EnsureLinetype()`：先 `Linetypes.Item` → 失败 `Linetypes.Load("DASHED","acad.lin")` → 仍失败则 CONTINUOUS + `LINETYPE_UNAVAILABLE` warning（不整条失败）。实测 read 回 `linetype:"DASHED"` |
| `SendCommand` 语义 | Classic 近似同步；2025 排队异步 | **排队异步** | 统一 `WaitIdle()`（`CMDACTIVE=0` + `LASTPROMPT` 干净 + Count 连续两次稳定）；undo 提交带结尾 `\n` |
| LISP 变量桥 | `Get/SetLispVariable` 可用（标量） | **无此成员**：`0x80020006 DISP_E_UNKNOWNNAME`（`getLispVariable`/`setLispVariableRoundTrip` 均报错） | `read` 增加 `LispBridgeAvailable()` 探测：不可用则**永远走 COM**（即使实体数 > 阈值），并给 `LISP_BRIDGE_UNAVAILABLE` warning；本次 read 即带此 warning 且结果正确 |
| `EvaluateLispExpression` | 存在但 `E_NOTIMPL 0x80004001`（空壳） | **方法不存在**：`0x80020006 DISP_E_UNKNOWNNAME` | doctor 只探测并如实报 HRESULT；业务代码从不使用（双端同构） |
| `LASTPROMPT` | 干净（`命令:`） | 存在，但常驻 `; 错误: 读入的 (八进制) 字符不正确: 0`（**用户自定义启动项的历史遗留**，与本次无关） | `PromptPolluted()` 只认 `Missing:`/结尾 `>`，该文本**不**算污染（实测 `promptPolluted:false`），不影响流程 |
| 冷启动 | Classic 9–14 s；2025 32 s | **132 s**（helper 132 123 ms / wall 137.6 s） | `open -Launch` 超时建议 ≥180 s（本次用 `-TimeoutSec 260`）；**未出现激活/许可弹窗** |
| 退出 | `Close($false)+Quit()` 10 s 级 | 同样干净，但**首次实测 20 s 仍未退完**（procs=1），20.1 s 后消失 | `finish` 默认等待从 20 s 提到 **45 s**；最终 build 实测 `processWaitMs=14`、`processesAfter:0` |

## 三、需要用户做什么

**什么都不用做**。AutoCAD 2007 在本机可直接被 COM 驱动、可正常冷启动/退出，无激活框、无许可弹窗。
（本机曾有的「Windows 正在配置 AutoCAD 2007 / MSI 1308」已于 2026-11-04 10:50 静默修复；本次运行未触发自愈弹窗、未新增 1308 事件。）

## 四、耗时基线（AutoCAD 2007，最终 build）

| 命令 | helper | wall（含 PS 启动） |
|---|---|---|
| doctor（无 CAD） | 45 ms | 4.3 s（含首跑编译） |
| doctor（有 CAD） | 50 ms | 3.9 s |
| open `-Launch -New`（冷启动） | **132 123 ms** | 137.6 s |
| section 真画 67 实体（建层/样式/线型/回读 verify） | **336 ms** | 4.4 s |
| read 67 实体（COM，含几何） | 179 ms | 4.1 s |
| undo（1 组 67 实体） | 230 ms | 3.9 s |
| finish `-Quit`（Close×2 + Quit） | 73 ms | 6.3 s（waitMs 14） |

## 五、已知限制 / 后续

1. **AutoCAD 2007 无 LISP 变量桥** ⇒ `read -Fast`（只回句柄的快路径）在 AutoCAD 上不可用，已自动降级 COM；大图（>2000 实体）会慢（0.5 ms/实体量级），M4 若要大图可用，需另找通道（`SaveAs DXF` 或 NETLOAD）。
2. **冷启动 132 s** 是本机 AutoCAD 2007 的固有成本（首启动 + 老版本初始化），`open` 必须给足超时；不建议每次命令冷启动（M4 若做常驻 daemon 可摊薄）。
3. 本次**未做截图**：AutoCAD 已退出，重新冷启动 132 s 只为截图不划算；视觉等价性由 `verify_section.py` 的 268 条几何断言（顶点/位置/文字/图层/线型/圆）+ 中望侧已有人眼截图覆盖。（已按脱敏要求移除）
4. 兼容补丁全部是**增量**（双名 `TryInvoke`、探测式降级、超时放宽），未改中望路径；中望侧 `run_m1.ps1 -DryRunOnly` PASS 9/0/2 + 真画 67/268 PASS/undo 67→0 已复测。
5. 沿用纪律：**只退本工具起的实例**。本轮曾因上游遗留的 ZWCAD 实例（11:18 误解析时起的）被 `open` 附着，`finish` 正确 `SKIP_QUIT` 拒绝退出，随后按 pid 手工清理（该实例由本工具早前误起，非用户图纸）。**绝不按映像名批量杀进程**。

## 六、复跑方法

```powershell
cd <repo>
# 0) 离线回归（不碰任何 CAD）
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run_m1.ps1 -DryRunOnly

# 1) 起 AutoCAD 2007（冷启动 ~132 s，务必给足超时）并新开一张空图
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\cad.ps1 open -Launch -New -App autocad -TimeoutSec 260

# 2) 画断面（fixture 30 点 → 67 实体）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\cad.ps1 section -App autocad -Doc Drawing2.dwg -Data tmp\section-30.model.json

# 3) 回读 + 独立复核（应为 268 PASS / 0 FAIL）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\cad.ps1 read -App autocad -Doc Drawing2.dwg | Out-File -Encoding utf8 tmp\evidence-acad\read-final.json
python tests\verify_section.py --model tmp\evidence-acad\read-final.json --expected tests\fixtures\section-30.expected.json

# 4) 一步撤销 + 干净收尾（应 0 残留）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\cad.ps1 undo -App autocad -Doc Drawing2.dwg -Token <undoToken>
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\cad.ps1 finish -Quit -App autocad -TimeoutSec 120
Get-Process acad*   # 期望：没有输出
```

## 七、中文显示（????）与"水位线看着像实线"的修复（task-7，2026-11-04 11:40–11:50）

### 7.1 现象与根因

- 现象：AutoCAD 2007 里水面文字渲染成 `???? 42.30 m`、深泓成 `?? 38.55 m`；**数据层一直是对的**（`read` 回读中文字符串正确、`verify_section.py` 268 断言全过）。
- 根因：新建文字样式默认字体是**西文 SHX**（AutoCAD 默认 `txt.shx`、中望默认 `txt.shx`），SHX 无中文字形 ⇒ 渲染成 `?`。中望之所以没暴露，是它的默认样式带了中文字体/替换。
- 水位线"看着是实线"的根因：**AutoCAD 的 `acad.lin` 是英制线型定义**（DASHED = `0.5 / -0.25` 图形单位）；本图 1 图形单位 = 1 mm ⇒ 虚线短划只有 **0.5 mm**，正常缩放下就是一个"看着像实线"的伪连续线；而实体 `Linetype` 字段确实是 `DASHED`（数据层不会错，纯视觉问题）。`acadiso.lin` 才是公制（`12.7 / -6.35 mm`）。

### 7.2 修复方案（helper 内，纯晚绑定、失败只降级 warning）

| 项 | 做法 |
|---|---|
| 中文字体选型 | **不通用的组合不能写死**：实测 AutoCAD 2007 的 `Fonts` 目录**没有 `hztxt.shx`**（有 `gbenor.shx` + `gbcbig.shx`），中望 Classic 的 `Fonts` 目录**没有 `gbenor.shx`**（有 `txt.shx` + `HZTXT.SHX`）。⇒ helper 改为**探测该 CAD 安装目录下的 Fonts 文件夹**，按"西文 SHX + GB2312 大字库 SHX"配对：AutoCAD 2007 → `gbenor.shx` + `gbcbig.shx`；ZWCAD Classic 16 → `txt.shx` + `hztxt.shx` |
| 兜底 | TrueType `simsun.ttc`（系统字体）；两者都失败 → `TEXTSTYLE_CJK_UNAVAILABLE` warning，**不报错、不阻断绘图** |
| 旧样式纠正 | 样式已存在时**就地纠正**（幂等），只碰 `AI-SEC-*` 前缀，不动用户样式 |
| 自检 | `section.result.textStyle{name,created,method:"shx"/"ttf",fontFile,bigFontFile,cjk}`，`cjk` 必须为 true |
| 虚线 | ① 线型文件按产品排序：AutoCAD `acadiso.lin → acad.lin`；ZWCAD `zwcadiso.lin → zwcad.lin`（顺带消掉中望命令行"文件 acadiso.lin 未找到"的噪声）；② 只有英制文件可用时把实体 `LinetypeScale` 提到 **20**（0.5 mm×20 = 10 mm 短划）；③ **图层 `AI-SEC-WATER` 的 Linetype 也设为 DASHED**（实体仍显式设置，双保险）；④ verify 新增回读断言 `LINETYPE_NOT_APPLIED` / `LINETYPE_SCALE_MISMATCH` |

### 7.3 双端复验（最终 build，均为 67 实体 + `verify.ok` + read→268 PASS）

| 项 | ZWCAD Classic 16 | AutoCAD 2007 |
|---|---|---|
| textStyle（实测回读） | `method:"shx"`、`fontFile:"txt.shx"`、`bigFontFile:"hztxt.shx"`、`cjk:true` | `method:"shx"`、`fontFile:"gbenor.shx"`、`bigFontFile:"gbcbig.shx"`、`cjk:true` |
| linetype（实测回读） | `source:"loaded:zwcadiso.lin"`、`scale:1` | `source:"loaded:acadiso.lin"`、`scale:1` |
| section | 67 实体 / `verify.ok` / helper **455 ms** | 67 实体 / `verify.ok` / helper **403 ms** |
| read + python | 67 实体 → **268 PASS / 0 FAIL / 0 SKIP** | 67 实体 → **268 PASS / 0 FAIL / 0 SKIP** |
| 截图 | 同上，中文不再 `????`、水位线虚线（已按脱敏要求移除） |
| undo | `removed:67, after:0, undoGroups:2, verify.ok:true` | `removed:67, after:0, undoGroups:1, verify.ok:true` |
| finish -Quit | `ours:true, procsAfter:0` | `ours:true, docsClosed:2, procsAfter:0` |
| 中望离线回归 | — | `run_m1.ps1 -DryRunOnly` **PASS 9 / FAIL 0 / SKIP 2** |

### 7.4 本轮顺带发现的坑

1. **`ZoomExtents` 在中望 Classic 上占用一个撤销槽**（AutoCAD 上不占）：截图脚本先 `ZoomExtents` 再 `undo` 时，第一次 `_.U` 撤掉的是"缩放"，实体计数不动 ⇒ 旧的"发 N 次 U 就完事"实现会误报 `removed:0 / verify.ok:false`。已改为**按 token baseline 逐次 `_.U` 直到 Count 回到基线（最多 groups+4 次，一到基线立刻停）**；实测中望需要 2 次（`undoGroups:2`），AutoCAD 1 次。
2. 中望命令行会打印**失败的线型文件加载**（"文件 acadiso.lin 未找到。"）——虽不影响功能但会污染用户命令行；按产品排序候选文件后消失。
3. 截图注意：`SetProcessDPIAware` + `GetWindowRect` + `CopyFromScreen` 才能裁对窗口；`SetForegroundWindow` 可能被前台锁拒绝，脚本用 `GetForegroundWindow` 校验并以 `AppActivate` 兜底；GDI+ 先写 `%TEMP%` 再复制进 `docs\`（直写工作区曾失败）。
## 八、ProgID 选择的修正（task-8，2026-11-04 12:15–12:40）

`ZWCAD.Application`（无版本 ProgID）会被**最后启动的产品**改写（2025 跑过后指向 2025 的 CLSID，Classic 启动后指向 Classic 的 CLSID），因此本工具改为：**显式 `-ProgId` > 正在运行的进程对应产品 > 默认顺序**；两个不同产品同时在跑时返回 `MULTI_PRODUCT` 拒绝猜测。详见 [progid-selection.md](progid-selection.md)。

**同时追加的 2025 差异**：ZWCAD 2025 上 `Get/SetLispVariable` 与 `EvaluateLispExpression` **都是 `DISP_E_UNKNOWNNAME 0x80020006`**（Classic 16 的 Get/Set 可用、Evaluate 是 `E_NOTIMPL` 空壳）⇒ `read` 在 2025 上自动降级 COM（`LISP_BRIDGE_UNAVAILABLE`，实测 268 PASS）；`section` 67 实体耗时 2025 ≈ **2150 ms** vs Classic ≈ **442–533 ms**（慢 4–5 倍），COM 冷启动 2025 ≈16.7 s vs Classic ≈12–18 s。