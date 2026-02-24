#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.ViewModel.AI {

    public class AiCommandRouter : IAiCommandRouter {
        public IList<AiCommand> Route(string prompt) {
            var commands = new List<AiCommand>();
            var actionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var text = prompt?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text)) {
                return commands;
            }

            if (TryParseJsonCommands(text, out var jsonCommands)) {
                commands.AddRange(jsonCommands);
                return commands;
            }

            var normalized = text.ToLowerInvariant();
            if (HasAny(normalized, "connect", "连接", "连设备", "connect all")) {
                AddUnique(commands, actionSet, "connect", text);
            }

            if (HasAny(normalized, "disconnect", "断开", "断连", "disconnect all")) {
                AddUnique(commands, actionSet, "disconnect", text);
            }

            if (HasAny(normalized, "help", "帮助", "commands", "指令")) {
                AddUnique(commands, actionSet, "help", text);
            }

            if (HasAny(normalized, "status", "状态", "health", "telemetry")) {
                AddUnique(commands, actionSet, "status", text);
            }

            if (HasAny(normalized, "start sequence", "开始序列", "开拍", "start capture")) {
                AddUnique(commands, actionSet, "start_sequence", text);
            }

            var mentionsGuiding = HasAny(normalized, "guiding", "导星");
            var mentionsDomeFollow = HasAny(normalized, "dome follow", "穹顶跟随");
            if (HasAny(normalized, "stop", "停止", "终止", "急停", "cancel sequence") && !mentionsGuiding && !mentionsDomeFollow) {
                AddUnique(commands, actionSet, "stop", text);
            }

            var matchesUnpark = HasAny(normalized, "unpark", "解锁赤道仪", "取消驻车");
            if (matchesUnpark) {
                AddUnique(commands, actionSet, "unpark", text);
            }

            var mentionsDome = HasAny(normalized, "dome", "roof", "穹顶", "天顶");
            var matchesPark = HasAny(normalized, "park", "驻车", "回park位") && !matchesUnpark && !mentionsDome;
            if (matchesPark) {
                AddUnique(commands, actionSet, "park", text);
            }

            if (HasAny(normalized, "home mount", "find mount home", "mount home", "赤道仪回零")) {
                AddUnique(commands, actionSet, "home_mount", text);
            }

            if (HasAny(normalized, "platesolve", "plate solve", "板解", "解算")) {
                AddUnique(commands, actionSet, "platesolve", text);
            }

            if (HasAny(normalized, "center", "居中")) {
                AddUnique(commands, actionSet, "center", text);
            } else if (HasAny(normalized, "slew", "goto", "转到", "指向")) {
                AddUnique(commands, actionSet, "slew", text);
            }

            if (HasAny(normalized, "tracking on", "enable tracking", "开启跟踪", "打开跟踪", "开跟踪")) {
                AddUnique(commands, actionSet, "tracking_on", text);
            }

            if (HasAny(normalized, "tracking off", "disable tracking", "关闭跟踪", "关跟踪", "停跟踪")) {
                AddUnique(commands, actionSet, "tracking_off", text);
            }

            if (HasAny(normalized, "start guiding", "guide start", "开始导星", "开导星", "启动导星")) {
                AddUnique(commands, actionSet, "guide_start", text);
            }

            if (HasAny(normalized, "stop guiding", "guide stop", "停止导星", "停导星", "关闭导星")) {
                AddUnique(commands, actionSet, "guide_stop", text);
            }

            if (HasAny(normalized, "cool camera", "camera cool", "相机制冷", "相机降温", "制冷相机")) {
                AddUnique(commands, actionSet, "cool_camera", text);
            }

            if (HasAny(normalized, "warm camera", "camera warm", "相机升温", "相机回温", "相机回暖")) {
                AddUnique(commands, actionSet, "warm_camera", text);
            }

            if (HasAny(normalized, "open dome", "open roof", "dome open", "开穹顶", "开天顶", "打开穹顶", "开顶")) {
                AddUnique(commands, actionSet, "dome_open", text);
            }

            if (HasAny(normalized, "close dome", "close roof", "dome close", "关穹顶", "关天顶", "关闭穹顶", "关顶")) {
                AddUnique(commands, actionSet, "dome_close", text);
            }

            if (HasAny(normalized, "enable dome follow", "start dome follow", "dome follow on", "开启穹顶跟随", "打开穹顶跟随", "穹顶跟随开启")) {
                AddUnique(commands, actionSet, "dome_follow_on", text);
            }

            if (HasAny(normalized, "disable dome follow", "stop dome follow", "dome follow off", "关闭穹顶跟随", "停止穹顶跟随", "穹顶跟随关闭")) {
                AddUnique(commands, actionSet, "dome_follow_off", text);
            }

            if (HasAny(normalized, "park dome", "dome park", "穹顶驻车", "穹顶停车")) {
                AddUnique(commands, actionSet, "dome_park", text);
            }

            if (HasAny(normalized, "home dome", "dome home", "穹顶回零", "穹顶回家")) {
                AddUnique(commands, actionSet, "dome_home", text);
            }

            if (HasAny(normalized, "flat light on", "flat panel on", "开启平场灯", "打开平场灯", "平场灯开")) {
                AddUnique(commands, actionSet, "flat_light_on", text);
            }

            if (HasAny(normalized, "flat light off", "flat panel off", "关闭平场灯", "关平场灯", "平场灯关")) {
                AddUnique(commands, actionSet, "flat_light_off", text);
            }

            if (commands.Count == 0) {
                commands.Add(Create("unknown", text));
            }

            return commands;
        }

        private static bool TryParseJsonCommands(string text, out IList<AiCommand> commands) {
            commands = new List<AiCommand>();
            try {
                if (!text.StartsWith("{", StringComparison.Ordinal) || !text.EndsWith("}", StringComparison.Ordinal)) {
                    return false;
                }

                var root = JObject.Parse(text);
                if (root["commands"] is JArray cmdArray) {
                    foreach (var token in cmdArray) {
                        if (token is not JObject commandObject) {
                            continue;
                        }
                        var parsed = ParseSingleJsonCommand(commandObject, text);
                        if (parsed != null) {
                            commands.Add(parsed);
                        }
                    }
                    return commands.Count > 0;
                }

                var single = ParseSingleJsonCommand(root, text);
                if (single != null) {
                    commands.Add(single);
                    return true;
                }

                return false;
            } catch {
                return false;
            }
        }

        private static AiCommand ParseSingleJsonCommand(JObject root, string rawPrompt) {
            var action = root.Value<string>("action")?.Trim();
            if (string.IsNullOrWhiteSpace(action)) {
                return null;
            }

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root["parameters"] is JObject pObj) {
                foreach (var prop in pObj.Properties()) {
                    parameters[prop.Name] = prop.Value?.ToString() ?? string.Empty;
                }
            }

            return new AiCommand {
                Action = action.ToLowerInvariant(),
                RawPrompt = rawPrompt,
                Parameters = parameters
            };
        }

        private static AiCommand Create(string action, string rawPrompt) {
            return new AiCommand {
                Action = action,
                RawPrompt = rawPrompt
            };
        }

        private static void AddUnique(ICollection<AiCommand> commands, ISet<string> actionSet, string action, string rawPrompt) {
            if (actionSet.Add(action)) {
                commands.Add(Create(action, rawPrompt));
            }
        }

        private static bool HasAny(string source, params string[] phrases) {
            return phrases.Any(p => ContainsPhrase(source, p));
        }

        private static bool ContainsPhrase(string source, string phrase) {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(phrase)) {
                return false;
            }

            var token = phrase.Trim();
            if (token.Length > 2 && token.All(IsAsciiLetter)) {
                return ContainsWord(source, token);
            }

            return source.Contains(token, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAsciiLetter(char c) {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private static bool ContainsWord(string source, string word) {
            var start = 0;
            while (true) {
                var index = source.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0) {
                    return false;
                }

                var beforeOk = index == 0 || !char.IsLetterOrDigit(source[index - 1]);
                var endIndex = index + word.Length;
                var afterOk = endIndex >= source.Length || !char.IsLetterOrDigit(source[endIndex]);
                if (beforeOk && afterOk) {
                    return true;
                }

                start = index + word.Length;
            }
        }
    }
}
