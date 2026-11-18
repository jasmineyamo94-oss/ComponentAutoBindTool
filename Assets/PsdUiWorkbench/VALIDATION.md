# 独立显隐与外置 Inspector 验证记录

## 2026-11-16：字面与真实外描边联合验证

完成 13 个纯 TOXIA 图层、34～50px 字号、2～5px 外描边的原位对照。Photoshop 从磁盘隔离副本导出原缓存和效果，不重排文本、不安装字体、不修改用户当前文档。源 PSD/`.meta` 哈希、当前 Photoshop 文档状态检查通过，临时副本已清理。

首 H 在正式 Shader 中将描边从 0 切到原 3px，字面覆盖逐像素不变。剩余偏胖来自进入描边前的模拟粗体差异。网格 `boldStyle=0`、Shader 字重 0 的横向扩张候选，左右分配 0:100、25:75、50:50 分别使 7、10、13 个图层的字面与有效外轮廓原位 L1 均低于旧参数。不是视觉还原百分比，也没有推出所有字体公式；单向方案存在数字偏移反例。

Unity 定向探针回归 6/6 通过，新增开关描边不膨胀字面、保留原描边 Alpha=0.8、布局与共享资产不变、临时对象清理检查。编译成功，Console 序号 519 后无新增 error。这些文本没有启用投影，阴影不纳入验收。生产默认未变，仍需将实验屏幕像素量改为随字形/Canvas 缩放的表达。

说明与截图：[联合验证](Documentation/TMP_OUTLINE_BOLD_VERIFICATION.md)、[标题对照](Documentation/PsdTextAudit/StrokeDiagnosis/effects-title-review.png)、[完整数据](Documentation/PsdTextAudit/StrokeDiagnosis/effects-summary.json)、[测试原始记录](Documentation/PsdTextAudit/StrokeDiagnosis/effects-unity-tests.json)。

日期：2026-11-12。环境：Unity 6000.3.20f1、2D PSD Importer 12.0.2。通过 Unity CLI 连接当前工程的运行中 Editor 验证。

## 编译与自动化

- 最终编译状态：`compilationFailed=false`、`compiling=false`；最终 Console 当前错误数为 0。未清理历史日志；历史失败测试记录不代表最终状态。
- EditMode **13/13 通过**，无失败或跳过。原始结果见 `Documentation/visibility-tests.json`。
- Core 覆盖原位坐标、绘制顺序、重叠矩形命中、组边界、父级门控、父级关闭期间修改子开关、整张 PSD 显隐、隐藏选中节点、合并组不重复绘制和禁用合并子层。
- Core 覆盖重名、重排、图层改名、删除后清除选择、已删除隐藏 ID 暂留、相同 ID 再出现仍隐藏、新图层默认显示，以及重置清除残留配置。
- 临时副本集成验证：首次打开不生成配置；首次修改创建配置；Undo/Redo、重开恢复配置及选择、PSD 重命名、官方 Apply 后刷新并保留独立状态、重置均通过。临时 PSD、目录和配置均已删除。
- 对显隐、Undo/Redo、关闭与重开期间的 PSD 字节、PSD `.meta` 字节、Importer Override 和 Importer 完整序列化设置逐一对比，均未变化；监听 PSD 资源通知计数为 0。
- 实际 PointerDown/PointerUp 点击另一行的 Toggle 后，原行选择保持；虚拟化滚动遍历后再绑定仍正确，勾选恢复后已有 Image 实例全部复用。树与画布双向选择、隐藏选框及恢复选框均通过。
- 行内勾选调整后，13 项测试再次全部通过。实际控件验证唯一 Layers 列、勾选位于名称单元格首位、子层勾选的横坐标大于父层；点击组勾选不折叠组、不改变原行选择。父级关闭后子级勾选仍可点击，勾选 opacity=1、名称 opacity=0.5；恢复父级保留关闭期间修改的子级组合。Core、配置格式与官方 Inspector 接口未改动。
- 窄面板测试将树宽设为 240 像素、节点名称替换为 160 个字符，验证同级勾选对齐、勾选宽度保持 18 像素、名称收缩到单元格内并使用省略号。测试发现并修复 Label 默认不收缩导致的溢出；修复后最终 13/13 通过。

## 正常编辑器会话补验

EditMode Runner 中，原生 PropertyEditor 的跟踪器不保证创建官方 Editor，因此集成测试断言外置属性窗口已创建，并使用**由测试拥有的**官方 Editor 验证 Apply。生产 Workbench 不创建 Editor。外置窗口本身另在正常 Editor 会话实测：

- 使用战斗 PSD 临时副本，经工作台 `OpenOfficialInspector` 打开主资源，实际找到由原生窗口创建的 `UnityEditor.U2D.PSD.PSDImporterEditor`。
- 通过该原生窗口中的官方 Editor 将 Include Hidden Layers 应用为 true，工作台随后刷新为 69 个节点、`IsBusy=false`，仍选择图层 1091，且该节点保持工作台独立隐藏。
- 调用 `EditorUtility.RequestScriptReload` 后，实际恢复 69 个节点、选择 1091 和独立隐藏状态。
- 临时属性窗口、PSD 及其配置均已清理；最终工作台回到原始战斗 PSD。
- 连续创建并关闭 10 个工作台窗口后，PSD 资源通知订阅数仍为 1 → 1，未出现重复订阅或新增 Console 错误。

## 战斗 PSD 与截图

实际读取：1080×2400、69 个节点。Energy Level 的源矩形为 `(426,419,229,35)`，所有绘制节点均解析到 Sprite。

按源位置恢复上下栏、右侧图标和背包；分别在独立内存会话中隐藏与恢复分组，实际结果如下。坐标为 PSD 左上角原点，非 Mosaic 图集坐标。

| 分组 | 稳定图层 ID | 隐藏的 Sprite 数 | 原位边界 x,y,w,h |
| --- | --- | --- | --- |
| 上栏 | 1093 | 16 | 30,91,1040,369 |
| 下栏 | 1028 | 22 | 15,1619,1064,690 |
| 右侧图标 | 971 | 13 | 934,335,123,591 |
| 背包 | 980 | 2 | 15,2130,185,179 |

- `Documentation/workbench-visibility.png`：当前两栏布局，左侧显示勾选位于名称行内，随层级缩进，保留交替底色与图标；中间原位预览与 Energy Level 选框；顶部外置 Inspector、重置显示和适应窗口按钮。
- `Documentation/workbench-inline-narrow.png`：将树面板临时限定为 240 像素后的实际战斗 PSD 布局，节点缩进与勾选仍正常显示；截图后恢复面板原布局。
- `Documentation/external-inspector.png`：临时副本的原生属性窗口，Importer 选择、Settings / Layer Management、Include Hidden Layers、Apply/Revert 均由 Unity 官方面板提供。
- `Documentation/phase1-window.png` 为此前阶段历史截图，不代表当前布局。

