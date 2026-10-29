# Unity 项目启动框架：AutoBind UI + VContainer

架构与 AI 接手入口：[ComponentAutoBindTool 新架构导读](<Assets/Third Party/ComponentAutoBindTool/ARCHITECTURE.md>)，包含生成闭环、Scope/Presenter/Widget 生命周期、示例装配与按任务查找源码的导航。

当前可运行演示：[移动横屏框架实验台](<Assets/Third Party/ComponentAutoBindTool/Example/README.md>)，包含五组运行时实验和 UI Toolkit 编辑器绑定实验。

> **UI 框架迁移说明（2026-10-28）**：当前接入方式请先阅读 [生成流水线与新 UI 框架](<Assets/Third Party/ComponentAutoBindTool/MIGRATION.md>)。下文保留迁移前的项目说明，其中 ViewLifetimeScope 等旧接入方式已归档；当前采用 ViewBase / WidgetViewBase / WindowEntry，项目版本为 Unity 6000.3.20f1。

> 这是一个面向 Unity 6 的项目启动框架，用 AutoBind 解决 UI 引用收集与生成，用 VContainer 承接 View 生命周期与依赖注入，并预留 `Assets/AAGame/` 作为业务主战场。

## 项目定位

这不是单纯的脚本仓库，也不建议只把它理解成 `ComponentAutoBindTool` 的示例工程。

更准确的定位是：

- 一个可持续演进的 **Unity 项目启动框架**
- 一个以 **AutoBind + VContainer** 为核心的 UI 基础设施母板
- 一个已经预留业务层目录、可以直接继续孵化实际项目的工程骨架

如果你准备基于它继续开发项目，建议这样理解目录职责：

- `Assets/Third Party/ComponentAutoBindTool/`：框架基座，承载 AutoBind、Editor 工具链、View 基础设施
- `Assets/AAGame/`：业务主战场，承载场景、模块、UI、配置、资源
- `Assets/AATest/`：测试预留区，用于后续补齐 EditMode / PlayMode 测试

默认开发策略也应如此：

1. 先判断需求能否在 `Assets/AAGame/` 内完成
2. 只有业务层出现明显重复、性能瓶颈或扩展点缺失时，再演进框架层
3. 不要把具体业务逻辑直接写进 `Assets/Third Party/ComponentAutoBindTool/`
4. 不要手动编辑 `*.BindComponents.cs`，应始终通过 Inspector 工作流重新生成

## 它解决什么问题

在传统 Unity UI 开发里，最常见的问题是：

- 脚本里要手写大量字段
- Prefab 或场景对象需要反复拖拽引用
- 重构层级或改名时容易漏改
- Presenter / ViewModel 想拿到 View 引用时，通常缺少清晰的中间层

这个项目的方案是：

- 用 **节点命名前缀** 代表要绑定的组件类型
- 用扫描器在编辑器期收集绑定信息
- 用代码生成器生成 `partial class + UIView`
- 在运行时通过 `ComponentAutoBindTool` 提供强类型取值
- 在 `ViewLifetimeScope` 里提前执行 `EnsureAutoBind`，让 `UIView` 可以直接注入到 Presenter

## 核心能力

### AutoBind 工具链

- 根据层级节点命名自动识别组件类型
- 支持单节点多组件绑定
- 支持默认映射 + 自定义映射
- 支持绑定数据可视化编辑与重扫
- 支持生成 `.BindComponents.cs` 强类型代码文件
- 支持生成前校验，减少错误代码产出

### 运行时绑定层

- 运行时缓存 `Component` 列表与字段名列表
- 支持 `字段名 -> 索引` 的查找回退机制
- 绑定失败时会输出明确错误信息
- 避免业务层直接依赖字符串查找组件

### VContainer 视图层示例

- `ViewLifetimeScope` 在 `Configure` 时自动触发 AutoBind
- `UIView` 可作为依赖直接注入到 Presenter
- `ViewManager` 提供简单的弹窗栈管理能力
- 支持 `Normal / Top / System / HUD` 视图层级概念

## 一分钟理解它的工作流

