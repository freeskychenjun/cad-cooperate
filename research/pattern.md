# wps-office-cooperate 的可迁移骨架 → ZWCAD 协作 skill 设计稿

> 任务：task-3。**只读** `<sibling-repo>`（v1.34，commit `e52ae34`），从中提炼「在用户开着的窗口里实时改文档」这套已验证架构的**可迁移骨架**，并草拟 CAD 版命令面。
> 本文是**证据型设计文档**，不是 SKILL.md 正式版（正式版由 Lead 收口）。
> 证据分级：**【实测】**＝WPS 仓库内有具体数字/日期的实测记录；**【机制】**＝WPS 代码可佐证的结构（引用 `文件:行号`）；**【推断】**＝本人对 ZWCAD（AutoCAD 同源 ActiveX）的推断，**未经本机实测**，已标出由 `com-probe` / `api-research` 回填的位置。
> 本机环境事实（2026-11-04 核对）：`ZWCAD.Application` ProgID 存在，开始菜单有「中望CAD 经典版」；task-1 记录版本为 **ZWCAD Classic Chs 16.0.1111**（`C:\Program Files (x86)\ZWCAD Classic Chs\ZWCAD.EXE`）。本文所有 CAD 侧 api 名均按 **AutoCAD ActiveX 同名接口**书写，**是否齐全必须由 com-probe 逐条打勾**（见 §6.2）。

---

## 0. 一页结论（给 Lead 收口用）

**最该照搬的 3 件**（省的是"每条命令的地板价"和"改坏后的不可恢复"，与业务无关）

1. **常驻 daemon + 入口 MTA/helper STA 分离**（§1.1–§1.2）。WPS 实测：本体装修 ≥5–8 s/命令，拆开后热命令 0.5–3.5 s，其中 **2.2 s 是 STA 公寓初始化**（`-Sta` 2.2–2.4 s vs `-Mta` 0.13–0.17 s，且一个 4 KB 的 `[STAThread]` 探针 exe 也要 2.2 s）。CAD 的 COM attach 只会更贵，这条**照抄不改**。
2. **写侧三件套：锚点校验 → 写入 → 回读校验 → 失败整体回滚**（§1.5）。WPS 里这条把"改错一行/改错一格"从沉默事故变成可拦截的 `ANCHOR_MISSING`/`rolled back N`；CAD 没有修订层、误删不可逆，这条比在 WPS 里更值钱。
3. **读侧「一次取全 + 本地解析 + 按路径哈希缓存 + 双路径 A/B」**（§1.4）。WPS 用 `Content.WordOpenXML` 一次取全稿把 `snapshot` 48.5 s→6.1 s、`diff` 48.6 s→5.5 s（7334 段逐字节等价）。CAD 的对应物是**导出 DXF 再本地解析**（而非逐实体 COM 枚举），但**不能无条件照抄**——见下面"最该改"第 3 条。

**最该改的 3 件**（WPS 的前提在 CAD 上不成立）

1. **回退机制**：WPS 靠 Word 修订模式（可逐条拒绝，save 也不影响）+ 撤销栈；**CAD 没有修订层**，且外部 COM 改动**是否进 DWG 撤销栈未知**（WPS 的 Excel/PPT 已实测"COM 改动不进撤销栈、Ctrl+Z 撤不回"）。⇒ 改成 **helper 侧写前 journal（句柄 + 前值/克隆）+ `undoToken` + `restore`**，`undo`（调文档撤销）只在实测证明安全后开放，并保留"撤销不分作者"护栏。
2. **渲染核对**：WPS 是"一页 EMF 57–82 KB、`render` 不动用户视图、可逐页/总览/按表渲"；CAD 出图（Plot→PDF）是**秒级/页**且**改视图会动用户眼前的缩放**。⇒ 从"每页必渲"改成 **改区渲（ZoomWindow 局部）+ 渲前保存/渲后恢复用户视图 + 联系表**，整图出图只在"落盘前一次"和"批量打印本身就是任务"时做。
3. **锚点模型**：WPS 用"段号 + `expect` 文本"寻址（段号会漂，但有明确计数规则）；CAD **没有段落号**，几何对象靠 **Handle + 几何/属性指纹（类型+图层+首点+文字哈希）**，且"看到的实体 ≠ 枚举到的实体"（块内实体、XREF 内容、布局视口、属性 ATTRIB 都不在 `ModelSpace.Count` 里）。⇒ 锚点体系整体换掉，并把"Count 到底数了什么"当成**第一条必须实测**的语义（WPS 踩过同类坑：表格每行会多算一个空段落，见 §4.1 风险 1）。

---

## 1. 骨架七件套

WPS 侧的总架构（`OPTIMIZATION-PLAN.md:17`）：**入口层 → 包装层 → helper 层 → 编排层**，全部围绕一件事：**一次工具调用只付一次进程/公寓/attach 的地板价，然后把"读的全量"和"写的原子性"做成两条硬契约**。

| # | 件 | WPS 位置 | 一句话职责 |
|---|---|---|---|
| 1 | 入口包装器 | `scripts/woc.cmd`、`scripts/woc.ps1` | 只组 JSON、不碰 COM，跑 MTA，把参数原样送到包装层 |
| 2 | 常驻 daemon + 会话状态 | `scripts/woc-app-common.ps1:78-186`、`helper/shared/PipeServer.cs`、`word/State.cs` | 一个 app 一个热 helper，命名管道；会话状态落在 `%LOCALAPPDATA%` 按路径哈希 |
| 3 | COM 附着与忙重试 | `helper/shared/MessageFilter.cs`、各 helper 的 attach 逻辑 | `GetActiveObject` 附着到用户实例、按路径解析文档、被拒时 150 ms×6 s 重试 |
| 4 | 读侧快照 | `word/XmlText.cs`、`word/FastText.cs`、`State.cs:66` | 一次取全稿 + 本地解析 + 哈希缓存 + `-Slow` 双路径 A/B |
| 5 | 写侧原子批 | `word/Review.cs`、`excel/EtHelper.cs`(write/fill)、`word/Tables3.cs`(normalize) | pass1 全校验、pass2 应用、回读、失败整体回滚、一个撤销单元 |
| 6 | 渲染核对 | 三端 `render` / `render -Overview` / `render -Table` / `Excel -IncludeCharts` | 把"文档长什么样"变成 agent 能看的 PNG，并把渲染成本压成 O(需要的页) |
| 7 | 用户可见提示 | `shared/Tray.cs`、`hint` 命令、`open` 自动打标题 | 让用户**知道 AI 在动他眼前这份文档**，且**不写文档、不落盘** |

### 1.1 入口包装器（`woc.cmd` / `woc.ps1`）

**WPS 做法**【实测/机制】

- `woc.cmd:31` 最终就一句：`powershell -Mta -NoProfile -ExecutionPolicy Bypass -File <app>.ps1 %ARGS%`。它**明确不碰 COM**（注释 `woc.cmd:9-11`："The wrapper never uses COM - it only builds a JSON request and talks to the resident helper - so it can run in MTA and that 2 s simply disappears."）。
- 双入口是有原因的：`woc.cmd` 走 `cmd.exe`，而 **cmd 会在批处理看到命令行之前改写它**——以 `=` 开头的裸 token 会丢 `=`，`^` 被当转义符（`woc.ps1:5-14` 记录实测：`-Formula "=A15^1.5"` 到子进程变成 `A151.5`）。所以 PowerShell 调用方一律走 `woc.ps1`（PowerShell→PowerShell 不经 cmd 解析）。
- `woc.ps1:42-61` 还兜住了 PowerShell 的**参数绑定失败**（stdout 为空、stderr 是宿主文本，打破"stdout 永远一个 JSON"契约）：stderr 暂存到临时文件、退出后回放，并补一段 ASCII `BAD_ARGS` JSON。

**解决什么问题**：把"每条命令 2.2 s 的 STA 税"和"参数被 shell 吃掉导致静默改错"两个坑一次性堵住。

**CAD 版对应物与差异**

- 对应物：`zwc.cmd <zwcad> <命令> [参数]` + `zwc.ps1`，同样 `-Mta` 跑包装层。**理由同源**：包装层只拼 JSON 走管道，不需要公寓。
- **差异 1（更值钱）**：CAD 的 helper 不仅必须 STA，`CreateObject("ZWCAD.Application")` / attach 已运行实例的开销大概率**高于** WPS 的 attach（CAD 进程体量大）。WPS 那份"热命令 ~0.5–3.5 s"是 CAD 的下限参照，不是承诺——**首次 attach 与冷启动必须单独测**（§6.1 A）。
- **差异 2**：CAD 参数里会有大量**几何串**（`"0,0 100,0"`、`"1.5,2.0"`）和 LISP 片段（含 `(` `)` `"` `;` `'`）。`^` 之外还要防 **cmd 的 `%` 变量展开**与 `&`。⇒ 沿用结论：**一律 PowerShell 入口 + 长/几何/LISP 参数走 `-Items <json 文件>`**（WPS 已把中文正文一律走文件，`SKILL.md:150`、`excel-quirks.md:51`）。
- **差异 3**：CAD 有 `SendCommand` 这条"发原生命令"的通道，它**本身就是字符串→命令的注入面**。包装层必须把 `SendCommand` 收进 helper 内部（白名单化），绝不暴露成"随便传命令字符串"的 CLI 参数。

### 1.2 常驻 daemon（helper 进程 + 单实例 + 会话状态）

**WPS 做法**【实测/机制】

