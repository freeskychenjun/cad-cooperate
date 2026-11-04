# ZWCAD 自动化 API 全貌与版本差异（供 `zwcad-cooperate` skill 定接口）

> 调研人：api-research（task-2）｜日期：2026-11-04
> 口径：本机 **ZWCAD Classic Chs 16.0.1111（= ZWCAD 2016 Classic，32 位）**，安装目录 `C:\Program Files (x86)\ZWCAD Classic Chs\`。
> 全部「本机」结论来自**只读**读取安装目录 / 注册表 / 帮助文件；未启动任何 CAD 进程（实测由 com-probe 负责）。
> 证据分级：**【实测/本机】** 一手证据 ＞ **【官方文档】** 中望文档/在线帮助 ＞ **【社区】** 第三方 ＞ **【推断】** 未验证。

---

## 0. 结论先行

### 0.1 可行性判断（针对用户三条口径）

| 用户要求 | 判断 | 依据 |
|---|---|---|
| ① 只做「用户开着图纸、agent 当场改」 | **可行**，机制与 WPS 版同源（COM/OLE 附着已运行实例 + 盘上文档） | 【实测/本机】`ZWCAD.Application` 的 `LocalServer32` 直接指向 `ZWCAD.EXE`，即"启动/附着同一个 EXE"；`IIcadDocument.StartUndoMark/EndUndoMark` 提供撤销组 |
| ② 场景＝按数据画图（断面图/工程量图）、图纸校对、改图、信息提取统计 | **场景 A/C/D 覆盖度高；场景 B 覆盖度高但"表格类"与"字段/动态块"是硬缺口** | 见 §2 能力面清单 |
| ③ 要兼容多版本，不绑死某一版 | **COM(晚绑定)＋LISP＋DXF 共同面可覆盖 2016→2026**；但必须放弃 `.NET` 与 `ZRX` 作为主通道 | 见 §3 版本矩阵 |

### 0.2 五条关键结论（可直接拿去做设计决策）

1. **主通道＝COM 晚绑定（IDispatch/`dynamic`/`InvokeMember`），不要用早绑定 interop。** 本机 v16 的 COM 类型库内部接口名是 `IIcadApplication`/`IIcadDocument`…（**ZWCAD 自有前缀 `Icad` + MIDL 的 `I` → 双 I**），与 AutoCAD 的 `IAcad*` 不同名；但**方法名/属性名与 AutoCAD ActiveX 完全对齐**。所以：按名字晚绑定可跨版；按类型引用（`ZwSoft.ZWCAD.Interop*.dll` 早绑定）会绑死版本且 v16 根本没有托管 Interop。
2. **LISP 是第二主通道，而且是有返回值的同步通道。** `IIcadDocument` 暴露 `EvaluateLispExpression(expr) → VARIANT`、`GetLispVariable`/`SetLispVariable`、`SendCommand`；`IIcadApplication` 另有 `Eval`。【实测/本机】这意味着"算 + 画 + 回传结果"可以一次调用完成，不必依赖 `SendCommand` 的异步语义。
3. **LISP 里 `vla-*` 全套可用（vla- 方法名 / vla-get- / vla-put- 由 ZWCAD 类型库自动生成），`vl-*`/`vlax-*`/`vlr-*`/`vl-file-*` 大部分可用**；**坑：获取应用对象的函数叫 `vlax-get-zwcad-object`，不是 AutoCAD 的 `vlax-get-acad-object`**（【官方文档】`LISP_Guide\ActiveX Method.htm` 示例代码）。另有一份官方"**暂时未实现**"清单（见 §2.3），主要是 `vl-doc-*`/`vl-bb-*`/`vl-arx-import`/`vl-exit-*`/`layerstate-*`/`acet-*` 及多数 `vlr-*-reactor`。
4. **本机 v16 没有 .NET 托管 API（明确结论）。**【实测/本机】安装目录（含子目录）无 `ZwDatabaseMgd.dll` / `ZwManaged.dll` / `ZwSoft.ZWCAD.*.dll` 任何一个；`ZRXSDK\lib` 只有 `vc9\ZRX.lib`、`vc9\zxdb.lib` 两个原生 lib；`ZRXSDK\inc` 仅提供 C++/CLI 互操作头 `zmgdinterop.h` + 托管宿主接口 `zmgdhost.h`（`ZcDbManagedHost`/`zcdbInitializeManagedLayer`），即**产品内核具备托管层能力但本安装包不带托管程序集**。.NET 二次开发应视为 **2019/2021 之后**才具备的能力（【社区】+ NuGet 包证据，见 §3），**不能作为跨版本共同面**。
5. **"不装插件即可分发"成立。** COM/LISP 都由安装包自带（`IcadAuto.dll` + `ZWCAD.EXE` 自注册；LISP 内建），无需 ZRX 编译、无需签名。ZRX（C++ 插件）虽然本机自带 SDK，但需要编译、`ZRX.lib` 与产品严格同版、且分发要过 `APPLOAD`/受信任路径等策略——**不建议作为 v1 通道**。

### 0.3 一句话架构建议

> **写侧**：COM 晚绑定 `Add*/属性 put`（画图、改图、修标注）＋ `StartUndoMark/EndUndoMark` 包一个撤销组；
> **难做/缺失的**：甩给 `EvaluateLispExpression`（表格、字段、MLeader、批量选择、`entmake` 级操作）；
> **读侧**：COM 枚举（`Handle`/`ObjectName`/属性）为主，必要时 `Export("...dxf")` + ezdxf 做一次全图结构化快照；
> **核对**：`Plot.PlotToFile` 出 PDF（或 `-plot` 命令兜底）→ 栅格化比对；截图作为次选（无 COM API，需窗口截图）；
> **版本探测**：启动时 `GetVariable("ACADVER")` + 探测关键方法是否存在（`property-available-p`/IDispatch `GetIDsOfNames`），动态降级。

---

## 1. 本机事实（v16 一手证据）

### 1.1 COM 注册与身份

| 项 | 值 | 证据 |
|---|---|---|
| 版本无关 ProgID | `ZWCAD.Application` | 【实测/本机】`HKLM\SOFTWARE\Classes\ZWCAD.Application\CurVer = ZWCAD.Application.16` |
| 版本相关 ProgID | `ZWCAD.Application.16` | 同上 |
| CLSID | `{FB081ED6-0D7E-489F-A6B7-F7CEAEC10661}` | 【实测/本机】`HKLM\SOFTWARE\Classes\Wow6432Node\CLSID\...`（32 位进程） |
| LocalServer32 | `C:\Program Files (x86)\ZWCAD Classic Chs\ZWCAD.EXE` | 【实测/本机】⇒ 自动化实例就是主 EXE 本身（与 AutoCAD 同构） |
| 文档类 ProgID | `ZWCAD.Drawing` → CLSID `{6EEE4C76-6952-445D-862D-D22C7429770E}`（= 类型库里 `ZwcadDocument` 的 CLSID，即 OLE 嵌入用的文档对象） | 【实测/本机】`HKLM\SOFTWARE\Classes\ZWCAD.Drawing` |
| 自动化实现 DLL | 根目录 `IcadAuto.dll`（无版本资源，故版本号读不到） | 【实测/本机】 |
| 类型库 | `ZRXSDK\inc\Icad.tlb`（310 KB）+ 生成头 `IcadTLB.h`（66 973 行）+ `Icad_i.c`；另有 `zxdb.tlb`、`arxPort\axdb.tlb` | 【实测/本机】 |
| 类型库生成时间 | MIDL 7.00.0500，**2015-09-23**（`IcadTLB.h` 文件头注释；源 IDL 为 `..\..\lib\AUTO\Icad.idl`） | 【实测/本机】 |
| 授权 | 根目录有 `Rockey4ND.dll`、`zwlm_ts.exe`、`LocalTrial_ZWCADCLASSIC.asr`、`Licensing_Guide.chm` ⇒ 加密狗/网络授权/试用并存 | 【实测/本机】 |

### 1.2 COM 对象模型规模（由类型库反推）

| 指标 | 数量 | 备注 |
|---|---|---|
| COM 接口（`IIcad*`） | **112** | 【实测/本机】外加上 2 个事件 dispinterface（`IZwcadApplicationEvents`、`_EIcadDocument`、`_IcadWorkspaceEvents`）→ 共 **114** 个 `interface` 声明 |
| coclass（`CLSID_Zwcad*`） | **110** | 每个可 `New` 的对象都注册了独立 CLSID |
| 枚举类型 | ~57 个（`ZcSaveAsType`、`ZcPlotType`、`ZcSelectionSetType`、`ZcPlotPaperUnits`…） | 枚举名全是 `Zc*` 前缀（不是 `Ac*`） |
| 单个接口最大方法数 | `IIcadDocument` 106 / `IIcadApplication` 64 / `IIcadPlotConfiguration` 51 / `IIcadEntities` 51 | 【实测/本机】 |

> 对照：lead 提到的"113 个接口"与我的统计（114 个 interface 声明 = 112 个 `IIcad*` + 2 个事件接口）略有差异，差异来自是否把事件 dispinterface 计入；coclass 110 个是新增事实。

### 1.3 `IIcadApplication`（64 个非访问器方法，摘录关键的）

`Eval`、`GetAcadState`（→ `IsQuiescent`，**忙闲判定关键**）、`LoadZVB`/`UnloadZVB`/`RunMacro`（VBA）、`LoadLISP`、`RunCommand`、`RunScript`、`GetInterfaceObject`（拿 ARX/其他 COM 对象，如打印引擎）、`ZoomAll/ZoomCenter/ZoomExtents/ZoomPrevious/ZoomPickWindow/ZoomScaled/ZoomWindow`、`Update`、`Quit`、`DefineFunction`/`UndefineFunction`（**动态注册 LISP 命令**）、`LoadSDS`/`UnloadSDS`/`ListSDS`、`LoadZrx`/`UnloadZrx`/`ListZrx`、`IsFunctionLoaded`、`SetAppForMPrint`/`MPrint`（**批量打印**）、`Help`；属性侧 `ActiveDocument`、`Documents`、`Caption`（只读，**无 `put_Caption`**）、`StatusBar`（`get/put`，可用于"AI 正在操作"提示）、`HWND`、`WindowState`、`Visible`、`Version`/`Path`/`Name`/`FullName`、`Preferences`、`MenuBar`/`MenuGroups`/`Toolbars`、`Workspace`、`PlotManager`、`ApplicationPreferences`。

### 1.4 `IIcadDocument`（106 方法，全部关键项）

- 撤销组：**`StartUndoMark()` / `EndUndoMark()`**（注意不是 `BeginUndoMark`）
- LISP 双向通道：**`EvaluateLispExpression(BSTR) → VARIANT`**、`GetLispVariable(Name) → VARIANT`、`SetLispVariable(Name, Value)`
- 命令/脚本：`SendCommand(BSTR)`（**同步返回 void，但命令本身异步执行**，需 `GetAcadState().IsQuiescent` 轮询）、`RunScript` 在 Application 侧
- 文件/交换：`Save`、**`SaveAs(SaveFile, [ZcSaveAsType Version = zc2004_dwg])`**、**`Export(FileName, Extension, [SelectionSet])`**、`Import(FileName, [InsertionPoint], [ScaleFactor])`、`WBlock(FileName, SelectionSet, [Origin])`、`New`、`Close([SaveChanges], [File])`、`Activate`
- 读写属性：`GetVariable`/`SetVariable`（**系统变量总开关**）、`ReadOnly`、`Saved`（**是否有未保存改动**）、`WindowTitle`（**可读标题，与 WPS 版打〔AI〕标记不同，CAD 标题只读可得**）
- 对象定位：`HandleToObject(Handle)`、`ObjectIdToObject(ObjectID)`（**改图寻址的基石**）
- 空间/集合：`ModelSpace`、`PaperSpace`、`Blocks/Layers/Layouts/Linetypes/TextStyles/DimensionStyles/RegisteredApplications/SelectionSets/UserCoordinateSystems/Viewports/Views/Groups/Dictionaries/PlotConfigurations/ActiveLayout/Plot/SummaryInfo/PickfirstSelectionSet/ActiveSelectionSet`
- 其它：`PurgeAll`、`Regen(Viewports)`、`LoadShapeFile`、`CopyObjects`、`PrintDrawing([nCopies])`、`PrintPreview`

### 1.5 `IIcadModelSpace` / `IIcadBlock`：`Add*` 家族（51 个方法，**完整清单**）

`Add3DFace, Add3DMesh, Add3DPoly, AddArc, AddAttribute, AddBox, AddCircle, AddCone, AddCylinder, AddDim3PointAngular, AddDimAligned, AddDimAngular, AddDimDiametric, AddDimOrdinate, AddDimRadial, AddDimRotated, AddEllipse, AddHatch, AddRaster, AddXline, AddLeader, AddLightWeightPolyline, AddLine, AddMText, AddPointEntity, AddPolyline, AddPolyfaceMesh, AddRay, AddSolid, AddSphere, AddSpline, AddText, AddTolerance, AddTorus, AddTrace, AddWedge, AddEllipticalCone, AddEllipticalCylinder, AddExtrudedSolid, AddExtrudedSolidAlongPath, AddRevolvedSolid, AttachExternalReference, InsertBlock, AddShape, AddRegion, AddMLine`（+ `Item`/`_NewEnum`）

**缺口（对 AutoCAD ActiveX 而言）**：

| AutoCAD 有、ZWCAD v16 COM **没有** | 影响 | 兜底 |
|---|---|---|
| `AddTable` | **不能通过 COM 建表格** | `SendCommand("_TABLE ...")` / LISP `entmake`（难）/ 外部插入 DXF 片段 |
| `AddMLeader`；无 `IIcadMLeader` 接口 | **不能通过 COM 建多重引线**（工程图常用） | `SendCommand("_MLEADER ...")`，或用 `AddLeader`+`AddMText` 组合 |
| `GetDynamicBlockProperties` / `DynamicBlockReferenceProperty` | **动态块参数不可读写**（只能插块、读属性） | `SendCommand` 的 `-insert`/特性面板不行 ⇒ 只能 LISP `vla-`（若无则无解） |
| `IIcadField` / `AddField` / `GetField` | **字段（Field）无 COM API**（字段只能在文字里存在，改文字等于抹掉字段） | 保留原 `TextString` 不触碰字段；或 ZRX |
| `IIcadTable` 接口存在但**成员为空**（`IIcadTable : public IIcadEntity`，无任何方法） | **表格单元格读写、行列增删、合并 —— COM 全无** | `SendCommand`/LISP；或读 DXF 侧 `TABLE` 实体解析 |
| `AddMInsertBlock`、`AddOle`（`IIcadOle` 只读改造） | 阵列插入块、OLE 对象创建不可用 | 循环 `InsertBlock` |
| `Publish`（COM 无 `Publish`/`-PUBLISH` 方法；`IIcadPlot` 有 `StartBatchMode`+`SetLayoutsToPlot` 作批量） | 批量出图走 Plot 批处理，不走 Publish | — |
| `Caption` 可写、`ActiveDocument` 可切换 | `Caption` 只读 | `WindowTitle` 只读，用状态栏 `StatusBar` 做提示 |

> 另有**反向利好**：`IIcadPlotConfiguration` 51 个方法（`GetPaperSize/SetPaperSize`、`GetWindowToPlot/SetWindowToPlot`、`SetCustomScale/GetCustomScale`、`GetPlotDeviceNames`、`GetPlotStyleTableNames`、`GetCanonicalMediaNames`、`CopyFrom`、`RefreshPlotDeviceInfo`…），`IIcadPlot` 有 `PlotToFile / PlotToDevice / DisplayPlotPreview / StartBatchMode / BatchPlotProgress` ⇒ **静默出 PDF 的 API 面是完整的**（详见 §6）。

### 1.6 `ZcSaveAsType` 枚举（**v16 只能存到 2007 格式**）

| 常量 | 值 | 常量 | 值 |
|---|---|---|---|
| `zcUnknown` | -1 | `zc2000_dwg` / `_dxf` / `_Template` | 12 / 13 / 14 |
| `zcR12_dxf` | 1 | `zcR15_dwg` / `_dxf` / `_Template` | 12 / 13 / 14 |
| `zcR13_dwg` / `zcR13_dxf` | 4 / 5 | `zc2004_dwg` / `_dxf` / `_Template` | **24 / 25 / 26**（= `zcNative` 默认值） |
| `zcR14_dwg` / `zcR14_dxf` | 8 / 9 | `zcR18_dwg` / `_dxf` / `_Template` | 24 / 25 / 26 |
| **`zcNative`** | **24** | `zc2007_dwg` / `_dxf` / `_Template` | **36 / 37 / 38** |

⇒ **最高只有 DWG/DXF 2007**。SaveAs 到 2010/2013/2018 格式在本版 COM 不可用（产品 UI 可能靠其它路径另存，但 COM 面到此为止）。这也是"不要用 SaveAs 改格式"的理由——**已开文档原地改、用户自己保存**最安全。

其它常用枚举：`ZcPlotType`（`zcDisplay/zcExtents/zcLimits/zcView/zcWindow/zcLayout` = 0..5）、`ZcPrintArea`（`zcPACurrentView/zcPASavedView/zcPAExtents/zcPALimits/zcPAWindow`）、`ZcPlotPaperUnits`、`ZcPlotRotation`、`ZcPlotScale`（`zcScaleToFit`=0 …）、`ZcSelectionSetType`、`ZcEntityType`、`ZcLineWeight`。

### 1.7 `IIcadEntity` / `IIcadSelectionSet` / `IIcadLayer`

- `IIcadEntity`（33）：`Handle`、`ObjectName`、`EntityType`、`Layer`、`Linetype`、`LinetypeScale`、`Color`、`TrueColor`（`IIcadAcCmColor`）、`Visible`、`Lineweight`、`Plottable`、`Hyperlinks`、`GetBoundingBox`、`IntersectWith`、`Copy`、`Move`、`Rotate`、`Rotate3D`、`Mirror`、`Mirror3D`、`ScaleEntity`、`TransformBy`、`ArrayPolar`、`ArrayRectangular`、`Delete`、`Erase`、`Update`、`Highlight`、`GetXData/SetXData`、`GetExtensionDictionary`。
- `IIcadSelectionSet`（36）：`Select(Mode, Points, [FilterType], [FilterData])`、`SelectAtPoint`、`SelectByPolygon`、`SelectOnScreen`、`AddItems`、`RemoveItems`、`Item`、`Count`、`Clear`、`Delete`、`Erase`、`Highlight`、`Update`，以及**选择集级的 `Move/Rotate/Rotate3D/Mirror/Mirror3D/ScaleEntity/TransformBy/Copy`**（批量改图很省往返）。
- `IIcadLayer`（17 个纯属性）：Name、Color、Linetype、LayerOn、Freeze、Lock、Plottable、Lineweight、Description、Used、ViewportDefault…
- `IIcadBlock`（21）：`AddItems/Item/_NewEnum`、`GetXData/SetXData`、`Delete`、**`Bind`/`Detach`/`Unload`/`Reload`（外部参照操作在此）**、`WBlock`、`_handitem`。
- `IIcadExternalReference`（19）：`AttachExternalReference` 在 `IIcadEntities` 上，获取后 `Path`、`Name`、`Layer`、`GetAttributes` 等。

### 1.8 LISP 能力（本机帮助文件证据）

来源：【官方文档】`Help\LISP_Reference.chm`（396 个函数条目）、`Help\LISP_Guide.chm`（含 `ActiveX Method/Object/Property`、`Interactive with Other App`、`Version Overview`、`Appendix-*` 分卷）。用 7-Zip 解压到临时目录后检索（只读，未改安装目录）。

- **vla- 系列（ActiveX 包装函数）：可用**。官方原文："`vla-方法名`、`vla-get-属性名`、`vla-put-属性名` … 是由 ZWCAD 类型库创建的包装函数"；示例 `(setq circleObj (vla-AddCircle mspace (vlax-variant->vla-object (vlax-3d-point (list 120.0 130.0 0.0)) 3) 53.0))`。
- **拿应用对象：`(vlax-get-zwcad-object)`**（另有 `vla-get-ActiveDocument`、`vla-get-ModelSpace`）。**AutoCAD 的 `vlax-get-acad-object` 不在参考手册条目里** ⇒ 移植 AutoCAD LISP 时必改。
- **vlax- 家族齐全**（手册有独立条目）：`vlax-get-property`、`vlax-put-property`、`vlax-invoke-method`、`vlax-create-object`、`vlax-get-object`、`vlax-get-or-create-object`、`vlax-ename->vla-object`、`vlax-vla-object->ename`、`vlax-dump-object`、`vlax-for`、`vlax-ldata-*`、`vlax-safearray-*`、`vlax-variant-*`、`vlax-3d-point`、`vlax-tmatrix`、`vlax-curve-*`（17 个：`-getPointAtDist`/`-getParamAtPoint`/`-getDistAtParam`/`-getStartPoint`/`-getEndParam`/`-isClosed`…）、`vlax-product-key`、`vlax-import-type-library`（**可从 LISP 导入 Excel/Word 类型库反向驱动 Office COM**，手册给了 Excel 2003 完整示例）、`vlax-typeinfo-available-p`、`vlax-property-available-p`（**能力探测用得上**）、`vlax-read-enabled-p`/`vlax-write-enabled-p`。
- **文件 I/O 可用**：`vl-file-copy`、`vl-file-delete`、`vl-file-rename`、`vl-file-size`、`vl-file-systime`、`vl-filename-mktemp`、`vl-directory-files`、`vl-mkdir`、`vl-registry-read/write/delete` ⇒ **LISP 自身能把结果写盘**（agent 读盘拿结果，或直接用 `EvaluateLispExpression` 的返回值）。
- **命令面**：`command`、`vl-cmdf`、`entmake`、`entmakex`、`entget/entmod/entdel`、`ssget`/`ssgetfirst`、`getvar/setvar`、`tblsearch/tblnext/tblobjname`、`dict*`、`namedobjdict`、`xdata`（通过 `entget` + `regapp`）、`findfile`、`getfiled`、`eval`、（`vlisp-compile`、`vl-vbaload`/`vl-vbarun` 也在参考条目里）。
- **官方"暂时未实现"清单**（`LISP_Guide\Version Overview.htm`，标题为 "LISP 2011 新版本说明"，即该版本的兼容性说明；原文明确"暂时未实现的接口"）：
  `acad_push_dbmod / acad_pop_dbmod`、`acad_truecolorcli/dlg`、`acdimenableupdate`、`acet-layerp-mode/mark`、`acet-laytrans`、`acet-ms-to-ps`、`acet-ps-to-ms`、`defun-q-list-ref`、`layerstate-*`（整族）、`mem`、`vl-acad-defun/undefun`、`vl-arx-import`、`vl-bb-ref/set`、`vl-doc-export/import/ref`、`vl-exit-with-error/value`、`vl-get-resource`、`vl-list-exported-functions`、`vl-list-loaded-vlx`、`vl-propagate`、`vl-unload-vlx`、`vl-vlx-loaded-p`、`vl-curve-getClosestPointToProjection`、以及**大量 reactor**：`vlr-dwg-reactor`、`vlr-dxf-reactor`、`vlr-editor-reactor`、`vlr-sysvar-reactor`、`vlr-undo-reactor`、`vlr-xref-reactor`、`vlr-wblock-reactor`、`vlr-insert-reactor`、`vlr-lisp-reactor`、`vlr-owner-*`、`vlr-pers-*`、`vlr-reaction-*`、`vlr-remove-all`、`vlr-set-notification`、`vlr-trace-reaction`、`vlr-deepclone-reactor`、`vlr-miscellaneous-reactor`、`vlr-current-reaction-name`、`vlr-data-set`。
  已实现并可用的 reactor：`vlr-acdb-reactor`、`vlr-command-reactor`、`vlr-mouse-reactor`、`vlr-object-reactor`、`vlr-docmanager-reactor`、`vlr-beep-reaction`、`vlr-add/remove/added-p`、`vlr-reactors/vlr-types/vlr-type`。
  > 注意：该清单是这一代（2016 安装包所带文档）的口径，**高版本可能已补齐**；且这份文档标题停留在 "LISP 2011"，不能当作 2024/2026 的准确清单。
- **ZWCAD 特有函数**：`lsplicense`（授权状态）、`log10`（后加的）、`vlax-get-zwcad-object`。

### 1.9 ZRX（C++）SDK 事实

| 项 | 事实 | 证据 |
|---|---|---|
| 目录 | `ZRXSDK\{arxPort, inc, lib, docs, samples, utils}`；`utils\ZrxWizards.msi`（VS 向导） | 【实测/本机】 |
| inc 前缀 | **原生类前缀是 `Zc*`**：`ZcDb*`（`zdbmain.h`/`zdbents.h`…）、`ZcRx*`、`ZcPl*`（打印）、`ZcUi*`、`ZcGe*`、`ZcTc*`、`ZcDbMLeader`(zdbmleader.h)、`ZcDbTable`(zdbtable.h)、`ZcDbField`(zdbfield.h)、`ZcDbDynamicBlock`(zdbdynblk.h) | 【实测/本机】 |
| `arxPort` | **465 个头文件**，全部是 `Ac*`→`Zc*` 的薄别名层，例：`arxPort\acdb.h` 里 `typedef ZcDb AcDb; #define AcDbDwgVersion ZcDbDwgVersion …`；`arxPort\acdb.h / aced.h / acrx.h …` 覆盖 ObjectARX 常用面（含 `AcPl*` 打印、`AcTc*` 工具选项板、`AcFdUi*` 字段 UI、`AcRx*`、`AcGi*` 图形接口、`AcDb*` 全部数据库类）。 | 【实测/本机】 |
| lib | 仅 `lib\vc9\ZRX.lib`、`lib\vc9\zxdb.lib`（**VC9 / VS2008 时代 ABI，32 位**）；产出物是 **`.zrx` / `.drx`**（目录里可见 `drx` 文件）、可执行 exe（`ZwInteApp` 样例） | 【实测/本机】 |
| 托管层 | 头文件有 `zmgdhost.h`（`ZcDbManagedHost`、`zcdbGetManagedHost`、`zcdbInitializeManagedLayer`）+ `zmgdinterop.h`（`#include <vcclr.h>`、`gcroot.h`、引用 `Autodesk::AutoCAD::Runtime::DisposableWrapper`），**但无任何托管 dll** | 【实测/本机】 |
| 文档 | `docs\ZRX_Doc.chm`（1.11 MB）、`docs\ZRX_Ref.chm`（3.77 MB） | 【实测/本机】 |
| 样例 | 12 组：`SimpleLine`、`DataStorage`、`DemandLoading`、`DocumentManager`、**`LispBridge`**（ZRX 与 LISP 互通，含 `drawline.lsp`）、`MenuToolbar`、`ModalDialog`、`PersistentReactor`、`RasterImage`、`ScreenMenu`、`ZRX_Misc`（自定义实体+COM 包装 `CustomEntComWrap`、`SignEntityDb`、`SignEntityUi`）、`ZwInteBase`（COM 服务器样例） | 【实测/本机】 |
| 授权 | SDK 随安装包提供；未见单独 SDK 授权文件/许可证文本（`Licensing_Guide.chm` 是产品授权说明）；**"分发 .zrx 是否需要开发者授权"未验证** | 【实测/本机】缺口 |

