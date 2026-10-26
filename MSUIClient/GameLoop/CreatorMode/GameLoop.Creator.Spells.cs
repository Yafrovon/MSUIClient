using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator;
using MSUIClient.Formats;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// The spell workshop: pick any spell, loop any phase, and tune its emitters in
// realtime. Edits are byte-patches over the effect M2s (the MangosSuperUI
// patchers, ported in Creator/), hot-swapped into SpellEffectSource via its
// model-override layer - the respawning loop shows every change within one
// cycle, no MPQ rebuild, no restart.
//
// Every effect model's TEXTURE TABLE is exposed: each phase lists the actual
// BLPs it is built from (with thumbnails), and each BLP that particles
// reference gets its OWN hue dial - so a phase composed of several images can
// be recolored per image, not just as a whole. The whole-model hue shift
// remains as a master dial; per-BLP dials override it for their emitters.
//
// The tuned spell's design is captured into the ongoing SESSION json (see
// GameLoop.Creator.Session.cs): the byte-patched effect M2s, recolored BLPs and
// custom audio, all base64-embedded, which MangosSuperUI's Spell Completer web
// app consumes to build the finished custom spell.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private static readonly string[] CreatorBlendModes =
    {
        "0 Opaque", "1 Alpha key", "2 Alpha blend", "3 Add (no alpha)",
        "4 Additive", "5 Mod", "6 Mod x2"
    };
    private static readonly string[] CreatorEmitterTypes = { "1 Plane", "2 Sphere", "3 Spline" };
    private static readonly ushort[] CreatorEmitterTypeIds = { 1, 2, 3 };
    private static readonly string[] CreatorLineAxes = { "Local X", "Local Y", "Local Z (up)" };
    private static readonly string[] CreatorLineCounts = { "3 sources", "5 sources", "7 sources", "9 sources" };
    private int _creatorLineAxis;
    private int _creatorLineCountChoice = 1;
    private float _creatorLineSpacing = 1f;

    /// <summary>A per-BLP hue dial: rotate every particle color of the emitters that
    /// reference this texture toward Color, preserving luminance/saturation.</summary>
    private sealed class CreatorTexHue
    {
        public bool On;
        public Vector3 Color = new(1f, 0.4f, 0.1f);
    }

    /// <summary>One user-added emitter: a clone of SourceIndex retargeted at
    /// TextureSlot. Its live index is OriginalEmitterCount + list position.</summary>
    private sealed class CreatorAddedEmitter
    {
        public int SourceIndex;
        public int TextureSlot;
    }

    private enum CreatorAudioCue
    {
        Precast,
        Cast,
        Missile,
        Impact,
        State,
        Channel,
        Area,
    }

    private static readonly CreatorAudioCue[] CreatorAudioCueOrder =
    [
        CreatorAudioCue.Precast,
        CreatorAudioCue.Cast,
        CreatorAudioCue.Missile,
        CreatorAudioCue.Impact,
        CreatorAudioCue.State,
        CreatorAudioCue.Channel,
        CreatorAudioCue.Area,
    ];

    /// <summary>A creator-owned replacement for one phase's SoundEntries cue.
    /// Bytes stay in memory for immediate preview and are embedded into the
    /// session document.</summary>
    private sealed class CreatorAudioTrack
    {
        public required string SourcePath;
        public required string MpqPath;
        public required byte[] Bytes;
        public float Volume = 1f;
        public bool Looping;
        public bool NoDuplicates;
        public uint SoundType = 1;
        public uint ExtraFlags;
        public uint Eax;
        public float MinDistance;
        public float CutoffDistance;
    }

    /// <summary>Everything the workshop knows about one effect M2 being tuned.</summary>
    private sealed class CreatorModelDoc
    {
        public required string Path;
        public required byte[] Original;
        public byte[] Working = [];
        public List<EmitterSnapshot> Emitters = [];
        public readonly Dictionary<int, EmitterPatch> Edits = [];
        // Mesh layers (batches of the model's own geometry) and ribbon emitters, read from
        // the working bytes like Emitters; edits and off-switches patch the bytes on rebuild
        // (GameLoop.Creator.MeshRibbon.cs, SPELL_CREATOR_IDE §2.5).
        public List<MeshSnapshot> Meshes = [];
        public List<RibbonSnapshot> Ribbons = [];
        public readonly HashSet<int> HiddenSubmeshes = [];
        public readonly Dictionary<int, MeshPatch> MeshEdits = [];
        public readonly HashSet<int> DisabledRibbons = [];
        public readonly Dictionary<int, RibbonPatch> RibbonEdits = [];
        // The skeleton (GameLoop.Creator.Bones.cs): pivots and poses patch in place.
        public List<BoneSnapshot> Bones = [];
        public readonly Dictionary<int, BonePatch> BoneEdits = [];
        /// <summary>Whole-track rewrites by track id ("e3.rate", "b2.rotation" - M2TrackPatcher), from
        /// the dope sheet (SPELL_CREATOR_PLAN.md P3.1/P3.2). Applied after every other edit, so a
        /// rewritten track wins over a first-key dial on the same track.</summary>
        public readonly Dictionary<string, Creator.TrackKeys> TrackEdits = new(StringComparer.Ordinal);
        /// <summary>Emitters / trails grafted from other models (GameLoop.Creator.Transplant.cs).</summary>
        public readonly List<CreatorTransplant> Transplants = [];
        // Cloned ribbons / mesh layers (sources), like AddedEmitters: clone i of each kind
        // lives at Original*Count + i, so edits and off-switches address clones by index.
        public readonly List<int> AddedRibbons = [];
        public readonly List<int> AddedLayers = [];
        public int OriginalRibbonCount;
        public int OriginalLayerCount;
        // The M2's texture table (the actual BLPs), with emitter back-references.
        public List<Creator.M2TextureEntry> Textures = [];
        public readonly Dictionary<int, CreatorTexHue> TextureHues = [];
        // True palette swaps: the BLP's own pixels are hue-mapped (live via the
        // renderers' tint layer, and baked into the export). For textures whose
        // color is authored into the art, where the emitter-hue dial does nothing.
        public readonly Dictionary<int, CreatorTexHue> TextureTints = [];
        // Slot -> replacement BLP path: splice any existing image into this model
        // (patched into the M2's texture table, so it exports too). Also the way
        // to recolor a SHARED BLP for one phase only: swap the slot to different
        // art first, then tint the swap - path-keyed tints stop bleeding.
        public readonly Dictionary<int, string> TextureSwaps = [];
        // Emitters switched off wholesale (their emission is zeroed in the bytes).
        public readonly HashSet<int> DisabledEmitters = [];
        // User-added emitters (clones appended to the M2's emitter array), in
        // append order. The authored array size is OriginalEmitterCount; added
        // emitter i lives at index OriginalEmitterCount + i, so Edits and
        // DisabledEmitters address clones exactly like authored emitters.
        public readonly List<CreatorAddedEmitter> AddedEmitters = [];
        public int OriginalEmitterCount;
        // Emitter index -> resolved geometry-model path for emitters that spawn a
        // per-particle M2. Those emitters NEVER draw their billboard texture: the
        // on-screen pixels come from the geometry file's OWN texture table, so a
        // swap on their host slot must follow the pixels into that model.
        public readonly Dictionary<int, string> EmitterGeometry = [];
        // Whole-model dials (multipliers over the authored values + hue shift).
        public bool HueShift;
        public Vector3 HueColor = new(1f, 0.4f, 0.1f);
        public float RateMul = 1f, ScaleMul = 1f, LifeMul = 1f, SpeedMul = 1f, GravityAdd;
        public bool Modified;
    }

    private sealed class CreatorSpellDoc
    {
        public required SpellInfo Info;
        public SpellVisualStages Stages;
        public readonly List<(SpellStage Stage, string Path)> PhaseModels = [];
        public string? MissilePath;
        public readonly Dictionary<string, CreatorModelDoc> Models = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Geometry-particle models (per-particle M2s like cloud puffs):
        /// path -> the host model file that spawns them. Their art lives in their
        /// OWN texture tables, invisible from the host's slots.</summary>
        public readonly Dictionary<string, string> GeometryHosts = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<CreatorAudioCue, CreatorAudioTrack> Audio = [];
        /// <summary>Spells this one triggers, made in the same design (GameLoop.Creator.Children.cs).</summary>
        public readonly List<CreatorChildSpell> Children = [];
        /// <summary>The totem this spell plants, made here (GameLoop.Creator.Actors.cs).</summary>
        public CreatorActor? Actor;
        // The kit level (GameLoop.Creator.Composition.cs): per stage, which models sit in
        // which attachment slot at what scale, and the caster animation; the missile.
        public readonly Dictionary<SpellStage, CreatorStageComposition> Composition = [];
        public string? MissileOverride;
        public float MissileScale = 1f;
        /// <summary>A SoundEntries id picked for the missile's flight loop (SpellVisual f10), P3.9.</summary>
        public uint? MissileSound;
        // Imported art (GameLoop.Creator.TextureImport.cs): custom MPQ path -> BLP bytes,
        // served by the mount's override layer and shipped in the session.
        public readonly Dictionary<string, byte[]> ImportedTextures = new(StringComparer.OrdinalIgnoreCase);
        // The BLPs the Sketch compiler last produced, served through the same override layer.
        // Kept apart from ImportedTextures because they are OUTPUT, regenerated from the pieces:
        // the export ships the fresh set from a one-shot recompile and the design never stores
        // them (SPELL_CREATOR_PLAN.md P0.1/P0.2).
        public readonly Dictionary<string, byte[]> SketchTextures = new(StringComparer.OrdinalIgnoreCase);
        // A stable identity for this design across saves, pushes and reopens - a temp name can
        // be retyped and a new spell's local id is only a session counter.
        public string DesignId = Guid.NewGuid().ToString("N");
        // Free-form geometry (GameLoop.Creator.Sketch.cs, shared_docs/SPELL_SKETCH.md): the
        // authored pieces per stage. These are the SOURCE - the compiled M2 they produce is
        // an ordinary phase model in Models above - so they are what has to be saved for a
        // spell to reopen editable.
        public readonly Dictionary<SpellStage, Creator.Sketch.SketchDoc> Sketches = [];
        // What the AUTHOR says this spell is made of (GameLoop.Creator.Anatomy.cs). A spell's
        // anatomy is not derivable from its art: an empty precast and a precast you decided
        // not to have look identical in the data, and only one of them is a to-do.
        public readonly HashSet<SpellStage> PlannedStages = [];
        public bool PlannedMissile;
        public bool PlanStarted;
        // What the spell DOES (GameLoop.Creator.Rules.cs, SPELL_CREATOR_PLAN.md P1): created on
        // first use from the spell's own Spell.dbc row (or the from-zero template).
        public Creator.Spells.SpellRules? Rules;
        // The lasting ground part (GameLoop.Creator.Area.cs, P2.5).
        public CreatorAreaComposition Area = new();
    }

    private CreatorSpellDoc? _creatorSpell;
    private readonly byte[] _creatorSpellSearchBuf = new byte[64];
    private List<SpellInfo>? _creatorSpellResults;
    private readonly byte[] _creatorNewSpellNameBuf = new byte[48];

    /// <summary>Ids handed to spells that do not exist in Spell.dbc yet.
    ///
    /// NOT zero, for two reasons that both bite. ReapPresentedEffect treats
    /// _presentedEffectSpell == 0 as "nothing is presented" and returns early, so a spell with
    /// id 0 would spawn kits that are never reaped. And PresentSpellEffect's first guard is a
    /// Spell.dbc lookup, so a new spell has to be recognisable as the creator's own before that
    /// lookup gets a chance to fail. 1.12 tops out around 30,000 real ids; this is far clear of
    /// them and of the 0xFFFFFFFF none-sentinel.</summary>
    private const uint CreatorNewSpellIdBase = 9_000_000;
    private uint _creatorNextNewSpellId = CreatorNewSpellIdBase;

    /// <summary>Is this id one the creator invented, rather than one from the archives?</summary>
    private static bool IsCreatorNewSpellId(uint id) => id >= CreatorNewSpellIdBase;
    private bool _creatorSpellSearchDirty = true;

    // Which phases the loop plays each cycle - the user picks EXACTLY what loops.
    private bool _creatorLoopPrecast;
    private bool _creatorLoopCast = true;
    private bool _creatorLoopMissilePhase;
    private bool _creatorLoopImpactPhase = true;
    private bool _creatorLoopStateHold;
    private bool _creatorLoopChannelHold;
    private bool _creatorLoopAreaHold;

    // The area machine (Blizzard / Rain of Fire / Flamestrike rains): the live
    // client drives it from the server's DynamicObject; the creator drives the
    // SAME SpawnAreaVisual machinery from a synthetic key at the target spot.
    private const ulong CreatorAreaGuid = 0xF000_0000_0000_00F0UL;
    private float _creatorAreaRadius = 8f;
    private bool _creatorAreaActive;
    // A deferred ground spell's area lives from the missile's ARRIVAL for the rules' duration -
    // the server places the DynamicObject then (SPELL_CUSTOM_DEFERRED_DEST); the preview follows.
    private double _creatorAreaFrom = double.MaxValue, _creatorAreaUntil = double.MinValue;

    private bool _creatorLoopOn;
    private float _creatorLoopPeriod = 2f;
    private double _creatorLoopNextAt;
    private double _creatorLoopCastAt = double.MaxValue;
    private double _creatorLoopMissileAt = double.MaxValue;
    private double _creatorLoopImpactAt = double.MaxValue;
    private double _creatorMissileSoundEndsAt = double.MaxValue;
    private readonly Dictionary<CreatorAudioCue, long> _creatorAudioVoices = [];
    private readonly HashSet<CreatorAudioCue> _creatorSustainedAudio = [];
    private long _creatorAudioPreviewVoice;
    private CreatorAudioCue? _creatorAudioPickerCue;
    private string _creatorAudioPickerDir = "";
    private string _creatorAudioPickerError = "";
    private string _creatorExportStatus = "";

    // ── loop machine (called every Update while the creator owns the world) ──
    //
    // Each cycle sequences the CHECKED phases: precast fires at the tick, cast
    // releases after a short precast hold, the missile launches with the cast,
    // and impact lands when the missile arrives (or shortly after the cast when
    // no missile is looped). Impact is anchored ON the spawned target when one
    // exists. State/channel are holds, re-presented each tick so byte-patches
    // keep landing.

    private void UpdateCreatorSpellLoop()
    {
        if (!_creatorWorldRequested) return;
        UpdateCreatorAreaVisual(SpellClockNow);
        // A paused effect clock holds the loop too: nothing re-presents mid-hold.
        if (!_creatorLoopOn || _creatorSpell is null || CreatorSpellPaused) return;
        double now = SpellClockNow;
        uint spell = _creatorSpell.Info.Id;

        UpdateCreatorSustainedAudio();
        if (now >= _creatorMissileSoundEndsAt)
        {
            _creatorMissileSoundEndsAt = double.MaxValue;
            StopCreatorAudio(CreatorAudioCue.Missile);
        }

        if (now >= _creatorLoopCastAt)
        {
            _creatorLoopCastAt = double.MaxValue;
            StopCreatorAudio(CreatorAudioCue.Precast);
            if (!SketchSoloBlocks(SpellStage.Cast) && PresentSpellEffect(spell, "cast"))
                StartCreatorAudio(CreatorAudioCue.Cast, LocalPlayerGuid,
                    _controller?.Position ?? Vector3.Zero);
        }
        if (now >= _creatorLoopMissileAt)
        {
            _creatorLoopMissileAt = double.MaxValue;
            double flight = SketchSoloBlocks(SpellStage.Impact) ? 0 : SpawnCreatorMissile();
            if (flight > 0)
            {
                StartCreatorAudio(CreatorAudioCue.Missile, LocalPlayerGuid,
                    _controller?.Position ?? Vector3.Zero, forceLoop: true);
                _creatorMissileSoundEndsAt = now + flight;
            }
            if (_creatorLoopImpactPhase)
                _creatorLoopImpactAt = now + (flight > 0 ? flight : 0.5);
        }
        if (now >= _creatorLoopImpactAt)
        {
            _creatorLoopImpactAt = double.MaxValue;
            StopCreatorAudio(CreatorAudioCue.Missile);
            _creatorMissileSoundEndsAt = double.MaxValue;
            if (CreatorAreaWaitsForArrival(_creatorSpell))
            {
                _creatorAreaFrom = now;
                _creatorAreaUntil = now + CreatorAreaPreviewSeconds(_creatorSpell);
                RestartCreatorArea();
            }
            ulong target = CreatorMissileTargetGuid();
            ulong anchor = target != 0 ? target : LocalPlayerGuid;
            // A ground spell lands on its SPOT unless a dummy stands there to be hit.
            Vector3? spot = target == 0 && CreatorGroundTargeted(_creatorSpell) ? CreatorGroundPreviewPoint() : null;
            if (!SketchSoloBlocks(SpellStage.Impact) &&
                PresentSpellEffect(spell, "impact", target != 0 ? target : null, spot))
            {
                var pose = SpellEffectUnitPose(anchor);
                StartCreatorAudio(CreatorAudioCue.Impact, anchor,
                    pose.Found ? pose.Position : _controller?.Position ?? Vector3.Zero);
            }
        }

        if (now < _creatorLoopNextAt) return;
        _creatorLoopNextAt = now + Math.Max(_creatorLoopPeriod, 0.25f);

        bool any = false;
        if (_creatorLoopPrecast)
        {
            if (!SketchSoloBlocks(SpellStage.Precast) && PresentSpellEffect(spell, "precast"))
                StartCreatorAudio(CreatorAudioCue.Precast, LocalPlayerGuid,
                    _controller?.Position ?? Vector3.Zero);
            any = true;
        }
        // Cast (and the missile it releases) waits out a short precast hold when
        // precast is looped too; otherwise it fires at the tick.
        double castDelay = _creatorLoopPrecast ? Math.Min(_creatorLoopPeriod * 0.35, 1.2) : 0.0;
        if (_creatorLoopCast) { _creatorLoopCastAt = now + castDelay; any = true; }
        if (_creatorLoopMissilePhase) { _creatorLoopMissileAt = now + castDelay; any = true; }
        else if (_creatorLoopImpactPhase)
        {
            // No missile to carry it: impact lands shortly after the cast release.
            double impactDelay = castDelay +
                (_creatorLoopCast ? Math.Min(_creatorLoopPeriod * 0.45, 0.9) : 0.0);
            _creatorLoopImpactAt = now + impactDelay;
            any = true;
        }
        if (_creatorLoopStateHold) { PresentSpellEffect(spell, "state"); any = true; }
        if (_creatorLoopChannelHold) { PresentSpellEffect(spell, "channel"); any = true; }
        if (_creatorLoopAreaHold) any = true;   // the hold runs continuously below
        if (!any)
        {
            _creatorLoopOn = false;   // nothing checked - stop rather than spin
            StopAllCreatorAudio();
        }
    }

    private uint? CreatorAuthoredSound(CreatorSpellDoc doc, CreatorAudioCue cue)
    {
        if (_spellVisualCatalog is null) return null;
        if (cue == CreatorAudioCue.Missile)
            return doc.Stages.MissileSound is 0 or uint.MaxValue ? null : doc.Stages.MissileSound;
        if (cue == CreatorAudioCue.Area)
            return _spellVisualCatalog.TryGetAreaVisual(doc.Info.VisualId,
                out SpellAreaVisualInfo area) ? area.Sound : null;
        uint kitId = cue switch
        {
            CreatorAudioCue.Precast => doc.Stages.Precast,
            CreatorAudioCue.Cast => doc.Stages.Cast,
            CreatorAudioCue.Impact => doc.Stages.Impact,
            CreatorAudioCue.State => doc.Stages.State,
            CreatorAudioCue.Channel => doc.Stages.Channel,
            _ => 0,
        };
        return kitId != 0 && _spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit)
            ? kit.Sound : null;
    }

    private uint CreatorAudioKitId(CreatorSpellDoc doc, CreatorAudioCue cue) => cue switch
    {
        CreatorAudioCue.Precast => doc.Stages.Precast,
        CreatorAudioCue.Cast => doc.Stages.Cast,
        CreatorAudioCue.Impact => doc.Stages.Impact,
        CreatorAudioCue.State => doc.Stages.State,
        CreatorAudioCue.Channel => doc.Stages.Channel,
        CreatorAudioCue.Area => doc.Stages.AreaKit,
        _ => 0,
    };

    // A NEW spell has no source kits: a phase the design plans or composes gets one on demand in
    // the Completer, so it can carry a sound too (P3.9).
    private bool CreatorAudioAvailable(CreatorSpellDoc doc, CreatorAudioCue cue) => cue switch
    {
        CreatorAudioCue.Missile => doc.Info.Speed > 0 || doc.MissilePath is { Length: > 0 } ||
                                   doc.Stages.MissileSound != 0 || CreatorEffectiveMissile(doc) is { Length: > 0 } ||
                                   (doc.Rules?.Speed ?? 0f) > 0f,
        CreatorAudioCue.Area => CreatorAudioKitId(doc, cue) != 0 || doc.Area.Any,
        _ => CreatorAudioKitId(doc, cue) != 0 ||
             (CreatorAudioStage(cue) is { } stage && (doc.PlannedStages.Contains(stage) ||
              (doc.Composition.TryGetValue(stage, out CreatorStageComposition? c) && c.Slots.Any(s => s is { Length: > 0 })))),
    };

    private static SpellStage? CreatorAudioStage(CreatorAudioCue cue) => cue switch
    {
        CreatorAudioCue.Precast => SpellStage.Precast,
        CreatorAudioCue.Cast => SpellStage.Cast,
        CreatorAudioCue.Impact => SpellStage.Impact,
        CreatorAudioCue.State => SpellStage.State,
        CreatorAudioCue.Channel => SpellStage.Channel,
        _ => null,
    };

    /// <summary>The game sound a cue plays: one picked in the workshop, else the source's own.</summary>
    private uint? CreatorPickedSound(CreatorSpellDoc doc, CreatorAudioCue cue) => cue switch
    {
        CreatorAudioCue.Missile => doc.MissileSound,
        CreatorAudioCue.Area => doc.Area.Sound,
        _ => CreatorAudioStage(cue) is { } stage && doc.Composition.TryGetValue(stage, out CreatorStageComposition? c) &&
             c.Sound != c.AuthoredSound ? c.Sound : null,
    };

    private void SetCreatorPickedSound(CreatorSpellDoc doc, CreatorAudioCue cue, uint? sound)
    {
        switch (cue)
        {
            case CreatorAudioCue.Missile: doc.MissileSound = sound; break;
            case CreatorAudioCue.Area: doc.Area.Sound = sound; break;
            default:
                if (CreatorAudioStage(cue) is { } stage && doc.Composition.TryGetValue(stage, out CreatorStageComposition? c))
                    c.Sound = sound ?? c.AuthoredSound;
                break;
        }
    }

    private long PlayCreatorAudio(CreatorAudioCue cue, ulong owner, Vector3 source,
        bool forceLoop = false)
    {
        if (_creatorSpell is not { } doc || _spellSounds is null) return 0;
        Vector3 listener = _controller?.Position ?? source;
        if (doc.Audio.TryGetValue(cue, out CreatorAudioTrack? custom))
            return _spellSounds.PlayCustom($"creator:{doc.Info.Id}:{cue}", custom.MpqPath,
                custom.Bytes, owner, source, listener, custom.Volume,
                forceLoop || custom.Looping, custom.NoDuplicates,
                custom.MinDistance, custom.CutoffDistance, trackHold: false,
                extraFlags: custom.ExtraFlags, eax: custom.Eax);
        return _spellSounds.Play(CreatorPickedSound(doc, cue) ?? CreatorAuthoredSound(doc, cue), owner, source, listener,
            forceLoop, trackHold: false, category: $"creator-{cue.ToString().ToLowerInvariant()}");
    }

    private void StartCreatorAudio(CreatorAudioCue cue, ulong owner, Vector3 source,
        bool forceLoop = false)
    {
        StopCreatorAudio(cue);
        long voice = PlayCreatorAudio(cue, owner, source, forceLoop);
        if (voice != 0) _creatorAudioVoices[cue] = voice;
    }

    private void StopCreatorAudio(CreatorAudioCue cue)
    {
        if (_creatorAudioVoices.Remove(cue, out long voice)) _spellSounds?.Stop(voice);
    }

    private void StopAllCreatorAudio()
    {
        foreach (long voice in _creatorAudioVoices.Values) _spellSounds?.Stop(voice);
        _creatorAudioVoices.Clear();
        _creatorSustainedAudio.Clear();
        _creatorMissileSoundEndsAt = double.MaxValue;
        if (_creatorAudioPreviewVoice != 0) _spellSounds?.Stop(_creatorAudioPreviewVoice);
        _creatorAudioPreviewVoice = 0;
    }

    private void UpdateCreatorSustainedAudio()
    {
        void Sustain(CreatorAudioCue cue, bool wanted)
        {
            if (!wanted)
            {
                if (_creatorSustainedAudio.Remove(cue)) StopCreatorAudio(cue);
                return;
            }
            if (!_creatorSustainedAudio.Add(cue)) return;
            StartCreatorAudio(cue, LocalPlayerGuid,
                _controller?.Position ?? Vector3.Zero);
        }

        Sustain(CreatorAudioCue.State, _creatorLoopStateHold);
        Sustain(CreatorAudioCue.Channel, _creatorLoopChannelHold);
    }

    /// <summary>
    /// Keep the DynamicObject-style area visual (the falling-shard rain +
    /// looping centre model) alive at the target spot while the loop wants it -
    /// the exact machinery UpdateDynamicObjectVisuals drives on the live path,
    /// minus the server. Runs every Update so the rain follows a moving target
    /// and dies the moment the loop stops.
    /// </summary>
    private void UpdateCreatorAreaVisual(double now)
    {
        bool want = _creatorLoopOn && _creatorLoopAreaHold && _creatorSpell is { } doc &&
                    _spellEffects is not null && TryCreatorComposedArea(doc, out SpellAreaVisualInfo _) &&
                    (!CreatorAreaWaitsForArrival(doc) || (now >= _creatorAreaFrom && now < _creatorAreaUntil));
        if (!want)
        {
            if (_creatorAreaActive)
            {
                _spellEffects?.ReapArea(CreatorAreaGuid);
                _spellSounds?.StopHold(CreatorAreaGuid);
                StopCreatorAudio(CreatorAudioCue.Area);
                _creatorAreaActive = false;
            }
            return;
        }

        var spell = _creatorSpell!.Info.Id;
        TryCreatorComposedArea(_creatorSpell, out SpellAreaVisualInfo area);
        Vector3 position = CreatorAreaPosition();
        _creatorAreaRadius = CreatorAreaPreviewRadius(_creatorSpell);
        if (!_creatorAreaActive)
        {
            bool customAudio = _creatorSpell.Audio.ContainsKey(CreatorAudioCue.Area);
            bool loopingSound = customAudio
                ? _creatorSpell.Audio[CreatorAudioCue.Area].Looping
                : _spellSounds?.IsAuthoredLoop(area.Sound) == true;
            Action<uint, ulong, Vector3>? birthSound = !customAudio && !loopingSound &&
                area.Emitters.Count != 0
                ? (sound, key, at) => PlaySpellSoundAt(key, sound, at, trackHold: false)
                : null;
            int spawned = _spellEffects!.SpawnAreaVisual(
                CreatorAreaGuid, spell, area, position, _creatorAreaRadius, now, birthSound);
            if (customAudio || loopingSound)
                StartCreatorAudio(CreatorAudioCue.Area, LocalPlayerGuid, position,
                    forceLoop: loopingSound);
            else if (area.Emitters.Count == 0)
                StartCreatorAudio(CreatorAudioCue.Area, LocalPlayerGuid, position);
            _creatorAreaActive = true;
            Console.WriteLine($"[creator] area visual up: spell {spell} radius {_creatorAreaRadius:0.0} " +
                              $"emitters {area.Emitters.Count} " +
                              $"rate {area.Emitters.Sum(e => e.InstancesPerSecond):0.0}/s loaded {spawned}");
        }
        else
        {
            _spellEffects!.UpdateAreaVisual(CreatorAreaGuid, spell, position, _creatorAreaRadius);
        }
    }

    /// <summary>Where the area rain stands: on the targeted/last spawn, else 15 yd
    /// ahead of the player on the ground.</summary>
    private Vector3 CreatorAreaPosition()
    {
        if (_creatorSpell is { } groundDoc && CreatorGroundTargeted(groundDoc)) return CreatorGroundPreviewPoint();
        ulong target = CreatorMissileTargetGuid();
        if (target != 0 && _entities.TryGet(target, out var entity)) return entity.Position;
        if (_controller is null) return default;
        var forward = new Vector3(MathF.Cos(_controller.Yaw), MathF.Sin(_controller.Yaw), 0f);
        Vector3 spot = _controller.Position + forward * 15f;
        if (_terrain?.SampleHeight(spot.X, spot.Y) is float ground) spot.Z = ground;
        return spot;
    }

    /// <summary>Launch the missile at the creator target (or straight ahead) and
    /// return its flight time in seconds, -1 when this spell has no missile.</summary>
    private double SpawnCreatorMissile()
    {
        if (_creatorSpell is not { } missileDoc || CreatorEffectiveMissile(missileDoc) is not { Length: > 0 } path ||
            _spellEffects is null || _controller is null) return -1;

        Vector3 from = _controller.Position with { Z = _controller.Position.Z + 1.5f };
        Vector3 to;
        ulong targetGuid = CreatorMissileTargetGuid();
        if (CreatorGroundTargeted(missileDoc) && targetGuid == 0)
            to = CreatorGroundPreviewPoint() + new Vector3(0f, 0f, 0.3f);   // the live client flies to the spot itself
        else if (targetGuid != 0 && _entities.TryGet(targetGuid, out var dummy))
            to = dummy.Position with { Z = dummy.Position.Z + 1.5f };
        else
        {
            float yaw = _controller.Yaw;
            to = from + new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f) * 20f;
        }
        // The RULES' speed (what the server times arrival by), not a stand-in.
        float ruleSpeed = CreatorMissileSpeed(missileDoc);
        float speed = ruleSpeed > 0.4f ? ruleSpeed : 20f;
        double duration = Vector3.Distance(from, to) / speed;
        _spellEffects.SpawnMissile(LocalPlayerGuid, _creatorSpell.Info.Id, path,
            from, to, SpellClockNow, duration, missileDoc.MissileScale);
        return duration;
    }

    // ── document build / patch / hot-swap ────────────────────────────────────

    /// <summary>The BLP a texture slot actually renders with: the user's swap if
    /// one is set, else the authored filename.</summary>
    private static string EffectiveTexturePath(CreatorModelDoc model, int texIndex)
    {
        if (model.TextureSwaps.TryGetValue(texIndex, out string? swapped)) return swapped;
        return model.Textures.FirstOrDefault(t => t.Index == texIndex)?.Filename ?? "";
    }

    /// <summary>Push (or clear) one texture's palette swap into all three effect
    /// renderers so the world shows it immediately. Keys on the slot's EFFECTIVE
    /// path so tints follow swaps.</summary>
    private void SetCreatorTextureTint(CreatorModelDoc model, int texIndex, Vector3? color)
    {
        string path = EffectiveTexturePath(model, texIndex);
        if (path.Length == 0) return;
        uint? packed = color is { } c ? PackArgb(c) & 0x00FFFFFF : null;
        _spellParticles?.SetTextureTint(path, packed);
        _spellEffectMeshes?.SetTextureTint(path, packed);
        _spellRibbons?.SetTextureTint(path, packed);
    }

    /// <summary>Swap (or with null, restore) a texture slot's BLP. An active tint
    /// moves with the slot: cleared from the old path, re-applied on the new.</summary>
    private void ApplyCreatorTextureSwap(CreatorModelDoc model, int texIndex, string? newPath)
    {
        bool tintOn = model.TextureTints.TryGetValue(texIndex, out var tint) && tint.On;
        if (tintOn) SetCreatorTextureTint(model, texIndex, null);
        if (newPath is null or { Length: 0 }) model.TextureSwaps.Remove(texIndex);
        else model.TextureSwaps[texIndex] = newPath;
        if (tintOn) SetCreatorTextureTint(model, texIndex, tint!.Color);
        RebuildCreatorModel(model);
    }

    /// <summary>
    /// The swap the picker actually performs. The same BLP usually appears in
    /// SEVERAL of a spell's phase models (CLOUDS.BLP lives in the precast hand
    /// AND the cast hand of Cone of Cold) - swapping one slot while the phase
    /// you are watching draws another model's copy looks like "the swap did
    /// nothing". With <see cref="_texSwapAllModels"/> (default) the swap follows
    /// the IMAGE: every model in the spell whose table holds the same original
    /// gets the replacement too.
    /// </summary>
    private void ApplyCreatorTextureSwapEverywhere(
        CreatorSpellDoc doc, CreatorModelDoc primary, int texIndex, string? newPath)
    {
        string original = primary.Textures.FirstOrDefault(t => t.Index == texIndex)?.Filename ?? "";
        ApplyCreatorTextureSwap(primary, texIndex, newPath);
        PropagateSwapToGeometry(doc, primary, texIndex, newPath);
        if (!_texSwapAllModels || original.Length == 0) return;
        foreach (var other in doc.Models.Values)
        {
            if (ReferenceEquals(other, primary)) continue;
            foreach (var tex in other.Textures)
                if (string.Equals(tex.Filename, original, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyCreatorTextureSwap(other, tex.Index, newPath);
                    PropagateSwapToGeometry(doc, other, tex.Index, newPath);
                }
        }
    }

    /// <summary>
    /// The swap must FOLLOW THE PIXELS. An emitter with a geometry model never
    /// draws its billboard texture - each particle is a little M2 whose art
    /// lives in the geometry file's OWN texture table (Cone of Cold's hand
    /// lists CLOUDS.BLP, but the visible puffs are ConeofCold_Geo meshes
    /// textured with CLOUDS4.BLP). Swapping the host slot alone is invisible,
    /// which read as "the swap does nothing". So: when a swapped slot's
    /// emitters spawn geometry, apply the same replacement (or restore) to the
    /// geometry model's texture slots too.
    /// </summary>
    private void PropagateSwapToGeometry(
        CreatorSpellDoc doc, CreatorModelDoc model, int texIndex, string? newPath)
    {
        var tex = model.Textures.FirstOrDefault(t => t.Index == texIndex);
        if (tex is null || model.EmitterGeometry.Count == 0) return;
        foreach (string geoPath in tex.ReferencedByEmitters
                     .Select(e => model.EmitterGeometry.GetValueOrDefault(e))
                     .Where(p => p is { Length: > 0 })
                     .Distinct(StringComparer.OrdinalIgnoreCase)!)
        {
            if (!doc.Models.TryGetValue(geoPath, out var geoDoc) ||
                ReferenceEquals(geoDoc, model)) continue;
            Console.WriteLine($"[creator] slot {texIndex} of {Path.GetFileName(model.Path)} is drawn " +
                $"as {Path.GetFileName(geoPath)} per-particle meshes - " +
                $"{(newPath is null ? "restoring" : "swapping")} that model's art too");
            foreach (var geoTex in geoDoc.Textures)
                ApplyCreatorTextureSwap(geoDoc, geoTex.Index, newPath);
        }
    }

    /// <summary>A brand-new spell: no source, no inherited visual, an empty anatomy.
    ///
    /// The id is a local, synthetic one (see CreatorNewSpellIdBase) until the Completer
    /// assigns a real one on export. That is enough to author and preview with; turning it
    /// into real DBC rows is the Completer's job, and its "no template to clone" gap is still
    /// open (SPELL_SKETCH.md §5).</summary>
    private void StartNewCreatorSpell(string name, uint? localId = null)
    {
        // Reopening a saved design keeps its local id, because every custom path it compiled
        // (Spells\Custom\{id}_{name}\...) is spelled with it; the counter moves past it so a
        // later New spell can never collide.
        uint id = localId is { } kept && IsCreatorNewSpellId(kept) ? kept : ++_creatorNextNewSpellId;
        if (id > _creatorNextNewSpellId) _creatorNextNewSpellId = id;
        var blank = new SpellInfo(
            Id: id, Name: name, Rank: "", IconPath: "",
            Attributes: 0, AttributesEx2: 0, AttributesEx3: 0,
            InterruptFlags: 0, ChannelInterruptFlags: 0, Targets: 0, ImplicitTarget: 0,
            RecoveryMs: 0, CategoryRecoveryMs: 0,
            PowerType: 0, ManaCost: 0, ManaCostPercent: 0,
            StartRecoveryCategory: 0, StartRecoveryMs: 0,
            VisualId: 0,                       // no visual: nothing is inherited
            Speed: 0f, Description: "", RangeIndex: 0);

        SelectCreatorSpell(blank);
        if (_creatorSpell is { } fresh)
        {
            // A derived spell arrives with its anatomy already decided by whoever made it; a
            // new one has not been decided yet, so the Sketch window opens on the question.
            fresh.PlanStarted = false;
            fresh.PlannedStages.Clear();
            fresh.PlannedMissile = false;
        }
        _sketchOpen = true;
        _sketchStage = SpellStage.Cast;
        _sketchSelected = 0;
        _creatorExportStatus = $"New spell \"{name}\" - pick what it is made of, then draw it.";
    }

    private void SelectCreatorSpell(in SpellInfo info)
    {
        // Clear any overrides, tints and color hues the previous document installed.
        if (_creatorSpell is not null)
        {
            foreach (CreatorAudioTrack track in _creatorSpell.Audio.Values)
                _spellSounds?.RemoveCustomFile(track.MpqPath);
            foreach (var model in _creatorSpell.Models.Values)
            {
                _spellEffects?.SetModelOverride(model.Path, null);
                string renderPath = SpellVisualCatalog.ModelPath(model.Path);
                _spellParticles?.SetGeometryModelOverride(renderPath, null);
                _spellRibbons?.SetModelColorHue(renderPath, null);
                _spellEffectMeshes?.SetModelColorHue(renderPath, null);
                _spellEffectMeshes?.InvalidateModel(renderPath);
                foreach (int texIndex in model.TextureTints.Keys)
                    SetCreatorTextureTint(model, texIndex, null);
            }
        }
        // The previous spell's imported art leaves the override layer with it.
        if (_creatorSpell is { } previousDoc)
        {
            foreach (string imported in previousDoc.ImportedTextures.Keys) _mpq?.ClearOverride(imported);
            foreach (string sketched in previousDoc.SketchTextures.Keys) _mpq?.ClearOverride(sketched);
            foreach (Creator.Sketch.SketchDoc sketch in previousDoc.Sketches.Values)
                _mpq?.ClearOverride(Creator.Sketch.SketchDoc.ModelPath(
                    (int)previousDoc.Info.Id, previousDoc.Info.Name, sketch.Stage));
        }
        StopAllCreatorAudio();
        _creatorAudioPickerCue = null;
        _creatorAudioPickerError = "";
        _spellEffects?.ReapArea(CreatorAreaGuid);
        _spellSounds?.StopHold(CreatorAreaGuid);
        _creatorAreaActive = false;

        var doc = new CreatorSpellDoc { Info = info };
        if (_spellVisualCatalog?.TryGetStages(info.VisualId, out doc.Stages) != true)
        {
            // No visual to inherit - either a spell that never had one, or a brand-new spell.
            // Either way it still needs its (empty) per-stage composition, or there is nowhere
            // for a Sketch to put the model it compiles.
            InitCreatorComposition(doc);
            _creatorSpell = doc;
            SpellIdeSelectionReset();   // every ws-{path} id just died with the old doc
            _creatorLoopNextAt = 0;
            return;
        }

        void AddKit(SpellStage stage, uint kitId)
        {
            if (kitId == 0 || _spellVisualCatalog?.TryGetKit(kitId, out SpellVisualKitInfo kit) != true) return;
            foreach (var effect in kit.Effects)
                if (effect.ModelPath.Length > 0) doc.PhaseModels.Add((stage, effect.ModelPath));
        }
        AddKit(SpellStage.Precast, doc.Stages.Precast);
        AddKit(SpellStage.Cast, doc.Stages.Cast);
        AddKit(SpellStage.Impact, doc.Stages.Impact);
        AddKit(SpellStage.State, doc.Stages.State);
        AddKit(SpellStage.Channel, doc.Stages.Channel);
        doc.MissilePath = _spellVisualCatalog?.MissilePath(doc.Stages);

        var paths = doc.PhaseModels.Select(p => p.Path).ToList();
        if (doc.MissilePath is { Length: > 0 }) paths.Add(doc.MissilePath);
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            byte[]? original = _spellEffects?.ReadOriginalModel(path);
            if (original is null) continue;
            var model = new CreatorModelDoc { Path = path, Original = original };
            model.Working = (byte[])original.Clone();
            model.Emitters = M2EmitterParser.ReadEmitters(model.Working);
            model.OriginalEmitterCount = model.Emitters.Count;
            model.Textures = Creator.M2TextureParser.ParseTextures(model.Original);
            model.Meshes = M2MeshParser.ReadMeshes(model.Working);
            model.Ribbons = M2RibbonParser.ReadRibbons(model.Working);
            model.Bones = M2BoneParser.ReadBones(model.Working);
            model.OriginalLayerCount = model.Meshes.Count;
            model.OriginalRibbonCount = model.Ribbons.Count;
            doc.Models[path] = model;
        }

        AttachCreatorGeometryModels(doc);
        InitCreatorComposition(doc);

        _creatorSpell = doc;
        SpellIdeSelectionReset();   // doc.Models was replaced wholesale; heal to phase one
        _creatorLoopNextAt = 0;   // fire the loop immediately on next tick
    }

    /// <summary>Every geometry-particle model spawned by an emitter of any model in the doc
    /// joins the workshop as an editable model (idempotent; also run when a composed model
    /// is added).</summary>
    private void AttachCreatorGeometryModels(CreatorSpellDoc doc)
    {
        // GEOMETRY-PARTICLE models: an emitter can spawn a little M2 per particle
        // (Cone of Cold's cloud puffs) whose art lives in ITS OWN texture table -
        // completely invisible from the host model's slots, which is why editing
        // the host's CLOUDS slot changed nothing on screen. Pull each geometry
        // model into the workshop as a first-class editable model.
        foreach (var (hostPath, host) in doc.Models.ToList())
        {
            if (M2Reader.Parse(host.Original) is not { } parsed) continue;
            for (int emitterIndex = 0; emitterIndex < parsed.ParticleEmitters.Count; emitterIndex++)
            {
                M2ParticleEmitter emitter = parsed.ParticleEmitters[emitterIndex];
                if (emitter.GeometryModel.Length == 0) continue;
                string geoPath = SpellVisualCatalog.ModelPath(emitter.GeometryModel);
                host.EmitterGeometry[emitterIndex] = geoPath;
                if (doc.Models.ContainsKey(geoPath))
                {
                    doc.GeometryHosts.TryAdd(geoPath, Path.GetFileName(hostPath));
                    continue;
                }
                byte[]? original = _spellEffects?.ReadOriginalModel(geoPath);
                if (original is null) continue;
                var geoModel = new CreatorModelDoc { Path = geoPath, Original = original };
                geoModel.Working = (byte[])original.Clone();
                geoModel.Emitters = M2EmitterParser.ReadEmitters(geoModel.Working);
                geoModel.OriginalEmitterCount = geoModel.Emitters.Count;
                geoModel.Textures = Creator.M2TextureParser.ParseTextures(geoModel.Original);
                geoModel.Meshes = M2MeshParser.ReadMeshes(geoModel.Working);
                geoModel.Ribbons = M2RibbonParser.ReadRibbons(geoModel.Working);
                geoModel.Bones = M2BoneParser.ReadBones(geoModel.Working);
                geoModel.OriginalLayerCount = geoModel.Meshes.Count;
                geoModel.OriginalRibbonCount = geoModel.Ribbons.Count;
                doc.Models[geoPath] = geoModel;
                doc.GeometryHosts[geoPath] = Path.GetFileName(hostPath);
                Console.WriteLine($"[creator] geometry model {Path.GetFileName(geoPath)} " +
                                  $"(spawned by {Path.GetFileName(hostPath)}) joined the workshop");
            }
        }
    }

    /// <summary>Original bytes -> whole-model dials -> per-BLP hue dials ->
    /// per-emitter absolute edits -> hot-swap into SpellEffectSource. Rebuilt from
    /// Original every time, so the multipliers never compound.</summary>
    private void RebuildCreatorModel(CreatorModelDoc model, bool replay = true)
    {
        byte[] working = ComposeCreatorModelBytes(model, model.Original, out bool bytesPatched);

        // The byte patcher cannot reach ribbon or MESH color tracks (keyframed
        // data), so the whole-model hue routes to those renderers' color layers -
        // otherwise "hue everything" left the missile trail and the mesh planes
        // (glow sheets, cloud swirls) unmoved.
        string renderPath = SpellVisualCatalog.ModelPath(model.Path);
        uint? modelHue = model.HueShift ? PackArgb(model.HueColor) & 0x00FFFFFF : null;
        _spellRibbons?.SetModelColorHue(renderPath, modelHue);
        _spellEffectMeshes?.SetModelColorHue(renderPath, modelHue);

        // Meshes bake their textures at build time - drop the cached mesh so
        // byte patches and texture swaps show on the mesh-drawn parts too.
        _spellEffectMeshes?.InvalidateModel(renderPath);

        model.Working = working;
        model.Emitters = M2EmitterParser.ReadEmitters(working);
        model.Meshes = M2MeshParser.ReadMeshes(working);
        model.Ribbons = M2RibbonParser.ReadRibbons(working);
        model.Bones = M2BoneParser.ReadBones(working);
        // Tints live in the renderers' texture layer, not the M2 bytes, but they
        // count as "modified" so the export includes the recolored BLPs.
        model.Modified = bytesPatched || model.TextureTints.Any(t => t.Value.On);
        // A sketch's "original" is the compiled M2 - there is no archive file behind its path,
        // only the mount override CompileSketch installed. Dropping the override here (nothing
        // byte-patched, so "back to the archive") sent every renderer back through the mount
        // for the same bytes and re-baked the mesh and its animator on EVERY frame a Tint
        // swatch was being dragged (msui-console.log, 2026-09-19: 234 clears in six seconds).
        bool keepBytes = bytesPatched ||
                         (_creatorSpell is { } sketchOwner && IsSketchModel(sketchOwner, model.Path));
        _spellEffects?.SetModelOverride(model.Path, keepBytes ? working : null);
        // Geometry-particle models load through the particle system's own cache,
        // not SpellEffectSource - push the same override there (a no-op for
        // paths nothing spawns as geometry).
        _spellParticles?.SetGeometryModelOverride(renderPath, keepBytes ? working : null);
        // Editing while paused: re-lay the held picture deterministically with the new bytes
        // (shared_docs/SPELL_CREATOR_IDE.md §2.2). A drag in flight skips it (§2.10) and
        // replays once on release.
        if (replay && CreatorSpellPaused) ReplayCreatorEffects();
    }

    /// <summary>Every byte-level edit of a model, applied to <paramref name="source"/> in the
    /// one fixed order (globals → authored-emitter edits → clones → per-BLP hues → clone
    /// edits → off-switches → mesh/ribbon/bone edits → texture swaps). Pure with respect to
    /// the renderers: it only re-reads the texture table into <c>model.Textures</c>, which the
    /// hue step needs. RebuildCreatorModel feeds it the preview original; the session export
    /// feeds it a sketch recompiled WITHOUT the editing loop, so what ships is what the
    /// author tuned on top of the one-shot bytes (SPELL_CREATOR_PLAN.md P0.1).</summary>
    private byte[] ComposeCreatorModelBytes(CreatorModelDoc model, byte[] baseBytes, out bool bytesPatched)
    {
        bool globalsActive = model.HueShift || model.GravityAdd != 0f ||
            model.RateMul != 1f || model.ScaleMul != 1f || model.LifeMul != 1f || model.SpeedMul != 1f;
        bool texHuesActive = model.TextureHues.Any(h => h.Value.On);
        byte[] working;
        if (globalsActive)
        {
            var globals = new M2ParticlePatcher.ParticlePatchParams
            {
                UseHueShift = model.HueShift,
                HueShiftColor = model.HueShift ? PackArgb(model.HueColor) : 0,
                EmissionRateMultiplier = model.RateMul == 1f ? null : model.RateMul,
                ScaleMultiplier = model.ScaleMul == 1f ? null : model.ScaleMul,
                LifespanMultiplier = model.LifeMul == 1f ? null : model.LifeMul,
                EmissionSpeedMultiplier = model.SpeedMul == 1f ? null : model.SpeedMul,
                GravityAdd = model.GravityAdd == 0f ? null : model.GravityAdd,
            };
            working = M2ParticlePatcher.PatchParticles(baseBytes, globals)
                      ?? (byte[])baseBytes.Clone();
        }
        else working = (byte[])baseBytes.Clone();

        // Apply authored-emitter edits before cloning. A duplicate should copy
        // the source as the user currently tuned it, not silently revert to the
        // file's authored scalar/shape/position values. Clone-specific edits are
        // applied after the array grows below.
        foreach (var edit in model.Edits.Values.Where(e =>
                     e.EmitterIndex < model.OriginalEmitterCount))
            M2EmitterParser.ApplyEmitterPatch(working, edit);

        // User-added emitters next, so the clones inherit both global dials and
        // authored-source edits. Everything downstream (per-BLP hues, clone
        // edits, disables) addresses the grown array. Each clone lands at
        // OriginalEmitterCount + position; a
        // failed clone still consumes its slot (a dead entry keeps later indices
        // stable) - CloneEmitter only fails on malformed headers, which the
        // parse at document build already screened out.
        foreach (var added in model.AddedEmitters)
        {
            if (M2ParticlePatcher.CloneEmitter(working, added.SourceIndex, added.TextureSlot)
                is { } cloned)
                working = cloned.m2Data;
            else
                Console.WriteLine($"[creator] {Path.GetFileName(model.Path)}: emitter clone " +
                    $"of {added.SourceIndex} failed - header rejected");
        }
        // Cloned ribbons and mesh layers (SPELL_CREATOR_IDE §2.5), before the edits that
        // address them by index below.
        foreach (int source in model.AddedRibbons)
            if (M2RibbonParser.CloneRibbon(working, source) is { } ribbonClone) working = ribbonClone.Data;
            else Console.WriteLine($"[creator] {Path.GetFileName(model.Path)}: ribbon clone of r{source} failed");
        foreach (int source in model.AddedLayers)
            if (M2MeshParser.CloneLayer(working, source) is { } layerClone) working = layerClone.Data;
            else Console.WriteLine($"[creator] {Path.GetFileName(model.Path)}: layer clone of m{source} failed");
        // Grafts from other models (P3.3), after the clones: their indices follow them.
        working = ApplyCreatorTransplants(model, working);

        // The texture table's emitter back-references must see the clones (the
        // per-BLP hue dials and the UI's texture<->emitter grouping key on them).
        // Reparsed every rebuild so removing the last clone heals the refs too;
        // swaps have not been applied yet, so filenames stay the authored ones.
        model.Textures = Creator.M2TextureParser.ParseTextures(working);

        // Per-BLP hue dials AFTER the master hue: the per-texture target simply
        // wins for the emitters that reference that texture (the rotation sets
        // the target hue absolutely, it does not stack).
        if (texHuesActive)
        {
            foreach (var (texIndex, hue) in model.TextureHues)
            {
                if (!hue.On) continue;
                var tex = model.Textures.FirstOrDefault(t => t.Index == texIndex);
                if (tex is null || tex.ReferencedByEmitters.Count == 0) continue;
                M2ParticlePatcher.HueShiftEmitters(working,
                    PackArgb(hue.Color) & 0x00FFFFFF, tex.ReferencedByEmitters);
            }
        }

        foreach (var edit in model.Edits.Values.Where(e =>
                     e.EmitterIndex >= model.OriginalEmitterCount))
            M2EmitterParser.ApplyEmitterPatch(working, edit);

        // Wholesale emitter off-switches (zeroed emission), after the per-emitter
        // edits so a disabled emitter stays silent whatever its sliders say.
        foreach (int disabled in model.DisabledEmitters)
            M2ParticlePatcher.DisableEmitter(working, disabled);

        // Mesh layers and ribbons (SPELL_CREATOR_IDE §2.5): private materials and lookup
        // entries append at EOF and repoint, first keys write in place, hidden submeshes
        // zero their triangles, silenced ribbons zero their edges - all before the texture
        // swaps' resize below, which never moves what was written.
        working = ApplyCreatorMeshAndRibbonEdits(model, working);

        // Dope-sheet track rewrites (P3.1): new key arrays at EOF, blocks repointed.
        foreach ((string trackId, Creator.TrackKeys keys) in model.TrackEdits)
            if (Creator.M2TrackPatcher.Find(working, trackId) is { } track)
                working = Creator.M2TrackPatcher.Write(working, track, keys);
            else Console.WriteLine($"[creator] {Path.GetFileName(model.Path)}: track {trackId} not in the model - edit skipped");

        // Texture-slot swaps LAST: longer paths append at EOF (resize), which
        // never moves the fixed-offset structures the patchers above wrote.
        if (model.TextureSwaps.Count > 0)
        {
            (working, int swapped) = Creator.M2TextureParser.PatchTextureFilenamesResize(
                working, model.TextureSwaps);
            Console.WriteLine($"[creator] {Path.GetFileName(model.Path)}: " +
                $"{swapped}/{model.TextureSwaps.Count} texture slot(s) swapped " +
                $"({string.Join(", ", model.TextureSwaps.Select(sw => $"{sw.Key}->{Path.GetFileName(sw.Value)}"))})");
        }

        bytesPatched = globalsActive || texHuesActive || model.Edits.Count > 0 ||
                       model.TextureSwaps.Count > 0 || model.DisabledEmitters.Count > 0 ||
                       model.AddedEmitters.Count > 0 || CreatorMeshOrRibbonEditsActive(model) ||
                       model.AddedRibbons.Count > 0 || model.AddedLayers.Count > 0 || model.TrackEdits.Count > 0 ||
                       model.Transplants.Count > 0;
        return working;
    }

    /// <summary>The identity color of one texture slot: a stable golden-angle
    /// palette, shown on the texture's row AND on every emitter drawing with it,
    /// so the texture-to-emitter wiring reads at a glance when drilling a phase.</summary>
    private static Vector4 CreatorSlotColor(int slotIndex)
    {
        float hue = slotIndex * 137.508f % 360f / 360f;
        ImGui.ColorConvertHSVtoRGB(hue, 0.72f, 0.95f, out float r, out float g, out float b);
        return new Vector4(r, g, b, 1f);
    }

    /// <summary>Drop one user-added emitter and re-key everything addressed by
    /// emitter index. Only AUTHORED emitters may be clone sources (the UI offers
    /// Duplicate on those alone), so removals never invalidate another clone's
    /// source - but clones after the removed one slide down an index, and their
    /// edits/disables must slide with them.</summary>
    private static void RemoveCreatorAddedEmitter(CreatorModelDoc model, int emitterIndex)
    {
        int pos = emitterIndex - model.OriginalEmitterCount;
        if (pos < 0 || pos >= model.AddedEmitters.Count) return;
        model.AddedEmitters.RemoveAt(pos);

        var edits = model.Edits.Values
            .Where(e => e.EmitterIndex != emitterIndex)
            .ToList();
        model.Edits.Clear();
        foreach (var edit in edits)
        {
            if (edit.EmitterIndex > emitterIndex) edit.EmitterIndex--;
            model.Edits[edit.EmitterIndex] = edit;
        }

        var disabled = model.DisabledEmitters
            .Where(i => i != emitterIndex)
            .Select(i => i > emitterIndex ? i - 1 : i)
            .ToList();
        model.DisabledEmitters.Clear();
        foreach (int i in disabled) model.DisabledEmitters.Add(i);
    }

    /// <summary>
    /// Compose a centered row/column from one authored emitter. The original
    /// remains at offset zero; appended clones occupy +/- spacing, +/-2*spacing,
    /// and so on. Every clone receives an explicit raw local position override,
    /// so the result widens spatial coverage instead of merely stacking opacity.
    /// </summary>
    private static void AddCreatorEmitterLine(CreatorModelDoc model, EmitterSnapshot source,
        int textureSlot, int axis, int totalCount, float spacing)
    {
        if (source.Index < 0 || source.Index >= model.OriginalEmitterCount) return;
        int half = Math.Clamp(totalCount, 3, 9) / 2;
        int availablePairs = (255 - model.OriginalEmitterCount - model.AddedEmitters.Count) / 2;
        half = Math.Min(half, availablePairs);
        spacing = Math.Clamp(spacing, 0.05f, 10f);
        for (int step = 1; step <= half; step++)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                int cloneIndex = model.OriginalEmitterCount + model.AddedEmitters.Count;
                model.AddedEmitters.Add(new CreatorAddedEmitter
                {
                    SourceIndex = source.Index,
                    TextureSlot = textureSlot,
                });

                float x = source.PositionX;
                float y = source.PositionY;
                float z = source.PositionZ;
                float offset = sign * step * spacing;
                if (axis == 0) x += offset;
                else if (axis == 1) y += offset;
                else z += offset;
                model.Edits[cloneIndex] = new EmitterPatch
                {
                    EmitterIndex = cloneIndex,
                    PositionX = x,
                    PositionY = y,
                    PositionZ = z,
                };
            }
        }
    }

    private static uint PackArgb(Vector3 rgb) =>
        0xFF000000u |
        ((uint)Math.Clamp((int)(rgb.X * 255f), 0, 255) << 16) |
        ((uint)Math.Clamp((int)(rgb.Y * 255f), 0, 255) << 8) |
        (uint)Math.Clamp((int)(rgb.Z * 255f), 0, 255);

    // ── the panel (registered sections; see Program.Creator.Ui.cs) ───────────

    private partial void RegisterCreatorSpellsSections()
    {
        CreatorSection("Spells", "ws-spell", _creatorSpell is null
            ? "Spell" : $"Spell: {_creatorSpell.Info.Name}", true, DrawCreatorSpellPickerBody);

        if (_creatorSpell is not { } doc) return;

        CreatorSection("Spells", "ws-loop", _creatorLoopOn ? "Loop  (running)" : "Loop", true,
            DrawCreatorLoopBody);
        CreatorSection("Spells", "ws-audio", doc.Audio.Count == 0 ? "Audio" : "Audio *", true,
            DrawCreatorAudioBody);
        // The Spell Creator IDE (shared_docs/SPELL_CREATOR_IDE.md): void stage + grid + gizmo
        // switches, and the effect clock.
        CreatorSection("Spells", "ws-stage", _creatorStageActive ? "Void stage  (on)" : "Void stage & gizmos",
            false, DrawCreatorStageBody);
        CreatorSection("Spells", "ws-clock", _creatorClockPaused ? "Clock  (paused)" : "Clock",
            false, DrawCreatorClockBody);
        CreatorSection("Spells", "ws-composition", CreatorCompositionChanged(doc) ? "Composition *" : "Composition",
            false, DrawCreatorCompositionBody);

        // Per-model editors, grouped under the phases that use them. The id is
        // the model path (stable); the label's * marker may change per frame.
        foreach (var model in doc.Models.Values)
        {
            var m = model;
            CreatorSection("Spells", $"ws-{m.Path}", CreatorModelLabel(doc, m), false,
                () => DrawCreatorModelEditor(m, CreatorUiScale));
        }

        CreatorSection("Spells", "ws-session", "Session", false, DrawCreatorSessionSection);
    }

    /// <summary>"cast: Foo.m2", "missile: Bar.m2", "geometry: Puff.m2 (in Host.m2)", with the
    /// ' *' dirty marker - the one phase label the sections, the deck and the IDE share.</summary>
    private static string CreatorModelLabel(CreatorSpellDoc doc, CreatorModelDoc m)
    {
        string label;
        if (doc.GeometryHosts.TryGetValue(m.Path, out string? geoHost))
        {
            // A per-particle model (cloud puffs etc.) - name who spawns it.
            label = $"geometry: {Path.GetFileName(m.Path)} (in {geoHost})";
        }
        else
        {
            string phases = string.Join("+", doc.PhaseModels
                .Where(p => string.Equals(p.Path, m.Path, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Stage.ToString().ToLowerInvariant()).Distinct());
            if (phases.Length == 0 &&
                string.Equals(CreatorEffectiveMissile(doc), m.Path, StringComparison.OrdinalIgnoreCase))
                phases = "missile";
            label = $"{phases}: {Path.GetFileName(m.Path)}";
        }
        if (m.Modified) label += " *";
        return label;
    }

    private void DrawCreatorSpellPickerBody()
    {
        float cs = CreatorUiScale;
        if (_spellCatalog is null || _spellVisualCatalog is null || _spellEffects is null)
        {
            ImGui.TextWrapped("Spell catalogs are unavailable - check the console.");
            return;
        }

        // ── start from nothing ───────────────────────────────────────────────
        // The workshop was built to DERIVE: search a spell, inherit its visual, change it.
        // That is a fine way to learn the format and a bad way to author, because it makes
        // every new spell an edit of somebody else's and forces a choice before the author
        // has made any. A new spell starts with no visual at all: no kits, no models, no
        // inherited slots - an empty anatomy the author fills in part by part.
        ImGui.TextDisabled("name");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f * cs);
        ImGui.InputText("##new-spell-name", _creatorNewSpellNameBuf,
            (uint)_creatorNewSpellNameBuf.Length);
        ImGui.SameLine();
        string newName = BufToString(_creatorNewSpellNameBuf).Trim();
        bool nameable = newName.Length >= 2;
        if (!nameable) ImGui.BeginDisabled();
        if (CreatorButton("New spell", 90f * cs))
        {
            StartNewCreatorSpell(newName);
            Array.Clear(_creatorNewSpellNameBuf);
        }
        if (!nameable) ImGui.EndDisabled();
        CreatorHelp("Start from nothing: no phases, no models, no inherited art. The Sketch " +
                    "window then asks what kind of spell this is and you build it part by " +
                    "part.\n\nDeriving from an existing spell (the search below) is the other " +
                    "way in - use it when you want something that already works as a starting " +
                    "point, or to take a real spell apart and see how it was made.");

        ImGui.Separator();
        ImGui.TextDisabled("or derive from an existing spell");

        ImGui.SetNextItemWidth(220f * cs);
        if (ImGui.InputText("##spell-search", _creatorSpellSearchBuf,
                (uint)_creatorSpellSearchBuf.Length))
            _creatorSpellSearchDirty = true;
        ImGui.SameLine();
        ImGui.TextDisabled(_creatorSpell is null ? "pick a spell"
            : $"{_creatorSpell.Info.Id} {_creatorSpell.Info.Name}");

        string query = BufToString(_creatorSpellSearchBuf);
        if (_creatorSpellSearchDirty)
        {
            _creatorSpellSearchDirty = false;
            _creatorSpellResults = query.Length >= 2
                ? (uint.TryParse(query, out uint asId)
                    ? _spellCatalog.Spells.Where(s => s.Id == asId).ToList()
                    : _spellCatalog.Spells
                        .Where(s => s.VisualId != 0 &&
                                    s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(s => s.Id).Take(40).ToList())
                : null;
        }
        if (_creatorSpellResults is { Count: > 0 } results && query.Length >= 2)
        {
            if (BeginCreatorResults("##spell-results", results.Count))
            {
                foreach (var spell in results)
                {
                    string rank = spell.Rank.Length > 0 ? $" ({spell.Rank})" : "";
                    if (CreatorResultRow($"{spell.Id}  {spell.Name}{rank}"))
                    {
                        SelectCreatorSpell(spell);
                        Array.Clear(_creatorSpellSearchBuf);
                        _creatorSpellSearchDirty = true;
                    }
                }
            }
            EndCreatorResults();
        }

        if (_creatorSpell is { } doc && doc.Models.Count == 0)
            ImGui.TextWrapped("This spell's visual has no effect models to tune " +
                              "(or the models failed to load).");

        bool advanced = Settings.Creator.SpellAdvancedMode;
        if (ImGui.Checkbox("Advanced mode", ref advanced))
        {
            Settings.Creator.SpellAdvancedMode = advanced;
            SettingsFile?.Save();
        }
        CreatorHelp("Simple mode preserves the complete original spell workshop: phases, " +
            "model-wide look controls, images, audio, emitter creation/removal, enable, " +
            "blend, birth shape, rate, speed, gravity, lifespan, spread, area and scale. " +
            "Advanced mode only adds the newer technical layer: composition/placement, " +
            "zSource, drag, billboard spin, behavior flags, and future bone/model controls.");
    }

    private void DrawCreatorLoopBody()
    {
        float cs = CreatorUiScale;
        var doc = _creatorSpell;
        if (doc is null) { ImGui.TextDisabled("Pick a spell first."); return; }

        // One checkbox per phase - loop EXACTLY the combination you want.
        // Phases this spell's visual does not author are shown disabled.
        ImGui.TextDisabled("PHASES TO LOOP");
        CreatorHelp("A spell visual is a chain of phases; check exactly what you want " +
            "repeating:\n\n" +
            "Precast - the charge-up on the caster's hands while casting.\n" +
            "Cast - the release burst when the cast completes.\n" +
            "Missile - the projectile flying to the target.\n" +
            "Impact - what lands ON the victim.\n" +
            "State - the persistent effect while a buff/debuff holds.\n" +
            "Channel - held on the caster while channeling.\n" +
            "Area (rain) - the ground effect at the target spot: a looping centre " +
            "model plus one-shot shards scattered over the radius (Blizzard's " +
            "snowfall, Rain of Fire's meteors). This is a separate machine from " +
            "Missile - it is where the 'many missiles' of an AoE come from.\n\n" +
            "Greyed phases are not authored by this spell's visual.");
        bool PhaseBox(string label, ref bool value, bool available, bool sameLine)
        {
            if (sameLine) ImGui.SameLine();
            if (!available)
            {
                value = false;
                ImGui.BeginDisabled();
                bool off = false;
                ImGui.Checkbox(label, ref off);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("This spell's visual has no " + label + " kit.");
                return false;
            }
            return ImGui.Checkbox(label, ref value);
        }

        bool hasArea = _spellVisualCatalog?.TryGetAreaVisual(doc.Info.VisualId,
            out SpellAreaVisualInfo _) == true;
        PhaseBox("Precast", ref _creatorLoopPrecast, CreatorStageAvailable(doc, SpellStage.Precast), false);
        PhaseBox("Cast", ref _creatorLoopCast, CreatorStageAvailable(doc, SpellStage.Cast), true);
        PhaseBox("Missile", ref _creatorLoopMissilePhase, CreatorEffectiveMissile(doc) is { Length: > 0 }, true);
        PhaseBox("Impact", ref _creatorLoopImpactPhase, CreatorStageAvailable(doc, SpellStage.Impact), true);
        PhaseBox("State", ref _creatorLoopStateHold, CreatorStageAvailable(doc, SpellStage.State), false);
        PhaseBox("Channel", ref _creatorLoopChannelHold, CreatorStageAvailable(doc, SpellStage.Channel), true);
        PhaseBox("Area (rain)", ref _creatorLoopAreaHold, hasArea, true);
        if (hasArea && _creatorLoopAreaHold)
        {
            ImGui.SetNextItemWidth(120f * cs);
            ImGui.SliderFloat("Area radius", ref _creatorAreaRadius, 2f, 30f, "%.0f yd");
            if (CreatorResetKnob("arearad")) _creatorAreaRadius = 8f;
            CreatorHelp("Radius of the ground disc the shards fall over. On the live " +
                "server this comes from the spell's area object; here it is yours to dial.");
        }

        ImGui.SetNextItemWidth(110f * cs);
        ImGui.SliderFloat("Period", ref _creatorLoopPeriod, 0.5f, 6f, "%.1fs");
        if (CreatorResetKnob("period")) _creatorLoopPeriod = 2f;
        CreatorHelp("Seconds between loop cycles. Each cycle plays the checked phases " +
            "in sequence (precast, then cast, missile in flight, impact on arrival).");
        ImGui.SameLine();
        bool anyPhase = _creatorLoopPrecast || _creatorLoopCast || _creatorLoopMissilePhase ||
                        _creatorLoopImpactPhase || _creatorLoopStateHold || _creatorLoopChannelHold ||
                        _creatorLoopAreaHold;
        if (!anyPhase && !_creatorLoopOn) ImGui.BeginDisabled();
        if (CreatorButton(_creatorLoopOn ? "Stop" : "Loop", 70f * cs)) ToggleCreatorLoop();
        if (!anyPhase && !_creatorLoopOn) ImGui.EndDisabled();

        if (!anyPhase)
            ImGui.TextDisabled("Check at least one phase.");
        else if (_creatorLoopImpactPhase)
            ImGui.TextDisabled(CreatorMissileTargetGuid() != 0
                ? "Impact lands on the spawned target."
                : "Impact lands on you - spawn a target (Target menu) to see it land there.");
    }

    /// <summary>Start or stop the loop machine - the Loop section's button and the IDE
    /// strip's. Stopping reaps the presented kit and silences creator audio.</summary>
    private void ToggleCreatorLoop()
    {
        _creatorLoopOn = !_creatorLoopOn;
        _creatorLoopNextAt = 0;
        _creatorLoopCastAt = _creatorLoopMissileAt = _creatorLoopImpactAt = double.MaxValue;
        if (!_creatorLoopOn)
        {
            ReapPresentedEffect();
            StopAllCreatorAudio();
        }
    }

    /// <summary>
    /// Play ONE phase on repeat - the IDE inspector's "solo" button, so selecting a
    /// row can also mean "show me just this". Driven through the loop machinery
    /// rather than PresentSpellEffect, because that only accepts the five stage
    /// kits: the missile runs through its own spawn path and the area through the
    /// area machine, and the loop is the one place all of them are sequenced.
    /// </summary>
    private void SoloCreatorSpellPhase(string sectionId)
    {
        if (_creatorSpell is not { } doc) return;
        if (!sectionId.StartsWith("ws-", StringComparison.Ordinal)) return;
        string path = sectionId[3..];

        // A geometry child has no phase of its own - it is spawned per particle by a
        // host emitter, so soloing it means soloing that host. GeometryHosts stores a
        // FILE NAME, not a full path, hence the filename-to-filename join.
        if (doc.GeometryHosts.TryGetValue(path, out string? hostFile))
        {
            foreach (string candidate in doc.Models.Keys)
                if (string.Equals(Path.GetFileName(candidate), hostFile,
                        StringComparison.OrdinalIgnoreCase))
                {
                    path = candidate;
                    break;
                }
        }

        var stages = doc.PhaseModels
            .Where(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Stage).Distinct().ToList();
        bool missile = CreatorEffectiveMissile(doc) is { Length: > 0 } effectiveMissile &&
                       string.Equals(effectiveMissile, path, StringComparison.OrdinalIgnoreCase);
        // Nothing to solo: leave playback exactly as the user left it.
        if (stages.Count == 0 && !missile) return;

        // Drop whatever kit is currently presented. The state/channel holds STICK to
        // the character until something reaps them, so soloing the missile (or any
        // phase that does not itself present) would otherwise leave the old effect
        // glued on with no obvious way off but the Loop section's Stop button.
        ReapPresentedEffect();

        _creatorLoopPrecast = _creatorLoopCast = _creatorLoopMissilePhase =
            _creatorLoopImpactPhase = _creatorLoopStateHold = _creatorLoopChannelHold =
            _creatorLoopAreaHold = false;
        foreach (SpellStage stage in stages)
        {
            switch (stage)
            {
                case SpellStage.Precast: _creatorLoopPrecast = true; break;
                case SpellStage.Cast: _creatorLoopCast = true; break;
                case SpellStage.Impact: _creatorLoopImpactPhase = true; break;
                case SpellStage.State: _creatorLoopStateHold = true; break;
                case SpellStage.Channel: _creatorLoopChannelHold = true; break;
            }
        }
        if (missile) _creatorLoopMissilePhase = true;

        // ORDER MATTERS: the loop tick switches itself off (and stops all audio) when
        // no phase is checked, so _creatorLoopOn must be set only once the boxes are.
        _creatorLoopOn = true;
        _creatorLoopNextAt = 0;
        _creatorLoopCastAt = _creatorLoopMissileAt = _creatorLoopImpactAt = double.MaxValue;
    }

    private static string CreatorAudioLabel(CreatorAudioCue cue) => cue switch
    {
        CreatorAudioCue.Precast => "Precast",
        CreatorAudioCue.Cast => "Cast release",
        CreatorAudioCue.Missile => "Missile flight",
        CreatorAudioCue.Impact => "Impact",
        CreatorAudioCue.State => "Persistent state",
        CreatorAudioCue.Channel => "Channel",
        CreatorAudioCue.Area => "Area",
        _ => cue.ToString(),
    };

    private string CreatorAudioDescription(CreatorSpellDoc doc, CreatorAudioCue cue)
    {
        if (doc.Audio.TryGetValue(cue, out CreatorAudioTrack? custom))
            return $"custom: {Path.GetFileName(custom.SourcePath)}";
        if (CreatorPickedSound(doc, cue) is uint picked && _spellSounds?.TryGetEntry(picked, out SoundEntry pickedEntry) == true)
            return $"game sound {picked}: {(pickedEntry.Name.Length > 0 ? pickedEntry.Name : Path.GetFileName(pickedEntry.Variants.FirstOrDefault().Path ?? ""))}" +
                   (pickedEntry.Looping ? "  (loop)" : "");
        uint? sound = CreatorAuthoredSound(doc, cue);
        if (_spellSounds?.TryGetEntry(sound, out SoundEntry entry) == true)
        {
            string variants = string.Join(", ", entry.Variants.Take(2)
                .Select(v => Path.GetFileName(v.Path)));
            if (entry.Variants.Count > 2) variants += $" +{entry.Variants.Count - 2}";
            return $"authored {entry.Id}: {(entry.Name.Length > 0 ? entry.Name : variants)}" +
                   (entry.Looping ? "  (loop)" : "");
        }
        return "no authored cue";
    }

    private void DrawCreatorAudioBody()
    {
        if (_creatorSpell is not { } doc) return;
        float cs = CreatorUiScale;
        ImGui.TextWrapped("Each sound belongs to a spell phase. Preview the original cue or " +
            "replace it with a WAV/MP3; custom audio is carried into both the session " +
            "and the exported patch.");
        CreatorHelp("A phase has one SoundEntries cue, which may itself contain several " +
            "weighted variants. This first creator pass imports one file per phase; " +
            "variant lists will expand here without changing the spell model.");

        if (_creatorAudioPreviewVoice != 0)
        {
            if (ImGui.SmallButton("Stop preview"))
            {
                _spellSounds?.Stop(_creatorAudioPreviewVoice);
                _creatorAudioPreviewVoice = 0;
            }
            ImGui.Spacing();
        }

        foreach (CreatorAudioCue cue in CreatorAudioCueOrder)
        {
            ImGui.PushID($"audio-{cue}");
            bool available = CreatorAudioAvailable(doc, cue);
            bool custom = doc.Audio.TryGetValue(cue, out CreatorAudioTrack? track);
            if (!available) ImGui.BeginDisabled();

            ImGui.TextUnformatted(CreatorAudioLabel(cue));
            ImGui.SameLine();
            ImGui.TextDisabled(CreatorAudioDescription(doc, cue));

            bool hasSound = custom || CreatorPickedSound(doc, cue) is not null || CreatorAuthoredSound(doc, cue) is not null;
            if (!hasSound) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Preview"))
            {
                if (_creatorAudioPreviewVoice != 0) _spellSounds?.Stop(_creatorAudioPreviewVoice);
                Vector3 source = cue == CreatorAudioCue.Area
                    ? CreatorAreaPosition() : _controller?.Position ?? Vector3.Zero;
                _creatorAudioPreviewVoice = PlayCreatorAudio(cue, LocalPlayerGuid, source,
                    forceLoop: cue == CreatorAudioCue.Missile);
            }
            if (!hasSound) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton(custom ? "Replace file" : "Import file"))
                BeginCreatorAudioImport(cue);
            ImGui.SameLine();
            if (ImGui.SmallButton(_creatorSoundPickerCue == cue ? "Close##gs" : "Game sound"))
                _creatorSoundPickerCue = _creatorSoundPickerCue == cue ? null : cue;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Use one of the game's own sounds for this part (any of its 4,600 " +
                                 "SoundEntries) - nothing to import, every variant kept.");
            if (CreatorPickedSound(doc, cue) is not null && !custom)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear##gs")) { SetCreatorPickedSound(doc, cue, null); StopCreatorAudio(cue); }
            }
            if (_creatorSoundPickerCue == cue) DrawCreatorSoundPicker(doc, cue, cs);
            if (custom)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Restore authored")) RemoveCreatorAudio(cue);

                var defaults = CreatorAudioDefaults(doc, cue);
                float volume = track!.Volume;
                ImGui.SetNextItemWidth(150f * cs);
                if (ImGui.SliderFloat("Volume", ref volume, 0f, 1f, "%.2f"))
                    track.Volume = volume;
                if (CreatorResetKnob("avolume")) track.Volume = defaults.Volume;
                bool missileLoop = cue == CreatorAudioCue.Missile;
                bool loop = missileLoop || track.Looping;
                if (missileLoop) ImGui.BeginDisabled();
                bool loopChanged = ImGui.Checkbox("Loop", ref loop);
                if (missileLoop) ImGui.EndDisabled();
                if (!missileLoop && CreatorResetKnob("aloop") && track.Looping != defaults.Looping)
                {
                    loop = defaults.Looping;
                    loopChanged = true;
                }
                if (!missileLoop && loopChanged)
                {
                    track.Looping = loop;
                    StopCreatorAudio(cue);
                    _creatorSustainedAudio.Remove(cue);
                    if (cue == CreatorAudioCue.Area && _creatorAreaActive)
                    {
                        _spellEffects?.ReapArea(CreatorAreaGuid);
                        _creatorAreaActive = false;
                    }
                }
                CreatorHelp(missileLoop
                    ? "Missile audio loops automatically for exactly the projectile lifetime."
                    : "Looping cues continue until their phase ends.");

                if (Settings.Creator.SpellAdvancedMode)
                {
                    bool noDup = track.NoDuplicates;
                    if (ImGui.Checkbox("No immediate duplicate", ref noDup))
                        track.NoDuplicates = noDup;
                    if (CreatorResetKnob("anodup")) track.NoDuplicates = defaults.NoDuplicates;
                    float min = track.MinDistance, cutoff = track.CutoffDistance;
                    ImGui.SetNextItemWidth(150f * cs);
                    if (ImGui.SliderFloat("Full volume distance", ref min, 0f, 50f, "%.1f yd"))
                        track.MinDistance = Math.Min(min, track.CutoffDistance);
                    if (CreatorResetKnob("amindist"))
                        track.MinDistance = Math.Min(defaults.MinDistance, track.CutoffDistance);
                    ImGui.SetNextItemWidth(150f * cs);
                    if (ImGui.SliderFloat("Cutoff distance", ref cutoff, 0f, 150f, "%.1f yd"))
                    {
                        track.CutoffDistance = cutoff;
                        track.MinDistance = Math.Min(track.MinDistance, cutoff);
                    }
                    if (CreatorResetKnob("acutoff"))
                    {
                        track.CutoffDistance = defaults.CutoffDistance;
                        track.MinDistance = Math.Min(track.MinDistance, defaults.CutoffDistance);
                    }
                }
            }

            if (!available)
            {
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This source visual has no corresponding kit/flight lane yet.");
            }
            ImGui.Separator();
            ImGui.PopID();
        }
    }

    // ── the game-sound picker (SPELL_CREATOR_PLAN.md P3.9) ──
    private CreatorAudioCue? _creatorSoundPickerCue;
    private readonly byte[] _creatorSoundSearchBuf = new byte[64];
    private static readonly string[] CreatorSoundChips =
        { "lightning", "thunder", "fire", "frost", "arcane", "shadow", "holy", "nature", "impact", "cast", "precast", "explosion", "loop" };

    private void DrawCreatorSoundPicker(CreatorSpellDoc doc, CreatorAudioCue cue, float cs)
    {
        if (_soundKits?.Catalog is not { } catalog) { ImGui.TextDisabled("SoundEntries are not loaded."); return; }
        ImGui.SetNextItemWidth(200f * cs);
        ImGui.InputText("Search##gs", _creatorSoundSearchBuf, (uint)_creatorSoundSearchBuf.Length);
        bool first = true;
        foreach (string chip in CreatorSoundChips)
        {
            float width = ImGui.CalcTextSize(chip).X + ImGui.GetStyle().FramePadding.X * 2f + ImGui.GetStyle().ItemSpacing.X;
            if (!first && ImGui.GetItemRectMax().X + width < ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X)
                ImGui.SameLine();
            if (ImGui.SmallButton(chip + "##gschip"))
            {
                Array.Clear(_creatorSoundSearchBuf);
                System.Text.Encoding.UTF8.GetBytes(chip, _creatorSoundSearchBuf);
            }
            first = false;
        }
        string[] words = BufToString(_creatorSoundSearchBuf).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Name or any variant's file: "Spell" sounds are named for the spell, files for the sound.
        List<SoundEntry> hits = catalog.Entries
            .Where(e => words.All(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                       e.Variants.Any(v => v.Path.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(80).ToList();
        uint? current = CreatorPickedSound(doc, cue);
        if (ImGui.BeginChild("##gs-list", new Vector2(-1f, 180f * cs), true))
        {
            foreach (SoundEntry entry in hits)
            {
                ImGui.PushID((int)entry.Id);
                if (ImGui.SmallButton(">"))
                {
                    if (_creatorAudioPreviewVoice != 0) _spellSounds?.Stop(_creatorAudioPreviewVoice);
                    Vector3 at = _controller?.Position ?? Vector3.Zero;
                    _creatorAudioPreviewVoice = _spellSounds?.Play(entry.Id, LocalPlayerGuid, at, at,
                        forceLoop: false, trackHold: false, category: "creator-pick") ?? 0;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Listen");
                ImGui.SameLine();
                string files = string.Join(", ", entry.Variants.Take(2).Select(v => Path.GetFileNameWithoutExtension(v.Path)));
                if (entry.Variants.Count > 2) files += $" +{entry.Variants.Count - 2}";
                if (ImGui.Selectable($"{entry.Id}  {entry.Name}  ({files}){(entry.Looping ? "  loop" : "")}", current == entry.Id))
                {
                    RemoveCreatorAudio(cue);                  // an imported file would win over it
                    SetCreatorPickedSound(doc, cue, entry.Id);
                    StopCreatorAudio(cue);
                    _creatorSustainedAudio.Remove(cue);
                }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.TextDisabled(hits.Count == 80 ? "First 80 shown; narrow the search." : $"{hits.Count} sounds");
    }

    private void BeginCreatorAudioImport(CreatorAudioCue cue)
    {
        _creatorAudioPickerCue = cue;
        _creatorAudioPickerError = "";
        if (Directory.Exists(_creatorAudioPickerDir)) return;
        string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        _creatorAudioPickerDir = new[] { music, desktop, Environment.CurrentDirectory }
            .FirstOrDefault(Directory.Exists) ?? Environment.CurrentDirectory;
    }

    private static string CreatorAudioAssetToken(string value)
    {
        string token = new(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_'
            ? char.ToLowerInvariant(c) : '_').ToArray());
        while (token.Contains("__", StringComparison.Ordinal)) token = token.Replace("__", "_");
        return token.Trim('_');
    }

    /// <summary>The authored-cue values a custom track starts from - shared by
    /// import and the per-knob resets in the Audio section.</summary>
    private (float Volume, bool Looping, bool NoDuplicates, uint SoundType, uint ExtraFlags,
        uint Eax, float MinDistance, float CutoffDistance)
        CreatorAudioDefaults(CreatorSpellDoc doc, CreatorAudioCue cue)
    {
        uint? sourceId = CreatorAuthoredSound(doc, cue);
        SoundEntry source = default;
        bool hasSource = _spellSounds?.TryGetEntry(sourceId, out source) == true;
        return (
            hasSource ? source.Volume : 1f,
            cue == CreatorAudioCue.Missile || (hasSource && source.Looping) ||
                (!hasSource && cue is CreatorAudioCue.State or CreatorAudioCue.Channel or CreatorAudioCue.Area),
            hasSource && source.NoDuplicates,
            hasSource ? source.Type : 1u,
            hasSource ? source.Flags & ~(0x200u | 0x20u) : 0u,
            hasSource ? source.Eax : 0u,
            hasSource ? source.MinDistance : 10f,
            hasSource ? source.CutoffDistance : 80f);
    }

    private void ImportCreatorAudio(CreatorAudioCue cue, string file)
    {
        if (_creatorSpell is not { } doc) return;
        try
        {
            string full = Path.GetFullPath(file);
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext is not (".wav" or ".mp3"))
                throw new InvalidDataException("Spell audio must be a WAV or MP3 file.");
            byte[] bytes = File.ReadAllBytes(full);
            if (bytes.Length == 0) throw new InvalidDataException("The selected file is empty.");
            if (bytes.Length > 64 * 1024 * 1024)
                throw new InvalidDataException("The selected file is larger than 64 MB.");
            if (ext == ".wav" && (bytes.Length < 12 ||
                !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)))
                throw new InvalidDataException("The selected .wav is not a RIFF/WAVE file.");

            string spell = CreatorAudioAssetToken(doc.Info.Name);
            if (spell.Length == 0) spell = $"spell_{doc.Info.Id}";
            string stem = CreatorAudioAssetToken(Path.GetFileNameWithoutExtension(full));
            if (stem.Length == 0) stem = "audio";
            string cueName = cue.ToString().ToLowerInvariant();
            string mpqPath = $@"Sound\Spells\Custom\{doc.Info.Id}_{spell}\{cueName}_{stem}{ext}";

            var defaults = CreatorAudioDefaults(doc, cue);
            var track = new CreatorAudioTrack
            {
                SourcePath = full,
                MpqPath = mpqPath,
                Bytes = bytes,
                Volume = defaults.Volume,
                Looping = defaults.Looping,
                NoDuplicates = defaults.NoDuplicates,
                SoundType = defaults.SoundType,
                ExtraFlags = defaults.ExtraFlags,
                Eax = defaults.Eax,
                MinDistance = defaults.MinDistance,
                CutoffDistance = defaults.CutoffDistance,
            };
            if (doc.Audio.Remove(cue, out CreatorAudioTrack? old))
                _spellSounds?.RemoveCustomFile(old.MpqPath);
            doc.Audio[cue] = track;
            StopCreatorAudio(cue);
            _creatorSustainedAudio.Remove(cue);
            if (_creatorAudioPreviewVoice != 0) _spellSounds?.Stop(_creatorAudioPreviewVoice);
            _creatorAudioPreviewVoice = 0;
            if (cue == CreatorAudioCue.Area && _creatorAreaActive)
            {
                _spellEffects?.ReapArea(CreatorAreaGuid);
                _creatorAreaActive = false;
            }
            _creatorAudioPickerDir = Path.GetDirectoryName(full) ?? _creatorAudioPickerDir;
            _creatorAudioPickerCue = null;
            _creatorExportStatus = $"Imported {Path.GetFileName(full)} for {CreatorAudioLabel(cue)}.";
        }
        catch (Exception ex)
        {
            _creatorAudioPickerError = ex.Message;
        }
    }

    private void RemoveCreatorAudio(CreatorAudioCue cue)
    {
        if (_creatorSpell is not { } doc || !doc.Audio.Remove(cue, out CreatorAudioTrack? track)) return;
        _spellSounds?.RemoveCustomFile(track.MpqPath);
        StopCreatorAudio(cue);
        _creatorSustainedAudio.Remove(cue);
        if (_creatorAudioPreviewVoice != 0) _spellSounds?.Stop(_creatorAudioPreviewVoice);
        _creatorAudioPreviewVoice = 0;
        if (cue == CreatorAudioCue.Area && _creatorAreaActive)
        {
            _spellEffects?.ReapArea(CreatorAreaGuid);
            _creatorAreaActive = false;
        }
    }

    private void DrawCreatorAudioFilePicker()
    {
        if (_creatorAudioPickerCue is not { } cue) return;
        bool close = false;
        float cs = CreatorUiScale;
        ImGui.SetNextWindowSize(new Vector2(560f * cs, 520f * cs), ImGuiCond.FirstUseEver);
        PushCreatorStyle();
        if (ImGui.Begin("###creator-audio-file-picker", CreatorChromeFlags))
        {
            ClampCreatorWindowOnScreen();
            if (DrawCreatorPanelChrome($"Import {CreatorAudioLabel(cue)} audio")) close = true;
            ImGui.SetWindowFontScale(CreatorTextScale);
            BeginCreatorContent();

            ImGui.TextWrapped("Choose a WAV or MP3. The file is copied into the spell " +
                "session and patch; MSUI never depends on this disk path after import.");
            if (_creatorAudioPickerError.Length > 0)
                ImGui.TextWrapped($"Could not import: {_creatorAudioPickerError}");

            ImGui.TextDisabled(_creatorAudioPickerDir);
            if (ImGui.SmallButton("Up"))
            {
                try
                {
                    DirectoryInfo? parent = Directory.GetParent(_creatorAudioPickerDir);
                    if (parent is not null) _creatorAudioPickerDir = parent.FullName;
                }
                catch (Exception ex) { _creatorAudioPickerError = ex.Message; }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel")) close = true;

            ImGui.BeginChild("##creator-audio-files", new Vector2(0f, 380f * cs), true);
            try
            {
                var dirs = new List<string>();
                DirectoryInfo? parent = Directory.GetParent(_creatorAudioPickerDir);
                if (parent is null && OperatingSystem.IsWindows())
                    dirs.AddRange(DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName));
                dirs.AddRange(Directory.EnumerateDirectories(_creatorAudioPickerDir)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(200));

                string? nextDir = null;
                foreach (string dir in dirs)
                {
                    string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar));
                    if (name.Length == 0) name = dir;
                    if (ImGui.Selectable($"[folder] {name}##{dir}")) { nextDir = dir; break; }
                }
                if (nextDir is not null)
                {
                    _creatorAudioPickerDir = nextDir;
                    _creatorAudioPickerError = "";
                }
                else
                {
                    foreach (string file in Directory.EnumerateFiles(_creatorAudioPickerDir)
                                 .Where(f => Path.GetExtension(f).Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                                             Path.GetExtension(f).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(500))
                    {
                        if (!ImGui.Selectable($"{Path.GetFileName(file)}##{file}")) continue;
                        ImportCreatorAudio(cue, file);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _creatorAudioPickerError = ex.Message;
            }
            ImGui.EndChild();
            EndCreatorContent();
        }
        ImGui.End();
        PopCreatorStyle();
        if (close)
        {
            _creatorAudioPickerCue = null;
            _creatorAudioPickerError = "";
        }
    }

    private void DrawCreatorSessionSection()
    {
        var doc = _creatorSpell;
        if (doc is null) { ImGui.TextDisabled("Pick a spell first."); return; }

        if (CreatorButton("Reset all"))
        {
            foreach (var model in doc.Models.Values)
            {
                model.Edits.Clear();
                model.TextureHues.Clear();
                foreach (int texIndex in model.TextureTints.Keys)
                    SetCreatorTextureTint(model, texIndex, null);
                model.TextureTints.Clear();
                model.TextureSwaps.Clear();
                model.DisabledEmitters.Clear();
                model.AddedEmitters.Clear();
                model.HueShift = false;
                model.RateMul = model.ScaleMul = model.LifeMul = model.SpeedMul = 1f;
                model.GravityAdd = 0f;
                RebuildCreatorModel(model);
            }
            foreach (CreatorAudioCue cue in doc.Audio.Keys.ToArray())
                RemoveCreatorAudio(cue);
            StopAllCreatorAudio();
        }
        DrawCreatorSessionBody(doc);
        if (_creatorExportStatus.Length > 0) ImGui.TextWrapped(_creatorExportStatus);
    }

    private void DrawCreatorModelEditor(CreatorModelDoc model, float cs)
    {
        bool dirty = DrawCreatorModelLook(model, cs);
        bool advanced = Settings.Creator.SpellAdvancedMode;

        var (texByEmitter, slotByEmitter) = CreatorEmitterTextureMaps(model);

        dirty |= DrawCreatorMeshRibbonCategories(model, cs);

        ImGui.Spacing();
        ImGui.TextDisabled("EMITTERS");
        CreatorHelp(advanced
            ? "Each emitter is one particle source inside the model - its own image, " +
              "blend mode, spray shape and motion. These sliders set ABSOLUTE values (the " +
              "model dials above are multipliers over them). A * on a slider means the " +
              "authored track is animated over time; the slider overrides its first key."
            : "Each emitter is one visible ingredient in this model. The complete original " +
              "workshop remains here: add/remove/duplicate, enable, blend, birth shape, " +
              "rate, speed, gravity, lifespan, spread, area and scale. Advanced mode adds " +
              "technical composition, placement, drag/spin and behavior controls.");
        foreach (var emitter in model.Emitters)
        {
            // Category id is model path + index: stable while blend/type in the
            // label change as they are edited.
            string texName = texByEmitter.GetValueOrDefault(emitter.Index, "no tex");
            bool emitterOff = model.DisabledEmitters.Contains(emitter.Index);
            bool isAdded = emitter.Index >= model.OriginalEmitterCount;
            // The emitter wears its texture's identity color - the same swatch
            // as the image's row above, so the wiring reads at a glance.
            Vector4? marker = slotByEmitter.TryGetValue(emitter.Index, out int slot)
                ? CreatorSlotColor(slot) : null;
            string formatDetails = advanced
                ? $", blend {emitter.BlendMode}, shape {emitter.EmitterType}, bone {emitter.Bone}"
                : $", blend {emitter.BlendMode}, shape {emitter.EmitterType}";
            bool emitterOpen = CreatorCategory($"ws-{model.Path}-em{emitter.Index}",
                $"Emitter {emitter.Index}  " +
                $"({texName}{formatDetails})" +
                (isAdded ? "  [added]" : "") +
                (emitterOff ? "  [OFF]" : ""), marker: marker);
            // The gizmo layer draws the hovered header's emitter bold (SPELL_CREATOR_IDE §2.3).
            if (_creatorCategoryHovered) _creatorGizmoHover = (model.Path, emitter.Index);
            if (!emitterOpen) continue;
            ImGui.PushID(emitter.Index);
            ImGui.Indent(10f * cs);
            CreatorEmitterEdit verdict = DrawCreatorEmitterBody(model, emitter, slotByEmitter, cs);
            ImGui.Unindent(10f * cs);
            ImGui.PopID();
            if (verdict == CreatorEmitterEdit.Removed)
            {
                RebuildCreatorModel(model);
                return;   // indices shifted - redraw next frame from the new list
            }
            if (verdict == CreatorEmitterEdit.Dirty) dirty = true;
        }

        if (dirty) RebuildCreatorModel(model);
    }

    /// <summary>The model-wide dials and the texture rows: everything about a phase that is
    /// not one emitter. Returns true when a rebuild is due. Shared by the classic editor
    /// and the IDE's phase inspector (GameLoop.Creator.SpellIde.cs).</summary>
    private bool DrawCreatorModelLook(CreatorModelDoc model, float cs)
    {
        bool dirty = false;

        // A GENERATED model is a special case worth interrupting for. The dials below all work
        // on it, but the Sketch rebuilds this file from its cards on the next change, so an edit
        // typed here is temporary and vanishes without a word. Say so before that happens rather
        // than after - the owner had already edited a bone here and would have lost it.
        CreatorSpellDoc? sketchOwner = _creatorSpell;
        Creator.Sketch.SketchDoc? sketchDoc = null;
        bool sketchModel = sketchOwner is not null &&
                           TryGetSketchForModel(sketchOwner, model.Path, out sketchDoc);
        if (sketchModel)
        {
            ImGui.TextColored(new Vector4(0.95f, 0.72f, 0.35f, 1f), "GENERATED BY THE SKETCH WINDOW");
            CreatorHelp("This model is built from the Sketch window's cards, and it is rebuilt " +
                "from them every time one of those cards changes.\n\nThe dials below still work, " +
                "but they are TEMPORARY: the next card change throws them away. Edit the piece in " +
                "the Sketch window instead.\n\nThe one exception is dragging in the world - the " +
                "translate arrows and rotate rings on a sketch piece are read straight back into " +
                "its PLACE card, so those do stick.");
            ImGui.SameLine();
            if (ImGui.SmallButton("open Sketch")) _sketchOpen = true;

            // The colour of a sketch piece is its LOOK card, and this is where the owner went
            // looking for it (2026-09-19: six seconds dragging a Tint swatch on the sketch's
            // white image, which the hue-map could not colour). So the piece colours live here
            // too, bound to the same field the LOOK card writes; the image rows below carry no
            // Hue or Tint for a sketch, because the image is white BY DESIGN and the colour
            // record is the one place the colour is meant to be.
            ImGui.TextDisabled("PIECE COLOURS (the LOOK card's colour, per piece)");
            for (int i = 0; i < sketchDoc!.Pieces.Count; i++)
            {
                Creator.Sketch.SketchPiece piece = sketchDoc.Pieces[i];
                ImGui.PushID(5000 + i);
                Vector3 pieceColour = piece.Look.Colour;
                ImGui.SetNextItemWidth(140f * cs);
                if (ImGui.ColorEdit3("##piececolour", ref pieceColour, ImGuiColorEditFlags.NoInputs))
                {
                    piece.Look.Colour = pieceColour;
                    SketchChanged();
                }
                ImGui.SameLine();
                float pieceAlpha = piece.Look.Alpha;
                ImGui.SetNextItemWidth(90f * cs);
                if (ImGui.SliderFloat("##piecealpha", ref pieceAlpha, 0.05f, 1f, "a %.2f"))
                {
                    piece.Look.Alpha = pieceAlpha;
                    SketchChanged();
                }
                ImGui.SameLine();
                if (ImGui.Selectable(piece.Name, _sketchSelected == i))
                {
                    SelectSketchPiece(sketchOwner!, sketchDoc, i);
                    _sketchOpen = true;
                }
                ImGui.PopID();
            }
            ImGui.Separator();
        }

        // Whole-model dials, each with its own reset and explainer.
        ImGui.TextDisabled("MODEL DIALS (multipliers over the authored values)");
        dirty |= ImGui.Checkbox("Hue (particles + trail)", ref model.HueShift);
        if (model.HueShift)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140f * cs);
            dirty |= ImGui.ColorEdit3("##hue", ref model.HueColor, ImGuiColorEditFlags.NoInputs);
        }
        CreatorHelp("Rotates every particle's start/mid/end colors AND the ribbon trail's " +
            "color track toward this color, keeping each one's brightness (a bright core " +
            "stays bright, a dark swirl stays dark).\n\nIt does NOT repaint the texture " +
            "images themselves - art whose color is baked into the BLP needs the per-image " +
            "Tint below. For full recolors combine this with Tint on each image.");

        bool Dial(string label, ref float value, float min, float max, float reset,
            string format, string help, bool log = false)
        {
            ImGui.SetNextItemWidth(CreatorControlWidth);
            bool changed = ImGui.SliderFloat(label, ref value, min, max, format,
                log ? ImGuiSliderFlags.Logarithmic : ImGuiSliderFlags.None);
            if (CreatorResetKnob(label)) { value = reset; changed = true; }
            CreatorHelp(help);
            return changed;
        }

        dirty |= Dial("Density", ref model.RateMul, 0.1f, 10f, 1f, "%.2fx",
            "Emission-rate multiplier over every emitter: how many particles are born " +
            "per second. Higher = thicker, fuller effect; lower = sparse wisps.", log: true);
        dirty |= Dial("Size", ref model.ScaleMul, 0.1f, 10f, 1f, "%.2fx",
            "Particle size multiplier (scales each particle's start/mid/end size " +
            "together). The emitter shape and count stay the same.", log: true);
        dirty |= Dial("Duration", ref model.LifeMul, 0.1f, 5f, 1f, "%.2fx",
            "Lifespan multiplier: how long each particle lives before fading. Longer " +
            "lifetimes read as lingering smoke/trails; shorter as sharp crackle.", log: true);
        dirty |= Dial("Speed", ref model.SpeedMul, 0.1f, 5f, 1f, "%.2fx",
            "Emission-speed multiplier: how fast particles fly away from their emitter. " +
            "Faster = explosive burst; slower = hovering shimmer.", log: true);
        dirty |= Dial("Gravity +", ref model.GravityAdd, -10f, 10f, 0f, "%.2f",
            "ADDED to every emitter's authored gravity. Positive pulls particles down " +
            "(rain, embers falling); negative makes them rise (smoke, sparks floating up).");

        // The actual BLPs this model is built from, each with its own hue dial
        // when particles reference it. A phase made of several images recolors
        // per image here; the master dial above moves everything at once.
        if (model.Textures.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("TEXTURES (the BLPs behind this phase)");
            CreatorHelp("Every image this model draws with. Hue recolors the PARTICLES " +
                "that use an image; Tint repaints the image's own pixels; Swap replaces " +
                "the image with any other BLP in the game - the splicing tool.\n\n" +
                "NOTE: Tint is keyed by image path, so tinting a BLP that other phases " +
                "or spells share changes it everywhere it appears. To recolor ONE phase " +
                "only, Swap that slot to different art first, then Tint the swap.");
            foreach (var tex in model.Textures)
            {
                ImGui.PushID(1000 + tex.Index);
                // Identity swatch: the same color marks every emitter below that
                // draws with this image.
                ImGui.ColorButton($"##slotc{tex.Index}", CreatorSlotColor(tex.Index),
                    ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop,
                    new Vector2(12f * cs, 12f * cs));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This image's identity color - the emitters below " +
                                     "wearing the same swatch draw with this image.");
                ImGui.SameLine();
                string effectivePath = EffectiveTexturePath(model, tex.Index);
                float thumb = MathF.Min(26f * cs, 40f);
                uint art = effectivePath.Length > 0 ? _gameplayArt?.AdditiveHandle(effectivePath) ?? 0 : 0;
                if (art != 0)
                {
                    ImGui.Image((nint)art, new Vector2(thumb, thumb));
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(effectivePath);
                        ImGui.Image((nint)art, new Vector2(128f, 128f));
                        ImGui.EndTooltip();
                    }
                    ImGui.SameLine();
                }
                bool swapped = model.TextureSwaps.ContainsKey(tex.Index);
                string name = tex.Filename.Length > 0 ? Path.GetFileName(tex.Filename) : $"(slot {tex.Index})";
                if (swapped) name = $"{name} -> {Path.GetFileName(effectivePath)}";
                bool used = tex.ReferencedByEmitters.Count > 0;

                // Hue: rotates the emitter particle colors (particle-driven only).
                if (used && !sketchModel)
                {
                    var hue = model.TextureHues.TryGetValue(tex.Index, out var found)
                        ? found : new CreatorTexHue();
                    bool on = hue.On;
                    if (ImGui.Checkbox($"Hue##tex{tex.Index}", ref on))
                    {
                        hue.On = on;
                        model.TextureHues[tex.Index] = hue;
                        dirty = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Recolor the PARTICLES drawn with this image (their " +
                                         "start/mid/end colors), leaving the image pixels alone. " +
                                         "Overrides the whole-model hue for these emitters.");
                    if (hue.On)
                    {
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(140f * cs);
                        if (ImGui.ColorEdit3($"##texhue{tex.Index}", ref hue.Color,
                                ImGuiColorEditFlags.NoInputs))
                        {
                            model.TextureHues[tex.Index] = hue;
                            dirty = true;
                        }
                    }
                    ImGui.SameLine();
                }

                // Tint: the TRUE palette swap - hue-maps the BLP's own pixels, so
                // art whose color is baked into the image really changes color.
                // Available on every texture, including mesh/ribbon ones the
                // particle hue can't reach - except a sketch's own white images, whose colour
                // is the PIECE COLOURS block above (the LOOK card).
                if (tex.Filename.Length > 0 && !sketchModel)
                {
                    var tint = model.TextureTints.TryGetValue(tex.Index, out var foundTint)
                        ? foundTint : new CreatorTexHue();
                    bool tintOn = tint.On;
                    if (ImGui.Checkbox($"Tint##textint{tex.Index}", ref tintOn))
                    {
                        tint.On = tintOn;
                        model.TextureTints[tex.Index] = tint;
                        SetCreatorTextureTint(model, tex.Index, tintOn ? tint.Color : null);
                        dirty = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Palette-swap the image itself (recolors the BLP's pixels).\n" +
                                         "Use this when Hue does nothing - the color is baked into the art.");
                    if (tint.On)
                    {
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(140f * cs);
                        if (ImGui.ColorEdit3($"##textint{tex.Index}", ref tint.Color,
                                ImGuiColorEditFlags.NoInputs))
                        {
                            model.TextureTints[tex.Index] = tint;
                            SetCreatorTextureTint(model, tex.Index, tint.Color);
                            // A tint lives in the renderers' texture layer and touches no byte
                            // of the M2, so dragging the swatch must not rebuild the model: it
                            // did, once per frame for as long as the mouse moved, re-parsing
                            // the file and re-baking the mesh sixty times a second for a change
                            // the texture layer had already applied. Only the "modified" fact
                            // is owed here (the export must include the recolored BLP).
                            model.Modified = true;
                        }
                    }
                    ImGui.SameLine();
                }

                if (ImGui.SmallButton("Swap"))
                    _texSwapTarget = (model.Path, tex.Index);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Replace this image with any other BLP - from this " +
                                     "spell, any other spell, or a typed path.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Import"))
                    BeginCreatorTextureImport(model, tex.Index);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Bring in YOUR OWN art: a PNG (converted to BLP at the nearest " +
                                     "power-of-two size) or a BLP. It previews at once and ships in " +
                                     "the session.");
                // Grow the effect from this image: clone an emitter onto this
                // slot (its own emitter when it has one, else any authored one
                // retargeted here) and tune the clone like any other emitter.
                if (model.OriginalEmitterCount > 0 &&
                    model.OriginalEmitterCount + model.AddedEmitters.Count < 255)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("+Em"))
                    {
                        int source = tex.ReferencedByEmitters
                            .FirstOrDefault(e => e < model.OriginalEmitterCount, 0);
                        model.AddedEmitters.Add(new CreatorAddedEmitter
                        { SourceIndex = source, TextureSlot = tex.Index });
                        dirty = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("ADD an emitter drawing with this image: a clone of " +
                            (tex.ReferencedByEmitters.Count > 0
                                ? "this image's first emitter"
                                : "emitter 0, retargeted to this image") +
                            ", appended to the model. Tune or remove it in EMITTERS below.");
                }
                if (swapped)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("x##unswap"))
                    {
                        if (_creatorSpell is { } curDoc)
                            ApplyCreatorTextureSwapEverywhere(curDoc, model, tex.Index, null);
                        else
                            ApplyCreatorTextureSwap(model, tex.Index, null);
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Restore the authored image");
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(name);
                ImGui.SameLine();
                // A slot whose emitters spawn per-particle geometry M2s never draws
                // this image directly - say where the pixels really come from.
                string? geoDrawn = tex.ReferencedByEmitters
                    .Select(e => model.EmitterGeometry.GetValueOrDefault(e))
                    .FirstOrDefault(p => p is { Length: > 0 });
                ImGui.TextDisabled(used
                    ? $"emitter{(tex.ReferencedByEmitters.Count == 1 ? "" : "s")} " +
                      string.Join(",", tex.ReferencedByEmitters) +
                      (geoDrawn is not null
                          ? $"  (drawn as {Path.GetFileName(geoDrawn)} meshes - edits follow)"
                          : "")
                    : "(mesh - use Tint)");
                ImGui.PopID();
            }
        }

        return dirty;
    }

    /// <summary>Emitter index -> the file name and slot of the texture it draws with, from
    /// the texture table's back-references (offset 0x016, the real textureId).</summary>
    private static (Dictionary<int, string> TexByEmitter, Dictionary<int, int> SlotByEmitter)
        CreatorEmitterTextureMaps(CreatorModelDoc model)
    {
        var texByEmitter = new Dictionary<int, string>();
        var slotByEmitter = new Dictionary<int, int>();
        foreach (var tex in model.Textures)
            foreach (int e in tex.ReferencedByEmitters)
            {
                texByEmitter[e] = Path.GetFileName(tex.Filename);
                slotByEmitter[e] = tex.Index;
            }

        return (texByEmitter, slotByEmitter);
    }

    private enum CreatorEmitterEdit { None, Dirty, Removed }

    /// <summary>One emitter's complete editor body (add/remove/duplicate, enable, blend, birth
    /// shape, placement, tracks, flags, scale). The caller owns the surrounding header, id
    /// scope and the rebuild: Dirty means rebuild, Removed means the index space shifted.
    /// Shared by the classic editor and the IDE's emitter inspector.</summary>
    private CreatorEmitterEdit DrawCreatorEmitterBody(CreatorModelDoc model, EmitterSnapshot emitter,
        Dictionary<int, int> slotByEmitter, float cs)
    {
        bool dirty = false;
        bool advanced = Settings.Creator.SpellAdvancedMode;
        bool emitterOff = model.DisabledEmitters.Contains(emitter.Index);
        bool isAdded = emitter.Index >= model.OriginalEmitterCount;

        if (isAdded)
        {
            if (ImGui.SmallButton("Remove emitter"))
            {
                RemoveCreatorAddedEmitter(model, emitter.Index);
                return CreatorEmitterEdit.Removed;   // indices shifted - the caller rebuilds
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Delete this added emitter (authored emitters can " +
                                 "only be disabled, never removed).");
        }
        else if (model.OriginalEmitterCount + model.AddedEmitters.Count < 255 &&
                 ImGui.SmallButton("Duplicate"))
        {
            model.AddedEmitters.Add(new CreatorAddedEmitter
            {
                SourceIndex = emitter.Index,
                TextureSlot = slotByEmitter.GetValueOrDefault(emitter.Index, 0),
            });
            dirty = true;
        }
        if (!isAdded &&
            model.OriginalEmitterCount + model.AddedEmitters.Count < 255 &&
            ImGui.IsItemHovered())
            ImGui.SetTooltip("ADD a copy of this emitter to the model - then tune the " +
                             "copy independently (thicker layers, second color, etc).");

        bool emitterOn = !emitterOff;
        if (ImGui.Checkbox("Enabled", ref emitterOn))
        {
            if (emitterOn) model.DisabledEmitters.Remove(emitter.Index);
            else model.DisabledEmitters.Add(emitter.Index);
            dirty = true;
        }
        CreatorHelp("Switch this emitter off wholesale - its emission is zeroed, no " +
            "particles are born, everything else keeps playing. Isolate emitters one " +
            "at a time to learn which does what. Re-enabling restores its authored " +
            "values and any Advanced-mode edits.");
        if (emitterOff)   // the dials stay: an empty inspector reads as "nothing is editable"
            ImGui.TextDisabled("Off. The dials below still edit this emitter; they show once it is on again.");

        EmitterPatch edit = model.Edits.TryGetValue(emitter.Index, out var found)
            ? found : new EmitterPatch { EmitterIndex = emitter.Index };

        if (advanced && !isAdded &&
            model.OriginalEmitterCount + model.AddedEmitters.Count <= 253)
        {
            ImGui.TextDisabled("COMPOSE A SOURCE LINE");
            ImGui.SetNextItemWidth(130f * cs);
            ImGui.Combo("Line axis", ref _creatorLineAxis,
                CreatorLineAxes, CreatorLineAxes.Length);
            if (CreatorResetKnob("lineaxis")) _creatorLineAxis = 0;
            ImGui.SetNextItemWidth(130f * cs);
            ImGui.Combo("Line count", ref _creatorLineCountChoice,
                CreatorLineCounts, CreatorLineCounts.Length);
            if (CreatorResetKnob("linecount")) _creatorLineCountChoice = 1;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            ImGui.SliderFloat("Line spacing", ref _creatorLineSpacing,
                0.05f, 10f, "%.2f yd");
            if (CreatorResetKnob("linespacing")) _creatorLineSpacing = 1f;
            if (ImGui.SmallButton("Create centered line"))
            {
                int count = 3 + _creatorLineCountChoice * 2;
                AddCreatorEmitterLine(model, emitter,
                    slotByEmitter.GetValueOrDefault(emitter.Index, 0),
                    _creatorLineAxis, count, _creatorLineSpacing);
                dirty = true;
            }
            CreatorHelp("Keep this source at the center and add evenly spaced cloned " +
                "sources on both sides. This is composition, not a density trick: the " +
                "clones receive real local position offsets and can be tuned or removed " +
                "individually. Local X/Y make a horizontal row; local Z makes a vertical " +
                "column. It does not rotate the birth plane, so the source model's local " +
                "frame still determines which way the effect faces.");
        }

        int blend = edit.BlendMode ?? emitter.BlendMode;
        ImGui.SetNextItemWidth(130f * cs);
        if (ImGui.Combo("Blend", ref blend, CreatorBlendModes, CreatorBlendModes.Length))
        { edit.BlendMode = blend; dirty = true; model.Edits[emitter.Index] = edit; }
        if (CreatorResetKnob("blend") && edit.BlendMode is not null)
        { edit.BlendMode = null; model.Edits[emitter.Index] = edit; dirty = true; }
        CreatorHelp("How the particles composite with the world:\n" +
            "0 Opaque - solid, no transparency.\n" +
            "1 Alpha key - hard cutout transparency.\n" +
            "2 Alpha blend - soft transparency by the image's alpha.\n" +
            "3 Add (no alpha) - additive color without alpha weighting.\n" +
            "4 Additive - pure light: the image's brightness ADDS to the scene, " +
            "black is invisible. Most fire/magic glows are 4.\n" +
            "5 Mod - multiply/darken.\n" +
            "6 Mod x2 - doubled multiply.");

        ushort effectiveType = (ushort)(edit.EmitterType ?? emitter.EmitterType);
        int type = Array.IndexOf(CreatorEmitterTypeIds, effectiveType);
        if (type < 0) type = 0;
        ImGui.SetNextItemWidth(130f * cs);
        if (ImGui.Combo("Birth shape", ref type, CreatorEmitterTypes, CreatorEmitterTypes.Length))
        { edit.EmitterType = CreatorEmitterTypeIds[type]; dirty = true; model.Edits[emitter.Index] = edit; }
        if (CreatorResetKnob("etype") && edit.EmitterType is not null)
        { edit.EmitterType = null; model.Edits[emitter.Index] = edit; dirty = true; }
        CreatorHelp("The shape particles are born from:\n" +
            "1 Plane - a flat local rectangle; Area L/W set its dimensions.\n" +
            "2 Sphere - a radial shell between the two Area radii.\n" +
            "3 Spline - along an authored path (changing another shape to Spline " +
            "cannot invent the required path).\n\nThis is the verified 16-bit M2 shape field; " +
            "the older Point/Sphere/Plane mapping edited a padding byte and had no effect.");

        if (advanced)
        {
            var position = new Vector3(
                edit.PositionX ?? emitter.PositionX,
                edit.PositionY ?? emitter.PositionY,
                edit.PositionZ ?? emitter.PositionZ);
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.DragFloat3("Local position", ref position, 0.05f, -50f, 50f, "%.3f"))
            {
                edit.PositionX = position.X;
                edit.PositionY = position.Y;
                edit.PositionZ = position.Z;
                model.Edits[emitter.Index] = edit;
                dirty = true;
            }
            if (CreatorResetKnob("position") &&
                (edit.PositionX is not null || edit.PositionY is not null || edit.PositionZ is not null))
            {
                edit.PositionX = edit.PositionY = edit.PositionZ = null;
                model.Edits[emitter.Index] = edit;
                dirty = true;
            }
            CreatorHelp("Move this particle source inside its M2. Coordinates use WoW's authored " +
                "local frame: X/Y are the ground plane and Z is up. If the emitter rides an " +
                "animated bone, this offset moves with that bone; it does not rotate the bone.");
        }

        bool TrackSlider(string label, string track, float min, float max,
            Func<EmitterPatch, float?> get, Action<EmitterPatch, float?> set, string help)
        {
            float? authored = emitter.TrackValues.GetValueOrDefault(track);
            if (authored is null) return false;   // no keyframes - nothing to patch
            float value = get(edit) ?? authored.Value;
            int keys = emitter.TrackKeyframeCounts.GetValueOrDefault(track);
            ImGui.SetNextItemWidth(CreatorControlWidth);
            bool moved = ImGui.SliderFloat(keys > 1 ? $"{label} *" : label, ref value, min, max, "%.3f");
            if (moved) { set(edit, value); model.Edits[emitter.Index] = edit; }
            if (CreatorResetKnob(track) && get(edit) is not null)
            { set(edit, null); model.Edits[emitter.Index] = edit; moved = true; }
            CreatorHelp(help + $"\n\nAuthored value: {authored.Value:0.###}" +
                (keys > 1 ? $" (animated, {keys} keys - the slider overrides the first)" : ""));
            return moved;
        }

        dirty |= TrackSlider("Rate", "emissionRate", 0f, 200f,
            e => e.EmissionRate, (e, v) => e.EmissionRate = v,
            "Particles born per second from this emitter.");
        dirty |= TrackSlider("Speed", "emissionSpeed", 0f, 30f,
            e => e.EmissionSpeed, (e, v) => e.EmissionSpeed = v,
            "Initial velocity (yards/second) each particle leaves the emitter with.");
        dirty |= TrackSlider("Speed var", "speedVariation", 0f, 2f,
            e => e.SpeedVariation, (e, v) => e.SpeedVariation = v,
            "Random speed spread as a fraction of Speed - 0 is uniform, higher " +
            "makes some particles crawl and others shoot out.");
        dirty |= TrackSlider("Gravity", "gravity", -20f, 20f,
            e => e.Gravity, (e, v) => e.Gravity = v,
            "Downward acceleration on each particle. Positive falls, negative rises, " +
            "0 drifts straight.");
        dirty |= TrackSlider("Lifespan", "lifespan", 0.05f, 10f,
            e => e.Lifespan, (e, v) => e.Lifespan = v,
            "Seconds each particle lives before it fades out.");
        dirty |= TrackSlider("Spread V", "verticalRange", 0f, MathF.PI,
            e => e.VerticalRange, (e, v) => e.VerticalRange = v,
            "Vertical emission cone, in radians: 0 fires flat, pi sprays over the " +
            "full vertical fan.");
        dirty |= TrackSlider("Spread H", "horizontalRange", 0f, MathF.PI,
            e => e.HorizontalRange, (e, v) => e.HorizontalRange = v,
            "Horizontal emission cone, in radians: 0 fires straight ahead, pi sprays " +
            "across the full half-circle.");
        dirty |= TrackSlider("Area L", "emissionAreaLength", 0f, 20f,
            e => e.EmissionAreaLength, (e, v) => e.EmissionAreaLength = v,
            "Length (yards) of the plane/sphere region particles are born across - " +
            "bigger areas make wider, more diffuse sources.");
        dirty |= TrackSlider("Area W", "emissionAreaWidth", 0f, 20f,
            e => e.EmissionAreaWidth, (e, v) => e.EmissionAreaWidth = v,
            "Width (yards) of the emission region, paired with Area L.");
        if (advanced)
        {
            dirty |= TrackSlider("Z source", "zSource", -20f, 20f,
                e => e.ZSource, (e, v) => e.ZSource = v,
                "Pull particle velocity toward a point on the emitter's local Z axis. " +
                "This is attraction/steering, not drag; 0 disables it.");

            float drag = edit.Drag ?? emitter.Drag;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("Drag", ref drag, 0f, 50f, "%.3f"))
            { edit.Drag = drag; model.Edits[emitter.Index] = edit; dirty = true; }
            if (CreatorResetKnob("drag") && edit.Drag is not null)
            { edit.Drag = null; model.Edits[emitter.Index] = edit; dirty = true; }
            CreatorHelp("Velocity damping per second. Higher drag rapidly contains a fast spray. " +
                $"This is a plain M2 field, separate from Z source. Authored: {emitter.Drag:0.###}.");

            float spriteSpin = edit.SpriteSpin ?? emitter.SpriteSpin;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("Billboard spin", ref spriteSpin, -20f, 20f, "%.3f"))
            { edit.SpriteSpin = spriteSpin; model.Edits[emitter.Index] = edit; dirty = true; }
            if (CreatorResetKnob("spriteSpin") && edit.SpriteSpin is not null)
            { edit.SpriteSpin = null; model.Edits[emitter.Index] = edit; dirty = true; }
            CreatorHelp("Rotates each particle image over that particle's own age. It does not " +
                "spin the entire spell or sweep the emitter through space; those effects usually " +
                $"come from animated bones. Authored: {emitter.SpriteSpin:0.###} rad/s.");

            ImGui.TextDisabled("BEHAVIOR FLAGS");
            uint flags = edit.Flags ?? emitter.Flags;
            bool FlagSwitch(string label, uint bit, string help)
            {
                bool on = (flags & bit) != 0;
                if (!ImGui.Checkbox(label, ref on))
                {
                    CreatorHelp(help);
                    return false;
                }
                flags = on ? flags | bit : flags & ~bit;
                edit.Flags = flags;
                model.Edits[emitter.Index] = edit;
                CreatorHelp(help);
                return true;
            }

            dirty |= FlagSwitch("Model-space motion", 0x10,
                "Keep particle motion in the moving/rotating model frame instead of leaving " +
                "spawned particles behind in world space.");
            dirty |= FlagSwitch("Scale with spell instance", 0x20,
                "Multiply particle size by the spell effect instance's scale.");
            dirty |= FlagSwitch("Inherit source motion", 0x40,
                "Add the moving source's velocity when particles are born.");
            dirty |= FlagSwitch("Kill outside sphere", 0x80,
                "Sphere only: discard particles after they travel outside the authored shell.");
            dirty |= FlagSwitch("Sphere points up", 0x100,
                "Sphere only: emit upward instead of along the radial shell direction.");
            dirty |= FlagSwitch("Random tumble sign", 0x200,
                "Spawned-model particles only: randomly reverse tumble axes. Billboard images " +
                "do not use model tumble.");
            dirty |= FlagSwitch("Clamp tail to life", 0x400,
                "Tail particles only: never draw a trail older than the particle itself.");
            dirty |= FlagSwitch("Local XY quad", 0x1000,
                "Draw quads in the emitter's local XY plane instead of camera-facing them.");
            dirty |= FlagSwitch("Snap to ground", 0x2000,
                "Place newly born particles on the sampled ground height when available.");
            dirty |= FlagSwitch("Follow source", 0x4000,
                "Steer existing particles as the source moves, using the emitter's authored " +
                "follow response fields.");
            dirty |= FlagSwitch("Burst once", 0x8000,
                "Emit the rate as a one-time burst when the emitter becomes active instead of " +
                "continuously accumulating particles per second.");
            if (CreatorResetKnob("flags") && edit.Flags is not null)
            { edit.Flags = null; model.Edits[emitter.Index] = edit; dirty = true; }
            CreatorHelp($"Reset restores the complete authored flag word (0x{emitter.Flags:X}). " +
                "Unlisted/unknown bits are preserved whenever one of these switches changes.");
        }

        // The three ARGB ramp colours and the ramp midpoint, straight from the inline block
        // (SPELL_CREATOR_IDE §2.6): the Hue dials rotate these, here they are set.
        ImGui.TextDisabled("COLOURS");
        bool ColourKey(string label, uint authored, Func<uint?> get, Action<uint?> set, string id)
        {
            Vector4 rgba = CreatorArgbToVector(get() ?? authored);
            ImGui.SetNextItemWidth(150f * cs);
            bool moved = ImGui.ColorEdit4(label, ref rgba,
                ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf);
            if (moved) { set(CreatorVectorToArgb(rgba)); model.Edits[emitter.Index] = edit; }
            if (CreatorResetKnob(id) && get() is not null) { set(null); model.Edits[emitter.Index] = edit; moved = true; }
            return moved;
        }
        dirty |= ColourKey("Start", emitter.ColorStart, () => edit.ColorStart, v => edit.ColorStart = v, "col0");
        ImGui.SameLine();
        dirty |= ColourKey("Mid", emitter.ColorMid, () => edit.ColorMid, v => edit.ColorMid = v, "col1");
        ImGui.SameLine();
        dirty |= ColourKey("End", emitter.ColorEnd, () => edit.ColorEnd, v => edit.ColorEnd = v, "col2");
        CreatorHelp("Each particle's colour and alpha at birth, at the midpoint and at death. " +
            "Alpha is the fade envelope (0 at death = dissolve). The Hue dials rotate these " +
            "toward a target; here you set them outright.");
        float midPoint = edit.MidPoint ?? emitter.MidPoint;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.SliderFloat("Midpoint", ref midPoint, 0f, 1f, "%.2f"))
        { edit.MidPoint = midPoint; model.Edits[emitter.Index] = edit; dirty = true; }
        if (CreatorResetKnob("midpoint") && edit.MidPoint is not null)
        { edit.MidPoint = null; model.Edits[emitter.Index] = edit; dirty = true; }
        CreatorHelp("Where in a particle's life the middle colour and size land (0 = birth, 1 = death). " +
            $"Authored: {emitter.MidPoint:0.##}.");

        if (advanced)
        {
            ImGui.TextDisabled("SPRITE, BONE, IMAGE");
            int bone = edit.Bone ?? emitter.Bone;
            ImGui.SetNextItemWidth(110f * cs);
            if (ImGui.InputInt("Bone", ref bone))
            {
                edit.Bone = (ushort)Math.Clamp(bone, 0, Math.Max(0, model.Bones.Count - 1));
                model.Edits[emitter.Index] = edit;
                dirty = true;
            }
            if (CreatorResetKnob("embone") && edit.Bone is not null)
            { edit.Bone = null; model.Edits[emitter.Index] = edit; dirty = true; }
            CreatorHelp("Re-parent this emitter to another bone of the effect: its origin and birth " +
                "frame follow that bone's pose and animation - the way to point it somewhere else. " +
                "Turn on 'Show bones' (Void stage row) to see b<n> labels in the world; pose a bone " +
                $"in the phase's BONES section. Authored: bone {emitter.Bone} of {model.Bones.Count}.");
            int? emitterSlot = edit.TextureSlot;
            if (CreatorTextureSlotCombo(model, "Image", emitter.TextureId, ref emitterSlot, "emtex"))
            { edit.TextureSlot = emitterSlot; model.Edits[emitter.Index] = edit; dirty = true; }
            int headTail = Math.Clamp((int)(edit.HeadOrTail ?? emitter.HeadOrTail), 0, 2);
            ImGui.SetNextItemWidth(150f * cs);
            if (ImGui.Combo("Head / tail", ref headTail, CreatorHeadTailModes, CreatorHeadTailModes.Length))
            { edit.HeadOrTail = (byte)headTail; model.Edits[emitter.Index] = edit; dirty = true; }
            if (CreatorResetKnob("headtail") && edit.HeadOrTail is not null)
            { edit.HeadOrTail = null; model.Edits[emitter.Index] = edit; dirty = true; }
            CreatorHelp("What each particle draws: a camera-facing head quad, a tail quad stretched " +
                $"along its velocity, or both. Authored: {emitter.HeadOrTail}.");
            float tailTime = edit.TailTime ?? emitter.TailTime;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("Tail length", ref tailTime, 0f, 2f, "%.3f s"))
            { edit.TailTime = tailTime; model.Edits[emitter.Index] = edit; dirty = true; }
            if (CreatorResetKnob("tailtime") && edit.TailTime is not null)
            { edit.TailTime = null; model.Edits[emitter.Index] = edit; dirty = true; }
            CreatorHelp("How far behind the particle its tail quad stretches, in seconds of travel " +
                $"(tail modes only). Authored: {emitter.TailTime:0.###}.");
            int rows = edit.TextureRows ?? emitter.TextureRows, cols = edit.TextureCols ?? emitter.TextureCols;
            ImGui.SetNextItemWidth(90f * cs);
            if (ImGui.InputInt("Rows", ref rows)) { edit.TextureRows = (ushort)Math.Clamp(rows, 1, 64); model.Edits[emitter.Index] = edit; dirty = true; }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90f * cs);
            if (ImGui.InputInt("Cols", ref cols)) { edit.TextureCols = (ushort)Math.Clamp(cols, 1, 64); model.Edits[emitter.Index] = edit; dirty = true; }
            if (CreatorResetKnob("emcells") && (edit.TextureRows is not null || edit.TextureCols is not null))
            { edit.TextureRows = edit.TextureCols = null; model.Edits[emitter.Index] = edit; dirty = true; }
            CreatorHelp("Sprite-sheet cells of the image (rows x columns); the flipbook ramps step " +
                $"through them. Authored: {emitter.TextureRows} x {emitter.TextureCols}.");
            float inherit = edit.InheritScale ?? emitter.InheritScale;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("Inherit scale", ref inherit, -2f, 2f, "%.2f"))
            { edit.InheritScale = inherit; model.Edits[emitter.Index] = edit; dirty = true; }
            if (CreatorResetKnob("inherit") && edit.InheritScale is not null)
            { edit.InheritScale = null; model.Edits[emitter.Index] = edit; dirty = true; }
            CreatorHelp("How much of the source's own motion a particle inherits at birth (with the " +
                $"'Inherit source motion' flag). Authored: {emitter.InheritScale:0.##}.");
            Vector3 tumbleMin = edit.AngularMin ?? emitter.AngularMin, tumbleMax = edit.AngularMax ?? emitter.AngularMax;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.DragFloat3("Tumble min", ref tumbleMin, 0.05f, -50f, 50f, "%.2f"))
            { edit.AngularMin = tumbleMin; model.Edits[emitter.Index] = edit; dirty = true; }
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.DragFloat3("Tumble max", ref tumbleMax, 0.05f, -50f, 50f, "%.2f"))
            { edit.AngularMax = tumbleMax; model.Edits[emitter.Index] = edit; dirty = true; }
            if (CreatorResetKnob("tumble") && (edit.AngularMin is not null || edit.AngularMax is not null))
            { edit.AngularMin = edit.AngularMax = null; model.Edits[emitter.Index] = edit; dirty = true; }
            CreatorHelp("Spawned-geometry particles tumble with a random angular velocity between " +
                "these (rad/s per axis). Billboard images ignore it.");
            bool flatten = edit.FlattenTracks;
            if (ImGui.Checkbox("Sliders override animated tracks (write every key)", ref flatten))
            { edit.FlattenTracks = flatten; model.Edits[emitter.Index] = edit; dirty = true; }
            CreatorHelp("A * slider normally sets only the FIRST key of an animated track and the " +
                "authored ramp continues from there. With this on, every key is written, so the " +
                "slider is a constant that replaces the authored animation (Cleave's rate ramp, " +
                "an impact's burst window).");
        }

        var scale = new Vector3(
            edit.ScaleStart ?? emitter.ScaleStart,
            edit.ScaleMid ?? emitter.ScaleMid,
            edit.ScaleEnd ?? emitter.ScaleEnd);
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.SliderFloat3("Scale s/m/e", ref scale, 0f, 8f, "%.2f"))
        {
            edit.ScaleStart = scale.X; edit.ScaleMid = scale.Y; edit.ScaleEnd = scale.Z;
            model.Edits[emitter.Index] = edit;
            dirty = true;
        }
        if (CreatorResetKnob("scale") &&
            (edit.ScaleStart is not null || edit.ScaleMid is not null || edit.ScaleEnd is not null))
        {
            edit.ScaleStart = edit.ScaleMid = edit.ScaleEnd = null;
            model.Edits[emitter.Index] = edit;
            dirty = true;
        }
        CreatorHelp("Particle size over its life: at birth (s), at the authored midpoint (m), " +
            $"and at death (e). Authored: {emitter.ScaleStart:0.##} / {emitter.ScaleMid:0.##} / " +
            $"{emitter.ScaleEnd:0.##}. Grow-then-shrink shapes read as puffs; " +
            "shrink-to-zero as dissolving sparks.");

        if (ImGui.SmallButton("Reset emitter") && model.Edits.Remove(emitter.Index)) dirty = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear every override on this emitter.");
        return dirty ? CreatorEmitterEdit.Dirty : CreatorEmitterEdit.None;
    }

    // ── texture swap picker ──────────────────────────────────────────────────
    // Splice any existing BLP into a texture slot: pick from this spell's own
    // images, browse ANY other spell's images, or type a path directly.

    private (string ModelPath, int TexIndex)? _texSwapTarget;
    private bool _texSwapAllModels = true;
    private readonly byte[] _texSwapPathBuf = new byte[160];
    private readonly byte[] _texSwapSpellBuf = new byte[64];
    private List<SpellInfo>? _texSwapSpellResults;
    private bool _texSwapSpellDirty = true;
    private string _texSwapSourceName = "";
    private List<string> _texSwapSourceTextures = [];

    private void DrawCreatorTextureSwapPicker()
    {
        if (_texSwapTarget is not { } target) return;
        if (_creatorSpell is not { } doc ||
            !doc.Models.TryGetValue(target.ModelPath, out CreatorModelDoc? model))
        {
            _texSwapTarget = null;
            return;
        }

        _activePanelTune = "Swap Texture";
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        var cond = _creatorLayoutResetFrames > 0 ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        // 540*s is dead centre of the IDE's stage, and this picker is the ONLY route to
        // a texture Swap - so it cannot just be suppressed there: park it left of the
        // inspector instead.
        float swapX = SpellFocusActive
            ? MathF.Max(12f, ImGui.GetIO().DisplaySize.X - SpellIdeRightInset - 430f * cs - 20f * s)
            : 540f * s;
        ImGui.SetNextWindowPos(new Vector2(swapX, 90f * s), cond);
        ImGui.SetNextWindowSize(new Vector2(430f * cs, 520f * cs), cond);
        ImGui.SetNextWindowSizeConstraints(new Vector2(300f * cs, 220f * cs),
            new Vector2(float.MaxValue, float.MaxValue));
        PushCreatorStyle();
        bool open = true;
        if (ImGui.Begin("###creator-swap-texture", CreatorChromeFlags))
        {
            ClampCreatorWindowOnScreen();
            if (DrawCreatorPanelChrome("Swap Texture", "Swap Texture")) open = false;
            ImGui.SetWindowFontScale(CreatorTextScale);
            BeginCreatorContent();

            var slot = model.Textures.FirstOrDefault(t => t.Index == target.TexIndex);
            string original = slot?.Filename ?? "";
            string effective = EffectiveTexturePath(model, target.TexIndex);
            ImGui.TextUnformatted($"{Path.GetFileName(model.Path)}  slot {target.TexIndex}");
            ImGui.TextDisabled($"authored: {original}");
            if (!string.Equals(effective, original, StringComparison.OrdinalIgnoreCase))
            {
                ImGui.TextDisabled($"current:  {effective}");
                if (CreatorButton("Restore original"))
                    ApplyCreatorTextureSwapEverywhere(doc, model, target.TexIndex, null);
            }

            ImGui.Checkbox("Apply to every phase using this image", ref _texSwapAllModels);
            CreatorHelp("The same BLP usually appears in several of this spell's phase models " +
                "(precast AND cast both draw CLOUDS.BLP on Cone of Cold). Checked, the swap " +
                "replaces the image EVERYWHERE in this spell, so what you see always follows. " +
                "Uncheck to swap only this one model's slot.");

            void SwapRow(string path)
            {
                ImGui.PushID(path);
                float thumb = MathF.Min(24f * cs, 40f);
                uint art = _gameplayArt?.AdditiveHandle(path) ?? 0;
                if (art != 0) { ImGui.Image((nint)art, new Vector2(thumb, thumb)); ImGui.SameLine(); }
                bool current = string.Equals(path, effective, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{Path.GetFileName(path)}##row", current))
                    ApplyCreatorTextureSwapEverywhere(doc, model, target.TexIndex,
                        string.Equals(path, original, StringComparison.OrdinalIgnoreCase) ? null : path);
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(path);
                    if (art != 0) ImGui.Image((nint)art, new Vector2(128f, 128f));
                    ImGui.EndTooltip();
                }
                ImGui.PopID();
            }

            ImGui.Spacing();
            if (CreatorCategory("swap-own", "This spell's images", defaultOpen: true))
            {
                ImGui.Indent(10f * cs);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var other in doc.Models.Values)
                    foreach (var tex in other.Textures)
                        if (tex.Filename.Length > 0 && seen.Add(tex.Filename))
                            SwapRow(tex.Filename);
                ImGui.Unindent(10f * cs);
            }

            if (CreatorCategory("swap-other", "From another spell", defaultOpen: true))
            {
                ImGui.Indent(10f * cs);
                ImGui.SetNextItemWidth(200f * cs);
                if (ImGui.InputText("##swap-spell-search", _texSwapSpellBuf, (uint)_texSwapSpellBuf.Length))
                    _texSwapSpellDirty = true;
                CreatorHelp("Search any spell by name or id, click it, and its images " +
                    "appear below - splice a Frostbolt cloud into a Fireball, or " +
                    "anything into anything.");
                string query = BufToString(_texSwapSpellBuf);
                if (_texSwapSpellDirty)
                {
                    _texSwapSpellDirty = false;
                    _texSwapSpellResults = query.Length >= 2 && _spellCatalog is not null
                        ? (uint.TryParse(query, out uint asId)
                            ? _spellCatalog.Spells.Where(sp => sp.Id == asId).ToList()
                            : _spellCatalog.Spells
                                .Where(sp => sp.VisualId != 0 &&
                                             sp.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(sp => sp.Name, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(sp => sp.Id).Take(20).ToList())
                        : null;
                }
                if (_texSwapSpellResults is { Count: > 0 } spells && query.Length >= 2)
                {
                    if (BeginCreatorResults("##swap-spells", spells.Count, 0.30f))
                        foreach (var sp in spells)
                            if (CreatorResultRow($"{sp.Id}  {sp.Name}##src{sp.Id}"))
                                LoadSwapSourceTextures(sp);
                    EndCreatorResults();
                }
                if (_texSwapSourceTextures.Count > 0)
                {
                    ImGui.TextDisabled(_texSwapSourceName);
                    foreach (string path in _texSwapSourceTextures)
                        SwapRow(path);
                }
                ImGui.Unindent(10f * cs);
            }

            if (CreatorCategory("swap-path", "Typed path"))
            {
                ImGui.Indent(10f * cs);
                ImGui.SetNextItemWidth(280f * cs);
                ImGui.InputText("##swap-path", _texSwapPathBuf, (uint)_texSwapPathBuf.Length);
                ImGui.SameLine();
                if (CreatorButton("Use", 60f * cs))
                {
                    string typed = BufToString(_texSwapPathBuf).Trim();
                    if (typed.Length > 0)
                    {
                        if (_gameplayArt?.Handle(typed) is > 0)
                            ApplyCreatorTextureSwapEverywhere(doc, model, target.TexIndex, typed);
                        else
                            Console.WriteLine($"[creator] swap path not found in MPQs: {typed}");
                    }
                }
                CreatorHelp(@"Any BLP path inside the game archives, e.g. Spells\Frost_Cloud.blp " +
                    "or Textures\\SunGlare.blp. If nothing changes, the path was not found " +
                    "(check the console).");
                ImGui.Unindent(10f * cs);
            }
            EndCreatorContent();
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();
        PopCreatorStyle();
        _activePanelTune = null;
        if (!open) _texSwapTarget = null;
    }

    /// <summary>Collect the distinct texture paths of another spell's effect models
    /// (all stages + missile), for the splice picker.</summary>
    private void LoadSwapSourceTextures(in SpellInfo info)
    {
        _texSwapSourceName = $"{info.Id}  {info.Name}";
        _texSwapSourceTextures = [];
        if (_spellVisualCatalog?.TryGetStages(info.VisualId, out SpellVisualStages stages) != true)
            return;

        var paths = new List<string>();
        void AddKit(uint kitId)
        {
            if (kitId == 0 || _spellVisualCatalog?.TryGetKit(kitId, out SpellVisualKitInfo kit) != true) return;
            foreach (var effect in kit.Effects)
                if (effect.ModelPath.Length > 0) paths.Add(effect.ModelPath);
        }
        AddKit(stages.Precast);
        AddKit(stages.Cast);
        AddKit(stages.Impact);
        AddKit(stages.State);
        AddKit(stages.Channel);
        if (_spellVisualCatalog?.MissilePath(stages) is { Length: > 0 } missile) paths.Add(missile);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string modelPath in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            byte[]? bytes = _spellEffects?.ReadOriginalModel(modelPath);
            if (bytes is null) continue;
            foreach (var tex in Creator.M2TextureParser.ParseTextures(bytes))
                if (tex.Filename.Length > 0 && seen.Add(tex.Filename))
                    _texSwapSourceTextures.Add(tex.Filename);
        }
        Console.WriteLine($"[creator] swap source '{info.Name}': {_texSwapSourceTextures.Count} image(s)");
    }

    // ── session payload helpers ──────────────────────────────────────────────
    //
    // Tinted-BLP encoding and the per-model tuning payload: both feed the SESSION
    // export in GameLoop.Creator.Session.cs. There is no longer a direct patch-MPQ
    // or tuning-JSON export; MangosSuperUI's Spell Completer builds the final patch.

    /// <summary>Decode a BLP, hue-map its pixels toward targetRgb (0x00RRGGBB), and
    /// re-encode it as DXT3 BLP for the session payload.</summary>
    private byte[]? BuildTintedBlp(string path, uint targetRgb)
    {
        try
        {
            var decoded = Formats.AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, path);
            if (decoded is not { } d) return null;
            BlpRecolor.HueMapBgra(d.bgra, targetRgb);
            using var bitmap = new SkiaSharp.SKBitmap(d.width, d.height,
                SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
            System.Runtime.InteropServices.Marshal.Copy(d.bgra, 0, bitmap.GetPixels(), d.bgra.Length);
            var writer = new BlpWriterService();
            return writer.EncodeBitmapToBlp(bitmap, useDxt1: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[creator] BuildTintedBlp({path}) failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>One model's complete tuning: whole-model dials, per-BLP hues and
    /// tints, swaps, per-emitter absolutes, off-switches, added clones, and the
    /// mesh/ribbon texture list. EVERY workshop modification is represented here -
    /// this object (plus the patched bytes) is what the session export and
    /// MangosSuperUI's Spell Completer consume.</summary>
    private object CreatorModelTuningPayload(CreatorModelDoc m) => new
    {
        dials = new
        {
            hueShift = m.HueShift,
            hueColor = m.HueShift ? $"#{PackArgb(m.HueColor) & 0xFFFFFF:x6}" : null,
            rateMultiplier = m.RateMul,
            scaleMultiplier = m.ScaleMul,
            lifespanMultiplier = m.LifeMul,
            speedMultiplier = m.SpeedMul,
            gravityAdd = m.GravityAdd,
        },
        textureHues = m.TextureHues.Where(h => h.Value.On).Select(h => new
        {
            slotIndex = h.Key,
            filename = m.Textures.FirstOrDefault(t => t.Index == h.Key)?.Filename,
            hueColor = $"#{PackArgb(h.Value.Color) & 0xFFFFFF:x6}",
            emitters = m.Textures.FirstOrDefault(t => t.Index == h.Key)?.ReferencedByEmitters,
        }),
        textureTints = m.TextureTints.Where(t => t.Value.On).Select(t => new
        {
            slotIndex = t.Key,
            filename = EffectiveTexturePath(m, t.Key),
            tintColor = $"#{PackArgb(t.Value.Color) & 0xFFFFFF:x6}",
        }),
        textureSwaps = m.TextureSwaps.Select(sw => new
        {
            slotIndex = sw.Key,
            original = m.Textures.FirstOrDefault(x => x.Index == sw.Key)?.Filename,
            replacement = sw.Value,
        }),
        emitters = m.Edits.Values,
        // Emitter off-switches and user-added clones: without these the
        // consumer resurrects silenced emitters and drops added layers.
        disabledEmitters = m.DisabledEmitters.OrderBy(i => i),
        addedEmitters = m.AddedEmitters.Select((a, i) => new
        {
            index = m.OriginalEmitterCount + i,
            sourceIndex = a.SourceIndex,
            textureSlot = a.TextureSlot,
        }),
        // Mesh layers and ribbons: the bytes carry every patch; this is the record of
        // what was changed (hidden geometry, per-layer material/colour edits, silenced
        // and edited ribbons).
        hiddenSubmeshes = m.HiddenSubmeshes.OrderBy(i => i),
        meshLayers = m.MeshEdits.Values,
        disabledRibbons = m.DisabledRibbons.OrderBy(i => i),
        ribbons = m.RibbonEdits.Values,
        bones = m.BoneEdits.Values,
        addedRibbons = m.AddedRibbons.Select((src, i) => new { index = m.OriginalRibbonCount + i, sourceIndex = src }),
        addedLayers = m.AddedLayers.Select((src, i) => new { index = m.OriginalLayerCount + i, sourceIndex = src }),
        // Ribbon/mesh color tracks are keyframed data the byte patcher
        // cannot reach - the consumer approximates the whole-model hue on
        // these by hue-mapping their BLPs (safe on a cloned spell's own
        // texture copies).
        meshOrRibbonTextures = m.Textures
            .Where(t => t.ReferencedByEmitters.Count == 0 && t.Filename.Length > 0)
            .Select(t => new { slotIndex = t.Index, filename = t.Filename }),
    };
}
