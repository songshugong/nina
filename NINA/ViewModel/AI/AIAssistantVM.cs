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
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System;

namespace NINA.ViewModel.AI {

    public class AIAssistantVM : DockableVM, IAIAssistantVM {
        private const int MaxMessageCount = 300;
        private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(90);
        private readonly IAsyncCommand sendPromptCommand;
        private readonly IAiCommandPlanner commandPlanner;
        private readonly IAiActionExecutor actionExecutor;
        private IList<AiCommand> pendingCommands = new List<AiCommand>();
        private string pendingSource = string.Empty;
        private DateTimeOffset pendingCreatedAtUtc;
        private bool isExecuting;
        private string prompt;

        public AIAssistantVM(
            IProfileService profileService,
            IAiCommandPlanner commandPlanner,
            IAiActionExecutor actionExecutor) : base(profileService) {
            this.commandPlanner = commandPlanner;
            this.actionExecutor = actionExecutor;

            Title = "AI Assistant";

            if (Application.Current?.Resources["BrainBulbSVG"] is GeometryGroup brainBulb) {
                ImageGeometry = brainBulb;
            }

            Messages = new ObservableCollection<string>();
            AppendMessage("[system] AI assistant initialized. Planner+executor mode is active.");
            AppendMessage("[system] Supported: connect, disconnect, status, start_sequence, stop, park, unpark, platesolve, center, slew, tracking_on/off, guide_start/stop, cool_camera, warm_camera.");
            AppendMessage("[system] Input \"help\" to show command hints and JSON examples.");
            AppendMessage("[system] High-risk actions require confirm/cancel (90s window).");
            AppendMessage("[system] Use \"pending\"/\"待确认\" to inspect queued high-risk actions.");
            AppendMessage("[system] While pending exists, new high-risk actions are ignored until confirm/cancel.");
            AppendMessage("[system] Override confirmation by adding #force in prompt or confirmed=true in JSON parameters.");
            AppendMessage("[system] Optional LLM bridge: set NINA_AI_API_URL (and optionally NINA_AI_API_KEY, NINA_AI_MODEL).");
            AppendMessage("[system] Audit log: %LocalAppData%/NINA/Logs/ai-assistant-audit.jsonl");

            sendPromptCommand = new AsyncCommand<bool>(SendPromptAsync, (o) => CanSendPrompt());
        }

        public override bool IsTool => true;

        public ObservableCollection<string> Messages { get; }

