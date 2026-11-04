# M3-E1 性能底座：常驻 daemon + ZwcClient.exe + read 分级/分页/失败降级

> 2026-11-04 14:14–14:55，core-cad（task-13）。目标：把「每命令 4.0 s 地板价」与「624 实体 read 209 s」压到 `status <600 ms` / `read -Mode handles <1.5 s` / `read -Mode geom <10 s`。
> 证据：`tmp\evidence-daemon\*`、`tmp\compare-e1\*`；坑台账：`tmp\core-cad-notes.md` 附录 5。

## 一、结论：三项目标全部达标（远超）

| 目标 | 改前（Lead 实测） | 改后（本轮实测） | 倍数 |
|---|---|---|---|
| `status`（已附着，warm） | **4,033 ms**（6 次 avg：4174/3960/3814/3827/3954/4467） | **129–233 ms**（6 次：233/202/197/184/201/216，avg **206 ms**） | **~20×** |
| `read -Mode handles` 624 实体 | 218,027 ms（audit 快照 / 我的 209 s） | **571 ms**（LISP 拼串 + 客户端；helper 段约 0.3 s） | **~380×** |
| `read -Mode geom` 624 实体 | 218,027–209,000 ms | **1,558 ms** | **~140×** |
| `read -Mode summary` 624 实体（新增档） | （不存在，等价 geom ≈209 s） | **1,807 ms** | — |

> 624 实体所用图纸：`<big-map-dir>\<示例大图>.dwg`（只读打开，624 model-space 实体：SPLINE 306 / MTEXT 128 / CIRCLE 100 / LWPOLYLINE 59 / HATCH 16 / LINE 15）。

## 二、架构：三层，每层都有明确职责

```
zwc.cmd ──► bin\ZwcClient.exe <cmd> [flags]          ~60–100 ms 启动（不再经过 PowerShell）
                ├─ 1) 连命名管道 zwc-<helper源码sha12>（warm daemon）   ← 常态路径
                ├─ 2) 管道不可用 → 自起 ZwcHelper.exe --serve <pipe> → 轮询 ≤20 s
                └─ 3) 仍无应答 → 一次性 ZwcHelper.exe --request/--response（临时文件）
                        ↑ 每一步都有有界超时，客户端永不挂死

scripts\zwc.ps1 <cmd> [flags]   ← 兼容入口（复杂参数构造/错误兜底）+ serve 生命周期 + --build-client
```

- **管道协议**（与 WPS 版同构）：**4 字节小端长度 + UTF-8 JSON**，一请求一连接，严格串行；`NamedPipeServerStream(maxInstances:1)`；管道名 = helper 源码哈希（改源码自动换管道，旧 daemon 自己闲置退出）。
- **daemon 与一次性模式收同一份 request/response JSON**：两者都走 `Program.HandleRequestText()`，只是传输不同（可逐字节 A/B）。
- **会话状态**：daemon 常驻 `[STAThread]` 主线程，COM 对象只在该线程使用；每请求仍按 `FullName`/`Name` 重解析文档并用 `HWND` 校验 app 引用，发现实例换了就重附着。
- **生命周期**：`zwc.ps1 serve -Action start|status|stop|reset`（client 会把 `serve` 转交 zwc.ps1）；闲置 20 min 自灭（`--idle-ms` 可调）；`reset` 只杀**本版本 exe 全路径**的 helper，绝不按映像名。
- **`--build-client`**：client/helper 的 csc 编译缓存仍按源码哈希；`zwc.cmd` 首次运行发现 `bin\ZwcClient.exe` 缺失会自动 bootstrap，失败则回退 PowerShell 入口。

## 三、read 分级 / 分页 / 失败策略

| 档 | 回什么 | 实现 | 624 实体实测 |
|---|---|---|---|
| `handles`（兼容 `-Handles`/`-Fast`） | handle / type / kind / layer / space | Classic：LISP `entnext` 拼串一次取全（`GetLispVariable` 标量）；2025 / AutoCAD 2007：COM 逐实体只读 3 个属性 | 571 ms |
| `summary`（**默认**） | + bbox | 按实体类型读"最便宜的一对属性"算 bbox（LINE→Start/End、CIRCLE→Center/Radius、TEXT→Insertion/Height、LWPOLYLINE→Coordinates、SPLINE→ControlPoints）；**不再调 `GetBoundingBox`**（见 §四） | 1,807 ms |
| `geom` | + 顶点/文字/高度/样式/半径/center | 只读该类型**确实存在**的属性，避免"读不存在的属性" | 1,558 ms |

