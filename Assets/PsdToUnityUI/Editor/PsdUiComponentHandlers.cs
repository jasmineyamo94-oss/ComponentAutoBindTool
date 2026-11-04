using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using PsdTools.Layers;

namespace PsdTools
{
    /// <summary>
    /// UI component export and editor extensions: one handler per type; register new ones (e.g. ScrollView / Slider) here.
    /// </summary>
    public static class UiComponentHandlerRegistry
    {
        private static readonly Dictionary<string, IUiComponentHandler> HandlersByType =
            new Dictionary<string, IUiComponentHandler>(StringComparer.Ordinal);

        /// <summary>Valid values for the right-hand "Type" dropdown and JSON uiComponentType (including None).</summary>
        public static readonly string[] AllComponentTypes =
        {
            "None", "Button", "ScrollBar", "Slider", "ScrollRect", "Toggle",
            "InputField(Legacy)", "InputField(TMP)",
            "Dropdown(Legacy)", "Dropdown(TMP)"
        };

        /// <summary>ScrollBar / Slider direction strings, matching Unity <see cref="Scrollbar.Direction"/> / <see cref="Slider.Direction"/>.</summary>
        public static readonly string[] ScrollBarDirectionOptions =
        {
            "left_to_right",
            "right_to_left",
            "bottom_to_top",
            "top_to_bottom"
        };

        static UiComponentHandlerRegistry()
        {
            Register(new ButtonUiComponentHandler());
            Register(new ScrollBarUiComponentHandler());
            Register(new SliderUiComponentHandler());
            Register(new ScrollRectUiComponentHandler());
            Register(new ToggleUiComponentHandler());
            Register(new InputFieldLegacyUiComponentHandler());
            Register(new InputFieldTmpUiComponentHandler());
            Register(new DropdownLegacyUiComponentHandler());
            Register(new DropdownTmpUiComponentHandler());
        }

        private static void Register(IUiComponentHandler handler)
        {
            HandlersByType[handler.ComponentType] = handler;
        }

        /// <summary>None returns null (no component to attach).</summary>
        public static IUiComponentHandler Get(string componentType)
        {
            if (string.IsNullOrEmpty(componentType) || componentType == "None")
                return null;
            return HandlersByType.TryGetValue(componentType, out var h) ? h : null;
        }
    }

    /// <summary>Export logic and optional editor sub-panel for one UI component type.</summary>
    public interface IUiComponentHandler
    {
        string ComponentType { get; }

        /// <summary>Attach components on the generated GameObject (called when exporting prefab).</summary>
        void Apply(Layer layer, GameObject go, LayerConfigEntry entry);

        /// <summary>Type-specific fields (direction, child names, etc.); leave empty if not needed.</summary>
        void DrawEditorUI(Layer layer, LayerConfigEntry entry, Action onChanged);
    }

    /// <summary>RectTransform helpers: Scrollbar handle, etc.</summary>
    public static class PsdUiRectTransformUtil
    {
        public static void SetStretchFill(RectTransform rt)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Slider drives the fill rect's anchor only on the main axis. After full stretch, correct with offsets: cross-axis still uses PSD insets;
        /// on the main axis the fill should have equal margin to the parent on both sides along the slide direction, taken from the PSD "start side" inset:
        /// LeftToRight uses left, RightToLeft uses right, BottomToTop uses bottom, TopToBottom uses top.
        /// </summary>
        public static void ApplySliderFillInsetsFromPsd(
            RectTransform fillRt,
            Slider.Direction direction,
            float bottomInset,
            float topInset,
            float leftInset,
            float rightInset)
        {
            if (fillRt == null) return;

            float left = leftInset;
            float right = rightInset;
            float bottom = bottomInset;
            float top = topInset;

            switch (direction)
            {
                case Slider.Direction.LeftToRight:
                    left = right = leftInset;
                    break;
                case Slider.Direction.RightToLeft:
                    left = right = rightInset;
                    break;
                case Slider.Direction.BottomToTop:
                    bottom = top = bottomInset;
                    break;
                case Slider.Direction.TopToBottom:
                    bottom = top = topInset;
                    break;
            }

            fillRt.offsetMin = new Vector2(left, bottom);
            fillRt.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>
        /// Same idea as <see cref="ApplySliderFillInsetsFromPsd"/> for Scrollbar.handle: symmetric margins on the main axis;
        /// horizontal matches Slider. Vertical is inverted vs Slider: BottomToTop uses PSD top inset for vertical symmetry,
        /// TopToBottom uses PSD bottom inset (matches Scrollbar movement when value increases).
        /// </summary>
        public static void ApplyScrollBarHandleInsetsFromPsd(
            RectTransform handleRt,
            Scrollbar.Direction direction,
            float bottomInset,
            float topInset,
            float leftInset,
            float rightInset)
        {
            if (handleRt == null) return;

            float left = leftInset;
            float right = rightInset;
            float bottom = bottomInset;
            float top = topInset;

            switch (direction)
            {
                case Scrollbar.Direction.LeftToRight:
                    left = right = leftInset;
                    break;
                case Scrollbar.Direction.RightToLeft:
                    left = right = rightInset;
                    break;
                case Scrollbar.Direction.BottomToTop:
                    bottom = top = topInset;
                    break;
                case Scrollbar.Direction.TopToBottom:
                    bottom = top = bottomInset;
                    break;
            }

            handleRt.offsetMin = new Vector2(left, bottom);
            handleRt.offsetMax = new Vector2(-right, -top);
        }

        public static RectTransform FindDescendantRectTransformByName(Transform root, string childName)
        {
            if (root == null || string.IsNullOrEmpty(childName)) return null;
            var rects = root.GetComponentsInChildren<RectTransform>(true);
            foreach (var rt in rects)
            {
                if (rt.gameObject != root.gameObject && rt.name == childName)
                    return rt;
            }
            return null;
        }

        /// <summary>Copy RectTransform layout from <paramref name="src"/> to <paramref name="dst"/> (e.g. sibling alignment under same parent).</summary>
        public static void CopyRectTransformLayout(RectTransform src, RectTransform dst)
        {
            if (src == null || dst == null) return;
            dst.localScale = src.localScale;
            dst.localRotation = src.localRotation;
            dst.anchorMin = src.anchorMin;
            dst.anchorMax = src.anchorMax;
            dst.pivot = src.pivot;
            dst.anchoredPosition = src.anchoredPosition;
            dst.sizeDelta = src.sizeDelta;
            dst.offsetMin = src.offsetMin;
            dst.offsetMax = src.offsetMax;
        }

        /// <summary>Find a direct child RectTransform by name under <paramref name="parent"/>.</summary>
        public static RectTransform FindDirectChildRectTransformByName(Transform parent, string childName)
        {
            if (parent == null || string.IsNullOrEmpty(childName)) return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (c.name == childName)
                    return c as RectTransform;
            }
            return null;
        }

