using FluentAssertions;
using NINA.ViewModel.AI;
using System.Collections.Generic;

namespace NINA.Test.ViewModel.AI {

    [TestFixture]
    public class AiCoordinateParserTest {

        [Test]
        public void TryParse_HoursValid_ShouldSucceed() {
            var parameters = new Dictionary<string, string> {
                ["ra"] = "5.5",
                ["dec"] = "-2.1",
                ["ra_unit"] = "hours"
            };

            var ok = AiCoordinateParser.TryParse(parameters, out var coordinates, out var error);

            ok.Should().BeTrue();
            coordinates.Should().NotBeNull();
            error.Should().BeEmpty();
        }

        [Test]
        public void TryParse_DegreesValid_ShouldSucceed() {
            var parameters = new Dictionary<string, string> {
                ["ra"] = "120.25",
                ["dec"] = "45",
                ["ra_unit"] = "deg"
            };

            var ok = AiCoordinateParser.TryParse(parameters, out var coordinates, out var error);

            ok.Should().BeTrue();
            coordinates.Should().NotBeNull();
            error.Should().BeEmpty();
        }

        [Test]
        public void TryParse_MissingDec_ShouldFail() {
            var parameters = new Dictionary<string, string> {
                ["ra"] = "12"
            };

            var ok = AiCoordinateParser.TryParse(parameters, out var coordinates, out var error);

            ok.Should().BeFalse();
            coordinates.Should().BeNull();
            error.Should().Contain("ra and dec");
        }

        [Test]
        public void TryParse_RaOutOfRangeHours_ShouldFail() {
            var parameters = new Dictionary<string, string> {
                ["ra"] = "24",
                ["dec"] = "0"
            };

            var ok = AiCoordinateParser.TryParse(parameters, out var coordinates, out var error);

            ok.Should().BeFalse();
            coordinates.Should().BeNull();
            error.Should().Contain("[0, 24)");
        }

        [Test]
        public void TryParse_DecOutOfRange_ShouldFail() {
            var parameters = new Dictionary<string, string> {
                ["ra"] = "12",
                ["dec"] = "91"
            };

            var ok = AiCoordinateParser.TryParse(parameters, out var coordinates, out var error);

            ok.Should().BeFalse();
            coordinates.Should().BeNull();
            error.Should().Contain("[-90, 90]");
        }

        [Test]
        public void HasCoordinateParameters_ShouldDetectEitherField() {
            AiCoordinateParser.HasCoordinateParameters(new Dictionary<string, string> { ["ra"] = "1" }).Should().BeTrue();
            AiCoordinateParser.HasCoordinateParameters(new Dictionary<string, string> { ["dec"] = "1" }).Should().BeTrue();
            AiCoordinateParser.HasCoordinateParameters(new Dictionary<string, string> { ["RA"] = "1" }).Should().BeTrue();
            AiCoordinateParser.HasCoordinateParameters(new Dictionary<string, string> { ["x"] = "1" }).Should().BeFalse();
            AiCoordinateParser.HasCoordinateParameters(null).Should().BeFalse();
        }
    }
}
