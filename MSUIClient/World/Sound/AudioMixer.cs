using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using MSUIClient.Formats;

namespace MSUIClient.World.Sound;

/// <summary>A play request, in the only terms the device understands: a file, a
/// category, and an absolute gain. Everything about WHICH file and WHY belongs to
/// the caller; the extra fields are carried only so the journal can name it.</summary>
public readonly record struct AudioPlayRequest(
    string Path,
    string Category,
    float Gain,
    bool Looping,
    string RequestedCue = "",
    uint SoundId = 0,
    ulong Owner = 0,
    bool TrackHold = false,
    /// <summary>Start even at zero gain. A crossfade begins silent and rises; an
    /// effect that is already inaudible should never open a device at all.</summary>
    bool StartWhenSilent = false,
    /// <summary>Log the start. The long-lived world beds are worth a line each;
    /// a combat cue firing ten times a second is not.</summary>
    bool Announce = false,
    /// <summary>Stereo balance, -1 left through 0 center to +1 right.</summary>
    float Pan = 0f,
    /// <summary>Optional authored absolute playback frequency. Zero uses the
    /// source's own sample rate.</summary>
    uint PlaybackFrequency = 0,
    /// <summary>Reject a second logical-live instance of this SoundEntries kit.
    /// For creator files whose SoundId is zero, the normalized virtual path is
    /// the stable identity instead.</summary>
    bool NoDuplicates = false,
    /// <summary>Producer-side 0x20 reservation acquired before variation/pitch RNG.
    /// Zero asks the mixer to reserve atomically at Play time.</summary>
    long NoDuplicateReservation = 0);

