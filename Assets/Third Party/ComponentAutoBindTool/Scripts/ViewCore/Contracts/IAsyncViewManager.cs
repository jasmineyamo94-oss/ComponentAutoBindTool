using System.Threading;
using UnityEngine;

namespace ComponentAutoBindTool.ViewCore
{
    /// <summary>异步开窗是可选能力，保持既有 IViewManager 实现方兼容。</summary>
    public interface IAsyncViewManager
    {
        Awaitable<int> ShowViewAsync(string viewName, IViewArgs args = null, CancellationToken cancellationToken = default);
        Awaitable<int> ShowViewAsync(ViewRoute route, IViewArgs args, CancellationToken cancellationToken = default);
        Awaitable<int> ReplaceTopViewAsync(string viewName, IViewArgs args = null,
            UILayer layer = UILayer.Normal, CancellationToken cancellationToken = default);
        Awaitable<int> ReplaceTopViewAsync(ViewRoute route, IViewArgs args,
            UILayer layer = UILayer.Normal, CancellationToken cancellationToken = default);
    }
}
