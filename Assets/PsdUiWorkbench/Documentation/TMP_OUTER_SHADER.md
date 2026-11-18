# 单 Pass PSD 外描边：验证与导出入口

**2026-11-16 对照更新：** 以下截图保留历史记录；当前以 [Photoshop 原生 PNG](PSD_REFERENCE_CORRECTION.md) 独立对照，不再把去白底算法的一致性当作视觉还原证明。去白底候选仍有不透明颜色差异，Shader 与导出功能测试不因此作废。当前使用普通 TMP + 单字形外描边，未因本轮复核改变默认参数，也未宣称偏胖问题解决。

工作台“导出 UGUI Prefab”现在先显示两个选项：**标准 TMP**、**PSD 外描边（实验，单 Pass）**。两种方案都只读取已应用的导入结果，冻结独立显隐，新建唯一 Prefab；取消菜单或保存对话框不生成资源。标准方案保留原行为，实验方案需要明确选择，不迁移或覆盖已有输出。

当前交付：`Exports/战斗-主菜单界面_单Pass外描边验证 3.prefab`。共 19 个文本层、27 个 TMP、32 个 Image、59 个 Graphic、1 个 Canvas。其中 21 个已有字体文字段使用自定义 Shader，6 个缺字体占位段保留标准默认字体材质。没有叠加文字对象，没有新增工具运行时脚本。

## 来源与计算

Shader 名称为 `TextMeshPro/Mobile/Distance Field - PSD Outer Stroke`，文件为 `Shaders/TMP_SDF-Mobile-PSDOuterStroke.shader`。基于工程内 `Assets/Third Party/ComponentAutoBindTool/Example/TextMesh Pro/Shaders/TMP_SDF-Mobile.shader` 复制修改，原文件没有修改。对应 `TMPro_Properties.cginc` 一并复制进本工具，运行时不依赖旧 PSD 插件或其 Editor 程序集。

原 Shader SHA256：`44c39aabc7e88e7e1feffc1880df754f4adf86b62e9512aa89e9d2d65171aff4`。原 Properties include SHA256：`66db1f03e8d7a413eba79bcb6602fdb2de710b586f34a6f2584ed6f68f028e90`。

改动保留原有预乘 alpha、UI Stencil、RectMask2D、Alpha Clip 和渲染状态。当前启用的无阴影配置仍只有一个 Pass、一次图集采样；增加屏幕导数和覆盖合成的 ALU 运算，不增加文字绘制层数。尚未进行移动设备 GPU 时间验收，不能将“一个 Pass”当作零额外开销或整张 UI 只有一个 draw call。

填充和外描边分别计算覆盖率，再在同一片元内合成：

```text
F = 填充覆盖
O = 外轮廓覆盖
颜色 = 填充预乘颜色 × F + 描边预乘颜色 × max(O − F, 0)
```

填充边界不再为了描边增加 Face Dilate。实验导出使用 `FaceDilate=0`；字重和填充边缘过渡独立控制，描边只移动外侧阈值。屏幕导数 AA 根据距离场在屏幕上的梯度计算过渡宽度，可通过 `_PsdDerivativeAA` 对照关闭。

## 宽度与网格空间

设距离场 Gradient Scale 为 G、实际 glyph scale 为 S、R=(G−1)/G，源外描边为 P 个 UI 单位。新材质使用：

```text
_OutlineWidth = P / (G × S × R)
外轮廓阈值偏移 = _OutlineWidth × R × shaderScale / 2
```

完整宽度同时提供给 TMP 的网格 padding 计算。此前实验只为半边宽度预留网格，可能在较大描边处裁边；本轮已经修正。适配器在距离场余量不足时拒绝，不截断参数继续导出。手工将材质宽度调到超出图集 padding 的范围，也不能凭 Shader 恢复图集中不存在的数据。

`_PsdWidthConvention=1` 表示新的完整宽度约定；旧实验材质默认 0，保留旧宽度语义，避免加载新 Shader 后描边突然减半。该标识由导出适配器管理，不作为美术调参项。

## 参数与校准范围

