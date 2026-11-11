# 互斥资源后端接入

本项目支持 Resources、Addressables、YooAsset。切换完成后只保留当前后端所需 SDK，默认 Resources。场景始终通过 Build Settings 和 SceneManager 加载。

## 一键切换

退出 Play 后，打开 **Tools → 项目工具 → 资源后端与环境**。窗口显示当前生效后端，在“目标后端”中选择 Resources、Addressables 或 YooAsset，然后点击“切换并准备资源”。只改变下拉框不会写入配置，也不会立即安装或卸载。

“检查并修复当前后端”针对当前生效配置补齐依赖和内容，不使用尚未提交的目标选择；“更新当前后端 SDK”才会明确查询新版本。Resources 为内置能力，更新按钮禁用并提供说明，仍可修复/同步资源。

| 目标 | 完成后保留的 SDK |
|---|---|
| Resources | 无资源管理 SDK |
| Addressables | Addressables、Addressables for Android |
| YooAsset | YooAsset |

窗口中的切换、修复和更新统一进入 `BackendSwitchCoordinator`，先预览实际操作清单，再准备目标依赖、归档旧配置、卸载旧 SDK、恢复目标配置和构建当前平台内容。只有最终编译和资源准备成功才提交后端选择。切换中禁止 Play 和重复任务；准备新 SDK 时可以短暂共存，完成态必须互斥。

同一后端在环境完整、源资源及构建平台未变化时不重复安装或构建。修改资源、配置或目标平台后，点击“检查并修复当前后端”重新准备资源。SDK 安装缺失时查询当前兼容最新正式版；明确更新时重新查询。回滚使用操作前实际版本恢复原状态。

## 环境检测与安装

打开 **Tools → 项目工具 → 资源后端与环境**。检测会执行 Node.js、npm、OpenUPM、WinGet 的版本命令，并通过 UPM 查询项目依赖。

切换、修复和更新操作统一走完整互斥流程，窗口提供三种目标后端，没有同时安装全部演示 SDK 的按钮。选择 Addressables 时自动准备主包和 Android 扩展；选择 YooAsset 时通过 OpenUPM 国际源安装最新正式版。Node.js/npm 和 OpenUPM 仍可单独安装，但与项目切换任务串行互斥。

基础工具已满足要求时复用。缺少 Node.js 时优先 WinGet 安装最新 LTS，没有 WinGet 时使用官方安装器并验证 SHA-256 与签名。安装后重新发现路径；进程日志和持久状态支持程序集重载恢复。Windows UAC 仍由用户授权，需要重启时暂停后续步骤，不自动重启系统。

目标选择跨程序集重载保留；任务运行时显示并锁定实际任务目标，Play、切换任务或待恢复状态禁止冲突操作。成功后选择同步到当前后端，失败保留请求目标供检查。闲置时不会每次刷新覆盖用户尚未执行的选择。窗口继续提供环境检测、Node.js/npm 与 OpenUPM 工具入口、取消、重试和日志。

### Unity 第三方包签名告知

通过 OpenUPM 国际源安装 YooAsset 时，本次实测出现的模态框标题为 **Missing Signature**，按钮为 **Open Package Manager** 和 **Close**。这是未签名包告知及是否打开 Package Manager 的选择，没有“信任”或“确认安装”按钮。它会暂停编辑器的正常更新，工具进度或远程命令可能暂时没有响应；需要时从任务栏恢复正确的 Unity 编辑器窗口查看。

用户核对来源后，可选择 **Open Package Manager** 查看包详情，或通过 **Close / Escape** 关闭该告知。关闭告知不更改安全检查，也不表示授予包信任。本工具不绕过签名检查；如果之后出现其它实际确认或安全提示，应按其具体内容核对和处理。用户拒绝其它安装步骤后若目标包缺失，仍按现有失败与回滚流程处理。

本次主线程调用栈的等待链为 `InProjectPackagesMonitor.CheckForUntrustedPackages → CustomDisplayDialog.Show → ShowModal`；方法名不能代替实际弹窗内容。本文历史验证记录中的“包信任确认”是当时对模态等待的称呼，应按上述实际界面解释。YooAsset 安装后的 AddRequest 与注册快照核验用于保持 UPM 操作顺序和观察精确版本，不代表消除了 Unity 的包告知窗口。

## 归档与资源位置

`ResourceBackendProfiles/` 位于工程根目录、Assets 之外，保存非当前后端的配置和原始 `.meta`，可由 Git 管理。恢复时保留 GUID，不重新创建原配置。

