# PSD UI Workbench

最新实验：扩张已改为字形局部坐标量，完成 0.5/1/2 倍均匀 UI 缩放、90°旋转及同组件改字验证。见 [局部坐标验证](Documentation/TMP_GLYPH_SPACE_VERIFICATION.md)。仍为可选实验，尚未替换正式导出默认。

支持 Unity 6000.3.20f1 / 2D PSD Importer 12.0.2，入口：`Tools/PSD UI Workbench`。

当前推荐导出为普通 TMP + `TextMeshPro/Mobile/Distance Field - PSD Outer Stroke`：不添加 Workbench 运行时组件，不生成邻字布局纹理。详见 [单字形共享材质方案](Documentation/TMP_SINGLE_GLYPH.md)。

2026-11-16 已完成 13 个 TOXIA 文本层的字面/真实外描边联合对照。首 H 在开关正式描边时字面逐像素不变；横向扩张的左右分配会影响不同字符。对称候选在本组 13 层的字面及外轮廓诊断量均改善，但仍需处理 Canvas 缩放后才能接入生产。详见 [字面与外描边联合验证](Documentation/TMP_OUTLINE_BOLD_VERIFICATION.md)，正式默认未改。

2026-11-16 无描边验证：关闭模拟粗体后的 TOXIA 48 字号首 H 已较接近 Photoshop；恢复源 Tracking 后，两个 Normal 样本的排版改善。原 PSD 的 `fauxBold=true` 字面仍有差异，间距不能修复单字增粗。下一步优先校准 Character／Word，补充 Line／Paragraph，Horizontal Scale 保持 1。方向性增粗仅保留为实验，正式参数未改。见 [字面、字重与间距诊断](Documentation/TMP_FILL_DIAGNOSIS.md) 和 [原位对照](Documentation/PsdTextAudit/StrokeDiagnosis/fill-spacing-review.png)。视觉基准采用 [Photoshop 原生 PNG](Documentation/PSD_REFERENCE_CORRECTION.md)，不将 IoU 当作还原率，也未宣称基本完全一致。

## 使用

选择或拖入项目内 PSD/PSB。左侧为独立图层树，右侧画布按原始 PSD 位置显示**已应用的导入结果**。滚轮缩放，中键或右键平移，“适应窗口”恢复全图。

“打开官方 Inspector”调用公共接口 `EditorUtility.OpenPropertyEditor`，打开当前 PSD 主资源的独立属性窗口。Importer 选择、Settings、Layer Management 和 Apply/Revert 都由 Unity 官方窗口管理，工作台不持有官方 Editor，也不拦截其待应用设置。官方 Apply 后，工作台收到资源通知，刷新快照并重新叠加自己的显隐配置。

左侧 `Layers` 列将显示勾选放在节点行内，排列为“层级缩进 → 展开箭头／占位 → 勾选 → 文件夹图标 → 名称”，勾选随层级缩进，不再集中在最左侧独立列。它只控制工作台预览。文档根和组的勾选表示自身开关，没有后代汇总或混合状态。关闭父级保留子级组合，父级关闭期间仍可修改子级；受父级隐藏时仅名称和图标置灰，勾选保持清晰，悬停可查看原因。缺少独立 Sprite 的节点禁用操作，提示“未导入”或“已合并到组”。合并组作为一张 Sprite 控制，内部层不能独立显示。

勾选不改变当前选择。隐藏节点保留树选择，隐藏选框并退出画布命中；恢复显示后恢复选框。组边界取当前可见的导入后代并集。“重置显示”恢复全部开关，同时清除已经删除图层的残留配置。

## 分层

Core、Infrastructure、Presentation 三个正式程序集均限定 Editor 平台，不依赖运行时同步模块。所有文件位于本工具目录，不依赖旧 `PsdTools.Editor`。

