namespace PsdUiWorkbench.Core
{
    public interface IPsdNodeNameRepository
    {
        PsdNodeNameState Load(string assetGuid);
        void Save(string assetGuid, PsdNodeNameState state);
    }
}
