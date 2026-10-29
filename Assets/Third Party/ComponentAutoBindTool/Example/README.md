# ComponentAutoBindTool 框架实验台

理解框架职责、调用链、Scope 和两个实验台的实现关系，先读 [新架构导读](../ARCHITECTURE.md)；本文侧重运行操作。

Unity 6000.3.20f1。打开本目录的 Scenes/Example.unity 后运行；不需要联网。原 Example 场景与 AutoBindTest 宿主 GUID 保留。

## 运行时操作

左侧选择实验，下方按钮调用正式框架 API。右上角打开观察抽屉；抽屉可滚动，并显示真实上下文状态、实例 ID、Scope ID、UIView 身份、Tick 与最近 200 条生命周期事件。关闭抽屉后继续操作舞台。重置或切换实验会释放整个实验容器。

Play Mode 下可另开菜单 **自动绑定UI组件 / 框架实验台 / 运行期窗口监控台**。选择当前实验的 ViewManager，查看四层顺序、缓存/待销毁实例、过渡状态和优先级队列；使用定位、关闭、返回及清空队列按钮直接调用框架。实验重置后选择新管理器。监控台是 EditorWindow，不需要往场景加脚本；它与示例观察抽屉分别展示框架调度快照和示例生命周期探针。

本轮运行期增强没有给现有 Prefab 添加动画组件，无动画窗口维持同步表现。`IViewTransition` 接入步骤及 `WaitClosedAsync` 的逻辑关闭语义见 [架构导读第 5 节](../ARCHITECTURE.md#5-调度与生命周期中需要保持的契约)。

| 实验 | 推荐操作 | 预期与观察位置 |
|---|---|---|
| 层级与遮挡 | HUD → Normal/A → Overlay → 暂停前窗 → Top → System | 四层排序递增；Overlay 下 A 继续 Tick；Cover 将同层前窗暂停。窗口青色标记是排序 +2 的子 Canvas。 |
| 实例与参数 | A → B → 刷新 A；新增 Multiple 两次；关闭最后 ID | A 的 InstanceId、Scope 和 UIView 不变，参数修订与打开次数增加。Multiple 有独立实例与 Scope；精确关闭不影响另一个。 |
| 生命周期 | 打开缓存窗并关闭；打开延迟窗并关闭，3 秒内重开；再次关闭等待到期 | 缓存重开复用。延迟重开取消计时，到期后再开创建新实例。观察 OpenSession 与实例订阅释放的区别。 |
| 导航与队列 | 打开 A → 排队 B → 排队 Multiple → 连续返回 | Normal 清空且 Top/System 没有打开窗口时，每次 Tick 处理一个排队请求。替换为 B 成功；替换缺失资源会在 Console 输出预期错误并保留原窗口。 |
| Widget / DI | 打开 Widget → 左侧数据 +1 → 隐藏/恢复 → 暂停窗口 → 返回 → 解绑 | 两个叶 Widget 独立计数，共享窗口 Scope。隐藏时左侧停止 Tick，恢复后继续；父窗暂停时局部显示不会恢复 Tick。解绑清理两层 Widget 订阅。 |

解绑后重开缓存窗口不会自动重建已解绑 Widget；点击“重置实验”开始新的完整实验。这展示解绑的实例生命周期语义。

活动 Widget 解绑会依次完成 Pause、Close、Destroy；已隐藏的叶节点不重复 Pause。OpenSession 和实例资源分别释放，两个叶节点的逻辑树与订阅都会清理，宿主 GameObject 的隐藏仍由示例处理。

## 编辑器流水线实验

菜单：**自动绑定UI组件 → 框架实验台 → 编辑器绑定实验台**。

1. 选择实验，点击“恢复实验初始状态”。
2. 点击“重扫与校验”或“生成并保存”。
3. 比较绑定表、源码变化、统一报告以及文件 UTC 时间。首次生成或改名会触发正常脚本编译；编译结束后面板恢复。
4. 使用“定位对象”查看真实层级，使用“打开生成文件”检查源码。

实验资产位于 Example/LocalPipelineLab，已排除出版本控制；配置是内存副本。正式演示 Prefab、全局路径配置与映射资产不受实验影响。新建隔离场景前，当前未命名场景必须先保存。命名场景中的既有未保存编辑不会被实验自动保存。

故障实验通过 sceneSaved 回调将实验代码文件短暂设为只读，触发真实提交异常。finally 解除回调并恢复属性。报告比较组件序列化、代码、所属场景文件与既有未保存编辑，不预设成功结果。失败错误与 Inspector 使用相同 Console 输出。

## 重建与代码组织

菜单 **重建正式示例** 重建本目录的场景、8 个目录 Prefab、字体图集，以及配置输出目录中的生成绑定；同时更新全局额外映射的 Widget / WidgetGroup 两项，必要时将根目录 TMP Essentials 移入 Example。菜单 **恢复实验初始状态** 只处理本地隔离实验。正式重建保留已有资产 .meta；构建模板的四种宿主通过 Inspector.RunPipeline 生成，不手写 BindComponents。

- AutoBindExampleScope：外层组合入口；Update 唯一驱动实验 ViewManager.Tick。
- LabSession：独立可释放实验容器，窗口资源由 Prefab 目录固定配置。
- AutoBindTest / Presenter：共享绑定结构的通用窗口，强类型 LabOpenArgs。
- LabWidgetGroup → AutoBindExampleWidget：两层嵌套；Presenter 为 Transient，LabScopeProbe 为窗口 Scoped。
- LabTelemetry：示例专用观察服务，只读取公开上下文与回调，不访问框架私有容器。
- PipelineLabHost：没有手写生成字段依赖，可安全演示字段改名。

首次从零搭建时顺序为：可编译宿主骨架 → 层级与 Prefab → Inspector 生成 → 强类型 Presenter。交付的产物已完成此流程。

全局配置现在默认跟随宿主脚本目录，四份正式绑定产物位于 `Example/Scripts`。旧版本升级可使用菜单 **迁移示例生成目录（保留GUID）**：移动既有生成文件并调用正式流水线更新场景与八个 Prefab，保留生成文件和资产 GUID。编辑器实验仍显式覆盖为 `LocalPipelineLab`，不随全局默认值改写正式宿主目录。

## 字体与离线资源

Noto Sans CJK SC Regular 来自 [Noto CJK](https://github.com/notofonts/noto-cjk)，授权文件见 Fonts/OFL.txt（SIL Open Font License 1.1）。原字体 SHA-256：2C76254F6FC379FDDFCE0A7E84FB5385BB135D3E399294F6EEB6680D0365B74B。

Fonts/LabChinese.asset 预烘焙示例源码中的中文及 ASCII，使用 Static 图集。正式重建会补齐新增示例字符。TMP Essentials 随示例保留，无额外 UI、动画或资源加载依赖。竖屏仅显示横屏提示。

从空字体图集重建时需要启用 Unity 图形设备；命令行重建不要使用 -nographics。运行测试可以使用 -nographics。

## 验证

测试位于 Assets/AATest/Editor。PlayMode 集成由现有 EditMode 测试程序集通过 EnterPlayMode/ExitPlayMode 执行，保持现有 NUnit 与程序集布局。测试报告、多分辨率截图及真实重载记录见项目根目录 Validation/FrameworkLab。Editor 渲染与射线检查不代表真机性能测试。
