# AutoCAD 2021 实机验收（M4：从"只有 2007"到"2007 + 2021 双版本"）

> 2026-11-05，用户新装 AutoCAD 2021 并打开真实图纸 `毛坊湖内插等高线.dwg`（8511 实体）要求验证。
> 环境：AutoCAD 2021 `24.0s (LMS Tech)`（`AutoCAD.Application.24`，`C:\Program Files\Autodesk\AutoCAD 2021\acad.exe`）与 AutoCAD 2007（`AutoCAD.Application.17`）共存；中望 Classic 16 / 2025 亦在本机。
> 结论：**AutoCAD 2021 读/写/撤销/信任门禁/版本自动发现全部通过**；对比记录见 [autocad-2007-acceptance.md](autocad-2007-acceptance.md)。

## 一、验收结论

| 项 | 判据 | 实测 |
|---|---|---|
| A 版本自动发现 | `-App autocad` 不给 `-ProgId` 命中正在运行的版本 | ✅ `mappedBy:"process"`、`progId:"AutoCAD.Application.24"`、`product:"AutoCAD 2021"`（修复前：`NO_INSTANCE`，报 "no running AutoCAD.Application.17 instance"） |
| B 产品标签 | 响应里的 product 与实际产品一致 | ✅ `"AutoCAD 2021"`（修复前一律 `"AutoCAD 2007"`） |
| C 双版本枚举 | `doctor` 列出全部已安装版本 | ✅ `AutoCAD 2021=AutoCAD.Application.24[registered]`、`AutoCAD 2007=AutoCAD.Application.17[registered]` |
| D 读（清单） | 8511 实体 | ✅ `read -Mode handles` 5.6 s / `readPath:"lisp-file"`；`info` 318 ms |
| E 读（几何） | 顶点 + 等高线高程 + 块属性 | ✅ 见 §三；老式多段线顶点经 COM 补齐后与 AutoCAD 原生值逐位一致 |
| F 写 + 一步撤销 | 3 实体 → 回读 → undo 归零 | ✅ 空图 `entity` add 3（line/circle/text）**169 ms**、`undo -Token` `removed:3 countAfter:0 verify.ok:true` |
| G 不落盘 / 零残留 | 用户图 `saved:false` 不变、进程不增 | ✅ 全程未保存；`finish` 后 `ours:false`（用户实例永不动） |
| H 信任门禁 | 未信任时不注入、不弹框、给明确告警 | ✅ `LISP_TRUST_BLOCKED` + 自动降级 COM；强推时看门狗 **1.46 s** 内点掉对话框、无残留弹窗 |

## 二、ProgID / 注册表事实（本机实测）

- `AutoCAD.Application.24` → CLSID `{8B4929F8-076F-4AEC-AFEE-8928747B7AE3}` → `LocalServer32 = C:\Program Files\Autodesk\AutoCAD 2021\acad.exe /Automation`。
- **`AutoCAD.Application`（无版本）仍指向 2007 的 CLSID**（`CurVer=.17`，2007 未卸载）⇒ 无版本键不可依赖；2007 的 `LocalServer32` 只在 **WOW6432Node**，64 位视图读不到，必须双视图探测。
- ProgID 后缀 = ACADVER 去掉尾部 `.0`：2021=`.24`、2022=`.24.1`、2023=`.24.2`、2024=`.24.3`、2025=`.25`；`.24` 覆盖 2021–2024 一族，精确锁年份用三段式。
- 实现：`scripts/helper/ProductRegistry.cs`（注册表枚举 → CLSID → LocalServer32 → 目录名反推 "AutoCAD 2021"），`Cad.KnownProducts/ProductName/ClassifyProcess/ProductFromClsid` 全部改为动态，保留 2007 兜底。

## 三、几何：老式 2D 多段线（这张图的 388 条等高线）

真实图统计：**8123 个高程点块（层 GCD，插入点 z = 高程，块属性 `height` = 注记值）+ 388 条老式 `AcDb2dPolyline` 等高线（DGX 354 / DSX 34）+ 0 个 TEXT**。

