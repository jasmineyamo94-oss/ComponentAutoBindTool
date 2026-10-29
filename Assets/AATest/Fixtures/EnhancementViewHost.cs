using System;
using System.Collections.Generic;
using ComponentAutoBindTool.ViewCore;
using VContainer;

namespace ComponentAutoBindTool.Tests
{
    // 只用于调度与生命周期契约测试，不依赖生成字段。
    public sealed class EnhancementViewHost : ViewBase
    {
        public override void EnsureAutoBind(UnityEngine.GameObject go) { }

        public override void ConfigureWindow(IContainerBuilder builder)
        {
            RegisterWindow<EnhancementPresenter, object>(builder, new object());
        }
    }

    public sealed class EnhancementProbe
    {
        public readonly List<string> Events = new();
        public readonly Dictionary<int, IWindowContext> Contexts = new();
        public Action<string, IWindowContext> Callback;

        public void Record(string phase, IWindowContext context)
        {
            Events.Add(context.ViewName + "." + phase);
            Contexts[context.InstanceId] = context;
            Callback?.Invoke(phase, context);
        }
    }

    public sealed class EnhancementPresenter : WindowPresenterBase<object>
    {
        private readonly EnhancementProbe m_Probe;

        public EnhancementPresenter(object view, EnhancementProbe probe) : base(view)
        {
            m_Probe = probe;
        }

        protected override void OnCreate() => m_Probe.Record("Create", Context);
        protected override void OnOpen(IViewArgs args) => m_Probe.Record("Open", Context);
        protected override void OnResume() => m_Probe.Record("Resume", Context);
        protected override void OnPause() => m_Probe.Record("Pause", Context);
        protected override void OnClose() => m_Probe.Record("Close", Context);
        protected override void OnDestroy() => m_Probe.Record("Destroy", Context);
    }
}

