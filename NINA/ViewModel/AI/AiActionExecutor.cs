#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.ViewModel.AI {

    public class AiActionExecutor : IAiActionExecutor {
        private readonly ICameraMediator cameraMediator;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IFilterWheelMediator filterWheelMediator;
        private readonly IFocuserMediator focuserMediator;
        private readonly IRotatorMediator rotatorMediator;
        private readonly IFlatDeviceMediator flatDeviceMediator;
        private readonly IGuiderMediator guiderMediator;
        private readonly ISwitchMediator switchMediator;
        private readonly IWeatherDataMediator weatherDataMediator;
        private readonly IDomeMediator domeMediator;
        private readonly ISafetyMonitorMediator safetyMonitorMediator;
        private readonly ISequenceMediator sequenceMediator;
        private readonly IFramingAssistantVM framingAssistantVM;
        private readonly IAnchorablePlateSolverVM plateSolverVM;
        private readonly IAiAuditLogWriter auditLogWriter;

        public AiActionExecutor(
            ICameraMediator cameraMediator,
            ITelescopeMediator telescopeMediator,
            IFilterWheelMediator filterWheelMediator,
            IFocuserMediator focuserMediator,
            IRotatorMediator rotatorMediator,
            IFlatDeviceMediator flatDeviceMediator,
            IGuiderMediator guiderMediator,
            ISwitchMediator switchMediator,
            IWeatherDataMediator weatherDataMediator,
            IDomeMediator domeMediator,
            ISafetyMonitorMediator safetyMonitorMediator,
            ISequenceMediator sequenceMediator,
            IFramingAssistantVM framingAssistantVM,
            IAnchorablePlateSolverVM plateSolverVM,
            IAiAuditLogWriter auditLogWriter) {
            this.cameraMediator = cameraMediator;
            this.telescopeMediator = telescopeMediator;
            this.filterWheelMediator = filterWheelMediator;
            this.focuserMediator = focuserMediator;
            this.rotatorMediator = rotatorMediator;
            this.flatDeviceMediator = flatDeviceMediator;
            this.guiderMediator = guiderMediator;
            this.switchMediator = switchMediator;
            this.weatherDataMediator = weatherDataMediator;
            this.domeMediator = domeMediator;
            this.safetyMonitorMediator = safetyMonitorMediator;
            this.sequenceMediator = sequenceMediator;
            this.framingAssistantVM = framingAssistantVM;
            this.plateSolverVM = plateSolverVM;
            this.auditLogWriter = auditLogWriter;
        }

        public async Task<AiExecutionResult> ExecuteAsync(AiCommand command) {
            var sw = Stopwatch.StartNew();
            var commandId = Guid.NewGuid().ToString("N");
            var action = command?.Action ?? string.Empty;
            var routingSource = command?.RoutingSource ?? string.Empty;
            var parameters = command?.Parameters != null
                ? new Dictionary<string, string>(command.Parameters)
                : new Dictionary<string, string>();
            var rawPrompt = command?.RawPrompt ?? string.Empty;

            AiExecutionResult result;
            if (command == null || string.IsNullOrWhiteSpace(command.Action)) {
                result = Fail("Invalid command");
                await WriteAudit(commandId, action, routingSource, parameters, rawPrompt, result, sw.ElapsedMilliseconds);
                return result;
            }

            try {
                switch (command.Action.ToLowerInvariant()) {
                    case "help":
                        result = Ok("Supported: connect, disconnect, status, start_sequence, stop, park, unpark, platesolve, center, slew, tracking_on, tracking_off, guide_start, guide_stop, cool_camera, warm_camera. Control: confirm, cancel, pending. JSON example: {\"action\":\"slew\",\"parameters\":{\"ra\":\"5.5\",\"dec\":\"-2.1\",\"ra_unit\":\"hours\",\"confirmed\":\"true\"}}. Slew ranges: ra_unit=hours => ra [0,24); ra_unit=deg => ra [0,360); dec [-90,90]. High-risk actions require confirm/cancel unless overridden; while pending exists, new high-risk actions are ignored.");
                        break;

                    case "connect":
                        result = await ConnectAllAsync();
                        break;

                    case "disconnect":
                        result = await DisconnectAllAsync();
                        break;

                    case "status":
                        result = Ok(BuildStatusSummary());
                        break;

                    case "start_sequence":
                        var startCheck = CheckStartSequenceGuards();
                        if (startCheck != null) {
                            result = startCheck;
                            break;
                        }
                        var skipValidation = GetBool(command.Parameters, "skip_validation", false);
                        await sequenceMediator.StartAdvancedSequence(skipValidation);
                        result = Ok("Advanced sequence start requested.");
                        break;

                    case "stop":
                        if (IsSequencerInitializedSafe() && sequenceMediator.IsAdvancedSequenceRunning()) {
                            sequenceMediator.CancelAdvancedSequence();
                        }
                        telescopeMediator.StopSlew();
                        result = Ok("Stop requested: sequence cancelled and mount slew stopped.");
                        break;

                    case "park":
                        if (!telescopeMediator.GetInfo().Connected) {
                            result = Fail("Mount is not connected.");
                            break;
                        }
                        if (IsSequencerInitializedSafe() && sequenceMediator.IsAdvancedSequenceRunning()) {
                            result = Fail("Sequence is running. Stop sequence before parking.");
                            break;
                        }
                        var parked = await telescopeMediator.ParkTelescope(new Progress<ApplicationStatus>(), CancellationToken.None);
                        result = parked ? Ok("Park completed.") : Fail("Park failed.");
                        break;

                    case "unpark":
                        if (!telescopeMediator.GetInfo().Connected) {
                            result = Fail("Mount is not connected.");
                            break;
                        }
                        var unparked = await telescopeMediator.UnparkTelescope(new Progress<ApplicationStatus>(), CancellationToken.None);
                        result = unparked ? Ok("Unpark completed.") : Fail("Unpark failed.");
                        break;

                    case "platesolve":
                        if (!cameraMediator.GetInfo().Connected) {
                            result = Fail("Camera is not connected.");
                            break;
                        }
                        result = await ExecuteAsyncCommand(plateSolverVM.SolveCommand, null, "Plate solve");
                        break;

                    case "center":
                        if (!framingAssistantVM.RectangleCalculated) {
                            result = Fail("No framing target loaded. Load target first, then run center.");
                            break;
                        }
                        result = await ExecuteAsyncCommand(framingAssistantVM.SlewToCoordinatesCommand, "Center", "Center target");
                        break;

                    case "slew":
                        if (AiCoordinateParser.HasCoordinateParameters(command.Parameters)) {
                            if (!AiCoordinateParser.TryParse(command.Parameters, out var parsedCoordinates, out var coordinateError)) {
                                result = Fail(coordinateError);
                                break;
                            }

                            if (!telescopeMediator.GetInfo().Connected) {
                                result = Fail("Mount is not connected.");
                                break;
                            }

                            var slewOk = await telescopeMediator.SlewToCoordinatesAsync(parsedCoordinates, CancellationToken.None);
                            result = slewOk
                                ? Ok($"Slew requested to RA {parsedCoordinates.RAString}, Dec {parsedCoordinates.DecString}.")
                                : Fail("Slew failed.");
                            break;
                        }
                        result = await ExecuteAsyncCommand(framingAssistantVM.SlewToCoordinatesCommand, "Slew", "Slew to target");
                        break;

                    case "tracking_on":
                        if (!telescopeMediator.GetInfo().Connected) {
                            result = Fail("Mount is not connected.");
                            break;
                        }
                        result = telescopeMediator.SetTrackingEnabled(true)
                            ? Ok("Mount tracking enabled.")
                            : Fail("Failed to enable mount tracking.");
                        break;

                    case "tracking_off":
                        if (!telescopeMediator.GetInfo().Connected) {
                            result = Fail("Mount is not connected.");
                            break;
                        }
                        result = telescopeMediator.SetTrackingEnabled(false)
                            ? Ok("Mount tracking disabled.")
                            : Fail("Failed to disable mount tracking.");
                        break;

                    case "guide_start":
                        if (!guiderMediator.GetInfo().Connected) {
                            result = Fail("Guider is not connected.");
                            break;
                        }
                        var forceCalibration = GetBool(command.Parameters, "force_calibration", false);
                        var guideStartOk = await guiderMediator.StartGuiding(forceCalibration, new Progress<ApplicationStatus>(), CancellationToken.None);
                        result = guideStartOk ? Ok("Guiding started.") : Fail("Failed to start guiding.");
                        break;

                    case "guide_stop":
                        if (!guiderMediator.GetInfo().Connected) {
                            result = Fail("Guider is not connected.");
                            break;
                        }
                        var guideStopOk = await guiderMediator.StopGuiding(CancellationToken.None);
                        result = guideStopOk ? Ok("Guiding stopped.") : Fail("Failed to stop guiding.");
                        break;

                    case "cool_camera":
                        if (!cameraMediator.GetInfo().Connected) {
                            result = Fail("Camera is not connected.");
                            break;
                        }
                        if (!cameraMediator.GetInfo().CanSetTemperature) {
                            result = Fail("Camera does not support temperature control.");
                            break;
                        }
                        var targetTemperature = GetDouble(command.Parameters, "temperature")
                            ?? GetDouble(command.Parameters, "temp");
                        if (!targetTemperature.HasValue) {
                            result = Fail("cool_camera requires temperature parameter.");
                            break;
                        }
                        var coolDurationMinutes = GetDouble(command.Parameters, "duration_min") ?? 0d;
                        var coolDuration = TimeSpan.FromMinutes(Math.Max(0d, coolDurationMinutes));
                        var coolOk = await cameraMediator.CoolCamera(targetTemperature.Value, coolDuration, new Progress<ApplicationStatus>(), CancellationToken.None);
                        result = coolOk
                            ? Ok($"Camera cooling requested: target {targetTemperature.Value.ToString(CultureInfo.InvariantCulture)}C, duration {coolDuration.TotalMinutes:0.#} min.")
                            : Fail("Failed to cool camera.");
                        break;

                    case "warm_camera":
                        if (!cameraMediator.GetInfo().Connected) {
                            result = Fail("Camera is not connected.");
                            break;
                        }
                        if (!cameraMediator.GetInfo().CanSetTemperature) {
                            result = Fail("Camera does not support temperature control.");
                            break;
                        }
                        var warmDurationMinutes = GetDouble(command.Parameters, "duration_min") ?? 0d;
                        var warmDuration = TimeSpan.FromMinutes(Math.Max(0d, warmDurationMinutes));
                        var warmOk = await cameraMediator.WarmCamera(warmDuration, new Progress<ApplicationStatus>(), CancellationToken.None);
                        result = warmOk
                            ? Ok($"Camera warming requested for {warmDuration.TotalMinutes:0.#} min.")
                            : Fail("Failed to warm camera.");
                        break;

                    default:
                        result = Fail("Unknown action. Try \"help\".");
                        break;
                }
            } catch (Exception ex) {
                Logger.Error(ex);
                result = Fail($"Execution error: {ex.Message}");
            }

            await WriteAudit(commandId, action, routingSource, parameters, rawPrompt, result, sw.ElapsedMilliseconds);
            return result;
        }

        private async Task<AiExecutionResult> ConnectAllAsync() {
            var failures = new List<string>();
            var connected = 0;

            connected += await ConnectDevice("camera", cameraMediator.GetInfo().Connected, () => cameraMediator.Connect(), failures);
            connected += await ConnectDevice("mount", telescopeMediator.GetInfo().Connected, () => telescopeMediator.Connect(), failures);
            connected += await ConnectDevice("filter wheel", filterWheelMediator.GetInfo().Connected, () => filterWheelMediator.Connect(), failures);
            connected += await ConnectDevice("focuser", focuserMediator.GetInfo().Connected, () => focuserMediator.Connect(), failures);
            connected += await ConnectDevice("rotator", rotatorMediator.GetInfo().Connected, () => rotatorMediator.Connect(), failures);
            connected += await ConnectDevice("flat device", flatDeviceMediator.GetInfo().Connected, () => flatDeviceMediator.Connect(), failures);
            connected += await ConnectDevice("guider", guiderMediator.GetInfo().Connected, () => guiderMediator.Connect(), failures);
            connected += await ConnectDevice("switch hub", switchMediator.GetInfo().Connected, () => switchMediator.Connect(), failures);
            connected += await ConnectDevice("weather", weatherDataMediator.GetInfo().Connected, () => weatherDataMediator.Connect(), failures);
            connected += await ConnectDevice("dome", domeMediator.GetInfo().Connected, () => domeMediator.Connect(), failures);
            connected += await ConnectDevice("safety monitor", safetyMonitorMediator.GetInfo().Connected, () => safetyMonitorMediator.Connect(), failures);

            if (failures.Count > 0) {
                return Fail($"Connected {connected} devices, failures: {string.Join(", ", failures)}");
            }

            return Ok($"Connect-all finished. Newly connected: {connected}.");
        }

        private async Task<AiExecutionResult> DisconnectAllAsync() {
            var failures = new List<string>();
            var disconnected = 0;

            disconnected += await DisconnectDevice("camera", cameraMediator.GetInfo().Connected, () => cameraMediator.Disconnect(), failures);
            disconnected += await DisconnectDevice("mount", telescopeMediator.GetInfo().Connected, () => telescopeMediator.Disconnect(), failures);
            disconnected += await DisconnectDevice("filter wheel", filterWheelMediator.GetInfo().Connected, () => filterWheelMediator.Disconnect(), failures);
            disconnected += await DisconnectDevice("focuser", focuserMediator.GetInfo().Connected, () => focuserMediator.Disconnect(), failures);
            disconnected += await DisconnectDevice("rotator", rotatorMediator.GetInfo().Connected, () => rotatorMediator.Disconnect(), failures);
            disconnected += await DisconnectDevice("flat device", flatDeviceMediator.GetInfo().Connected, () => flatDeviceMediator.Disconnect(), failures);
            disconnected += await DisconnectDevice("guider", guiderMediator.GetInfo().Connected, () => guiderMediator.Disconnect(), failures);
            disconnected += await DisconnectDevice("switch hub", switchMediator.GetInfo().Connected, () => switchMediator.Disconnect(), failures);
            disconnected += await DisconnectDevice("weather", weatherDataMediator.GetInfo().Connected, () => weatherDataMediator.Disconnect(), failures);
            disconnected += await DisconnectDevice("dome", domeMediator.GetInfo().Connected, () => domeMediator.Disconnect(), failures);
            disconnected += await DisconnectDevice("safety monitor", safetyMonitorMediator.GetInfo().Connected, () => safetyMonitorMediator.Disconnect(), failures);

            if (failures.Count > 0) {
                return Fail($"Disconnected {disconnected} devices, failures: {string.Join(", ", failures)}");
            }

            return Ok($"Disconnect-all finished. Newly disconnected: {disconnected}.");
        }

        private static async Task<int> ConnectDevice(string name, bool alreadyConnected, Func<Task<bool>> connectFunc, IList<string> failures) {
            if (alreadyConnected) {
                return 0;
            }

            try {
                var success = await connectFunc();
                if (success) {
                    return 1;
                }
                failures.Add(name);
            } catch {
                failures.Add(name);
            }

            return 0;
        }

        private static async Task<int> DisconnectDevice(string name, bool currentlyConnected, Func<Task> disconnectFunc, IList<string> failures) {
            if (!currentlyConnected) {
                return 0;
            }

            try {
                await (disconnectFunc?.Invoke() ?? Task.CompletedTask);
                return 1;
            } catch {
                failures.Add(name);
            }

            return 0;
        }

        private static async Task<AiExecutionResult> ExecuteAsyncCommand(IAsyncCommand command, object parameter, string actionName) {
            if (command == null) {
                return Fail($"{actionName} command is unavailable.");
            }

            if (!command.CanExecute(parameter)) {
                return Fail($"{actionName} command cannot run in current state.");
            }

            if (Application.Current?.Dispatcher == null) {
                await command.ExecuteAsync(parameter);
                return Ok($"{actionName} executed.");
            }

            Task commandTask = Task.CompletedTask;
            await Application.Current.Dispatcher.InvokeAsync(() => {
                commandTask = command.ExecuteAsync(parameter);
            });
            await commandTask;
            return Ok($"{actionName} executed.");
        }

        private AiExecutionResult CheckStartSequenceGuards() {
            if (!IsSequencerInitializedSafe()) {
                return Fail("Sequencer is not initialized yet.");
            }
            if (!cameraMediator.GetInfo().Connected) {
                return Fail("Camera is not connected.");
            }
            if (!telescopeMediator.GetInfo().Connected) {
                return Fail("Mount is not connected.");
            }
            if (telescopeMediator.GetInfo().AtPark) {
                return Fail("Mount is parked. Unpark before starting sequence.");
            }
            if (safetyMonitorMediator.GetInfo().Connected && !safetyMonitorMediator.GetInfo().IsSafe) {
                return Fail("Safety monitor reports unsafe conditions.");
            }
            if (domeMediator.GetInfo().Connected) {
                var shutter = domeMediator.GetInfo().ShutterStatus;
                if (shutter == ShutterState.ShutterClosed || shutter == ShutterState.ShutterClosing || shutter == ShutterState.ShutterError) {
                    return Fail($"Dome/roof shutter is not open ({shutter}).");
                }
            }
            return null;
        }

        private string BuildStatusSummary() {
            var camera = cameraMediator.GetInfo();
            var mount = telescopeMediator.GetInfo();
            var guider = guiderMediator.GetInfo();
            var safety = safetyMonitorMediator.GetInfo();
            var dome = domeMediator.GetInfo();
            var seqRunning = IsSequencerInitializedSafe() && sequenceMediator.IsAdvancedSequenceRunning();

            return $"Camera={camera.Connected}, Mount={mount.Connected}, Tracking={mount.TrackingEnabled}, " +
                   $"AtPark={mount.AtPark}, Guider={guider.Connected}, SafetyConnected={safety.Connected}, " +
                   $"IsSafe={(safety.Connected ? safety.IsSafe : true)}, DomeConnected={dome.Connected}, " +
                   $"Shutter={dome.ShutterStatus}, SeqRunning={seqRunning}";
        }

        private bool IsSequencerInitializedSafe() {
            try {
                return sequenceMediator.Initialized;
            } catch {
                return false;
            }
        }

        private static double? GetDouble(IDictionary<string, string> parameters, string key) {
            if (parameters == null || !parameters.TryGetValue(key, out var raw)) {
                return null;
            }
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                return value;
            }
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) {
                return value;
            }
            return null;
        }

        private static bool GetBool(IDictionary<string, string> parameters, string key, bool defaultValue) {
            if (parameters == null || !parameters.TryGetValue(key, out var raw)) {
                return defaultValue;
            }
            if (bool.TryParse(raw, out var value)) {
                return value;
            }
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue)) {
                return intValue != 0;
            }
            return defaultValue;
        }

        private static AiExecutionResult Ok(string message) {
            return new AiExecutionResult {
                Success = true,
                Message = message
            };
        }

        private Task WriteAudit(string commandId, string action, string routingSource, IDictionary<string, string> parameters, string rawPrompt, AiExecutionResult result, long elapsedMs) {
            return auditLogWriter.WriteAsync(new AiAuditRecord {
                CommandId = commandId,
                TimestampUtc = DateTimeOffset.UtcNow,
                Action = action,
                RoutingSource = routingSource,
                Parameters = parameters ?? new Dictionary<string, string>(),
                RawPrompt = rawPrompt,
                Success = result?.Success == true,
                Message = result?.Message ?? string.Empty,
                DurationMs = elapsedMs
            });
        }

        private static AiExecutionResult Fail(string message) {
            return new AiExecutionResult {
                Success = false,
                Message = message
            };
        }
    }
}
