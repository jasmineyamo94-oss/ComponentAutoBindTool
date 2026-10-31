namespace ComponentAutoBindTool.ViewCore
{
    /// <summary>可选的路由能力；原 IViewManager 实现方无需增加成员。</summary>
    public interface IViewRouteManager
    {
        int ShowView(ViewRoute route, IViewArgs args, bool immediately = true);
        void EnqueueView(ViewRoute route, IViewArgs args, int priority = 0);
        int ReplaceTopView(ViewRoute route, IViewArgs args, UILayer layer = UILayer.Normal);
        bool CloseView(ViewRoute route);
    }
}
