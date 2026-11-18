# 正式 TMP 导出与视觉验收

工具栏“导出 UGUI Prefab”已接入文本缓存。当前交付为 `Exports/战斗-主菜单界面_TMP校正 1.prefab`：19 个文本层、27 个 TMP 组件、32 个 Image、17 个原始分组、6 个缺字体占位段。源 ID 947 未导入，仍跳过；缓存包含全部 20 个源文本层。旧候选与已有输出没有覆盖。

## 分层与数据流

```text
源 PSD → 官方 PSDImporter → 官方 Sprite / 纹理
                       → 独立文本读取器 → workbench.text 子资源
                                                   ↓
工作台已应用快照 + 独立显隐 → Prefab 计划 + 文本布局计划
                                                   ↓
                              字体解析 → TMP / Image Prefab
```

- Core：`PsdTextStyle`、`PsdTextSource` 和 `PsdTextLayoutPlanner` 保存不可变样式段与坐标，不持有 Unity 对象。字距、自动字偶距与字号独立表达。
- Infrastructure：`PsdLayerMetadataReader` 复用有界的图层容器遍历，在 ID 完整验证通过后交付文本附加块；类型读取路径不分配文本负载。`PsdTextDescriptor` 解析 Descriptor / EngineData，`PsdTextMetadataReader` 转成 Core 模型。不解压图像，不使用官方内部解析器或旧插件。
- 派生 Importer 扩展版本为 4，官方基线仍为 12.0.2；先完成官方导入，再添加固定标识 `workbench.text`，格式版本为 3。类型缓存 `workbench.layerTypes` 的契约不变。文本错误保存诊断，不使成功的官方导入失效。导入回调不调用 AssetDatabase 的导入或保存方法。
- `PsdTextPrefabExport` 只读取导入缓存，在确认导出后精确匹配项目 TTF/OTF 的 PostScript name 并生成独立静态 SDF 字库，调用既有布局、构建与保存模块。Presentation 只处理路径、冻结显隐、调用和状态。原公开 Image 导出方法继续可用。

窗口加载、勾选、重绘及导出不解析源 PSD、不自动切换 Importer。官方原版或缺少有效缓存时使用 Image，并报告原因。当前战斗资源原本已是 Workbench Importer，只重建缓存，没有修改源 PSD、`.meta`、Importer 设置或显隐配置。

## 字距和对齐规则

Photoshop Tracking 以千分之一 em 表达。TMP 的 characterSpacing 以字号的百分之一表达，因此基础换算为：

```text
TMP characterSpacing = Tracking / 10
相邻字符增加的 UI 间隔 = Tracking × 字号 / 1000
```

例如 34 字号、Tracking=100，增加 3.4 UI 单位；Tracking=200 增加 6.8。值保存在每个样式段，支持负值与零值，不能把整份 PSD 统一设为同一个数。源计时器 ID 1067 为 200，Energy Level ID 1091 为 100。

当前伪粗体沿用真实样本验证的公共起点 WeightBold=0.425、额外粗体 advance=2.5%。为避免字库自身 boldSpacing 再叠加一次，组件使用：

```text
characterSpacing = Tracking / 10 + (FauxBold ? 2.5 - 字库.boldSpacing : 0)
```

该粗体起点是当前样本的近似，不是所有字体的 Photoshop 伪粗体通式。既有 Font Asset 不被修改。缺字体占位没有额外粗体 advance。

Auto Kerning 读取并独立缓存，但不直接当作 TMP kern 的等价模式。源段开启时先生成对应方案；已有字体完整且有源排版宽度时，再比较 kern 开启／关闭两种实际 advance，选择更接近源宽度的方案。改善至少超过 0.5 UI 单位或源宽度 0.5% 才切换，不拉伸、不修改 Tracking 或字号。新字库包含对应字体特性；占位关闭，混排缺字体时不以原稿整行宽度强制匹配。字偶距由字体自身的表决定，不等价于 Photoshop 光学字距。显式非零 Kerning、非标准字形缩放、基线偏移等尚未验证的结构保留 Image 并诊断，避免忽略参数后静默导出。

布局保留源字号、均匀变换、小数基线及左对齐／居中。文字段按实际 glyph advance 连续摆放，保留段间间距；居中总宽度扣除最后字符之后的一次额外间距，不把尾字距算成整行宽度。自动字号、换行、富文本关闭。不拉伸字形填满 Sprite 边框。当前仅单行，行间距和段落换行不在支持范围；不把它们错误地当成字距处理。

## 描边与占位

标准 Outline + Face Dilate 补偿继续使用已验证的 SDF 方程和最终 RatioA 检查，按实际字形缩放求解；距离场不足时明确失败，不截断参数。当前战斗 PSD 启用的文字效果仅单色外描边，大小为 2/3/5 px，支持颜色与不透明度。保存但未启用的阴影不会被导出。活动阴影、渐变描边等其他效果尚未接入，相关层保留 Image 并提示；官方 Sprite 本身不能当作 Photoshop 全效果还原证据。

