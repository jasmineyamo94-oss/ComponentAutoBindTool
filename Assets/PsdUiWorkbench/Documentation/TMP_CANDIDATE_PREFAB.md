# 可编辑 TMP 候选 Prefab

本阶段把已有验证推进到实际 Prefab：**19 个已导入文本层、27 个 TextMeshProUGUI 组件、32 个 Image、17 个原始分组**。4 个已导入混排文本各拆为三个文字段；两层纯缺字体文字使用默认占位，共 6 个导出占位段。源 ID 947 未导入，仍按原导出计划跳过。

交付资产：`Assets/PsdUiWorkbench/Exports/战斗主菜单 TMP候选 1/战斗-主菜单界面_TMP候选.prefab`。可以在 Project 双击打开，编辑子节点 Text 或 Placeholder 的文字；没有向用户场景添加实例。每次生成使用唯一目录，保留已有输出。

本文记录早期离线样板。当前正式窗口已接入 C# 文本缓存，见 [正式接入说明](TMP_EXPORT.md)；本文样板与 JSON 入口仍只用于回归对照。

## 整图证据

[1080×2400 候选渲染](PsdTextAudit/tmp-candidate-render.png) 已检查上栏、右侧装备图标、Energy Level、背包与 Emergency 区域。截图在独立 Preview Scene 中临时改用 Camera Canvas 输出，灰底用于展示；没有回写 Prefab，其保存模式仍为 Screen Space - Overlay。

[输出报告](PsdTextAudit/tmp-candidate.json) 保存实际路径及源文件一致性检查；[完整回归结果](PsdTextAudit/tmp-candidate-tests.json) 为 39/39 通过，无失败或跳过。早期未校正的候选保留在无数字后缀目录，当前请以本说明和输出报告指向的 `TMP候选 1` 为准。

## 分层与模块

所有新模块均位于原工作台目录，仍只有三个 Editor 程序集；不依赖旧插件，不增加运行时脚本。

- **Core / Domain / PsdTextLayout.cs**：不可变文字、样式、UTF-16 段范围和布局计划。保留源图层 ID、字号、Tracking、颜色和单色外描边。段必须连续覆盖文字，不能拆开代理对。本轮契约仅覆盖单行点文本。
- **Core / Application / PsdTextLayoutPlanner.cs**：将 PSD 基线转换到文字层容器的局部坐标；按可用字体集合决定是否占位。缺字体仅替换该段：默认字体标识、固定 36 UI 单位、白色普通字重、字距 0、无描边或阴影；源数据保持不可变。
- **Infrastructure / PsdTmpTextBuilder.cs**：接收布局计划与字体/材质解析函数，建立实际 TMP 子节点。显式关闭 AutoSize、换行、富文本和 raycast；基线与对齐由文字度量定位，不拉伸字形填满 Sprite 边界。
- **Infrastructure / TmpOutlineCompensation.cs**：集中维护标准 SDF 的 Outline／Face Dilate 解析解、参数范围和最终方程检查。原 GPU 验证器已改为调用该模块，避免两份公式各自演变。
- **Infrastructure / PsdTmpPrefabExporter.cs**：复用既有图片导出计划，先按图层 ID/父级/同级索引映射节点，再替换有文本计划的 Image。管理持久化材质、失败清理、原有显隐、独立场景保存；不依赖离线 JSON 或 Python。
- **Tests / Editor / PsdTmpCandidateExport.cs**：当前战斗 PSD 的试验协调入口。验证审计源哈希，把 JSON 转成 Core 模型，创建独立静态字库，调用上述通用模块并生成证据。Python 只用于前置审计，不进入 Prefab 或运行时。

Presentation 没有读取源文本结构或加入排版逻辑。正式接入已由 Infrastructure 提供 C# 元数据读取与导入缓存，再由独立导出入口组装用例；不让窗口读取验证报告，也不把样板路径写入生产模块。

## 字距、基线及描边

