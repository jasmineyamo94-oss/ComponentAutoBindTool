using System;
using System.Collections.Generic;
using UnityEngine;

namespace PrefabAutoBind
{
    public enum PrefabAutoBindNodeKind
    {
        Field = 0,
        View = 1,
    }

    public enum PrefabAutoBindExportMode
    {
        DirectReference = 0,
        BindByFind = 1,
    }

    public enum PrefabAutoBindFieldAccessModifier
    {
        Private = 0,
        Public = 1,
        Protected = 2,
        Internal = 3,
    }

    public enum PrefabAutoBindContainerKind
    {
        None = 0,
        Array = 1,
        List = 2,
    }

    public enum PrefabAutoBindComponentEventKind
    {
        Click = 0,
        ValueChanged = 1,
        EndEdit = 2,
        Submit = 3,
    }

    [Serializable]
    public struct PrefabAutoBindNodeId : IEquatable<PrefabAutoBindNodeId>
    {
        [SerializeField] private long targetObjectId;
        [SerializeField] private long targetPrefabId;

        public PrefabAutoBindNodeId(long targetObjectId, long targetPrefabId)
        {
            this.targetObjectId = targetObjectId;
            this.targetPrefabId = targetPrefabId;
        }

        public long TargetObjectId => targetObjectId;

        public long TargetPrefabId => targetPrefabId;

        public bool IsValid => targetObjectId != 0;

        public bool Equals(PrefabAutoBindNodeId other)
        {
            return targetObjectId == other.targetObjectId &&
                   targetPrefabId == other.targetPrefabId;
        }

        public override bool Equals(object obj)
        {
            return obj is PrefabAutoBindNodeId other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (targetObjectId.GetHashCode() * 397) ^ targetPrefabId.GetHashCode();
            }
        }

        public override string ToString()
        {
            return targetObjectId + ":" + targetPrefabId;
        }

        public static bool operator ==(PrefabAutoBindNodeId left, PrefabAutoBindNodeId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(PrefabAutoBindNodeId left, PrefabAutoBindNodeId right)
        {
            return !left.Equals(right);
        }
    }

    [Serializable]
    public sealed class PrefabAutoBindComponentConfig
    {
        [SerializeField] private string componentTypeName;
        [SerializeField] private int componentIndex;
        [SerializeField] private string fieldName;
        [SerializeField] private PrefabAutoBindFieldAccessModifier accessModifier = PrefabAutoBindFieldAccessModifier.Private;
        [SerializeField] private PrefabAutoBindContainerKind containerKind = PrefabAutoBindContainerKind.None;
        [SerializeField] private string containerName;
        [SerializeField] private List<PrefabAutoBindComponentEventKind> events = new List<PrefabAutoBindComponentEventKind>();

        public string ComponentTypeName
        {
            get => componentTypeName;
            set => componentTypeName = value;
        }

        public int ComponentIndex
        {
            get => componentIndex;
            set => componentIndex = value;
        }

        public string FieldName
        {
            get => fieldName;
            set => fieldName = value;
        }

        public PrefabAutoBindFieldAccessModifier AccessModifier
        {
            get => accessModifier;
            set => accessModifier = value;
        }

        public PrefabAutoBindContainerKind ContainerKind
        {
            get => containerKind;
            set => containerKind = value;
        }

        public string ContainerName
        {
            get => containerName;
            set => containerName = value;
        }

        public List<PrefabAutoBindComponentEventKind> Events => events;
    }

    [Serializable]
    public sealed class PrefabAutoBindNodeConfig
    {
        [SerializeField] private PrefabAutoBindNodeId nodeId;
        [SerializeField] private PrefabAutoBindNodeId parentNodeId;
        [SerializeField] private string nodePath;
        [SerializeField] private string nodeName;
        [SerializeField] private PrefabAutoBindNodeKind nodeKind = PrefabAutoBindNodeKind.Field;
        [SerializeField] private string viewClassName;
        [SerializeField] private PrefabAutoBindContainerKind viewContainerKind = PrefabAutoBindContainerKind.None;
        [SerializeField] private string viewContainerName;
        [SerializeField] private PrefabAutoBindSettings settings;
        [SerializeField, HideInInspector] private PrefabAutoBindExportMode exportMode = PrefabAutoBindExportMode.DirectReference;
        [SerializeField] private List<PrefabAutoBindComponentConfig> components = new List<PrefabAutoBindComponentConfig>();

