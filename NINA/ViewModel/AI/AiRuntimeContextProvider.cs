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

namespace NINA.ViewModel.AI {

    public class AiRuntimeContextProvider : IAiRuntimeContextProvider {
        private readonly ICameraMediator cameraMediator;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IGuiderMediator guiderMediator;
        private readonly IFlatDeviceMediator flatDeviceMediator;
        private readonly IDomeMediator domeMediator;
        private readonly ISafetyMonitorMediator safetyMonitorMediator;
        private readonly ISequenceMediator sequenceMediator;

        public AiRuntimeContextProvider(
            ICameraMediator cameraMediator,
            ITelescopeMediator telescopeMediator,
            IGuiderMediator guiderMediator,
            IFlatDeviceMediator flatDeviceMediator,
            IDomeMediator domeMediator,
            ISafetyMonitorMediator safetyMonitorMediator,
            ISequenceMediator sequenceMediator) {
            this.cameraMediator = cameraMediator;
            this.telescopeMediator = telescopeMediator;
            this.guiderMediator = guiderMediator;
            this.flatDeviceMediator = flatDeviceMediator;
            this.domeMediator = domeMediator;
            this.safetyMonitorMediator = safetyMonitorMediator;
            this.sequenceMediator = sequenceMediator;
        }

        public string GetRuntimeContextSummary() {
            var lines = new List<string>();

            var camera = SafeGet(() => cameraMediator.GetInfo());
            lines.Add($"camera: connected={ToBool(camera?.Connected)}, can_set_temperature={ToBool(camera?.CanSetTemperature)}, cooler_on={ToBool(camera?.CoolerOn)}");

            var mount = SafeGet(() => telescopeMediator.GetInfo());
            lines.Add($"mount: connected={ToBool(mount?.Connected)}, at_park={ToBool(mount?.AtPark)}, tracking={ToBool(mount?.TrackingEnabled)}, can_find_home={ToBool(mount?.CanFindHome)}, slewing={ToBool(mount?.Slewing)}");

            var guider = SafeGet(() => guiderMediator.GetInfo());
            lines.Add($"guider: connected={ToBool(guider?.Connected)}, can_clear_calibration={ToBool(guider?.CanClearCalibration)}");

            var dome = SafeGet(() => domeMediator.GetInfo());
            lines.Add($"dome: connected={ToBool(dome?.Connected)}, shutter={dome?.ShutterStatus.ToString() ?? "unknown"}, can_set_shutter={ToBool(dome?.CanSetShutter)}, can_park={ToBool(dome?.CanPark)}, can_find_home={ToBool(dome?.CanFindHome)}, following_driver={ToBool(dome?.DriverFollowing)}, following_app={ToBool(dome?.ApplicationFollowing)}");

            var flat = SafeGet(() => flatDeviceMediator.GetInfo());
            var brightness = flat == null ? "unknown" : flat.Brightness.ToString(CultureInfo.InvariantCulture);
            lines.Add($"flat_panel: connected={ToBool(flat?.Connected)}, supports_on_off={ToBool(flat?.SupportsOnOff)}, light_on={ToBool(flat?.LightOn)}, brightness={brightness}");

            var safety = SafeGet(() => safetyMonitorMediator.GetInfo());
            lines.Add($"safety_monitor: connected={ToBool(safety?.Connected)}, is_safe={ToBool(safety?.IsSafe)}");

            var sequenceInitialized = SafeGet(() => sequenceMediator.Initialized, false);
            var sequenceRunning = sequenceInitialized && SafeGet(() => sequenceMediator.IsAdvancedSequenceRunning(), false);
            lines.Add($"sequencer: initialized={ToBool(sequenceInitialized)}, running={ToBool(sequenceRunning)}");

            return string.Join("\n", lines);
        }

        private static string ToBool(bool? value) {
            if (!value.HasValue) {
                return "unknown";
            }
            return value.Value ? "true" : "false";
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