## 限制

- 仅适配官方包 12.0.2；包升级需复核序列化字段。未导入或已合并的子层不提供额外图像获取路径。
- 显隐只控制当前可用 Sprite 的预览，不承担导出规则；文字仍显示为 Sprite，不保证 Photoshop 全部效果。
- 命中使用矩形，不判断透明像素；组全部后代隐藏后无选框。
- 配置首次修改 Undo 后保留默认配置资产，以支持 Redo。选择保存在 Editor SessionState，覆盖同次 Editor 会话中的重开与脚本重载，不承诺退出 Editor 后恢复。
- 临时副本导入会触发官方包对“.”图层名称规范化的警告，稳定映射使用 ID；不按名称匹配。其他项目既有警告未在本次修改范围内处理。
- 自动化覆盖事件分发及官方 SaveChanges；未逐个鼠标点击官方全部导入选项。

## 图层类型识别与派生 Importer（2026-11-12）

实现限定在本工具目录，保留三个 Editor 程序集；Core 新增不可变 SourceKind，Infrastructure 新增独立读取器、元数据子资源、派生 Importer/薄桥接 Editor 和专用切换模块，Presentation 只读取缓存显示类型。

### 兼容性发现与已授权调整

直接调用 SetImporterOverride 或使用原生下拉框切换不同 Importer 类型，会重建托管导入设置。临时副本实测 PPU 123 → 100、Sprite ID 变化；该路径不满足无损切换。用户随后明确选择增加专用保留设置命令。

专用命令针对官方 12.0.2 的 SerializeField 字段保存与恢复（排除瞬态 m_ImportData），保留 userData、AssetBundle 设置和外部对象映射；重导入后校验共同设置、Sprite ID/local file ID 和主资源类型，不一致时尝试回退。它只在用户执行 Assets 菜单时工作，未自动迁移当前战斗 PSD。使用原生下拉框直接切换已有资源仍有丢失设置/引用风险，README 和原版资源的工作台状态均已明确提示。

### 已通过项

- 战斗 PSD 源记录识别为 69 个有效节点，其中 20 个 Text、17 个 Group；Energy Level ID 1091、Backpack ID 979，以及未导入文本 ID 947 均按 ID 关联。分组结束记录不作为节点输出。
- PSD/PSB 内存夹具覆盖现代 TySh、旧版 tySh、常规图层区、全局 Layr、奇数块填充、重名、缺失/重复 ID、16/32 位不支持及逐字节截断。PixelGuard 在读取像素负载时主动报错，读取器仍成功，证明只定位跳过像素。
- 临时战斗副本通过专用命令往返切换：PPU=123、共同序列化设置、Sprite GUID/local file ID、主资源类型和已有序列化 Sprite 引用保持；重新导入时元数据子资源标识稳定；文件重命名后 ID 1091 类型仍正确。
- 最小 8 位 PSD 和 PSB 实际导入成功；修改源类型后重新导入，类型缓存更新，不沿用旧文本类型。
- 正常 Editor 会话实际创建 WorkbenchPsdImporterEditor，复用官方面板；Include Hidden Layers 的 Apply 以及未应用修改的 DiscardChanges/Revert 实测成功。Apply 后工作台刷新为 69 个节点、20 个 Text、选择仍为 1091；隐藏及恢复选中节点的选框联动正常。
- 新增类型图标保持箭头、勾选、图标和名称的层级布局；截图 `Documentation/workbench-layer-types.png` 使用验证副本，已检查 T 和非文本图像符号的实际渲染。
- 最终测试原始结果另存 `Documentation/layer-types-tests.json`。历史导入失败来自早期测试夹具中官方解析器不接受的奇数未知块长度，集成夹具已改用官方可接受的偶数负载；独立读取器仍保留奇数块填充测试。历史日志未清空。

### 范围和限制

仅支持类型识别，不解析文字、字体或排版，不转换 TMP。非文本不细分像素、形状、智能对象、调整层。16/32 位仅给出未知与诊断；不影响官方图像导入。仅派生 Importer 产出类型，原版资源非分组显示未知，工作台不扫描源文件。ID 缺失/重复时停止类型关联，避免官方基于名称修复 ID 后误配。

专用命令是新增的显式导入动作，会执行切换及恢复后的重导入；显隐操作继续不修改导入设置。切换不提供 Undo；回退仅在命令捕获异常时尝试执行，磁盘写入失败等异常情况下需根据错误报告检查资源。字段适配锁定 12.0.2，升级必须复验。

最终结果：EditMode **21/21 通过**，无失败或跳过；Unity 编译成功。类型测试、副本切换测试与既有显隐/窗口测试均纳入该次完整运行，详见原始 JSON 报告。

## UGUI Prefab 导出（2026-11-12）

### 已通过的验收

- 完整 EditMode 测试 **26/26 通过**，无失败或跳过；原始结果见 `Documentation/prefab-tests.json`。Core 测试覆盖嵌套坐标、半像素、完整组边界、父子自身显隐、合并组、跳过无图像节点及无法由层级表达的绘制顺序诊断。
- 实际战斗 PSD 导出 **51 个 Image＋17 个分组＝68 个 PSD 节点**，额外包含 Canvas 根和 Content；仅跳过未导入的 ID 947（10/10）。图像直接引用源 PSD 的 Sprite，文本保持 Image，无 PNG 切图产物。
- 导出 Prefab 重新加载后，逐一检查节点父级、同级顺序、Sprite 引用、尺寸、锚点/pivot、旋转、缩放及 activeSelf；将全部矩形换算回 PSD 左上角坐标，X/Y/宽/高误差均在 **0.01 UI 单位**以内。
- Canvas 为 Screen Space - Overlay，CanvasScaler 参考分辨率 1080×2400、Match=0.5、参考 PPU=100，带 GraphicRaycaster；未包含额外 Canvas、LayoutGroup、Mask 或 EventSystem。Image 为 Simple/白色，关闭 preserveAspect/useSpriteMesh/raycastTarget。
- 文档关闭时只有 Content inactive；关闭上栏组后保留 Energy Level 的 activeSelf=true，重新启用组后恢复有效显示。另验证自身关闭的子节点不被父级恢复覆盖。
- 同名导出产生唯一新路径，原 Prefab 内容哈希不变；缺失 Sprite 在构建前报错且不产生 Prefab；Assets 外路径被拒绝。用户场景根对象集合及 dirty 状态保持，Preview Scene 数量回到原值。
- 导出前后对比源 PSD、PSD .meta、Importer Override、完整 Importer 序列化设置及已有显隐配置文件哈希，全部保持不变。
- 正常 Editor 会话调用窗口实际导出入口成功，显示实际路径、51 个 Image 和 ID 947 跳过原因；没有将对象实例化到用户场景。保存对话框取消的路径由空返回直接结束，不创建导出目录或资产；未自动操作原生保存对话框。