### 1.10 VBA / 其它

- VBA：`Help\VBA_Guide.chm`、`VBA_Reference.chm`；样例 `Sample\VBA\*.zvb`（11 个，含 `Example_Excel.zvb`）；运行时 `msvbvm60.dll`（VB6 运行时）；VBA 工程由 `Application.LoadZVB/UnloadZVB/RunMacro` 驱动 ⇒ **VBA 可用，但对 agent 不友好**（需装 VBA 模块、宏签名/安全策略）。
- SDS（老 ADS C API）：`SDS\inc\sds.h`（54 KB）、`SDS\lib\vc9\sds.lib`、样例 `FirstSDS`；`Application.LoadSDS/ListSDS` ⇒ 遗留通道，**不建议**。
- Help 目录全清单：`Customization_Guide / DCL / Licensing_Guide / LISP_Guide / LISP_Reference / movies / SDS / VBA_Guide / VBA_Reference / ZWCAD / ZWCAD_Dev / ZWCAD_ET / ZWCAD_ICR / ZWCAD_UG`（UG=用户手册 4.26 MB，ET=扩展工具，ICR=光栅图像 2.25 MB）。
- 样例 LISP：`Sample\LISP\` 13 个（含 `GetLayerList.lsp`、`ColorCircle.lsp`、`TextFrame.lsp`+DCL、双向偏移等）——**可直接作为"LISP 能干什么"的样本**。

---

## 2. 通道对比表

| 通道 | 能做什么 | 不能做什么 / 限制 | 依赖 | 版本面 |
|---|---|---|---|---|
| **COM / ActiveX（晚绑定）** | 挂已开文档；枚举/增删改 40+ 种实体；图层/线型/文字样式/标注样式；选择集；**Undo 组**；系统变量读写；`SendCommand` 全命令面；`Export`/`SaveAs`/`WBlock`；**`EvaluateLispExpression` 带返回值**；**`Plot.PlotToFile` 静默出 PDF**；批量打印 `StartBatchMode`/`MPrint`；Handle 寻址改图 | 无 `AddTable`/`AddMLeader`/表格单元格/字段/动态块属性；无 `Publish`；`Caption` 只读；`SaveAs` 最高 2007 格式；**无截图/渲染 API**；大量逐实体属性访问＝往返成本高（需一次取全的策略） | 仅需安装产品（ProgID 已注册）；32/64 位进程需匹配（本机 v16 是 32 位 ⇒ agent/helper 必须 x86 或走 COM 跨位代理） | ZWCAD 各代均有（见 §3），**首选** |
| **LISP（AutoLISP）** | 全套 `command`/`entmake`/`ssget`/`getvar`；`vla-*`＝ActiveX 全部能力；`vlax-import-type-library` 反向驱动 Office COM；`vl-file-*`/注册表读写；`vlax-curve-*` 几何计算；reactor（部分）；`(princ)` 输出 | `vl-doc-*`/`vl-bb-*`/`vl-arx-import`/`layerstate-*`/`acet-*`/多数 reactor 官方标注未实现；**`vlax-get-acad-object` 不存在**；无类型检查、错误处理粗糙 | 产品自带，无需安装；用 `EvaluateLispExpression`/`LoadLISP`/`SendCommand("(load ...)")` 触发 | ZWCAD 各代均有；vlx/fas 加密需注意 |
| **ZRX C++ SDK** | 全量数据库/图形/打印/选择/事务/自定义实体；`ZcDbTable`/`ZcDbMLeader`/`ZcDbField`/动态块等 COM 缺的功能都在；可直接编译 `.zrx`；可与 LISP 互通（`LispBridge` 样例）；可做 COM 服务器（`ZwInteBase`/`CustomEntComWrap`） | 需 VS2008 时代工具链（vc9 ABI）编译、**与产品版本严格配对**（每代重编）；分发要用户 `APPLOAD`/受信任路径；调试成本高；**授权/分发条款未验证** | 需 SDK（本机自带）+ 编译器 | 各代有 SDK（版本需匹配）；**不适合 v1** |
| **.NET 托管 API** | 与 ZRX 等价的托管封装（事务、数据库、Editor、事件），开发体验最好；`NETLOAD` 加载 dll；可做常驻 bridge 供 MCP 用（先例见 §5） | **本机 v16 无托管程序集** ⇒ 明确不支持；需 .NET Framework 4.7.2+ / 与产品同版 SDK dll；跨版本需重编；`NETLOAD` 受安全设置影响 | 产品安装目录的托管 dll（2019/2021+），本机没有 | **2019/2021 起**（推断+社区），**不作为共同面** |
| **DXF / DWG 离线** | DXF：ezdxf 可**读+写**（R12→R2018），能建实体、改属性、加图层、写块、算面积；DWG：只能靠 ODA File Converter / LibreDWG 转换后处理；可直接用 ezdxf 生成"补丁 DXF"再让 CAD 导入；读侧做全图结构化快照/审计/回归比对最稳 | DWG 是私有二进制，**Python 不能直接可靠改 DWG**；DXF 往返会丢"CAD 内核语义"（标注关联、字段、动态块参数、表格样式、代理对象）；ezdxf 不评估 CAD 专有对象；导出的 DXF 版本受产品限制（v16 最高 2007） | Python + `ezdxf`（MIT）；DWG↔DXF 需 ODA（商业/免费 File Converter、需注册）或 LibreDWG（GPLv3+，覆盖不完整） | 与产品版本弱相关（DXF 格式开放） |
| **直接操作 DWG 二进制** | 无实用路径 | 私有格式、无公开规范；LibreDWG 只做部分读；ODA 是"文件级转换"不是"编辑内核" | — | — |

> **必须回到 CAD 内核的操作**（离线方案做不了或做了会失真）：标注关联更新与标注文字覆盖、字段求值、动态块参数驱动、表格（含合并/样式）、代理对象（自定义实体）、打印样式表（CTB/STB）解析与出图、外部参照路径解析与绑定、图形重生成/显示顺序（DrawOrder）、`AUDIT`/`OVERKILL` 级别的修复与清理。

---

## 3. 版本能力矩阵（2012 → 2026）

### 3.1 产品代数（本机可见规律 + 官方/社区证据）

- 本机：产品目录名 `ZWCAD Classic Chs`，安装版本 `16.0.1111`，ProgID 后缀 **`.16`** ⇒ **版本号 = 2 位年份（2016）**。
- 【社区/官方】ZWCAD 2017 于 **2016-06 正式发布**（"该版本产品实现了从底层…"，见 §8 的招股书 PDF 片段）：说明 2016/2017 是相邻两代且版本号规则延续。
- 【官方】NuGet 第三方重打包 `ZWCAD.NetApi` 的版本号是 **20.14.1 / 20.15.0 / 20.17.0 … 20.24.0 / 20.25.0 / 20.26.0**（20.26.0 = "ZWCAD2026 NetApi"，2025-07 发布）⇒ 存在 2014～2026 各代的 **.NET API 程序集**（`ZwDatabaseMgd.dll`、`ZwDatabaseMgdBrep.dll`、`ZwManaged.dll`、`ZcCui.dll`、`ZcWindows.dll`、`ZdWindows.dll`、`ZwSoft.ZwCAD.Interop.dll`、`ZwSoft.ZWCAD.Interop.Common.dll`）。
- 【社区】`IFox.CAD.ZCAD2022` 包说明："面向 **ZWCAD 2021-2024** x64 API 代际…使用 **ZRXSDK 2022** x64 编译基线"；`IFox.CAD.ZCAD2025`："面向 **ZWCAD 2025-2026** x64… 使用 **ZWCAD 2025 SDK** 编译基线"。
- 【官方】**ZWCAD 2026 的在线帮助仍有 `VBA_Reference` 树**（`helpmanager.zwsoft.cn/.../ZWCAD_2026_Chs/OriginalData/VBA_Reference/html/ObjectModel.htm`）⇒ ActiveX/VBA 在 2026 版仍在维护（正文抓取失败，仅 URL 结构证据）。
- 【官方】`dl.zwsoft.com/support/ZWCAD/2025/COM/ZWCAD_COM_Guide_enu_2025.pdf`（**2025 版 COM 自动化开发指南**）与 `.../2024/ZWCAD_Lisp_FAQ_enu_2024.pdf`（2024 版 LISP 兼容 FAQ）在官方下载域存在（本次下载被重定向，正文未取，**需人工取一次存档**）。
- 【官方】ZWCAD 2019 SP2 发布说明含 "-PUBLISH  To execute PUBLISH command in command line" ⇒ **命令行 `-PUBLISH` 是 2019 SP2 起新增**。

### 3.2 矩阵（✅有 / ⚠️有条件或未验证 / ❌无 / —未查）

| 能力 | 2012 | 2015 | **2016（本机）** | 2017 | 2019 | 2021 | 2024 | 2025 | 2026 |
|---|---|---|---|---|---|---|---|---|---|
| COM `ZWCAD.Application` | 推断✅ | 推断✅ | **✅实测** | 推断✅ | 推断✅ | 推断✅ | 推断✅ | ✅社区（MCP 项目用 `ZWCAD.Application`） | 推断✅ |
| `ZWCAD.Application.NN` 版本化 ProgID | 推断 `.12` | 推断 `.15` | **✅实测 `.16`** | 推断 `.17` | 推断 `.19` | 推断 `.21` | 推断 `.24` | 推断 `.25` | 推断 `.26` |
| ActiveX 对象模型（`Icad*`）与 AutoCAD 同名成员 | 推断✅ | 推断✅ | **✅实测 114 接口** | 推断✅ | 推断✅ | 推断✅ | 推断✅ | 推断✅ | 推断✅（VBA_Reference 仍在） |
| LISP + `vlax-*` + `vla-*` | 推断✅ | ✅（官方 Version Overview 提到 2011 起补 vlax/vl） | **✅实测** | 推断✅ | 推断✅ | 推断✅ | ✅（有 LISP FAQ 2024） | 推断✅ | 推断✅ |
| .NET 托管 API | ⚠️（NuGet 有 20.14/20.15，但本机 16 无 ⇒ 存疑） | ⚠️ | **❌实测无托管 dll** | ⚠️ | 推断✅ | ✅社区（IFox 2021-2024） | ✅ | ✅（IFox 2025-2026） | ✅（NetApi 20.26.0） |
| ZRX C++ SDK 随安装包 | 推断✅ | 推断✅ | **✅实测** | 推断✅ | 推断✅ | ✅（ZRXSDK 2022） | ✅ | ✅（2025 SDK） | 推断✅ |
| `SaveAs` 可写格式上限 | R12～2007 推断 | 同 | **✅实测 ≤2007** | 同 | 可能更高 | 可能更高 | 可能更高 | 可能更高 | 可能更高 |
| `Export` DXF | 推断✅ | 推断✅ | ⚠️**接口在，扩展名列表未测** | 同 | 同 | 同 | 同 | 同 | 同 |
| `Plot.PlotToFile` / 批量打印 | 推断✅ | 推断✅ | **✅接口实测在** | 同 | 同 | 同 | 同 | 同 | 同 |
| 命令行 `-PUBLISH` | ❌推断 | ❌推断 | **❌（无 COM Publish；命令未验证）** | ❌推断 | **✅ 2019 SP2 起** | ✅ | ✅ | ✅ | ✅ |
| `AddTable` / 表格单元格 COM | 推断❌ | 推断❌ | **❌实测缺** | 推断❌ | 未查 | 未查 | 未查 | 未查 | 未查 |
| `AddMLeader` / MLeader COM | 推断❌ | 推断❌ | **❌实测缺** | 未查 | 未查 | 未查 | 未查 | 未查 | 未查 |
| Field / 动态块 COM | 推断❌ | 推断❌ | **❌实测缺** | 未查 | 未查 | 未查 | 未查 | 未查 | 未查 |
| 官方 AI / MCP | — | — | — | — | — | — | — | 官方有 AI 宣传（[中望CAD+AI](https://www.zwsoft.cn/news/291-16324.html)） | 同上 |

**跨版本结论（回答 lead 的问题）**：
1. **能靠 `ZWCAD.Application` + ActiveX + LISP + DXF 覆盖 2016→2026 的日常自动化**（读图、画图、改属性、标注、图层、出 PDF、提取统计）。三条通道都是产品内建、无插件分发负担。
2. **风险点集中在"版本化 ProgID 命名"与"新实体的 COM 覆盖"**：前者只需统一用**版本无关 ProgID `ZWCAD.Application`**（有自己的 `CurVer` 兜底）即可绕开；后者要靠 LISP/命令兜底而不是指望 COM。
3. **不存在"中望弃用 COM"的迹象**：2025 有官方 COM 指南 PDF，2026 在线帮助仍有 VBA_Reference 树；相反 .NET 才是"新且分代"的。
4. **`ZRX`/`.NET` 分代严重**（每代 SDK 不同、需重编），**不适合"不绑版本"的目标**；把它们定位成"可选加速插件"（用户明确要高性能时再针对该版编译）。

---

## 4. 与 AutoCAD 的差异 / 坑（逐条，含规避方案）

### 4.1 命名与类型库
1. **原生类前缀是 `Zc*`**（`ZcDbLine`、`ZcDbDatabase`…），`Ac*` 只是 `arxPort` 里的 `#define/typedef` 别名层（465 个头）。写 C++ 时含 `arxPort` 目录即得 AutoCAD 源码兼容；但**编译目标与库是 ZRX 不是 ARX**。
2. **COM 接口名是 `IIcad*`**（`Icad` + MIDL 的 `I`），不是 `IAcad*`。后果：AutoCAD VBA/C# 早绑定代码**不能直接编译**；必须改类型名或改晚绑定。**建议直接晚绑定**（`Type.GetTypeFromProgID` / `dynamic` / `InvokeMember`）。
3. **枚举名是 `Zc*`**（`ZcSaveAsType`、`ZcPlotType`…），AutoCAD 的 `AcSaveAsType` 不存在。
4. 事件：`IZwcadApplicationEvents` / `_EIcadDocument` / `_IcadWorkspaceEvents` 三个 dispinterface ⇒ 事件可用，但**事件名与 AutoCAD 不完全一致**，需实读类型库。

