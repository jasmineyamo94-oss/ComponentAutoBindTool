using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PrefabAutoBind
{
    internal static class PrefabAutoBindDirectReferenceBinder
    {
        internal sealed class BindUnit
        {
            public string ClassName;
            public string RootPath;
            public int[] RootHierarchyIndices;
            public string GeneratedNamespace;
            public string EffectiveBaseClassName;
            public bool AssignSerializedFields = true;
            public List<BindMember> Members = new List<BindMember>();
            public List<BindContainer> Containers = new List<BindContainer>();
        }

        internal sealed class BindMember
        {
            public BindMemberKind Kind;
            public string FieldName;
            public string NodePath;
            public int[] HierarchyIndices;
            public string ComponentTypeName;
            public int ComponentIndex;
            public string ChildClassName;
            public string ChildNamespace;
            public string ChildBaseClassName;
            public BindContainer Container;
            public int ContainerIndex = -1;
        }

        internal enum BindMemberKind
        {
            Component = 0,
            ChildView = 1,
        }

        internal sealed class BindContainer
        {
            public string FieldName;
            public PrefabAutoBindContainerKind ContainerKind;
            public Type ComponentType;
            public List<BindMember> Members = new List<BindMember>();
        }

        public static bool TryRemoveBindByFindComponents(
            string prefabPath,
            IReadOnlyCollection<string> scriptGuids,
            out int removedComponentCount,
            out string errorMessage)
        {
            removedComponentCount = 0;
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(prefabPath))
            {
                errorMessage = "BindByFind cleanup failed: configuration or prefab is missing.";
                return false;
            }

            if (scriptGuids == null || scriptGuids.Count == 0)
            {
                return true;
            }

            var absolutePrefabPath = Path.Combine(
                PrefabAutoBindEditorUtility.GetProjectRoot(),
                prefabPath);
            if (!File.Exists(absolutePrefabPath))
            {
                errorMessage = "BindByFind cleanup failed: prefab file '" + prefabPath + "' was not found.";
                return false;
            }

            var content = File.ReadAllText(absolutePrefabPath);
            if (!content.StartsWith("%YAML", StringComparison.Ordinal))
            {
                errorMessage = "BindByFind cleanup failed: the prefab is not text-serialized. Set Asset Serialization Mode to Force Text.";
                return false;
            }

            var updatedContent = RemoveScriptComponentsFromPrefabYaml(
                content,
                scriptGuids,
                out removedComponentCount);
            if (removedComponentCount == 0)
            {
                return true;
            }

            File.WriteAllText(absolutePrefabPath, updatedContent, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);
            return true;
        }

        private static string RemoveScriptComponentsFromPrefabYaml(
            string content,
            IReadOnlyCollection<string> scriptGuids,
            out int removedComponentCount)
        {
            var lineEnding = content.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var normalizedContent = content.Replace("\r\n", "\n").Replace('\r', '\n');
            var hadTrailingLineEnding = normalizedContent.EndsWith("\n", StringComparison.Ordinal);
            var lines = normalizedContent.Split('\n');
            var removedFileIds = new HashSet<string>(StringComparer.Ordinal);
            var removedLines = new bool[lines.Length];

            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                const string monoBehaviourHeader = "--- !u!114 &";
                if (!lines[lineIndex].StartsWith(monoBehaviourHeader, StringComparison.Ordinal))
                {
                    continue;
                }

                var blockEndIndex = lineIndex + 1;
                while (blockEndIndex < lines.Length &&
                       !lines[blockEndIndex].StartsWith("--- !u!", StringComparison.Ordinal))
                {
                    blockEndIndex++;
                }

                var matchesGeneratedScript = false;
                for (var blockLineIndex = lineIndex + 1; blockLineIndex < blockEndIndex; blockLineIndex++)
                {
                    if (lines[blockLineIndex].IndexOf("m_Script:", StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    foreach (var scriptGuid in scriptGuids)
                    {
                        if (lines[blockLineIndex].IndexOf(
                                "guid: " + scriptGuid + ",",
                                StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        matchesGeneratedScript = true;
                        break;
                    }

                    if (matchesGeneratedScript)
                    {
                        break;
                    }
                }

                if (!matchesGeneratedScript)
                {
                    lineIndex = blockEndIndex - 1;
                    continue;
                }

                removedFileIds.Add(lines[lineIndex].Substring(monoBehaviourHeader.Length).Trim());
                for (var blockLineIndex = lineIndex; blockLineIndex < blockEndIndex; blockLineIndex++)
                {
                    removedLines[blockLineIndex] = true;
                }

                lineIndex = blockEndIndex - 1;
            }

            removedComponentCount = removedFileIds.Count;
            if (removedComponentCount == 0)
            {
                return content;
            }

            var builder = new StringBuilder(content.Length);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                if (removedLines[lineIndex] || IsRemovedComponentListEntry(lines[lineIndex], removedFileIds))
                {
                    continue;
                }

                var line = lines[lineIndex];
                foreach (var removedFileId in removedFileIds)
                {
                    line = line.Replace(
                        "{fileID: " + removedFileId + "}",
                        "{fileID: 0}");
                }

                if (lineIndex == lines.Length - 1 && line.Length == 0 && hadTrailingLineEnding)
                {
                    continue;
                }

                builder.Append(line);
                builder.Append(lineEnding);
            }

            if (!hadTrailingLineEnding && builder.Length >= lineEnding.Length)
            {
                builder.Length -= lineEnding.Length;
            }

            return builder.ToString();
        }

        private static bool IsRemovedComponentListEntry(
            string line,
            HashSet<string> removedFileIds)
        {
            var trimmedLine = line.Trim();
            foreach (var removedFileId in removedFileIds)
            {
                if (string.Equals(
                        trimmedLine,
                        "- component: {fileID: " + removedFileId + "}",
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TryBindPrefab(
            string prefabPath,
            IReadOnlyList<BindUnit> units,
            out string errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(prefabPath) || units == null || units.Count == 0)
            {
                errorMessage = "DirectReference binding failed: prefab or export unit is missing.";
                return false;
            }

            var prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                for (var unitIndex = 0; unitIndex < units.Count; unitIndex++)
                {
                    var unit = units[unitIndex];
                    if (!TryBindUnit(prefabRoot, unit, out errorMessage))
                    {
                        return false;
                    }
                }

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        private static bool TryBindUnit(GameObject prefabRoot, BindUnit unit, out string errorMessage)
        {
            errorMessage = null;
            var unitRoot = ResolveNodeTransform(prefabRoot.transform, unit.RootHierarchyIndices);
            if (unitRoot == null)
            {
                errorMessage = "DirectReference binding failed: node path '" + unit.RootPath + "' was not found.";
                return false;
            }

            var logicType = ResolveGeneratedType(unit.GeneratedNamespace, unit.ClassName);
            if (logicType == null)
            {
                errorMessage = "DirectReference automatic binding failed: compiled type '" + BuildFullTypeName(unit.GeneratedNamespace, unit.ClassName) + "' was not found. Ensure the C# scripts compiled successfully.";
                return false;
            }

            if (!typeof(MonoBehaviour).IsAssignableFrom(logicType))
            {
                errorMessage = "DirectReference binding failed: type '" + logicType.FullName + "' must inherit from MonoBehaviour.";
                return false;
            }

            var component = unitRoot.GetComponent(logicType) as MonoBehaviour;
            if (component == null)
            {
                component = unitRoot.gameObject.AddComponent(logicType) as MonoBehaviour;
            }

            if (component == null)
            {
                errorMessage = "DirectReference binding failed: unable to attach type '" + logicType.FullName + "' to node '" + unitRoot.name + "'.";
                return false;
            }

            if (!unit.AssignSerializedFields)
            {
                return true;
            }

            var serializedObject = new SerializedObject(component);
            for (var memberIndex = 0; memberIndex < unit.Members.Count; memberIndex++)
            {
                var member = unit.Members[memberIndex];
                if (member.Container != null)
                {
                    continue;
                }

                if (!TryAssignMember(prefabRoot.transform, serializedObject, member, out errorMessage))
                {
                    return false;
                }
            }

            for (var containerIndex = 0; containerIndex < unit.Containers.Count; containerIndex++)
            {
                if (!TryAssignContainer(prefabRoot.transform, serializedObject, unit.Containers[containerIndex], out errorMessage))
                {
                    return false;
                }
            }

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        private static bool TryAssignMember(
            Transform prefabRoot,
            SerializedObject serializedObject,
            BindMember member,
            out string errorMessage)
        {
            errorMessage = null;
            var property = serializedObject.FindProperty(member.FieldName);
            if (property == null)
            {
                errorMessage = "DirectReference binding failed: field '" + member.FieldName + "' does not exist or is not serializable.";
                return false;
            }

            if (member.Kind == BindMemberKind.ChildView)
            {
                var childTransform = ResolveNodeTransform(prefabRoot, member.HierarchyIndices);
                if (childTransform == null)
                {
                    errorMessage = "DirectReference binding failed: child view node '" + member.NodePath + "' was not found.";
                    return false;
                }

                var childType = ResolveGeneratedType(member.ChildNamespace, member.ChildClassName);
                if (childType == null)
                {
                    errorMessage = "DirectReference binding failed: child view type '" + BuildFullTypeName(member.ChildNamespace, member.ChildClassName) + "' was not found.";
                    return false;
                }

                if (!typeof(MonoBehaviour).IsAssignableFrom(childType))
                {
                    errorMessage = "DirectReference binding failed: child view type '" + childType.FullName + "' must inherit from MonoBehaviour.";
                    return false;
                }

                var childComponent = childTransform.GetComponent(childType) as MonoBehaviour;
                if (childComponent == null)
                {
                    childComponent = childTransform.gameObject.AddComponent(childType) as MonoBehaviour;
                }

                property.objectReferenceValue = childComponent;
                return true;
            }

            var targetTransform = ResolveNodeTransform(prefabRoot, member.HierarchyIndices);
            if (targetTransform == null)
            {
                errorMessage = "DirectReference binding failed: node '" + member.NodePath + "' was not found.";
                return false;
            }

            var componentType = PrefabAutoBindEditorUtility.ResolveType(member.ComponentTypeName);
            if (componentType == null || !typeof(Component).IsAssignableFrom(componentType))
            {
                errorMessage = "DirectReference binding failed: component type is invalid.";
                return false;
            }

            var components = targetTransform.GetComponents(componentType);
            if (member.ComponentIndex < 0 || member.ComponentIndex >= components.Length)
            {
                errorMessage = "DirectReference binding failed: component index on node '" + member.NodePath + "' is out of range.";
                return false;
            }

            property.objectReferenceValue = components[member.ComponentIndex];
            return true;
        }

        private static bool TryAssignContainer(
            Transform prefabRoot,
            SerializedObject serializedObject,
            BindContainer container,
            out string errorMessage)
        {
            errorMessage = null;
            var property = serializedObject.FindProperty(container.FieldName);
            if (property == null)
            {
                errorMessage = "DirectReference binding failed: container field '" + container.FieldName + "' does not exist or is not serializable.";
                return false;
            }

            if (container.ContainerKind == PrefabAutoBindContainerKind.Array ||
                container.ContainerKind == PrefabAutoBindContainerKind.List)
            {
                property.arraySize = container.Members.Count;
                for (var index = 0; index < container.Members.Count; index++)
                {
                    var member = container.Members[index];
                    if (!TryAssignContainerElement(prefabRoot, property.GetArrayElementAtIndex(index), member, out errorMessage))
                    {
                        return false;
                    }
                }

                return true;
            }

            errorMessage = "DirectReference binding failed: unsupported container type.";
            return false;
        }

        private static bool TryAssignContainerElement(
            Transform prefabRoot,
            SerializedProperty elementProperty,
            BindMember member,
            out string errorMessage)
        {
            errorMessage = null;
            var targetTransform = ResolveNodeTransform(prefabRoot, member.HierarchyIndices);
            if (targetTransform == null)
            {
                errorMessage = "DirectReference binding failed: container element node '" + member.NodePath + "' was not found.";
                return false;
            }

            if (member.Kind == BindMemberKind.ChildView)
            {
                var childType = ResolveGeneratedType(member.ChildNamespace, member.ChildClassName);
                if (childType == null)
                {
                    errorMessage = "DirectReference failed: child view type '" +
                                   BuildFullTypeName(member.ChildNamespace, member.ChildClassName) +
                                   "' was not found.";
                    return false;
                }

                if (!typeof(MonoBehaviour).IsAssignableFrom(childType))
                {
                    errorMessage = "DirectReference failed: child view type '" + childType.FullName +
                                   "' must inherit MonoBehaviour.";
                    return false;
                }

                var childComponent = targetTransform.GetComponent(childType) as MonoBehaviour;
                if (childComponent == null)
                {
                    childComponent = targetTransform.gameObject.AddComponent(childType) as MonoBehaviour;
                }

                elementProperty.objectReferenceValue = childComponent;
                return true;
            }

            var componentType = PrefabAutoBindEditorUtility.ResolveType(member.ComponentTypeName);
            if (componentType == null || !typeof(Component).IsAssignableFrom(componentType))
            {
                errorMessage = "DirectReference binding failed: container element component type is invalid.";
                return false;
            }

            var components = targetTransform.GetComponents(componentType);
            if (member.ComponentIndex < 0 || member.ComponentIndex >= components.Length)
            {
                errorMessage = "DirectReference binding failed: container element component index is out of range.";
                return false;
            }

            elementProperty.objectReferenceValue = components[member.ComponentIndex];
            return true;
        }

        internal static Transform ResolveNodeTransform(Transform prefabRoot, int[] hierarchyIndices)
        {
            if (prefabRoot == null || hierarchyIndices == null)
            {
                return null;
            }

            var current = prefabRoot;
            for (var index = 0; index < hierarchyIndices.Length; index++)
            {
                var childIndex = hierarchyIndices[index];
                if (childIndex < 0 || childIndex >= current.childCount)
                {
                    return null;
                }

                current = current.GetChild(childIndex);
            }

            return current;
        }

        private static Type ResolveGeneratedType(string generatedNamespace, string className)
        {
            if (string.IsNullOrWhiteSpace(className))
            {
                return null;
            }

            var fullName = BuildFullTypeName(generatedNamespace, className);
            var type = Type.GetType(fullName);
            if (type != null)
            {
                return type;
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var index = 0; index < assemblies.Length; index++)
            {
                type = assemblies[index].GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static string BuildFullTypeName(string generatedNamespace, string className)
        {
            return string.IsNullOrWhiteSpace(generatedNamespace)
                ? className
                : generatedNamespace.Trim() + "." + className;
        }
    }
}
