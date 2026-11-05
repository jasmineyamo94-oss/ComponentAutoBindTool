# 场景 Flow 演示

使用 Unity 6000.3.20f1，打开 `Scenes/BootstrapScene.unity` 运行。开始页进入主界面，主界面进入关卡；关卡支持原地重开与返回。Main 在关卡期间只隐藏渲染和交互，仍继续 Tick。

- 根容器与全局 Overlay 常驻，各业务场景独立管理窗口和资源租约；Level 请求在延迟 Build 前注入。
- `Settings/FlowCatalog.asset` 通过路由 Key 映射新 UI Prefab。默认使用 DirectCatalogBackend，目录外部继续持有原始资产。
- 旧 PSD 美术 Prefab 保留；旧 LevelMenuView、旧绑定与专属导出配置已移除。新关卡窗口为 LevelHudView。
- 业务场景直接 Play 由 Editor 桥接到 Bootstrap，再进入原目标阶段；退出恢复原编辑设置。
- 菜单 `AAGame/场景流程/生成演示资产` 会重建本演示场景与四个新 UI Prefab，覆盖这些演示资产的人工编辑。绑定调用正式 AutoBindBuildPipeline.GenerateCode，不直接编写生成文件。

`Scripts/Flow/Core/` 存放场景加载和切换协调、Loading、UI 舞台与视图注册等流程基础代码；`Scripts/Flow/Business/` 存放本游戏的场景 Scope、关卡状态与会话、各演示界面和 Presenter。每个窗口的宿主与生成绑定位于同一窗口目录。当前协调器、Loading 和部分舞台注册仍引用本游戏的具体场景或路由，目录划分表示职责，不代表它们已脱离业务依赖。

## 验证

进入 Bootstrap Play 后调用 `SceneFlowValidation.Run()`，结果写入忽略的 `Temp/SceneFlow/result.txt`。验证连续三轮关卡进入/重开/返回、场景请求注入、会话复用、Main 实例和计时连续性、管理器释放以及唯一全局对象。手工用真实按钮重复验证。

本演示不包含真实战斗、存档或联网；排序偏移只新增到 ViewManager，原三参数构造维持偏移 0。

2026-11-05 验证：真实流程连续三轮通过；`SceneFlowFailureValidation.Run()` 覆盖加载、Scope、取消、卸载、重开、重复请求、Loading 缺失及舞台状态恢复，结果在 `Temp/SceneFlow/failures.txt`。原框架套件 272 项首次为 266 通过、6 失败，定位并修复 VContainer 构造函数选择后，6 项实验台回归全部通过；新增排序、构造兼容与资源释放顺序测试 3/3 通过。Android Player 脚本编译输出 51 个程序集。

已验证直接从 Start/Main/Level 启动时的 Bootstrap 桥接，以及 1024×768、1920×1080、2560×1080 的实际 GameView 渲染。演示使用独立的 `FlowChinese` 静态 TMP 图集。修改演示文本后，如新增图集未包含的字符，应重新生成字体图集；不要改动框架实验台的字体。
