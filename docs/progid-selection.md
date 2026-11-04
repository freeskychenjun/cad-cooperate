# ProgID 选择：为什么必须按"正在运行的实例"决定（不能硬编码版本）

> 2026-11-04 12:15–12:40，core-cad。起因：Lead 用 A/B 实验证明 **`ZWCAD.Application` 这个无版本 ProgID 会被"最后启动的产品"动态改写**，导致"带 `-App autocad` 却起了中望"、"2025 凭空出现"这类事故。
> 证据 JSON：`tmp\evidence-progid\*`（21 个文件）；坑台账：`tmp\core-cad-notes.md` 附录 3。

## 一、结论

```
显式 -ProgId  >  正在运行的 CAD 进程所对应的产品  >  默认顺序（仅 -Launch 且无实例时）
两个不同产品同时在跑 → 返回 MULTI_PRODUCT，拒绝猜测
```

`doctor` 现在把这条链路的每一步都摊开：`mappedBy`、`unversionedProgIdPointsTo`、`progIds[].runningInstance`、`runningProducts`、`multiProduct`。

## 二、A/B 实验：无版本 ProgID 是"最后启动者改写"

`HKLM\SOFTWARE\Classes\ZWCAD.Application\CLSID` 的实测值：

| 操作序列 | `ZWCAD.Application` → CLSID | 指向产品 |
|---|---|---|
| 先启动 ZWCAD 2025 | `{2F671EA1-669F-11E7-91B7-BC5FF42AC839}` | **ZWCAD 2025** |
| 随后启动 ZWCAD Classic 16 | `{FB081ED6-0D7E-489F-A6B7-F7CEAEC10661}` | **ZWCAD Classic 16** |

两个产品**启动时都会注册同一个无版本 ProgID** ⇒ 它表示"最后一个启动的产品"，不是"用户正在看的产品"。版本化的 ProgID 则稳定：

| 版本化 ProgID | CLSID | LocalServer32（实测） |
|---|---|---|
| `ZWCAD.Application.16` | `{FB081ED6-…}` | `…\ZWCAD Classic Chs\ZWCAD.EXE` |
| `ZWCAD.Application.2025` | `{2F671EB4-…}` | `C:\Program Files\ZWSOFT\ZWCAD 2025\ZWCAD.exe /Automation` |

## 三、为什么"硬编码 `.16` 优先"也是错的

- 用户只开了 **2025**（没开 Classic）：硬编码 `.16` ⇒ `CreateObject` 会**另起一个 Classic（约 14 s）**，而不是附着用户眼前的 2025 —— 这是"实时协作"最不能接受的错（用户会看到凭空多开一个 CAD）。
- 用户只开了 **Classic**，而 `ZWCAD.Application` 恰被 2025 改写过：走无版本 ProgID 会去启动/附着 2025 ⇒ 同样错。

⇒ 唯一正确的依据是**正在运行的进程**（用户眼前的那个），而不是注册表里的某个键，也不是版本号排序。

## 四、现行实现（helper `Cad.ResolveProgIds` / `AttachWithChoice`）

1. **显式 `-ProgId`**：完全按它走（最高优先级）。若检测到另一个产品也在跑，只加 `PRODUCT_MISMATCH` warning，不改变行为。
2. **检测运行实例**：`Process.GetProcessesByName("ZWCAD")`（AutoCAD 侧 `acad`）→ 取 `MainModule.FileName`，失败回退 **WMI `Win32_Process.ExecutablePath`**，再回退 `MainWindowTitle`；按路径/标题归类为 `ZWCAD Classic 16` / `ZWCAD 2025` / `AutoCAD 2007`。
3. **单产品在跑** ⇒ 候选顺序 = 该产品的版本化 ProgID → 无版本 ProgID → 其它产品的 ProgID；`mappedBy:"process"`。
4. **两个不同产品在跑** ⇒ `MULTI_PRODUCT`（`ok:false`, exit 2），`detail` 列出 `[{product,pid,exePath,suggestedProgId}]`，hint 让调用方用 `-ProgId` 指定；**不猜**。
5. **没有实例在跑** ⇒ 仅在 `-Launch` 时用文档默认顺序（`.16` → 无版本 → `.2025`），并给 `LAUNCH_DEFAULT_PRODUCT` warning 说明"无版本键可能被改写"。
6. 附着成功后读 `app.FullName` **复核产品**；与检测结论不符时报 `PRODUCT_MISMATCH`。
7. `open -Launch` 会把**每个**本工具启动的实例（hwnd/pid/progId）记进 state 的 `session.instances[]`；`finish` 据此判定 `ours`，所以**同一会话里启动的 Classic + 2025 都能各自干净退出**（S3 实测两个 `ours:true`、最终 0 残留）。

`doctor` 新增/变更字段：

```json
{"mappedBy":"process|explicit|default",
 "multiProduct":false,
 "runningProducts":["ZWCAD 2025","ZWCAD Classic 16"],
 "unversionedProgIdPointsTo":{"progId":"ZWCAD.Application","registered":true,
    "clsid":"2f671ea1-…","product":"ZWCAD 2025"},
 "progIds":[{"product":"ZWCAD Classic 16","progId":"ZWCAD.Application.16",
             "registered":true,"clsid":"fb081ed6-…","runningInstance":null}, …]}
```
`app{}`（所有命令）也新增 `product` 与 `mappedBy`，排障时一眼能看出"这次到底驱动了谁"。

