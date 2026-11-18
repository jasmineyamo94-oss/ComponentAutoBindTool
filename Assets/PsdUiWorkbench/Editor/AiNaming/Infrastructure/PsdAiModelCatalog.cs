using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace PsdUiWorkbench.AiNaming
{
    [Serializable]
    public sealed class PsdAiModel
    {
        [SerializeField] private string id;
        [SerializeField] private string name;
        [SerializeField] private string[] input_modalities;
        [SerializeField] private Effort effort;
        private bool _requiresThinking;
        [Serializable] private sealed class Effort { public string[] supported_levels; }
        public string Id => id;
        public string Name => string.IsNullOrWhiteSpace(name) ? id : name;
        public bool SupportsImages => input_modalities != null && Array.IndexOf(input_modalities, "image") >= 0;
        public bool ImageCapabilityKnown => input_modalities != null && input_modalities.Length > 0;
        public bool RequiresThinking => _requiresThinking;
        public bool SupportsEffort(PsdAiReasoningEffort value) => Enum.IsDefined(typeof(PsdAiReasoningEffort), value) &&
            (!RequiresThinking || value == PsdAiReasoningEffort.High) &&
            (value == PsdAiReasoningEffort.None || effort?.supported_levels == null ||
             Array.IndexOf(effort.supported_levels, PsdAiModelCatalog.EffortValue(value)) >= 0);
        internal static PsdAiModel Fallback() => new PsdAiModel
        {
            id = "deepseek-flash", name = "DeepSeek Flash", input_modalities = new[] { "text", "image" },
            effort = new Effort { supported_levels = new[] { "low", "high", "max" } }
        };
        internal static PsdAiModel ZhipuDefault(string model = "glm-4.6v", bool requiresThinking = false) => new PsdAiModel
        {
            id = model, name = model, input_modalities = new[] { "text", "image" }, _requiresThinking = requiresThinking,
            effort = new Effort { supported_levels = new[] { "high" } }
        };
    }

    public static class PsdAiModelCatalog
    {
        private const string CacheKey = "PsdUiWorkbench.AiNaming.Models.v1";
        [Serializable] private sealed class ModelList { public PsdAiModel[] data; }
        private static IReadOnlyList<PsdAiModel> _current;
        private static readonly IReadOnlyList<PsdAiModel> ZhipuModels = Array.AsReadOnly(new[]
        {
            PsdAiModel.ZhipuDefault(), PsdAiModel.ZhipuDefault("glm-4.6v-flashx"), PsdAiModel.ZhipuDefault("glm-4.6v-flash"),
            PsdAiModel.ZhipuDefault("glm-5.3-flash", true), PsdAiModel.ZhipuDefault("glm-5.3-flashx", true)
        });
        private static UnityWebRequest _request;
        private static string _source = "尚未加载";
        public static event Action Changed;
        public static string Status { get; private set; } = "尚未刷新模型列表。";
        public static bool IsRefreshing => _request != null;
        public static string SourceSummary
        {
            get
            {
                var models = Current;
                return "当前来源：" + _source + "；共 " + models.Count + " 个型号，图片支持 " +
                    models.Count(model => model.ImageCapabilityKnown && model.SupportsImages) + " 个，不支持 " +
                    models.Count(model => model.ImageCapabilityKnown && !model.SupportsImages) + " 个，未知 " +
                    models.Count(model => !model.ImageCapabilityKnown) + " 个。";
            }
        }
        static PsdAiModelCatalog()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Cancel;
            EditorApplication.quitting += Cancel;
        }
        public static IReadOnlyList<PsdAiModel> Current
        {
            get
            {
                if (_current != null) return _current;
                try
                {
                    _current = ParseModels(EditorPrefs.GetString(CacheKey, ""));
                    _source = "本机缓存（尚未向服务刷新）";
                }
                catch (Exception)
                {
                    _current = Array.AsReadOnly(new[] { PsdAiModel.Fallback() });
                    _source = "内置默认型号（非服务返回）";
                }
                return _current;
            }
        }
        public static IReadOnlyList<PsdAiModel> GetModels(PsdAiProvider provider)
        {
            switch (provider)
            {
                case PsdAiProvider.DeepSeek: return Current;
                case PsdAiProvider.Zhipu: return ZhipuModels;
                default: throw new PsdAiNamingException("供应商无效。");
            }
        }
        public static string ProviderName(PsdAiProvider provider)
        {
            switch (provider)
            {
                case PsdAiProvider.DeepSeek: return "DeepSeek";
                case PsdAiProvider.Zhipu: return "智谱";
                default: throw new PsdAiNamingException("供应商无效。");
            }
        }
        public static string ChatCompletionsUrl(PsdAiProvider provider)
        {
            switch (provider)
            {
                case PsdAiProvider.DeepSeek: return "https://api.deepseek.com/chat/completions";
                case PsdAiProvider.Zhipu: return "https://open.bigmodel.cn/api/paas/v4/chat/completions";
                default: throw new PsdAiNamingException("供应商无效。");
            }
        }
        public static string GetStatus(PsdAiProvider provider) => provider == PsdAiProvider.Zhipu
            ? "智谱使用内置视觉型号，不调用模型列表接口；GLM-5.3-Flash/FlashX 仅支持高（启用思考），旧型号仍支持关闭／高。内置视觉型号每次最多 50 张图片，单张须小于 5 MiB；手工型号须自行确认图片和参数兼容性。" : Status;
        public static string GetSourceSummary(PsdAiProvider provider) => provider == PsdAiProvider.Zhipu
            ? "当前来源：内置智谱视觉型号；未联网验证可用性或账户权限。" : SourceSummary;
        public static string Validate(PsdAiProvider provider, string model, PsdAiReasoningEffort effort)
        {
            if (!Enum.IsDefined(typeof(PsdAiProvider), provider)) return "供应商无效。";
            if (provider == PsdAiProvider.DeepSeek) return Validate(model, effort);
            string validation = Validate(ZhipuModels, model, effort);
            if (validation != null) return validation;
            if (effort != PsdAiReasoningEffort.None && effort != PsdAiReasoningEffort.High)
                return "智谱推理仅支持关闭或高（启用），不支持 DeepSeek 推理强度参数。";
            return null;
        }
        public static bool RequiresThinking(PsdAiProvider provider, string model) => provider == PsdAiProvider.Zhipu &&
            ZhipuModels.Any(entry => entry.Id == model && entry.RequiresThinking);
        internal static string EffortValue(PsdAiReasoningEffort effort)
        {
            switch (effort)
            {
                case PsdAiReasoningEffort.None: return "none";
                case PsdAiReasoningEffort.Low: return "low";
                case PsdAiReasoningEffort.High: return "high";
                case PsdAiReasoningEffort.Max: return "max";
                default: throw new PsdAiNamingException("推理强度无效。");
            }
        }
        public static string Validate(string model, PsdAiReasoningEffort effort)
        {
            return Validate(Current, model, effort);
        }
        public static string Validate(IReadOnlyList<PsdAiModel> models, string model, PsdAiReasoningEffort effort)
        {
            if (string.IsNullOrWhiteSpace(model) || model.Length > 128 || model.Any(char.IsControl)) return "模型名称无效。";
            if (!Enum.IsDefined(typeof(PsdAiReasoningEffort), effort)) return "推理强度无效。";
            var entry = models.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.Ordinal));
            if ((entry != null && entry.ImageCapabilityKnown && !entry.SupportsImages) ||
                (model == "deepseek-v4-pro" && (entry == null || !entry.ImageCapabilityKnown))) return "该模型不支持图片输入。";
            if (entry != null && entry.RequiresThinking && effort != PsdAiReasoningEffort.High)
                return "该模型仅支持启用思考，请将推理设为高（启用）；不支持关闭思考或其他推理强度。";
            if (entry != null && !entry.SupportsEffort(effort)) return "该模型不支持所选推理强度。";
            // 手工型号允许能力未知，不伪造能力保证。
            return null;
        }
        private static ModelList Parse(string json)
        {
            var parsed = JsonUtility.FromJson<ModelList>(json);
            if (parsed?.data == null || parsed.data.Length == 0) throw new FormatException();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in parsed.data)
                if (model == null || string.IsNullOrWhiteSpace(model.Id) || model.Id.Length > 128 ||
                    model.Id.Any(char.IsControl) || !seen.Add(model.Id)) throw new FormatException();
            return parsed;
        }
        public static IReadOnlyList<PsdAiModel> ParseModels(string json)
        {
            return Array.AsReadOnly(Parse(json).data);
        }
        public static List<string> BuildModelChoices(IReadOnlyList<PsdAiModel> models, string currentModel)
        {
            if (models == null) throw new ArgumentNullException(nameof(models));
            var choices = models.Select(model => model.Id).Distinct(StringComparer.Ordinal).ToList();
            if (!string.IsNullOrWhiteSpace(currentModel) && !choices.Contains(currentModel))
                choices.Add(currentModel);
            return choices;
        }
        public static string FormatModelChoice(IReadOnlyList<PsdAiModel> models, string id)
        {
            if (string.IsNullOrEmpty(id)) return "请选择型号";
            var model = models.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            string capability = model == null || !model.ImageCapabilityKnown ? "图片能力未知" :
                model.SupportsImages ? "支持图片" : "不支持图片";
            return id + "（" + capability + (model != null && model.RequiresThinking ? "；仅支持启用思考" : "") +
                (model == null ? "；手动型号，未在列表中" : "") + "）";
        }
        public static void Refresh(PsdAiProvider provider, string apiKey, int timeoutSeconds)
        {
            if (provider == PsdAiProvider.Zhipu) { Changed?.Invoke(); return; }
            if (provider != PsdAiProvider.DeepSeek) throw new PsdAiNamingException("供应商无效。");
            Refresh(apiKey, timeoutSeconds);
        }
        public static void Refresh(string apiKey, int timeoutSeconds)
        {
            if (IsRefreshing) return;
            if (string.IsNullOrWhiteSpace(apiKey)) { Status = "模型未刷新：请先配置 API Key，保留已有列表。"; Changed?.Invoke(); return; }
            try
            {
                _request = UnityWebRequest.Get("https://api.deepseek.com/models");
                _request.timeout = Mathf.Clamp(timeoutSeconds, 10, 600);
                _request.SetRequestHeader("Authorization", "Bearer " + apiKey.Trim());
                _request.SendWebRequest();
                EditorApplication.update += Poll;
                Status = "正在刷新模型列表……";
            }
            catch (Exception) { Release(); Status = "无法启动模型刷新，保留已有列表。"; }
            Changed?.Invoke();
        }
        private static void Poll()
        {
            if (_request == null || !_request.isDone) return;
            try
            {
                if (_request.result != UnityWebRequest.Result.Success)
                {
                    Status = _request.responseCode == 401 || _request.responseCode == 403
                        ? "模型刷新鉴权失败，保留已有列表。" : "模型刷新失败，保留已有列表。";
                    return;
                }
                var parsed = Parse(_request.downloadHandler.text);
                // 只缓存白名单模型元数据，不缓存原始响应、凭据或请求头。
                EditorPrefs.SetString(CacheKey, JsonUtility.ToJson(parsed));
                _current = Array.AsReadOnly(parsed.data);
                _source = "DeepSeek 服务最新返回";
                Status = "模型列表刷新成功。";
            }
            catch (Exception) { Status = "模型列表格式无效，保留已有列表。"; }
            finally { Release(); Changed?.Invoke(); }
        }
        public static void Cancel()
        {
            bool active = IsRefreshing;
            Release();
            if (active) { Status = "已取消模型刷新。"; Changed?.Invoke(); }
        }
        private static void Release()
        {
            EditorApplication.update -= Poll;
            var request = _request; _request = null;
            if (request == null) return;
            request.Abort(); request.Dispose();
        }
    }
}
