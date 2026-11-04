using UnityEngine;

namespace PrefabAutoBind
{
    public sealed class PrefabAutoBindSettings : ScriptableObject
    {
        public const string DefaultAddEventStatementTemplate = "AddEvent(${event_obj}, \"${event_name}\", ${handler_name});";
        public const string DefaultRemoveEventStatementTemplate = "${event_obj}.${event_name}.RemoveAllListeners();";
        public const string DefaultEventHandlerNameTemplate = "On${event_obj}${event_name}";
        public const string DefaultFindStatementTemplate = "${obj_name} = FindChild(${parent_obj}.transform, \"${path}\", typeof(${component}));";
        public const string DefaultPlainChildViewBindTemplate =
            "${obj_name} = new ${class_name}();\n" +
            "${obj_ref}.Bind(${obj_path}.gameObject);";
        public const string DefaultRootObjectName = "root";
        public const string DefaultBindTemplate =
            "${usings}\n" +
            "\n" +
            "public partial class ${class_name}\n" +
            "{\n" +
            "    ${fields}\n" +
            "\n" +
            "    private void OnEnable()\n" +
            "    {\n" +
            "        RegisterEvent();\n" +
            "    }\n" +
            "\n" +
            "    private void OnDisable()\n" +
            "    {\n" +
            "        UnregisterEvent();\n" +
            "    }\n" +
            "\n" +
            "    private void RegisterEvent()\n" +
            "    {\n" +
            "        ${add_event_statements}\n" +
            "    }\n" +
            "\n" +
            "    private void UnregisterEvent()\n" +
            "    {\n" +
            "        ${remove_event_statements}\n" +
            "    }\n" +
            "\n" +
            "    public void Bind(GameObject ${root_object})\n" +
            "    {\n" +
            "        UnregisterEvent();\n" +
            "        ${find_statements}\n" +
            "        RegisterEvent();\n" +
            "    }\n" +
            "\n" +
            "    ${event_handlers}\n" +
            "}\n";

        private const string PresetGeneratedNamespace = "PrefabAutoBind.Demo";
        private const string PresetLogicOutputDirectory = "Assets/Scripts";
        private const string PresetPartialOutputDirectory = "Assets/Scripts";
        private const string PresetAddEventStatementTemplate =
            "AddUIEvent(${event_obj}.${event_name}, ${handler_name});";
        private const string PresetRemoveEventStatementTemplate =
            "RemoveUIEvent(${event_obj}.${event_name}, ${handler_name});";
        private const string PresetEventHandlerNameTemplate = "On${event_obj}${event_name}";
        private const string PresetRootObjectName = "root";
        private const string PresetFindStatementTemplate =
            "${obj_name} = FindUI<${component}>(${parent_obj}.transform, \"${path}\");";
        private const string PresetPlainChildViewBindTemplate =
            "${obj_name} = TrackChildView(new ${class_name}());\n" +
            "${obj_ref}.Bind(${obj_path}.gameObject);";
        private const string DirectReferencePresetBindTemplate =
            "${usings}\n" +
            "\n" +
            "public partial class ${class_name}:${parent_class}\n" +
            "{\n" +
            "    ${fields}\n" +
            "\n" +
            "    protected override void AddUIEvents()\n" +
            "    {\n" +
            "        ${add_event_statements}\n" +
            "    }\n" +
            "\n" +
            "    protected override void RemoveUIEvents()\n" +
            "    {\n" +
            "        ${remove_event_statements}\n" +
            "    }\n" +
            "\n" +
            "    ${event_handlers}\n" +
            "}\n";
        private const string BindByFindPresetBindTemplate =
            "${usings}\n" +
            "\n" +
            "public partial class ${class_name}:${parent_class}\n" +
            "{\n" +
            "    ${fields}\n" +
            "\n" +
            "    protected override void BindUI(GameObject ${root_object})\n" +
            "    {\n" +
            "        ${find_statements}\n" +
            "    }\n" +
            "\n" +
            "    protected override void AddUIEvents()\n" +
            "    {\n" +
            "        ${add_event_statements}\n" +
            "    }\n" +
            "\n" +
            "    protected override void RemoveUIEvents()\n" +
            "    {\n" +
            "        ${remove_event_statements}\n" +
            "    }\n" +
            "\n" +
            "    ${event_handlers}\n" +
            "}\n";

