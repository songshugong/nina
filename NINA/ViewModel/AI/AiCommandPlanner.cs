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
                var jsonCommands = commandRouter.Route(text);
                if (HasExecutableCommands(jsonCommands)) {
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
                    var llmCommands = commandRouter.Route(translatedJson);
                    if (HasExecutableCommands(llmCommands)) {
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

            var routedCommands = commandRouter.Route(text);
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

        private static bool HasExecutableCommands(IList<AiCommand> commands) {
            return commands != null &&
                   commands.Count > 0 &&
                   commands.Any(c => !string.Equals(c.Action, "unknown", StringComparison.OrdinalIgnoreCase));
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