- 分页：`-Limit N -Cursor X`（`X` 为不透明游标，数字=下一条索引，也接受 handle 跳过），响应给 `returned/total/nextCursor/truncated`；`-Max` 为旧别名。
- 失败策略：
  - **永久错误不重试**：`DISP_E_UNKNOWNNAME(0x80020006)` / `DISP_E_MEMBERNOTFOUND(0x80020003)` / `DISP_E_BADINDEX` / `E_NOINTERFACE` → 第一次就放弃；只有 `RPC_E_CALL_REJECTED` 类"忙"错误才值得重试。
  - 默认 `ReadRetries=1`；`-Strict` 恢复旧的 6×60 ms。
  - 单实体预算 + **全局熔断**：同一属性失败 ≥20 次或总失败 ≥40 → 剩余实体自动降级 handles，并报 `DEGRADED_TO_HANDLES`（HATCH 这类"本来就没有便宜 bbox"的类型只报一次 `BBOX_UNAVAILABLE_FOR_TYPE`，不计入熔断）。
- `-Space`/`-Scope` 支持 `model|paper`（`-Space` 为别名）；624 那个数字是 **ModelSpace** 实体数，paper=0。

## 四、209 s 的三个真实元凶（实测定位，全部修掉）

1. **重试风暴**：`Com.TryGet()` 对**任何**异常都 `6×60 ms` 重试。624 实体 ≈ 335 ms/实体 ≈ 每实体一个"永久失败"属性 × 300 ms 白等 → 209 s。
2. **SPLINE 被当成 LINE**：`KindOf()` 用 `IndexOf("line")` 匹配，"spline" 命中了 "line" ⇒ 306 条 SPLINE 全部去读 LINE 的 `StartPoint`（不存在）→ 触发 1 的惩罚；同时 `GetBoundingBox` 在 ZWCAD Classic 上**所有实体都不实现**，summary/geom 首版因此对每条实体失败。
3. **`GetBoundingBox` 在 Classic 上不可用**：改为按类型读属性算 bbox（SPLINE 用 `ControlPoints`，HATCH 无法算只给 warning），并把"类型本来就不支持"与"读失败"分开计数，熔断不再误触发。
   > 修完后同一张 624 实体图：`geom` 从 218 s → 1.56 s，`bbox` 覆盖 608/624（16 个 HATCH 无便宜 bbox）。

## 五、回归（全绿，均在最终 build 上真跑）

| 项 | 结果 |
|---|---|
| `run_m1.ps1 -DryRunOnly` | **PASS 9 / FAIL 0 / SKIP 2** |
| v1 单序列（30 点 fixture） | `section` 67/67 实体 `verify.ok`；`read -Mode geom` 67/67 → `verify_section.py` **268 PASS / 0 FAIL**；`undo` 67→0 |
| 真数据 overlay（`db-overlay-<站码>`，84 实体） | ZWCAD Classic **84/84 `verify.ok`**（section 客户端 wall 875 ms / helper 808 ms，read 275 ms）；AutoCAD 2007 **84/84 `verify.ok`**（helper 449 ms，read 237 ms）；`undo` 均 84→0；`cross_app_diff` = **ALL MATCH** |
| 进程 | 全部 `finish -Quit` 自己 `launched:true` 的实例；机器上残留的 ZWCAD 是 audit-dev 的 2025（未触碰），daemon 用 `serve -Action stop` 退出 |

## 六、本轮新坑（都已修，详见 notes 附录 5）

1. **daemon 里的 `WaitForPipeDrain()` 会死锁**：客户端只写不读时，daemon 停在 drain 上，`maxInstances:1` 让它拒绝之后所有连接（"stop 了但没停、后面的命令全挂"）。已彻底移除 drain。
2. **一次性回退读子进程 stdout 未排空 = 4 KB 缓冲死锁**：helper 把整段响应打到 stdout 再写文件，client 若 `RedirectStandardOutput=true` 却等 `WaitForExit` → 双方互等。改为 `BeginOutputReadLine/BeginErrorReadLine` 异步排空。
3. `zwc.cmd` 改用 client 后，`serve`/`--build-client` 这类**管理层命令不是 helper 命令**：client 里显式转交 `zwc.ps1`（否则 helper 回 `BAD_ARGS unknown cmd 'serve'`）。
4. 测试脚本坑：PowerShell 里把函数命名成 `Cli` 会撞上 `Clear-Item` 的内置别名（报"找不到路径 …\open"）。
5. `read` 默认档从 geom 变 summary 是**接口变更**：audit-dev 的 E4 基线已改用显式 `-Mode geom`（旧 helper 会忽略未知参数，所以两边都能跑）。

