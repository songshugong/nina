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
using NINA.Astrometry;

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
                        result = Ok("Supported: connect, status, start_sequence, stop, park, unpark, platesolve, center, slew. JSON example: {\"action\":\"slew\",\"parameters\":{\"ra\":\"5.5\",\"dec\":\"-2.1\",\"ra_unit\":\"hours\",\"confirmed\":\"true\"}}. High-risk actions require confirm/cancel unless overridden.");
                        break;

                    case "connect":
                        result = await ConnectAllAsync();
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
                        var coords = ParseCoordinates(command.Parameters);
                        if (coords != null) {
                            if (!telescopeMediator.GetInfo().Connected) {
                                result = Fail("Mount is not connected.");
                                break;
                            }
                            var slewOk = await telescopeMediator.SlewToCoordinatesAsync(coords, CancellationToken.None);
                            result = slewOk
                                ? Ok($"Slew requested to RA {coords.RAString}, Dec {coords.DecString}.")
                                : Fail("Slew failed.");
                            break;
                        }
                        result = await ExecuteAsyncCommand(framingAssistantVM.SlewToCoordinatesCommand, "Slew", "Slew to target");
                        break;

                    default:
                        result = Fail("Unknown action. Try \"help\". Supported: connect, status, start_sequence, stop, park, unpark, platesolve, center, slew.");
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

        private static Coordinates ParseCoordinates(IDictionary<string, string> parameters) {
            if (parameters == null || parameters.Count == 0) {
                return null;
            }

            var ra = GetDouble(parameters, "ra");
            var dec = GetDouble(parameters, "dec");
            if (!ra.HasValue || !dec.HasValue) {
                return null;
            }

            var raUnit = GetString(parameters, "ra_unit")?.ToLowerInvariant();
            var raType = raUnit == "deg" || raUnit == "degree" || raUnit == "degrees"
                ? Coordinates.RAType.Degrees
                : Coordinates.RAType.Hours;

            return new Coordinates(ra.Value, dec.Value, Epoch.J2000, raType);
        }

        private static string GetString(IDictionary<string, string> parameters, string key) {
            return parameters.TryGetValue(key, out var value) ? value : null;
        }

        private bool IsSequencerInitializedSafe() {
            try {
                return sequenceMediator.Initialized;
            } catch {
                return false;
            }
        }

        private static double? GetDouble(IDictionary<string, string> parameters, string key) {
            if (!parameters.TryGetValue(key, out var raw)) {
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