```text
命名后的 UI 层级
    -> AutoBindHierarchyScanner 扫描
    -> BindDatas（编辑器绑定数据）
    -> AutoBindSerializedBindingUtility 同步运行时缓存
    -> AutoBindCodeGenerator 生成 partial class
    -> EnsureAutoBind() 构造 UIView
    -> VContainer 将 UIView 注入 Presenter
```

## 快速开始

### 环境要求

当前仓库实际使用的环境：

- Unity `6000.0.68f1`
- `jp.hadashikick.vcontainer` `1.17.0`
- `com.unity.ugui` `2.0.0`
- `com.cysharp.unitask` `2.5.10`

### 如果你准备用它开新项目

建议按这个顺序落地：

1. 先在 `Assets/AAGame/Scenes`、`Assets/AAGame/Scripts`、`Assets/AAGame/UI` 内建立业务目录
2. 把 `Assets/Third Party/ComponentAutoBindTool/` 视为框架层，尽量只做通用能力改造
3. 新界面优先继承 `ViewLifetimeScope`，或在其思路上扩展自己的业务 View 基类
4. UI 节点遵循 AutoBind 命名规则，生成代码只承载绑定结果，不承载业务逻辑
5. Presenter / Service / Module 逻辑优先落在 `Assets/AAGame/`，不要把业务逻辑回写到 Third Party
6. 当项目稳定后，再决定是否把框架层抽成独立包

### 打开项目后先看哪里

建议按这个顺序了解仓库：

1. 打开场景 `Assets/Third Party/ComponentAutoBindTool/Example/Scenes/Example.unity`
2. 查看示例脚本 `Assets/Third Party/ComponentAutoBindTool/Example/Scripts/AutoBindTest.cs`
3. 查看生成结果 `Assets/Third Party/ComponentAutoBindTool/Scripts/ViewBindComponents/AutoBindTest.BindComponents.cs`
4. 查看核心 Inspector 实现 `Assets/Third Party/ComponentAutoBindTool/Scripts/Core/Editor/ComponentAutoBindToolInspector.cs`

> 注意：当前仓库里的 `EditorBuildSettings.asset` 仍然指向旧路径 `Assets/ComponentAutoBindTool/Example/Scenes/Example.unity`。因此请手动打开示例场景，不要依赖 Build Settings。

### 体验一次完整生成流程

1. 在一个 UI 根节点上挂载你的业务脚本。
2. 在同一个对象上挂载 `ComponentAutoBindTool`。
3. 给子节点按约定命名，例如 `Btn_Start`、`Txt_Title`、`Img_Icon`。
4. 在 Inspector 中点击 `重扫校验`。
5. 确认校验通过后点击 `生成代码`。
6. 在生成的 `UIView` 中使用强类型字段。
7. 在 `ViewLifetimeScope` / Presenter 中消费这个 `UIView`。

## 命名规则

### 基本格式

```text
Prefix_FieldSuffix
PrefixA_PrefixB_FieldSuffix
```

规则说明：

- 最后一个 `_` 之后的部分是字段后缀
- 最后一个 `_` 之前的每一段都会被当成组件前缀
- 每个前缀都必须能在 `AutoBindKeyMapSetting` 中解析到组件类型
- 没有 `_` 的节点不会参与绑定

### 示例

| 节点名 | 解析结果 | 生成字段 |
| --- | --- | --- |
| `Btn_Start` | `Button` | `btnStart` |
| `Img_Icon` | `Image` | `imgIcon` |
| `Txt_Title` | `Text` | `txtTitle` |
| `Drop_Img_Quality` | `Dropdown` + `Image` | `dropQuality` + `imgQuality` |
| `TMTxt_Title` | `TMPro.TextMeshProUGUI` | `tMTxtTitle` |

### 字段名生成规则

字段名由 `AutoBindFieldNameUtility` 生成，逻辑很直接：

- 去掉所有 `_`
- 将结果的首字符转换为小写

因此：

- `Btn_Start` -> `btnStart`
- `Img_PlayerAvatar` -> `imgPlayerAvatar`
- `TMTxt_Title` -> `tMTxtTitle`

这也意味着带有连续大写缩写的前缀时，生成字段名不一定完全符合常见命名审美，但它和当前实现保持一致。

### 跳过规则

以下节点不会被当前根节点纳入扫描：

- 子节点上再次挂了 `ComponentAutoBindTool`
- 节点名包含 `NonRoot ==>`
- 节点名不包含 `_`

