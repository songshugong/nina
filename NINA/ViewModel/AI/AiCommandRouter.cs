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

            if (HasAny(normalized, "help", "帮助", "commands", "指令")) {
                AddUnique(commands, actionSet, "help", text);
            }

            if (HasAny(normalized, "status", "状态", "health", "telemetry")) {
                AddUnique(commands, actionSet, "status", text);
            }

            if (HasAny(normalized, "start sequence", "开始序列", "开拍", "start capture")) {
                AddUnique(commands, actionSet, "start_sequence", text);
            }

            if (HasAny(normalized, "stop", "停止", "终止", "急停", "cancel sequence")) {
                AddUnique(commands, actionSet, "stop", text);
            }

            var matchesUnpark = HasAny(normalized, "unpark", "解锁赤道仪", "取消驻车");
            if (matchesUnpark) {
                AddUnique(commands, actionSet, "unpark", text);
            }

            var matchesPark = HasAny(normalized, "park", "驻车", "回park位") && !matchesUnpark;
            if (matchesPark) {
                AddUnique(commands, actionSet, "park", text);
            }

            if (HasAny(normalized, "platesolve", "plate solve", "板解", "解算")) {
                AddUnique(commands, actionSet, "platesolve", text);
            }

            if (HasAny(normalized, "center", "居中")) {
                AddUnique(commands, actionSet, "center", text);
            } else if (HasAny(normalized, "slew", "goto", "转到", "指向")) {
                AddUnique(commands, actionSet, "slew", text);
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
