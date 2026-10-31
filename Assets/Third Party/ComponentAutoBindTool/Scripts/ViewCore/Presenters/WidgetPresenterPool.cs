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
        private readonly THost m_Template;
        private readonly Transform m_Parent;
        private readonly Func<THost, TPresenter> m_Bind;
        private readonly Action<TPresenter> m_Unbind;
        private readonly Stack<(THost Host, TPresenter Presenter)> m_Inactive = new();
        private readonly Dictionary<TPresenter, THost> m_Active = new();
        private bool m_Disposed;

        public WidgetPresenterPool(THost template, Transform parent, Func<THost, TPresenter> bindFunc,
            Action<TPresenter> unbindFunc)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            m_Template = template;
            m_Parent = parent;
            m_Bind = bindFunc ?? throw new ArgumentNullException(nameof(bindFunc));
            m_Unbind = unbindFunc ?? throw new ArgumentNullException(nameof(unbindFunc));
            template.SetWidgetVisible(false);
        }
        public int ActiveCount => m_Active.Count;
        public int InactiveCount => m_Inactive.Count;

        public TPresenter Spawn(out THost host)
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(WidgetPresenterPool<THost, TPresenter>));
            TPresenter presenter;
            if (m_Inactive.Count > 0)
            {
                var item = m_Inactive.Pop();
                host = item.Host;
                presenter = item.Presenter;
            }
            else
            {
                host = UnityEngine.Object.Instantiate(m_Template, m_Parent, false);
                try
                {
                    host.SetWidgetVisible(true);
                    if (m_Disposed) throw new ObjectDisposedException(nameof(WidgetPresenterPool<THost, TPresenter>));
                    presenter = m_Bind(host) ?? throw new InvalidOperationException("Widget 绑定返回空 Presenter。");
                }
                catch { DestroyHost(host); throw; }
            }
            if (m_Disposed)
            {
                try { m_Unbind(presenter); } finally { DestroyHost(host); }
                throw new ObjectDisposedException(nameof(WidgetPresenterPool<THost, TPresenter>));
            }
            m_Active.Add(presenter, host);
            host.SetWidgetVisible(true);
            if (!m_Disposed) presenter.SetVisible(true, host);
            if (m_Disposed) throw new ObjectDisposedException(nameof(WidgetPresenterPool<THost, TPresenter>));
            return presenter;
        }

        public void Despawn(TPresenter presenter)
        {
            if (presenter == null || !m_Active.Remove(presenter, out var host)) return;
            m_Inactive.Push((host, presenter));
            presenter.SetVisible(false, host);
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            foreach (var item in m_Active) m_Inactive.Push((item.Value, item.Key));
            m_Active.Clear();
            List<Exception> errors = null;
            while (m_Inactive.Count > 0)
            {
                var item = m_Inactive.Pop();
                try { m_Unbind(item.Presenter); }
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