/// <summary>
/// THE AUDIO DEVICE, and nothing else: one software mix, one WinMM output, one
/// control worker, and one set of live logical voices.
///
/// It knows about files, gains, categories and voice ids. It does NOT know what a
/// spell is, what a zone is, or that SoundEntries.dbc exists - those are policy,
/// and policy lives in the systems above (SpellSoundSystem, WorldSoundscape). The
/// split exists because there is exactly one sound device and several unrelated
/// callers, and the previous arrangement had the world soundscape borrowing the
/// spell system's channels through a hole punched in it for that purpose.
///
/// EVERY VOICE IS DECODED PCM BEFORE ADMISSION. Reading, decoding and WAV repair
/// happen on the thread pool; the control worker keeps stops and mix changes off
/// the game thread, while one dedicated render thread continuously feeds the one
/// physical output. Callers never block and no logical voice owns a device/session
/// volume control.
///
/// This replaced MCI on 2026-08-16, and the reason is worth keeping: MCI's
/// MPEGVideo device skipped continuously on zone music while the client was idle,
/// and it did so through a DirectShow graph whose COM apartment and notification
/// window lived on this thread, streaming off a temp file. A synthesized sine and
/// an ambience bed through the path below were both clean in the same session.
///
/// On platforms without winmm the routing stays fully observable - journal,
/// counters, voice ids, liveness - and silent.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    private static readonly bool DiagnosticToneSolo =
        Environment.GetEnvironmentVariable("MSUI_AUDIO_TONE") == "1";

    public sealed record SoundPlayJournalEntry(long Sequence, double TimeSeconds,
        string Category, string RequestedCue, uint SoundId, string ResolvedPath,
        ulong Owner, bool Looping, bool TrackHold);

    /// <summary>A live logical PCM source in the one shared renderer.</summary>
    private sealed record Voice(long Id, string Category, long StartedAtMs, bool Looping,
        WaveOutVoice Pcm, string Path, AudioVoicePriorityLaw.Rank Rank)
    {
        /// <summary>Latest applied output gain; admission prefers stealing the quietest voice.</summary>
        public float Gain { get; set; }

        /// <summary>Rolling baseline for the drift probe: the wall clock and driver
        /// byte position at the start of the window being judged. Zero clock = not
        /// sampled yet.</summary>
        public long ProbedAtMs { get; set; }
        public uint ProbedBytes { get; set; }

        /// <summary>Total audio the device FAILED to play, accumulated over every
        /// window. This is the number that matters: a continuous chop is many small
        /// shortfalls, and any single-window threshold big enough not to fire on
        /// driver granularity is too big to see them.</summary>
        public long DeficitMs { get; set; }
        public long ReportedDeficitMs { get; set; }
        public int Dropouts { get; set; }

        /// <summary>Whether the device answered a position query at least once. A
        /// device that refuses leaves the probe BLIND, and a blind probe reporting
        /// nothing looks exactly like a device that kept up - which is the one
        /// mistake an evidence channel must never make.</summary>
        public bool ProbeArmed { get; set; }
        public bool ProbeBlindReported { get; set; }
    }

    /// <summary>Linearizes a pre-start Stop against renderer activation. The file
    /// may decode concurrently, but a cancelled request can never become audible
    /// after Stop has won this gate.</summary>
    private sealed class VoiceRequestState
    {
        public readonly object Gate = new();
        public bool Cancelled;
    }

    // ── the master mix (2026-08-14, world-soundscape pass) ───────────────────
    // Category volumes follow the 1.12 registrar defaults (music 0.4, ambience
    // 0.6 - a fresh vanilla install is NOT uniform full volume). Categories are
    // decided by the CALLER, never derived from SoundEntries.SoundType: that is
    // the reference client's own rule (benilla pinned finding B3).

    public bool SoundEnabled { get; set; } = true;

    /// <summary>The driven body's guid: its sounds outrank every other unit's when the mix is
    /// full (<see cref="AudioVoicePriorityLaw"/>). Set by the game loop each frame.</summary>
    public ulong PriorityOwner
    {
        get => (ulong)Interlocked.Read(ref _priorityOwner);
        set => Interlocked.Exchange(ref _priorityOwner, (long)value);
    }
    private long _priorityOwner;
    private readonly ConcurrentDictionary<string, long> _otherUnitStarts = new(StringComparer.OrdinalIgnoreCase);
    private long _otherUnitRepeatsDropped;
    public bool MusicEnabled { get; set; } = true;
    public bool AmbienceEnabled { get; set; } = true;
    public float MasterVolume { get; set; } = 1f;
    public float EffectsVolume { get; set; } = 1f;
    public float MusicVolume { get; set; } = 0.4f;
    public float AmbienceVolume { get; set; } = 0.6f;

    /// <summary>The category multiplier applied on top of per-entry gain.</summary>
    public float CategoryAmp(string category)
    {
        if (!SoundEnabled) return 0f;
        float amp = category switch
        {
            "music" => MusicEnabled ? MusicVolume : 0f,
            "ambience" => AmbienceEnabled ? AmbienceVolume : 0f,
            _ => EffectsVolume,
        };
        return Math.Clamp(amp * MasterVolume, 0f, 1f);
    }

    private readonly MpqMount _mpq;
    private readonly ConcurrentDictionary<string, byte[]> _customFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private long _nextVoice;

    // Worker state. _voices is touched ONLY on the worker thread.
    //
    // A plain queue plus an event rather than BlockingCollection, because the
    // worker must wait on the WINDOW MESSAGE QUEUE as well as on work, and only a
    // real kernel handle can go into MsgWaitForMultipleObjectsEx alongside it.
    private readonly ConcurrentQueue<Action> _jobQueue = new();
    private readonly AutoResetEvent _jobSignal = new(false);
    private readonly object _enqueueLock = new();
    private readonly object _disposeLock = new();
    private volatile bool _shutdown;
    private bool _disposeCompleted;
    private readonly Thread _worker;
    private readonly Dictionary<long, Voice> _voices = [];

    /// <summary>
    /// Voices that have been REQUESTED and not yet stopped or finished.
    ///
    /// Registered at request time rather than at start time, on purpose: the worker
    /// takes real time to open an mp3, and IsLive answering "dead" during that
    /// window made the music transport declare every track ended one frame after
    /// starting it - scheduling the silence interval OVER a track that then played
    /// to completion. Every failure path removes the entry again.
    /// </summary>
    private readonly ConcurrentDictionary<long, VoiceRequestState> _live = new();

    /// <summary>
    /// SoundEntries flag 0x20 is a GLOBAL same-key admission gate, not a variant
    /// hint. It lives here because Soundscape, liquid ambience and SpellSoundSystem
    /// all route directly to this mixer; keeping it in any one producer lets the
    /// other producers overlap the same authored kit. Creator cues have SoundId 0,
    /// so their normalized virtual path supplies the key.
    /// </summary>
    private readonly object _noDuplicateLock = new();
    private sealed class NoDuplicateClaim(long reservationId)
    {
        public long ReservationId = reservationId;
        public long VoiceId;
        public WaveOutVoice? Pcm;
    }
    private readonly Dictionary<string, NoDuplicateClaim> _noDuplicateClaims =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> _noDuplicateKeys = [];
    private readonly Dictionary<long, string> _noDuplicateReservations = [];
    private long _nextNoDuplicateReservation;

    private readonly ConcurrentQueue<SoundPlayJournalEntry> _playJournal = new();

    /// <summary>
    /// Latest wanted volume per voice, plus a single latch saying a drain job is
    /// already in the queue.
    ///
    /// A VOLUME IS A STATE, NOT AN EVENT: only the newest value matters, so a fade
    /// must never become a queue of individual commands. A 5 s ambience crossfade
    /// legitimately produces a new value nearly every frame, and enqueueing one
    /// closure per change once put 45 control writes behind a blocking open.
    /// Collapsed here, a fade of any length costs one target update per voice per
    /// worker drain; the renderer glides between those targets itself.
    /// </summary>
    private readonly record struct PendingMix(int Volume, int? Pan);
    private readonly ConcurrentDictionary<long, PendingMix> _pendingVolume = new();
    private int _volumeDrainQueued;

    private double _lastPollSeconds;

    public long Plays => Interlocked.Read(ref _plays);
    private long _plays;
    public long Stops => Interlocked.Read(ref _stops);
    private long _stops;
    private long _routes;

    /// <summary>Whether this platform actually has a backend behind the routing.</summary>
    public bool Audible => OperatingSystem.IsWindows();

    public AudioMixer(MpqMount mpq)
    {
        _mpq = mpq;
        if (DiagnosticToneSolo)
            Console.WriteLine("[audio] MSUI_AUDIO_TONE=1 - solo mixer mode; " +
                              "all non-test requests suppressed");
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "audio-mixer",
            // Keep request ordering and voice-control work responsive while
            // startup's asset parsers and upload worker are busy. The independent
            // audio-render thread owns the real-time output periods.
            Priority = ThreadPriority.AboveNormal,
        };
        // No apartment state and no message pump: MCI was the only thing here
        // that was COM, and it is gone.
        _worker.Start();
    }

    public IReadOnlyList<SoundPlayJournalEntry> JournalSnapshot() => _playJournal.ToArray();

    /// <summary>Whether a voice is still live (requested and not yet stopped or
    /// finished). The end-of-clip poll retires finished one-shots, so a music
    /// track's end shows up here within a quarter second.</summary>
    public bool IsLive(long voiceId) => voiceId != 0 && _live.ContainsKey(voiceId);

    /// <summary>Reserve an authored 0x20 slot BEFORE variation and pitch draws.
    /// The first request owns the slot while its async decode runs, so completion
    /// order cannot let a later request jump the queue. Returns zero when busy.</summary>
    public long TryReserveNoDuplicate(uint soundId, string fallbackPath)
    {
        string key = NoDuplicateKey(soundId, fallbackPath);
        lock (_noDuplicateLock)
        {
            if (NoDuplicateBusyUnsafe(key)) return 0;
            long reservation = Interlocked.Increment(ref _nextNoDuplicateReservation);
            _noDuplicateClaims[key] = new NoDuplicateClaim(reservation);
            _noDuplicateReservations[reservation] = key;
            return reservation;
        }
    }

    public void ReleaseNoDuplicateReservation(long reservation)
    {
        if (reservation == 0) return;
        lock (_noDuplicateLock)
        {
            if (!_noDuplicateReservations.Remove(reservation, out string? key) ||
                !_noDuplicateClaims.TryGetValue(key, out NoDuplicateClaim? claim) ||
                claim.ReservationId != reservation || claim.VoiceId != 0) return;
            _noDuplicateClaims.Remove(key);
        }
    }

    /// <summary>Register bytes under a virtual path so they can be played before
    /// they exist in any MPQ (the creator's authored cues). The virtual path is
    /// also what the creator exporter writes, so preview and patch name the same
    /// asset.</summary>
    public void RegisterCustomFile(string virtualPath, byte[] bytes)
    {
        if (!string.IsNullOrWhiteSpace(virtualPath) && bytes.Length > 0)
            _customFiles[virtualPath.Replace('/', '\\')] = bytes;
    }

    public void RemoveCustomFile(string virtualPath)
    {
        if (!string.IsNullOrWhiteSpace(virtualPath))
            _customFiles.TryRemove(virtualPath.Replace('/', '\\'), out _);
    }

    private const string TestTonePath = "msui-audio-selftest.wav";

    /// <summary>
    /// A KNOWN-GOOD SIGNAL THROUGH A KNOWN-GOOD PATH, so "is the skipping ours"
    /// stops being a matter of opinion.
    ///
    /// The samples are synthesized here, so no MPQ, no decoder and no disk touch
    /// them. They enter the same fixed-period shared renderer as every production
    /// voice. A continuous sine is the easiest waveform in existence to hear a
    /// discontinuity in, so this now tests the complete final-mix path.
    /// </summary>
    public long PlayTestTone(int hz = 440, float seconds = 2f)
    {
        const int rate = 44100;
        int frames = (int)(rate * Math.Clamp(seconds, 0.25f, 10f));
        var pcm = new byte[frames * 2];

        // A whole number of cycles, so the loop point is continuous and any click
        // heard at the wrap is the SYSTEM's, not an artefact of this generator.
        double cycles = Math.Max(1, Math.Round(frames * (double)hz / rate));
        for (int i = 0; i < frames; i++)
        {
            double phase = 2.0 * Math.PI * cycles * i / frames;
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2, 2), (short)(Math.Sin(phase) * 9000));
        }

        byte[] wav = BuildWav(pcm, channels: 1, rate: rate, bits: 16);
        RegisterCustomFile(TestTonePath, wav);
        Console.WriteLine($"[audio] SELF TEST: {hz} Hz sine, {frames} frames, synthesized in " +
                          "memory and looped through the shared final renderer. A break means " +
                          "the final output stalled; this tone does not assign the cause.");
        return Play(new AudioPlayRequest(TestTonePath, "ambience", 0.35f, Looping: true,
            RequestedCue: "audio-self-test", StartWhenSilent: true, Announce: true));
    }

    /// <summary>Start a voice. Returns its id, or 0 when nothing was started -
    /// which is not an error: an inaudible or unsupported request is still
    /// journalled and counted, because "we decided to play this" is the fact the
    /// audio protocols assert on.</summary>
    public long Play(in AudioPlayRequest request)
    {
        // A diagnostic tone is useful only when it is genuinely the sole mixer voice. World
        // soundscape suppression alone does not cover footsteps, creature loops, spell cues,
        // GameObject events, or server-pushed sounds, all of which enter through this method.
        if (DiagnosticToneSolo &&
            !request.RequestedCue.Equals("audio-self-test", StringComparison.Ordinal))
        {
            ReleaseNoDuplicateReservation(request.NoDuplicateReservation);
            return 0;
        }

        long sequence = Interlocked.Increment(ref _plays);
        _playJournal.Enqueue(new(sequence, Environment.TickCount64 / 1000.0,
            request.Category, request.RequestedCue, request.SoundId, request.Path,
            request.Owner, request.Looping, request.TrackHold));
        while (_playJournal.Count > 4096) _playJournal.TryDequeue(out _);

        if (!Audible || request.Gain <= 0f && !request.StartWhenSilent)
        {
            ReleaseNoDuplicateReservation(request.NoDuplicateReservation);
            return 0;
        }

        AudioVoicePriorityLaw.Rank rank = AudioVoicePriorityLaw.Classify(
            request.Category, request.Owner, PriorityOwner, request.Looping);
        if (AudioVoicePriorityLaw.IsOtherUnit(rank) && !OtherUnitRepeatAllowed(request))
        {
            ReleaseNoDuplicateReservation(request.NoDuplicateReservation);
            return 0;
        }

        long voiceId = Interlocked.Increment(ref _nextVoice);
        var requestState = new VoiceRequestState();
        string path = request.Path;
        if (request.NoDuplicates)
        {
            string key = NoDuplicateKey(request.SoundId, path);
            if (!BindNoDuplicate(
                    request.NoDuplicateReservation, key, voiceId, requestState))
            {
                ReleaseNoDuplicateReservation(request.NoDuplicateReservation);
                return 0;
            }
        }
        else
        {
            ReleaseNoDuplicateReservation(request.NoDuplicateReservation);
            _live[voiceId] = requestState;
        }
        string category = request.Category;
        float gain = request.Gain;
        bool looping = request.Looping;
        bool announce = request.Announce;
        float pan = request.Pan;
        uint playbackFrequency = request.PlaybackFrequency;
        Task<PreparedSource> fileTask = PrepareFile(voiceId, path);
        Enqueue(() => PlayOnWorker(voiceId, requestState, path, fileTask, looping, gain, pan,
            playbackFrequency, category, announce, rank));
        return voiceId;
    }

    /// <summary>The same sound from other units restarts at most once per
    /// <see cref="AudioVoicePriorityLaw.OtherUnitRepeatWindowMs"/>: a raid's ten identical precasts
    /// in one instant are one sound to the ear, and ten voices to the budget.</summary>
    private bool OtherUnitRepeatAllowed(in AudioPlayRequest request)
    {
        string key = request.SoundId != 0 ? $"#{request.SoundId}" : request.Path;
        long now = Environment.TickCount64;
        if (_otherUnitStarts.TryGetValue(key, out long last) &&
            now - last < AudioVoicePriorityLaw.OtherUnitRepeatWindowMs)
        {
            Interlocked.Increment(ref _otherUnitRepeatsDropped);
            return false;
        }
        _otherUnitStarts[key] = now;
        if (_otherUnitStarts.Count > 512)
            foreach (var stale in _otherUnitStarts)
                if (now - stale.Value > 10_000) _otherUnitStarts.TryRemove(stale.Key, out _);
        return true;
    }

    public void Stop(long voiceId)
    {
        if (voiceId == 0) return;
        RetireLive(voiceId);
        Enqueue(() => StopOnWorker(voiceId));
    }

    private static string NoDuplicateKey(uint soundId, string fallbackPath) =>
        soundId != 0
            ? $"kit:{soundId}"
            : $"path:{(fallbackPath ?? string.Empty).Replace('/', '\\').Trim()}";

    /// <summary>Retire liveness and its global 0x20 claim as one lifecycle action.
    /// Every failure, natural finish and explicit stop comes through here.</summary>
    private void RetireLive(long voiceId)
    {
        if (_live.TryGetValue(voiceId, out VoiceRequestState? requestState))
        {
            lock (requestState.Gate)
            {
                requestState.Cancelled = true;
                _live.TryRemove(voiceId, out _);
            }
        }
        _pendingVolume.TryRemove(voiceId, out _);
        lock (_noDuplicateLock)
        {
            if (!_noDuplicateKeys.Remove(voiceId, out string? key)) return;
            if (_noDuplicateClaims.TryGetValue(key, out NoDuplicateClaim? claim) &&
                claim.VoiceId == voiceId)
                RemoveNoDuplicateClaimUnsafe(key, claim);
        }
    }

    private bool BindNoDuplicate(long reservation, string key, long voiceId,
        VoiceRequestState requestState)
    {
        lock (_noDuplicateLock)
        {
            NoDuplicateClaim claim;
            if (reservation != 0)
            {
                if (!_noDuplicateReservations.TryGetValue(reservation, out string? reservedKey) ||
                    !reservedKey.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    !_noDuplicateClaims.TryGetValue(key, out claim!) ||
                    claim.ReservationId != reservation || claim.VoiceId != 0)
                    return false;
                _noDuplicateReservations.Remove(reservation);
                claim.ReservationId = 0;
            }
            else
            {
                if (NoDuplicateBusyUnsafe(key)) return false;
                claim = new NoDuplicateClaim(0);
                _noDuplicateClaims[key] = claim;
            }

            claim.VoiceId = voiceId;
            _noDuplicateKeys[voiceId] = key;
            _live[voiceId] = requestState;
            return true;
        }
    }

    private bool ActivateNoDuplicate(long voiceId, WaveOutVoice pcm)
    {
        lock (_noDuplicateLock)
        {
            if (!_noDuplicateKeys.TryGetValue(voiceId, out string? key)) return true;
            if (!_noDuplicateClaims.TryGetValue(key, out NoDuplicateClaim? claim) ||
                claim.VoiceId != voiceId) return false;
            claim.Pcm = pcm;
            return true;
        }
    }

    private bool NoDuplicateBusyUnsafe(string key)
    {
        if (!_noDuplicateClaims.TryGetValue(key, out NoDuplicateClaim? claim)) return false;
        if (claim.VoiceId == 0) return true; // reserved before selection/decode
        if (_live.ContainsKey(claim.VoiceId) &&
            (claim.Pcm is null || !claim.Pcm.Finished)) return true;
        RemoveNoDuplicateClaimUnsafe(key, claim);
        return false;
    }

    private void RemoveNoDuplicateClaimUnsafe(string key, NoDuplicateClaim claim)
    {
        _noDuplicateClaims.Remove(key);
        if (claim.ReservationId != 0)
            _noDuplicateReservations.Remove(claim.ReservationId);
        if (claim.VoiceId != 0)
            _noDuplicateKeys.Remove(claim.VoiceId);
    }

    /// <summary>Set a voice's absolute output gain (0..1). The caller owns the
    /// whole product - entry volume, category amp, fade envelope.</summary>
    public void SetVoiceGain(long voiceId, float gain)
    {
        if (voiceId == 0) return;
        QueueVolume(voiceId, (int)Math.Clamp(gain * 1000f, 0, 1000), pan: null);
    }

    /// <summary>Set one positional voice's absolute gain and stereo balance atomically.</summary>
    public void SetVoiceGainPan(long voiceId, float gain, float pan)
    {
        if (voiceId == 0) return;
        QueueVolume(voiceId, (int)Math.Clamp(gain * 1000f, 0, 1000),
            (int)Math.Clamp(pan * 1000f, -1000f, 1000f));
    }

    /// <summary>Record the wanted volume and make sure exactly one drain job is
    /// pending. Callable from the game thread at frame rate without growing the
    /// queue by a frame's worth of work each time.</summary>
    private void QueueVolume(long voiceId, int volume, int? pan)
    {
        _pendingVolume[voiceId] = new PendingMix(volume, pan);
        if (Interlocked.Exchange(ref _volumeDrainQueued, 1) == 0)
            Enqueue(ApplyPendingVolumes);
    }

    private void ApplyPendingVolumes()
    {
        // Clear the latch BEFORE draining: a value written while this runs has to
        // be able to queue a fresh job, or the last step of a fade can be stranded
        // until something else changes. The cost of losing that race is one
        // redundant drain, which is nothing; the cost of the other order is a
        // voice stuck at the wrong volume.
        Interlocked.Exchange(ref _volumeDrainQueued, 0);
        foreach (long id in _pendingVolume.Keys.ToArray())
        {
            // Preparation is asynchronous. Keep the newest target attached to a
            // still-live pending voice; PlayOnWorker consumes it before Add. The
            // old remove-first order silently threw away a whole short crossfade
            // when an MP3 took longer to decode than its envelope.
            if (!_voices.TryGetValue(id, out Voice? voice))
            {
                if (!_live.ContainsKey(id)) _pendingVolume.TryRemove(id, out _);
                continue;
            }
            if (!_pendingVolume.TryRemove(id, out PendingMix mix)) continue;
            if (mix.Pan is int pan) voice.Pcm.SetMix(mix.Volume / 1000f, pan / 1000f);
            else voice.Pcm.SetGain(mix.Volume / 1000f);
            voice.Gain = mix.Volume / 1000f;
        }
    }

    /// <summary>
    /// Retire finished logical one-shots after their last mixed period passes the
    /// shared device clock. Looping voices never finish on their own.
    /// </summary>
    public void PollFinished()
    {
        double now = Environment.TickCount64 / 1000.0;
        if (now - _lastPollSeconds < .25) return;
        _lastPollSeconds = now;
        Enqueue(() =>
        {
            foreach (Voice voice in _voices.Values.ToArray())
                if (voice.Pcm.Finished) StopOnWorker(voice.Id);
                else ProbePlaybackProgress(voice);
            ReportVoiceCensus();
        });
    }

    /// <summary>
    /// Requests versus sounds that reached the shared renderer. The distinction is
    /// essential: distant/zero-gain cues are journalled requests but never become
    /// voices, and calling both numbers "started" overstated a captured crowd by
    /// roughly eight times.
    /// </summary>
    private void ReportVoiceCensus()
    {
        long now = Environment.TickCount64;
        long requested = Interlocked.Read(ref _plays);
        long routed = Interlocked.Read(ref _routes);
        if (_lastCensusAtMs == 0)
        {
            _lastCensusAtMs = now;
            _lastCensusPlays = requested;
            _lastCensusRoutes = routed;
            return;
        }
        if (now - _lastCensusAtMs < CensusIntervalMs) return;
        long requests = requested - _lastCensusPlays;
        long newRoutes = routed - _lastCensusRoutes;
        long windowMs = now - _lastCensusAtMs;
        _lastCensusAtMs = now;
        _lastCensusPlays = requested;
        _lastCensusRoutes = routed;
        if (requests == 0 && newRoutes == 0) return;
        (long opens, long sharedRoutes) = WaveOutVoice.PoolCounters();
        long newOpens = opens - _lastCensusOpens;
        long reusedOutput = sharedRoutes - _lastCensusReuses;
        _lastCensusOpens = opens;
        _lastCensusReuses = sharedRoutes;
        Console.WriteLine($"[audio] {_voices.Count} logical voice(s); {requests} request(s), " +
                          $"{newRoutes} routed in the last {windowMs} ms " +
                          $"({newOpens} physical output open(s), {reusedOutput} shared route(s))");
    }

    private const long CensusIntervalMs = 2000;
    private long _lastCensusAtMs;
    private long _lastCensusPlays;
    private long _lastCensusRoutes;
    private long _lastCensusOpens;
    private long _lastCensusReuses;

    /// <summary>
    /// THE OUTPUT-DELIVERY PROBE. Ask the driver how much of the shared stream it
    /// actually consumed and compare that against wall-clock. Material shortfall is
    /// evidence that final output delivery stalled; it is one kind of missing audio.
    ///
    /// A clean answer is deliberately narrower: it says nothing about source cutoffs,
    /// gain/pan pumping, overlap density, clipping, or wrong PCM. The old probe was
    /// clean while per-footstep session-volume writes audibly chopped the entire mix;
    /// keeping that limitation in the contract prevents the same tunnel vision.
    /// </summary>
    private void ProbePlaybackProgress(Voice voice)
    {
        long now = Environment.TickCount64;
        if (voice.Pcm.PlayedBytes() is not uint played)
        {
            // SAY SO, once per voice. Silence here would be indistinguishable from
            // a clean run, and the whole point of this probe is that its negative
            // answer is trustworthy.
            if (!voice.ProbeBlindReported)
            {
                voice.ProbeBlindReported = true;
                Console.WriteLine($"[audio] no play position from the device for " +
                                  $"'{voice.Path}' ({voice.Category}) - the dropout probe " +
                                  "is BLIND on this voice");
            }
            return;
        }
        // The shared device has up to 80 ms of already-queued mix ahead of a newly
        // admitted source. Do not baseline that source until its first frame has
        // actually crossed the device clock, or the queue lead becomes a fake
        // first-window deficit.
        if (voice.ProbedAtMs == 0 && played == 0) return;
        if (!voice.ProbeArmed)
        {
            voice.ProbeArmed = true;
            // ONCE PER PROCESS, not per voice. The question this answers is "is the
            // probe alive at all", and asking it per voice buried the log it exists to
            // keep readable - a populated zone armed it dozens of times a second.
            if (!_probeArmedAnnounced)
            {
                _probeArmedAnnounced = true;
                Console.WriteLine($"[audio] dropout probe armed (first voice " +
                                  $"'{voice.Path}', {voice.Pcm.BytesPerSecond} B/s); " +
                                  "silence from here means the device is keeping up");
            }
        }
        bool first = voice.ProbedAtMs == 0;
        long windowMs = now - voice.ProbedAtMs;
        uint previous = voice.ProbedBytes;
        uint rate = voice.Pcm.BytesPerSecond;
        if (first || rate == 0 || windowMs < ProbeWindowMs)
        {
            if (first) { voice.ProbedAtMs = now; voice.ProbedBytes = played; }
            return;
        }
        voice.ProbedAtMs = now;
        voice.ProbedBytes = played;

        // A loop wrap reports a position JUMP backwards, not a stall - re-baseline
        // and charge the window nothing rather than inventing a deficit.
        if (played < previous) return;

        long playedMs = (played - previous) * 1000L / rate;
        long shortfall = Math.Clamp(windowMs - playedMs, 0, windowMs);
        if (shortfall <= ProbeGranularityMs) return;   // driver reporting granularity
        voice.DeficitMs += shortfall;
        if (voice.DeficitMs - voice.ReportedDeficitMs < ProbeReportEveryMs) return;

        voice.ReportedDeficitMs = voice.DeficitMs;
        voice.Dropouts++;
        Console.WriteLine($"[audio] DROPOUT '{voice.Path}' ({voice.Category}): device is " +
                          $"{voice.DeficitMs} ms of audio behind wall clock " +
                          $"(latest window played {playedMs} of {windowMs} ms; " +
                          $"report #{voice.Dropouts}, {_voices.Count} voice(s) open, " +
                          $"queue {_jobQueue.Count})");
    }

    /// <summary>
    /// One line per finished long-lived voice summarizing the rolling windows that
    /// were actually sampled. This is not an end-to-end duration comparison.
    /// </summary>
    private static void ReportVoiceProgress(Voice voice)
    {
        long openMs = Environment.TickCount64 - voice.StartedAtMs;
        if (openMs < 2000 || !voice.ProbeArmed || voice.Pcm.BytesPerSecond == 0) return;
        Console.WriteLine($"[audio] voice done '{voice.Path}' ({voice.Category}): open " +
                          $"{openMs} ms, probe accumulated {voice.DeficitMs} ms of sampled " +
                          "output shortfall " +
                          $"({voice.Dropouts} report(s))");
    }

    /// <summary>Shortest window worth judging: several worker wakes, so a single late
    /// poll cannot read as a stall.</summary>
    private const long ProbeWindowMs = 400;

    /// <summary>Per-window slack for the driver's own position granularity. Anything
    /// at or under this is not evidence of anything.</summary>
    private const long ProbeGranularityMs = 15;

    /// <summary>How much ACCUMULATED lost audio is worth another line. Small enough
    /// that a continuous micro-chop reports steadily, large enough that a healthy
    /// session stays silent.</summary>
    private const long ProbeReportEveryMs = 100;

    // ── worker ───────────────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        // A plain work queue now. Nothing on this thread is a cross-process call
        // any more: opening a waveOut device costs a millisecond or two against
        // the 50-300 ms mciSendString("open") used to, and there is no COM
        // apartment and no notification window left to pump. The thread survives
        // only to keep device work off the game thread and to keep stops ordered
        // ahead of the plays that replace them.
        long lastRanAt = Environment.TickCount64;
        while (!_shutdown)
        {
            try
            {
                while (_jobQueue.TryDequeue(out Action? job)) job();
            }
            catch { /* one bad cue must not kill the audio thread */ }

            // THE STALL WATCHDOG, kept because it is what settled this: it says
            // whether a gap belongs to our own work (queue non-empty) or to the OS
            // not scheduling this thread (queue empty). It should now never fire.
            long ranAt = Environment.TickCount64;
            long gap = ranAt - lastRanAt;
            if (gap >= AudioStallWarnMs && ranAt - _lastStallReportAtMs >= 1000)
            {
                _lastStallReportAtMs = ranAt;
                Console.WriteLine($"[audio] worker stalled for {gap} ms " +
                                  $"(queue {_jobQueue.Count}, voices {_voices.Count})");
            }
            lastRanAt = ranAt;
            _jobSignal.WaitOne(WorkerWakeMs);
        }
        foreach (long id in _voices.Keys.ToArray()) StopOnWorker(id);
    }

    /// <summary>Hand a job to the worker and wake it now, rather than at the end of
    /// whatever it is currently sleeping through.</summary>
    private void Enqueue(Action job)
    {
        // Dispose takes this same short gate while publishing shutdown. No caller
        // can pass the state check, pause, and signal an event that Dispose has
        // since closed; decode continuations are allowed to arrive this late.
        lock (_enqueueLock)
        {
            if (_shutdown) return;
            _jobQueue.Enqueue(job);
            _jobSignal.Set();
        }
    }

    private const int WorkerWakeMs = 15;

    /// <summary>Pump gap worth reporting. Comfortably above the wake interval and
    /// a normal status round trip, comfortably below a single blocking open.</summary>
    private const int AudioStallWarnMs = 120;

    private long _lastStallReportAtMs;

    /// <summary>Decoded, ready-to-play PCM in a RIFF/WAVE wrapper. Null means the
    /// asset could not be found or decoded.</summary>
    private readonly record struct PreparedSource(byte[]? Pcm);

    // ── decoded-source cache ─────────────────────────────────────────────────
    // Zone music, ambience beds and busy cues are requested over and over — a
    // doorway alone re-requests both beds — and each start used to pay the full
    // MPQ read + MP3 decode again (tens of MB of LOH churn per start). The
    // finished WAV bytes are immutable once prepared (WaveOutVoice only copies
    // them out), so they are shared across voices and kept under a byte budget
    // with oldest-use eviction. Custom (creator) files bypass the cache — they
    // are replaceable at runtime.
    private readonly object _preparedCacheLock = new();
    private readonly Dictionary<string, (byte[] Wav, long UsedAt)> _preparedCache =
        new(StringComparer.OrdinalIgnoreCase);
    // A cache miss is also shared while it is in flight. Without this gate, two
    // callers reaching the same uncached 28 MB track together each performed the
    // full MPQ read and MP3 decode, recreating the large allocation/CPU wave that
    // the prepared cache was meant to remove.
    private readonly Dictionary<string, Task<PreparedSource>> _preparingSources =
        new(StringComparer.OrdinalIgnoreCase);
    private long _preparedCacheBytes;
    private long _preparedCacheUseClock;
    private const long PreparedCacheCapBytes = 192L << 20;
    private const long PreparedCacheMaxEntryBytes = 48L << 20;

    private void PreparedCachePut(string path, byte[] wav)
    {
        if (wav.LongLength > PreparedCacheMaxEntryBytes) return;
        lock (_preparedCacheLock)
        {
            if (_preparedCache.ContainsKey(path)) return;
            while (_preparedCacheBytes + wav.LongLength > PreparedCacheCapBytes &&
                   _preparedCache.Count > 0)
            {
                string? oldest = null;
                long oldestUse = long.MaxValue;
                foreach ((string key, (byte[], long UsedAt) entry) in _preparedCache)
                    if (entry.UsedAt < oldestUse) { oldestUse = entry.UsedAt; oldest = key; }
                if (oldest is null) break;
                _preparedCacheBytes -= _preparedCache[oldest].Wav.LongLength;
                _preparedCache.Remove(oldest);
            }
            _preparedCache[path] = (wav, ++_preparedCacheUseClock);
            _preparedCacheBytes += wav.LongLength;
        }
    }

    /// <summary>
    /// Read and DECODE off the audio thread, so what the worker receives is
    /// already samples.
    ///
    /// This is where the whole cost of a cue now lives - the MPQ read, the header
    /// repair, the MP3 decode - and none of it is anywhere near playback. Started
    /// at request time so it overlaps whatever the worker is doing, and job order
    /// is unchanged: the worker still runs plays and stops as they were asked for.
    /// </summary>
    private Task<PreparedSource> PrepareFile(long voiceId, string path)
    {
        _ = voiceId;
        string normalizedPath = path.Replace('/', '\\');

        // Creator previews are replaceable at runtime and deliberately bypass
        // both the prepared cache and the in-flight table. Snapshot the caller's
        // bytes before background repair/decoding can touch them.
        if (_customFiles.TryGetValue(normalizedPath, out byte[]? custom))
        {
            byte[] snapshot = (byte[])custom.Clone();
            return Task.Run(() => PrepareBytes(normalizedPath, snapshot, cacheable: false));
        }

        Task<PreparedSource> preparation;
        lock (_preparedCacheLock)
        {
            if (_preparedCache.TryGetValue(normalizedPath,
                    out (byte[] Wav, long UsedAt) hit))
            {
                _preparedCache[normalizedPath] =
                    (hit.Wav, ++_preparedCacheUseClock);
                return Task.FromResult(new PreparedSource(hit.Wav));
            }

            if (_preparingSources.TryGetValue(normalizedPath, out preparation!))
                return preparation;

            preparation = Task.Run(() =>
            {
                byte[]? bytes = _mpq.ReadFile(normalizedPath);
                return PrepareBytes(normalizedPath, bytes, cacheable: true);
            });
            _preparingSources[normalizedPath] = preparation;
        }

        // Remove only the task we installed. A synchronously-completed failure
        // may run this continuation immediately; registration happens after the
        // dictionary assignment, so it cannot leave a dead entry behind.
        _ = preparation.ContinueWith(completed =>
        {
            lock (_preparedCacheLock)
            {
                if (_preparingSources.TryGetValue(normalizedPath, out Task<PreparedSource>? current) &&
                    ReferenceEquals(current, completed))
                    _preparingSources.Remove(normalizedPath);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return preparation;
    }

    private PreparedSource PrepareBytes(string path, byte[]? bytes, bool cacheable)
    {
        if (bytes is null || bytes.Length == 0)
        {
            Console.WriteLine($"[audio] '{path}' not found in the MPQs");
            return default;
        }
        if (Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            if (!Mp3Decoder.TryDecode(bytes, path, out byte[] decoded))
                return default;
            if (cacheable) PreparedCachePut(path, decoded);
            return new PreparedSource(decoded);
        }

        SanitizeWavHeader(bytes);
        if (cacheable) PreparedCachePut(path, bytes);
        return new PreparedSource(bytes);
    }

    private void PlayOnWorker(long voiceId, VoiceRequestState requestState,
        string path, Task<PreparedSource> fileTask,
        bool looping, float gain, float pan, uint playbackFrequency,
        string category, bool announce, AudioVoicePriorityLaw.Rank rank)
    {
        // Stopped before it ever started: do not open a device for a dead voice.
        if (!_live.ContainsKey(voiceId)) return;

        // NEVER park the one audio thread behind a decode. While this file is
        // still being prepared, re-arm the job as a continuation and return, so
        // stops, volume drains and other voices keep flowing — a fade used to
        // freeze mid-curve for the whole decode and then land in a lump. Per-voice
        // stop-vs-play order is preserved by the _live check above; order BETWEEN
        // voices is not load-bearing (the shared renderer admits them independently).
        if (!fileTask.IsCompleted)
        {
            fileTask.ContinueWith(_ => Enqueue(() => PlayOnWorker(
                    voiceId, requestState, path, fileTask, looping, gain, pan, playbackFrequency,
                    category, announce, rank)),
                TaskContinuationOptions.ExecuteSynchronously);
            return;
        }

        PreparedSource source;
        try
        {
            source = fileTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[audio] '{path}' could not be prepared ({category}) - {ex.Message}");
            RetireLive(voiceId);
            return;
        }
        if (source.Pcm is not { } pcmBytes)
        {
            // Loudly, always: a missing file that presents later as "the zone
            // has no music" is the vmap lesson all over again.
            Console.WriteLine($"[audio] '{path}' could not be decoded ({category})");
            RetireLive(voiceId);
            return;
        }

        // Opening the process's first physical output may involve a slow driver.
        // Keep it outside the cancellation gate: the returned logical voice is
        // inactive, so Stop can win immediately and the source still cannot emit.
        WaveOutVoice? voice = WaveOutVoice.OpenPending(
            pcmBytes, looping, gain, pan, playbackFrequency);
        if (voice is null)
        {
            if (_live.ContainsKey(voiceId))
                Console.WriteLine($"[audio] waveOut refused '{path}' ({category})");
            RetireLive(voiceId);
            return;
        }

        lock (requestState.Gate)
        {
            if (requestState.Cancelled || !_live.ContainsKey(voiceId))
            {
                voice.Dispose();
                return;
            }
            if (_pendingVolume.TryGetValue(voiceId, out PendingMix admissionMix))
                gain = admissionMix.Volume / 1000f;
            if (!MakeRoomForVoice(category, rank, gain))
            {
                voice.Dispose();
                RetireLive(voiceId);
                return;
            }
            // A fade or moving emitter may have changed while its file was decoding.
            // SetMix primes an inactive source, so even a change received while
            // the native output was opening becomes its first rendered sample —
            // there is no one-period stale-volume chirp.
            if (_pendingVolume.TryRemove(voiceId, out PendingMix pendingMix))
            {
                gain = pendingMix.Volume / 1000f;
                if (pendingMix.Pan is int pendingPan) pan = pendingPan / 1000f;
                voice.SetMix(gain, pan);
            }
            if (!ActivateNoDuplicate(voiceId, voice))
            {
                voice.Dispose();
                RetireLive(voiceId);
                return;
            }
            _voices[voiceId] = new Voice(
                voiceId, category, Environment.TickCount64, looping, voice, path, rank) { Gain = gain };
            voice.Activate();
            Interlocked.Increment(ref _routes);
            if (announce)
                Console.WriteLine($"[audio] playing '{path}' ({category}, loop={looping}, " +
                                  $"{pcmBytes.Length / 1024} KB resident)");
        }
    }

    /// <summary>
    /// Keep the logical mix bounded, spending the budget by importance
    /// (<see cref="AudioVoicePriorityLaw"/>). Three limits apply: the whole mix, other units'
    /// voices together, and other units' loops. When a limit is reached the best victim inside
    /// it (lowest rank, then quietest, then oldest) is stopped only if it is strictly less
    /// important than the incoming cue; otherwise the INCOMING cue is dropped. Cutting a sound
    /// that is already playing is what the ear hears as chop; a cue that never starts in a
    /// crowd is not missed. Music and ambience beds are never stolen.
    /// </summary>
    private bool MakeRoomForVoice(string category, AudioVoicePriorityLaw.Rank rank, float gain)
    {
        bool other = AudioVoicePriorityLaw.IsOtherUnit(rank);
        int total = _voices.Count, others = 0, otherLoops = 0;
        foreach (Voice live in _voices.Values)
        {
            if (!AudioVoicePriorityLaw.IsOtherUnit(live.Rank)) continue;
            others++;
            if (live.Rank == AudioVoicePriorityLaw.Rank.OtherLoop) otherLoops++;
        }
        // Tightest limit first: a loop over the loop cap competes with loops only, an other-unit
        // cue over its budget with other-unit voices only, anything else with the whole mix.
        Func<Voice, bool>? pool =
            rank == AudioVoicePriorityLaw.Rank.OtherLoop && otherLoops >= AudioVoicePriorityLaw.OtherUnitLoops
                ? v => v.Rank == AudioVoicePriorityLaw.Rank.OtherLoop
            : other && others >= AudioVoicePriorityLaw.OtherUnitVoices
                ? v => AudioVoicePriorityLaw.IsOtherUnit(v.Rank)
            : total >= MaxConcurrentVoices
                ? v => true
            : null;
        if (pool is null) return true;

        Voice? victim = null;
        foreach (Voice candidate in _voices.Values)
        {
            if (!pool(candidate) || candidate.Rank == AudioVoicePriorityLaw.Rank.Bed) continue;
            if (victim is null || AudioVoicePriorityLaw.IsBetterVictim(
                    candidate.Rank, candidate.Gain, candidate.StartedAtMs,
                    victim.Rank, victim.Gain, victim.StartedAtMs))
                victim = candidate;
        }
        if (victim is not null &&
            AudioVoicePriorityLaw.MayReplace(victim.Rank, victim.Gain, rank, gain))
        {
            StopOnWorker(victim.Id);
            return true;
        }
        _voiceBudgetDrops++;
        long now = Environment.TickCount64;
        if (now - _lastVoiceBudgetReportAtMs >= 2000)
        {
            _lastVoiceBudgetReportAtMs = now;
            Console.WriteLine($"[audio] voice budget: {total} open ({others} other-unit, " +
                              $"{otherLoops} other-unit loops); dropped {_voiceBudgetDrops} " +
                              $"lower-priority cue(s) and {Interlocked.Read(ref _otherUnitRepeatsDropped)} " +
                              $"other-unit repeat(s) so far - latest '{category}' ({rank})");
        }
        return false;
    }

    /// <summary>Concurrent logical sources. Vanilla's own Sound_NumChannels sat in
    /// this range; the ceiling bounds both mix work and pathological cue overlap.</summary>
    private const int MaxConcurrentVoices = AudioVoicePriorityLaw.MaximumVoices;

    private long _voiceBudgetDrops;
    private long _lastVoiceBudgetReportAtMs;
    private bool _probeArmedAnnounced;

    private void StopOnWorker(long voiceId)
    {
        RetireLive(voiceId);
        if (!_voices.Remove(voiceId, out Voice? voice)) return;
        ReportVoiceProgress(voice);
        _pendingVolume.TryRemove(voiceId, out _);
        voice.Pcm.Dispose();
        Interlocked.Increment(ref _stops);
    }

    // ── file repair ──────────────────────────────────────────────────────────

    /// <summary>
    /// Repair the malformed fmt chunk many vanilla WAVs ship with: stereo
    /// 16-bit files whose blockAlign says 2 (and whose byteRate follows suit).
    /// MCI's waveaudio device validates blockAlign == channels * bits / 8 and
    /// refuses the file with MCIERR 326 ("no wave device can play this
    /// format") - which is how every ZoneAmbience bed came out silent. The
    /// data itself is fine; only the two derived header fields lie. Patched in
    /// place on our own copy of the bytes, PCM (format tag 1) only.
    /// </summary>
    /// <summary>Wrap raw PCM in the RIFF/WAVE container the waveOut path takes.
    /// The decoders hand back samples; this is the only thing between them and the
    /// driver.</summary>
    internal static byte[] BuildWav(byte[] pcm, int channels, int rate, int bits)
    {
        var wav = new byte[HeaderBytes + pcm.Length];
        WriteWavHeader(wav, pcm.Length, channels, rate, bits);
        pcm.CopyTo(wav, HeaderBytes);
        return wav;
    }

    /// <summary>The canonical 44-byte RIFF/WAVE header, written IN PLACE.
    ///
    /// Exposed separately so a decoder can lay its samples straight down after it
    /// instead of building a PCM array and having it copied in here. For a 28 MB
    /// music track that difference is two whole Large Object Heap allocations and
    /// two full-track copies, at the one moment - a track starting - when the
    /// client is usually also streaming a world. See Mp3Decoder.
    ///
    /// The CHUNK SIZES are authoritative, not the array length: a caller may hand
    /// over a buffer with slack after the samples, and every reader here
    /// (TryDescribe, SanitizeWavHeader) clamps to the data chunk.</summary>
    internal const int HeaderBytes = 44;

    internal static void WriteWavHeader(byte[] wav, int pcmBytes, int channels, int rate, int bits)
    {
        int blockAlign = channels * bits / 8;
        void Ascii(int at, string tag)
        {
            for (int i = 0; i < 4; i++) wav[at + i] = (byte)tag[i];
        }
        Ascii(0, "RIFF");
        BitConverter.TryWriteBytes(wav.AsSpan(4, 4), 36 + pcmBytes);
        Ascii(8, "WAVE");
        Ascii(12, "fmt ");
        BitConverter.TryWriteBytes(wav.AsSpan(16, 4), 16);
        BitConverter.TryWriteBytes(wav.AsSpan(20, 2), (ushort)1);   // PCM
        BitConverter.TryWriteBytes(wav.AsSpan(22, 2), (ushort)channels);
        BitConverter.TryWriteBytes(wav.AsSpan(24, 4), rate);
        BitConverter.TryWriteBytes(wav.AsSpan(28, 4), rate * blockAlign);
        BitConverter.TryWriteBytes(wav.AsSpan(32, 2), (ushort)blockAlign);
        BitConverter.TryWriteBytes(wav.AsSpan(34, 2), (ushort)bits);
        Ascii(36, "data");
        BitConverter.TryWriteBytes(wav.AsSpan(40, 4), pcmBytes);
    }

    private static void SanitizeWavHeader(byte[] wav)
    {
        if (wav.Length < 44 ||
            wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' ||
            wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
            return;

        // Walk the chunks to the fmt chunk; it is almost always at 12 but a
        // LIST chunk before it costs nothing to step over.
        int at = 12;
        while (at + 8 <= wav.Length)
        {
            uint chunkSize = BitConverter.ToUInt32(wav, at + 4);
            if (wav[at] == 'f' && wav[at + 1] == 'm' && wav[at + 2] == 't' && wav[at + 3] == ' ')
            {
                int fmt = at + 8;
                if (fmt + 16 > wav.Length) return;
                ushort format = BitConverter.ToUInt16(wav, fmt);
                if (format != 1) return;   // PCM only; compressed blockAligns are real
                ushort channels = BitConverter.ToUInt16(wav, fmt + 2);
                uint rate = BitConverter.ToUInt32(wav, fmt + 4);
                ushort bits = BitConverter.ToUInt16(wav, fmt + 14);
                if (channels is 0 or > 8 || bits is 0 or > 32) return;
                ushort expectedAlign = (ushort)(channels * bits / 8);
                uint expectedRate = rate * expectedAlign;
                if (BitConverter.ToUInt16(wav, fmt + 12) != expectedAlign)
                    BitConverter.TryWriteBytes(wav.AsSpan(fmt + 12, 2), expectedAlign);
                if (BitConverter.ToUInt32(wav, fmt + 8) != expectedRate)
                    BitConverter.TryWriteBytes(wav.AsSpan(fmt + 8, 4), expectedRate);
                return;
            }
            at += 8 + (int)chunkSize + ((int)chunkSize & 1);
        }
    }

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposeCompleted) return;
            lock (_enqueueLock)
            {
                _shutdown = true;
                _jobSignal.Set();
            }
            if (!_worker.Join(TimeSpan.FromSeconds(2)))
            {
                // Keep the event and shared output alive while their owner thread
                // might still touch them. A later Dispose can finish cleanup after
                // the background worker observes _shutdown.
                Console.WriteLine("[audio] control worker did not stop; output resources " +
                                  "retained until cleanup can be retried");
                return;
            }
            if (!WaveOutVoice.DrainPool())
            {
                // Keep this owner retryable while WaveOutVoice retains the shared
                // renderer and its still-native buffers.
                Console.WriteLine("[audio] output cleanup incomplete; retry Dispose shortly");
                return;
            }
            _jobSignal.Dispose();
            _disposeCompleted = true;
        }
    }

}
