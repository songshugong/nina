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

namespace NINA.ViewModel.AI {

    public class AIAssistantVM : DockableVM, IAIAssistantVM {
        private const int MaxMessageCount = 300;
        private readonly IAsyncCommand sendPromptCommand;
        private readonly IAiCommandPlanner commandPlanner;
        private readonly IAiActionExecutor actionExecutor;
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
            AppendMessage("[system] Supported: connect, status, start_sequence, stop, park, unpark, platesolve, center, slew.");
            AppendMessage("[system] Input \"help\" to show command hints and JSON examples.");
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
            return !string.IsNullOrWhiteSpace(Prompt);
        }

        private async Task<bool> SendPromptAsync() {
            var userPrompt = Prompt?.Trim();
            if (string.IsNullOrWhiteSpace(userPrompt)) {
                return false;
            }

            AppendMessage("[user] " + userPrompt);
            var plan = await commandPlanner.PlanAsync(userPrompt) ?? new AiCommandPlan();
            var commands = plan.Commands ?? new List<AiCommand>();
            if (commands.Count == 0) {
                AppendMessage("[assistant] No command parsed.");
                return false;
            }

            AppendMessage("[planner] source=" + plan.Source);
            AppendMessage("[router] " + string.Join(", ", commands.Select(c => c.Action)));
            foreach (var command in commands) {
                var result = await actionExecutor.ExecuteAsync(command);
                var prefix = result.Success ? "[assistant][ok] " : "[assistant][fail] ";
                AppendMessage(prefix + result.Message);
            }

            Prompt = string.Empty;
            return true;
        }

        private void AppendMessage(string message) {
            if (Messages.Count >= MaxMessageCount) {
                Messages.RemoveAt(0);
            }
            Messages.Add(message);
        }
    }
}
