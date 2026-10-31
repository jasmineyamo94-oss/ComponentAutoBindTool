using System;

namespace ComponentAutoBindTool.ViewCore
{
    /// <summary>不可变的资源键与宿主、参数契约。资源键由项目显式指定。</summary>
    public abstract class ViewRoute
    {
        public string Key { get; }
        public Type ViewType { get; }
        public Type ArgsType { get; }

        internal ViewRoute(string key, Type viewType, Type argsType)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("窗口资源键不能为空。", nameof(key));
            Key = key;
            ViewType = viewType;
            ArgsType = argsType;
        }

        internal bool ValidateArgs(IViewArgs args)
        {
            if (args != null && ArgsType.IsInstanceOfType(args)) return true;
            ViewLogger.Error($"路由 [{Key}] 参数不匹配：预期 {ArgsType.FullName}，实际 {args?.GetType().FullName ?? "null"}。");
            return false;
        }

        internal bool ValidateHost(ViewBase host)
        {
            if (host != null && ViewType.IsInstanceOfType(host)) return true;
            ViewLogger.Error($"路由 [{Key}] 宿主不匹配：预期 {ViewType.FullName}，实际 {(host != null ? host.GetType().FullName : "null")}。");
            return false;
        }

        internal bool ValidatePresenter(IWindowPresenter presenter)
        {
            Type actual = (presenter as IWindowArgsContract)?.ArgsType;
            if (actual == ArgsType) return true;
            ViewLogger.Error($"路由 [{Key}] Presenter 参数契约不匹配：预期 {ArgsType.FullName}，实际 {actual?.FullName ?? "未提供 IWindowArgsContract"}，Presenter {presenter?.GetType().FullName ?? "null"}。");
            return false;
        }
    }

    public sealed class ViewRoute<TView, TArgs> : ViewRoute
        where TView : ViewBase where TArgs : class, IViewArgs
    {
        public ViewRoute(string key) : base(key, typeof(TView), typeof(TArgs)) { }
    }
}