### 4.2 明确缺失的成员（见 §1.5 表）
`AddTable`、`AddMLeader`、`IIcadMLeader*`、IIcadTable 成员、`IIcadField`、动态块属性、`Publish`、`AddMInsertBlock`、可写 `Caption`。

### 4.3 `SendCommand` 行为坑
- 返回 `void` 且**立即返回**，命令在 CAD 主线程异步执行；连续 `SendCommand` 会排队，但**不能假设上一条已完成**。必须用 `Application.GetAcadState().IsQuiescent` 轮询，或让命令写一个"完成标记"（系统变量 / 临时文件 / LISP 全局变量）再由 agent 读。
- 命令行里命令名/选项要用 `_`（国际化前缀，如 `_PLOT`、`_-PLOT`）与 `.`（原始语言前缀）约定；中文版 UI 下建议 `_.` 双前缀。
- 需要用户交互的命令（含 `-PLOT` 某些分支）在隐藏实例上会挂起 ⇒ **不要对隐藏实例发交互式命令**。
- 空串、`ESC`（`"\x03"`）用于打断卡住的命令。

### 4.4 出图 / PDF
- COM 路径：配置 `PlotConfiguration`（`ConfigName` 一般 `"DWG To PDF.pc3"`、`CanonicalMediaName`（如 `ISO_A3`）、`PlotType=zcWindow/zcExtents`、`SetWindowToPlot`、`SetCustomScale`、`PlotRotation`、`PlotWithPlotStyles`、`StyleSheet`）→ `doc.Plot.PlotToFile(FileName)`。**打印设备名/纸张名必须先用 `GetPlotDeviceNames`/`GetCanonicalMediaNames` 枚举**，不要硬编码。
- 命令路径（兜底）：`SendCommand("_-PLOT\n...")` 或 `-PUBLISH`（2019 SP2+）。
- **本机缺 `IIcadPublish`**，`Publish` 只能用命令行。
- 中望的 PDF 导出 DLL 在根目录（`DD_PdfExport_2.05src_9.dll`、`PDFDriver.dll`、`PDFToolkit_2.05src_9.dll`、`pdflib.dll`）——是 ODA 打印栈（`2.05src_9` 说明是 Teigha/ODA 2.05 源码构建）⇒ 打印/PDF 走 ODA 技术，**行为可参考 ODA 打印栈**。
- 另外注意：本机有**透明加密（Agile DGS）**，落地文件可能是密文（头 `17DA5FA01633CD9A`），PDF/DXF 导出后需解密才能被 Python 读取（见全局记忆「本机文档处理环境坑」「decrypt-office-pdf」）。

