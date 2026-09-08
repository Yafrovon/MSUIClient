using System.Diagnostics;
using System.Reflection;
using MSUIClient;
using MSUIClient.Formats;

/// <summary>
/// A DEVICE EXPERIMENT, not a check. Run with --audio-device-probe.
///
/// Shared-output regression experiment. It runs decoded sources through the same
/// one-stream renderer as the client at ZERO GAIN and compares the shared device clock
/// with wall clock without requiring a world session.
///
/// The 28 MB / 1 MB A/B now proves source residency does not change the renderer's
/// fixed 10 ms output buffers. The device never receives either source as one giant
/// allocation; logical voices are sampled into the same small final-mix periods.
/// </summary>
internal static class AudioDeviceProbe
{
    public static void Run()
    {
        string root = ClientConfig.FindRepoRoot();
        string data = ClientDataRoot.Path;
        if (!Directory.Exists(data))
        {
            Console.WriteLine("[probe] no GameData/Data - nothing to measure");
            return;
        }
        using var mpq = new MpqMount(data);

        Assembly client = typeof(MSUIClient.World.Sound.SpatialAudioLaw).Assembly;
        MethodInfo decode = client
            .GetType("MSUIClient.World.Sound.Mp3Decoder", throwOnError: true)!
            .GetMethod("TryDecode",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Mp3Decoder.TryDecode missing");
        Type voiceType = client
            .GetType("MSUIClient.World.Sound.WaveOutVoice", throwOnError: true)!;
        MethodInfo open = voiceType.GetMethod("Open",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WaveOutVoice.Open missing");
        MethodInfo playedBytes = voiceType.GetMethod("PlayedBytes",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WaveOutVoice.PlayedBytes missing");
        PropertyInfo bytesPerSecond = voiceType.GetProperty("BytesPerSecond",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WaveOutVoice.BytesPerSecond missing");

        const string track = @"Sound\Music\GlueScreenMusic\wow_main_theme.mp3";
        if (mpq.ReadFile(track) is not { Length: > 0 } mp3)
        {
            Console.WriteLine($"[probe] '{track}' not in the archives");
            return;
        }
        object?[] arguments = [mp3, track, null];
        if (decode.Invoke(null, arguments) is not true)
        {
            Console.WriteLine($"[probe] '{track}' would not decode");
            return;
        }
        byte[] big = (byte[])arguments[2]!;

        // The small clip is the SAME audio, just less of it: identical format, identical
        // code path, so buffer size is the only variable between the two runs.
        int dataOffset = 44;
        uint dataBytes = BitConverter.ToUInt32(big, 40);
        int smallBytes = (int)Math.Min(dataBytes, 1 << 20);
        var small = new byte[dataOffset + smallBytes];
        Array.Copy(big, 0, small, 0, dataOffset);
        Array.Copy(big, dataOffset, small, dataOffset, smallBytes);
        BitConverter.TryWriteBytes(small.AsSpan(4, 4), 36 + smallBytes);
        BitConverter.TryWriteBytes(small.AsSpan(40, 4), smallBytes);

        Console.WriteLine($"[probe] track decoded to {dataBytes / 1024} KB; " +
                          $"small control is {smallBytes / 1024} KB");
        ScanPcm(big, track);
        foreach (string zoneTrack in new[]
                 {
                     @"Sound\Music\ZoneMusic\Forest\DayForest03.mp3",
                     @"Sound\Music\ZoneMusic\Mountain\NightMountain04.mp3",
                 })
        {
            if (mpq.ReadFile(zoneTrack) is not { Length: > 0 } bytes) continue;
            object?[] zoneArgs = [bytes, zoneTrack, null];
            if (decode.Invoke(null, zoneArgs) is true)
                ScanPcm((byte[])zoneArgs[2]!, zoneTrack);
        }
        Measure(open, playedBytes, bytesPerSecond, big, $"{dataBytes / 1024} KB (whole track)");
        Measure(open, playedBytes, bytesPerSecond, small, $"{smallBytes / 1024} KB (control)");
        MeasureChurn(voiceType, open, mpq);
    }

    /// <summary>
    /// Look for the chop INSIDE the samples. A device that plays every byte on time
    /// still sounds broken if the bytes themselves are broken, and that is exactly
    /// the shape left over once the device is exonerated. Two defects are audible
    /// as "cutting in and out": a run of digital silence where music should be, and
    /// a hard sample-to-sample jump, which is a click.
    /// </summary>
    /// <summary>
    /// Replay the old per-cue churn shape and prove it is now logical routing: one
    /// physical output remains open while every cue joins and leaves the software mix.
    /// </summary>
    private static void MeasureChurn(Type voiceType, MethodInfo open, MpqMount mpq)
    {
        const string step = @"Sound\Character\Footsteps\mFootSmallGrassA.wav";
        if (mpq.ReadFile(step) is not { Length: > 0 } wav)
        {
            Console.WriteLine($"[probe] '{step}' not in the archives");
            return;
        }
        MethodInfo counters = voiceType.GetMethod("PoolCounters",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        MethodInfo drain = voiceType.GetMethod("DrainPool",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        drain.Invoke(null, null);

        // TWO LEGS, because the game does two different things. It SPAWNS a cue on
        // the audio thread, and it REAPS a finished one later from the poll - and a
        // teardown that has to abandon a live stream costs a whole audio period more
        // than one whose buffer the driver already marked done.
        (long openedBefore, long reusedBefore) = Counters(counters);
        const int cycles = 200;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < cycles; i++)
        {
            if (open.Invoke(null, [wav, false, 0f, 0f, 0u]) is IDisposable voice) voice.Dispose();
        }
        long cycleMs = clock.ElapsedMilliseconds;

        const int batch = 32;
        var live = new List<IDisposable>(batch);
        clock.Restart();
        for (int i = 0; i < batch; i++)
            if (open.Invoke(null, [wav, false, 0f, 0f, 0u]) is IDisposable voice) live.Add(voice);
        long spawnMs = clock.ElapsedMilliseconds;
        Thread.Sleep(1200);                       // let the one-shots finish on their own
        clock.Restart();
        foreach (IDisposable voice in live) voice.Dispose();
        long reapMs = clock.ElapsedMilliseconds;

        (long openedAfter, long reusedAfter) = Counters(counters);
        drain.Invoke(null, null);

        Console.WriteLine($"[probe] churn: {cycles} spawn+release cycles in {cycleMs} ms " +
                          $"({cycleMs / (double)cycles:F2} ms each); {batch} concurrent spawns " +
                          $"in {spawnMs} ms ({spawnMs / (double)batch:F2} ms each), reaped after " +
                          $"finishing in {reapMs} ms ({reapMs / (double)batch:F2} ms each); " +
                          $"{openedAfter - openedBefore} physical output open(s), " +
                          $"{reusedAfter - reusedBefore} shared route(s)");
    }

    private static (long, long) Counters(MethodInfo counters)
    {
        object boxed = counters.Invoke(null, null)!;
        Type type = boxed.GetType();
        return ((long)type.GetField("Item1")!.GetValue(boxed)!,
                (long)type.GetField("Item2")!.GetValue(boxed)!);
    }

    private static void ScanPcm(byte[] wav, string label)
    {
        uint rate = BitConverter.ToUInt32(wav, 24);
        ushort channels = BitConverter.ToUInt16(wav, 22);
        uint dataBytes = BitConverter.ToUInt32(wav, 40);
        if (rate == 0 || channels == 0 || dataBytes < 4) return;
        int frames = (int)(dataBytes / (channels * 2));
        int framesPerMs = (int)Math.Max(1, rate / 1000);

        int silentRun = 0, longestSilentRun = 0, silentGaps = 0;
        int clicks = 0;
        int previous = 0;
        // 20 ms of exact zero is not music; a half-scale step between adjacent
        // samples is not a waveform. Both are generous - real audio trips neither.
        int gapFrames = framesPerMs * 20;
        const int ClickStep = 16384;
        for (int frame = 0; frame < frames; frame++)
        {
            int at = 44 + frame * channels * 2;
            short sample = BitConverter.ToInt16(wav, at);
            if (sample == 0)
            {
                silentRun++;
                if (silentRun == gapFrames) silentGaps++;
                longestSilentRun = Math.Max(longestSilentRun, silentRun);
            }
            else silentRun = 0;
            if (Math.Abs(sample - previous) > ClickStep) clicks++;
            previous = sample;
        }

        Console.WriteLine($"[probe] PCM '{Path.GetFileName(label)}': " +
                          $"{frames / (double)rate:F1} s, {silentGaps} silent gap(s) over 20 ms " +
                          $"(longest {longestSilentRun / (double)framesPerMs:F0} ms), " +
                          $"{clicks} hard step(s)");
    }

    private static void Measure(MethodInfo open, MethodInfo playedBytes,
        PropertyInfo bytesPerSecond, byte[] wav, string label)
    {
        // GAIN ZERO: the device consumes samples and reports position exactly as it
        // would at full volume, and the room stays quiet.
        object? voice = open.Invoke(null, [wav, false, 0f, 0f, 0u]);
        if (voice is null)
        {
            Console.WriteLine($"[probe] {label}: waveOut refused the buffer");
            return;
        }
        try
        {
            uint rate = (uint)bytesPerSecond.GetValue(voice)!;
            if (rate == 0) { Console.WriteLine($"[probe] {label}: no byte rate"); return; }
            if (playedBytes.Invoke(voice, null) is not uint)
            {
                Console.WriteLine($"[probe] {label}: the device will not report position " +
                                  "- the in-game probe is blind on this machine");
                return;
            }

            var clock = Stopwatch.StartNew();
            Thread.Sleep(200);                       // let playback actually begin
            uint baseBytes = (uint)playedBytes.Invoke(voice, null)!;
            long baseMs = clock.ElapsedMilliseconds;
            long worstWindowDeficit = 0;
            uint previous = baseBytes;
            long previousMs = baseMs;

            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(250);
                uint now = (uint)playedBytes.Invoke(voice, null)!;
                long nowMs = clock.ElapsedMilliseconds;
                long windowMs = nowMs - previousMs;
                long playedMs = now >= previous ? (now - previous) * 1000L / rate : windowMs;
                worstWindowDeficit = Math.Max(worstWindowDeficit, windowMs - playedMs);
                previous = now;
                previousMs = nowMs;
            }

            long totalWall = previousMs - baseMs;
            long totalPlayed = (previous - baseBytes) * 1000L / rate;
            Console.WriteLine($"[probe] {label}: device played {totalPlayed} ms over " +
                              $"{totalWall} ms of wall clock " +
                              $"(deficit {totalWall - totalPlayed} ms, worst 250 ms window " +
                              $"short by {worstWindowDeficit} ms)");
        }
        finally
        {
            (voice as IDisposable)?.Dispose();
        }
    }
}
