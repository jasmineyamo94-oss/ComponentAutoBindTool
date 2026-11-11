using AAGame.Assets;
using ComponentAutoBindTool.ViewCore;

public static class LevelLoadingRoutes
{
    public static readonly ViewRoute<LevelLoadingView, EmptyViewArgs> Main = new(
        DemoAssetKeys.LevelLoading
    );
}