### 视觉证据与实现修正

- `Documentation/prefab-workbench.png`：工作台预览和新增导出按钮，保存成功状态显示节点数量及跳过原因。
- `Documentation/prefab-render.png`：实际导出 Prefab 在 1080×2400 目标中的 UGUI 渲染，已检查上栏、右侧图标、下栏、背包位置及遮挡顺序。仅为验证截图；临时加载副本使用 Screen Space - Camera 渲染到离屏目标，未回写 Prefab，其保存模式仍为 Overlay。临时相机、纹理和加载场景均已释放。
- 初次验证发现 Preview Scene 没有屏幕尺寸，激活 Canvas 会使受驱动根矩形保存为零缩放；已改为 inactive 构建、保存后激活持久化根再保存。测试同时检查资产根缩放为 1、activeSelf=true，以及重载后的矩形，最终全部通过。历史失败测试日志未清空。

### 限制

首版仅新建，保留重名节点，不执行增量覆盖或手工修改合并。显隐为导出瞬间快照，之后工作台勾选不自动更新 Prefab。源 PSD 和 .meta 必须保留，不能直接切换 Importer 使 Sprite ID 重建。只保证参考画布中的原位还原，不保证任意屏幕比例或 Photoshop 效果；TMP、交互组件、AutoBind 和资源优化留待后续。

补充验证：在临时副本中分别使用官方 PSDImporter 与派生 WorkbenchPsdImporter 导出，并对两种结果执行同样的 Canvas、完整层级、坐标、显隐和引用检查，均通过。最终完整回归为 26/26。

## TMP 外描边补偿实验（2026-11-12）

完整回归 **31/31 通过**，无失败或跳过，含原有 26 项与新增 5 项真实 GPU 检查。新增项覆盖 0.5/1/2 倍均匀缩放、初始字重及 Face Dilate 导致 RatioA 变化时的通式补偿、超出距离场范围时拒绝。验证 Preview Scene 数量、活动场景和 RenderTexture.active 恢复。

另执行三个字体、两个标准 Shader 的 **864 组**离屏渲染矩阵。684 组可表达轮廓，其中 648 组阴影参数也在标准范围内；180 组轮廓超范围、36 组阴影超范围均被标记。分离字形且外扩 3/5 px 时，填充相对差异分别不超过 0.000139%/0%；1 px 仍有标准 Shader 混色残差，默认字距下大描边还可能覆盖邻字。未宣称全部字体或 Photoshop 像素级兼容。

公式、误差定义、限制、原始 JSON 与九张原生渲染对照见 [TMP_OUTLINE_VALIDATION.md](Documentation/TMP_OUTLINE_VALIDATION.md)。本轮仅增加验证代码和测试程序集 TMP 引用，正式导出、PSD 导入设置、已有字体及材质保持原有路径。Console 编译成功，无当前错误；本次 13 条警告均来自既有集成测试导入临时 PSD 副本时，官方 Importer 将名为 `.` 的源图层改为 `._`，不是 TMP 验证异常。未清除历史日志。没有将 TMP 转换接入正式 Prefab 导出。

## TMP 扩展字体及联合参数验证（2026-11-12）

追加 **15 个字体文件／字面、12 个家族、3024 组**，包含 SDFAA 主矩阵 2880 组、SDF16 对照 96 组、Sharpness 对照 48 组。主矩阵 1440 组参数可表达、1440 组超范围；没有将“执行成功”视为“视觉全部通过”。源文件哈希复核 15/15 一致，无回退字形、裁剪或空基准掩码。

原有公式未按字体增加经验系数。分离字形的可表达 2/3/5 px 样本最大填充相对差异约 0.000091%；1 px 在 Courier New 16 字号下增至约 37.619%。SDF16 没有普遍解决混色；Sharpness 虽能保持同样锐化后的基准，但相对原始填充最高改变约 20.627%/33.825%，不能默认当作无损补偿。思源宋体本机文件缺“鬱”的失败报告保留，改用其实际字符重测，未使用回退字库。

完整回归 **35/35 通过**，无失败或跳过；编译成功，当前 Console 错误为 0。新增代码仍只在 Tests/Editor，未接入正式 TMP 导出。完整范围、字体表、公式适用边界、15 张实际渲染对照和原始数据见 [扩展报告](Documentation/TMP_OUTLINE_EXTENDED_VALIDATION.md)。

## 战斗 PSD 实际文本及缺字体占位（2026-11-12）

全部 20 个文本层按源 ID 审计成功，读取文字、UTF-16 样式段、字体、字号、颜色、变换和启用的效果。13 层只用 TOXIA，完成 390 次填充／布局参数组合；5 层混排引用 Rubik-Medium，另 2 层引用江城圆体。按用户确认规则，这 7 个缺字体段使用项目默认 LiberationSans SDF、固定 36 UI 单位、普通字重、无描边或阴影；已有字体段保留自身样式，占位不做原稿匹配，不阻塞验证。

官方 12.0.2 忽略图层 Effects，因此其 Sprite 仅作填充基准，不能证明 PSD 外描边还原。实测填充相对覆盖差异的等权平均由默认候选约 64.0% 降至逐层有限网格最低候选约 27.6%，仍未达到像素级一致；没有用任意位移或拉伸掩盖差异，也未把候选参数接入正式导出。

Unity 完整回归 **36/36 通过**，Python 格式测试 **7/7 通过**。源 PSD、.meta、TOXIA、默认 TMP 字体的文件哈希保持；临时对象和场景状态恢复。原有 Inspector 测试的双 Editor 时序问题已在测试内修正，生产桥接代码不变。

详细清单、占位规则、参考基准、原始 JSON 与渲染证据见 [PSD_TEXT_AUDIT.md](Documentation/PSD_TEXT_AUDIT.md)。当前代码仍限于 Tests/Editor 和说明，正式 Prefab 导出继续使用 Image。

## 可编辑 TMP 候选 Prefab（2026-11-13）

新增 Core 不可变文本模型与布局规划，Infrastructure 标准 SDF 补偿、TMP 构建及持久化 Prefab 导出模块；验证入口继续留在 Tests/Editor，当前生产 Importer 不读取离线 JSON。窗口原 Image 导出入口没有切换。