### 一个必须知道的命名陷阱

```text
Btn_Login_Close
```

这会被解析成：

- 前缀：`Btn`
- 前缀：`Login`
- 后缀：`Close`

如果 `Login` 不是一个合法组件前缀，扫描会直接报错。

也就是说：

- 这个工具 **不会** 把 `Login_Close` 自动理解成一个完整字段名
- 多单词字段后缀请写成 `LoginClose`

推荐写法：

```text
Btn_LoginClose
```

## 默认组件映射

项目内置了一组常用组件前缀，典型项如下：

| Prefix | Type |
| --- | --- |
| `Tf` | `Transform` |
| `Rtf` | `RectTransform` |
| `Btn` | `Button` |
| `Img` | `Image` |
| `Txt` | `Text` |
| `Inf` | `InputField` |
| `Sld` | `Slider` |
| `Tog` | `Toggle` |
| `SRect` | `ScrollRect` |
| `Drop` | `Dropdown` |
| `CGroup` | `CanvasGroup` |
| `TMTxt` | `TMPro.TextMeshProUGUI` |
| `TMInf` | `TMPro.TMP_InputField` |
| `TMDrop` | `TMPro.TMP_Dropdown` |

自定义映射保存在：

- `Assets/Third Party/ComponentAutoBindTool/AutoBindKeyMapSetting.asset`

当前仓库中还额外定义了一条项目侧映射：

```text
Nbi -> AAGame.Scripts.Views.Menu.Main.NavigationBarItem
```

## 典型开发流程

### 1. 创建或选择一个 View 根节点

如果你使用本仓库里的运行时方案，推荐让根节点脚本继承：

- `ViewLifetimeScope`

这样根节点会自动具备：

- `Canvas`
- `GraphicRaycaster`
- `ComponentAutoBindTool`

这三个约束是通过 `[RequireComponent]` 写死在 `ViewLifetimeScope` 上的。

### 2. 让目标脚本和 AutoBindTool 在同一个对象上

这是生成前校验的硬性要求。

如果不是同一个对象，校验会失败。

你也可以对任意 `MonoBehaviour` 使用右键菜单：

- `Add AutoBindComponentTool`

它会自动把 `ComponentAutoBindTool` 挂到同一个对象，并将当前脚本写入 `m_targetScript`。

### 3. 配置输出路径与命名空间

项目使用 `AutoBindGlobalSetting` 管理默认输出行为。

默认配置文件路径：

- `Assets/Third Party/ComponentAutoBindTool/AutoBindGlobalSetting.asset`

当前仓库默认值：

- 默认命名空间：空
- 默认输出目录：`Assets/Third Party/ComponentAutoBindTool/Scripts/ViewBindComponents`
- `跟随目标脚本的路径`：关闭

实际行为是：

- **关闭**：所有生成文件统一输出到全局目录
- **开启**：根据目标脚本所在目录同步每个对象自己的代码输出路径

### 4. 扫描、校验、生成

`ComponentAutoBindTool` 的 Inspector 是整个工作流入口，核心按钮只有三个：

- `重扫校验`
- `生成代码`
- `清空`

其中：

- `重扫校验` 会重新扫描层级、重建绑定数据，并弹出校验结果
- `生成代码` 会在生成前再次执行校验
- `清空` 只会清除当前对象的绑定列表

### 5. 在业务代码中使用生成结果

生成文件会为目标脚本补齐一个 `partial class`，并生成：

- `UIView` 嵌套类
- `private UIView view;`
- `EnsureAutoBind(GameObject go)` 方法

## 生成代码长什么样

当前示例生成出来的代码形态如下：

```csharp
public partial class AutoBindTest
{
    [Serializable]
    public class UIView : IUiViewComponent
    {
        public Image imgTest1;
        public Button btnTest2;
        public Text txtTest3;
        public Dropdown dropTest4;
        public Image imgTest4;
    }

    private UIView view;

    public override void EnsureAutoBind(GameObject go)
    {
        if (view != null)
        {
            return;
        }

        var autoBindTool = go.GetComponent<ComponentAutoBindTool>();
        view = new UIView
        {
            imgTest1 = autoBindTool.GetBindComponent<Image>(0, nameof(UIView.imgTest1)),
            btnTest2 = autoBindTool.GetBindComponent<Button>(1, nameof(UIView.btnTest2)),
            txtTest3 = autoBindTool.GetBindComponent<Text>(2, nameof(UIView.txtTest3)),
            dropTest4 = autoBindTool.GetBindComponent<Dropdown>(3, nameof(UIView.dropTest4)),
            imgTest4 = autoBindTool.GetBindComponent<Image>(4, nameof(UIView.imgTest4)),
        };
    }
}
```

