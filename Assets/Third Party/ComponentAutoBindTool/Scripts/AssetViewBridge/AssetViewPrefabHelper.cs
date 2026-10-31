using System;
using System.Collections.Generic;
using System.Threading;
using ComponentAutoBindTool.AssetCore;
using UnityEngine;

namespace ComponentAutoBindTool.ViewCore
{
    public sealed class AssetViewPrefabHelper : IViewPrefabLeaseHelper, IDisposable
    {
        private readonly IAssetService m_Assets;
        private readonly Func<string, string> m_KeyResolver;
        private readonly Dictionary<string, Stack<AssetHandle<GameObject>>> m_Pending = new(StringComparer.Ordinal);
        private readonly Dictionary<int, (GameObject Instance, AssetHandle<GameObject> Handle)> m_Instances = new();
        private bool m_Disposed;
        public AssetViewPrefabHelper(IAssetService assetService, Func<string, string> keyResolver = null)
        {
            m_Assets = assetService ?? throw new ArgumentNullException(nameof(assetService));
            m_KeyResolver = keyResolver ?? (key => key);
        }
        public GameObject GetViewPrefab(string viewName)
        {
            Check();
            return Retain(viewName, m_Assets.LoadAssetSync<GameObject>(m_KeyResolver(viewName)));
        }
        public async Awaitable<GameObject> GetViewPrefabAsync(string viewName, CancellationToken cancellationToken = default)
        {
            Check();
            var handle = await m_Assets.LoadAssetAsync<GameObject>(m_KeyResolver(viewName), cancellationToken: cancellationToken);
            try
            {
                Check();
                cancellationToken.ThrowIfCancellationRequested();
                return Retain(viewName, handle);
            }
            catch { handle.Dispose(); throw; }
        }
        private GameObject Retain(string name, AssetHandle<GameObject> handle)
        {
            if (!m_Pending.TryGetValue(name, out var pending)) m_Pending.Add(name, pending = new());
            pending.Push(handle);
            return handle.Asset;
        }
        private AssetHandle<GameObject> Take(string name)
        {
            if (!m_Pending.TryGetValue(name, out var pending) || pending.Count == 0) return null;
            var handle = pending.Pop();
            if (pending.Count == 0) m_Pending.Remove(name);
            return handle;
        }
        public void RegisterViewInstance(string viewName, GameObject instance)
        {
            Check();
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            int id = instance.GetInstanceID();
            if (m_Instances.ContainsKey(id)) throw new InvalidOperationException("窗口实例已登记租约。");
            var handle = Take(viewName) ?? throw new InvalidOperationException($"窗口 {viewName} 没有待用 Prefab 租约。");
            m_Instances.Add(id, (instance, handle));
        }
        public void ReleaseUninstantiatedPrefab(string viewName) => Take(viewName)?.Dispose();
        public void ReleaseViewInstance(string viewName, GameObject instance)
        {
            if (ReferenceEquals(instance, null)) return;
            if (!m_Instances.Remove(instance.GetInstanceID(), out var owned)) return;
            try { Destroy(owned.Instance); }
            finally { owned.Handle.Dispose(); }
        }
        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            var pending = new List<AssetHandle<GameObject>>();
            foreach (var stack in m_Pending.Values) pending.AddRange(stack);
            var instances = new List<(GameObject Instance, AssetHandle<GameObject> Handle)>(m_Instances.Values);
            m_Pending.Clear();
            m_Instances.Clear();
            List<Exception> errors = null;
            foreach (var instance in instances)
            {
                try { Destroy(instance.Instance); }
                catch (Exception error) { (errors ??= new()).Add(error); }
                pending.Add(instance.Handle);
            }
            foreach (var handle in pending)
                try { handle.Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
            if (errors != null) throw new AggregateException("窗口资源释放失败。", errors);
        }
        private static void Destroy(GameObject instance)
        {
            if (instance == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(instance);
            else UnityEngine.Object.DestroyImmediate(instance);
        }
        private void Check()
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(AssetViewPrefabHelper));
        }
    }
}