        /// <summary>ScrollRect content: same size as viewport; vertical = top-aligned, horizontal = left-aligned, both = top-left.</summary>
        public static void SetupScrollRectContentLayout(RectTransform contentRt, RectTransform viewportRt, bool horizontal, bool vertical)
        {
            if (contentRt == null || viewportRt == null) return;
            float w = Mathf.Max(0f, viewportRt.rect.width);
            float h = Mathf.Max(0f, viewportRt.rect.height);

            if (vertical && !horizontal)
            {
                contentRt.anchorMin = new Vector2(0f, 1f);
                contentRt.anchorMax = new Vector2(1f, 1f);
                contentRt.pivot = new Vector2(0.5f, 1f);
                contentRt.sizeDelta = new Vector2(0f, h);
                contentRt.anchoredPosition = Vector2.zero;
            }
            else if (horizontal && !vertical)
            {
                contentRt.anchorMin = Vector2.zero;
                contentRt.anchorMax = new Vector2(0f, 1f);
                contentRt.pivot = new Vector2(0f, 0.5f);
                contentRt.sizeDelta = new Vector2(w, 0f);
                contentRt.anchoredPosition = Vector2.zero;
            }
            else if (vertical && horizontal)
            {
                contentRt.anchorMin = new Vector2(0f, 1f);
                contentRt.anchorMax = new Vector2(0f, 1f);
                contentRt.pivot = new Vector2(0f, 1f);
                contentRt.sizeDelta = new Vector2(w, h);
                contentRt.anchoredPosition = Vector2.zero;
            }
            else
            {
                contentRt.anchorMin = new Vector2(0f, 1f);
                contentRt.anchorMax = new Vector2(1f, 1f);
                contentRt.pivot = new Vector2(0.5f, 1f);
                contentRt.sizeDelta = new Vector2(0f, h);
                contentRt.anchoredPosition = Vector2.zero;
            }
        }

        /// <summary>Find a <see cref="Scrollbar"/> on a descendant by name (matching RectTransform’s GameObject must have Scrollbar).</summary>
        public static Scrollbar FindDescendantScrollbarByName(Transform root, string childName)
        {
            var rt = FindDescendantRectTransformByName(root, childName);
            if (rt == null) return null;
            return rt.GetComponent<Scrollbar>();
        }

        /// <summary>
        /// Parent for dropdown list items under Template: if Template root has <see cref="ScrollRect"/> with content set, use content; otherwise Template itself.
        /// </summary>
        public static RectTransform GetDropdownTemplateItemMountParent(RectTransform templateRt)
        {
            if (templateRt == null) return null;
            var sr = templateRt.GetComponent<ScrollRect>();
            if (sr != null && sr.content != null)
                return sr.content;
            return templateRt;
        }

        /// <summary>
        /// True if <paramref name="t"/> is under <paramref name="ancestor"/> (includes when they are the same Transform).
        /// </summary>
        public static bool IsTransformUnderOrEqual(Transform t, Transform ancestor)
        {
            if (t == null || ancestor == null) return false;
            if (t == ancestor) return true;
            return t.IsChildOf(ancestor);
        }

        /// <summary>
        /// If itemText’s parent is not under the Template mount point, log an error and keep hierarchy unchanged.
        /// </summary>
        public static void EnsureDropdownItemTextParentUnderTemplate(RectTransform templateRt, RectTransform itemTextRt)
        {
            if (templateRt == null || itemTextRt == null) return;
            Transform itemParent = itemTextRt.parent;
            RectTransform mount = GetDropdownTemplateItemMountParent(templateRt);
            if (itemParent == null || mount == null) return;
            Transform mountT = mount.transform;
            if (IsTransformUnderOrEqual(itemParent, mountT)) return;
            Debug.LogError($"[Dropdown] Item Text parent \"{itemParent.name}\" is not under Template mount point \"{mountT.name}\".");
        }

        /// <summary>
        /// If <paramref name="rt"/> is not under <paramref name="parent"/> (per <see cref="IsTransformUnderOrEqual"/>), log an error and keep hierarchy unchanged.
        /// </summary>
        public static void EnsureRectTransformUnderParent(RectTransform rt, RectTransform parent)
        {
            if (rt == null || parent == null) return;
            if (IsTransformUnderOrEqual(rt, parent)) return;
            Debug.LogError($"[InputField(TMP)] Node \"{rt.name}\" is not under required parent \"{parent.name}\".");
        }
    }

    /// <summary>Sync UI-type dicts with the PSD editor (LayerConfigEntry carries UI-related fields only).</summary>
    public static class PsdUiComponentEditorHelper
    {
        public static LayerConfigEntry BuildUiOnlyEntryFromLayer(
            Layer layer,
            Dictionary<Layer, string> uiTypeByLayer,
            Dictionary<Layer, string> scrollDirByLayer,
            Dictionary<Layer, string> scrollHandleByLayer,
            Dictionary<Layer, string> sliderDirByLayer,
            Dictionary<Layer, string> sliderFillRectByLayer,
            Dictionary<Layer, string> sliderHandleRectByLayer,
            Dictionary<Layer, string> scrollRectScrollBgByLayer,
            Dictionary<Layer, string> scrollRectContentByLayer,
            Dictionary<Layer, bool> scrollRectHorizontalByLayer,
            Dictionary<Layer, bool> scrollRectVerticalByLayer,
            Dictionary<Layer, string> scrollRectHorizontalScrollbarByLayer,
            Dictionary<Layer, string> scrollRectVerticalScrollbarByLayer,
            Dictionary<Layer, string> toggleGraphicByLayer,
            Dictionary<Layer, string> inputFieldTextChildByLayer,
            Dictionary<Layer, string> inputFieldPlaceholderChildByLayer,
            Dictionary<Layer, string> inputFieldTextViewportChildByLayer,
            Dictionary<Layer, string> dropdownTemplateChildByLayer,
            Dictionary<Layer, string> dropdownCaptionTextChildByLayer,
            Dictionary<Layer, string> dropdownItemTextChildByLayer)
        {
            var e = new LayerConfigEntry();
            if (layer.LayerId.HasValue)
                e.id = layer.LayerId.Value;

            e.uiComponentType = uiTypeByLayer.TryGetValue(layer, out string t) && !string.IsNullOrEmpty(t) ? t : "None";
            if (System.Array.IndexOf(UiComponentHandlerRegistry.AllComponentTypes, e.uiComponentType) < 0)
                e.uiComponentType = "None";

            e.scrollBarDirection = scrollDirByLayer.TryGetValue(layer, out string d) && !string.IsNullOrEmpty(d)
                ? d
                : "left_to_right";
            e.scrollBarHandleChildName = scrollHandleByLayer.TryGetValue(layer, out string h) ? h ?? "" : "";
            e.sliderDirection = sliderDirByLayer.TryGetValue(layer, out string sd) && !string.IsNullOrEmpty(sd)
                ? sd
                : "left_to_right";
            e.sliderFillRectChildName = sliderFillRectByLayer.TryGetValue(layer, out string sf) ? sf ?? "" : "";
            e.sliderHandleRectChildName = sliderHandleRectByLayer.TryGetValue(layer, out string sh) ? sh ?? "" : "";
            e.scrollRectViewportChildName = scrollRectScrollBgByLayer.TryGetValue(layer, out string sbg) ? sbg ?? "" : "";
            e.scrollRectContentChildName = scrollRectContentByLayer.TryGetValue(layer, out string sct) ? sct ?? "" : "";
            e.scrollRectHorizontal = scrollRectHorizontalByLayer.TryGetValue(layer, out bool hScr) ? hScr : true;
            e.scrollRectVertical = scrollRectVerticalByLayer.TryGetValue(layer, out bool vScr) ? vScr : true;
            e.scrollRectHorizontalScrollbarChildName = scrollRectHorizontalScrollbarByLayer.TryGetValue(layer, out string hsb) ? hsb ?? "" : "";
            e.scrollRectVerticalScrollbarChildName = scrollRectVerticalScrollbarByLayer.TryGetValue(layer, out string vsb) ? vsb ?? "" : "";
            e.toggleGraphicChildName = toggleGraphicByLayer.TryGetValue(layer, out string g) ? g ?? "" : "";
            e.inputFieldTextChildName = inputFieldTextChildByLayer.TryGetValue(layer, out string ift) ? ift ?? "" : "";
            e.inputFieldPlaceholderChildName = inputFieldPlaceholderChildByLayer.TryGetValue(layer, out string ifp) ? ifp ?? "" : "";
            e.inputFieldTextViewportChildName = inputFieldTextViewportChildByLayer.TryGetValue(layer, out string ifv) ? ifv ?? "" : "";
            e.dropdownTemplateChildName = dropdownTemplateChildByLayer.TryGetValue(layer, out string dtm) ? dtm ?? "" : "";
            e.dropdownCaptionTextChildName = dropdownCaptionTextChildByLayer.TryGetValue(layer, out string dcap) ? dcap ?? "" : "";
            e.dropdownItemTextChildName = dropdownItemTextChildByLayer.TryGetValue(layer, out string ditm) ? ditm ?? "" : "";
            return e;
        }