- 传输契约：**4 字节小端长度 + UTF-8 JSON，一请求一连接，严格串行**（`PipeServer.cs:9-16`）。"daemon 与一次性模式收**同一份 request JSON**、回**同一份 response JSON**，所以两者只是传输差异，可逐字节 A/B"——这条让 `tests/word/check_daemon.py` 能 40/40 断言一致性。
- **单实例**：`NamedPipeServerStream(..., maxInstances: 1, ...)`（`PipeServer.cs:50-51`），且 `handler` 跑在 `[STAThread] Main` 线程上（`PipeServer.cs:10-12`：WPS 自动化对象必须由创建它的线程使用）。
- **管道名 = 编译产物哈希**：`Get-DaemonPipeName($app,$sha)` → `woc-<app>-<sha12>`（`woc-app-common.ps1:85`）。改源码 = 新管道，旧 daemon 自己闲置退出，**不会跑错版本**。
- **编译缓存**：`csc.exe` 首跑编译，产物放 `%LOCALAPPDATA%\wps-office-cooperate\<app>-helper-<sha>`；源码哈希用「文件数 + 大小 + 最新 mtime」做键缓存（`woc-common.ps1:5-14`，因为**每次命令 `Get-FileHash` 全扫要 ~50 ms**，实测 52 ms）。
- **生命周期**：`open` 之后自动拉起（每个会话第一条命令，最合适的摊薄点）；`close` 关完最后一份 / `quit` / `serve -Action stop` 自动退；闲置 20 min 自灭（`PipeServer.cs:36`）；`WOC_DAEMON_IDLE_MS` 可调。**任何异常都静默回退一次性路径**（`woc-app-common.ps1:282-290`）。
- **会话状态**：`word/State.cs` 每文档一份签名（段数/修订数/保存标志/抽样文本哈希，`State.cs:30-104`），写在 `%LOCALAPPDATA%...\<Sha16(小写全路径)>.json`（`State.cs:66`）——**按文档路径哈希分目录**（Excel/PPT 原按文件名，v1.21 才改成路径哈希，因为"同名不同目录的两份文档共用基线"，`changelog.md:74`）。写命令前后各取一次签名，不一致就在响应里塞 `changedOutside` + `changedOutsideHint`（`State.cs:137-166`）：**"文档被人动过"是被检测出来的，不是假设的**。
- **跨进程共享资源上锁**：剪贴板 `Local\woc-clipboard`、状态文件 `Local\woc-state`（`WocLocks.cs:1-13`）。写前 tmp 再替换（原子写），否则并发 `open` 会丢 attach 条目。

**解决什么问题**：把「起 exe + attach COM + 找文档」从每条命令里拿掉（WPS 实测每条省 ~2.5 s，v1.14），并且让"同一份文档被两个进程改"从沉默事故变成可见警告。

**CAD 版对应物与差异**

- 对应物：`ZwcHelper.exe --serve <pipe>`，`[STAThread] Main`，`CreateObject("ZWCAD.Application")` 或 `Marshal.GetActiveObject` 附着，管道同款协议。`serve -Action start|status|stop|reset` 同款。
- **差异 1（必须比 WPS 更严）**：CAD 是**命令驱动**的应用，存在"当前有活动命令"这一状态（用户正拖一条线、正开着选项对话框）。daemon 缓存的 `ActiveDocument` 引用在用户切文档后会漂（WPS 已有同类问题：`wb.ActiveWindow` 常为 null，`SKILL.md:293`）。⇒ **文档必须按 `FullName` 解析，绝不依赖 ActiveDocument**（WPS 已经这么做，`DESIGN-parallel-tasks.md:83`；CAD 照抄）；并且每次写前查一次"是否有活动命令"（AutoCAD 有 `CMDACTIVE` 系统变量/`GetVariable`）→ 有则 `BUSY`。
- **差异 2**：WPS 的 `maxInstances=1` 只保证同一 app 串行；WPS 实测"daemon 忙时第二条命令 900 ms 抢不到管道就**静默另起一个 helper**"（`DESIGN-parallel-tasks.md:80`，实测 `helper_max=2`），两个 helper 同时挂同一个 WPS 实例。**CAD 上这个 bug 更致命**：两个 helper 各自开 UndoMark、各自 Regen、各自改选择集，会把用户的命令栈搞乱。⇒ **WPS 的 900 ms 回退在 CAD 上不能照抄**，要么排队（等管道，有超时），要么落一次性模式前先抢**文档级命名互斥锁**（`Local\zwc-doc-<sha16(FullName)>`）。这是"最该改"清单外的一条硬改动。
- **差异 3**：会话状态签名要重设计。WPS 用"段数/修订数/保存标志/抽样文本哈希"；CAD 没有页/段，等价物是 **`doc.Saved` + 实体计数（按类型/空间）+ 句柄最大值 + Extents 哈希 + 当前布局名**。注意 **`Saved=false` 时磁盘 mtime 不变**，所以文件指纹必须"内存签名优先、磁盘指纹兜底"（否则用户改了没存，快照会假命中缓存）。
- **差异 4**：CAD 的"写前快照"不能是文件拷贝。`SaveAs` 会**把活动文档换成新文件**（WPS 已踩过"SaveAs 换身份"，`memory`/`excel-quirks`），而 `Save` 会动用户文件并清撤销栈。⇒ 快照必须是**逻辑快照**：受影响实体的 `Handle` + 属性/几何前值，或"导出 DXF 到临时目录"（导出不改身份，但**是否包含未保存的内存状态必须实测**，§6.1 B）。

### 1.3 COM 附着与忙重试（OLE MessageFilter）

**WPS 做法**【实测/机制】

- `helper/shared/MessageFilter.cs` 在 `Main` 第一行注册 OLE 消息过滤器（`:19` "Register it before the first COM call"）。策略逐字借自 dsh-office：`HandleInComingCall → 0 (SERVERCALL_ISHANDLED)`、`RetryRejectedCall → dwRejectType==SERVERCALL_RETRYLATER && tick<6000 ? 150 : -1`、`MessagePending → 2 (PENDINGMSG_WAITDEFPROCESS)`。**即：被拒就每 150 ms 重试，最多等 6 s，超时才把 `BUSY` 抛给调用方**（`MessageFilter.cs:36-44`）。
- 覆盖的错误码：`0x80010001`（RPC_E_CALL_REJECTED）、`0x8001010A`（RPC_E_SERVERCALL_RETRYFLT）、`0x800AC472`（Office 忙变体）统一映射成 `BUSY`（`changelog.md:27`）。
- 诚实记录（重要，别当它已完全验证）："拒绝→重试→6 s 后 BUSY"**端到端未能在本机构造真实忙场景**，"机制已装、策略已验证、未实弹"（`changelog.md:27`）。
- 另有一条对调用方的一般性结论：**PS 5.1 的 `Start-Process -Wait` 会等子进程的全部后代**，而 `open` 会拉起长寿 daemon ⇒ 永不返回（`OPTIMIZATION-PLAN.md:224`）。任何用 `-Wait` 包 wrapper 的宿主脚本都会挂。

**解决什么问题**：用户正在打字/开了模态框时，COM 调用会被 OLE 立刻拒绝；不装过滤器 = 命令**立刻失败**，装了 = 等 ≤6 s，用户停手就直接成功。

**CAD 版对应物与差异**

- 对应物：**同一份 `MessageFilter.cs`（几乎零改动）**，注册在 helper 的 STA Main 首行。
- **差异 1**：CAD 的"忙"更多是**命令态**而不是"打字"。除了 OLE 拒调，还有"CAD 处于某个命令中间 → 写入被拒/被排队"。⇒ 除 MessageFilter 外要加**语义级忙检测**：`GetVariable("CMDACTIVE")`、活动文档是否有未完成命令、是否有模态对话框（CAD 的选项/打印对话框是真的模态，与 WPS 同）。
- **差异 2（更危险）**：`SendCommand` **不是同步阻塞到命令结束的语义**（AutoCAD 的 `SendCommand` 在命令需要用户输入时会挂起/排队）。⇒ **`SendCommand` 必须白名单化 + 前置"无活动命令"断言 + 后置"命令完成"轮询（带超时）**，绝不允许裸透传。这块要去 com-probe 拿实测（`DESIGN` 里 task-1 的第 4 问）。
- **差异 3**：CAD 可能装**加密狗/授权组件**（task-1 关注点 1），首次 `CreateObject` 可能弹授权窗口——那就是一个"不装过滤器就永久挂"的场景。**`serve -Action reset` 这条恢复通道必须照搬**（`woc-app-common.ps1:351-366`：按**完整 exe 路径**杀本版本 helper，绝不按映像名）；wrapper 侧读管道必须**异步 + 切片 + 截止**（`Read-PipeBounded`，`:138-154`，v1.34 才补上，之前是"永久挂起"）。

### 1.4 读侧快照（一次取全 + 按路径哈希缓存）

**WPS 做法**【实测】

- 根因链：`Content.Text` 块模型在真实报告上**不成立**——7334 段的稿子切出 7328 块、从第 2 段起整篇错位；更根本的是 **WPS 把每个表格行也算一个段落**（Σ表行 555 + 正文 6779 = 7334 = `Paragraphs.Count`）；且块模型要求 `Revisions==0`，而实时协作永远有未处理修订 ⇒ **协作场景下这条路等于关闭**（`changelog.md:90`）。
- 解法：`doc.Content.WordOpenXML` **一次取全稿**（8.2 MB 实测 0.4–1.7 s；4.1 MB 0.16 s）→ 本地 XML 解析（`w:p` 按文档序、每 `w:tr` 后补一个空段还原 WPS 段号、`w:delText` 不取以对齐 `Range.Text`）→ **抽样若干段与 COM 逐字比对，不符整体回退**。
- 战果：`snapshot` **48.5 s→6.1 s（8×）**、`diff` **48.6 s→5.5 s（8.9×）**、`read -SkipTableCells` 全篇 8.2 s；61 页/7334 段/23 表/16 处修订上**逐字节一致**。
- 双路径与可观测性：响应带 `textsPath` = `xml`/`fast`/`chunk`/`slow`，回退时带 `fastFail`；`-Slow` 强制旧路径；`textdiag` 一条命令出"为什么慢"（`SKILL.md:129`）。
- 缓存与隔离：快照按**文档全路径 SHA16** 分目录（`State.cs:66`），`-Out` 可把快照写到文档旁以便跨机器接力。
- 组合命令的价值：`captions`（一条命令替掉"全篇扫段落配对"）10521 段/32 表 **6.4 s**，而此前 22 次 `read` 全扫要 **4.5 min**；`table -Action plan` 6.9 s 给全 14 表结构/风险/题注/引用；`audit` 从 29.9 s→4.4 s（`README.md:5`、`changelog.md:105`）。

**解决什么问题**：CAD/Office 的"读"有两笔成本 —— **COM 往返次数**（结构性的，见 §2）和**把读到的内容塞进模型上下文**（token）。WPS 的答案是：**一次原生取全 → 本地解析 → 只回摘要 + 分页明细**，并把"慢"变成可解释的字段。

**CAD 版对应物与差异**

