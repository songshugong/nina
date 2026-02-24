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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System;

namespace NINA.ViewModel.AI {

    public class AIAssistantVM : DockableVM, IAIAssistantVM {
        private const int MaxMessageCount = 300;
        private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan AutonomyMonitorInterval = TimeSpan.FromSeconds(15);
        private const int SoftUnsafeThreshold = 3;
        private const int SoftRecoveryThreshold = 4;
        private const int HardRecoveryThreshold = 8;
        private static readonly string[] SupportedActions = {
            "connect",
            "disconnect",
            "status",
            "start_sequence",
            "stop",
            "park",
            "unpark",
            "home_mount",
            "platesolve",
            "center",
            "slew",
            "tracking_on",
            "tracking_off",
            "guide_start",
            "guide_stop",
            "cool_camera",
            "warm_camera",
            "dome_open",
            "dome_close",
            "dome_follow_on",
            "dome_follow_off",
            "dome_park",
            "dome_home",
            "flat_light_on",
            "flat_light_off"
        };

        private static readonly string[] HighRiskActions = {
            "start_sequence",
            "park",
            "unpark",
            "home_mount",
            "slew",
            "tracking_off",
            "dome_open",
            "dome_close",
            "dome_park"
        };

        private enum AutonomyMode {
            Disabled,
            Monitoring,
            SoftStopped,
            HardStopped
        }

        private readonly IAsyncCommand sendPromptCommand;
        private readonly IAsyncCommand runQuickToolCommand;
        private readonly IAiCommandPlanner commandPlanner;
        private readonly IAiRuntimeContextProvider runtimeContextProvider;
        private readonly IAiActionExecutor actionExecutor;
        private readonly DispatcherTimer autonomyMonitorTimer;
        private IList<AiCommand> pendingCommands = new List<AiCommand>();
        private string pendingSource = string.Empty;
        private DateTimeOffset pendingCreatedAtUtc;
        private bool isExecuting;
        private bool isAutonomyTicking;
        private AutonomyMode autonomyMode = AutonomyMode.Disabled;
        private int unsafeStreak;
        private int safeStreak;
        private string prompt;
        private string pendingSummary;
        private string lastExecutionSummary;
        private string lastUpdated;
        private string autonomyStatus;
        private string autonomyLastReason;

        public AIAssistantVM(
            IProfileService profileService,
            IAiCommandPlanner commandPlanner,
            IAiRuntimeContextProvider runtimeContextProvider,
            IAiActionExecutor actionExecutor) : base(profileService) {
            this.commandPlanner = commandPlanner;
            this.runtimeContextProvider = runtimeContextProvider;
            this.actionExecutor = actionExecutor;

            Title = "AI Assistant";

            if (Application.Current?.Resources["BrainBulbSVG"] is GeometryGroup brainBulb) {
                ImageGeometry = brainBulb;
            }

            ProjectPhase = "Local planner+executor is active. Observatory controls expanded to mount, dome and flat panel actions.";
            CapabilitySummary = "Rule routing + optional LLM translation + risk confirmation queue + audit logging.";
            AuditLogPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA",
                "Logs",
                "ai-assistant-audit.jsonl");
            pendingSummary = "No pending high-risk action.";
            lastExecutionSummary = "No command executed yet.";
            lastUpdated = DateTimeOffset.Now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            autonomyStatus = "Disabled";
            autonomyLastReason = "Autonomy monitor is stopped.";
            UpdateAutonomyStatus("Autonomy monitor is stopped.");

