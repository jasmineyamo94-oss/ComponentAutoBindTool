namespace PsdUiWorkbench.Core
{
    public interface IPsdNodeAnnotationRepository
    {
        PsdNodeAnnotationState Load(string assetGuid);
        void Save(string assetGuid, PsdNodeAnnotationState state);
    }
}