- Addressables：配置移出 Assets；Build Settings 的 Addressables 配置对象引用同步移除，切回时恢复。
- YooAsset：收集配置移出 Assets，切回后恢复同一配置。
- Resources：仅演示生成副本在 Resources 模式下放入 `Assets/AAGame/Resources/AAGameDemo/`。使用 SDK 后端时移出，避免重复打包；切回时恢复并从原始资源同步。
- 清理只作用于工具明确管理的生成产物，不清理 Unity 全局包缓存，不卸载 Node.js/npm/OpenUPM。共享包依赖由 UPM 解析。

原始 UI Prefab 保留在 `Assets/AAGame/UI/Prefabs/`，普通资源源文件位于 `Assets/AAGame/Content/`。适配器源码和条件程序集保留，通过 asmdef 自动宏随 SDK 安装状态参与编译。业务 Presenter 始终使用 `IAssetService`；统一 Key 为 `UI_StartMenuView`、`UI_MainRootMenuView`、`UI_LevelHudView`、`UI_LevelLoadingView` 和 `Data_DemoText`。调用方使用 `AAGame.Assets.DemoAssetKeys`，不在业务代码中拼接后端物理路径。

### Key 与资源分组规范

三种后端使用同一套 `UI_*` / `Data_*` Key。YooAsset 采用内置 `AddressByGroupAndFileName`，UI、Data 分组前缀与源文件名组合成上述 Key；Addressables 对应收集到 `AAGameDemo-UI`、`AAGameDemo-Data` 分组，并设置相同 Address。配置前调用 `AAGame.Editor.Assets.DemoAssetKeySetup.ValidateUniqueKeys()`，存在重复 Key 时先报错，不覆盖既有配置。

Resources 副本扁平放在 `Assets/AAGame/Resources/AAGameDemo/`，例如 `UI_StartMenuView.prefab`、`Data_DemoText.txt`。运行时 Resources 后端加上 `AAGameDemo/` 前缀；业务仍只传统一 Key。原始 Prefab、绑定代码和源文件路径保持不变。

旧 `UI/StartMenuView` 等斜杠 Key 已迁移为新规范，没有运行时旧 Key 别名。恢复旧 Resources 归档并同步时，先通过 `AssetDatabase.MoveAsset` 迁移既有 `UI/*.prefab` 和 `Data/DemoText.txt`，保留其 GUID 与 `.meta`；新旧副本同时存在时拒绝覆盖并提示核对，全部冲突检查通过后才开始移动。只移除已经空的旧 UI/Data 目录，其它文件保留。迁移函数不生成缺失副本，当前 SDK 模式也不能借手动同步重新生成 Resources 副本。

迁移完成后，通过统一窗口“检查并修复当前后端”重建 Addressables/YooAsset 的本地内容；Resources 模式则同步扁平副本。再执行当前后端的三轮 Flow 验证并核对生成副本 GUID，确认没有重复 Key 或斜杠 Key 残留。

资源 SDK 的配置/构建菜单仍位于 **Tools → 项目工具 → 资源后端 → Addressables / YooAsset**，仅在对应 SDK 已安装时出现。通常完整切换会自动构建，无需额外手动操作。

## 失败、取消与恢复

切换前检测相关未保存场景和配置，允许保存后继续或取消。归档完成校验后才清理原位置；操作快照用于恢复依赖、配置、GUID 和原后端选择。

安装、卸载、归档、恢复、编译或构建失败会进入回滚；取消等待已启动操作结束后再恢复。若网络等原因阻止回滚，保留归档与恢复任务，环境窗口显示待恢复状态，可以重试，不会把未完成切换报告为成功。

Addressables 的 `Use Asset Database` 在编辑器中通过有效的设置资产 GUID 初始化，直接加载工程资源，不要求本地构建内容；无效 GUID、资产丢失或类型错误会在 SDK 初始化前报“编辑器设置资产无效”，修正后可重试。`Use Existing Build` 和 Player 启动前仍预检本地 settings.json 与 Catalog，缺失内容可补齐后重试。构建工具仍默认选择 Existing Build，以验证真实资源包；手动选择 Asset Database 仅影响编辑器取数方式，后端仍是 Addressables。

若已经进入 SDK 且 SDK 内部初始化失败，当前 SDK 无公开完整重置入口，需要退出 Play 后重新运行。YooAsset 普通释放只销毁自有包，应用/Play 退出时由 SDK 全局退出流程收尾。

## 验证

**Tools → 项目工具 → 资源后端 → 验证 → 依次运行三种后端三轮流程** 会逐项等待完整互斥切换，再进入 Play 验证三轮流程；结束时恢复原后端依赖与配置。普通编辑器必须保持 Game 视图可渲染，batch 不执行 Loading 所需的帧末渲染等待。

当前后端验证检查并发资源加载、预取消、错误类型、缺失 Key、重复释放、租约回收以及 Main 连续计时、关卡请求、重开复用、Loading 清理和唯一全局对象。退出 Play 后还会检查生命周期错误日志。证据保留在 `Library/AAGameValidation/`。