        [SerializeField, InspectorName("Namespace")] private string generatedNamespace = string.Empty;
        [SerializeField, InspectorName("Parent Class")] private string parentClassName;
        [SerializeField, InspectorName("Export Mode")] private PrefabAutoBindExportMode exportMode = PrefabAutoBindExportMode.DirectReference;
        [SerializeField, InspectorName("Logic Dir")] private string logicOutputDirectory = "Assets/Scripts/Logic";
        [SerializeField, InspectorName("Partial Dir")] private string partialOutputDirectory = "Assets/Scripts/Logic/Generated";
        [SerializeField, InspectorName("Template"), TextArea(8, 30)] private string bindTemplate = DefaultBindTemplate;
        [SerializeField, InspectorName("Add Event Statement Template \u2192 ${add_event_statements}"), TextArea(2, 8)]
        private string addEventStatementTemplate = DefaultAddEventStatementTemplate;
        [SerializeField, InspectorName("Remove Event Statement Template \u2192 ${remove_event_statements}"), TextArea(2, 8)]
        private string removeEventStatementTemplate = DefaultRemoveEventStatementTemplate;
        [SerializeField, InspectorName("Event Handler Name Template \u2192 ${handler_name}")]
        private string eventHandlerNameTemplate = DefaultEventHandlerNameTemplate;
        [SerializeField, InspectorName("Root Object Name \u2192 ${root_object}")]
        private string rootObjectName = DefaultRootObjectName;
        [SerializeField, InspectorName("Find Statement Template \u2192 ${find_statements}"), TextArea(2, 8)]
        private string findStatementTemplate = DefaultFindStatementTemplate;
        [SerializeField, InspectorName("Plain Child View Bind Template \u2192 ${find_statements}"), TextArea(2, 8)]
        private string plainChildViewBindTemplate = DefaultPlainChildViewBindTemplate;

        public string GeneratedNamespace
        {
            get => generatedNamespace;
            set => generatedNamespace = value;
        }

        public string ParentClassName
        {
            get => parentClassName;
            set => parentClassName = value;
        }

        public PrefabAutoBindExportMode ExportMode
        {
            get => exportMode;
            set => exportMode = value;
        }

        public string LogicOutputDirectory
        {
            get => logicOutputDirectory;
            set => logicOutputDirectory = value;
        }

        public string PartialOutputDirectory
        {
            get => partialOutputDirectory;
            set => partialOutputDirectory = value;
        }

        public string BindTemplate
        {
            get => bindTemplate;
            set => bindTemplate = value;
        }

        public string AddEventStatementTemplate
        {
            get => addEventStatementTemplate;
            set => addEventStatementTemplate = value;
        }

        public string RemoveEventStatementTemplate
        {
            get => removeEventStatementTemplate;
            set => removeEventStatementTemplate = value;
        }

        public string EventHandlerNameTemplate
        {
            get => eventHandlerNameTemplate;
            set => eventHandlerNameTemplate = value;
        }

        public string RootObjectName
        {
            get => rootObjectName;
            set => rootObjectName = value;
        }

        public string FindStatementTemplate
        {
            get => findStatementTemplate;
            set => findStatementTemplate = value;
        }

        public string PlainChildViewBindTemplate
        {
            get => plainChildViewBindTemplate;
            set => plainChildViewBindTemplate = value;
        }

        internal static PrefabAutoBindSettings CreateDirectReferencePreset()
        {
            return CreatePreset(
                PrefabAutoBindExportMode.DirectReference,
                "PrefabAutoBind.DirectReferenceViewBase",
                DirectReferencePresetBindTemplate);
        }

        internal static PrefabAutoBindSettings CreateBindByFindPreset()
        {
            return CreatePreset(
                PrefabAutoBindExportMode.BindByFind,
                "PrefabAutoBind.BindByFindViewBase",
                BindByFindPresetBindTemplate);
        }

        private static PrefabAutoBindSettings CreatePreset(
            PrefabAutoBindExportMode mode,
            string parentClass,
            string template)
        {
            var settings = CreateInstance<PrefabAutoBindSettings>();
            settings.GeneratedNamespace = PresetGeneratedNamespace;
            settings.ParentClassName = parentClass;
            settings.ExportMode = mode;
            settings.LogicOutputDirectory = PresetLogicOutputDirectory;
            settings.PartialOutputDirectory = PresetPartialOutputDirectory;
            settings.BindTemplate = template;
            settings.AddEventStatementTemplate = PresetAddEventStatementTemplate;
            settings.RemoveEventStatementTemplate = PresetRemoveEventStatementTemplate;
            settings.EventHandlerNameTemplate = PresetEventHandlerNameTemplate;
            settings.RootObjectName = PresetRootObjectName;
            settings.FindStatementTemplate = PresetFindStatementTemplate;
            settings.PlainChildViewBindTemplate = PresetPlainChildViewBindTemplate;
            return settings;
        }
    }
}