## 五、三场景验收（真机，证据在 `tmp\evidence-progid\`）

| 场景 | 操作 | 结果 |
|---|---|---|
| **只开 2025** | `open -Launch -New -ProgId ZWCAD.Application.2025`（16.9 s / helper 12.7 s），随后**不带** `-ProgId` 跑 `status` / `doctor` | `status` → `app.product:"ZWCAD 2025"`, `pid:48856`, `mappedBy:"process"`，**没有另起 Classic**；`doctor.unversionedProgIdPointsTo → ZWCAD 2025`；`finish -Quit` → `ours:true`, 0 残留（`s1-*.json`） |
| **只开 Classic** | `open -Launch -New -ProgId ZWCAD.Application.16`，随后**不带** `-ProgId` 跑 `status` / `doctor` | `app.product:"ZWCAD Classic 16"`, `pid:43780`, `mappedBy:"process"`；`doctor.unversionedProgIdPointsTo → ZWCAD Classic 16`；`finish` 0 残留（`s2-*.json`） |
| **两个同时开** | 先后启动 Classic(pid 48812) 与 2025(pid 32232)，**不带** `-ProgId` 跑 `status` | **`ok:false / MULTI_PRODUCT`（exit 2）**，detail 列出两个产品的 pid/exe/suggestedProgId；带 `-ProgId .16` → Classic、带 `-ProgId .2025` → 2025（各带 `PRODUCT_MISMATCH` 提示另一产品也在跑）；`finish` 两次分别 `ours:true`，最终 0 残留（`s3-*.json`） |

## 六、回归（本轮最终 build）

| 项 | 结果 |
|---|---|
| `run_m1.ps1 -DryRunOnly` | **PASS 9 / FAIL 0 / SKIP 2** |
| ZWCAD Classic 真画（**不带** `-ProgId`，靠进程检测选中 Classic） | section **67 实体 / verify.ok / 533 ms**、textStyle.cjk=true、linetype=zwcadiso.lin；read 67 → **268 PASS**；undo `removed:67, after:0`；finish 0 残留 |
| AutoCAD 2007 真画 | open 冷启动 138.2 s（helper 134.0 s，`mappedBy:"default"`）；section 67 / verify.ok / **491 ms**（gbenor.shx+gbcbig.shx、acadiso.lin）；read 67 → **268 PASS**；undo 67→0；finish 0 残留 |

## 七、2025 与 Classic 16 的其它差异（追加记录）

| 项 | ZWCAD Classic 16 | ZWCAD 2025 |
|---|---|---|
| `Get/SetLispVariable` | **可用**（标量） | **`DISP_E_UNKNOWNNAME 0x80020006`（无此成员）** |
| `EvaluateLispExpression` | 存在但 `E_NOTIMPL 0x80004001`（空壳） | 同样是 `DISP_E_UNKNOWNNAME` |
| 读侧 | 默认 COM；可用 LISP 句柄快路径 | 自动降级 COM + `LISP_BRIDGE_UNAVAILABLE` warning（实测 268 PASS） |
| `section` 67 实体耗时 | **442–533 ms** | **≈2150 ms**（Lead 实测，慢约 4–5 倍） |
| COM 冷启动 | ≈12–18 s | ≈16.7 s |
| 版本化 ProgID | `ZWCAD.Application.16` | `ZWCAD.Application.2025` |

> 结论：2025 的 API 面比 Classic 更"窄"（LISP 变量桥整体消失），但本工具的晚绑定通道足够，`section/read/undo/finish` 全部可用；`read` 的 LISP 快路径在 2025 上不可用，大图只能慢速 COM（M4 若要 2025 大图，需另找通道）。

## 八、复跑方法

```powershell
cd <repo>
# 场景 1：只开 2025，然后不带 -ProgId 验证附着对象
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 open -Launch -New -ProgId ZWCAD.Application.2025 -TimeoutSec 200
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 status      # app.product 应为 ZWCAD 2025
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 doctor      # 看 mappedBy / unversionedProgIdPointsTo
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 finish -Quit

# 场景 3：两个都开 → 应返回 MULTI_PRODUCT（exit 2）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 open -Launch -New -ProgId ZWCAD.Application.16 -TimeoutSec 200
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 open -Launch -New -ProgId ZWCAD.Application.2025 -TimeoutSec 200
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 status          # MULTI_PRODUCT
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 status -ProgId ZWCAD.Application.2025
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 finish -Quit -ProgId ZWCAD.Application.2025
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\zwc.ps1 finish -Quit -ProgId ZWCAD.Application.16
```

## 九、建议（写给 SKILL/后续版本）

1. **文档里永远教用户 pin 版本化 ProgID**（`ZWCAD.Application.2025` / `.16`、`AutoCAD.Application.17`）：无版本键不可依赖，这一点要在 SKILL 的"陷阱"清单里显式写明。
2. 用户同时开两个版本时，SKILL 应主动提示"检测到两个产品，请用 `-ProgId` 指定"，而不是静默选一个。
3. AutoCAD 侧目前只有 2007（未观察到无版本键被改写），M4 装多版本时**沿用同一套进程检测**即可，不需要新机制。
