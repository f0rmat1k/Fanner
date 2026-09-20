using Fanner.Core.Config;
using Fanner.Core.Curves;

namespace Fanner.Core.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fanner-tests", Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_dir, "config.json");

    private static CurveBindingConfig BindingConfig() => CurveBindingConfig.From(new CurveBinding
    {
        FanId = "/lpc/nct6687dr/0/header/10",
        SensorId = "/amdcpu/0/temperature/2",
        Curve = new FanCurve([new CurvePoint(40, 25), new CurvePoint(80, 100)]),
        Response = TimeSpan.FromSeconds(7),
        HysteresisPercent = 3,
        KickstartDuty = 55,
    });

    private static FannerConfig WithOneProfile() => new()
    {
        Profiles =
        [
            new Profile
            {
                Name = "Gaming",
                Bindings = [BindingConfig()],
                ManualDuties = [new ManualDutyConfig { FanId = "/lpc/nct6687dr/0/header/1", DutyPercent = 100 }],
            },
        ],
        ActiveProfile = "Gaming",
    };

    [Fact]
    public void Round_trips_a_profile()
    {
        var store = new ConfigStore(ConfigPath);
        Assert.True(store.Save(WithOneProfile()));

        var profile = Assert.Single(store.Load().Profiles);

        Assert.Equal("Gaming", profile.Name);

        var restored = Assert.Single(profile.Bindings).ToBinding();
        Assert.NotNull(restored);
        Assert.Equal("/amdcpu/0/temperature/2", restored.SensorId);
        Assert.Equal(TimeSpan.FromSeconds(7), restored.Response);
        Assert.Equal([40, 80], restored.Curve.Points.Select(p => p.TemperatureC));

        var manual = Assert.Single(profile.ManualDuties);
        Assert.Equal("/lpc/nct6687dr/0/header/1", manual.FanId);
        Assert.Equal(100, manual.DutyPercent);
    }

    [Fact]
    public void Remembers_which_profile_was_active()
    {
        var store = new ConfigStore(ConfigPath);
        store.Save(WithOneProfile() with
        {
            Profiles = [new Profile { Name = "Silent" }, new Profile { Name = "Gaming" }],
            ActiveProfile = "Gaming",
        });

        Assert.Equal("Gaming", store.Load().Active.Name);
    }

    [Fact]
    public void Migrates_a_version_1_file_into_a_default_profile()
    {
        // v1 stored one flat list of bindings and knew nothing about profiles. An
        // upgrade must not quietly discard curves the user had already configured.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """
            {
              "version": 1,
              "bindings": [
                {
                  "fanId": "/lpc/nct6687dr/0/header/10",
                  "sensorId": "/amdcpu/0/temperature/2",
                  "points": [
                    { "temperatureC": 30, "dutyPercent": 30 },
                    { "temperatureC": 80, "dutyPercent": 100 }
                  ],
                  "responseSeconds": 5,
                  "hysteresisPercent": 2,
                  "kickstartDuty": 60
                }
              ]
            }
            """);

        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.Equal(FannerConfig.CurrentVersion, loaded.Version);

        var profile = Assert.Single(loaded.Profiles);
        Assert.Equal(Profile.DefaultName, profile.Name);
        Assert.Single(profile.Bindings);
        Assert.Equal("/lpc/nct6687dr/0/header/10", profile.Bindings[0].FanId);

        // The legacy list is folded in, not left to be applied a second time.
        Assert.Empty(loaded.Bindings);
    }

    [Fact]
    public void Always_yields_at_least_one_profile()
    {
        // Downstream code should never have to handle "no profiles at all".
        Assert.NotEmpty(new ConfigStore(ConfigPath).Load().Profiles);
        Assert.Equal(Profile.DefaultName, new ConfigStore(ConfigPath).Load().Active.Name);
    }

    [Fact]
    public void Returns_a_usable_config_when_the_file_is_corrupt()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, "{ this is not json");

        // Refusing to start because a settings file got truncated would be a far
        // worse failure than losing the curves in it.
        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.NotEmpty(loaded.Profiles);
        Assert.True(loaded.Active.IsEmpty);
    }

    [Fact]
    public void Keeps_the_tray_and_startup_settings()
    {
        var store = new ConfigStore(ConfigPath);
        store.Save(WithOneProfile() with { CloseToTray = false, RunAtStartup = true });

        var loaded = store.Load();

        Assert.False(loaded.CloseToTray);
        Assert.True(loaded.RunAtStartup);
    }

    [Fact]
    public void Skips_an_entry_that_cannot_make_a_valid_curve()
    {
        var onePoint = new CurveBindingConfig
        {
            FanId = "fan-1",
            SensorId = "temp-1",
            Points = [new CurvePoint(40, 20)],
        };

        var noSensor = new CurveBindingConfig
        {
            FanId = "fan-2",
            SensorId = "",
            Points = [new CurvePoint(40, 20), new CurvePoint(80, 100)],
        };

        Assert.Null(onePoint.ToBinding());
        Assert.Null(noSensor.ToBinding());
    }

    [Fact]
    public void Creates_the_directory_on_save()
    {
        var nested = Path.Combine(_dir, "deep", "deeper", "config.json");

        Assert.True(new ConfigStore(nested).Save(WithOneProfile()));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Leaves_no_temporary_file_behind()
    {
        var store = new ConfigStore(ConfigPath);
        store.Save(WithOneProfile());

        // The save writes beside the target and moves it into place; a leftover
        // .tmp would mean the move did not happen and the real file may be stale.
        Assert.False(File.Exists(ConfigPath + ".tmp"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup; not worth failing a test over.
        }
    }
}
