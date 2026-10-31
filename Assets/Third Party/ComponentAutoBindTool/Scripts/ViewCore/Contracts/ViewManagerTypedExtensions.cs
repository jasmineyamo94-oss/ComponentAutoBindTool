using System;
using System.Threading;
using UnityEngine;

namespace ComponentAutoBindTool.ViewCore
{
    public static class ViewManagerTypedExtensions
    {
        public static int ShowView<TView, TArgs>(this IViewManager manager,
            ViewRoute<TView, TArgs> route, TArgs args, bool immediately = true)
            where TView : ViewBase where TArgs : class, IViewArgs =>
            RequireRoutes(manager).ShowView(route, args, immediately);

        public static int ShowView<TView>(this IViewManager manager,
            ViewRoute<TView, EmptyViewArgs> route, bool immediately = true)
            where TView : ViewBase => RequireRoutes(manager).ShowView(route, EmptyViewArgs.Instance, immediately);

        public static void EnqueueView<TView, TArgs>(this IViewManager manager,
            ViewRoute<TView, TArgs> route, TArgs args, int priority = 0)
            where TView : ViewBase where TArgs : class, IViewArgs =>
            RequireRoutes(manager).EnqueueView(route, args, priority);

        public static int ReplaceTopView<TView, TArgs>(this IViewManager manager,
            ViewRoute<TView, TArgs> route, TArgs args, UILayer layer = UILayer.Normal)
            where TView : ViewBase where TArgs : class, IViewArgs =>
            RequireRoutes(manager).ReplaceTopView(route, args, layer);

        public static bool CloseView<TView, TArgs>(this IViewManager manager, ViewRoute<TView, TArgs> route)
            where TView : ViewBase where TArgs : class, IViewArgs => RequireRoutes(manager).CloseView(route);

        public static Awaitable<int> ShowViewAsync(this IViewManager manager, string viewName,
            IViewArgs args = null, CancellationToken cancellationToken = default) =>
            RequireAsync(manager).ShowViewAsync(viewName, args, cancellationToken);

        public static Awaitable<int> ShowViewAsync<TView, TArgs>(this IViewManager manager,
            ViewRoute<TView, TArgs> route, TArgs args, CancellationToken cancellationToken = default)
            where TView : ViewBase where TArgs : class, IViewArgs =>
            RequireAsync(manager).ShowViewAsync(route, args, cancellationToken);

        public static Awaitable<int> ShowViewAsync<TView>(this IViewManager manager,
            ViewRoute<TView, EmptyViewArgs> route, CancellationToken cancellationToken = default)
            where TView : ViewBase => RequireAsync(manager).ShowViewAsync(route, EmptyViewArgs.Instance, cancellationToken);

        public static Awaitable<int> ReplaceTopViewAsync(this IViewManager manager, string viewName,
            IViewArgs args = null, UILayer layer = UILayer.Normal, CancellationToken cancellationToken = default) =>
            RequireAsync(manager).ReplaceTopViewAsync(viewName, args, layer, cancellationToken);

        public static Awaitable<int> ReplaceTopViewAsync<TView, TArgs>(this IViewManager manager,
            ViewRoute<TView, TArgs> route, TArgs args, UILayer layer = UILayer.Normal,
            CancellationToken cancellationToken = default)
            where TView : ViewBase where TArgs : class, IViewArgs =>
            RequireAsync(manager).ReplaceTopViewAsync(route, args, layer, cancellationToken);

        private static IAsyncViewManager RequireAsync(IViewManager manager)
        {
            if (manager == null) throw new ArgumentNullException(nameof(manager));
            return manager as IAsyncViewManager ?? throw new NotSupportedException(
                $"{manager.GetType().FullName} 未实现 IAsyncViewManager。");
        }

        private static IViewRouteManager RequireRoutes(IViewManager manager)
        {
            if (manager == null) throw new ArgumentNullException(nameof(manager));
            return manager as IViewRouteManager ?? throw new NotSupportedException(
                $"{manager.GetType().FullName} 未实现 IViewRouteManager，无法执行类型化路由。");
        }
    }
}
