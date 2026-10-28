using ComponentAutoBindTool.ViewCore;
using ComponentAutoBindTool.Example;
using VContainer;

namespace ComponentAutoBindTool
{
    public partial class AutoBindTest : ViewBase
    {
        public override void ConfigureWindow(IContainerBuilder builder)
        {
            RegisterWindow<AutoBindTestPresenter, UIView>(builder, view);
            builder.Register<LabScopeProbe>(Lifetime.Scoped);
            builder.Register<LabWidgetGroupPresenter>(Lifetime.Transient);
            builder.Register<AutoBindExampleWidgetPresenter>(Lifetime.Transient);
        }
    }
}
