# tests\audit — 图纸体检（audit）验收与自测

> 对 `scripts\audit\audit.py`（只读规则引擎）与 `scripts\audit\run.ps1`（串联 + 门禁）的离线回归 + 真机验证。
> 规则判据/阈值/误报边界见 `docs\audit-rules.md`。

## 目录契约

| 文件 | 作用 |
|---|---|
| `make_fixtures.py` | 生成 `fixtures\problem-rich.dxf`（45 实体，每条规则至少一个实例）+ 三套 JSON 夹具（与 `cad.ps1 info/read` schema 同构，离线确定性） |
| `make_clean_dxf.py` | 生成「零问题」干净图 `fixtures\clean.dxf`（14 实体） |
| `fixtures\*.{info,read}.json` | 离线夹具：`problem-rich` / `clean` / `strict`（图层命名、未知图层、缺失文字样式） |
| `fixtures\expected-hits.json` | 回归契约：每个规则最少命中数、必须 `not_measured` 的规则、最少 blocker 数 |
| `test_audit.py` | **98 项断言**：规则目录、夹具命中、干净图零问题、严格配置、severity 覆盖、`--rules` 过滤、命中上限、无 info 降级、坏输入、报告渲染、真机快照回归预算 |
| `run-real-validation.ps1` | 真机验证：自起 ZWCAD 2025 独占实例 → 逐张 `open -Doc -ReadOnly` → `run.ps1` → 校验报告里的 issues/blockers → `finish -Quit` 只退自己 pid → 进程归零 |
| `real\*.json` | 真机 info/read 快照（含 624 实体真实水系图、87 实体断面图、45 实体问题夹具、14 实体干净图） |
| `out\*.report.{md,json}` | 验收报告样例（可直接当"示例报告"看） |
| `out\real-validation.log` / `out\real-validation-summary.json` | 最近一次真机验证的控制台日志与机器摘要 |

## 跑法

```powershell
# 离线全量自测（不需要 CAD）
python tests\audit\test_audit.py
python tests\audit\test_audit.py -v            # 逐项打印
python tests\audit\test_audit.py --regen       # 先重生成夹具再测

# 重生成夹具（ezdxf 在 tools\py-libs）
$env:PYTHONPATH='<repo>\tools\py-libs'
python tests\audit\make_fixtures.py ; python tests\audit\make_clean_dxf.py

# 真机验证（会起 CAD，约 3 分钟；自动收尾）
powershell -NoProfile -ExecutionPolicy Bypass -File tests\audit\run-real-validation.ps1
#   可选：-ProgId ZWCAD.Application.2025（默认）/ -App autocad / -RealDrawing <某张真实图.dwg>
```

## 真机验证的 CAD 纪律（踩过坑，务必保留）

1. **`-Launch` ≠ 独占实例**：目标产品已有实例时，`open -Launch` 是**附着**，随后 `finish -Quit` 会关掉**那个实例的所有文档并退出**。
   唯一判据是响应里的 `result.launched:true`。`run-real-validation.ps1` 因此在启动前检查目标产品是否已有进程，有就拒绝运行。
2. **只退自己**：`finish -Quit` 只对 `launched:true` 的实例；别人的实例只用 `info/read/status`。
3. **大图 read 超时**：helper 默认 60 s，≈100 ms/实体（ZWCAD 2025 无 LISP 桥时更慢，624 实体实测 224 s）→ `-TimeoutSec 300`。
4. 本机三款产品共存：显式 `-ProgId`/`-App`，不要依赖默认顺序。

## 判定口径

- 干净图（`clean`）：**0 条**命中（误报预算 0）。
- 真实水系图（`real-map`，624 实体）：**≤5 条**、**0 blocker**（当前 3 条，全部真报，见 docs §9.3）。
- 真实断面图（`section-zwcad16`，87 实体）：**≤3 条**、**0 blocker**（当前 2 条，均为真报：标注压字）。
- 问题夹具（`problem-rich`）：每规则至少命中 1 条，`dim_no_measurement` 必须为 `not_measured`，blocker ≥1。

这些预算写在 `test_audit.py::test_real_snapshots` 里；如果改了规则导致超预算，测试会红，必须先解释是"真报增加"还是"误报回退"。

## 证据快照未随仓库分发

`real/`（真实图纸的 `info`/`read` 回读 JSON）与 `out/`（审计报告）是真机证据快照，含本机路径与真实图纸文字，按脱敏要求**不入库**。需要时在本机跑：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\audit\run-real-validation.ps1 -ProgId ZWCAD.Application.16
```

缺快照时 `test_audit.py` 会把相关用例记为 SKIP（`tests/audit/real/<name>.*.json not present`），不会伪造 PASS。
