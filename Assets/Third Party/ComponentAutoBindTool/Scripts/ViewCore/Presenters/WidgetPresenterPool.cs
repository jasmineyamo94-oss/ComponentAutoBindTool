using System;
using System.Collections.Generic;
using UnityEngine;

namespace ComponentAutoBindTool.ViewCore
{
    public interface IWidgetVisibility
    {
        void SetVisible(bool visible, WidgetViewBase hostComponent = null);
    }

    /// <summary>绑定只发生在创建时；归还仅暂停。由父 Presenter 的实例资源袋持有。</summary>
    public sealed class WidgetPresenterPool<THost, TPresenter> : IDisposable
        where THost : WidgetViewBase
        where TPresenter : class, IWidgetPresenter, IWidgetVisibility
    {
        private readonly THost _template;
        private readonly Transform _parent;
        private readonly Func<THost, TPresenter> _bind;
        private readonly Action<TPresenter> _unbind;
        private readonly Stack<(THost Host, TPresenter Presenter)> _inactive = new();
        private readonly Dictionary<TPresenter, THost> _active = new();
        private bool _disposed;

        public WidgetPresenterPool(THost template, Transform parent, Func<THost, TPresenter> bindFunc,
            Action<TPresenter> unbindFunc)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            _template = template;
            _parent = parent;
            _bind = bindFunc ?? throw new ArgumentNullException(nameof(bindFunc));
            _unbind = unbindFunc ?? throw new ArgumentNullException(nameof(unbindFunc));
            template.SetWidgetVisible(false);
        }
        public int ActiveCount => _active.Count;
        public int InactiveCount => _inactive.Count;

        public TPresenter Spawn(out THost host)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WidgetPresenterPool<THost, TPresenter>));
            TPresenter presenter;
            if (_inactive.Count > 0)
            {
                var item = _inactive.Pop();
                host = item.Host;
                presenter = item.Presenter;
            }
            else
            {
                host = UnityEngine.Object.Instantiate(_template, _parent, false);
                try
                {
                    host.SetWidgetVisible(true);
                    if (_disposed) throw new ObjectDisposedException(nameof(WidgetPresenterPool<THost, TPresenter>));
                    presenter = _bind(host) ?? throw new InvalidOperationException("Widget 绑定返回空 Presenter。");
                }
                catch { DestroyHost(host); throw; }
            }
            if (_disposed)
            {
                try { _unbind(presenter); } finally { DestroyHost(host); }
                throw new ObjectDisposedException(nameof(WidgetPresenterPool<THost, TPresenter>));
            }
            _active.Add(presenter, host);
            host.SetWidgetVisible(true);
            if (!_disposed) presenter.SetVisible(true, host);
            if (_disposed) throw new ObjectDisposedException(nameof(WidgetPresenterPool<THost, TPresenter>));
            return presenter;
        }

        public void Despawn(TPresenter presenter)
        {
            if (presenter == null || !_active.Remove(presenter, out var host)) return;
            _inactive.Push((host, presenter));
            presenter.SetVisible(false, host);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var item in _active) _inactive.Push((item.Value, item.Key));
            _active.Clear();
            List<Exception> errors = null;
            while (_inactive.Count > 0)
            {
                var item = _inactive.Pop();
                try { _unbind(item.Presenter); }
                catch (Exception error) { (errors ??= new()).Add(error); }
                finally { DestroyHost(item.Host); }
            }
            if (errors != null) throw new AggregateException("Widget 池清理失败。", errors);
        }

        private static void DestroyHost(THost host)
        {
            if (host == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(host.gameObject);
            else UnityEngine.Object.DestroyImmediate(host.gameObject);
        }
    }
}
