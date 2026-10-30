# 生成流水线与新 UI 框架

完整的当前架构、源码导航和示例装配关系见 [新架构导读](ARCHITECTURE.md)。本文侧重迁移和宿主接入步骤。

本次迁移基于 Unity **6000.3.20f1**。当前运行时使用命名空间
ComponentAutoBindTool.ViewCore，入口为 ViewBase、WidgetViewBase、WindowEntry 和 ViewManager。

## 直接运行示例

打开 Example/Scenes/Example.unity 运行移动横屏框架实验台。五组实验覆盖窗口层级、
实例与参数、关闭策略、导航排队和两层嵌套 Widget；观察抽屉显示真实生命周期与 Scope。
完整步骤见 [实验台操作说明](Example/README.md)。

菜单“自动绑定UI组件 / 框架实验台”提供正式示例重建与独立编辑器绑定实验台。
示例使用固定策略的八个 Prefab，每次实验拥有独立可释放容器；旧枚举整数不能直接迁移。
所有资源装配和演示逻辑位于 Example，普通业务界面仍使用各自的 Inspector 按钮。

## 运行期增强接入（2026-10-29）

本轮保留 Core / ViewCore / Editor 三程序集及所有生成代码。现有无动画窗口无需重新生成或修改 Prefab。

- 排队：旧 `ShowView(..., immediately: false)` 对应优先级 0。需要优先级时使用 `EnqueueView(name, args, priority)`；同级保持提交顺序。默认 Top/System 打开时阻止队列出队；需要旧门禁可设置具体管理器的 `BlockQueueWhenHigherLayersOpen = false`。`ClearWaitingQueue` 独立于关闭窗口。
- 动画：窗口根节点挂载 `IViewTransition` 实现；入场、退场最终在主线程调用完成回调，Cancel 停止自身动效。关闭立即结束逻辑会话，退场延后隐藏和最终释放。需要控制独立子 GraphicRaycaster 时重写宿主 `SetInteractable`。
- 等待：`await Context.WaitClosedAsync(token)` 或 `await manager.WaitViewClosedAsync(id, token)` 等待当前打开会话的关闭；多个消费者分别调用 API，每个返回值只 await 一次。取消等待不会关闭窗口。该 API 不表示退场结束或资源已销毁。
- 诊断：Play Mode 打开“自动绑定UI组件 / 框架实验台 / 运行期窗口监控台”，无需额外挂组件。自定义诊断使用 `IViewManagerDiagnostics` 和复用 List，不反射管理器私有字典。
- 生命周期：WindowPresenter 的关闭/销毁现在与 Widget 一样完成 Pause → Close → Destroy，重复与重入调用幂等；异常时继续尝试剩余资源清理后传播错误。不要在同一实例的 OnClose 尚未返回时重入打开它；可在 OnViewClosed 或关闭等待结束后打开。

