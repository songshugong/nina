#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NINA.ViewModel.AI {

    public static class AiCoordinateParser {
        public static bool HasCoordinateParameters(IDictionary<string, string> parameters) {
            return TryGetValue(parameters, "ra", out _) || TryGetValue(parameters, "dec", out _);
        }

        public static bool TryParse(IDictionary<string, string> parameters, out Coordinates coordinates, out string error) {
            coordinates = null;
            error = string.Empty;

            if (!TryGetValue(parameters, "ra", out var raRaw) || !TryGetValue(parameters, "dec", out var decRaw)) {
                error = "Slew JSON requires both ra and dec parameters.";
                return false;
            }

            if (!TryParseDouble(raRaw, out var ra) || !TryParseDouble(decRaw, out var dec)) {
                error = "Failed to parse ra/dec as numbers.";
                return false;
            }

            if (double.IsNaN(ra) || double.IsInfinity(ra) || double.IsNaN(dec) || double.IsInfinity(dec)) {
                error = "ra/dec must be finite numeric values.";
                return false;
            }

            var raUnit = TryGetValue(parameters, "ra_unit", out var unitRaw)
                ? unitRaw?.Trim().ToLowerInvariant()
                : string.Empty;

            var raType = raUnit is "deg" or "degree" or "degrees"
                ? Coordinates.RAType.Degrees
                : Coordinates.RAType.Hours;

            if (raType == Coordinates.RAType.Hours) {
                if (ra < 0 || ra >= 24) {
                    error = "For ra_unit=hours, ra must be in [0, 24).";
                    return false;
                }
            } else {
                if (ra < 0 || ra >= 360) {
                    error = "For ra_unit=deg, ra must be in [0, 360).";
                    return false;
                }
            }

            if (dec < -90 || dec > 90) {
                error = "dec must be in [-90, 90].";
                return false;
            }

            coordinates = new Coordinates(ra, dec, Epoch.J2000, raType);
            return true;
        }

        private static bool TryGetValue(IDictionary<string, string> parameters, string key, out string value) {
            value = string.Empty;
            if (parameters == null || parameters.Count == 0) {
                return false;
            }

            var matchedKey = parameters.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (matchedKey == null || !parameters.TryGetValue(matchedKey, out value)) {
                value = string.Empty;
                return false;
            }

            return true;
        }

        private static bool TryParseDouble(string raw, out double value) {
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) {
                return true;
            }

            return double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }
    }
}
