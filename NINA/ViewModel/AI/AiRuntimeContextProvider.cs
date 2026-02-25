#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.Globalization;
using NINA.Equipment.Interfaces;

namespace NINA.ViewModel.AI {

    public class AiRuntimeContextProvider : IAiRuntimeContextProvider {
        private readonly ICameraMediator cameraMediator;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IGuiderMediator guiderMediator;
        private readonly IFlatDeviceMediator flatDeviceMediator;
        private readonly IWeatherDataMediator weatherDataMediator;
        private readonly IDomeMediator domeMediator;
        private readonly ISafetyMonitorMediator safetyMonitorMediator;
        private readonly ISequenceMediator sequenceMediator;

        public AiRuntimeContextProvider(
            ICameraMediator cameraMediator,
            ITelescopeMediator telescopeMediator,
            IGuiderMediator guiderMediator,
            IFlatDeviceMediator flatDeviceMediator,
            IWeatherDataMediator weatherDataMediator,
            IDomeMediator domeMediator,
            ISafetyMonitorMediator safetyMonitorMediator,
            ISequenceMediator sequenceMediator) {
            this.cameraMediator = cameraMediator;
            this.telescopeMediator = telescopeMediator;
            this.guiderMediator = guiderMediator;
            this.flatDeviceMediator = flatDeviceMediator;
            this.weatherDataMediator = weatherDataMediator;
            this.domeMediator = domeMediator;
            this.safetyMonitorMediator = safetyMonitorMediator;
            this.sequenceMediator = sequenceMediator;
        }

        public AiRuntimeSnapshot GetSnapshot() {
            var snapshot = new AiRuntimeSnapshot {
                TimestampUtc = DateTimeOffset.UtcNow
            };

            var camera = SafeGet(() => cameraMediator.GetInfo());
            snapshot.CameraConnected = camera?.Connected == true;
            snapshot.CameraCanSetTemperature = camera?.CanSetTemperature == true;
            snapshot.CameraCoolerOn = camera?.CoolerOn == true;

            var mount = SafeGet(() => telescopeMediator.GetInfo());
            snapshot.MountConnected = mount?.Connected == true;
            snapshot.MountAtPark = mount?.AtPark == true;
            snapshot.MountTrackingEnabled = mount?.TrackingEnabled == true;
            snapshot.MountCanFindHome = mount?.CanFindHome == true;
            snapshot.MountSlewing = mount?.Slewing == true;

            var guider = SafeGet(() => guiderMediator.GetInfo());
            snapshot.GuiderConnected = guider?.Connected == true;
            snapshot.GuiderCanClearCalibration = guider?.CanClearCalibration == true;

            var dome = SafeGet(() => domeMediator.GetInfo());
            snapshot.DomeConnected = dome?.Connected == true;
            snapshot.DomeShutterStatus = dome?.ShutterStatus ?? ShutterState.ShutterNone;
            snapshot.DomeCanSetShutter = dome?.CanSetShutter == true;
            snapshot.DomeCanPark = dome?.CanPark == true;
            snapshot.DomeCanFindHome = dome?.CanFindHome == true;
            snapshot.DomeDriverFollowing = dome?.DriverFollowing == true;
            snapshot.DomeApplicationFollowing = dome?.ApplicationFollowing == true;

            var flat = SafeGet(() => flatDeviceMediator.GetInfo());
            snapshot.FlatPanelConnected = flat?.Connected == true;
            snapshot.FlatPanelSupportsOnOff = flat?.SupportsOnOff == true;
            snapshot.FlatPanelLightOn = flat?.LightOn == true;
            snapshot.FlatPanelBrightness = flat == null ? null : flat.Brightness;

            var weather = SafeGet(() => weatherDataMediator.GetInfo());
            snapshot.WeatherConnected = weather?.Connected == true;
            snapshot.CloudCover = NormalizeFinite(weather?.CloudCover);
            snapshot.Humidity = NormalizeFinite(weather?.Humidity);
            snapshot.RainRate = NormalizeFinite(weather?.RainRate);

            var safety = SafeGet(() => safetyMonitorMediator.GetInfo());
            snapshot.SafetyMonitorConnected = safety?.Connected == true;
            snapshot.SafetyMonitorIsSafe = safety?.IsSafe == true;

            snapshot.SequencerInitialized = SafeGet(() => sequenceMediator.Initialized, false);
            snapshot.SequencerRunning = snapshot.SequencerInitialized && SafeGet(() => sequenceMediator.IsAdvancedSequenceRunning(), false);

            return snapshot;
        }

        public string GetRuntimeContextSummary() {
            var snapshot = GetSnapshot();
            var lines = new List<string> {
                $"camera: connected={ToBool(snapshot.CameraConnected)}, can_set_temperature={ToBool(snapshot.CameraCanSetTemperature)}, cooler_on={ToBool(snapshot.CameraCoolerOn)}",
                $"mount: connected={ToBool(snapshot.MountConnected)}, at_park={ToBool(snapshot.MountAtPark)}, tracking={ToBool(snapshot.MountTrackingEnabled)}, can_find_home={ToBool(snapshot.MountCanFindHome)}, slewing={ToBool(snapshot.MountSlewing)}",
                $"guider: connected={ToBool(snapshot.GuiderConnected)}, can_clear_calibration={ToBool(snapshot.GuiderCanClearCalibration)}",
                $"dome: connected={ToBool(snapshot.DomeConnected)}, shutter={snapshot.DomeShutterStatus}, can_set_shutter={ToBool(snapshot.DomeCanSetShutter)}, can_park={ToBool(snapshot.DomeCanPark)}, can_find_home={ToBool(snapshot.DomeCanFindHome)}, following_driver={ToBool(snapshot.DomeDriverFollowing)}, following_app={ToBool(snapshot.DomeApplicationFollowing)}",
                $"flat_panel: connected={ToBool(snapshot.FlatPanelConnected)}, supports_on_off={ToBool(snapshot.FlatPanelSupportsOnOff)}, light_on={ToBool(snapshot.FlatPanelLightOn)}, brightness={snapshot.FlatPanelBrightness?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}",
                $"weather: connected={ToBool(snapshot.WeatherConnected)}, cloud_cover={FormatDouble(snapshot.CloudCover)}, humidity={FormatDouble(snapshot.Humidity)}, rain_rate={FormatDouble(snapshot.RainRate)}",
                $"safety_monitor: connected={ToBool(snapshot.SafetyMonitorConnected)}, is_safe={ToBool(snapshot.SafetyMonitorIsSafe)}",
                $"sequencer: initialized={ToBool(snapshot.SequencerInitialized)}, running={ToBool(snapshot.SequencerRunning)}"
            };
            return string.Join("\n", lines);
        }

        private static double? NormalizeFinite(double? value) {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) {
                return null;
            }
            return value;
        }

        private static string FormatDouble(double? value) {
            return value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "unknown";
        }

        private static string ToBool(bool value) {
            return value ? "true" : "false";
        }

        private static T SafeGet<T>(Func<T> getter, T fallback = default) {
            try {
                return getter != null ? getter() : fallback;
            } catch {
                return fallback;
            }
        }
    }
}
