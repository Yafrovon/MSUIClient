using MSUIClient.World.Units;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Spells;
using Silk.NET.Input;

namespace MSUIClient;

public sealed record LiveRunOptions(string OutputDirectory, string? Protocol, double TimeoutSeconds, string? Character, bool Background = false, bool CharacterSelect = false, bool NativeSpawn = false, bool StartInPlace = false);

public static partial class Program
{
    private static bool TryParseLiveRunArgs(string[] args, out LiveRunOptions? options,
        out string? configPath, out string? error)
    {
        options = null; configPath = null; error = null;
        string output = "live-runs"; string? protocol = null, character = null; double timeout = 120;
        bool background = false, characterSelect = false, nativeSpawn = false, startInPlace = false;
        for (int i=0;i<args.Length;i++)
        {
            string arg=args[i];
            if (arg=="--live-bootstrap") continue;
            if (arg=="--background") { background=true; continue; }
            if (arg=="--character-select") { characterSelect=true; continue; }
            if (arg=="--native-spawn") { nativeSpawn=true; continue; }
            if (arg=="--live-in-place") { startInPlace = true; continue; }
            if (arg is "--live-protocol" or "--out" or "--timeout" or "--character")
            {
                if (++i>=args.Length) { error=$"missing value for {arg}"; return false; }
                if (arg=="--live-protocol") protocol=args[i];
                else if (arg=="--out") output=args[i];
                else if (arg=="--character") character=args[i];
                else if (!double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out timeout) || timeout<=0)
                { error="--timeout must be positive"; return false; }
                continue;
            }
            if (arg.StartsWith('-')) { error=$"unknown option {arg}"; return false; }
            if (configPath is not null) { error=$"unexpected argument {arg}"; return false; }
            configPath=arg;
        }
        options=new(output,protocol,timeout,character,background,characterSelect,nativeSpawn,startInPlace); return true;
    }
}

public sealed partial class GameLoop
{
    private readonly LiveRunOptions? _liveRunOptions;
    private double _liveRunElapsed;
    private bool _liveTeleportSent;
    private double _liveTeleportSentAt;
    private readonly HashSet<string> _liveHeld = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Key> _liveInputHeld = [];
    private string _liveForcedCursorStem = "";
    private int _liveForcedCursorFrames;
    private long _liveSoundMarkSequence;
    private int _liveInspectWireMarkCount;
    private int _liveQuestWireMarkCount;
    private long _liveSoundProtocolStartSequence;
    private List<string>? _liveSteps;
    private int _liveStep;
    private double _liveWaitUntil;
    private string? _liveWaitPattern;
    private double _liveWaitGroundedUntil;
    // char-create: a real CMSG_CHAR_CREATE from the character select screen (--character-select runs).
    private double _liveCreateDeadline;
    private string _liveCreateLine = "";
    private byte? _lastCreateResult;
    // walk-to: a real walk (W held, facing re-aimed every frame) - seamless zone entry, paths, bridges.
    private Vector2? _liveWalkTarget;
    private float _liveWalkRadius, _liveWalkBest, _liveWalkStartZ;
    private double _liveWalkDeadline, _liveWalkProgressAt;
    private double _liveWaitTimeout;
    private string? _liveSpellWaitResult;
    private double _liveSpellWaitTimeout;
    private int _liveSpellWaitAfter;
    private string? _liveInterfaceWaitFamily;
    private string? _liveInterfaceWaitStep;
    private double _liveInterfaceWaitTimeout;
    private int _liveInterfaceWaitAfter;
    private uint _liveLastCastSpell;
    private readonly List<string> _liveLog = [];
    private string _liveStamp = "";
    private readonly List<ulong> _liveSpawnGuids = [];
    private HashSet<ulong>? _liveSpawnBefore;
    private double _liveSelectWaitStarted;
    private ulong _liveAnchorGuid;
    public int LiveRunExitCode { get; private set; } = 1;

    private void AdvanceLiveRun(float dt)
    {
        if (_liveRunOptions is null) return;
        _liveRunElapsed += dt;
        if (_liveRunElapsed > 10 && _net?.State is NetState.Failed or NetState.Disconnected)
        {
            FinishLiveBootstrap("NETWORK_FAILED", $"client network state={_net.State}");
            return;
        }
        if (_liveRunElapsed > 20 && _net?.State == NetState.CharacterSelect &&
            !string.IsNullOrWhiteSpace(_liveRunOptions.Character) &&
            !_net.Characters.Any(c => c.Name.Equals(_liveRunOptions.Character, StringComparison.OrdinalIgnoreCase)))
        {
            FinishLiveBootstrap("CHARACTER_NOT_FOUND",
                $"requested={_liveRunOptions.Character};roster={string.Join('|', _net.Characters.Select(c => c.Name))}");
            return;
        }
        if (_liveSteps is null && _liveRunElapsed > 180)
        {
            FinishLiveBootstrap("TIMEOUT",
                $"world did not become ready within 180 seconds;state={_net?.State};roster={string.Join('|', _net?.Characters.Select(c => c.Name) ?? [])}");
            return;
        }
        if (_liveSteps is not null && _liveRunElapsed > _liveRunOptions.TimeoutSeconds)
        { FinishLiveBootstrap("TIMEOUT", "protocol exceeded its separate run timeout"); return; }
        if (_liveRunOptions.CharacterSelect && _net?.State == NetState.CharacterSelect)
        {
            if (_liveRunOptions.Protocol is null) FinishLiveBootstrap("READY", "character select ready");
            else AdvanceProtocol();
            return;
        }
        // _worldLoadedOnce: between InWorld and the first BeginWorldLoad the loading flag is
        // still false, and the arena teleport used to fire into that gap.
        if (_net is not { IsInWorld:true } || _worldLoading || !_worldLoadedOnce || _controller is null || _character is null) return;
        // Raid/checkpoint runs (--live-in-place) and native spawns (--native-spawn) start where the
        // character actually is; the generic movement fixture's arena teleport is skipped.
        if (_liveRunOptions.NativeSpawn || _liveRunOptions.StartInPlace)
        {
            if (_liveRunOptions.Protocol is null) FinishLiveBootstrap("READY", _liveRunOptions.NativeSpawn
                ? "native world spawn ready; no fixture teleport" : "world ready at saved location");
            else AdvanceProtocol();
            return;
        }
        if (_liveTeleportSent)
        {
            // Once the protocol has started it owns position. Do not keep testing
            // the bootstrap arena after a later scenario step deliberately moves
            // the character away from it.
            if (_liveSteps is not null) { AdvanceProtocol(); return; }
            var readyArena=VantageStore.Load(_config.RepoRoot).Find("movement-arena");
            if (readyArena is null) { FinishLiveBootstrap("NO_VANTAGE","movement-arena missing"); return; }
            float dx=_controller.Position.X-readyArena.X,dy=_controller.Position.Y-readyArena.Y,dz=_controller.Position.Z-readyArena.Z;
            bool atArena=dx*dx+dy*dy+dz*dz<=9f;
            if (!atArena && NowSeconds()-_liveTeleportSentAt<2.0) return;
            if (!atArena && !_liveLog.Contains("TELEPORT_UNCONFIRMED"))
            {
                _liveLog.Add("TELEPORT_UNCONFIRMED");
                EmitCombat("BootstrapTeleportUnconfirmed","gm-command-no-position-change",0,
                    $"position={_controller.Position.X:R}|{_controller.Position.Y:R}|{_controller.Position.Z:R}");
            }
            if (_liveRunOptions.Protocol is null) { FinishLiveBootstrap("READY", "world+wire+verdict ready"); return; }
            AdvanceProtocol(); return;
        }
        var arena=VantageStore.Load(_config.RepoRoot).Find("movement-arena");
        if (arena is null) { FinishLiveBootstrap("NO_VANTAGE", "movement-arena missing"); return; }
        string command=string.Create(CultureInfo.InvariantCulture,
            $".go xyz {arena.X:R} {arena.Y:R} {arena.Z:R} {arena.Map}");
        _liveTeleportSent=SendGmCommand(command,"live-bootstrap");
        if(_liveTeleportSent) _liveTeleportSentAt=NowSeconds();
        if (!_liveTeleportSent) FinishLiveBootstrap("GM_SEND_FAILED", command);
    }

    private uint _liveTrackEntry;
    private double _liveTrackUntil, _liveTrackNext;

