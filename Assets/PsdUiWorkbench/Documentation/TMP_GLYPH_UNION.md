# 邻字合成与 advance 校验

**状态：邻字合成已退役。** 源码、Shader 与静态／动态 Prefab 已保留 GUID 移入 `Tests/Editor/ArchivedGlyphUnion/`，不进入正式导出或 Player。以下记录当时的实验过程及旧路径；当前方案见 [单字形共享材质](TMP_SINGLE_GLYPH.md)。advance 校验仍保留。

本轮有两项结果：正式的单 Pass 外描边实验导出增加了受限 advance 补偿；另外完成了单 Pass 邻字合成的**静态原型**。没有增加运行时脚本，没有叠加文字对象或增加 Shader Pass。邻字原型暂不进入导出菜单，不能直接当作动态文字方案。

后续用户已确认允许事件驱动组件；[动态邻字合成](TMP_GLYPH_UNION_RUNTIME.md) 已作为单独实验导出模式接入。以下无运行时脚本、必须离线重建的限制专指本页静态原型，静态资产保留用于对照。

## 可查看资产

- `Exports/战斗-主菜单界面_单Pass字形校验.prefab`：单字形 Shader，加上受限 advance 补偿，仍可从现有实验导出入口重新生成。
- `Exports/单Pass邻字合成静态原型.prefab`：同样的文字布局，增加静态邻字覆盖合成；21 个已有字体段使用原型 Shader，6 个缺字体段按原规则占位。
- [原稿／上一版／静态原型标题对照](PsdTextAudit/StrokeDiagnosis/union-title-comparison.png)、[重载后的整图](PsdTextAudit/StrokeDiagnosis/union-static-reloaded.png)。

**静态原型不要直接用于会改字的计时器、数值或标题。** 修改文字、字距或字体等排版数据后，邻字表可能过期，必须重新生成。当前没有自动同步组件；这是沿用原方案“不增加运行时脚本”的边界，不是已经支持动态更新。

## 1. 根据源排版宽度补偿小额 advance

逐字检查发现，源 `Hell` 的排版宽度约 102.74 UI，而 TMP 约 101.80 UI。末尾字符会逐渐偏左，不能只用填充总面积判定字形是否正确。

Core 的 `PsdTextAdvanceCorrection` 只计算有界补偿：

```text
额外 characterSpacing 百分比 = 100 × (源宽度 − 实测 advance) / (实际字符数 × 字号)
```

只对实验模式下的单一样式、已有字体、伪粗体文本应用；至少两个字符，差异不超过源宽度 2%。单个字符、缺字体混排、较大误差均不拟合。保留源 Tracking、字号和字形缩放，补偿的是此前统一 2.5% 伪粗体 advance 的小额度量差。标准 TMP 导出不变。

新增真实 TMP 回归先复现了 0.8 UI 的差值，再验证补偿后 advance 与输入一致；同时验证 10% 的大误差被拒绝，源 Tracking、默认字体材质与缩放保持。

当前样本 8 个文本层应用补偿，导出诊断显示数量。`Hell` 后两个字母的可见填充左边界由约 714/734 改为 715/735，原稿约为 716/736。该结果改善位置累积，不等于所有轮廓更相似。局部阈值掩码 IoU 的比较结果有升有降，见 [advance 指标](PsdTextAudit/StrokeDiagnosis/advance-metrics.json)，没有将拟合源宽度宣称为全部视觉通过。

## 2. 静态邻字合成原型

Shader 名称：`TextMeshPro/Mobile/Distance Field - PSD Glyph Union Prototype`，文件为 `Shaders/TMP_SDF-Mobile-PSDGlyphUnionPrototype.shader`，沿用同一官方 TMP Mobile Shader 副本。验证与静态资产构建在 `Tests/Editor/PsdGlyphUnionPrototype.cs`，没有纳入正式窗口的渲染模式。