实际生成 `Exports/战斗主菜单 TMP候选 1/战斗-主菜单界面_TMP候选.prefab`：19 个文本层、27 个 TMP、32 个 Image、17 个原始分组；6 个缺字体段保持默认字体、固定 36、无描边或阴影。未导入的 ID 947 仍跳过。所有文字字体与材质为持久化引用，新 TOXIA 字库包含 95 字形并转换为 Static；没有增加运行时工具脚本。

新增检查首先复现了居中时将末尾 Tracking 计入宽度导致的约 2.5 单位偏移，修正后与原生 TMP 居中在 0.0001 单位内一致。另修正文档和测试曾将计时器 Tracking=200 误写为全体 100 的假设，实际读取与导出保留源值。共享字库 boldSpacing 通过组件字距补偿，不修改默认字体。

完整回归 **39/39 通过**，无失败或跳过。重载检查包括基线/局部矩形、父子各自显隐、占位效果隔离、字体/材质引用、资源重名不覆盖、材质目录碰撞失败清理及临时场景恢复。PSD、.meta、默认字体及显隐配置保持。最终编译成功，Console 当前错误为 0；13 条警告来自既有临时 PSD 测试中的源层名 `.` 重命名，历史缓冲未清除。

整图 Camera 渲染已检查上下栏、右侧图标、背包与 Emergency；灰底仅用于离屏展示，未回写 Prefab。详见 [候选说明](Documentation/TMP_CANDIDATE_PREFAB.md)、[整图](Documentation/PsdTextAudit/tmp-candidate-render.png) 和 [原始测试结果](Documentation/PsdTextAudit/tmp-candidate-tests.json)。该候选仍不宣称 Photoshop 像素级一致，下一步需完成 C# 源文本读取与导入缓存后再接正式窗口。

## 正式 TMP 导出接入（2026-11-13）

窗口默认导出入口已由 Image 扩展为 TMP 缓存导出：C# 有界文本读取器由派生 Importer 在官方成功后生成 `workbench.text` 子资源。窗口和导出不解析源 PSD，不使用离线审计 JSON，也不自动切换 Importer。Core 保持不可变文字与逐段样式，Infrastructure 解析及保存，Presentation 只调用用例。原 Image 导出接口保留。

全部 20 个源文本层与独立审计逐项一致；正式交付 `Exports/战斗-主菜单界面_TMP 1.prefab` 包含 19 个文本层、27 个 TMP、32 个 Image，6 个缺字体段为默认字体、固定 36、无字距与效果占位。当前缓存包含逐段字号、Tracking、Auto Kerning、颜色、外描边、变换基线和对齐。项目字体按 PostScript name 精确匹配，不再寻找系统字体。

新增 10 项测试后完整回归 **49/49 通过**，无失败或跳过；Python 审计 **7/7 通过**。实际几何验证负字距、0、100、200 的 `Tracking × 字号 / 1000` 间隔变化，容差 0.0001；计时器 200 与其他段 100 分别保留。原居中末尾间距、混排占位、父子各自显隐、持久化引用与失败清理继续通过。临时副本验证正式窗口入口、文本缓存重导入／重命名稳定、Sprite local file ID 和源文件／默认材质不变，官方原版仍可导出 51 个 Image。

早期实现误判 TySh 内部最多 3 字节零填充，已修正；将 InvalidDataException 明确纳入文本诊断路径，避免文本错误影响官方结果。扩展 Importer 版本更新为 3，使早期失败缓存失效；文本缓存格式版本 2 包含 Kerning。历史错误记录未清除。

“98/100”定义为人工视觉验收目标，不是已测得的像素相似率。文字内容／布局／外观／间距按 30/35/25/10 分配权重，明显丢字、裁剪、错位、显隐或遮挡错误不能放行。实际 1080×2400 整图已查看；当前仅支持已验证的水平单行点文本和单色外描边，其他活动效果保留 Image 并诊断，未宣称全部 PSD 或字体达到 98 分。

完整说明及分层：[TMP_EXPORT.md](Documentation/TMP_EXPORT.md)。证据：[测试结果](Documentation/PsdTextAudit/tmp-export-tests.json)、[交付报告](Documentation/PsdTextAudit/tmp-export.json)、[实际渲染](Documentation/PsdTextAudit/tmp-export-render.png)。

## 文本发胖：使用 PSD 保存合成图回归（2026-11-13）

此前查看导出整图不足以确认字重。用户指出后，从原 PSD 只读提取保存的 RGBA 合成图，按相同位置与背景对照原稿、原导出及单变量版本。确认直接把源 AutoKerning 布尔值映射到 TMP kern 会将 Energy Level 填充宽度从 227 压到 207 px，填充密度高约 13.8%；单独清零 Dilate 则会使填充明显变细，不能作为修复。伪粗体 0.5 也使部分单独数字偏厚。

Core 新增不可变源排版框宽度；缓存格式为 3，派生 Importer 扩展版本为 4。构建时在字体完整的文字层比较两种离散字偶距方案，选择更接近源宽度的一种，不拉伸、不修改 Tracking 或源开关。当前 8 个文本层调整 kern。对 13 个已有字体样本共同验证后，TOXIA 伪粗体起点改为 0.425；标准 Outline + Face Dilate 联合补偿公式保持。

交付 `Exports/战斗-主菜单界面_TMP校正 1.prefab`，仍为 19 文本层、27 TMP、32 Image、6 个固定占位段。Energy Level 填充宽度为 227 px，密度约为原稿 97.9%；13 样本平均绝对密度差异约 4.74%，最差约 10.90%。这是局部回归指标，不是整图相似度或 98 分证明。

新增“应关闭／应保留 kern”的 Unity 用例先见失败，再修复；完整回归 **51/51 通过**，Python 审计 **7/7 通过**。局部 Python 回归可捕捉原导出的错误，也能捕捉仅修字距但未调字重的中间版本；最终全部通过。源 PSD 与 .meta 哈希仍与初始审计一致。已有输出保留，未覆盖手工资源。

[诊断与分层说明](Documentation/TMP_STROKE_DIAGNOSIS.md)、[三列截图](Documentation/PsdTextAudit/StrokeDiagnosis/final-comparison.png)、[实际最终整图](Documentation/PsdTextAudit/StrokeDiagnosis/final.png)、[51 项原始测试结果](Documentation/PsdTextAudit/StrokeDiagnosis/final-tests.json)。所有变更位于 PsdUiWorkbench 内，无运行时工具脚本。

