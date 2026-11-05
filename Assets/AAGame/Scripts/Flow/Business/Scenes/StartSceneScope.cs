using VContainer;
using VContainer.Unity;

public sealed class StartSceneScope : LifetimeScope
{
    public LevelLoadRequestContent Request { get; set; }

    protected override LifetimeScope FindParent() => LifetimeScope.Find<GameRootLifetimeScope>();

    protected override void Configure(IContainerBuilder builder)
    {
        builder.RegisterSceneViewArchitecture(false);
    }
}
