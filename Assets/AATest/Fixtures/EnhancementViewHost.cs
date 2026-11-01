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
        private readonly EnhancementProbe _probe;

        public EnhancementPresenter(object view, EnhancementProbe probe) : base(view)
        {
            _probe = probe;
        }

        protected override void OnCreate() => _probe.Record("Create", Context);
        protected override void OnOpen(IViewArgs args) => _probe.Record("Open", Context);
        protected override void OnResume() => _probe.Record("Resume", Context);
        protected override void OnPause() => _probe.Record("Pause", Context);
        protected override void OnClose() => _probe.Record("Close", Context);
        protected override void OnDestroy() => _probe.Record("Destroy", Context);
    }
}