## 七、复现（本机，Classic 16）

```powershell
cd <repo>
# 1) 起 daemon（首次会自动编译 helper/client）
.\bin\ZwcClient.exe serve -Action start
# 2) 目标三项（先开一张图；下面用 624 实体真实图）
$dw='<big-map-dir>\<示例大图>.dwg'
.\bin\ZwcClient.exe open -Launch -New -ProgId ZWCAD.Application.16
.\bin\ZwcClient.exe open -ProgId ZWCAD.Application.16 -Doc $dw -ReadOnly -NoHint
Measure-Command { .\bin\ZwcClient.exe status -ProgId ZWCAD.Application.16 }
Measure-Command { .\bin\ZwcClient.exe read -ProgId ZWCAD.Application.16 -Doc $dw -Mode handles }
Measure-Command { .\bin\ZwcClient.exe read -ProgId ZWCAD.Application.16 -Doc $dw -Mode geom }
# 3) 收尾
.\bin\ZwcClient.exe finish -Quit -ProgId ZWCAD.Application.16
.\bin\ZwcClient.exe serve -Action stop
```
> 兼容入口仍在：`scripts\zwc.ps1 <cmd>`（复杂参数、错误兜底）；M3 之后 agent 默认走 `zwc.cmd` / `bin\ZwcClient.exe`。

## 八、未做 / 后续

1. daemon 未做"跨请求的文档指纹缓存"（当前每请求重解析，代价 ~1–3 ms，可接受）；未做 `changedOutside` 检测。
2. `summary` 的 bbox 对 TEXT 是"插入点 + 高度估算"，不是精确外框（精确外框需 font metrics 或 GetBoundingBox，Classic 不可用）。
3. `handles` 在 2025/AutoCAD 上仍是 COM 逐实体（无 LISP 桥），624 实体实测可接受；超大图（>5k）建议 `-Limit` 分页。
4. client 的 `-RequestFile` 直通已实现但未在回归中覆盖；`serve -Action reset` 只按 exe 全路径杀（安全），未做"孤儿 daemon"自动清理。
5. `zwc.ps1` 仍保留完整一次性路径（作为兜底与复杂参数入口），未删。

## 九、入口对照表与 cold start 稳定性（Lead 复核后的修复，2026-11-04 15:05–15:50）

### 9.1 背景：E1 一度把 `zwc.ps1` 入口拖慢（反向回归）

`zwc.ps1` 仍走"自己起 helper + 文件往返"的旧路径，即使 daemon 是热的，每条命令也要 **5.4–7.8 s**（PS 启动 1.9 s + 旧 wrapper 开销），把 daemon 的收益全吃掉。
**修法**：`zwc.ps1` 现在只做两件事——**参数/兼容性兜底** + **把 helper 命令原样转交 `bin\ZwcClient.exe`**（`serve` / `--build-client` 仍留在 PS 里，client 会把这两个转交回来，避免递归）。转交前先检查"当前源码哈希对应的 helper exe 是否存在"，不存在则走旧路径（编译后执行），所以首次使用仍然自洽。

### 9.2 三个入口实测（同一台机、同一时刻，warm daemon + Classic 16 已附着）

| 入口 | ×5 实测 | 中位 | 备注 |
|---|---|---|---|
| `bin\ZwcClient.exe status` | 110, 108, 102, 90, 134 ms | **108 ms** | 最优入口 |
| `cmd /c scripts\zwc.cmd status` | 135, 131, 218, 110, 600 ms | **135 ms** | cmd→exe，多一次进程创建 |
| `powershell -File scripts\zwc.ps1 status` | 2448, 2462, 2480, 20350*, 2621 ms | **2480 ms** | 纯 PS 启动 ~1.9–2.0 s + client ~0.1 s；*一次 20 s 为机器瞬时负载，非代码问题 |

> **结论：agent 一律用 `zwc.cmd`（或直接 `bin\ZwcClient.exe`）**；`zwc.ps1` 只作为"复杂参数构造 / 兼容入口 / 兜底"，目标 ≤2.5 s 已达成（原 5.4–7.8 s）。

### 9.3 cold start 挂死（P0）与修复

