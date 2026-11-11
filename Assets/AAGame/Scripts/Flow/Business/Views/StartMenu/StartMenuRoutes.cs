using AAGame.Assets;
using ComponentAutoBindTool.ViewCore;

public static class StartMenuRoutes
{
    public static readonly ViewRoute<StartMenuView, EmptyViewArgs> Main = new(DemoAssetKeys.StartMenu);
}
