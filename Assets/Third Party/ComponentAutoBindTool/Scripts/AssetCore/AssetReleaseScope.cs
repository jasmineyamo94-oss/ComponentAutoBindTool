using System;
using System.Collections.Generic;

namespace ComponentAutoBindTool.AssetCore
{
    public sealed class AssetReleaseScope : IAssetReleaseScope
    {
        private readonly List<IAssetHandle> _handles = new();
        public AssetReleaseScope(string scopeName = null) =>
            ScopeName = string.IsNullOrWhiteSpace(scopeName) ? nameof(AssetReleaseScope) : scopeName;
        public string ScopeName { get; }
        public bool IsDisposed { get; private set; }
        public T Track<T>(T handle) where T : class, IAssetHandle
        {
            if (handle == null) return null;
            if (IsDisposed) handle.Dispose();
            else _handles.Add(handle);
            return handle;
        }
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            List<Exception> errors = null;
            for (int i = _handles.Count - 1; i >= 0; i--)
            {
                try { _handles[i].Dispose(); }
                catch (Exception error) { (errors ??= new()).Add(error); }
            }
            _handles.Clear();
            if (errors != null) throw new AggregateException("资源作用域释放失败。", errors);
        }
    }
}