## 单 Pass 自定义 Shader 接入与回归（2026-11-13）

基于官方 TMP Mobile Distance Field 的独立副本，保留后缀命名 `TextMeshPro/Mobile/Distance Field - PSD Outer Stroke`。新增显式导出渲染模式，工作台按钮提供标准／实验选择，不自动切换；Core 与文档配置不引用 Shader。实验仍为 27 TMP、32 Image、59 Graphic、1 Canvas，没有新运行时工具脚本或额外文字层。

完整回归 **55/55 通过**。覆盖独立字形 0→5 px 描边时填充保持、外扩 4～6 px 范围、实际 RectMask2D、单 Pass、标准与实验窗口入口、缺字体占位、引用／源 PSD／.meta／默认材质不变，以及清理。首次全量回归曾发现默认材质新增 CullMode，定位到字体 setter 的即时初始化；现已先提供独立材质再设置字体，并以缺失该序列化字段的克隆材质锁定回归。测试字体浅副本解除共享图集后才释放，不删除源图集。历史失败日志保留。

修正了自定义外描边只为半边宽度预留网格的问题；新宽度约定通过隐藏版本字段兼容旧实验材质。当前实验起点为 WeightBold=0.35 与屏幕导数 AA，基于 13 个已有字体样本联合比较；不作为所有字体的通式。标题轮廓仍未视觉验收，相邻字形的描边覆盖仍是单行组合限制，不能以单字形测试替代整行还原。

最终资产 `Exports/战斗-主菜单界面_单Pass外描边验证 3.prefab`。架构、公式、参数、来源指纹、限制与证据见 [TMP_OUTER_SHADER.md](Documentation/TMP_OUTER_SHADER.md)。

## 有界排版补偿与静态邻字合成（2026-11-13）

新增 Core advance 补偿及真实 TMP 测试，仅在实验模式、已有字体单一样式伪粗体、源宽度误差不超过 2% 时应用。8 个文本层获得补偿；保留源 Tracking、字号、缩放与缺字体占位。实际字符位置改善，但阈值掩码 IoU 有升有降，没有宣称所有字形轮廓均改善。横纵向加粗与其他 SDF 模式实验未获得统一改进，未引入默认方案。

新增独立静态 Shader 原型 `TextMeshPro/Mobile/Distance Field - PSD Glyph Union Prototype` 与 Editor 验证构建器。使用 Paragraph UV 对应 32×2 字形布局纹理，解决 Canvas 批处理下局部坐标不一致；邻字覆盖在单 Pass 内合成，没有新增文字对象或运行时脚本。Energy Level、Backpack 在 0→5 px 描边下，未合成基线分别有 17/5 个填充通道变化像素，原型均为 0。此项只证明样本的邻字覆盖修复，不等于原稿轮廓完全还原。

完整回归 **63/63 通过**；补强基线断言和布局采样精度后，原型两项检查再次 **2/2 通过**。静态 Prefab 重导入后确认 27 TMP、32 Image、59 Graphic、1 Canvas，21 个持久化邻字纹理、22 个文字材质，所有文字材质均单 Pass。没有 PsdUiWorkbench 运行时组件，临时资源由测试清理。修复了创建材质资产时临时纹理引用丢失的问题，登记纹理子资源后重新绑定并保存。

最终 Console 实时状态：compilationFailed=false、compiling=false、consoleErrors=0；历史日志未清除。源 PSD 与 .meta 的 SHA256 分别仍为 `96b36bb03aa75bdbad9198be777c63abbd25a9d4792bbe1f51a0c1a0f3d2d128`、`c8f36fef1431002e81b321f93d22271c67676cc62cf707691d29f573fcd4c1e8`，与初始审计一致。

交付 `Exports/单Pass邻字合成静态原型.prefab`，尚未接入生产导出菜单。文字或排版变化必须重建邻字数据，动态同步策略待确认；移动端 GPU 成本尚未验收。详细说明、对照截图与原始报告见 [TMP_GLYPH_UNION.md](Documentation/TMP_GLYPH_UNION.md)。

## 事件驱动的运行时邻字合成（2026-11-13）

用户确认允许轻量运行时组件后，新增独立 `PsdUiWorkbench.Runtime` 模块，只引用 TMP 和 uGUI，无 UnityEditor、PSD 包、Core 或旧插件依赖。原三层继续为 Editor。新增明确导出选项“PSD 邻字合成（实验，支持改字，单 Pass）”，不替换标准／原单字形模式。组件 Inspector 使用 UI Toolkit，通过 TMP 回调更新状态，不持续轮询。

正式窗口导出的 `Exports/战斗-主菜单界面_动态邻字合成.prefab` 包含 27 TMP、32 Image、6 占位段、21 个邻字组件。21 个已有字体段实际预览均成功启用，诊断为空，每段首次上传 1 次，文字材质全部单 Pass。保存的基础材质仍为持久化引用；激活后才创建 HideAndDontSave 实例材质和布局纹理，缺字体段没有组件。运行时编译程序集引用检查只有 Unity.TextMeshPro、UnityEngine.UI，ShaderHasError=false。

完整回归 **72/72 通过**，无失败／跳过。实际 Play Mode 确认帧数推进，在不调用 ForceMeshUpdate 的情况下多次改字及改变字号／字距，邻字计数自动更新；反复启停和销毁后释放自己的材质／纹理。GPU 比较先改变文本、字距与字号，再将运行时结果对照按新布局生成的静态结果，PNG 完全相同。对象复用、不变布局不上传、超长／多行／混合样式回退恢复、Stencil Mask、真实回退图集及源材质隔离均通过。

补充处理了 TMP 材质 setter 复制临时邻字表的问题：复用 TMP 缓存的副本但保留原基础材质恢复目标，禁用时解除纹理借用，副本由 TMP 自己管理。另在 72 项完整回归后补强同一帧 setter 后立即禁用、反复启停和 fontMaterial getter 复用路径，单独运行最终针对性检查；结果见下方原始记录。回退图集的子材质及其 Mask 材质同样清零邻字计数，避免残留旧表。测试浅克隆 FontAsset 的清理已解除共享图集和材质引用，不允许销毁源资产。

源 PSD 和 .meta 哈希仍与初始审计一致。实际整图已查看，保留字形局部轮廓差异、缺字体占位及原来的活动效果边界。超过容量、多材质、非邻接交叠等布局回退为单字形描边，不隐藏诊断；移动设备 GPU 性能与任意字体还原仍未验收。

