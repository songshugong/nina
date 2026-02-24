using FluentAssertions;
using NINA.ViewModel.AI;
using System.Collections.Generic;

namespace NINA.Test.ViewModel.AI {

    [TestFixture]
    public class AiRiskControlTest {

        [TestCase("confirm", AiControlAction.Confirm)]
        [TestCase("确认", AiControlAction.Confirm)]
        [TestCase("cancel", AiControlAction.Cancel)]
        [TestCase("取消", AiControlAction.Cancel)]
        [TestCase("pending", AiControlAction.Pending)]
        [TestCase("待确认", AiControlAction.Pending)]
        [TestCase("please confirm", AiControlAction.Confirm)]
        [TestCase("确认一下", AiControlAction.Confirm)]
        [TestCase("请确认一下", AiControlAction.Confirm)]
        [TestCase("cancel now", AiControlAction.Cancel)]
        [TestCase("查看待确认", AiControlAction.Pending)]
        [TestCase("status", AiControlAction.None)]
        public void ParseControlAction_ShouldMapExpectedAction(string prompt, AiControlAction expected) {
            var result = AiRiskControl.ParseControlAction(prompt);
            result.Should().Be(expected);
        }

        [Test]
        public void ParseControlAction_EnglishPartialWord_ShouldNotMatch() {
            AiRiskControl.ParseControlAction("confirmation").Should().Be(AiControlAction.None);
            AiRiskControl.ParseControlAction("cancelled").Should().Be(AiControlAction.None);
            AiRiskControl.ParseControlAction("depending").Should().Be(AiControlAction.None);
        }

        [Test]
        public void RequiresConfirmation_HighRiskWithoutConfirmation_ShouldBeTrue() {
            var commands = new List<AiCommand> {
                new AiCommand { Action = "park" }
            };

            var result = AiRiskControl.RequiresConfirmation(commands, "park mount");

            result.Should().BeTrue();
        }

        [Test]
        public void RequiresConfirmation_MixedRiskAndNonRisk_ShouldBeTrue() {
            var commands = new List<AiCommand> {
                new AiCommand { Action = "status" },
                new AiCommand { Action = "park" }
            };

            var result = AiRiskControl.RequiresConfirmation(commands, "status and park");

            result.Should().BeTrue();
        }

        [Test]
        public void RequiresConfirmation_HighRiskWithConfirmedFlag_ShouldBeFalse() {
            var commands = new List<AiCommand> {
                new AiCommand {
                    Action = "park",
                    Parameters = new Dictionary<string, string> {
                        ["confirmed"] = "true"
                    }
                }
            };

            var result = AiRiskControl.RequiresConfirmation(commands, "{\"action\":\"park\",\"parameters\":{\"confirmed\":true}}");

            result.Should().BeFalse();
        }

        [Test]
        public void RequiresConfirmation_HighRiskWithCaseInsensitiveConfirmedKey_ShouldBeFalse() {
            var commands = new List<AiCommand> {
                new AiCommand {
                    Action = "slew",
                    Parameters = new Dictionary<string, string> {
                        ["Confirmed"] = "1"
                    }
                }
            };

            var result = AiRiskControl.RequiresConfirmation(commands, "{\"action\":\"slew\"}");

            result.Should().BeFalse();
        }

        [Test]
        public void NeedsConfirmation_SingleCommand_ShouldRespectRiskRules() {
            AiRiskControl.NeedsConfirmation(new AiCommand { Action = "status" }, "status").Should().BeFalse();
            AiRiskControl.NeedsConfirmation(new AiCommand { Action = "slew" }, "slew target").Should().BeTrue();
            AiRiskControl.NeedsConfirmation(new AiCommand { Action = "tracking_off" }, "disable tracking").Should().BeTrue();
            AiRiskControl.NeedsConfirmation(new AiCommand { Action = "home_mount" }, "home mount").Should().BeTrue();
            AiRiskControl.NeedsConfirmation(new AiCommand { Action = "dome_follow_on" }, "enable dome follow").Should().BeFalse();
            AiRiskControl.NeedsConfirmation(new AiCommand { Action = "slew" }, "slew #force").Should().BeFalse();
        }

        [Test]
        public void RequiresConfirmation_ForcePrompt_ShouldBypassGate() {
            var commands = new List<AiCommand> {
                new AiCommand { Action = "start_sequence" }
            };

            var result = AiRiskControl.RequiresConfirmation(commands, "start capture #force");

            result.Should().BeFalse();
        }

        [Test]
        public void RequiresConfirmation_NonRiskAction_ShouldBeFalse() {
            var commands = new List<AiCommand> {
                new AiCommand { Action = "status" }
            };

            var result = AiRiskControl.RequiresConfirmation(commands, "status");

            result.Should().BeFalse();
        }
    }
}
