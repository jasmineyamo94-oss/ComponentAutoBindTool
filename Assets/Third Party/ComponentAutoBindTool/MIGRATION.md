# 生成流水线与新 UI 框架

本次迁移基于 Unity **6000.3.20f1**。当前运行时使用命名空间
ComponentAutoBindTool.ViewCore，入口为 ViewBase、WidgetViewBase、WindowEntry 和 ViewManager。

## 直接运行示例

打开 Example/Scenes/Example.unity 运行移动横屏框架实验台。五组实验覆盖窗口层级、
实例与参数、关闭策略、导航排队和两层嵌套 Widget；观察抽屉显示真实生命周期与 Scope。
完整步骤见 [实验台操作说明](Example/README.md)。

菜单“自动绑定UI组件 / 框架实验台”提供正式示例重建与独立编辑器绑定实验台。
示例使用固定策略的八个 Prefab，每次实验拥有独立可释放容器；旧枚举整数不能直接迁移。
所有资源装配和演示逻辑位于 Example，普通业务界面仍使用各自的 Inspector 按钮。

## 接入新宿主

1. 先创建能独立编译的 partial 窗口类，继承 ViewBase；嵌套控件继承 WidgetViewBase。
2. 初次生成前保留空的 ConfigureWindow 实现，不引用尚不存在的 UIView 类型。
3. 把宿主挂到绑定根节点，将同节点 ComponentAutoBindTool 的目标脚本设为该宿主。
4. 按 Prefix_FieldSuffix 命名节点。嵌套 Widget 根节点添加自己的 AutoBind，配置 Widget 前缀映射。
5. 保存场景，点击“生成代码”，等待编译。无需先按“重扫校验”，生成本身始终重新扫描。
6. 接入强类型 Presenter：窗口 ConfigureWindow 中调用 RegisterWindow<TPresenter, UIView>(builder, view)，
   并注册所需的 Widget Presenter；Presenter 通过 BindWidget<TPresenter> 绑定控件。

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

测试位于 Assets/AATest/Editor，使用项目现有 NUnit 引用，不引入新 asmdef。
EditMode 套件包括通过 EnterPlayMode 执行的真实示例集成测试。
在 Test Runner 的 EditMode 页运行 ComponentAutoBindTool.Tests，或用 Unity batchmode：

    Unity.exe -batchmode -nographics -projectPath "<项目路径>" -runTests -testPlatform EditMode -testFilter ComponentAutoBindTool.Tests -testResults "<结果.xml>" -logFile "<日志.log>"

不要给 -runTests 同时传入 -quit，由 Test Runner 自行结束。
建议在项目副本运行：测试会临时切换示例场景并进入 Play Mode。

AutoBindGeneratedCodeVerification 提供额外两阶段验证：在项目副本先以 -executeMethod 调用 Prepare，
退出后再次启动 Unity 调用 Verify。它会先生成四类宿主的真实代码，再经过脚本编译，
验证 IAutoBindHost 调用和缓存幂等性；生成的测试产物只应保留在验证副本。
首次迁移验证见 Validation/ComponentAutoBindTool；新实验台验证与截图见 Validation/FrameworkLab。