[分层、生命周期和范围](Documentation/TMP_GLYPH_UNION_RUNTIME.md)、[完整回归](Documentation/PsdTextAudit/StrokeDiagnosis/union-runtime-tests.json)、[最终针对性检查](Documentation/PsdTextAudit/StrokeDiagnosis/union-runtime-final-checks.json)、[逐段输出](Documentation/PsdTextAudit/StrokeDiagnosis/union-runtime-output.json)、[整图](Documentation/PsdTextAudit/StrokeDiagnosis/union-runtime.png)。

最终针对性检查 **7/7 通过**。由于同一 Editor 的另一套 SceneFlow 回归重置了异步状态，改用 Editor 内逐项执行 NUnit 方法，每项独立 Preview Scene 与 SetUp／TearDown，原始报告明确标注；没有将异步 running 记录宣称通过，仅取消本轮自己残留的请求。最终 Console 实时编译正常且 consoleErrors=0，保留历史日志。

## 简化为普通 TMP 与单字形外描边（2026-11-13，当前方案）

用户确认邻字合成的实际视觉收益不足以承担组件、实例材质和布局纹理成本，正式导出改为普通 TMP + 单字形 PSD 外描边。菜单默认推荐 PSD 外描边，标准 TMP 保留兼容选项；三处默认导出 API 同步，邻字模式从菜单／枚举／构建器移除，旧数字值 2 明确拒绝。Core 没有修改。

MaterialStore 按字体身份和最终完整材质序列化属性复用，不再直接按源字号、Tracking 或伪粗体标记拆分。新 Prefab 测试验证不同字号／字距／粗体、最终材质相同可共享，填充色或描边不同仍分开；重载引用持久化，无 Workbench 运行时组件或布局纹理，不改默认字体材质。

`Exports/战斗-主菜单界面_单字形共享材质.prefab` 通过正式窗口默认接口生成：19 文本层、27 TMP、32 Image、6 缺字体占位、14 个共享文字材质。没有历史实验依赖，也没有 `_PsdGlyphData` 属性。原单字形基线也是 14 个材质，本次没有进一步减少该 PSD 的材质数；与该基线整图 PNG 字节完全一致，截图已查看。字体、字重和描边参数保留，未声称整体偏胖已解决。

邻字运行时源码、组件 Inspector、两个 Shader、静态／动态 Prefab 及所属资源通过 AssetDatabase.MoveAsset 移入 `Tests/Editor/ArchivedGlyphUnion/`，GUID 保留。原同步器程序集身份仅为历史引用兼容保留，已限定 Editor，组件从添加菜单隐藏，历史测试改为 Explicit。两个历史 Prefab MissingScripts=0，三个 ShaderHasError=false；Infrastructure、Presentation 不引用该程序集，Player 编译程序集没有 PsdUiWorkbench。正式新 Prefab 不依赖归档资源。

完整回归 **65/65 通过**，无失败／跳过；历史 Explicit 实验不计入。新增真实 Play Mode 验证帧数推进，不调用 ForceMeshUpdate，连续短文本、60 字符长文本和换行均正常，材质引用保持不变，没有附加同步器。现有导入器缓存、重导入、重命名、Sprite ID、显隐与 Prefab 引用继续通过。测试临时副本已清理；源 PSD 与 .meta SHA256 仍分别为 `96b36bb03aa75bdbad9198be777c63abbd25a9d4792bbe1f51a0c1a0f3d2d128`、`c8f36fef1431002e81b321f93d22271c67676cc62cf707691d29f573fcd4c1e8`。

明确接受少数字形交界处的轻微覆盖，不做像素级整层合成。新增材质复用仅在导出时比较，不增加运行时工作；业务调用 TMP 的材质实例化 API 仍可能产生副本。移动端 UI 批次和 GPU 性能尚未实测，不能用组件／纹理减少的数量换算帧率收益。

[当前说明](Documentation/TMP_SINGLE_GLYPH.md)、[完整测试](Documentation/PsdTextAudit/StrokeDiagnosis/single-glyph-tests.json)、[实际输出](Documentation/PsdTextAudit/StrokeDiagnosis/single-glyph-output.json)、[最终检查](Documentation/PsdTextAudit/StrokeDiagnosis/single-glyph-final.json)、[实际整图](Documentation/PsdTextAudit/StrokeDiagnosis/single-glyph.png)。

最终 Console 检查：compilationFailed=false、compiling=false、consoleErrors=0，保留警告与历史日志。警告已分类为截断夹具的预期文本诊断、官方原稿 `.` 图层名提示，以及本轮 4 个截图／JSON 证据在测试期间完成登记引起的新增文件提示；这些交付证据明确保留，不删除来隐藏警告。临时 PSD／Prefab 资源没有遗留。[Console 原始记录](Documentation/PsdTextAudit/StrokeDiagnosis/single-glyph-console.json)。

## 保存预览去白底与视觉探针（2026-11-14）

本节保留历史验证；当时仅凭通道分析及公式一致性就判定旧视觉基准错误，证据不足，该定性已撤回。实际 Photoshop 独立输出的复核见下一节。

当时新增白底反合成及保存预览读取器，仅 Tests/Editor 离线验收使用，不进入 Importer／预览／导出路径；生产三层、Shader、默认字重和描边未改。PSD/PSB raw/RLE、8 位 RGB 与明确合成透明度的测试通过；重新从当前 PSD 解码并反合成，与历史原始通道 PNG 经同样处理后的像素完全一致。这验证了解码和处理一致性，不是独立 Photoshop 视觉证据。旧原图及 `psd-saved-composite-unmatted.png` 候选均保留。

17 组材质／位置探针没有产生足够一致的全局改进，仍保留字重 0.35、FaceDilate 0、AA 边缘 1。Hell 的近黑像素参考在原始通道与去白底候选中分别为 1029、1179，当时 TMP 为 1162，对应数量比 112.9% 和 98.6%；缩描边 10% 的探针相对去白底候选为 86.7%，触发当时的数量回归断言。这些比例不是整体相似率，也不足以单独拒绝视觉方案或否定偏胖观察。

当时 Python **13/13**、Unity **66/66** 通过，无失败／跳过。Unity 用例验证无参数探针与正式渲染 PNG 完全一致、负面对照确实变化、源共享材质不变及临时场景恢复。相对去白底候选，Energy Level 行宽约 100.4%、密度约 96.7%；13 个已有字体样本平均绝对密度差异约 4.66%，最差 10.90%。这些是历史局部指标，不作为本轮视觉通过结论。17 组度量和选中截图保留，其余当轮临时探针图已清理。

源 PSD 与 .meta SHA256 仍为 `96b36bb03aa75bdbad9198be777c63abbd25a9d4792bbe1f51a0c1a0f3d2d128`、`c8f36fef1431002e81b321f93d22271c67676cc62cf707691d29f573fcd4c1e8`。编译正常，Console 实时错误 0；原有警告和历史日志未清除。不实测移动端性能。

