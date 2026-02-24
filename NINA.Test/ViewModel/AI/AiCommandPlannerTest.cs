using FluentAssertions;
using Moq;
using NINA.ViewModel.AI;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Test.ViewModel.AI {

    [TestFixture]
    public class AiCommandPlannerTest {

        [Test]
        public async Task PlanAsync_JsonPrompt_ShouldUseJsonSourceAndSkipTranslator() {
            var router = new AiCommandRouter();
            var translator = new Mock<IAiPromptTranslator>(MockBehavior.Strict);
            var sut = new AiCommandPlanner(router, translator.Object);
            var prompt = "{\"action\":\"status\"}";

            var plan = await sut.PlanAsync(prompt);

            plan.Source.Should().Be("json");
            plan.Commands.Select(c => c.Action).Should().ContainSingle().Which.Should().Be("status");
            plan.Commands[0].RoutingSource.Should().Be("json");
            translator.Verify(t => t.TranslateToCommandJsonAsync(It.IsAny<string>()), Times.Never);
        }

        [Test]
        public async Task PlanAsync_WhenTranslatorReturnsValidJson_ShouldUseLlmSource() {
            var router = new AiCommandRouter();
            var translator = new Mock<IAiPromptTranslator>();
            translator.Setup(t => t.TranslateToCommandJsonAsync(It.IsAny<string>()))
                      .ReturnsAsync("{\"action\":\"status\"}");
            var sut = new AiCommandPlanner(router, translator.Object);
            var prompt = "what is current equipment status";

            var plan = await sut.PlanAsync(prompt);

            plan.Source.Should().Be("llm");
            plan.Commands.Select(c => c.Action).Should().ContainSingle().Which.Should().Be("status");
            plan.Commands[0].RawPrompt.Should().Be(prompt);
            plan.Commands[0].RoutingSource.Should().Be("llm");
        }

        [Test]
        public async Task PlanAsync_WhenTranslatorReturnsMixedSupportedAndUnsupported_ShouldKeepOnlySupported() {
            var router = new AiCommandRouter();
            var translator = new Mock<IAiPromptTranslator>();
            translator.Setup(t => t.TranslateToCommandJsonAsync(It.IsAny<string>()))
                      .ReturnsAsync("{\"commands\":[{\"action\":\"status\"},{\"action\":\"reboot\"}]}");
            var sut = new AiCommandPlanner(router, translator.Object);

            var plan = await sut.PlanAsync("give me status");

            plan.Source.Should().Be("llm");
            plan.Commands.Select(c => c.Action).Should().Equal("status");
        }

        [Test]
        public async Task PlanAsync_WhenTranslatorUnavailable_ShouldFallbackToRules() {
            var router = new AiCommandRouter();
            var translator = new Mock<IAiPromptTranslator>();
            translator.Setup(t => t.TranslateToCommandJsonAsync(It.IsAny<string>()))
                      .ReturnsAsync((string)null);
            var sut = new AiCommandPlanner(router, translator.Object);
            var prompt = "connect all devices";

            var plan = await sut.PlanAsync(prompt);

            plan.Source.Should().Be("rules");
            plan.Commands.Select(c => c.Action).Should().Contain("connect");
            plan.Commands.Should().OnlyContain(c => c.RoutingSource == "rules");
        }

        [Test]
        public async Task PlanAsync_WhenTranslatorReturnsInvalidPayload_ShouldFallbackToRules() {
            var router = new AiCommandRouter();
            var translator = new Mock<IAiPromptTranslator>();
            translator.Setup(t => t.TranslateToCommandJsonAsync(It.IsAny<string>()))
                      .ReturnsAsync("NOT JSON");
            var sut = new AiCommandPlanner(router, translator.Object);
            var prompt = "status";

            var plan = await sut.PlanAsync(prompt);

            plan.Source.Should().Be("rules");
            plan.Commands.Select(c => c.Action).Should().Contain("status");
            plan.Commands.Should().OnlyContain(c => c.RoutingSource == "rules");
        }
    }
}