候选统一使用先前真实文本实验的公共起点：Shader WeightBold=0.5、额外粗体间距 2.5%、关闭 kern；没有逐层拟合参数或自由调整 X/Y。它们是当前 TOXIA 的候选设置，不是 Photoshop 全部字体的通用伪粗体公式。

共享 Font Asset 保持原有 boldSpacing。组件使用：

```text
characterSpacing = Tracking / 10 + (2.5 - FontAsset.boldSpacing)
```

因此 TMP 在 Bold 模式额外加入字库 boldSpacing 后，总间距仍为源 Tracking/10＋2.5。没有为了改字距而修改项目默认字体。Tracking=100 的段总值为 12.5%；ID 1067 的 Tracking=200，总值为 22.5%，没有被统一成 100。

居中宽度使用实际字形 advance，并排除最后字符后的一次额外间距。新增独立对照测试先用 TMP 原生 Baseline 居中复现了 40 字号下约 2.5 UI 单位的偏移，修正后与原生对齐在 0.0001 单位内一致。跨段之间仍保留应有的间距，文字内容中的空格不删减。

基线从 PSD 左上角坐标转换为容器中心局部坐标：X=源基线X−容器中心X，Y=容器中心Y−源基线Y，保留小数。TMP 排版需要有效 Canvas，因此导出在独立场景中暂时关闭 Canvas 驱动、激活原节点完成度量，再恢复父子各自的 activeSelf；没有将有效可见性覆盖到子层自身开关。

外描边继续使用标准 SDF 解析补偿。材质按字体、实际字形缩放、颜色和描边样式缓存为独立资源，默认占位材质单独清理效果，不能继承邻接 TOXIA 的描边。距离场不足时停止导出，不强制截断参数后保存空白或不完整结果。

## 资源与运行时范围

候选字体为新生成的静态 TOXIA 字库，包含当前源字符与字体可用的 ASCII，共 95 个字形，使用 2048×2048 Alpha8 图集；不在运行时增加源字形。文字可编辑，但新增非 ASCII 字符的字库覆盖不在本轮验收范围。

占位引用项目现有默认 `LiberationSans SDF`，只新建干净的材质，不改默认字体及其原始材质。非文本图片仍引用原 PSD Sprite。运行时仅需要标准 Canvas、uGUI、TMP、字体和材质资源；没有 PsdUiWorkbench 的 MonoBehaviour、轮询、读像素或额外 Canvas。

当前候选共有 59 个 Graphic、14 种 TMP 材质、1 个 Canvas。新字体图集在 Editor 的纹理内存计数约 8 MiB（包含 Editor 中可读纹理的成本），这不是手机 GPU 内存验收结果。

静态字库不等于已完成移动端优化。当前图集大小和预设数量以验证为先，尚未进行设备上的内存、批次与 UI 重建性能验收。候选渲染仍有字体光栅化、抗锯齿和原稿效果差异，不宣称 Photoshop 像素级还原。

## 验收

- 完整 Editor 测试 39/39，通过既有导入、显隐、类型、图片导出和 GPU 验证回归。
- Core 覆盖分数基线、逐段占位、源数据不变、错误段范围及代理对截断。
- Prefab 重载检查 Canvas 根缩放、固定容器矩形、原始父子关系和 activeSelf；关闭 Content/父级时子节点仍保持自己的组合。
- TMP 字体和材质均为持久化引用，图集非空；占位固定 36、普通字重、无描边/阴影；TOXIA 段仍保留候选描边。
- 原生 TMP 居中对照通过；计时器 Tracking=200 被保留。
- 同名输出产生新路径；已存在材质目录导致失败时保留原目录，只清理本次新建的 Prefab；场景与 Preview Scene 数量恢复。
- PSD、`.meta`、项目默认 TMP 字体和显隐配置哈希保持。新字体为 Static，sourceFontFile 运行时引用为空；没有增加运行时工具程序集。

复现当前样板：通过 Unity CLI script eval 执行 `return PsdUiWorkbench.Tests.PsdTmpCandidateExport.Run();`。函数会新建唯一目录，不覆盖人工编辑资源。审计数据与源文件哈希不一致时先重做只读审计；这种输入方式仅属于验证入口。
