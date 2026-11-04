using System;
using System.Collections.Generic;

namespace PrefabAutoBind
{
    internal sealed class PrefabAutoBindSupportedComponentEvent
    {
        public PrefabAutoBindSupportedComponentEvent(
            PrefabAutoBindComponentEventKind kind,
            string displayName,
            string eventPropertyName,
            string handlerSuffix,
            string listenerParameterTypeName)
        {
            Kind = kind;
            DisplayName = displayName;
            EventPropertyName = eventPropertyName;
            HandlerSuffix = handlerSuffix;
            ListenerParameterTypeName = listenerParameterTypeName;
        }

        public PrefabAutoBindComponentEventKind Kind { get; }

        public string DisplayName { get; }

        public string EventPropertyName { get; }

        public string HandlerSuffix { get; }

        public string ListenerParameterTypeName { get; }

        public bool HasListenerParameter => !string.IsNullOrEmpty(ListenerParameterTypeName);
    }

    internal static class PrefabAutoBindComponentEventCatalog
    {
        private static readonly IReadOnlyList<PrefabAutoBindSupportedComponentEvent> EmptyEvents = Array.Empty<PrefabAutoBindSupportedComponentEvent>();

        private static readonly Dictionary<string, PrefabAutoBindSupportedComponentEvent[]> EventsByComponentType =
            new Dictionary<string, PrefabAutoBindSupportedComponentEvent[]>(StringComparer.Ordinal)
            {
                {
                    "UnityEngine.UI.Button",
                    new[]
                    {
                        new PrefabAutoBindSupportedComponentEvent(
                            PrefabAutoBindComponentEventKind.Click,
                            "Click",
                            "onClick",
                            "Click",
                            string.Empty),
                    }
                },
                {
                    "UnityEngine.UI.Toggle",
                    new[]
                    {
                        new PrefabAutoBindSupportedComponentEvent(
                            PrefabAutoBindComponentEventKind.ValueChanged,
                            "Value Changed",
                            "onValueChanged",
                            "ValueChanged",
                            "bool"),
                    }
                },
                {
                    "UnityEngine.UI.Slider",
                    new[]
                    {
                        new PrefabAutoBindSupportedComponentEvent(
                            PrefabAutoBindComponentEventKind.ValueChanged,
                            "Value Changed",
                            "onValueChanged",
                            "ValueChanged",
                            "float"),
                    }
                },
                {
                    "UnityEngine.UI.ScrollRect",
                    new[]
                    {
                        new PrefabAutoBindSupportedComponentEvent(
                            PrefabAutoBindComponentEventKind.ValueChanged,
                            "Value Changed",
                            "onValueChanged",
                            "ValueChanged",
                            "Vector2"),
                    }
                },
                {
                    "UnityEngine.UI.Dropdown",
                    new[]
                    {
                        new PrefabAutoBindSupportedComponentEvent(
                            PrefabAutoBindComponentEventKind.ValueChanged,
                            "Value Changed",
                            "onValueChanged",
                            "ValueChanged",
                            "int"),
                    }
                },
                {
                    "TMPro.TMP_Dropdown",
                    new[]
                    {
                        new PrefabAutoBindSupportedComponentEvent(
                            PrefabAutoBindComponentEventKind.ValueChanged,
                            "Value Changed",
                            "onValueChanged",
                            "ValueChanged",
                            "int"),
                    }
                },
                {
                    "UnityEngine.UI.InputField",
                    new[]
                    {
                        new PrefabAutoBindSupportedComponentEvent(
                            PrefabAutoBindComponentEventKind.ValueChanged,
                            "Value Changed",
                            "onValueChanged",
                            "ValueChanged",
                            "string"),
                        new PrefabAutoBindSupportedComponentEvent(
                            PrefabAutoBindComponentEventKind.EndEdit,
                            "End Edit",
                            "onEndEdit",
                            "EndEdit",
                            "string"),
                        new PrefabAutoBindSupportedComponentEvent(
                            PrefabAutoBindComponentEventKind.Submit,
                            "Submit",
                            "onSubmit",
                            "Submit",
                            "string"),
                    }
                },
                {
                    "TMPro.TMP_InputField",
                    new[]
                    {
                        new PrefabAutoBindSupportedComponentEvent(
                            PrefabAutoBindComponentEventKind.ValueChanged,
                            "Value Changed",
                            "onValueChanged",
                            "ValueChanged",
                            "string"),
                        new PrefabAutoBindSupportedComponentEvent(
                            PrefabAutoBindComponentEventKind.EndEdit,
                            "End Edit",
                            "onEndEdit",
                            "EndEdit",
                            "string"),
                        new PrefabAutoBindSupportedComponentEvent(
                            PrefabAutoBindComponentEventKind.Submit,
                            "Submit",
                            "onSubmit",
                            "Submit",
                            "string"),
                    }
                },
            };

        public static IReadOnlyList<PrefabAutoBindSupportedComponentEvent> GetSupportedEvents(string storedTypeName)
        {
            var componentType = PrefabAutoBindEditorUtility.ResolveType(storedTypeName);
            if (componentType == null)
            {
                return EmptyEvents;
            }

            return EventsByComponentType.TryGetValue(componentType.FullName, out var supportedEvents)
                ? supportedEvents
                : EmptyEvents;
        }

        public static bool TryGetSupportedEvent(
            string storedTypeName,
            PrefabAutoBindComponentEventKind eventKind,
            out PrefabAutoBindSupportedComponentEvent supportedEvent)
        {
            var supportedEvents = GetSupportedEvents(storedTypeName);
            for (var index = 0; index < supportedEvents.Count; index++)
            {
                if (supportedEvents[index].Kind == eventKind)
                {
                    supportedEvent = supportedEvents[index];
                    return true;
                }
            }

            supportedEvent = null;
            return false;
        }

        public static string BuildHandlerMethodName(string fieldName, PrefabAutoBindSupportedComponentEvent supportedEvent)
        {
            return "On" + fieldName + supportedEvent.HandlerSuffix;
        }

        public static string BuildWrapperMethodName(string fieldName, PrefabAutoBindSupportedComponentEvent supportedEvent)
        {
            return "Handle" + fieldName + supportedEvent.HandlerSuffix;
        }
    }
}
