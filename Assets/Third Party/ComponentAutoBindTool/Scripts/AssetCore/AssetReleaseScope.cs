using System;
using System.Collections.Generic;

// ReSharper disable once CheckNamespace
namespace ComponentAutoBindTool.AssetCore
{
    // 集中管理已追踪的资源句柄，在作用域释放时统一释放。
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
            // 作用域已经释放时，不能再保存句柄，立即释放传入的句柄。
            if (IsDisposed) handle.Dispose();
            else _handles.Add(handle);
            return handle;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            // 先标记为已释放，避免重复释放，也确保释放期间新加入的句柄会立即释放。
            IsDisposed = true;
            List<Exception> errors = null;
            // 按追踪的相反顺序释放；单个句柄失败时继续释放其余句柄。
            for (int i = _handles.Count - 1; i >= 0; i--)
            {
                try
                {
                    _handles[i].Dispose();
                }
                catch (Exception error)
                {
                    (errors ??= new()).Add(error);
                }
            }

            _handles.Clear();
            // 全部处理完成后，再统一报告释放过程中收集到的异常。
            if (errors != null) throw new AggregateException("资源作用域释放失败。", errors);
        }
    }
}