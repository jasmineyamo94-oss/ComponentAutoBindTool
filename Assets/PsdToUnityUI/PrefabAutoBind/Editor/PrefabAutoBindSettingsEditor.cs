using UnityEditor;
using UnityEngine;

namespace PrefabAutoBind
{
    [CustomEditor(typeof(PrefabAutoBindSettings))]
    [CanEditMultipleObjects]
    internal sealed class PrefabAutoBindSettingsEditor : Editor
    {
        private const string ScriptPropertyName = "m_Script";
        private const string GeneratedNamespacePropertyName = "generatedNamespace";
        private const string ParentClassNamePropertyName = "parentClassName";
        private const string ExportModePropertyName = "exportMode";
        private const string LogicOutputDirectoryPropertyName = "logicOutputDirectory";
        private const string PartialOutputDirectoryPropertyName = "partialOutputDirectory";
        private const string BindTemplatePropertyName = "bindTemplate";
        private const string AddEventStatementTemplatePropertyName = "addEventStatementTemplate";
        private const string RemoveEventStatementTemplatePropertyName = "removeEventStatementTemplate";
        private const string EventHandlerNameTemplatePropertyName = "eventHandlerNameTemplate";
        private const string RootObjectNamePropertyName = "rootObjectName";
        private const string FindStatementTemplatePropertyName = "findStatementTemplate";
        private const string PlainChildViewBindTemplatePropertyName = "plainChildViewBindTemplate";

        private static readonly GUIContent FindStatementTemplateLabel = new GUIContent(
            "Find Statement Template \u2192 ${find_statements}",
            "Generates ordinary component lookup statements inserted at ${find_statements} in Bind Template.");

        private static readonly GUIContent AddEventStatementTemplateLabel = new GUIContent(
            "Add Event Statement Template \u2192 ${add_event_statements}",
            "Generates the subscription statements inserted at ${add_event_statements} in Bind Template.");

        private static readonly GUIContent RemoveEventStatementTemplateLabel = new GUIContent(
            "Remove Event Statement Template \u2192 ${remove_event_statements}",
            "Generates the unsubscription statements inserted at ${remove_event_statements} in Bind Template.");

        private static readonly GUIContent EventHandlerNameTemplateLabel = new GUIContent(
            "Event Handler Name Template \u2192 ${handler_name}",
            "Generates the handler method name exposed as ${handler_name} in event statement templates.");

        private static readonly GUIContent RootObjectNameLabel = new GUIContent(
            "Root Object Name \u2192 ${root_object}",
            "Names the BindUI GameObject parameter and becomes the initial ${parent_obj} for top-level find statements.");

        private static readonly GUIContent PlainChildViewBindTemplateLabel = new GUIContent(
            "Plain Child View Bind Template \u2192 ${find_statements}",
            "Generates acquisition and initialization statements for every BindByFind child view, including MonoBehaviour child views, and inserts them at ${find_statements}.");

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawDisabledProperty(ScriptPropertyName);
            DrawProperty(GeneratedNamespacePropertyName);
            DrawProperty(ParentClassNamePropertyName);
            var exportModeProperty = DrawProperty(ExportModePropertyName);
            DrawProperty(LogicOutputDirectoryPropertyName);
            DrawProperty(PartialOutputDirectoryPropertyName);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Main Code Template", EditorStyles.boldLabel);
            DrawProperty(BindTemplatePropertyName, new GUIContent("Bind Template"));
            EditorGUILayout.HelpBox(BuildBindTemplateTokenHelpText(), MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Inserted Statement Templates", EditorStyles.boldLabel);
            var hideBindByFindSettings = ShouldHideBindByFindSettings(exportModeProperty);
            if (!hideBindByFindSettings)
            {
                DrawProperty(FindStatementTemplatePropertyName, FindStatementTemplateLabel);
            }

            DrawProperty(AddEventStatementTemplatePropertyName, AddEventStatementTemplateLabel);
            DrawProperty(RemoveEventStatementTemplatePropertyName, RemoveEventStatementTemplateLabel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Event Handler Naming", EditorStyles.boldLabel);
            DrawProperty(EventHandlerNameTemplatePropertyName, EventHandlerNameTemplateLabel);

            if (!hideBindByFindSettings)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Bind By Find", EditorStyles.boldLabel);
                DrawProperty(RootObjectNamePropertyName, RootObjectNameLabel);
                DrawProperty(
                    PlainChildViewBindTemplatePropertyName,
                    PlainChildViewBindTemplateLabel);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private SerializedProperty DrawProperty(string propertyName, GUIContent label = null)
        {
            var property = serializedObject.FindProperty(propertyName);
            if (property != null)
            {
                if (label == null)
                {
                    EditorGUILayout.PropertyField(property, true);
                }
                else
                {
                    EditorGUILayout.PropertyField(property, label, true);
                }
            }

            return property;
        }

        private void DrawDisabledProperty(string propertyName)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                DrawProperty(propertyName);
            }
        }

        private static bool ShouldHideBindByFindSettings(SerializedProperty exportModeProperty)
        {
            return exportModeProperty != null &&
                   !exportModeProperty.hasMultipleDifferentValues &&
                   exportModeProperty.intValue == (int)PrefabAutoBindExportMode.DirectReference;
        }

        private static string BuildBindTemplateTokenHelpText()
        {
            return "Bind Template multiline insertions:\n" +
                   "${find_statements} \u2190 Find Statement Template + Plain Child View Bind Template\n" +
                   "${add_event_statements} \u2190 Add Event Statement Template\n" +
                   "${remove_event_statements} \u2190 Remove Event Statement Template\n" +
                   "Inline BindByFind token: ${root_object} \u2190 Root Object Name.\n" +
                   "Also available: ${usings}, ${fields}, ${event_handlers}. Multiline tokens must be on their own line.";
        }
    }
}