- `_WeightBold`：实验导出起点为 **0.35**，标准方案仍为 **0.425**。这是 TOXIA 当前样本的联合验证起点，不是 Photoshop 全部字体的伪粗体通式。
- `_PsdFaceEdge`：填充边缘过渡，默认 1。
- `_PsdOutlineEdge`：外描边边缘过渡，默认 1。
- `_PsdDerivativeAA`：默认 1，启用屏幕导数 AA；设 0 可回到统一过渡尺度作对照。
- `_FaceDilate`：仅作为填充边界偏移，导出默认 0；不再承担外描边补偿。

直接把标准方案的 0.425 套入屏幕导数 AA，会增加接近纯填充色的像素，不能据此认为自定义 Shader 自动更准确。对 13 个已有字体样本比较 0.325／0.35／0.375 后，0.35 的平均绝对填充密度误差约 4.56%，最差约 10.90%。这是局部阈值统计，不是整图相似率，也不保证每个字形轮廓更接近。

原稿／标准方案／实验方案的 [标题对照](PsdTextAudit/StrokeDiagnosis/outer-final-title.png) 已查看，标题局部轮廓仍有可见差异，不能据此宣称“发胖已完全解决”或达到 98 分。原稿的两个缺字体标点与导出占位外观不同，按已确认规则不做原字体效果匹配。

## 分层接入

- Core 不感知 Shader 或 Unity 对象，文档与显隐配置格式不变。
- Infrastructure：`PsdTmpRenderingMode` 表示导出渲染策略；`PsdTmpOuterStroke.Configure` 负责参数范围、距离场余量和材质配置；`PsdTmpPrefabExporter` 管理自身新建材质；`PsdTextPrefabExport` 组织缓存和字体资源。
- Presentation：导出菜单传递模式、打开保存对话框、显示实验诊断。两种模式共用相同快照、层级、字距、选择和显隐规划。没有把 Shader 计算放进窗口。
- `Shaders/` 是运行时需要的资源依赖；三个 C# 程序集依然仅 Editor 平台，没有新增运行时程序集。

本轮还修复了初次 TMP 排版对共享字体材质的隐式写入：对象先保持 inactive，先提供同图集的独立度量材质，再赋字体。字体 setter 会立即初始化材质，不能等首次排版之后才克隆。度量材质成功或失败都会释放，不改项目默认字体材质。

## 验证与限制

完整回归 **55/55 通过**，无失败或跳过；包含标准／实验两种实际窗口导出入口、Prefab 重载、Sprite 引用与源文件不变、自己的显隐、默认材质隔离、失败清理和临时场景恢复。

独立数字 ID 1090 在 0／5 px 描边切换时，填充红色通道逐像素保持；5 px 目标的四边外扩落在 4～6 px 栅格容差内。RectMask2D 实际渲染检查左半字形保留、右半字形裁掉。Shader 编译及材质检查确认单 Pass，所有缺字体段继续固定占位。

**相邻字形仍是限制。** 一个 TMP 文本会由多个字形四边形依次绘制，较粗描边可能覆盖旁边字形的填充。独立字形的边界解耦不等于整行字形的填充并集完全保持；不能隐藏该差异，也不能仅靠调 RatioA 消除。当前未引入额外绘制层或 Pass，未宣称完成 Photoshop 的整层效果合成。

后续已独立完成 [邻字合成静态原型](TMP_GLYPH_UNION.md)，在不增加 Pass 或文字对象的前提下验证邻字覆盖合成。该实现需要随文字布局生成邻字表，暂未进入本节所述实验导出菜单，不能当作动态文字的完整解决方案。本节的相邻字形限制仍适用于原单字形 Shader。

像素验证为当前 Editor、当前参考分辨率和当前字体样本，尚未覆盖全部字体、非均匀缩放或移动端 GPU。活动阴影等尚未支持的源效果继续遵守原来的保留 Image 与诊断规则；没有因复制了 Shader 就新增效果导出能力。

证据：[完整测试](PsdTextAudit/StrokeDiagnosis/outer-acceptance-tests.json)、[最终输出报告](PsdTextAudit/StrokeDiagnosis/outer-final.json)、[校准数据](PsdTextAudit/StrokeDiagnosis/outer-calibration.json)、[整图](PsdTextAudit/StrokeDiagnosis/outer-final.png)。此前无数字后缀、后缀 1／2 的输出是中间实验，当前请使用本说明指向的后缀 3。