- **Core / Domain**：不可变导入快照、像素矩形、独立显隐状态。源 `IsImported`、`IsVisible` 和层级不受勾选影响。
- **Core / Application**：`IPsdDocumentSource`、`IPsdVisibilityRepository` 端口及 `PsdSession`。会话负责父级门控、可见绘制集合、命中、边界和稳定 ID 选择，无 Unity 引用。
- **Infrastructure → Core**：官方 12.0.2 数据适配器、Sprite 解析、合并资源通知、显隐配置仓储。仅适配器了解官方序列化字段；不解析 `.meta` 或猜测名称。
- **Presentation → Core、Infrastructure**：窗口组装依赖；虚拟化树仅派发显隐命令，绑定使用稳定 ID 和 `SetValueWithoutNotify`；预览复用已有 Image/Sprite，只更新 display 和选框。Presentation 不直接引用官方 PSD 程序集。
- **历史实验**：邻字合成源码与资产迁入 `Tests/Editor/ArchivedGlyphUnion/`；原程序集身份仅为兼容历史资源保留，已限定 Editor。正式三层均不引用它，Player 中没有 Workbench 程序集。

导出描述与文本布局规划位于 Core / Application；源解析、导入缓存、字体解析及资产保存位于 Infrastructure，窗口仅组装用例、冻结显隐和显示结果。

## 配置与生命周期

项目配置为 `Assets/PsdUiWorkbench/Configs/<PSD_GUID>.asset`，保存 PSD GUID、文档开关和隐藏图层 ID。首次读取不创建配置；首次显隐修改才创建。配置支持 Undo/Redo；连续修改合并到约 0.3 秒后的保存，窗口关闭或脚本重载时立即写入待保存配置。Undo 首次修改会留下默认配置资产，便于 Redo。

新图层默认显示；删除图层的 ID 保留但不生效，重置时清除。PSD 重命名、移动和重导入均按 GUID + 图层 ID 恢复。选择与最近资源另存编辑器 SessionState，按项目路径和资源 GUID 隔离，在同一次 Editor 会话内重开窗口及脚本重载后恢复，不写入共享显隐配置。

加载、导入或数据失效时才重建快照和 Image。显隐操作不写 PSD、PSD `.meta`、Importer Override 或官方设置，不触发 PSD 重导入，不复制纹理、不读 CPU 像素、不修改 Read/Write。

## 限制与验证

矩形命中按最上层选择，不检查透明像素。仅预览官方已经导入的 Sprite，文字仍是 Sprite，不保证 Photoshop 全部效果还原。未导入或合并子层没有额外图像获取路径。PNG、UGUI Prefab、TMP、九宫格、去重和原稿对照留待后续阶段。

测试位于 `Tests/Editor/`，实际结果与截图见 [VALIDATION.md](VALIDATION.md)。

## 源图层类型识别与保留设置切换

类型扩展针对 Unity 6000.3.20f1 / 官方包 12.0.2。请先在 Project 中选择使用官方 PSDImporter 的 PSD/PSB，再运行：

- `Assets/PSD UI Workbench/保留设置切换到 Workbench Importer`
- 返回原版时使用 `Assets/PSD UI Workbench/保留设置切换到官方 PSDImporter`

**已有资源不要直接在 Inspector 下拉框切换 Importer 类型。** 实测 Unity 会重建托管导入设置并生成新的 Sprite ID；专用命令保存官方序列化字段、切换、恢复并重导入，再校验设置、Sprite ID/local file ID 和主资源类型，失败时尝试回退。未 Apply 的 Inspector 修改会阻止专用切换。该命令显式改变 Importer，会执行两次导入；普通预览和显隐不会导入。命令仅处理单个选中资源，不提供 Undo，不自动迁移项目资源。

菜单依赖 12.0.2 的官方序列化字段适配，版本升级必须复验。官方 Inspector 仍承担 Settings、Layer Management、Apply/Revert；派生类只用薄桥接 Editor 注册，不重写布局。

Core 的 `PsdNode.SourceKind` 表达 `Unknown / Group / Text / NonText`。Infrastructure 的独立 `PsdLayerMetadataReader.Read(Stream)` 只读取 8 位 PSD/PSB 图层记录，识别 lyid、lsct/lsdk、TySh/tySh；跳过像素。派生 Importer 调用官方导入后，检查本次主资源和纹理，再将结果保存为固定标识 `workbench.layerTypes` 的 Editor 子资源。它不额外创建配置文件，也不在导入回调中调用 AssetDatabase 导入或保存方法。

