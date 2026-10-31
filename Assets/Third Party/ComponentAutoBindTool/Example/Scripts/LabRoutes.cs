using ComponentAutoBindTool.ViewCore;

namespace ComponentAutoBindTool.Example
{
    /// <summary>同一宿主类型对应八个独立资源键及窗口策略。</summary>
    public static class LabRoutes
    {
        public static readonly ViewRoute<AutoBindTest, LabOpenArgs> Normal = new("AutoBindTest");
        public static readonly ViewRoute<AutoBindTest, LabOpenArgs> Cover = new("Cover");
        public static readonly ViewRoute<AutoBindTest, LabOpenArgs> Multi = new("Multi");
        public static readonly ViewRoute<AutoBindTest, LabOpenArgs> Delayed = new("Delayed");
        public static readonly ViewRoute<AutoBindTest, LabOpenArgs> HUD = new("HUD");
        public static readonly ViewRoute<AutoBindTest, LabOpenArgs> Top = new("Top");
        public static readonly ViewRoute<AutoBindTest, LabOpenArgs> System = new("System");
        public static readonly ViewRoute<AutoBindTest, LabOpenArgs> Widgets = new("Widgets");

        public static ViewRoute<AutoBindTest, LabOpenArgs> Find(string key) => key switch
        {
            "AutoBindTest" => Normal,
            "Cover" => Cover,
            "Multi" => Multi,
            "Delayed" => Delayed,
            "HUD" => HUD,
            "Top" => Top,
            "System" => System,
            "Widgets" => Widgets,
            _ => null
        };
    }
}
