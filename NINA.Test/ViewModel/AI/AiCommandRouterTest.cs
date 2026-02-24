using FluentAssertions;
using NINA.ViewModel.AI;
using System.Linq;

namespace NINA.Test.ViewModel.AI {

    [TestFixture]
    public class AiCommandRouterTest {

        [Test]
        public void Route_Unpark_ShouldNotTriggerPark() {
            var sut = new AiCommandRouter();

            var result = sut.Route("please unpark mount");

            result.Select(c => c.Action).Should().ContainSingle().Which.Should().Be("unpark");
        }

        [Test]
        public void Route_ShouldDeduplicateSameAction() {
            var sut = new AiCommandRouter();

            var result = sut.Route("connect all, then connect camera");

            result.Select(c => c.Action).Should().ContainSingle().Which.Should().Be("connect");
        }

        [Test]
        public void Route_ShouldParseJsonBatchCommands() {
            var sut = new AiCommandRouter();
            var json = "{\"commands\":[{\"action\":\"status\"},{\"action\":\"slew\",\"parameters\":{\"ra\":\"5.5\",\"dec\":\"-2.1\"}}]}";

            var result = sut.Route(json);

            result.Select(c => c.Action).Should().Equal("status", "slew");
            result[1].Parameters["ra"].Should().Be("5.5");
            result[1].Parameters["dec"].Should().Be("-2.1");
        }

        [Test]
        public void Route_ChineseUnpark_ShouldNotTriggerPark() {
            var sut = new AiCommandRouter();

            var result = sut.Route("取消驻车然后看看状态");

            result.Select(c => c.Action).Should().Contain("unpark");
            result.Select(c => c.Action).Should().NotContain("park");
        }

        [Test]
        public void Route_HelpKeyword_ShouldMapToHelpAction() {
            var sut = new AiCommandRouter();

            var result = sut.Route("help");

            result.Select(c => c.Action).Should().Contain("help");
        }

        [Test]
        public void Route_NewDeviceControlKeywords_ShouldMapExpectedActions() {
            var sut = new AiCommandRouter();

            sut.Route("disconnect all devices").Select(c => c.Action).Should().Contain("disconnect");
            sut.Route("enable tracking").Select(c => c.Action).Should().Contain("tracking_on");
            sut.Route("disable tracking").Select(c => c.Action).Should().Contain("tracking_off");
            sut.Route("start guiding").Select(c => c.Action).Should().Contain("guide_start");
            sut.Route("stop guiding").Select(c => c.Action).Should().Contain("guide_stop");
            sut.Route("相机制冷").Select(c => c.Action).Should().Contain("cool_camera");
            sut.Route("相机回温").Select(c => c.Action).Should().Contain("warm_camera");
        }

        [Test]
        public void Route_StopGuiding_ShouldNotMapToSequenceStop() {
            var sut = new AiCommandRouter();

            var actions = sut.Route("stop guiding").Select(c => c.Action).ToList();

            actions.Should().Contain("guide_stop");
            actions.Should().NotContain("stop");
        }
    }
}