这个设计有几个明显优点：

- 业务脚本本体保持干净
- 生成代码和手写代码分离
- `UIView` 天然适合作为 Presenter 的注入对象
- 运行时仍然能走索引访问，避免完全依赖字符串查找

## 运行时架构

### `ComponentAutoBindTool`

职责：

- 保存编辑器期的 `BindDatas`
- 保存运行时的 `m_BindComs`
- 保存运行时的 `m_BindFieldNames`
- 提供 `GetBindComponent<T>()` 访问接口

运行时查找策略：

1. 先尝试通过字段名匹配索引
2. 匹配不到时回退到传入索引
3. 类型不匹配或引用为空时输出明确错误日志

### `ViewLifetimeScope`

职责：

- 作为 VContainer 的 View 根节点基类
- 在 `Configure` 中调用 `EnsureAutoBind(gameObject)`
- 提供 `RegisterCommon<T>()` 简化 Presenter 注册
- 管理视图层级、初始化参数、显示与暂停生命周期

### `ViewManager`

这是仓库里附带的一个轻量级视图管理器，主要能力：

- `ShowView()`：打开界面
- `HideView()`：关闭栈顶界面
- `ShowViewAtHidePeekView()`：关闭当前栈顶并强行插入新界面
- `Tick()`：当栈为空时，从等待队列中补开界面

当前实现重点管理的是 `Normal` 层弹窗：

- `Normal` 层会入栈
- 新 `Normal` 打开时会暂停旧栈顶
- 关闭当前界面时会恢复上一层
- `Top` / `System` 目前只参与排序，不默认入栈

### `ViewBundle`

`ViewBundle` 是一个极轻量的参数容器：

- 用来在 `ViewManager` 打开界面时传入 `object[] args`
- 在 Presenter 或 View 内消费这些参数

## VContainer 集成方式

示例脚本：

- `Assets/Third Party/ComponentAutoBindTool/Example/Scripts/AutoBindTest.cs`
- `Assets/Third Party/ComponentAutoBindTool/Example/Scripts/AutoBindTestPresenter.cs`

示例流程是：

1. `AutoBindTest : ViewLifetimeScope`
2. `base.Configure(builder)` 内部先执行 `EnsureAutoBind`
3. `RegisterCommon<AutoBindTestPresenter>(builder, view)` 把生成的 `view` 注入容器
4. `AutoBindTestPresenter` 通过 `[Inject]` 拿到 `AutoBindTest.UIView`
5. Presenter 直接操作 `view.txtTest3` 等字段

这让项目里的依赖关系变成：

```text
Scene / Prefab
    -> ViewLifetimeScope
    -> AutoBind generated UIView
    -> Presenter
```

而不是让 Presenter 直接去 `GetComponent` 或自己维护大量序列化字段。

## 目录结构

推荐按“框架层 / 业务层 / 测试层”理解当前工程：

- `Assets/Third Party/ComponentAutoBindTool/`：框架层
- `Assets/AAGame/`：业务层
- `Assets/AATest/`：测试层

```text
Assets/
  AAGame/
    Scenes/
    Scripts/
      Core/
      Gameplay/
      Modules/
      Network/
      UI/
      Utility/
    UI/
      Prefabs/
      Views/
    Prefabs/
    Resources/
    Config/
    Data/
    Settings/
    Localization/
    Audio/
    Art/
    Shaders/
  AATest/
    (推荐放置 EditMode / PlayMode 测试与测试 asmdef)
  Third Party/
    ComponentAutoBindTool/
      AutoBindGlobalSetting.asset
      AutoBindKeyMapSetting.asset
      Example/
        Scenes/
          Example.unity
        Scripts/
          AutoBindTest.cs
          AutoBindTestPresenter.cs
      Scripts/
        Core/
          ComponentAutoBindTool.cs
          CustomContextMenu.cs
          Editor/
            ComponentAutoBindToolInspector.cs
            AutoBindHierarchyScanner.cs
            AutoBindCodeGenerator.cs
            AutoBindValidator.cs
        ViewCore/
          ViewLifetimeScope.cs
          ViewManager.cs
          ViewBundle.cs
        ViewBindComponents/
          AutoBindTest.BindComponents.cs
```