- 对应物（三条候选，按代价排序）：
  1. **导出 DXF 再本地解析**（最接近 WordOpenXML 的角色）：一次 `Export(dxfPath, "DXF", ...)`，然后 Python（`ezdxf`）或 helper 内部解析出实体索引：`handle / type(ObjectName) / layer / bbox / 文字内容 / 块名 / 标注测量值`。**优点**：一次调用拿全图，本地解析零 COM 往返，可做 diff/audit/工程量统计的公共底座。**缺点**：DXF 有损（动态块、代理对象、自定义对象、部分 XDATA、xref 内容缺失），且**大图导出可能秒级到十几秒**（WPS 的 0.16 s 是 4 MB OOXML；DWG 的 DXF 文本体积通常是 DWG 的 3–10 倍）。
  2. **COM 枚举 + 批量取属性**：`ModelSpace.Item(i)` 逐实体拿 `ObjectName/Layer/GetBoundingBox`（AutoCAD 一次 `GetBoundingBox` 给 min+max 两点，`Polyline.Coordinates` 一次给全部顶点 → **"数组型属性一次取"是 CAD 比 Word 强的地方**）。**优点**：真值、含块外属性、无需临时文件。**缺点**：O(实体数) 往返，1 万实体就是 3–4 万次跨进程调用。
  3. **原生 .NET API 在进程内**（若 `api-research` 确认 ZWCAD v16 带 `ZwSoft.ZwCAD.*` 托管程序集）：可以进程内遍历，省掉全部跨进程往返。**这是最优解，但依赖版本**（task-2 第 1/2 问）。
- **差异**：**两条路径的交叉点必须实测**（§6.1 B）。WPS 的结论是"一次取全永远赢"，因为它是一次 4 MB OOXML；CAD 上**小图 COM 枚举可能更快**（千级实体），"总是导 DXF"会亏。⇒ 设计上必须**按实体量/文件大小自动选路 + 响应里显式报 `readPath:"dxf"|"com"|"mgd"` + 保留 `-Slow` 强制开关**（双路径 A/B 逐字段比对是 WPS 最值钱的验收纪律，照抄）。
- **缓存**：键必须是 `(FullName 小写 SHA16, doc.Saved, 指纹)` 而不是磁盘 mtime —— 用户改了没存时 mtime 不变。DXF 缓存文件放 `%LOCALAPPDATA%\zwcad-cooperate\snap\<sha16>\<tag>.dxf`，**绝不写进用户图纸目录**（WPS 同一纪律，`workflows.md:166`）。

### 1.5 写侧原子批（锚点校验 + 回读校验 + 失败整体回滚）

**WPS 做法**【实测/机制】

- `revise`（Word）：`items[]` 每条带 `para` + `expect`（该段开头必须匹配）；**Pass 1 只校验**（段落存在 / `expect` / `find`），任一条不过则**一条都不改**；**Pass 2 按 `para` 降序**写（插入/删除不影响更小段号）；中途出错则把本批新增的修订逐条 `Reject()` 回滚，错误里写 `rolled back N revision(s)`；每条结果带 `textAfter`，调用方不必再 `read`（`DESIGN-word.md:71-75`）。
- `write`（Excel）：`expect` = 该格**当前显示文本必须完全相等**，不符即 `ANCHOR_MISSING`、**整批一条不改**；整批原子，中途 COM 错则用捕获的旧 `Formula` 逐格还原（`SKILL.md:429-430`）。
- `table -Action normalize`（Word，最成熟的一版，**CAD 应直接抄这个形态**）：`plan` 只读侦察 → `normalize` **逐表 try/catch 隔离**（单表失败只回滚该表、记 `failures[]`、继续其它表；不再整批回滚）+ **整条命令仍是一个用户级撤销单元** + **绝不隐式 save** + 写前快照/写后回读（列宽漂移 >2% 即恢复）+ 自检 `verification`（`DESIGN-table-normalize.md:67-73`）。
- 关键的"回读"教训：`table -KeepTogether` 逐行回读，WPS 没接受的行走 → 整条命令 `ok:false` + 点名行号，**不再"报成功其实没落上"**（`SKILL.md:126`）；`formula` 插入后校验 `OMaths.Count` 必须增加，否则回滚并 `MATH_FAILED`，"两者都不留残留"（`SKILL.md:120`）。
- 反例（省了回读的代价）：参考实现开"逐字符清空格"后表格阶段 **1.6 s → 126.9 s（+79×）**；因此 normalize 改成"整段一次写、没有逐字符操作"（`changelog.md:19`）。**含义：批量写必须"整块"，不能"逐单元顺手改"**——CAD 上就是"逐实体逐属性顺手改"= 灾难。

**解决什么问题**：把"改错位置/改一半"从沉默事故变成**可拦截 + 可整体撤回**。WPS 的原子性是"要么全不改，要么全改且失败全撤"，锚点是"我要改的那个东西还是我要改的样子吗"。

**CAD 版对应物与差异**

- 对应物：`entity -Items <json>` / `text` / `dim` / `layer` / `block` 全部走同一套原语：
  1. **Pass 1 校验**：handle 存在 + 类型匹配 + `expect`（关键属性/文字/图层）相符 + 不落在锁定图层/外部参照/只读空间 → 任一条不过，整批不改；
  2. **开始 UndoMark**（`doc.StartUndoMark()`，AutoCAD ActiveX 同名方法，待验证是否在 ZWCAD 暴露）；
  3. **Pass 2 应用**（批量、整块；禁止逐属性回环）；
  4. **Pass 3 回读**：按 handle 重新取属性/文字/`GetBoundingBox` 比对；标注要回读**测量值**（不是只回读文字）；
  5. **失败 → 整体回滚**：删本批新增（记下的新 handle）+ 把修改过的写回前值 + `EndUndoMark` 收尾（若 UndoMark 可用则一次 `Undo` 撤销整组，**但这依赖实测**，见 §2 第 15 条）。
- **差异 1（最硬）**：**没有"修订标记"这层**。Word 的 `revise` 用户能逐条接受/拒绝，save 也不影响；CAD 里"把线长改错"就是改错。⇒ 补偿手段只剩三样：**(a) 逐条回读 + 报告 before/after；(b) 写前 journal + `undoToken` + `restore`；(c) 默认不 save**（WPS 铁律 6 的直接沿用）。
- **差异 2**：**锚点指纹要多维**。WPS 的 `expect` 只要"段首文本"；CAD 的一段文字实体可能同时存在多个同内容 MTEXT（标题栏、说明、索引），所以指纹 = `handle` 为主 + `(type, layer, 首点坐标容差, 文字内容哈希/几何长度)` 为辅；`handle` 不符但指纹全符 → 允许"重定位"并**在响应里报 `relocated:true`**（这是 CAD 独有的机会，因为 handle 在整个 DWG 生命周期稳定，比段号可靠）。
- **差异 3**：**分区隔离要按"空间"而不是"表"**：模型空间 / 每个布局（`Layout`）/ 每个块定义（`Block`）/ 外部参照（**只读，一律跳过并计入 `skipped.xref`**）。逐布局 try/catch 隔离 = WPS 逐表隔离的对应物（批量打印、图框套用必须逐布局隔离）。
- **差异 4**：**批量写的"整块"载体不同**。Excel 有 `values` 二维块一次落位（数组尺寸必须精确，错则静默不落位）；CAD 没有对应原语，最接近的是 `Polyline.Coordinates`（一次给整条折线）与 `AddTable`。⇒ CAD 的"批"只能靠**减少实体数**（能用一条多段线就不用十条线）+ **减少属性写次数**（一次性 `SetXData`/逐属性合并）。

### 1.6 渲染核对（render 出图人眼可验）

**WPS 做法**【实测/机制】

- Word：`Windows(1).Panes(1).Pages(n).EnhMetaFileBits` → GDI+ 画 PNG（**没有 `Range.CopyAsPicture` 可用**）；页视图必须切到 `View.Type=3` 否则页数为 0（`word-quirks.md:17,60`）。单页 EMF 57–82 KB、非空白像素 4.77% 作为"不是空页"的判据（`DESIGN-word.md:94`）。
- 三档成本模型：`render -Page n`（单页放大）→ `render -Overview`（**6 页/张、3 列、格宽 640、最多 18 页**，超出 `capped:true`）→ `render -Table n`（只渲该表跨的页）。目的是**把"看"的成本压成 O(需要的页)**。
- 视觉纪律（v1.16 由来）：一次"新建 Excel + 讲公式"的会话 80 步/53.7 min，其中约 24 步（30%）耗在"截图→裁剪→放大→再看"上；**整屏截图会让后续每一步变慢（每步 27→40~80 s，图像累积进上下文）**（`SKILL.md:378-383`）。
- 剪贴板礼仪：Excel/PPT 的 render 走系统剪贴板 ⇒ `ClipboardKeeper` 前后保存/恢复，且"只有序列号仍等于我们最后一次 Sync 才恢复"，恢复数据标"不进 Win+V/不上云"（`workflows.md:426-440`）；跨 app 并发 render 用 `Local\woc-clipboard` 互斥（否则串图）。

**解决什么问题**：agent 说"改好了"必须能被**人眼**验；而"看"是这个体系里最贵的一环（token + 步数）。

**CAD 版对应物与差异**（这是**最该改**的一条）

- 对应物候选（按推荐度）：
  1. **`Export` WMF/EMF 或位图**（AutoCAD `Document.Export` 支持 `.wmf`/`.bmp` 等，ZWCAD 覆盖度待测）——最接近 WPS 的 `EnhMetaFileBits`，**不动用户视图**；
  2. **`Plot` 到 PDF 再渲 PNG**（`Plot.PlotToFile`）——语义最真（含打印样式表 CTB/STB），但**秒级/页**，只能用于"落盘前整图核对"和"批量打印本身"；
  3. **窗口截图**（`PrintWindow`/`BitBlt`）——最便宜但会带入 UI 且分辨率不可控；WPS 的结论是"能原生出图就别截屏"。
- **差异 1（成本模型变了）**：WPS 一页 EMF 是**毫秒级**，CAD 一页 Plot 是**秒级**（还要算上开打印对话框/缺打印机的风险）。⇒ **"每改一次就渲一次"在 CAD 上直接判死**。规则改成：**改区渲（`ZoomWindow` 到 bbox + 截图/导出）** → 一批改动只在**关键节点**渲 1 次 → 落盘前 `render -Overview`（布局缩略图联系表）1 次。
- **差异 2（会打扰用户）**：`ZoomExtents`/`ZoomWindow`/`Regen` **会改变用户眼前的视图**——WPS 的 render 完全不碰窗口。⇒ 必须先记录视图状态（`doc.ActiveViewport`/`VIEW` 表/`VIEWCTR`+`VIEWSIZE` 系统变量），渲完**恢复**；并且**禁止**在用户交互期间渲（`CMDACTIVE`）。这一条不做好，用户会看到自己的图"自己乱跳"。
- **差异 3（判据更强）**：CAD 不必只靠"非空白像素占比"。更硬的机械判据：**包围盒与视口范围的关系**（出框/超界）、**实体计数 > 0 且可见图层数 > 0**（全冻结=空白）、**文字高度/标注比例是否在该图纸比例的合理区间**、**PDF 页数 == 布局数**。像素判据只做最后一道"图没画歪"的兜底。
- **差异 4**：CAD 的 `render` 有"屏幕 ≠ 出图"的特有问题（打印样式表把颜色映射成线宽/灰度）。⇒ **凡涉及"出图长什么样"的核对，必须以 Plot→PDF 为准，不能以屏幕截图为准**（这是 Word/Excel/PPT 都没有的一类风险）。