### 4.5 表格 / 字段 / 动态块（对本项目最疼的三处）
- **表格**：COM 只能看到"一个 Table 实体"（图层/颜色/句柄/包围盒），**单元格内容读不到**。候选路径：(a) LISP `tblsearch` 不行（那是符号表）；要用 `(entget (car (entsel)))` 读 extended data（表格内容不在 DXF 常规组码，需 `ACAD_TABLE` 解析）；(b) `Export` 成 DXF 后由 ezdxf 解析 `TABLE` 实体的 `ACAD_TABLE` 组码；(c) `-DATAEXTRACTION` / `-TABLE` 命令生成 CSV 再读；(d) 兜底：让用户把表转成"直线+文字"或直接由 agent 用 `AddLine/AddText` 重绘（**对"按数据画工程量表"这种从零生成的场景反而更可控**）。
- **字段**：无 COM。任何"改文字"操作都可能把字段替换成静态文本。**校对/改图时要先判断该 Text/MText 是否含字段**（DXF 里是 `AcDbField` 对象 + 文字里的 `%<\AcVar ...>%` 模式）——读 DXF 才可靠。
- **动态块**：COM 只能 `InsertBlock` 插块、`GetAttributes` 读属性；**动态参数不可读写**。工程图里"参数化图框/阀门"若要按数据驱动，只能 ZRX/.NET 或让用户预先把需要的形态做成独立块。

