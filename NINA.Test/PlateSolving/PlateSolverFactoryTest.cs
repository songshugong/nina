#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NINA.Core.Enum;
using NINA.PlateSolving;
using NINA.Profile;
using NUnit.Framework;

namespace NINA.Test.PlateSolving {

    [TestFixture]
    public class PlateSolverFactoryTest {

        private static PlateSolveSettings CreateAstrometrySettings(bool useTextUpload) {
            return new PlateSolveSettings {
                PlateSolverType = PlateSolverEnum.ASTROMETRY_NET,
                BlindSolverType = BlindSolverEnum.ASTROMETRY_NET,
                AstrometryUseTextUpload = useTextUpload,
                AstrometryURL = "http://nova.astrometry.net",
                AstrometryAPIKey = "dummy-api-key"
            };
        }

        [Test]
        public void GetPlateSolver_AstrometryWithTextUploadDisabled_ReturnsStandardAstrometrySolver() {
            var settings = CreateAstrometrySettings(useTextUpload: false);

            var solver = PlateSolverFactory.GetPlateSolver(settings);

            solver.GetType().Name.Should().Be("AstrometryPlateSolver");
        }

        [Test]
        public void GetPlateSolver_AstrometryWithTextUploadEnabled_ReturnsTextUploadAstrometrySolver() {
            var settings = CreateAstrometrySettings(useTextUpload: true);

            var solver = PlateSolverFactory.GetPlateSolver(settings);

            solver.GetType().Name.Should().Be("AstrometryTextSolver");
        }

        [Test]
        public void GetBlindSolver_AstrometryWithTextUploadEnabled_ReturnsTextUploadAstrometrySolver() {
            var settings = CreateAstrometrySettings(useTextUpload: true);

            var solver = PlateSolverFactory.GetBlindSolver(settings);

            solver.GetType().Name.Should().Be("AstrometryTextSolver");
        }

        [Test]
        public void GetBlindSolver_AstrometryWithTextUploadDisabled_ReturnsStandardAstrometrySolver() {
            var settings = CreateAstrometrySettings(useTextUpload: false);

            var solver = PlateSolverFactory.GetBlindSolver(settings);

            solver.GetType().Name.Should().Be("AstrometryPlateSolver");
        }
    }
}