### 1.7 用户可见提示（标题〔AI〕前缀、hint、状态栏/托盘、悬浮窗永久否决）

**WPS 做法**【实测/机制】

- `open` **自动**给窗口标签打前缀：`〔AI接管中〕 报告.docx`（`SKILL.md:68`）。**为什么必须放前面**：WPS 12 的标签页**从右截断**（约 22 个半角字符），后缀标记在长文件名上"截图像素与不加完全相同（等于没打）"（`word-quirks.md:31`）。只读文档自动标成 `〔AI接管中·只读〕`。
- `hint` 四通道：气泡（默认 6 s 托盘气泡）/ 状态栏一行 / 标题文字 / 托盘图标（`-Tray`，悬停显示"文档 · 当前步骤"，右键"结束协作：还原标题标记"，**4 小时自灭**）（`workflows.md:387-407`）。
- 状态栏的诚实结论：**Writer 的 `Application.StatusBar` 只写不读、且界面不渲染**（截图仍是页面/字数）；表格侧写入会真的显示 ⇒ 响应里 `statusBar:true` 只代表"写入被接受"（`word-quirks.md:32`）。
- PPT 侧的教训（对 CAD 极有参考价值）：标签页是**自绘**的 ⇒ 改窗口标题**只有任务栏/Alt+Tab 看得见**；唯一"界面里看得见"的通道是**逐页药丸**，但那是**内容**（用户 Ctrl+S 会存进文件）⇒ 默认不贴、`-SlideMark` 才开、落盘前剥离、被存进去自动修复（`workflows.md:401-404`）。
- 托盘实现坑：daemon 用 `--tray <state.json>` 重开自身，状态文件是唯一 IPC（**daemon 不懂 COM**）；spawn 必须 `UseShellExecute=true`，否则子进程继承调用方 stdout 管道，会被图标的 4 小时寿命卡死（`workflows.md:407`）。文档被用户关掉后图标自己消失（RestartManager 判定，实测 Word 9.6 s / Excel 8.6 s / PPT 7.4 s）。
- **永久否决**：不加置顶悬浮条/浮层（遮正文、抢焦点、要常驻跟随窗口、异常退出要收拾）——"用户明确决策，不要再推荐，也不要偷偷加回来"（`SKILL.md:74`）。

**解决什么问题**：用户的原话是"agent 接管后可否有比较友好的提示"——接管这一步原本**不可见**，用户会以为文件被"外人"改了。而"可见"必须**不打扰、不写文档、不落盘**。

**CAD 版对应物与差异**

- 对应物：
  - **窗口标题前缀**：`SetWindowText` 打在 ZWCAD 主窗口（或其 MDI 子窗口/标签）——**必须先实测哪个句柄真的可见**（WPS 的教训是"能写 ≠ 用户看得见"）。
  - **托盘图标 `-Tray`**：同款实现可直接复用（`shared/Tray.cs` 与 app 无关，只靠回调脚本还原标题）。
  - **气泡 `-Toast`**：同款。
  - **状态栏**：CAD 有命令行窗口（`Ctrl+9`）和状态栏，`SendCommand` 里的 `(princ "...")` 能在命令行显示——**但那是"画在图形界面里的输出"，且会进命令历史**，属于"可见但会污染用户界面"，需实测后决定，**默认不做**。
- **差异 1**：CAD 的"标签页"是文件标签（多文档 MDI），改标题可能被 CAD 自己重置（WPS 也如此：切标签页时 WPS 重置标题，下一条命令自动补回，`SKILL.md:71`）⇒ **每条命令都可幂等补标**。
- **差异 2**：**绝不使用"临时标记图层/图元"做可见提示**。WPS 的 PPT 药丸是"内容"，已经要处理"用户 Ctrl+S 存进文件"的污染链；CAD 里画进文件的标记会**进图、进打印、进他人参照**，污染面更大。⇒ **CAD 版明确不做"图上可见标记"**（这是"不迁移清单"里的一条）。
- **差异 3**：CAD 用户有"命令行回显"这个独有通道，但也最容易被误认为是程序输出。⇒ 若用，必须带固定前缀并与 `hint` 的 `-Clear` 对称（且 `finish`/`close` 自动还原）。

---

## 2. 性能结论的可迁移性（逐条判定）

WPS 侧的性能结论都是**带日期、带数字、带机器**的实测（`OPTIMIZATION-PLAN.md:31` 明确把它列为"应保留的资产"：`-Mta` 2.2 s、哈希缓存 50 ms、batch 220 ms/命令 vs 管道 1–15 ms）。下面逐条判断在 ZWCAD COM 上是否成立。

| # | WPS 结论（证据） | CAD 判定 | 理由 / 差异 |
|---|---|---|---|
| 1 | 每条命令 2 s 真凶 = **STA 公寓初始化**，不是 PowerShell、不是 .NET（`-Sta` 2.2–2.4 s、`-Mta` 0.13–0.17 s、`cmd /c exit` 0.03 s、4 KB `[STAThread]` 探针 exe 也要 2.2 s）【实测 v1.15】 | **极可能成立，且必须重测** | 同一台机器、同一类安全过滤驱动（本机装 Agile DGS 透明加密）。CAD helper 也必须 STA。⇒ 必须实测 `-Sta` vs `-Mta` 空转 + `[STAThread]` 空 exe 两个数；预期同量级。**且要测"CAD 侧 attach 的额外固化成本"**（CreateObject/GetActiveObject 本身的秒数）。 |
| 2 | 常驻 daemon 每条省 ~2.5 s（`status` 5.0→2.6 s 等）【实测 v1.14】 | **成立，且收益更大** | CAD attach 更贵。参照量级：WPS 热命令 0.5–3.5 s（v1.15 后 `status` 0.50 / `snapshot` 1.34 / `diff` 1.32 / `captions` 2.20 / `search` 2.41 / `read` 3.46 / `audit` 4.04）。**但目标不是"到 WPS 的数"，而是"到实测的下限"**。 |
| 3 | **COM 往返次数是唯一瓶颈**（不是数据量、不是 CPU）【实测 v1.10/v1.14】 | **成立，且在 CAD 上更极端** | 维度差：Word 一份 10521 段的报告 ≈ 1 万"单元"；CAD 一张中等图纸 ≈ 1 万–10 万实体，且每实体若取 4 个属性就是 4–40 万次跨进程调用。**但 CAD 有"数组型属性一次取"的优势**（`Coordinates`、`GetBoundingBox`、`XData`），优化空间比 Word 大。 |
| 4 | **多 Sub Agent 并行数学上无效**（总往返不变，只是排队）【实测：3 条重命令并发 7044 ms vs 串行 3980 ms；轻命令并发 1242→653 ms 有 1.9×】 | **成立，且必须加锁** | CAD 单实例 COM 全局串行更严格（还有全局命令态）。WPS 只有"事后警告 `multiInstanceWarning`"，CAD 建议**文档级命名互斥锁**（见 §1.2 差异 2）。 |
| 5 | `Content.WordOpenXML` 一次取全：`snapshot` 48.5→6.1 s、`diff` 48.6→5.5 s，7334 段逐字节等价【实测 v1.13】 | **原理成立，载体与阈值必须重测** | 对应物 = 导出 DXF 再本地解析。**不能无条件照抄**：WPS 的取全是 0.16–1.7 s；CAD 大图 DXF 导出可能秒级到十几秒，且 DXF 有损。⇒ **必须测"COM 枚举 vs DXF 导出"交叉点**（§6.1 B），并按实体量自动选路。 |
| 6 | 快照/缓存按**文档全路径 SHA16** 隔离（Excel/PPT 原按文件名，同名不同目录串基线）【实测 v1.21】 | **成立，且要多一维** | CAD 还要加 `doc.Saved`/内存指纹：**用户改了没存时磁盘 mtime 不变**，只按 mtime 缓存会假命中。 |
| 7 | **A/B 逐字段比对验收 + `-Slow` 强制开关**（快慢两份输出逐字段/逐字节一致才算过）【实测 v1.10–v1.15，多个版本都用这条验收】 | **必须照搬，且更狠** | CAD 双路径差异更大（DXF 有损）⇒ 除"逐字段一致"外还要有**已知有损项白名单**（动态块/代理对象/XREF），并把"降级"字段暴露给 agent（`readPath`、`lossy:[]`）。 |
| 8 | batch：5 条命令 1982→891 ms；CLI 405–460 ms/命令 vs 管道往返 1–15 ms【实测 v1.19】 | **成立，收益更大** | CAD 每条命令都要过 daemon 却省掉 attach ⇒ 更大的绝对节省。**但 batch 内多条的语义必须"各条独立、失败不回滚已成功"**（WPS 就是这么定义的，`SKILL.md:363`），重要批次仍要快照。 |
| 9 | 源码哈希缓存：每命令 `Get-FileHash` 全扫 ~50 ms（实测 52 ms）⇒ 缓存键="文件数+大小+最新 mtime"，386→330 ms【实测 v1.19】 | **成立，直接照抄** | 与 app 无关的纯工程优化。 |
| 10 | 剩下的 ~2 s 是**调用方 PowerShell 启动**，skill 侧压不掉【实测 v1.14/v1.15】 | **成立** | CAD 同理；这是宿主工具的开销，只能靠 batch 摊薄。 |
| 11 | `render` 的非空白像素判据（单页 EMF 57–82 KB、非空白像素 4.77%）【实测】 | **可迁移但不够用** | CAD 有更强的机械判据（包围盒/实体数/可见图层数/PDF 页数）；像素只做兜底（§1.6 差异 3）。 |
| 12 | **整屏截图会让后续每步变慢（27→40~80 s）**，图像累积进上下文【实测 v1.16】 | **成立，且 CAD 更严重** | CAD 截图分辨率高、图元密、PNG 更大 ⇒ token 更贵。⇒ CAD 的 `render` 默认必须**裁剪 + 缩放到固定宽度 + 联系表**，禁止整屏。 |
| 13 | "隐藏结构"陷阱：WPS 把**每个表格行也算一个段落**，块模型必然错位【实测 v1.13】 | **同型风险 100% 存在** | CAD 版对应："`ModelSpace.Count` 到底数什么"（块参照内部实体？XREF？布局视口？ATTRIB？）。⇒ **这是 CAD 侧第一条必须实测的语义**（§6.1 B0）。 |
| 14 | 一实例多文档会挂：一个 ET 实例开两个工作簿 → `Workbooks.Open` 不返回、实例变幽灵【实测 v1.20】 | **必须重测（高优先级）** | CAD 是天然 MDI（多文档），但 COM 侧能不能稳定 hold 多个 `Document` 对象、切换是否影响 `ActiveDocument`/撤销栈/命令态，必须实测。**在测出来之前，默认"一会儿只碰一份文档"。** |
| 15 | `undo` **不分作者**（4 次 undo 后原稿 2 条批注丢了 1 条、段数 4069→4066）；`save` **会清空撤销栈**；Excel/PPT 的 COM 改动**根本不进撤销栈**（Ctrl+Z 撤不回）【实测】 | **必须重测，且按最坏情况设计** | CAD 三种可能：① COM 改动**根本不进**撤销栈（最可能，与 Excel/PPT 同）；② 进了，但 `StartUndoMark/EndUndoMark` 能成组；③ 进了但会**覆盖用户自己的撤销记录**（最坏）。⇒ 设计必须**以 ① 为默认假设**（自带 journal + restore），把 ② 当加分项，把 ③ 当必须防的坑（写前检测撤销栈深度/禁止无关 `Undo` 调用）。 |
| 16 | 逐单元顺手改的代价：参考实现"逐字符清空格"让表格阶段 **1.6 s→126.9 s（+79×）** | **成立** | CAD 上"逐实体逐属性顺手改"同理（甚至更糟）。⇒ 批量写必须"整块 + 少实体"。 |
| 17 | `SendCommand`/原生命令不可滥用（WPS 侧对应的是"不要绕过 skill 裸写 COM"：`SKILL.md` 铁律 0 + Excel"合法逃生通道"只允许**只读探测/纯窗口操作**） | **成立，且 CAD 的诱惑更大** | CAD 的 `SendCommand` 能一行干很多事，但它**不同步、可被打断、会把用户界面搅乱、错误无法捕获**。⇒ 只做白名单 + 前后断言，不做通用透传。 |

