using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;

namespace PsdUiWorkbench.AiNaming
{
    public sealed class PsdAiNamingClient : IPsdAiNamingClient, IPsdAiProviderPreflight
    {
        private UnityWebRequest _request;
        public bool HasCredentials => !string.IsNullOrWhiteSpace(PsdAiCredentials.ResolveKey());
        public bool HasCredentialsFor(PsdAiProvider provider) => !string.IsNullOrWhiteSpace(PsdAiCredentials.ResolveKey(provider));
        public IEnumerator Analyze(PsdAiNamingSnapshot snapshot, PsdAiRequestSettings settings,
            Action<IReadOnlyList<PsdAiSuggestion>> completed)
        {
            byte[] body = PsdAiProtocol.BuildBody(snapshot, settings);
            string key = PsdAiCredentials.ResolveKey(settings.Provider);
            string providerName = PsdAiModelCatalog.ProviderName(settings.Provider);
            if (string.IsNullOrWhiteSpace(key)) throw new PsdAiNamingException("请先配置" + providerName +
                "本机 API Key 或 " + PsdAiCredentials.EnvironmentVariable(settings.Provider) + "。");
            if (_request != null) throw new PsdAiNamingException("已有请求正在执行。");
            var request = new UnityWebRequest(PsdAiModelCatalog.ChatCompletionsUrl(settings.Provider), "POST");
            _request = request;
            try
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = settings.TimeoutSeconds;
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + key);
                key = null;
                var operation = request.SendWebRequest();
                while (!operation.isDone) yield return null;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    string message;
                    switch (request.responseCode)
                    {
                        case 401: case 403: message = providerName + "鉴权失败，请检查本机凭据和权限。"; break;
                        case 402: message = providerName + "账户余额不足。"; break;
                        case 429: message = providerName + "请求受限，请稍后手动重试。"; break;
                        case 400: case 422: message = providerName + "拒绝请求，请检查模型图片能力及设置。"; break;
                        default: message = providerName + "请求失败或超时，请检查网络后手动重试。"; break;
                    }
                    throw new PsdAiNamingException(message);
                }
                completed(PsdAiProtocol.ParseResponse(request.downloadHandler.text, snapshot.NodeKeys));
            }
            finally
            {
                if (ReferenceEquals(_request, request)) { _request = null; request.Dispose(); }
            }
        }
        public void Dispose()
        {
            var request = _request; _request = null;
            if (request == null) return;
            request.Abort(); request.Dispose();
        }
    }
}