另外，仓库中还带有以下辅助依赖或工具：

- `generic-serializable-dictionary`：用于可编辑的自定义组件映射字典
- `Kogane.DebugLogger` / Console 工具：用于日志与编辑器体验
- `vFolders`：编辑器目录增强工具
- AutoBind 使用纯 C# `StringBuilder` 生成绑定代码，不依赖 Handlebars 或 NuGetForUnity。

### 轮回 UI 框架同步说明

- `ViewLifetimeScope.Show/Hide` 默认切换 GameObject 激活状态。`NonDestroy` 窗口关闭后按名称缓存，重新打开会复用实例并调用 `Init`；同名栈顶也会重新接收参数。
- `DelayDestroy` 在关闭时隐藏窗口，再按 `m_DestroyDelaySeconds` 使用真实时间销毁；默认零延迟。延迟期间重开会创建新实例。
- 原三参数 `ViewManager` 构造函数保持默认暂停前一个 Normal 窗口。需要叠加显示时，通过工厂注册四参数构造函数：

```csharp
builder.Register<ViewManager>(resolver => new ViewManager(
    resolver.Resolve<LifetimeScope>(),
    resolver.Resolve<IViewPrefabHelper>(),
    resolver.Resolve<IViewRootHelper>(),
    ViewStackDisplayMode.Overlay), Lifetime.Scoped).AsImplementedInterfaces();
```

上例将同一管理器实例注册为 `IViewManager` 和 `ITickable`，由 VContainer 调度排队窗口。

- 实现 `IViewManagerInjectable` 的视图在 `Init` 前接收管理器。窗口内的依赖注入仍由子容器的显式组件注册或自动注入配置负责，管理器不会从父容器递归注入窗口。
- Normal 层级仍为 700，HUD / Top / System 仍为 500 / 1000 / 2000；Canvas 的相机、Sorting Layer 和 `overrideSorting` 保持原设置。
- 扫描会保留可绑定的嵌套根节点，但不进入其后代，并跳过 TMP 自动生成的子网格。映射向导按类型定位唯一配置，不依赖插件所在目录。
- 回归测试位于 `Assets/AATest/Editor/ViewFrameworkSyncTests.cs`。`AutoBindExampleWorkflow.Run` 可在独立批处理项目中调用，执行与 Inspector“重扫校验 / 生成代码”按钮相同的处理流程。

当前同步保留原框架边界：只有 Normal 入栈；`HideView` 在栈为空时仅补充关闭最后显示的窗口，并不提供完整的多层窗口管理。`Init` 更新参数不会自动通知既有 Presenter，业务仍需自行刷新。缓存视图的子容器会保留，其订阅及 `ITickable` 也需要业务按显隐状态管理。首次实例化时容器可能先于 `Init` 构建，构造函数及 `IInitializable` 不应读取尚未传入的开窗参数。这些问题留待后续单独评估。

## 示例项目里最值得看的文件

如果你是第一次接手这个项目，建议优先看这几处：

- `Scripts/Core/ComponentAutoBindTool.cs`
  运行时绑定容器本体
- `Scripts/Core/Editor/ComponentAutoBindToolInspector.cs`
  整个编辑器工作流入口
- `Scripts/Core/Editor/AutoBindHierarchyScanner.cs`
  命名扫描规则的真实实现
- `Scripts/Core/Editor/AutoBindCodeGenerator.cs`
  生成文件的真实来源
- `Scripts/ViewCore/ViewLifetimeScope.cs`
  AutoBind 与 VContainer 衔接的关键点
- `Scripts/ViewCore/ViewManager.cs`
  弹窗栈和视图调度逻辑
- `Example/Scripts/AutoBindTest.cs`
  最小可用 View 示例
- `Scripts/ViewBindComponents/AutoBindTest.BindComponents.cs`
  生成结果样例