### 4.6 其它
- **位宽**：本机 v16 是 32 位（`Program Files (x86)`、`Wow6432Node` 注册表）⇒ COM 跨进程调用需要 **x86 的 helper/agent 侧**（或在 64 位进程里走 DCOM 代理）。2021+ 为 64 位。**这是"跨版本"最容易踩的工程坑**：一套 helper 二进制覆盖不了 32/64 两代，需按位宽准备两份或都用 out-of-process 脚本（如 PowerShell 32 位/x64 各一份）。
- **坐标系**：`vla-`/COM 点一律 WCS；`Editor.GetPoint`（.NET/LISP `getpoint`）返回 UCS。转换需 `TranslateCoordinates`（`IIcadUtility`）或矩阵。
- **中文/字体**：`AddText/AddMText` 的字高是图形单位（不是磅）；中文依赖 `TextStyle` 的 `fontFile`（如 `gbcbig.shx` + `hztxt.shx`），缺字体会变 `?`；MText 里的 `\U+XXXX` 转义要正确。
- **只读/加密图纸**：`ReadOnly` 属性可判；被 DGS 加密的图纸在别的进程看来是密文，COM 打开也可能失败 ⇒ 需要先解密（见全局记忆）。
- **`PurgeAll` / `Regen`**：批量操作后 `Regen` 很慢（大图数秒），按需调用；`PurgeAll` 会删未使用块/图层，慎用（可能删用户要的）。