| 通道 | 修复前 | 修复后 |
|---|---|---|
| COM `Coordinates` | 按 (x,y) 二元组切 (x,y,z) 三元组 ⇒ 顶点错位（16 个“顶点”里一半是 `[0,x]`/`[y,0]`）、bbox 错 | ✅ 5 顶点等高线回 5 个顶点、坐标与 AutoCAD 原生一致 |
| LISP `entnext` 走 VERTEX 子实体 | `nverts=1`、顶点全 0 | ❌ 进一步实测**不可用**：链会跨 SEQEND 串到下一条多段线（11 顶点 → 99 条 V 记录），且 ≈2 ms/顶点（串线后虚增到 14 万+，耗时 ≈12.5 分钟） |
| 结论 | — | LISP dump 只给 `elevation`（assoc 10 z），顶点由 `LispGeom.FillPolylineVertices` 用 **COM 一次 `Coordinates` 调用**补齐（`polylineComFilled` 回显） |

夹具验收（`tests/bench/fixtures/acad-oldpoly.dxf`，由 `tests/bench/make_acad2021_fixture.py` 生成，含 2 条老式 POLYLINE + 1 条 LWPOLYLINE + 2 个带属性块 + 1 个 TEXT）：

```
readPath=lisp-file-geom  polylineComFilled=2  wall≈0.4–4 s
2F: nverts=5  elev=10    closed=True   (0,0)(40,0)(40,30)…
36: nverts=8  elev=12.5  closed=False
40: nverts=4  elev=7.25  closed=True   (LWPOLYLINE，顶点本来就在 dump 里)
45: height=11.11   48: height=22.22   (块属性)
```

块属性两条通道都通：LISP 走 `entnext` 收 `ATTRIB`（8123 条 `A|` 记录 / 8123 个 INSERT），COM 走 `GetAttributes`（8123/8123 带 `attributes`）。

## 四、SECURELOAD：为什么会出现「安全性 - 未签名的可执行文件」

- 本机 AutoCAD 2021：`SECURELOAD=1`、`TRUSTEDPATHS` 空。
- Autodesk 文档明确 SECURELOAD 覆盖 **LSP/FAS/VLX/MNL**，不是只管二进制 ⇒ `(load "%TEMP%\zwcad-cooperate\geom-*.lsp")` 必然弹窗（按钮：始终加载 / 加载一次 / 不加载）。
- 弹窗是**模态**的：`Documents` 集合返回空、所有 COM 调用挂起、helper 超时 ⇒ 表现像 CAD 死机，用户侧就是"总是弹窗"。
- 处置（`scripts/helper/Trust.cs`）：
  1. **预检**：`SECURELOAD=1` 且临时目录不在 `TRUSTEDPATHS` ⇒ 不注入、降级 COM、告警 `LISP_TRUST_BLOCKED`；
  2. **看门狗**：强推（`-ForceLispInject`）时后台线程 80 ms 轮询该 CAD 进程的 `#32770` 窗口，命中安全框即点「不加载」（实测 1.46 s 内取消）；
  3. **一次性修**：`trust -Action addTemp` 把 `%TEMP%\zwcad-cooperate` 追加进 `TRUSTEDPATHS`（本机已执行，回读 `tempTrusted=true`；`removeTemp` 可还原）；
  4. `LASTPROMPT` 里的「文件加载已取消」被识别为“干净状态”（否则一次取消会把快路径永久判成 `PROMPT_POLLUTED`）。
- **不采用** `SECURELOAD=0`：它是注册表持久值，会全局降级安全策略。

## 五、性能与规模（本机，非承诺）

| 场景（8511 实体真实图） | 数值 |
|---|---|
| `status` / `info` | 81 ms / 318 ms |
| `read -Mode geom`（LISP 清单 + 388 条多段线 COM 补齐） | **19.5 s**、dump **1.49 MB**、JSON 4.75 MB、`polylineComFilled=388`（修复前因串线虚增到 9.2 MB / ≈12.5 分钟） |
| `read -Mode geom` 纯 COM 全扫 8500 实体 | ≈115 s（8123 块属性全读到） |
| `snapshot` / `diff` | ≈41 s（COM 指纹，8511 实体） |
| 夹具 LISP 路径（6 实体） | 0.4–4 s |