        /// <summary>Write UI fields back to editor dicts (same as SaveMergeExportConfig).</summary>
        public static void SyncUiOnlyEntryToLayerDicts(
            Layer layer,
            LayerConfigEntry entry,
            Dictionary<Layer, string> uiTypeByLayer,
            Dictionary<Layer, string> scrollDirByLayer,
            Dictionary<Layer, string> scrollHandleByLayer,
            Dictionary<Layer, string> sliderDirByLayer,
            Dictionary<Layer, string> sliderFillRectByLayer,
            Dictionary<Layer, string> sliderHandleRectByLayer,
            Dictionary<Layer, string> scrollRectScrollBgByLayer,
            Dictionary<Layer, string> scrollRectContentByLayer,
            Dictionary<Layer, bool> scrollRectHorizontalByLayer,
            Dictionary<Layer, bool> scrollRectVerticalByLayer,
            Dictionary<Layer, string> scrollRectHorizontalScrollbarByLayer,
            Dictionary<Layer, string> scrollRectVerticalScrollbarByLayer,
            Dictionary<Layer, string> toggleGraphicByLayer,
            Dictionary<Layer, string> inputFieldTextChildByLayer,
            Dictionary<Layer, string> inputFieldPlaceholderChildByLayer,
            Dictionary<Layer, string> inputFieldTextViewportChildByLayer,
            Dictionary<Layer, string> dropdownTemplateChildByLayer,
            Dictionary<Layer, string> dropdownCaptionTextChildByLayer,
            Dictionary<Layer, string> dropdownItemTextChildByLayer)
        {
            string type = string.IsNullOrEmpty(entry.uiComponentType) ? "None" : entry.uiComponentType;
            if (type == "None")
                uiTypeByLayer.Remove(layer);
            else
                uiTypeByLayer[layer] = type;

            if (type != "ScrollBar")
            {
                scrollDirByLayer.Remove(layer);
                scrollHandleByLayer.Remove(layer);
            }
            else
            {
                string dir = string.IsNullOrEmpty(entry.scrollBarDirection) ? "left_to_right" : entry.scrollBarDirection;
                scrollDirByLayer[layer] = dir;
                if (!string.IsNullOrEmpty(entry.scrollBarHandleChildName))
                    scrollHandleByLayer[layer] = entry.scrollBarHandleChildName;
                else
                    scrollHandleByLayer.Remove(layer);
            }

            if (type != "Slider")
            {
                sliderDirByLayer.Remove(layer);
                sliderFillRectByLayer.Remove(layer);
                sliderHandleRectByLayer.Remove(layer);
            }
            else
            {
                string sdir = string.IsNullOrEmpty(entry.sliderDirection) ? "left_to_right" : entry.sliderDirection;
                sliderDirByLayer[layer] = sdir;
                if (!string.IsNullOrEmpty(entry.sliderFillRectChildName))
                    sliderFillRectByLayer[layer] = entry.sliderFillRectChildName;
                else
                    sliderFillRectByLayer.Remove(layer);
                if (!string.IsNullOrEmpty(entry.sliderHandleRectChildName))
                    sliderHandleRectByLayer[layer] = entry.sliderHandleRectChildName;
                else
                    sliderHandleRectByLayer.Remove(layer);
            }

            if (type != "ScrollRect")
            {
                scrollRectScrollBgByLayer.Remove(layer);
                scrollRectContentByLayer.Remove(layer);
                scrollRectHorizontalByLayer.Remove(layer);
                scrollRectVerticalByLayer.Remove(layer);
                scrollRectHorizontalScrollbarByLayer.Remove(layer);
                scrollRectVerticalScrollbarByLayer.Remove(layer);
            }
            else
            {
                if (!string.IsNullOrEmpty(entry.scrollRectViewportChildName))
                    scrollRectScrollBgByLayer[layer] = entry.scrollRectViewportChildName;
                else
                    scrollRectScrollBgByLayer.Remove(layer);

                if (!string.IsNullOrEmpty(entry.scrollRectContentChildName))
                    scrollRectContentByLayer[layer] = entry.scrollRectContentChildName;
                else
                    scrollRectContentByLayer.Remove(layer);
                scrollRectHorizontalByLayer[layer] = entry.scrollRectHorizontal;
                scrollRectVerticalByLayer[layer] = entry.scrollRectVertical;
                if (!string.IsNullOrEmpty(entry.scrollRectHorizontalScrollbarChildName))
                    scrollRectHorizontalScrollbarByLayer[layer] = entry.scrollRectHorizontalScrollbarChildName;
                else
                    scrollRectHorizontalScrollbarByLayer.Remove(layer);
                if (!string.IsNullOrEmpty(entry.scrollRectVerticalScrollbarChildName))
                    scrollRectVerticalScrollbarByLayer[layer] = entry.scrollRectVerticalScrollbarChildName;
                else
                    scrollRectVerticalScrollbarByLayer.Remove(layer);
            }

            if (type != "Toggle")
            {
                toggleGraphicByLayer.Remove(layer);
            }
            else
            {
                if (!string.IsNullOrEmpty(entry.toggleGraphicChildName))
                    toggleGraphicByLayer[layer] = entry.toggleGraphicChildName;
                else
                    toggleGraphicByLayer.Remove(layer);
            }

            if (type != "InputField(Legacy)" && type != "InputField(TMP)")
            {
                inputFieldTextChildByLayer.Remove(layer);
                inputFieldPlaceholderChildByLayer.Remove(layer);
                inputFieldTextViewportChildByLayer.Remove(layer);
            }
            else
            {
                if (!string.IsNullOrEmpty(entry.inputFieldTextChildName))
                    inputFieldTextChildByLayer[layer] = entry.inputFieldTextChildName;
                else
                    inputFieldTextChildByLayer.Remove(layer);
                if (!string.IsNullOrEmpty(entry.inputFieldPlaceholderChildName))
                    inputFieldPlaceholderChildByLayer[layer] = entry.inputFieldPlaceholderChildName;
                else
                    inputFieldPlaceholderChildByLayer.Remove(layer);
                if (type == "InputField(TMP)" && !string.IsNullOrEmpty(entry.inputFieldTextViewportChildName))
                    inputFieldTextViewportChildByLayer[layer] = entry.inputFieldTextViewportChildName;
                else
                    inputFieldTextViewportChildByLayer.Remove(layer);
            }

            if (type != "Dropdown(Legacy)" && type != "Dropdown(TMP)")
            {
                dropdownTemplateChildByLayer.Remove(layer);
                dropdownCaptionTextChildByLayer.Remove(layer);
                dropdownItemTextChildByLayer.Remove(layer);
            }
            else
            {
                if (!string.IsNullOrEmpty(entry.dropdownTemplateChildName))
                    dropdownTemplateChildByLayer[layer] = entry.dropdownTemplateChildName;
                else
                    dropdownTemplateChildByLayer.Remove(layer);
                if (!string.IsNullOrEmpty(entry.dropdownCaptionTextChildName))
                    dropdownCaptionTextChildByLayer[layer] = entry.dropdownCaptionTextChildName;
                else
                    dropdownCaptionTextChildByLayer.Remove(layer);
                if (!string.IsNullOrEmpty(entry.dropdownItemTextChildName))
                    dropdownItemTextChildByLayer[layer] = entry.dropdownItemTextChildName;
                else
                    dropdownItemTextChildByLayer.Remove(layer);
            }
        }
    }

