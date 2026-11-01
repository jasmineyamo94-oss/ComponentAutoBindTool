using System;
using System.Collections.Generic;
using System.Threading;
using ComponentAutoBindTool.AssetCore;
using UnityEngine;

namespace ComponentAutoBindTool.ViewCore
{
    public sealed class AssetViewPrefabHelper : IViewPrefabLeaseHelper, IDisposable
    {
        private readonly IAssetService _assets;
        private readonly Func<string, string> _keyResolver;
        private readonly Dictionary<string, Stack<AssetHandle<GameObject>>> _pending = new(StringComparer.Ordinal);
        private readonly Dictionary<int, (GameObject Instance, AssetHandle<GameObject> Handle)> _instances = new();
        private bool _disposed;
        public AssetViewPrefabHelper(IAssetService assetService, Func<string, string> keyResolver = null)
        {
            _assets = assetService ?? throw new ArgumentNullException(nameof(assetService));
            _keyResolver = keyResolver ?? (key => key);
        }
        public GameObject GetViewPrefab(string viewName)
        {
            Check();
            return Retain(viewName, _assets.LoadAssetSync<GameObject>(_keyResolver(viewName)));
        }
        public async Awaitable<GameObject> GetViewPrefabAsync(string viewName, CancellationToken cancellationToken = default)
        {
            Check();
            var handle = await _assets.LoadAssetAsync<GameObject>(_keyResolver(viewName), cancellationToken: cancellationToken);
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
            if (!_pending.TryGetValue(name, out var pending)) _pending.Add(name, pending = new());
            pending.Push(handle);
            return handle.Asset;
        }
        private AssetHandle<GameObject> Take(string name)
        {
            if (!_pending.TryGetValue(name, out var pending) || pending.Count == 0) return null;
            var handle = pending.Pop();
            if (pending.Count == 0) _pending.Remove(name);
            return handle;
        }
        public void RegisterViewInstance(string viewName, GameObject instance)
        {
            Check();
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            int id = instance.GetInstanceID();
            if (_instances.ContainsKey(id)) throw new InvalidOperationException("窗口实例已登记租约。");
            var handle = Take(viewName) ?? throw new InvalidOperationException($"窗口 {viewName} 没有待用 Prefab 租约。");
            _instances.Add(id, (instance, handle));
        }
        public void ReleaseUninstantiatedPrefab(string viewName) => Take(viewName)?.Dispose();
        public void ReleaseViewInstance(string viewName, GameObject instance)
        {
            if (ReferenceEquals(instance, null)) return;
            if (!_instances.Remove(instance.GetInstanceID(), out var owned)) return;
            try { Destroy(owned.Instance); }
            finally { owned.Handle.Dispose(); }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var pending = new List<AssetHandle<GameObject>>();
            foreach (var stack in _pending.Values) pending.AddRange(stack);
            var instances = new List<(GameObject Instance, AssetHandle<GameObject> Handle)>(_instances.Values);
            _pending.Clear();
            _instances.Clear();
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
            if (_disposed) throw new ObjectDisposedException(nameof(AssetViewPrefabHelper));
        }
    }
}