运行 **验证 → 编译当前目标 Player 脚本** 可检查 Player 侧不包含 Editor 程序集。Editor 变更还需覆盖安装、卸载、归档、恢复、构建失败及取消/重复点击/程序集重载，并测试六个切换方向。

日常验收使用上述互斥三后端流程。`Editor/Verification/ResourceBackendMatrix.ps1` 是旧共存模式的兼容性夹具工具，仅供检查程序集在不同包组合中的编译，不应为运行它而在主工程同时安装全部 SDK。先将主工程正常切到 Resources，以便生成无 SDK 运行所需副本，再通过可选参数 `-SdkManifestPath '独立测试夹具/manifest.json'` 提供已有的 SDK 版本及注册源声明；可使用之前保存的完整测试 manifest。脚本只读取该文件，把 SDK 声明写入新建的隔离副本，主工程仍保持互斥。它不查询或写死 SDK 版本，也不执行主工程安装。只测 `-Combinations None` 时无需完整 SDK 夹具。共存组合的结果不作为项目正式完成状态。

缺工具、无 WinGet、UAC 拒绝及重启等安装分支仍需干净 Windows 虚拟机实测，不得用本机已有工具的复用结果冒充安装证明。

## 历史验证记录（2026-11-09，Key 规范迁移前）

- 六个切换方向已实际执行，各完成状态均通过三轮 Flow，并核验最终 SDK 集合互斥，证据为 `Library/AAGameValidation/exclusive-switches.txt`。初期 YooAsset 安装曾停在 Unity 第三方包信任模态框，经定位、重启恢复和用户确认后，后续 Resources → YooAsset 直接切换成功。这不是全程无人值守的安装证明，也没有消除或绕过 Unity 信任确认。
- 在 `archive`、`remove`、`restore`、`build`、`commit`、`installWait` 六个阶段注入故障，均恢复到原 Resources 配置、实际 SDK 为零且无待恢复任务。取消测试等待到了实际 AddRequest 的 `verify` 阶段；重复切换请求被拒绝。证据为 `Library/AAGameValidation/switch-failures.txt`。
- 另行验证 Addressables → Resources 在实际卸载完成后的 `removeWait` 阶段失败，恢复了原 Addressables 4.2.0、Android 扩展 1.1.0、配置 GUID、Build Settings 引用和可运行内容。这里的版本号是该次回滚快照，不是安装策略的固定版本；证据为 `Library/AAGameValidation/remove-after-uninstall.txt`。
- 本框架相关回归共 293 项，293 通过、0 失败，结果为 `Library/AAGameValidation/framework-tests-final.json`。
- Addressables 单 SDK 状态的 Android Player 脚本编译通过，证据为 `Library/AAGameValidation/player-compile-Addressables.txt`。
- Resources 同后端源文件变更使就绪指纹失效并重新同步，生成副本 GUID 保持不变；测试源文件已逐字节恢复。重复选择完整 Resources 后端未重写副本，也未重复安装或构建。证据为 `Library/AAGameValidation/resources-refresh-idempotency.txt`。
- 在真实环境预扫描 `_preparing` 期间请求取消，扫描完成后没有覆盖取消标记、没有继续安装 SDK，Resources 保持可用且无残留任务。证据为 `Library/AAGameValidation/cancel-preparing.txt`。
- C# 冻结后的 `EnvironmentInstallerTests` 复测 8/8 通过，证据为 `Library/AAGameValidation/environment-tests-final.json`。
- Resources 单后端的 Android Player 脚本编译通过，输出不含 `Unity.Addressables`、`Unity.ResourceManager`、`YooAsset` 或本项目的 SDK 适配程序集，保留 UniTask、VContainer。`UniTask.Addressables` 是 UniTask 自带的条件程序集，其 SDK 实现已由宏关闭；程序集文件名不代表 Addressables SDK 残留，不能为清理它而删除 UniTask。证据为 `Library/AAGameValidation/player-compile-Resources.txt`。
- 最终环境窗口从已注册包快照显示三项 SDK 均未安装；Node.js、npm、OpenUPM、WinGet 均经版本命令核验可用。证据为 `Library/AAGameValidation/environment-window-final.json`。
- 最终收尾再次运行 Resources 三轮 Flow，通过后退出 Play 并恢复原 Play 启动与后台运行设置。当前停留在 BootstrapScene，资源就绪、SDK 数量为零、无编译错误或待恢复事务；UniTask 2.5.10、VContainer 1.17.0 保持原版本。证据为 `Library/AAGameValidation/exclusive-switches.txt` 和 `Library/AAGameValidation/final-state.json`。

以上阶段故障属于可控注入，不等于真实网络中断、权限拒绝或磁盘故障的系统实测。干净 Windows 中缺失工具、无 WinGet、UAC 拒绝和重启分支，以及真实网络失败，仍保留未实测限制。