## 六、产品自动解析（`-App any`，默认）

问题：同一台机器同时装着中望与 AutoCAD（各自还有多个版本），"打开某个文档"时到底用哪个程序？
`.dwg` 文件本身不记录该用哪个 CAD（Windows 文件关联与 COM 无关），所以工具按**运行实例**判定：

| 场景（2026-11-05 真机实测） | 结果 |
|---|---|
| 只开着 AutoCAD 2021，`status`（不给 `-App`） | ✅ 自动 → `product=AutoCAD 2021, progId=.24, mappedBy=process`（改动前默认 zwcad → `NO_INSTANCE`） |
| `doctor`（不给 `-App`） | ✅ `appRequested:"any"`、`appResolved:"autocad"`、`appAnyDetail={zwcad:[], autocad:[{product:"AutoCAD 2021",pid:6060}]}` |
| `open -Doc <未打开的夹具>`（只开 AutoCAD） | ✅ 在 AutoCAD 里打开（`opened:true`），不启动新程序 |
| 同一个文件再 `open` 一次 | ✅ `opened:false, matchedBy:"fullName"`——不重复开第二份 |
| **两族都在跑**（中望 Classic 16 pid 13780 + AutoCAD 2021 pid 6060），`status` | ✅ `AMBIGUOUS_PRODUCT`，`detail` 列出两族候选与 pid，hint 指明传 `-App` 或先打开目标图 |
| 两族都在跑，`open -Doc <只在 AutoCAD 里开着的夹具>` | ✅ **自动附着"已经开着这份图"的那个实例**（实测选中 AutoCAD 2021，`matchedBy:"cached"`），不报歧义 |
| 两族都在跑，`-App zwcad` | ✅ 强制中望 Classic 16 |
| 收尾 `finish -Quit -App zwcad` | ✅ 只退我们自己起的 pid（`ours:true, processesAfter:0, docsClosed:2`），AutoCAD 用户实例不受影响 |

实现：`Cad.IsAnyKind / DetectRunningBoth / FindRunningDocOwner / ResolveAnyKind / AttachFor`；`Ctx.Kind` 记录实际解析结果，供 `finish` 的进程族计数与 `section` 的线型/字体分支使用。新增错误码 `AMBIGUOUS_PRODUCT`（同族多版本仍报 `MULTI_PRODUCT`）。

## 七、复跑

```powershell
cd <repo>
$env:PYTHONPATH = "<repo>\tools\py-libs"
python tests\bench\make_acad2021_fixture.py          # 生成夹具（R2007 DXF）

powershell -File tests\selftest_lisp.ps1             # 离线 C# 6 套 644 断言
powershell -File tests\run_m1.ps1 -DryRunOnly -Python <完整 python 路径>   # 离线 8/0/4

bin\ZwcClient.exe status -App autocad                # 期望 product=AutoCAD 2021、mappedBy=process
bin\ZwcClient.exe doctor -App autocad                # 期望 progIds 列出 2021 + 2007、lispTrust 有值
bin\ZwcClient.exe trust -Action status -App autocad  # 看 secureLoad/trustedPaths
bin\ZwcClient.exe open -Doc <repo>\tests\bench\fixtures\acad-oldpoly.dxf -NoHint -App autocad
bin\ZwcClient.exe read -Mode geom -Doc acad-oldpoly.dxf -ForceLispFile -LispThreshold 1 -App autocad
# 期望 readPath=lisp-file-geom、polylineComFilled=2、nverts=5/8/4、attributes height=11.11/22.22
```

## 七、已知限制

1. 只验过 **AutoCAD 2021**（另加 2007 历史记录）；2022+ 依赖同一套动态发现，未实机复验。
2. `TRUSTEDPATHS` 是用户级 CAD 配置；本工具只在用户明确执行 `trust -Action addTemp` 时才写。
3. 老式多段线的顶点依赖一次 COM 调用/实体：数千条多段线时仍会慢（数量级远好于 LISP 逐顶点）。
4. `snapshot/diff` 在 AutoCAD 2021 上走 COM 指纹（≈41 s / 8511 实体），大图请给足超时。
