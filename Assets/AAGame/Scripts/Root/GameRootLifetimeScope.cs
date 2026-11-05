using ComponentAutoBindTool.AssetCore;
using VContainer;
using VContainer.Unity;

public class GameRootLifetimeScope : LifetimeScope
{
    public FlowCatalog Catalog;
    public GlobalUIRootRegistry UIRoots;

    protected override void Configure(IContainerBuilder builder)
    {
        builder.RegisterInstance(UIRoots);
        builder.RegisterInstance<IAssetBackend>(new DirectCatalogBackend(Catalog.CreateCatalog()));
        builder.Register<AssetService>(Lifetime.Singleton).AsSelf().As<IAssetService>();
        builder.Register<FlowState>(Lifetime.Singleton);
        builder.Register<SceneLoaderService>(Lifetime.Singleton).As<ISceneLoader>();
        builder.Register<LoadingOverlayService>(Lifetime.Singleton);
        builder
            .Register<SceneFlowCoordinator>(Lifetime.Singleton)
            .AsSelf()
            .As<ISceneFlowCoordinator>();
        builder.RegisterEntryPoint<GameColdBootEntryPoint>();
        builder.RegisterEntryPoint<OverlayTick>();
    }

    private sealed class OverlayTick : ITickable
    {
        private readonly LoadingOverlayService _overlay;

        public OverlayTick(LoadingOverlayService overlay) => _overlay = overlay;

        public void Tick() => _overlay.Tick();
    }
}