        // Kept serialized for one-time migration of pre-settings assets.
        [SerializeField, HideInInspector] private bool inheritLogicOutputDirectory = true;
        [SerializeField, HideInInspector] private string logicOutputDirectory;
        [SerializeField, HideInInspector] private bool inheritPartialOutputDirectory = true;
        [SerializeField, HideInInspector] private string partialOutputDirectory;
        [SerializeField, HideInInspector] private bool inheritExportMode = true;

        public PrefabAutoBindNodeId NodeId
        {
            get => nodeId;
            internal set => nodeId = value;
        }

        public PrefabAutoBindNodeId ParentNodeId
        {
            get => parentNodeId;
            internal set => parentNodeId = value;
        }

        public string NodePath
        {
            get => nodePath;
            set => nodePath = value;
        }

        public string NodeName
        {
            get => nodeName;
            set => nodeName = value;
        }

        public PrefabAutoBindNodeKind NodeKind
        {
            get => nodeKind;
            set => nodeKind = value;
        }

        public string ViewClassName
        {
            get => viewClassName;
            set => viewClassName = value;
        }

        public PrefabAutoBindContainerKind ViewContainerKind
        {
            get => viewContainerKind;
            set => viewContainerKind = value;
        }

        public string ViewContainerName
        {
            get => viewContainerName;
            set => viewContainerName = value;
        }

        public PrefabAutoBindSettings Settings
        {
            get => settings;
            set => settings = value;
        }

        [Obsolete("View nodes now use an explicit PrefabAutoBindSettings reference.")]
        public bool InheritLogicOutputDirectory
        {
            get => inheritLogicOutputDirectory;
            set => inheritLogicOutputDirectory = value;
        }

        [Obsolete("View nodes now read LogicOutputDirectory from PrefabAutoBindSettings.")]
        public string LogicOutputDirectory
        {
            get => logicOutputDirectory;
            set => logicOutputDirectory = value;
        }

        [Obsolete("View nodes now use an explicit PrefabAutoBindSettings reference.")]
        public bool InheritPartialOutputDirectory
        {
            get => inheritPartialOutputDirectory;
            set => inheritPartialOutputDirectory = value;
        }

        [Obsolete("View nodes now read PartialOutputDirectory from PrefabAutoBindSettings.")]
        public string PartialOutputDirectory
        {
            get => partialOutputDirectory;
            set => partialOutputDirectory = value;
        }

        [Obsolete("Export Mode is now explicit for every view node.")]
        public bool InheritExportMode
        {
            get => inheritExportMode;
            set => inheritExportMode = value;
        }

        [Obsolete("Use Settings.ExportMode.")]
        public PrefabAutoBindExportMode ExportMode
        {
            get => settings != null ? settings.ExportMode : exportMode;
            set
            {
                exportMode = value;
                if (settings != null)
                {
                    settings.ExportMode = value;
                }
            }
        }

        public List<PrefabAutoBindComponentConfig> Components => components;

#if UNITY_EDITOR
        public bool LegacyInheritLogicOutputDirectory => inheritLogicOutputDirectory;
        public string LegacyLogicOutputDirectory => logicOutputDirectory;
        public bool LegacyInheritPartialOutputDirectory => inheritPartialOutputDirectory;
        public string LegacyPartialOutputDirectory => partialOutputDirectory;
        public bool LegacyInheritExportMode => inheritExportMode;
        public PrefabAutoBindExportMode LegacyExportMode
        {
            get => exportMode;
            set => exportMode = value;
        }
#endif
    }

    public sealed class PrefabAutoBindConfig : ScriptableObject
    {
        public const int CurrentSchemaVersion = 3;

        [Obsolete("Use PrefabAutoBindSettings.DefaultBindTemplate.")]
        public const string DefaultBindTemplate = PrefabAutoBindSettings.DefaultBindTemplate;