新增接口成员意味着自行实现 `IViewManager`、`IWindowContext` 的外部适配器需补齐相应方法与属性；使用内置 ViewManager / WindowEntry 的调用方保持原调用方式。主线程调用约束、异步完成源池与动画版本保护见 [架构导读第 5 节](ARCHITECTURE.md#5-调度与生命周期中需要保持的契约)，验证结果见 [运行期增强报告](../../../Validation/ViewEnhancements/README.md)。

## 接入新宿主

1. 先创建能独立编译的 partial 窗口类，继承 ViewBase；嵌套控件继承 WidgetViewBase。
2. 初次生成前保留空的 ConfigureWindow 实现，不引用尚不存在的 UIView 类型。
3. 把宿主挂到绑定根节点，将同节点 ComponentAutoBindTool 的目标脚本设为该宿主。
4. 按 Prefix_FieldSuffix 命名节点。嵌套 Widget 根节点添加自己的 AutoBind，直接使用内置 `Widget` 前缀，例如 `Widget_Details`，无需注册具体 Widget 派生类。多个 Widget 同节点时，在同节点 AutoBind 的目标脚本指定实际绑定对象。
5. 保存场景，点击“生成代码”，等待编译。无需先按“重扫校验”，生成本身始终重新扫描。
6. 接入强类型 Presenter：窗口 ConfigureWindow 中调用 RegisterWindow<TPresenter, UIView>(builder, view)，
   并注册所需的 Widget Presenter；Presenter 通过 BindWidget<TPresenter> 绑定控件。

迁移已有工程时，内置 `Widget` 优先于额外映射中同名项；如果旧 `Widget` 前缀指向非 Widget 组件，请为其选用其他前缀并重新生成。旧的自定义 Widget 前缀（例如 `WidgetGroup`）可继续使用原映射；切换到 `Widget` 后字段名会随前缀改变，需同步手写 Presenter 并通过 Inspector 重新生成和保存所属资产。类型无法解析时，映射 Inspector 的清理按钮会删除对应记录，使用前请检查编译状态和具体内容。

生成的 EnsureAutoBind 重写最近的匹配虚方法。Widget 生成的 GetGeneratedUIView 在需要时绑定，
并返回缓存的同一 UIView。没有生成文件的 Widget 可以编译和挂载，调用默认 getter 会报告缺少生成。
绑定失败时不会缓存残缺 UIView，修复绑定后可以重试。

普通 MonoBehaviour 可以使用同一生成器。生成类会显式声明新 IAutoBindHost，
保证基类已有显式接口实现时接口重新实现也会进入生成代码。
sealed、非虚或不可访问的最近匹配基类方法会阻止生成，不跳过它去重写更远的祖先。

## 编辑器流水线

AutoBindBuildPipeline 提供无 GUI 的 RescanAndValidate 与 GenerateCode：
传入目标组件、AutoBindGlobalSetting 和 AutoBindKeyMapSetting，返回统一的 AutoBindPipelineReport。
报告包含成功状态、扫描和校验结果、生成路径、绑定与代码变化标记。
Inspector 显示并记录同一份报告；选中对象和重绘不会隐式修改绑定。

准备阶段只在内存中解析宿主、扫描层级、校验候选绑定并生成完整源码。
未知前缀、缺失组件、非法/重复字段、手写成员冲突、无效路径或程序集不匹配均阻止提交。
遍历顺序、嵌套绑定边界、TMP 跳过规则以及字段命名转换规则保持原有语义。

- 重扫校验：成功后同步绑定表和运行时缓存，支持 Undo，不写生成代码。
- 生成代码：同步元数据及三份绑定列表，保存所属资产，再提交代码文件。
- 普通场景与场景中的 Prefab 实例：保存场景及实例覆盖，不自动 Apply 到原 Prefab。
- Prefab 编辑模式：保存当前打开的 Prefab；直接选中的 Prefab 资产保存该资产。
- 未命名场景：返回“请先保存当前场景”，流水线不弹窗。
- 可捕获的保存或写入失败：恢复组件、原代码与所属资产内容，并保留操作前的未保存编辑。

生成是对象与文件的一次持久化提交，不单独提供组件 Undo；需要可撤销的扫描使用“重扫校验”。
回滚不覆盖进程崩溃、断电等无法捕获的失败。

输出使用 UTF-8 无 BOM、LF、四空格缩进，没有时间戳；组件类型采用 global:: 完整限定名。
相同源码不重写文件，也不主动导入脚本；变化时仅 ImportAsset 目标文件，不执行全局 Refresh。
代码必须与宿主位于同一程序集。配置的“跟随脚本目录”布尔值开启时跟随脚本目录；
旧 /Third Party/... 路径会按 Assets 下相对路径解析。

GetBindComponent<T>(index, fieldName) 的有效字段名不存在时，记录错误并返回 null，
不会回退到另一个相同类型组件；纯索引重载仍可使用。

## 旧代码与生成产物

旧 ViewCore 根目录中的十个脚本和旧 AutoBindTestBase 已移至项目根目录
Legacy/ComponentAutoBindTool，保留迁移前工作区原内容、目录结构和 .meta，不参与 Unity 编译。
新 Contracts、Management、Presenters、ViewHosts 保留在 Assets。
详见该归档目录的 README.md 类型映射。

原 AutoBindTest 脚本 GUID 保留。所有 BindComponents 产物通过生成流程产生，禁止手工补写；
手写逻辑应放入宿主的另一个 partial 文件或 Presenter。

## 回归验证

测试位于 Assets/AATest/Editor，沿用默认 Editor 程序集和现有 NUnit 引用。
Fixtures/AssemblyHosts 新增独立程序集夹具，验证带 asmdef 的业务宿主。
EditMode 套件包括通过 EnterPlayMode 执行的真实示例集成测试。
在 Test Runner 的 EditMode 页运行 ComponentAutoBindTool.Tests，或用 Unity batchmode：

    Unity.exe -batchmode -nographics -projectPath "<项目路径>" -runTests -testPlatform EditMode -testFilter ComponentAutoBindTool.Tests -testResults "<结果.xml>" -logFile "<日志.log>"

不要给 -runTests 同时传入 -quit，由 Test Runner 自行结束。
建议在项目副本运行：测试会临时切换示例场景并进入 Play Mode。

AutoBindGeneratedCodeVerification 提供额外两阶段验证：在项目副本先以 -executeMethod 调用 Prepare，
退出后再次启动 Unity 调用 Verify。它会先生成四类宿主的真实代码，再经过脚本编译，
验证 IAutoBindHost 调用和缓存幂等性；生成的测试产物只应保留在验证副本。
首次迁移验证见 Validation/ComponentAutoBindTool；新实验台验证与截图见 Validation/FrameworkLab。

## 程序集、路径与生命周期边界更新（2026-10-29）

- 框架拆为 Core、ViewCore、Editor 三个程序集；Editor 仅在编辑器编译。业务 asmdef 显式引用 `ComponentAutoBindTool.Core`、`ComponentAutoBindTool.ViewCore` 及实际使用的 VContainer、uGUI、TMP 程序集。生成 partial 必须与宿主同程序集，校验器仍禁止跨程序集输出。
- 新配置默认开启“跟随目标脚本的路径”。已有项目配置也已切换；四份正式示例生成文件移到 `Example/Scripts`，保留 `.meta` GUID。菜单“自动绑定UI组件 / 框架实验台 / 迁移示例生成目录（保留GUID）”可重复执行迁移与正式流水线生成，不重建 UI 层级。
- `AutoBindKeyMapSetting` 迁至 Editor 目录，命名空间为 `ComponentAutoBindTool.Core`，原脚本 GUID、映射资产和额外映射保留。外部源码需要替换旧 `Third_Party.ComponentAutoBindTool.Scripts.Core` 引用；类型迁移标记用于 Unity 类型迁移，不等同于旧源码命名空间别名。
- 单斜杠旧配置按广泛兼容规则处理：如 `/tmp/Output` 会指向项目 Assets 内的 `tmp/Output`。真实绝对路径检查不重解释配置，因此选择器仍拒绝工程外目录，`..` 越界和 `AssetsBackup` 不会通过。
- Inspector 显式切换脚本时同步元数据并支持同一次 Undo；重绘只读摘要，不改绑定表。解析失败不提交部分元数据。
- Widget 活动时动态解绑补齐 Pause、Close、Destroy；隐藏/暂停状态不重复 Pause，重复销毁不重复回调。回调异常不阻止其余清理尝试，最后仍向调用方传播异常。

新增 `AutoBindAssemblyVerification` 两阶段编译验证：`Prepare` 通过 Inspector 为独立程序集的普通、显式接口、窗口、Widget 宿主生成；下次启动执行 `Verify` 检查真实接口分派、缓存和 Player 程序集边界。`CompilePlayer` 实际编译 Windows Player 脚本。只在验证副本执行这些入口，避免留下临时场景和测试生成文件。

本次验证为 Windows / Unity 6000.3.20f1：129 项回归全部通过，原 51 项全部保留；生成后编译、Player 脚本编译及真实脚本重载恢复均通过。命令、证据和平台边界见 [测试报告](../../../Validation/BoundaryFixes/README.md)。程序集依赖遵循 [Unity 6.3 程序集引用规则](https://docs.unity3d.com/6000.3/Documentation/Manual/assembly-definitions-referencing.html)。
