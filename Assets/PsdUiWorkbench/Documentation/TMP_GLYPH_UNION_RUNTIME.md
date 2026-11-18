# 事件驱动的 TMP 邻字合成

**状态：已退役并归档。** 实际视觉收益不足以承担逐段组件、独立材质与纹理成本。正式导出入口已删除；源码、Shader 和 Prefab 移入 `Tests/Editor/ArchivedGlyphUnion/`，原程序集已限定 Editor，正式三层不再引用。以下为历史过程及当时路径，当前请使用 [普通 TMP + 单字形外描边](TMP_SINGLE_GLYPH.md)。

用户已确认允许轻量运行时组件，支持运行时改字。当前接入单独实验选项，不替换标准 TMP 或原单字形外描边模式。仍保持单 Pass、每段一个 TMP，不叠加文字对象。

## 使用与交付

工作台“导出 UGUI Prefab” → **PSD 邻字合成（实验，支持改字，单 Pass）**。已有字体段自动添加 `PsdTmpGlyphUnion`，缺字体段继续默认字库、36 UI、白色、普通字重、字距 0、无描边阴影的占位规则。

交付：`Exports/战斗-主菜单界面_动态邻字合成.prefab`。19 个文本层、27 TMP、32 Image、6 个占位段，21 个已有字体段全部启用邻字合成，无回退诊断。预览截图：[整图](PsdTextAudit/StrokeDiagnosis/union-runtime.png)。

运行时正常设置 TMP 的 `text`、`fontSize`、`characterSpacing` 等属性即可。组件响应 TMP 的几何重建，无需业务侧手动重建邻字表。导出后的参考位置和字号仍来自已应用的 PSD 缓存；运行时内容增长后的业务布局不由组件负责。导出字库已烘焙所需字符及现有字体可用 ASCII；不承诺任意新字符都有原字体字形，出现回退图集时进入下述回退规则。

## 分层与依赖

- `Runtime/PsdUiWorkbench.Runtime.asmdef`：新增可选运行时模块，仅引用 `Unity.TextMeshPro` 和 `UnityEngine.UI`，不引用 Core、Editor、官方 PSD 包或旧插件。
- `Runtime/PsdGlyphLayout.cs`：读取已完成的 TMP 几何，校验范围，填充固定缓冲区；不读取 PSD、字体像素或图集像素。
- `Runtime/PsdTmpGlyphUnion.cs`：事件订阅、实例材质、纹理上传、回退及释放。Shader 显式序列化到组件，随 Prefab 进入构建，不依赖运行时 Shader.Find。
- `Editor/Infrastructure/PsdTmpPrefabExporter.cs`：按模式生成持久化基础材质，并在 inactive 层级下添加组件；保存时不将实例材质或纹理写入 Prefab。
- `Editor/Presentation/`：菜单传递模式；独立 UI Toolkit 组件 Inspector 显示合成数量、上传次数和回退原因。Inspector 订阅 TMP 事件刷新，不持续轮询。

Core 保持无 Unity 引用。原三层仍为 Editor 程序集；此次经用户确认增设可选 Runtime 边界，没有将 Importer 或源解析移到 Player。

## 事件与数据生命周期

