using System;

namespace ComponentAutoBindTool.ViewCore
{
    public interface IViewManager
    {
        event Action<IWindowContext> OnViewOpened;
        event Action<IWindowContext> OnViewClosed;

        /// <summary>
        /// 强类型参数开窗主入口（推荐）
        /// </summary>
        int ShowView(string viewName, IViewArgs args = null, bool immediately = true);

        /// <summary>
        /// 兼容旧版 params object[] 开窗入口
        /// </summary>
        int ShowView(string viewName, bool immediately, params object[] args);

        /// <summary>
        /// 事务安全地替换指定层级的栈顶窗口（解决 A-P07：新窗口准备成功后才关闭旧栈顶）
        /// </summary>
        int ReplaceTopView(string viewName, IViewArgs args = null, UILayer layer = UILayer.Normal);

        /// <summary>
        /// 按唯一 InstanceId 精确关闭窗口
        /// </summary>
        bool CloseView(int instanceId);

        /// <summary>
        /// 按窗口名关闭窗口（若有多个同名实例，优先关闭最顶层实例）
        /// </summary>
        bool CloseView(string viewName);

        /// <summary>
        /// 导航返回：按 System -> Top -> Normal 顺序关闭最顶层允许返回的窗口（解决 A-P01）
        /// </summary>
        bool TryNavigateBack();

        /// <summary>
        /// 关闭所有已打开窗口（可选是否连同 NonDestroy 缓存一并销毁）
        /// </summary>
        void CloseAllViews(bool destroyCached = false);

        bool IsViewOpen(string viewName);
    }
}