using System;
using ComponentAutoBindTool.ViewCore;

namespace ComponentAutoBindTool.Example
{
    public sealed class LabWidgetGroupPresenter : WidgetPresenterBase<LabWidgetGroup.UIView>
    {
        private readonly LabScopeProbe m_Probe;
        public AutoBindExampleWidgetPresenter Left { get; private set; }
        public AutoBindExampleWidgetPresenter Right { get; private set; }
        public Action RequestDetach;
        public LabWidgetGroupPresenter(LabScopeProbe probe) { m_Probe = probe; }
        protected override void OnCreate()
        {
            Left = BindWidget<AutoBindExampleWidgetPresenter>(View.widgetLeft);
            Right = BindWidget<AutoBindExampleWidgetPresenter>(View.widgetRight);
            View.tMTxtScope.text = $"嵌套 Widget / 共享 Scope #{m_Probe.Id} / 两个独立 Presenter";
            View.btnToggle.onClick.AddListener(ToggleLeft);
            View.btnUnbind.onClick.AddListener(Detach);
        }
        public void ToggleLeft() => Left.SetVisible(!Left.IsVisible, View.widgetLeft);
        private void Detach() => RequestDetach?.Invoke();
        protected override void OnDestroy()
        {
            View.btnToggle.onClick.RemoveListener(ToggleLeft);
            View.btnUnbind.onClick.RemoveListener(Detach);
            RequestDetach = null;
        }
    }
}
