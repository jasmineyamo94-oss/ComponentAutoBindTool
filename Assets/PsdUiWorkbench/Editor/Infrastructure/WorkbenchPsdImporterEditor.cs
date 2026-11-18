using UnityEditor;
using UnityEditor.U2D.PSD;

namespace PsdUiWorkbench.Infrastructure
{
    [CustomEditor(typeof(WorkbenchPsdImporter))]
    [CanEditMultipleObjects]
    public sealed class WorkbenchPsdImporterEditor : PSDImporterEditor
    {
    }
}
