using System.Threading;
using UnityEngine;

namespace ComponentAutoBindTool.ViewCore
{
    public interface IAsyncViewPrefabHelper : IViewPrefabHelper
    {
        Awaitable<GameObject> GetViewPrefabAsync(string viewName, CancellationToken cancellationToken = default);
        void ReleaseUninstantiatedPrefab(string viewName);
    }

    /// <summary>实例化后将待用租约转交给具体实例，按实例而非名字归还。</summary>
    public interface IViewPrefabLeaseHelper : IAsyncViewPrefabHelper
    {
        void RegisterViewInstance(string viewName, GameObject instance);
    }
}
