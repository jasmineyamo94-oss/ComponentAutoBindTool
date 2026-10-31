# 资源服务接入

`AssetCore` 使用 Unity 6 原生 Awaitable，不依赖 UniTask。所有服务入口在 Unity 主线程调用；每次返回的 Awaitable 只等待一次。并发请求共享内部 Task 与原生加载，各调用方拥有独立取消令牌和句柄。取消不终止其他人的加载；全部取消时，迟到结果自动释放。

## 选择后端

| 后端 | 条件与初始化 |
|---|---|
| DirectCatalogBackend | 默认可用；构造时传入资源键到 UnityEngine.Object 的只读目录；外部目录继续拥有资产本体。 |
| AddressablesAssetBackend | 可选安装 `com.unity.addressables` 2.x，按 2.7 API 适配；独立 asmdef 自动启用。同步调用使用 WaitForCompletion，WebGL 请使用异步入口。 |
| YooAssetBackend | 可选安装 `com.tuyoogame.yooasset` 3.0.6～3.x；独立 asmdef 自动启用。接收 ResourcePackage 与可选初始化委托；组合根配置文件系统、运行模式、资源版本及清单，PackageValid 后才加载。不是 2.x 适配器。 |

无需全局手动添加 USE_ADDRESSABLES / USE_YOOASSET；符号由各适配器 asmdef 的包版本定义。源码直接放入 Assets 的非 UPM 安装需在对应适配器的 versionDefines 中按项目安装方式配置。当前项目未安装这两个可选包。

Addressables 同步加载可能等待其他活动加载，应优先异步开窗。YooAsset 的同步入口要求包和清单已经初始化。AssetService 拥有 backend 的 Dispose；YooAssetBackend 不销毁外部共享 ResourcePackage。不要让多个 AssetService 共享同一个有独占释放职责的 backend。

## 组合根注册

```csharp
// 后端选其一；离线目录也可来自现有 Prefab 配置。
builder.RegisterInstance<IAssetBackend>(new DirectCatalogBackend(catalog));
// 安装 Addressables 后可替换为：
// builder.Register<AddressablesAssetBackend>(Lifetime.Singleton).As<IAssetBackend>();
// YooAsset：new YooAssetBackend(package, InitializePackageAndManifestAsync)
builder.Register<AssetService>(Lifetime.Singleton).As<IAssetService>();
builder.Register<AssetViewPrefabHelper>(Lifetime.Singleton)
    .As<IViewPrefabHelper>().As<IAsyncViewPrefabHelper>();
builder.Register<ViewManager>(Lifetime.Scoped).AsSelf().As<IViewManager>();
```

`AssetViewPrefabHelper` 默认把窗口资源键原样用于加载，可传入 keyResolver 映射路径。无需让资源名等于宿主类名。

## 窗口与资源寿命

```csharp
int id = await manager.ShowViewAsync(EquipmentRoutes.Detail, args, cancellationToken);
// 确认新窗口准备成功后，才关闭原层栈顶：
await manager.ReplaceTopViewAsync(EquipmentRoutes.Detail, args, cancellationToken: cancellationToken);

// Presenter 本次打开的资源；关闭或下次参数刷新时释放：
var icon = await assets.LoadAssetAsync<Sprite>(args.IconKey, cancellationToken: OpenSessionToken);
AddToOpenSession(icon);
View.imgIcon.sprite = icon.Asset;
```

长期资源使用 AddToInstanceLifetime；多个资源可纳入 CreateReleaseScope，其本身同样是 IDisposable。归还句柄后 Asset/Instance 返回 null，重复 Dispose 无副作用。作用域逆序释放，某个释放失败仍继续其余清理。

实例化统一采用“加载 Prefab → Object.Instantiate / VContainer 注入 → 销毁实例并释放 Prefab”。`InstantiateSync` / `InstantiateAsync` 支持 resolver、父节点与作用域；`ReleaseInstance(gameObject)` 与句柄 Dispose 可混用，不重复释放。

窗口桥接器每次获取 Prefab 取得一份租约，实例化后由 ViewManager 登记具体实例。NonDestroy 缓存与 DestroyDelayed 等待期间继续持有资源；真实释放实例才归还。Multiple 的每个实例独立持有租约。管理器先释放，再释放 helper/service；helper 的 Dispose 用于最终兜底，不替代窗口正常关闭。

同步与异步请求同时加载同一资源时，可能短暂产生两份底层句柄，先发布的结果继续使用，多余句柄释放。Dispose 期间仍在途的加载会在完成后清理；后端最终 Dispose 延后到全部在途操作与引用结束。取消响应受第三方后端/helper 的实现影响。

## 动态列表

在父 Presenter 中调用 `CreateWidgetPool<ItemHost, ItemPresenter>(template, parent)`，池自动归入实例资源袋。`Spawn(out host)` 取出，`Despawn(presenter)` 归还；首次创建才 Resolve，归还只 Pause/隐藏，停止 Tick。池释放时解绑并销毁活动和闲置项，模板保留。

ItemPresenter 继承 WidgetPresenterBase 即具备 IWidgetVisibility。归还不结束 OpenSession，列表数据和每条目的取消令牌应由业务层按复用周期更新，避免上一次物品请求写入复用后的控件。

后端 API 依据：[Addressables 初始化](https://docs.unity3d.com/Packages/com.unity.addressables@2.7/api/UnityEngine.AddressableAssets.Addressables.InitializeAsync.html)、[同步限制](https://docs.unity3d.com/Packages/com.unity.addressables@2.7/manual/SynchronousAddressables.html)、[YooAsset ResourcePackage](https://www.yooasset.com/docs/api/YooAsset/ResourcePackage)。
