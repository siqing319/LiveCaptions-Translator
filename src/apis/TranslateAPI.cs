using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis
{
    public static class TranslateAPI
    {
        /*
         * The key of this field is used as the content for `translateAPIBox` in the `LecturePage`.
         * If you'd like to add a new API, please insert the key-value pair here.
         */
        public static readonly Dictionary<string, Func<string, CancellationToken, Task<string>>>
            TRANSLATE_FUNCTIONS = new()
        {
            { "Google", Google },
            { "Google2", Google2 },
            { "Ollama", Ollama },
            { "OpenAI", OpenAI },
            { "LMStudio", LMStudio },
            { "DeepL", DeepL },
            { "OpenRouter", OpenRouter },
            { "Youdao", Youdao },
            { "MTranServer", MTranServer },
            { "Baidu", Baidu },
            { "LibreTranslate", LibreTranslate },
        };
        public static readonly List<string> LLM_BASED_APIS = new()
        {
            "Ollama", "OpenAI", "OpenRouter", "LMStudio"
        };
        public static readonly List<string> NO_CONFIG_APIS = new()
        {
            "Google", "Google2"
        };

        public static Func<string, CancellationToken, Task<string>> TranslateFunction =>
            TRANSLATE_FUNCTIONS[Translator.Setting.ApiName];
        public static bool IsLLMBased => LLM_BASED_APIS.Contains(Translator.Setting.ApiName);
        public static string Prompt => Translator.Setting.Prompt;

        private static readonly HttpClient client = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        private static int openai_fallback_index = 0;

        private static string HttpError(HttpResponseMessage response)
        {
            return $"[ERROR] 翻译失败：HTTP 错误 - {response.StatusCode}";
        }

        private static string Md5Hex(string input)
        {
            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        }

        public static async Task<string> OpenAI(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["OpenAI"] as OpenAIConfig;
            string language = OpenAIConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;

            // Qwen-MT style endpoints reject `system` messages and accept
            // exactly one `user` message, so they take a different wire format entirely.
            bool isQwenMT = string.Equals(config.ApiStyle, "qwen-mt", StringComparison.OrdinalIgnoreCase);

            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language) + LectureState.SubjectHint() },
                new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
            };

            if (Translator.Setting.ContextAware)
            {
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");

                    messages.InsertRange(1, [
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" },
                        new BaseLLMConfig.Message { role = "assistant", content = $"{translatedText}" }
                    ]);
                }
            }

            if (isQwenMT)
            {
                // Collapse everything into a single user message and carry the instruction
                // plus the language pair in translation_options.
                string sourceText = text;
                if (Translator.Setting.ContextAware && messages.Count > 2)
                {
                    // Keep the recent source sentences as context, prefixed so the model can
                    // tell them apart from the sentence that actually needs translating.
                    var contextLines = new List<string>();
                    for (int i = 1; i < messages.Count - 1; i += 2)
                        contextLines.Add(messages[i].content);
                    if (contextLines.Count > 0)
                        sourceText = string.Join("\n", contextLines) + "\n" + text;
                }

                string targetLang = QwenMTLanguage(language);
                var requestData = new QwenMTRequestData(config.ModelName,
                    new List<BaseLLMConfig.Message>
                    {
                        new BaseLLMConfig.Message { role = "user", content = sourceText }
                    },
                    config.Temperature);
                requestData.translation_options.source_lang =
                    string.IsNullOrWhiteSpace(config.SourceLanguage) ? "auto" : config.SourceLanguage.Trim();
                requestData.translation_options.target_lang = targetLang;

                var terms = ParseGlossaryTerms(config.GlossaryTerms);
                if (terms.Count > 0)
                    requestData.translation_options.terms = terms;

                string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
                var request = new HttpRequestMessage(HttpMethod.Post, TextUtil.NormalizeUrl(config.ApiUrl))
                {
                    Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());

                try
                {
                    var mtResponse = await client.SendAsync(request, token);
                    string body = await mtResponse.Content.ReadAsStringAsync();
                    if (!mtResponse.IsSuccessStatusCode)
                        return FormatOpenAIError(mtResponse.StatusCode, body);

                    var responseObj = JsonSerializer.Deserialize<OpenAIConfig.Response>(body);
                    if (responseObj?.choices == null || responseObj.choices.Count == 0)
                        return "[ERROR] 翻译失败：接口返回了空结果。";
                    string output = responseObj.choices[0].message.content ?? "";
                    return RegexPatterns.ModelThinking().Replace(output, "");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return $"[ERROR] 翻译失败：{ex.Message}";
                }
            }

            if (string.IsNullOrWhiteSpace(config.ApiKey))
                return "[ERROR] 翻译失败：还没有填 API Key，请到“设置”页的“模型与 API”里填写。";

            HttpResponseMessage response;
            try
            {
                while (true)
                {
                    var requestData = LLMRequestDataFactory.Create(openai_fallback_index,
                        config.ModelName, messages, config.Temperature);
                    string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
                    // Set the key on each request: sentences are translated in parallel, and changing the
                    // shared client's default headers let some requests go out without it (HTTP 401).
                    var request = new HttpRequestMessage(HttpMethod.Post, TextUtil.NormalizeUrl(config.ApiUrl))
                    {
                        Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());

                    response = await client.SendAsync(request, token);
                    if (response.StatusCode != HttpStatusCode.BadRequest &&
                        response.StatusCode != HttpStatusCode.UnprocessableEntity)
                        break;
                    // The account has no balance: another request format would fail the same way.
                    if (TextUtil.IsArrears(await response.Content.ReadAsStringAsync()))
                        break;
                    await Task.Delay(15);

                    openai_fallback_index++;
                    if (openai_fallback_index >= LLMRequestDataFactory.FallbackCount)
                    {
                        openai_fallback_index = 0;
                        break;
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] 翻译失败：请求超时（超过 8 秒），" +
                           $"请换一个更快的接口或检查网络。";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] 翻译失败：{ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<OpenAIConfig.Response>(responseString);
                var output = responseObj.choices[0].message.content;
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
                return "[ERROR] 翻译失败：API Key 无效（HTTP 401），请在“设置”页里重新粘贴 API Key 并点“测试连接”。";
            else if (response.StatusCode == HttpStatusCode.Forbidden &&
                     TextUtil.IsQuotaExhausted(await response.Content.ReadAsStringAsync()))
                return $"[ERROR] 翻译失败：{TextUtil.QuotaExhaustedMessage}";
            else if (response.StatusCode == HttpStatusCode.BadRequest &&
                     TextUtil.IsArrears(await response.Content.ReadAsStringAsync()))
                return $"[ERROR] 翻译失败：{TextUtil.ArrearageMessage}";
            else
                return HttpError(response);
        }

        /// <summary>
        /// Maps the app's BCP-47-ish target language to the language name/code that the
        /// Qwen-MT translation endpoints expect in `translation_options.target_lang`.
        /// </summary>
        private static string QwenMTLanguage(string language)
        {
            return language switch
            {
                "zh-CN" => "Chinese",
                "zh-TW" => "Traditional Chinese",
                "en-US" => "English",
                "en-GB" => "English",
                "ja-JP" => "Japanese",
                "ko-KR" => "Korean",
                "fr-FR" => "French",
                "th-TH" => "Thai",
                "ru-RU" => "Russian",
                "es-ES" => "Spanish",
                "pt-BR" => "Portuguese",
                "tr-TR" => "Turkish",
                "ar-SA" => "Arabic",
                _ => language
            };
        }

        /// <summary>
        /// Parses the glossary textarea. One term per line, written as
        /// "source = target" or "source=target". Blank lines are ignored.
        /// </summary>
        private static List<QwenMTRequestData.Term> ParseGlossaryTerms(string glossary)
        {
            var terms = new List<QwenMTRequestData.Term>();
            if (string.IsNullOrWhiteSpace(glossary))
                return terms;

            foreach (var rawLine in glossary.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                int sep = line.IndexOf('=');
                if (sep <= 0)
                    continue;
                string source = line[..sep].Trim();
                string target = line[(sep + 1)..].Trim();
                if (source.Length == 0 || target.Length == 0)
                    continue;
                terms.Add(new QwenMTRequestData.Term { source = source, target = target });
            }
            return terms;
        }

        /// <summary>
        /// Renders a non-success HTTP response as a readable one-liner for the caption area.
        /// </summary>
        private static string FormatOpenAIError(HttpStatusCode status, string body)
        {
            if (status == HttpStatusCode.Unauthorized)
                return "[ERROR] 翻译失败：API Key 无效（HTTP 401），请在“设置”页里重新粘贴 API Key 并点“测试连接”。";
            if (status == HttpStatusCode.NotFound)
                return "[ERROR] 翻译失败：接口地址不存在（HTTP 404）。请检查“接口地址”是否填写完整，" +
                       "通常需要以 /v1/chat/completions 结尾。";
            if (status == HttpStatusCode.BadRequest)
            {
                if (TextUtil.IsArrears(body))
                    return $"[ERROR] 翻译失败：{TextUtil.ArrearageMessage}";
                return "[ERROR] 翻译失败：请求参数被接口拒绝（HTTP 400）。请检查模型名称、接口地址和请求协议。";
            }
            if (body.Length > 0 && body.Length < 400)
                return $"[ERROR] 翻译失败（HTTP {(int)status}）：{body}";
            return $"[ERROR] 翻译失败（HTTP {(int)status}）。";
        }

        public static async Task<string> Ollama(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["Ollama"] as OllamaConfig;
            string language = OllamaConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl + "/api/chat");

            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language) },
                new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
            };

            if (Translator.Setting.ContextAware)
            {
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");

                    messages.InsertRange(1, [
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" },
                        new BaseLLMConfig.Message { role = "assistant", content = $"{translatedText}" }
                    ]);
                }
            }

            var requestData = LLMRequestDataFactory.Create("Ollama", config.ModelName, messages, config.Temperature);
            requestData.keep_alive = config.keep_alive;
            string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = content };
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] 翻译失败：请求超时（超过 8 秒），" +
                           $"请换一个更快的接口或检查网络。";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] 翻译失败：{ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<OllamaConfig.Response>(responseString);
                var output = responseObj.message.content;
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                return HttpError(response);
        }

        public static async Task<string> LMStudio(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["LMStudio"] as LMStudioConfig;
            string language = LMStudioConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl) + "/chat";

            string systemPrompt = string.Format(Prompt, language);

            // Build input with optional context
            string input = $"🔤 {text} 🔤";
            if (Translator.Setting.ContextAware)
            {
                var contextLines = new List<string>();
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");
                    contextLines.Add($"🔤 {entry.SourceText} 🔤 → {translatedText}");
                }
                if (contextLines.Count > 0)
                    input = string.Join("\n", contextLines) + "\n" + input;
            }

            var requestData = new
            {
                model = config.ModelName,
                system_prompt = systemPrompt,
                input = input,
                temperature = config.Temperature
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = content };
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] 翻译失败：请求超时（超过 8 秒），" +
                           $"请换一个更快的接口或检查网络。";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] 翻译失败：{ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;

                // LMStudio native /api/v1/chat response:
                // { "output": [ { "type": "message", "content": "..." }, ... ] }
                if (root.TryGetProperty("output", out var outputArray) &&
                    outputArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in outputArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var typeProp) &&
                            typeProp.GetString() == "message" &&
                            item.TryGetProperty("content", out var contentProp))
                        {
                            return RegexPatterns.ModelThinking().Replace(contentProp.GetString() ?? "", "");
                        }
                    }
                }

                return "[ERROR] 翻译失败：接口返回格式异常";
            }
            else
            {
                string body = await response.Content.ReadAsStringAsync();
                return $"[ERROR] 翻译失败：HTTP 错误 - {response.StatusCode}: {body}";
            }
        }

        public static async Task<string> OpenRouter(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["OpenRouter"] as OpenRouterConfig;
            string language = OpenRouterConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = "https://openrouter.ai/api/v1/chat/completions";

            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language) },
                new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
            };

            if (Translator.Setting.ContextAware)
            {
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");

                    messages.InsertRange(1, [
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" },
                        new BaseLLMConfig.Message { role = "assistant", content = $"{translatedText}" }
                    ]);
                }
            }

            var requestData = LLMRequestDataFactory.Create("OpenRouter", config.ModelName, messages, config.Temperature);

            string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = content };
                request.Headers.Add("Authorization", $"Bearer {config?.ApiKey}");
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] 翻译失败：请求超时（超过 8 秒），" +
                           $"请换一个更快的接口或检查网络。";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] 翻译失败：{ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
                var output = jsonResponse.GetProperty("choices")[0]
                                         .GetProperty("message")
                                         .GetProperty("content")
                                         .GetString() ?? string.Empty;
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                return HttpError(response);
        }

        public static async Task<string> Google(string text, CancellationToken token = default)
        {
            var language = Translator.Setting?.TargetLanguage;

            string encodedText = Uri.EscapeDataString(text);
            var url = $"https://clients5.google.com/translate_a/t?" +
                      $"client=dict-chrome-ex&sl=auto&" +
                      $"tl={language}&" +
                      $"q={encodedText}";

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] 翻译失败：请求超时（超过 8 秒），" +
                           $"请换一个更快的接口或检查网络。";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] 翻译失败：{ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();

                var responseObj = JsonSerializer.Deserialize<List<List<string>>>(responseString);

                string translatedText = responseObj[0][0];
                return translatedText;
            }
            else
                return HttpError(response);
        }

        public static async Task<string> Google2(string text, CancellationToken token = default)
        {
            string apiKey = "AIzaSyA6EEtrDCfBkHV8uU2lgGY-N383ZgAOo7Y";
            var language = Translator.Setting?.TargetLanguage;
            string strategy = "2";

            string encodedText = Uri.EscapeDataString(text);
            string url = $"https://dictionaryextension-pa.googleapis.com/v1/dictionaryExtensionData?" +
                         $"language={language}&" +
                         $"key={apiKey}&" +
                         $"term={encodedText}&" +
                         $"strategy={strategy}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-referer", "chrome-extension://mgijmajocgfcbeboacabfgobmjgjcoja");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] 翻译失败：请求超时（超过 8 秒），" +
                           $"请换一个更快的接口或检查网络。";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] 翻译失败：{ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync();

                using var jsonDoc = JsonDocument.Parse(responseBody);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("translateResponse", out JsonElement translateResponse))
                {
                    string translatedText = translateResponse.GetProperty("translateText").GetString();
                    return translatedText;
                }
                else
                    return "[ERROR] 翻译失败：接口返回格式异常";
            }
            else
                return HttpError(response);
        }

        public static async Task<string> DeepL(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["DeepL"] as DeepLConfig;
            string language = DeepLConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                text = new[] { text },
                target_lang = language
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = content };
                request.Headers.Add("Authorization", $"DeepL-Auth-Key {config?.ApiKey}");
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] 翻译失败：请求超时（超过 8 秒），" +
                           $"请换一个更快的接口或检查网络。";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] 翻译失败：{ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);

                if (doc.RootElement.TryGetProperty("translations", out var translations) &&
                    translations.ValueKind == JsonValueKind.Array && translations.GetArrayLength() > 0)
                {
                    return translations[0].GetProperty("text").GetString();
                }
                return "[ERROR] 翻译失败：没有有效返回";
            }
            else
                return HttpError(response);
        }


        public static async Task<string> Youdao(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["Youdao"] as YoudaoConfig;
            string language = YoudaoConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;

            string salt = DateTime.Now.Millisecond.ToString();
            string sign = Md5Hex($"{config.AppKey}{text}{salt}{config.AppSecret}");

            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appKey"] = config.AppKey,
                ["salt"] = salt,
                ["sign"] = sign
            };

            var content = new FormUrlEncodedContent(parameters);

            HttpResponseMessage response;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, config.ApiUrl) { Content = content };
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] 翻译失败：请求超时（超过 8 秒），" +
                           $"请换一个更快的接口或检查网络。";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] 翻译失败：{ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<YoudaoConfig.TranslationResult>(responseString);

                if (responseObj.errorCode != "0")
                    return $"[ERROR] 翻译失败：有道错误 - {responseObj.errorCode}";

                return responseObj.translation?.FirstOrDefault() ?? "[ERROR] 翻译失败：没有内容";
            }
            else
            {
                return HttpError(response);
            }
        }

        public static async Task<string> MTranServer(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["MTranServer"] as MTranServerConfig;
            string targetLanguage = MTranServerConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string sourceLanguage = config.SourceLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                text = text,
                to = targetLanguage,
                from = sourceLanguage
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = content };
                request.Headers.Add("Authorization", $"Bearer {config?.ApiKey}");
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] 翻译失败：请求超时（超过 8 秒），" +
                           $"请换一个更快的接口或检查网络。";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] 翻译失败：{ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<MTranServerConfig.Response>(responseString);
                return responseObj.result;
            }
            else
                return HttpError(response);
        }

        public static async Task<string> Baidu(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["Baidu"] as BaiduConfig;
            string language = BaiduConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;

            string salt = DateTime.Now.Millisecond.ToString();
            string sign = Md5Hex($"{config.AppId}{text}{salt}{config.AppSecret}");

            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appid"] = config.AppId,
                ["salt"] = salt,
                ["sign"] = sign
            };

            var content = new FormUrlEncodedContent(parameters);

            HttpResponseMessage response;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, config.ApiUrl) { Content = content };
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] 翻译失败：请求超时（超过 8 秒），" +
                           $"请换一个更快的接口或检查网络。";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] 翻译失败：{ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<BaiduConfig.TranslationResult>(responseString);

                if (responseObj.error_code is not null && responseObj.error_code != "0")
                    return $"[ERROR] 翻译失败：百度错误 - {responseObj.error_code}";

                return responseObj.trans_result?.FirstOrDefault()?.dst ?? "[ERROR] 翻译失败：没有内容";
            }
            else
            {
                return HttpError(response);
            }
        }

        public static async Task<string> LibreTranslate(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["LibreTranslate"] as LibreTranslateConfig;
            string targetLanguage = LibreTranslateConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                q = text,
                target = targetLanguage,
                source = "auto",
                format = "text",
                api_key = config?.ApiKey
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = content };
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] 翻译失败：请求超时（超过 8 秒），" +
                           $"请换一个更快的接口或检查网络。";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] 翻译失败：{ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<LibreTranslateConfig.Response>(responseString);
                return responseObj.translatedText;
            }
            else
                return HttpError(response);
        }
    }

    public class ConfigDictConverter : JsonConverter<Dictionary<string, List<TranslateAPIConfig>>>
    {
        private static Type ResolveConfigType(string key)
        {
            var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
            return configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType)
                ? configType
                : typeof(TranslateAPIConfig);
        }

        public override Dictionary<string, List<TranslateAPIConfig>> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected a StartObject token.");
            var configs = new Dictionary<string, List<TranslateAPIConfig>>();

            reader.Read();
            while (reader.TokenType == JsonTokenType.PropertyName)
            {
                string key = reader.GetString();
                reader.Read();

                var configType = ResolveConfigType(key);
                TranslateAPIConfig config;

                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    var list = new List<TranslateAPIConfig>();
                    reader.Read();

                    while (reader.TokenType != JsonTokenType.EndArray)
                    {
                        config = (TranslateAPIConfig)JsonSerializer.Deserialize(ref reader, configType, options);

                        list.Add(config);
                        reader.Read();
                    }
                    configs[key] = list;
                }
                else
                    throw new JsonException("Expected a StartObject token or a StartArray token.");

                reader.Read();
            }

            if (reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException("Expected an EndObject token.");
            return configs;
        }

        public override void Write(
            Utf8JsonWriter writer, Dictionary<string, List<TranslateAPIConfig>> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kvp in value)
            {
                writer.WritePropertyName(kvp.Key);
                var configType = ResolveConfigType(kvp.Key);

                if (kvp.Value is IEnumerable<TranslateAPIConfig> configList)
                {
                    writer.WriteStartArray();
                    foreach (var config in configList)
                    {
                        JsonSerializer.Serialize(writer, config, configType, options);
                    }
                    writer.WriteEndArray();
                }
                else
                    throw new JsonException($"Unsupported config type: {kvp.Value.GetType()}");
            }
            writer.WriteEndObject();
        }
    }
}