缺字体段按确认规则固定为默认 TMP 字体、36 UI 单位、普通字重、白色、字距 0、无描边和阴影。只替换缺字体的段，保留已有字体段。默认字库仍需覆盖占位所需字符；本样本所需字符均覆盖。不以占位字符的形状或行宽评价原字体还原程度。

## 98/100 视觉标准

**98/100 是人工视觉验收目标，不是测得的像素相似率。** 自动测试验证数据与几何约束，人工对照评价可见差异。现已使用 PSD 文件保存的合成图做局部原稿对照，详见 [字重诊断与截图](TMP_STROKE_DIAGNOSIS.md)；仍不将局部指标折算成整图 98 分。

| 项目 | 权重 | 验收内容 |
|---|---:|---|
| 内容与字体 | 30 | 内容无丢失，已有字体精确匹配；缺字体段符合占位规则 |
| 位置与布局 | 35 | 基线、对齐、层级、遮挡和自身显隐正确，无明显偏移、裁剪、异常重叠 |
| 外观 | 25 | 字号、颜色、字重和启用的外描边肉眼接近；小幅抗锯齿差异不扣分 |
| 间距 | 10 | 每段 Tracking、字偶距开关、混排衔接和居中尾字距正确 |

在参考分辨率 1080×2400 的 100% 显示下对照，再看整图缩放后的整体观感。可用定位预算为基线约 1 UI 单位、行宽与可见高度约 5% 或 2 UI 单位取较大者、外描边约 1 px；这是排查预算，不是已经全部量得符合的结论。有可见文字丢失、错误字体替代、明显错位／裁剪、显隐或遮挡错误时直接不放行，不能用总分掩盖。98 分允许少量不影响使用的轮廓与光栅化差异。

## 实际验证

- Unity 完整回归 49/49 通过，无失败或跳过；Python PSD 审计测试 7/7 通过。最后一次字库 Unicode 安全调整后，相关检查再次 10/10 通过，见 [最终检查](PsdTextAudit/tmp-export-final-checks.json)。
- 原生 C# 读取全部 20 层，与独立审计逐项核对文字、段范围、字体、字号、基线、对齐、Tracking、颜色及外描边；含未导入层。
- PSD / PSB、未知奇数长度块与填充、文本块先于 ID、重复 ID、截断和旧版本诊断均覆盖。重复 ID 不通过名称或顺序推断关联。像素仍由容器遍历直接跳过。
- 负字距／0／100／200 的实际 TMP 字形距离变化均符合 `Tracking × 字号 / 1000`，容差 0.0001 UI 单位；原有居中与分段占位测试继续通过。
- 临时副本检查缓存首次生成、重导入与重命名后子资源 local file ID 稳定、Sprite 引用稳定；调用正式窗口导出入口后重载 Prefab 检查 19/27/6/32 数量、Kerning 开关、场景清理、源 PSD/.meta 与默认材质不变。官方原版路径保留 51 个 Image，不自动迁移。
- [测试原始结果](PsdTextAudit/tmp-export-tests.json)、[最终资产报告](PsdTextAudit/tmp-export.json)、[实际整图渲染](PsdTextAudit/tmp-export-render.png)。截图使用隔离相机离屏输出，不回写 Overlay Prefab；已查看标题、计时器、Energy Level、右侧图标、Backpack 与 Emergency。

本次开发中发现 TySh 内部允许最多 3 字节的零填充，最初读取器误判尾部；已经修正并增加有界处理。早期失败导入缓存曾在脚本重载时被复用，已通过扩展导入器版本更新失效并重新验证。历史 Console 记录保留，最终状态以本轮编译、导入和测试结果为准。

新 TOXIA 字库为 Static、2048²、当前文字加可用 ASCII；运行时无源字体文件引用，不动态补字。当前 59 个 Graphic、14 种 TMP 材质、1 个 Canvas。图集与材质以验证为先，尚未完成手机上的批次和内存优化，也不承诺任意字体或 Photoshop 全部排版效果。

## 原稿字重回归补充

后续用户指出“发胖”后，实际局部对照发现直接开启 TMP kern 会压窄长文本，旧伪粗体 0.5 也让部分数字偏厚。已缓存源排版框宽度并做两种字偶距方案校验，当前伪粗体起点调整为 0.425；并未清零 Dilate。前文历史 49 项测试不是视觉正确性的充分证据。完整原因、像素密度定义、原稿截图、修复前失败与修复后检查见 [TMP_STROKE_DIAGNOSIS.md](TMP_STROKE_DIAGNOSIS.md)。