工作台仅消费导入缓存：文本显示 T，非文本显示图像符号，未知显示 ?，分组保留文件夹图标。NonText 不进一步区分形状、智能对象、调整层；预览仍使用官方 Sprite；派生 Importer 另缓存源文字与样式，供正式 TMP 导出使用。16/32 位、类型读取失败或源 ID 缺失/重复时显示未知和诊断，保留官方导入产物。原版 PSDImporter 保持原预览，非分组类型为未知；窗口不会额外扫描 PSD。

新增类型与显隐配置独立：未导入和合并子层可以识别源类型，但不因此获得独立 Sprite。最新验证见 `Documentation/layer-types-tests.json` 和 VALIDATION.md 的类型识别记录。

## 导出 UGUI Prefab

点击工具栏“导出 UGUI Prefab”，在 Assets 内选择保存位置。默认目录为 `Assets/PsdUiWorkbench/Exports/`。首版只新建，同名自动生成唯一文件名；取消对话框不创建资源。成功后状态栏显示实际路径、Image 数及跳过原因，并定位 Project 资产，不往当前场景添加对象。

输出为自带 Screen Space - Overlay Canvas、CanvasScaler、GraphicRaycaster 的 Prefab，下设固定 PSD 画布尺寸的 Content。CanvasScaler 参考分辨率等于源画布，Match Width Or Height=0.5，参考 PPU=100。Content 与 PSD 节点使用中心锚点和 pivot；图片使用 Simple Image、白色、关闭 preserveAspect/useSpriteMesh/raycastTarget，尺寸按 PSD 原始矩形显式设置。

导出包括所有可用 Sprite 和必要分组；具有受支持文本缓存的节点默认转 TMP，其余保留 Image。Content.activeSelf 保存文档开关，节点 activeSelf 保存自身开关，父级关闭不覆盖子级组合。布局边界取完整导入快照，不随显隐变化。未导入或已合并且没有独立 Sprite 的子层跳过，并报告原因。导出基于已应用结果，不包含官方 Inspector 待 Apply 的设置。

Core 的 `PsdPrefabPlanner.Build(document, visibility)` 产生不可变计划，负责节点筛选、组边界、局部坐标、绘制顺序和自身显隐；`PsdSession.CaptureVisibility()` 冻结显隐。Infrastructure 的 `PsdPrefabExporter.Export(plan, assetPath, resolveSprite)` 解析并检查持久化 Sprite，在 Preview Scene 内构建和保存，返回路径、数量及诊断。Presentation 只负责路径对话框、冻结输入和显示结果；UI 定时任务仅更新按钮可用状态，不轮询或重建 PSD 数据。

Preview Scene 没有屏幕尺寸，提前激活 Canvas 会导致 Unity 将受驱动根矩形保存为零缩放。本工具先在 inactive 根下构建，保存后仅将持久化 Prefab 根设为 active 并再次保存；Content 仍使用文档自身开关。保存后已验证根缩放为 1，重载后矩形正确。临时对象和 Preview Scene 在成功或失败时都释放。

原版和派生 PSDImporter 均可导出，不要求类型已识别。导出不切换 Importer、不写 PSD/.meta 或显隐配置，不生成 PNG 切图；Prefab 的 Sprite/纹理引用依赖源资源和稳定 Sprite ID。后续勾选不会自动修改已导出的 Prefab。

本轮不包含交互组件推断、EventSystem、AutoBind、布局组件、遮罩、屏幕适配和增量覆盖；重名节点保留源名称，首次导出不按名称匹配已有对象。参考分辨率之外的表现由 CanvasScaler 和实际场景决定，不保证任意屏幕比例自动适配。

## TMP 外描边验证

标准 Outline＋Face Dilate 的独立实验、跨字体公式、阴影联动与适用范围见 [TMP_OUTLINE_VALIDATION.md](Documentation/TMP_OUTLINE_VALIDATION.md)。已完成三个字体、两个标准 Shader 的 864 组矩阵与缩放/字重探针；细描边混色、邻字遮挡和距离场范围均有明确限制。该段记录早期独立实验；正式接入状态见下方“正式 TMP 导出”。