### 2.1 必须实测的 6 个关键指标（建议直接变成 task-1/com-probe 的验收清单）

| # | 指标 | 怎么测（最小可判） | 判据 / 决策用途 |
|---|---|---|---|
| **A** | **地板价三兄弟**：冷启动 `CreateObject` / 附着已运行实例 / 常驻热命令 | 同一张测试图，分别测：① `CreateObject("ZWCAD.Application")` 到可用的秒数；② `GetActiveObject`/ROT 附着秒数；③ 热 daemon 下 `status` 的端到端 ms（跑 5 次取中位数）；再测 `powershell -Sta` vs `-Mta` 空转、`[STAThread]` 空 exe | 决定：wrapper 是否必须 `-Mta`（预期是）、daemon 必须常驻（预期是）、以及"每条命令的地板价"预算 |
| **B** | **读侧双路径交叉点** | 造 3 张图（~1k / ~1万 / ~10万 实体），每张比：① COM 枚举全实体+基本属性耗时；② `Export` DXF 耗时 + 本地解析耗时；③ 两者结果**逐字段比对** | 决定：`snapshot` 的自动选路阈值、缓存策略、`-Slow` 语义；预期"小图 COM、大图 DXF" |
| **B0** | **`Count` 到底数什么**（隐藏结构） | 一张含"块参照（内有 5 实体）+ XREF + 2 个布局 + 带属性的块"的图：数 `ModelSpace.Count`、`PaperSpace.Count`、`doc.Blocks.Count`、`doc.Layouts.Count`，并逐类枚举 | 决定读侧对象模型（等价于 WPS 的"表行算段落"根因）；**不做这一步，后面所有读取都会错位** |
| **C** | **写侧原子性与撤销归属** | 同图做 10 条属性写入：① 单属性写入往返 ms；② 是否线性（1/10/100/1000 条曲线）；③ `StartUndoMark`/`EndUndoMark` 是否存在且生效；④ **写入后按 Ctrl+Z 一次会发生什么**（撤掉整批？撤掉用户上一步？撤不动？）；⑤ `Save` 后撤销栈是否清空 | 决定回退机制（journal vs 撤销栈）、`undo` 命令是否开放、铁律措辞 |
| **D** | **显示刷新与打扰** | 1k/1万/10万实体图：`Regen`/`Update`/`ZoomExtents` 各自耗时；能否局部刷新；**执行后用户视口是否变化**（截图前后比） | 决定"一批改动末尾只 Regen 一次"的可行性、`render` 是否必须保存/恢复视图 |
| **E** | **出图三通道** | 单布局：① `Plot.PlotToFile`(PDF) 耗时与是否需要打印机/`pc3`；② `Export` WMF/位图 耗时；③ 窗口截图耗时；三者产物质量对比；**并测 3 个布局批量打印的总耗时与失败隔离** | 决定 `render` 的实现（优先非 Plot 通道）、`plot` 的批处理模型、以及"批量打印"任务的墙钟预期 |
| **F** | **忙与命令态** | 让 CAD 处于：① 有活动命令（拖线中）；② 开着模态对话框；③ 用户正在选择对象。此时从外部发 COM 写入：会立刻拒（`RPC_E_CALL_REJECTED`）还是排队？装了 MessageFilter 后行为？`SendCommand` 是否阻塞返回？ | 决定 `BUSY` 语义、MessageFilter 是否同款有效、`SendCommand` 是否只能白名单化 |

---

## 3. 设计风险：CAD 与流式文本办公文档的本质差异

| # | 本质差异 | 会在哪炸 | 对策（写进 SKILL 铁律） |
|---|---|---|---|
| 1 | **图形 vs 流式文本：没有"序"** | Word 有段落序号（哪怕表格行会多算一段，至少规则确定）；CAD 的实体**没有文档序**，只有 `Handle`/插入顺序/`Z` 序。所有"第 N 个"式寻址都会漂。 | 一律 **`Handle` 寻址 + 多维指纹**；禁止"第 N 个实体"；`snapshot` 返回的索引必须自带 `handle`，agent 不得自行推算 |
| 2 | **坐标系与单位（图形单位 vs 米）** | 文字高度、标注比例、图框尺寸写死必然错：同一份图可能 1 图形单位 = 1 mm 或 1 m；`DIMSCALE`/`LTSCALE`/`INSUNITS` 决定观感。 | `info` 必须回 `units/insunits/dimscale/ltscale/extents/`常用比例；所有尺寸参数走"图纸毫米 → 模型单位"换算函数；**单位判不出就拒绝写**（`UNITS_UNKNOWN`）而不是猜 |
| 3 | **复杂实体：块参照/动态块/属性/外部参照/标注关联** | ① `ModelSpace` 里看到的"一个块参照"内部有 5 个实体，用户看到的文字其实在 `AttributeReference` 里；② 动态块的可见性/拉伸状态改变几何；③ XREF 内容**不在本图里**，改了也不该改；④ 标注是**关联**的：改几何 → 标注值自己变，改标注文字 → 只是覆盖。 | 分命令处理：`block`（块定义/块参照/属性）与 `entity`（普通实体）分开；`snapshot` 必须报 `xrefs[]` 且在写侧**一律跳过 XREF 并计入 `skipped.xref`**；标注校对读**测量值**而非显示文字（`Measurement` vs `TextOverride`） |
| 4 | **图层/线型/颜色 BYLAYER** | 逐实体改颜色会破坏"随层"约定，让图无法按图层批量维护。 | **默认改图层/样式，不改实体显式属性**；要改颜色先问"是不是该改图层"；`layer` 命令独立 |
| 5 | **大文件** | 全量快照/全图 DXF 在大图上不可接受（WPS 的 7334 段已经是"大"，CAD 的 10 万实体是另一个量级）。 | `snapshot -Scope model\|layout\|bbox\|selection\|layer` + 缓存 + 增量；`audit` 支持"只查当前布局/只查选定范围" |
| 6 | **即时渲染成本 + 打扰用户** | 每次写完 Regen 会卡用户；`ZoomExtents` 会把用户眼前的图挪走。 | 批量末尾一次 Regen；`render` 必须先存视图后恢复；用户交互期间（`CMDACTIVE`）拒渲 |
| 7 | **DWG 私有格式** | 不能像 OOXML 那样解包；DXF 是唯一开放中介但**有损**（动态块/代理对象/自定义对象/部分 XDATA/自定义字典丢失）。 | 读侧用 DXF 但**明确标注近似性**（`lossy:[]`）；**写侧永远走 COM，绝不"改 DXF 再回灌"**（那会毁掉代理对象与图纸结构） |
| 8 | **误删不可逆（无修订层，Undo 组存疑）** | 用户按 Ctrl+Z 可能撤不回，也可能撤掉自己的上一步。 | 写前 journal + `undoToken` + `restore`；**绝不隐式 `save`**；`undo` 仅在 §6.1 C 测通后开放，并保留"只在刚写完且无 `changedOutside` 时用"的护栏（WPS 原文） |
| 9 | **多布局与打印样式表（CTB/STB）** | 同一个颜色在屏幕上是红、出图是黑或粗线；布局各自有页面设置与比例；"图框/会签栏"在布局空间而"图"在模型空间。 | `info` 报 `layouts[]`（纸型/比例/打印机/打印样式表）+ `plotStyles[]`；**凡"出图长什么样"的核对以 PDF 为准**（§1.6 差异 4）；图框类任务显式声明"在布局空间操作" |
| 10 | **透明加密与文件占用** | 本机装了透明加密（Agile DGS）：**落盘即密文**（头 `17DA5FA01633CD9A`），发给同事打不开；CAD 图纸同样中招（本机已有 `decrypt-cad-image` 技能专门处理 dwg/dxf 密文）。文件被别处占用时只能只读。 | 沿用 WPS 铁律 5：**保存后必须提醒"落盘可能是密文"**并给解密路径；`readOnly` 就拒绝写（不绕） |
| 11 | **命令态（CAD 独有的"应用忙"）** | 用户正在拖线/在命令中间，外部写入会被拒或排队；`SendCommand` 在命令中间发会乱。 | 写前断言"无活动命令"；MessageFilter + 语义级忙检测；`SendCommand` 白名单 + 前置/后置断言 |
| 12 | **用户状态（视口/选择集/图层状态）就是工作上下文** | WPS 的"用户状态"只有光标/滚动；CAD 的缩放位置、当前图层、选择集、对象捕捉、孤岛检测…… 都是用户正在用的东西，动了就是打断工作。 | `render`/`audit` 不得保留任何视图/选择集改动（一律 finally 恢复）；不抢前台、不 `SetForegroundWindow`（WPS 已明确禁止截图抢前台，`SKILL.md:380`） |

