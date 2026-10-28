using ComponentAutoBindTool.ViewCore;
using UnityEngine;
namespace ComponentAutoBindTool.Example
{
    public sealed class AutoBindExampleWidgetPresenter : WidgetPresenterBase<AutoBindExampleWidget.UIView>
    {
        private readonly LabScopeProbe m_Probe;
        private readonly LabTelemetry m_Trace;
        private float m_Refresh;
        public int Count { get; private set; }
        public int Ticks { get; private set; }
        public int ScopeId => m_Probe.Id;
        public bool Destroyed { get; private set; }
        public AutoBindExampleWidgetPresenter(LabScopeProbe probe, LabTelemetry trace) { m_Probe = probe; m_Trace = trace; }
        protected override void OnCreate()
        {
            View.btnIncrement.onClick.AddListener(Increment);
            AddToInstanceLifetime(new LabCleanup(() => m_Trace.Log($"叶 Widget / Scope {ScopeId} 订阅释放")));
        }
        protected override void OnOpen() { Count = 0; Refresh(); }
        public void Increment() { if (Destroyed) return; Count++; Refresh(); }
        protected override void OnResume() => m_Trace.Log($"叶 Widget / Scope {ScopeId} OnResume");
        protected override void OnPause() => m_Trace.Log($"叶 Widget / Scope {ScopeId} OnPause");
        protected override void OnWidgetTick(float deltaTime)
        {
            Ticks++;
            m_Refresh += Time.unscaledDeltaTime;
            if (m_Refresh < 0.1f) return;
            m_Refresh = 0;
            Refresh();
        }
        private void Refresh() => View.tMTxtCounter.text = $"计数 {Count} · Tick {Ticks}";
        protected override void OnDestroy() { Destroyed = true; View.btnIncrement.onClick.RemoveListener(Increment); }
    }
}
