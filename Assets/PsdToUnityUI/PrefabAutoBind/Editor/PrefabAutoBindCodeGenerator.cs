using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PrefabAutoBind
{
    internal enum PrefabAutoBindResultStatus
    {
        Failed = 0,
        Completed = 1,
        Pending = 2,
    }

    internal readonly struct PrefabAutoBindResult
    {
        public PrefabAutoBindResult(bool success, string message)
            : this(
                success ? PrefabAutoBindResultStatus.Completed : PrefabAutoBindResultStatus.Failed,
                message)
        {
        }

        public PrefabAutoBindResult(PrefabAutoBindResultStatus status, string message)
        {
            Status = status;
            Message = message;
        }

        public PrefabAutoBindResultStatus Status { get; }

        public bool Success => Status != PrefabAutoBindResultStatus.Failed;

        public bool Pending => Status == PrefabAutoBindResultStatus.Pending;

        public string Message { get; }
    }

    internal static class PrefabAutoBindCodeGenerator
    {
        private sealed class ExportGraph
        {
            public ExportUnit RootUnit;
            public readonly List<ExportUnit> Units = new List<ExportUnit>();
            public bool RequiresPostCompileBinding;
        }

        private sealed class ExportUnit
        {
            public string RootPath;
            public PrefabAutoBindNodeConfig RootNode;
            public string ClassName;
            public PrefabAutoBindSettings Settings;
            public string LogicOutputDirectory;
            public string PartialOutputDirectory;
            public PrefabAutoBindExportMode ExportMode;
            public readonly List<ExportMember> Members = new List<ExportMember>();
            public readonly List<ExportContainer> Containers = new List<ExportContainer>();
        }

        private sealed class ExportContainer
        {
            public string GroupName;
            public string FieldName;
            public PrefabAutoBindContainerKind ContainerKind;
            public PrefabAutoBindFieldAccessModifier AccessModifier;
            public ExportMemberKind MemberKind;
            public Type ComponentType;
            public ExportUnit ChildUnit;
            public readonly List<ExportMember> Members = new List<ExportMember>();
        }

        private enum ExportMemberKind
        {
            Component = 0,
            ChildView = 1,
        }

        private sealed class ExportMember
        {
            public ExportMemberKind Kind;
            public PrefabAutoBindNodeConfig Node;
            public PrefabAutoBindComponentConfig Component;
            public Type ComponentType;
            public string FieldName;
            public string RelativeNodePath;
            public ExportUnit ChildUnit;
            public ExportContainer Container;
            public int ContainerIndex = -1;
        }

        private sealed class GeneratedComponentEvent
        {
            public ExportMember Member;
            public PrefabAutoBindSupportedComponentEvent SupportedEvent;
            public string HandlerMethodName;
            public string WrapperMethodName;
        }

        [Serializable]
        private sealed class MetadataManifest
        {
            public int schemaVersion = 1;
            public string generatedAtUtc;
            public string prefabAssetPath;
            public string prefabAssetName;
            public string prefabName;
            public string generatedNamespace;
            public string rootClassName;
            public string rootExportMode;
            public string logicOutputDirectory;
            public string partialOutputDirectory;
            public List<MetadataUnit> units = new List<MetadataUnit>();
        }

        [Serializable]
        private sealed class MetadataUnit
        {
            public string rootPath;
            public string rootNodeName;
            public string rootNodeKind;
            public string className;
            public string parentClassName;
            public string parentFieldName;
            public string parentRootPath;
            public string exportMode;
            public string logicOutputDirectory;
            public string partialOutputDirectory;
            public List<MetadataMember> members = new List<MetadataMember>();
            public List<MetadataContainer> containers = new List<MetadataContainer>();
        }

        [Serializable]
        private sealed class MetadataMember
        {
            public string kind;
            public string nodePath;
            public string nodeName;
            public string fieldName;
            public string relativeNodePath;
            public string accessModifier;
            public string componentTypeName;
            public int componentIndex = -1;
            public string childClassName;
            public string childRootPath;
            public string containerKind;
            public string containerName;
            public string containerFieldName;
            public int containerIndex = -1;
            public List<string> events = new List<string>();
        }

        [Serializable]
        private sealed class MetadataContainer
        {
            public string groupName;
            public string fieldName;
            public string containerKind;
            public string accessModifier;
            public string componentTypeName;
            public List<string> memberFieldNames = new List<string>();
            public List<string> memberNodePaths = new List<string>();
        }

        public static PrefabAutoBindResult Export(PrefabAutoBindConfig config)
        {
            return Export(config, false);
        }

        internal static PrefabAutoBindResult Export(
            PrefabAutoBindConfig config,
            bool overwriteExistingLogicFiles)
        {
            if (config == null || config.Prefab == null)
            {
                return new PrefabAutoBindResult(false, "Select a prefab asset before exporting.");
            }

            var prefabPath = AssetDatabase.GetAssetPath(config.Prefab);
            if (string.IsNullOrWhiteSpace(prefabPath) || !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return new PrefabAutoBindResult(false, "The current object is not a prefab asset, so binding scripts cannot be exported.");
            }

            var prefabAssetName = Path.GetFileNameWithoutExtension(prefabPath);

            NormalizeRootConfig(config, prefabAssetName);

            var snapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(config.Prefab);
            PrefabAutoBindEditorUtility.SyncNodes(config, snapshots);

            if (!TryBuildExportGraph(config, out var graph, out var errorMessage))
            {
                return new PrefabAutoBindResult(false, errorMessage);
            }

            if (!TryValidateBindByFindMemberPaths(graph, out errorMessage))
            {
                return new PrefabAutoBindResult(false, errorMessage);
            }

            if (!TryValidateExportTemplates(graph, out errorMessage))
            {
                return new PrefabAutoBindResult(false, errorMessage);
            }

            if (!TryValidateMetadataDirectories(graph, out errorMessage))
            {
                return new PrefabAutoBindResult(false, errorMessage);
            }

            var bindByFindScriptGuidsToRemove = BuildBindByFindScriptGuidsToRemove(graph);
            if (!PrefabAutoBindDirectReferenceBinder.TryRemoveBindByFindComponents(
                    prefabPath,
                    bindByFindScriptGuidsToRemove,
                    out var removedDirectReferenceComponentCount,
                    out errorMessage))
            {
                return new PrefabAutoBindResult(false, errorMessage);
            }

            var createdLogicFiles = new List<string>();
            var overwrittenLogicFiles = new List<string>();
            var skippedLogicFiles = new List<string>();
            var writtenBindFiles = new List<string>();
            var generatedClassNames = new HashSet<string>(StringComparer.Ordinal);

            for (var unitIndex = 0; unitIndex < graph.Units.Count; unitIndex++)
            {
                var unit = graph.Units[unitIndex];
                if (generatedClassNames.Add(unit.ClassName))
                {
                    var logicDirectory = ResolveProjectDirectory(unit.LogicOutputDirectory);
                    var partialDirectory = ResolveProjectDirectory(unit.PartialOutputDirectory);
                    if (logicDirectory == null || partialDirectory == null)
                    {
                        return new PrefabAutoBindResult(false, "The output directory for class '" + unit.ClassName + "' is invalid.");
                    }

                    Directory.CreateDirectory(logicDirectory);
                    Directory.CreateDirectory(partialDirectory);

                    var bindFilePath = Path.Combine(partialDirectory, BuildBindFileName(unit.ClassName));
                    var bindContent = BuildPartialFile(unit);
                    File.WriteAllText(bindFilePath, bindContent, new UTF8Encoding(false));
                    writtenBindFiles.Add(ToProjectPath(bindFilePath));

                    var logicFilePath = Path.Combine(logicDirectory, BuildLogicFileName(unit.ClassName));
                    if (File.Exists(logicFilePath))
                    {
                        if (overwriteExistingLogicFiles)
                        {
                            var logicContent = BuildLogicFile(unit);
                            File.WriteAllText(logicFilePath, logicContent, new UTF8Encoding(false));
                            overwrittenLogicFiles.Add(ToProjectPath(logicFilePath));
                        }
                        else
                        {
                            skippedLogicFiles.Add(ToProjectPath(logicFilePath));
                        }
                    }
                    else
                    {
                        var logicContent = BuildLogicFile(unit);
                        File.WriteAllText(logicFilePath, logicContent, new UTF8Encoding(false));
                        createdLogicFiles.Add(ToProjectPath(logicFilePath));
                    }

                    EnsureLogicFileContainsEventHandlers(logicFilePath, unit);
                }
            }

            PrefabAutoBindEditorUtility.SaveConfig(config);
            if (graph.RequiresPostCompileBinding &&
                !PrefabAutoBindDirectReferenceBindScheduler.TrySchedule(config, out errorMessage))
            {
                AssetDatabase.Refresh();
                return new PrefabAutoBindResult(false, errorMessage);
            }

            AssetDatabase.Refresh();

            var message = new StringBuilder();
            message.AppendLine("C# script export completed.");
            message.AppendLine();
            message.AppendLine("Units: " + graph.Units.Count);
            if (removedDirectReferenceComponentCount > 0)
            {
                message.AppendLine("Removed old DirectReference components: " + removedDirectReferenceComponentCount);
            }

            if (createdLogicFiles.Count > 0)
            {
                message.AppendLine("Created Logic:");
                for (var index = 0; index < createdLogicFiles.Count; index++)
                {
                    message.AppendLine("  " + createdLogicFiles[index]);
                }
            }

            if (skippedLogicFiles.Count > 0)
            {
                message.AppendLine("Skipped existing Logic:");
                for (var index = 0; index < skippedLogicFiles.Count; index++)
                {
                    message.AppendLine("  " + skippedLogicFiles[index]);
                }
            }

            if (overwrittenLogicFiles.Count > 0)
            {
                message.AppendLine("Overwritten Logic:");
                for (var index = 0; index < overwrittenLogicFiles.Count; index++)
                {
                    message.AppendLine("  " + overwrittenLogicFiles[index]);
                }
            }

            message.AppendLine("Overwritten Bind:");
            for (var index = 0; index < writtenBindFiles.Count; index++)
            {
                message.AppendLine("  " + writtenBindFiles[index]);
            }

            if (graph.RequiresPostCompileBinding)
            {
                message.AppendLine("Prefab components will be attached after Unity finishes compiling.");
            }

            return new PrefabAutoBindResult(
                graph.RequiresPostCompileBinding
                    ? PrefabAutoBindResultStatus.Pending
                    : PrefabAutoBindResultStatus.Completed,
                message.ToString().TrimEnd());
        }

        private static void NormalizeRootConfig(PrefabAutoBindConfig config, string prefabAssetName)
        {
            config.ClassName = PrefabAutoBindEditorUtility.MakeSafeIdentifier(
                string.IsNullOrWhiteSpace(config.ClassName) ? prefabAssetName : config.ClassName,
                PrefabAutoBindEditorUtility.BuildDefaultClassName(config.Prefab));
        }

        internal static bool TryValidateBindByFindNodePaths(
            PrefabAutoBindConfig config,
            out string errorMessage)
        {
            if (!TryBuildExportGraph(config, out var graph, out errorMessage))
            {
                return false;
            }

            return TryValidateBindByFindMemberPaths(graph, out errorMessage);
        }

        private static bool TryValidateBindByFindMemberPaths(
            ExportGraph graph,
            out string errorMessage)
        {
            errorMessage = null;
            if (graph == null)
            {
                return true;
            }

            for (var unitIndex = 0; unitIndex < graph.Units.Count; unitIndex++)
            {
                var unit = graph.Units[unitIndex];
                if (unit == null || unit.ExportMode != PrefabAutoBindExportMode.BindByFind)
                {
                    continue;
                }

                if (!TryValidateBindByFindMemberPaths(unit, out errorMessage))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateBindByFindMemberPaths(
            ExportUnit unit,
            out string errorMessage)
        {
            errorMessage = null;
            var paths = new Dictionary<string, PrefabAutoBindNodeConfig>(StringComparer.Ordinal);
            for (var memberIndex = 0; memberIndex < unit.Members.Count; memberIndex++)
            {
                var node = unit.Members[memberIndex].Node;
                if (node == null)
                {
                    continue;
                }

                var nodePath = node.NodePath ?? string.Empty;
                if (!paths.TryGetValue(nodePath, out var existingNode))
                {
                    paths.Add(nodePath, node);
                    continue;
                }

                if (existingNode.NodeId == node.NodeId)
                {
                    continue;
                }

                errorMessage =
                    "BindByFind export failed: exported node path '" +
                    (string.IsNullOrEmpty(nodePath) ? "(Root)" : nodePath) +
                    "' in class '" + unit.ClassName +
                    "' is not unique. Stop exporting one of the nodes, rename sibling nodes, or change this export unit to DirectReference.";
                return false;
            }

            return true;
        }

        private static bool TryValidateExportTemplates(
            ExportGraph graph,
            out string errorMessage)
        {
            errorMessage = null;
            for (var unitIndex = 0; unitIndex < graph.Units.Count; unitIndex++)
            {
                if (!TryValidateExportUnit(graph.Units[unitIndex], out errorMessage))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateExportUnit(ExportUnit unit, out string errorMessage)
        {
            errorMessage = null;
            var settings = unit.Settings;
            if (settings == null)
            {
                errorMessage = "Export failed: class '" + unit.ClassName + "' has no Settings asset.";
                return false;
            }

            var prefix = "Export failed for class '" + unit.ClassName + "': ";
            if (string.IsNullOrWhiteSpace(settings.BindTemplate))
            {
                errorMessage = prefix + "Template cannot be empty.";
                return false;
            }

            if (!PrefabAutoBindTemplateRenderer.ContainsRequiredPlaceholders(
                    settings.BindTemplate,
                    "class_name"))
            {
                errorMessage = prefix + "Template must contain ${class_name}.";
                return false;
            }

            if (!TryRejectRemovedBindTemplateToken(
                    settings.BindTemplate,
                    "findfunction",
                    "find_statements",
                    out errorMessage) ||
                !TryRejectRemovedBindTemplateToken(
                    settings.BindTemplate,
                    "register_event",
                    "add_event_statements",
                    out errorMessage) ||
                !TryRejectRemovedBindTemplateToken(
                    settings.BindTemplate,
                    "remove_event",
                    "remove_event_statements",
                    out errorMessage))
            {
                errorMessage = prefix + errorMessage;
                return false;
            }

            if (!PrefabAutoBindTemplateRenderer.ContainsRequiredPlaceholders(
                    settings.EventHandlerNameTemplate,
                    "event_obj",
                    "event_name"))
            {
                errorMessage = prefix + "Event Handler Name Template must contain ${event_obj} and ${event_name}.";
                return false;
            }

            if (!PrefabAutoBindTemplateRenderer.ContainsRequiredPlaceholders(
                    settings.AddEventStatementTemplate,
                    "event_obj",
                    "event_name",
                    "handler_name"))
            {
                errorMessage = prefix + "Add Event Statement Template must contain ${event_obj}, ${event_name}, and ${handler_name}.";
                return false;
            }

            if (!PrefabAutoBindTemplateRenderer.ContainsRequiredPlaceholders(
                    settings.RemoveEventStatementTemplate,
                    "event_obj",
                    "event_name"))
            {
                errorMessage = prefix + "Remove Event Statement Template must contain ${event_obj} and ${event_name}.";
                return false;
            }

            var hasGeneratedFields = unit.Members.Count > 0 || unit.Containers.Count > 0;
            var hasGeneratedEvents = GetGeneratedComponentEvents(unit).Count > 0;
            var usesBindByFind = unit.ExportMode == PrefabAutoBindExportMode.BindByFind;
            if (usesBindByFind && string.IsNullOrWhiteSpace(settings.RootObjectName))
            {
                errorMessage = prefix + "BindByFind requires Root Object Name.";
                return false;
            }

            if (usesBindByFind &&
                !PrefabAutoBindEditorUtility.IsValidCSharpIdentifier(settings.RootObjectName))
            {
                errorMessage = prefix + "Root Object Name must be a valid C# identifier.";
                return false;
            }

            if (usesBindByFind &&
                !PrefabAutoBindTemplateRenderer.ContainsRequiredPlaceholders(
                    settings.BindTemplate,
                    "root_object"))
            {
                errorMessage = prefix + "Template must contain ${root_object}.";
                return false;
            }

            if (!TryValidateBindTemplateBlock(settings.BindTemplate, "usings", true, out errorMessage) ||
                !TryValidateBindTemplateBlock(settings.BindTemplate, "fields", hasGeneratedFields, out errorMessage) ||
                !TryValidateBindTemplateBlock(settings.BindTemplate, "find_statements", usesBindByFind, out errorMessage) ||
                !TryValidateBindTemplateBlock(settings.BindTemplate, "add_event_statements", hasGeneratedEvents, out errorMessage) ||
                !TryValidateBindTemplateBlock(settings.BindTemplate, "remove_event_statements", hasGeneratedEvents, out errorMessage) ||
                !TryValidateBindTemplateBlock(settings.BindTemplate, "event_handlers", hasGeneratedEvents, out errorMessage))
            {
                errorMessage = prefix + errorMessage;
                return false;
            }

            if (usesBindByFind &&
                !PrefabAutoBindTemplateRenderer.ContainsRequiredPlaceholders(
                    settings.FindStatementTemplate,
                    "obj_name",
                    "parent_obj",
                    "path",
                    "component"))
            {
                errorMessage = prefix + "Find Statement Template must contain ${obj_name}, ${parent_obj}, ${path}, and ${component}.";
                return false;
            }

            if (usesBindByFind &&
                !TryValidatePlainChildViewBindTemplates(unit, out errorMessage))
            {
                errorMessage = prefix + errorMessage;
                return false;
            }

            if (unit.ExportMode == PrefabAutoBindExportMode.DirectReference &&
                !string.IsNullOrWhiteSpace(settings.ParentClassName) &&
                !PrefabAutoBindEditorUtility.TryResolveMonoBehaviourBaseType(
                    settings.ParentClassName,
                    out errorMessage))
            {
                errorMessage = prefix + errorMessage;
                return false;
            }

            return true;
        }

        private static bool TryValidatePlainChildViewBindTemplates(ExportUnit unit, out string errorMessage)
        {
            errorMessage = null;
            var validatedChildUnits = new HashSet<ExportUnit>();
            for (var memberIndex = 0; memberIndex < unit.Members.Count; memberIndex++)
            {
                var member = unit.Members[memberIndex];
                if (member.Kind != ExportMemberKind.ChildView ||
                    member.ChildUnit == null ||
                    !validatedChildUnits.Add(member.ChildUnit))
                {
                    continue;
                }

                var template = GetPlainChildViewBindTemplate(member.ChildUnit.Settings);
                var childClassName = string.IsNullOrWhiteSpace(member.ChildUnit.ClassName)
                    ? member.FieldName
                    : member.ChildUnit.ClassName;
                if (string.IsNullOrWhiteSpace(template))
                {
                    errorMessage = "Plain Child View Bind cannot be empty for child view '" + childClassName + "'.";
                    return false;
                }

                if (!PrefabAutoBindTemplateRenderer.ContainsRequiredPlaceholders(
                        template,
                        "obj_name",
                        "class_name",
                        "obj_path"))
                {
                    errorMessage = "Plain Child View Bind for child view '" + childClassName +
                                   "' must contain ${obj_name}, ${class_name}, and ${obj_path}.";
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateBindTemplateBlock(
            string template,
            string placeholder,
            bool required,
            out string errorMessage)
        {
            errorMessage = null;
            var containsPlaceholder = PrefabAutoBindTemplateRenderer.ContainsPlaceholder(template, placeholder);
            if (required && !containsPlaceholder)
            {
                errorMessage = "Template must contain ${" + placeholder + "}.";
                return false;
            }

            if (containsPlaceholder && !PrefabAutoBindTemplateRenderer.IsStandalonePlaceholder(template, placeholder))
            {
                errorMessage = "${" + placeholder + "} in Template must appear on a line by itself.";
                return false;
            }

            return true;
        }

        private static bool TryRejectRemovedBindTemplateToken(
            string template,
            string removedPlaceholder,
            string replacementPlaceholder,
            out string errorMessage)
        {
            errorMessage = null;
            if (!PrefabAutoBindTemplateRenderer.ContainsPlaceholder(template, removedPlaceholder))
            {
                return true;
            }

            errorMessage = "Template token ${" + removedPlaceholder +
                           "} is no longer supported. Use ${" + replacementPlaceholder + "}.";
            return false;
        }

        private static string ResolveEffectiveBaseClassName(
            PrefabAutoBindSettings settings,
            PrefabAutoBindExportMode exportMode)
        {
            if (settings != null && !string.IsNullOrWhiteSpace(settings.ParentClassName))
            {
                return settings.ParentClassName.Trim();
            }

            return exportMode == PrefabAutoBindExportMode.DirectReference
                ? "MonoBehaviour"
                : string.Empty;
        }

        private static bool IsMonoBehaviourExportUnit(ExportUnit unit)
        {
            if (unit == null)
            {
                return false;
            }

            if (unit.ExportMode == PrefabAutoBindExportMode.DirectReference)
            {
                return true;
            }

            var settings = unit.Settings;
            if (settings == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(settings.ParentClassName))
            {
                return IsMonoBehaviourBaseName(settings.ParentClassName);
            }

            return TryGetTemplateBaseClassName(settings, unit, out var baseClassName) &&
                   IsMonoBehaviourBaseName(baseClassName);
        }

        private static bool TryGetTemplateBaseClassName(
            PrefabAutoBindSettings settings,
            ExportUnit unit,
            out string baseClassName)
        {
            baseClassName = null;
            if (settings == null || unit == null || string.IsNullOrWhiteSpace(unit.ClassName))
            {
                return false;
            }

            var renderedTemplate = PrefabAutoBindTemplateRenderer.RenderTemplate(
                settings.BindTemplate,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "class_name", unit.ClassName ?? string.Empty },
                    { "namespace", GetGeneratedNamespace(unit) },
                    { "parent_class", settings.ParentClassName ?? string.Empty },
                });

            return TryGetTemplateBaseClassName(renderedTemplate, unit.ClassName, out baseClassName);
        }

        private static bool TryGetTemplateBaseClassName(
            string template,
            string className,
            out string baseClassName)
        {
            baseClassName = null;
            if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(className))
            {
                return false;
            }

            var searchIndex = 0;
            while (searchIndex < template.Length)
            {
                var classIndex = template.IndexOf("class", searchIndex, StringComparison.Ordinal);
                if (classIndex < 0)
                {
                    return false;
                }

                searchIndex = classIndex + "class".Length;
                if (!IsTokenBoundary(template, classIndex - 1) ||
                    !IsTokenBoundary(template, classIndex + "class".Length))
                {
                    continue;
                }

                var nameIndex = SkipWhitespace(template, classIndex + "class".Length);
                if (!TryReadIdentifier(template, nameIndex, out var parsedClassName, out var afterNameIndex) ||
                    !string.Equals(parsedClassName, className, StringComparison.Ordinal))
                {
                    continue;
                }

                var colonIndex = SkipWhitespace(template, afterNameIndex);
                if (colonIndex >= template.Length || template[colonIndex] != ':')
                {
                    return false;
                }

                var baseStartIndex = SkipWhitespace(template, colonIndex + 1);
                var baseEndIndex = baseStartIndex;
                while (baseEndIndex < template.Length &&
                       template[baseEndIndex] != '{' &&
                       template[baseEndIndex] != '\r' &&
                       template[baseEndIndex] != '\n' &&
                       template[baseEndIndex] != ',')
                {
                    baseEndIndex++;
                }

                baseClassName = template.Substring(baseStartIndex, baseEndIndex - baseStartIndex).Trim();
                return !string.IsNullOrWhiteSpace(baseClassName);
            }

            return false;
        }

        private static bool IsMonoBehaviourBaseName(string baseClassName)
        {
            var normalized = NormalizeTypeName(baseClassName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            return string.Equals(normalized, "MonoBehaviour", StringComparison.Ordinal) ||
                   string.Equals(normalized, "UnityEngine.MonoBehaviour", StringComparison.Ordinal) ||
                   PrefabAutoBindEditorUtility.TryResolveMonoBehaviourBaseType(normalized, out _);
        }

        private static string NormalizeTypeName(string typeName)
        {
            var normalized = (typeName ?? string.Empty).Trim();
            const string globalPrefix = "global::";
            if (normalized.StartsWith(globalPrefix, StringComparison.Ordinal))
            {
                normalized = normalized.Substring(globalPrefix.Length);
            }

            var genericIndex = normalized.IndexOf('<');
            if (genericIndex >= 0)
            {
                normalized = normalized.Substring(0, genericIndex).Trim();
            }

            return normalized;
        }

        private static bool IsTokenBoundary(string text, int index)
        {
            return index < 0 ||
                   index >= text.Length ||
                   (!char.IsLetterOrDigit(text[index]) && text[index] != '_');
        }

        private static int SkipWhitespace(string text, int startIndex)
        {
            var index = Math.Max(0, startIndex);
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            return index;
        }

        private static bool TryReadIdentifier(
            string text,
            int startIndex,
            out string identifier,
            out int endIndex)
        {
            identifier = null;
            endIndex = startIndex;
            if (startIndex < 0 || startIndex >= text.Length)
            {
                return false;
            }

            if (!char.IsLetter(text[startIndex]) && text[startIndex] != '_')
            {
                return false;
            }

            var index = startIndex + 1;
            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
            {
                index++;
            }

            identifier = text.Substring(startIndex, index - startIndex);
            endIndex = index;
            return true;
        }

        private static string GetGeneratedNamespace(ExportUnit unit)
        {
            return unit != null && unit.Settings != null &&
                   !string.IsNullOrWhiteSpace(unit.Settings.GeneratedNamespace)
                ? unit.Settings.GeneratedNamespace.Trim()
                : string.Empty;
        }

        private static string GetChildTypeReference(ExportUnit ownerUnit, ExportUnit childUnit)
        {
            if (childUnit == null)
            {
                return string.Empty;
            }

            var ownerNamespace = GetGeneratedNamespace(ownerUnit);
            var childNamespace = GetGeneratedNamespace(childUnit);
            if (string.Equals(ownerNamespace, childNamespace, StringComparison.Ordinal))
            {
                return childUnit.ClassName;
            }

            return string.IsNullOrWhiteSpace(childNamespace)
                ? "global::" + childUnit.ClassName
                : "global::" + childNamespace + "." + childUnit.ClassName;
        }

        private static string BuildLogicFileName(string className)
        {
            return (string.IsNullOrWhiteSpace(className) ? "Prefab" : className.Trim()) + ".cs";
        }

        private static string BuildBindFileName(string className)
        {
            return (string.IsNullOrWhiteSpace(className) ? "Prefab" : className.Trim()) + ".Bind.cs";
        }

        private static HashSet<string> BuildBindByFindScriptGuidsToRemove(ExportGraph graph)
        {
            var scriptGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (graph == null)
            {
                return scriptGuids;
            }

            for (var unitIndex = 0; unitIndex < graph.Units.Count; unitIndex++)
            {
                var unit = graph.Units[unitIndex];
                if (unit == null ||
                    unit.ExportMode != PrefabAutoBindExportMode.BindByFind ||
                    IsMonoBehaviourExportUnit(unit))
                {
                    continue;
                }

                AddGeneratedScriptGuid(
                    scriptGuids,
                    PrefabAutoBindEditorUtility.CombineProjectPath(
                        unit.LogicOutputDirectory,
                        BuildLogicFileName(unit.ClassName)));
                AddGeneratedScriptGuid(
                    scriptGuids,
                    PrefabAutoBindEditorUtility.CombineProjectPath(
                        unit.PartialOutputDirectory,
                        BuildBindFileName(unit.ClassName)));
            }

            return scriptGuids;
        }

        private static void AddGeneratedScriptGuid(HashSet<string> scriptGuids, string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath))
            {
                return;
            }

            var guid = AssetDatabase.AssetPathToGUID(scriptPath);
            if (!string.IsNullOrWhiteSpace(guid))
            {
                scriptGuids.Add(guid);
            }
        }

        private static Dictionary<PrefabAutoBindNodeId, PrefabAutoBindNodeSnapshot> BuildSnapshotLookup(
            IReadOnlyList<PrefabAutoBindNodeSnapshot> snapshots)
        {
            var result = new Dictionary<PrefabAutoBindNodeId, PrefabAutoBindNodeSnapshot>();
            for (var index = 0; index < snapshots.Count; index++)
            {
                result[snapshots[index].NodeId] = snapshots[index];
            }

            return result;
        }

        private static int[] GetHierarchyIndices(
            IReadOnlyDictionary<PrefabAutoBindNodeId, PrefabAutoBindNodeSnapshot> snapshotsById,
            PrefabAutoBindNodeConfig node)
        {
            if (node == null || !snapshotsById.TryGetValue(node.NodeId, out var snapshot))
            {
                return null;
            }

            return snapshot.HierarchyIndices;
        }

        private static PrefabAutoBindDirectReferenceBinder.BindUnit BuildDirectReferenceBindUnit(
            ExportUnit unit,
            IReadOnlyDictionary<PrefabAutoBindNodeId, PrefabAutoBindNodeSnapshot> snapshotsById,
            bool assignSerializedFields)
        {
            var bindUnit = new PrefabAutoBindDirectReferenceBinder.BindUnit
            {
                ClassName = unit.ClassName,
                RootPath = unit.RootPath ?? string.Empty,
                RootHierarchyIndices = GetHierarchyIndices(snapshotsById, unit.RootNode),
                GeneratedNamespace = GetGeneratedNamespace(unit),
                EffectiveBaseClassName = ResolveEffectiveBaseClassName(unit.Settings, unit.ExportMode),
                AssignSerializedFields = assignSerializedFields,
            };

            for (var memberIndex = 0; memberIndex < unit.Members.Count; memberIndex++)
            {
                var member = unit.Members[memberIndex];
                var bindMember = new PrefabAutoBindDirectReferenceBinder.BindMember
                {
                    FieldName = member.FieldName,
                    NodePath = member.Node != null ? member.Node.NodePath ?? string.Empty : string.Empty,
                    HierarchyIndices = GetHierarchyIndices(snapshotsById, member.Node),
                    Container = member.Container != null
                        ? new PrefabAutoBindDirectReferenceBinder.BindContainer
                        {
                            FieldName = member.Container.FieldName,
                            ContainerKind = member.Container.ContainerKind,
                            ComponentType = member.Container.ComponentType,
                        }
                        : null,
                    ContainerIndex = member.ContainerIndex,
                };

                if (member.Kind == ExportMemberKind.ChildView)
                {
                    bindMember.Kind = PrefabAutoBindDirectReferenceBinder.BindMemberKind.ChildView;
                    bindMember.ChildClassName = member.ChildUnit != null ? member.ChildUnit.ClassName : string.Empty;
                    bindMember.ChildNamespace = member.ChildUnit != null
                        ? GetGeneratedNamespace(member.ChildUnit)
                        : string.Empty;
                    bindMember.ChildBaseClassName = member.ChildUnit != null
                        ? ResolveEffectiveBaseClassName(member.ChildUnit.Settings, member.ChildUnit.ExportMode)
                        : string.Empty;
                }
                else
                {
                    bindMember.Kind = PrefabAutoBindDirectReferenceBinder.BindMemberKind.Component;
                    bindMember.ComponentTypeName = member.Component != null
                        ? member.Component.ComponentTypeName
                        : member.ComponentType != null
                            ? member.ComponentType.AssemblyQualifiedName
                            : string.Empty;
                    bindMember.ComponentIndex = member.Component != null ? member.Component.ComponentIndex : 0;
                }

                bindUnit.Members.Add(bindMember);
            }

            for (var containerIndex = 0; containerIndex < unit.Containers.Count; containerIndex++)
            {
                var container = unit.Containers[containerIndex];
                var bindContainer = new PrefabAutoBindDirectReferenceBinder.BindContainer
                {
                    FieldName = container.FieldName,
                    ContainerKind = container.ContainerKind,
                    ComponentType = container.ComponentType,
                };

                for (var memberIndex = 0; memberIndex < container.Members.Count; memberIndex++)
                {
                    var member = container.Members[memberIndex];
                    var bindMember = new PrefabAutoBindDirectReferenceBinder.BindMember
                    {
                        FieldName = member.FieldName,
                        NodePath = member.Node != null ? member.Node.NodePath ?? string.Empty : string.Empty,
                        HierarchyIndices = GetHierarchyIndices(snapshotsById, member.Node),
                    };

                    if (member.Kind == ExportMemberKind.ChildView)
                    {
                        bindMember.Kind = PrefabAutoBindDirectReferenceBinder.BindMemberKind.ChildView;
                        bindMember.ChildClassName = member.ChildUnit != null ? member.ChildUnit.ClassName : string.Empty;
                        bindMember.ChildNamespace = member.ChildUnit != null
                            ? GetGeneratedNamespace(member.ChildUnit)
                            : string.Empty;
                        bindMember.ChildBaseClassName = member.ChildUnit != null
                            ? ResolveEffectiveBaseClassName(member.ChildUnit.Settings, member.ChildUnit.ExportMode)
                            : string.Empty;
                    }
                    else
                    {
                        bindMember.Kind = PrefabAutoBindDirectReferenceBinder.BindMemberKind.Component;
                        bindMember.ComponentTypeName = member.Component != null
                            ? member.Component.ComponentTypeName
                            : member.ComponentType != null
                                ? member.ComponentType.AssemblyQualifiedName
                                : string.Empty;
                        bindMember.ComponentIndex = member.Component != null ? member.Component.ComponentIndex : 0;
                    }

                    bindContainer.Members.Add(bindMember);
                }

                bindUnit.Containers.Add(bindContainer);
            }

            return bindUnit;
        }

        internal static PrefabAutoBindResult BindDirectReferences(PrefabAutoBindConfig config)
        {
            if (!TryBuildDirectReferenceBindUnits(
                    config,
                    out var prefabPath,
                    out var directReferenceUnits,
                    out var errorMessage))
            {
                return new PrefabAutoBindResult(false, errorMessage);
            }

            if (directReferenceUnits.Count == 0)
            {
                return new PrefabAutoBindResult(
                    true,
                    "DirectReference automatic binding completed: the current configuration has no DirectReference units to bind.");
            }

            if (!PrefabAutoBindDirectReferenceBinder.TryBindPrefab(
                    prefabPath,
                    directReferenceUnits,
                    out errorMessage))
            {
                return new PrefabAutoBindResult(false, errorMessage);
            }

            return new PrefabAutoBindResult(
                true,
                "DirectReference automatic binding completed.\nPrefab: " + prefabPath);
        }

        internal static bool TryBuildDirectReferenceBindUnits(
            PrefabAutoBindConfig config,
            out string prefabPath,
            out List<PrefabAutoBindDirectReferenceBinder.BindUnit> directReferenceUnits,
            out string errorMessage)
        {
            prefabPath = null;
            directReferenceUnits = new List<PrefabAutoBindDirectReferenceBinder.BindUnit>();
            errorMessage = null;

            if (config == null || config.Prefab == null)
            {
                errorMessage = "DirectReference automatic binding failed: configuration or prefab was not found.";
                return false;
            }

            prefabPath = AssetDatabase.GetAssetPath(config.Prefab);
            if (string.IsNullOrWhiteSpace(prefabPath) ||
                !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "DirectReference automatic binding failed: the object referenced by the configuration is not a prefab asset.";
                return false;
            }

            var snapshots = PrefabAutoBindEditorUtility.BuildNodeSnapshots(config.Prefab);
            var snapshotsById = BuildSnapshotLookup(snapshots);
            if (!TryBuildExportGraph(config, out var graph, out errorMessage))
            {
                return false;
            }

            for (var unitIndex = 0; unitIndex < graph.Units.Count; unitIndex++)
            {
                var unit = graph.Units[unitIndex];
                if (unit.ExportMode == PrefabAutoBindExportMode.DirectReference)
                {
                    directReferenceUnits.Add(BuildDirectReferenceBindUnit(unit, snapshotsById, true));
                }
                else if (unit.ExportMode == PrefabAutoBindExportMode.BindByFind &&
                         IsMonoBehaviourExportUnit(unit))
                {
                    directReferenceUnits.Add(BuildDirectReferenceBindUnit(unit, snapshotsById, false));
                }
            }

            return true;
        }

        private static bool TryBuildExportGraph(
            PrefabAutoBindConfig config,
            out ExportGraph graph,
            out string errorMessage)
        {
            graph = new ExportGraph();
            errorMessage = null;

            var rootNode = PrefabAutoBindEditorUtility.FindRootNodeConfig(config);
            if (rootNode == null)
            {
                errorMessage = "Export failed: unable to resolve the prefab root node configuration. Refresh first.";
                return false;
            }

            if (rootNode.NodeKind == PrefabAutoBindNodeKind.View)
            {
                errorMessage = "Export failed: the prefab root node cannot be marked as view.";
                return false;
            }

            if (config.Settings == null)
            {
                errorMessage = "Export failed: root class '" + config.ClassName + "' has no Settings asset.";
                return false;
            }

            var childLookup = BuildChildLookup(config);
            config.ClassName = PrefabAutoBindEditorUtility.MakeSafeIdentifier(
                config.ClassName,
                PrefabAutoBindEditorUtility.BuildDefaultClassName(config.Prefab));

            var rootUnit = new ExportUnit
            {
                RootPath = string.Empty,
                RootNode = rootNode,
                ClassName = config.ClassName,
                Settings = config.Settings,
                LogicOutputDirectory = config.Settings != null
                    ? PrefabAutoBindEditorUtility.NormalizeDirectory(
                        config.Settings.LogicOutputDirectory,
                        "Assets/Scripts/Logic")
                    : null,
                PartialOutputDirectory = config.Settings != null
                    ? PrefabAutoBindEditorUtility.NormalizeDirectory(
                        config.Settings.PartialOutputDirectory,
                        "Assets/Scripts/Logic/Generated")
                    : null,
                ExportMode = config.Settings.ExportMode,
            };

            graph.RootUnit = rootUnit;
            graph.Units.Add(rootUnit);

            var usedFieldNames = new HashSet<string>(StringComparer.Ordinal);
            if (!CollectUnitMembers(
                    rootUnit,
                    rootNode,
                    childLookup,
                    graph.Units,
                    usedFieldNames,
                    out errorMessage))
            {
                graph = null;
                return false;
            }

            for (var unitIndex = 0; unitIndex < graph.Units.Count; unitIndex++)
            {
                if (!FinalizeUnitContainers(graph.Units[unitIndex], out errorMessage))
                {
                    graph = null;
                    return false;
                }
            }

            if (!TryValidateDuplicateViewClasses(graph, out errorMessage))
            {
                graph = null;
                return false;
            }

            var totalMemberCount = 0;
            for (var unitIndex = 0; unitIndex < graph.Units.Count; unitIndex++)
            {
                totalMemberCount += graph.Units[unitIndex].Members.Count;
                var unit = graph.Units[unitIndex];
                if (unit.ExportMode == PrefabAutoBindExportMode.DirectReference ||
                    IsMonoBehaviourExportUnit(unit))
                {
                    graph.RequiresPostCompileBinding = true;
                }
            }

            if (totalMemberCount == 0)
            {
                errorMessage = "Export failed: there are no fields or view nodes to export.";
                graph = null;
                return false;
            }

            return true;
        }

        private static Dictionary<PrefabAutoBindNodeId, List<PrefabAutoBindNodeConfig>> BuildChildLookup(
            PrefabAutoBindConfig config)
        {
            var lookup = new Dictionary<PrefabAutoBindNodeId, List<PrefabAutoBindNodeConfig>>();
            for (var nodeIndex = 0; nodeIndex < config.Nodes.Count; nodeIndex++)
            {
                var node = config.Nodes[nodeIndex];
                if (node == null)
                {
                    continue;
                }

                if (!node.ParentNodeId.IsValid)
                {
                    continue;
                }

                if (!lookup.TryGetValue(node.ParentNodeId, out var children))
                {
                    children = new List<PrefabAutoBindNodeConfig>();
                    lookup[node.ParentNodeId] = children;
                }

                children.Add(node);
            }

            return lookup;
        }

        private static bool CollectUnitMembers(
            ExportUnit unit,
            PrefabAutoBindNodeConfig node,
            Dictionary<PrefabAutoBindNodeId, List<PrefabAutoBindNodeConfig>> childLookup,
            List<ExportUnit> allUnits,
            HashSet<string> usedFieldNames,
            out string errorMessage)
        {
            errorMessage = null;
            if (node == null)
            {
                return true;
            }

            var isUnitRoot = unit.RootNode != null && node.NodeId == unit.RootNode.NodeId;
            if (node.NodeKind == PrefabAutoBindNodeKind.View)
            {
                if (!node.ParentNodeId.IsValid)
                {
                    errorMessage = "Export failed: the prefab root node cannot be marked as view.";
                    return false;
                }

                if (node.Components.Count > 0)
                {
                    errorMessage = "Export failed: view node '" + node.NodeName + "' cannot retain component bindings. Change it back to field or clear the node's components.";
                    return false;
                }

                if (!isUnitRoot)
                {
                    if (node.Settings == null)
                    {
                        errorMessage = "Export failed: view class '" + node.ViewClassName + "' has no Settings asset.";
                        return false;
                    }

                    var childUnit = CreateViewExportUnit(node);
                    NormalizeViewContainer(node);
                    var fieldName = ReserveUniqueIdentifier(usedFieldNames, node.NodeName, childUnit.ClassName);
                    unit.Members.Add(new ExportMember
                    {
                        Kind = ExportMemberKind.ChildView,
                        Node = node,
                        FieldName = fieldName,
                        RelativeNodePath = PrefabAutoBindEditorUtility.GetPathRelativeTo(node.NodePath, unit.RootPath),
                        ChildUnit = childUnit,
                    });
                    allUnits.Add(childUnit);

                    var childFieldNames = new HashSet<string>(StringComparer.Ordinal);
                    return CollectUnitMembers(
                        childUnit,
                        node,
                        childLookup,
                        allUnits,
                        childFieldNames,
                        out errorMessage);
                }
            }

            if (!(isUnitRoot && node.NodeKind == PrefabAutoBindNodeKind.View))
            {
                if (!CollectComponentMembers(unit, node, usedFieldNames, out errorMessage))
                {
                    return false;
                }
            }

            if (!childLookup.TryGetValue(node.NodeId, out var children))
            {
                return true;
            }

            for (var childIndex = 0; childIndex < children.Count; childIndex++)
            {
                if (!CollectUnitMembers(
                        unit,
                        children[childIndex],
                        childLookup,
                        allUnits,
                        usedFieldNames,
                        out errorMessage))
                {
                    return false;
                }
            }

            return true;
        }

        private static ExportUnit CreateViewExportUnit(PrefabAutoBindNodeConfig node)
        {
            node.ViewClassName = PrefabAutoBindEditorUtility.MakeSafeIdentifier(
                node.ViewClassName,
                BuildDefaultViewClassName(node));

            return new ExportUnit
            {
                RootPath = node.NodePath,
                RootNode = node,
                ClassName = node.ViewClassName,
                Settings = node.Settings,
                LogicOutputDirectory = PrefabAutoBindEditorUtility.NormalizeDirectory(
                    node.Settings.LogicOutputDirectory,
                    "Assets/Scripts/Logic"),
                PartialOutputDirectory = PrefabAutoBindEditorUtility.NormalizeDirectory(
                    node.Settings.PartialOutputDirectory,
                    "Assets/Scripts/Logic/Generated"),
                ExportMode = node.Settings.ExportMode,
            };
        }

        private static bool TryValidateDuplicateViewClasses(
            ExportGraph graph,
            out string errorMessage)
        {
            errorMessage = null;
            var canonicalUnits = new Dictionary<string, ExportUnit>(StringComparer.Ordinal);
            for (var unitIndex = 1; unitIndex < graph.Units.Count; unitIndex++)
            {
                var unit = graph.Units[unitIndex];
                if (string.Equals(unit.ClassName, graph.RootUnit.ClassName, StringComparison.Ordinal))
                {
                    errorMessage =
                        "Export failed: View node '" + FormatViewPath(unit) +
                        "' uses View Class '" + unit.ClassName +
                        "', which has the same name as the root Class.";
                    return false;
                }

                if (!canonicalUnits.TryGetValue(unit.ClassName, out var canonicalUnit))
                {
                    canonicalUnits.Add(unit.ClassName, unit);
                    continue;
                }

                if (canonicalUnit.Settings != unit.Settings)
                {
                    errorMessage =
                        "Export failed: View Class '" + unit.ClassName +
                        "' is incompatible across nodes. Nodes '" + FormatViewPath(canonicalUnit) +
                        "' and '" + FormatViewPath(unit) +
                        "' must reference the same Settings.";
                    return false;
                }

                if (!TryValidateCompatibleViewUnits(canonicalUnit, unit, out errorMessage))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateCompatibleViewUnits(
            ExportUnit canonicalUnit,
            ExportUnit candidateUnit,
            out string errorMessage)
        {
            var difference = FindViewUnitDifference(canonicalUnit, candidateUnit);
            errorMessage = difference == null
                ? null
                : BuildDuplicateViewError(canonicalUnit, candidateUnit, difference);
            return difference == null;
        }

        private static string FindViewUnitDifference(
            ExportUnit canonicalUnit,
            ExportUnit candidateUnit)
        {
            var canonicalMembers = BuildMembersByFieldName(canonicalUnit);
            var candidateMembers = BuildMembersByFieldName(candidateUnit);

            for (var memberIndex = 0; memberIndex < canonicalUnit.Members.Count; memberIndex++)
            {
                var canonicalMember = canonicalUnit.Members[memberIndex];
                if (!candidateMembers.TryGetValue(canonicalMember.FieldName, out var candidateMember))
                {
                    return "Node '" + FormatViewPath(candidateUnit) +
                           "' is missing variable '" + canonicalMember.FieldName + "'.";
                }

                if (canonicalMember.Kind != candidateMember.Kind)
                {
                    return "Variable '" + canonicalMember.FieldName + "' has a different member kind.";
                }

                var canonicalType = GetMemberTypeIdentity(canonicalMember);
                var candidateType = GetMemberTypeIdentity(candidateMember);
                if (!string.Equals(canonicalType, candidateType, StringComparison.Ordinal))
                {
                    return "Variable '" + canonicalMember.FieldName + "' has a different type: '" +
                           canonicalType + "' / '" + candidateType + "'.";
                }

                var canonicalContainerKind = GetContainerKind(canonicalMember);
                var candidateContainerKind = GetContainerKind(candidateMember);
                if (canonicalContainerKind != candidateContainerKind)
                {
                    return "Variable '" + canonicalMember.FieldName + "' has a different container type: '" +
                           canonicalContainerKind + "' / '" + candidateContainerKind + "'.";
                }

                var canonicalContainerName = GetContainerFieldName(canonicalMember);
                var candidateContainerName = GetContainerFieldName(candidateMember);
                if (!string.Equals(canonicalContainerName, candidateContainerName, StringComparison.Ordinal))
                {
                    return "Variable '" + canonicalMember.FieldName + "' is assigned to a different container variable: '" +
                           canonicalContainerName + "' / '" + candidateContainerName + "'.";
                }

                if (canonicalUnit.ExportMode != PrefabAutoBindExportMode.BindByFind)
                {
                    continue;
                }

                var canonicalPath = canonicalMember.RelativeNodePath ?? string.Empty;
                var candidatePath = candidateMember.RelativeNodePath ?? string.Empty;
                if (!string.Equals(canonicalPath, candidatePath, StringComparison.Ordinal))
                {
                    return "Variable '" + canonicalMember.FieldName + "' has a different lookup path: '" +
                           canonicalPath + "' / '" + candidatePath + "'.";
                }

                if (canonicalMember.Container != null &&
                    canonicalMember.ContainerIndex != candidateMember.ContainerIndex)
                {
                    return "Variable '" + canonicalMember.FieldName + "' has a different container index: " +
                           canonicalMember.ContainerIndex + " / " + candidateMember.ContainerIndex + ".";
                }
            }

            for (var memberIndex = 0; memberIndex < candidateUnit.Members.Count; memberIndex++)
            {
                var candidateMember = candidateUnit.Members[memberIndex];
                if (canonicalMembers.ContainsKey(candidateMember.FieldName))
                {
                    continue;
                }

                return "Node '" + FormatViewPath(candidateUnit) +
                       "' has an extra variable '" + candidateMember.FieldName + "'.";
            }

            return null;
        }

        private static Dictionary<string, ExportMember> BuildMembersByFieldName(ExportUnit unit)
        {
            var members = new Dictionary<string, ExportMember>(StringComparer.Ordinal);
            for (var memberIndex = 0; memberIndex < unit.Members.Count; memberIndex++)
            {
                var member = unit.Members[memberIndex];
                members[member.FieldName] = member;
            }

            return members;
        }

        private static string GetMemberTypeIdentity(ExportMember member)
        {
            if (member.Kind == ExportMemberKind.ChildView)
            {
                var generatedNamespace = GetGeneratedNamespace(member.ChildUnit);
                return string.IsNullOrWhiteSpace(generatedNamespace)
                    ? member.ChildUnit.ClassName
                    : generatedNamespace + "." + member.ChildUnit.ClassName;
            }

            return member.ComponentType.AssemblyQualifiedName ??
                   member.ComponentType.FullName ??
                   member.ComponentType.Name;
        }

        private static PrefabAutoBindContainerKind GetContainerKind(ExportMember member)
        {
            return member.Container != null
                ? member.Container.ContainerKind
                : PrefabAutoBindContainerKind.None;
        }

        private static string GetContainerFieldName(ExportMember member)
        {
            return member.Container != null ? member.Container.FieldName ?? string.Empty : string.Empty;
        }

        private static string BuildDuplicateViewError(
            ExportUnit canonicalUnit,
            ExportUnit candidateUnit,
            string detail)
        {
            return "Export failed: View Class '" + canonicalUnit.ClassName +
                   "' is incompatible across nodes. Nodes '" + FormatViewPath(canonicalUnit) +
                   "' and '" + FormatViewPath(candidateUnit) + "': " + detail;
        }

        private static string FormatViewPath(ExportUnit unit)
        {
            return string.IsNullOrWhiteSpace(unit.RootPath) ? "<root>" : unit.RootPath;
        }

        private static bool CollectComponentMembers(
            ExportUnit unit,
            PrefabAutoBindNodeConfig node,
            HashSet<string> usedFieldNames,
            out string errorMessage)
        {
            errorMessage = null;
            for (var componentIndex = 0; componentIndex < node.Components.Count; componentIndex++)
            {
                var component = node.Components[componentIndex];
                if (component == null)
                {
                    continue;
                }

                var type = PrefabAutoBindEditorUtility.ResolveType(component.ComponentTypeName);
                if (type == null || !typeof(Component).IsAssignableFrom(type))
                {
                    errorMessage = "Export failed: node '" + node.NodeName + "' has an invalid component type.";
                    return false;
                }

                NormalizeComponentEvents(component);
                NormalizeComponentContainer(component);

                component.FieldName = ReserveUniqueIdentifier(
                    usedFieldNames,
                    component.FieldName,
                    BuildDefaultComponentFieldName(node, component.ComponentTypeName, componentIndex == 0));

                unit.Members.Add(new ExportMember
                {
                    Kind = ExportMemberKind.Component,
                    Node = node,
                    Component = component,
                    ComponentType = type,
                    FieldName = component.FieldName,
                    RelativeNodePath = PrefabAutoBindEditorUtility.GetPathRelativeTo(node.NodePath, unit.RootPath),
                });
            }

            return true;
        }

        private static string BuildLogicFile(ExportUnit unit)
        {
            var generatedNamespace = GetGeneratedNamespace(unit);
            var baseClassName = ResolveEffectiveBaseClassName(unit.Settings, unit.ExportMode);
            var builder = new StringBuilder();
            builder.AppendLine("using UnityEngine;");
            builder.AppendLine();
            AppendClassBlock(builder, generatedNamespace, unit.ClassName, baseClassName, innerBuilder =>
            {
                innerBuilder.AppendLine("    {");
                innerBuilder.AppendLine("    }");
            });
            return EnsureTrailingLineEnding(builder.ToString());
        }

        private static void AppendClassBlock(
            StringBuilder builder,
            string generatedNamespace,
            string className,
            string baseClassName,
            Action<StringBuilder> writeClassBody)
        {
            if (string.IsNullOrWhiteSpace(generatedNamespace))
            {
                AppendClassDeclaration(builder, className, baseClassName, string.Empty);
                writeClassBody(builder);
                return;
            }

            builder.AppendLine("namespace " + generatedNamespace);
            builder.AppendLine("{");
            AppendClassDeclaration(builder, className, baseClassName, "    ");
            writeClassBody(builder);
            builder.AppendLine("}");
        }

        private static void AppendClassDeclaration(
            StringBuilder builder,
            string className,
            string baseClassName,
            string indent)
        {
            builder.Append(indent);
            builder.Append("public partial class ");
            builder.Append(className);
            if (!string.IsNullOrWhiteSpace(baseClassName))
            {
                builder.Append(" : ");
                builder.Append(baseClassName);
            }

            builder.AppendLine();
        }

        private static string BuildFieldDeclaration(ExportUnit ownerUnit, ExportMember member)
        {
            if (member.Kind == ExportMemberKind.ChildView)
            {
                var childTypeName = GetChildTypeReference(ownerUnit, member.ChildUnit);
                if (ownerUnit.ExportMode == PrefabAutoBindExportMode.DirectReference)
                {
                    return "[SerializeField] private " + childTypeName + " " + member.FieldName + ";";
                }

                return "private " + childTypeName + " " + member.FieldName + ";";
            }

            var accessModifier = GetAccessModifierKeyword(member.Component.AccessModifier);
            var fieldType = member.ComponentType.Name;
            if (ownerUnit.ExportMode == PrefabAutoBindExportMode.DirectReference)
            {
                return "[SerializeField] " + accessModifier + " " + fieldType + " " + member.FieldName + ";";
            }

            return accessModifier + " " + fieldType + " " + member.FieldName + ";";
        }

        private static string BuildFieldDeclaration(ExportUnit ownerUnit, ExportContainer container)
        {
            var accessModifier = GetAccessModifierKeyword(container.AccessModifier);
            var fieldType = BuildContainerFieldType(ownerUnit, container);
            if (ownerUnit.ExportMode == PrefabAutoBindExportMode.DirectReference)
            {
                return "[SerializeField] " + accessModifier + " " + fieldType + " " + container.FieldName + ";";
            }

            return accessModifier + " " + fieldType + " " + container.FieldName + ";";
        }

        private static string BuildPartialFile(ExportUnit unit)
        {
            var settings = unit.Settings;
            var generatedNamespace = GetGeneratedNamespace(unit);
            var generatedEvents = GetGeneratedComponentEvents(unit);
            var namespaces = new HashSet<string>(StringComparer.Ordinal)
            {
                "UnityEngine",
            };

            if (HasListContainers(unit))
            {
                namespaces.Add("System.Collections.Generic");
            }

            for (var memberIndex = 0; memberIndex < unit.Members.Count; memberIndex++)
            {
                var member = unit.Members[memberIndex];
                if (member.Kind != ExportMemberKind.Component)
                {
                    continue;
                }

                var componentNamespace = member.ComponentType.Namespace;
                if (!string.IsNullOrWhiteSpace(componentNamespace))
                {
                    namespaces.Add(componentNamespace);
                }
            }

            var usingBuilder = new StringBuilder();
            usingBuilder.AppendLine("using UnityEngine;");
            foreach (var usingNamespace in namespaces)
            {
                if (string.Equals(usingNamespace, "UnityEngine", StringComparison.Ordinal))
                {
                    continue;
                }

                usingBuilder.AppendLine("using " + usingNamespace + ";");
            }

            var content = PrefabAutoBindTemplateRenderer.RenderTemplate(
                settings.BindTemplate,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "class_name", unit.ClassName ?? string.Empty },
                    { "namespace", generatedNamespace ?? string.Empty },
                    { "parent_class", settings.ParentClassName ?? string.Empty },
                    { "root_object", (settings.RootObjectName ?? string.Empty).Trim() },
                });

            content = PrefabAutoBindTemplateRenderer.RenderMultilinePlaceholder(
                content,
                "usings",
                usingBuilder.ToString().TrimEnd());
            content = PrefabAutoBindTemplateRenderer.RenderMultilinePlaceholder(
                content,
                "fields",
                BuildFieldDeclarations(unit));
            content = PrefabAutoBindTemplateRenderer.RenderMultilinePlaceholder(
                content,
                "find_statements",
                unit.ExportMode == PrefabAutoBindExportMode.BindByFind
                    ? BuildFindStatements(unit)
                    : string.Empty);
            content = PrefabAutoBindTemplateRenderer.RenderMultilinePlaceholder(
                content,
                "add_event_statements",
                BuildEventStatements(settings.AddEventStatementTemplate, generatedEvents));
            content = PrefabAutoBindTemplateRenderer.RenderMultilinePlaceholder(
                content,
                "remove_event_statements",
                BuildEventStatements(settings.RemoveEventStatementTemplate, generatedEvents));
            content = PrefabAutoBindTemplateRenderer.RenderMultilinePlaceholder(
                content,
                "event_handlers",
                BuildEventHandlerDeclarations(generatedEvents));

            content = EnsureLogicTemplateNamespace(content, generatedNamespace);
            return EnsureTrailingLineEnding(content);
        }

        private static string BuildFieldDeclarations(ExportUnit unit)
        {
            var builder = new StringBuilder();
            var previousNodePath = string.Empty;
            var wroteField = false;
            for (var memberIndex = 0; memberIndex < unit.Members.Count; memberIndex++)
            {
                var member = unit.Members[memberIndex];
                if (member.Container != null)
                {
                    continue;
                }

                if (!string.Equals(previousNodePath, member.Node.NodePath, StringComparison.Ordinal))
                {
                    if (wroteField)
                    {
                        builder.AppendLine();
                    }

                    previousNodePath = member.Node.NodePath;
                }

                builder.AppendLine(BuildFieldDeclaration(unit, member));
                wroteField = true;
            }

            if (unit.Containers.Count > 0)
            {
                if (wroteField)
                {
                    builder.AppendLine();
                }

                for (var containerIndex = 0; containerIndex < unit.Containers.Count; containerIndex++)
                {
                    builder.AppendLine(BuildFieldDeclaration(unit, unit.Containers[containerIndex]));
                }
            }

            return builder.ToString().TrimEnd();
        }

        private static string BuildEventStatements(
            string statementTemplate,
            IReadOnlyList<GeneratedComponentEvent> generatedEvents)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < generatedEvents.Count; index++)
            {
                var generatedEvent = generatedEvents[index];
                var memberAvailability = BuildMemberAvailabilityCondition(generatedEvent.Member);
                builder.AppendLine("if (" + memberAvailability + ")");
                builder.AppendLine("{");
                AppendIndentedLines(
                    builder,
                    "    ",
                    PrefabAutoBindTemplateRenderer.RenderTemplate(
                        statementTemplate,
                        BuildEventStatementTemplateVariables(generatedEvent)).Trim());
                builder.AppendLine("}");
            }

            return builder.ToString().TrimEnd();
        }

        private static string BuildEventHandlerDeclarations(IReadOnlyList<GeneratedComponentEvent> generatedEvents)
        {
            var builder = new StringBuilder();
            var declaredHandlerMethodNames = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < generatedEvents.Count; index++)
            {
                var handlerMethodName = generatedEvents[index].HandlerMethodName;
                if (!declaredHandlerMethodNames.Add(handlerMethodName))
                {
                    continue;
                }

                builder.Append("partial void " + handlerMethodName + "(");
                builder.Append(BuildMethodParameterList(generatedEvents[index].SupportedEvent, "value"));
                builder.AppendLine(");");
            }

            return builder.ToString().TrimEnd();
        }

        private static Dictionary<string, string> BuildEventStatementTemplateVariables(GeneratedComponentEvent generatedEvent)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "event_obj", BuildMemberAccessExpression(generatedEvent.Member) },
                { "event_name", generatedEvent.SupportedEvent.EventPropertyName },
                { "handler_name", generatedEvent.HandlerMethodName },
            };
        }

        private static string BuildFindStatements(ExportUnit unit)
        {
            var builder = new StringBuilder();
            var boundMembers = new List<ExportMemberBinding>();
            AppendContainerAllocations(builder, unit);

            var sortedMembers = new List<ExportMember>(unit.Members);
            sortedMembers.Sort((left, right) =>
            {
                var leftDepth = PrefabAutoBindTemplateRenderer.GetNodePathDepth(left.Node != null ? left.Node.NodePath : string.Empty);
                var rightDepth = PrefabAutoBindTemplateRenderer.GetNodePathDepth(right.Node != null ? right.Node.NodePath : string.Empty);
                var depthCompare = leftDepth.CompareTo(rightDepth);
                if (depthCompare != 0)
                {
                    return depthCompare;
                }

                return string.Compare(left.FieldName, right.FieldName, StringComparison.Ordinal);
            });

            for (var memberIndex = 0; memberIndex < sortedMembers.Count; memberIndex++)
            {
                var member = sortedMembers[memberIndex];
                if (member.Container != null)
                {
                    AppendContainerFindLine(builder, unit, member, boundMembers);
                    continue;
                }

                if (member.Kind == ExportMemberKind.Component || member.Kind == ExportMemberKind.ChildView)
                {
                    AppendFindMemberLine(builder, unit, member, boundMembers);
                    if (CanUseAsFindParent(member))
                    {
                        boundMembers.Add(CreateMemberBinding(member));
                    }
                }
            }

            return builder.ToString().TrimEnd();
        }

        private static void AppendContainerAllocations(StringBuilder builder, ExportUnit unit)
        {
            for (var containerIndex = 0; containerIndex < unit.Containers.Count; containerIndex++)
            {
                var container = unit.Containers[containerIndex];
                var elementType = container.MemberKind == ExportMemberKind.ChildView
                    ? GetChildTypeReference(unit, container.ChildUnit)
                    : container.ComponentType.Name;
                if (container.ContainerKind == PrefabAutoBindContainerKind.Array)
                {
                    builder.AppendLine(
                        container.FieldName + " = new " + elementType + "[" + container.Members.Count + "];");
                    continue;
                }

                builder.AppendLine(
                    container.FieldName + " = new List<" + elementType + ">(" + container.Members.Count + ");");
            }
        }

        private static void AppendContainerFindLine(
            StringBuilder builder,
            ExportUnit unit,
            ExportMember member,
            List<ExportMemberBinding> boundMembers)
        {
            var container = member.Container;
            if (container.ContainerKind == PrefabAutoBindContainerKind.Array)
            {
                var tempMember = new ExportMember
                {
                    Kind = member.Kind,
                    Node = member.Node,
                    Component = member.Component,
                    ComponentType = member.ComponentType,
                    FieldName = container.FieldName + "[" + member.ContainerIndex + "]",
                    RelativeNodePath = member.RelativeNodePath,
                    ChildUnit = member.ChildUnit,
                };
                AppendFindMemberLine(builder, unit, tempMember, boundMembers);
                return;
            }

            var tempVariableName = "temp_" + member.FieldName;
            var listTempMember = new ExportMember
            {
                Kind = member.Kind,
                Node = member.Node,
                Component = member.Component,
                ComponentType = member.ComponentType,
                FieldName = member.Kind == ExportMemberKind.ChildView
                    ? tempVariableName
                    : "var " + tempVariableName,
                RelativeNodePath = member.RelativeNodePath,
                ChildUnit = member.ChildUnit,
                Container = container,
            };
            AppendFindMemberLine(builder, unit, listTempMember, boundMembers);
            builder.AppendLine(container.FieldName + ".Add(" + tempVariableName + ");");
        }

        private static void AppendFindMemberLine(
            StringBuilder builder,
            ExportUnit unit,
            ExportMember member,
            List<ExportMemberBinding> boundMembers)
        {
            if (member.Kind == ExportMemberKind.ChildView)
            {
                AppendPlainChildViewFindLines(builder, unit, member, boundMembers);
                return;
            }

            AppendFindStatementLine(builder, unit, member, boundMembers);
        }

        private static bool CanUseAsFindParent(ExportMember member)
        {
            return member != null &&
                   (member.Kind == ExportMemberKind.Component ||
                    member.Kind == ExportMemberKind.ChildView &&
                    IsMonoBehaviourExportUnit(member.ChildUnit));
        }

        private static void AppendPlainChildViewFindLines(
            StringBuilder builder,
            ExportUnit unit,
            ExportMember member,
            List<ExportMemberBinding> boundMembers)
        {
            ResolveFindContext(member, unit, boundMembers, out var parentObj, out var relativePath);
            var targetName = member.FieldName ?? string.Empty;
            var childTypeName = GetChildTypeReference(unit, member.ChildUnit);
            var pathExpression = BuildFindPathExpression(parentObj, relativePath);
            var declareTarget = member.Container != null && IsSimpleIdentifier(targetName);
            var objectName = (declareTarget ? "var " : string.Empty) + targetName;
            var template = GetPlainChildViewBindTemplate(member.ChildUnit != null ? member.ChildUnit.Settings : null);
            var renderedTemplate = PrefabAutoBindTemplateRenderer.RenderTemplate(
                template,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "obj_name", objectName },
                    { "obj_ref", targetName },
                    { "class_name", childTypeName },
                    { "obj_path", pathExpression },
                }).Trim();

            if (!string.IsNullOrEmpty(renderedTemplate))
            {
                builder.AppendLine(renderedTemplate);
            }
        }

        private static string GetPlainChildViewBindTemplate(PrefabAutoBindSettings settings)
        {
            return settings != null && settings.PlainChildViewBindTemplate != null
                ? settings.PlainChildViewBindTemplate
                : PrefabAutoBindSettings.DefaultPlainChildViewBindTemplate;
        }

        private static string BuildFindPathExpression(string parentObj, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return BuildTransformReference(parentObj);
            }

            return parentObj + ".transform.Find(\"" + EscapeString(relativePath) + "\")";
        }

        private static string BuildTransformReference(string parentObj)
        {
            var trimmed = (parentObj ?? string.Empty).Trim();
            if (trimmed.EndsWith(".transform", StringComparison.Ordinal))
            {
                return trimmed;
            }

            return trimmed + ".transform";
        }

        private static bool IsSimpleIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!char.IsLetter(value[0]) && value[0] != '_')
            {
                return false;
            }

            for (var index = 1; index < value.Length; index++)
            {
                if (!char.IsLetterOrDigit(value[index]) && value[index] != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static void AppendFindStatementLine(
            StringBuilder builder,
            ExportUnit unit,
            ExportMember member,
            List<ExportMemberBinding> boundMembers)
        {
            ResolveFindContext(member, unit, boundMembers, out var parentObj, out var relativePath);
            var componentName = member.Kind == ExportMemberKind.ChildView
                ? GetChildTypeReference(unit, member.ChildUnit)
                : member.ComponentType.Name;

            var line = PrefabAutoBindTemplateRenderer.RenderTemplate(unit.Settings.FindStatementTemplate, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "obj_name", member.FieldName },
                { "parent_obj", parentObj },
                { "path", relativePath },
                { "component", componentName },
            });

            builder.AppendLine(line.Trim());
        }

        private static void ResolveFindContext(
            ExportMember member,
            ExportUnit unit,
            List<ExportMemberBinding> boundMembers,
            out string parentObj,
            out string relativePath)
        {
            var memberNodePath = member.Node != null ? member.Node.NodePath ?? string.Empty : string.Empty;
            if (PrefabAutoBindTemplateRenderer.TryFindNearestBoundAncestor(memberNodePath, boundMembers, out var ancestor))
            {
                parentObj = ancestor.FieldName;
                relativePath = PrefabAutoBindTemplateRenderer.GetRelativePathFromAncestor(ancestor.NodePath, memberNodePath);
                return;
            }

            parentObj = unit.Settings.RootObjectName.Trim();
            relativePath = member.RelativeNodePath ?? string.Empty;
        }

        private static void AppendIndentedLines(StringBuilder builder, string indentation, string content)
        {
            var normalizedContent = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalizedContent.Split('\n');
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                builder.Append(indentation);
                builder.AppendLine(lines[lineIndex]);
            }
        }

        private static ExportMemberBinding CreateMemberBinding(ExportMember member)
        {
            return new ExportMemberBinding
            {
                NodePath = member.Node != null ? member.Node.NodePath ?? string.Empty : string.Empty,
                FieldName = member.FieldName,
                ComponentTypeName = member.ComponentType != null ? member.ComponentType.Name : string.Empty,
                IsChildView = member.Kind == ExportMemberKind.ChildView,
                ChildClassName = member.ChildUnit != null ? member.ChildUnit.ClassName : string.Empty,
            };
        }

        private static string BuildDefaultLogicTemplate()
        {
            var builder = new StringBuilder();
            builder.AppendLine("using UnityEngine;");
            builder.AppendLine();
            builder.AppendLine("namespace ${namespace}");
            builder.AppendLine("{");
            builder.AppendLine("    public partial class ${class_name} : MonoBehaviour");
            builder.AppendLine("    {");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static string RenderLogicTemplate(
            string template,
            string generatedNamespace,
            ExportUnit unit,
            string prefabAssetName)
        {
            var content = string.IsNullOrEmpty(template) ? BuildDefaultLogicTemplate() : template;
            var rootNodeName = unit.RootNode != null && !string.IsNullOrWhiteSpace(unit.RootNode.NodeName)
                ? unit.RootNode.NodeName
                : prefabAssetName;

            content = content.Replace("${prefab_name}", unit.ClassName);
            content = content.Replace("${class_name}", unit.ClassName);
            content = content.Replace("${namespace}", generatedNamespace ?? string.Empty);
            content = content.Replace("${prefab_asset_name}", prefabAssetName ?? string.Empty);
            content = content.Replace("${root_node_name}", rootNodeName ?? string.Empty);
            content = content.Replace("${root_node_path}", unit.RootPath ?? string.Empty);
            content = content.Replace("${export_mode}", unit.ExportMode.ToString());
            return content;
        }

        private static string EnsureLogicTemplateNamespace(string content, string generatedNamespace)
        {
            if (string.IsNullOrWhiteSpace(content) ||
                string.IsNullOrWhiteSpace(generatedNamespace) ||
                ContainsNamespaceDeclaration(content))
            {
                return content;
            }

            var lineEnding = DetectLineEnding(content);
            var normalizedContent = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var builder = new StringBuilder();
            builder.Append("namespace ");
            builder.Append(generatedNamespace);
            builder.Append(lineEnding);
            builder.Append("{");
            builder.Append(lineEnding);

            for (var index = 0; index < normalizedContent.Length; index++)
            {
                builder.Append("    ");
                builder.Append(normalizedContent[index]);
                builder.Append(lineEnding);
            }

            builder.Append("}");
            builder.Append(lineEnding);
            return builder.ToString();
        }

        private static bool ContainsNamespaceDeclaration(string content)
        {
            return !string.IsNullOrWhiteSpace(content) && content.IndexOf("namespace ", StringComparison.Ordinal) >= 0;
        }

        private static bool ContainsLogicClassDeclaration(string content, string className)
        {
            return !string.IsNullOrWhiteSpace(content) &&
                   content.IndexOf("partial class " + className, StringComparison.Ordinal) >= 0;
        }

        private static string EnsureTrailingLineEnding(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return Environment.NewLine;
            }

            var lineEnding = DetectLineEnding(content);
            if (content.EndsWith("\r\n", StringComparison.Ordinal) || content.EndsWith("\n", StringComparison.Ordinal))
            {
                return content;
            }

            return content + lineEnding;
        }

        private static string BuildDefaultViewClassName(PrefabAutoBindNodeConfig node)
        {
            var baseName = PrefabAutoBindEditorUtility.MakeSafeIdentifier(node != null ? node.NodeName : "View", "View");
            if (!baseName.EndsWith("View", StringComparison.Ordinal))
            {
                baseName += "View";
            }

            return baseName;
        }

        private static void NormalizeComponentEvents(PrefabAutoBindComponentConfig component)
        {
            var existingEvents = new HashSet<PrefabAutoBindComponentEventKind>();
            for (var index = component.Events.Count - 1; index >= 0; index--)
            {
                var eventKind = component.Events[index];
                if (!existingEvents.Add(eventKind) ||
                    !PrefabAutoBindComponentEventCatalog.TryGetSupportedEvent(component.ComponentTypeName, eventKind, out _))
                {
                    component.Events.RemoveAt(index);
                }
            }
        }

        private static List<GeneratedComponentEvent> GetGeneratedComponentEvents(ExportUnit unit)
        {
            var generatedEvents = new List<GeneratedComponentEvent>();
            for (var memberIndex = 0; memberIndex < unit.Members.Count; memberIndex++)
            {
                var member = unit.Members[memberIndex];
                if (member.Kind != ExportMemberKind.Component)
                {
                    continue;
                }

                for (var eventIndex = 0; eventIndex < member.Component.Events.Count; eventIndex++)
                {
                    var eventKind = member.Component.Events[eventIndex];
                    if (!PrefabAutoBindComponentEventCatalog.TryGetSupportedEvent(member.Component.ComponentTypeName, eventKind, out var supportedEvent))
                    {
                        continue;
                    }

                    var handlerMethodName = PrefabAutoBindTemplateRenderer.RenderEventHandlerName(
                        unit.Settings.EventHandlerNameTemplate,
                        member.FieldName,
                        supportedEvent.EventPropertyName);

                    generatedEvents.Add(new GeneratedComponentEvent
                    {
                        Member = member,
                        SupportedEvent = supportedEvent,
                        HandlerMethodName = handlerMethodName,
                        WrapperMethodName = PrefabAutoBindComponentEventCatalog.BuildWrapperMethodName(member.FieldName, supportedEvent),
                    });
                }
            }

            return generatedEvents;
        }

        private static void EnsureLogicFileContainsEventHandlers(
            string logicFilePath,
            ExportUnit unit)
        {
            var generatedEvents = GetGeneratedComponentEvents(unit);
            if (generatedEvents.Count == 0)
            {
                return;
            }

            var content = File.ReadAllText(logicFilePath);
            var contentChanged = false;
            var missingGeneratedEvents = new List<GeneratedComponentEvent>();
            var seenHandlerMethodNames = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < generatedEvents.Count; index++)
            {
                var generatedEvent = generatedEvents[index];
                var handlerMethodName = generatedEvent.HandlerMethodName;
                if (!seenHandlerMethodNames.Add(handlerMethodName))
                {
                    continue;
                }

                if (ContainsHandlerMethod(content, generatedEvent))
                {
                    continue;
                }

                if (TryUpgradeLegacyHandlerSignature(ref content, generatedEvent))
                {
                    contentChanged = true;
                    continue;
                }

                missingGeneratedEvents.Add(generatedEvent);
            }

            if (missingGeneratedEvents.Count > 0)
            {
                content = InsertLogicHandlerMethods(content, unit.ClassName, missingGeneratedEvents);
                if (content == null)
                {
                    return;
                }

                contentChanged = true;
            }

            if (!contentChanged)
            {
                return;
            }

            File.WriteAllText(logicFilePath, content, new UTF8Encoding(false));
        }

        private static bool ContainsHandlerMethod(string content, GeneratedComponentEvent generatedEvent)
        {
            if (!TryGetMethodParameterList(content, generatedEvent.HandlerMethodName, out var parameterList))
            {
                return false;
            }

            return ParameterListMatches(parameterList, generatedEvent.SupportedEvent);
        }

        private static string InsertLogicHandlerMethods(
            string content,
            string className,
            IReadOnlyList<GeneratedComponentEvent> generatedEvents)
        {
            var classToken = "partial class " + className;
            var classIndex = content.IndexOf(classToken, StringComparison.Ordinal);
            if (classIndex < 0)
            {
                return null;
            }

            var classOpenBraceIndex = content.IndexOf('{', classIndex);
            if (classOpenBraceIndex < 0)
            {
                return null;
            }

            var classCloseBraceIndex = FindMatchingBrace(content, classOpenBraceIndex);
            if (classCloseBraceIndex < 0)
            {
                return null;
            }

            var insertionIndex = FindLineStart(content, classCloseBraceIndex);
            var prependBlankLine = ContainsNonWhitespace(content, classOpenBraceIndex + 1, insertionIndex);

            return content.Insert(
                insertionIndex,
                BuildLogicHandlerMethods(DetectLineEnding(content), generatedEvents, prependBlankLine));
        }

        private static bool TryUpgradeLegacyHandlerSignature(ref string content, GeneratedComponentEvent generatedEvent)
        {
            if (!generatedEvent.SupportedEvent.HasListenerParameter)
            {
                return false;
            }

            var legacySignature = "void " + generatedEvent.HandlerMethodName + "()";
            var legacySignatureIndex = content.IndexOf(legacySignature, StringComparison.Ordinal);
            if (legacySignatureIndex < 0)
            {
                return false;
            }

            var replacementSignature =
                "void " + generatedEvent.HandlerMethodName + "(" +
                BuildMethodParameterList(generatedEvent.SupportedEvent, "value") + ")";
            content = content.Remove(legacySignatureIndex, legacySignature.Length)
                .Insert(legacySignatureIndex, replacementSignature);
            return true;
        }

        private static bool TryGetMethodParameterList(string content, string methodName, out string parameterList)
        {
            var signature = "void " + methodName + "(";
            var signatureIndex = content.IndexOf(signature, StringComparison.Ordinal);
            if (signatureIndex < 0)
            {
                parameterList = null;
                return false;
            }

            var parameterStartIndex = signatureIndex + signature.Length;
            var parameterEndIndex = content.IndexOf(')', parameterStartIndex);
            if (parameterEndIndex < 0)
            {
                parameterList = null;
                return false;
            }

            parameterList = content.Substring(parameterStartIndex, parameterEndIndex - parameterStartIndex);
            return true;
        }

        private static bool ParameterListMatches(
            string parameterList,
            PrefabAutoBindSupportedComponentEvent supportedEvent)
        {
            var normalizedParameterList = parameterList.Trim();
            if (!supportedEvent.HasListenerParameter)
            {
                return normalizedParameterList.Length == 0;
            }

            return normalizedParameterList.IndexOf(supportedEvent.ListenerParameterTypeName, StringComparison.Ordinal) >= 0;
        }

        private static int FindLineStart(string content, int index)
        {
            while (index > 0)
            {
                var previous = content[index - 1];
                if (previous == '\r' || previous == '\n')
                {
                    break;
                }

                index--;
            }

            return index;
        }

        private static bool ContainsNonWhitespace(string content, int startIndex, int endIndex)
        {
            for (var index = startIndex; index < endIndex; index++)
            {
                if (!char.IsWhiteSpace(content[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private static int FindMatchingBrace(string content, int openBraceIndex)
        {
            var depth = 0;
            var inLineComment = false;
            var inBlockComment = false;
            var inString = false;
            var inVerbatimString = false;
            var inCharacter = false;

            for (var index = openBraceIndex; index < content.Length; index++)
            {
                var current = content[index];
                var next = index + 1 < content.Length ? content[index + 1] : '\0';

                if (inLineComment)
                {
                    if (current == '\r' || current == '\n')
                    {
                        inLineComment = false;
                    }

                    continue;
                }

                if (inBlockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        inBlockComment = false;
                        index++;
                    }

                    continue;
                }

                if (inString)
                {
                    if (inVerbatimString)
                    {
                        if (current == '"')
                        {
                            if (next == '"')
                            {
                                index++;
                            }
                            else
                            {
                                inString = false;
                                inVerbatimString = false;
                            }
                        }
                    }
                    else if (current == '\\')
                    {
                        index++;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (inCharacter)
                {
                    if (current == '\\')
                    {
                        index++;
                    }
                    else if (current == '\'')
                    {
                        inCharacter = false;
                    }

                    continue;
                }

                if (current == '/' && next == '/')
                {
                    inLineComment = true;
                    index++;
                    continue;
                }

                if (current == '/' && next == '*')
                {
                    inBlockComment = true;
                    index++;
                    continue;
                }

                if (current == '@' && next == '"')
                {
                    inString = true;
                    inVerbatimString = true;
                    index++;
                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    continue;
                }

                if (current == '\'')
                {
                    inCharacter = true;
                    continue;
                }

                if (current == '{')
                {
                    depth++;
                    continue;
                }

                if (current == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }
                }
            }

            return -1;
        }

        private static string DetectLineEnding(string content)
        {
            return content.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        }

        private static string BuildLogicHandlerMethods(
            string lineEnding,
            IReadOnlyList<GeneratedComponentEvent> generatedEvents,
            bool prependBlankLine)
        {
            var builder = new StringBuilder();
            if (prependBlankLine)
            {
                builder.Append(lineEnding);
            }

            for (var index = 0; index < generatedEvents.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(lineEnding);
                }

                builder.Append("        partial void ");
                builder.Append(generatedEvents[index].HandlerMethodName);
                builder.Append("(");
                builder.Append(BuildMethodParameterList(generatedEvents[index].SupportedEvent, "value"));
                builder.Append(")");
                builder.Append(lineEnding);
                builder.AppendLine("        {");
                builder.AppendLine("        }");
            }

            return builder.ToString();
        }

        private static string BuildMethodParameterList(
            PrefabAutoBindSupportedComponentEvent supportedEvent,
            string parameterName)
        {
            if (!supportedEvent.HasListenerParameter)
            {
                return string.Empty;
            }

            return GetMethodParameterTypeName(supportedEvent) + " " + parameterName;
        }

        private static string GetMethodParameterTypeName(PrefabAutoBindSupportedComponentEvent supportedEvent)
        {
            if (supportedEvent == null || string.IsNullOrWhiteSpace(supportedEvent.ListenerParameterTypeName))
            {
                return string.Empty;
            }

            return string.Equals(supportedEvent.ListenerParameterTypeName, "Vector2", StringComparison.Ordinal)
                ? "UnityEngine.Vector2"
                : supportedEvent.ListenerParameterTypeName;
        }

        private static string BuildDefaultComponentFieldName(
            PrefabAutoBindNodeConfig node,
            string componentTypeName,
            bool isFirstComponent)
        {
            var baseName = PrefabAutoBindEditorUtility.MakeSafeIdentifier(node != null ? node.NodeName : "node", "node");
            if (!isFirstComponent)
            {
                baseName += "_" + PrefabAutoBindEditorUtility.GetShortTypeName(componentTypeName);
            }

            return baseName;
        }

        private static void NormalizeComponentContainer(PrefabAutoBindComponentConfig component)
        {
            component.ContainerName = string.IsNullOrWhiteSpace(component.ContainerName)
                ? string.Empty
                : PrefabAutoBindEditorUtility.MakeSafeIdentifier(component.ContainerName, "container");
        }

        private static void NormalizeViewContainer(PrefabAutoBindNodeConfig node)
        {
            node.ViewContainerName = string.IsNullOrWhiteSpace(node.ViewContainerName)
                ? string.Empty
                : PrefabAutoBindEditorUtility.MakeSafeIdentifier(node.ViewContainerName, "container");
        }

        private static bool FinalizeUnitContainers(ExportUnit unit, out string errorMessage)
        {
            errorMessage = null;
            unit.Containers.Clear();

            var usedFieldNames = new HashSet<string>(StringComparer.Ordinal);
            for (var memberIndex = 0; memberIndex < unit.Members.Count; memberIndex++)
            {
                var member = unit.Members[memberIndex];
                member.Container = null;
                member.ContainerIndex = -1;
                if (!UsesContainer(member))
                {
                    usedFieldNames.Add(member.FieldName);
                }
            }

            var containersByName = new Dictionary<string, ExportContainer>(StringComparer.Ordinal);
            for (var memberIndex = 0; memberIndex < unit.Members.Count; memberIndex++)
            {
                var member = unit.Members[memberIndex];
                if (!UsesContainer(member))
                {
                    continue;
                }

                var containerName = GetConfiguredContainerName(member);
                if (!containersByName.TryGetValue(containerName, out var container))
                {
                    container = new ExportContainer
                    {
                        GroupName = containerName,
                        ContainerKind = GetConfiguredContainerKind(member),
                        AccessModifier = GetConfiguredAccessModifier(member),
                        MemberKind = member.Kind,
                        ComponentType = member.ComponentType,
                        ChildUnit = member.ChildUnit,
                    };
                    containersByName.Add(containerName, container);
                    unit.Containers.Add(container);
                }
                else
                {
                    if (container.ContainerKind != GetConfiguredContainerKind(member))
                    {
                        errorMessage = "Export failed: container '" + containerName + "' in class '" + unit.ClassName + "' uses multiple container types.";
                        return false;
                    }

                    if (!string.Equals(
                            GetContainerElementTypeIdentity(container),
                            GetMemberTypeIdentity(member),
                            StringComparison.Ordinal))
                    {
                        errorMessage = "Export failed: container '" + containerName + "' in class '" + unit.ClassName + "' contains different component or view types. Split them into separate containers.";
                        return false;
                    }
                }

                member.Container = container;
                member.ContainerIndex = container.Members.Count;
                container.Members.Add(member);
            }

            for (var containerIndex = 0; containerIndex < unit.Containers.Count; containerIndex++)
            {
                var container = unit.Containers[containerIndex];
                container.FieldName = ReserveUniqueIdentifier(
                    usedFieldNames,
                    container.GroupName,
                    GetContainerFallbackName(container));
            }

            return true;
        }

        private static bool UsesContainer(ExportMember member)
        {
            if (member.Kind == ExportMemberKind.Component)
            {
                return member.Component != null &&
                       member.Component.ContainerKind != PrefabAutoBindContainerKind.None &&
                       !string.IsNullOrWhiteSpace(member.Component.ContainerName);
            }

            return member.Kind == ExportMemberKind.ChildView &&
                   member.Node != null &&
                   member.Node.ViewContainerKind != PrefabAutoBindContainerKind.None &&
                   !string.IsNullOrWhiteSpace(member.Node.ViewContainerName);
        }

        private static PrefabAutoBindContainerKind GetConfiguredContainerKind(ExportMember member)
        {
            return member.Kind == ExportMemberKind.ChildView && member.Node != null
                ? member.Node.ViewContainerKind
                : member.Component != null
                    ? member.Component.ContainerKind
                    : PrefabAutoBindContainerKind.None;
        }

        private static string GetConfiguredContainerName(ExportMember member)
        {
            return member.Kind == ExportMemberKind.ChildView && member.Node != null
                ? (member.Node.ViewContainerName ?? string.Empty).Trim()
                : member.Component != null
                    ? (member.Component.ContainerName ?? string.Empty).Trim()
                    : string.Empty;
        }

        private static PrefabAutoBindFieldAccessModifier GetConfiguredAccessModifier(ExportMember member)
        {
            return member.Kind == ExportMemberKind.Component && member.Component != null
                ? member.Component.AccessModifier
                : PrefabAutoBindFieldAccessModifier.Private;
        }

        private static string GetContainerFallbackName(ExportContainer container)
        {
            if (container.MemberKind == ExportMemberKind.ChildView && container.ChildUnit != null)
            {
                return container.ChildUnit.ClassName;
            }

            return container.ComponentType != null
                ? PrefabAutoBindEditorUtility.GetShortTypeName(container.ComponentType.FullName)
                : "container";
        }

        private static string GetContainerElementTypeIdentity(ExportContainer container)
        {
            if (container.MemberKind == ExportMemberKind.ChildView && container.ChildUnit != null)
            {
                var generatedNamespace = GetGeneratedNamespace(container.ChildUnit);
                return string.IsNullOrWhiteSpace(generatedNamespace)
                    ? container.ChildUnit.ClassName
                    : generatedNamespace + "." + container.ChildUnit.ClassName;
            }

            return container.ComponentType != null
                ? container.ComponentType.AssemblyQualifiedName ??
                  container.ComponentType.FullName ??
                  container.ComponentType.Name
                : string.Empty;
        }

        private static bool HasListContainers(ExportUnit unit)
        {
            for (var containerIndex = 0; containerIndex < unit.Containers.Count; containerIndex++)
            {
                if (unit.Containers[containerIndex].ContainerKind == PrefabAutoBindContainerKind.List)
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildContainerFieldType(ExportUnit ownerUnit, ExportContainer container)
        {
            var elementType = container.MemberKind == ExportMemberKind.ChildView
                ? GetChildTypeReference(ownerUnit, container.ChildUnit)
                : container.ComponentType.Name;

            if (container.ContainerKind == PrefabAutoBindContainerKind.Array)
            {
                return elementType + "[]";
            }

            return "List<" + elementType + ">";
        }

        private static string BuildMemberAccessExpression(ExportMember member)
        {
            if (member.Container == null)
            {
                return member.FieldName;
            }

            return member.Container.FieldName + "[" + member.ContainerIndex + "]";
        }

        private static string BuildMemberAvailabilityCondition(ExportMember member)
        {
            if (member.Container == null)
            {
                return member.FieldName + " != null";
            }

            var containerLengthProperty = member.Container.ContainerKind == PrefabAutoBindContainerKind.Array
                ? "Length"
                : "Count";
            var memberAccess = BuildMemberAccessExpression(member);
            return member.Container.FieldName + " != null && " + member.Container.FieldName + "." + containerLengthProperty + " > " + member.ContainerIndex + " && " + memberAccess + " != null";
        }

        private static string ReserveUniqueIdentifier(
            HashSet<string> usedNames,
            string proposed,
            string fallback)
        {
            var baseName = PrefabAutoBindEditorUtility.MakeSafeIdentifier(proposed, fallback);
            var candidate = baseName;
            var suffix = 2;
            while (!usedNames.Add(candidate))
            {
                candidate = baseName + suffix;
                suffix++;
            }

            return candidate;
        }

        private static string EscapeString(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static bool TryValidateMetadataDirectories(ExportGraph graph, out string errorMessage)
        {
            errorMessage = null;
            for (var unitIndex = 0; unitIndex < graph.Units.Count; unitIndex++)
            {
                var unit = graph.Units[unitIndex];
                if (ResolveProjectDirectory(unit.LogicOutputDirectory) == null)
                {
                    errorMessage = "The logic dir for class '" + unit.ClassName + "' must be under the Assets directory of the current Unity project.";
                    return false;
                }

                if (ResolveProjectDirectory(unit.PartialOutputDirectory) == null)
                {
                    errorMessage = "The partial dir for class '" + unit.ClassName + "' must be under the Assets directory of the current Unity project.";
                    return false;
                }
            }

            return true;
        }

        private static MetadataManifest BuildMetadataManifest(
            PrefabAutoBindConfig config,
            ExportGraph graph,
            string prefabPath,
            string prefabAssetName)
        {
            var manifest = new MetadataManifest
            {
                generatedAtUtc = DateTime.UtcNow.ToString("O"),
                prefabAssetPath = prefabPath.Replace('\\', '/'),
                prefabAssetName = prefabAssetName,
                prefabName = config.Prefab != null ? config.Prefab.name : prefabAssetName,
                generatedNamespace = GetGeneratedNamespace(graph.RootUnit),
                rootClassName = graph.RootUnit.ClassName,
                rootExportMode = graph.RootUnit.ExportMode.ToString(),
                logicOutputDirectory = graph.RootUnit.LogicOutputDirectory ?? string.Empty,
                partialOutputDirectory = graph.RootUnit.PartialOutputDirectory ?? string.Empty,
            };

            for (var unitIndex = 0; unitIndex < graph.Units.Count; unitIndex++)
            {
                manifest.units.Add(BuildMetadataUnit(graph, graph.Units[unitIndex]));
            }

            return manifest;
        }

        private static MetadataUnit BuildMetadataUnit(ExportGraph graph, ExportUnit unit)
        {
            var metadataUnit = new MetadataUnit
            {
                rootPath = unit.RootPath ?? string.Empty,
                rootNodeName = unit.RootNode != null ? unit.RootNode.NodeName ?? string.Empty : string.Empty,
                rootNodeKind = unit.RootNode != null ? unit.RootNode.NodeKind.ToString() : PrefabAutoBindNodeKind.Field.ToString(),
                className = unit.ClassName ?? string.Empty,
                exportMode = unit.ExportMode.ToString(),
                logicOutputDirectory = unit.LogicOutputDirectory ?? string.Empty,
                partialOutputDirectory = unit.PartialOutputDirectory ?? string.Empty,
            };

            if (TryFindParentMember(graph, unit, out var parentUnit, out var parentMember))
            {
                metadataUnit.parentClassName = parentUnit.ClassName ?? string.Empty;
                metadataUnit.parentFieldName = parentMember.FieldName ?? string.Empty;
                metadataUnit.parentRootPath = parentUnit.RootPath ?? string.Empty;
            }

            for (var memberIndex = 0; memberIndex < unit.Members.Count; memberIndex++)
            {
                metadataUnit.members.Add(BuildMetadataMember(unit.Members[memberIndex]));
            }

            for (var containerIndex = 0; containerIndex < unit.Containers.Count; containerIndex++)
            {
                metadataUnit.containers.Add(BuildMetadataContainer(unit.Containers[containerIndex]));
            }

            return metadataUnit;
        }

        private static MetadataMember BuildMetadataMember(ExportMember member)
        {
            var metadataMember = new MetadataMember
            {
                kind = member.Kind.ToString(),
                nodePath = member.Node != null ? member.Node.NodePath ?? string.Empty : string.Empty,
                nodeName = member.Node != null ? member.Node.NodeName ?? string.Empty : string.Empty,
                fieldName = member.FieldName ?? string.Empty,
                relativeNodePath = member.RelativeNodePath ?? string.Empty,
                containerKind = member.Container != null ? member.Container.ContainerKind.ToString() : PrefabAutoBindContainerKind.None.ToString(),
                containerName = member.Container != null ? member.Container.GroupName ?? string.Empty : string.Empty,
                containerFieldName = member.Container != null ? member.Container.FieldName ?? string.Empty : string.Empty,
                containerIndex = member.ContainerIndex,
            };

            if (member.Kind == ExportMemberKind.Component)
            {
                metadataMember.accessModifier = member.Component != null
                    ? member.Component.AccessModifier.ToString()
                    : PrefabAutoBindFieldAccessModifier.Private.ToString();
                metadataMember.componentTypeName = member.Component != null
                    ? member.Component.ComponentTypeName ?? string.Empty
                    : member.ComponentType != null
                        ? member.ComponentType.AssemblyQualifiedName ?? member.ComponentType.FullName ?? string.Empty
                        : string.Empty;
                metadataMember.componentIndex = member.Component != null ? member.Component.ComponentIndex : -1;

                if (member.Component != null)
                {
                    for (var eventIndex = 0; eventIndex < member.Component.Events.Count; eventIndex++)
                    {
                        metadataMember.events.Add(member.Component.Events[eventIndex].ToString());
                    }
                }
            }
            else if (member.Kind == ExportMemberKind.ChildView && member.ChildUnit != null)
            {
                metadataMember.childClassName = member.ChildUnit.ClassName ?? string.Empty;
                metadataMember.childRootPath = member.ChildUnit.RootPath ?? string.Empty;
            }

            return metadataMember;
        }

        private static MetadataContainer BuildMetadataContainer(ExportContainer container)
        {
            var metadataContainer = new MetadataContainer
            {
                groupName = container.GroupName ?? string.Empty,
                fieldName = container.FieldName ?? string.Empty,
                containerKind = container.ContainerKind.ToString(),
                accessModifier = container.AccessModifier.ToString(),
                componentTypeName = GetContainerElementTypeIdentity(container),
            };

            for (var memberIndex = 0; memberIndex < container.Members.Count; memberIndex++)
            {
                var member = container.Members[memberIndex];
                metadataContainer.memberFieldNames.Add(member.FieldName ?? string.Empty);
                metadataContainer.memberNodePaths.Add(member.Node != null ? member.Node.NodePath ?? string.Empty : string.Empty);
            }

            return metadataContainer;
        }

        private static bool TryFindParentMember(
            ExportGraph graph,
            ExportUnit childUnit,
            out ExportUnit parentUnit,
            out ExportMember parentMember)
        {
            parentUnit = null;
            parentMember = null;

            for (var unitIndex = 0; unitIndex < graph.Units.Count; unitIndex++)
            {
                var candidateUnit = graph.Units[unitIndex];
                for (var memberIndex = 0; memberIndex < candidateUnit.Members.Count; memberIndex++)
                {
                    var candidateMember = candidateUnit.Members[memberIndex];
                    if (candidateMember.Kind != ExportMemberKind.ChildView || candidateMember.ChildUnit != childUnit)
                    {
                        continue;
                    }

                    parentUnit = candidateUnit;
                    parentMember = candidateMember;
                    return true;
                }
            }

            return false;
        }

        private static string BuildMetadataFileName(string className)
        {
            return (string.IsNullOrWhiteSpace(className) ? "PrefabView" : className.Trim()) + ".bind.json";
        }

        private static string GetAccessModifierKeyword(PrefabAutoBindFieldAccessModifier accessModifier)
        {
            switch (accessModifier)
            {
                case PrefabAutoBindFieldAccessModifier.Public:
                    return "public";
                case PrefabAutoBindFieldAccessModifier.Protected:
                    return "protected";
                case PrefabAutoBindFieldAccessModifier.Internal:
                    return "internal";
                default:
                    return "private";
            }
        }

        private static string ResolveProjectDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            var normalized = directory.Replace('\\', '/');
            if (Path.IsPathRooted(normalized))
            {
                normalized = PrefabAutoBindEditorUtility.ToProjectRelativePath(normalized);
            }

            if (string.IsNullOrWhiteSpace(normalized) || !normalized.StartsWith("Assets", StringComparison.Ordinal))
            {
                return null;
            }

            return Path.GetFullPath(Path.Combine(PrefabAutoBindEditorUtility.GetProjectRoot(), normalized));
        }

        private static string ToProjectPath(string absolutePath)
        {
            var projectRelative = PrefabAutoBindEditorUtility.ToProjectRelativePath(absolutePath);
            if (!string.IsNullOrWhiteSpace(projectRelative))
            {
                return projectRelative;
            }

            return absolutePath.Replace('\\', '/');
        }
    }
}