    #region Handlers

    internal sealed class ButtonUiComponentHandler : IUiComponentHandler
    {
        public string ComponentType => "Button";

        public void Apply(Layer layer, GameObject go, LayerConfigEntry entry)
        {
            if (go.GetComponent<Button>() != null)
                return;

            var btn = go.AddComponent<Button>();
            var graphic = go.GetComponent<Graphic>();
            if (graphic == null)
                graphic = go.GetComponentInChildren<Graphic>(true);
            if (graphic != null)
            {
                btn.targetGraphic = graphic;
                graphic.raycastTarget = true;
            }
            else
            {
                var img = go.AddComponent<Image>();
                img.color = new Color(1f, 1f, 1f, 0f);
                img.raycastTarget = true;
                btn.targetGraphic = img;
            }
        }

        public void DrawEditorUI(Layer layer, LayerConfigEntry entry, Action onChanged) { }
    }

    internal sealed class ScrollBarUiComponentHandler : IUiComponentHandler
    {
        public string ComponentType => "ScrollBar";

        public void Apply(Layer layer, GameObject go, LayerConfigEntry entry)
        {
            var sb = go.GetComponent<Scrollbar>();
            if (sb == null)
                sb = go.AddComponent<Scrollbar>();

            sb.size = 1f;

            string handleName = entry.scrollBarHandleChildName ?? "";
            if (string.IsNullOrEmpty(handleName))
            {
                Debug.LogWarning($"[ScrollBar] {layer.Name}: handle child name not set; handleRect not assigned.");
                return;
            }

            RectTransform handleRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, handleName);
            if (handleRt == null)
            {
                Debug.LogWarning($"[ScrollBar] {layer.Name}: no child named \"{handleName}\" found; handleRect not assigned.");
                return;
            }

            sb.direction = ParseScrollbarDirection(entry.scrollBarDirection);
            var parsedDir = sb.direction;
            var handleParent = handleRt.parent as RectTransform;
            float bottomInset = 0f, topInset = 0f, leftInset = 0f, rightInset = 0f;
            if (handleParent != null)
            {
                Rect pr = handleParent.rect;
                Bounds b = RectTransformUtility.CalculateRelativeRectTransformBounds(handleParent, handleRt);
                bottomInset = b.min.y - pr.yMin;
                topInset = pr.yMax - b.max.y;
                leftInset = b.min.x - pr.xMin;
                rightInset = pr.xMax - b.max.x;
            }

            sb.handleRect = handleRt;
            if (handleParent != null)
                PsdUiRectTransformUtil.ApplyScrollBarHandleInsetsFromPsd(
                    handleRt, parsedDir, bottomInset, topInset, leftInset, rightInset);
        }

        public void DrawEditorUI(Layer layer, LayerConfigEntry entry, Action onChanged)
        {
            GUILayout.Space(4);

            string curDir = string.IsNullOrEmpty(entry.scrollBarDirection) ? "left_to_right" : entry.scrollBarDirection;
            if (System.Array.IndexOf(UiComponentHandlerRegistry.ScrollBarDirectionOptions, curDir) < 0)
                curDir = "left_to_right";
            int dirIdx = System.Array.IndexOf(UiComponentHandlerRegistry.ScrollBarDirectionOptions, curDir);
            int newDirIdx = EditorGUILayout.Popup(new GUIContent("ScrollBar direction", "Sets Scrollbar.Direction: left_to_right / right_to_left / bottom_to_top / top_to_bottom."), dirIdx, UiComponentHandlerRegistry.ScrollBarDirectionOptions);
            if (newDirIdx != dirIdx)
            {
                entry.scrollBarDirection = UiComponentHandlerRegistry.ScrollBarDirectionOptions[newDirIdx];
                onChanged?.Invoke();
            }

            string handleName = entry.scrollBarHandleChildName ?? "";
            string newHandleName = EditorGUILayout.TextField(new GUIContent("Handle child name", "After export, assign handleRect by name; Scrollbar only drives handle anchors, offsets from PSD: horizontal = symmetric left/right (LTR uses PSD left, RTL right), vertical insets from PSD; vertical = symmetric top/bottom (BottomToTop uses PSD top, TopToBottom bottom), horizontal insets from PSD. size is set to 1."), handleName);
            if (newHandleName != handleName)
            {
                entry.scrollBarHandleChildName = string.IsNullOrWhiteSpace(newHandleName) ? "" : newHandleName.Trim();
                onChanged?.Invoke();
            }
            // Help text moved to tooltips
        }