## 校验规则

代码生成前，工具会检查：

- 已设置 `m_targetScript`
- `m_targetScript` 与 `ComponentAutoBindTool` 在同一个 `GameObject`
- Inspector 上显示的类名与目标脚本真实类名一致
- Inspector 上显示的命名空间与目标脚本真实命名空间一致
- 输出路径存在于 `Assets` 目录之下
- 绑定名称能生成合法 C# 标识符
- 生成后的字段名没有重复
- 绑定组件不为空
- 绑定组件位于当前 AutoBind 根节点层级内

这套校验让它更像一个“有约束的工作流工具”，而不是单纯的字符串扫描器。

## 当前框架状态与已知问题

以下内容不是 README 想象中的理想设计，而是当前代码库真实状态：

### 1. 这是启动框架，不是现成成熟插件

当前工程更适合作为项目母板继续开发，而不是直接当成已经完全产品化的 UPM 插件使用。

它现在同时具备三种属性：

- 一个可直接继续落业务的 Unity 项目
- 一个承载 AutoBind + VContainer 约束的基础设施框架
- 一个未来仍可继续抽离、产品化的工具基座

### 2. 配置资源被设计成单例

`AutoBindEditorAssetLocator` 会按类型查找资源，并要求项目中：

- `AutoBindGlobalSetting` 只能有一份
- `AutoBindKeyMapSetting` 只能有一份

如果有多份资源，Inspector 会直接报错，而不是做多配置管理。

### 3. `Add Component To AutoBindKeyMapSetting` 向导存在硬编码路径问题

当前向导读取的是：

```text
Assets/ComponentAutoBindTool/AutoBindKeyMapSetting.asset
```

但当前仓库里的真实资源路径是：

```text
Assets/Third Party/ComponentAutoBindTool/AutoBindKeyMapSetting.asset
```

因此在当前仓库状态下，这个右键向导大概率无法正常工作。

### 4. Build Settings 里的示例场景路径已经过期

`ProjectSettings/EditorBuildSettings.asset` 当前仍指向旧路径：

```text
Assets/ComponentAutoBindTool/Example/Scenes/Example.unity
```

实际场景路径是：

```text
Assets/Third Party/ComponentAutoBindTool/Example/Scenes/Example.unity
```

### 5. `m_UseGlobalDefaultSavePath` 的命名和行为不一致

从代码行为看，这个布尔值为 `true` 时表示：

- **跟随目标脚本路径**

而不是“使用全局默认保存路径”。

Inspector 文案已经按“跟随目标脚本的路径”展示，但字段名本身仍然容易误导维护者。

### 6. 当前仓库没有针对 AutoBind 主流程的自动化测试

项目里包含 Unity 包自带的测试工程引用，但 `Assets/Third Party/ComponentAutoBindTool` 这部分没有独立的 Editor / PlayMode 自动化测试覆盖。

## 如果基于它继续开项目，优先应该做什么

如果你的目标是把它作为启动框架继续做项目，优先级最高的工作建议是：

1. 先在 `Assets/AAGame/` 下建立自己的模块目录、启动场景与 UI 流程
2. 修掉现有硬编码路径，避免框架层继续依赖旧示例路径
3. 为扫描、校验、代码生成链路补 Editor 测试
4. 根据项目 UI 规范补充自己的前缀映射、View 基类和 Presenter 接入约束
5. 把生成路径、命名空间、业务层接入方式整理成团队规范
6. 当框架稳定后，再考虑是否抽离为独立包或继续产品化

## 适合谁使用

这个项目适合：

- 想减少 Unity UI 手工拖拽引用的团队
- 已经在项目里使用 VContainer 的团队
- 希望建立 `View -> Presenter` 清晰边界的项目
- 想把 UI 绑定生成这件事纳入规范化工作流的团队

它不太适合：

- 想直接拿一个零配置、即插即用的成熟商业插件的人
- 只需要偶尔绑定几个对象，不需要生成层和约束工作流的人
- 不打算接受命名规范约束的人

## 致谢

- 思路来源参考了 ET 框架中的 `ReferenceCollector`
- 使用了 `generic-serializable-dictionary` 支持自定义映射的可编辑序列化

## License

见根目录 `LICENSE`。