---

## 4. CAD 版命令面草案（25 条）

**命名约定**：`zwc.ps1 zwcad <命令>`；所有命令支持 `-TimeoutSec`（默认 180，`SendCommand` 类另设）；**响应永远是单段 JSON**；退出码沿用 WPS 分层（1=调用错、2=`ok:false`、3=`check/audit` 门禁）。**`-DryRun` 一律"只读可用"**（WPS 已验证：只读文档也能跑 dry-run，`SKILL.md:127`）。

### 4.1 读（8 条）

| 命令 | 参数 | 返回 JSON 要点 | 为什么要它（对应真实任务） |
|---|---|---|---|
| `doctor` | — | `app{progId,version,build,path,installed,license}`、`running[]`、`comAttach{method,ms}`、`dxfExport{ok,ms}`、`render{wmf,plot,shot}`、`daemon{pipe,hot}`、`processes{zwcad}` | 与 WPS `doctor` 同款。**先回答"这台机器能不能干"**（授权/加密狗/打印驱动/DXF 能力），避免把环境问题当图纸问题查 |
| `status` | — | `instances[]`、`docs[{path,fullName,saved,readOnly,active,layouts,entities,extents}]`、`activeLayout`、`cmdActive`、`undoMarks?` | 铁律第 1 条：**先说清在哪个实例、哪份图纸、有没有未保存改动**；多文档是本机最容易认错对象的场景 |
| `open` | `-Doc <路径> [-Show] [-ReadOnly] [-NoHint]` | `readOnly`、`units/insunits`、`extents`、`layouts[]`、`entitiesByType{}`、`hint{caption,captionSet}`、`multiInstanceWarning?` | 接管入口；**自动打〔AI〕标题**；返回"图纸摘要"让 agent 不用再问一轮 |
| `info` | `-Doc <路径> [-Detail layers\|dim\|text\|blocks\|xrefs\|plots]` | `units/insunits/extmin/extmax/dwgVersion`、`layers[{name,on,frozen,locked,color,linetype}]`、`dimstyles[{name,DIMSCALE,DIMTXT,DIMLFAC}]`、`textstyles[]`、`blocks[{name,count,dynamic,hasAttribs}]`、`xrefs[{name,path,status}]`、`layouts[{name,paper,plotScale,printer,plotStyle}]`、`plotStyles[]`、`counts{}` | **CAD 版最高价值的一条只读命令**：这些信息以前只能靠几十次 COM 枚举或解 DWG。对应 WPS 的 `captions`/`table -Action plan`（一条命令替掉 22 次 read：6.4 s vs 4.5 min）。直接支撑"图框/会签栏""标注批量校对""批量打印" |
| `snapshot` | `-Doc <路径> [-Tag 名] [-Scope model\|layout:<名>\|bbox:x1,y1,x2,y2\|selection] [-Layers <含/排>] [-Include xref,blockdef] [-Out <路径>] [-Slow]` | `readPath:"dxf"\|"com"\|"mgd"`、`counts{}`、`fingerprint{sha,entities,maxHandle,saved}`、`entities[{handle,type,layer,bbox,text?,value?,block?,space}]`（分页）、`lossy[]`、`skipped{blockInner,xref,viewport}`、`ms`、`cacheHit` | `diff`/`audit`/`measure` 的公共底座。**对应 WPS v1.13 的一次 `WordOpenXML` 取全稿**（48.5→6.1 s）。范围化是 CAD 特有必需品（大图） |
| `search` | `-Doc <路径> -Text <正则> [-Layer] [-Type MTEXT,TEXT,ATTRIB,DIMENSION] [-Scope] [-Max 50] [-Case]` | 每命中：`handle/type/layer/space/bbox/text/value`、`counts{}`、`truncated` | **定位优先于翻页**（WPS `search` 的动机原话：定位用这个，别一页页 read）。CAD 用途：找所有"设计洪水位"文字、找所有标注值异常、找某图层的全部图元 |
| `selection` | `-Doc <路径>` | `handles[]`、`types{}`、`bbox`、`summary{count,layerTop}`、`inBlock`/`inViewport` 标记 | 用户说"就改我选的这几个"时的锚点。WPS 同款（`selection` 直接给段号/表号/图号）。**只读，不改选择集** |
| `measure` | `-Doc <路径> (-Layer 名\|-Scope\|-Selection) [-Types LINE,POLYLINE,LWPOLYLINE,HATCH] [-Group Layer] [-Export csv\|xlsx] [-Tolerance]` | `groups[{key,count,length,area,perimeter}]`、`total{}`、`unclosed[]`、`skipped[]`、`exportPath?` | **工程量提取**的唯一入口。要点：面积只算**闭合**图元并单列"未闭合"（否则工程量静默偏小）；单位随 `info.units` 标注 |

### 4.2 写（7 条）

| 命令 | 参数 | 返回 JSON 要点 | 为什么要它（对应真实任务） |
|---|---|---|---|
| `entity` | `-Items <json>`，每条 `{op:add\|update\|delete\|move\|copy, handle?, type?, layer?, expect?, geom{}, props{}}`；`-DryRun`、`-Continue`（失败不中断，逐条隔离） | 每条：`ok/handle/op/before/after/textAfter?`、`applied`、`rolledBack`、`failed[]`、`undoToken`、`needsRegen` | **写侧总原语**。对应 WPS 的 `revise`/`write`：Pass1 全校验 → 写入 → 回读 → 失败整体回滚。改图/画图/批量修图元都走它 |
| `text` | `-Items <json>` 或 `-Find/-Replace [-DryRun] [-Scope]`；`{handle, expect, find, replace}` / `{handle, text, style?, height?}` | 每条 `handle/before/after/readBack`；`hits/skipped{blockInner,xref}` | 文字批量改（说明、标题、索引、会签栏字段）。**照 WPS v1.31 教训：绝不用原生 FIND 反复替换**（WPS 会二次替换成 `P--`）⇒ 内存块替换 + 回读。CAD 同理：不用 `SendCommand` 发 `FIND` |
| `dim` | `-Items <json>` / `-Action add\|update\|audit` / `-Style`、`-Scale`、`-LinearFactor`、`-DryRun` | `handle/type(rotated\|aligned\|angular\|diameter\|radius)/measurement/textOverride/style/scale/before/after`、`changed[]` | **标注批量校对/批改**（真实高频）。要点：区分 `Measurement`（关联实测值）与 `TextOverride`（人为覆盖文字），校对只报前者异常 |
| `layer` | `-Action add\|update\|rename\|delete\|freeze\|thaw\|lock\|unlock` / `-Name(s)`、`-Color`、`-Linetype`、`-Lineweight`、`-Plot`、`-DryRun` | `layers[{name,before,after}]`、`created/renamed/deleted`、`inUse[]`（删除前检查）、`warnings[]` | 图层规范统一（出图前必做）。**删除前必查"图层是否在用"**（在用则拒绝并报告哪些实体） |
| `block` | `-Action insert\|update\|rename\|attrib\|redefine\|list` / `-Name`、`-At x,y`、`-Scale`、`-Rotation`、`-Attribs{tag:value}` | `handle/blockName/at/scale/rotation`、`attribs[{tag,value,before}]`、`redefined[]`、`readBack` | **图框/会签栏/标准符号**任务的主力：套图框 = 插块 + 填属性；换标准 = 重定义块定义（`redefine` 必须 warn"会改变所有参照"） |
| `layout` | `-Action add\|copy\|rename\|pagesetup\|viewport` / `-Layout 名`、`-Paper A3`、`-PlotScale 1:500`、`-Printer`、`-PlotStyle` | `layouts[{name,paper,orientation,plotScale,printer,plotStyle,viewports}]`、`created/renamed`、`pageSetupApplied` | **批量打印/图纸目录/多布局**的地基：页面设置与比例是打印正确性的前提；视口比例错了整张图的标注/文字都不对 |
| `frame`（图框/会签栏） | `-Layout <名\|all>`、`-Block 图框块名`、`-Attribs <json>`、`-Number`/`-Volume`、`-Position auto\|center`、`-DryRun` | 每布局：`layout/blockHandle/attribs[{tag,before,after}]/applied/skipped`、`missingAttribs[]`、`overflow[]` | **把"图框+会签栏"做成一条结构化命令**（不是让 agent 用 20 条 block 拼）。对应 WPS 的 `table -Action plan\|normalize` 那种"一条命令干一类活"的形态（127 次调用 → 7 条命令 ≈44 s） |

### 4.3 核对（5 条）

| 命令 | 参数 | 返回 JSON 要点 | 为什么要它（对应真实任务） |
|---|---|---|---|
| `audit` | `-Doc`、`-Scope`、`-Rules <名单>`、`-Max N`、`-ExitCode` | `issues[{rule,severity(blocker\|major\|minor),handle,layout,layer,bbox,sample,suggest}]`、`summary{}`、`gate{passed,blockers}` | 机械体检（对应 WPS `audit`+`check`）：文字重叠/超出图框/图层不规范/标注比例异常/单位缺失/空图元/未闭合多段线/重复实体/XREF 未绑定/会签栏字段缺失。**`-ExitCode` 可当 CI 门**（照 PPT `check -ExitCode`） |
| `render` | `-Doc`、`-Target extents\|layout:<名>\|bbox\|handle:<h>`、`-Width`、`-Overview`、`-Out`、`-NoRestoreView`（默认恢复视图） | `images[{path,width,height,bytes,inkRatio}]`、`viewBefore/viewAfter`、`restored:true`、`capped?`、`method:"export"\|"plot"\|"shot"` | **人眼可验**的唯一通道。关键差异：默认**保存/恢复用户视口**、默认**只渲目标区域**、支持 `-Overview` 布局联系表（照 WPS 6 页/张、最多 18 页） |
| `diff` | `-Doc`、`-Since <snapshot\|last>`、`-Scope`、`-IncludeGeometry` | `added[{handle,type,layer}]`、`removed[]`、`changed[{handle,before,after,fields}]`、`same`、`lossy[]`、`degraded?` | 与快照比。**人改的和 agent 改的都在里面**（WPS 原文口径），是"这一轮我到底动了什么"的账本，也是 `changedOutside` 的判据 |
| `plot` | `-Doc`、`-Layouts <all\|名,名>`、`-To pdf`、`-Out <目录/文件>`、`-DryRun`、`-Background`? | `sheets[{layout,page,path,ok,ms}]`、`total/okCount/failedCount`、`pageCount`、`expectedPages`、`warnings[]` | **批量打印**这类任务本身就是"一条命令"：逐布局隔离（单张失败不影响其它，照 normalize 逐表隔离）、`-DryRun` 先出将打印清单。**这是 CAD 版最长的单命令，必须有进度与失败隔离** |
| `export` | `-Doc`、`-Format dxf\|dwg副本\|png\|wmf\|csv`、`-Out`、`-Selection?` | `path`、`bytes`、`format`、`encryptedLikely?`、`note` | 交付导出（送审 DXF、出图片、出统计表）。**不改当前文档身份**（`SaveAs` 会换身份，WPS 已踩），且**必须提醒落盘可能是密文** |

