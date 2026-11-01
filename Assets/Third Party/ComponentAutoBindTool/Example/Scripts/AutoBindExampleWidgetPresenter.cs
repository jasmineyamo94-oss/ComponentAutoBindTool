using ComponentAutoBindTool.ViewCore;
using UnityEngine;
namespace ComponentAutoBindTool.Example
{
    public sealed class AutoBindExampleWidgetPresenter : WidgetPresenterBase<AutoBindExampleWidget.UIView>
    {
        private readonly LabScopeProbe _probe;
        private readonly LabTelemetry _trace;
        private float _refresh;
        public int Count { get; private set; }
        public int Ticks { get; private set; }
        public int ScopeId => _probe.Id;
        public bool Destroyed { get; private set; }
        public AutoBindExampleWidgetPresenter(LabScopeProbe probe, LabTelemetry trace) { _probe = probe; _trace = trace; }
        protected override void OnCreate()
        {
            View.btnIncrement.onClick.AddListener(Increment);
            AddToInstanceLifetime(new LabCleanup(() => _trace.Log($"叶 Widget / Scope {ScopeId} 订阅释放")));
        }
        protected override void OnOpen() { Count = 0; Refresh(); }
        public void Increment() { if (Destroyed) return; Count++; Refresh(); }
        protected override void OnResume() => _trace.Log($"叶 Widget / Scope {ScopeId} OnResume");
        protected override void OnPause() => _trace.Log($"叶 Widget / Scope {ScopeId} OnPause");
        protected override void OnClose() => _trace.Log($"叶 Widget / Scope {ScopeId} OnClose");
        protected override void OnWidgetTick(float deltaTime)
        {
            Ticks++;
            _refresh += Time.unscaledDeltaTime;
            if (_refresh < 0.1f) return;
            _refresh = 0;
            Refresh();
        }
        private void Refresh() => View.tMTxtCounter.text = $"计数 {Count} · Tick {Ticks}";
        protected override void OnDestroy()
        {
            Destroyed = true;
            View.btnIncrement.onClick.RemoveListener(Increment);
            _trace.Log($"叶 Widget / Scope {ScopeId} OnDestroy");
        }
    }
}
