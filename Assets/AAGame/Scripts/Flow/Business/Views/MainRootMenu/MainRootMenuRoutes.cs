using AAGame.Assets;
using ComponentAutoBindTool.ViewCore;

public static class MainRootMenuRoutes
{
    public static readonly ViewRoute<MainRootMenuView, EmptyViewArgs> Main = new(
        DemoAssetKeys.MainRootMenu
    );
}
