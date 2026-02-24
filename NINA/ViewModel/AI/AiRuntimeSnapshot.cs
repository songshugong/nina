#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Interfaces;
using System;

namespace NINA.ViewModel.AI {

    public class AiRuntimeSnapshot {
        public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

        public bool CameraConnected { get; set; }
        public bool CameraCanSetTemperature { get; set; }
        public bool CameraCoolerOn { get; set; }

        public bool MountConnected { get; set; }
        public bool MountAtPark { get; set; }
        public bool MountTrackingEnabled { get; set; }
        public bool MountCanFindHome { get; set; }
        public bool MountSlewing { get; set; }

        public bool GuiderConnected { get; set; }
        public bool GuiderCanClearCalibration { get; set; }

        public bool DomeConnected { get; set; }
        public ShutterState DomeShutterStatus { get; set; } = ShutterState.ShutterNone;
        public bool DomeCanSetShutter { get; set; }
        public bool DomeCanPark { get; set; }
        public bool DomeCanFindHome { get; set; }
        public bool DomeDriverFollowing { get; set; }
        public bool DomeApplicationFollowing { get; set; }

        public bool FlatPanelConnected { get; set; }
        public bool FlatPanelSupportsOnOff { get; set; }
        public bool FlatPanelLightOn { get; set; }
        public int? FlatPanelBrightness { get; set; }

        public bool WeatherConnected { get; set; }
        public double? CloudCover { get; set; }
        public double? Humidity { get; set; }
        public double? RainRate { get; set; }

        public bool SafetyMonitorConnected { get; set; }
        public bool SafetyMonitorIsSafe { get; set; }

        public bool SequencerInitialized { get; set; }
        public bool SequencerRunning { get; set; }

        public bool DomeShutterClosedLike =>
            DomeShutterStatus == ShutterState.ShutterClosed ||
            DomeShutterStatus == ShutterState.ShutterClosing ||
            DomeShutterStatus == ShutterState.ShutterError;
    }
}
