#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NINA.ViewModel.AI {

    public enum AiControlAction {
        None,
        Confirm,
        Cancel,
        Pending
    }

    public static class AiRiskControl {
        private static readonly HashSet<string> HighRiskActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "start_sequence",
            "park",
            "unpark",
            "slew",
            "tracking_off"
        };

        public static AiControlAction ParseControlAction(string prompt) {
            var normalized = Normalize(prompt);
            if (string.IsNullOrWhiteSpace(normalized)) {
                return AiControlAction.None;
            }

            if (ContainsEnglishWord(normalized, "pending") || normalized.Contains("待确认", StringComparison.OrdinalIgnoreCase)) {
                return AiControlAction.Pending;
            }

            if (ContainsEnglishWord(normalized, "confirm") || normalized.Contains("确认", StringComparison.OrdinalIgnoreCase)) {
                return AiControlAction.Confirm;
            }

            if (ContainsEnglishWord(normalized, "cancel") || normalized.Contains("取消", StringComparison.OrdinalIgnoreCase)) {
                return AiControlAction.Cancel;
            }

            return AiControlAction.None;
        }

        public static bool RequiresConfirmation(IEnumerable<AiCommand> commands, string prompt) {
            if (commands == null) {
                return false;
            }

            foreach (var command in commands) {
                if (NeedsConfirmation(command, prompt)) {
                    return true;
                }
            }

            return false;
        }

        public static bool NeedsConfirmation(AiCommand command, string prompt) {
            if (command == null) {
                return false;
            }

            if (IsForcePrompt(prompt)) {
                return false;
            }

            return IsHighRiskAction(command.Action) && !HasConfirmedFlag(command.Parameters);
        }

        public static bool IsHighRiskAction(string action) {
            return !string.IsNullOrWhiteSpace(action) && HighRiskActions.Contains(action);
        }

        private static bool IsForcePrompt(string prompt) {
            var normalized = Normalize(prompt);
            return normalized.Contains("#force", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("强制执行", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("忽略确认", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasConfirmedFlag(IDictionary<string, string> parameters) {
            if (parameters == null) {
                return false;
            }

            var key = parameters.Keys.FirstOrDefault(k => string.Equals(k, "confirmed", StringComparison.OrdinalIgnoreCase));
            if (key == null || !parameters.TryGetValue(key, out var raw)) {
                return false;
            }

            if (bool.TryParse(raw, out var boolValue)) {
                return boolValue;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue)) {
                return intValue != 0;
            }

            return false;
        }

        private static string Normalize(string text) {
            return text?.Trim().ToLowerInvariant() ?? string.Empty;
        }

        private static bool ContainsEnglishWord(string source, string word) {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(word)) {
                return false;
            }

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
