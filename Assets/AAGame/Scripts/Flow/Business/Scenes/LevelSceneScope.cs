using VContainer;
using VContainer.Unity;

public sealed class LevelSceneScope : LifetimeScope
{
    public LevelLoadRequestContent Request { get; set; }

    protected override LifetimeScope FindParent() => LifetimeScope.Find<GameRootLifetimeScope>();

    protected override void Configure(IContainerBuilder builder)
    {
        builder.RegisterSceneViewArchitecture(true);
        builder.Register<LevelGameplaySession>(Lifetime.Scoped);
        builder.RegisterInstance(
            Request ?? throw new System.InvalidOperationException("缺少关卡请求")
        );
    }
}
