using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PrefabAutoBind
{
    internal sealed class PrefabAutoBindAvailableComponent
    {
        public PrefabAutoBindAvailableComponent(string typeName, string displayName, int componentIndex)
        {
            TypeName = typeName;
            DisplayName = displayName;
            ComponentIndex = componentIndex;
        }

        public string TypeName { get; }

        public string DisplayName { get; }

        public int ComponentIndex { get; }
    }

    internal sealed class PrefabAutoBindNodeSnapshot
    {
        public PrefabAutoBindNodeSnapshot(
            PrefabAutoBindNodeId nodeId,
            PrefabAutoBindNodeId parentNodeId,
            string path,
            string displayName,
            int[] hierarchyIndices,
            List<PrefabAutoBindAvailableComponent> availableComponents)
        {
            NodeId = nodeId;
            ParentNodeId = parentNodeId;
            Path = path;
            DisplayName = displayName;
            HierarchyIndices = hierarchyIndices;
            AvailableComponents = availableComponents;
        }

        public PrefabAutoBindNodeId NodeId { get; }

        public PrefabAutoBindNodeId ParentNodeId { get; }

        public string Path { get; }

        public string DisplayName { get; }

        public int[] HierarchyIndices { get; }

        public int Depth => HierarchyIndices.Length;

        public List<PrefabAutoBindAvailableComponent> AvailableComponents { get; }
    }

    internal static class PrefabAutoBindEditorUtility
    {
        private sealed class SettingsData
        {
            public string GeneratedNamespace;
            public string ParentClassName;
            public string LogicOutputDirectory;
            public string PartialOutputDirectory;
            public string BindTemplate;
            public string AddEventStatementTemplate;
            public string RemoveEventStatementTemplate;
            public string EventHandlerNameTemplate;
            public string RootObjectName;
            public string FindStatementTemplate;
            public string PlainChildViewBindTemplate;

            public string BuildSignature()
            {
                var builder = new StringBuilder();
                AppendSignatureValue(builder, GeneratedNamespace);
                AppendSignatureValue(builder, ParentClassName);
                AppendSignatureValue(builder, LogicOutputDirectory);
                AppendSignatureValue(builder, PartialOutputDirectory);
                AppendSignatureValue(builder, BindTemplate);
                AppendSignatureValue(builder, AddEventStatementTemplate);
                AppendSignatureValue(builder, RemoveEventStatementTemplate);
                AppendSignatureValue(builder, EventHandlerNameTemplate);
                AppendSignatureValue(builder, RootObjectName);
                AppendSignatureValue(builder, FindStatementTemplate);
                AppendSignatureValue(builder, PlainChildViewBindTemplate);
                return builder.ToString();
            }

            public void ApplyTo(PrefabAutoBindSettings settings)
            {
                settings.GeneratedNamespace = GeneratedNamespace;
                settings.ParentClassName = ParentClassName;
                settings.LogicOutputDirectory = LogicOutputDirectory;
                settings.PartialOutputDirectory = PartialOutputDirectory;
                settings.BindTemplate = BindTemplate;
                settings.AddEventStatementTemplate = AddEventStatementTemplate;
                settings.RemoveEventStatementTemplate = RemoveEventStatementTemplate;
                settings.EventHandlerNameTemplate = EventHandlerNameTemplate;
                settings.RootObjectName = RootObjectName;
                settings.FindStatementTemplate = FindStatementTemplate;
                settings.PlainChildViewBindTemplate = PlainChildViewBindTemplate;
            }

            private static void AppendSignatureValue(StringBuilder builder, string value)
            {
                value = value ?? string.Empty;
                builder.Append(value.Length);
                builder.Append(':');
                builder.Append(value);
                builder.Append('|');
            }
        }

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum",
            "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto",
            "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
            "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
            "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
            "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
            "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
        };

        private const string DefaultNamespace = "";

        private const string ConfigFolderName = "Config";

        private const string ConfigAssetSuffix = ".PrefabAutoBind.asset";

        public static PrefabAutoBindConfig GetOrCreateConfig(GameObject prefab)
        {
            var prefabPath = AssetDatabase.GetAssetPath(prefab);
            var configDirectory = EnsureConfigDirectory();
            var matchingConfigs = FindConfigsForPrefab(prefab);
            var config = SelectPreferredConfig(matchingConfigs, configDirectory);
            if (matchingConfigs.Count > 1)
            {
                Debug.LogWarning(
                    "Multiple PrefabAutoBind configurations reference prefab '" + prefabPath +
                    "'. Using '" + AssetDatabase.GetAssetPath(config) + "'. Other configurations were left unchanged.");
            }

            if (config == null)
            {
                var configPath = GetConfigAssetPath(prefabPath);
                config = ScriptableObject.CreateInstance<PrefabAutoBindConfig>();
                config.Prefab = prefab;
                config.ClassName = BuildDefaultClassName(prefab);
                config.SchemaVersion = PrefabAutoBindConfig.CurrentSchemaVersion;
                AssetDatabase.CreateAsset(config, configPath);
            }
            else
            {
                var configPath = AssetDatabase.GetAssetPath(config);
                if (!IsAssetPathInDirectory(configPath, configDirectory))
                {
                    var destinationPath = GetConfigAssetPath(prefabPath);
                    var moveError = AssetDatabase.MoveAsset(configPath, destinationPath);
                    if (string.IsNullOrEmpty(moveError))
                    {
                        configPath = destinationPath;
                    }
                    else
                    {
                        Debug.LogError(
                            "Failed to move PrefabAutoBind configuration from '" + configPath +
                            "' to '" + destinationPath + "': " + moveError +
                            ". The existing configuration will continue to be used in its current location.");
                    }
                }

                if (config.SchemaVersion < PrefabAutoBindConfig.CurrentSchemaVersion)
                {
                    MigrateLegacyConfig(config, configPath);
                }
            }

            if (config.Prefab != prefab)
            {
                config.Prefab = prefab;
            }

            if (string.IsNullOrWhiteSpace(config.ClassName))
            {
                config.ClassName = BuildDefaultClassName(prefab);
            }

            SaveConfig(config);
            return config;
        }

        public static void SaveConfig(PrefabAutoBindConfig config)
        {
            if (config == null)
            {
                return;
            }

            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
        }

        public static bool TryRenamePrefabNodes(
            PrefabAutoBindConfig config,
            IReadOnlyList<PrefabAutoBindNodeSnapshot> snapshots,
            string nodeName,
            out string errorMessage)
        {
            errorMessage = null;
            if (config == null || config.Prefab == null)
            {
                errorMessage = "Rename failed: missing prefab config.";
                return false;
            }

            if (snapshots == null || snapshots.Count == 0)
            {
                errorMessage = "Rename failed: select at least one prefab node.";
                return false;
            }

            var nextName = (nodeName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(nextName))
            {
                errorMessage = "Rename failed: Node Name cannot be empty.";
                return false;
            }

            var prefabPath = AssetDatabase.GetAssetPath(config.Prefab);
            if (string.IsNullOrWhiteSpace(prefabPath) ||
                !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "Rename failed: selected object is not a prefab asset.";
                return false;
            }

            GameObject prefabRoot = null;
            try
            {
                prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                var targets = new List<Transform>(snapshots.Count);
                for (var index = 0; index < snapshots.Count; index++)
                {
                    var snapshot = snapshots[index];
                    if (snapshot == null)
                    {
                        errorMessage = "Rename failed: missing prefab node snapshot.";
                        return false;
                    }

                    var target = PrefabAutoBindDirectReferenceBinder.ResolveNodeTransform(
                        prefabRoot.transform,
                        snapshot.HierarchyIndices);
                    if (target == null)
                    {
                        errorMessage = "Rename failed: could not resolve prefab node '" +
                                       (string.IsNullOrEmpty(snapshot.Path) ? "<root>" : snapshot.Path) + "'.";
                        return false;
                    }

                    targets.Add(target);
                }

                for (var index = 0; index < targets.Count; index++)
                {
                    targets[index].name = nextName;
                }

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
                return true;
            }
            catch (Exception exception)
            {
                errorMessage = "Rename failed: " + exception.Message;
                return false;
            }
            finally
            {
                if (prefabRoot != null)
                {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }
            }
        }

        private static void MigrateLegacyConfig(PrefabAutoBindConfig config, string configPath)
        {
            if (config.SchemaVersion < 1)
            {
                MigrateLegacySettings(config, configPath);
            }

            if (config.SchemaVersion < 2)
            {
                MigrateLegacyExportModes(config);
            }

            if (config.SchemaVersion < 3)
            {
                MigrateLegacyNodeIds(config);
            }

            config.SchemaVersion = PrefabAutoBindConfig.CurrentSchemaVersion;
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
        }

        private static void MigrateLegacyNodeIds(PrefabAutoBindConfig config)
        {
            if (config.Prefab == null)
            {
                return;
            }

            var nodesByPath = new Dictionary<string, Queue<PrefabAutoBindNodeConfig>>(StringComparer.Ordinal);
            for (var nodeIndex = 0; nodeIndex < config.Nodes.Count; nodeIndex++)
            {
                var node = config.Nodes[nodeIndex];
                if (node == null)
                {
                    continue;
                }

                var nodePath = node.NodePath ?? string.Empty;
                if (!nodesByPath.TryGetValue(nodePath, out var nodes))
                {
                    nodes = new Queue<PrefabAutoBindNodeConfig>();
                    nodesByPath.Add(nodePath, nodes);
                }

                nodes.Enqueue(node);
            }

            var snapshots = BuildNodeSnapshots(config.Prefab);
            for (var snapshotIndex = 0; snapshotIndex < snapshots.Count; snapshotIndex++)
            {
                var snapshot = snapshots[snapshotIndex];
                if (!nodesByPath.TryGetValue(snapshot.Path, out var nodes) || nodes.Count == 0)
                {
                    continue;
                }

                var node = nodes.Dequeue();
                node.NodeId = snapshot.NodeId;
                node.ParentNodeId = snapshot.ParentNodeId;
                node.NodePath = snapshot.Path;
            }

            SyncNodes(config, snapshots);
        }

        private static void MigrateLegacySettings(PrefabAutoBindConfig config, string configPath)
        {
            var rootData = config.Settings != null
                ? BuildSettingsData(config.Settings)
                : BuildLegacyRootSettingsData(config);
            var rootSettings = config.Settings != null
                ? config.Settings
                : FindOrCreateMigratedSettings(
                    BuildRootSettingsPath(configPath),
                    rootData);
            config.Settings = rootSettings;

            var settingsBySignature = new Dictionary<string, PrefabAutoBindSettings>(StringComparer.Ordinal)
            {
                { rootData.BuildSignature(), rootSettings },
            };

            for (var nodeIndex = 0; nodeIndex < config.Nodes.Count; nodeIndex++)
            {
                var node = config.Nodes[nodeIndex];
                if (node == null || node.NodeKind != PrefabAutoBindNodeKind.View)
                {
                    continue;
                }

                node.LegacyExportMode = GetLegacyEffectiveExportMode(config, node);
                if (node.Settings != null)
                {
                    settingsBySignature[BuildSettingsData(node.Settings).BuildSignature()] = node.Settings;
                    continue;
                }

                var nodeData = BuildLegacyNodeSettingsData(config, node, rootData);
                var signature = nodeData.BuildSignature();
                if (!settingsBySignature.TryGetValue(signature, out var nodeSettings))
                {
                    nodeSettings = FindOrCreateMigratedSettings(
                        BuildNodeSettingsPath(configPath, node),
                        nodeData);
                    settingsBySignature.Add(signature, nodeSettings);
                }

                node.Settings = nodeSettings;
            }

            config.SchemaVersion = 1;
        }

        private static void MigrateLegacyExportModes(PrefabAutoBindConfig config)
        {
            var migratedSettings = new HashSet<PrefabAutoBindSettings>();
            if (config.Settings != null)
            {
                config.Settings.ExportMode = config.LegacyExportMode;
                migratedSettings.Add(config.Settings);
                EditorUtility.SetDirty(config.Settings);
            }

            for (var nodeIndex = 0; nodeIndex < config.Nodes.Count; nodeIndex++)
            {
                var node = config.Nodes[nodeIndex];
                if (node == null ||
                    node.NodeKind != PrefabAutoBindNodeKind.View ||
                    node.Settings == null ||
                    !migratedSettings.Add(node.Settings))
                {
                    continue;
                }

                node.Settings.ExportMode = node.LegacyExportMode;
                EditorUtility.SetDirty(node.Settings);
            }
        }

        private static SettingsData BuildLegacyRootSettingsData(PrefabAutoBindConfig config)
        {
            return new SettingsData
            {
                GeneratedNamespace = string.IsNullOrWhiteSpace(config.LegacyGeneratedNamespace)
                    ? DefaultNamespace
                    : config.LegacyGeneratedNamespace.Trim(),
                ParentClassName = string.IsNullOrWhiteSpace(config.LegacyParentClassName)
                    ? string.Empty
                    : config.LegacyParentClassName.Trim(),
                LogicOutputDirectory = NormalizeDirectory(
                    config.LegacyLogicOutputDirectory,
                    "Assets/Scripts/Logic"),
                PartialOutputDirectory = NormalizeDirectory(
                    config.LegacyPartialOutputDirectory,
                    "Assets/Scripts/Logic/Generated"),
                BindTemplate = string.IsNullOrEmpty(config.LegacyBindTemplate)
                    ? PrefabAutoBindSettings.DefaultBindTemplate
                    : config.LegacyBindTemplate,
                AddEventStatementTemplate = string.IsNullOrWhiteSpace(config.LegacyEventTemplate)
                    ? PrefabAutoBindSettings.DefaultAddEventStatementTemplate
                    : config.LegacyEventTemplate.Trim(),
                RemoveEventStatementTemplate = string.IsNullOrWhiteSpace(config.LegacyRemoveEventTemplate)
                    ? PrefabAutoBindSettings.DefaultRemoveEventStatementTemplate
                    : config.LegacyRemoveEventTemplate.Trim(),
                EventHandlerNameTemplate = string.IsNullOrWhiteSpace(config.LegacyEventNameRule)
                    ? PrefabAutoBindSettings.DefaultEventHandlerNameTemplate
                    : config.LegacyEventNameRule.Trim(),
                RootObjectName = string.IsNullOrWhiteSpace(config.LegacyRootTransform)
                    ? PrefabAutoBindSettings.DefaultRootObjectName
                    : config.LegacyRootTransform.Trim(),
                FindStatementTemplate = string.IsNullOrWhiteSpace(config.LegacyFindTemplate)
                    ? PrefabAutoBindSettings.DefaultFindStatementTemplate
                    : config.LegacyFindTemplate.Trim(),
                PlainChildViewBindTemplate = PrefabAutoBindSettings.DefaultPlainChildViewBindTemplate,
            };
        }

        private static SettingsData BuildLegacyNodeSettingsData(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig node,
            SettingsData rootData)
        {
            return new SettingsData
            {
                GeneratedNamespace = rootData.GeneratedNamespace,
                ParentClassName = rootData.ParentClassName,
                LogicOutputDirectory = GetLegacyEffectiveLogicOutputDirectory(config, node),
                PartialOutputDirectory = GetLegacyEffectivePartialOutputDirectory(config, node),
                BindTemplate = rootData.BindTemplate,
                AddEventStatementTemplate = rootData.AddEventStatementTemplate,
                RemoveEventStatementTemplate = rootData.RemoveEventStatementTemplate,
                EventHandlerNameTemplate = rootData.EventHandlerNameTemplate,
                RootObjectName = rootData.RootObjectName,
                FindStatementTemplate = rootData.FindStatementTemplate,
                PlainChildViewBindTemplate = rootData.PlainChildViewBindTemplate,
            };
        }

        private static SettingsData BuildSettingsData(PrefabAutoBindSettings settings)
        {
            return new SettingsData
            {
                GeneratedNamespace = settings.GeneratedNamespace ?? string.Empty,
                ParentClassName = settings.ParentClassName ?? string.Empty,
                LogicOutputDirectory = NormalizeDirectory(
                    settings.LogicOutputDirectory,
                    "Assets/Scripts/Logic"),
                PartialOutputDirectory = NormalizeDirectory(
                    settings.PartialOutputDirectory,
                    "Assets/Scripts/Logic/Generated"),
                BindTemplate = settings.BindTemplate ?? string.Empty,
                AddEventStatementTemplate = settings.AddEventStatementTemplate ?? string.Empty,
                RemoveEventStatementTemplate = settings.RemoveEventStatementTemplate ?? string.Empty,
                EventHandlerNameTemplate = settings.EventHandlerNameTemplate ?? string.Empty,
                RootObjectName = settings.RootObjectName ?? string.Empty,
                FindStatementTemplate = settings.FindStatementTemplate ?? string.Empty,
                PlainChildViewBindTemplate = settings.PlainChildViewBindTemplate ??
                                             PrefabAutoBindSettings.DefaultPlainChildViewBindTemplate,
            };
        }

        private static PrefabAutoBindSettings FindOrCreateMigratedSettings(
            string preferredPath,
            SettingsData data)
        {
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(preferredPath);
            var existing = mainAsset as PrefabAutoBindSettings;
            if (existing != null &&
                string.Equals(
                    BuildSettingsData(existing).BuildSignature(),
                    data.BuildSignature(),
                    StringComparison.Ordinal))
            {
                return existing;
            }

            var assetPath = mainAsset == null
                ? preferredPath
                : AssetDatabase.GenerateUniqueAssetPath(preferredPath);
            var settings = ScriptableObject.CreateInstance<PrefabAutoBindSettings>();
            data.ApplyTo(settings);
            AssetDatabase.CreateAsset(settings, assetPath);
            EditorUtility.SetDirty(settings);
            return settings;
        }

        private static string BuildRootSettingsPath(string configPath)
        {
            var directory = Path.GetDirectoryName(configPath);
            var fileName = Path.GetFileNameWithoutExtension(configPath);
            const string configSuffix = ".PrefabAutoBind";
            if (fileName.EndsWith(configSuffix, StringComparison.Ordinal))
            {
                fileName = fileName.Substring(0, fileName.Length - configSuffix.Length);
            }

            return CombineProjectPath(directory, fileName + ".PrefabAutoBindSettings.asset");
        }

        private static string BuildNodeSettingsPath(
            string configPath,
            PrefabAutoBindNodeConfig node)
        {
            var rootPath = BuildRootSettingsPath(configPath);
            var directory = Path.GetDirectoryName(rootPath);
            var rootFileName = Path.GetFileNameWithoutExtension(rootPath);
            var viewName = MakeSafeIdentifier(
                string.IsNullOrWhiteSpace(node.ViewClassName) ? node.NodeName : node.ViewClassName,
                "View");
            return CombineProjectPath(
                directory,
                rootFileName + "." + viewName + ".asset");
        }

        private static string GetLegacyEffectiveLogicOutputDirectory(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig node)
        {
            var inherited = GetLegacyInheritedLogicOutputDirectory(config, node);
            return node == null || node.LegacyInheritLogicOutputDirectory
                ? inherited
                : NormalizeDirectory(node.LegacyLogicOutputDirectory, inherited);
        }

        private static string GetLegacyInheritedLogicOutputDirectory(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig node)
        {
            var ancestor = FindNearestAncestorViewConfigByPath(config, node != null ? node.NodePath : null);
            return ancestor != null
                ? GetLegacyEffectiveLogicOutputDirectory(config, ancestor)
                : NormalizeDirectory(config.LegacyLogicOutputDirectory, "Assets/Scripts/Logic");
        }

        private static string GetLegacyEffectivePartialOutputDirectory(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig node)
        {
            var inherited = GetLegacyInheritedPartialOutputDirectory(config, node);
            return node == null || node.LegacyInheritPartialOutputDirectory
                ? inherited
                : NormalizeDirectory(node.LegacyPartialOutputDirectory, inherited);
        }

        private static string GetLegacyInheritedPartialOutputDirectory(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig node)
        {
            var ancestor = FindNearestAncestorViewConfigByPath(config, node != null ? node.NodePath : null);
            return ancestor != null
                ? GetLegacyEffectivePartialOutputDirectory(config, ancestor)
                : NormalizeDirectory(config.LegacyPartialOutputDirectory, "Assets/Scripts/Logic/Generated");
        }

        private static PrefabAutoBindExportMode GetLegacyEffectiveExportMode(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig node)
        {
            if (node == null || !node.LegacyInheritExportMode)
            {
                return node != null ? node.LegacyExportMode : config.LegacyExportMode;
            }

            var ancestor = FindNearestAncestorViewConfigByPath(config, node.NodePath);
            return ancestor != null
                ? GetLegacyEffectiveExportMode(config, ancestor)
                : config.LegacyExportMode;
        }

        public static string GetConfigAssetPath(string prefabPath)
        {
            var fileName = Path.GetFileNameWithoutExtension(prefabPath);
            var readablePath = CombineProjectPath(
                GetConfigDirectoryPath(),
                fileName + ConfigAssetSuffix);
            var existingConfig = AssetDatabase.LoadAssetAtPath<PrefabAutoBindConfig>(readablePath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (AssetDatabase.LoadMainAssetAtPath(readablePath) == null || existingConfig?.Prefab == prefab)
            {
                return readablePath;
            }

            var prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
            return CombineProjectPath(
                GetConfigDirectoryPath(),
                fileName + "." + prefabGuid + ConfigAssetSuffix);
        }

        internal static string GetConfigDirectoryPath()
        {
            var scriptGuids = AssetDatabase.FindAssets(nameof(PrefabAutoBindEditorUtility) + " t:MonoScript");
            for (var index = 0; index < scriptGuids.Length; index++)
            {
                var scriptPath = AssetDatabase.GUIDToAssetPath(scriptGuids[index]);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                if (script == null || script.GetClass() != typeof(PrefabAutoBindEditorUtility))
                {
                    continue;
                }

                return BuildConfigDirectoryPath(scriptPath);
            }

            throw new InvalidOperationException(
                "Unable to locate PrefabAutoBindEditorUtility.cs and resolve the Config folder path.");
        }

        internal static string BuildConfigDirectoryPath(string utilityScriptPath)
        {
            var editorDirectory = Path.GetDirectoryName(utilityScriptPath);
            var rootDirectory = string.IsNullOrWhiteSpace(editorDirectory)
                ? null
                : Path.GetDirectoryName(editorDirectory);
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new InvalidOperationException(
                    "Unable to resolve the PrefabAutoBind root folder from '" + utilityScriptPath + "'.");
            }

            return CombineProjectPath(rootDirectory, ConfigFolderName);
        }

        private static string EnsureConfigDirectory()
        {
            var configDirectory = GetConfigDirectoryPath();
            if (AssetDatabase.IsValidFolder(configDirectory))
            {
                return configDirectory;
            }

            var parentDirectory = Path.GetDirectoryName(configDirectory);
            if (string.IsNullOrWhiteSpace(parentDirectory) || !AssetDatabase.IsValidFolder(parentDirectory))
            {
                throw new InvalidOperationException(
                    "Unable to create PrefabAutoBind Config folder because its parent folder is missing: '" +
                    parentDirectory + "'.");
            }

            var folderGuid = AssetDatabase.CreateFolder(parentDirectory, ConfigFolderName);
            if (string.IsNullOrWhiteSpace(folderGuid))
            {
                throw new InvalidOperationException(
                    "Unable to create PrefabAutoBind Config folder at '" + configDirectory + "'.");
            }

            return configDirectory;
        }

        private static List<PrefabAutoBindConfig> FindConfigsForPrefab(GameObject prefab)
        {
            var matchingConfigs = new List<PrefabAutoBindConfig>();
            var configGuids = AssetDatabase.FindAssets("t:PrefabAutoBindConfig");
            for (var index = 0; index < configGuids.Length; index++)
            {
                var configPath = AssetDatabase.GUIDToAssetPath(configGuids[index]);
                var config = AssetDatabase.LoadAssetAtPath<PrefabAutoBindConfig>(configPath);
                if (config != null && config.Prefab == prefab)
                {
                    matchingConfigs.Add(config);
                }
            }

            return matchingConfigs;
        }

        private static PrefabAutoBindConfig SelectPreferredConfig(
            List<PrefabAutoBindConfig> configs,
            string configDirectory)
        {
            PrefabAutoBindConfig preferred = null;
            for (var index = 0; index < configs.Count; index++)
            {
                var candidate = configs[index];
                if (preferred == null)
                {
                    preferred = candidate;
                    continue;
                }

                var candidatePath = AssetDatabase.GetAssetPath(candidate);
                var preferredPath = AssetDatabase.GetAssetPath(preferred);
                var candidateIsCentral = IsAssetPathInDirectory(candidatePath, configDirectory);
                var preferredIsCentral = IsAssetPathInDirectory(preferredPath, configDirectory);
                if (candidateIsCentral != preferredIsCentral)
                {
                    if (candidateIsCentral)
                    {
                        preferred = candidate;
                    }

                    continue;
                }

                if (string.CompareOrdinal(candidatePath, preferredPath) < 0)
                {
                    preferred = candidate;
                }
            }

            return preferred;
        }

        private static bool IsAssetPathInDirectory(string assetPath, string directoryPath)
        {
            return !string.IsNullOrWhiteSpace(assetPath) &&
                   assetPath.StartsWith(directoryPath + "/", StringComparison.Ordinal);
        }

        public static List<PrefabAutoBindNodeSnapshot> BuildNodeSnapshots(GameObject prefab)
        {
            var prefabPath = AssetDatabase.GetAssetPath(prefab);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var transforms = new List<Transform>();
            var hierarchyIndices = new List<int[]>();
            CollectTransforms(root.transform, Array.Empty<int>(), transforms, hierarchyIndices);

            var objects = new UnityEngine.Object[transforms.Count];
            for (var index = 0; index < transforms.Count; index++)
            {
                objects[index] = transforms[index].gameObject;
            }

            var globalObjectIds = new GlobalObjectId[objects.Length];
            GlobalObjectId.GetGlobalObjectIdsSlow(objects, globalObjectIds);

            var nodeIds = new Dictionary<Transform, PrefabAutoBindNodeId>();
            var snapshots = new List<PrefabAutoBindNodeSnapshot>(transforms.Count);
            for (var index = 0; index < transforms.Count; index++)
            {
                var current = transforms[index];
                var globalObjectId = globalObjectIds[index];
                var nodeId = new PrefabAutoBindNodeId(
                    unchecked((long)globalObjectId.targetObjectId),
                    unchecked((long)globalObjectId.targetPrefabId));
                var parentNodeId = current == root.transform
                    ? default
                    : nodeIds[current.parent];
                nodeIds.Add(current, nodeId);
                snapshots.Add(new PrefabAutoBindNodeSnapshot(
                    nodeId,
                    parentNodeId,
                    GetRelativePath(root.transform, current),
                    current.name,
                    hierarchyIndices[index],
                    BuildAvailableComponents(current)));
            }

            return snapshots;
        }

        public static void SyncNodes(PrefabAutoBindConfig config, IReadOnlyList<PrefabAutoBindNodeSnapshot> snapshots)
        {
            var existing = new Dictionary<PrefabAutoBindNodeId, PrefabAutoBindNodeConfig>();
            for (var index = 0; index < config.Nodes.Count; index++)
            {
                var node = config.Nodes[index];
                if (node != null && node.NodeId.IsValid)
                {
                    existing[node.NodeId] = node;
                }
            }

            var merged = new List<PrefabAutoBindNodeConfig>(snapshots.Count);
            for (var index = 0; index < snapshots.Count; index++)
            {
                var snapshot = snapshots[index];
                if (!existing.TryGetValue(snapshot.NodeId, out var node))
                {
                    node = new PrefabAutoBindNodeConfig
                    {
                        NodeId = snapshot.NodeId,
                        ParentNodeId = snapshot.ParentNodeId,
                        NodePath = snapshot.Path,
                        NodeName = snapshot.DisplayName,
                    };
                }

                node.NodeId = snapshot.NodeId;
                node.ParentNodeId = snapshot.ParentNodeId;
                node.NodePath = snapshot.Path;
                node.NodeName = snapshot.DisplayName;

                for (var componentIndex = node.Components.Count - 1; componentIndex >= 0; componentIndex--)
                {
                    var component = node.Components[componentIndex];
                    if (!ContainsAvailableComponent(snapshot, component))
                    {
                        node.Components.RemoveAt(componentIndex);
                    }
                }

                merged.Add(node);
            }

            config.Nodes.Clear();
            for (var index = 0; index < merged.Count; index++)
            {
                config.Nodes.Add(merged[index]);
            }
        }

        public static string BuildDefaultClassName(GameObject prefab)
        {
            return MakeSafeIdentifier(prefab != null ? prefab.name : "Prefab", "Prefab");
        }

        public static bool TryResolveMonoBehaviourBaseType(string typeName, out string errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(typeName))
            {
                errorMessage = "Base class name cannot be empty.";
                return false;
            }

            if (string.Equals(typeName.Trim(), "MonoBehaviour", StringComparison.Ordinal))
            {
                return true;
            }

            var resolvedType = ResolveType(typeName);
            if (resolvedType == null)
            {
                errorMessage = "Unable to resolve base class '" + typeName + "'.";
                return false;
            }

            if (!typeof(MonoBehaviour).IsAssignableFrom(resolvedType))
            {
                errorMessage = "Base class '" + resolvedType.FullName + "' must inherit from MonoBehaviour.";
                return false;
            }

            return true;
        }

        public static string BuildDefaultViewClassName(PrefabAutoBindConfig config, PrefabAutoBindNodeConfig node)
        {
            var baseName = MakeSafeIdentifier(node != null ? node.NodeName : "View", "View");
            if (!baseName.EndsWith("View", StringComparison.Ordinal))
            {
                baseName += "View";
            }

            return baseName;
        }

        public static string MakeSafeIdentifier(string value, string fallback)
        {
            var workingValue = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            var builder = new StringBuilder(workingValue.Length);

            for (var index = 0; index < workingValue.Length; index++)
            {
                var character = workingValue[index];
                if (char.IsLetterOrDigit(character) || character == '_')
                {
                    builder.Append(character);
                    continue;
                }

                if (builder.Length == 0 || builder[builder.Length - 1] != '_')
                {
                    builder.Append('_');
                }
            }

            var result = builder.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(result))
            {
                result = fallback;
            }

            if (char.IsDigit(result[0]))
            {
                result = "_" + result;
            }

            if (Keywords.Contains(result))
            {
                result = "_" + result;
            }

            return result;
        }

        public static bool IsValidCSharpIdentifier(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0 ||
                (!char.IsLetter(trimmed[0]) && trimmed[0] != '_'))
            {
                return false;
            }

            for (var index = 1; index < trimmed.Length; index++)
            {
                if (!char.IsLetterOrDigit(trimmed[index]) && trimmed[index] != '_')
                {
                    return false;
                }
            }

            return !Keywords.Contains(trimmed);
        }

        public static string BuildDefaultFieldName(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig node,
            string componentTypeName,
            bool isFirstComponent,
            PrefabAutoBindComponentConfig currentComponent = null)
        {
            var baseName = MakeSafeIdentifier(node != null ? node.NodeName : "node", "node");
            if (!isFirstComponent)
            {
                baseName += "_" + GetShortTypeName(componentTypeName);
            }

            return EnsureUniqueFieldName(config, node, currentComponent, baseName);
        }

        public static string EnsureUniqueFieldName(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig node,
            PrefabAutoBindComponentConfig currentComponent,
            string proposed)
        {
            var candidate = MakeSafeIdentifier(proposed, "node");
            var suffix = 2;
            while (FieldNameExists(config, node, currentComponent, candidate))
            {
                candidate = MakeSafeIdentifier(proposed, "node") + suffix;
                suffix++;
            }

            return candidate;
        }

        public static bool FieldNameExists(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig node,
            PrefabAutoBindComponentConfig currentComponent,
            string candidate)
        {
            if (config == null)
            {
                return false;
            }

            var ownerRootId = GetOwningExportRootId(config, node);

            for (var nodeIndex = 0; nodeIndex < config.Nodes.Count; nodeIndex++)
            {
                var otherNode = config.Nodes[nodeIndex];
                if (otherNode == null || otherNode.NodeKind == PrefabAutoBindNodeKind.View)
                {
                    continue;
                }

                if (GetOwningExportRootId(config, otherNode) != ownerRootId)
                {
                    continue;
                }

                for (var componentIndex = 0; componentIndex < otherNode.Components.Count; componentIndex++)
                {
                    var component = otherNode.Components[componentIndex];
                    if (component == null || component == currentComponent)
                    {
                        continue;
                    }

                    if (string.Equals(component.FieldName, candidate, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static Type ResolveType(string storedTypeName)
        {
            if (string.IsNullOrWhiteSpace(storedTypeName))
            {
                return null;
            }

            var type = Type.GetType(storedTypeName, false);
            if (type != null)
            {
                return type;
            }

            var fullName = storedTypeName;
            var separatorIndex = storedTypeName.IndexOf(',');
            if (separatorIndex >= 0)
            {
                fullName = storedTypeName.Substring(0, separatorIndex).Trim();
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

        public static string GetShortTypeName(string storedTypeName)
        {
            var type = ResolveType(storedTypeName);
            if (type != null)
            {
                return type.Name;
            }

            var name = storedTypeName;
            var separatorIndex = name.IndexOf(',');
            if (separatorIndex >= 0)
            {
                name = name.Substring(0, separatorIndex).Trim();
            }

            var dotIndex = name.LastIndexOf('.');
            if (dotIndex >= 0)
            {
                name = name.Substring(dotIndex + 1);
            }

            return name.Replace('+', '.');
        }

        public static string BuildTagLabel(string storedTypeName)
        {
            var shortName = GetShortTypeName(storedTypeName);
            var builder = new StringBuilder(4);
            for (var index = 0; index < shortName.Length; index++)
            {
                var character = shortName[index];
                if (char.IsUpper(character))
                {
                    builder.Append(character);
                }

                if (builder.Length == 4)
                {
                    break;
                }
            }

            if (builder.Length >= 2)
            {
                return builder.ToString();
            }

            if (shortName.Length <= 4)
            {
                return shortName.ToUpperInvariant();
            }

            return shortName.Substring(0, 4).ToUpperInvariant();
        }

        public static string BuildStoredTypeName(Type type)
        {
            return type.FullName + ", " + type.Assembly.GetName().Name;
        }

        public static string ToProjectRelativePath(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                return string.Empty;
            }

            var normalizedAbsolute = absolutePath.Replace('\\', '/');
            var projectRoot = GetProjectRoot().Replace('\\', '/');
            if (!normalizedAbsolute.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var relative = normalizedAbsolute.Substring(projectRoot.Length).TrimStart('/');
            return string.IsNullOrEmpty(relative) ? "Assets" : relative;
        }

        public static string NormalizeDirectory(string input, string fallback)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return fallback;
            }

            var trimmed = input.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(trimmed))
            {
                var projectRelative = ToProjectRelativePath(trimmed);
                return string.IsNullOrEmpty(projectRelative) ? trimmed : projectRelative;
            }

            return trimmed;
        }

        public static string GetProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }

        public static PrefabAutoBindNodeConfig FindNodeConfig(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeId nodeId)
        {
            if (config == null || !nodeId.IsValid)
            {
                return null;
            }

            for (var index = 0; index < config.Nodes.Count; index++)
            {
                var node = config.Nodes[index];
                if (node != null && node.NodeId == nodeId)
                {
                    return node;
                }
            }

            return null;
        }

        public static int ClearDescendantComponentBindings(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig ancestorNode)
        {
            if (config == null || ancestorNode == null || !ancestorNode.NodeId.IsValid)
            {
                return 0;
            }

            return ClearDescendantComponentBindings(config, ancestorNode.NodeId);
        }

        private static int ClearDescendantComponentBindings(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeId parentNodeId)
        {
            var clearedBindingCount = 0;
            for (var index = 0; index < config.Nodes.Count; index++)
            {
                var childNode = config.Nodes[index];
                if (childNode == null || childNode.ParentNodeId != parentNodeId)
                {
                    continue;
                }

                clearedBindingCount += childNode.Components.Count;
                childNode.Components.Clear();
                clearedBindingCount += ClearDescendantComponentBindings(config, childNode.NodeId);
            }

            return clearedBindingCount;
        }

        public static PrefabAutoBindNodeConfig FindRootNodeConfig(PrefabAutoBindConfig config)
        {
            if (config == null)
            {
                return null;
            }

            for (var index = 0; index < config.Nodes.Count; index++)
            {
                var node = config.Nodes[index];
                if (node != null && node.NodeId.IsValid && !node.ParentNodeId.IsValid)
                {
                    return node;
                }
            }

            return null;
        }

        private static PrefabAutoBindNodeConfig FindNodeConfigByPath(
            PrefabAutoBindConfig config,
            string nodePath)
        {
            if (config == null)
            {
                return null;
            }

            for (var index = 0; index < config.Nodes.Count; index++)
            {
                var node = config.Nodes[index];
                if (node != null && string.Equals(node.NodePath, nodePath, StringComparison.Ordinal))
                {
                    return node;
                }
            }

            return null;
        }

        public static PrefabAutoBindNodeId GetOwningExportRootId(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig node)
        {
            if (config == null || node == null)
            {
                return default;
            }

            var ancestorView = FindNearestAncestorViewConfig(config, node);
            if (ancestorView != null)
            {
                return ancestorView.NodeId;
            }

            var rootNode = FindRootNodeConfig(config);
            return rootNode != null ? rootNode.NodeId : default;
        }

        public static string GetParentPath(string nodePath)
        {
            if (string.IsNullOrEmpty(nodePath))
            {
                return null;
            }

            var separatorIndex = nodePath.LastIndexOf('/');
            if (separatorIndex < 0)
            {
                return string.Empty;
            }

            return nodePath.Substring(0, separatorIndex);
        }

        public static bool IsNodeWithinPath(string nodePath, string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath))
            {
                return true;
            }

            if (string.IsNullOrEmpty(nodePath))
            {
                return false;
            }

            if (string.Equals(nodePath, rootPath, StringComparison.Ordinal))
            {
                return true;
            }

            return nodePath.StartsWith(rootPath + "/", StringComparison.Ordinal);
        }

        public static string GetPathRelativeTo(string nodePath, string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath))
            {
                return nodePath ?? string.Empty;
            }

            if (string.Equals(nodePath, rootPath, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            if (!IsNodeWithinPath(nodePath, rootPath))
            {
                return nodePath ?? string.Empty;
            }

            return nodePath.Substring(rootPath.Length + 1);
        }

        public static PrefabAutoBindNodeConfig FindNearestAncestorViewConfig(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig node)
        {
            var parentNodeId = node != null ? node.ParentNodeId : default;
            while (parentNodeId.IsValid)
            {
                var parentNode = FindNodeConfig(config, parentNodeId);
                if (parentNode != null && parentNode.NodeKind == PrefabAutoBindNodeKind.View)
                {
                    return parentNode;
                }

                if (parentNode == null)
                {
                    break;
                }

                parentNodeId = parentNode.ParentNodeId;
            }

            return null;
        }

        private static PrefabAutoBindNodeConfig FindNearestAncestorViewConfigByPath(
            PrefabAutoBindConfig config,
            string nodePath)
        {
            var parentPath = GetParentPath(nodePath);
            while (parentPath != null)
            {
                var parentNode = FindNodeConfigByPath(config, parentPath);
                if (parentNode != null && parentNode.NodeKind == PrefabAutoBindNodeKind.View)
                {
                    return parentNode;
                }

                parentPath = GetParentPath(parentPath);
            }

            return null;
        }

        public static string GetInheritedLogicOutputDirectory(PrefabAutoBindConfig config, PrefabAutoBindNodeConfig node)
        {
            var ancestorView = FindNearestAncestorViewConfig(config, node);
            if (ancestorView != null)
            {
                return GetEffectiveLogicOutputDirectory(config, ancestorView);
            }

            return NormalizeDirectory(
                config != null && config.Settings != null
                    ? config.Settings.LogicOutputDirectory
                    : null,
                "Assets/Scripts/Logic");
        }

        public static string GetEffectiveLogicOutputDirectory(PrefabAutoBindConfig config, PrefabAutoBindNodeConfig node)
        {
            var settings = GetSettingsForNode(config, node);
            return NormalizeDirectory(
                settings != null ? settings.LogicOutputDirectory : null,
                "Assets/Scripts/Logic");
        }

        public static string GetInheritedPartialOutputDirectory(PrefabAutoBindConfig config, PrefabAutoBindNodeConfig node)
        {
            var ancestorView = FindNearestAncestorViewConfig(config, node);
            if (ancestorView != null)
            {
                return GetEffectivePartialOutputDirectory(config, ancestorView);
            }

            return NormalizeDirectory(
                config != null && config.Settings != null
                    ? config.Settings.PartialOutputDirectory
                    : null,
                "Assets/Scripts/Logic/Generated");
        }

        public static string GetEffectivePartialOutputDirectory(PrefabAutoBindConfig config, PrefabAutoBindNodeConfig node)
        {
            var settings = GetSettingsForNode(config, node);
            return NormalizeDirectory(
                settings != null ? settings.PartialOutputDirectory : null,
                "Assets/Scripts/Logic/Generated");
        }

        public static PrefabAutoBindExportMode GetEffectiveExportMode(PrefabAutoBindConfig config, PrefabAutoBindNodeConfig node)
        {
            var settings = GetSettingsForNode(config, node);
            return settings != null
                ? settings.ExportMode
                : PrefabAutoBindExportMode.DirectReference;
        }

        public static PrefabAutoBindSettings GetSettingsForNode(
            PrefabAutoBindConfig config,
            PrefabAutoBindNodeConfig node)
        {
            if (node != null && node.NodeKind == PrefabAutoBindNodeKind.View)
            {
                return node.Settings;
            }

            var ancestor = FindNearestAncestorViewConfig(config, node);
            return ancestor != null
                ? ancestor.Settings
                : config != null
                    ? config.Settings
                    : null;
        }

        public static Component ResolveComponent(Transform root, string nodePath, string storedTypeName, int componentIndex)
        {
            var targetType = ResolveType(storedTypeName);
            if (targetType == null)
            {
                return null;
            }

            var target = string.IsNullOrEmpty(nodePath) ? root : root.Find(nodePath);
            if (target == null)
            {
                return null;
            }

            var components = target.GetComponents(targetType);
            if (componentIndex < 0 || componentIndex >= components.Length)
            {
                return null;
            }

            return components[componentIndex];
        }

        public static string CombineProjectPath(string left, string right)
        {
            if (string.IsNullOrEmpty(left))
            {
                return right.Replace('\\', '/');
            }

            return (left.TrimEnd('/', '\\') + "/" + right.TrimStart('/', '\\')).Replace('\\', '/');
        }

        private static bool ContainsAvailableComponent(PrefabAutoBindNodeSnapshot snapshot, PrefabAutoBindComponentConfig component)
        {
            for (var index = 0; index < snapshot.AvailableComponents.Count; index++)
            {
                var available = snapshot.AvailableComponents[index];
                if (available.ComponentIndex == component.ComponentIndex &&
                    string.Equals(available.TypeName, component.ComponentTypeName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void CollectTransforms(
            Transform current,
            int[] hierarchyIndices,
            List<Transform> transforms,
            List<int[]> collectedHierarchyIndices)
        {
            transforms.Add(current);
            collectedHierarchyIndices.Add(hierarchyIndices);

            for (var childIndex = 0; childIndex < current.childCount; childIndex++)
            {
                var childHierarchyIndices = new int[hierarchyIndices.Length + 1];
                Array.Copy(hierarchyIndices, childHierarchyIndices, hierarchyIndices.Length);
                childHierarchyIndices[childHierarchyIndices.Length - 1] = childIndex;
                CollectTransforms(
                    current.GetChild(childIndex),
                    childHierarchyIndices,
                    transforms,
                    collectedHierarchyIndices);
            }
        }

        private static List<PrefabAutoBindAvailableComponent> BuildAvailableComponents(Transform transform)
        {
            var components = transform.GetComponents<Component>();
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                if (component == null)
                {
                    continue;
                }

                var key = BuildStoredTypeName(component.GetType());
                if (!totals.ContainsKey(key))
                {
                    totals[key] = 0;
                }

                totals[key]++;
            }

            var counters = new Dictionary<string, int>(StringComparer.Ordinal);
            var result = new List<PrefabAutoBindAvailableComponent>(components.Length);
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                if (component == null)
                {
                    continue;
                }

                var typeName = BuildStoredTypeName(component.GetType());
                if (!counters.ContainsKey(typeName))
                {
                    counters[typeName] = 0;
                }

                var componentIndex = counters[typeName];
                counters[typeName]++;

                var displayName = GetShortTypeName(typeName);
                if (totals[typeName] > 1)
                {
                    displayName += " #" + (componentIndex + 1);
                }

                result.Add(new PrefabAutoBindAvailableComponent(typeName, displayName, componentIndex));
            }

            return result;
        }

        private static string GetRelativePath(Transform root, Transform current)
        {
            if (root == current)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            var working = current;
            while (working != null && working != root)
            {
                parts.Add(working.name);
                working = working.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