    private void AdvanceProtocol()
    {
        if (_liveSteps is null)
        {
            string path=Path.GetFullPath(Path.IsPathRooted(_liveRunOptions!.Protocol!) ? _liveRunOptions.Protocol! :
                Path.Combine(_config.RepoRoot,_liveRunOptions.Protocol!));
            _liveSteps=File.ReadLines(path).Select(x=>x.Split('#')[0].Trim()).Where(x=>x.Length>0).ToList();
            _liveStamp=DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture);
            _liveSoundProtocolStartSequence = _spellSounds?.JournalSnapshot().LastOrDefault()?.Sequence ?? 0;
            _multiActionProtocolFixtureStaged = false;
            _liveLog.Add($"START protocol={path}");
        }
        double now=NowSeconds();
        // `track <entry> <seconds>`: print every visible creature of that entry twice a second
        // (World Builder pathing proof - shared_docs/WORLD_BUILDER.md). Runs through waits.
        if (_liveTrackEntry != 0 && now >= _liveTrackNext)
        {
            _liveTrackNext = now + 0.5;
            foreach (var e in _entities.Units.Where(x => x.IsCreature && x.Entry == _liveTrackEntry))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[track] {now:F1} {e.Entry} {e.Guid & 0xFFFFFF} {e.Position.X:F2} {e.Position.Y:F2} {e.Position.Z:F2}"));
            if (now >= _liveTrackUntil) _liveTrackEntry = 0;
        }
        if (_liveWaitUntil>now) return;
        if (_liveSpellWaitResult is not null)
        {
            bool foundSpell = _verdicts.Snapshot("spell-sweep").OfType<SpellSweepVerdict>()
                .Skip(_liveSpellWaitAfter).Any(v => v.SpellId == _liveLastCastSpell &&
                    v.Result.Equals(_liveSpellWaitResult, StringComparison.OrdinalIgnoreCase));
            if (foundSpell)
            { Log(true,$"waitspell {_liveSpellWaitResult}"); _liveSpellWaitResult=null; _liveStep++; }
            else if (now>=_liveSpellWaitTimeout)
            { Log(false,$"waitspell {_liveSpellWaitResult} timeout"); _liveSpellWaitResult=null; _liveStep++; }
            else return;
        }
        if (_liveInterfaceWaitFamily is not null)
        {
            bool found = _verdicts.Snapshot("interface").OfType<InterfaceVerdict>()
                .Skip(_liveInterfaceWaitAfter).Any(v =>
                    v.Family.Equals(_liveInterfaceWaitFamily, StringComparison.OrdinalIgnoreCase) &&
                    v.Step.Equals(_liveInterfaceWaitStep, StringComparison.OrdinalIgnoreCase) &&
                    v.Outcome is not "SENT" and not "SEND_FAILED");
            if (found)
            {
                Log(true, $"probe-interface {_liveInterfaceWaitFamily} {_liveInterfaceWaitStep} response");
                _liveInterfaceWaitFamily = null; _liveInterfaceWaitStep = null; _liveStep++;
            }
            else if (now >= _liveInterfaceWaitTimeout)
            {
                EmitInterface(_liveInterfaceWaitFamily, _liveInterfaceWaitStep!,
                    "BLOCKED-BY:F-SILENT-INTERACT", _selectionGuid, "boundedWaitExpired=true");
                Log(true, $"probe-interface {_liveInterfaceWaitFamily} {_liveInterfaceWaitStep} blocked");
                _liveInterfaceWaitFamily = null; _liveInterfaceWaitStep = null; _liveStep++;
            }
            else return;
        }
        if (_liveCreateDeadline > 0)
        {
            if (_lastCreateResult is { } created)
            {
                _lastCreateResult = null;
                Log(created == 0x2E, FormattableString.Invariant($"{_liveCreateLine} result=0x{created:X2};roster={string.Join('|', _net.Characters.Select(c => c.Name))}"));
                _liveCreateDeadline = 0; _liveStep++;
            }
            else if (now >= _liveCreateDeadline) { Log(false, _liveCreateLine + " timeout"); _liveCreateDeadline = 0; _liveStep++; }
            else return;
        }
        if (_liveWalkTarget is { } walkTo && _controller is not null)
        {
            var here = new Vector2(_controller.Position.X, _controller.Position.Y);
            float left = Vector2.Distance(here, walkTo);
            if (left < _liveWalkBest - 0.5f) { _liveWalkBest = left; _liveWalkProgressAt = now; }
            string verdict = left <= _liveWalkRadius ? "arrived"
                : _controller.Position.Z < _liveWalkStartZ - 60f ? "fell"
                : now - _liveWalkProgressAt > 5 ? "stuck"
                : now >= _liveWalkDeadline ? "timeout" : "";
            if (verdict.Length > 0)
            {
                _liveHeld.Remove("W");
                _liveWalkTarget = null;
                Log(verdict == "arrived", FormattableString.Invariant(
                    $"walk-to {walkTo.X:F1} {walkTo.Y:F1} {verdict} at ({_controller.Position.X:F1}, {_controller.Position.Y:F1}, {_controller.Position.Z:F1}) left={left:F1} ground={_controller.GroundSource}"));
                _liveStep++;
            }
            else
            {
                // Re-aim every frame: slopes, walls and the capsule push the body off the straight line.
                float yaw = MathF.Atan2(walkTo.Y - here.Y, walkTo.X - here.X);
                _controller.Yaw = yaw; _window.Camera.Yaw = yaw; _window.Camera.OrbitYaw = 0;
                _liveHeld.Add("W");
                return;
            }
        }
        if (_liveWaitGroundedUntil > 0)
        {
            // wait-grounded: a body teleported onto a wandering mob's live (server) position can land on
            // a slope or a lip and slide a moment before it stands; combat fixtures need it standing.
            if (_controller?.Grounded == true)
            { Log(true, FormattableString.Invariant($"wait-grounded z={_controller.Position.Z:F1}")); _liveWaitGroundedUntil = 0; _liveStep++; }
            else if (now >= _liveWaitGroundedUntil)
            { Log(false, FormattableString.Invariant($"wait-grounded timeout ground={_controller?.GroundSource};z={_controller?.Position.Z:F1}")); _liveWaitGroundedUntil = 0; _liveStep++; }
            else return;
        }
        if (_liveWaitPattern is not null)
        {
            if ((_liveWaitNewOnly ? VerdictLinesSinceMark() : VerdictLines()).Any(x=>x.Contains(_liveWaitPattern,StringComparison.OrdinalIgnoreCase)))
            { Log(true,$"waitfor {_liveWaitPattern}"); _liveWaitPattern=null; _liveWaitNewOnly=false; _liveStep++; }
            else if (now>=_liveWaitTimeout)
            { Log(false,$"waitfor {_liveWaitPattern} timeout"); _liveWaitPattern=null; _liveWaitNewOnly=false; _liveStep++; }
            else return;
        }
        if (_liveStep>=_liveSteps!.Count)
        {
            if (!PollLiveInbox()) { FinishProtocol(); return; }
            if (_liveStep>=_liveSteps.Count) return;
        }
        string line=_liveSteps[_liveStep];
        try
        {
            string[] p=line.Split(' ',3,StringSplitOptions.RemoveEmptyEntries);
            switch(p[0].ToLowerInvariant())
            {
                case "raidqa":
                    if (!AdvanceCommanderRaidLiveQa(line)) return;
                    break;
                case "inbox": Log(OpenLiveInbox(line), line); break;
                case "worldmap-probe": RunLiveWorldMapProbe(line); break;
                case "worldmap-continent": OpenLiveWorldMapContinent(line); break;
                case "worldmap-hover": PositionLiveWorldMapHover(line); break;
                case "gm":
                    if(line[3..].StartsWith(".npc spawn add",StringComparison.OrdinalIgnoreCase))
                        _liveSpawnBefore=_entities.Units.Where(x=>x.IsCreature).Select(x=>x.Guid).ToHashSet();
                    bool gmSent = SendGmCommand(line[3..],"protocol-runner");
                    if ((_raidQaAttemptDirectory is not null || _raidQaPersistent) && _raidQaStage == MSUIClient.Engine.UI.CommanderRaidAttemptStage.Failed) return;
                    Log(gmSent,line); break;
                case "gm-to-selection":
                    if (_entities.TryGet(_selectionGuid, out WorldEntity selectedForTeleport))
                    {
                        string go = string.Create(CultureInfo.InvariantCulture,
                            $".go xyz {selectedForTeleport.Position.X:R} {selectedForTeleport.Position.Y:R} {selectedForTeleport.Position.Z:R} {_config.Start.Map}");
                        bool placementSent = SendGmCommand(go, "protocol-runner-selection-placement");
                        if ((_raidQaAttemptDirectory is not null || _raidQaPersistent) && _raidQaStage == MSUIClient.Engine.UI.CommanderRaidAttemptStage.Failed) return;
                        Log(placementSent, $"{line} command={go}");
                    }
                    else Log(false, $"{line} selected descriptor missing");
                    break;
                case "bags":
                    string[] bags = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (bags.Length == 2 && bags[1].Equals("reset", StringComparison.OrdinalIgnoreCase) &&
                        _net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity resetPlayer))
                    {
                        SetBagWindowOpen(0, false, playSound: false);
                        for (int container = 1; container <= 4; container++)
                            SetBagWindowOpen(container, false, playSound: false);
                        SetBagWindowOpen(InventoryUiLaw.KeyringContainer, false, playSound: false);
                        bool pass = !_backpackOpen && !_equippedBagOpen.Any(x => x) && !_keyringOpen;
                        EmitInterface("inventory", "bag-input-setup", pass ? "PASS" : "FAIL",
                            resetPlayer.Guid, "normalBags=closed;keyring=closed;sounds=suppressed");
                        Log(pass, line);
                    }
                    else if (bags.Length == 2 && bags[1].Equals("assert-backpack", StringComparison.OrdinalIgnoreCase))
                    {
                        bool pass = _backpackOpen && !_equippedBagOpen.Any(x => x);
                        EmitInterface("inventory", "backpack-binding", pass ? "PASS" : "FAIL",
                            _net?.PlayerGuid ?? 0,
                            $"productionInput=true;backpack={_backpackOpen};equippedOpen={_equippedBagOpen.Count(x => x)}");
                        Log(pass, line);
                    }
                    else if (bags.Length == 2 && bags[1].Equals("assert-all", StringComparison.OrdinalIgnoreCase) &&
                             _net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity allPlayer))
                    {
                        bool[] exists = Enumerable.Range(1, 4)
                            .Select(container => allPlayer.Fields.PlayerInventorySlot(18 + container) != 0).ToArray();
                        bool statesMatch = Enumerable.Range(0, 4).All(i => _equippedBagOpen[i] == exists[i]);
                        bool pass = _backpackOpen && exists.Any(x => x) && statesMatch;
                        EmitInterface("inventory", "all-bags-binding", pass ? "PASS" : "FAIL",
                            allPlayer.Guid,
                            $"productionInput=true;backpack={_backpackOpen};existing={string.Join('|', exists.Select(x => x ? 1 : 0))};open={string.Join('|', _equippedBagOpen.Select(x => x ? 1 : 0))}");
                        Log(pass, line);
                    }
                    else if (bags.Length == 2 && bags[1].Equals("assert-closed", StringComparison.OrdinalIgnoreCase))
                    {
                        bool pass = !_backpackOpen && !_equippedBagOpen.Any(x => x);
                        EmitInterface("inventory", "bag-binding-closed", pass ? "PASS" : "FAIL",
                            _net?.PlayerGuid ?? 0,
                            $"productionInput=true;backpack={_backpackOpen};equippedOpen={_equippedBagOpen.Count(x => x)}");
                        Log(pass, line);
                    }
                    else if (bags.Length == 2 && bags[1].Equals("backpack", StringComparison.OrdinalIgnoreCase))
                    {
                        CloseAllBagWindows();
                        long before = _spellSounds?.Plays ?? 0;
                        ToggleBackpack();
                        long soundDelta = (_spellSounds?.Plays ?? 0) - before;
                        bool pass = _backpackOpen && !_equippedBagOpen.Any(x => x) && soundDelta == 1;
                        EmitInterface("inventory", "backpack-binding", pass ? "PASS" : "FAIL",
                            _net?.PlayerGuid ?? 0,
                            $"backpack={_backpackOpen};equippedOpen={_equippedBagOpen.Count(x => x)};soundDelta={soundDelta}");
                        Log(pass, line);
                    }
                    else if (bags.Length == 2 && bags[1].Equals("open", StringComparison.OrdinalIgnoreCase))
                    {
                        CloseAllBagWindows();
                        long before = _spellSounds?.Plays ?? 0;
                        bool changed = ToggleAllBags();
                        long soundDelta = (_spellSounds?.Plays ?? 0) - before;
                        bool pass = changed && _backpackOpen && _equippedBagOpen.Any(x => x) && soundDelta == 1;
                        EmitInterface("inventory", "all-bags-binding", pass ? "PASS" : "FAIL",
                            _net?.PlayerGuid ?? 0,
                            $"backpack={_backpackOpen};equippedOpen={_equippedBagOpen.Count(x => x)};soundDelta={soundDelta}");
                        Log(pass, line);
                    }
                    else if (bags.Length == 2 && bags[1].Equals("keyring", StringComparison.OrdinalIgnoreCase) &&
                             _net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity bagPlayer) &&
                             HasKey(bagPlayer))
                    { SetBagWindowOpen(InventoryUiLaw.KeyringContainer, true); Log(_keyringOpen, line); }
                    else if (bags.Length == 2 && bags[1].Equals("close", StringComparison.OrdinalIgnoreCase))
                        Log(CloseAllBagWindows(), line);
                    else Log(false, $"unknown {line}");
                    break;
                case "sound":
                    string[] sound = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (sound.Length == 2 && sound[1].Equals("mark", StringComparison.OrdinalIgnoreCase))
                    {
                        _liveSoundMarkSequence = _spellSounds?.JournalSnapshot().LastOrDefault()?.Sequence ?? 0;
                        Log(true, $"{line} sequence={_liveSoundMarkSequence}");
                    }
                    else if (sound.Length is 4 or 5 &&
                             (sound[1].Equals("assert", StringComparison.OrdinalIgnoreCase) ||
                              sound[1].Equals("assert-category", StringComparison.OrdinalIgnoreCase)) &&
                             int.TryParse(sound[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int expectedSounds))
                    {
                        IReadOnlyList<World.Sound.AudioMixer.SoundPlayJournalEntry> events =
                            _spellSounds?.JournalSnapshot().Where(x => x.Sequence > _liveSoundMarkSequence).ToArray() ?? [];
                        string expectedCue = sound[3];
                        string expectedCategory = sound.Length == 5 ? sound[4] : "ui.inventory";
                        if (sound[1].Equals("assert-category", StringComparison.OrdinalIgnoreCase))
                            events = events.Where(x => x.Category.Equals(expectedCategory, StringComparison.Ordinal)).ToArray();
                        bool pass = events.Count == expectedSounds && events.All(x =>
                            x.Category.Equals(expectedCategory, StringComparison.Ordinal) &&
                            x.RequestedCue.Equals(expectedCue, StringComparison.OrdinalIgnoreCase));
                        string actual = events.Count == 0 ? "none" : string.Join('|', events.Select(x =>
                            $"{x.Sequence}:{x.Category}:{x.RequestedCue}:{x.SoundId}:{x.ResolvedPath}:owner=0x{x.Owner:X16}"));
                        EmitInterface("ui-sound", "sound-contract", pass ? "PASS" : "FAIL",
                            _net?.PlayerGuid ?? 0,
                            $"after={_liveSoundMarkSequence};expected={expectedSounds}:{expectedCue}:{expectedCategory};actual={actual}");
                        Log(pass, $"{line} actual={actual}");
                    }
                    else Log(false, $"unknown {line}");
                    break;
                case "wait": _liveWaitUntil=now+double.Parse(p[1],CultureInfo.InvariantCulture); Log(true,line); break;
                case "track":
                {
                    string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    _liveTrackEntry = uint.Parse(t[1], CultureInfo.InvariantCulture);
                    _liveTrackUntil = now + double.Parse(t[2], CultureInfo.InvariantCulture);
                    _liveTrackNext = now;
                    Log(true, line);
                    break;
                }
                case "waitfor":
                    string[] w=line[8..].Split(' '); double timeout=double.Parse(w[^1],CultureInfo.InvariantCulture);
                    _liveWaitPattern=string.Join(' ',w[..^1]); _liveWaitTimeout=now+timeout; return;
                case "assert": Log(VerdictLines().Any(x=>x.Contains(line[7..],StringComparison.OrdinalIgnoreCase)),line); break;
                // mark / waitfor-new / assert-new: only verdicts emitted AFTER the mark count, so a
                // per-NPC check cannot pass on an earlier NPC's vendor list (World Pack tier 3).
                case "mark": _liveMarkTime = NowSeconds(); Log(true, line); break;
                // assert-grounded <minZ>: held by the world - above minZ and not in free fall (standing, or
                // sliding down a slope; a cellar or crypt floor under a terrain hole is fine) - not in the
                // void below it. The World Pack hole walk (tools/worldpack/gen-live.py).
                case "assert-grounded":
                {
                    float minZ = float.Parse(p[1], CultureInfo.InvariantCulture);
                    bool grounded = _controller?.Grounded == true;
                    float vz = _controller?.Velocity.Z ?? 0f;
                    float z = _entities.TryGet(ControlledGuid, out WorldEntity grounder) ? grounder.Position.Z : float.NaN;
                    Log(z > minZ && (grounded || vz > -5f), FormattableString.Invariant($"{line} grounded={grounded};vz={vz:F1};z={z:F1};ground={_controller?.GroundSource}"));
                    break;
                }
                // assert-z-above <z>: the acting body has NOT fallen through the world (World Pack hole walk).
                case "assert-z-above":
                {
                    float floorZ = float.Parse(p[1], CultureInfo.InvariantCulture);
                    bool up = _entities.TryGet(ControlledGuid, out WorldEntity body) && body.Position.Z > floorZ;
                    Log(up, FormattableString.Invariant($"{line} z={(up || _entities.TryGet(ControlledGuid, out body) ? body.Position.Z : float.NaN):F1}"));
                    break;
                }
                case "wait-grounded": _liveWaitGroundedUntil = now + double.Parse(p[1], CultureInfo.InvariantCulture); return;
                // char-create <name> <race> <class> <gender>: create a character on this account from the character
                // select screen (run with --character-select) through the same CMSG_CHAR_CREATE the glue screen sends.
                case "char-create":
                {
                    // (p holds at most three fields: split the whole line for the four arguments)
                    string[] cc = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (_net is null || _net.State != NetState.CharacterSelect) { Log(false, line + " not at character select"); break; }
                    _net.CreateCharacter(new CharCreateParams(cc[1], byte.Parse(cc[2], CultureInfo.InvariantCulture),
                        byte.Parse(cc[3], CultureInfo.InvariantCulture), byte.Parse(cc[4], CultureInfo.InvariantCulture), 0, 0, 0, 0, 0));
                    _lastCreateResult = null; _liveCreateLine = line; _liveCreateDeadline = now + 20;
                    return;
                }
                // walk-to <x> <y> <timeoutSeconds> [radius]: walk there for real; fails when stuck 5 s, falling or out of time.
                case "walk-to":
                {
                    string[] wt = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);   // p holds at most three fields
                    if (_controller is null) { Log(false, line + " no controller"); break; }
                    _liveWalkTarget = new Vector2(float.Parse(wt[1], CultureInfo.InvariantCulture), float.Parse(wt[2], CultureInfo.InvariantCulture));
                    _liveWalkDeadline = now + double.Parse(wt[3], CultureInfo.InvariantCulture);
                    _liveWalkRadius = wt.Length > 4 ? float.Parse(wt[4], CultureInfo.InvariantCulture) : 3f;
                    _liveWalkBest = float.MaxValue; _liveWalkProgressAt = now; _liveWalkStartZ = _controller.Position.Z;
                    return;
                }
                case "waitfor-new":
                    string[] wn=line[12..].Split(' '); double wnTimeout=double.Parse(wn[^1],CultureInfo.InvariantCulture);
                    _liveWaitPattern=string.Join(' ',wn[..^1]); _liveWaitTimeout=now+wnTimeout; _liveWaitNewOnly=true; return;
                case "assert-new": Log(VerdictLinesSinceMark().Any(x=>x.Contains(line[11..],StringComparison.OrdinalIgnoreCase)),line); break;
                // assert-skills-capped: every Weapon Skills line (SkillLine category 6, Defense included) the
                // character knows is at its cap and the cap is level x 5. A character GM-levelled from 1 keeps
                // weapon skill 5 and defense 1 - it misses nearly every swing (Gilnwar's first fair run, 2026-09-27:
                // the wolf never left 100%). From the player's own PLAYER_SKILL_INFO fields.
                case "assert-skills-capped":
                {
                    if (_mpq is null || _net is not { PlayerGuid: not 0 } snet || !_entities.TryGet(snet.PlayerGuid, out WorldEntity me))
                    { Log(false, line + " no player"); break; }
                    _skillLines ??= SkillLineCatalog.Load(_mpq);
                    uint cap = me.Fields.Level * 5;
                    var rows = new List<string>();
                    bool capped = _skillLines is not null;
                    foreach ((byte slot, ushort skillId, ushort rank) in me.Fields.PlayerSkills())
                    {
                        if (_skillLines is null || !_skillLines.TryGet(skillId, out SkillLineInfo info) || info.CategoryId != 6) continue;
                        uint max = (me.Fields.GetU32((ushort)(ObjectFields.PLAYER_SKILL_INFO_1_1 + slot * 3 + 1)) ?? 0) >> 16;
                        if (max <= 1) continue;                  // Dual Wield: a proficiency, no rank
                        bool ok = rank == max && max == cap;
                        capped &= ok;
                        rows.Add($"{info.Name.Replace(' ', '_')}={rank}/{max}{(ok ? "" : "!")}");
                    }
                    capped &= rows.Count > 0;
                    string detail = $"level={me.Fields.Level};cap={cap};lines={string.Join(',', rows)}";
                    EmitInterface("skill", "capped", capped ? "PASS" : "FAIL", snet.PlayerGuid, detail);
                    Log(capped, line + " " + detail);
                    break;
                }
                case "select":
                    if (p.Length == 2 && p[1].StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
                    {
                        bool selected = ulong.TryParse(p[1][5..], NumberStyles.None, CultureInfo.InvariantCulture, out ulong observedGuid) && SelectLiveObservedGuid(observedGuid);
                        Log(selected, line); break;
                    }
                    RefreshLiveSpawnIdentities();
                    bool self=p[1].Equals("self",StringComparison.OrdinalIgnoreCase);
                    bool anchor=p[1].Equals("anchor",StringComparison.OrdinalIgnoreCase);
                    bool npcFlagNearest=p[1].StartsWith("npc-flag-nearest:",StringComparison.OrdinalIgnoreCase);
                    bool entryNearest=p[1].StartsWith("entry-nearest:",StringComparison.OrdinalIgnoreCase);
                    // corpse-nearest:<entry> - the nearest DEAD creature of that entry (entry-nearest skips the
                    // dead, so "kill, then loot it" could never select its own corpse).
                    bool corpseNearest=p[1].StartsWith("corpse-nearest:",StringComparison.OrdinalIgnoreCase);
                    bool objectEntryNearest=p[1].StartsWith("object-entry-nearest:",StringComparison.OrdinalIgnoreCase);
                    bool objectTypeNearest=p[1].StartsWith("object-type-nearest:",StringComparison.OrdinalIgnoreCase);
                    bool mailboxNearest=p[1].Equals("mailbox-nearest",StringComparison.OrdinalIgnoreCase);
                    int ordinal=self||anchor||npcFlagNearest||mailboxNearest?0:int.Parse(p[1].Split(':')[^1],CultureInfo.InvariantCulture);
                    bool wildEntryNearest=p[1].StartsWith("wild-entry-nearest:",StringComparison.OrdinalIgnoreCase);
                    bool wildEntry=p[1].StartsWith("wild-entry:",StringComparison.OrdinalIgnoreCase);
                    bool wildHostile=p[1].StartsWith("wild-hostile:",StringComparison.OrdinalIgnoreCase);
                    bool wild=p[1].StartsWith("wild:",StringComparison.OrdinalIgnoreCase);
                    bool spawned=p[1].StartsWith("spawn:",StringComparison.OrdinalIgnoreCase);
                    ulong guid=self?_net?.PlayerGuid??0:npcFlagNearest?LiveNpcFlagNearestGuid(p[1].Split(':')[^1]):
                        mailboxNearest?LiveMailboxNearestGuid():
                        objectTypeNearest?LiveObjectTypeNearestGuid(ordinal):
                        objectEntryNearest?LiveObjectEntryNearestGuid(ordinal):
                        entryNearest?LiveEntryNearestGuid(ordinal):
                        corpseNearest?LiveEntryNearestGuid(ordinal, dead: true):
                        anchor&&_entities.TryGet(_liveAnchorGuid,out _)?_liveAnchorGuid:
                        wildEntryNearest?LiveWildEntryNearestGuid(ordinal):wildEntry?LiveWildEntryGuid(ordinal):wildHostile?LiveWildHostileGuid(ordinal):
                        wild?LiveWildGuid(ordinal):LiveSpawnGuid(ordinal);
                    bool unavailable=guid==0||(spawned&&!_entities.TryGet(guid,out _));
                    if(unavailable&&now-(_liveSelectWaitStarted==0?now:_liveSelectWaitStarted)<5)
                    {
                        if(_liveSelectWaitStarted==0) _liveSelectWaitStarted=now;
                        _liveWaitUntil=now+0.05;
                        return;
                    }
                    _liveSelectWaitStarted=0;
                    if(guid!=0) CommitSelection(guid,false); Log(guid!=0,$"{line} guid=0x{guid:X16}"); break;
                case "invite":
                    // Send CMSG_GROUP_INVITE through the popup's exact name path and log the
                    // raw name bytes — surfaces invisible junk in the cached name.
                    // "invite player-nearest" uses the name cache; "invite name:X" a literal.
                    ulong invGuid=0; string invName;
                    if(p[1].StartsWith("name:",StringComparison.OrdinalIgnoreCase))
                        invName=line[(line.IndexOf("name:",StringComparison.OrdinalIgnoreCase)+5)..];
                    else { invGuid=LiveNearestRemotePlayerGuid(); invName=_playerNames.GetValueOrDefault(invGuid,""); }
                    if(invGuid==0) invGuid=KnownPlayerGuid(invName);
                    bool inviteSent=invName.Length>0&&
                        !RefuseTacticalFreezeLiveCommand("inviting a party member")&&
                        !RefuseTacticalFrozenActor(invGuid,"invite them to a party")&&
                        (_net?.GroupInvite(invName)??false);
                    Log(inviteSent,$"{line} guid=0x{invGuid:X16} name='{invName}' "+
                        $"utf8={BitConverter.ToString(System.Text.Encoding.UTF8.GetBytes(invName))}");
                    break;
                case "sui-possess":
                    // CRPG possession through the client's own request path, nearest remote
                    // player; selection is committed so dumps report player↔selection distance.
                    ulong suiGuid=LiveNearestRemotePlayerGuid();
                    if(suiGuid!=0){ CommitSelection(suiGuid,false); RequestPossess(suiGuid); }
                    Log(suiGuid!=0,$"{line} guid=0x{suiGuid:X16} state={_controlState}");
                    break;
                case "sui-release":
                    RequestControlRelease(toFreecam:false);
                    Log(true,$"{line} state={_controlState}");
                    break;
                case "unit-popup":
                    // Open the right-click unit popup on the current selection, or on the
                    // nearest remote player ("unit-popup player-nearest"), for dump captures.
                    ulong popupGuid=p.Length>1&&p[1].Equals("player-nearest",StringComparison.OrdinalIgnoreCase)
                        ?LiveNearestRemotePlayerGuid():_selectionGuid;
                    if(popupGuid==0&&now-(_liveSelectWaitStarted==0?now:_liveSelectWaitStarted)<5)
                    {
                        if(_liveSelectWaitStarted==0) _liveSelectWaitStarted=now;
                        _liveWaitUntil=now+0.05;
                        return;
                    }
                    _liveSelectWaitStarted=0;
                    bool popupOpened=false;
                    if(popupGuid!=0&&_entities.TryGet(popupGuid,out WorldEntity popupUnit))
                    {
                        CommitSelection(popupGuid,false);
                        if(_controller is not null)
                        {
                            float towards=MathF.Atan2(popupUnit.Position.Y-_controller.Position.Y,
                                popupUnit.Position.X-_controller.Position.X);
                            _controller.Yaw=towards; _window.Camera.Yaw=towards; _window.Camera.OrbitYaw=0;
                        }
                        if(UnitFrameMenuWhich(popupUnit) is { } popupWhich)
                        {
                            OpenUnitPopup(popupGuid,popupWhich,
                                new Vector2(370,94)*GameplayUiScale(),InspectBinding.Target);
                            popupOpened=_unitPopupGuid==popupGuid;
                        }
                    }
                    Log(popupOpened,$"{line} guid=0x{popupGuid:X16} which={_unitPopupWhich}");
                    break;
                case "anchor":
                    RefreshLiveSpawnIdentities();
                    bool selectedAnchor=p[1].Equals("selected",StringComparison.OrdinalIgnoreCase);
                    int anchorOrdinal=selectedAnchor?0:int.Parse(p[1].Split(':')[^1],CultureInfo.InvariantCulture);
                    bool spawnedAnchor=p[1].StartsWith("spawn:",StringComparison.OrdinalIgnoreCase);
                    ulong anchorGuid=selectedAnchor?_selectionGuid:spawnedAnchor?LiveSpawnGuid(anchorOrdinal):
                        p[1].StartsWith("wild-entry-nearest:",StringComparison.OrdinalIgnoreCase)
                        ?LiveWildEntryNearestGuid(anchorOrdinal):p[1].StartsWith("wild-entry:",StringComparison.OrdinalIgnoreCase)
                            ?LiveWildEntryGuid(anchorOrdinal):p[1].StartsWith("wild-hostile:",StringComparison.OrdinalIgnoreCase)
                            ?LiveWildHostileGuid(anchorOrdinal):LiveWildGuid(anchorOrdinal);
                    WorldEntity? anchorTarget=null;
                    if(anchorGuid==0||!_entities.TryGet(anchorGuid,out anchorTarget))
                    {
                        if(now-(_liveSelectWaitStarted==0?now:_liveSelectWaitStarted)<5)
                        {
                            if(_liveSelectWaitStarted==0) _liveSelectWaitStarted=now;
                            _liveWaitUntil=now+0.05;
                            return;
                        }
                    }
                    _liveSelectWaitStarted=0;
                    _liveAnchorGuid=anchorGuid;
                    float anchorOffset=p.Length>2?float.Parse(p[2],CultureInfo.InvariantCulture):0f;
                    bool anchored=anchorGuid!=0&&anchorTarget is not null&&SendGmCommand(
                        string.Create(CultureInfo.InvariantCulture,
                            $".go xyz {anchorTarget.Position.X-anchorOffset:R} {anchorTarget.Position.Y:R} {anchorTarget.Position.Z:R} {_config.Start.Map}"),
                        "protocol-runner-anchor");
                    Log(anchored,$"{line} guid=0x{anchorGuid:X16} position="+
                        (anchorTarget is null?"unavailable":$"{anchorTarget.Position.X:R}|{anchorTarget.Position.Y:R}|{anchorTarget.Position.Z:R}"));
                    break;
                case "attack":
                    if(p[1]=="start")
                    {
                        _lastAttackPreconditionGatePassed=null;
                        CommitSelection(_selectionGuid,true);
                        Log(_lastAttackPreconditionGatePassed==true,
                            $"{line} gate={(_lastAttackPreconditionGatePassed==true?"PASS":"REFUSED")}");
                    }
                    else { StopAttack("user-cancel"); Log(true,line); }
                    break;
                case "select-fight-target":
                    bool fightTargetPresent = _liveFightGuid != 0 && _entities.TryGet(_liveFightGuid, out _);
                    if (fightTargetPresent) CommitSelection(_liveFightGuid, false);
                    Log(fightTargetPresent, $"{line} guid=0x{_liveFightGuid:X16}");
                    break;
                case "interact":
                    Log(p.Length == 2 && p[1].Equals("gossip", StringComparison.OrdinalIgnoreCase) && RequestGossip(_selectionGuid),
                        $"{line} guid=0x{_selectionGuid:X16}");
                    break;
                case "gossip":
                    if (p[1].Equals("close", StringComparison.OrdinalIgnoreCase))
                    {
                        ResetGossip();
                        Log(true, line);
                    }
                    else if (p[1].Equals("select-icon", StringComparison.OrdinalIgnoreCase))
                    {
                        // Pick the option a player would click for a service (1 vendor, 3 trainer, 5 inn, 6 bank).
                        byte icon = byte.Parse(p[2], CultureInfo.InvariantCulture);
                        int index = _gossipMenu?.Options.Select((o, i) => (o, i)).FirstOrDefault(x => x.o.Icon == icon, (default, -1)).Item2 ?? -1;
                        Log(index >= 0 && SelectGossipOption(index), $"{line} index={index};options={_gossipMenu?.Options.Count ?? 0}");
                    }
                    else if (!p[1].Equals("select", StringComparison.OrdinalIgnoreCase))
                        Log(false, $"unknown {line}");
                    else
                    {
                        int option = int.Parse(p[2], CultureInfo.InvariantCulture);
                        Log(SelectGossipOption(option), line);
                    }
                    break;
                case "vendor":
                    string[] vendor = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if(p[1].Equals("open",StringComparison.OrdinalIgnoreCase))
                        Log(RequestVendor(_selectionGuid),$"{line} guid=0x{_selectionGuid:X16}");
                    else if (vendor.Length == 4 && vendor[1].Equals("buy-entry", StringComparison.OrdinalIgnoreCase))
                        Log(BuyVendorEntry(uint.Parse(vendor[2], CultureInfo.InvariantCulture),
                            byte.Parse(vendor[3], CultureInfo.InvariantCulture)), line);
                    else Log(false,$"unknown {line}");
                    break;
                case "trainer":
                    if (p[1].Equals("open", StringComparison.OrdinalIgnoreCase))
                        Log(RequestTrainer(_selectionGuid), $"{line} guid=0x{_selectionGuid:X16}");
                    else if (p[1].Equals("simulate", StringComparison.OrdinalIgnoreCase))
                    { SimulateTrainerList(); Log(true, line); }
                    else if (p[1].Equals("buy", StringComparison.OrdinalIgnoreCase))
                        Log(BuyTrainerSpell(uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].Equals("buy-first", StringComparison.OrdinalIgnoreCase))
                        Log(BuyFirstAvailableTrainerSpell(), line);
                    else if (p[1].Equals("confirm", StringComparison.OrdinalIgnoreCase))
                    {
                        bool pending = _trainerConfirmation is not null;
                        if (pending) { AcceptTrainerConfirmation(); CancelTrainerConfirmation(); }
                        Log(pending, $"{line} source=production-confirmation-handler;awaitServerReply=true");
                    }
                    else Log(false, $"unknown {line}");
                    break;
                case "quest":
                    string[] quest = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (quest.Length == 3 && quest[1].Equals("assert-reward-money", StringComparison.OrdinalIgnoreCase) &&
                        uint.TryParse(quest[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint expectedRewardMoney))
                    {
                        uint actualRewardMoney = _questOffer is { } offer
                            ? QuestNpcRewardMoney(offer.QuestId, offer.Money, revealed: true)
                            : _questDetails is { } details
                                ? QuestNpcRewardMoney(details.QuestId, details.Money, revealed: false) : 0;
                        Log((_questOffer is not null || _questDetails is not null) && actualRewardMoney == expectedRewardMoney,
                            $"{line} actual={actualRewardMoney};actor=0x{ControlledGuid:X16}");
                    }
                    else if (quest.Length == 2 && quest[1].Equals("mark-wire", StringComparison.OrdinalIgnoreCase))
                    {
                        _liveQuestWireMarkCount = _wire.SnapshotDetailed().Count(x =>
                            x.Packet.Outgoing && IsQuestProtocolOpcode(x.Packet.Opcode));
                        EmitInterface("quest", "wire-mark", "PASS", QuestGiverGuid(),
                            $"outgoingQuestBaseline={_liveQuestWireMarkCount}");
                        Log(true, $"{line} outgoingQuestBaseline={_liveQuestWireMarkCount}");
                    }
                    else if (quest.Length == 4 &&
                             quest[1].Equals("assert-wire", StringComparison.OrdinalIgnoreCase) &&
                             int.TryParse(quest[3], NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out int expectedQuestWire) &&
                             TryQuestWireSpec(quest[2], out ushort expectedQuestOpcode,
                                 out int expectedQuestPayloadSize))
                    {
                        WirePacketDetail[] allQuestPackets = _wire.SnapshotDetailed().Where(x =>
                            x.Packet.Outgoing && IsQuestProtocolOpcode(x.Packet.Opcode)).ToArray();
                        int delta = allQuestPackets.Length - _liveQuestWireMarkCount;
                        WirePacketDetail[] newQuestPackets = delta > 0 && delta <= allQuestPackets.Length
                            ? allQuestPackets[^delta..] : [];
                        bool pass = delta == expectedQuestWire && newQuestPackets.All(x =>
                            x.Packet.Opcode == expectedQuestOpcode &&
                            x.Packet.Size == expectedQuestPayloadSize &&
                            x.Prefix.Length == expectedQuestPayloadSize);
                        string actual = newQuestPackets.Length == 0 ? "none" : string.Join('|',
                            newQuestPackets.Select(x =>
                                $"{x.Packet.OpcodeName}:{x.Packet.Size}:{Convert.ToHexString(x.Prefix)}"));
                        EmitInterface("quest", "wire-contract", pass ? "PASS" : "FAIL",
                            QuestGiverGuid(),
                            $"expected={expectedQuestWire}:{WireRing.NameFor(expectedQuestOpcode)}:" +
                            $"{expectedQuestPayloadSize};actual={actual}");
                        Log(pass, $"{line} actual={actual}");
                    }
                    else if (quest.Length == 3 &&
                             quest[1].Equals("assert-panel", StringComparison.OrdinalIgnoreCase) &&
                             Enum.TryParse(quest[2], true, out QuestNpcPanel expectedPanel))
                    {
                        QuestNpcPanel actualPanel = QuestNpcPanelNow();
                        bool pass = actualPanel == expectedPanel;
                        EmitInterface("quest", "panel-state", pass ? "PASS" : "FAIL",
                            QuestGiverGuid(), $"expected={expectedPanel};actual={actualPanel}");
                        Log(pass, $"{line} actual={actualPanel}");
                    }
                    else if (quest.Length == 3 &&
                             quest[1].Equals("assert-giver-kind", StringComparison.OrdinalIgnoreCase))
                    {
                        ulong giver = QuestGiverGuid();
                        string actualKind = giver == 0 ? "none" :
                            GuidInfo.IsItem(giver) ? "item" : "world-unit";
                        bool pass = actualKind.Equals(quest[2], StringComparison.OrdinalIgnoreCase);
                        EmitInterface("quest", "giver-kind", pass ? "PASS" : "FAIL", giver,
                            $"expected={quest[2]};actual={actualKind}");
                        Log(pass, $"{line} actual={actualKind};giver=0x{giver:X16}");
                    }
                    else if (quest.Length == 4 &&
                             quest[1].Equals("assert-greeting-counts", StringComparison.OrdinalIgnoreCase) &&
                             int.TryParse(quest[2], NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out int expectedActive) &&
                             int.TryParse(quest[3], NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out int expectedAvailable))
                    {
                        int active = _questList?.Quests.Count(x =>
                            QuestFrameUiLaw.GreetingPool(x.Icon) == QuestGreetingPool.Active) ?? 0;
                        int available = _questList?.Quests.Count(x =>
                            QuestFrameUiLaw.GreetingPool(x.Icon) == QuestGreetingPool.Available) ?? 0;
                        bool pass = active == expectedActive && available == expectedAvailable;
                        EmitInterface("quest", "greeting-split", pass ? "PASS" : "FAIL",
                            QuestGiverGuid(),
                            $"expected={expectedActive}|{expectedAvailable};actual={active}|{available}");
                        Log(pass, $"{line} actual={active}|{available}");
                    }
                    else if (quest.Length == 3 &&
                             quest[1].Equals("assert-completable", StringComparison.OrdinalIgnoreCase) &&
                             bool.TryParse(quest[2], out bool expectedCompletable))
                    {
                        bool actualCompletable = _questRequestItems?.Completable == true;
                        bool pass = _questRequestItems is not null &&
                            actualCompletable == expectedCompletable;
                        EmitInterface("quest", "progress-completable", pass ? "PASS" : "FAIL",
                            QuestGiverGuid(),
                            $"expected={expectedCompletable};actual={actualCompletable};" +
                            $"panel={QuestNpcPanelNow()}");
                        Log(pass, $"{line} actual={actualCompletable};panel={QuestNpcPanelNow()}");
                    }
                    else if (p[1].Equals("status", StringComparison.OrdinalIgnoreCase))
                        Log(RequestQuestStatus(_selectionGuid), $"{line} guid=0x{_selectionGuid:X16}");
                    else if (p[1].Equals("hello", StringComparison.OrdinalIgnoreCase))
                        Log(RequestQuestHello(_selectionGuid), $"{line} guid=0x{_selectionGuid:X16}");
                    else if (p[1].Equals("query", StringComparison.OrdinalIgnoreCase))
                        Log(RequestQuestDetails(_selectionGuid, uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].Equals("accept", StringComparison.OrdinalIgnoreCase)) Log(AcceptQuest(), line);
                    else if (p[1].Equals("complete", StringComparison.OrdinalIgnoreCase)) Log(RequestQuestCompletion(), line);
                    else if (p[1].Equals("request-reward", StringComparison.OrdinalIgnoreCase)) Log(RequestQuestReward(), line);
                    else if (p[1].Equals("choose", StringComparison.OrdinalIgnoreCase))
                        Log(ChooseQuestReward(uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].Equals("abandon", StringComparison.OrdinalIgnoreCase))
                        Log(AbandonQuest(uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].Equals("inspect-log", StringComparison.OrdinalIgnoreCase)) Log(InspectQuestLog(), line);
                    else if (p[1].Equals("simulate", StringComparison.OrdinalIgnoreCase))
                    { SimulateQuestFlow(); Log(true, line); }
                    else if (p[1].Equals("stage", StringComparison.OrdinalIgnoreCase) && p.Length > 2)
                        Log(StageQuestFrameProof(p[2]), line);
                    else Log(false, $"unknown {line}");
                    break;
                case "loot":
                {
                    // p holds at most three fields: the four-field forms split the whole line.
                    string[] lp = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (p[1].Equals("master-setup", StringComparison.OrdinalIgnoreCase)) Log(TryLiveRaidMasterLootSetup(), line);
                    else if (lp.Length == 4 && lp[1].Equals("master-give", StringComparison.OrdinalIgnoreCase))
                        Log(TryLiveMasterLoot(byte.Parse(lp[2], CultureInfo.InvariantCulture), ulong.Parse(lp[3], CultureInfo.InvariantCulture)), line);
                    else if (lp.Length == 4 && lp[1].Equals("equip-reward", StringComparison.OrdinalIgnoreCase))
                        Log(TryLiveEquipRaidLoot(uint.Parse(lp[2], CultureInfo.InvariantCulture), ulong.Parse(lp[3], CultureInfo.InvariantCulture)), line);
                    else if (p.Length == 3 && p[1].Equals("inventory-report", StringComparison.OrdinalIgnoreCase)) Log(WriteLiveRaidInventoryReport(p[2]), line);
                    else if (p.Length == 3 && p[1].Equals("report", StringComparison.OrdinalIgnoreCase)) Log(WriteLiveLootReport(p[2]), line);
                    else if (p[1].Equals("request", StringComparison.OrdinalIgnoreCase)) Log(RequestLoot(_selectionGuid), line);
                    else if (p[1].Equals("money", StringComparison.OrdinalIgnoreCase)) Log(TakeLootMoney(), line);
                    else if (p[1].Equals("item-first", StringComparison.OrdinalIgnoreCase)) Log(TakeFirstLootItem(), line);
                    else if (p[1].Equals("take-all", StringComparison.OrdinalIgnoreCase)) Log(TakeAllLoot(), line);
                    else if (p[1].Equals("release", StringComparison.OrdinalIgnoreCase))
                    { bool wasOpen = _loot.IsOpen; ReleaseLoot(); Log(wasOpen, line); }
                    else if (p[1].Equals("simulate", StringComparison.OrdinalIgnoreCase))
                    { SimulateLootFlow(); Log(true, line); }
                    else if (p[1].Equals("simulate-empty", StringComparison.OrdinalIgnoreCase))
                    { SimulateLootFlow(empty: true); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                }
                case "gameobject":
                    if (p[1].Equals("use", StringComparison.OrdinalIgnoreCase)) Log(UseGameObject(_selectionGuid), line);
                    else if (p[1].Equals("snapshot", StringComparison.OrdinalIgnoreCase)) { SnapshotGameObjects(); Log(true, line); }
                    else if (p[1].Equals("simulate", StringComparison.OrdinalIgnoreCase)) { SimulateGameObjectFlow(); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "gathering":
                    if (p[1].Equals("snapshot", StringComparison.OrdinalIgnoreCase))
                    { SnapshotGathering(); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "rest-xp":
                    if (p[1].Equals("snapshot", StringComparison.OrdinalIgnoreCase))
                    { RestSnapshot? rs = CurrentRestSnapshot(); if (rs is not null) { EmitRestSnapshot("CAPTURED", rs.Value); _restXpOpen = true; } Log(rs is not null, line); }
                    else if (p[1].Equals("simulate", StringComparison.OrdinalIgnoreCase)) { SimulateRestXpFlow(); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "death-rez":
                    if (p[1].Equals("snapshot", StringComparison.OrdinalIgnoreCase)) { ObserveDeathRez(); ObserveCorpseStore(); Log(true, line); }
                    else if (p[1].Equals("repop", StringComparison.OrdinalIgnoreCase)) Log(RequestRepop(), line);
                    else if (p[1].Equals("reclaim", StringComparison.OrdinalIgnoreCase)) Log(ReclaimCorpse(), line);
                    else if (p[1].Equals("accept", StringComparison.OrdinalIgnoreCase)) Log(AnswerResurrect(true), line);
                    else if (p[1].Equals("simulate", StringComparison.OrdinalIgnoreCase)) { SimulateDeathRezFlow(); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "hearth":
                    if (p[1].Equals("bind", StringComparison.OrdinalIgnoreCase)) Log(RequestBind(_selectionGuid), line);
                    else if (p[1].Equals("use", StringComparison.OrdinalIgnoreCase)) Log(UseHearthstone(), line);
                    else if (p[1].Equals("simulate", StringComparison.OrdinalIgnoreCase)) { SimulateHearthFlow(); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "taxi":
                    if (p[1].Equals("open", StringComparison.OrdinalIgnoreCase)) Log(RequestTaxiMap(_selectionGuid), line);
                    else if (p[1].Equals("status", StringComparison.OrdinalIgnoreCase)) Log(RequestTaxiStatus(_selectionGuid), line);
                    else if (p[1].Equals("activate", StringComparison.OrdinalIgnoreCase))
                        Log(ActivateTaxi(uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].Equals("simulate", StringComparison.OrdinalIgnoreCase)) { SimulateTaxiFlow(); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "environment":
                    if (p[1].Equals("audit", StringComparison.OrdinalIgnoreCase)) { RunEnvironmentAudit(); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "glue": Log(RunLiveGlue(line), line); break;
                case "character":
                    if (p[1].Equals("inspect", StringComparison.OrdinalIgnoreCase)) Log(InspectCharacterInventory(), line);
                    else if (p[1].Equals("open", StringComparison.OrdinalIgnoreCase))
                    { _characterOpen = true; _paperDollDirty = true; Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "inventory":
                    string[] inventory = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (inventory.Length == 4 && inventory[1].Equals("equip-bag", StringComparison.OrdinalIgnoreCase))
                        Log(LiveEquipBag(uint.Parse(inventory[2], CultureInfo.InvariantCulture),
                            int.Parse(inventory[3], CultureInfo.InvariantCulture)), line);
                    // ensure-bag <entry> <container 1-4>: a bag already in that slot passes (re-runnable SETUP),
                    // otherwise equip a carried <entry> there - a fair tester carries bags like a real character.
                    else if (inventory.Length == 4 && inventory[1].Equals("ensure-bag", StringComparison.OrdinalIgnoreCase))
                    {
                        int bagContainer = int.Parse(inventory[3], CultureInfo.InvariantCulture);
                        bool present = bagContainer is >= 1 and <= 4 && _net is not null &&
                                       _entities.TryGet(_net.PlayerGuid, out WorldEntity bagOwner) &&
                                       bagOwner.Fields.PlayerInventorySlot(18 + bagContainer) != 0;
                        Log(present || LiveEquipBag(uint.Parse(inventory[2], CultureInfo.InvariantCulture), bagContainer),
                            line + (present ? " present" : ""));
                    }
                    else if (inventory.Length == 3 && inventory[1].Equals("stage-bag", StringComparison.OrdinalIgnoreCase))
                        Log(LiveStageBag(uint.Parse(inventory[2], CultureInfo.InvariantCulture)), line);
                    else if (inventory.Length == 2 && inventory[1].Equals("require-key", StringComparison.OrdinalIgnoreCase))
                        Log(_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity keyPlayer) &&
                            HasKey(keyPlayer), line);
                    else if (inventory.Length == 4 && inventory[1].Equals("require-bag", StringComparison.OrdinalIgnoreCase))
                        Log(LiveRequireBag(int.Parse(inventory[2], CultureInfo.InvariantCulture), null,
                            int.Parse(inventory[3], CultureInfo.InvariantCulture)), line);
                    else if (inventory.Length == 5 && inventory[1].Equals("require-bag", StringComparison.OrdinalIgnoreCase))
                        Log(LiveRequireBag(int.Parse(inventory[2], CultureInfo.InvariantCulture),
                            uint.Parse(inventory[3], CultureInfo.InvariantCulture),
                            int.Parse(inventory[4], CultureInfo.InvariantCulture)), line);
                    else if (inventory.Length == 3 && inventory[1].Equals("ammo", StringComparison.OrdinalIgnoreCase))
                        Log(uint.TryParse(inventory[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint selectedAmmo) &&
                            _entities.TryGet(ControlledGuid, out var ammoOwner) && CarriedAmmoCount(ammoOwner, selectedAmmo) > 0 &&
                            SelectAmmo(selectedAmmo), line);
                    else if (inventory.Length == 2 && inventory[1].Equals("inspect-bags", StringComparison.OrdinalIgnoreCase))
                        Log(LiveInspectBags(), line);
                    else if (inventory.Length == 5 && inventory[1].Equals("simulate-push", StringComparison.OrdinalIgnoreCase))
                    {
                        TriggerItemPushAnimation(byte.Parse(inventory[2], CultureInfo.InvariantCulture),
                            uint.Parse(inventory[3], CultureInfo.InvariantCulture),
                            uint.Parse(inventory[4], CultureInfo.InvariantCulture));
                        Log(true, line);
                    }
                    else if (p[1].StartsWith("equip-entry", StringComparison.OrdinalIgnoreCase))
                        Log(EquipBackpackEntry(uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].StartsWith("unequip-slot", StringComparison.OrdinalIgnoreCase))
                        Log(UnequipSlot(int.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else Log(false, $"unknown {line}");
                    break;
                case "action-icon":
                    Log(p.Length > 1 && p[1].Equals("attack", StringComparison.OrdinalIgnoreCase) &&
                        EmitAttackIconEvidence(), line);
                    break;
                case "action-gesture":
                    RunLiveActionGesture(line);
                    break;
                case "item-gesture": Log(RunLiveItemGesture(line), line); break;
                case "pet-gesture": Log(RunLivePetGesture(line), line); break;
                case "pvp-state": Log(RunLivePvpState(line), line); break;
                case "pose": Log(RunLivePose(line), line); break;
                case "equipment": Log(InspectLiveEquipment(line), line); break;
                case "unit-state": Log(InspectLiveUnitState(line), line); break;
                case "combat-text": Log(InspectLiveCombatText(line), line); break;
                case "audit-mail": Log(RunLiveMailAudit(line), line); break;
                case "audit-control": Log(RunLiveControlAudit(line), line); break;
                case "support-at": Log(InspectLiveSupport(line), line); break;
                case "liquid-visible":
                    if (p.Length == 2 && bool.TryParse(p[1], out bool liquidVisible) && _liquid is not null)
                    { _liquid.Enabled = liquidVisible; Log(true, line + " diagnostic runtime visibility"); }
                    else Log(false, line);
                    break;
                case "escort-until-complete":
                    if (!AdvanceLiveEscort(line)) return;
                    break;
                case "fight-until-dead":
                    if (!AdvanceLiveFight(line)) return;
                    break;
                case "boss-trial":
                    if (!AdvanceBossTrial(line)) return;
                    break;
                case "pack-trial":
                    if (!AdvancePackTrial(line)) return;
                    break;
                case "patrol-watch":
                    if (!AdvancePatrolWatch(line)) return;
                    break;
                case "patrol-at":
                    if (!AdvancePatrolWatch(line, mustTravel: false)) return;
                    break;
                case "kill-for-quest":
                    if (!AdvanceQuestKills(line)) return;
                    break;
                case "companion":
                    RunLiveCompanionStep(line);
                    break;
                case "fish-until-bite":
                    if (!AdvanceLiveFishing(line)) return;
                    break;
                case "select-enemy":
                    WorldEntity? enemy = _controller is null ? null : _entities.Units
                        .Where(x => x.IsCreature && !x.IsDead && ReactionPlayerToward(x) == FactionReaction.Hostile)
                        .OrderBy(x => Vector3.DistanceSquared(x.Position, _controller.Position))
                        .ThenBy(x => x.Guid).FirstOrDefault();
                    if (enemy is not null) CommitSelection(enemy.Guid, false);
                    Log(enemy is not null, $"{line} guid=0x{enemy?.Guid ?? 0:X16};entry={enemy?.Entry ?? 0};" +
                        $"flags=0x{enemy?.Fields.UnitFlags ?? 0:X8};health={enemy?.Fields.Health ?? 0}");
                    break;
                case "action-stage":
                    string[] stagedAction = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (stagedAction.Length == 3 &&
                        int.TryParse(stagedAction[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out int stagedSlot) && stagedSlot is >= 0 and < 120 &&
                        uint.TryParse(stagedAction[2], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out uint stagedSpell))
                    {
                        _actions.Set(stagedSlot, new ActionSlot(ActionSlot.Spell, stagedSpell));
                        _multiActionProtocolFixtureStaged = true;
                        Log(true, $"{line} provenance=explicit-live-protocol-fixture");
                    }
                    else Log(false, $"unknown {line}");
                    break;
                case "action-grid":
                    if (p[1].Equals("show", StringComparison.OrdinalIgnoreCase))
                    {
                        _draggingSpellId = 1459;
                        _multiActionProtocolFixtureStaged = true;
                        Log(true, $"{line} provenance=explicit-live-protocol-fixture");
                    }
                    else if (p[1].Equals("hide", StringComparison.OrdinalIgnoreCase))
                    {
                        _draggingSpellId = 0;
                        _multiActionProtocolFixtureStaged = true;
                        Log(true, $"{line} provenance=explicit-live-protocol-fixture");
                    }
                    else Log(false, $"unknown {line}");
                    break;
                case "party-stage":
                    Log(false, "party-stage rejected: Party proof requires observed wire/runtime state; no state mutated");
                    break;
                case "party-invite-stage":
                    Log(false, "party-invite-stage rejected: Party invite proof requires an inbound invitation; no state mutated");
                    break;
                case "party-clear":
                    Log(false, "party-clear rejected: command cannot erase authenticated roster/invite state; no state mutated");
                    break;
                case "bank":
                    if (p[1].Equals("open", StringComparison.OrdinalIgnoreCase)) Log(RequestBank(_selectionGuid), line);
                    else if (p[1].Equals("deposit-entry", StringComparison.OrdinalIgnoreCase))
                        Log(DepositBankEntry(uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].Equals("withdraw-entry", StringComparison.OrdinalIgnoreCase))
                        Log(WithdrawBankEntry(uint.Parse(p[2], CultureInfo.InvariantCulture)), line);
                    else if (p[1].Equals("buy-slot", StringComparison.OrdinalIgnoreCase)) Log(BuyNextBankSlot(), line);
                    else Log(false, $"unknown {line}");
                    break;
                case "mail":
                    string[] mail = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (mail[1].Equals("open", StringComparison.OrdinalIgnoreCase)) Log(RequestMail(_selectionGuid), line);
                    else if (mail[1].Equals("send", StringComparison.OrdinalIgnoreCase) && mail.Length == 6)
                        Log(SendMailFlow(mail[2], uint.Parse(mail[3], CultureInfo.InvariantCulture),
                            uint.Parse(mail[4], CultureInfo.InvariantCulture), uint.Parse(mail[5], CultureInfo.InvariantCulture)), line);
                    else if (mail[1].Equals("take-money-first", StringComparison.OrdinalIgnoreCase))
                    { uint id = FirstMailId("money"); Log(id != 0 && TakeMailMoney(id), $"{line} id={id}"); }
                    else if (mail[1].Equals("take-item-first", StringComparison.OrdinalIgnoreCase))
                    { uint id = FirstMailId("item"); Log(id != 0 && TakeMailItem(id), $"{line} id={id}"); }
                    else if (mail[1].Equals("return-first", StringComparison.OrdinalIgnoreCase))
                    { uint id = FirstMailId("any"); Log(id != 0 && ReturnMail(id), $"{line} id={id}"); }
                    else if (mail[1].Equals("delete-first", StringComparison.OrdinalIgnoreCase))
                    { uint id = FirstMailId("deletable"); Log(id != 0 && DeleteMail(id), $"{line} id={id}"); }
                    else if (mail[1].Equals("simulate-list", StringComparison.OrdinalIgnoreCase))
                    { SimulateMailList(); Log(true, line); }
                    else if (mail[1].Equals("simulate-actions", StringComparison.OrdinalIgnoreCase))
                    { SimulateMailActions(); Log(true, line); }
                    else if (mail[1].Equals("tab-send", StringComparison.OrdinalIgnoreCase))
                    { SetMailTab(1, playSound: true); Log(_mailOpen && _mailTab == 1, line); }
                    else if (mail[1].Equals("open-first", StringComparison.OrdinalIgnoreCase) && _mail.Count > 0)
                    { ToggleOpenMail(_mail[0]); Log(_openMailId == _mail[0].Id, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "auction":
                    string[] auction = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (auction[1].Equals("open", StringComparison.OrdinalIgnoreCase)) Log(RequestAuction(_selectionGuid), line);
                    else if (auction[1].Equals("browse", StringComparison.OrdinalIgnoreCase)) Log(BrowseAuctions(0), line);
                    else if (auction[1].Equals("owner", StringComparison.OrdinalIgnoreCase)) Log(RequestOwnerAuctions(0), line);
                    else if (auction[1].Equals("bidder", StringComparison.OrdinalIgnoreCase)) Log(RequestBidderAuctions(0), line);
                    else if (auction[1].Equals("simulate", StringComparison.OrdinalIgnoreCase)) { SimulateAuctionFlow(); Log(true, line); }
                    else if (auction[1].Equals("bid-first", StringComparison.OrdinalIgnoreCase) && _auctionLists[0].Rows.Count > 0)
                    { AuctionListEntry row = _auctionLists[0].Rows[0]; Log(BidAuction(0, row.Id, AuctionFrameUiLaw.MinimumBid(row.StartBid, row.Bid, row.MinIncrement)), line); }
                    else if (auction[1].Equals("cancel-first", StringComparison.OrdinalIgnoreCase) && _auctionLists[2].Rows.Count > 0)
                        Log(CancelAuction(_auctionLists[2].Rows[0].Id), line);
                    else if (auction[1].Equals("stage", StringComparison.OrdinalIgnoreCase) && auction.Length == 4)
                    {
                        // "auction stage <container> <slot>" seats that bag slot of the DRIVEN body in the sell slot.
                        int container = int.Parse(auction[2], CultureInfo.InvariantCulture), slot = int.Parse(auction[3], CultureInfo.InvariantCulture);
                        WorldEntity? staged = ResolveInventoryItem(container, slot);
                        if (staged is not null)
                        { _carriedContainer = container; _carriedSlot = slot; AttachAuctionSellItem(staged); }
                        Log(staged is not null, $"{line} -> {(staged is null ? "empty" : $"item {staged.Entry} x{staged.Fields.ItemStackCount}")}");
                    }
                    else if (auction[1].Equals("create", StringComparison.OrdinalIgnoreCase) && auction.Length == 5)
                        // "auction create <bid> <buyout> <minutes>" sells whatever sits in the sell slot.
                        Log(CreateAuction(uint.Parse(auction[2], CultureInfo.InvariantCulture), uint.Parse(auction[3], CultureInfo.InvariantCulture),
                            uint.Parse(auction[4], CultureInfo.InvariantCulture)), line);
                    else Log(false, $"unknown {line}");
                    break;
                case "profession":
                    string[] profession = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (profession.Length == 2 && profession[1].Equals("snapshot", StringComparison.OrdinalIgnoreCase))
                    { SnapshotProfessionRecipes(); Log(true, line); }
                    else if (profession.Length == 2 && profession[1].Equals("diagnose", StringComparison.OrdinalIgnoreCase))
                    { DiagnoseProfessionLines(); Log(true, line); }
                    else if (profession.Length == 2 && profession[1].Equals("open", StringComparison.OrdinalIgnoreCase))
                        Log(OpenFirstProfession(), line);
                    else if (profession.Length > 2 && profession[1].Equals("open", StringComparison.OrdinalIgnoreCase))
                        Log(OpenProfessionNamed(string.Join(' ', profession.Skip(2))), line);
                    else if (profession.Length == 2 && profession[1].Equals("inspect", StringComparison.OrdinalIgnoreCase))
                    { EmitProfessionRecipeSnapshot(); Log(_professionOpen, line); }
                    else if (profession.Length == 2 && profession[1].Equals("provision-first", StringComparison.OrdinalIgnoreCase))
                        Log(ProvisionFirstProfessionRecipe(), line);
                    else if (profession.Length == 3 && profession[1].Equals("provision", StringComparison.OrdinalIgnoreCase))
                        Log(ProvisionProfessionRecipe(int.Parse(profession[2], CultureInfo.InvariantCulture)), line);
                    else if (profession.Length == 3 && profession[1].Equals("provision-spell", StringComparison.OrdinalIgnoreCase))
                        Log(ProvisionProfessionSpell(uint.Parse(profession[2], CultureInfo.InvariantCulture)), line);
                    else if (profession.Length == 2 && profession[1].Equals("craft-first", StringComparison.OrdinalIgnoreCase))
                        Log(CraftProfessionRecipe(0), line);
                    else if (profession.Length == 3 && profession[1].Equals("craft", StringComparison.OrdinalIgnoreCase))
                        Log(CraftProfessionRecipe(int.Parse(profession[2], CultureInfo.InvariantCulture)), line);
                    else if (profession.Length == 3 && profession[1].Equals("craft-spell", StringComparison.OrdinalIgnoreCase))
                        Log(CraftProfessionSpell(uint.Parse(profession[2], CultureInfo.InvariantCulture)), line);
                    else if (profession.Length == 2 && profession[1].Equals("cleanup-last", StringComparison.OrdinalIgnoreCase))
                        Log(CleanupLastProfessionProduct(), line);
                    else if (profession.Length == 2 && profession[1].Equals("simulate", StringComparison.OrdinalIgnoreCase))
                    { SimulateProfessionFlow(); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "guild":
                    string[] guild = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    if (guild.Length == 2 && guild[1].Equals("roster", StringComparison.OrdinalIgnoreCase)) Log(RequestGuildRoster(), line);
                    else if (guild.Length == 3 && guild[1].Equals("motd", StringComparison.OrdinalIgnoreCase)) Log(SetGuildMotd(guild[2]), line);
                    else if (guild.Length == 3 && guild[1].Equals("promote", StringComparison.OrdinalIgnoreCase)) Log(PromoteGuildMember(guild[2]), line);
                    else if (guild.Length == 3 && guild[1].Equals("demote", StringComparison.OrdinalIgnoreCase)) Log(DemoteGuildMember(guild[2]), line);
                    else if (guild.Length == 2 && guild[1].Equals("leave", StringComparison.OrdinalIgnoreCase)) Log(LeaveGuild(), line);
                    else if (guild.Length == 2 && guild[1].Equals("disband", StringComparison.OrdinalIgnoreCase)) Log(DisbandGuild(), line);
                    else if (guild.Length == 2 && guild[1].Equals("simulate", StringComparison.OrdinalIgnoreCase)) { SimulateGuildFlow(); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "tabard":
                    string[] tabard = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tabard.Length == 2 && tabard[1].Equals("open", StringComparison.OrdinalIgnoreCase))
                        Log(RequestTabardDesigner(_selectionGuid), $"{line} guid=0x{_selectionGuid:X16}");
                    else if (tabard.Length == 2 && tabard[1].Equals("close", StringComparison.OrdinalIgnoreCase))
                    { _tabardOpen = false; Log(true, line); }
                    else if (tabard.Length == 7 && tabard[1].Equals("save", StringComparison.OrdinalIgnoreCase))
                        Log(SaveTabardDesign(uint.Parse(tabard[2], CultureInfo.InvariantCulture),
                            uint.Parse(tabard[3], CultureInfo.InvariantCulture), uint.Parse(tabard[4], CultureInfo.InvariantCulture),
                            uint.Parse(tabard[5], CultureInfo.InvariantCulture), uint.Parse(tabard[6], CultureInfo.InvariantCulture)), line);
                    else if (tabard.Length == 7 && tabard[1].Equals("simulate", StringComparison.OrdinalIgnoreCase))
                    { SimulateTabardFlow(uint.Parse(tabard[2], CultureInfo.InvariantCulture),
                        uint.Parse(tabard[3], CultureInfo.InvariantCulture), uint.Parse(tabard[4], CultureInfo.InvariantCulture),
                        uint.Parse(tabard[5], CultureInfo.InvariantCulture), uint.Parse(tabard[6], CultureInfo.InvariantCulture)); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "talent":
                    string[] talent = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (talent.Length == 2 && talent[1].Equals("open", StringComparison.OrdinalIgnoreCase)) Log(OpenTalentPanel(), line);
                    else if (talent.Length == 2 && talent[1].Equals("inspect", StringComparison.OrdinalIgnoreCase))
                    { byte cls = _net is not null && _entities.TryGet(_net.PlayerGuid, out var tp) ? tp.Fields.Bytes0.Class : (byte)0; EmitTalentSnapshot(cls); Log(true, line); }
                    else if (talent.Length == 2 && talent[1].Equals("spend-first", StringComparison.OrdinalIgnoreCase)) Log(SpendFirstEligibleTalent(), line);
                    else if (talent.Length == 3 && talent[1].Equals("spend", StringComparison.OrdinalIgnoreCase)) Log(SpendTalent(uint.Parse(talent[2], CultureInfo.InvariantCulture)), line);
                    else if (talent.Length == 2 && talent[1].Equals("confirm-wipe", StringComparison.OrdinalIgnoreCase)) Log(ConfirmTalentWipe(), line);
                    else if (talent.Length == 2 && talent[1].Equals("simulate", StringComparison.OrdinalIgnoreCase)) { SimulateTalentRoster(); Log(true, line); }
                    else Log(false, $"unknown {line}");
                    break;
                case "panel":
                    string panel = p[1].ToLowerInvariant();
                    CloseLiveRunPanels();
                    bool opened = panel switch
                    {
                        "character" => OpenLiveCharacter(),
                        "spellbook" => OpenLiveSpellbook(),
                        "quest" => _questLogOpen = true,
                        "social" => OpenLiveSocial(),
                        "who" => OpenLiveWho(),
                        "worldmap" => _worldMapOpen = true,
                        "help" => OpenLiveHelp(),
                        "keybindings" => OpenLiveBindings(),
                        "macro" => OpenLiveMacros(),
                        "guild" => OpenLiveGuild(),
                        "auction" => OpenLiveAuction(),
                        "mail" => OpenLiveMail(),
                        "profession" => OpenLiveProfession(),
                        "talent" => OpenLiveTalent(),
                        "trade" => _tradeOpen = true,
                        "bank" => _bankOpen = true,
                        "trainer" => OpenLiveTrainer(),
                        "taxi" => OpenLiveTaxi(),
                        "game-menu" => OpenLiveGameMenu(),
                        "none" => true,
                        _ => false,
                    };
                    Log(opened, line);
                    break;
                case "game-menu":
                    string[] gameMenu = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    bool gameMenuPass = gameMenu.Length == 2 &&
                        (gameMenu[1].ToLowerInvariant() switch
                        {
                            "assert-open" => _settingsOpen && _menuPage == MenuPage.GameMenu,
                            "assert-closed" => !_settingsOpen && !_settingsPopupCloseRequested,
                            "assert-player-panels-closed" => !HasPlayerPanelForEscape() && !_loot.IsOpen,
                            "assert-target-cleared" => _selectionGuid == 0,
                            "assert-targeting-cleared" => _groundCastSpell == 0 && _itemCastSpell == 0,
                            "assert-stack-split-closed" => _splitContainer == InventoryUiLaw.EmptyContainer,
                            "assert-carried-cleared" => !HasCarriedItem,
                            _ => false,
                        });
                    EmitInterface("game-menu", gameMenu.Length > 1 ? gameMenu[1] : "unknown",
                        gameMenuPass ? "PASS" : "FAIL", _net?.PlayerGuid ?? 0,
                        $"menuOpen={_settingsOpen};page={_menuPage};playerPanels={HasPlayerPanelForEscape()};" +
                        $"loot={_loot.IsOpen};target=0x{_selectionGuid:X16};groundSpell={_groundCastSpell};" +
                        $"itemSpell={_itemCastSpell};split={_splitContainer};carried={HasCarriedItem}");
                    Log(gameMenuPass, line);
                    break;
                case "inspect":
                    string[] inspect = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    IReadOnlyList<WirePacketDetail> inspectWire = _wire.SnapshotDetailed();
                    bool inspectPass;
                    string inspectStep = inspect.Length > 1 ? inspect[1].ToLowerInvariant() : "unknown";
                    string inspectDetail;
                    if (inspect.Length == 2 && inspectStep == "mark-wire")
                    {
                        _liveInspectWireMarkCount = inspectWire.Count(x => x.Packet.Outgoing &&
                            x.Packet.Opcode == (ushort)Op.CMSG_INSPECT);
                        inspectPass = true;
                        inspectDetail = $"outgoingInspectBaseline={_liveInspectWireMarkCount}";
                    }
                    else if (inspect.Length == 3 && inspectStep == "assert-wire" &&
                             int.TryParse(inspect[2], NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out int expectedInspectWire))
                    {
                        WirePacketDetail[] current = inspectWire.Where(x => x.Packet.Outgoing &&
                            x.Packet.Opcode == (ushort)Op.CMSG_INSPECT).ToArray();
                        int delta = current.Length - _liveInspectWireMarkCount;
                        WirePacketDetail[] newPackets = delta > 0 && delta <= current.Length
                            ? current[^delta..] : [];
                        inspectPass = delta == expectedInspectWire && newPackets.All(x =>
                            x.Packet.Size == 8 && x.Prefix.Length == 8);
                        inspectDetail = $"expected={expectedInspectWire};actual={delta};" +
                            $"payloads={string.Join('|', newPackets.Select(x =>
                                x.Prefix.Length == 8 ? $"0x{BitConverter.ToUInt64(x.Prefix):X16}" :
                                $"invalid-{x.Prefix.Length}-bytes"))}";
                    }
                    else
                    {
                        inspectPass = inspect.Length == 2 && inspectStep switch
                        {
                            "assert-open" => _inspectOpen && _inspectGuid != 0 &&
                                _entities.TryGet(_inspectGuid, out _),
                            "assert-closed" => !_inspectOpen && _inspectGuid == 0,
                            "assert-target-binding" => _inspectOpen &&
                                _inspectBinding.Kind == InspectTokenKind.Target,
                            "assert-party-binding" => _inspectOpen &&
                                _inspectBinding.Kind == InspectTokenKind.Party &&
                                _inspectBinding.PartyIndex >= 0,
                            "assert-public-only" => _inspectOpen &&
                                _entities.TryGet(_inspectGuid, out WorldEntity inspectedPlayer) &&
                                inspectedPlayer.IsPlayer,
                            _ => false,
                        };
                        inspectDetail = $"open={_inspectOpen};guid=0x{_inspectGuid:X16};" +
                            $"binding={_inspectBinding.Kind};partyIndex={_inspectBinding.PartyIndex};" +
                            $"selection=0x{_selectionGuid:X16}";
                    }
                    EmitInterface("inspect", inspectStep, inspectPass ? "PASS" : "FAIL",
                        _inspectGuid, inspectDetail);
                    Log(inspectPass, $"{line} {inspectDetail}");
                    break;
                case "interface-blocked":
                    string[] blocked = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                    EmitInterface(blocked[1], blocked[2], $"BLOCKED-BY:{blocked[3]}", _selectionGuid, "boundedWaitExpired=true");
                    Log(true, line);
                    break;
                case "spellbook":
                    int known = EmitKnownSpellInventory();
                    Log(known > 0, $"{line} known={known}");
                    break;
                case "cast":
                    uint castSpell = uint.Parse(p[1], CultureInfo.InvariantCulture);
                    int beforeCast = _verdicts.Snapshot("spell-sweep").Count;
                    TryCast(castSpell);
                    _liveLastCastSpell = castSpell;
                    _liveSpellWaitAfter = _verdicts.Snapshot("spell-sweep").Count;
                    Log(_verdicts.Snapshot("spell-sweep").Count > beforeCast, line);
                    break;
                case "castground":
                    // castground <spellId> [forwardYards=15] — commit a ground-target cast at a
                    // point ahead of the player, bypassing the mouse (the scripted twin of the
                    // targeting-cursor click; same CommitGroundCast wire path).
                    uint groundSpell = uint.Parse(p[1], CultureInfo.InvariantCulture);
                    float forwardYards = p.Length > 2
                        ? float.Parse(p[2], CultureInfo.InvariantCulture) : 15f;
                    bool groundOk = false;
                    if (_controller is not null)
                    {
                        var fwd = new Vector3(MathF.Cos(_controller.Yaw), MathF.Sin(_controller.Yaw), 0f);
                        Vector3 spot = _controller.Position + fwd * forwardYards;
                        spot.Z = SpellParticleGroundHeight(spot.X, spot.Y, _controller.Position.Z + 5f)
                            ?? _controller.Position.Z;
                        CommitGroundCast(groundSpell, spot);
                        _liveLastCastSpell = groundSpell;
                        groundOk = true;
                    }
                    Log(groundOk, line);
                    break;
                case "cv-probe":
                {
                    // Command View scheme probe: what the rig and camera are doing right now.
                    var cam = _window.Camera;
                    if (p.Length > 1 && p[1] == "lock" && _freeView)
                    {
                        // Dev shortcut: make the own character the primary and engage the lock.
                        _freecamSelection.Clear();
                        _freecamSelection.Add(LocalPlayerGuid);
                        _rtsPrimaryGuid = LocalPlayerGuid;
                        if (!Settings.Controls.CommandViewLockOnPrimary) ToggleCommandViewLock();
                    }
                    if (p.Length > 2 && p[1] == "cursor")
                    {
                        // Force a cursor stem through the real draw path for a few frames, or
                        // report what the window has installed.
                        if (p[2] != "state") { _liveForcedCursorStem = p[2]; _liveForcedCursorFrames = 6; _window.CursorTrace = true; }
                        string cur = $"stem={p[2]} installed={_window.CursorModeName} forcedLeft={_liveForcedCursorFrames}";
                        EmitInterface("cv-probe", "cursor", "OK", _net?.PlayerGuid ?? 0, cur);
                        Log(true, $"{line} => {cur}");
                        break;
                    }
                    if (p.Length > 1 && p[1] == "hover")
                    {
                        // First on-screen live creature within 40 yd: does the hover pick find it,
                        // is the eye-to-unit ray occluded, and what cursor stem would the law draw?
                        Vector2 vp = ImGuiNET.ImGui.GetIO().DisplaySize;
                        Vector3 me = _entities.TryGet(LocalPlayerGuid, out WorldEntity selfU) ? selfU.Position : Vector3.Zero;
                        string hover = "no creature on screen";
                        foreach (WorldEntity u in _entities.Units.OrderBy(x => Vector3.DistanceSquared(x.Position, me)))
                        {
                            if (!u.IsCreature || u.IsDead || Vector3.Distance(u.Position, me) > 40f) continue;
                            if (!_window.Camera.TryProjectToScreen(u.Position + new Vector3(0f, 0f, 1f), vp, out Vector2 px, out _)) continue;
                            if (px.X < 0 || px.Y < 0 || px.X > vp.X || px.Y > vp.Y) continue;
                            ulong pickedAt = PickUnit(px, out float hitDist);
                            var (o, d) = _window.Camera.ScreenPointToRay(px, _window.FramebufferSize) ?? (Vector3.Zero, Vector3.UnitZ);
                            bool occluded = CommandViewOccluded(o, d, hitDist);
                            var rayHit = _collision?.Raycast(o, d, 200f);
                            hover = $"unit=0x{u.Guid:X} entry={u.Entry} dist={Vector3.Distance(u.Position, me):0.#} px=({px.X:0},{px.Y:0}) " +
                                $"pickedAt=0x{pickedAt:X} hitDist={hitDist:0.#} occluded={occluded} worldHit={(rayHit is { } rh ? rh.Distance.ToString("0.#") : "none")} " +
                                $"canAttack={CanAttack(u)} freeView={_freeView} cursorMode={_window.CursorModeName}";
                            break;
                        }
                        EmitInterface("cv-probe", "hover", "OK", _net?.PlayerGuid ?? 0, hover);
                        Log(true, $"{line} => {hover}");
                        break;
                    }
                    if (p.Length > 1 && p[1] == "loot")
                    {
                        // Nearest dead lootable creature: does the hover pick find it at its own
                        // screen point, what cursor would the law show, and does a loot request go?
                        WorldEntity? corpse = null; float best = float.MaxValue;
                        Vector3 me = _controller?.Position ?? Vector3.Zero;
                        if (_entities.TryGet(LocalPlayerGuid, out WorldEntity selfUnit)) me = selfUnit.Position;
                        foreach (WorldEntity u in _entities.Units)
                        {
                            if (!u.IsCreature || !u.IsDead) continue;
                            float d = Vector3.DistanceSquared(u.Position, me);
                            if (d < best) { best = d; corpse = u; }
                        }
                        string lootProbe;
                        if (corpse is null) lootProbe = "no corpse";
                        else
                        {
                            Vector2 vp = ImGuiNET.ImGui.GetIO().DisplaySize;
                            bool onScreen = _window.Camera.TryProjectToScreen(corpse.Position + new Vector3(0f, 0f, 0.5f), vp, out Vector2 px, out _);
                            ulong pickedAt = onScreen ? PickUnit(px) : 0;
                            bool reach = CommandViewInteractInReach(CommandViewInteractKind.Loot, corpse);
                            string poseInfo = "pose=none";
                            if (_creatures?.TryGetSpellPose(corpse.Guid, out SpellUnitPose cp) == true)
                            {
                                var (o, d) = _window.Camera.ScreenPointToRay(px, _window.FramebufferSize) ?? (Vector3.Zero, Vector3.UnitZ);
                                bool exact = TargetMeshPickLaw.TryPick(cp, o, d, false, out float he);
                                bool infl = TargetMeshPickLaw.TryPick(cp, o, d, true, out float hi);
                                poseInfo = $"pose=ok boundsR={cp.PickBoundsRadius:0.##} boundsC=({cp.PickBoundsCenter.X:0.#},{cp.PickBoundsCenter.Y:0.#},{cp.PickBoundsCenter.Z:0.#}) " +
                                    $"exact={exact}/{he:0.#} inflated={infl}/{hi:0.#} verts={cp.Model?.Vertices.Count}";
                            }
                            lootProbe = poseInfo + " ";
                            lootProbe += $"corpse=0x{corpse.Guid:X} lootable={corpse.Fields.Lootable} dist={MathF.Sqrt(best):0.##} " +
                                $"onScreen={onScreen} px=({px.X:0},{px.Y:0}) pickedAt=0x{pickedAt:X} inReach={reach} " +
                                $"selfPose={TryGetSessionBodyPose(out _)} requested={(corpse.Fields.Lootable && RequestLoot(corpse.Guid))}";
                        }
                        EmitInterface("cv-probe", "loot", "OK", _net?.PlayerGuid ?? 0, lootProbe);
                        Log(true, $"{line} => {lootProbe}");
                        break;
                    }
                    string lockInfo = "";
                    if (_entities.TryGet(RtsPrimaryGuid, out WorldEntity primaryUnit))
                    {
                        Vector3 rel = (_controller?.Position ?? Vector3.Zero) - primaryUnit.Position;
                        float bearing = MathF.Atan2(rel.Y, rel.X) - cam.Yaw;
                        lockInfo = $" locked={CommandViewLocked} unit=({primaryUnit.Position.X:0.##},{primaryUnit.Position.Y:0.##}) " +
                            $"offsetLen={new Vector2(rel.X, rel.Y).Length():0.##} bearing={MathF.Atan2(MathF.Sin(bearing), MathF.Cos(bearing)):0.###}";
                    }
                    string probe = $"freeView={_freeView} scheme={Settings.Controls.CommandViewScheme} " +
                        $"camTarget=({cam.Target.X:0.##},{cam.Target.Y:0.##},{cam.Target.Z:0.##}) smoothing={Settings.Controls.CommandViewSmoothing}{lockInfo} " +
                        $"knobDeg={Settings.Controls.CommandViewPitchDegrees:0.##} pitchLocked={_window.LookPitchLocked} " +
                        $"camYaw={cam.Yaw:0.####} camPitch={cam.Pitch:0.####} camDist={cam.Distance:0.##} " +
                        $"rig=({_controller?.Position.X:0.##},{_controller?.Position.Y:0.##},{_controller?.Position.Z:0.##}) " +
                        $"flying={_controller?.Flying} moveF={_moveForward:0.##} moveS={_moveStrafe:0.##} " +
                        $"guideEnabled={_enableControlGuide}";
                    EmitInterface("cv-probe", "sample", "OK", _net?.PlayerGuid ?? 0, probe);
                    Log(true, $"{line} => {probe}");
                    break;
                }
                case "camera":
                    string[] camArgs = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (_window?.Camera is { } liveCam)
                    {
                        liveCam.OrbitYaw = float.Parse(camArgs[1], CultureInfo.InvariantCulture) * MathF.PI / 180f;
                        if (camArgs.Length > 2) liveCam.Pitch = float.Parse(camArgs[2], CultureInfo.InvariantCulture) * MathF.PI / 180f;
                        if (camArgs.Length > 3) liveCam.Distance = float.Parse(camArgs[3], CultureInfo.InvariantCulture);
                        Log(true, $"{line} orbitYaw={liveCam.OrbitYaw:R} pitch={liveCam.Pitch:R} dist={liveCam.Distance:R}");
                    }
                    else Log(false, $"{line} no camera");
                    break;
                case "particle-census":
                    string censusPrefix = p.Length > 1 ? p[1] : "Spells\\";
                    string census = censusPrefix.StartsWith("spell", StringComparison.OrdinalIgnoreCase)
                        ? $"player=({_controller?.Position.X:0.##},{_controller?.Position.Y:0.##},{_controller?.Position.Z:0.##}) " +
                          (_spellParticles?.CensusReport() ?? "no spell particle system")
                        : _particles?.CensusReport(censusPrefix) ?? "no particle renderer";
                    EmitCombat("ParticleCensus", "diagnostic", 0, census);
                    Log(true, $"{line} => {census}");
                    break;
                case "waitspell":
                    string[] spellWait = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    _liveSpellWaitResult = spellWait[1];
                    _liveSpellWaitTimeout = now + double.Parse(spellWait[2], CultureInfo.InvariantCulture);
                    return;
                case "probe-interface":
                    string[] interfaceProbe = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    _liveInterfaceWaitFamily = interfaceProbe[1];
                    _liveInterfaceWaitStep = interfaceProbe[2];
                    _liveInterfaceWaitTimeout = now + double.Parse(interfaceProbe[3], CultureInfo.InvariantCulture);
                    _liveInterfaceWaitAfter = _verdicts.Snapshot("interface").Count;
                    return;
                case "aura":
                    bool expectedAura = p[1].Equals("present", StringComparison.OrdinalIgnoreCase);
                    uint auraSpell = uint.Parse(p[2], CultureInfo.InvariantCulture);
                    Log(EmitAuraEffectCheck(auraSpell, expectedAura), line);
                    break;
                case "aura-cancel":
                    Log(TryCancelAuraBySpell(uint.Parse(p[1], CultureInfo.InvariantCulture), "PROTOCOL_RUNNER"), line);
                    break;
                case "aura-simulate":
                    string[] auraSim = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Log(auraSim.Length == 7 && SimulateAura(auraSim[1],
                        byte.Parse(auraSim[2], CultureInfo.InvariantCulture),
                        uint.Parse(auraSim[3], CultureInfo.InvariantCulture),
                        byte.Parse(auraSim[4], CultureInfo.InvariantCulture),
                        byte.Parse(auraSim[5], CultureInfo.InvariantCulture),
                        uint.Parse(auraSim[6], CultureInfo.InvariantCulture)), line);
                    break;
                case "spell-blocked":
                    EmitSpellBlocked(_liveLastCastSpell, p[1]);
                    Log(true, line);
                    break;
                case "spell-failure":
                    string[] failure = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    ApplySpellCastFailureResult(
                        uint.Parse(failure[1], CultureInfo.InvariantCulture),
                        Convert.ToByte(failure[2], 16));
                    Log(true, line);
                    break;
                case "spell-animation":
                    string[] animation = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Log(animation.Length == 3 &&
                        PresentSpellAnimation(uint.Parse(animation[1], CultureInfo.InvariantCulture), animation[2], "SYNTHETIC_DBC_RENDERER"), line);
                    break;
                case "spell-effect":
                    string[] effect = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Log(effect.Length == 3 && PresentSpellEffect(
                        uint.Parse(effect[1], CultureInfo.InvariantCulture), effect[2]), line);
                    break;
                case "spell-animation-sample":
                    string[] animationSample = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Log(animationSample.Length == 3 &&
                        SampleSpellAnimation(uint.Parse(animationSample[1], CultureInfo.InvariantCulture),
                            animationSample[2], "RENDERER_POST_TICK"), line);
                    break;
                case "animation-sequence":
                    string[] sequence = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (sequence.Length == 4 && sequence[1].Equals("start", StringComparison.OrdinalIgnoreCase))
                        Log(BeginAnimationSequence(uint.Parse(sequence[2], CultureInfo.InvariantCulture), sequence[3]), line);
                    else if (sequence.Length == 3 && sequence[1].Equals("sample", StringComparison.OrdinalIgnoreCase))
                        Log(SampleAnimationSequence(sequence[2]), line);
                    else if (sequence.Length == 2 && sequence[1].Equals("stop", StringComparison.OrdinalIgnoreCase))
                        Log(EndAnimationSequence(), line);
                    else Log(false, $"unknown {line}");
                    break;
                case "channel-simulate":
                    string[] channelStart = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    uint simulatedSpell = uint.Parse(channelStart[1], CultureInfo.InvariantCulture);
                    uint simulatedDuration = uint.Parse(channelStart[2], CultureInfo.InvariantCulture);
                    BeginChannel(simulatedSpell, simulatedDuration);
                    Log(true, line);
                    break;
                case "channel-update":
                    UpdateChannel(uint.Parse(p[1], CultureInfo.InvariantCulture));
                    Log(true, line);
                    break;
                case "channel-tick":
                    string[] channelTick = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    EmitChannelVerdict("TICK", remainingMs: (uint)Math.Max(0,
                        (_castBarEnds - NowSeconds()) * 1000.0), tickKind: channelTick[1].ToUpperInvariant(),
                        amount: uint.Parse(channelTick[2], CultureInfo.InvariantCulture), source: "SYNTHETIC_WIRE_REPLAY");
                    Log(true, line);
                    break;
                case "trace": if(p[1]=="start") { _combatTraceName=p[2]; StartCombatTrace(); } else StopCombatTrace(); Log(true,line); break;
                case "move-trace": if(p[1]=="start") StartMovementTrace(p[2]); else StopMovementTrace(); Log(true,line); break;
                case "wmo-faces-at":
                {
                    // "wmo-faces-at x,y,z,r" — one comma token, the runner splits on the first spaces only.
                    float[] q = p[1].Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    _wmo?.DumpFacesNear(new Vector3(q[0], q[1], q[2]), q[3]);
                    Log(true, line); break;
                }
                case "wmo-faces":
                    if (_controller is not null)
                    {
                        float r = float.Parse(p[1], CultureInfo.InvariantCulture);
                        _wmo?.DumpFacesNear(_controller.Position, r);
                        _doodads?.DumpInstancesNear(_controller.Position, p.Length > 2 ? float.Parse(p[2], CultureInfo.InvariantCulture) : r * 3f);
                    }
                    Log(true, line); break;
                case "wire-trace":
                    if(p[1]=="start") Log(true,$"{line} path={_wireLog.Start(_config.RepoRoot)}");
                    else { _wireLog.Stop(); Log(true,line); }
                    break;
                case "socket-trace":
                    if(p[1]=="start") { StartSocketTrace(p[2]); Log(true,$"{line} path={_socketTracePath}"); }
                    else { StopSocketTrace(); Log(true,line); }
                    break;
                case "dump": _currentVantage=p[1]; ArmGameplayDump(); Log(true,line); break;
                // A client slash command exactly as chat would submit it ("slash /editui").
                case "chat-self":
                    bool canWhisperSelf = _net is { IsInWorld: true } && !string.IsNullOrWhiteSpace(_net.PlayerName);
                    if (canWhisperSelf) SubmitChatLine($"/w {_net!.PlayerName} {line[9..].Trim()}");
                    Log(canWhisperSelf, line);
                    break;
                case "slash": Log(TrySubmitClientSlashCommand(line[5..].Trim()), line); break;
                // HUD layout editor (PLAN_21): place a registered frame through the live edit
                // session, the same path the selection card's anchor pick + nudge take -
                // "hud-place minimap BottomRight -8 -8". Stands in for a pointer drop.
                case "hud-place":
                {
                    string[] hp = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);   // p caps at 3
                    if (_hudEdit is null || hp.Length < 5 ||
                        !Enum.TryParse(hp[2], ignoreCase: true, out HudAnchor hudAnchor))
                        Log(false, line);
                    else
                    {
                        HudEditApply(HudLayoutState, _hudEdit, hp[1], HudPlacement.At(hudAnchor,
                            float.Parse(hp[3], CultureInfo.InvariantCulture),
                            float.Parse(hp[4], CultureInfo.InvariantCulture)));
                        Log(true, $"{line} context={_hudEdit.Context} layout={HudLayoutState.ActiveLayout}");
                    }
                    break;
                }
                case "ui-parity":
                    ArmUiParityCapture(p[1], stageFixture: false);
                    Log(_uiParityArmed, $"{line} provenance={UiParityProvenance}");
                    break;
                case "ui-parity-stage":
                    ArmUiParityCapture(p[1], stageFixture: true);
                    Log(_uiParityArmed, $"{line} provenance={UiParityProvenance}");
                    break;
                case "ui-parity-assert":
                    string[] captureAssert = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string expectedProvenance = captureAssert.Length > 2 ? captureAssert[2] : "";
                    string expectedScenario = captureAssert.Length > 3
                        ? string.Join(' ', captureAssert.Skip(3)) : "";
                    bool capturePass = _uiParityCompletedPanel.Equals(p[1], StringComparison.Ordinal) &&
                        _uiParityCaptureError.Length == 0 && _uiParityCompletedManifest.Length > 0 &&
                        File.Exists(_uiParityCompletedManifest) && new FileInfo(_uiParityCompletedManifest).Length > 0 &&
                        (expectedProvenance.Length == 0 || _uiParityCompletedProvenance.Equals(
                            expectedProvenance, StringComparison.Ordinal)) &&
                        (expectedScenario.Length == 0 || _uiParityCompletedScenario.Contains(
                            expectedScenario, StringComparison.OrdinalIgnoreCase));
                    EmitInterface("ui-parity", p[1], capturePass ? "PASS" : "FAIL", _net?.PlayerGuid ?? 0,
                        $"completed={_uiParityCompletedPanel};manifest={_uiParityCompletedManifest};" +
                        $"provenance={_uiParityCompletedProvenance};scenario={_uiParityCompletedScenario};" +
                        $"error={_uiParityCaptureError}");
                    Log(capturePass, $"{line} manifest={_uiParityCompletedManifest} " +
                        $"provenance={_uiParityCompletedProvenance} scenario={_uiParityCompletedScenario} " +
                        $"error={_uiParityCaptureError}");
                    break;
                case "enchant-confirm-capture-assert":
                    string[] enchantCaptureAssert = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string enchantCaptureDetail = "";
                    bool enchantCapturePass = enchantCaptureAssert.Length == 3 &&
                        ValidateEnchantConfirmCapture(enchantCaptureAssert[1],
                            enchantCaptureAssert[2], out enchantCaptureDetail);
                    if (enchantCaptureAssert.Length != 3)
                        enchantCaptureDetail =
                            "usage=enchant-confirm-capture-assert <bind|replace> <provenance>";
                    EmitInterface("enchant-confirm", "ui-parity-capture",
                        enchantCapturePass ? "PASS" : "FAIL", _net?.PlayerGuid ?? 0,
                        enchantCaptureDetail);
                    Log(enchantCapturePass, $"{line} {enchantCaptureDetail}");
                    break;
                case "inspect-frame-capture-assert":
                    string[] inspectCaptureAssert = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string inspectCaptureDetail = "";
                    bool inspectCapturePass = inspectCaptureAssert.Length == 2 &&
                        ValidateInspectFrameCapture(inspectCaptureAssert[1], out inspectCaptureDetail);
                    if (inspectCaptureAssert.Length != 2)
                        inspectCaptureDetail =
                            "usage=inspect-frame-capture-assert <observed-runtime-state>";
                    EmitInterface("inspect", "ui-parity-capture",
                        inspectCapturePass ? "PASS" : "FAIL", _inspectGuid,
                        inspectCaptureDetail);
                    Log(inspectCapturePass, $"{line} {inspectCaptureDetail}");
                    break;
                case "skill-frame-capture-assert":
                    string[] skillCaptureAssert = line.Split(' ',
                        StringSplitOptions.RemoveEmptyEntries);
                    string skillCaptureDetail = "";
                    bool skillCapturePass = skillCaptureAssert.Length == 2 &&
                        ValidateSkillFrameCapture(skillCaptureAssert[1], out skillCaptureDetail);
                    if (skillCaptureAssert.Length != 2)
                        skillCaptureDetail =
                            "usage=skill-frame-capture-assert <observed-runtime-state>";
                    EmitInterface("skill-frame", "ui-parity-capture",
                        skillCapturePass ? "PASS" : "FAIL", _net?.PlayerGuid ?? 0,
                        skillCaptureDetail);
                    Log(skillCapturePass, $"{line} {skillCaptureDetail}");
                    break;
                case "bag-containment":
                    Log(ArmBagContainmentCapture(p[1]), line);
                    break;
                case "bag-containment-assert":
                    bool containmentPass = BagContainmentCapturePassed(p[1]);
                    EmitInterface("ui-parity-containment-capture", p[1], containmentPass ? "PASS" : "FAIL",
                        _net?.PlayerGuid ?? 0,
                        $"completed={_bagContainmentCompletedElement};manifest={_bagContainmentCompletedManifest};error={_bagContainmentError}");
                    Log(containmentPass,
                        $"{line} manifest={_bagContainmentCompletedManifest} error={_bagContainmentError}");
                    break;
                case "ui-scale":
                    float requestedUiScale=float.Parse(p[1],CultureInfo.InvariantCulture);
                    if(_skin is null) Log(false,line);
                    else { _skin.Scale=Math.Clamp(requestedUiScale,.5f,4f); Log(true,$"{line} effective={GameplayUiScale():R}"); }
                    break;
                case "press": _liveHeld.Add(NormalizeMovementKey(p[1])); Log(true,line); break;
                case "release": _liveHeld.Remove(NormalizeMovementKey(p[1])); Log(true,line); break;
                case "key":
                    string[] key = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Key inputKey = Key.Unknown;
                    bool parsed = key.Length == 3 && TryParseLiveInputKey(key[2], out inputKey);
                    if (parsed && key[1].Equals("press", StringComparison.OrdinalIgnoreCase))
                    { _liveInputHeld.Add(inputKey); Log(true, $"{line} productionBindingState=down"); }
                    else if (parsed && key[1].Equals("release", StringComparison.OrdinalIgnoreCase))
                    { _liveInputHeld.Remove(inputKey); Log(true, $"{line} productionBindingState=up"); }
                    else Log(false, $"unknown {line}");
                    break;
                case "face":
                    float facing;
                    if (p[1].Equals("selected", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_controller is null || !_entities.TryGet(_selectionGuid, out WorldEntity faceTarget))
                        { Log(false, line + " selected entity unavailable"); break; }
                        Vector3 direction = faceTarget.Position - _controller.Position;
                        if (direction.X == 0f && direction.Y == 0f)
                        { Log(false, line + " selected entity has no horizontal direction"); break; }
                        facing = MathF.Atan2(direction.Y, direction.X);
                    }
                    else facing = float.Parse(p[1], CultureInfo.InvariantCulture);
                    if (_controller is null) Log(false, line);
                    else
                    {
                        _controller.Yaw = facing;
                        _window.Camera.Yaw = facing;
                        _window.Camera.OrbitYaw = 0;
                        Log(true, $"{line} radians={facing:R}");
                    }
                    break;
                case "waitdeath":
                    int deathOrdinal=int.Parse(p[1].Split(':')[^1],CultureInfo.InvariantCulture);
                    ulong deathGuid=LiveSpawnGuid(deathOrdinal);
                    if(deathGuid==0) deathGuid=_selectionGuid;
                    bool dead=_entities.TryGet(deathGuid,out WorldEntity victim)&&victim.IsDead;
                    Log(dead,$"{line} guid=0x{deathGuid:X16} health={(dead?0:victim?.Fields.Health??0)}"); break;
                case "waitgone":
                    int goneOrdinal=int.Parse(p[1].Split(':')[^1],CultureInfo.InvariantCulture);
                    ulong goneGuid=LiveSpawnGuid(goneOrdinal);
                    if(goneGuid==0) goneGuid=_selectionGuid;
                    bool gone=goneGuid!=0&&!_entities.TryGet(goneGuid,out _);
                    Log(gone,$"{line} guid=0x{goneGuid:X16} descriptorPresent={!gone}"); break;
                default: Log(false,$"unknown {line}"); break;
            }
        }
        catch(Exception ex)
        {
            Log(false,$"{line} error={ex.GetType().Name}:{ex.Message}");
            if (line.StartsWith("raidqa ", StringComparison.OrdinalIgnoreCase))
            {
                RaidQaFail($"{line}: {ex.GetType().Name}: {ex.Message}");
                return;
            }
        }
        _liveStep++;
    }

    private IEnumerable<string> VerdictLines()=>_verdicts.SnapshotAll().Select(v=>$"[{v.Channel}] {v.ToLine()}");
    private double _liveMarkTime;
    private bool _liveWaitNewOnly;
    private IEnumerable<string> VerdictLinesSinceMark() => VerdictLines().Where(x =>
    {
        int at = x.IndexOf("time=", StringComparison.Ordinal);
        if (at < 0) return false;
        int end = x.IndexOf(' ', at);
        return double.TryParse(end < 0 ? x[(at + 5)..] : x[(at + 5)..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && t >= _liveMarkTime;
    });

    private ulong LiveSpawnGuid(int ordinal)
    {
        // SpawnObserved is the authoritative identity assertion. Resolve from
        // the verdict ring as well as the cache so a descriptor that lands at a
        // protocol-step boundary cannot be lost to ordering between refreshes.
        ulong[] observed=_verdicts.Snapshot("combat").OfType<CombatVerdict>()
            .Where(x=>x.Event=="SpawnObserved").Select(x=>x.TargetGuid).Distinct().ToArray();
        if(ordinal>0&&ordinal<=observed.Length) return observed[ordinal-1];
        return ordinal>0&&ordinal<=_liveSpawnGuids.Count?_liveSpawnGuids[ordinal-1]:0;
    }
    private ulong LiveNearestRemotePlayerGuid()
    {
        if (_controller is null) return 0;
        ulong self = _net?.PlayerGuid ?? 0;
        return _entities.Units
            .Where(x => x.IsPlayer && x.Guid != self)
            .OrderBy(x => Vector3.Distance(x.Position, _controller.Position)).ThenBy(x => x.Guid)
            .FirstOrDefault()?.Guid ?? 0;
    }

    private ulong LiveWildGuid(int ordinal)
    {
        if(_controller is null||ordinal<=0) return 0;
        var candidates=_entities.Units
            .Where(x=>x.IsCreature&&!_liveSpawnGuids.Contains(x.Guid))
            .Select(x=>(Unit:x,Distance:Vector3.Distance(x.Position,_controller.Position)))
            .OrderBy(x=>x.Unit.Guid).ToList();
        if(ordinal>candidates.Count) return 0;
        var selected=candidates[ordinal-1];
        EmitCombat("WildObserved","pre-existing-object-store",selected.Unit.Guid,
            $"entry={selected.Unit.Fields.Entry};distance={selected.Distance:R};"+
            $"faction={selected.Unit.Fields.FactionTemplate};flags=0x{selected.Unit.Fields.UnitFlags:X8};position="+
            $"{selected.Unit.Position.X:R}|{selected.Unit.Position.Y:R}|{selected.Unit.Position.Z:R}");
        return selected.Unit.Guid;
    }
    private ulong LiveWildEntryGuid(int entry)
    {
        if(_controller is null||entry<=0) return 0;
        WorldEntity? selected=_entities.Units
            .Where(x=>x.IsCreature&&!_liveSpawnGuids.Contains(x.Guid)&&x.Entry==(uint)entry)
            .OrderBy(x=>x.Guid).FirstOrDefault();
        if(selected is null) return 0;
        float distance=Vector3.Distance(selected.Position,_controller.Position);
        EmitCombat("WildObserved","pre-existing-object-store-entry-control",selected.Guid,
            $"entry={selected.Fields.Entry};distance={distance:R};faction={selected.Fields.FactionTemplate};"+
            $"flags=0x{selected.Fields.UnitFlags:X8};position="+
            $"{selected.Position.X:R}|{selected.Position.Y:R}|{selected.Position.Z:R}");
        return selected.Guid;
    }
    private ulong LiveWildEntryNearestGuid(int entry)
    {
        if(_controller is null||entry<=0) return 0;
        WorldEntity? selected=_entities.Units
            .Where(x=>x.IsCreature&&!_liveSpawnGuids.Contains(x.Guid)&&!x.IsDead&&x.Entry==(uint)entry)
            .OrderBy(x=>Vector3.Distance(x.Position,_controller.Position)).ThenBy(x=>x.Guid).FirstOrDefault();
        if(selected is null) return 0;
        float distance=Vector3.Distance(selected.Position,_controller.Position);
        EmitCombat("WildObserved","pre-existing-object-store-entry-nearest",selected.Guid,
            $"entry={selected.Fields.Entry};distance={distance:R};faction={selected.Fields.FactionTemplate};"+
            $"flags=0x{selected.Fields.UnitFlags:X8};position="+
            $"{selected.Position.X:R}|{selected.Position.Y:R}|{selected.Position.Z:R}");
        return selected.Guid;
    }
    private ulong LiveWildHostileGuid(int ordinal)
    {
        if(_controller is null||ordinal<=0) return 0;
        var candidates=_entities.Units
            .Where(x=>x.IsCreature&&!_liveSpawnGuids.Contains(x.Guid)&&!x.IsDead&&
                x.Fields.UnitFlags==0&&ReactionPlayerToward(x)==FactionReaction.Hostile)
            .Select(x=>(Unit:x,Distance:Vector3.Distance(x.Position,_controller.Position)))
            .OrderBy(x=>x.Distance).ThenBy(x=>x.Unit.Guid).ToList();
        if(ordinal>candidates.Count) return 0;
        var selected=candidates[ordinal-1];
        EmitCombat("WildObserved","pre-existing-object-store-hostile",selected.Unit.Guid,
            $"entry={selected.Unit.Fields.Entry};distance={selected.Distance:R};faction={selected.Unit.Fields.FactionTemplate};"+
            $"reaction=Hostile;flags=0x{selected.Unit.Fields.UnitFlags:X8};position="+
            $"{selected.Unit.Position.X:R}|{selected.Unit.Position.Y:R}|{selected.Unit.Position.Z:R}");
        return selected.Unit.Guid;
    }

    private void CloseLiveRunPanels()
    {
        _characterOpen = _spellbookOpen = _questLogOpen = _socialOpen = _worldMapOpen =
            _helpOpen = _keybindingsOpen = _macroOpen = _guildOpen = _auctionOpen = _mailOpen =
            _professionOpen = _talentOpen = _tradeOpen = _bankOpen = false;
    }

    private bool OpenLiveGameMenu()
    {
        OpenSettings();
        return _settingsOpen;
    }

    private bool LiveEquipBag(uint entry, int container)
    {
        if (container is < 1 or > 4 || _net is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        if (FindCarriedEntry(player, entry) is not { } found) return false;
        if (found.Container == container)
        {
            EmitInterface("inventory", "equip-bag", "REFUSED", found.Guid,
                $"entry={entry};container={container};reason=source-inside-destination;use=inventory stage-bag {entry}");
            return false;
        }
        int equipmentSlot = 18 + container;
        ulong destination = player.Fields.PlayerInventorySlot(equipmentSlot);
        PickupOrPlaceItem(found.Container, found.Slot, found.Guid, ignoreModifiers: true);
        PickupOrPlaceItem(InventoryUiLaw.EquipmentContainer, equipmentSlot, destination,
            ignoreModifiers: true);
        return !HasCarriedItem;
    }

    private (int Container, int Slot, ulong Guid)? FindCarriedEntry(WorldEntity player, uint entry)
    {
        for (int slot = 0; slot < InventoryUiLaw.BackpackSlots; slot++)
        {
            ulong guid = player.Fields.PlayerBackpackSlot(slot);
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
                return (0, slot, guid);
        }
        for (int bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            ulong bagGuid = player.Fields.PlayerInventorySlot(19 + bagIndex);
            if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bag)) continue;
            int slots = (int)Math.Min(bag.Fields.ContainerNumSlots, InventoryUiLaw.MaxContainerSlots);
            for (int slot = 0; slot < slots; slot++)
            {
                ulong guid = bag.Fields.ContainerSlot(slot);
                if (guid == 0 || !_entities.TryGet(guid, out WorldEntity item) || item.Entry != entry) continue;
                return (bagIndex + 1, slot, guid);
            }
        }
        return null;
    }

    private bool LiveStageBag(uint entry)
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player) ||
            FindCarriedEntry(player, entry) is not { } found) return false;
        if (found.Container == 0) return true;
        int empty = Enumerable.Range(0, InventoryUiLaw.BackpackSlots)
            .FirstOrDefault(slot => player.Fields.PlayerBackpackSlot(slot) == 0, -1);
        if (empty < 0)
        {
            EmitInterface("inventory", "stage-bag", "REFUSED", found.Guid,
                $"entry={entry};sourceContainer={found.Container};reason=backpack-full");
            return false;
        }
        PickupOrPlaceItem(found.Container, found.Slot, found.Guid, ignoreModifiers: true);
        PickupOrPlaceItem(0, empty, 0, ignoreModifiers: true);
        return !HasCarriedItem;
    }

    private bool LiveRequireBag(int container, uint? expectedEntry, int minimumSlots)
    {
        if (container is < 1 or > 4 || _net is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player))
        {
            EmitInterface("inventory", "require-bag", "FAIL", 0,
                $"container={container};expectedEntry={expectedEntry};minimumSlots={minimumSlots};reason=player-unavailable");
            return false;
        }
        ulong guid = player.Fields.PlayerInventorySlot(18 + container);
        WorldEntity? bag = null;
        bool found = guid != 0 && _entities.TryGet(guid, out bag);
        uint entry = bag?.Entry ?? 0;
        uint actualSlots = bag?.Fields.ContainerNumSlots ?? 0;
        bool pass = found && (!expectedEntry.HasValue || entry == expectedEntry.Value) && actualSlots >= minimumSlots;
        EmitInterface("inventory", "require-bag", pass ? "PASS" : "FAIL", guid,
            $"container={container};entry={entry};expectedEntry={expectedEntry};actualSlots={actualSlots};minimumSlots={minimumSlots}");
        return pass;
    }

    private bool LiveInspectBags()
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        var details = new List<string>();
        for (int container = 1; container <= 4; container++)
        {
            ulong guid = player.Fields.PlayerInventorySlot(18 + container);
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity bag))
                details.Add($"container{container}=entry:{bag.Entry}|slots:{bag.Fields.ContainerNumSlots}|guid:0x{guid:X16}");
            else
                details.Add($"container{container}=empty");
        }
        EmitInterface("inventory", "inspect-bags", "OBSERVED", player.Guid, string.Join(';', details));
        return true;
    }

    private bool OpenLiveCharacter() { _characterOpen = true; _paperDollDirty = true; return true; }
    private bool OpenLiveSpellbook() { _spellbookLine = 0; _spellbookPage = 0; _spellbookOpen = true; return true; }
    private bool OpenLiveSocial() { OpenSocial(); return true; }
    private bool OpenLiveWho() { OpenSocial(); _socialPage = 1; _net?.Who(""); return true; }
    private bool OpenLiveHelp() { OpenHelp(); return true; }
    private bool OpenLiveBindings() { OpenKeybindings(); return true; }
    private bool OpenLiveMacros() { OpenMacros(); return true; }
    private bool OpenLiveGuild() { SimulateGuildFlow(); return true; }
    private bool OpenLiveAuction() { SimulateAuctionFlow(); return true; }
    private bool OpenLiveMail() { SimulateMailList(); return true; }
    private bool OpenLiveProfession()
    {
        SimulateProfessionFlow();
        return OpenProfessionNamed("Alchemy") || OpenFirstProfession();
    }
    private bool OpenLiveTalent() { SimulateTalentRoster(); return true; }
    private bool OpenLiveTrainer() { SimulateTrainerList(); return true; }
    private bool OpenLiveTaxi() { SimulateTaxiFlow(); _taxiOpen = true; return true; }
    private ulong LiveNpcFlagNearestGuid(string flagName)
    {
        if(_controller is null) return 0;
        uint flag=flagName.ToLowerInvariant() switch
        {
            "vendor" => NpcVendor,
            "trainer" => NpcTrainer,
            "quest" or "questgiver" => NpcQuestGiver,
            "flightmaster" => NpcFlightMaster,
            "innkeeper" => NpcInnkeeper,
            "banker" => NpcBanker,
            "auctioneer" => NpcAuctioneer,
            "tabard" or "tabarddesigner" => NpcTabardDesigner,
            _ => 0,
        };
        if(flag==0) return 0;
        WorldEntity? selected=_entities.Units
            .Where(x=>x.IsCreature&&!x.IsDead&&(x.NpcFlags&flag)!=0)
            .OrderBy(x=>Vector3.Distance(x.Position,_controller.Position)).ThenBy(x=>x.Guid)
            .FirstOrDefault();
        if(selected is null) return 0;
        float distance=Vector3.Distance(selected.Position,_controller.Position);
        EmitInterface("gossip","npc-flag-observed","PASS",selected.Guid,
            $"class={flagName.ToLowerInvariant()};entry={selected.Entry};npcFlags=0x{selected.NpcFlags:X8};distance={distance:R}");
        return selected.Guid;
    }
    private bool SelectLiveObservedGuid(ulong guid)
    {
        if (guid == 0 || !_entities.TryGet(guid, out _)) return false;
        CommitSelection(guid, false);
        return true;
    }

    private ulong LiveEntryNearestGuid(int entry, bool dead = false)
    {
        if (_controller is null || entry <= 0) return 0;
        WorldEntity? selected = _entities.Units.Where(x => x.IsCreature && x.IsDead == dead && x.Entry == (uint)entry)
            .OrderBy(x => Vector3.Distance(x.Position, _controller.Position)).ThenBy(x => x.Guid).FirstOrDefault();
        if (selected is null) return 0;
        float distance = Vector3.Distance(selected.Position, _controller.Position);
        EmitCombat("EntryObserved", "object-store-entry-nearest", selected.Guid,
            $"entry={entry};distance={distance:R};position={selected.Position.X:R}|{selected.Position.Y:R}|{selected.Position.Z:R}");
        return selected.Guid;
    }
    private ulong LiveObjectEntryNearestGuid(int entry)
    {
        if (_controller is null || entry <= 0) return 0;
        WorldEntity? selected = _entities.Entities.Values.Where(x => x.IsGameObject && x.Entry == (uint)entry)
            .OrderBy(x => Vector3.Distance(x.Position, _controller.Position)).ThenBy(x => x.Guid).FirstOrDefault();
        if (selected is null) return 0;
        float distance = Vector3.Distance(selected.Position, _controller.Position);
        EmitInterface("mail", "mailbox-observed", "PASS", selected.Guid,
            $"entry={entry};distance={distance:R};position={selected.Position.X:R}|{selected.Position.Y:R}|{selected.Position.Z:R}");
        return selected.Guid;
    }
    private ulong LiveObjectTypeNearestGuid(int type)
    {
        if (_controller is null || type < 0) return 0;
        WorldEntity? selected = _entities.Entities.Values.Where(x => x.IsGameObject && x.GameObjectType == (uint)type)
            .OrderBy(x => Vector3.Distance(x.Position, _controller.Position)).ThenBy(x => x.Guid).FirstOrDefault();
        if (selected is null) return 0;
        float distance = Vector3.Distance(selected.Position, _controller.Position);
        EmitInterface("gameobject", "type-observed", "PASS", selected.Guid,
            $"entry={selected.Entry};type={type};kind={GameObjectKind((uint)type)};distance={distance:R};position={selected.Position.X:R}|{selected.Position.Y:R}|{selected.Position.Z:R}");
        return selected.Guid;
    }
    private ulong LiveMailboxNearestGuid()
    {
        if (_controller is null) return 0;
        WorldEntity? selected = _entities.Entities.Values.Where(x => x.IsGameObject && x.GameObjectType == 19)
            .OrderBy(x => Vector3.Distance(x.Position, _controller.Position)).ThenBy(x => x.Guid).FirstOrDefault();
        if (selected is null) return 0;
        float distance = Vector3.Distance(selected.Position, _controller.Position);
        EmitInterface("mail", "mailbox-observed", "PASS", selected.Guid,
            $"entry={selected.Entry};type={selected.GameObjectType};distance={distance:R};position={selected.Position.X:R}|{selected.Position.Y:R}|{selected.Position.Z:R}");
        return selected.Guid;
    }
    private void RefreshLiveSpawnIdentities()
    {
        if (_liveSpawnBefore is null || _controller is null) return;
        var appeared=_entities.Units
            .Where(x=>x.IsCreature&&!_liveSpawnBefore.Contains(x.Guid))
            .Select(x=>(Unit:x,Distance:Vector3.Distance(x.Position,_controller.Position)))
            .OrderBy(x=>x.Distance).ThenBy(x=>x.Unit.Guid).ToList();
        foreach(var candidate in appeared)
        {
            if(_liveSpawnGuids.Contains(candidate.Unit.Guid)) continue;
            _liveSpawnGuids.Add(candidate.Unit.Guid);
            EmitCombat("SpawnObserved","post-command-entity-delta",candidate.Unit.Guid,
                $"entry={candidate.Unit.Fields.Entry};distance={candidate.Distance:R};"+
                $"within3={(candidate.Distance<=3f).ToString().ToLowerInvariant()};position="+
                $"{candidate.Unit.Position.X:R}|{candidate.Unit.Position.Y:R}|{candidate.Unit.Position.Z:R}");
        }
        if(appeared.Count>0) _liveSpawnBefore=null;
    }

    private static bool IsQuestProtocolOpcode(ushort opcode) => opcode is
        (ushort)Op.CMSG_QUEST_QUERY or
        (ushort)Op.CMSG_QUESTGIVER_STATUS_QUERY or
        (ushort)Op.CMSG_QUESTGIVER_HELLO or
        (ushort)Op.CMSG_QUESTGIVER_QUERY_QUEST or
        (ushort)Op.CMSG_QUESTGIVER_ACCEPT_QUEST or
        (ushort)Op.CMSG_QUESTGIVER_COMPLETE_QUEST or
        (ushort)Op.CMSG_QUESTGIVER_REQUEST_REWARD or
        (ushort)Op.CMSG_QUESTGIVER_CHOOSE_REWARD or
        (ushort)Op.CMSG_QUESTLOG_REMOVE_QUEST;

    private static bool TryQuestWireSpec(string name, out ushort opcode, out int payloadSize)
    {
        (Op Op, int Size)? spec = name.ToLowerInvariant() switch
        {
            "template-query" => (Op.CMSG_QUEST_QUERY, 4),
            "status" => (Op.CMSG_QUESTGIVER_STATUS_QUERY, 8),
            "hello" => (Op.CMSG_QUESTGIVER_HELLO, 8),
            "query" => (Op.CMSG_QUESTGIVER_QUERY_QUEST, 12),
            "accept" => (Op.CMSG_QUESTGIVER_ACCEPT_QUEST, 12),
            "complete" => (Op.CMSG_QUESTGIVER_COMPLETE_QUEST, 12),
            "request-reward" => (Op.CMSG_QUESTGIVER_REQUEST_REWARD, 12),
            "choose" => (Op.CMSG_QUESTGIVER_CHOOSE_REWARD, 16),
            "abandon" => (Op.CMSG_QUESTLOG_REMOVE_QUEST, 1),
            _ => null,
        };
        opcode = spec is null ? (ushort)0 : (ushort)spec.Value.Op;
        payloadSize = spec?.Size ?? 0;
        return spec is not null;
    }

    private void Log(bool pass,string text)
    {
        RefreshLiveSpawnIdentities();
        string entry=$"{_liveStep+1},{(pass?"PASS":"FAIL")},{text}"; _liveLog.Add(entry); Console.WriteLine($"[protocol] {entry}");
    }

    private void FinishProtocol()
    {
        StopCombatTrace(); StopMovementTrace(); StopSocketTrace(); _wireLog.Stop(); _liveHeld.Clear(); _liveInputHeld.Clear();
        string dir=Path.GetFullPath(Path.IsPathRooted(_liveRunOptions!.OutputDirectory)?_liveRunOptions.OutputDirectory:Path.Combine(_config.RepoRoot,_liveRunOptions.OutputDirectory));
        Directory.CreateDirectory(dir);
        string log=Path.Combine(dir,$"runner-{_liveStamp}.csv"), verdict=Path.Combine(dir,$"verdicts-{_liveStamp}.txt");
        File.WriteAllLines(log,new[]{"step,result,detail"}.Concat(_liveLog)); File.WriteAllLines(verdict,VerdictLines());
        string spellCsv=Path.Combine(dir,$"spell-sweep-{_liveStamp}.csv");
        WriteSpellSweepCsv(spellCsv);
        string castBarCsv=Path.Combine(dir,$"cast-bar-{_liveStamp}.csv");
        WriteCastBarCsv(castBarCsv);
        string spellAnimationCsv=Path.Combine(dir,$"spell-animation-{_liveStamp}.csv");
        WriteSpellAnimationCsv(spellAnimationCsv);
        string spellAnimationSequenceCsv=Path.Combine(dir,$"spell-animation-sequence-{_liveStamp}.csv");
        WriteSpellAnimationSequenceCsv(spellAnimationSequenceCsv);
        string spellChannelCsv=Path.Combine(dir,$"spell-channel-{_liveStamp}.csv");
        WriteSpellChannelCsv(spellChannelCsv);
        string spellAuraCsv=Path.Combine(dir,$"spell-aura-{_liveStamp}.csv");
        WriteSpellAuraCsv(spellAuraCsv);
        string spellErrorCsv=Path.Combine(dir,$"spell-error-{_liveStamp}.csv");
        WriteSpellErrorCsv(spellErrorCsv);
        string soundCsv=Path.Combine(dir,$"sound-journal-{_liveStamp}.csv");
        WriteSoundJournalCsv(soundCsv);
        int failures=_liveLog.Count(x=>x.Contains(",FAIL,"));
        Console.WriteLine($"[live-run] PROTOCOL_DONE failures={failures}; log={log}; verdicts={verdict}; spells={spellCsv}; castbar={castBarCsv}; animations={spellAnimationCsv}; channels={spellChannelCsv}; auras={spellAuraCsv}; errors={spellErrorCsv}; sounds={soundCsv}");
        LiveRunExitCode=failures==0?0:1;
        _quitRequested = true;
    }

    private static bool TryParseLiveInputKey(string value, out Key key)
    {
        string normalized = value.Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);
        if (normalized.Equals("LEFTSHIFT", StringComparison.OrdinalIgnoreCase)) normalized = nameof(Key.ShiftLeft);
        if (normalized.Equals("RIGHTSHIFT", StringComparison.OrdinalIgnoreCase)) normalized = nameof(Key.ShiftRight);
        return Enum.TryParse(normalized, ignoreCase: true, out key) && key != Key.Unknown;
    }

    private bool ValidateSkillFrameCapture(string expectedProvenance, out string detail)
    {
        var failures = new List<string>();
        if (!expectedProvenance.Equals("observed-runtime-state", StringComparison.Ordinal))
            failures.Add("provenance-must-be-observed-runtime-state");
        if (!_uiParityCompletedPanel.Equals("skill-frame", StringComparison.Ordinal))
            failures.Add($"completed-panel={_uiParityCompletedPanel}");
        if (!_uiParityCompletedProvenance.Equals(expectedProvenance, StringComparison.Ordinal))
            failures.Add($"provenance={_uiParityCompletedProvenance}");
        if (_uiParityCaptureError.Length > 0) failures.Add($"capture-error={_uiParityCaptureError}");
        if (_uiParityCompletedManifest.Length == 0 || !File.Exists(_uiParityCompletedManifest))
        {
            failures.Add("manifest-missing");
            detail = string.Join('|', failures);
            return false;
        }

        int rows = 0, instrumented = 0, notDrawn = 0, blankCoverage = -1;
        string csvPath = "";
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(_uiParityCompletedManifest));
            JsonElement root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 3)
                failures.Add("schema-version");
            if (!root.GetProperty("panel").GetString()!.Equals(
                    "skill-frame", StringComparison.Ordinal)) failures.Add("manifest-panel");
            if (!root.GetProperty("provenance").GetString()!.Equals(
                    expectedProvenance, StringComparison.Ordinal))
                failures.Add("manifest-provenance");
            if (!root.GetProperty("captureCommand").GetString()!.Equals(
                    "ui-parity", StringComparison.Ordinal)) failures.Add("capture-command");
            if (!root.GetProperty("sameRenderedFrame").GetBoolean())
                failures.Add("csv-png-frame-not-shared");
            rows = root.GetProperty("rows").GetInt32();
            instrumented = root.GetProperty("instrumentedRows").GetInt32();
            notDrawn = root.GetProperty("notDrawnRows").GetInt32();
            blankCoverage = root.GetProperty("blankCoverageRows").GetInt32();
            if (rows < 55 || instrumented < 35 || rows != instrumented + notDrawn)
                failures.Add($"row-census={rows}/{instrumented}/{notDrawn}");
            if (blankCoverage != 0) failures.Add($"blank-coverage={blankCoverage}");

            JsonElement scenario = root.GetProperty("scenario");
            if (!scenario.GetProperty("stateSource").GetString()!.Equals(
                    "player-skill-fields", StringComparison.Ordinal))
                failures.Add("state-source");
            if (scenario.GetProperty("captureStateMutation").GetBoolean() ||
                scenario.GetProperty("captureNetworkMutation").GetBoolean())
                failures.Add("capture-mutation-contract");
            if (!scenario.GetProperty("characterOpen").GetBoolean() ||
                scenario.GetProperty("characterTab").GetInt32() != SkillFrameUiLaw.SkillsTab ||
                scenario.GetProperty("skillsTab").GetInt32() != SkillFrameUiLaw.SkillsTab)
                failures.Add("skills-pane-not-open");
            if (scenario.GetProperty("skillCount").GetInt32() <= 0 ||
                scenario.GetProperty("headerCount").GetInt32() <= 0 ||
                scenario.GetProperty("rowCount").GetInt32() <= 0)
                failures.Add("authoritative-skill-rows-empty");
            if (scenario.GetProperty("visibleRows").GetInt32() !=
                    SkillFrameUiLaw.VisibleRows ||
                scenario.GetProperty("rowHitWidth").GetDouble() !=
                    SkillFrameUiLaw.SkillRowHitWidth ||
                scenario.GetProperty("rowHitHeight").GetDouble() !=
                    SkillFrameUiLaw.SkillRowHitHeight ||
                scenario.GetProperty("dividerTop").GetDouble() !=
                    SkillFrameUiLaw.DividerLeftRect.Y)
                failures.Add("list-geometry-contract");
            if (!scenario.GetProperty("directBindingCommand").GetString()!.Equals(
                    SkillFrameUiLaw.BindingCommand, StringComparison.Ordinal) ||
                !scenario.GetProperty("directBindingLabel").GetString()!.Equals(
                    SkillFrameUiLaw.BindingLabel, StringComparison.Ordinal))
                failures.Add("direct-binding-contract");
            if (!scenario.GetProperty("unlearnOpcode").GetString()!.Equals(
                    "0x0202", StringComparison.Ordinal) ||
                !scenario.GetProperty("authoritativeMutation").GetString()!.Equals(
                    "PLAYER_SKILL_INFO-update-only", StringComparison.Ordinal))
                failures.Add("unlearn-wire-boundary");
            if (scenario.GetProperty("popupPresent").GetBoolean() &&
                (!scenario.GetProperty("popupMatchesSelection").GetBoolean() ||
                 !scenario.GetProperty("selectedAbandonable").GetBoolean()))
                failures.Add("popup-selection-gate");

            string manifestDirectory = Path.GetDirectoryName(_uiParityCompletedManifest)!;
            foreach (JsonElement file in root.GetProperty("files").EnumerateArray())
            {
                string name = file.GetProperty("path").GetString() ?? "";
                string path = Path.Combine(manifestDirectory, name);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    failures.Add($"capture-file={name}");
                if (name.EndsWith("-actual.csv", StringComparison.Ordinal)) csvPath = path;
            }
        }
        catch (Exception ex)
        {
            failures.Add($"manifest-read={ex.GetType().Name}");
        }

        if (csvPath.Length == 0 || !File.Exists(csvPath)) failures.Add("csv-missing");
        else
        {
            string csv = File.ReadAllText(csvPath);
            string[] requiredElements =
            [
                "CharacterFrame", "BenillaSkillFrame",
                "BenillaSkillFrame/BackgroundTopLeft",
                "BenillaSkillFrame/BackgroundTopRight",
                "BenillaSkillFrame/BackgroundBottomLeft",
                "BenillaSkillFrame/BackgroundBottomRight",
                "BenillaSkillExpandButtonFrame", "BenillaSkillExpandTabLeft",
                "BenillaSkillExpandTabMiddle", "BenillaSkillExpandTabRight",
                "BenillaSkillCollapseAllButton", "BenillaSkillWheelCatcher",
                "BenillaSkillListScrollFrame", "BenillaSkillListScrollFrameScrollBar",
                "BenillaSkillListScrollFrameScrollBarScrollUpButton",
                "BenillaSkillListScrollFrameScrollBarScrollDownButton",
                "BenillaSkillListScrollFrameScrollBarThumbTexture",
                "BenillaSkillTypeLabel1", "BenillaSkillRankFrame1",
                "BenillaSkillHorizontalBarLeft", "BenillaSkillHorizontalBarRight",
                "BenillaSkillDetailBar", "BenillaSkillDetailUnlearnButton", "StaticPopup1"
            ];
            foreach (string element in requiredElements)
                if (!csv.Contains($"\"{element}\"", StringComparison.Ordinal))
                    failures.Add($"element={element}");
            if (!csv.Contains("\"281\",\"32\"", StringComparison.Ordinal))
                failures.Add("281x32-row-hit-census");
            if (csv.Contains("DRAWN-NOT-INSTRUMENTED", StringComparison.Ordinal))
                failures.Add("drawn-not-instrumented");
            if (csv.Contains("UNMEASURED", StringComparison.Ordinal))
                failures.Add("unmeasured-interaction");
            if (csv.Contains("MISSING:", StringComparison.OrdinalIgnoreCase))
                failures.Add("missing-asset");
        }

        detail = $"manifest={_uiParityCompletedManifest};provenance={expectedProvenance};" +
                 $"rows={rows};instrumented={instrumented};notDrawn={notDrawn};" +
                 $"blankCoverage={blankCoverage};" +
                 $"failures={(failures.Count == 0 ? "none" : string.Join('|', failures))}";
        return failures.Count == 0;
    }

    private bool ValidateInspectFrameCapture(string expectedProvenance, out string detail)
    {
        var failures = new List<string>();
        if (!expectedProvenance.Equals("observed-runtime-state", StringComparison.Ordinal))
            failures.Add("provenance-must-be-observed-runtime-state");
        if (!_uiParityCompletedPanel.Equals("inspect-frame", StringComparison.Ordinal))
            failures.Add($"completed-panel={_uiParityCompletedPanel}");
        if (!_uiParityCompletedProvenance.Equals(expectedProvenance, StringComparison.Ordinal))
            failures.Add($"provenance={_uiParityCompletedProvenance}");
        if (_uiParityCaptureError.Length > 0) failures.Add($"capture-error={_uiParityCaptureError}");
        if (_uiParityCompletedManifest.Length == 0 || !File.Exists(_uiParityCompletedManifest))
        {
            failures.Add("manifest-missing");
            detail = string.Join('|', failures);
            return false;
        }

        int rows = 0, instrumented = 0, notDrawn = 0, blankCoverage = -1;
        string csvPath = "";
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(_uiParityCompletedManifest));
            JsonElement root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 3) failures.Add("schema-version");
            if (!root.GetProperty("panel").GetString()!.Equals(
                    "inspect-frame", StringComparison.Ordinal)) failures.Add("manifest-panel");
            if (!root.GetProperty("provenance").GetString()!.Equals(
                    expectedProvenance, StringComparison.Ordinal)) failures.Add("manifest-provenance");
            if (!root.GetProperty("captureCommand").GetString()!.Equals(
                    "ui-parity", StringComparison.Ordinal)) failures.Add("capture-command");
            if (!root.GetProperty("sameRenderedFrame").GetBoolean())
                failures.Add("csv-png-frame-not-shared");
            rows = root.GetProperty("rows").GetInt32();
            instrumented = root.GetProperty("instrumentedRows").GetInt32();
            notDrawn = root.GetProperty("notDrawnRows").GetInt32();
            blankCoverage = root.GetProperty("blankCoverageRows").GetInt32();
            if (rows < 150 || instrumented < 75 || rows != instrumented + notDrawn)
                failures.Add($"row-census={rows}/{instrumented}/{notDrawn}");
            if (blankCoverage != 0) failures.Add($"blank-coverage={blankCoverage}");

            JsonElement scenario = root.GetProperty("scenario");
            if (!scenario.GetProperty("stateSource").GetString()!.Equals(
                    "inspect-runtime", StringComparison.Ordinal)) failures.Add("state-source");
            if (scenario.GetProperty("captureStateMutation").GetBoolean() ||
                scenario.GetProperty("captureNetworkMutation").GetBoolean())
                failures.Add("capture-mutation-contract");
            if (!scenario.GetProperty("inspectOpen").GetBoolean() ||
                scenario.GetProperty("inspectedGuid").GetString() == "0x0000000000000000")
                failures.Add("inspect-not-open");
            if (scenario.GetProperty("frameWidth").GetInt32() != 384 ||
                scenario.GetProperty("frameHeight").GetInt32() != 512 ||
                scenario.GetProperty("slotCount").GetInt32() != 19)
                failures.Add("frame-or-slot-geometry");
            if (scenario.GetProperty("selectedTabEnabled").GetBoolean())
                failures.Add("selected-tab-enabled");
            if (!scenario.GetProperty("portraitAperture").GetString()!.Equals(
                    "authored-background-overlay", StringComparison.Ordinal) ||
                !scenario.GetProperty("slotTooltipAnchor").GetString()!.Equals(
                    "ANCHOR_RIGHT", StringComparison.Ordinal))
                failures.Add("containment-or-tooltip-anchor");
            if (!scenario.GetProperty("equipmentDataSource").GetString()!.Equals(
                    "PLAYER_VISIBLE_ITEM", StringComparison.Ordinal) ||
                scenario.GetProperty("privateItemFieldsRead").GetBoolean())
                failures.Add("public-equipment-boundary");
            if (!scenario.GetProperty("modelUsable").GetBoolean())
                failures.Add("paper-doll-model-unavailable");
            if (scenario.GetProperty("hoveredSlots").GetInt32() != 0)
                failures.Add("slot-hover-obscures-baseline");

            string manifestDirectory = Path.GetDirectoryName(_uiParityCompletedManifest)!;
            foreach (JsonElement file in root.GetProperty("files").EnumerateArray())
            {
                string name = file.GetProperty("path").GetString() ?? "";
                string path = Path.Combine(manifestDirectory, name);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    failures.Add($"capture-file={name}");
                if (name.EndsWith("-actual.csv", StringComparison.Ordinal)) csvPath = path;
            }
        }
        catch (Exception ex)
        {
            failures.Add($"manifest-read={ex.GetType().Name}");
        }

        if (csvPath.Length == 0 || !File.Exists(csvPath)) failures.Add("actual-csv-missing");
        else
        {
            string csv = File.ReadAllText(csvPath);
            string[] requiredElements =
            [
                "InspectFrame", "InspectPaperDollFrame", "InspectFramePortrait",
                "InspectPaperDollFrame/Texture", "InspectPaperDollFrame/Texture#2",
                "InspectPaperDollFrame/Texture#3", "InspectPaperDollFrame/Texture#4",
                "InspectNameText", "InspectLevelText", "InspectModel",
                "InspectModelRotateLeftButton", "InspectModelRotateRightButton",
                "InspectFrameTab1", "InspectFrameTab1/LeftTexture",
                "InspectFrameTab1/MiddleTexture", "InspectFrameTab1/RightTexture",
                "InspectFrameCloseButton", "InspectFrameCloseButton/NormalTexture",
            ];
            string[] slots =
            [
                "InspectHeadSlot", "InspectNeckSlot", "InspectShoulderSlot", "InspectBackSlot",
                "InspectChestSlot", "InspectShirtSlot", "InspectTabardSlot", "InspectWristSlot",
                "InspectHandsSlot", "InspectWaistSlot", "InspectLegsSlot", "InspectFeetSlot",
                "InspectFinger0Slot", "InspectFinger1Slot", "InspectTrinket0Slot",
                "InspectTrinket1Slot", "InspectMainHandSlot", "InspectSecondaryHandSlot",
                "InspectRangedSlot",
            ];
            foreach (string element in requiredElements)
                if (!csv.Contains($"\"{element}\"", StringComparison.Ordinal))
                    failures.Add($"element={element}");
            foreach (string slot in slots)
                foreach (string suffix in new[] { "", "IconTexture", "NormalTexture" })
                {
                    string element = slot + suffix;
                    if (!csv.Contains($"\"{element}\"", StringComparison.Ordinal))
                        failures.Add($"element={element}");
                }
            if (csv.Contains("DRAWN-NOT-INSTRUMENTED", StringComparison.Ordinal))
                failures.Add("drawn-not-instrumented");
            if (csv.Contains("UNMEASURED", StringComparison.Ordinal))
                failures.Add("unmeasured-interaction");
            if (csv.Contains("MISSING:", StringComparison.OrdinalIgnoreCase))
                failures.Add("missing-asset");
        }

        detail = $"manifest={_uiParityCompletedManifest};provenance={expectedProvenance};" +
                 $"rows={rows};instrumented={instrumented};notDrawn={notDrawn};" +
                 $"blankCoverage={blankCoverage};" +
                 $"failures={(failures.Count == 0 ? "none" : string.Join('|', failures))}";
        return failures.Count == 0;
    }

    private bool ValidateEnchantConfirmCapture(string expectedState, string expectedProvenance,
        out string detail)
    {
        var failures = new List<string>();
        expectedState = expectedState.ToLowerInvariant();
        if (expectedState is not ("bind" or "replace")) failures.Add("invalid-expected-state");
        if (!_uiParityCompletedPanel.Equals("enchant-confirm", StringComparison.Ordinal))
            failures.Add($"completed-panel={_uiParityCompletedPanel}");
        if (!_uiParityCompletedProvenance.Equals(expectedProvenance, StringComparison.Ordinal))
            failures.Add($"provenance={_uiParityCompletedProvenance}");
        if (_uiParityCaptureError.Length > 0) failures.Add($"capture-error={_uiParityCaptureError}");
        if (_uiParityCompletedManifest.Length == 0 || !File.Exists(_uiParityCompletedManifest))
        {
            failures.Add("manifest-missing");
            detail = string.Join('|', failures);
            return false;
        }

        int rows = 0, instrumented = 0, notDrawn = 0, blankCoverage = -1;
        string csvPath = "";
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(_uiParityCompletedManifest));
            JsonElement root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 3) failures.Add("schema-version");
            if (!root.GetProperty("panel").GetString()!.Equals(
                    "enchant-confirm", StringComparison.Ordinal)) failures.Add("manifest-panel");
            if (!root.GetProperty("provenance").GetString()!.Equals(
                    expectedProvenance, StringComparison.Ordinal)) failures.Add("manifest-provenance");
            bool fixture = expectedProvenance.Equals(
                "explicit-ui-parity-fixture", StringComparison.Ordinal);
            string expectedCommand = fixture ? "ui-parity-stage" : "ui-parity";
            if (!root.GetProperty("captureCommand").GetString()!.Equals(
                    expectedCommand, StringComparison.Ordinal)) failures.Add("capture-command");
            if (!root.GetProperty("sameRenderedFrame").GetBoolean())
                failures.Add("csv-png-frame-not-shared");
            rows = root.GetProperty("rows").GetInt32();
            instrumented = root.GetProperty("instrumentedRows").GetInt32();
            notDrawn = root.GetProperty("notDrawnRows").GetInt32();
            blankCoverage = root.GetProperty("blankCoverageRows").GetInt32();
            if (rows < 12 || instrumented < 10 || rows != instrumented + notDrawn)
                failures.Add($"row-census={rows}/{instrumented}/{notDrawn}");
            if (blankCoverage != 0) failures.Add($"blank-coverage={blankCoverage}");

            JsonElement scenario = root.GetProperty("scenario");
            if (!scenario.GetProperty("capturedState").GetString()!.Equals(
                    expectedState, StringComparison.Ordinal)) failures.Add("captured-state");
            if (fixture && !scenario.GetProperty("requestedState").GetString()!.Equals(
                    expectedState, StringComparison.Ordinal)) failures.Add("requested-state");
            string expectedSource = fixture ? "ui-parity-stage" : "item-target-runtime";
            if (!scenario.GetProperty("stateSource").GetString()!.Equals(
                    expectedSource, StringComparison.Ordinal)) failures.Add("state-source");
            if (scenario.GetProperty("captureStateMutation").GetBoolean() ||
                scenario.GetProperty("captureNetworkMutation").GetBoolean())
                failures.Add("capture-mutation-contract");
            if (scenario.GetProperty("buttonsInteractive").GetBoolean() == fixture)
                failures.Add("fixture-button-interactivity");
            if (!scenario.GetProperty("alertIconVisible").GetBoolean() ||
                scenario.GetProperty("frameWidth").GetDouble() != EnchantConfirmUiLaw.FrameWidth ||
                scenario.GetProperty("frameHeight").GetDouble() != EnchantConfirmUiLaw.FrameHeight ||
                !scenario.GetProperty("layoutProfile").GetString()!.Equals(
                    "benilla-staticpopup-alert-420", StringComparison.Ordinal))
                failures.Add("benilla-layout-contract");

            string manifestDirectory = Path.GetDirectoryName(_uiParityCompletedManifest)!;
            foreach (JsonElement file in root.GetProperty("files").EnumerateArray())
            {
                string name = file.GetProperty("path").GetString() ?? "";
                string path = Path.Combine(manifestDirectory, name);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    failures.Add($"capture-file={name}");
                if (name.EndsWith("-actual.csv", StringComparison.Ordinal)) csvPath = path;
            }
        }
        catch (Exception ex)
        {
            failures.Add($"manifest-read={ex.GetType().Name}");
        }

        if (csvPath.Length == 0 || !File.Exists(csvPath)) failures.Add("actual-csv-missing");
        else
        {
            string csv = File.ReadAllText(csvPath);
            string[] requiredElements =
            [
                "StaticPopup1", "StaticPopup1/BackdropBackground",
                "StaticPopup1/BackdropBorder", "StaticPopup1AlertIcon",
                "StaticPopup1Text1", "StaticPopup1Button1",
                "StaticPopup1Button1/NormalTexture", "StaticPopup1Button1/Text",
                "StaticPopup1Button2", "StaticPopup1Button2/NormalTexture",
                "StaticPopup1Button2/Text",
            ];
            foreach (string element in requiredElements)
                if (!csv.Contains($"\"{element}\"", StringComparison.Ordinal))
                    failures.Add($"element={element}");
            if (csv.Contains("DRAWN-NOT-INSTRUMENTED", StringComparison.Ordinal))
                failures.Add("drawn-not-instrumented");
            if (csv.Contains("UNMEASURED", StringComparison.Ordinal))
                failures.Add("unmeasured-interaction");
            if (csv.Contains("MISSING:", StringComparison.OrdinalIgnoreCase))
                failures.Add("missing-asset");
        }

        detail = $"manifest={_uiParityCompletedManifest};state={expectedState};" +
                 $"provenance={expectedProvenance};rows={rows};instrumented={instrumented};" +
                 $"notDrawn={notDrawn};blankCoverage={blankCoverage};" +
                 $"failures={(failures.Count == 0 ? "none" : string.Join('|', failures))}";
        return failures.Count == 0;
    }

    private void WriteSoundJournalCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "sequence,time,category,requested_cue,resolved_id,resolved_path,owner,looping,track_hold"
        };
        foreach (World.Sound.AudioMixer.SoundPlayJournalEntry e in (_spellSounds?.JournalSnapshot() ?? [])
                     .Where(x => x.Sequence > _liveSoundProtocolStartSequence))
            lines.Add(string.Join(',', e.Sequence, e.TimeSeconds.ToString("F3", CultureInfo.InvariantCulture),
                Csv(e.Category), Csv(e.RequestedCue), e.SoundId, Csv(e.ResolvedPath),
                $"0x{e.Owner:X16}", e.Looping ? "true" : "false", e.TrackHold ? "true" : "false"));
        File.WriteAllLines(path, lines);
    }

    private void WriteSpellSweepCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "time,character,class_id,spell_id,name,school,cast_type,result_enum,animation_state,effect_check,target_type,gcd_ready,cooldown_ready,resource_type,resource_before,resource_cost,resolved_guid,sent"
        };
        foreach (SpellSweepVerdict v in _verdicts.Snapshot("spell-sweep").OfType<SpellSweepVerdict>())
            lines.Add(string.Join(',', v.Time.ToString("F3", CultureInfo.InvariantCulture), Csv(v.Character),
                v.ClassId, v.SpellId, Csv(v.SpellName), v.School, v.CastType, v.Result,
                Csv(v.AnimationState), Csv(v.EffectCheck), v.TargetType, v.GcdReady, v.CooldownReady,
                v.ResourceType, v.ResourceBefore, v.ResourceCost, $"0x{v.ResolvedGuid:X16}", v.Sent));
        File.WriteAllLines(path, lines);
    }

    private void WriteCastBarCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "time,character,spell_id,name,event,classification,server_duration_ms,dbc_cast_time_ms,dbc_duration_ms,phase,started,ends,pushback_total_ms,cancel_source,animation_state"
        };
        foreach (CastBarVerdict v in _verdicts.Snapshot("cast-bar").OfType<CastBarVerdict>())
            lines.Add(string.Join(',', v.Time.ToString("F3", CultureInfo.InvariantCulture), Csv(v.Character),
                v.SpellId, Csv(v.SpellName), v.Event, v.Classification, v.ServerDurationMs,
                v.DbcCastTimeMs, v.DbcDurationMs, v.Phase,
                v.StartedAt.ToString("F3", CultureInfo.InvariantCulture),
                v.EndsAt.ToString("F3", CultureInfo.InvariantCulture), v.PushbackTotalMs,
                v.CancelSource, Csv(v.AnimationState)));
        File.WriteAllLines(path, lines);
    }

    private void WriteSpellAnimationCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "time,character,spell_id,name,school,stage,kit_id,authored_animation_id,requested_animation_id,played_animation_id,resolution,renderer_state,moving,legal_while_moving,movement_interrupts,base_animation,previous_base_animation,action_animation,hold_animation,blend_weight,source"
        };
        foreach (SpellAnimationVerdict v in _verdicts.Snapshot("spell-animation").OfType<SpellAnimationVerdict>())
            lines.Add(string.Join(',', v.Time.ToString("F3", CultureInfo.InvariantCulture),
                Csv(v.Character), v.SpellId, Csv(v.SpellName), v.School, v.Stage, v.KitId,
                v.AuthoredAnimationId, v.RequestedAnimationId, v.PlayedAnimationId,
                v.Resolution, Csv(v.RendererState), v.Moving, v.LegalWhileMoving,
                v.MovementInterrupts, Csv(v.BaseAnimation), Csv(v.PreviousBaseAnimation),
                Csv(v.ActionAnimation), Csv(v.HoldAnimation),
                v.BlendWeight.ToString("F4", CultureInfo.InvariantCulture), v.Source));
        File.WriteAllLines(path, lines);
    }

    private void WriteSpellAnimationSequenceCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "time,character,spell_id,cell,row_kind,coverage,sample_index,frame,actual_stage,expected_animation_id,requested_animation_id,played_animation_id,resolution,renderer_state,base_animation,previous_base_animation,action_animation,hold_animation,blend_weight,moving,player_health,player_power,selection_health,player_x,player_y,player_z,unit_count,player_auras,selection_auras,inventory_fingerprint,health_changed,target_health_changed,position_changed,unit_count_changed,aura_changed,inventory_changed,power_changed,precast_visual,cast_visual,missile_visual,impact_visual,visual_instances,spell_visual_verdict,active_models,asset_sources,caster_animation_verdict,blend_verdict,gm_mode,source"
        };
        foreach (SpellAnimationSequenceVerdict v in _verdicts.Snapshot("spell-animation-sequence").OfType<SpellAnimationSequenceVerdict>())
            lines.Add(string.Join(',', v.Time.ToString("F3", CultureInfo.InvariantCulture), Csv(v.Character),
                v.SpellId, v.Cell, v.RowKind, v.Coverage, v.SampleIndex, Csv(v.Frame), v.ActualStage,
                v.ExpectedAnimationId, v.RequestedAnimationId, v.PlayedAnimationId, v.Resolution,
                Csv(v.RendererState), Csv(v.BaseAnimation), Csv(v.PreviousBaseAnimation),
                Csv(v.ActionAnimation), Csv(v.HoldAnimation), v.BlendWeight.ToString("F4", CultureInfo.InvariantCulture),
                v.Moving, v.PlayerHealth, v.PlayerPower, v.SelectionHealth,
                v.PlayerX.ToString("F4", CultureInfo.InvariantCulture), v.PlayerY.ToString("F4", CultureInfo.InvariantCulture),
                v.PlayerZ.ToString("F4", CultureInfo.InvariantCulture), v.UnitCount, Csv(v.PlayerAuras),
                Csv(v.SelectionAuras), Csv(v.InventoryFingerprint), v.HealthChanged, v.TargetHealthChanged,
                v.PositionChanged, v.UnitCountChanged, v.AuraChanged, v.InventoryChanged, v.PowerChanged,
                v.PrecastVisual, v.CastVisual, v.MissileVisual, v.ImpactVisual, Csv(v.VisualInstances),
                v.SpellVisualVerdict,
                Csv(v.ActiveModels), Csv(v.AssetSources), v.AnimationVerdict, v.BlendVerdict,
                v.GmMode, v.Source));
        File.WriteAllLines(path, lines);
    }

    private void WriteSpellChannelCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "time,character,spell_id,name,event,duration_ms,remaining_ms,tick_index,tick_delta_ms,tick_kind,amount,moving,animation_state,source"
        };
        foreach (ChannelSpellVerdict v in _verdicts.Snapshot("spell-channel").OfType<ChannelSpellVerdict>())
            lines.Add(string.Join(',', v.Time.ToString("F3", CultureInfo.InvariantCulture),
                Csv(v.Character), v.SpellId, Csv(v.SpellName), v.Event, v.DurationMs,
                v.RemainingMs, v.TickIndex, v.TickDeltaMs.ToString("F3", CultureInfo.InvariantCulture),
                v.TickKind, v.Amount, v.Moving, Csv(v.AnimationState), v.Source));
        File.WriteAllLines(path, lines);
    }

    private void WriteSpellAuraCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "time,character,unit_guid,slot,spell_id,name,event,helpful,cancelable,stacks,duration_ms,remaining_ms,display,source"
        };
        foreach (AuraVerdict v in _verdicts.Snapshot("spell-aura").OfType<AuraVerdict>())
            lines.Add(string.Join(',', v.Time.ToString("F3", CultureInfo.InvariantCulture),
                Csv(v.Character), $"0x{v.UnitGuid:X16}", v.Slot, v.SpellId, Csv(v.SpellName), v.Event,
                v.Helpful, v.Cancelable, v.Stacks, v.DurationMs, v.RemainingMs, v.Display, v.Source));
        File.WriteAllLines(path, lines);
    }

    private void WriteSpellErrorCsv(string path)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        var lines = new List<string>
        {
            "time,character,spell_id,name,reason,display_text,displayed,source"
        };
        foreach (SpellErrorVerdict v in _verdicts.Snapshot("spell-error").OfType<SpellErrorVerdict>())
            lines.Add(string.Join(',', v.Time.ToString("F3", CultureInfo.InvariantCulture),
                Csv(v.Character), v.SpellId, Csv(v.SpellName), v.Reason,
                Csv(v.DisplayText), v.Displayed, v.Source));
        File.WriteAllLines(path, lines);
    }

    private void FinishLiveBootstrap(string result, string detail)
    {
        if (_raidQaStage is CommanderRaidAttemptStage.Fighting or CommanderRaidAttemptStage.PullPending)
        {
            _raidQaStage = CommanderRaidAttemptStage.Failed;
            WriteRaidQaEvent("failed", new { reason = "Live observation ended", result, detail });
        }
        if (_liveSteps is not null && result != "READY")
        {
            Log(false, $"run interrupted: {result};{detail};nextStep={_liveStep}");
            FinishProtocol(); // Preserve completed evidence even when the socket or timeout ends the run.
        }
        string dir=Path.GetFullPath(Path.IsPathRooted(_liveRunOptions!.OutputDirectory)
            ? _liveRunOptions.OutputDirectory : Path.Combine(_config.RepoRoot,_liveRunOptions.OutputDirectory));
        Directory.CreateDirectory(dir);
        string stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture);
        string path=Path.Combine(dir,$"bootstrap-{stamp}.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new { result,detail,
            account=_config.Server.Account,character=_config.Server.Character,realm=_config.Server.Realm,
            elapsed=_liveRunElapsed },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"[live-run] {result}: {detail}; artifact={path}");
        LiveRunExitCode=result=="READY"?0:1;
        _quitRequested = true;
    }
}
