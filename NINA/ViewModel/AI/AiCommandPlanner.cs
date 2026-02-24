#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.ViewModel.AI {

    public class AiCommandPlanner : IAiCommandPlanner {
        private static readonly HashSet<string> SupportedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "connect",
            "help",
            "status",
            "start_sequence",
            "stop",
            "park",
            "unpark",
            "platesolve",
            "center",
            "slew"
        };

        private readonly IAiCommandRouter commandRouter;
        private readonly IAiPromptTranslator promptTranslator;

        public AiCommandPlanner(IAiCommandRouter commandRouter, IAiPromptTranslator promptTranslator) {
            this.commandRouter = commandRouter;
            this.promptTranslator = promptTranslator;
        }

        public async Task<AiCommandPlan> PlanAsync(string prompt) {
            var text = prompt?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) {
                return new AiCommandPlan {
                    Source = "empty"
                };
            }

            if (LooksLikeJson(text)) {
                var jsonCommands = FilterSupportedCommands(commandRouter.Route(text), "json");
                if (jsonCommands.Count > 0) {
                    ApplyMetadata(jsonCommands, text, "json");
                    return new AiCommandPlan {
                        Commands = jsonCommands,
                        Source = "json"
                    };
                }
            }

            try {
                var translatedJson = await promptTranslator.TranslateToCommandJsonAsync(text);
                if (!string.IsNullOrWhiteSpace(translatedJson)) {
                    var llmCommands = FilterSupportedCommands(commandRouter.Route(translatedJson), "llm");
                    if (llmCommands.Count > 0) {
                        ApplyMetadata(llmCommands, text, "llm");
                        return new AiCommandPlan {
                            Commands = llmCommands,
                            Source = "llm"
                        };
                    }
                }
            } catch (Exception ex) {
                Logger.Warning($"AI translator failed, falling back to rule-based routing: {ex.Message}");
            }

            var routedCommands = FilterSupportedCommands(commandRouter.Route(text), "rules");
            ApplyMetadata(routedCommands, text, "rules");
            return new AiCommandPlan {
                Commands = routedCommands,
                Source = "rules"
            };
        }

        private static bool LooksLikeJson(string text) {
            var trimmed = text.Trim();
            return trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal);
        }

        private static IList<AiCommand> FilterSupportedCommands(IList<AiCommand> commands, string source) {
            var filtered = new List<AiCommand>();
            if (commands == null || commands.Count == 0) {
                return filtered;
            }

            foreach (var command in commands) {
                if (command == null || string.IsNullOrWhiteSpace(command.Action)) {
                    continue;
                }

                if (!SupportedActions.Contains(command.Action)) {
                    Logger.Warning($"AI planner dropped unsupported action '{command.Action}' from {source}.");
                    continue;
                }

                filtered.Add(command);
            }

            return filtered;
        }

        private static void ApplyMetadata(IEnumerable<AiCommand> commands, string rawPrompt, string source) {
            if (commands == null) {
                return;
            }

            foreach (var command in commands) {
                if (command != null) {
                    command.RawPrompt = rawPrompt;
                    command.RoutingSource = source;
                }
            }
        }
    }
}