追加的 15 个字体文件／字面、3024 组矩阵与 SDF16、Sharpness 联合验证见 [扩展验证](Documentation/TMP_OUTLINE_EXTENDED_VALIDATION.md)。本轮确认公式无需按字体名称分别调系数，但 1 px 小字号细笔画仍有明显混色；Sharpness 会改变原始填充外观，不能作为默认无损修复。

## 实际 PSD 文本审计与占位验证

[战斗 PSD 文本审计](Documentation/PSD_TEXT_AUDIT.md) 已读取全部 20 层文字、样式段、变换和启用的效果。13 层 TOXIA 完成实际填充对照；另外 7 个缺字体段按已确认规则使用项目默认 TMP 字体、固定 36 UI 单位、普通字重、无描边/阴影，混排中的已有字体段继续正常处理。占位不参与原稿严格匹配统计。

该阶段的离线 JSON 继续仅用于 Tests/Editor 审计；正式导出已经改为消费 C# Importer 缓存。官方 Sprite 不含 Photoshop 图层效果，因此当前填充对照不能代替原稿描边验收。

## 可编辑 TMP 候选

已生成可在 Unity 中直接打开的 [TMP 候选 Prefab](<Exports/战斗主菜单 TMP候选 1/战斗-主菜单界面_TMP候选.prefab>)：19 个文本图层、27 个 TMP 组件、32 个 Image，其中 6 个缺字体段使用固定占位。新增独立 Core 文本模型/布局规划和 Infrastructure TMP 构建/保存模块；这是历史验证样板；当前窗口默认入口已接入正式文本缓存。

[候选说明、整图渲染及验收](Documentation/TMP_CANDIDATE_PREFAB.md) 记录了原生居中修正、字体/材质持久化、显隐、失败清理和当前限制。该样板仍使用离线审计入口，不是任意 PSD 的正式文本导出功能；每次生成创建唯一目录，不覆盖已有编辑。

## 正式 TMP 导出

工具栏“导出 UGUI Prefab”默认将受支持文本转成 TextMeshProUGUI。派生 Importer 在官方导入成功后保存 `workbench.text` 子资源；工作台打开、勾选、重绘及导出都不重新解析 PSD。源文字、逐段 Tracking、自动字偶距、字号、颜色、基线、对齐和外描边按图层 ID 缓存，解析异常只产生诊断，保留官方产物。原版 Importer 或缓存缺失时保留 Image，提示手动使用已有保留设置切换命令，不自动切换。

项目字体通过 TTF/OTF 的 PostScript name 精确匹配；重复名称不猜测。不扫描系统字体，不将近似字体冒充源字体。缺字体只替换对应段：项目默认 TMP 字库、固定 36、白色、普通字重、字距 0、无描边或阴影。已有字体段保留源样式。现阶段仅支持 8 位、72 DPI、正向均匀缩放的水平单行点文本，左对齐或居中，RGB 填充与正常混合单色外描边；其他活动效果和不支持的样式保留 Image，并显示原因。

当前交付 [TMP Prefab](<Exports/战斗-主菜单界面_TMP校正 1.prefab>)：19 个文本层、27 个 TMP、32 个 Image、6 个占位段。当前资源原本已使用派生 Importer，本次只更新其缓存，没有替换 Importer 身份。详细字距公式、98/100 视觉验收标准、验证结果及限制见 [正式接入说明](Documentation/TMP_EXPORT.md)。

字重与排版已增加原稿合成图的局部回归；不能把 AutoKerning 布尔值直接视作 TMP kern 模式。已保存源排版宽度，在已有字体完整时校验两种离散方案；当前 TOXIA 伪粗体起点改为 0.425，Tracking 和外描边补偿公式保留。原稿／修改前／修改后见 [发胖问题诊断](Documentation/TMP_STROKE_DIAGNOSIS.md)。

## 单 Pass PSD 外描边

