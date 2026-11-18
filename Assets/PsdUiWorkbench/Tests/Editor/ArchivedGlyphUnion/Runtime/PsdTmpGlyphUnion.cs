using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace PsdUiWorkbench.Runtime
{
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(TextMeshProUGUI))]
    [AddComponentMenu("")]
    public sealed class PsdTmpGlyphUnion : MonoBehaviour
    {
        public const string ShaderName = "TextMeshPro/Mobile/Distance Field - PSD Glyph Union";
        private static readonly int GlyphData = Shader.PropertyToID("_PsdGlyphData");
        private static readonly int GlyphCount = Shader.PropertyToID("_PsdGlyphCount");
        [SerializeField] private Shader _unionShader;
        private TextMeshProUGUI _text;
        private Material _sourceMaterial;
        private Material _material;
        private bool _ownsMaterial;
        private Material _tmpMaterial;
        private Texture2D _layout;
        private readonly Color[] _data = new Color[64];
        private readonly Color[] _uploaded = new Color[64];
        private readonly Rect[] _rectangles = new Rect[32];
        private readonly List<TMP_SubMeshUI> _subMeshes = new List<TMP_SubMeshUI>(2);
        private TextureMappingOptions _horizontalMapping;
        private TextureMappingOptions _verticalMapping;
        private bool _subscribed;
        private bool _hasUpload;
        public int ActiveGlyphCount { get; private set; }
        public int LayoutUploadCount { get; private set; }
        public string Diagnostic { get; private set; }

        // Shader 显式序列化，确保随 Prefab 进入构建，不依赖运行时 Shader.Find。
        public void Configure(Shader shader)
        {
            if (shader == null || shader.name != ShaderName) throw new ArgumentException("需要工作台邻字合成 Shader。", nameof(shader));
            if (isActiveAndEnabled) Release();
            _unionShader = shader;
            if (isActiveAndEnabled) Initialize();
        }

        private void OnEnable() => Initialize();
        private void OnDisable() => Release();
        private void OnDestroy() => Release();

        private void Initialize()
        {
            if (_subscribed) return;
            _text = GetComponent<TextMeshProUGUI>();
            if (_unionShader == null || !_unionShader.isSupported || !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
            {
                Diagnostic = "缺少邻字 Shader 或设备不支持 RGBAFloat 布局纹理，保持原材质。";
                return;
            }
            _horizontalMapping = _text.horizontalMapping;
            _verticalMapping = _text.verticalMapping;
            _text.horizontalMapping = TextureMappingOptions.Paragraph;
            _text.verticalMapping = TextureMappingOptions.Paragraph;
            _layout = new Texture2D(32, 2, TextureFormat.RGBAFloat, false, true)
            {
                name = "TMP Glyph Layout (Instance)", hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
            };
            _text.OnPreRenderText += RefreshLayout;
            _subscribed = true;
            AdoptMaterial();
            _text.SetVerticesDirty();
        }

        private bool AdoptMaterial()
        {
            var current = _text.fontSharedMaterial;
            if (current == _material && _material != null) return true;
            if (current == null || !current.HasProperty("_GradientScale") ||
                (current.shader.name != "TextMeshPro/Mobile/Distance Field - PSD Outer Stroke" && current.shader != _unionShader))
            {
                Diagnostic = "需要 PSD 外描边材质，保持当前材质。";
                ActiveGlyphCount = 0;
                return false;
            }
            // TMP 的 outlineColor 等 setter 会克隆当前实例材质。该副本仍借用本组件的纹理，
            // 不能把它当作禁用后的恢复目标，否则释放布局纹理后会留下悬空引用。
            bool borrowedLayout = current == _tmpMaterial || (current.shader == _unionShader && current.GetTexture(GlyphData) == _layout);
            var old = _material;
            bool ownedOld = _ownsMaterial;
            if (borrowedLayout)
            {
                // TMP 拥有并缓存该副本；直接复用，避免下次 fontMaterial getter 再产生副本。
                _material = _tmpMaterial = current;
                _ownsMaterial = false;
                SetLayout(current, 0);
                if (ownedOld) DestroyOwned(old);
                return true;
            }
            _sourceMaterial = current;
            _material = new Material(current)
            {
                shader = _unionShader, name = "TMP Glyph Union (Instance)", hideFlags = HideFlags.HideAndDontSave
            };
            _material.SetTexture(GlyphData, _layout);
            _material.SetFloat(GlyphCount, 0);
            _ownsMaterial = true;
            _text.fontSharedMaterial = _material;
            if (ownedOld) DestroyOwned(old);
            return true;
        }

        private void RefreshLayout(TMP_TextInfo info)
        {
            if (!isActiveAndEnabled || !AdoptMaterial()) return;
            int count = 0;
            string diagnostic = _material.IsKeywordEnabled("UNDERLAY_ON") || _material.IsKeywordEnabled("UNDERLAY_INNER")
                ? "阴影暂不参与邻字合成，使用单字形描边。"
                : PsdGlyphLayout.Fill(_text, info, _data, _rectangles, out count);
            if (diagnostic != null) count = 0;
            bool changed = !_hasUpload;
            for (int i = 0; i < count; i++)
                changed |= !_data[i].Equals(_uploaded[i]) || !_data[32 + i].Equals(_uploaded[32 + i]);
            if (count > 0 && changed)
            {
                _layout.SetPixels(_data);
                _layout.Apply(false, false);
                Array.Copy(_data, _uploaded, 64);
                _hasUpload = true;
                LayoutUploadCount++;
            }
            ActiveGlyphCount = count;
            Diagnostic = diagnostic;
            SetLayout(_material, count);
            // Mask 的派生材质会缓存属性；同步它的计数，避免仍使用上一帧的字形表。
            var rendering = _text.materialForRendering;
            if (rendering != _material && rendering != null && rendering.HasProperty(GlyphCount)) SetLayout(rendering, count);
            if (count == 0 && info.materialCount > 1)
            {
                // TMP 的回退材质可能复制启用中的邻字表；子图集必须同样清零，不能残留旧邻字。
                _text.GetComponentsInChildren(false, _subMeshes);
                foreach (var sub in _subMeshes)
                {
                    if (sub.textComponent != _text || sub.sharedMaterial == null || sub.sharedMaterial.shader != _unionShader) continue;
                    sub.sharedMaterial.SetFloat(GlyphCount, 0);
                    var subRendering = sub.materialForRendering;
                    if (subRendering != null) subRendering.SetFloat(GlyphCount, 0);
                }
                _subMeshes.Clear();
            }
        }

        private void SetLayout(Material material, int count)
        {
            material.SetTexture(GlyphData, _layout);
            material.SetFloat(GlyphCount, count);
        }

        private void Release()
        {
            if (_subscribed && _text != null)
            {
                _text.OnPreRenderText -= RefreshLayout;
                var current = _text.fontSharedMaterial;
                bool borrowedLayout = current != null && (current == _tmpMaterial || (_layout != null && current.shader == _unionShader && current.GetTexture(GlyphData) == _layout));
                if (current == _material || borrowedLayout) _text.fontSharedMaterial = _sourceMaterial;
                if (borrowedLayout && (!_ownsMaterial || current != _material))
                {
                    // setter 后同一帧禁用时，尚未进入 RefreshLayout，也必须解除借用。
                    current.SetFloat(GlyphCount, 0);
                    current.SetTexture(GlyphData, null);
                    _tmpMaterial = current;
                }
                if (_tmpMaterial != null && _tmpMaterial.GetTexture(GlyphData) == _layout)
                {
                    _tmpMaterial.SetFloat(GlyphCount, 0);
                    _tmpMaterial.SetTexture(GlyphData, null);
                }
                if (_text.horizontalMapping == TextureMappingOptions.Paragraph) _text.horizontalMapping = _horizontalMapping;
                if (_text.verticalMapping == TextureMappingOptions.Paragraph) _text.verticalMapping = _verticalMapping;
            }
            _subscribed = false;
            ActiveGlyphCount = 0;
            _hasUpload = false;
            if (_ownsMaterial) DestroyOwned(_material);
            DestroyOwned(_layout);
            _material = null;
            _ownsMaterial = false;
            _layout = null;
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
