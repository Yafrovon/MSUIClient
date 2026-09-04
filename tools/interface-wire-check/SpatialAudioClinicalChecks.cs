using System.Numerics;
using System.Reflection;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.Engine;
using MSUIClient.World.Sound;
using MSUIClient.World.Units;

internal static class SpatialAudioClinicalChecks
{
    public static void Run()
    {
        Check(SpatialAudioLaw.CharacterListener(new Vector3(10f, 20f, 30f)) ==
              new Vector3(10f, 20f, 31.7f),
            "character listener must sit at the avatar head");

        float inside = SpatialAudioLaw.Gain(.8f, 10f, 100f,
            new Vector3(10f, 0f, 0f), Vector3.Zero);
        float inverse = SpatialAudioLaw.Gain(1f, 10f, 100f,
            new Vector3(20f, 0f, 0f), Vector3.Zero);
        float finalBand = SpatialAudioLaw.Gain(1f, 10f, 100f,
            new Vector3(95f, 0f, 0f), Vector3.Zero);
        Check(Near(inside, .8f) && Near(inverse, .2f) &&
              Near(finalBand, (10f / 350f) * .5f) &&
              SpatialAudioLaw.Gain(1f, 10f, 100f,
                  new Vector3(100f, 0f, 0f), Vector3.Zero) == 0f,
            "FMOD factor-four rolloff, last-ten-percent fade, or strict cutoff drifted");

        Check(Near(SpatialAudioLaw.Pan(new Vector3(0f, -10f, 0f), Vector3.Zero, 0f), 1f) &&
              Near(SpatialAudioLaw.Pan(new Vector3(0f, 10f, 0f), Vector3.Zero, 0f), -1f) &&
              Near(SpatialAudioLaw.Pan(new Vector3(10f, 0f, 0f), Vector3.Zero,
                  MathF.PI * .5f), 1f) &&
              SpatialAudioLaw.Pan(Vector3.Zero, Vector3.Zero, 1f) == 0f,
            "stereo side must follow character facing, not camera orbit");

        Check(SpatialAudioLaw.StereoLevels(.8f, 0f) == (.8f, .8f) &&
              SpatialAudioLaw.StereoLevels(.8f, 1f) == (0f, .8f) &&
              SpatialAudioLaw.StereoLevels(.8f, -1f) == (.8f, 0f) &&
              SpatialAudioLaw.StereoLevels(.8f, .25f) == (.6f, .8f),
            "waveOut stereo balance projection drifted");

        string root = ClientConfig.FindRepoRoot();
        string mixer = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "AudioMixer.cs"));
        string voice = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "WaveOutVoice.cs"));
        string sharedOutput = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "SharedWaveOutMixer.cs"));
        string library = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "SoundKitLibrary.cs"));
        string policy = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "AudioFeaturePolicy.cs"));
        string spells = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Spells",
            "SpellSoundSystem.cs"));
        string soundscape = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "WorldSoundscape.cs"));
        string liquidLoops = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "LiquidAmbientLoopSystem.cs"));
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Sound.cs"));
        string creatures = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.CreatureVoices.cs"));
        string creatureRenderer = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CreatureRenderer.cs"));
        string creatureMounts = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CreatureRenderer.Mounts.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string footsteps = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Footsteps.cs"));
        string gameObjects = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.GameObjectSounds.cs"));
        Check(mixer.Contains("SetVoiceGainPan", StringComparison.Ordinal) &&
              sharedOutput.Contains("SpatialAudioLaw.StereoLevels", StringComparison.Ordinal) &&
              spells.Contains("SpatialAudioLaw.Pan", StringComparison.Ordinal) &&
              spells.Contains("SpatialAudioLaw.Gain", StringComparison.Ordinal) &&
              runtime.Contains("SpatialAudioLaw.CharacterListener", StringComparison.Ordinal) &&
              runtime.Contains("_window.Camera.ViewYaw", StringComparison.Ordinal),
            "production listener/source/pan/rolloff wiring drifted");
        // The quarantine is OPT-OUT since 2026-08-30: its premise (a voice-count device
        // problem) was disproved by the 2026-08-28 evidence session, so world audio plays
        // by default and the env var survives only as a kill switch.
        Check(policy.Contains("MSUI_EXPANDED_WORLD_AUDIO", StringComparison.Ordinal) &&
              policy.Contains("!= \"0\"", StringComparison.Ordinal) &&
              !policy.Contains("== \"1\"", StringComparison.Ordinal) &&
              creatures.Contains("if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled)",
                  StringComparison.Ordinal) &&
              footsteps.Contains("if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled)",
                  StringComparison.Ordinal) &&
              gameObjects.Contains("if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled)",
                  StringComparison.Ordinal) &&
              spells.Contains("Preserve the attenuation law from the last known-clean audio build.",
                  StringComparison.Ordinal),
            "known-clean producer compatibility boundary drifted");
        // Device timing remains one evidence channel, but the output topology is now
        // pinned too. Legacy waveOut volume is a process-session control on modern
        // Windows, so a per-voice call here would reintroduce the exact audible chop
        // that byte progress cannot observe.
        Check(sharedOutput.Contains("waveOutGetPosition", StringComparison.Ordinal) &&
              voice.Contains("public uint? PlayedBytes()", StringComparison.Ordinal) &&
              voice.Contains("public uint BytesPerSecond", StringComparison.Ordinal) &&
              mixer.Contains("ProbePlaybackProgress", StringComparison.Ordinal) &&
              mixer.Contains("[audio] DROPOUT", StringComparison.Ordinal) &&
              mixer.Contains("MaxConcurrentVoices", StringComparison.Ordinal),
            "the device dropout probe / voice budget went missing");

        // ONE REAL OUTPUT, many logical voices. Gain/pan are multiplied into each
        // source before summing, changes glide for 15 ms, forced stops de-click, and
        // the final wide mix is soft-limited before int16 conversion. There must be
        // no call back to the process-wide legacy volume control.
        Check(voice.Contains("SharedWaveOutMixer", StringComparison.Ordinal) &&
              sharedOutput.Contains("private static extern int waveOutOpen", StringComparison.Ordinal) &&
              !voice.Contains("waveOutSetVolume(", StringComparison.Ordinal) &&
              !sharedOutput.Contains("waveOutSetVolume(", StringComparison.Ordinal) &&
              sharedOutput.Contains("GainRampFrames = OutputRate * 15 / 1000", StringComparison.Ordinal) &&
              sharedOutput.Contains("StopRampFrames = OutputRate * 15 / 1000", StringComparison.Ordinal) &&
              sharedOutput.Contains("SoftLimit", StringComparison.Ordinal) &&
              sharedOutput.Contains("lock (_targetLock)", StringComparison.Ordinal) &&
              sharedOutput.Contains("PendingSubmit", StringComparison.Ordinal) &&
              sharedOutput.Contains("MaxRenderFailures = 3", StringComparison.Ordinal) &&
              sharedOutput.Contains("if (_owner.Unhealthy) return true", StringComparison.Ordinal) &&
              !sharedOutput.Contains("Looping && !stopping", StringComparison.Ordinal) &&
              voice.Contains("public static bool DrainPool()", StringComparison.Ordinal) &&
              mixer.Contains("if (!WaveOutVoice.DrainPool())", StringComparison.Ordinal) &&
              sharedOutput.Contains("BufferCount = 8", StringComparison.Ordinal) &&
              sharedOutput.Contains("RenderFallbackPollMs = 5", StringComparison.Ordinal) &&
              sharedOutput.Contains("AvSetMmThreadCharacteristics", StringComparison.Ordinal) &&
              sharedOutput.Contains("OUTPUT STARVED", StringComparison.Ordinal),
            "the isolated shared-output mixer / de-click law went missing");

        MethodInfo softLimit = typeof(SpatialAudioLaw).Assembly
            .GetType("MSUIClient.World.Sound.SharedWaveOutMixer", throwOnError: true)!
            .GetMethod("SoftLimit",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SharedWaveOutMixer.SoftLimit seam missing");
        float untouched = (float)softLimit.Invoke(null, [.5f])!;
        float limited = (float)softLimit.Invoke(null, [2f])!;
        Check(Near(untouched, .5f) && limited > .95f && limited < 1f,
            "the final-mix soft knee clips ordinary samples or permits integer wrap");

        MethodInfo isolationFixture = typeof(SpatialAudioLaw).Assembly
            .GetType("MSUIClient.World.Sound.SharedWaveOutMixer", throwOnError: true)!
            .GetMethod("IsolationFixtureMaxError",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("shared-mix isolation fixture missing");
        int isolationError = (int)isolationFixture.Invoke(null, null)!;
        Check(isolationError <= 2,
            $"moving/crowd cues mutated the independent bed (max {isolationError} PCM units)");

        MethodInfo lifecycleFixture = typeof(SpatialAudioLaw).Assembly
            .GetType("MSUIClient.World.Sound.SharedWaveOutMixer", throwOnError: true)!
            .GetMethod("LifecycleFixtureFailures",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("shared-mix lifecycle fixture missing");
        int lifecycleFailures = (int)lifecycleFixture.Invoke(null, null)!;
        Check(lifecycleFailures == 0,
            $"shared-mix activation/ramp/stop lifecycle failed (mask {lifecycleFailures})");

        Check(mixer.Contains("_preparingSources.TryGetValue", StringComparison.Ordinal) &&
              mixer.Contains("ReferenceEquals(current, completed)", StringComparison.Ordinal) &&
              mixer.Contains("WaveOutVoice.OpenPending", StringComparison.Ordinal) &&
              mixer.IndexOf("WaveOutVoice.OpenPending", StringComparison.Ordinal) <
              mixer.IndexOf("lock (requestState.Gate)",
                  mixer.IndexOf("WaveOutVoice.OpenPending", StringComparison.Ordinal),
                  StringComparison.Ordinal),
            "large-source single-flight or non-blocking pending activation regressed");

        Check(SoundVariationLaw.Draw(0) == 0 &&
              SoundVariationLaw.Draw(uint.MaxValue) == 30 &&
              SoundVariationLaw.Draw(1u << 31) == 15 &&
              SoundVariationLaw.PitchFrequency(0) == 18_742 &&
              SoundVariationLaw.PitchFrequency(15) == 22_050 &&
              SoundVariationLaw.PitchFrequency(30) == 25_357,
            "SoundEntries 0x400 pitch variation drifted from the build-5875 integer law");

        int inaudibleGate = spells.IndexOf("if (gain <= 0f) return 0", StringComparison.Ordinal);
        int duplicateGate = spells.IndexOf("TryReserveNoDuplicate", inaudibleGate,
            StringComparison.Ordinal);
        int variationPick = spells.IndexOf("_library.PickVariant", duplicateGate,
            StringComparison.Ordinal);
        Check(inaudibleGate >= 0 && duplicateGate > inaudibleGate &&
              variationPick > duplicateGate &&
              spells.Contains("SoundVariationLaw.NextPitchFrequency", StringComparison.Ordinal) &&
              spells.Contains("NoDuplicateReservation:", StringComparison.Ordinal) &&
              spells.Contains("Stop(held)", StringComparison.Ordinal) &&
              !spells.Contains("_mixer.Stop(held)", StringComparison.Ordinal) &&
              mixer.Contains("NoDuplicateBusyUnsafe", StringComparison.Ordinal) &&
              mixer.Contains("_pendingVolume.TryRemove(voiceId", StringComparison.Ordinal) &&
              liquidLoops.Contains("TryReserveNoDuplicate", StringComparison.Ordinal) &&
              liquidLoops.Contains("NoDuplicateReservation:", StringComparison.Ordinal) &&
              library.Contains("_remainingWeights", StringComparison.Ordinal) &&
              library.Contains("remaining[pickedIndex]--", StringComparison.Ordinal) &&
              !library.Contains("_lastVariant", StringComparison.Ordinal),
            "same-kit suppression must precede weighted depletion/decode, with authored pitch variation");

        int streamStart = soundscape.IndexOf("private long PlayStreamKit", StringComparison.Ordinal);
        int ordinaryStart = soundscape.IndexOf("private long PlayOrdinaryKit", StringComparison.Ordinal);
        string streamBody = streamStart >= 0 && ordinaryStart > streamStart
            ? soundscape[streamStart..ordinaryStart] : string.Empty;
        string ordinaryBody = ordinaryStart >= 0 ? soundscape[ordinaryStart..] : string.Empty;
        Check(streamBody.Contains("_library.PickVariant", StringComparison.Ordinal) &&
              !streamBody.Contains("TryReserveNoDuplicate", StringComparison.Ordinal) &&
              !streamBody.Contains("NextPitchFrequency", StringComparison.Ordinal) &&
              ordinaryBody.Contains("TryReserveNoDuplicate", StringComparison.Ordinal) &&
              ordinaryBody.Contains("NextPitchFrequency", StringComparison.Ordinal) &&
              mixer.Contains("path:", StringComparison.Ordinal) &&
              spells.Contains("new SoundEntry(0", StringComparison.Ordinal),
            "streamed beds must bypass ordinary flags while ordinary/custom kits keep global 0x20");

        float parkedAt = -1f;
        bool firstOutside = AnimationEventElectionLaw.IsElected(
            visible: false, moreAudible: false, now: 10f, ref parkedAt);
        bool insideGrace = AnimationEventElectionLaw.IsElected(
            visible: false, moreAudible: false, now: 10.49f, ref parkedAt);
        bool parked = AnimationEventElectionLaw.IsElected(
            visible: false, moreAudible: false, now: 10.5f, ref parkedAt);
        bool exempt = AnimationEventElectionLaw.IsElected(
            visible: false, moreAudible: true, now: 11f, ref parkedAt);
        Check(firstOutside && insideGrace && !parked && exempt && parkedAt < 0f &&
              Near(AnimationEventElectionLaw.PaddedRadius(1f), 6f) &&
              Near(AnimationEventElectionLaw.PaddedRadius(2f), 8f),
            "animation-event election lost its padded footprint, edge grace, or MORE_AUDIBLE reset");

        Check(creatureRenderer.Contains("AnimationEventsElected", StringComparison.Ordinal) &&
              creatureRenderer.Contains("const uint MoreAudible = 0x20", StringComparison.Ordinal) &&
              creatureRenderer.Contains("AnimationEventElectionLaw.PaddedRadius", StringComparison.Ordinal) &&
              creatureRenderer.Contains("ForgetAnimationEventClocks", StringComparison.Ordinal) &&
              creatureMounts.Contains("if (emitAnimationEvents)", StringComparison.Ordinal) &&
              creatureMounts.IndexOf("_mountFootstepTime.Remove(guid)",
                  creatureMounts.IndexOf("if (mountDisplayId <= 0", StringComparison.Ordinal),
                  StringComparison.Ordinal) >= 0 &&
              net.Contains("_creatures.TypeFlagsFor", StringComparison.Ordinal),
            "off-frustum event tracks must stay silent except for MORE_AUDIBLE creatures");

        CheckMp3DecodeAgainstShippedMusic(root);

        int prepare = sharedOutput.IndexOf("waveOutPrepareHeader", StringComparison.Ordinal);
        int write = sharedOutput.IndexOf("FillAndSubmit(buffer);", prepare,
            StringComparison.Ordinal);
        Check(prepare >= 0 && write > prepare,
            "shared output buffers must be prepared before their first submission");
    }

    /// <summary>
    /// Decode a track the client actually ships and prove the WAV describes it.
    ///
    /// The decoder writes its samples straight into the final RIFF buffer now, behind
    /// the header, instead of filling a MemoryStream, calling ToArray, and having
    /// BuildWav copy it again - four full-track buffers for one song, which the
    /// 2026-08-30 log caught starving the whole process (audio worker descheduled
    /// 250 ms on an EMPTY queue, game thread at 0.35 M cycles/ms, device measurably
    /// 180 ms short of real time). An in-place decoder is easy to get subtly wrong -
    /// an off-by-one on the header offset, a stale chunk size, a buffer that stops
    /// early - and each of those is silent or noisy rather than a crash, so it is
    /// checked against a real file with a known duration.
    /// </summary>
    private static void CheckMp3DecodeAgainstShippedMusic(string root)
    {
        string data = ClientDataRoot.Path;
        if (!Directory.Exists(data)) return;
        using var mpq = new MpqMount(data);
        const string track = @"Sound\Music\GlueScreenMusic\wow_main_theme.mp3";
        if (mpq.ReadFile(track) is not { Length: > 0 } mp3) return;

        // Public method on an INTERNAL type still binds as Public; NonPublic alone
        // finds nothing and the null lands as a bare NullReferenceException here.
        MethodInfo decode = typeof(SpatialAudioLaw).Assembly
            .GetType("MSUIClient.World.Sound.Mp3Decoder", throwOnError: true)!
            .GetMethod("TryDecode",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Mp3Decoder.TryDecode seam missing");
        object?[] arguments = [mp3, track, null];
        Check(decode.Invoke(null, arguments) is true, $"Mp3Decoder refused '{track}'");
        byte[] wav = (byte[])arguments[2]!;

        Check(wav.Length > 44 &&
              wav[0] == 'R' && wav[1] == 'I' && wav[2] == 'F' && wav[3] == 'F' &&
              wav[8] == 'W' && wav[9] == 'A' && wav[10] == 'V' && wav[11] == 'E' &&
              wav[12] == 'f' && wav[13] == 'm' && wav[14] == 't' && wav[15] == ' ' &&
              wav[36] == 'd' && wav[37] == 'a' && wav[38] == 't' && wav[39] == 'a',
            "the decoded WAV lost its RIFF/fmt/data chunk layout");

        ushort format = BitConverter.ToUInt16(wav, 20);
        ushort channels = BitConverter.ToUInt16(wav, 22);
        uint rate = BitConverter.ToUInt32(wav, 24);
        uint byteRate = BitConverter.ToUInt32(wav, 28);
        ushort blockAlign = BitConverter.ToUInt16(wav, 32);
        ushort bits = BitConverter.ToUInt16(wav, 34);
        uint dataBytes = BitConverter.ToUInt32(wav, 40);
        uint riffSize = BitConverter.ToUInt32(wav, 4);

        Check(format == 1 && bits == 16 && channels is 1 or 2 && rate > 0 &&
              blockAlign == channels * 2 && byteRate == rate * blockAlign,
            "the decoded WAV's fmt chunk is not the PCM the waveOut path takes");

        // THE ONE THAT CATCHES AN IN-PLACE MISTAKE: the samples must fit inside the
        // buffer, and be a whole number of frames. Slack AFTER them is legal (the
        // size estimate rounds up and the chunk sizes are what readers trust);
        // running past the end is not.
        Check(dataBytes > 0 && 44L + dataBytes <= wav.LongLength &&
              dataBytes % blockAlign == 0 && riffSize == 36 + dataBytes,
            $"the data chunk ({dataBytes} B) does not describe the buffer " +
            $"({wav.LongLength} B) as whole {blockAlign} B frames");

        // And it must be the WHOLE track: the glue theme runs minutes, so anything
        // under a minute means the decode stopped early.
        double seconds = dataBytes / (double)byteRate;
        Check(seconds > 60d, $"'{track}' decoded to only {seconds:F1} s of audio");
    }

    private static bool Near(float left, float right) => MathF.Abs(left - right) < 1e-5f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