点击“导出 UGUI Prefab”后选择“PSD 外描边（推荐，单 Pass）”，或“标准 TMP（兼容选项）”。默认 API 导出同样使用单字形外描边；不自动修改既有资产。使用 `TextMeshPro/Mobile/Distance Field - PSD Outer Stroke`，保持原有文字组件数量，不叠加文字层。填充与外描边覆盖分开计算，Face Dilate 默认为 0，并提供屏幕导数 AA。缺字体段仍按固定占位处理。

当前 [精简 Prefab](<Exports/战斗-主菜单界面_单字形共享材质.prefab>) 为 27 TMP、32 Image、14 个共享文字材质，无 Workbench 运行时组件或布局纹理。与此前单字形基线的整图 PNG 完全一致。该 PSD 的材质数没有进一步下降，不能据此宣称已有额外合批收益；新缓存规则已覆盖不同字号等情况下最终材质相同的复用。早期 Shader 原理和参数见 [说明](Documentation/TMP_OUTER_SHADER.md)。

## 邻字覆盖：静态单 Pass 原型

当前实验导出增加小额 advance 补偿，仅处理已有字体、单一样式、伪粗体文本且源宽度差异不超过 2% 的情况，源 Tracking、字号和缩放保持。[字形校验 Prefab](<Exports/战斗-主菜单界面_单Pass字形校验.prefab>) 可直接查看。

[邻字合成静态原型](<Tests/Editor/ArchivedGlyphUnion/Assets/单Pass邻字合成静态原型.prefab>) 已归档。使用 `TextMeshPro/Mobile/Distance Field - PSD Glyph Union Prototype`；早期验证证实部分样本的交界覆盖改善，但实际 PSD 中收益不明显，当前不再提供该导出模式。历史回归 63/63 的证据保留，不能视为当前方案的回归数量。

该原型尚未进入导出菜单：文字、字体或字距等排版变化后必须重新生成邻字表，不能直接用于动态计时器或数值。没有加入运行时同步脚本。额外布局查询、邻字采样与独立材质会增加成本，尚未做移动设备性能验收。详细架构、限制及截图见 [邻字合成验证](Documentation/TMP_GLYPH_UNION.md)。

## 动态邻字合成（已退役）

邻字合成的导出入口和正式接入已删除，相关代码、Shader、Prefab 与测试迁入 Editor 限定的 [历史实验目录](Tests/Editor/ArchivedGlyphUnion/README.md)。资源迁移保留 GUID；历史测试改为 Explicit，不进入普通回归。旧模式参数现在会被拒绝，不会静默生成另一种结果。

普通 TMP 自身即可运行时改字、换行和改变排版，不需要此同步器；也不再受邻字原型 32 个可见字形的限制。少数字形交界处的轻微覆盖差异被接受，整体偏胖继续从字体、伪粗体和描边参数核对，不在本轮盲目降低字重。

历史的 [事件、生命周期和验证说明](Documentation/TMP_GLYPH_UNION_RUNTIME.md) 留作调查记录。当前材质共享、资源成本与验收以 [TMP_SINGLE_GLYPH.md](Documentation/TMP_SINGLE_GLYPH.md) 为准，移动设备批次和 GPU 性能尚未实测。


## 可选字形局部补偿

导出菜单新增“PSD 外描边＋字形局部补偿（TOXIA，可选验证）”。仅对 TOXIA 使用独立字库与局部水平伪粗体，普通字重不受影响。保留普通 TMP、字距及共享材质；默认导出未变。[新 Prefab](<Exports/战斗-主菜单界面_字形局部补偿.prefab>)、[验证与实际截图](Documentation/TMP_GLYPH_SPACE_VERIFICATION.md)。这是当前字体的校准，不是所有字体通用公式；缺字体仍固定占位。


可选字形局部补偿已修正 `4` 顶部斜向缺口，新导出使用三次主图集采样及少量纵向补偿；旧材质表现保留。[细笔画修正 Prefab](<Exports/战斗-主菜单界面_细笔画修正.prefab>)、[实际对照与限制](Documentation/TMP_THIN_STROKE_REPAIR.md)。仍是单 Pass 及共享材质，GPU 成本未实测。
