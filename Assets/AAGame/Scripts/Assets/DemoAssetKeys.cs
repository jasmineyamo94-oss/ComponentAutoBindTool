namespace AAGame.Assets
{
    /// <summary>三种资源后端共用的逻辑地址，不包含物理目录或扩展名。</summary>
    public static class DemoAssetKeys
    {
        public const int ConventionVersion = 2;
        public const string StartMenu = "UI_StartMenuView";
        public const string MainRootMenu = "UI_MainRootMenuView";
        public const string LevelHud = "UI_LevelHudView";
        public const string LevelLoading = "UI_LevelLoadingView";
        public const string DemoText = "Data_DemoText";
        public static string Ui(string name) => "UI_" + name;
        public static string Data(string name) => "Data_" + name;
    }
}