[当前基准说明](Documentation/PSD_REFERENCE_CORRECTION.md)、[历史 Unity 回归](Documentation/PsdTextAudit/StrokeDiagnosis/appearance-unity-tests.json)、[历史 Python 记录](Documentation/PsdTextAudit/StrokeDiagnosis/appearance-python-tests.txt)、[去白底候选的探针数据](Documentation/PsdTextAudit/StrokeDiagnosis/appearance-probes-unmatted.json)。

## Photoshop 独立基准与视觉复核（2026-11-16）

通过本机 Photoshop 22.0 官方 COM／JavaScript 接口，从当前已保存的项目 PSD 导出独立 PNG。源文档为 1080×2400、72 dpi、sRGB；只保存临时副本并关闭副本，源 PSD 和 `.meta` 导出前后哈希与上述基线一致。预先合并副本和保留图层副本两种导出路径的 PNG 全图 RGBA 完全一致，排除先合并副本引起本次差异。

原生 PNG、历史原始通道、去白底候选的 Alpha 全图一致。标题区域 1498 个半透明像素，原生 PNG 与去白底候选 RGB 完全一致；但还有 1464 个不透明像素的颜色通道差异大于 5，最大 32，原因尚未定位。因此后续直接采用原生 PNG，不再将候选称为与 Photoshop 全等，也不依据黑边面积否定用户看到的局部偏胖。

离线检查默认读取原生 PNG，普通 PNG 不自动反合成；支持显式 `--reference`，输出参考路径、SHA256 及处理方式。两侧统一合成背景，验证尺寸一致；位置诊断使用零填充而非循环平移。`verify_photoshop_reference.py` 核验两份原生 PNG、导出记录及当前源 PSD／`.meta` 哈希。

本轮 Python 全量 **19/19**、Unity 定向 `PsdTextAppearanceTests` **1/1** 通过。重新捕获 `photoshop-current-tmp.png`，与历史 `appearance-base.png` 的全图 RGBA 完全一致。Unity Refresh 后 compiling=false、compilationFailed=false、ShaderHasError=false；本轮 Console 检查起点之后无新增错误，两条 2026-11-14 的 `UnityConnect Token Exchange` 旧错误仍保留，没有清空 Console，不将总错误数报告为零。

以原生 PNG 重新比较既有探针：Hell 默认填充像素数量比约 1.023174、近黑数量比 0.957955、原位填充 IoU 0.774015；降低字重至 0.325 后数量比更接近 1，但原位填充 IoU 降至 0.767514；描边缩至 90% 后近黑数量比降至 0.842539。指标之间没有一致改善，本轮生产代码、Shader、正式 Prefab 和参数均未修改，仍需处理局部轮廓差异。上述数值不是还原率，未宣称达到 98% 或适配所有字体；暂不实测移动端性能。

[独立基准说明](Documentation/PSD_REFERENCE_CORRECTION.md)、[原生与当前 TMP 对照](Documentation/PsdTextAudit/StrokeDiagnosis/photoshop-vs-current-tmp.png)、[参考与哈希核验](Documentation/PsdTextAudit/StrokeDiagnosis/photoshop-reference-verification.json)、[Python 原始记录](Documentation/PsdTextAudit/StrokeDiagnosis/photoshop-reference-python-tests.txt)、[Unity 定向回归](Documentation/PsdTextAudit/StrokeDiagnosis/photoshop-reference-unity-tests.json)、[探针新统计](Documentation/PsdTextAudit/StrokeDiagnosis/photoshop-existing-probes.json)、[Energy Level 检查](Documentation/PsdTextAudit/StrokeDiagnosis/photoshop-energy-check.jsonl)。

## 无描边字面与模拟粗体 A/B（2026-11-16）

在 Photoshop 临时副本中按 ID 隔离 1057／1058，分别导出保留缓存的粗体字面、重新生成的普通字面、模拟粗体字面，以及保留原效果的重绘版本。有效对照保持源 48 字号、Tracking 100 和文字起点；TMP 探针关闭全部描边／阴影，使用黑底白字，只改变 Shader 权重 0／0.35，保持正式排版。整段普通字重仍含 Photoshop／TMP 后续 advance 差异，字形结论主要来自固定首 H 窗口。

首 H 覆盖量：Photoshop 普通 555.133、粗体 666.667；TMP 真正 `FontStyles.Normal` 为 550.337、Bold 权重 0.35 为 673.396。真正普通 H 原位 IoU 为 0.9232、最佳位移[0,0]、50%包围盒与PS相同；粗体原位IoU为0.7986。真正正常→粗体增量为PS111.533、TMP123.059，约多10.3%，不是整体还原率。粗体50%包围盒中TMP还向左扩一像素，需进一步核对Bold网格／UV与增粗分布，不宣称已反推出Photoshop算法。

补充控制确认，固定 Bold 样式仅将 Shader 权重归零得到508.494，与真正Normal不同；origin／baseline虽然一致，quad纵向范围仍不同。旧164.902增量及约47.8%的比较使用了不等价零点，已撤回其作为真实增粗强度的解释。1057／1058报告和面板已更新为`formatVersion=2`，有效A/B使用真正Normal，固定Bold权重零另列。

Photoshop 缓存与重绘粗体的首 H 逐像素一致，整段宽度／后续字符位置变化不能用来证明字体版本不匹配。临时 48 采样 `SDFAA_HINTED` 将首 H 权重零／0.35 探针的覆盖 L1 残差从 88.53／156.11 降至 71.42／132.14；首点整体对齐修正均为零，未逐字平移或拉伸。权重零控制存在上述样式差异，该候选尚未验证全字符和多字号，不修改正式字库或参数。

用户授权临时安装已有 TOXIA 字体后移除。本轮仅将项目原字体绝对路径注册到 HKCU Fonts，不复制、下载或改写字体；实际新文字和导出探针确认 `fontAvailable=true`、字体为 TOXIA。各轮脚本在 finally 清理临时副本并撤销注册；最终只读复核确认临时注册不存在，源 PSD、`.meta` 和字体 SHA256 与基线一致。Photoshop 当前文档为未保存状态，实验不保存、关闭或恢复用户文档；实验 PNG 来自早先已验证副本，不宣称最终 saved=true。

