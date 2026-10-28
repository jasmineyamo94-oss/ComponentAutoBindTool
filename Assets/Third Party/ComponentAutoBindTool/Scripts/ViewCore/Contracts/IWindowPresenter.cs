using VContainer;

namespace ComponentAutoBindTool.ViewCore
{
    public interface IWindowPresenter
    {
        void InternalAttach(IWindowContext context, IObjectResolver windowScope);
        void InternalOnCreate();
        void InternalOnOpen(IViewArgs args);
        void InternalOnResume();
        void InternalOnPause();
        void InternalOnClose();
        void InternalOnDestroy();
        void InternalTick(float deltaTime);
    }

    public interface IWidgetPresenter
    {
        bool IsVisible { get; }
        void InternalAttach(object uiView, IWindowContext windowContext, IObjectResolver windowScope);
        void InternalOnCreate();
        void InternalOnOpen();
        void InternalOnResume();
        void InternalOnPause();
        void InternalOnClose();
        void InternalOnDestroy();
        void InternalTick(float deltaTime);
    }
}