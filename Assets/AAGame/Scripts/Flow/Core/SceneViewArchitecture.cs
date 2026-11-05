using System;
using ComponentAutoBindTool.AssetCore;
using ComponentAutoBindTool.ViewCore;
using VContainer;
using VContainer.Unity;

public sealed class SceneViewArchitecture : IDisposable, ITickable
{
    public readonly ViewManager Manager;
    public readonly IAssetReleaseScope Resources;
    private readonly AssetViewPrefabHelper _helper;
    private readonly StageViewRootHelper _root;

    public SceneViewArchitecture(
        IObjectResolver resolver,
        AssetService assets,
        StageViewRootHelper root,
        int offset,
        string name,
        IAssetReleaseScope resources = null
    )
    {
        _root = root;
        _root.Resume();
        Resources = resources ?? assets.CreateReleaseScope(name);
        _helper = new AssetViewPrefabHelper(assets);
        Manager = new ViewManager(resolver, _helper, root, offset);
        Manager.OnViewOpened += _root.Observe;
    }

    public void Tick() => Manager.Tick();

    public void Dispose()
    {
        Manager.OnViewOpened -= _root.Observe;
        try
        {
            Manager.Dispose();
        }
        finally
        {
            try
            {
                _helper.Dispose();
            }
            finally
            {
                Resources.Dispose();
                _root.Resume();
            }
        }
    }
}

public static class SceneViewContainerBuilderExtensions
{
    public static void RegisterSceneViewArchitecture(this IContainerBuilder builder, bool level)
    {
        // ExistingInstanceProvider 不单独 Dispose；唯一所有者是 SceneViewArchitecture。
        var resources = new AssetReleaseScope(level ? "Level" : "Main");
        builder.RegisterInstance<IAssetReleaseScope>(resources);
        builder.Register(
            r => new SceneViewArchitecture(
                r,
                r.Resolve<AssetService>(),
                level
                    ? r.Resolve<GlobalUIRootRegistry>().Level
                    : r.Resolve<GlobalUIRootRegistry>().Main,
                level ? 8000 : 0,
                level ? "Level" : "Main",
                resources
            ),
            Lifetime.Scoped
        );
        builder
            .Register(r => r.Resolve<SceneViewArchitecture>().Manager, Lifetime.Scoped)
            .AsSelf()
            .As<IViewManager>()
            .As<IViewRouteManager>()
            .As<IAsyncViewManager>()
            .As<IViewManagerDiagnostics>();
        builder.RegisterEntryPoint<SceneViewTick>();
    }

    private sealed class SceneViewTick : ITickable
    {
        private readonly SceneViewArchitecture _views;

        public SceneViewTick(SceneViewArchitecture views) => _views = views;

        public void Tick() => _views.Tick();
    }
}
