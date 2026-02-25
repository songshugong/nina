using FluentAssertions;
using Moq;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.ViewModel.AI;

namespace NINA.Test.ViewModel.AI {

    [TestFixture]
    public class AiRuntimeContextProviderTest {

        [Test]
        public void GetSnapshot_ShouldCaptureRuntimeState() {
            var cameraMediator = new Mock<ICameraMediator>();
            var telescopeMediator = new Mock<ITelescopeMediator>();
            var guiderMediator = new Mock<IGuiderMediator>();
            var flatMediator = new Mock<IFlatDeviceMediator>();
            var weatherMediator = new Mock<IWeatherDataMediator>();
            var domeMediator = new Mock<IDomeMediator>();
            var safetyMediator = new Mock<ISafetyMonitorMediator>();
            var sequenceMediator = new Mock<ISequenceMediator>();

            cameraMediator.Setup(m => m.GetInfo()).Returns(new CameraInfo {
                Connected = true,
                CanSetTemperature = true,
                CoolerOn = true
            });
            telescopeMediator.Setup(m => m.GetInfo()).Returns(new TelescopeInfo {
                Connected = true,
                AtPark = false,
                TrackingEnabled = true,
                CanFindHome = true,
                Slewing = false
            });
            guiderMediator.Setup(m => m.GetInfo()).Returns(new GuiderInfo {
                Connected = true,
                CanClearCalibration = true
            });
            flatMediator.Setup(m => m.GetInfo()).Returns(new FlatDeviceInfo {
                Connected = true,
                SupportsOnOff = true,
                LightOn = true,
                Brightness = 120
            });
            weatherMediator.Setup(m => m.GetInfo()).Returns(new WeatherDataInfo {
                Connected = true,
                CloudCover = 82.5,
                Humidity = 93.2,
                RainRate = 0.1
            });
            domeMediator.Setup(m => m.GetInfo()).Returns(new DomeInfo {
                Connected = true,
                ShutterStatus = ShutterState.ShutterOpen,
                CanSetShutter = true,
                CanPark = true,
                CanFindHome = true,
                DriverFollowing = false,
                ApplicationFollowing = true
            });
            safetyMediator.Setup(m => m.GetInfo()).Returns(new SafetyMonitorInfo {
                Connected = true,
                IsSafe = false
            });
            sequenceMediator.SetupGet(m => m.Initialized).Returns(true);
            sequenceMediator.Setup(m => m.IsAdvancedSequenceRunning()).Returns(true);

            var sut = new AiRuntimeContextProvider(
                cameraMediator.Object,
                telescopeMediator.Object,
                guiderMediator.Object,
                flatMediator.Object,
                weatherMediator.Object,
                domeMediator.Object,
                safetyMediator.Object,
                sequenceMediator.Object);

            var snapshot = sut.GetSnapshot();

            snapshot.CameraConnected.Should().BeTrue();
            snapshot.MountConnected.Should().BeTrue();
            snapshot.GuiderConnected.Should().BeTrue();
            snapshot.FlatPanelConnected.Should().BeTrue();
            snapshot.WeatherConnected.Should().BeTrue();
            snapshot.DomeConnected.Should().BeTrue();
            snapshot.SafetyMonitorConnected.Should().BeTrue();
            snapshot.SafetyMonitorIsSafe.Should().BeFalse();
            snapshot.SequencerRunning.Should().BeTrue();
            snapshot.CloudCover.Should().BeApproximately(82.5, 0.001);
            snapshot.Humidity.Should().BeApproximately(93.2, 0.001);
            snapshot.RainRate.Should().BeApproximately(0.1, 0.001);
        }

        [Test]
        public void GetRuntimeContextSummary_ShouldRenderUnknownForNaNWeather() {
            var cameraMediator = new Mock<ICameraMediator>();
            var telescopeMediator = new Mock<ITelescopeMediator>();
            var guiderMediator = new Mock<IGuiderMediator>();
            var flatMediator = new Mock<IFlatDeviceMediator>();
            var weatherMediator = new Mock<IWeatherDataMediator>();
            var domeMediator = new Mock<IDomeMediator>();
            var safetyMediator = new Mock<ISafetyMonitorMediator>();
            var sequenceMediator = new Mock<ISequenceMediator>();

            cameraMediator.Setup(m => m.GetInfo()).Returns(new CameraInfo());
            telescopeMediator.Setup(m => m.GetInfo()).Returns(new TelescopeInfo());
            guiderMediator.Setup(m => m.GetInfo()).Returns(new GuiderInfo());
            flatMediator.Setup(m => m.GetInfo()).Returns(new FlatDeviceInfo());
            weatherMediator.Setup(m => m.GetInfo()).Returns(new WeatherDataInfo {
                Connected = true,
                CloudCover = double.NaN,
                Humidity = double.NaN,
                RainRate = double.NaN
            });
            domeMediator.Setup(m => m.GetInfo()).Returns(new DomeInfo());
            safetyMediator.Setup(m => m.GetInfo()).Returns(new SafetyMonitorInfo());
            sequenceMediator.SetupGet(m => m.Initialized).Returns(false);

            var sut = new AiRuntimeContextProvider(
                cameraMediator.Object,
                telescopeMediator.Object,
                guiderMediator.Object,
                flatMediator.Object,
                weatherMediator.Object,
                domeMediator.Object,
                safetyMediator.Object,
                sequenceMediator.Object);

            var summary = sut.GetRuntimeContextSummary();

            summary.Should().Contain("cloud_cover=unknown");
            summary.Should().Contain("humidity=unknown");
            summary.Should().Contain("rain_rate=unknown");
        }
    }
}