        public string Prompt {
            get => prompt;
            set {
                prompt = value;
                RaisePropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public ICommand SendPromptCommand => sendPromptCommand;

        private bool CanSendPrompt() {
            return !isExecuting && !string.IsNullOrWhiteSpace(Prompt);
        }

        private async Task<bool> SendPromptAsync() {
            var userPrompt = Prompt?.Trim();
            if (string.IsNullOrWhiteSpace(userPrompt)) {
                return false;
            }

            SetExecuting(true);
            try {
                if (await TryHandleControlPromptAsync(userPrompt)) {
                    Prompt = string.Empty;
                    return true;
                }

                ClearPendingIfExpired("Pending high-risk request expired. Issue command again if needed.");

                AppendMessage("[user] " + userPrompt);
                var plan = await commandPlanner.PlanAsync(userPrompt) ?? new AiCommandPlan();
                var commands = plan.Commands ?? new List<AiCommand>();
                if (commands.Count == 0) {
                    AppendMessage("[assistant] No command parsed.");
                    return false;
                }

                var hasExistingPending = pendingCommands.Count > 0;
                var queuedCommands = new List<AiCommand>();
                var immediateCommands = new List<AiCommand>();
                foreach (var command in commands) {
                    if (AiRiskControl.NeedsConfirmation(command, userPrompt)) {
                        queuedCommands.Add(command);
                    } else {
                        immediateCommands.Add(command);
                    }
                }

                var executedImmediately = false;
                if (immediateCommands.Count > 0) {
                    await ExecuteCommandsAsync(immediateCommands, plan.Source ?? string.Empty);
                    executedImmediately = true;
                }

                if (queuedCommands.Count > 0) {
                    if (hasExistingPending) {
                        var ignoredActions = string.Join(", ", queuedCommands.Select(c => c.Action));
                        AppendMessage("[assistant] Existing pending high-risk request kept. Resolve current pending first.");
                        AppendMessage("[assistant] New high-risk actions ignored: " + ignoredActions);
                        Prompt = string.Empty;
                        return true;
                    }

                    pendingCommands = queuedCommands;
                    pendingSource = plan.Source ?? string.Empty;
                    pendingCreatedAtUtc = DateTimeOffset.UtcNow;
                    var actions = string.Join(", ", queuedCommands.Select(c => c.Action));
                    AppendMessage("[planner] source=" + pendingSource);
                    AppendMessage("[router] " + actions);
                    if (executedImmediately) {
                        AppendMessage("[assistant] Non-high-risk actions were executed immediately.");
                    }
                    AppendMessage("[assistant][confirm] High-risk actions queued. Type \"confirm\"/\"确认\" within 90s to execute, \"pending\"/\"待确认\" to inspect, or \"cancel\"/\"取消\".");
                    Prompt = string.Empty;
                    return true;
                }

                if (!executedImmediately) {
                    if (hasExistingPending) {
                        AppendMessage("[assistant] No immediate command executed. Pending queue is unchanged.");
                        Prompt = string.Empty;
                        return true;
                    } else {
                        AppendMessage("[assistant] No executable command.");
                        return false;
                    }
                }

                Prompt = string.Empty;
                return true;
            } finally {
                SetExecuting(false);
            }
        }

        private async Task<bool> TryHandleControlPromptAsync(string promptText) {
            var action = AiRiskControl.ParseControlAction(promptText);
            if (action == AiControlAction.None) {
                return false;
            }

            AppendMessage("[user] " + promptText);

            if (action == AiControlAction.Pending) {
                if (ClearPendingIfExpired("Pending high-risk request expired. Please issue the command again.")) {
                    return true;
                }

                AppendMessage(pendingCommands.Count == 0
                    ? "[assistant] No pending high-risk action."
                    : "[assistant] " + BuildPendingStatusMessage());
                return true;
            }

            if (pendingCommands.Count == 0) {
                AppendMessage("[assistant] No pending high-risk action.");
                return true;
            }

            if (ClearPendingIfExpired("Pending high-risk request expired. Please issue the command again.")) {
                return true;
            }

            if (action == AiControlAction.Cancel) {
                ClearPending("Pending high-risk request cancelled.");
                return true;
            }

            var toExecute = pendingCommands.ToList();
            var source = pendingSource;
            pendingCommands = new List<AiCommand>();
            pendingSource = string.Empty;
            pendingCreatedAtUtc = default;

            AppendMessage("[assistant][confirm] Confirmation accepted. Executing queued actions.");
            await ExecuteCommandsAsync(toExecute, source);
            return true;
        }

        private string BuildPendingStatusMessage() {
            var actions = string.Join(", ", pendingCommands.Select(c => c.Action));
            var elapsed = DateTimeOffset.UtcNow - pendingCreatedAtUtc;
            var remaining = ConfirmationWindow - elapsed;
            var remainingSeconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
            return $"Pending actions: {actions}. Source={pendingSource}. Expires in {remainingSeconds}s.";
        }

        private async Task ExecuteCommandsAsync(IList<AiCommand> commands, string source) {
            AppendMessage("[planner] source=" + source);
            AppendMessage("[router] " + string.Join(", ", commands.Select(c => c.Action)));
            foreach (var command in commands) {
                var result = await actionExecutor.ExecuteAsync(command);
                var prefix = result.Success ? "[assistant][ok] " : "[assistant][fail] ";
                AppendMessage(prefix + result.Message);
            }
        }

        private void ClearPending(string reason) {
            pendingCommands = new List<AiCommand>();
            pendingSource = string.Empty;
            pendingCreatedAtUtc = default;
            AppendMessage("[assistant] " + reason);
        }

        private bool ClearPendingIfExpired(string reason) {
            if (pendingCommands.Count == 0) {
                return false;
            }

            if (DateTimeOffset.UtcNow - pendingCreatedAtUtc <= ConfirmationWindow) {
                return false;
            }

            ClearPending(reason);
            return true;
        }

        private void AppendMessage(string message) {
            if (Messages.Count >= MaxMessageCount) {
                Messages.RemoveAt(0);
            }
            Messages.Add(message);
        }

        private void SetExecuting(bool value) {
            if (isExecuting == value) {
                return;
            }

            isExecuting = value;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