### 4.4 会话（5 条）

| 命令 | 参数 | 返回 JSON 要点 | 为什么要它（对应真实任务） |
|---|---|---|---|
| `serve` | `-Action start\|status\|stop\|reset` | `action/running/pipe/killed?` | 常驻 helper 控制（WPS 同款）。**`reset` 是"挂死后唯一的恢复通道"**（v1.34 才补，之前 catches 到就永久挂） |
| `hint` | `-Doc`、`-Text "正在做什么"`、`-HintTag`、`-Toast`、`-Tray`、`-Quiet`、`-Clear` | `caption/captionSet`、`toast`、`tray{state,note}`、`cleared{}` | **接管可见**（用户原话需求）。四通道里 CAD 版**明确不做"图上标记"**（见不迁移清单）；托盘图标 + 标题前缀 + 气泡足够 |
| `restore`（兼 `undo`） | `-Token <undoToken>`、`-Doc`、`-DryRun`；`undo -Count N`（仅实测安全后开放） | `restored{entities,deleted,recreated}`、`verify{ok,issues[]}`、`undoAvailable` | **CAD 版回退的唯一可靠手段**（journal 回滚）。`undo` 走文档撤销栈属"待实测后开放"，且遵守"只在刚写完且无 `changedOutside` 时用" |
| `save` | `-Doc`、`-As <路径>`、`-DeferUndoCheck` | `saved`、`path`、`encryptedLikely`、`undoStackCleared`、`processes{}` | 落盘。默认**不 save**（铁律 6），save 前必须"用户看过 或 render 核对通过"；**必须提示密文风险** |
| `close` / `finish` | `-Doc`、`-Save`；`finish -Note` | `processes{zwcad}`、`documentsLeft`、`hintCleared{}` | 收尾：关本次明确那一份 + 还原标题/托盘 + **汇报进程数**（WPS 铁律 4：绝不按映像名批量杀进程，只清自己起的无窗口自动化实例） |

### 4.5 两条完整 JSON 协议示例

**（1）写侧：批量改标注（成功，带回读与 undoToken）**

请求（`entity`/`dim` 同一信封，这里用 `dim`，走 `-Items` 文件）：

```json
{
  "cmd": "dim",
  "doc": "<图纸目录>\\某闸\\闸室结构图.dwg",
  "action": "update",
  "layout": "model",
  "dryRun": false,
  "items": [
    {
      "handle": "2A7F",
      "expect": { "type": "AcDbRotatedDimension", "layer": "DIM-主体", "measurement": 3.2, "textOverride": null },
      "set": { "dimlfac": 1000, "textOverride": "3200", "dimstyle": "ZHU-TI-1:50" }
    },
    {
      "handle": "2A81",
      "expect": { "type": "AcDbRotatedDimension", "layer": "DIM-主体", "measurement": 3.2, "textOverride": null },
      "set": { "dimlfac": 1000, "textOverride": "3200" }
    }
  ]
}
```

成功响应：

```json
{
  "ok": true,
  "cmd": "dim",
  "doc": {
    "path": "<图纸目录>\\某闸\\闸室结构图.dwg",
    "fullName": "<图纸目录>\\某闸\\闸室结构图.dwg",
    "saved": false,
    "readOnly": false,
    "activeLayout": "模型",
    "units": { "insunits": 4, "insunitsName": "毫米", "dimscale": 50, "ltscale": 25 },
    "fingerprint": { "entities": 18422, "maxHandle": "4F1C", "savedAt": "2026-11-04T08:52:10+08:00" }
  },
  "undoToken": "u-20261104-093512-3f2a",
  "data": {
    "action": "update",
    "applied": 2,
    "rolledBack": 0,
    "items": [
      { "i": 1, "handle": "2A7F", "ok": true,
        "before": { "measurement": 3.2, "textOverride": null, "dimstyle": "STANDARD", "dimlfac": 1 },
        "after":  { "measurement": 3.2, "textOverride": "3200", "dimstyle": "ZHU-TI-1:50", "dimlfac": 1000 },
        "readBack": { "textOverride": "3200", "dimlfac": 1000, "visible": true } },
      { "i": 2, "handle": "2A81", "ok": true,
        "before": { "measurement": 3.2, "textOverride": null, "dimstyle": "STANDARD", "dimlfac": 1 },
        "after":  { "measurement": 3.2, "textOverride": "3200", "dimlfac": 1000 },
        "readBack": { "textOverride": "3200", "dimlfac": 1000, "visible": true } }
    ],
    "skipped": { "xref": 0, "lockedLayer": 0, "frozenLayer": 1 },
    "needsRegen": true
  },
  "warnings": [
    { "code": "DIM_LFAC_CHANGED", "message": "本批修改了 DIMLFAC（测量值换算系数）但没有改 DIMSTYLE，图内同类标注可能显示不一致", "handles": ["2A7F", "2A81"] },
    { "code": "NOT_SAVED", "message": "改动只在内存里；本 skill 绝不隐式 save（撤销/回滚依赖 undoToken）" }
  ],
  "elapsedMs": 812,
  "daemon": { "pipe": "zwc-0a1b2c3d4e5f", "hot": true, "pid": 20416 }
}
```

**（2）写侧失败（Pass 1 校验不过 → 一条都不改；含 `restore` 用法）**

```json
{
  "ok": false,
  "cmd": "entity",
  "doc": { "path": "<图纸目录>\\某闸\\闸室结构图.dwg", "saved": false, "readOnly": false, "activeLayout": "模型" },
  "error": {
    "code": "ANCHOR_MISSING",
    "message": "第 2 条 item 的锚点不成立（handle 2A81 的 measurement 实际为 3.15，期望 3.2）。整批未做任何修改。",
    "hint": "先跑 `snapshot -Scope bbox:...` 或 `search -Text` 重取 handle 与指纹，再按新指纹重发；不要放宽 expect 硬改。",
    "detail": {
      "i": 2,
      "handle": "2A81",
      "failures": [
        { "field": "measurement", "expect": 3.2, "actual": 3.15 },
        { "field": "layer", "expect": "DIM-主体", "actual": "DIM-主体" }
      ],
      "fingerprintMatch": false,
      "relocatable": false
    }
  },
  "undoToken": null,
  "applied": 0,
  "rolledBack": 0,
  "elapsedMs": 611
}
```

失败后若要撤回**已经发生过**的上一批（用上一批返回的 token）：

```json
{ "cmd": "restore", "doc": "<图纸目录>\\某闸\\闸室结构图.dwg", "token": "u-20261104-093512-3f2a", "dryRun": false }
```

```json
{
  "ok": true,
  "cmd": "restore",
  "data": {
    "token": "u-20261104-093512-3f2a",
    "restored": { "modified": 2, "recreated": 0, "deleted": 0 },
    "verify": { "ok": true, "issues": [] },
    "notes": ["按 journal 写回前值（不走文档撤销栈，撤销归属不受影响）"]
  },
  "elapsedMs": 903
}
```

**信封字段约定（三端共用，照 WPS 的契约再加 CAD 必需项）**

| 字段 | 说明 | WPS 对应 |
|---|---|---|
| `ok` | 顶层布尔；**判据只看顶层**（WPS 踩过"正文里出现字面 `\"ok\": false` 被误判"，`woc-app-common.ps1:32-50`） | 同 |
| `cmd` | 命令名（回显） | 同 |
| `doc` | `path/fullName/saved/readOnly/activeLayout/units/fingerprint` | WPS 只有 path/saved/readOnly；**CAD 必须带 units + fingerprint**（否则 agent 会拿错比例写尺寸） |
| `data` | 命令结果（形状随命令） | WPS 用 `result`（可继续叫 `result`，本文用 `data` 只是与既有文档区分，**实现时统一成 `result` 更好**） |
| `warnings[]` | **不阻断**但会影响用户观感的提示（如"改了 DIMLFAC 没改样式"） | 同 |
| `error{code,message,hint,detail}` | `code` 稳定可判（`ANCHOR_MISSING/READONLY/BUSY/TIMEOUT/UNITS_UNKNOWN/CMD_ACTIVE/XREF_SKIPPED/PLOT_FAILED/COM_ERROR`）；`hint` 是**下一步怎么办**（WPS 的故障排查表本质就是把这个字段文档化） | 同 |
| `elapsedMs` | 命令自身耗时（不含进程启动） | WPS 在 `batch` 里逐条带 `ms` |
| `undoToken` | 本批的可回滚句柄（`null` = 无可回滚内容） | **CAD 新增**（WPS 靠修订/快照，不需要） |
| `daemon{pipe,hot,pid}` | 传输可观测性（复现"到底走没走常驻"） | 同（`textsPath`/`path` 字段的思路） |

---

## 5. 不迁移清单（明确不该照搬的 WPS 做法）

