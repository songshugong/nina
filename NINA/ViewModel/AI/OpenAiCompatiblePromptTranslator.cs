#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NINA.Core.Utility;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace NINA.ViewModel.AI {

    public class OpenAiCompatiblePromptTranslator : IAiPromptTranslator {
        private const string ApiUrlKey = "NINA_AI_API_URL";
        private const string ApiKeyKey = "NINA_AI_API_KEY";
        private const string ModelKey = "NINA_AI_MODEL";
        private const string TimeoutSecondsKey = "NINA_AI_TIMEOUT_SECONDS";

        private const string SystemInstruction =
            "You convert user astronomy control intent into NINA command JSON only. " +
            "Allowed actions: connect, disconnect, status, start_sequence, stop, park, unpark, home_mount, platesolve, center, slew, tracking_on, tracking_off, guide_start, guide_stop, cool_camera, warm_camera, dome_open, dome_close, dome_follow_on, dome_follow_off, dome_park, dome_home, flat_light_on, flat_light_off, help. " +
            "Return a JSON object only. Single command format: " +
            "{\"action\":\"status\",\"parameters\":{}}. " +
            "Multi command format: " +
            "{\"commands\":[{\"action\":\"status\",\"parameters\":{}},{\"action\":\"connect\",\"parameters\":{}}]}. " +
            "For slew use parameters ra, dec, optional ra_unit (hours|deg). " +
            "Never include markdown or explanations.";

        public async Task<string> TranslateToCommandJsonAsync(string prompt, string runtimeContext = null) {
            var settings = ReadSettings();
            if (settings == null || string.IsNullOrWhiteSpace(prompt)) {
                return null;
            }

            try {
                using var httpClient = new HttpClient {
                    Timeout = settings.Timeout
                };
                using var request = BuildRequest(settings, prompt, runtimeContext);

                using var response = await httpClient.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    Logger.Warning($"AI API request failed with status {(int)response.StatusCode}: {response.ReasonPhrase}");
                    return null;
                }

                return TryExtractJsonFromResponse(body);
            } catch (Exception ex) {
                Logger.Warning($"AI API request failed: {ex.Message}");
                return null;
            }
        }

        private static HttpRequestMessage BuildRequest(AiApiSettings settings, string prompt, string runtimeContext) {
            var messages = new JArray {
                new JObject {
                    ["role"] = "system",
                    ["content"] = SystemInstruction
                }
            };

            if (!string.IsNullOrWhiteSpace(runtimeContext)) {
                messages.Add(new JObject {
                    ["role"] = "system",
                    ["content"] = "Runtime equipment context (real-time):\n" + runtimeContext + "\nPrefer commands that match this context and avoid impossible or unsupported operations."
                });
            }

            messages.Add(new JObject {
                ["role"] = "user",
                ["content"] = prompt
            });

            var payload = new JObject {
                ["model"] = settings.Model,
                ["temperature"] = 0,
                ["messages"] = messages
            };

            var request = new HttpRequestMessage(HttpMethod.Post, settings.ApiUrl);
            request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(settings.ApiKey)) {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            }
            return request;
        }

        private static string TryExtractJsonFromResponse(string body) {
            if (string.IsNullOrWhiteSpace(body)) {
                return null;
            }

            try {
                var root = JObject.Parse(body);
                var content = ExtractContentText(root.SelectToken("choices[0].message.content"))
                              ?? ExtractContentText(root.SelectToken("output[0].content[0].text"))
                              ?? ExtractContentText(root["response"]);
                return ExtractJsonObject(content);
            } catch (Exception ex) {
                Logger.Warning($"AI API response parse failed: {ex.Message}");
                return null;
            }
        }

        private static string ExtractContentText(JToken contentToken) {
            if (contentToken == null) {
                return null;
            }

            if (contentToken.Type == JTokenType.String) {
                return contentToken.ToString();
            }

            if (contentToken is JArray parts) {
                var texts = parts.Select(part => part?["text"]?.ToString())
                                 .Where(t => !string.IsNullOrWhiteSpace(t));
                return string.Join("", texts);
            }

            return contentToken.ToString(Formatting.None);
        }

        private static string ExtractJsonObject(string content) {
            if (string.IsNullOrWhiteSpace(content)) {
                return null;
            }

            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end < start) {
                return null;
            }

            var candidate = content.Substring(start, end - start + 1).Trim();
            try {
                _ = JObject.Parse(candidate);
                return candidate;
            } catch {
                return null;
            }
        }

        private static AiApiSettings ReadSettings() {
            var apiUrl = Environment.GetEnvironmentVariable(ApiUrlKey)?.Trim();
            if (string.IsNullOrWhiteSpace(apiUrl)) {
                return null;
            }
            var apiKey = Environment.GetEnvironmentVariable(ApiKeyKey)?.Trim();

            var model = Environment.GetEnvironmentVariable(ModelKey)?.Trim();
            if (string.IsNullOrWhiteSpace(model)) {
                model = "gpt-4.1-mini";
            }

            var timeout = TimeSpan.FromSeconds(20);
            var timeoutRaw = Environment.GetEnvironmentVariable(TimeoutSecondsKey)?.Trim();
            if (!string.IsNullOrWhiteSpace(timeoutRaw) && int.TryParse(timeoutRaw, out var timeoutSeconds) && timeoutSeconds > 1) {
                timeout = TimeSpan.FromSeconds(timeoutSeconds);
            }

            return new AiApiSettings {
                ApiUrl = apiUrl,
                ApiKey = apiKey,
                Model = model,
                Timeout = timeout
            };
        }

        private sealed class AiApiSettings {
            public string ApiUrl { get; set; } = string.Empty;
            public string ApiKey { get; set; } = string.Empty;
            public string Model { get; set; } = string.Empty;
            public TimeSpan Timeout { get; set; }
        }
    }
}
