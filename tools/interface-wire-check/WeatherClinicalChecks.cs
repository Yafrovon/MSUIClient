using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World;
using System.Numerics;

internal static class WeatherClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.SMSG_WEATHER == 0x02F4, "SMSG_WEATHER opcode drift");
        WeatherPacket packet = WeatherPackets.Parse(
            Convert.FromHexString("010000000000403F5521000001"));
        Check(packet.WeatherType == 1 && Math.Abs(packet.Grade - .75f) < .0001f &&
              packet.SoundId == 8533 && packet.Instant,
            "SMSG_WEATHER type/grade/sound/instant decode drift");
        ExpectInvalid(() => WeatherPackets.Parse(new byte[12]));
        ExpectInvalid(() => WeatherPackets.Parse(new byte[14]));

        var weather = new WeatherVisualLaw();
        weather.Apply(1, 1f, instant: false, now: 100);
        weather.Resolve(105.005);
        Check(weather.WeatherKind == WeatherVisualLaw.Kind.Rain &&
              weather.EffectKind == WeatherVisualLaw.Kind.Rain &&
              weather.CutSequence == 1 &&
              Math.Abs(weather.IntensityA - .5f) < .002f &&
              Math.Abs(weather.EffectDensity - (1f / 3f)) < .003f &&
              Math.Abs(weather.StormBlend - .5f) < .003f,
            "weather dual-channel ten-second ramp or effect knee drift");

        weather.Apply(1, .4f, instant: false, now: 105.005);
        Check(Math.Abs(weather.IntensityA - 1f) < .001f && weather.CutSequence == 1,
            "same-type retarget must restart from the old target without cutting");
        weather.Resolve(111.015);
        Check(Math.Abs(weather.IntensityA - .4f) < .002f,
            "weather retarget duration drift");

        weather.Apply(0, 1f, instant: true, now: 112);
        Check(weather.WeatherKind == WeatherVisualLaw.Kind.Fine &&
              weather.EffectKind == WeatherVisualLaw.Kind.Fine &&
              weather.CutSequence == 2 && weather.EffectDensity == 0f &&
              weather.SkyDensity == 0f && weather.StormBlend == 0f,
            "fine weather must target zero and a type change must cut immediately");
        Check(WeatherVisualLaw.DensityGain(0) == .1f &&
              WeatherVisualLaw.DensityGain(1) == .33f &&
              WeatherVisualLaw.DensityGain(2) == .66f &&
              WeatherVisualLaw.DensityGain(3) == 1f &&
              WeatherVisualLaw.DensityGain(200) == 1f,
            "weatherDensity quality gain table drift");

        Check(!WeatherPrecipitationLaw.IndoorBlocked(false, false) &&
              !WeatherPrecipitationLaw.IndoorBlocked(true, true) &&
              WeatherPrecipitationLaw.IndoorBlocked(true, false),
            "weather indoor gate must preserve a portal-visible exterior doorway");
        Check(WeatherPrecipitationLaw.FrameSpawnCount(WeatherVisualLaw.Kind.Rain,
                  1f, 1f / 60f, WeatherPrecipitationLaw.DropCapacity) == 291 &&
              WeatherPrecipitationLaw.FrameSpawnCount(WeatherVisualLaw.Kind.Snow,
                  1f, 1f / 60f, WeatherPrecipitationLaw.DropCapacity) == 116 &&
              WeatherPrecipitationLaw.FrameSpawnCount(WeatherVisualLaw.Kind.Rain,
                  .001f, 1f / 60f, WeatherPrecipitationLaw.DropCapacity) == 0,
            "reference-30fps rain/snow frame-local emission count drift");

        WeatherPrecipitationLaw.Spawn rain = WeatherPrecipitationLaw.SpawnParticle(
            WeatherVisualLaw.Kind.Rain, 1f, Vector3.Zero, Vector3.Zero,
            Quaternion.Identity, .5f, .5f, .5f, .5f, .5f);
        WeatherPrecipitationLaw.Spawn snow = WeatherPrecipitationLaw.SpawnParticle(
            WeatherVisualLaw.Kind.Snow, 1f, Vector3.Zero, Vector3.Zero,
            Quaternion.Identity, .5f, .5f, .5f, .5f, .5f);
        Check(MathF.Abs(rain.Velocity.Z + 33f) < .001f &&
              MathF.Abs(rain.Position.Z - WeatherPrecipitationLaw.RainHeight) < .001f &&
              rain.Velocity.X < -9.49f &&
              MathF.Abs(snow.Velocity.Z + 6f) < .001f &&
              MathF.Abs(snow.Position.Z - WeatherPrecipitationLaw.SnowHeight) < .001f,
            "rain/snow five-draw placement or kinematics drift");
        Check(WeatherPrecipitationLaw.SnowPixelSize(0f) == 14f &&
              WeatherPrecipitationLaw.SnowPixelSize(50f) == 1f &&
              WeatherPrecipitationLaw.MistRate(WeatherVisualLaw.Kind.Rain, 1f) == 38f &&
              WeatherPrecipitationLaw.MistRate(WeatherVisualLaw.Kind.Snow, 1f) == 48f &&
              WeatherPrecipitationLaw.MistRate(WeatherVisualLaw.Kind.Rain, .5f) == 0f &&
              WeatherPrecipitationLaw.FirstInvalidMistPath(
                  [10f, 10.1f, 15f, 15f]) == 0 &&
              WeatherPrecipitationLaw.FirstInvalidMistPath(
                  [10f, 10.2f, 10.4f, 10.6f]) == -1,
            "snow point-size, mist rate knee, or wall-path validity drift");

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string glue = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Sound.cs"));
        string soundscape = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "WorldSoundscape.cs"));
        string lighting = SourceText.Read(Path.Combine(root, "MSUIClient", "World",
            "ExteriorLighting.cs"));
        string sky = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "SkyRenderer.cs"));
        string precip = SourceText.Read(Path.Combine(root, "MSUIClient", "World",
            "WeatherPrecipitationRenderer.cs"));
        string wmo = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Wmo",
            "WmoRenderer.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(dispatch.Contains("case Op.SMSG_WEATHER:", StringComparison.Ordinal) &&
              dispatch.Contains("ApplyWeather(body);", StringComparison.Ordinal),
            "SMSG_WEATHER dispatch drift");
        Check(glue.Contains("_weatherSoundKit = weather.SoundId;", StringComparison.Ordinal) &&
              glue.Contains("_soundscape.WeatherAmbienceKit = _weatherSoundKit;",
                  StringComparison.Ordinal) &&
              glue.Contains("_soundscapeIndoors = indoorsNow;", StringComparison.Ordinal) &&
              // Both commit sites take the dwelled value; a hardcoded verdict is the
              // regression this guards, and banning it catches either site.
              !glue.Contains("_soundscapeIndoors = true;", StringComparison.Ordinal),
            "weather sound retention or WMO indoor verdict drift");
        Check(glue.Contains("_weatherVisual.Apply(weather.WeatherType, weather.Grade, " +
                           "weather.Instant, NowSeconds());", StringComparison.Ordinal) &&
              lighting.Contains("ReadWeatherInto(sample, mapDefault", StringComparison.Ordinal) &&
              lighting.Contains("ReadWeatherInto(scratch, zone", StringComparison.Ordinal) &&
              sky.Contains("Bcc = Math.Clamp(stormBlend, 0f, 1f)", StringComparison.Ordinal),
            "weather visual state, authored storm lighting, or cloud-storm wiring drift");
        Check(precip.Contains(@"textures\weather\raindrop01.blp", StringComparison.Ordinal) &&
              precip.Contains(@"textures\weather\raindropsplash01.blp", StringComparison.Ordinal) &&
              precip.Contains(@"textures\weather\snowflake01.blp", StringComparison.Ordinal) &&
              precip.Contains(@"textures\weather\snowmist01.blp", StringComparison.Ordinal) &&
              precip.Contains("if (indoorBlocked) return", StringComparison.Ordinal) &&
              wmo.Contains("CameraExteriorPortalVisible", StringComparison.Ordinal) &&
              program.Contains("_weatherPrecipitation?.Update", StringComparison.Ordinal) &&
              program.Contains("_weatherPrecipitation.Render", StringComparison.Ordinal),
            "precipitation assets, portal-aware freeze, or frame wiring drift");
        Check(soundscape.Contains("if (!Interior && WeatherAmbienceKit != 0) return WeatherAmbienceKit;",
                  StringComparison.Ordinal) &&
              soundscape.Contains("if (Submerged) return UnderwaterLoopKit;",
                  StringComparison.Ordinal) &&
              soundscape.Contains("AmbienceFadeSeconds = 5.0f", StringComparison.Ordinal),
            "weather/underwater/indoor ambience selector or crossfade drift");

        CheckActualWeatherTextures(root);
    }

    private static void CheckActualWeatherTextures(string root)
    {
        string data = Path.Combine(root, "GameData", "Data");
        if (!Directory.Exists(data)) return;
        using var mpq = new MpqMount(data);
        string[] paths =
        [
            @"textures\weather\raindrop01.blp",
            @"textures\weather\raindropsplash01.blp",
            @"textures\weather\snowflake01.blp",
            @"textures\weather\snowmist01.blp",
        ];
        foreach (string path in paths)
        {
            byte[] bytes = mpq.ReadFile(path) ??
                throw new InvalidDataException($"actual weather texture unavailable: {path}");
            byte[] pixels = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            Check(width > 0 && height > 0 && pixels.Length == width * height * 4,
                $"actual weather texture did not decode: {path}");
        }
    }

    private static void ExpectInvalid(Action action)
    {
        try
        {
            action();
            throw new InvalidDataException("malformed weather body was accepted");
        }
        catch (InvalidDataException) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