## 统一窗口改造验收（2026-11-09）

新入口为 `Tools/项目工具/资源后端与环境`，旧窗口菜单与三个“使用后端”菜单已移除，程序调用入口保留。窗口状态验证覆盖目标选择不提交配置、真实程序集重载保留选择、Resources 更新禁用、活动目标锁定以及失败回滚后的生效后端与请求目标区分，证据为 `Library/AAGameValidation/unified-window.txt`。

本次重新执行三后端互斥切换及各三轮 Flow，退出 Play 无错误日志，最后恢复 Resources（`three-backends.txt`）。YooAsset 安装的 Unity 包信任确认由用户完成。验证工具现会等待自动环境检测结束，避免与切换互斥门禁竞争。

缺失资源测试发现并修复了同 Resources 后端旧归档与新副本冲突的问题：仅从校验过的归档补回缺失文件及 `.meta`，保留现有文件，再从源资源同步；跨后端仍保留冲突检查。缺失文件恢复后的内容与 GUID、同后端就绪时不重复构建均已验证。环境定向回归 8/8、归档回归（含新增缺失修复用例）7/7 通过，证据为 `unified-environment-tests.json`、`unified-profile-tests.json`。Android Player 脚本编译通过，证据为 `unified-player-compile.txt`。上述文件均位于 `Library/AAGameValidation/`。

## Addressables 编辑器 GUID 初始化修复（2026-11-09，Key 规范迁移前）

已先复现有效 `GUID:` 被当作文件路径的错误，再验证修复后的两条初始化路径：移走整个工具管理的本地构建目录后，Asset Database 模式仍完成三轮 Flow；恢复原构建目录后，Existing Build 模式也完成三轮真实资源包流程。证据为 `Library/AAGameValidation/guid-fast-mode.txt`、`guid-existing-build.txt`。

预检回归 7/7（含无效 GUID/错误类型修复后重试、取消和缺 Catalog 重试），AssetCore 10/10、AssetBackend 4/4 通过。主工程 Android Player 编译，以及不含资源 SDK 的隔离工程 Editor/Player 编译均通过；运行时程序集没有新增 Editor SDK 引用。证据为同目录 `guid-preflight-tests.json`、`guid-assetcore-tests.json`、`guid-assetbackend-tests.json`、`guid-player-compile.txt` 和 `guid-nosdk-*/result.json`。

最终保留 Addressables 后端与原 Asset Database 选择，原构建目录、编辑场景及 Play 启动设置已恢复。`all_code.txt`、manifest、packages-lock 的任务前后 SHA-256 一致，未安装或卸载 SDK，也未变更用户当前的 UniTask、VContainer 版本。

## 内置规则与统一 Key 验收（2026-11-10）

YooAsset 已移除自定义 `DemoLogicalAddress`，UI/Data 两组采用内置 `AddressByGroupAndFileName`；UI 仅收集 Prefab。Addressables 使用 `AAGameDemo-UI`、`AAGameDemo-Data`，启用分组层级显示。三种后端的四个窗口和演示文本统一使用 `DemoAssetKeys` 的五个下划线 Key，未增加运行时地址转换或旧 Key 别名。

Resources 与 Addressables 在迁移后各通过三轮真实 Flow，证据为 `Library/AAGameValidation/three-backends.txt`。该轮自动验证随后因 Unity 签名告知阻塞而超时回滚，保留了原始失败记录；关闭告知并重新执行互斥切换后，YooAsset 本地内容重新构建，三轮 Flow 通过，退出 Play 至 Player 编译期间无异常，证据为 `builtin-yoo-flow.txt`、`builtin-yoo-exit-log.txt`。首次过早发起验证时尚在冷启动切换，工具明确拒绝；就绪后重试通过，初次记录保留为 `builtin-yoo-early-validation.txt`。不能将这次分段验证描述为全程无人值守成功。

收尾定向回归共 28/28 通过：归档 12、Key 唯一性 2、资源后端 4、AssetCore 10，结果为 `builtin-*-tests-final.json`。覆盖旧归档校验、迁移幂等、冲突拒绝、同组重名拒绝及不同组同名区分；Android Player 脚本编译通过（`builtin-player-compile.txt`）。

三份归档引用的负载 SHA-256 全部通过校验；Resources 五份资源 `.meta` 与迁移前快照逐字节相同。原始 Prefab、生成绑定及 `all_code.txt` 共 17 个文件保持原 SHA-256，见 `builtin-preservation-final.json`。最终保留 YooAsset 后端和单 SDK 状态，UniTask 2.5.11、VContainer 1.19.0 未变；编辑器退出 Play，恢复验证前的后台运行和启动场景设置，无活动切换或待恢复任务。

所有操作只针对本次资源后端工作，不自动提交或推送代码。
