# ZWCAD Classic 16.0.1111 · COM / LISP 自动化实测报告

> 探针：`scripts/probe/`（纯 ASCII）。原始日志：`tmp/log-20-core.txt`、`tmp/log-23-diagnose.txt`、`tmp/log-24-attach.txt`、`tmp/log-25-batch-export.txt`、`tmp/log-26-dgs.txt`、`tmp/log-27-limit.txt`。
> 所有测试只用**新建空图纸**，文件落在 `%TEMP%\zwprobe\`（ASCII 路径）与 `<repo>\tmp\`；未触碰任何用户图纸。**收尾 `Get-Process zwcad*` = 0 残留**。

---

## 0. 结论先行

**判定：有条件可行 —— 核心协作链路（附着用户实例 / 一次读全图 / 批量写 / 撤销分组 / 干净退出）全部实测通过，且成本远低于 WPS 侧。**
但有三条**硬约束**会直接改变 skill 的实现方式（见 §1），其中两条是「官方 API 是空壳」与「落盘文件被透明加密」。

### 三条最强证据

| # | 证据 | 实测数字 |
|---|---|---|
| 1 | **能附着用户已开实例，且第二次 CreateObject 自动复用同一实例** | `Marshal.GetActiveObject("ZWCAD.Application")` 191 ms 拿到跨进程实例，HWND 与宿主一致；第二次 `New-Object -ComObject` 进程数 1→1、HWND 不变；附着后跨进程 `SendCommand`+`GetLispVariable` 往返 **10 ms** |
| 2 | **写侧「一次画完」成立，且撤销可分组** | 一条 `SendCommand` 画 **10,000 条线 = 152 ms（CAD 内）/ 179 ms（墙钟）**；`StartUndoMark/EndUndoMark` 把 10 次 COM 编辑合成 **1 步**（一次 `_.U` 全回退 delta=0）；一条 SendCommand 天然 1 步 |
| 3 | **收尾可做到 0 残留** | `Document.Close($false)` + `Application.Quit()` = **5.4–6.9 s，残留 0**（对比：直接 `Quit()` 会卡在「是否保存」模态框并留下僵尸进程） |

### 三条硬约束（决定架构）

1. **`EvaluateLispExpression` 是空壳**：Document 上该方法（以及 Application.Eval）在 IDL 里声明齐全，但运行时返回 `E_NOTIMPL 0x80004001`（`app.Eval` 更糟，`RPC_E_SERVERFAULT 0x80010105`）。**没有「同步返回 LISP 值」的原生 API** ⇒ 只能用 **`SendCommand`（执行）+ `GetLispVariable`（取结果）** 组合，实测同步、无文件、ms 级。
2. **CAD 写出的 `.dxf/.dwg` 被透明加密**（Agile DGS 类，文件头 `17DA5FA01633CD9A`）⇒「DXFOUT/另存 DXF 再解析」的读侧路线**默认不可用**（除非解密）。但加密是**按扩展名**的：同内容写 `.txt` 是明文（见 §4.3）⇒ 有干净绕法。
3. **语法错的 LISP 会污染命令态**：CAD 停在 `Missing: 2) >` 续行提示，**后续所有 SendCommand 被当成续行吞掉**（实测连续 4 条命令全部失效）。必须前置括号/引号校验 + 提供恢复手段。

---

## 1. 环境事实

| 项 | 值 |
|---|---|
| 产品 | 中望CAD 经典版（ZWCAD Classic Chs），文件版本 16.0.1111 |
| `Application.Version` | `2015.11.10(17516)`（内核为 2015 一代） |
| 系统变量 | `ACADVER=16.0i`、`ZWCADVER=16.0`、`PROGRAM=ZWCAD`、`PLATFORM=Microsoft Windows NT Version 6.2`、`DWGCODEPAGE=ANSI_936` |
| 不支持的变量 | `ACADMAINTVER`、`SAVEVER`（GetVariable 报错） |
| 进程位数 | `PE machine=0x014C` ⇒ **x86（32 位）** |
| COM 注册位置 | **`HKLM\SOFTWARE\Classes\Wow6432Node\CLSID`**（32 位视图）；64 位客户端可正常驱动（out-of-proc） |
| ProgID → CLSID | `ZWCAD.Application` / `ZWCAD.Application.16` → `{FB081ED6-0D7E-489F-A6B7-F7CEAEC10661}`；`ZWCAD.Drawing` → `{6EEE4C76-6952-445D-862D-D22C7429770E}` |
| LocalServer32 | `C:\Program Files (x86)\ZWCAD Classic Chs\ZWCAD.EXE`（COM 启动时附加 `-Embedding`） |
| 类型库 | `ZRXSDK\inc\Icad.tlb`（310 KB）+ `IcadTLB.h`（55,279 行，MIDL 生成）⇒ **可离线拿到完整 API 面** |
| 授权 | **本次全部实测未遇授权阻塞**：`LocalTrial_ZWCADCLASSIC.asr` + `Rockey4ND.dll` 在位，CreateObject 一次成功（5 次冷启动 8.0–9.4 s 全部成功）。`%APPDATA%\ZWSOFT\zwco.log` 仅有 4 月历史 `获取授权失败，出错码：-1` 记录，本次无新增 |
| 客户端公寓 | PowerShell 5.1 默认 **STA**，一切正常；未测 MTA |

> 注：`SetVariable('ACADMAINTVER')`/`SAVEVER` 不支持说明**运行时能力探测必须 try/catch**，不能靠版本号分支。

---

## 2. A. 能力表（可用 / E_NOTIMPL 空壳 / 卡死）

### 2.1 可用（全部有实测返回）

| 能力 | API | 实测结果 | 耗时 |
|---|---|---|---|
| 创建实例 | `New-Object -ComObject ZWCAD.Application` | 可见实例（`Visible=True`），新 Drawing1 | **8.0–9.4 s**（冷） |
| 附着已开实例 | `Marshal.GetActiveObject`（P/Invoke oleaut32） | 跨进程拿到同一实例，HWND 一致 | **191 ms** |
| 复用实例 | 再次 `New-Object` | **复用**（进程 1→1，HWND 不变） | 532 ms |
| 读版本/路径 | `Version`/`Name`/`FullName`/`Caption` | 全部可读 | <5 ms |
| 主窗口句柄 | `Application.HWND` | 118827746 / 53611854 / 571418036（每次不同） | 24 ms |
| 改标题（用户可见提示） | `SetWindowText(app.HWND, "...")` | **成功**：标题变 `... [AI-COLLAB]`，`app.Caption` 同步变化 | 429 ms（含 400 ms sleep） |
| 状态栏写提示 | `Application.StatusBar = "..."` | put/get 均成功 | 122 ms |
| 忙闲探针 | `Application.GetAcadState().IsQuiescent` | 可读，空闲时 True | **3.5 ms/次** |
| 缩放 | `Application.ZoomExtents()` | 成功 | **7 ms** |
| 文档集合 | `Documents.Count` / `.Add()` / `.Item(i)` / `.Activate()` | 全部可用；Add 后 `ActiveDocument` 自动切到新图 | Add 193–1181 ms |
| 关闭文档 | `Document.Close(SaveChanges:bool, File)` | `Close($false)` 丢弃改动，**不弹框** | <300 ms |
| 退出 | `Application.Quit()` | 与 Close($false) 配合后干净退出 | 5.4–6.9 s |
| 系统变量 | `GetVariable` / `SetVariable` | 0.47 / 0.60 ms | — |
| 执行命令 | `Document.SendCommand(str)` | **近似同步**：返回时实体已落地；末尾 `\n` 非必需 | 5 ms |
| LISP 变量回传 | `GetLispVariable` / `SetLispVariable` | **标量（int/real/string）可用**；未设变量返回 `False` | 1–17 ms |
| 撤销分组 | `Document.StartUndoMark()` / `EndUndoMark()` | **有效**，见 §6 | <10 ms |
| 句柄→实体 | `Document.HandleToObject(handle)` | 成功（`AcDbLine`，layer 正确） | 9 ms |
| 图层 | `Layers.Add(name)` / `.Color` / `ActiveLayer=` | 成功（`AI_TMP` color=3 激活） | 81 ms |
| 选择集 | `SelectionSets.Add` / `.Select(11=All)` / `.Erase()` / `.Delete()` | Select 11,000 = 39 ms；Erase 11,000 = 2,134 ms | 见 §4.2 |
| 存盘 | `Document.SaveAs(path, versionCode)` | 成功，见 §4.4 | 142 ms |
| 实体枚举 | `foreach ($e in $doc.ModelSpace)` | 0.54 ms/实体 | 见 §4.1 |
| 错误/命令态 | `GetVariable("LASTPROMPT")` | 可读（如 `Missing: 2) >`） | 3 ms |
| 多文档 | `Documents.Item(i).Activate()` | ActiveDocument 语义正确 | 1,181 ms |

### 2.2 E_NOTIMPL / 空壳 / 故障（**决定通道，比"能用什么"更重要**）

| API | 声明 | 运行时 | 证据 |
|---|---|---|---|
| **`Document.EvaluateLispExpression(expr)`** | IDL 完整（`IcadTLB.h:64388`，`BSTR → VARIANT*`） | **`0x80004001 E_NOTIMPL`** | 4 种表达式（算术/`getvar`/`strcat`/`progn+entlast`）全部同一错误 |
| **`Application.Eval(expr)`** | 存在（`GetIDsOfNames` 命中） | **`0x80010105 RPC_E_SERVERFAULT`**（服务器抛异常，比 E_NOTIMPL 更危险） | 2 次均崩 |
| `Application.ListARX/LoadARX/UnloadARX` | — | **不存在**（`GetIDsOfNames` 未命中） | ⇒ NETLOAD 只能走命令，不能走 COM |
| `Application.BeginUndoMark/EndUndoMark/Undo` | — | **不存在**（撤销标记只在 Document 上） | 同名成员在 Document 上命中 |
| `Document.ZoomExtents` / `.Handle` / `.ObjectID` / `.AuditInfo` / `.GetEntityType` | — | **不存在**（ZoomExtents 在 Application 上；句柄用 `HandleToObject` 反查） | GetIDsOfNames |
| `Document.Saved`（写） | 只读 | **只读**：`$doc.Saved = $true` 报 `Cannot find an overload for "Saved"` | 无法用它抑制保存提示 |
| `SetLispVariable(name, @(1,2,3))` | 标量 | **`0x80070057 E_INVALIDARG`**（列表/数组不支持） | 数字/字符串均成功 |

### 2.3 卡死 / 静默失败

| 现象 | 复现 | 根因 / 结论 |
|---|---|---|
| **自定义 LISP 命令读 SetLispVariable 的全局变量 → 永不完成** | `SetLispVariable("PRB_N",1000)` + `SendCommand("PRB-MKN")`（`c:PRB-MKN` 内 `(while (< i PRB_N) ...)`）→ 15 s 超时，变量从未被赋值 | 自定义命令 `c:NAME` 经 SendCommand 调用**不可靠**（同一 `.lsp` 里不依赖外部变量的 `c:PRB-HSTR`/`c:PRB-DUMP` 却正常）。**规避：写侧一律用 inline `(progn ...)`，不调自定义命令、不依赖 COM 设进去的全局变量** |
| **长生成串 SendCommand 静默失败** | 生成 40 条 `(entmake ...)`（3,864 字符）以上一次发 → `delta=0`，**`LASTPROMPT` 仍是 `命令:`（无任何报错）** | **单条 SendCommand 上限 ≈2 KB**（1,944 ✅ / 3,864 ❌），见 §4.5；**静默失败最危险，必须回读校验** |
| **语法错 LISP 污染命令态** | 发 `(progn (setq X (+ 1 2)`（少 2 个右括号）→ `LASTPROMPT` 变 `Missing: 2) >`，之后 4 条命令（含 1000 点批量、脚本、多命令串）**全部被当续行吞掉** | 必须前置括号/引号配平校验；恢复手段未完全验证（见 §9 未测项） |
| **中文路径经 SendCommand 传入失效** | LISP `(open "<repo>/.../x.txt" "w")` → 15 s 无文件；`SaveAs` 到同路径 `E_FAIL 0x80004005` | COM→LISP 字符串按 ANSI 处理，中文路径被破坏。**规避：CAD 侧落盘一律用纯 ASCII 路径（`%TEMP%\zwprobe\`），再由 agent 拷进工作区** |

---

## 3. B. 成本模型（决定 skill 设计的数字）

| 操作 | 耗时 | 说明 |
|---|---|---|
| **冷启动实例** | **8,026 / 8,056 / 8,611 / 9,235 / 9,342 / 9,424 ms** | 6 次采样，均值 ≈ 8.8 s；`Visible=True` |
| 附着已存在实例 | **191 ms** | 后续调用建议一律「先 GetActiveObject，失败再 Create」 |
| 第二次 CreateObject（已运行） | 532 ms，**不新起进程** | 天然单实例 |
| `Documents.Add` | 193 ms（首次）–1,181 ms | — |
| `GetVariable` | **0.47 ms/次** | COM 往返基准 |
| `SetVariable` | **0.60 ms/次** | — |
| `GetAcadState().IsQuiescent` | **3.5 ms/次** | — |
| `GetLispVariable`（小标量） | 1–17 ms | 首次 17 ms |
| `SendCommand`（单条短命令） | **5 ms** | 返回时效果已生效 ⇒ 可当同步用 |
| **COM 逐实体 AddLine** | **0.90 ms/个**（100 个 = 90 ms；首个 13 ms） | 1,000 个 ≈ 0.9 s |
| COM 逐实体 AddText | **0.59 ms/个** | 100 个 = 59 ms |
| COM AddLightWeightPolyline / AddMText | 4 / 5 ms（单条） | — |
| **批量写 1,000 实体（一条 SendCommand + inline LISP 循环）** | **CAD 内 16 ms / 墙钟 64–85 ms** | — |
| **批量写 10,000 实体（同上）** | **CAD 内 152 ms / 墙钟 179–195 ms** | **写侧不是瓶颈** |
| **读全图句柄 11,000 条（一次 GetLispVariable，51,390 字符）** | **CAD 内 522 ms / 墙钟 561 ms** | 一次取全 |
| **读全图完整 CSV 11,000 行（288,950 字符）** | **CAD 内 3,724 ms / 墙钟 3,757 ms** | 一次取全 |
| COM 枚举 10,000 实体（逐个读 Handle） | **5,404 ms（0.54 ms/个）** | 比一次取全慢 10× |
| `SelectionSet.Select(11=All)` 11,000 实体 | **39 ms** | 一次调用选中全图 |
| `SelectionSet.Erase()` 11,000 实体 | **2,134 ms（0.19 ms/个）** | 一次调用批量删 |
| `SaveAs(path, 37=DXF2007)` 11,000 实体 | **142 ms，1,740,394 B** | 见 §4.4 |
| `ZoomExtents` | 7 ms | — |
| **`Close($false)` + `Quit()`** | **5,407 / 6,559 / 6,888 ms，残留 0** | 收尾正解 |
| `Quit()`（脏文档、不 Close） | **卡住**：15 s 未退出，残留 1 进程 + 模态框 | 见 §8 |

> 对比 WPS 侧已量化结论（`-Mta` 后 0.13 s/条）：**CAD 的 COM 往返快 2 个数量级（0.5–0.9 ms）**，「COM 往返太慢」在 CAD 上**不成立**，不需要为省往返做特别设计；真正的约束是**启动 9 s** 与**落盘加密**。

---

## 4. 读 / 写 / 导出 三条通道的实测对比

### 4.1 读侧：一次取全 vs 逐实体枚举

| 方案 | 11,000 实体耗时 | 数据形态 | 结论 |
|---|---|---|---|
| **LISP 一次性拼串 → `GetLispVariable`** | 句柄 **522 ms**；完整 CSV **3,724 ms** | 一个字符串（51 KB / 289 KB） | ✅ **首选**，等价 WPS 的 `Content.WordOpenXML` 一次取全；无文件、不受 DGS 影响 |
| COM 枚举 `foreach ModelSpace` | 5,404 ms | 逐个 COM 对象 | 慢 10×，但适合按需取少量 |
| **`SaveAs` 出 DXF（2007 码 37）** | 142 ms / 244 KB–1.74 MB | 文件被 DGS 加密，但**可解密**（见下） | 改文档身份；且**扩展名会被强制补成 `.dxf`** |
| `Document.Export(path,"DXF")` | **未跑通** | — | PowerShell late-binding 报 `Cannot convert the "DXF" value of type "string" to type "string"`；`$null` 三参报 NRE；`Missing` 三参报类型转换错。**未验证是否 API 本身不可用**（建议用 pywin32 复核，见 §11） |

**DXF 对账（`ModelSpace.Count` 到底数什么）—— 已完成**

`SaveAs(<tmp>\dxf_as_dxf.dxf, 37)` → 文件被 DGS 加密（头 `17DA5FA0…`）→ 用本机既有 `decrypt-cad-image` 技能的 `decrypt-files.ps1 -NoBatch` 解密（`acad.exe` 伪装白名单，成功）→ 头部恢复为 `  0\r\nSECTION…` → ezdxf 解析：

```
dxfversion = AC1021 (R2007)     ← 与 SaveAs 版本码 37 一致
modelspace_total = 105          ← COM ModelSpace.Count 也是 105 ✅
types = {LINE:100, CIRCLE:2, LWPOLYLINE:1, TEXT:1, MTEXT:1}   ← 与构造时完全一致
layers = {'0': 105}
```

⇒ **`ModelSpace.Count` 数的是「模型空间实体数」，与 DXF MODEL_SPACE 实体一一对应**（不含图层/块定义/布局）。这给了 skill 一个可靠的对账基准。
⇒ 另一条独立对账：LISP `(entnext)` 循环计数 = 11,000，与 COM `ModelSpace.Count` = 11,000 一致（§4.1）。

### 4.2 写侧：批量 vs 逐实体 vs 选择集

| 方案 | 规模 | 耗时 | 撤销归属 | 结论 |
|---|---|---|---|---|
| **一条 SendCommand + inline LISP `while`+`entmake`** | 10,000 | **152 ms（CAD 内）** | **整批 1 步** | ✅ **首选**；`(setvar "CMDECHO" 0)`+`(setvar "OSMODE" 0)` 已在串内设置 |
| COM 逐实体 `AddLine` | 1,000 | ≈900 ms | **每个 1 步**（10 个 → `_.U` 只退 1 个） | 适合少量/需逐实体返回句柄 |
| `SelectionSet` 批量 `Erase` | 11,000 | 2,134 ms | 1 步 | 适合批量删/移（选择集还有 `Move/Rotate/ScaleEntity`） |
| 自定义 LISP 命令 `c:NAME` | — | **不可靠** | — | ❌ 见 §2.3 |

### 4.3 落盘与透明加密（DGS）——**重要**

| 测试 | 结果 |
|---|---|
| LISP 写 `%TEMP%\zwprobe\from_lisp_temp.txt` | ✅ **明文**（头 `68656C6C6F...` = `hello-plaintext-from-lis`） |
| LISP 写 `%TEMP%\zwprobe\lisp_written.dxf`（内容是 DXF 文本） | ❌ **加密**（头 `17DA5FA01633CD9A`，DGS 标记） |
| LISP 写 `...\lisp_txt_dxfcontent.txt`（内容同样是 DXF 文本，只是扩展名 .txt） | ✅ **明文**（`0..SECTION..2..HEADER..`） |
| `SaveAs` 出 `saveas_2007.dxf`（11,000 实体，1.74 MB） | ❌ **加密**（ezdxf 报 `not a DXF file`，头 `17DA5FA01633CD9A`） |

**结论：加密按「扩展名」而非「进程」或「目录」生效**（同内容 `.txt` 明文 / `.dxf` 密文）。
- ⇒ 读侧若一定要 DXF，两条路都验证过：
  1. **解密**：本机既有 `decrypt-cad-image` 技能的 `decrypt-files.ps1 -NoBatch`（`acad.exe` 伪装白名单）对 CAD 写出的 `.dxf` **解密成功**，解密后 ezdxf 正常解析（§4.1 对账即走此路）；
  2. 让 CAD 把 DXF 文本写成 `.txt` —— **但 `SaveAs` 会强制把扩展名补成 `.dxf`**（实测传 `dxf_as_txt.txt` 得到的是 `dxf_as_txt.txt.dxf`，仍是密文），所以这条路走不通。
- ⇒ **但读侧首选根本不用文件**：LISP 拼串 + `GetLispVariable`（§4.1）完全绕开 DGS，也省掉解密步骤。

### 4.4 SaveAs 版本常量（`ZcSaveAsType`，来自 `IcadTLB.h:3093`）

| 常量 | 值 | 常量 | 值 |
|---|---|---|---|
| `zcR12_dxf` | 1 | `zc2000_dwg` / `zcR15_dwg` | 12 |
| `zcR13_dxf` | 5 | `zc2000_dxf` / `zcR15_dxf` | 13 |
| `zcR14_dwg` | 8 | `zc2004_dwg` / `zcR18_dwg` / **`zcNative`** | **24** |
| `zcR14_dxf` | 9 | `zc2004_dxf` / `zcR18_dxf` | 25 |
| `zc2007_dwg` | 36 | **`zc2007_dxf`** | **37** |

**本版本最高只到 2007 格式（无 2010/2013/2018）**；`SaveAs(path, 37)` 实测成功（142 ms / 244 KB–1.74 MB，输出 `$ACADVER=AC1021`），但 **`SaveAs` 会改变 `doc.Name`/`FullName`（身份漂移）** ⇒ **绝不可对用户的图纸调 SaveAs**；只读导出应用 `Export` 或「另开副本再 SaveAs」。另注意：**`SaveAs` 会按 Version 强制补扩展名**（给 `.txt` 也变成 `.txt.dxf`）。

### 4.5 单条 SendCommand 的规模上限（**已精确定界**）

`scripts/probe/27-sendcmd-limit.ps1` 用不同条数的显式 `(entmake ...)` 文本逐档测试（每档 15 s 超时判定）：

| entmake 条数 | 字符串长度 | 结果 | 墙钟 |
|---|---|---|---|
| 10 | **984 字符** | ✅ 全部创建（delta=10） | 48 ms |
| 20 | **1,944 字符** | ✅ 全部创建（delta=20） | 30 ms |
| 40 | **3,864 字符** | ❌ delta=0，`LASTPROMPT` 仍是 `命令:`（**静默**） | 15 s 超时 |
| 80 | 7,724 字符 | ❌ 同上 | 15 s |
| 160 | 15,564 字符 | ❌ 同上 | 15 s |
| 320 | 31,244 字符 | ❌ 同上 | 15 s |

- **结论：单条 `SendCommand` 的安全长度 ≈ 2 KB（实测边界在 1,944 ✅ 与 3,864 ❌ 之间，极可能是 2,048 缓冲）**；超限**不报错、静默丢弃**，最危险。
- **实体数量不是限制**：一条 ~250 字符的 `while` 循环表达式可以画 **10,000** 个实体（152 ms）。
- ⇒ **写侧规范：单条 SendCommand 只用「短循环表达式」（≤ ~2 KB），量靠 LISP `while` 循环承担；绝不要拼接大段显式 LISP 文本**。逐实体 COM（0.6–0.9 ms/个）作为需要逐个句柄时的兜底；超大批量按 2 KB 分批发。

---

## 5. 撤销语义（skill 回退机制的根据）

| 场景 | base | 创建后 | 一次 `_.U` 后 | 结论 |
|---|---|---|---|---|
| U1：10 次 COM `AddLine`，**无** mark | 208 | 218 | **217** | 每次 COM 编辑 = **独立 1 步** |
| U2：10 次 COM `AddLine`，包在 `StartUndoMark/EndUndoMark` | 217 | 227 | **217** | ✅ mark 把 10 次合成 **1 步** |
| U3：**一条** SendCommand 创建 10 个实体 | 217 | 227 | **217** | ✅ 一条 SendCommand 天然 **1 步** |
| U4：两次 mark（各 5 个），一次 `_.U` | 3000 | 3010 | **3005**；第二次 U → **3000** | ✅ **每个 mark = 1 步**（U4 复测确认，此前"只回退一半"是误记数量） |
| U5：一条 SendCommand 创建 100 个实体，一次 `_.U` | 3000 | 3100 | **3000** | ✅ 整批 1 步 |

**结论**：`StartUndoMark`/`EndUndoMark` **真实有效**（不是空壳），配合「一条 SendCommand = 一步」，skill 可以用 **「一次 AI 操作 = 一个 undo 组」** 表达可回退；用户一次 Ctrl+Z 即可整体撤销 AI 那一步。这比 WPS 的修订模式更简单。

---

## 6. C. 三种典型用户的实操路径

### ① CAD 没开 → 该不该起实例？
- **可以起**（无授权阻塞，6/6 成功），但 **冷启动 8.0–9.4 s** 是最大单笔开销。
- 建议：**懒启动 + 即时反馈**（先把状态栏/标题写「AI 启动中…」，或先返回"正在连接 CAD"）；`CreateObject` 是**幂等**的（已运行则复用，532 ms），所以「先 GetActiveObject（191 ms），失败再 Create」是最优路径。
- 不建议为省 9 s 做常驻 daemon：CAD 常驻会占用户桌面；但若要，本报告的 host-hold 模式（后台进程持有实例 + 跨进程附着 191 ms）已被证明可行。

### ② CAD 已开、用户正在操作 → 怎么判忙？
- `GetAcadState().IsQuiescent`：3.5 ms 可读，空闲时 True。
- ⚠️ **但实测在 30,000 实体批量执行期间 6 次采样全部为 True** ⇒ **`IsQuiescent` 不能当作"CAD 正在忙"的判据**（至少对 agent 自己发起的批量无效）。
- 可用的替代判据：
  - `GetVariable("LASTPROMPT")`：能看出是否停在 `Missing: ...>` 之类的续行/错误态（3 ms）；
  - 写操作一律包 `StartUndoMark/EndUndoMark` + 用 `HandleToObject`/句柄数回读校验；
  - 若担心与用户抢命令，先用 `StatusBar`/标题提示（§2.1），并在 SendCommand 前确认 `LASTPROMPT` 是干净的 `命令:`。

### ③ 收尾怎么不留进程
```powershell
try { $doc.Close($false) } catch {}      # 丢弃改动、不弹保存框
Start-Sleep -Milliseconds 400
try { $app.Quit() } catch {}
# 轮询 Get-Process zwcad* 直到 0（实测 5.4–6.9 s）
```
- **绝不能只调 `Quit()`**：脏文档会弹 `#32770` 模态框「是否保存对 Drawing1 的更改?」，`Quit()` 返回 OK 但进程滞留（实测残留 1 个，需 `WM_COMMAND(IDNO=7)` 才退）。
- **不要按映像名批量杀**（沿用 WPS 铁律）：`ZWCAD.EXE -Embedding` 的自动化实例与用户可见实例**进程名相同**；且实测 **`MainWindowHandle=0` 不可靠**（我们的 COM 实例 `app.HWND` 有效且窗口可见，但 PowerShell 的 `MainWindowHandle` 读出 0）⇒ 要区分只能**记住自己起的 PID**（`app.HWND` + `GetWindowThreadProcessId` 反查），只清自己那份。

---

## 7. 可复现代码片段

### 7.1 附着或创建 + 干净退出（PowerShell 5.1）
```powershell
# lib-com.ps1 里 ComProbe.GetActive 走 oleaut32!GetActiveObject
$app = $null
try { $app = [ComProbe]::GetActive('ZWCAD.Application') }   # 191 ms，复用用户实例
catch { $app = New-Object -ComObject 'ZWCAD.Application' }  # 8-9 s 冷启动
$doc = $app.ActiveDocument
[void]$doc.SetVariable('CMDECHO', 0); [void]$doc.SetVariable('OSMODE', 0)
# ... work ...
try { $doc.Close($false) } catch {}
Start-Sleep -Milliseconds 400
try { $app.Quit() } catch {}
```

### 7.2 一次取全图（读侧首选，无文件、绕开 DGS）
```powershell
$expr = '(progn (setq s "" e (entnext) n 0) (while e ' +
        '(setq s (strcat s (cdr (assoc 5 (entget e))) "|" (cdr (assoc 0 (entget e))) "|" (cdr (assoc 8 (entget e))) ";") ' +
        'n (1+ n) e (entnext e)) (setq PRB_DUMP s PRB_N n))'
$doc.SendCommand($expr + "`n")
do { $v = $doc.GetLispVariable('PRB_DUMP'); Start-Sleep -Milliseconds 25 } while ($v -eq $false)
# 11,000 实体 → 289 KB 字符串，3.7 s
```

### 7.3 批量写 + 撤销组（写侧首选）
```powershell
$doc.StartUndoMark()
$doc.SendCommand('(progn (setvar "CMDECHO" 0) (setvar "OSMODE" 0) (setq i 0) ' +
                 '(while (< i 10000) (entmake (list (cons 0 "LINE") ' +
                 '(cons 10 (list (* 1.0 i) 0.0 0.0)) (cons 11 (list (* 1.0 i) 10.0 0.0)))) (setq i (1+ i))) ' +
                 '(setq PRB_OK "done"))' + "`n")
$doc.EndUndoMark()   # 10,000 实体 152 ms；用户一次 Ctrl+Z 全撤
```

### 7.4 结果回传（同步、无文件）
```powershell
$doc.SetLispVariable('PRB_N', 7)                      # 标量可传；列表 E_INVALIDARG
$doc.SendCommand('(setq PRB_R (* 6 PRB_N))' + "`n")
$r = $doc.GetLispVariable('PRB_R')                    # → 42，实测 2 ms
```

---

## 8. 失败模式清单（skill 必须处理的）

1. **`Quit()` 卡模态框**（脏文档）→ 僵尸进程。正解 `Close($false)` + `Quit()`。
2. **语法错 LISP 污染命令态**（`Missing: 2) >`）→ 后续命令全被吞。**必须前置括号/引号配平校验**。
3. **长生成串静默失败**（48 KB 以上无报错）→ 必须靠「发送后回读句柄数/实体数」判定成败，不能信 SendCommand 无异常。
4. **中文路径经 SendCommand 失效** → CAD 侧落盘只用 ASCII 路径。
5. **`.dxf/.dwg` 落盘即密文**（DGS）→ 读侧别走 DXF 文件；必须走时用 `.txt` 扩展名或解密。
6. **`SetLispVariable` 传列表** → `E_INVALIDARG`；大结构只能传字符串再在 LISP 里 `(read ...)` 解析（未验证）。
7. **`doc.Saved` 只读** → 不能用它抑制保存提示。
8. **`Application.Eval` 抛 `RPC_E_SERVERFAULT`** → 不要碰；Document 上的同名方法也是空壳。
9. **`Export("...","DXF")` 在 PowerShell late-binding 下报类型转换错** → 改用 `SaveAs`（会漂身份）或 pywin32 复核。
10. **`IsQuiescent` 不可靠**（批量执行期间仍 True）→ 别拿它做互斥。

---

## 9. 未测 / 不确定项（明确标注）

| 项 | 状态 |
|---|---|
| `Document.Export` 是否 API 本身可用 | ❌ 未跑通（PS 封送问题），建议 pywin32 复核 |
| `GetEntityType` / `AuditInfo` / `PurgeAll` / `WBlock` / `Plot` / `Import` | 声明存在，**未逐个实测** |
| XData `SetXData/GetXData` | ❌ PS 报重载不匹配（`Cannot find an overload ... "1"`），未验证 CAD 侧是否可用 |
| `SelectionSet.Select` 的 **过滤器参数** | ❌ 传 `(11,$null,$null,8,'PRB_NOSUCH')` 仍返回全部 11,000 ⇒ 过滤器未生效（PS 封送问题或 API 差异），未定论 |
| `Application.Eval` 以外的其它「值返回」通道（`GetInterfaceObject`、`LoadZVB` 宏） | 未测 |
| 命令行开关 `/b script.scr`、`/t 模板`、直接带 dwg 路径启动 | ❌ 未测（时间不足）；`.scr` 经 SendCommand `_.SCRIPT` 的测试被命令态污染干扰，未定论 |
| 用户「手动开着图纸」的 GUI 实例能否被 ROT 附着 | ⚠️ 只验证了 **COM 启动实例**的跨进程附着（191 ms 成功）；GUI 直接启动实例的 ROT 注册**未测** |
| 忙时（用户正在拖拽/执行命令）COM 调用的阻塞/超时行为 | ❌ 未测 |
| MTA 公寓、pywin32/comtypes 客户端性能 | ❌ 未测（仅 STA PowerShell） |
| 图纸/视口渲染截图（`PrintWindow`/PDF 栅格化）做视觉核对 | ❌ 未测（`ZoomExtents` 7 ms 可用，截图未做） |
| `AddTable` 路径 | 未测（Lead 已从 IDL 确认 Block 无 AddTable） |

---

## 10. 收尾状态

- 每个探针脚本结束都执行 `Close($false)` + `Quit()` + 轮询；**全部脚本结束后 `Get-Process zwcad*` = 0 残留**。
- 中途出现过 2 次僵尸进程（`Quit()` 卡在保存对话框），均已用 `WM_COMMAND(IDNO=7)` 关掉并确认退出；**最终状态 0 残留**。
- 未触碰任何用户图纸；测试文件仅 `%TEMP%\zwprobe\` 与 `<repo>\tmp\`。
