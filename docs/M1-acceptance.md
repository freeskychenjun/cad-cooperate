# M1 实机验收记录（断面图闭环）

> 2026-11-04 11:04–11:12，Lead 在中望CAD 经典版（`ZWCAD.Application.16`，版本 `2015.11.10(17516)`，pid 48984）上亲自跑完整链路。
> 设计基线：[DESIGN-M1.md](../DESIGN-M1.md)；证据文件：`tmp\accept\*`、`tmp\evidence\*`；截图。（已按脱敏要求移除）

## 一、验收结论：M1 闭环成立（A–F 全过）

| 项 | 判据 | 实测 |
|---|---|---|
| A `doctor` 双态 | 无 CAD 不崩、有 CAD 给能力表 | 无 CAD → `ok:false/NO_INSTANCE/0x800401E3`（wall 1.97 s）；有 CAD → `ACADVER=16.0i`、`eNotImpl=[EvaluateLispExpression 0x80004001]`、LISP set/get 往返 ok（helper 27 ms） |
| B `section` 绘图 | 实体数/类型/图层 == expected | **67 实体** created，`byType{LWPOLYLINE:1,LINE:3,TEXT:62,CIRCLE:1}`、`byLayer{GROUND:1,WATER:1,ANNO:63,AXIS:2}`，与 expected 完全一致；`verify.ok=true`；helper **398–400 ms**、wall 2.7 s（含 PS 启动） |
| C 回读 + 独立复核 | `verify_section.py` 全绿 | `read` 67 实体 → **268 PASS / 0 FAIL / 0 SKIP**（独立第二实现，非复用 helper 的判断） |
| D 一步撤销 | `undo -Token` 后 Count 回基线 | `removed:67, countBefore:67, countAfter:0, undoGroups:1, verify.ok:true`（**一次撤销组**，252 ms） |
| E 不落盘 + 零残留 | 图纸 `saved:false`；进程归零 | 全程 `saved:false`（未保存）；`finish -quit` → `docsClosed:2, processesAfter` 只剩**非本工具的** 2025 实例，Classic 已退出 |
| F 跨版本抽象位 | `-App/-ProgId` 可切换 | `-App autocad` 分支已在 helper 内（M4 验）；`-ProgId` 覆盖实测生效（见下"环境更正"） |
| G 用户可见提示 | 标题出现〔AI协作〕 | 截图标题栏 `〔AI协作〕 ZWCAD Classic - [Drawing2]`；`finish` 还原为 `ZWCAD Classic - [Drawing2]` |
| 离线回归 | `run_m1.ps1 -DryRunOnly` | **PASS 9 / FAIL 0 / SKIP 2**（含 ezdxf 往返 16 断言、tamper 负例、3 个 bad 用例、helper 基准交叉核对） |

**视觉**（截图左半为画出的断面）：地面线由 30 个点组成 U 形，每点上方标高程（44.00→38.55→44.30）、下方标点号 1–30、蓝色虚线为设计水位 42.30、深泓点标「河底 38.55 m」并加圆标记、底部基线+左竖线。全部实体在 `AI-SEC-*` 图层，图面**没有任何 AI/工具字样**。

## 二、环境更正（重要，影响所有后续验收）

本机装**两套** CAD，`ZWCAD.Application` 已被 64 位 **ZWCAD 2025 试用版**抢注：

| 注册表视图 | ProgID | CLSID | 指向 |
|---|---|---|---|
| HKLM 64 位 | `ZWCAD.Application` | `{2F671EA1-…}` | `C:\Program Files\ZWSOFT\ZWCAD 2025\ZWCAD.exe` |
| HKLM 64 位 | **`ZWCAD.Application.16`** | `{FB081ED6-…}` | `C:\Program Files (x86)\ZWCAD Classic Chs\ZWCAD.EXE` |

⇒ **强验 Classic 必须 `-ProgId ZWCAD.Application.16`**；helper 默认顺序已改为 `.16` 优先、`ZWCAD.Application` 兜底，`-ProgId` 可覆盖。

**已修的 bug**：`zwc.ps1` 参数解析会把所有 flag 转小写，导致 `-ProgId X` 变成 `progid` 而 helper 只认 `progId` ⇒ **静默忽略、附着到错误的 CAD 版本**（本次实测中招：第一次 `open -Launch -New -Progid …` 附着到了 2025）。已在 `zwc.ps1` 加 `keyMap` 归一化并复测通过（`-ProgId` 大写也能正确只列 `.16`）。

## 三、本轮新发现的问题（已记录，未修）

1. **标注过密**：30 点 / 290 mm（1:1000）时每点都标高程+点号，字高 2 mm 与相邻点几乎贴边（截图中 16–18 号一带最密）。⇒ M2 加"按间距自动降密度"（`-LabelEvery` 已有，但应能按 字高/间距 自动选），或允许只标控制点/深泓/岸线。
2. **`Document.MSpace` 写属性报 `E_FAIL`**：无法用它切到模型空间做截图（本次截图靠"用户本来就是模型空间标签页"）。⇒ M2 的 `render` 需要另找通道（`TILEMODE` 系统变量 / `-View` 命令 / 直接 `Plot` 布局）。
3. **`finish -quit` 慢**：`Close($false)`+`Quit()` 实测 wall **29 s**（`processWaitMs` 20 s），比探针期的 5.4–6.9 s 慢很多（本次开过 2 张图）。⇒ 收尾超时默认值要 ≥60 s，别让宿主误判 TIMEOUT。
4. **本机 2025 实例会"凭空出现"**（本轮 11:07:56 又起了一个，非本工具启动）：⇒ 任何收尾都**只能退自己 pid 的实例**，绝不按映像名批量杀（helper 已按 pid 判断，实测 `SKIP_QUIT` 生效）。
5. `print/plot/render` 仍是 M2；`read` 默认走 COM（>2000 实体降级 LISP 只回句柄）。

## 四、耗时基线（本轮实测，供 SKILL 文档引用）

| 命令 | helper | wall（含 PS 启动） |
|---|---|---|
| doctor（有 CAD） | 27–68 ms | 1.8–4.5 s |
| open 附着 | ~0.5 s | 2.7–4.5 s |
| open `-Launch -New`（冷启动 Classic） | 9.1 s | 11.1 s |
| prepare.py（30 点 xlsx→模型） | — | ~0.3 s（python） |
| section 真画 67 实体（含建层/样式/回读 verify） | **398–400 ms** | 2.7 s |
| read 67 实体 | 143–161 ms | 4.5 s |
| undo（1 组 67 实体） | 252 ms | 2.1 s |
| finish `-quit` | 20 s（等进程退出） | 29 s |

## 五、复跑方法

```powershell
cd <repo>
# 0) 离线（不碰 CAD）：结构 + 基准 + 负例
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run_m1.ps1 -DryRunOnly

# 1) 起 Classic 并画断面（会新开一张 Drawing，不落盘）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 open -Launch -New -ProgId ZWCAD.Application.16
python scripts\section\prepare.py --data tests\fixtures\section-30.xlsx --out tmp\accept\m.json --water "42.30=设计水位"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 section -Data tmp\accept\m.json -ProgId ZWCAD.Application.16

# 2) 回读 + 独立复核
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 read -ProgId ZWCAD.Application.16 -Max 200 | Out-File -Encoding utf8 tmp\accept\read.json
python tests\verify_section.py --model tmp\accept\read.json --expected tests\fixtures\section-30.expected.json

# 3) 一步撤销 / 收尾
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 undo -Token <上一步返回的 undoToken> -ProgId ZWCAD.Application.16
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 finish -Quit -ProgId ZWCAD.Application.16
```