        private static Scrollbar.Direction ParseScrollbarDirection(string s)
        {
            switch (s)
            {
                case "right_to_left": return Scrollbar.Direction.RightToLeft;
                case "bottom_to_top": return Scrollbar.Direction.BottomToTop;
                case "top_to_bottom": return Scrollbar.Direction.TopToBottom;
                case "left_to_right":
                default:
                    return Scrollbar.Direction.LeftToRight;
            }
        }
    }

    internal sealed class SliderUiComponentHandler : IUiComponentHandler
    {
        public string ComponentType => "Slider";

        public void Apply(Layer layer, GameObject go, LayerConfigEntry entry)
        {
            var sl = go.GetComponent<Slider>();
            if (sl == null)
                sl = go.AddComponent<Slider>();

            var parsedDir = ParseSliderDirection(entry.sliderDirection);
            sl.direction = parsedDir;

            string fillName = entry.sliderFillRectChildName ?? "";
            if (!string.IsNullOrEmpty(fillName))
            {
                RectTransform fillRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, fillName);
                if (fillRt != null)
                {
                    var fillParent = fillRt.parent as RectTransform;
                    float bottomInset = 0f, topInset = 0f, leftInset = 0f, rightInset = 0f;
                    if (fillParent != null)
                    {
                        Rect pr = fillParent.rect;
                        Bounds b = RectTransformUtility.CalculateRelativeRectTransformBounds(fillParent, fillRt);
                        bottomInset = b.min.y - pr.yMin;
                        topInset = pr.yMax - b.max.y;
                        leftInset = b.min.x - pr.xMin;
                        rightInset = pr.xMax - b.max.x;
                    }

                    sl.fillRect = fillRt;
                    if (fillParent != null)
                        PsdUiRectTransformUtil.ApplySliderFillInsetsFromPsd(
                            fillRt, parsedDir, bottomInset, topInset, leftInset, rightInset);
                }
                else
                {
                    Debug.LogWarning($"[Slider] {layer.Name}: no child named \"{fillName}\" found; fillRect not assigned.");
                }
            }

            string handleName = entry.sliderHandleRectChildName ?? "";
            if (!string.IsNullOrEmpty(handleName))
            {
                RectTransform handleRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, handleName);
                if (handleRt != null)
                {
                    Vector2 originalSizeDelta = handleRt.sizeDelta;
                    sl.handleRect = handleRt;
                    handleRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, originalSizeDelta.x);
                    handleRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, originalSizeDelta.y);
                    Vector2 ap = handleRt.anchoredPosition;
                    switch (sl.direction)
                    {
                        case Slider.Direction.LeftToRight:
                        case Slider.Direction.RightToLeft:
                            handleRt.anchoredPosition = new Vector2(0f, ap.y);
                            break;
                        case Slider.Direction.BottomToTop:
                        case Slider.Direction.TopToBottom:
                            handleRt.anchoredPosition = new Vector2(ap.x, 0f);
                            break;
                    }

                }
                else
                    Debug.LogWarning($"[Slider] {layer.Name}: no child named \"{handleName}\" found; handleRect not assigned.");
            }

            sl.value = 1f;
        }

        public void DrawEditorUI(Layer layer, LayerConfigEntry entry, Action onChanged)
        {
            GUILayout.Space(4);

            string curDir = string.IsNullOrEmpty(entry.sliderDirection) ? "left_to_right" : entry.sliderDirection;
            if (System.Array.IndexOf(UiComponentHandlerRegistry.ScrollBarDirectionOptions, curDir) < 0)
                curDir = "left_to_right";
            int dirIdx = System.Array.IndexOf(UiComponentHandlerRegistry.ScrollBarDirectionOptions, curDir);
            int newDirIdx = EditorGUILayout.Popup(new GUIContent("Slider direction", "Sets Slider.Direction: left_to_right / right_to_left / bottom_to_top / top_to_bottom."), dirIdx, UiComponentHandlerRegistry.ScrollBarDirectionOptions);
            if (newDirIdx != dirIdx)
            {
                entry.sliderDirection = UiComponentHandlerRegistry.ScrollBarDirectionOptions[newDirIdx];
                onChanged?.Invoke();
            }

            string fillName = entry.sliderFillRectChildName ?? "";
            string newFillName = EditorGUILayout.TextField(new GUIContent("Fill Rect child name", "Assign Slider.fillRect by name; fill anchor driven on main axis, cross-axis offset from PSD. Symmetric main-axis margin: LTR = PSD left, RTL = right, BottomToTop = bottom, TopToBottom = top."), fillName);
            if (newFillName != fillName)
            {
                entry.sliderFillRectChildName = string.IsNullOrWhiteSpace(newFillName) ? "" : newFillName.Trim();
                onChanged?.Invoke();
            }

            string handleName = entry.sliderHandleRectChildName ?? "";
            string newHandleName = EditorGUILayout.TextField(new GUIContent("Handle Rect child name", "Assign Slider.handleRect by name; on export restore sizeDelta and zero one axis of anchoredPosition by direction. Slider.value set to 1."), handleName);
            if (newHandleName != handleName)
            {
                entry.sliderHandleRectChildName = string.IsNullOrWhiteSpace(newHandleName) ? "" : newHandleName.Trim();
                onChanged?.Invoke();
            }
            // Help text moved to tooltips
        }

        private static Slider.Direction ParseSliderDirection(string s)
        {
            switch (s)
            {
                case "right_to_left": return Slider.Direction.RightToLeft;
                case "bottom_to_top": return Slider.Direction.BottomToTop;
                case "top_to_bottom": return Slider.Direction.TopToBottom;
                case "left_to_right":
                default:
                    return Slider.Direction.LeftToRight;
            }
        }
    }

    internal sealed class ScrollRectUiComponentHandler : IUiComponentHandler
    {

        public string ComponentType => "ScrollRect";

        public void Apply(Layer layer, GameObject go, LayerConfigEntry entry)
        {
            var sr = go.GetComponent<ScrollRect>();
            if (sr == null)
                sr = go.AddComponent<ScrollRect>();

            sr.horizontal = entry.scrollRectHorizontal;
            sr.vertical = entry.scrollRectVertical;

            string viewportName = entry.scrollRectViewportChildName ?? "";
            if (!string.IsNullOrEmpty(viewportName))
            {
                RectTransform viewportRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, viewportName);
                if (viewportRt == null)
                {
                    Debug.LogWarning($"[ScrollRect] {layer.Name}: no Viewport child named \"{viewportName}\" found.");
                }
                else
                {
                    var vpImage = viewportRt.GetComponent<Image>();
                    if (vpImage == null)
                        vpImage = viewportRt.gameObject.AddComponent<Image>();
                    vpImage.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd");
                    vpImage.type = Image.Type.Sliced;

                    var mask = viewportRt.GetComponent<Mask>();
                    if (mask == null)
                        mask = viewportRt.gameObject.AddComponent<Mask>();
                    mask.showMaskGraphic = false;

                    sr.viewport = viewportRt;

                    string contentName = entry.scrollRectContentChildName ?? "";
                    if (!string.IsNullOrEmpty(contentName))
                    {
                        RectTransform contentRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, contentName);
                        if (contentRt == null)
                        {
                            Debug.LogWarning($"[ScrollRect] {layer.Name}: no Content child named \"{contentName}\" found; content not set.");
                        }
                        else
                        {
                            if (contentRt.parent != viewportRt)
                                Debug.LogError($"[ScrollRect] {layer.Name}: Content \"{contentName}\" is not a direct child of Viewport \"{viewportName}\".");
                            sr.content = contentRt;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[ScrollRect] {layer.Name}: Content child name not set; content not assigned.");
                    }
                }
            }

            string hBarName = entry.scrollRectHorizontalScrollbarChildName ?? "";
            if (!string.IsNullOrEmpty(hBarName))
            {
                Scrollbar hBar = PsdUiRectTransformUtil.FindDescendantScrollbarByName(go.transform, hBarName);
                if (hBar != null)
                {
                    sr.horizontalScrollbar = hBar;
                    sr.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
                }

                else
                {
                    var rt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, hBarName);
                    if (rt != null)
                        Debug.LogWarning($"[ScrollRect] {layer.Name}: node \"{hBarName}\" has no Scrollbar; horizontalScrollbar not set.");
                    else
                        Debug.LogWarning($"[ScrollRect] {layer.Name}: no child named \"{hBarName}\" found; horizontalScrollbar not set.");
                }
            }

            string vBarName = entry.scrollRectVerticalScrollbarChildName ?? "";
            if (!string.IsNullOrEmpty(vBarName))
            {
                Scrollbar vBar = PsdUiRectTransformUtil.FindDescendantScrollbarByName(go.transform, vBarName);
                if (vBar != null)
                {
                    sr.verticalScrollbar = vBar;
                    sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
                }
                else
                {
                    var rt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, vBarName);
                    if (rt != null)
                        Debug.LogWarning($"[ScrollRect] {layer.Name}: node \"{vBarName}\" has no Scrollbar; verticalScrollbar not set.");
                    else
                        Debug.LogWarning($"[ScrollRect] {layer.Name}: no child named \"{vBarName}\" found; verticalScrollbar not set.");
                }
            }
        }

        public void DrawEditorUI(Layer layer, LayerConfigEntry entry, Action onChanged)
        {
            GUILayout.Space(4);

            string bg = entry.scrollRectViewportChildName ?? "";
            string newBg = EditorGUILayout.TextField(new GUIContent("Viewport child name", "On export, this existing child is assigned to ScrollRect.viewport and ensured to have Image + Mask; no Viewport is created."), bg);
            if (newBg != bg)
            {
                entry.scrollRectViewportChildName = string.IsNullOrWhiteSpace(newBg) ? "" : newBg.Trim();
                onChanged?.Invoke();
            }

            string ct = entry.scrollRectContentChildName ?? "";
            string newCt = EditorGUILayout.TextField(new GUIContent("Content child name", "Find existing child by name and assign to ScrollRect.content. If it is not already a direct child of Viewport, only log an error."), ct);
            if (newCt != ct)
            {
                entry.scrollRectContentChildName = string.IsNullOrWhiteSpace(newCt) ? "" : newCt.Trim();
                onChanged?.Invoke();
            }

            bool h = entry.scrollRectHorizontal;
            bool newH = EditorGUILayout.Toggle(new GUIContent("Horizontal", "Allow horizontal scrolling."), h);
            if (newH != h)
            {
                entry.scrollRectHorizontal = newH;
                onChanged?.Invoke();
            }

            bool v = entry.scrollRectVertical;
            bool newV = EditorGUILayout.Toggle(new GUIContent("Vertical", "Allow vertical scrolling."), v);
            if (newV != v)
            {
                entry.scrollRectVertical = newV;
                onChanged?.Invoke();
            }

            string hsb = entry.scrollRectHorizontalScrollbarChildName ?? "";
            string newHsb = EditorGUILayout.TextField(new GUIContent("HorizontalScrollbar child name", "Child must have Scrollbar; assigned to ScrollRect.horizontalScrollbar on export."), hsb);
            if (newHsb != hsb)
            {
                entry.scrollRectHorizontalScrollbarChildName = string.IsNullOrWhiteSpace(newHsb) ? "" : newHsb.Trim();
                onChanged?.Invoke();
            }

            string vsb = entry.scrollRectVerticalScrollbarChildName ?? "";
            string newVsb = EditorGUILayout.TextField(new GUIContent("VerticalScrollbar child name", "Child must have Scrollbar; assigned to ScrollRect.verticalScrollbar on export."), vsb);
            if (newVsb != vsb)
            {
                entry.scrollRectVerticalScrollbarChildName = string.IsNullOrWhiteSpace(newVsb) ? "" : newVsb.Trim();
                onChanged?.Invoke();
            }
            // Help text moved to tooltips
        }
    }

    internal sealed class ToggleUiComponentHandler : IUiComponentHandler
    {
        public string ComponentType => "Toggle";

        public void Apply(Layer layer, GameObject go, LayerConfigEntry entry)
        {
            var toggle = go.GetComponent<Toggle>();
            if (toggle == null)
                toggle = go.AddComponent<Toggle>();

            string targetName = entry.toggleGraphicChildName ?? "";
            if (string.IsNullOrEmpty(targetName))
            {
                Debug.LogWarning($"[Toggle] {layer.Name}: Graphic child name not set; Toggle.graphic not assigned.");
                return;
            }

            RectTransform targetRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, targetName);
            if (targetRt == null)
            {
                Debug.LogWarning($"[Toggle] {layer.Name}: no child named \"{targetName}\" found; Toggle.graphic not assigned.");
                return;
            }

            var graphic = targetRt.GetComponent<Graphic>();
            if (graphic != null)
            {
                toggle.graphic = graphic;
                graphic.raycastTarget = true;
            }
        }

        public void DrawEditorUI(Layer layer, LayerConfigEntry entry, Action onChanged)
        {
            GUILayout.Space(4);
            string curName = entry.toggleGraphicChildName ?? "";
            string newName = EditorGUILayout.TextField(new GUIContent("Graphic child name", "After export, find Graphic by name under children, assign Toggle.graphic and enable raycast."), curName);
            if (newName != curName)
            {
                entry.toggleGraphicChildName = string.IsNullOrWhiteSpace(newName) ? "" : newName.Trim();
                onChanged?.Invoke();
            }
            // Help text moved to tooltips
        }
    }

    internal sealed class InputFieldLegacyUiComponentHandler : IUiComponentHandler
    {
        public string ComponentType => "InputField(Legacy)";

        public void Apply(Layer layer, GameObject go, LayerConfigEntry entry)
        {
            var input = go.GetComponent<InputField>();
            if (input == null)
                input = go.AddComponent<InputField>();
            input.transition = Selectable.Transition.None;

            string textName = entry.inputFieldTextChildName ?? "";
            if (string.IsNullOrEmpty(textName))
                Debug.LogWarning($"[InputField(Legacy)] {layer.Name}: Text child name not set; textComponent not assigned.");
            else
            {
                RectTransform textRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, textName);
                if (textRt == null)
                    Debug.LogWarning($"[InputField(Legacy)] {layer.Name}: no child named \"{textName}\" found; textComponent not assigned.");
                else
                {
                    var tx = textRt.GetComponent<Text>();
                    if (tx == null)
                    {
                        EditorUtility.DisplayDialog(
                            "Component type mismatch",
                            $"[InputField(Legacy)] {layer.Name}:\nChild \"{textName}\" must have UnityEngine.UI.Text for textComponent; component missing, assignment skipped.\n\nIf this child uses TextMeshPro, please go to the PSD Editor and change this node's type from InputField(Legacy) to InputField(TMP).",
                            "OK");
                    }
                    else
                    {
                        tx.supportRichText = false;
                        input.textComponent = tx;
                    }
                }
            }

            string phName = entry.inputFieldPlaceholderChildName ?? "";
            if (string.IsNullOrEmpty(phName))
                Debug.LogWarning($"[InputField(Legacy)] {layer.Name}: Placeholder child name not set; placeholder not assigned.");
            else
            {
                RectTransform phRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, phName);
                if (phRt == null)
                    Debug.LogWarning($"[InputField(Legacy)] {layer.Name}: no child named \"{phName}\" found; placeholder not assigned.");
                else
                {
                    var ph = phRt.GetComponent<Text>();
                    if (ph == null)
                    {
                        EditorUtility.DisplayDialog(
                            "Component type mismatch",
                            $"[InputField(Legacy)] {layer.Name}:\nChild \"{phName}\" must have UnityEngine.UI.Text for placeholder; component missing, assignment skipped.\n\nIf this child uses TextMeshPro, please go to the PSD Editor and change this node's type from InputField(Legacy) to InputField(TMP).",
                            "OK");
                    }
                    else
                        input.placeholder = ph;
                }
            }
        }

        public void DrawEditorUI(Layer layer, LayerConfigEntry entry, Action onChanged)
        {
            GUILayout.Space(4);
            string t = entry.inputFieldTextChildName ?? "";
            string newT = EditorGUILayout.TextField(new GUIContent("Text Component", "Child must be UnityEngine.UI.Text for InputField.textComponent; supportRichText turned off."), t);
            if (newT != t)
            {
                entry.inputFieldTextChildName = string.IsNullOrWhiteSpace(newT) ? "" : newT.Trim();
                onChanged?.Invoke();
            }

            string p = entry.inputFieldPlaceholderChildName ?? "";
            string newP = EditorGUILayout.TextField(new GUIContent("Placeholder", "Child must be UnityEngine.UI.Text for InputField.placeholder."), p);
            if (newP != p)
            {
                entry.inputFieldPlaceholderChildName = string.IsNullOrWhiteSpace(newP) ? "" : newP.Trim();
                onChanged?.Invoke();
            }
            // Help text moved to tooltips
        }
    }

    internal sealed class InputFieldTmpUiComponentHandler : IUiComponentHandler
    {
        public string ComponentType => "InputField(TMP)";

        public void Apply(Layer layer, GameObject go, LayerConfigEntry entry)
        {
            var input = go.GetComponent<TMP_InputField>();
            if (input == null)
                input = go.AddComponent<TMP_InputField>();
            input.transition = Selectable.Transition.None;

            RectTransform textRtResolved = null;
            string textName = entry.inputFieldTextChildName ?? "";
            if (string.IsNullOrEmpty(textName))
                Debug.LogWarning($"[InputField(TMP)] {layer.Name}: Text Component child name not set; textComponent not assigned.");
            else
            {
                RectTransform textRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, textName);
                if (textRt == null)
                    Debug.LogWarning($"[InputField(TMP)] {layer.Name}: no child named \"{textName}\" found; textComponent not assigned.");
                else
                {
                    var tmp = textRt.GetComponent<TMP_Text>();
                    if (tmp == null)
                    {
                        EditorUtility.DisplayDialog(
                            "Component type mismatch",
                            $"[InputField(TMP)] {layer.Name}:\nChild \"{textName}\" must have TextMeshPro (TMP_Text) for textComponent; component missing, assignment skipped.\n\nIf this child uses UnityEngine.UI.Text, please go to the PSD Editor and change this node's type from InputField(TMP) to InputField(Legacy).",
                            "OK");
                    }
                    else
                    {
                        input.textComponent = tmp;
                        textRtResolved = textRt;
                    }
                }
            }

            RectTransform phRtResolved = null;
            string phName = entry.inputFieldPlaceholderChildName ?? "";
            if (string.IsNullOrEmpty(phName))
                Debug.LogWarning($"[InputField(TMP)] {layer.Name}: Placeholder child name not set; placeholder not assigned.");
            else
            {
                RectTransform phRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, phName);
                if (phRt == null)
                    Debug.LogWarning($"[InputField(TMP)] {layer.Name}: no child named \"{phName}\" found; placeholder not assigned.");
                else
                {
                    var ph = phRt.GetComponent<TMP_Text>();
                    if (ph == null)
                    {
                        EditorUtility.DisplayDialog(
                            "Component type mismatch",
                            $"[InputField(TMP)] {layer.Name}:\nChild \"{phName}\" must have TextMeshPro (TMP_Text) for placeholder; component missing, assignment skipped.\n\nIf this child uses UnityEngine.UI.Text, please go to the PSD Editor and change this node's type from InputField(TMP) to InputField(Legacy).",
                            "OK");
                    }
                    else
                    {
                        input.placeholder = ph;
                        phRtResolved = phRt;
                    }
                }
            }

            string vpName = entry.inputFieldTextViewportChildName ?? "";
            if (string.IsNullOrEmpty(vpName))
                Debug.LogWarning($"[InputField(TMP)] {layer.Name}: Text Viewport child name not set; textViewport not assigned.");
            else
            {
                RectTransform vpRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, vpName);
                if (vpRt == null)
                    Debug.LogWarning($"[InputField(TMP)] {layer.Name}: no child named \"{vpName}\" found; textViewport not assigned.");
                else
                {
                    input.textViewport = vpRt;
                    if (vpRt.GetComponent<RectMask2D>() == null)
                        vpRt.gameObject.AddComponent<RectMask2D>();
                    PsdUiRectTransformUtil.EnsureRectTransformUnderParent(textRtResolved, vpRt);
                    PsdUiRectTransformUtil.EnsureRectTransformUnderParent(phRtResolved, vpRt);
                }
            }
        }

        public void DrawEditorUI(Layer layer, LayerConfigEntry entry, Action onChanged)
        {
            GUILayout.Space(4);
            string t = entry.inputFieldTextChildName ?? "";
            string newT = EditorGUILayout.TextField(new GUIContent("Text Component", "Child must be TextMeshProUGUI for TMP_InputField.textComponent."), t);
            if (newT != t)
            {
                entry.inputFieldTextChildName = string.IsNullOrWhiteSpace(newT) ? "" : newT.Trim();
                onChanged?.Invoke();
            }

            string p = entry.inputFieldPlaceholderChildName ?? "";
            string newP = EditorGUILayout.TextField(new GUIContent("Placeholder", "Child must be TextMeshProUGUI for TMP_InputField.placeholder."), p);
            if (newP != p)
            {
                entry.inputFieldPlaceholderChildName = string.IsNullOrWhiteSpace(newP) ? "" : newP.Trim();
                onChanged?.Invoke();
            }

            string v = entry.inputFieldTextViewportChildName ?? "";
            string newV = EditorGUILayout.TextField(new GUIContent("Text Viewport child name", "Assigns TMP_InputField.textViewport; adds RectMask2D if missing; if Text or Placeholder are outside Viewport, only logs an error."), v);
            if (newV != v)
            {
                entry.inputFieldTextViewportChildName = string.IsNullOrWhiteSpace(newV) ? "" : newV.Trim();
                onChanged?.Invoke();
            }
            // Help text moved to tooltips
        }
    }

    internal sealed class DropdownLegacyUiComponentHandler : IUiComponentHandler
    {
        public string ComponentType => "Dropdown(Legacy)";

        public void Apply(Layer layer, GameObject go, LayerConfigEntry entry)
        {
            var dd = go.GetComponent<Dropdown>();
            if (dd == null)
                dd = go.AddComponent<Dropdown>();
            dd.transition = Selectable.Transition.None;

            string templateName = entry.dropdownTemplateChildName ?? "";
            if (string.IsNullOrEmpty(templateName))
                Debug.LogWarning($"[Dropdown(Legacy)] {layer.Name}: Template child name not set; template not assigned.");
            else
            {
                RectTransform templateRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, templateName);
                if (templateRt == null)
                    Debug.LogWarning($"[Dropdown(Legacy)] {layer.Name}: no child named \"{templateName}\" found; template not assigned.");
                else
                    dd.template = templateRt;
            }

            string capName = entry.dropdownCaptionTextChildName ?? "";
            if (string.IsNullOrEmpty(capName))
                Debug.LogWarning($"[Dropdown(Legacy)] {layer.Name}: Caption Text child name not set; captionText not assigned.");
            else
            {
                RectTransform capRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, capName);
                if (capRt == null)
                    Debug.LogWarning($"[Dropdown(Legacy)] {layer.Name}: no child named \"{capName}\" found; captionText not assigned.");
                else
                {
                    var cap = capRt.GetComponent<Text>();
                    if (cap == null)
                    {
                        EditorUtility.DisplayDialog(
                            "Component type mismatch",
                            $"[Dropdown(Legacy)] {layer.Name}:\nChild \"{capName}\" must have UnityEngine.UI.Text for captionText; component missing, assignment skipped.\n\nIf this child uses TextMeshPro, please go to the PSD Editor and change this node's type from Dropdown(Legacy) to Dropdown(TMP).",
                            "OK");
                    }
                    else
                        dd.captionText = cap;
                }
            }

            string itemName = entry.dropdownItemTextChildName ?? "";
            if (string.IsNullOrEmpty(itemName))
                Debug.LogWarning($"[Dropdown(Legacy)] {layer.Name}: Item Text child name not set; itemText not assigned.");
            else
            {
                RectTransform itemRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, itemName);
                if (itemRt == null)
                    Debug.LogWarning($"[Dropdown(Legacy)] {layer.Name}: no child named \"{itemName}\" found; itemText not assigned.");
                else
                {
                    var item = itemRt.GetComponent<Text>();
                    if (item == null)
                    {
                        EditorUtility.DisplayDialog(
                            "Component type mismatch",
                            $"[Dropdown(Legacy)] {layer.Name}:\nChild \"{itemName}\" must have UnityEngine.UI.Text for itemText; component missing, assignment skipped.\n\nIf this child uses TextMeshPro, please go to the PSD Editor and change this node's type from Dropdown(Legacy) to Dropdown(TMP).",
                            "OK");
                    }
                    else
                    {
                        RectTransform templateRt = dd.template;
                        if (templateRt != null)
                            PsdUiRectTransformUtil.EnsureDropdownItemTextParentUnderTemplate(templateRt, itemRt);
                        dd.itemText = item;
                    }
                }
            }

            dd.RefreshShownValue();
        }

        public void DrawEditorUI(Layer layer, LayerConfigEntry entry, Action onChanged)
        {
            GUILayout.Space(4);
            string tpl = entry.dropdownTemplateChildName ?? "";
            string newTpl = EditorGUILayout.TextField(new GUIContent("Template child name", "Assigns Dropdown.template (RectTransform)."), tpl);
            if (newTpl != tpl)
            {
                entry.dropdownTemplateChildName = string.IsNullOrWhiteSpace(newTpl) ? "" : newTpl.Trim();
                onChanged?.Invoke();
            }

            string cap = entry.dropdownCaptionTextChildName ?? "";
            string newCap = EditorGUILayout.TextField(new GUIContent("Caption Text child name", "Child must be UnityEngine.UI.Text for Dropdown.captionText."), cap);
            if (newCap != cap)
            {
                entry.dropdownCaptionTextChildName = string.IsNullOrWhiteSpace(newCap) ? "" : newCap.Trim();
                onChanged?.Invoke();
            }

            string item = entry.dropdownItemTextChildName ?? "";
            string newItem = EditorGUILayout.TextField(new GUIContent("Item Text child name", "Child must be UnityEngine.UI.Text for Dropdown.itemText. If Item Text’s parent is not under Template, only logs an error."), item);
            if (newItem != item)
            {
                entry.dropdownItemTextChildName = string.IsNullOrWhiteSpace(newItem) ? "" : newItem.Trim();
                onChanged?.Invoke();
            }
            // Help text moved to tooltips
        }
    }

    internal sealed class DropdownTmpUiComponentHandler : IUiComponentHandler
    {
        public string ComponentType => "Dropdown(TMP)";

        public void Apply(Layer layer, GameObject go, LayerConfigEntry entry)
        {
            var dd = go.GetComponent<TMP_Dropdown>();
            if (dd == null)
                dd = go.AddComponent<TMP_Dropdown>();
            dd.transition = Selectable.Transition.None;

            string templateName = entry.dropdownTemplateChildName ?? "";
            if (string.IsNullOrEmpty(templateName))
                Debug.LogWarning($"[Dropdown(TMP)] {layer.Name}: Template child name not set; template not assigned.");
            else
            {
                RectTransform templateRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, templateName);
                if (templateRt == null)
                    Debug.LogWarning($"[Dropdown(TMP)] {layer.Name}: no child named \"{templateName}\" found; template not assigned.");
                else
                    dd.template = templateRt;
            }

            string capName = entry.dropdownCaptionTextChildName ?? "";
            if (string.IsNullOrEmpty(capName))
                Debug.LogWarning($"[Dropdown(TMP)] {layer.Name}: Caption Text child name not set; captionText not assigned.");
            else
            {
                RectTransform capRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, capName);
                if (capRt == null)
                    Debug.LogWarning($"[Dropdown(TMP)] {layer.Name}: no child named \"{capName}\" found; captionText not assigned.");
                else
                {
                    var cap = capRt.GetComponent<TextMeshProUGUI>();
                    if (cap == null)
                    {
                        EditorUtility.DisplayDialog(
                            "Component type mismatch",
                            $"[Dropdown(TMP)] {layer.Name}:\nChild \"{capName}\" must have TextMeshProUGUI for captionText; component missing, assignment skipped.\n\nIf this child uses UnityEngine.UI.Text, please go to the PSD Editor and change this node's type from Dropdown(TMP) to Dropdown(Legacy).",
                            "OK");
                    }
                    else
                        dd.captionText = cap;
                }
            }

            string itemName = entry.dropdownItemTextChildName ?? "";
            if (string.IsNullOrEmpty(itemName))
                Debug.LogWarning($"[Dropdown(TMP)] {layer.Name}: Item Text child name not set; itemText not assigned.");
            else
            {
                RectTransform itemRt = PsdUiRectTransformUtil.FindDescendantRectTransformByName(go.transform, itemName);
                if (itemRt == null)
                    Debug.LogWarning($"[Dropdown(TMP)] {layer.Name}: no child named \"{itemName}\" found; itemText not assigned.");
                else
                {
                    var item = itemRt.GetComponent<TextMeshProUGUI>();
                    if (item == null)
                    {
                        EditorUtility.DisplayDialog(
                            "Component type mismatch",
                            $"[Dropdown(TMP)] {layer.Name}:\nChild \"{itemName}\" must have TextMeshProUGUI for itemText; component missing, assignment skipped.\n\nIf this child uses UnityEngine.UI.Text, please go to the PSD Editor and change this node's type from Dropdown(TMP) to Dropdown(Legacy).",
                            "OK");
                    }
                    else
                    {
                        RectTransform templateRt = dd.template;
                        if (templateRt != null)
                            PsdUiRectTransformUtil.EnsureDropdownItemTextParentUnderTemplate(templateRt, itemRt);
                        dd.itemText = item;
                    }
                }
            }

            dd.RefreshShownValue();
        }

        public void DrawEditorUI(Layer layer, LayerConfigEntry entry, Action onChanged)
        {
            GUILayout.Space(4);
            string tpl = entry.dropdownTemplateChildName ?? "";
            string newTpl = EditorGUILayout.TextField(new GUIContent("Template child name", "Assigns TMP_Dropdown.template (RectTransform)."), tpl);
            if (newTpl != tpl)
            {
                entry.dropdownTemplateChildName = string.IsNullOrWhiteSpace(newTpl) ? "" : newTpl.Trim();
                onChanged?.Invoke();
            }

            string cap = entry.dropdownCaptionTextChildName ?? "";
            string newCap = EditorGUILayout.TextField(new GUIContent("Caption Text child name", "Child must be TextMeshProUGUI for TMP_Dropdown.captionText."), cap);
            if (newCap != cap)
            {
                entry.dropdownCaptionTextChildName = string.IsNullOrWhiteSpace(newCap) ? "" : newCap.Trim();
                onChanged?.Invoke();
            }

            string item = entry.dropdownItemTextChildName ?? "";
            string newItem = EditorGUILayout.TextField(new GUIContent("Item Text child name", "Child must be TextMeshProUGUI for TMP_Dropdown.itemText. If Item Text’s parent is not under Template, only logs an error."), item);
            if (newItem != item)
            {
                entry.dropdownItemTextChildName = string.IsNullOrWhiteSpace(newItem) ? "" : newItem.Trim();
                onChanged?.Invoke();
            }
            // Help text moved to tooltips
        }
    }

    #endregion
} // namespace PsdTools
