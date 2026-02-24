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
            sut.Route("find mount home now").Select(c => c.Action).Should().Contain("home_mount");
            sut.Route("enable tracking").Select(c => c.Action).Should().Contain("tracking_on");
            sut.Route("disable tracking").Select(c => c.Action).Should().Contain("tracking_off");
            sut.Route("start guiding").Select(c => c.Action).Should().Contain("guide_start");
            sut.Route("stop guiding").Select(c => c.Action).Should().Contain("guide_stop");
            sut.Route("相机制冷").Select(c => c.Action).Should().Contain("cool_camera");
            sut.Route("相机回温").Select(c => c.Action).Should().Contain("warm_camera");
            sut.Route("open dome shutter").Select(c => c.Action).Should().Contain("dome_open");
            sut.Route("关闭穹顶").Select(c => c.Action).Should().Contain("dome_close");
            sut.Route("enable dome follow").Select(c => c.Action).Should().Contain("dome_follow_on");
            sut.Route("disable dome follow").Select(c => c.Action).Should().Contain("dome_follow_off");
            sut.Route("park dome").Select(c => c.Action).Should().Contain("dome_park");
            sut.Route("dome home").Select(c => c.Action).Should().Contain("dome_home");
            sut.Route("打开平场灯").Select(c => c.Action).Should().Contain("flat_light_on");
            sut.Route("flat light off").Select(c => c.Action).Should().Contain("flat_light_off");
        }

        [Test]
        public void Route_StopGuiding_ShouldNotMapToSequenceStop() {
            var sut = new AiCommandRouter();

            var actions = sut.Route("stop guiding").Select(c => c.Action).ToList();

            actions.Should().Contain("guide_stop");
            actions.Should().NotContain("stop");
        }

        [Test]
        public void Route_DomeSpecificPhrases_ShouldNotTriggerMountParkOrSequenceStop() {
            var sut = new AiCommandRouter();

            var parkActions = sut.Route("park dome now").Select(c => c.Action).ToList();
            parkActions.Should().Contain("dome_park");
            parkActions.Should().NotContain("park");

            var stopActions = sut.Route("stop dome follow").Select(c => c.Action).ToList();
            stopActions.Should().Contain("dome_follow_off");
            stopActions.Should().NotContain("stop");
        }
    }
}
