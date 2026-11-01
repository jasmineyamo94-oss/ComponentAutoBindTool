using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    [FilePath("ProjectSettings/AutoBindViewTemplateSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class AutoBindViewTemplateSettings : ScriptableSingleton<AutoBindViewTemplateSettings>
    {
        internal const string DefaultPrefabRoot = "Assets/AAGame/UI/Prefabs";
        internal const string DefaultScriptRoot = "Assets/AAGame/Scripts/UI";
        [SerializeField] private string m_PrefabRoot = DefaultPrefabRoot;
        [SerializeField] private string m_ScriptRoot = DefaultScriptRoot;
        internal string PrefabRoot => m_PrefabRoot;
        internal string ScriptRoot => m_ScriptRoot;

        internal void Store(string prefabRoot, string scriptRoot)
        {
            string prefab = AutoBindViewTemplateDefinition.NormalizeFolder(prefabRoot);
            string scripts = AutoBindViewTemplateDefinition.NormalizeFolder(scriptRoot);
            AutoBindViewTemplateDefinition.ValidateAssembly(scripts + "/__TemplateProbe.cs");
            m_PrefabRoot = prefab;
            m_ScriptRoot = scripts;
            Save(true);
        }
    }
}