订阅官方 [OnPreRenderText](https://docs.unity3d.com/ja/Packages/com.unity.textmeshpro%403.0/api/TMPro.TMP_Text.OnPreRenderText.html)，在 TMP 上传网格前使用最终顶点和 UV。回调内不调用 ForceMeshUpdate，不重新标脏同一个布局，不产生重建循环。启用组件只标脏一次；业务改字通过 TMP 本身的机制触发后续重建。

每个实例使用固定 Color[64]、Rect[32]、一张 32×2 RGBAFloat 纹理，复用缓冲区和材质。缓存比较发现 UV 数据没变时跳过 SetPixels/Apply；没有 Update、LateUpdate 或逐帧查找。纹理属性更新同步到 Mask 缓存的派生材质。空文字由 TMP 清空几何，不会绘制旧邻字；再次输入内容后由下一次几何重建刷新。

启用后克隆基础材质、使用 Paragraph UV。禁用／销毁时退订事件，恢复基础材质和原 UV 映射，释放自己的材质与纹理。Play Mode 通过 Destroy 在帧末释放，编辑器预览使用 DestroyImmediate。外部新提供的 PSD 外描边材质会被克隆使用，不会由组件销毁。

TMP 的 outlineColor 等 setter 可能复制当前实例材质，副本会借用临时纹理。组件识别并复用 TMP 已缓存的副本，继续保留原基础材质作为恢复目标；禁用时解除借用，但副本由 TMP 自己管理与销毁。不能把它误认为持久化基础材质，也不能每次刷新再克隆一份，导致下一次 fontMaterial getter 又分配新材质。该问题通过实际 setter 调用先复现，再加入回归。

同一帧修改材质后立即禁用，可能尚未收到下一次网格回调；Release 也执行借用检查与恢复，不能只依赖刷新回调处理。反复启停时仍识别 TMP 的缓存副本。禁用后恢复基础样式，不将临时实例的材质改动写回资产。

回退图集会产生 TMP_SubMeshUI，派生材质可能复制上一版邻字计数；回退时同步清零这些派生材质及其 Mask 材质。所有这些材质均由当前实例派生，不改共享字体资产。

## Shader 与明确边界

Shader：`TextMeshPro/Mobile/Distance Field - PSD Glyph Union`，文件 `Shaders/TMP_SDF-Mobile-PSDGlyphUnion.shader`。来自已验证的静态原型，仍基于官方 TMP Mobile Distance Field 副本并保留后缀命名。

同一段内最多 32 个可见字形，单行、统一样式、正常排序、轴对齐四边形，只合成相邻字形。以下情况清零邻字计数，保留原单字形外描边，不使用过期表：

- 超过容量、多行、反向排序、Paragraph UV 被改掉。
- 混合字号／字重／字体／颜色，斜体、下划线、删除线、高亮、顶点渐变或 Sprite。
- 多材质、回退图集，或者非邻接字形四边形交叠。
- 启用 Underlay 阴影。源 PSD 不支持的活动效果仍按原规则保留 Image，复制 Shader 不代表已实现效果导出。

缺少 Shader、Shader 不受设备支持或设备不支持 RGBAFloat 时保持原材质。组件 Inspector 显示原因。当前已有 21 个字体段都在适用范围内；不同 TMP 组件、不同字体段之间不合成。外部更换为非 PSD 外描边材质时尊重该材质并停止合成。

## 验证与成本

实际 Play Mode 验证帧数推进，依次改为 Hell、16、Energy Level，并改变字号／字距；不手动调用 ForceMeshUpdate，等待运行帧后确认数据已更新。禁用、重启与销毁检查材质和纹理释放。

GPU 回归将同一文本先改字、改字距和字号，再比较运行时事件同步与按新布局重新生成的静态表，完整 PNG 字节一致。另覆盖布局不变不重复上传、对象复用、容量／混合样式回退恢复、Stencil Mask 计数、真实回退图集、TMP 材质 setter 和源材质隔离。正式窗口第三种模式覆盖缓存重导入、重命名、Sprite ID、源 PSD/.meta、缺字体规则及持久化基础材质。

证据：[输出检查](PsdTextAudit/StrokeDiagnosis/union-runtime-output.json)、[窗口导出](PsdTextAudit/StrokeDiagnosis/union-runtime-export.json)、[完整回归](PsdTextAudit/StrokeDiagnosis/union-runtime-tests.json)。原始记录包含执行状态和结果，不以启动测试视为通过。

完整回归 **72/72 通过**，包含进入／退出实际 Play Mode；随后对“材质 setter 后同一帧禁用”补强释放逻辑和断言，针对性最终记录见 [运行时检查](PsdTextAudit/StrokeDiagnosis/union-runtime-final-checks.json)。

最终针对性 **7/7 通过**。该次异步 Test Runner 等待期间，同一 Editor 出现另一套 SceneFlow 回归并重置状态；没有把混合的 running 状态当作结果。最终在 Editor 中逐项调用 NUnit 测试方法，每项独立 SetUp／TearDown 和 Preview Scene，报告明确标注执行方式；仅取消本轮残留的 PsdGlyphUnionRuntimeTests 请求，未取消其他测试作业。

每段增加实例材质和布局纹理，21 张纹理原始数据约 21 KB，不含对象与驱动开销。Shader 仍有顶点匹配与邻字采样，材质实例会影响批处理，**单 Pass 不等于整个界面一个 draw call**。尚未做移动设备 GPU 性能验收，也不宣称零总 GC 或全部字体达到 98% 视觉一致；本轮验证的是动态同步和生命周期，原稿局部字形轮廓差异依然存在。