            Messages = new ObservableCollection<string>();
            AppendMessage("[system] AI assistant initialized. Planner+executor mode is active.");
            AppendMessage("[system] Supported: connect, disconnect, status, start_sequence, stop, park, unpark, home_mount, platesolve, center, slew, tracking_on/off, guide_start/stop, cool_camera, warm_camera, dome_open/close, dome_follow_on/off, dome_park/home, flat_light_on/off.");
            AppendMessage("[system] Input \"help\" to show command hints and JSON examples.");
            AppendMessage("[system] Autonomy monitor available: start_monitor / stop_monitor / monitor_status.");
            AppendMessage("[system] High-risk actions require confirm/cancel (90s window).");
            AppendMessage("[system] Use \"pending\"/\"待确认\" to inspect queued high-risk actions.");
            AppendMessage("[system] While pending exists, new high-risk actions are ignored until confirm/cancel.");
            AppendMessage("[system] Override confirmation by adding #force in prompt or confirmed=true in JSON parameters.");
            AppendMessage("[system] Optional LLM bridge: set NINA_AI_API_URL (and optionally NINA_AI_API_KEY, NINA_AI_MODEL).");
            AppendMessage("[system] Audit log: " + AuditLogPath);

            sendPromptCommand = new AsyncCommand<bool>(SendPromptAsync, (o) => CanSendPrompt());
            runQuickToolCommand = new AsyncCommand<bool>(RunQuickToolAsync, (o) => !isExecuting);
            autonomyMonitorTimer = new DispatcherTimer {
                Interval = AutonomyMonitorInterval
            };
            autonomyMonitorTimer.Tick += AutonomyMonitorTimerOnTick;
        }

        public override bool IsTool => true;

        public ObservableCollection<string> Messages { get; }
        public string ProjectPhase { get; }
        public string CapabilitySummary { get; }
        public string AuditLogPath { get; }
        public int SupportedActionCount => SupportedActions.Length;
        public int HighRiskActionCount => HighRiskActions.Length;
        public int PendingActionCount => pendingCommands.Count;
        public string PendingSummary {
            get => pendingSummary;
            private set {
                if (pendingSummary == value) {
                    return;
                }
                pendingSummary = value;
                RaisePropertyChanged();
            }
        }
        public string LastExecutionSummary {
            get => lastExecutionSummary;
            private set {
                if (lastExecutionSummary == value) {
                    return;
                }
                lastExecutionSummary = value;
                RaisePropertyChanged();
            }
        }
        public string LastUpdated {
            get => lastUpdated;
            private set {
                if (lastUpdated == value) {
                    return;
                }
                lastUpdated = value;
                RaisePropertyChanged();
            }
        }
        public string AutonomyStatus {
            get => autonomyStatus;
            private set {
                if (autonomyStatus == value) {
                    return;
                }
                autonomyStatus = value;
                RaisePropertyChanged();
            }
        }
        public string AutonomyLastReason {
            get => autonomyLastReason;
            private set {
                if (autonomyLastReason == value) {
                    return;
                }
                autonomyLastReason = value;
                RaisePropertyChanged();
            }
        }
        public int AutonomyIntervalSeconds => (int)AutonomyMonitorInterval.TotalSeconds;

