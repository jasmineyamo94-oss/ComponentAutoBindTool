namespace PsdUiWorkbench.Core
{
    public interface IPsdVisibilityRepository
    {
        PsdVisibilityState Load(string assetGuid);
        void Save(string assetGuid, PsdVisibilityState state);
    }
}