| # | WPS 做法 | 为什么在 CAD 上不成立 / 不该照搬 | CAD 的替代方案 |
|---|---|---|---|
| 1 | **Word 修订模式（track changes）作为"天然可回退"层**——`revise` 全部走修订，用户逐条接受/拒绝，**save 也不影响** | CAD **没有修订层**。DWG 虽有历史/版本功能，但**不经 COM 暴露**、且不是逐笔修订。照抄会得到"改完无法反悔" | **写前 journal（handle + 前值/克隆）+ `undoToken` + `restore`**；默认不 save；`undo`（文档撤销栈）只在 §6.1 C 实测安全后开放 |
| 2 | **批注（`comment`）作为"人机沟通层"**——按 `para` 锚定，只加批注不改正文 | CAD 的"批注形态"是**图元**（`MLeader`/`REVCLOUD`/文字），会**进图、进打印、进他人参照、被算进工程量**；没有"不进文件的批注" | **不做图内批注**。改"旁挂报告"：`audit`/`diff` 输出 JSON/Markdown 报告 + `render` 出的图（图上不落标记），由人对照 handle 定位 |
| 3 | **段号寻址（`para` + `expect` 文本）** | 图形**无文档序**；块内实体/XREF/布局视口/属性让"数到的"和"看到的"不是一回事 | **`Handle` + 多维指纹**；`relocated:true` 允许指纹重定位；禁止"第 N 个实体" |
| 4 | **"表行也算一个段落"式隐藏结构的处理方式**（先按块模型读、发现对不上再整体换 XML 路径） | 那是 WPS 特有的计数怪癖；CAD 的隐藏结构完全不同（块内部/视口/XREF/ATTRIB）。**照抄会做无用功** | 保留**方法**（先实测 Count 语义 → 再定对象模型 → 双路径 A/B），**丢掉结论**。§6.1 B0 就是这一步 |
| 5 | **PPT 式的"逐页渲染核对"** | CAD 一"页"= 一个布局，出图是**秒级/页**（Plot），且渲会动用户视图；PPT 一页渲染是百毫秒级且不动窗口 | **改区渲 + 联系表 + 落盘前一次整图核对**；"逐布局全渲"只在批量打印任务里做（那本来就是交付物） |
| 6 | **`Excel` 的 `restore -Cells` 选择性回滚**（按格对比快照恢复） | CAD 没有"格"这种可枚举的可写单元；快照是近似（DXF 有损），照抄会造成"回滚回一个近似值" | **回滚只走 journal 前值**（精确、按 handle）；快照只用于 `diff` 与审计，**不用于回滚** |
| 7 | **`render` 走系统剪贴板（Excel/PPT `CopyPicture`/`Slide.Copy`）** | 剪贴板里放 OLE/图元格式读回来时会**在自己 STA 上重入 CAD**（WPS 已踩：`Slide.Duplicate` 留在剪贴板的 OLE 格式一读就带崩 helper） | **优先 `Export`/`Plot` 直接落盘**，**不走剪贴板**；若必须走（截图），复用 `ClipboardKeeper` 的"只接管简单格式"策略 |
| 8 | **状态栏进度（`hint -Bar`）** | WPS 的诚实结论是"**Writer 的状态栏只写不读且界面不渲染**"；CAD 的状态栏/命令行更不确定，且命令行回显会污染用户命令历史 | **只用标题前缀 + 托盘 + 气泡**；`statusBar` 字段可有，但**不承诺可见**（照 WPS：`statusBar:true` 只代表写入被接受） |
| 9 | **PPT 式的"图上/页上可见标记"**（`-SlideMark` 逐页药丸） | 那是"内容"，WPS 已经要处理"用户 Ctrl+S 把标记存进文件"的修复链；CAD 里画进图元的标记**会进打印、进工程量、进参照**，污染面不可接受 | **CAD 版不做图上标记**（照"悬浮窗永久否决"的同类决策，直接写死不做，别做成"可选模式"偷偷加回来） |
| 10 | **"一次取全永远赢"的读侧结论**（WordOpenXML 0.16 s 取代逐段读） | CAD 的"一次取全"是导出 DXF，**大图秒级到十几秒**，小图不如直接枚举 | 保留**双路径 + 自动选路 + `-Slow` + 逐字段 A/B** 这套方法，丢掉"永远走全量"的结论；交叉点必须实测（§6.1 B） |
| 11 | **`SaveAs` 做备份 / 换身份** | CAD 的 `SaveAs` 会把活动文档换成新文件（WPS 已踩"SaveAs 换身份"）；`Save` 会动用户文件并**可能清撤销栈** | **不 SaveAs 做备份**；备份=逻辑 journal 或 `Export` DXF 到临时目录（不改身份）；落盘只在用户认可后 |
| 12 | **`900 ms` 抢不到管道就静默另起 helper** | WPS 实测这会分裂出两个 helper 同时挂同一实例（`helper_max=2`）；CAD 上两个 helper 各自 UndoMark/Regen/改选择集 = 破坏用户命令栈 | **不静默分裂**：要么等管道（有界超时），要么先抢**文档级命名互斥锁**；一次性模式也要抢锁 |
| 13 | **"多 WPS 实例池"**（WPS 研究已否决） | 同理否决，且 CAD 实例更重（每个实例几秒启动 + 授权 + 显存） | 单实例 + 串行 + 并行只在"规划侧"（不同步程，`DESIGN-parallel-tasks.md` 的结论：**并行的是规划，写只有一个写者**） |
| 14 | **把 `SendCommand`/原生命令当万能逃生通道**（WPS 的"合法逃生通道"只开放只读探测） | CAD 的 `SendCommand` 看起来能一行干完所有事，但它**不同步、可被打断、错误不可捕获、会搅乱用户界面** | 白名单化 + 前后断言；写操作**只用 API**，不用 `SendCommand` |

---

## 6. 附录

### 6.1 待回填 / 待验证（交给 com-probe 与 api-research）

| 编号 | 待验证 | 谁 | 影响 |
|---|---|---|---|
| 1 | `ZWCAD.Application` 的 COM 对象模型：`Documents/Document/ModelSpace/PaperSpace/Layouts/Blocks/Layers/DimStyles/SelectionSets/Utility/Plot` 是否齐、名称是否与 AutoCAD 一致 | com-probe（task-1 第 3 问） | 决定 §4 全部命令能否落地 |
| 2 | `StartUndoMark`/`EndUndoMark`/`Undo` 是否存在且生效；COM 改动是否进撤销栈（§6.1 C） | com-probe | 决定回退机制与 `undo` 是否开放 |
| 3 | `Export("DXF"/"WMF"/"BMP")` 的格式覆盖与耗时；**能否导出未保存的内存状态** | com-probe | 决定 `snapshot`/`render` 实现 |
| 4 | `Plot.PlotToFile` 是否可静默出 PDF、是否需要 `pc3`/打印机、批量耗时 | com-probe | 决定 `plot`/`render` 的成本模型 |
| 5 | `SendCommand` 的同步语义与结果回传方式（LISP 写文件？全局变量？） | com-probe（task-1 第 4 问） | 决定是否根本不使用 `SendCommand` |
| 6 | ZWCAD v16 是否带 `ZwSoft.ZwCAD.*` 托管 .NET API（进程内遍历可省掉全部跨进程往返） | api-research（task-2 第 1 问） | 若有 ⇒ 读侧最优解改为进程内 MGD，DXF 只做旁路校验 |
| 7 | 打印样式表（CTB/STB）在 COM 侧的可读性与设置方式 | api-research + com-probe | 决定"出图核对"是否可信 |
| 8 | 窗口标题/标签在 CAD 里哪个句柄可见（`SetWindowText` 目标）、托盘/气泡是否照 WPS 方式可用 | com-probe | 决定 §1.7 实现 |

### 6.2 WPS 仓库证据索引（只读引用，按需复查）

| 主题 | 位置 |
|---|---|
| 入口与 `-Mta` 理由 | `scripts/woc.cmd:1-36`、`scripts/woc.ps1:1-62`、`reference/changelog.md:86`（v1.15 的 STA 2.2 s 实测） |
| daemon 协议/生命周期 | `scripts/helper/shared/PipeServer.cs:1-169`、`scripts/woc-app-common.ps1:78-186` |
| 源码哈希缓存 + batch | `scripts/woc-common.ps1:1-148`、`reference/changelog.md:78`（v1.19） |
| 忙重试 MessageFilter | `scripts/helper/shared/MessageFilter.cs:1-103`、`reference/changelog.md:27`（v1.32，含"未实弹"的诚实记录） |
| 跨进程锁/剪贴板 | `scripts/helper/shared/WocLocks.cs:1-53`、`reference/changelog.md:74`（v1.21） |
| 会话状态/changedOutside | `scripts/helper/word/State.cs:30-166` |
| 读侧一次取全（WordOpenXML） | `reference/changelog.md:90`（v1.13 全线数字） |
| 写侧原子批（Word） | `DESIGN-word.md:61-75`、`SKILL.md:114,117` |
| 写侧原子批（Excel） | `SKILL.md:415-430` |
| 逐表隔离 + 回读（最成熟形态） | `DESIGN-table-normalize.md`（全文；§7 事故复盘）、`reference/changelog.md:19-24`（v1.33） |
| render 三档 + 视觉纪律 | `word-quirks.md:17,60`、`SKILL.md:119,376-383`、`SKILL.md:240`（同一页看 7 次 ≈8.4 min） |
| 接管提示四通道 + 悬浮窗否决 | `SKILL.md:48,64-74`、`reference/workflows.md:387-407`、`word-quirks.md:31-32` |
| 并行研究全套实测 | `DESIGN-parallel-tasks.md`（附录 A 的 16 条实测） |
| 上下文/步数归因 | `SKILL.md:230-243`（127 次调用 / 88.6 min / 90% 等模型 / TTFT 4.6→60 s） |
| 优化记录（v1.34 工程质量版） | `OPTIMIZATION-PLAN.md:189-244` |
| `tools/` | **当前为空**（历史上放过 `build_guide.py`，v1.34 已删，见 `OPTIMIZATION-PLAN.md:213`） |

### 6.3 三条写给 Lead 的"收口提醒"

1. **不要在 SKILL.md 里承诺"每页渲染核对"**。CAD 的出图成本是 WPS 的 100–1000 倍，照抄会造成 SKILL 正文与实际成本模型冲突（WPS 自己已经因为"每步截图"吃了 30% 的步数）。
2. **不要在 SKILL.md 里把撤销写成"Ctrl+Z 可撤"**。WPS 血泪账：`undo` 不分作者，4 次 undo 丢了 1 条批注；Excel/PPT 的 COM 改动根本不进撤销栈。CAD 侧必须等 §6.1 C 的实测结果再定措辞，默认按"撤不回"写。
3. **第一批要能跑起来的最小闭环**（建议 Lead 按这个排期）：`doctor` → `status` → `open`（打标题）→ `info` → `snapshot` → `entity -Items`（含 Pass1/回读/回滚）→ `render -Target bbox` → `restore` → `save`/`close`。**有了这 9 条就能做"改图 + 当场可见 + 可反悔"**，其余命令按真实任务增量加（`frame`/`plot`/`measure` 优先，因为它们对应最高频的图框、批量打印、工程量提取）。