做法是在单个 TMP 文字段内记录可见字形四边形、图集 UV 和前后邻接关系。每个文字段持有一张 32×2 RGBAFloat 布局纹理：一行是段落 UV 区域，一行是字体图集区域。顶点阶段识别当前字形，片元阶段读取前后邻字，先合成填充与外轮廓覆盖；重叠区域只由一个四边形负责输出，避免重复混合。

布局采用 TMP 自带的 Paragraph UV，不能直接使用文本对象的本地顶点位置：Canvas 批处理会变换位置，导致本地坐标匹配失败。原型设定 horizontalMapping/verticalMapping 为 Paragraph，现有 TMP Canvas 通道即可传递该数据，不需要每帧上传世界矩阵。

原型限制为同一 TMP 内的单材质、统一样式、单行、1～32 个可见字形。只采样相邻字形；非邻接四边形相交或字形被斜切时明确拒绝。不同 TMP 组件、不同字体段之间不做合成。缺字体占位仍保留独立标准材质，未将其变成原字体。

## 3. 已验证的覆盖问题

采用黑背景、红填充、绿描边，隔离比较 0→5 px 描边，不将图片背景、颜色或原稿差异混进该测试。

| 文字段 | 不做邻字合成：填充通道改变的像素 | 静态原型：改变的像素 |
|---|---:|---:|
| Energy Level | 17 | 0 |
| Hell | 0 | 0 |
| Backpack | 5 | 0 |

基线必须实际复现覆盖，测试才接受原型的结果，避免使用没有交叠的文字证明修复。以上证明的是这些样本内，邻字描边不再改变原先的填充覆盖；不证明已经恢复 Photoshop 全部字形轮廓或达到 98 分。

证据：[覆盖指标](PsdTextAudit/StrokeDiagnosis/union-fill-proof.json)、[完整回归](PsdTextAudit/StrokeDiagnosis/union-tests.json)、[补强后的原型检查](PsdTextAudit/StrokeDiagnosis/union-final-checks.json)。完整回归 63/63 通过；最终原型两项检查再次 2/2 通过。静态检查包含 Prefab 重导入、布局纹理持久化、Paragraph UV、单 Pass 和无工具运行时组件。

## 4. 资源和生命周期

原型仍为 27 个 TMP、32 个 Image、59 个 Graphic、1 个 Canvas，全部文字材质均为单 Pass，没有叠加绘制对象。21 个文字段持有自己的元数据与材质，另保留占位材质；材质种类为 22，较前一版更多，不能把单 Pass 等同于整张 UI 只有一次 draw call。

布局纹理原始数据约为 21×1024 字节，不包含纹理对象、材质和驱动开销。Shader 增加了顶点布局查询和邻字纹理采样，尚未进行移动设备 GPU 性能验收。运行时改字若要支持，需要在 TMP 文字／排版变化时重建数据，并正确管理材质实例、纹理和禁用后的释放；该方案仍待用户选择，没有提前加入运行时程序集。

静态材质先创建资产、登记布局纹理子资源，再重新绑定纹理引用并保存。不能在纹理仍是临时对象时就认为材质引用已经持久化。此前发现的引用丢失已修复，重载验证确认所有 21 个布局纹理都是持久化引用。[输出报告](PsdTextAudit/StrokeDiagnosis/union-output.json)

## 5. 未采用的实验

横／纵向加粗比例 1:0、0:1、1:0.5、0.5:1 没有优于 1:1，已经从生产 Shader 和适配器中撤掉，避免额外计算与无效参数。[对照](PsdTextAudit/StrokeDiagnosis/axes-title.png)、[指标](PsdTextAudit/StrokeDiagnosis/axes-metrics.json)

SDF16 与 SDFAA_HINTED 对标题的不同部分各有升降，没有全局切换字体生成模式。[对照](PsdTextAudit/StrokeDiagnosis/render-mode-title.png)、[指标](PsdTextAudit/StrokeDiagnosis/render-mode-metrics.json)

字形本身的局部轮廓和抗锯齿差异仍然存在。本轮区分了排版累积误差、邻字覆盖和字形轮廓三类问题，解决了可复现的前两项的一部分，没有通过继续降低统一字重掩盖其他问题。