**现象**：无 warm daemon 时，`$x = & bin\ZwcClient.exe doctor ... 2>&1 | Out-String` **永不返回**（>300 s，Lead 复现两次、我复现两次）。
**根因**：client 自起 daemon 时用 `UseShellExecute=false` 创建子进程。Windows 的 `CreateProcess` 会在 `bInheritHandles=TRUE` 时让子进程继承父进程的**全部可继承句柄**（包括调用方的 stdout 管道写端），而 daemon 是长活进程 ⇒ 外层 PowerShell 的管道**永远等不到 EOF** ⇒ 挂死。给子进程单独 `RedirectStandardOutput` 并不能阻止"其它可继承句柄"被继承。
**修复**：daemon 改用 **`UseShellExecute=true` + `ProcessWindowStyle.Hidden`** 启动（ShellExecuteEx 不继承句柄），彻底与调用方的管道链解耦；client 侧保留"PingPipe 就绪探测（≤1 s/次）×30 + 请求本身 30 s 上限"，daemon 半初始化时不会占满请求超时。
**cold start 回归（每次先杀 daemon 再跑，捕获输出形式）**：

| 次数 | 耗时 | 结果 |
|---|---|---|
| 1 | **3,888 ms** | `ok:true / transport:"daemon-started"` |
| 2 | **3,580 ms** | 同 |
| 3 | **4,365 ms** | 同 |

`cmd /c scripts\zwc.cmd doctor`（cold）= **3,721 ms**；`powershell -File scripts\zwc.ps1 doctor`（cold）= **5,729 ms**（PS 1.9 s + cold daemon 3.7 s）——均**正常返回**，不再挂死。

### 9.4 顺带修掉的一个产品 bug（audit-dev 的 W4）

`-Origin x,y` 非 0 时，内部 bbox 自检拿 `GroundBbox`（**已含 origin 偏移**）直接和 W/H 比，必然 `VERIFY_FAILED / MODEL_BBOX_INTERNAL` 并整体回滚。修法：自检改为**先减 origin 再比**。
验证：`section -Origin 50,0` → **67/67 实体、`verify.ok=true`**，undo 干净（`tmp\evidence-daemon\origin-fix-check.json`）。

### 9.5 迁移提示（老脚本必读）

**`read` 的默认档在 E1 从 `geom` 改为 `summary`**：默认响应不再包含顶点/文字/半径。需要几何或文字的老脚本必须显式加 **`-Mode geom`**（`-Handles`/`-Fast` 仍是 handles 的兼容别名；未知参数在旧 helper 上会被忽略，所以同一份脚本对 E1 前后都能跑）。audit-dev 的 E4 基准已按此调整。
### 9.6 `finish -Quit` 走 daemon 变慢（audit-dev 报告 49.7 s）→ 已修

**根因**：daemon 常驻进程在 `finish -Quit` 里仍持有 `cad.App` 的 COM 引用（局部变量在 daemon 堆上活得比请求久），CAD 的退出被拖住；而 finish 的进程轮询默认上限是 45 s，于是几乎必然吃满上限。
**修法**：① `Quit()` 之后显式 `Marshal.ReleaseComObject(cad.App)` + `GC.Collect()/WaitForPendingFinalizers()`，并清空引用；② 进程轮询默认上限 45 s → **25 s**（轮询只是礼貌，不是契约）。
**实测**：`finish -Quit` 经 client+daemon = **6,622 ms**（`ours:true / docsClosed:2 / procsAfter:0 / waitMs:287`），改前 audit-dev 实测 49.7 s。

### 9.7 client 在"有源码无缓存"环境自编译 helper（frozen 快照坑）→ 已修

**现象**（audit-dev 的 `-Freeze` 快照）：`-Freeze` 复制 zwc.ps1/helper 源码/ZwcClient 到输出目录后，client 与 daemon 自行定位 `bin\cache\<sha>\ZwcHelper.exe` 会**间歇**报 `CLIENT_FAILED: helper binary missing`（同一快照下部分任务能跑、部分报缺 exe）。
**修法**：client 新增 `EnsureHelper()`：缓存缺失时按**与 zwc.ps1 相同的源码哈希规则**自调用 `csc` 编译 `scripts\helper\*.cs` 到 `bin\cache\<sha>\ZwcHelper.exe`（找不到 csc 才回退报错）。两个入口现在都能独立 bootstrap。
**实测**：删掉当前哈希的 cache 目录后冷跑 client → **3,765 ms** 返回（`transport:"daemon-started"`，`rebuilt=true`），不再 `CLIENT_FAILED`。
> 提示给基准：全量对比用 live 源码（先跑一次 `zwc.ps1` 预热缓存）最稳；frozen 快照现在是"能自愈"，但哈希必须与快照内源码一致。