---

## 5. 可复用生态与先例

| 项目 / 资源 | 用途 | 备注 |
|---|---|---|
| **ezdxf**（[github](https://github.com/mozman/ezdxf)，1517★，持续活跃到 2026-11） | Python 读写 DXF（R12→R2018）：读全图做快照/审计、生成补丁 DXF、算面积/长度/统计 | MIT；**DXF 侧的"事实标准"**；不处理 DWG、不评估 CAD 专有对象/字段语义 |
| **ODA File Converter**（Open Design Alliance） | DWG↔DXF 批量转换（含 2018 格式） | 免费但需注册下载；是"DWG 离线化"的工业级路径 |
| **LibreDWG**（GNU） | DWG 读写库 | GPLv3+；覆盖不完整，生产慎用 |
| **ZWCAD2025-MCP**（[GitHub routel/ZWCAD2025-MCP](https://github.com/routel/ZWCAD2025-MCP)，TS，2026-05 更新） | **同题先例**：MCP server + ZWCAD 内 C#.NET 常驻 bridge（Named Pipe + JSON），**COM 直连被标注为 experimental/初始验证**，环境变量 `ZWCAD_COM_PROGRAM_ID=ZWCAD.Application` | 印证两点：①`ZWCAD.Application` ProgID 在 2025 仍有效；②社区最终也走向"COM 起步 → .NET 常驻桥"的演进路线 |
| **中望官方开发者资源** | [开发支持页](https://www.zwsoft.cn/support/zwcad-devdoc)、[第三方应用/开发者平台 zdn.zwsoft.cn](https://zdn.zwsoft.cn/)、[开发者社区 forum.zwsoft.cn/ask/developer](https://forum.zwsoft.cn/ask/developer)、[在线帮助 help.zwsoft.cn](https://help.zwsoft.cn/) | 有开发者注册/上架体系（第三方应用平台）；本机未见强制签名要求（COM/LISP 路径无需上架） |
| **ZWCAD.NetApi**（NuGet，第三方 InspireFunction 重打包，MIT） | .NET 程序集分代包（20.14→20.26） | **非官方包**，用于确认各代托管 dll 名称/存在性；官方声明"不提供官方 NuGet"（社区口径） |
| **IFox.CAD.ZCAD2022 / ZCAD2025**（NuGet） | 基于 ZRX SDK 的 CAD 二次开发基础类库（按代际） | 佐证"SDK 按代际、需重编" |
| **AutoCAD MCP / CAD MCP 类项目** | 通用模式参考：MCP tool → 常驻 bridge → CAD 原生 API；工具面通常是"画线/插块/枚举/分析/批量改" | 本次未逐一取证据（GitHub 搜索受网络限制），建议 lead 侧用 `gh` 补 |
| **`wps-office-cooperate`（本机既有）** | 直接复用的架构：wrapper/daemon/helper、JSON 协议、快照+渲染核对、`-Mta`/常驻进程提速、原子批+回读校验、hint 提示 | 差异点：CAD 无"修订模式"⇒ 用 `StartUndoMark/EndUndoMark` + 盘上快照 |

---

## 6. 出图与"图形是否正确"的自动核对

**手段排序（成本 / 可信度）**：

1. **导出 PDF → 栅格化 → 与基线图比对**（推荐主手段）
   `PlotConfiguration` + `Plot.PlotToFile`（或 `_-PLOT`）→ PDF → 用 PDF 渲染（`Windows.Data.Pdf` / poppler / pdfium）出 PNG → 与"预期图"比对（像素差 + 结构相似度 SSIM + 关键区域 ROI 校验）。
   优点：所见即所得、与用户看到的一致；缺点：PDF 生成有字体/线宽/CTB 影响，纯像素比对噪声大 ⇒ 建议"关键量"比对（图框尺寸、标题栏文字 OCR、标注数字）。
2. **导出 DXF → 用 ezdxf 重建"结构快照"做断言**（推荐做"正确性断言"而非视觉）
   期望：某个图层上有 N 条线、闭合多段线面积 = X、某文字内容 = Y、标注测量值 = Z、图框块参照存在 → 这些都能在 ezdxf 里精确断言（比截图可靠得多）。
   注意：v16 的 `Export` 扩展名/版本需实测；且 DXF 往返丢字段/表格样式。
3. **窗口截图**（兜底，用于"用户视角确认"）
   【实测/本机】无 COM 截图 API，但 `IIcadApplication.HWND`、`IIcadDocument.HWND`、`Window.GetWindowRect` 都在 ⇒ 可 `ZoomExtents` → 前置窗口 → `PrintWindow`/`CopyFromScreen` 抓图。
   风险：被遮挡、DPI 缩放、CAD 用 DirectX/硬件加速（`WinGDI_2.05src_9.gs`、`W3dTk.dll` 提示有 GDI 与 3D 两套图形设备）⇒ 截图可能黑屏，需实测。
4. **`Plot.Preview` / `DisplayPlotPreview`**：会弹预览窗，不宜自动化。
5. **命令级自检**：`AUDIT`（图形数据库审计，可自动跑并读输出）、`OVERKILL`（重复对象清理，慎用）、`-PURGE`。

**"AI 改动 → 用户确认"的落地建议**：
- 变更前：`Handle` 级快照（实体句柄+关键属性）→ 写盘 JSON；
- 变更中：`StartUndoMark`/`EndUndoMark` 包住一次操作 ⇒ 用户一次 `U` 就能整体撤销；
- 变更后：①ezdxf 断言关键量 ②导出 PDF/截图给用户看 ③状态栏 `StatusBar` 打"AI 已改 N 处"。

---

## 7. 不确定项与必须实测清单（交给 com-probe）

> 以下每条都给出"为什么必须测"和"最小可判据"。

### P0（决定架构，必须先测）
1. **附着已运行实例**：`GetObject`/`Marshal.GetActiveObject`/ROT 是否能拿到**用户正开着的那份**；拿不到则必须"新建实例+打开同一文件"（会影响"实时协作"体验）。判据：附着后 `doc.FullName` == 用户界面标题文件。
2. **位宽**：v16 是 32 位 ⇒ 64 位 Python/pwsh 直接 `CreateObject` 是否成功（DCOM 代理是否自动）；失败则需 32 位 helper。判据：64 位进程 `New-Object -ComObject ZWCAD.Application` 能否拿到对象、`Version` 是否可读。
3. **`EvaluateLispExpression` 语义**：同步性、返回值类型（list/point/string/nil）、LISP 抛错时的 HRESULT/异常、能否在其中调用 `(command ...)`。判据：`(list 1 2 3)`、`(getvar "ACADVER")`、`(vlax-get-zwcad-object)` 三个表达式的返回值。
4. **`SendCommand` 完成判定**：连续命令的完成检测（`GetAcadState.IsQuiescent` 是否可靠）、`-PLOT`/`-SCRIPT` 在隐藏实例上是否挂起。
5. **`Export` 扩展名与 DXF 版本**：`Export("x","DXF")` 产出哪个 DXF 版本？`"DWG"/"BMP"/"WMF"/"EPS"/"SVG"/"PDF"?` 哪些有效？ezdxf 能否 100% 解析？
6. **`PlotToFile` 端到端**：枚举 `GetPlotDeviceNames()` 看本机是否只有 `DWG To PDF.pc3`；跑通"窗口 + 自定义比例 + A3"→ PDF；验证是否静默（无对话框）、耗时、PDF 页数/尺寸正确。
7. **加密图纸**：本机 DGS 透明加密下，COM 打开/导出是否正常、落地文件是否密文（读首 8 字节）。

### P1（影响功能面）
8. **撤销组**：`StartUndoMark/EndUndoMark` 是否真把多次 COM 修改合并为一次 `U`；嵌套行为；用户已有未保存改动时的影响。
9. **句柄寻址改图**：`HandleToObject` 对各类实体（Line/LWPolyline/Text/MText/Dim*/BlockRef/Hatch）是否稳定；跨 `Regen`/`Undo` 后是否失效。
10. **表格**：`IIcadTable` 真的是空接口？（`vla-dump-object` 一个 Table 实体）确认无单元格 API；验证 `-DATAEXTRACTION`/DXF 解析两条兜底。
11. **MLeader**：确认 `AddMLeader` 不存在；`AddLeader`+`AddMText` 组合能否满足工程标注（多段引线+文字对齐）。
12. **动态块/字段**：对含动态块与字段的样例图，COM 读写会怎样（字段是否被洗成静态文本）。
13. **性能基线**：1 万实体枚举（`Item(i)` vs `foreach/_NewEnum`）、1 千次属性 get/put 的往返成本、`AddLWPolyline` 1 万顶点耗时、`Export DXF` 耗时。用于决定是否要 daemon/批量接口。
14. **截图**：`ZoomExtents` + `PrintWindow`/`CopyFromScreen` 是否拿到非黑屏、DPI 缩放下分辨率是否正确。
15. **中文**：`AddText` 用默认样式写中文是否正常；需要哪些 `TextStyle`/字体；`MText` 中文换行与宽度。
16. **多文档**：`Application.Documents` 多开时如何定位目标文档、`ActiveDocument` 切换是否影响用户。
17. **版本探测**：`GetVariable("ACADVER")` 在各代返回什么（v16 预期 `AC1015`/`AC1018`/自有值？）；`GetVariable("PRODUCT")`/`"PROGRAM"` 是否可区分 中望/其它 CAD（**同一台机上 AutoCAD/WPS 抢 ProgID 的坑在 WPS 上已出现过，CAD 侧要防 `ZWCAD.Application` 被别的产品注册**）。
18. **`ACADVER` 与 `VERNUM`**：确认 ZWCAD 是否复用 AutoCAD 的版本变量语义（决定"按变量降级"策略是否可行）。

### P2（工程化）
19. 常驻 helper 的可行性：`-Mta` 思路（WPS 版结论）在 CAD COM 上是否同样显著（CAD 的 STA 初始化成本未知）。
20. `StatusBar` 提示能力（`get/put` 的是文本还是仅开关）；`WindowTitle` 是只读属性却叫方法（`WindowTitle()`）——实际调用方式要实测。
21. `ZRXSDK` 分发/授权：官方是否允许随 skill 分发 `.zrx`；`APPLOAD` 是否有受信任路径限制。
22. `-PUBLISH` 在 v16 是否存在（本机 2016 早于 2019 SP2）。

---

## 8. 参考来源（URL）

**官方（中望）**
- 中望CAD 开发支持（开发者文档入口）：https://www.zwsoft.cn/support/zwcad-devdoc
- 第三方应用 / 开发者平台：https://zdn.zwsoft.cn/
- 开发者社区：https://forum.zwsoft.cn/ask/developer
- 在线帮助（含 2026 中文帮助树，`VBA_Reference/ObjectModel.htm`）：https://helpmanager.zwsoft.cn/file/helpres/ZWCAD_zh-CN_2026/ZWCAD_2026_Chs/OriginalData/VBA_Reference/html/ObjectModel.htm
- ZWCAD 2025 COM 自动化开发指南（PDF，下载被重定向，需人工取）：https://dl.zwsoft.com/support/ZWCAD/2025/COM/ZWCAD_COM_Guide_enu_2025.pdf
- ZWCAD 2024 LISP 兼容 FAQ（PDF）：https://dl.zwsoft.com/support/ZWCAD/2024/ZWCAD_Lisp_FAQ_enu_2024.pdf
- 《Lesson 101 of developing in ZWCAD》（PDF）：http://dl.zwsoft.com/support/ZWCAD/Lesson_101_of_developing_in_ZWCAD.pdf
- ZWCAD 2019 SP2 发布说明（含 `-PUBLISH`）：https://zwcad.co.jp/main-new/wp-content/uploads/2019/02/ZWCAD_Release_Note_2019_SP2.0.pdf
- 官方新闻《工业软件升维，中望CAD+AI 正在重新定义工程设计》：https://www.zwsoft.cn/news/291-16324.html
- ZWCAD 2026 What's New：https://www.zwsoft.com/mea/product/zwcad/whats-new
- ZWCAD 2026 发布稿：https://test.zwsoft.com/news/products/zwcad-2026-design-with-speed-innovate-with-ease
- VBA 插件移植到 ZWCAD（官方 Confluence 导出）：https://confluence.zwcad.com/spaces/flyingpdf/pdfpageexport.action?pageId=337146462
- ZWCAD 中 VBA 相关函数为何失效（官方）：https://confluence.zwcad.com/spaces/flyingpdf/pdfpageexport.action?pageId=337146638
- ZWCAD 2017 发布时间与"底层重建"表述（招股书 PDF 片段）：http://file.finance.sina.com.cn/211.154.219.97:9494/MRGG/CNSESH_STOCK/2020/2020-9/2020-09-30/6641938.PDF

**生态 / 社区**
- ZWCAD2025-MCP（MCP + .NET bridge 先例）：https://github.com/routel/ZWCAD2025-MCP
- ZWCAD.NetApi（NuGet，分代托管程序集，含 dll 清单）：https://www.nuget.org/packages/ZWCAD.NetApi
- IFox.CAD.ZCAD2022 / ZCAD2025（按 SDK 代际封装的类库说明）：https://www.nuget.org/packages/IFox.CAD.ZCAD2022 、https://www.nuget.org/packages/IFox.CAD.ZCAD2025
- ezdxf（Python DXF 库）：https://github.com/mozman/ezdxf
- 中望CAD .NET 二次开发实战（社区，**注意：AI 生成内容，仅作线索**）：https://blog.csdn.net/weixin_42635064/article/details/167185023
- 中望CAD Python 自动化实战（社区，仅作线索）：https://cloud.tencent.com.cn/developer/article/2698997

**本机文件（一手，只读）**
- `...\ZRXSDK\inc\IcadTLB.h`（66 973 行，2015-09-23 由 `Icad.idl` 生成）、`Icad.tlb`、`Icad_i.c`
- `...\ZRXSDK\lib\vc9\{ZRX.lib, zxdb.lib}`、`...\ZRXSDK\inc\{zmgdhost.h, zmgdinterop.h}`、`...\ZRXSDK\arxPort\*`（465 个头）
- `...\Help\{LISP_Reference, LISP_Guide, VBA_Guide, VBA_Reference, ZRX_Doc(在 SDK\docs), Customization_Guide}.chm`
- `...\Sample\LISP\*`、`...\Sample\VBA\*.zvb`、`...\SDS\*`

---

## 9. 给 skill 设计的直接落点（5 条）

1. **接口层**：`ComChannel`（晚绑定 + `InvokeMember` 封装 + 方法存在性探测）＋ `LispChannel`（`EvaluateLispExpression` 带返回值）＋ `DxfChannel`（ezdxf 只读快照/断言）。三者按能力降级，不混用。
2. **版本探测放在最前**：`ACADVER`/`PRODUCT` + `GetIDsOfNames` 试 `AddTable`/`EvaluateLispExpression` ⇒ 生成一份 runtime capability 表，后续命令按表走。
3. **写入一律包撤销组**：`StartUndoMark → 批量属性写/Add* → EndUndoMark`，并记 `Handle` 清单作为"这次改了什么"的事实来源。
4. **缺能力就走 LISP 模板**：表格/字段/MLeader/批量选择/复杂几何计算写成 `.lsp` 模板 + 参数注入，用 `EvaluateLispExpression` 调用并收回值（不要把复杂逻辑塞进 `SendCommand` 字符串）。
5. **核对双轨**：ezdxf 断言（正确性）+ PDF/截图（观感）。不要只做像素比对。