        [SerializeField] private int schemaVersion;
        [SerializeField] private GameObject prefab;
        [SerializeField] private string className = "NewPrefab";
        [SerializeField] private PrefabAutoBindSettings settings;
        [SerializeField, HideInInspector] private PrefabAutoBindExportMode exportMode = PrefabAutoBindExportMode.DirectReference;
        [SerializeField] private List<PrefabAutoBindNodeConfig> nodes = new List<PrefabAutoBindNodeConfig>();

        // Kept serialized for one-time migration of pre-settings assets.
        [SerializeField, HideInInspector] private string generatedNamespace = string.Empty;
        [SerializeField, HideInInspector] private string parentClassName;
        [SerializeField, HideInInspector] private string eventTemplate = PrefabAutoBindSettings.DefaultAddEventStatementTemplate;
        [SerializeField, HideInInspector] private string removeEventTemplate = PrefabAutoBindSettings.DefaultRemoveEventStatementTemplate;
        [SerializeField, HideInInspector] private string eventNameRule = PrefabAutoBindSettings.DefaultEventHandlerNameTemplate;
        [SerializeField, HideInInspector] private string bindTemplate = PrefabAutoBindSettings.DefaultBindTemplate;
        [SerializeField, HideInInspector] private string rootTransform = PrefabAutoBindSettings.DefaultRootObjectName;
        [SerializeField, HideInInspector] private string findTemplate = PrefabAutoBindSettings.DefaultFindStatementTemplate;
        [SerializeField, HideInInspector] private string logicOutputDirectory = "Assets/Scripts/Logic";
        [SerializeField, HideInInspector] private string partialOutputDirectory = "Assets/Scripts/Logic/Generated";

        public int SchemaVersion
        {
            get => schemaVersion;
            set => schemaVersion = value;
        }

        public GameObject Prefab
        {
            get => prefab;
            set => prefab = value;
        }

        public string ClassName
        {
            get => className;
            set => className = value;
        }

        public PrefabAutoBindSettings Settings
        {
            get => settings;
            set => settings = value;
        }

        [Obsolete("Use Settings.GeneratedNamespace.")]
        public string GeneratedNamespace
        {
            get => generatedNamespace;
            set => generatedNamespace = value;
        }

        [Obsolete("Use Settings.ParentClassName.")]
        public string ParentClassName
        {
            get => parentClassName;
            set => parentClassName = value;
        }

        [Obsolete("Use Settings.BindTemplate.")]
        public string BindTemplate
        {
            get => bindTemplate;
            set => bindTemplate = value;
        }

        [Obsolete("Use Settings.LogicOutputDirectory.")]
        public string LogicOutputDirectory
        {
            get => logicOutputDirectory;
            set => logicOutputDirectory = value;
        }

        [Obsolete("Use Settings.PartialOutputDirectory.")]
        public string PartialOutputDirectory
        {
            get => partialOutputDirectory;
            set => partialOutputDirectory = value;
        }

        [Obsolete("Use Settings.ExportMode.")]
        public PrefabAutoBindExportMode ExportMode
        {
            get => settings != null ? settings.ExportMode : exportMode;
            set
            {
                exportMode = value;
                if (settings != null)
                {
                    settings.ExportMode = value;
                }
            }
        }

        public List<PrefabAutoBindNodeConfig> Nodes => nodes;

#if UNITY_EDITOR
        public string LegacyGeneratedNamespace => generatedNamespace;
        public string LegacyParentClassName => parentClassName;
        public string LegacyEventTemplate => eventTemplate;
        public string LegacyRemoveEventTemplate => removeEventTemplate;
        public string LegacyEventNameRule => eventNameRule;
        public string LegacyBindTemplate => bindTemplate;
        public string LegacyRootTransform => rootTransform;
        public string LegacyFindTemplate => findTemplate;
        public string LegacyLogicOutputDirectory => logicOutputDirectory;
        public string LegacyPartialOutputDirectory => partialOutputDirectory;
        public PrefabAutoBindExportMode LegacyExportMode
        {
            get => exportMode;
            set => exportMode = value;
        }
#endif
    }
}
