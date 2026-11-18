namespace PsdUiWorkbench.Core
{
    public interface IPsdExportRepository
    {
        PsdExportState LoadExport(string assetGuid);
        void SaveExport(string assetGuid, PsdExportState state);
    }
}
