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
            "slew"
        };

        public static AiControlAction ParseControlAction(string prompt) {
            var normalized = Normalize(prompt);
            if (string.IsNullOrWhiteSpace(normalized)) {
                return AiControlAction.None;
            }

            if (normalized is "confirm" or "确认") {
                return AiControlAction.Confirm;
            }

            if (normalized is "cancel" or "取消") {
                return AiControlAction.Cancel;
            }

            if (normalized is "pending" or "待确认") {
                return AiControlAction.Pending;
            }

            return AiControlAction.None;
        }

        public static bool RequiresConfirmation(IEnumerable<AiCommand> commands, string prompt) {
            if (commands == null) {
                return false;
            }

            if (IsForcePrompt(prompt)) {
                return false;
            }

            foreach (var command in commands) {
                if (command == null) {
                    continue;
                }

                if (IsHighRiskAction(command.Action) && !HasConfirmedFlag(command.Parameters)) {
                    return true;
                }
            }

            return false;
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
    }
}
