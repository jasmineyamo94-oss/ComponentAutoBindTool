using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    [FilePath("ProjectSettings/AutoBindViewTemplateSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class AutoBindViewTemplateSettings : ScriptableSingleton<AutoBindViewTemplateSettings>
    {
        internal const string DefaultPrefabRoot = "Assets/AAGame/UI/Prefabs";
        internal const string DefaultScriptRoot = "Assets/AAGame/Scripts/UI";
        [SerializeField] private string _prefabRoot = DefaultPrefabRoot;
        [SerializeField] private string _scriptRoot = DefaultScriptRoot;
        internal string PrefabRoot => _prefabRoot;
        internal string ScriptRoot => _scriptRoot;

        internal void Store(string prefabRoot, string scriptRoot)
        {
            string prefab = AutoBindViewTemplateDefinition.NormalizeFolder(prefabRoot);
            string scripts = AutoBindViewTemplateDefinition.NormalizeFolder(scriptRoot);
            AutoBindViewTemplateDefinition.ValidateAssembly(scripts + "/__TemplateProbe.cs");
            _prefabRoot = prefab;
            _scriptRoot = scripts;
            Save(true);
        }
    }
}