新增 Unity `PsdTextFillProbeTests` **4/4 通过**，覆盖权重改变真实渲染且固定排版、临时 Font 清理与首字符原点对齐、方向性 Shader 零值等价及非零覆盖变化、Character／Word 独立 advance 增量且不缩放字形，同时验证源材质／Prefab 不变与临时场景恢复。ID 1091 缩放文字切换粗体触发位置／字号／Tracking 护栏，未纳入有效 A/B；后续需扩展保留 transform 的操作再验证。所有新代码限于 `Tests/Editor`，生产三层、Shader、正式 Prefab 和默认值未改，局部偏胖尚未宣布解决，未实测移动端性能。

最终 Unity 检查 `compilationFailed=false`；正式外描边 Shader 和方向性实验 Shader 均 `ShaderHasError=false`。Console 检查游标 507–519 无新增 Error，不将历史 Console 总错误数报告为零。源文件哈希、临时字体注册及测试汇总见 [最终只读复核](Documentation/PsdTextAudit/StrokeDiagnosis/fill-session-verification.json)。

随后增加单 Pass、一次图集采样的方向性增粗实验 Shader，保留在 `Tests/Editor/Shaders/`。临时字体 mesh boldStyle 与 Shader 权重均归零，单独验证屏幕方向增粗 `X=1.5、Y=0`。首 H 覆盖 L1 残差由156.11降至53.25，原位50%掩码IoU由0.7986升至0.9333；同一参数在标题1057交叉检查中，L1由739.13降至414.66、IoU由0.8189升至0.8928。标题总覆盖量仍低于参考，不能只按局部指标宣布全部字形或效果还原，也不能把0.9333称为93.33%视觉还原。当前正式Shader与导出默认值未替换。

[诊断说明](Documentation/TMP_FILL_DIAGNOSIS.md)、[首 H 对照](Documentation/PsdTextAudit/StrokeDiagnosis/fill-h-ab-summary.png)、[Hell 数据](Documentation/PsdTextAudit/StrokeDiagnosis/fill-comparison-1058.json)、[标题数据](Documentation/PsdTextAudit/StrokeDiagnosis/fill-comparison-1057.json)、[字体候选](Documentation/PsdTextAudit/StrokeDiagnosis/fill-font-candidates.json)、[Unity 原始记录](Documentation/PsdTextAudit/StrokeDiagnosis/fill-unity-tests.json)。

[方向性候选](Documentation/PsdTextAudit/StrokeDiagnosis/fill-directional-candidates.json)、[标题交叉检查](Documentation/PsdTextAudit/StrokeDiagnosis/fill-title-directional-metrics.json)、[标题方向性对照](Documentation/PsdTextAudit/StrokeDiagnosis/fill-title-directional-review.png)。

### Normal 源 Tracking 与五项间距参数

新增独立 `CaptureSpacing` 和 `compare_psd_spacing_probe.py`，保持 TOXIA 正式图集、48 字号、真正 Normal、无描边／阴影以及源文本空格。Character=`Tracking/10=10`，Word／Line／Paragraph=0，Character Horizontal Scale=1；首字符 origin／baseline 守卫通过，不整体移动、拉伸或拟合总宽度。旧 Normal 的 Character 为 1057：5.777372、1058：5.990467，仍继承旧 Bold 字距补偿，不能用其整段误差直接判断基础字形。

原位 IoU@0.5：标题 1057 从旧 Normal 的 0.241071 提高到源 Tracking 的 0.514538；Hell 1058 从 0.568519 提高到 0.801834。开启 kern 后分别降为 0.252254、0.407735，因此当前 TOXIA 样本基线保持关闭；不泛化为所有字体都关闭 kerning。这些指标用于布局诊断，不是 51%／80% 视觉还原率。

Character 与 Word 各增加 1 的独立测试，在 48 字号下分别按字符／空格累计增加 0.48 UI 单位 advance，保持字形尺寸、首点、字号及纵向位置。本轮单行样本未验证 Line／Paragraph；Horizontal Scale 固定为 1，不用横向压缩掩盖粗细。当前结果只支持“首 H 普通字形较接近、恢复源 Tracking 后布局改善”，仍有局部约 1–4 像素的位置与边缘差异，不宣称无效果整段基本完全一致或达到 98%。

关闭图层效果不等于关闭模拟粗体：原 PSD 文本 `fauxBold=true`，即使没有描边／阴影，粗体字面仍不一致；“普通首 H 较接近”要求两侧连模拟粗体也关闭。间距改善只处理字符位置，不能修复单字内部增粗分布。最终 [同一窗口 2× 三行对照](Documentation/PsdTextAudit/StrokeDiagnosis/fill-spacing-review.png) 已进行人工视觉复核。

后续优先独立校准 Character／Word，再补多行／多段落验证；方向性增粗冻结为原型，生产导出参数尚未改动。

[标题间距记录](Documentation/PsdTextAudit/StrokeDiagnosis/fill-spacing-comparison-1057.json)、[标题 1×](Documentation/PsdTextAudit/StrokeDiagnosis/fill-spacing-comparison-1057-1x.png)、[标题 4×](Documentation/PsdTextAudit/StrokeDiagnosis/fill-spacing-comparison-1057-4x.png)、[Hell 间距记录](Documentation/PsdTextAudit/StrokeDiagnosis/fill-spacing-comparison-1058.json)、[Hell 1×](Documentation/PsdTextAudit/StrokeDiagnosis/fill-spacing-comparison-1058-1x.png)、[Hell 4×](Documentation/PsdTextAudit/StrokeDiagnosis/fill-spacing-comparison-1058-4x.png)。


## 2026-11-16：可选字形局部补偿导出

新增渲染及导出测试 3/3、原 TMP 导出回归 3/3、填充诊断回归 6/6、共享材质及旧接口回归 3/3，共 15 项；C# 编译及 Shader 检查通过。新 Prefab 为 27 TMP、32 Image、14 个共享材质；源 PSD/meta、旧字体/材质/Prefab 和显隐配置不变。已检查实际 Prefab 截图，字重改善仍有局部差异；仅校准 TOXIA，不宣称像素一致或跨字体通用。详见 [记录](Documentation/TMP_GLYPH_SPACE_VERIFICATION.md)。移动端及 Player 未实测。


## 2026-11-16：数字 4 顶部缺口

新增可复现缺口测试，确认原版本在 PSD 像素 (433,106) 字面覆盖为 0；修正后为 0.675，相邻两个验证点为 1。缺口及描边隔离 2/2、可选导出 3/3、默认共享与接口 3/3，共 8 项通过。实际新 Prefab 截图与候选相同。13 层中 12 层综合差异下降，小号 4 略有退步。主图集采样增加到三次，未实测移动端。详见 [修正记录](Documentation/TMP_THIN_STROKE_REPAIR.md)。