        public string Prompt {
            get => prompt;
            set {
                prompt = value;
                RaisePropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public ICommand SendPromptCommand => sendPromptCommand;
        public ICommand RunQuickToolCommand => runQuickToolCommand;

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
                var runtimeContext = runtimeContextProvider?.GetRuntimeContextSummary();
                var plan = await commandPlanner.PlanAsync(userPrompt, runtimeContext) ?? new AiCommandPlan();
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
                        RefreshDashboard("Ignored new high-risk actions while another pending queue exists.");
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
                    RefreshDashboard("Queued high-risk actions and waiting for confirmation.");
                    Prompt = string.Empty;
                    return true;
                }

                if (!executedImmediately) {
                    if (hasExistingPending) {
                        AppendMessage("[assistant] No immediate command executed. Pending queue is unchanged.");
                        RefreshDashboard("No immediate command executed; pending queue unchanged.");
                        Prompt = string.Empty;
                        return true;
                    } else {
                        AppendMessage("[assistant] No executable command.");
                        RefreshDashboard("No executable command was generated.");
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
            if (IsAutonomyStartPrompt(promptText)) {
                AppendMessage("[user] " + promptText);
                if (StartAutonomyMonitor("Started by user command.")) {
                    AppendMessage("[assistant] Autonomy monitor started.");
                } else {
                    AppendMessage("[assistant] Autonomy monitor is already running.");
                }
                return true;
            }

            if (IsAutonomyStopPrompt(promptText)) {
                AppendMessage("[user] " + promptText);
                if (StopAutonomyMonitor("Stopped by user command.")) {
                    AppendMessage("[assistant] Autonomy monitor stopped.");
                } else {
                    AppendMessage("[assistant] Autonomy monitor is already stopped.");
                }
                return true;
            }

            if (IsAutonomyStatusPrompt(promptText)) {
                AppendMessage("[user] " + promptText);
                AppendMessage("[assistant] " + BuildAutonomyStatusMessage());
                RefreshDashboard("Displayed autonomy monitor status.");
                return true;
            }

            if (IsProjectOverviewPrompt(promptText)) {
                AppendMessage("[user] " + promptText);
                AppendMessage("[assistant] " + BuildProjectOverviewMessage());
                RefreshDashboard("Displayed project overview.");
                return true;
            }

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
                RefreshDashboard("Displayed pending high-risk queue.");
                return true;
            }

            if (pendingCommands.Count == 0) {
                AppendMessage("[assistant] No pending high-risk action.");
                RefreshDashboard("Pending control requested, but queue is empty.");
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
                RefreshDashboard($"{command.Action}: {result.Message}");
            }
        }

        private void ClearPending(string reason) {
            pendingCommands = new List<AiCommand>();
            pendingSource = string.Empty;
            pendingCreatedAtUtc = default;
            AppendMessage("[assistant] " + reason);
            RefreshDashboard(reason);
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

        private async Task<bool> RunQuickToolAsync(object parameter) {
            var tool = parameter?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
            switch (tool) {
                case "project_overview":
                    AppendMessage("[tool] project_overview");
                    AppendMessage("[assistant] " + BuildProjectOverviewMessage());
                    RefreshDashboard("Displayed project overview.");
                    return true;
                case "status":
                case "pending":
                case "help":
                    Prompt = tool;
                    return await SendPromptAsync();
                case "monitor_start":
                    AppendMessage("[tool] monitor_start");
                    if (StartAutonomyMonitor("Started from quick tool.")) {
                        AppendMessage("[assistant] Autonomy monitor started.");
                    } else {
                        AppendMessage("[assistant] Autonomy monitor is already running.");
                    }
                    return true;
                case "monitor_stop":
                    AppendMessage("[tool] monitor_stop");
                    if (StopAutonomyMonitor("Stopped from quick tool.")) {
                        AppendMessage("[assistant] Autonomy monitor stopped.");
                    } else {
                        AppendMessage("[assistant] Autonomy monitor is already stopped.");
                    }
                    return true;
                case "monitor_status":
                    AppendMessage("[tool] monitor_status");
                    AppendMessage("[assistant] " + BuildAutonomyStatusMessage());
                    RefreshDashboard("Displayed autonomy monitor status.");
                    return true;
                default:
                    return false;
            }
        }

        private string BuildProjectOverviewMessage() {
            var pending = pendingCommands.Count == 0 ? "none" : string.Join(", ", pendingCommands.Select(c => c.Action));
            return $"Project phase: {ProjectPhase} Capabilities: {CapabilitySummary} Supported actions={SupportedActionCount}, high-risk actions={HighRiskActionCount}, pending={pending}. Autonomy={AutonomyStatus}. Last result: {LastExecutionSummary}.";
        }

        private static bool IsProjectOverviewPrompt(string promptText) {
            if (string.IsNullOrWhiteSpace(promptText)) {
                return false;
            }

            var normalized = promptText.Trim().ToLowerInvariant();
            if (normalized == "project" || normalized == "project progress" || normalized == "project status") {
                return true;
            }

            return normalized.Contains("项目进度", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("项目状态", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("当前进度", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAutonomyStartPrompt(string promptText) {
            if (string.IsNullOrWhiteSpace(promptText)) {
                return false;
            }

            var normalized = promptText.Trim().ToLowerInvariant();
            return normalized == "start_monitor"
                   || normalized == "monitor_start"
                   || normalized == "autonomy on"
                   || normalized == "start autonomy"
                   || normalized.Contains("开启监控", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("启动监控", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("自动监控开启", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAutonomyStopPrompt(string promptText) {
            if (string.IsNullOrWhiteSpace(promptText)) {
                return false;
            }

            var normalized = promptText.Trim().ToLowerInvariant();
            return normalized == "stop_monitor"
                   || normalized == "monitor_stop"
                   || normalized == "autonomy off"
                   || normalized == "stop autonomy"
                   || normalized.Contains("关闭监控", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("停止监控", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("自动监控关闭", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAutonomyStatusPrompt(string promptText) {
            if (string.IsNullOrWhiteSpace(promptText)) {
                return false;
            }

            var normalized = promptText.Trim().ToLowerInvariant();
            return normalized == "monitor_status"
                   || normalized == "status_monitor"
                   || normalized == "monitor status"
                   || normalized.Contains("监控状态", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("自动监控状态", StringComparison.OrdinalIgnoreCase);
        }

        private string BuildAutonomyStatusMessage() {
            return $"Autonomy status: {AutonomyStatus}. Last reason: {AutonomyLastReason}.";
        }

        private bool StartAutonomyMonitor(string reason) {
            if (autonomyMode != AutonomyMode.Disabled) {
                return false;
            }

            unsafeStreak = 0;
            safeStreak = 0;
            autonomyMode = AutonomyMode.Monitoring;
            UpdateAutonomyStatus(reason);
            autonomyMonitorTimer.Start();
            AutonomyMonitorTimerOnTick(this, EventArgs.Empty);
            RefreshDashboard("Autonomy monitor started.");
            return true;
        }

        private bool StopAutonomyMonitor(string reason) {
            if (autonomyMode == AutonomyMode.Disabled) {
                return false;
            }

            autonomyMonitorTimer.Stop();
            unsafeStreak = 0;
            safeStreak = 0;
            autonomyMode = AutonomyMode.Disabled;
            UpdateAutonomyStatus(reason);
            RefreshDashboard("Autonomy monitor stopped.");
            return true;
        }

        private async void AutonomyMonitorTimerOnTick(object sender, EventArgs e) {
            if (autonomyMode == AutonomyMode.Disabled || isAutonomyTicking) {
                return;
            }

            isAutonomyTicking = true;
            try {
                await EvaluateAutonomyAsync();
            } catch (Exception ex) {
                Logger.Error(ex);
                UpdateAutonomyStatus("Autonomy monitor tick failed: " + ex.Message);
                RefreshDashboard("Autonomy monitor tick failed.");
            } finally {
                isAutonomyTicking = false;
            }
        }

        private async Task EvaluateAutonomyAsync() {
            if (autonomyMode == AutonomyMode.Disabled) {
                return;
            }

            var snapshot = runtimeContextProvider?.GetSnapshot();
            if (snapshot == null) {
                unsafeStreak++;
                safeStreak = 0;
                UpdateAutonomyStatus("No runtime snapshot available.");
                return;
            }

            var hardReasons = BuildHardUnsafeReasons(snapshot);
            var softReasons = BuildSoftUnsafeReasons(snapshot);
            var isHardUnsafe = hardReasons.Count > 0;
            var isSoftUnsafe = softReasons.Count > 0;
            var isUnsafe = isHardUnsafe || isSoftUnsafe;

            if (isUnsafe) {
                unsafeStreak++;
                safeStreak = 0;
            } else {
                safeStreak++;
                unsafeStreak = 0;
            }

            if (autonomyMode == AutonomyMode.Monitoring) {
                if (isHardUnsafe) {
                    await ApplyHardStopAsync(snapshot, hardReasons);
                    return;
                }

                if (isSoftUnsafe && unsafeStreak >= SoftUnsafeThreshold && snapshot.SequencerRunning) {
                    await ApplySoftStopAsync(softReasons);
                    return;
                }
            }

            if (autonomyMode == AutonomyMode.SoftStopped) {
                if (isHardUnsafe) {
                    await ApplyHardStopAsync(snapshot, hardReasons);
                    return;
                }

                if (!isUnsafe && safeStreak >= SoftRecoveryThreshold) {
                    await TryResumeFromSoftStopAsync(snapshot);
                    return;
                }
            }

            if (autonomyMode == AutonomyMode.HardStopped) {
                if (!isUnsafe && safeStreak >= HardRecoveryThreshold) {
                    await TryRecoverFromHardStopAsync(snapshot);
                    return;
                }
            }

            UpdateAutonomyStatus();
        }

        private async Task ApplySoftStopAsync(IList<string> softReasons) {
            var reason = "Soft stop triggered: " + string.Join("; ", softReasons);
            await ExecuteAutonomyActionAsync("stop", reason);
            autonomyMode = AutonomyMode.SoftStopped;
            UpdateAutonomyStatus(reason);
            RefreshDashboard(reason);
        }

        private async Task ApplyHardStopAsync(AiRuntimeSnapshot snapshot, IList<string> hardReasons) {
            var reason = "Hard stop triggered: " + string.Join("; ", hardReasons);
            await ExecuteAutonomyActionAsync("stop", reason);
            if (snapshot.GuiderConnected) {
                await ExecuteAutonomyActionAsync("guide_stop", "Hard stop guider cleanup.");
            }

            if (snapshot.DomeConnected && snapshot.DomeCanSetShutter && !snapshot.DomeShutterClosedLike) {
                await ExecuteAutonomyActionAsync("dome_close", "Hard stop requires dome closure.");
            }

            if (snapshot.MountConnected && !snapshot.MountAtPark) {
                await ExecuteAutonomyActionAsync("park", "Hard stop requires mount parking.");
            }

            autonomyMode = AutonomyMode.HardStopped;
            UpdateAutonomyStatus(reason);
            RefreshDashboard(reason);
        }

        private async Task TryResumeFromSoftStopAsync(AiRuntimeSnapshot snapshot) {
            if (!CanResumeSequence(snapshot, out var blockReason)) {
                UpdateAutonomyStatus("Soft-stop recovery blocked: " + blockReason);
                return;
            }

            var resumed = await ExecuteAutonomyActionAsync(
                "start_sequence",
                "Soft-stop recovery: restarting sequence.",
                new Dictionary<string, string> {
                    ["skip_validation"] = "false"
                });

            if (resumed) {
                autonomyMode = AutonomyMode.Monitoring;
                UpdateAutonomyStatus("Soft-stop recovery succeeded. Sequence restarted.");
                RefreshDashboard("Autonomy resumed sequence after soft stop.");
            } else {
                UpdateAutonomyStatus("Soft-stop recovery attempt failed.");
            }
        }

        private async Task TryRecoverFromHardStopAsync(AiRuntimeSnapshot snapshot) {
            var reasonPrefix = "Hard-stop recovery:";
            var current = snapshot;

            if (current.MountConnected && current.MountAtPark) {
                var unparked = await ExecuteAutonomyActionAsync("unpark", reasonPrefix + " unpark mount.");
                if (!unparked) {
                    UpdateAutonomyStatus("Hard-stop recovery failed while unparking mount.");
                    return;
                }
                current = runtimeContextProvider?.GetSnapshot() ?? current;
            }

            if (current.DomeConnected && current.DomeCanSetShutter && current.DomeShutterClosedLike) {
                var opened = await ExecuteAutonomyActionAsync("dome_open", reasonPrefix + " open dome shutter.");
                if (!opened) {
                    UpdateAutonomyStatus("Hard-stop recovery failed while opening dome.");
                    return;
                }
                current = runtimeContextProvider?.GetSnapshot() ?? current;
            }

            if (!CanResumeSequence(current, out var blockReason)) {
                UpdateAutonomyStatus("Hard-stop recovery blocked: " + blockReason);
                return;
            }

            var resumed = await ExecuteAutonomyActionAsync(
                "start_sequence",
                reasonPrefix + " restart sequence.",
                new Dictionary<string, string> {
                    ["skip_validation"] = "false"
                });

            if (resumed) {
                autonomyMode = AutonomyMode.Monitoring;
                UpdateAutonomyStatus("Hard-stop recovery succeeded. Sequence restarted.");
                RefreshDashboard("Autonomy recovered from hard stop and restarted sequence.");
            } else {
                UpdateAutonomyStatus("Hard-stop recovery attempt failed during sequence restart.");
            }
        }

        private static bool CanResumeSequence(AiRuntimeSnapshot snapshot, out string reason) {
            if (snapshot == null) {
                reason = "runtime snapshot unavailable";
                return false;
            }
            if (!snapshot.SequencerInitialized) {
                reason = "sequencer not initialized";
                return false;
            }
            if (!snapshot.CameraConnected) {
                reason = "camera not connected";
                return false;
            }
            if (!snapshot.MountConnected) {
                reason = "mount not connected";
                return false;
            }
            if (snapshot.MountAtPark) {
                reason = "mount is parked";
                return false;
            }
            if (snapshot.SafetyMonitorConnected && !snapshot.SafetyMonitorIsSafe) {
                reason = "safety monitor reports unsafe";
                return false;
            }
            if (snapshot.DomeConnected && snapshot.DomeShutterClosedLike) {
                reason = "dome shutter is closed/closing/error";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static IList<string> BuildHardUnsafeReasons(AiRuntimeSnapshot snapshot) {
            var reasons = new List<string>();
            if (snapshot == null) {
                reasons.Add("runtime snapshot unavailable");
                return reasons;
            }

            if (snapshot.SafetyMonitorConnected && !snapshot.SafetyMonitorIsSafe) {
                reasons.Add("safety monitor reports unsafe");
            }

            if (snapshot.WeatherConnected && snapshot.RainRate.HasValue && snapshot.RainRate.Value > 0.02d) {
                reasons.Add($"rain rate={snapshot.RainRate.Value:0.###}");
            }

            if (snapshot.SequencerRunning && snapshot.DomeConnected && snapshot.DomeShutterClosedLike) {
                reasons.Add("dome shutter is not open while sequencing");
            }

            return reasons;
        }

        private static IList<string> BuildSoftUnsafeReasons(AiRuntimeSnapshot snapshot) {
            var reasons = new List<string>();
            if (snapshot == null) {
                return reasons;
            }

            if (snapshot.WeatherConnected && snapshot.CloudCover.HasValue && snapshot.CloudCover.Value >= 80d) {
                reasons.Add($"cloud cover={snapshot.CloudCover.Value:0.#}%");
            }

            if (snapshot.WeatherConnected && snapshot.Humidity.HasValue && snapshot.Humidity.Value >= 93d) {
                reasons.Add($"humidity={snapshot.Humidity.Value:0.#}%");
            }

            if (snapshot.SequencerRunning && !snapshot.GuiderConnected) {
                reasons.Add("guider disconnected during sequence");
            }

            return reasons;
        }

        private async Task<bool> ExecuteAutonomyActionAsync(string action, string reason, IDictionary<string, string> parameters = null) {
            var command = new AiCommand {
                Action = action,
                Parameters = parameters ?? new Dictionary<string, string>(),
                RawPrompt = "[autonomy] " + reason,
                RoutingSource = "autonomy"
            };

            var result = await actionExecutor.ExecuteAsync(command);
            var prefix = result.Success ? "[autonomy][ok] " : "[autonomy][fail] ";
            AppendMessage(prefix + action + ": " + result.Message);
            RefreshDashboard("Autonomy " + action + ": " + result.Message);
            return result.Success;
        }

        private void UpdateAutonomyStatus(string reason = null) {
            AutonomyStatus = $"{autonomyMode} | tick={AutonomyIntervalSeconds}s | unsafe={unsafeStreak} safe={safeStreak}";
            if (!string.IsNullOrWhiteSpace(reason)) {
                AutonomyLastReason = reason;
            }
        }

        private void RefreshDashboard(string lastExecution) {
            if (!string.IsNullOrWhiteSpace(lastExecution)) {
                LastExecutionSummary = lastExecution;
            }

            PendingSummary = pendingCommands.Count == 0
                ? "No pending high-risk action."
                : BuildPendingStatusMessage();
            UpdateAutonomyStatus();
            LastUpdated = DateTimeOffset.Now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            RaisePropertyChanged(nameof(PendingActionCount));
        }
    }
}
