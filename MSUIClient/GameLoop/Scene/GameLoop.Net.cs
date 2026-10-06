using System.Numerics;
using System.Diagnostics;
using System.Text;
using ImGuiNET;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;   // WowSkin (glue login chrome)
using MSUIClient.Net;
using MSUIClient.World;
using MSUIClient.World.Portals;
using MSUIClient.World.Units;
using MSUIClient.Formats;   // AreaTableCatalog (roster zone names) + AdtTerrainReader

namespace MSUIClient;

// Phase 2 networking glue for the game loop. Hooks in Program.cs:
//   Load()    -> InitNet(gl);  and BeginWorldLoad(gl) only runs when OFFLINE
//   Update()  -> PumpNet(dt);  then a guard that skips gameplay until the world loads
//   Gui()     -> NetHud();     login screen -> character select -> in-world status
//   Dispose() -> _net?.Dispose();
//
// The glue flow mirrors the real client (and benilla): Login screen -> Character
// Select (roster, pick a character) -> World. Networked mode stays STATELESS until
// SMSG_LOGIN_VERIFY_WORLD assigns a character + spawn point; only then does
// BeginWorldLoad run. Offline mode is unchanged.
public sealed partial class GameLoop
{
    private const int NetDrainPacketBudget = 256;
    private const double NetDrainTimeBudgetMs = 2.0;
    private const int NetDrainHighWaterMark = 1_024;
    private const int NetDrainCatchUpPacketBudget = 4_096;
    private const double NetDrainCatchUpTimeBudgetMs = 12.0;

    private NetworkClient? _net;
    private readonly WireRing _wire = new();
    private readonly WireLogRecorder _wireLog = new();
    private readonly EntityStore _entities = new();   // game-thread-owned world model (from UPDATE_OBJECT)
    private readonly CreatureLifecycleTracker _creatureLifecycle = new();
    private readonly CombatState _combat = new();
    private readonly LocalMovementSender _movementSender = new();
    private GL? _gl;                                   // kept so we can start the world load after login
    private GlueScene? _glue;                          // the login-screen glue scene (UI_MainMenu)
    private GlueBooth? _booth;                         // the character-select per-race booth (UI_<Race>)
    private CreatureRenderer? _creatures;              // draws streamed creatures and remote players (UPDATE_OBJECT)
    private WorldNameRenderer? _worldNames;
    private SpellEffectSource? _spellEffects;
    private QuestMarkerModelSource? _questMarkerModels;
    private SpellEffectMeshRenderer? _spellEffectMeshes;
    private SpellRibbonRenderer? _spellRibbons;
    private SpellChainBeamSource? _spellChainBeams;
    private SpellChainBeamRenderer? _spellChainBeamRenderer;
    private FishingLineRenderer? _fishingLineRenderer;
    private World.Spells.SpellParticleSystem? _spellParticles;
    // The audio device and the SoundEntries policy over it are SHARED: spell audio
    // and the world soundscape are two callers of one mixer, not one system with a
    // hole punched in it for the other.
    private World.Sound.AudioMixer? _audioMixer;
    private World.Sound.SoundKitLibrary? _soundKits;
    private World.Sound.LiquidAmbientLoopSystem? _liquidAmbient;
    private World.Spells.SpellSoundSystem? _spellSounds;
    private CreatureVoiceCatalog? _creatureVoices;
    private EmoteCatalog? _emotes;
    private EmoteTextSoundCatalog? _emoteTextSounds;
    private readonly Dictionary<ulong, long> _creatureHostileVoices = [];
    private bool _worldLoadStarted;                   // false until BeginWorldLoad has run (offline or on login)
    private int _worldEntryTransitionStage;           // 1=booth avatar, 2=HUD prime, 3=begin load, 4=adopt; never in network pump
    private EnterWorldInfo? _queuedWorldEntry;        // captured at the ordered inbound NEW_WORLD boundary
    private bool _worldportAckPending;                // only NEW_WORLD owns this; LOGIN_VERIFY_WORLD never does
    private uint _pendingWorldportMapId;
    private TransferPendingPacket? _pendingTransfer;
    private long _netInbound;
    private int _netUpdatesLastFrame;
    private int _creaturesLogged;
    private Task<ObjectUpdateBuffer>? _pendingObjectParse;
    private ObjectUpdateBuffer? _pendingObjectUpdates;
    // Three retained 4K chunks cover the observed login burst without ever
    // creating a contiguous >85-KB reference array.
    private ObjectUpdateBuffer _objectUpdateBuffer = new(12_000);
    private int _pendingObjectUpdateIndex;
    private long _pendingObjectReceivedStamp;

    // Login-screen input buffers. A launch configuration may explicitly persist
    // its password locally; otherwise this buffer remains session-only.
    private readonly byte[] _acctBuf = new byte[64];
    private readonly byte[] _passBuf = new byte[128];
    private bool _loginInit;
    private bool _rememberAccount = true;   // the "Remember Account Name" checkbox

    // Character-select selection.
    private int _selectedChar;
    private bool _charSelectionRestored;

    // AreaTable.dbc (zone names for the roster rows), loaded once on first character-select draw.
    private AreaTableCatalog? _areas;
    private bool _areasLoaded;
    private FactionTemplateCatalog? _factions;

    /// <summary>Create the network client (does not connect). Called at the end of Load(). Stores gl for the deferred world load.</summary>
    private void InitNet(GL gl)
    {
        _gl = gl;
        Engine.GlDebugOutput.EnableIfRequested(gl);

        EnsureLoginProfilesInitialized();
        ApplyActiveLoginProfiles(applyLaunchMode: true);

        // The Launch Options choice is the user's declared intent and wins over the
        // legacy server.enabled master switch. Before this, picking "SuperUI Client
        // Mode" saved the sticky LaunchMode but a server.enabled=false config kept
        // the serverless front door and never built the network client — the only
        // escape was editing client-config.json (and its bin-dir copy) by hand.
        // Fresh installs (empty LaunchMode) keep the offline-viewer default.
        if (!_config.Server.Enabled && Settings.LaunchMode == LaunchModeClient && !BatchInstrumentActive)
        {
            _config.Server.Enabled = true;
            Console.WriteLine("[net] server.enabled=false overridden by Launch Options (LaunchMode=Client)");
        }

        bool creator = CreatorLaunchActive;
        if (!_config.Server.Enabled && !GlueFrontDoorActive)
        {
            Console.WriteLine("[net] disabled (server.enabled = false) - offline batch mode");
            return;
        }

        // Live mode: do NOT render the offline debug character (the base HumanMale in Battlegear).
        // benilla shows no character at the login screen; the selected character appears only at
        // character-select, and the real in-world player model comes from the roster on login.
        if (_character is not null) _character.Enabled = false;

        // The login-screen glue scene (UI_MainMenu burning gate). Best-effort; draws only if it loads.
        try { if (_mpq is not null) _glue = new GlueScene(gl, _mpq, _config); }
        catch (Exception ex) { Console.WriteLine($"[glue] init failed: {ex.Message}"); }

        // The character-select per-race booth (UI_<Race> backgrounds, fog off). Best-effort; the
        // scene itself loads lazily on the first SetRace once we reach character select.
        try
        {
            if (_mpq is not null)
                _booth = new GlueBooth(gl, _mpq, _config, _assetWorkers, _uploads);
        }
        catch (Exception ex) { Console.WriteLine($"[booth] init failed: {ex.Message}"); }

        // The networked unit renderer. Loads the creature DBCs; draws streamed NPCs and
        // remote players as M2s once we are in world. Best-effort.
        try
        {
            if (_mpq is not null)
            {
                _creatures = new CreatureRenderer(
                    gl, _mpq, _config, _creatureLifecycle, _assetWorkers, _uploads);
                _creatures.AnimationResolved = CaptureAnimationChoice;
                _creatures.InteriorLightFor = _interiorUnitLight.For;
                _creatures.TuningFor = MountTuningFor;
                _creatures.EmoteAnimResolver = ResolveEmoteAnim;
                _creatures.TypeFlagsFor = entry =>
                    _creatureQueryRecords.TryGetValue(entry, out CreatureQueryInfo? info)
                        ? info?.TypeFlags : null;
            }
        }
        catch (Exception ce) { Console.WriteLine($"[creature] init failed: {ce.Message}"); }

        InitPortraits(gl);
        InitGameplayUi(gl);
        try { _minimapInteriorComposite = new InteriorMinimapComposite(gl); }
        catch (Exception ex) { Console.WriteLine($"[minimap] interior composite unavailable: {ex.Message}"); }
        try { _worldNames = new WorldNameRenderer(gl); }
        catch (Exception ex) { Console.WriteLine($"[names] world name renderer unavailable: {ex.Message}"); }
        try { _fishingLineRenderer = new FishingLineRenderer(gl); }
        catch (Exception ex) { Console.WriteLine($"[fishing-line] renderer unavailable: {ex.Message}"); }
        if (_mpq is not null)
        {
            _spellEffects = new SpellEffectSource(_mpq);
            if (_spellVisualCatalog is not null)
                _spellChainBeams = new SpellChainBeamSource(_spellVisualCatalog);
            _questMarkerModels = new QuestMarkerModelSource(_mpq);
            _emotes = EmoteCatalog.Load(_mpq);
            _emoteTextSounds = EmoteTextSoundCatalog.Load(_mpq);
            // MSUI_AUDIO_OFF=1 builds no audio at all: no mixer, no worker thread,
            // no waveOut device, no decode. A BISECT SWITCH, so "the audio rewrite
            // changed something visual" can be answered by running rather than by
            // arguing about whether a mechanism exists.
            if (Environment.GetEnvironmentVariable("MSUI_AUDIO_OFF") == "1")
            {
                Console.WriteLine("[audio] MSUI_AUDIO_OFF=1 - audio subsystem not created");
            }
            else
            {
                _audioMixer = new World.Sound.AudioMixer(_mpq);
                _soundKits = new World.Sound.SoundKitLibrary(_mpq);
                _spellSounds = new World.Spells.SpellSoundSystem(_audioMixer, _soundKits);
                _liquidAmbient = new World.Sound.LiquidAmbientLoopSystem(
                    _audioMixer, _soundKits, _mpq);
                _creatureVoices = CreatureVoiceCatalog.Load(_mpq);
                _npcGreetings = NpcGreetingCatalog.Load(_mpq);
                _gameObjectSounds = GameObjectSoundCatalog.Load(_mpq);

                // THE PERSISTED MIX HAS TO BE PUSHED HERE, because this is the first moment a
                // mixer exists to receive it. InitSettings runs before InitNet and calls
                // ApplySettings -> ApplyAudioSettings, which quietly early-returns on the null
                // mixer; nothing re-applied it afterwards, so the client booted on AudioMixer's
                // own field initialisers until the player happened to touch a settings widget.
                // Invisible while those initialisers matched the shipped defaults, and plainly
                // wrong the moment anyone saved "music off" and relaunched: the sliders read zero
                // while the mixer played at 0.4. Reported as "the music comes back on its own".
                ApplyAudioSettings(Settings);
            }
            WireFootstepPlayback();
            WireCreatureAnimationVoices();
            WireMeleeSounds();
            _spellEffects.AnimationSoundEvent = (sound, unit, position) =>
                PlaySpellSoundAt(unit, sound, position, forceLoop: false, trackHold: false);
            try
            {
                _spellEffectMeshes = new SpellEffectMeshRenderer(gl, _mpq);
                string fxShaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
                if (!File.Exists(Path.Combine(fxShaderDir, "attached.vert")))
                    fxShaderDir = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
                _spellEffectMeshes.LoadShaders(fxShaderDir);
                _spellRibbons = new SpellRibbonRenderer(gl, _mpq);
                _spellRibbons.LoadShaders();
                _spellChainBeamRenderer = new SpellChainBeamRenderer(gl, _mpq);
                _spellChainBeamRenderer.LoadShaders();
                string spellShaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
                if (!File.Exists(Path.Combine(spellShaderDir, "spell_particle.vert")))
                    spellShaderDir = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
                _spellParticles = new World.Spells.SpellParticleSystem(gl, _config, _mpq);
                _spellParticles.LoadShaders(spellShaderDir);
            }
            catch (Exception ex)
            {
                _spellEffectMeshes?.Dispose(); _spellEffectMeshes = null;
                _spellRibbons?.Dispose(); _spellRibbons = null;
                _spellChainBeamRenderer?.Dispose(); _spellChainBeamRenderer = null;
                _spellParticles?.Dispose(); _spellParticles = null;
                Console.WriteLine($"[spell-fx] mesh renderer unavailable: {ex.Message}");
            }
        }
        InitInventory();
        InitCharacterPage();

        try
        {
            byte[]? bytes = _mpq?.ReadFile(FactionTemplateCatalog.MpqPath);
            _factions = bytes is null ? null : FactionTemplateCatalog.Parse(bytes);
            Console.WriteLine(_factions is null
                ? "[target] FactionTemplate.dbc unavailable - neutral fallback"
                : $"[target] FactionTemplate.dbc loaded ({_factions.Count} rows)");
        }
        catch (Exception ex) { Console.WriteLine($"[target] faction init failed: {ex.Message}"); }

        // No server configured: the front door + presentation stack above is all an
        // interactive session needs - never construct the network client. (With a
        // server configured the client is still created so the login screen works
        // if the user switches modes, but auto-login is suppressed for creator.)
        if (!_config.Server.Enabled)
        {
            Console.WriteLine("[creator] no server configured - front door up, network skipped");
            return;
        }

        EnsureNetworkClient(suppressAutoLogin: creator);
    }

    /// <summary>
    /// Construct the network client (does not connect the world; auto-login only when
    /// configured and not suppressed). Safe to call again: a live client is kept. Also
    /// the runtime half of the Launch Options switch — picking "SuperUI Client Mode"
    /// on a serverless boot builds the client here so the switch works without a
    /// restart or a config edit.
    /// </summary>
    private void EnsureNetworkClient(bool suppressAutoLogin)
    {
        if (_net is not null) return;
        try
        {
            NetSettings netSettings = _config.ToNetSettings();
            if (_liveRunOptions?.CharacterSelect == true)
                netSettings = netSettings with { CharacterName = null };
            else if (!string.IsNullOrWhiteSpace(_liveRunOptions?.Character))
            {
                netSettings = netSettings with { CharacterName = _liveRunOptions.Character };
                Console.WriteLine($"[live-run] selecting requested character {_liveRunOptions.Character}");
            }

            _net = new NetworkClient(netSettings, CaptureWirePacket,
                _config.DevTools ? ObserveSocketWrite : null);
            _net.CombatSendObserved = ObserveCombatSend;
            // A World Builder script run (MSUI_WB_SCRIPT) is offline + web app only: logging the account
            // in would kick any live session on it (a live protocol run died this way, 2026-09-27).
            if (!suppressAutoLogin && WbScriptPath is null && _config.Server.AutoConnect &&
                !string.IsNullOrWhiteSpace(_config.Server.Account) &&
                !string.IsNullOrWhiteSpace(_config.Server.Password))
            {
                _net.Login(_config.Server.Account, _config.Server.Password);
                Console.WriteLine($"[net] auto-login as {_config.Server.Account}");
            }
            else
            {
                Console.WriteLine("[net] ready - log in via the in-client login screen");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[net] init failed: {ex.Message}");
            _net = null;
        }
    }

    /// <summary>Pump the network client once per frame. Called near the top of Update(dt), before the world-load guard.</summary>
    private void PumpNet(float dt)
    {
        // BeginWorldLoad can be entered from this method while handling the login
        // result.  Packets later in that same pre-load drain are not evidence that
        // the loading loop pumps the network, so only count a drain whose frame
        // entered PumpNet with the curtain already active.
        bool loadActiveAtPumpEntry = _worldLoading;
        _wireLog.Pump();
        if (_net is not { } net) return;
        if (net.State is NetState.Failed or NetState.Disconnected)
        {
            SaveCameraPoseForSession(forgetIdentity: true);
            ResetViewSubject();
            ResetVanillaClientControl();
            CancelRealPortalHandoff("world connection closed");
            // A dead session is terminal to packet application. Its queue can
            // contain a valid world boundary followed by destination updates,
            // but there is no socket left to ACK or establish their authority.
            if (_worldportAckPending)
                AbortPendingWorldportAdoption("world connection closed before destination adoption");
            else
                net.Stop();
            DiscardPendingNetApplicationState();
            ResetQuestSession(clearStatusStore: true);
            _tutorialFlags?.ResetSession();
            ResetPetActionBar();
            ResetPetInfoRefresh();
            ResetVendor();
            // Frozen group-session clear feeds PARTY_INVITE_CANCEL before the retained UI feed
            // drops the roster. ResetParty is idempotent after the first edge; a decline attempt
            // may be rejected by the already-closed socket and is not claimed as a successful wire.
            ResetParty();
            _forcedReactions.Clear();
            // No socket means no possession and no free view: the server's forced-release
            // ACK can never arrive to end them, so the client has to drop both itself.
            ResetSuiControl();
            ResetCommanderState();
            ResetPlayerIdentitySession();
            ResetWeatherSession();
            ResetBattlefieldSession();
            ResetReputationSession();
            UpdatePartyInviteLifecycle();
            return;
        }
        // StaticPopup's monotonic two-slot Advance runs even when WorldMap or another full-screen
        // owner suppresses DrawCombatHud. Keep slot time in the always-pumped lifecycle;
        // DrawPartyInvite only presents the current PARTY_INVITE instance.
        UpdatePartyInviteLifecycle();

        // Surface any character-create result (the create request runs while parked at select).
        if (_net.TryTakeCreateResult(out byte ccCode)) OnCreateResult(ccCode);

        // The server has assigned our character + spawn point. This is populated only when
        // the ordered inbound drain reaches LOGIN_VERIFY_WORLD / NEW_WORLD below. An
        // out-of-band notification here could overtake and discard the first destination
        // UPDATE_OBJECT packet.
        if (_queuedWorldEntry is { } enter && _controller is not null)
        {
            _queuedWorldEntry = null;
            LoadCameraPoseForWorldEntry();
            TransferPendingPacket? announcedTransfer = _pendingTransfer;
            _pendingTransfer = null;
            // Capture this BEFORE committing the authoritative destination below.
            // Comparing after `_config.Start.Map = enter.Map` makes every transfer
            // look same-map and leaves the old map's terrain/WMO/collision resident
            // at the destination coordinates.
            int previousMapId = _config.Start.Map;
            bool changingMaps = previousMapId != (int)enter.Map;

            // Resolve the destination identity before clearing the active
            // stores. ACKing with the old MapName is worse than refusing the
            // transfer: the server would publish destination objects while the
            // client renders unrelated terrain at the new coordinates.
            MapRow? destinationMap = null;
            if (changingMaps)
            {
                EnsureInstanceData();
                destinationMap = _maps?.Get((int)enter.Map);
                WdtFile? destinationWdt = _mapWdts?.GetValueOrDefault((int)enter.Map);
                if (destinationMap is null || destinationWdt is null || _adts is null)
                {
                    string reason = $"map {enter.Map} has no loadable Map.dbc/WDT identity";
                    if (_worldportAckPending) AbortPendingWorldportAdoption(reason);
                    else
                    {
                        _travelStatus = $"world entry blocked: {reason}";
                        Console.WriteLine($"[net] FATAL: {reason}; disconnecting");
                        CancelPendingWorldCurtain();
                        _net?.Stop();
                    }
                    return;
                }
            }

            // NEW_WORLD is authoritative over the tentative map-only match made
            // at TRANSFER_PENDING. Validate its exact destination before the
            // preview scene is retired by the map teardown below.
            bool matchedPreparedPortal = ConfirmRealPortalHandoff(
                enter.Map, enter.Position);
            bool promotedPreparedWorld = matchedPreparedPortal &&
                TryPromotePreparedRealPortalWorld(enter.Map, enter.Position);
            if (_pendingObjectParse is not null)
                _objectUpdateBuffer = new ObjectUpdateBuffer(12_000);
            ControlledTransportRide? crossingRide = null;
            WorldEntity? crossingTransport = null;
            bool crossingRideBelongsToController = false;
            if (announcedTransfer?.RidingTransport == true &&
                ControllerOwnsControlledBodyPose && ControlledGuid == net.PlayerGuid &&
                _controlledTransportRide is { } liveRide &&
                _entities.TryGet(liveRide.Guid, out WorldEntity? liveTransport) &&
                liveTransport.Entry == announcedTransfer.Value.TransportEntry)
            {
                crossingRide = new ControlledTransportRide
                {
                    Guid = liveRide.Guid,
                    LocalPosition = enter.Position,
                    TransportYaw = liveRide.TransportYaw,
                };
                crossingTransport = liveTransport;
                _entities.ClearExcept(liveRide.Guid);
                crossingRideBelongsToController = true;
            }
            else if (announcedTransfer?.RidingTransport == true &&
                     _entities.TryGet(net.PlayerGuid, out WorldEntity? streamedRider) &&
                     streamedRider.Transport is { } streamedRide &&
                     _entities.TryGet(streamedRide.Guid, out WorldEntity? streamedTransport) &&
                     streamedTransport.Entry == announcedTransfer.Value.TransportEntry)
            {
                // In Free View (and while possessing another unit) the session rider is an
                // observed world body. Its Transport tail, not the camera controller's stale
                // ride cache, identifies the vessel that must survive this map seam. NEW_WORLD
                // supplies the fresh rider-local position/orientation below.
                crossingRide = new ControlledTransportRide
                {
                    Guid = streamedRide.Guid,
                    LocalPosition = enter.Position,
                    TransportYaw = streamedTransport.GameObjectFacing,
                };
                crossingTransport = streamedTransport;
                _entities.ClearExcept(streamedRide.Guid);
            }
            else
            {
                _entities.Clear();
            }
            _spellChainBeams?.Clear();
            ClearChatBubbles();
            ResetControlledHardLandingArc();
            _combat.Clear();
            ResetActionStores();
            // NEW_WORLD is a map boundary, but group state is session-owned and must
            // survive zoning; the disconnected/session edge above is the authoritative reset.
            ResetPetActionBar();
            ResetPetInfoRefresh();
            EnterPlayerAuraWorld(_net.PlayerGuid);
            ResetMovementModes();
            ResetControlledSpeeds();
            _iceBlockFrozen = false;
            _iceBlockFacing = enter.Orientation;
            _movementSender.Reset(enter.Orientation);
            ResetTargeting();
            ResetCombatFeedback();
            ResetUiErrors();
            ResetPendingInventoryOps();
            ResetLoot();
            ResetGameObjects();
            ResetRestXp();
            ResetDeathRez();
            ResetMirrorTimers();
            // Home belongs to the login session and survives both near and far worldports.
            ResetHearth(clearBindPoint: false);
            ResetTaxi();
            ResetSkillRankWatch();
            ResetGossip();
            ResetGossipPoi();
            ResetVendor();
            ResetQuestSession(clearStatusStore: true);
            ResetMail();
            _net.QueryNextMailTime();
            ResetAuction();
            ResetConfirms();
            ResetZoneChannels();
            ResetGuild();
            ResetTabard();
            _creaturesLogged = 0;
            _pendingObjectParse = null;
            _pendingObjectUpdates = null;
            _pendingObjectUpdateIndex = 0;

            // SMSG_NEW_WORLD changes the renderer's map identity as well as the
            // coordinates. This must also run on the first login when the saved
            // bootstrap map differs from the character's server map.
            if (changingMaps && !promotedPreparedWorld)
            {
                TearDownWorldContent();
                _adts!.SetMap(destinationMap!.Directory);
                _residentCentre = null;
                _config.Start.MapName = destinationMap.Directory;
                // The old map may have collapsed the effective boom at
                // these unrelated coordinates. Do not carry that camera
                // collision result through an opaque loading transition.
                _window.Camera.EffectiveDistance = _window.Camera.Distance;
                Console.WriteLine($"[net] map change {previousMapId} -> {enter.Map}: " +
                                  $"content switched to {destinationMap.Directory}");
                NoteFrameRecorderEvent($"world-load-{enter.Map}");
            }
            else if (changingMaps)
            {
                _lastWorldSwitchAt = RealPortalNow();
                _window.Camera.EffectiveDistance = _window.Camera.Distance;
                Console.WriteLine($"[net] map change {previousMapId} -> {enter.Map}: " +
                                  "adopted prepared renderer/collision bundle");
            }

            // Commit to the server-authoritative spawn. BeginWorldLoad reads _config.Start for the
            // load centre, and its Finish phase teleports us onto real ground there.
            _config.Start.Map = (int)enter.Map;
            Vector3 adoptedPosition = enter.Position;
            float adoptedOrientation = enter.Orientation;
            if (crossingRide is not null && crossingTransport is not null)
            {
                // NEW_WORLD's pose is transport-local on a seam. Re-arm the
                // spared client-simulated vessel against the destination map,
                // then compose the rider before seeding terrain residency.
                UpdateGameObjectTransports();
                float boatYaw = crossingTransport.GameObjectFacing;
                crossingRide.TransportYaw = boatYaw;
                TransportRiderLaw.WorldPose world = TransportRiderLaw.Compose(
                    crossingTransport.Position, boatYaw,
                    crossingRide.LocalPosition, enter.Orientation);
                adoptedPosition = world.Position;
                adoptedOrientation = world.Orientation;
                // Only an embodied session rider may install controller transport state. A
                // streamed rider will be recreated from destination object updates and composed
                // by ComposeObservedTransportRiders; the observer rig remains transport-free.
                _controlledTransportRide = crossingRideBelongsToController ? crossingRide : null;
            }

            _config.Start.X = adoptedPosition.X;
            _config.Start.Y = adoptedPosition.Y;
            _config.Start.Z = adoptedPosition.Z;
            _config.Start.Orientation = adoptedOrientation;
            _controller.Teleport(adoptedPosition.X, adoptedPosition.Y, adoptedPosition.Z);
            if (crossingRide is not null && crossingRideBelongsToController)
                _controller.Transport = new TransportPose(crossingRide.Guid,
                    crossingRide.LocalPosition, enter.Orientation);

            // THE CAMERA IS THE FACING, so setting the controller's Yaw alone
            // did nothing: CharacterController.Update overwrites Yaw from
            // input.Yaw every frame, and input.Yaw is Camera.Yaw. The server's
            // spawn orientation was therefore discarded on the very next frame
            // and every login faced whatever the camera happened to be at -
            // Start.Orientation, which is zero. Set the one the controller
            // actually reads. Initial login starts behind the character; a worldport
            // preserves the user's world-space view direction instead of forcing the
            // boom directly into the portal wall and collapsing to first person.
            _controller.Yaw = adoptedOrientation;
            if (_worldLoadStarted)
                _window.Camera.SetFacingKeepingView(adoptedOrientation);
            else
            {
                _window.Camera.Yaw = adoptedOrientation;
                _window.Camera.OrbitYaw = 0f;
            }
            _window.Camera.Target = _controller.Position;

            if (!_worldLoadStarted)
            {
                Console.WriteLine($"[net] entering world: map {enter.Map} at " +
                                  $"({enter.Position.X:F0}, {enter.Position.Y:F0}, {enter.Position.Z:F0}) - loading");
                _worldLoadStarted = true;
                _preWorldHudPrimed = false;
                _worldEntryTransitionStage = 1;
            }
            else if (promotedPreparedWorld)
            {
                if (CompletePendingWorldportAckAfterAdoption(enter.Map))
                {
                    CompletePromotedRealPortalTransition();
                    Console.WriteLine(
                        $"[net] moved to prepared map {enter.Map} without entering the world loader");
                }
                else
                {
                    // Keep the transfer curtain closed. The ACK helper has
                    // already disconnected an ambiguous/failed socket, so this
                    // prepared scene must never be presented as a live world.
                    CancelRealPortalHandoff("worldport ACK failed after prepared adoption");
                }
            }
            else
            {
                Console.WriteLine($"[net] moved to map {enter.Map} at " +
                                  $"({enter.Position.X:F0}, {enter.Position.Y:F0}, {enter.Position.Z:F0})");
                _worldEntryTransitionStage = 3;
            }
        }

        // Drain + dispatch the inbound packet stream into the entity store. A
        // login burst can contain thousands of object updates; cap both work
        // units and elapsed time so the curtain and reveal frames stay bounded.
        int updates = 0;
        int packetsDrained = 0;
        long drainStarted = Stopwatch.GetTimestamp();
        bool catchUpDrain = loadActiveAtPumpEntry || net.InboundPacketCount >= NetDrainHighWaterMark;
        int packetBudget = catchUpDrain ? NetDrainCatchUpPacketBudget : NetDrainPacketBudget;
        double timeBudgetMs = catchUpDrain ? NetDrainCatchUpTimeBudgetMs : NetDrainTimeBudgetMs;

        if (_pendingObjectParse is { } parse)
        {
            if (!parse.IsCompleted) goto FinishNetPump;
            try { _pendingObjectUpdates = parse.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[net] object-update parse error: {ex.Message}");
                _pendingObjectUpdates = null;
                _objectUpdateBuffer.Clear();
            }
            _pendingObjectParse = null;
            _pendingObjectUpdateIndex = 0;
        }

        if (_pendingObjectUpdates is { } pending)
        {
            while (_pendingObjectUpdateIndex < pending.Count &&
                   Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds < timeBudgetMs)
                ApplyUpdate(pending[_pendingObjectUpdateIndex++], _pendingObjectReceivedStamp);
            if (_pendingObjectUpdateIndex < pending.Count) goto FinishNetPump;
            pending.Clear();
            _pendingObjectUpdates = null;
            _pendingObjectUpdateIndex = 0;
            updates++;
        }

        bool parseStarted = false;
        bool worldBoundaryReached = false;
        while (packetsDrained < packetBudget &&
               Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds < timeBudgetMs &&
               net.TryDequeue(out ushort opcode, out byte[] body, out long receivedStamp))
        {
            packetsDrained++;
            _netInbound++;
            NoteLoadPacketPumped(loadActiveAtPumpEntry);
            try
            {
                Op packetOpcode = (Op)opcode;
                switch (packetOpcode)
                {
                    case Op.SMSG_TUTORIAL_FLAGS:
                        ApplyTutorialFlags(body);
                        break;
                    case Op.SMSG_TRANSFER_PENDING:
                        {
                            TransferPendingPacket transfer =
                                SessionTransferPackets.ParsePending(body);
                            _pendingTransfer = transfer;
                            // An ordinary portal is about to unload this world: cover it now,
                            // while NEW_WORLD remains the only map/position authority. A boat or
                            // zeppelin seam remains visibly in-world until the worldport lands.
                            if (!transfer.RidingTransport)
                            {
                                BeginRealPortalWorldTransfer(transfer.MapId);
                                if (_gl is not null)
                                    ArmEnterWorldCurtain(_gl, checked((int)transfer.MapId));
                            }
                            _travelStatus = transfer.RidingTransport
                                ? $"server transferring aboard transport {transfer.TransportEntry} to map {transfer.MapId}"
                                : $"server transferring to map {transfer.MapId}";
                            _hitch.SuppressFor(5.0);
                        }
                        break;
                    case Op.SMSG_TRANSFER_ABORTED:
                        {
                            byte reason = SessionTransferPackets.ParseAborted(body);
                            _pendingTransfer = null;
                            CancelPendingWorldCurtain();
                            _travelStatus = $"server refused the portal transfer (reason {reason})";
                            ShowUiError("Transfer aborted.");
                            Console.WriteLine($"[portal] server aborted the world transfer (reason {reason})");
                        }
                        break;
                    case Op.SMSG_AREA_TRIGGER_MESSAGE:
                        {
                            string text = AreaTriggerPackets.ParseMessage(body);
                            _lastPortalMessage = text;
                            _travelStatus = text;
                            ShowUiError(text);
                            Console.WriteLine($"[area-trigger] server message: {text}");
                        }
                        break;
                    case Op.SMSG_LOGOUT_RESPONSE:
                        ApplyLogoutResponse(body);
                        break;
                    case Op.SMSG_LOGOUT_CANCEL_ACK:
                        ApplyLogoutCancelAck();
                        break;
                    case Op.SMSG_LOGOUT_COMPLETE:
                        _worldportAckPending = false;
                        _pendingWorldportMapId = 0;
                        ApplyLogoutComplete();
                        break;
                    case Op.SMSG_LOGIN_VERIFY_WORLD:
                    case Op.SMSG_NEW_WORLD:
                        ClearWorldStateUi();
                        {
                            // Recognition itself is the ordered boundary. Even a
                            // malformed/duplicate NEW_WORLD must stop this drain;
                            // later object packets cannot be applied to the old map.
                            worldBoundaryReached = true;
                            // Stop exactly at the ordered boundary. The next frame performs
                            // teardown before any destination object update can begin parsing.
                            var worldReader = new PacketReader(body);
                            var worldEntry = new EnterWorldInfo(worldReader.ReadU32(),
                                worldReader.ReadVector3(), worldReader.ReadF32());
                            if (worldReader.Remaining != 0)
                                throw new InvalidDataException(
                                    $"{(Op)opcode} has {worldReader.Remaining} trailing byte(s)");
                            if (worldEntry.Map > int.MaxValue ||
                                !float.IsFinite(worldEntry.Position.X) ||
                                !float.IsFinite(worldEntry.Position.Y) ||
                                !float.IsFinite(worldEntry.Position.Z) ||
                                !float.IsFinite(worldEntry.Orientation))
                                throw new InvalidDataException(
                                    $"{(Op)opcode} contains an invalid map or non-finite pose");
                            if (_queuedWorldEntry is not null)
                                throw new InvalidDataException(
                                    $"{(Op)opcode} arrived while another world boundary awaited adoption");

                            if ((Op)opcode == Op.SMSG_NEW_WORLD)
                            {
                                // The old map's lock broadcaster cannot deliver its later
                                // tombstone to an observer that has already crossed this
                                // boundary. NEW_WORLD is therefore authoritative retirement
                                // for every cached tactical lock and pose on this client.
                                ResetTacticalFreezeWorldState();
                                ClearRtsTerritoryCapture();
                                if (!_worldLoadStarted)
                                    throw new InvalidDataException(
                                        "NEW_WORLD arrived before initial world adoption");
                                // Evidence for portal/teleport proofs (World Pack tier 3: "trigger X → map N").
                                EmitInterface("world", "new-world", "ARRIVED", 0,
                                    FormattableString.Invariant($"map={worldEntry.Map};x={worldEntry.Position.X:F1};y={worldEntry.Position.Y:F1};z={worldEntry.Position.Z:F1}"));
                                MarkPendingWorldportAck(worldEntry.Map);
                            }
                            else if (_worldLoadStarted || _worldportAckPending)
                            {
                                throw new InvalidDataException(
                                    "LOGIN_VERIFY_WORLD arrived while a world was already active");
                            }
                            _queuedWorldEntry = worldEntry;
                            if (_net?.QueryTime() == true)
                                _questTimeAskedStamp = Stopwatch.GetTimestamp();
                        }
                        break;
                    case Op.SMSG_LOGIN_SETTIMESPEED:
                        {
                            // The server's game clock: a bit-packed game time (minute:6,
                            // hour:5, weekday:3, day:6, month:4, year:5 from the LSB)
                            // plus the timescale in game-minutes per real second
                            // (vanilla 0.01666667 = real time). The world clock advances
                            // it locally from the receive stamp; TimeSource.Server feeds
                            // it to the atmosphere every frame (UpdateWorldClock).
                            LoginTimeSpeedPacket time =
                                SessionTransferPackets.ParseTimeSpeed(body);
                            _worldClock.SetServerTime(
                                time.PackedDateTime, time.Timescale, receivedStamp);
                            Console.WriteLine(
                                $"[net] server game time {_worldClock.ServerHour:D2}:{_worldClock.ServerMinute:D2} " +
                                $"(timescale {time.Timescale:F7} game-min/s)");
                        }
                        break;
                    case Op.SMSG_PLAY_SOUND:
                    case Op.SMSG_PLAY_MUSIC:
                    case Op.SMSG_PLAY_OBJECT_SOUND:
                        ApplyServerSound(packetOpcode, body);
                        break;
                    case Op.SMSG_WEATHER:
                        ApplyWeather(body);
                        break;
                    case Op.SMSG_GROUP_LIST:
                        ApplyPartyRoster(body);
                        break;
                    case Op.SMSG_RAID_INSTANCE_INFO:
                        ApplyRaidInstanceInfo(body);
                        break;
                    case Op.SMSG_INSTANCE_SAVE_CREATED:
                        ApplyInstanceSaveCreated(body);
                        break;
                    case Op.SMSG_RAID_INSTANCE_MESSAGE:
                        ApplyRaidInstanceMessage(body);
                        break;
                    case Op.SMSG_INSTANCE_RESET:
                        ApplyInstanceReset(body, failed: false);
                        break;
                    case Op.SMSG_INSTANCE_RESET_FAILED:
                        ApplyInstanceReset(body, failed: true);
                        break;
                    case Op.MSG_LIST_STABLED_PETS:
                        ApplyStableList(body);
                        break;
                    case Op.SMSG_STABLE_RESULT:
                        ApplyStableResult(body);
                        break;
                    case Op.SMSG_GROUP_INVITE:
                        ApplyPartyInvite(body);
                        break;
                    case Op.SMSG_GROUP_DECLINE:
                        ApplyPartyDecline(body);
                        break;
                    case Op.SMSG_GROUP_UNINVITE:
                        ApplyPartyUninvited(body);
                        break;
                    case Op.SMSG_GROUP_SET_LEADER:
                        ApplyPartyLeaderChanged(body);
                        break;
                    case Op.SMSG_GROUP_DESTROYED:
                        ApplyPartyDestroyed(body);
                        break;
                    case Op.SMSG_PARTY_COMMAND_RESULT:
                        ApplyPartyCommandResult(body);
                        break;
                    case Op.SMSG_PARTY_MEMBER_STATS:
                        ApplyPartyMemberStats(body, fullSnapshot: false);
                        break;
                    case Op.SMSG_PARTY_MEMBER_STATS_FULL:
                        ApplyPartyMemberStats(body, fullSnapshot: true);
                        break;
                    case Op.MSG_MINIMAP_PING:
                        ApplyMinimapPing(body);
                        break;
                    case Op.MSG_RAID_TARGET_UPDATE:
                        ApplyPartyRaidTargetUpdate(body);
                        break;
                    case Op.MSG_RAID_READY_CHECK:
                        ApplyReadyCheck(body);
                        break;
                    case Op.SMSG_PET_SPELLS:
                        ApplyActorPetPacket(Op.SMSG_PET_SPELLS, body, net.PlayerGuid);
                        break;
                    case Op.SMSG_PET_MODE:
                        ApplyActorPetPacket(Op.SMSG_PET_MODE, body, net.PlayerGuid);
                        break;
                    case Op.SMSG_UPDATE_INSTANCE_OWNERSHIP:
                        ApplyInstanceOwnership(body, net.PlayerGuid);
                        break;
                    case Op.SMSG_UPDATE_LAST_INSTANCE:
                        ApplyLastInstance(body, net.PlayerGuid);
                        break;
                    case Op.SMSG_RAID_GROUP_ONLY:
                        ApplyRaidGroupOnly(body, net.PlayerGuid);
                        break;
                    case Op.SMSG_OPEN_CONTAINER:
                        ApplyServerContainerOpen(body);
                        break;
                    case Op.SMSG_ITEM_TIME_UPDATE:
                        ApplyItemTime(body);
                        break;
                    case Op.SMSG_READ_ITEM_OK:
                    case Op.SMSG_READ_ITEM_FAILED:
                        ApplyItemReadResult(packetOpcode, body);
                        break;
                    case Op.SMSG_GAMEOBJECT_PAGETEXT:
                        ApplyGameObjectPageText(body, net.PlayerGuid);
                        break;
                    case Op.SMSG_PET_TAME_FAILURE:
                    case Op.SMSG_PET_NAME_INVALID:
                    case Op.SMSG_PET_BROKEN:
                        ApplyPetNotice(packetOpcode, body, net.PlayerGuid);
                        break;
                    case Op.SMSG_PET_ACTION_SOUND:
                        ApplyPetActionSound(body, net.PlayerGuid);
                        break;
                    case Op.SMSG_PET_ACTION_FEEDBACK:
                        ApplyActorPetPacket(Op.SMSG_PET_ACTION_FEEDBACK, body, net.PlayerGuid);
                        break;
                    case Op.SMSG_PET_CAST_FAILED:
                        ApplyActorPetPacket(Op.SMSG_PET_CAST_FAILED, body, net.PlayerGuid);
                        break;
                    case Op.SMSG_SPELL_COOLDOWN:
                        ApplyAddressedSpellCooldowns(body);
                        break;
                    case Op.SMSG_ITEM_COOLDOWN:
                        ApplyItemCooldown(body);
                        break;
                    case Op.SMSG_COOLDOWN_EVENT:
                        ApplyCooldownEvent(body, clear: false);
                        break;
                    case Op.SMSG_CLEAR_COOLDOWN:
                        ApplyCooldownEvent(body, clear: true);
                        break;
                    case Op.SMSG_COOLDOWN_CHEAT:
                        ApplyCooldownCheat(body);
                        break;
                    case Op.SMSG_AREA_SPIRIT_HEALER_TIME:
                        ApplyAreaSpiritHealerTime(body);
                        break;
                    case Op.MSG_BATTLEGROUND_PLAYER_POSITIONS:
                        ApplyBattlefieldPositions(body);
                        break;
                    case Op.SMSG_BATTLEGROUND_PLAYER_JOINED:
                        ApplyBattlefieldRosterNotice(body, true);
                        break;
                    case Op.SMSG_BATTLEGROUND_PLAYER_LEFT:
                        ApplyBattlefieldRosterNotice(body, false);
                        break;
                    case Op.MSG_PVP_LOG_DATA:
                        ApplyBattlefieldScores(body);
                        break;
                    case Op.SMSG_BATTLEFIELD_LIST:
                        ApplyBattlefieldList(body);
                        break;
                    case Op.SMSG_BATTLEFIELD_STATUS:
                        ApplyBattlefieldStatus(body);
                        break;
                    case Op.SMSG_GROUP_JOINED_BATTLEGROUND:
                        ApplyBattlefieldJoinResult(body);
                        break;
                    case Op.MSG_INSPECT_HONOR_STATS:
                        ApplyInspectHonor(body, net.PlayerGuid);
                        break;
                    case Op.SMSG_INSPECT:
                        ApplyInspect(body);
                        break;
                    case Op.SMSG_FRIEND_LIST:
                        ApplyFriendList(body);
                        break;
                    case Op.SMSG_FRIEND_STATUS:
                        ApplyFriendStatus(body);
                        break;
                    case Op.SMSG_IGNORE_LIST:
                        ApplyIgnoreList(body);
                        break;
                    case Op.SMSG_WHO:
                        ApplyWhoList(body);
                        break;
                    case Op.SMSG_TRADE_STATUS:
                        ApplyTradeStatus(body);
                        break;
                    case Op.SMSG_TRADE_STATUS_EXTENDED:
                        ApplyTradeExtended(body);
                        break;
                    case Op.SMSG_DUEL_REQUESTED:
                        ApplyDuelRequested(body);
                        break;
                    case Op.SMSG_DUEL_OUTOFBOUNDS:
                        ApplyDuelBounds(body, outside: true);
                        break;
                    case Op.SMSG_DUEL_INBOUNDS:
                        ApplyDuelBounds(body, outside: false);
                        break;
                    case Op.SMSG_DUEL_COMPLETE:
                        ApplyDuelComplete(body);
                        break;
                    case Op.SMSG_DUEL_WINNER:
                        ApplyDuelWinner(body);
                        break;
                    case Op.SMSG_DUEL_COUNTDOWN:
                        ApplyDuelCountdown(body);
                        break;
                    case Op.SMSG_GMTICKET_CREATE:
                    case Op.SMSG_GMTICKET_UPDATETEXT:
                    case Op.SMSG_GMTICKET_DELETETICKET:
                    case Op.SMSG_GMTICKET_SYSTEMSTATUS:
                    case Op.SMSG_GMTICKET_GETTICKET:
                        ApplyHelpTicketPacket((Op)opcode, body);
                        break;
                    case Op.SMSG_SET_FACTION_ATWAR:
                        ApplyFactionAtWar(body);
                        break;
                    case Op.SMSG_SET_FORCED_REACTIONS:
                        _forcedReactions.Apply(net.PlayerGuid, body);
                        break;
                    case Op.SMSG_INITIALIZE_FACTIONS:
                        ApplyInitialFactions(body);
                        break;
                    case Op.SMSG_SET_FACTION_VISIBLE:
                        ApplyFactionVisible(body);
                        break;
                    case Op.SMSG_SET_FACTION_STANDING:
                        ApplyFactionStanding(body);
                        break;
                    case Op.SMSG_SET_FLAT_SPELL_MODIFIER:
                    case Op.SMSG_SET_PCT_SPELL_MODIFIER:
                        ApplySpellModifier(body, LocalPlayerGuid, packetOpcode == Op.SMSG_SET_PCT_SPELL_MODIFIER);
                        break;
                    case Op.SMSG_SET_PROFICIENCY:
                        ApplyItemProficiency(body, LocalPlayerGuid);
                        break;
                    case Op.MSG_MOVE_TELEPORT:
                    {
                        MovementRelay teleport = MovementRelayPackets.Parse((Op)opcode, body);
                        if (ControllerOwnsMovementPose(teleport.Guid))
                            ApplyServerAuthoredSelfMove(teleport);
                        else
                            _entities.ApplyRemoteTeleport(teleport.Guid, teleport.Movement, MovementInfo.ClientUptimeMs());
                        break;
                    }
                    case Op.MSG_MOVE_TELEPORT_ACK:
                        ApplyMoveTeleportAck(net, body);
                        break;
                    case Op.SMSG_SPLINE_MOVE_ROOT:
                    case Op.SMSG_SPLINE_MOVE_UNROOT:
                    case Op.SMSG_SPLINE_MOVE_FEATHER_FALL:
                    case Op.SMSG_SPLINE_MOVE_NORMAL_FALL:
                    case Op.SMSG_SPLINE_MOVE_SET_HOVER:
                    case Op.SMSG_SPLINE_MOVE_UNSET_HOVER:
                    case Op.SMSG_SPLINE_MOVE_WATER_WALK:
                    case Op.SMSG_SPLINE_MOVE_LAND_WALK:
                    case Op.SMSG_SPLINE_MOVE_SET_RUN_MODE:
                    case Op.SMSG_SPLINE_MOVE_SET_WALK_MODE:
                        _entities.ApplySplineMovementMode(SplineMovementModePackets.Parse((Op)opcode, body));
                        break;
                    case Op.SMSG_MOVE_KNOCK_BACK:
                        ApplyKnockbackCommand(net, body);
                        break;
                    case Op.MSG_MOVE_KNOCK_BACK:
                        ApplyKnockbackRelay(body);
                        break;
                    case Op.SMSG_FORCE_WALK_SPEED_CHANGE:
                    case Op.SMSG_FORCE_RUN_SPEED_CHANGE:
                    case Op.SMSG_FORCE_RUN_BACK_SPEED_CHANGE:
                    case Op.SMSG_FORCE_SWIM_SPEED_CHANGE:
                    case Op.SMSG_FORCE_SWIM_BACK_SPEED_CHANGE:
                    case Op.SMSG_FORCE_TURN_RATE_CHANGE:
                        ApplyForceSpeedChange(net, (Op)opcode, body);
                        break;
                    case Op.SMSG_CLIENT_CONTROL_UPDATE:
                        ApplyClientControlUpdate(net, body);
                        break;
                    case Op.MSG_MOVE_START_FORWARD:
                    case Op.MSG_MOVE_START_BACKWARD:
                    case Op.MSG_MOVE_STOP:
                    case Op.MSG_MOVE_START_STRAFE_LEFT:
                    case Op.MSG_MOVE_START_STRAFE_RIGHT:
                    case Op.MSG_MOVE_STOP_STRAFE:
                    case Op.MSG_MOVE_JUMP:
                    case Op.MSG_MOVE_START_TURN_LEFT:
                    case Op.MSG_MOVE_START_TURN_RIGHT:
                    case Op.MSG_MOVE_STOP_TURN:
                    case Op.MSG_MOVE_START_PITCH_UP:
                    case Op.MSG_MOVE_START_PITCH_DOWN:
                    case Op.MSG_MOVE_STOP_PITCH:
                    case Op.MSG_MOVE_SET_RUN_MODE:
                    case Op.MSG_MOVE_SET_WALK_MODE:
                    case Op.MSG_MOVE_FALL_LAND:
                    case Op.MSG_MOVE_START_SWIM:
                    case Op.MSG_MOVE_STOP_SWIM:
                    case Op.MSG_MOVE_SET_FACING:
                    case Op.MSG_MOVE_SET_PITCH:
                    case Op.MSG_MOVE_ROOT:
                    case Op.MSG_MOVE_UNROOT:
                    case Op.MSG_MOVE_HOVER:
                    case Op.MSG_MOVE_FEATHER_FALL:
                    case Op.MSG_MOVE_WATER_WALK:
                    case Op.MSG_MOVE_HEARTBEAT:
                    {
                        MovementRelay relay = MovementRelayPackets.Parse((Op)opcode, body);
                        if (relay.Guid == ControlledGuid && ControllerOwnsControlledBodyPose)
                            ApplyServerAuthoredSelfMove(relay);
                        else
                            _entities.ApplyRemotePlayerMove(
                                relay.Guid, relay.Movement, MovementInfo.ClientUptimeMs());
                        break;
                    }
                    case Op.SMSG_SPLINE_SET_WALK_SPEED:
                    case Op.SMSG_SPLINE_SET_RUN_SPEED:
                    case Op.SMSG_SPLINE_SET_RUN_BACK_SPEED:
                    case Op.SMSG_SPLINE_SET_SWIM_SPEED:
                    case Op.SMSG_SPLINE_SET_SWIM_BACK_SPEED:
                    case Op.SMSG_SPLINE_SET_TURN_RATE:
                    case Op.MSG_MOVE_SET_WALK_SPEED:
                    case Op.MSG_MOVE_SET_RUN_SPEED:
                    case Op.MSG_MOVE_SET_RUN_BACK_SPEED:
                    case Op.MSG_MOVE_SET_SWIM_SPEED:
                    case Op.MSG_MOVE_SET_SWIM_BACK_SPEED:
                    case Op.MSG_MOVE_SET_TURN_RATE:
                        ApplyObserverSpeedChange(net, (Op)opcode, body);
                        break;
                    case Op.SMSG_FORCE_MOVE_ROOT:
                    case Op.SMSG_FORCE_MOVE_UNROOT:
                    case Op.SMSG_MOVE_WATER_WALK:
                    case Op.SMSG_MOVE_LAND_WALK:
                    case Op.SMSG_MOVE_FEATHER_FALL:
                    case Op.SMSG_MOVE_NORMAL_FALL:
                    case Op.SMSG_MOVE_SET_HOVER:
                    case Op.SMSG_MOVE_UNSET_HOVER:
                        ApplyMovementModeChange(net, (Op)opcode, body);
                        break;
                    case Op.SMSG_UPDATE_OBJECT:
                        _pendingObjectReceivedStamp = receivedStamp;
                        ObjectUpdateBuffer updateBuffer = _objectUpdateBuffer;
                        _pendingObjectParse = Task.Run(
                            () => UpdateObjectParser.Parse(body, updateBuffer));
                        parseStarted = true;
                        break;
                    case Op.SMSG_COMPRESSED_UPDATE_OBJECT:
                        _pendingObjectReceivedStamp = receivedStamp;
                        ObjectUpdateBuffer compressedBuffer = _objectUpdateBuffer;
                        _pendingObjectParse = Task.Run(
                            () => UpdateObjectParser.ParseCompressed(body, compressedBuffer));
                        parseStarted = true;
                        break;
                    case Op.SMSG_COMPRESSED_MOVES:
                        foreach (CompressedMovementRecord compressed in
                                 CompressedMovementPackets.Parse(body))
                        {
                            if (ApplyAdditionalBatchedMovement(compressed)) continue;
                            if (compressed.Relay is MovementRelay relay)
                            {
                                if (relay.Guid == ControlledGuid && ControllerOwnsControlledBodyPose)
                                    ApplyServerAuthoredSelfMove(relay);
                                else
                                    _entities.ApplyRemotePlayerMove(
                                        relay.Guid, relay.Movement, MovementInfo.ClientUptimeMs());
                            }
                            // Mass bot movement batches creature splines and spline speeds
                            // into the same envelope; route them to their standalone handlers.
                            else if (compressed.Opcode is Op.SMSG_MONSTER_MOVE or Op.SMSG_MONSTER_MOVE_TRANSPORT)
                                ApplyMonsterMovePacket(compressed.Body, compressed.Opcode == Op.SMSG_MONSTER_MOVE_TRANSPORT);
                            else if (compressed.Opcode is Op.SMSG_SPLINE_SET_WALK_SPEED
                                     or Op.SMSG_SPLINE_SET_RUN_SPEED
                                     or Op.SMSG_SPLINE_SET_RUN_BACK_SPEED
                                     or Op.SMSG_SPLINE_SET_SWIM_SPEED
                                     or Op.SMSG_SPLINE_SET_SWIM_BACK_SPEED
                                     or Op.SMSG_SPLINE_SET_TURN_RATE)
                                ApplyObserverSpeedChange(net, compressed.Opcode, compressed.Body);
                            else if (_compressedMoveSkippedOps.Add(compressed.Opcode))
                                Console.WriteLine("[net] compressed-moves record " +
                                    $"{compressed.Opcode} has no handler - skipped (logged once)");
                        }
                        break;
                    case Op.SMSG_DESTROY_OBJECT:
                        ApplyDestroyObject(body);
                        break;
                    case Op.SMSG_TRIGGER_CINEMATIC:
                        {
                            uint cinematicId = body.Length >= 4
                                ? new PacketReader(body).ReadU32()
                                : 0;
                            net.CompleteCinematic();
                            Console.WriteLine($"[net] cinematic {cinematicId} triggered - acked (skip)");
                        }
                        break;
                    case Op.SMSG_MONSTER_MOVE:
                    case Op.SMSG_MONSTER_MOVE_TRANSPORT:
                        ApplyMonsterMovePacket(body, (Op)opcode == Op.SMSG_MONSTER_MOVE_TRANSPORT);
                        break;
                    case Op.SMSG_AI_REACTION:
                        // Dev window: the server's own aggro moment (no-op while closed).
                        ApplyAiReaction(body);
                        break;
                    case Op.SMSG_ACTION_BUTTONS:
                        // Un-proxied wire feeds always describe the logged-in character; a
                        // possessed bot's arrive wrapped in SMSG_SUI_PROXY and land in its store.
                        OwnActions.ApplyButtons(body);
                        break;
                    case Op.SMSG_SUI_CONTROL_ACK:
                        ApplySuiControlAck(body);
                        break;
                    case Op.SMSG_SUI_CONTROL_ROSTER:
                        ApplySuiControlRoster(body);
                        break;
                    case Op.SMSG_SUI_COMPANION:
                        ApplySuiCompanion(body);
                        break;
                    case Op.SMSG_SUI_PARTY_TAXI_RESULT:
                        ApplyPartyTaxiResult(body);
                        break;
                    case Op.SMSG_SUI_COMMANDER_RAID:
                        ApplyCommanderRaidStatus(body);
                        break;
                    case Op.SMSG_SUI_TACTICAL_FREEZE:
                        ApplyTacticalFreezeSnapshot(body);
                        break;
                    case Op.SMSG_SUI_TACTICAL_QUEUE:
                        ApplyTacticalQueueSnapshot(body);
                        break;
                    case Op.SMSG_SUI_PROXY:
                        ApplySuiProxy(body);
                        break;
                    case Op.SMSG_SUI_SNAPSHOT:
                        ApplySuiSnapshot(body);
                        break;
                    case Op.SMSG_SUI_ZONE_INTEL:
                        ApplySuiZoneIntel(body);
                        break;
                    case Op.SMSG_SUI_RTS_STATE:
                        ApplySuiRtsState(body);
                        break;
                    case Op.SMSG_SUI_RTS_ACTION_RESULT:
                        ApplySuiRtsActionResult(body);
                        break;
                    case Op.SMSG_SUI_FORCE_ROSTER:
                        ApplySuiForceRoster(body);
                        break;
                    case Op.SMSG_SUI_MEMBER_SPELLS:
                        ApplySuiMemberSpells(body);
                        break;
                    case Op.SMSG_SUI_MEMBER_ITEM_MOVE_RESULT:
                        ApplySuiMemberItemMoveResult(body);
                        break;
                    case Op.SMSG_SUI_QUEST_LOG:
                        ApplySuiQuestLog(body);
                        break;
                    case Op.SMSG_SUI_PARTY_QUEST_RESULT:
                        ApplySuiPartyQuestResult(body);
                        break;
                    case Op.SMSG_SUI_GIVER_STATUS:
                        ApplySuiGiverStatus(body);
                        break;
                    case Op.SMSG_SUI_GIVER_QUESTS:
                        ApplySuiGiverQuests(body);
                        break;
                    case Op.SMSG_SUI_PARTY_LEAD_RESULT:
                        ApplySuiPartyLeadResult(body);
                        break;
                    case Op.SMSG_SUI_THREAT:
                        ApplySuiThreat(body);
                        break;
                    case Op.SMSG_SUI_KART:
                        ApplySuiKart(body);
                        break;
                    case Op.MSG_QUEST_PUSH_RESULT:
                        ApplyQuestPushResult(body);
                        break;
                    case Op.SMSG_SUI_PORTAL_DESCRIPTOR:
                        {
                            PortalDescriptorPacket descriptor = PortalWire.ParseDescriptor(body);
                            ApplyRealPortalDescriptor(descriptor);
                        }
                        break;
                    case Op.SMSG_SUI_PORTAL_STATE:
                        {
                            PortalStatePacket state = PortalWire.ParseState(body);
                            ApplyRealPortalState(state);
                        }
                        break;
                    case Op.SMSG_UPDATE_AURA_DURATION:
                        ApplyAuraDuration(body, net.PlayerGuid);
                        break;
                    case Op.SMSG_INITIAL_SPELLS:
                        OwnActions.ApplyInitialSpells(body, MovementInfo.ClientUptimeMs() / 1000.0);
                        break;
                    case Op.SMSG_LEARNED_SPELL:
                        {
                            var spellReader = new PacketReader(body);
                            uint learned = spellReader.ReadU16();
                            OwnActions.Learn(learned);
                            ObserveTrainerLearned(learned);
                            ObserveProfessionLearned(learned);
                        }
                        break;
                    case Op.SMSG_SUPERCEDED_SPELL:
                        {
                            var spellReader = new PacketReader(body);
                            OwnActions.Supercede(spellReader.ReadU16(), spellReader.ReadU16());
                        }
                        break;
                    case Op.SMSG_REMOVED_SPELL:
                        {
                            var spellReader = new PacketReader(body);
                            uint removed = spellReader.ReadU16();
                            OwnActions.Remove(removed);
                            EmitInterface("talent", "spell-removed", "APPLIED", removed, "source=SMSG_REMOVED_SPELL");
                        }
                        break;
                    case Op.SMSG_ITEM_QUERY_SINGLE_RESPONSE:
                        if (_items is not null)
                        {
                            uint landedItem = _items.Apply(body);
                            ObserveQuestItemTemplateLanding(landedItem);
                        }
                        break;
                    case Op.SMSG_GOSSIP_MESSAGE:
                        ApplyGossipMenu(body, LocalPlayerGuid);
                        break;
                    case Op.SMSG_PET_UNLEARN_CONFIRM:
                        ApplyPetUnlearnConfirm(body, LocalPlayerGuid);
                        break;
                    case Op.SMSG_GOSSIP_COMPLETE:
                        EmitInterface("gossip", "complete", "RECEIVED", _gossipMenu?.SourceGuid ?? 0, "serverClosed=true");
                        if (ControlledGuid == LocalPlayerGuid) CompleteGossip();
                        CloseQuestNpcFrame(playSound: true);
                        break;
                    case Op.SMSG_GOSSIP_POI:
                        ApplyGossipPoi(body);
                        break;
                    case Op.SMSG_NPC_TEXT_UPDATE:
                        ApplyNpcText(body);
                        break;
                    case Op.SMSG_GAMEOBJECT_CUSTOM_ANIM:
                        ApplyGameObjectCustomAnim(body);
                        break;
                    case Op.SMSG_GAMEOBJECT_DESPAWN_ANIM:
                        ApplyGameObjectDespawnAnim(body);
                        break;
                    case Op.SMSG_FISH_NOT_HOOKED:
                        ApplyFishingVerdict(body, escaped: false);
                        break;
                    case Op.SMSG_FISH_ESCAPED:
                        ApplyFishingVerdict(body, escaped: true);
                        break;
                    case Op.SMSG_PAGE_TEXT_QUERY_RESPONSE:
                        ApplyPageText(body);
                        break;
                    case Op.SMSG_LIST_INVENTORY:
                        ApplyVendorList(body);
                        break;
                    case Op.SMSG_TRAINER_LIST:
                        ApplyTrainerList(body);
                        break;
                    case Op.SMSG_TRAINER_BUY_SUCCEEDED:
                        ApplyTrainerSuccess(body);
                        break;
                    case Op.SMSG_TRAINER_BUY_FAILED:
                        ApplyTrainerFailure(body);
                        break;
                    case Op.SMSG_BINDER_CONFIRM:
                        ApplyBinderConfirm(body, LocalPlayerGuid);
                        break;
                    case Op.SMSG_BINDPOINTUPDATE:
                        ApplyBindPoint(body);
                        break;
                    case Op.SMSG_PLAYERBOUND:
                        ApplyPlayerBound(body, LocalPlayerGuid);
                        break;
                    case Op.SMSG_TAXINODE_STATUS:
                        ApplyTaxiNodeStatus(body);
                        break;
                    case Op.SMSG_SHOWTAXINODES:
                        ApplyTaxiNodes(body);
                        break;
                    case Op.SMSG_ACTIVATETAXIREPLY:
                        ApplyTaxiReply(body);
                        break;
                    case Op.SMSG_NEW_TAXI_PATH:
                        ApplyNewTaxiPath(body);
                        break;
                    case Op.MSG_TALENT_WIPE_CONFIRM:
                        ApplyTalentWipeConfirm(body);
                        break;
                    case Op.SMSG_SHOW_BANK:
                        ApplyShowBank(body);
                        break;
                    case Op.SMSG_BUY_BANK_SLOT_RESULT:
                        ApplyBuyBankSlotResult(body);
                        break;
                    case Op.SMSG_MAIL_LIST_RESULT:
                        ApplyMailList(body);
                        break;
                    case Op.SMSG_SEND_MAIL_RESULT:
                        ApplyMailResult(body);
                        break;
                    case Op.SMSG_ITEM_TEXT_QUERY_RESPONSE:
                        ApplyMailItemText(body);
                        break;
                    case Op.SMSG_RECEIVED_MAIL:
                        ApplyReceivedMail(body);
                        break;
                    case Op.MSG_QUERY_NEXT_MAIL_TIME:
                        ApplyNextMailTime(body);
                        break;
                    case Op.MSG_AUCTION_HELLO:
                        ApplyAuctionHello(body);
                        break;
                    case Op.SMSG_AUCTION_LIST_RESULT:
                        ApplyAuctionList(body, "browse");
                        break;
                    case Op.SMSG_AUCTION_OWNER_LIST_RESULT:
                        ApplyAuctionList(body, "owner");
                        break;
                    case Op.SMSG_AUCTION_BIDDER_LIST_RESULT:
                        ApplyAuctionList(body, "bidder");
                        break;
                    case Op.SMSG_AUCTION_COMMAND_RESULT:
                        ApplyAuctionCommand(body);
                        break;
                    case Op.SMSG_AUCTION_BIDDER_NOTIFICATION:
                    case Op.SMSG_AUCTION_OWNER_NOTIFICATION:
                    case Op.SMSG_AUCTION_REMOVED_NOTIFICATION:
                        ApplyAuctionNotification((Op)opcode, body);
                        break;
                    case Op.SMSG_GUILD_ROSTER:
                        ApplyGuildRoster(body);
                        break;
                    case Op.SMSG_GUILD_QUERY_RESPONSE:
                        ApplyGuildQueryResponse(body);
                        break;
                    case Op.SMSG_GUILD_INVITE:
                        ApplyGuildInvite(body);
                        break;
                    case Op.SMSG_GUILD_DECLINE:
                        ApplyGuildDecline(body);
                        break;
                    case Op.SMSG_GUILD_INFO:
                        ApplyGuildInfo(body);
                        break;
                    case Op.SMSG_GUILD_EVENT:
                        ApplyGuildEvent(body);
                        break;
                    case Op.SMSG_GUILD_COMMAND_RESULT:
                        ApplyGuildCommandResult(body);
                        break;
                    case Op.SMSG_TABARDVENDOR_ACTIVATE:
                        ApplyTabardVendorActivate(body);
                        break;
                    case Op.MSG_SAVE_GUILD_EMBLEM:
                        ApplySaveGuildEmblemResult(body);
                        break;
                    case Op.SMSG_QUESTGIVER_STATUS:
                        ApplyQuestStatus(body);
                        break;
                    case Op.SMSG_QUEST_QUERY_RESPONSE:
                        ApplyQuestQuery(body);
                        break;
                    case Op.SMSG_QUESTGIVER_QUEST_LIST:
                        ApplyQuestList(body);
                        break;
                    case Op.SMSG_QUESTGIVER_QUEST_DETAILS:
                        ApplyQuestDetails(body);
                        break;
                    case Op.SMSG_QUESTGIVER_REQUEST_ITEMS:
                        ApplyQuestRequestItems(body);
                        break;
                    case Op.SMSG_QUESTGIVER_OFFER_REWARD:
                        ApplyQuestOffer(body);
                        break;
                    case Op.SMSG_QUESTUPDATE_ADD_KILL:
                        ApplyQuestKill(body);
                        break;
                    case Op.SMSG_QUESTUPDATE_ADD_ITEM:
                        ApplyQuestItem(body);
                        break;
                    case Op.SMSG_QUESTUPDATE_COMPLETE:
                        ApplyQuestObjectiveComplete(body);
                        break;
                    case Op.SMSG_QUESTGIVER_QUEST_COMPLETE:
                        ApplyQuestComplete(body);
                        break;
                    case Op.SMSG_QUESTGIVER_QUEST_INVALID:
                    case Op.SMSG_QUESTGIVER_QUEST_FAILED:
                    case Op.SMSG_QUESTUPDATE_FAILED:
                    case Op.SMSG_QUESTUPDATE_FAILEDTIMER:
                    case Op.SMSG_QUESTLOG_FULL:
                        ApplyQuestError((Op)opcode, body);
                        break;
                    case Op.SMSG_INIT_WORLD_STATES:
                        ApplyInitialWorldStates(body);
                        break;
                    case Op.SMSG_UPDATE_WORLD_STATE:
                        ApplyWorldState(body);
                        break;
                    case Op.SMSG_BUY_ITEM:
                        ApplyVendorStockUpdate(body);
                        break;
                    case Op.SMSG_BUY_FAILED:
                        ApplyVendorBuyFailure(body);
                        break;
                    case Op.SMSG_SELL_ITEM:
                        ApplyVendorSellFailure(body);
                        break;
                    case Op.SMSG_INVALIDATE_PLAYER:
                        ApplyInvalidatePlayer(body);
                        break;
                    case Op.SMSG_NAME_QUERY_RESPONSE:
                        {
                            PlayerNameQueryResponse response =
                                PlayerNamePackets.ParseResponse(body);
                            _queriedPlayerNames.Remove(response.Guid);
                            RefreshVisiblePlayerIdentity(response);
                            // Keep a negative answer too. ContainsKey is the ask-once gate, so an
                            // unknown GUID must not spin another query every frame.
                            _playerNames[response.Guid] = response.Name;
                            if (response.Name.Length > 0)
                                _playerTraits[response.Guid] = response.Traits;
                            else
                                _playerTraits.Remove(response.Guid);
                            ReorderSocialContactsAfterNameResolution();
                            FlushPendingChatMacros(response.Guid);
                            FlushPendingChatXp(response.Guid);
                            FlushPendingChatChannelNotices(response.Guid);
                            FlushPendingFriendStatus(response.Guid);
                        }
                        break;
                    case Op.SMSG_CREATURE_QUERY_RESPONSE:
                        {
                            CreatureQueryResponse response = CreatureQueryPacket.Parse(body);
                            _queriedCreatureNames.Remove(response.Entry);
                            _creatureQueryRecords[response.Entry] = response.Info;
                            if (response.Info is { Name.Length: > 0 } info)
                            {
                                _creatureNames[response.Entry] = info.Name;
                                FlushPendingChatMacros();
                            }
                            else
                                _creatureNames.Remove(response.Entry);
                            FlushPendingChatXp();
                        }
                        break;
                    case Op.SMSG_SPELL_START:
                        EnqueueSpellPresentation(new SpellStartEvent(SpellPacketParser.ParseStart(body)));
                        break;
                    case Op.SMSG_SPELL_GO:
                        EnqueueSpellPresentation(new SpellGoEvent(SpellPacketParser.ParseGo(body)));
                        break;
                    case Op.SMSG_STANDSTATE_UPDATE:
                        ApplyStandStateUpdate(body, net.PlayerGuid);
                        break;
                    case Op.SMSG_CAST_RESULT:
                        {
                            var result = SpellPacketParser.ParseResult(body);
                            if (result.Status == 2)
                                EnqueueSpellPresentation(new SpellCastResultEvent(net.PlayerGuid, result.SpellId, result.Reason) { Context = result.Context });
                        }
                        break;
                    case Op.SMSG_SPELL_FAILED_OTHER:
                        {
                            var r = new PacketReader(body);
                            ulong caster = r.ReadU64(); uint spell = r.ReadU32();
                            EnqueueSpellPresentation(new SpellFailedOtherEvent(caster, spell));
                        }
                        break;
                    case Op.SMSG_SPELL_DELAYED:
                        {
                            SpellDelayedPacket delayed = SpellLifecyclePacketParser.ParseDelayed(body);
                            EnqueueSpellPresentation(new SpellDelayedEvent(delayed.Caster, delayed.DelayMs));
                        }
                        break;
                    case Op.MSG_CHANNEL_START:
                        {
                            SpellChannelStartPacket channel = SpellLifecyclePacketParser.ParseChannelStart(body);
                            EnqueueSpellPresentation(new SpellChannelStartEvent(
                                net.PlayerGuid, channel.SpellId, channel.DurationMs));
                        }
                        break;
                    case Op.MSG_CHANNEL_UPDATE:
                        EnqueueSpellPresentation(new SpellChannelUpdateEvent(net.PlayerGuid,
                            SpellLifecyclePacketParser.ParseChannelUpdate(body)));
                        break;
                    case Op.SMSG_SPELL_UPDATE_CHAIN_TARGETS:
                        EnqueueSpellPresentation(new SpellChainTargetsEvent(
                            SpellLifecyclePacketParser.ParseChainTargets(body)));
                        break;
                    case Op.SMSG_PLAY_SPELL_VISUAL:
                        {
                            var r = new PacketReader(body);
                            EnqueueSpellPresentation(new SpellKitPushEvent(r.ReadU64(), r.ReadU32()));
                        }
                        break;
                    case Op.SMSG_FEIGN_DEATH_RESISTED:
                        EnqueueFeignDeathResisted(net.PlayerGuid, body);
                        break;
                    case Op.SMSG_CANCEL_COMBAT:
                        if (body.Length != 0)
                            throw new InvalidDataException(
                                $"SMSG_CANCEL_COMBAT expected empty body, got {body.Length}");
                        EnqueueSpellPresentation(new SpellCombatCancelledEvent(net.PlayerGuid));
                        break;
                    case Op.SMSG_CANCEL_AUTO_REPEAT:
                        if (body.Length != 0)
                            throw new InvalidDataException(
                                $"SMSG_CANCEL_AUTO_REPEAT expected empty body, got {body.Length}");
                        EnqueueSpellPresentation(new SpellAutoRepeatCancelledEvent(net.PlayerGuid));
                        break;
                    case Op.SMSG_LOOT_RESPONSE:
                        ApplyLootResponse(body);
                        break;
                    case Op.SMSG_LOOT_REMOVED:
                        ApplyLootRemoved(body);
                        break;
                    case Op.SMSG_LOOT_CLEAR_MONEY:
                        ApplyLootClearMoney();
                        break;
                    case Op.SMSG_LOOT_RELEASE_RESPONSE:
                        ApplyLootReleaseResponse(body);
                        break;
                    case Op.SMSG_LOOT_MONEY_NOTIFY:
                        break; // the purse rides PLAYER_FIELD_COINAGE; nothing to do
                    case Op.SMSG_LOOT_START_ROLL:
                        ApplyLootStartRoll(body);
                        break;
                    case Op.SMSG_LOOT_ROLL:
                        ApplyLootRoll(body);
                        break;
                    case Op.SMSG_LOOT_ROLL_WON:
                        ApplyLootRollWon(body);
                        break;
                    case Op.SMSG_LOOT_ALL_PASSED:
                        ApplyLootAllPassed(body);
                        break;
                    case Op.SMSG_ITEM_PUSH_RESULT:
                        ApplyItemPushResult(body);
                        break;
                    case Op.SMSG_ITEM_ENCHANT_TIME_UPDATE:
                        ApplyItemEnchantTime(body);
                        break;
                    case Op.SMSG_INVENTORY_CHANGE_FAILURE:
                        ApplyInventoryChangeFailure(body);
                        break;
                    case Op.SMSG_MOUNTRESULT:
                        ApplyMountResult(body, mount: true);
                        break;
                    case Op.SMSG_DISMOUNTRESULT:
                        ApplyMountResult(body, mount: false);
                        break;
                    case Op.SMSG_MOUNTSPECIAL_ANIM:
                    {
                        ulong rider = MountSpecialPackets.ParseGuid(body);
                        // The sender already played locally on the key edge. Some VMaNGOS
                        // configurations echo this broadcast to self and some do not.
                        if (rider != LocalPlayerGuid || ControlledBodyIsStreamed)
                            _creatures?.TriggerMountFlourish(rider);
                        break;
                    }
                    case Op.SMSG_ATTACKSTART:
                    case Op.SMSG_ATTACKSTOP:
                    case Op.SMSG_ATTACKERSTATEUPDATE:
                    case Op.SMSG_SPELLNONMELEEDAMAGELOG:
                    case Op.SMSG_PERIODICAURALOG:
                    case Op.SMSG_SPELLHEALLOG:
                    case Op.SMSG_SPELLENERGIZELOG:
                    case Op.SMSG_SPELLDAMAGESHIELD:
                    case Op.SMSG_ENVIRONMENTALDAMAGELOG:
                    case Op.SMSG_SPELLLOGMISS:
                    case Op.SMSG_SPELLDISPELLOG:
                    case Op.SMSG_DISPEL_FAILED:
                    case Op.SMSG_SPELLINSTAKILLLOG:
                    case Op.SMSG_PARTYKILLLOG:
                    case Op.SMSG_ENCHANTMENTLOG:
                    case Op.SMSG_SPELLLOGEXECUTE:
                    case Op.SMSG_PROCRESIST:
                    case Op.SMSG_SPELLORDAMAGE_IMMUNE:
                    case Op.SMSG_LOG_XPGAIN:
                        CombatEvent combatEvent = _combat.Apply(
                            CombatPacketParser.Parse((Op)opcode, body), _entities);
                        ObserveCombatReceive((Op)opcode, combatEvent);
                        ObserveChannelCombat(combatEvent);
                        ApplyCombatAnimation(combatEvent);
                        break;
                    case Op.SMSG_PVP_CREDIT:
                        ApplyPvpCredit(body);
                        break;
                    case Op.SMSG_GAMEOBJECT_QUERY_RESPONSE:
                        ApplyGameObjectQuery(body);
                        break;
                    case Op.SMSG_LEVELUP_INFO:
                        ApplyLevelUpInfo(body);
                        break;
                    case Op.SMSG_EXPLORATION_EXPERIENCE:
                        ApplyExplorationExperience(body);
                        break;
                    case Op.SMSG_RESURRECT_REQUEST:
                        ApplyResurrectRequest(body);
                        break;
                    case Op.SMSG_PLAYER_SKINNED:
                        ApplyPlayerSkinned(body);
                        break;
                    case Op.MSG_CORPSE_QUERY:
                        ApplyCorpseQuery(body);
                        break;
                    case Op.SMSG_CORPSE_RECLAIM_DELAY:
                        ApplyCorpseReclaimDelay(body);
                        break;
                    case Op.SMSG_SPIRIT_HEALER_CONFIRM:
                        ApplySpiritHealerConfirm(body);
                        break;
                    case Op.SMSG_DURABILITY_DAMAGE_DEATH:
                        ApplyDurabilityDamageDeath(body);
                        break;
                    case Op.SMSG_START_MIRROR_TIMER:
                        ApplyMirrorTimerStart(body, net.PlayerGuid);
                        break;
                    case Op.SMSG_PAUSE_MIRROR_TIMER:
                        ApplyMirrorTimerPause(body, net.PlayerGuid);
                        break;
                    case Op.SMSG_STOP_MIRROR_TIMER:
                        ApplyMirrorTimerStop(body, net.PlayerGuid);
                        break;
                    case Op.SMSG_ATTACKSWING_NOTINRANGE:
                    case Op.SMSG_ATTACKSWING_BADFACING:
                    case Op.SMSG_ATTACKSWING_NOTSTANDING:
                    case Op.SMSG_ATTACKSWING_DEADTARGET:
                    case Op.SMSG_ATTACKSWING_CANT_ATTACK:
                        ObserveCombatError((Op)opcode, body);
                        break;
                    case Op.SMSG_MESSAGECHAT:
                        HandleMessageChat(body);       // typed+coloured display
                        ObserveGmChatResponse(body);   // devtools GM-mode probe (no display)
                        break;
                    case Op.SMSG_CHANNEL_NOTIFY:
                        HandleChannelNotice(body);
                        break;
                    case Op.SMSG_CHANNEL_LIST:
                        HandleChannelList(body);
                        break;
                    case Op.SMSG_CHAT_PLAYER_NOT_FOUND:
                        AddChatMessage($"No player named '{ChatPackets.ParsePlayerNotFound(body)}' is currently playing.");
                        break;
                    case Op.SMSG_CHAT_WRONG_FACTION:
                        if (body.Length != 0) throw new InvalidDataException("SMSG_CHAT_WRONG_FACTION body must be empty");
                        AddChatMessage("You can only whisper to members of your alliance.");
                        break;
                    case Op.SMSG_PLAY_TIME_WARNING:
                        ApplyPlayTimeWarning(body);
                        break;
                    case Op.SMSG_PLAYED_TIME:
                        HandlePlayedTime(body);
                        break;
                    case Op.SMSG_QUERY_TIME_RESPONSE:
                        {
                            var clock = new PacketReader(body);
                            _questServerUnix = clock.ReadU32();
                            if (clock.Remaining != 0)
                                throw new InvalidDataException("SMSG_QUERY_TIME_RESPONSE must be one u32");
                            _questServerUnixStamp = receivedStamp;
                        }
                        break;
                    case Op.SMSG_PET_NAME_QUERY_RESPONSE:
                        {
                            PetNameQueryResponse response = PetNamePackets.ParseResponse(body);
                            _queriedPetNames.Remove(response.PetNumber);
                            _petNameTimestamps[response.PetNumber] = response.Timestamp;
                            if (response.Name.Length > 0)
                                _petNames[response.PetNumber] = response.Name;
                            else
                                _petNames.Remove(response.PetNumber);
                        }
                        break;
                    case Op.MSG_RANDOM_ROLL:
                        HandleRandomRoll(body);
                        break;
                    case Op.SMSG_NOTIFICATION:
                        // The reference routes this to the red UIErrorsFrame (handler 0x401800 →
                        // UI_ERROR_MESSAGE), not the chat frame; vmangos SetGameMaster sends BOTH a
                        // SysMessage and a Notification, so a chat sink printed "GM mode is ON" twice.
                        ShowUiError(new PacketReader(body).ReadCString());
                        ObserveGmChatResponse(body);   // devtools GM-mode probe (no display)
                        break;
                    case Op.SMSG_SUMMON_REQUEST:
                        ApplySummonRequest(body);
                        break;
                    case Op.SMSG_QUEST_CONFIRM_ACCEPT:
                        ApplyQuestConfirmAccept(body);
                        break;
                    case Op.SMSG_LOOT_MASTER_LIST:
                        ApplyLootMasterList(body);
                        break;
                    case Op.SMSG_SERVER_MESSAGE:
                        ApplyServerMessage(body);
                        break;
                    case Op.SMSG_ZONE_UNDER_ATTACK:
                        ApplyZoneUnderAttack(body);
                        break;
                    case Op.SMSG_DEFENSE_MESSAGE:
                        ApplyDefenseMessage(body);
                        break;
                    case Op.SMSG_CHAT_RESTRICTED:
                        ShowUiError(InventoryGlobalString("ERR_CHAT_RESTRICTED",
                            "Your account has been restricted from chat."));
                        break;
                    case Op.SMSG_TEXT_EMOTE:
                        HandleTextEmoteReceive(body);
                        break;
                    case Op.SMSG_EMOTE:
                        ApplyEmote(EmotePackets.Parse(body));
                        break;
                }
                // Packet-side half of the questgiver status refresh law. This runs only after
                // the selected handler returned successfully, so a malformed packet never
                // invalidates every visible giver's cached answer.
                if (QuestStatusRefreshLaw.PacketReasks(packetOpcode))
                    BumpQuestStatusReask();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[net] parse error on opcode 0x{opcode:X4}: {ex.Message}");
                if (worldBoundaryReached)
                {
                    // Stopping only this frame's drain is insufficient: the
                    // socket queue may already contain destination object
                    // updates, which the next frame would otherwise apply to
                    // the old world. A malformed/duplicate world boundary is
                    // unrecoverable without a fresh authenticated session.
                    string reason = $"invalid {(Op)opcode} world boundary: {ex.Message}";
                    if (_worldportAckPending)
                        AbortPendingWorldportAdoption(reason);
                    else
                    {
                        _queuedWorldEntry = null;
                        _worldEntryTransitionStage = 0;
                        _travelStatus = $"world entry blocked: {reason}";
                        CancelPendingWorldCurtain();
                        _net?.Stop();
                    }
                }
            }
            if (parseStarted || worldBoundaryReached) break;
        }

    FinishNetPump:
        _netUpdatesLastFrame = updates;

        // Spell packet parsing is intentionally side-effect free.  Apply its ordered edge stream
        // only after the current object-update slice, before any animation/spline simulation.
        DrainSpellPresentationEvents();

        // Advance every in-progress creature spline so NPCs actually move between packets.
        _entities.TickSplines(MovementInfo.ClientUptimeMs());
        if (TryGetWorldBodyPose(net.PlayerGuid, out WorldBodyPose sessionBody))
            _entities.FaceIdleTargets(dt, net.PlayerGuid, sessionBody.Position);
        DiscoverItemTemplates();
    }

    private void CaptureWirePacket(bool outgoing, ushort opcode, ReadOnlySpan<byte> payload)
    {
        var packet = new WirePacket(NowSeconds(), outgoing, opcode,
            WireRing.NameFor(opcode), payload.Length);
        ReadOnlySpan<byte> visiblePayload = WireLogRecorder.ShouldStorePayload(opcode)
            ? payload
            : ReadOnlySpan<byte>.Empty;
        _wire.Add(packet, visiblePayload);
        _wireLog.Enqueue(packet, payload);
        if ((Op)opcode is Op.CMSG_MESSAGECHAT or Op.SMSG_MESSAGECHAT or Op.SMSG_NOTIFICATION)
            ObserveGmChatWire(outgoing, opcode, payload);
        if ((Op)opcode is Op.MSG_MOVE_TELEPORT_ACK or Op.SMSG_NEW_WORLD or Op.SMSG_TRANSFER_PENDING)
            ObserveTeleportWire(outgoing, opcode, payload);
        if (outgoing) _creatureLifecycle.NoteOutgoingPacket(opcode, payload.Length);
    }

    /// <summary>Build 5875 server->client same-map teleport (MSG_MOVE_TELEPORT_ACK). Direct
    /// for the session body; proxied (SMSG_SUI_PROXY) for a possessed bot, whose near teleport
    /// keeps the possession since 2026-09-03 — the controller snaps and acks for the bot.</summary>
    private void ApplyMoveTeleportAck(NetworkClient net, byte[] body)
    {
                            // Build 5875 server->client same-map teleport:
                            // packed mover guid, movement counter, destination MovementInfo.
                            var teleportReader = new PacketReader(body);
                            ulong moverGuid = teleportReader.ReadPackedGuid();
                            uint counter = teleportReader.ReadU32();
                            MovementInfo destination = MovementInfo.Read(teleportReader);
                            if (teleportReader.Remaining != 0)
                                throw new InvalidDataException(
                                    $"MSG_MOVE_TELEPORT_ACK has {teleportReader.Remaining} trailing byte(s)");
                            // [SUI] The mirrored copy addresses the possessed bot: its mover guid is
                            // ControlledGuid and the controller path below adopts it.
                            if (_controller is null || (moverGuid != net.PlayerGuid && moverGuid != ControlledGuid))
                            {
                                Console.WriteLine($"[net] ignored same-map teleport for mover 0x{moverGuid:X16} " +
                                                  $"(player 0x{net.PlayerGuid:X16})");
                                return;
                            }

                            // The packet addresses the logged-in session body. During Free View
                            // the controller is only the observer rig; while possessing, it belongs
                            // to a different body. Adopt the authoritative teleport directly on the
                            // streamed entity and ACK it without moving the camera, changing its
                            // residency centre, or resetting the active controller's movement state.
                            if (moverGuid != ControlledGuid || !ControllerOwnsControlledBodyPose)
                            {
                                _entities.ApplyServerAuthoredMove(
                                    moverGuid, destination, MovementInfo.ClientUptimeMs());
                                ObserveTeleportApplied(moverGuid, counter, destination);
                                net.TeleportAck(moverGuid, counter);
                                Console.WriteLine(
                                    $"[net] adopted same-map teleport for streamed session body " +
                                    $"0x{moverGuid:X16} at ({destination.Position.X:F1}, " +
                                    $"{destination.Position.Y:F1}, {destination.Position.Z:F1}); " +
                                    "observer rig retained");
                                return;
                            }

                            // A same-map teleport can still be thousands of yards
                            // away.  The old handler treated "same map" as "same
                            // resident scene": it acknowledged immediately, then
                            // gravity ran while the destination ADTs/WMO collision
                            // were still streaming.  The hitch recorder caught the
                            // resulting fall from Z 29.6 to below -100.  Cross-map
                            // NEW_WORLD already owns an opaque, collision-gated
                            // adoption; give a non-resident same-map destination the
                            // same guarantee.
                            bool destinationResident =
                                MainWorldHasArrivalSupport(destination.Position);
                            if (!destinationResident && _gl is null)
                            {
                                Console.WriteLine(
                                    "[net] FATAL: same-map teleport destination has no resident " +
                                    "support and the world loader is unavailable; disconnecting without ACK");
                                net.Stop();
                                return;
                            }

                            bool promotedPreparedWorld = false;
                            if (!destinationResident)
                            {
                                uint destinationMapId = checked((uint)_config.Start.Map);
                                bool matchedPreparedPortal = ConfirmRealPortalHandoff(
                                    destinationMapId, destination.Position);
                                promotedPreparedWorld = matchedPreparedPortal &&
                                    TryPromotePreparedRealPortalWorld(
                                        destinationMapId, destination.Position);

                                if (!promotedPreparedWorld)
                                {
                                    TearDownWorldContent();
                                    _residentCentre = null;
                                    _hitch.SuppressFor(5.0);
                                }

                                _window.Camera.EffectiveDistance = _window.Camera.Distance;
                            }

                            // A teleport supersedes a server ride. In particular, a taxi landing
                            // teleport is the hand-back and must not also emit SPLINE_DONE.
                            AbortServerRideForTeleport();

                            // Apply on the game thread before acknowledging. Camera yaw is the
                            // next frame's MovementInput yaw, so updating it is what makes the
                            // server orientation survive rather than being overwritten immediately.
                            _config.Start.X = destination.Position.X;
                            _config.Start.Y = destination.Position.Y;
                            _config.Start.Z = destination.Position.Z;
                            _config.Start.Orientation = destination.Orientation;
                            _controller.Teleport(destination.Position.X, destination.Position.Y,
                                destination.Position.Z);
                            _controller.Yaw = destination.Orientation;
                            _window.Camera.Yaw = destination.Orientation;
                            _window.Camera.OrbitYaw = 0f;
                            _window.Camera.Target = _controller.Position;
                            _character?.SnapFacing(destination.Orientation);
                            _movementSender.Reset(destination.Orientation);
                            ObserveTeleportApplied(moverGuid, counter, destination);
                            HoldAreaTriggersAfterTeleport();

                            if (promotedPreparedWorld)
                            {
                                CompletePromotedRealPortalTransition();
                                Console.WriteLine(
                                    "[net] same-map teleport adopted the prepared destination without loading");
                            }
                            else if (!destinationResident)
                            {
                                try
                                {
                                    BeginWorldLoad(_gl!);
                                    var tile = TerrainRenderer.TileAt(
                                        destination.Position.X, destination.Position.Y);
                                    Console.WriteLine(
                                        $"[net] same-map teleport is non-resident; loading " +
                                        $"destination tile [{tile.col},{tile.row}] behind the curtain");
                                }
                                catch
                                {
                                    // Do not tell the core a destination was adopted
                                    // when the client could not even arm its loader.
                                    CancelRealPortalHandoff(
                                        "destination loader could not be started");
                                    net.Stop();
                                    throw;
                                }
                            }
                            else
                            {
                                // No curtain is needed when the main scene already
                                // supports the authoritative landing point.
                                CancelRealPortalHandoff(
                                    "authoritative destination was already resident");
                            }
                            net.TeleportAck(moverGuid, counter);
    }

    private void ApplyUpdate(ObjectUpdate u, long receivedStamp)
    {
        _entities.TryGet(u.Guid, out WorldEntity? auraUnitBefore);
        bool? readsDeadBefore = auraUnitBefore?.Fields.ReadsDead;
        // PLAYERS ONLY - see the level-up guard below for why a creature must never reach it.
        uint? levelBefore = u.Kind == UpdateKind.Values && auraUnitBefore?.IsPlayer == true
            ? auraUnitBefore.Fields.GetU32(ObjectFields.UNIT_LEVEL) : null;
        uint? playerFlagsBefore = auraUnitBefore?.IsPlayer == true
            ? auraUnitBefore.Fields.PlayerFlags : null;
        Dictionary<byte, AuraSnapshot> aurasBefore = SnapshotAuras(auraUnitBefore);
        if ((u.Kind is UpdateKind.CreateObject or UpdateKind.CreateObject2) &&
            u.Type == ObjectTypeId.Unit)
            _creatureLifecycle.NoteSpawnPacket(
                u.Guid, u.Fields?.DisplayId ?? 0, receivedStamp);
        if (u.Kind == UpdateKind.OutOfRange && u.Guids is not null)
        {
            foreach (ulong guid in u.Guids)
                _creatureLifecycle.NoteReason(
                    guid, CreatureLifecycleTracker.ReasonCode.NOT_IN_WORLD);
            ForgetCreatureVoiceState(u.Guids);
        }
        _entities.Apply(u, MovementInfo.ClientUptimeMs());
        if ((u.Kind is UpdateKind.CreateObject or UpdateKind.CreateObject2) &&
            (u.Type is ObjectTypeId.Unit or ObjectTypeId.Player))
            _combat.ApplySnapshot(u.Guid, u.Movement?.MeleeTarget, _entities);
        // A CREATURE'S LEVEL CHANGING IS NOT A LEVEL-UP, IT IS A RESPAWN, and this used to
        // fire the hardcoded Unit Level Up kit on every one of them.
        //
        // vmangos re-rolls a creature's level every time it respawns - Creature::SelectLevel,
        // `urand(level_min, level_max)` - so any creature whose template has a level RANGE
        // (most of them) comes back at a new level. The corpse never left view, so that
        // arrives as a VALUES update on an entity the client already holds, and the old test
        // (any IsUnit, `newLevel != oldLevel`) read it as "this unit levelled". The kit then
        // played straight over the two-second appear fade that CreatureRenderer had correctly
        // armed on the dead-to-alive edge. Reported 2026-09-01.
        //
        // Two corrections, both needed: PLAYERS only, since nothing in 1.12 levels a creature
        // up in front of you; and an INCREASE only, since a re-roll is as likely to go down
        // and a level loss is not a celebration either.
        if (levelBefore is uint oldLevel &&
            _entities.TryGet(u.Guid, out WorldEntity leveledUnit) && leveledUnit.IsPlayer &&
            leveledUnit.Fields.GetU32(ObjectFields.UNIT_LEVEL) is uint newLevel &&
            newLevel > oldLevel)
            PlayHardcodedUnitLevelUp(u.Guid, newLevel);
        if (u.Guid != 0 && _entities.TryGet(u.Guid, out WorldEntity updatedPlayer) &&
            updatedPlayer.IsPlayer && playerFlagsBefore != updatedPlayer.Fields.PlayerFlags)
        {
            if (u.Guid == _net?.PlayerGuid)
                _equipmentDisplayPreferences.Observe(updatedPlayer.Fields.PlayerFlags);
            if (u.Guid == ControlledGuid)
            {
                ApplyControlledCharacter();
                if (_dressUpOpen) RebuildDressUpLook();
            }
        }
        if (u.Guid != 0) ObserveCreatureDeathVoice(u.Guid, readsDeadBefore);
        if (u.Guid == ControlledGuid && _entities.TryGet(u.Guid, out WorldEntity controlledMover))
            SyncControlledSpeeds(controlledMover);
        if ((u.Kind is UpdateKind.CreateObject or UpdateKind.CreateObject2) &&
            u.Type == ObjectTypeId.GameObject && _entities.TryGet(u.Guid, out WorldEntity streamedGo))
            RequireGameObjectTemplate(streamedGo);
        if (u.Guid == _net?.PlayerGuid)
        {
            ObserveQuestLog();
            ObserveRestXp();
            ObserveDeathRez();
            if (_entities.TryGet(u.Guid, out WorldEntity player))
                ObservePlayerCombatTextState(player);
        }
        ObserveProfessionProductTransition();
        if (u.Type == ObjectTypeId.Corpse || (u.Guid != 0 && _entities.TryGet(u.Guid, out WorldEntity corpse) && corpse.Type == ObjectTypeId.Corpse)) ObserveCorpseStore();
        ObserveAuraObjectUpdate(u.Guid, aurasBefore);
        if ((u.Kind is UpdateKind.CreateObject or UpdateKind.CreateObject2) &&
            u.Type == ObjectTypeId.Unit && _creaturesLogged < 50)
        {
            _creaturesLogged++;
            var p = u.Movement?.Position ?? Vector3.Zero;
            Console.WriteLine($"[net] creature entry {u.Fields?.Entry ?? GuidInfo.Entry(u.Guid) ?? 0} " +
                              $"display {u.Fields?.DisplayId ?? 0} L{u.Fields?.Level ?? 0} at ({p.X:F0}, {p.Y:F0}, {p.Z:F0})");
        }
    }

    /// <summary>
    /// Configure the player avatar from the logged-in roster character instead of the
    /// offline test body. Faithful to benilla: the picked roster Character carries every
    /// appearance byte AND 19 already-resolved equipment display ids, which is sufficient
    /// to build the correct model at spawn (benilla's char-select preview builds the exact
    /// same composited model straight from the roster).
    /// </summary>
    private void ApplyServerCharacter(bool rebuild = true)
    {
        if (_character is null) return;
        Character? c = _net?.Player;
        if (c is null) { _character.Enabled = true; return; }

        string race = RaceFolder(c.Race);
        string gender = c.Gender == 1 ? "Female" : "Male";

        bool sameBody = race.Equals(_character.Race, StringComparison.OrdinalIgnoreCase) &&
                        gender.Equals(_character.Gender, StringComparison.OrdinalIgnoreCase);
        bool streamed = _entities.TryGet(c.Guid, out WorldEntity player) && player.IsPlayer;
        uint playerFlags = streamed ? player.Fields.PlayerFlags : c.Flags;
        // Once the body is in the world it is built the same way every other body is. The
        // roster-only kit survives strictly for the pre-entity case: glue and character select.
        CharacterEquipment equipment = streamed
            ? BuildVisibleItemKit(player, c)
            : BuildEquipment(c, playerFlags);
        ReportLocalKit(streamed ? "visible-items" : "roster", equipment);
        // SyncLiveEquipmentModel is keyed on a signature these fields do not move, so without
        // this it would early-out forever after any path through here. Every exit below either
        // installs this kit or leaves an older one in place; invalidating covers all of them.
        InvalidateLiveEquipment();
        bool sameAppearance = sameBody &&
                              _character.SkinId == c.Skin &&
                              _character.FaceId == c.Face &&
                              _character.HairStyleId == c.HairStyle &&
                              _character.HairColorId == c.HairColor &&
                              _character.FacialHairId == c.FacialHair &&
                              EquipmentVisuallyMatches(_character.Equipment, equipment);

        if (!rebuild && sameAppearance)
        {
            _character.Enabled = true;
            _playerPortraitDirty = true;
            Console.WriteLine($"[character] player model reused: {race} {gender} " +
                              $"({c.Equipment.Count(e => e.DisplayId != 0)} equipped)");
            return;
        }

        if (sameBody && _character.QueueAppearanceUpdate(
                c.Skin, c.Face, c.HairStyle, c.HairColor, c.FacialHair, equipment))
        {
            _character.Enabled = true;
            _playerPortraitDirty = true;
            Console.WriteLine($"[character] queued async player appearance diff: {race} {gender} " +
                              $"({c.Equipment.Count(e => e.DisplayId != 0)} equipped)");
            return;
        }

        // Re-load the base model only if the race/gender differs from what is loaded
        // (the offline test body is Human/Male; most logins will differ).
        if (!race.Equals(_character.Race, StringComparison.OrdinalIgnoreCase) ||
            !gender.Equals(_character.Gender, StringComparison.OrdinalIgnoreCase))
        {
            if (!_character.Load(race, gender))
                Console.WriteLine($"[character] could not load {race} {gender}; keeping current body");
        }

        _character.SkinId = c.Skin;
        _character.FaceId = c.Face;
        _character.HairStyleId = c.HairStyle;
        _character.HairColorId = c.HairColor;
        _character.FacialHairId = c.FacialHair;
        _character.Equipment = equipment;
        _character.Reload();          // rebuild texture slots + geosets, then composite the gear
        _character.Enabled = true;
        _playerPortraitDirty = true;

        Console.WriteLine($"[character] player model: {race} {gender} " +
                          $"skin {c.Skin} face {c.Face} hair {c.HairStyle}/{c.HairColor} facial {c.FacialHair} " +
                          $"({c.Equipment.Count(e => e.DisplayId != 0)} equipped)");
    }

    private void MarkPendingWorldportAck(uint destinationMapId)
    {
        if (_worldportAckPending)
        {
            uint previousMapId = _pendingWorldportMapId;
            AbortPendingWorldportAdoption(
                $"received a second NEW_WORLD for map {destinationMapId} while map {previousMapId} still awaited adoption");
            throw new InvalidDataException(
                $"duplicate NEW_WORLD while map {previousMapId} awaited adoption");
        }
        _worldportAckPending = true;
        _pendingWorldportMapId = destinationMapId;
    }

    /// <summary>
    /// Whether the currently installed main-world scene can safely reveal a
    /// server teleport at <paramref name="arrival"/>.  Preview-scene readiness
    /// is intentionally irrelevant here: its terrain and collision are isolated
    /// and cannot support the live character after the authoritative ACK.
    /// </summary>
    private bool MainWorldHasArrivalSupport(in Vector3 arrival)
    {
        if (PortalArrivalLaw.HasNearbySupport(
                arrival, _terrain?.SampleHeight(arrival.X, arrival.Y)))
            return true;

        var floor = _collision?.Raycast(
            arrival + new Vector3(0f, 0f, 3f), -Vector3.UnitZ, 80f);
        return PortalArrivalLaw.HasNearbySupport(arrival, floor?.Point.Z);
    }

    /// <summary>
    /// Consume the one ACK owned by the current NEW_WORLD after its destination
    /// scene/map/pose have been adopted. This is private but callable from any
    /// GameLoop partial, including the prepared-scene promotion path.
    /// </summary>
    private bool CompletePendingWorldportAckAfterAdoption(uint adoptedMapId)
    {
        if (!_worldportAckPending) return true;

        if (adoptedMapId != _pendingWorldportMapId)
        {
            AbortPendingWorldportAdoption(
                $"adopted map {adoptedMapId} did not match pending map {_pendingWorldportMapId}");
            return false;
        }

        // Consume before touching the socket. A failed/closed session cannot be
        // made safe by duplicating the ACK later, and a subsequent login must not
        // inherit this transfer's ownership token.
        uint destinationMapId = _pendingWorldportMapId;
        _worldportAckPending = false;
        _pendingWorldportMapId = 0;

        if (_net?.WorldportAck() == true)
        {
            Console.WriteLine($"[net] acknowledged adopted world map {destinationMapId}");
            return true;
        }
        else
        {
            // Retrying an ambiguously failed TCP write can duplicate the ACK;
            // continuing without it leaves the core waiting forever. Close the
            // session and require a clean login instead.
            Console.WriteLine(
                $"[net] FATAL: could not acknowledge adopted world map {destinationMapId}; disconnecting");
            _net?.Stop();
            return false;
        }
    }

    private void AbortPendingWorldportAdoption(string reason)
    {
        uint destinationMapId = _pendingWorldportMapId;
        _worldportAckPending = false;
        _pendingWorldportMapId = 0;
        _pendingTransfer = null;
        _queuedWorldEntry = null;
        _worldEntryTransitionStage = 0;
        _worldLoading = false;
        _worldLoadingMapId = null;
        _loadPhase = WorldLoadPhase.Done;
        _worldLoadStarted = false;
        _worldShown = false;
        _travelStatus = $"world transfer blocked: {reason}";
        Console.WriteLine(
            $"[net] FATAL: refused worldport map {destinationMapId}: {reason}; disconnecting without ACK");
        CancelPendingWorldCurtain();
        _net?.Stop();
    }

    private void DiscardPendingNetApplicationState()
    {
        _queuedWorldEntry = null;
        _pendingTransfer = null;
        _worldEntryTransitionStage = 0;

        // A parser task owns the buffer instance it captured. If it is still
        // running, detach a fresh buffer rather than clearing memory it may be
        // writing concurrently; the abandoned task then completes harmlessly.
        if (_pendingObjectParse is not null)
            _objectUpdateBuffer = new ObjectUpdateBuffer(12_000);
        else
            _objectUpdateBuffer.Clear();
        _pendingObjectParse = null;
        _pendingObjectUpdates?.Clear();
        _pendingObjectUpdates = null;
        _pendingObjectUpdateIndex = 0;
        _pendingObjectReceivedStamp = 0;
    }

    // Optional scene/transit ownership lives in another GameLoop partial. Parsing
    // remains here even when the hooks have no implementation, so malformed wire
    // packets are still rejected at the network boundary.
    partial void ApplyRealPortalDescriptor(PortalDescriptorPacket descriptor);
    partial void ApplyRealPortalState(PortalStatePacket state);

    /// <summary>
    /// Enter-world ownership transfer is deliberately outside <see cref="PumpNet"/>.  The load
    /// bootstrap and avatar hand-off are independent bounded stages, so neither can turn a login
    /// packet into a long network-pump frame and the loader cannot stack work on either frame.
    /// </summary>
    private bool PumpWorldEntryTransition()
    {
        if (_worldEntryTransitionStage == 0) return false;

        if (_worldEntryTransitionStage == 1)
        {
            // Auto-login can leave CharacterSelect on the network thread before the booth ever
            // renders. Materialize the selected roster avatar now, while the curtain is already
            // opaque but before BeginWorldLoad starts the measured interval.
            if (_net?.Player is { } rosterAvatar) _booth?.SetCharacter(rosterAvatar);
            _worldEntryTransitionStage = 2;
            return true;
        }

        if (_worldEntryTransitionStage == 2)
        {
            // Give the now-authoritative in-world HUD one invisible build while
            // the already-armed curtain is up, before BeginWorldLoad starts the
            // measured interval. Gui marks the prime complete later this frame.
            _worldEntryTransitionStage = 3;
            return true;
        }

        if (_worldEntryTransitionStage == 3)
        {
            if (_gl is not null) BeginWorldLoad(_gl);
            _worldEntryTransitionStage = 4;
            return true;
        }

        bool reusedSelectedAvatar = false;
        if (_net?.Player is { } selected &&
            _booth?.TakeCharacter(selected.Guid) is { } selectedAvatar)
        {
            selectedAvatar.CopyRuntimeTuningFrom(_character);
            _character?.Dispose();
            _character = selectedAvatar;
            reusedSelectedAvatar = true;
            Console.WriteLine("[character] adopted cached character-select avatar");
        }

        _glue?.Dispose(); _glue = null;
        _booth?.Dispose(); _booth = null;
        ApplyServerCharacter(rebuild: !reusedSelectedAvatar);
        _worldEntryTransitionStage = 0;
        return true;
    }

    /// <summary>
    /// The one way an in-world body's kit is built, local or remote: public visible-item entries
    /// resolved through ItemTemplate, so every piece carries the sheath/class/subclass/material
    /// bytes that the attachment point and the sheathe cue are both read from.
    ///
    /// The local body used to be the exception. <see cref="BuildEquipment"/> knows only display
    /// ids and inventory types, leaving all four of those bytes at zero. Zero sheath resolves to
    /// attachment -1, which does not mean "somewhere else", it means DRAW NOTHING, so stowing a
    /// weapon deleted it; and the cue table has no class-zero row, so the same gap silenced the
    /// sound. One builder, one set of bytes, both symptoms gone.
    ///
    /// A slot whose template is still in flight falls back to <paramref name="rosterFallback"/>
    /// display ids where there are any: display-only and still sheath-blind, but never worse
    /// than the roster kit it replaces, and upgraded on the next rebuild.
    /// </summary>
    private CharacterEquipment BuildVisibleItemKit(WorldEntity unit, Character? rosterFallback)
    {
        var kit = new CharacterEquipment();
        uint flags = unit.Fields.PlayerFlags;
        for (int slot = 0; slot < 19; slot++)
        {
            // Hoisted above BOTH branches. Slot-keyed and inventory-type-keyed forms of this
            // law say the same thing (head/cloak), and checking it only on the template branch
            // would let a hidden helm reappear through the roster fallback.
            if (!EquipmentDisplayPreferenceLaw.EquipmentSlotShown(slot, flags)) continue;
            uint entry = unit.Fields.PlayerVisibleItemEntry(slot);
            if (entry != 0 && _items is not null)
            {
                if (_net is not null) _items.Require(entry, unit.Guid, _net);
                if (_items.TryGet(entry, out ItemTemplate? t) && t is not null &&
                    t.DisplayInfoId != 0)
                {
                    kit.Add(t.Name, t.DisplayInfoId, (int)t.InventoryType, slot,
                        (byte)t.Class, (byte)t.Subclass, (byte)t.Material, (byte)t.Sheath,
                        Enumerable.Range(0, 7)
                            .Select(enchantSlot =>
                                unit.Fields.PlayerVisibleItemEnchant(slot, enchantSlot))
                            .ToArray());
                    continue;
                }
            }
            if (rosterFallback is null || slot >= rosterFallback.Equipment.Length) continue;
            var eq = rosterFallback.Equipment[slot];
            if (eq.DisplayId == 0) continue;
            kit.Add($"slot{slot}", eq.DisplayId, eq.InventoryType, slot);
        }
        return kit;
    }

    private string _reportedLocalKit = "";

    /// <summary>
    /// What the local body is wearing in its weapon slots and which builder produced it,
    /// printed only when the answer changes. A sheath of 0 on a real weapon here is the
    /// signature of the roster kit having won, and is exactly why a stowed weapon vanished.
    /// </summary>
    private void ReportLocalKit(string source, CharacterEquipment kit)
    {
        string hands = string.Join(" | ", new[] { 15, 16, 17 }
            .Select(slot => kit.Pieces.FirstOrDefault(piece => piece.EquipmentSlot == slot))
            .Where(piece => piece is not null)
            .Select(piece => $"slot{piece!.EquipmentSlot} [{piece.Name}] display={piece.DisplayId}" +
                $" sheath={piece.Sheath} class={piece.ItemClass}/{piece.ItemSubclass}" +
                $" material={piece.Material}"));
        string line = $"[equip] local kit from {source}: " +
            (hands.Length == 0 ? "no weapon slots" : hands);
        if (line == _reportedLocalKit) return;
        _reportedLocalKit = line;
        Console.WriteLine(line);
    }

    /// <summary>
    /// Roster-only kit: display ids and inventory types, nothing else. Correct ONLY before the
    /// body has streamed in (glue and character select). Once there is an entity, every caller
    /// goes through <see cref="BuildVisibleItemKit"/> instead.
    /// </summary>
    private static CharacterEquipment BuildEquipment(Character c, uint playerFlags)
    {
        var kit = new CharacterEquipment();
        for (int i = 0; i < c.Equipment.Length; i++)
        {
            var eq = c.Equipment[i];
            if (eq.DisplayId == 0) continue;
            int inv = eq.InventoryType;
            if (!EquipmentDisplayPreferenceLaw.InventoryTypeShown(inv, playerFlags)) continue;
            kit.Add($"slot{i}", eq.DisplayId, inv, i);
        }
        return kit;
    }

    /// <summary>
    /// Rebuild the first-person avatar as the CONTROLLED unit. For a possessed bot the look
    /// comes from its streamed entity fields (appearance bytes + public visible-item entries —
    /// the same recipe CreatureRenderer uses for remote players); for the session character it
    /// falls back to the roster-driven <see cref="ApplyServerCharacter"/>.
    /// </summary>
    private void ApplyControlledCharacter()
    {
        if (_character is null) return;
        ulong guid = ControlledGuid;
        _controlledBodyPending = false;
        ResetSheathMirror();
        RefreshControlledCharacterScale();
        if (guid == LocalPlayerGuid)
        {
            ApplyServerCharacter(rebuild: false);
            return;
        }
        if (!_entities.TryGet(guid, out WorldEntity bot) || !bot.IsPlayer)
        {
            // Possession granted before the bot's entity streamed in (far-away
            // Alt+click). UpdateControlInput retries until the fields arrive so the
            // body and portrait don't stay stuck on the session character.
            _controlledBodyPending = true;
            return;
        }

        (byte race, _, byte gender, _) = bot.Fields.Bytes0;
        (byte skin, byte face, byte hairStyle, byte hairColor) = bot.Fields.PlayerAppearance;
        string raceFolder = RaceFolder(race);
        string genderName = gender == 1 ? "Female" : "Male";

        // The possessed bot has no roster entry to fall back on; a slot whose template is
        // still in flight waits for the next rebuild, exactly as it always did.
        CharacterEquipment kit = BuildVisibleItemKit(bot, rosterFallback: null);

        if (!raceFolder.Equals(_character.Race, StringComparison.OrdinalIgnoreCase) ||
            !genderName.Equals(_character.Gender, StringComparison.OrdinalIgnoreCase))
        {
            if (!_character.Load(raceFolder, genderName))
            {
                Console.WriteLine($"[control] could not load {raceFolder} {genderName}; keeping current body");
                return;
            }
        }
        _character.SkinId = skin;
        _character.FaceId = face;
        _character.HairStyleId = hairStyle;
        _character.HairColorId = hairColor;
        _character.FacialHairId = bot.Fields.PlayerFacialHair;
        _character.Equipment = kit;
        _character.Reload();
        _character.Enabled = true;
        _playerPortraitDirty = true;
        _paperDollDirty = true;
    }

    /// <summary>
    /// The streamed renderer already consumes OBJECT_SCALE_X directly. The
    /// first-person CharacterRenderer replaces that streamed body while landed,
    /// so it must mirror the same field for the own character and possessed bots.
    /// This runs every frame because a hero upgrade changes the field in place.
    /// </summary>
    private void RefreshControlledCharacterScale()
    {
        if (_character is null) return;
        float scale = 1f;
        if (_entities.TryGet(ControlledGuid, out WorldEntity controlled) && controlled.IsPlayer &&
            float.IsFinite(controlled.Scale))
            scale = MathF.Max(0.01f, controlled.Scale);
        _character.ModelScale = scale;
    }

    /// <summary>ChrRaces id -> character model folder name (Undead's folder is "Scourge").</summary>
    private static string RaceFolder(byte race) => race switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "NightElf",
        5 => "Scourge", 6 => "Tauren", 7 => "Gnome", 8 => "Troll", _ => "Human"
    };

    /// <summary>Draw the login glue scene (UI_MainMenu) behind the glue UI. Called from Render(), networked + pre-world only.</summary>
    private void DrawGlueScene()
    {
        if (_glue is not { Ok: true } || _gl is null) return;
        if (_worldLoadStarted) return;
        // The front door renders the same UI_MainMenu scene with no net at all.
        if (!GlueFrontDoorActive)
        {
            if (!_config.Server.Enabled || _net is null) return;
            if (_net.State == NetState.CharacterSelect) return;   // the per-race booth draws here instead
        }

        Span<int> vp = stackalloc int[4];
        _gl.GetInteger(GetPName.Viewport, vp);
        int w = vp[2] > 0 ? vp[2] : _config.Window.Width;
        int h = vp[3] > 0 ? vp[3] : _config.Window.Height;
        _glue.Render(w, h);
    }

    /// <summary>
    /// Draw the character-select per-race booth (the UI_&lt;Race&gt; background) fullscreen, behind the
    /// roster chrome. Called from Render() right after DrawGlueScene(); CharacterSelect only. The
    /// race shown follows the SELECTED roster entry (Orc placeholder for an empty account), so
    /// clicking a different character swaps the background to that character's race scene.
    /// </summary>
    private void DrawCharacterSelectScene()
    {
        if (_booth is null || _gl is null) return;
        if (!_config.Server.Enabled || _net is null || _worldLoadStarted) return;
        if (_net.State != NetState.CharacterSelect) return;

        if (_charCreateOpen)
        {
            // Drive the booth from the create selection: race scene + live preview dressed in the
            // (race,class,sex) starting outfit (CharStartOutfit.dbc).
            _booth.SetCreateLook(_cc.Race, _cc.Sex, _cc.Class, _cc.Dials[0], _cc.Dials[1], _cc.Dials[2], _cc.Dials[3], _cc.Dials[4], BuildStartOutfit());
        }
        else
        {
            var chars = _net.Characters;
            if (chars.Count > 0)
            {
                if (_selectedChar >= chars.Count) _selectedChar = 0;
                _booth.SetCharacter(chars[_selectedChar]);   // switch background + build the dressed model
            }
            else
            {
                _booth.ShowPlaceholder();
            }
        }

        Span<int> vp = stackalloc int[4];
        _gl.GetInteger(GetPName.Viewport, vp);
        int w = vp[2] > 0 ? vp[2] : _config.Window.Width;
        int h = vp[3] > 0 ? vp[3] : _config.Window.Height;
        _booth.Render(w, h);
    }

    /// <summary>Draw streamed NPCs and remote players. Called from the in-world render pass.</summary>
    private void DrawCreatures()
    {
        if (_creatures is null) return;
        if (!_creatorWorldRequested && (_net is null || !_net.IsInWorld))
        {
            _creatures.NoteKnownNotDrawn(_entities);
            return;
        }
        _creatures.SelfPlayerGuid = ControlledUsesDisplayModel ? 0UL : RenderSelfGuid;
        _creatures.Render(_window.Camera, DisplayModelRenderUnits(), _visibleWorldCorpses);
    }

    /// <summary>
    /// Build the one per-frame admission list shared by visual unit consumers. Network,
    /// combat, targeting, audio and the minimap deliberately keep the complete entity store;
    /// this list answers only whether a unit can contribute a world-space pixel this frame.
    /// </summary>
    private readonly List<WorldEntity> _visibleWorldCorpses = [];

    private void BuildVisibleWorldUnits()
    {
        _visibleWorldUnits.Clear();
        _visibleWorldCorpses.Clear();
        _visibleUnitKnownLastFrame = 0;
        _visibleUnitDistanceCulledLastFrame = 0;
        _visibleUnitFrustumCulledLastFrame = 0;
        _visibleUnitPortalCulledLastFrame = 0;

        Vector3 eye = _window.Camera.Position;
        Matrix4x4 viewProjection = _window.Camera.RelativeViewProjection;
        float distance = _creatures?.AnimateDistance ?? 130f;
        float distanceSq = distance * distance;
        foreach (WorldEntity unit in _entities.Units)
        {
            if (!unit.IsUnit) continue;
            _visibleUnitKnownLastFrame++;

            // The locally driven body has its own first-person/character rules and must
            // remain available to those consumers even when its feet are behind the eye.
            if (unit.Guid == ControlledGuid)
            {
                _visibleWorldUnits.Add(unit);
                continue;
            }

            Vector3 position = unit.Position;
            if (Vector3.DistanceSquared(position, eye) > distanceSq)
            {
                _visibleUnitDistanceCulledLastFrame++;
                continue;
            }

            float renderScale = MathF.Max(unit.Scale, 0.01f);
            float radius = MathF.Max(1f, renderScale * 2f);
            Vector3 relative = position - eye;
            if (!Camera.BoxInFrustum(viewProjection,
                    relative - new Vector3(radius, radius, radius),
                    relative + new Vector3(radius, radius, radius * 1.5f)))
            {
                _visibleUnitFrustumCulledLastFrame++;
                continue;
            }
            if (_wmo is not null && !_wmo.IsPointPortalVisible(position))
            {
                _visibleUnitPortalCulledLastFrame++;
                continue;
            }

            _visibleWorldUnits.Add(unit);
        }
        foreach (WorldEntity corpse in _entities.Entities.Values)
        {
            if (corpse.Type != ObjectTypeId.Corpse) continue;
            var appearance = new CorpseAppearance(corpse.Fields);
            if (!appearance.CanRenderBody || Vector3.DistanceSquared(appearance.Position, eye) > distanceSq ||
                (_wmo is not null && !_wmo.IsPointPortalVisible(appearance.Position))) continue;
            _visibleWorldCorpses.Add(corpse);
        }
    }

    // ── Glue screens: Login -> Character Select -> in-world status ──────────────────────────────

    /// <summary>Called from Gui(). Draws whichever glue/status screen matches the connection state.</summary>
    private void NetHud()
    {
        // Establishes the base "Point" hardware cursor for every glue screen this frame
        // (login, character select, connecting) - previously only DrawCombatHud's own
        // cursor-authority frame set it, so the login/char-select screens stayed on the
        // OS default arrow until the world loaded (reported 2026-08-30). Harmless no-op
        // for the InWorld branch below: DrawCombatHud starts its own cursor-authority
        // frame right after, which simply resets and refines this same proposal further
        // (item-hover cursors etc.).
        // In world this is NOT harmless any more: the commit now holds a stem for two frames
        // before rebuilding the OS cursor, and a second Point frame every game frame made the
        // proposals alternate Point/Attack so the sword and loot bag never settled in first
        // person (owner, 2026-09-02). DrawCombatHud owns the cursor in world.
        if (_net?.IsInWorld != true)
        {
            _window.BeginHardwareCursorFrame();
            TryUseHardwareCursor(WorldCursorKind.Point.ToString());
            _window.EndHardwareCursorFrame();
        }

        // Offline HUD preview (Program.HudPreview.cs) - draws the real gameplay
        // frames against a synthetic player so UI work is checkable in the same
        // screenshot probe the world uses. FIRST, because the probe that boots
        // the offline world is the creator sandbox, and the creator branch below
        // returns before any net state is considered - so anywhere lower down
        // this never runs. It replaces the creator overlay rather than stacking
        // on it, which is what makes the screenshot worth reading. Env-gated and
        // self-disabling the moment a real session exists.
        if (HudPreview) { DrawHudPreview(); return; }

        // Creator sandbox owns the world: its own overlay, no net states involved.
        if (_creatorWorldRequested) { DrawCreatorHud(); return; }

        // The glue front door: the login screen doubles as the launch menu, even
        // with no network client at all. Enter World commits the chosen mode.
        if (GlueFrontDoorActive)
        {
            DrawLoginScreen();
            DrawLoginProfileWindows();
            DrawGlueTuning();
            DrawScreenshotStatus();
            return;
        }

        if (!_config.Server.Enabled || _net is null) return;

        NetState st = _net.State;
        if (st != NetState.CharacterSelect) CloseCharacterRename();
        if (st == NetState.InWorld)
        {
            DrawCombatHud();
            if (_config.DevTools && !_uiParityArmed && !PlayerPanelOpen)
            {
                DrawInWorldPanel();
                DrawMountToolkit();
                DrawMountKitBar();
            }
            return;
        }

        // The login screen is full-bleed glue chrome and owns its own full-screen window; the
        // connecting / character-select dialogs stay as centered panels for now.
        if (st is NetState.Idle or NetState.Failed or NetState.Disconnected)
        {
            DrawLoginScreen();
            DrawLoginProfileWindows();
            DrawGlueTuning();
            DrawScreenshotStatus();
            return;
        }

        // Character select is full-bleed skinned chrome (its own window), like the login. The create
        // screen (Program.CharCreate.cs) is a client-side overlay on the same parked net state.
        if (st == NetState.RealmSelect)
        {
            DrawRealmSelection();
            DrawScreenshotStatus();
            return;
        }
        if (st == NetState.CharacterSelect)
        {
            if (_charCreateOpen) { DrawCharacterCreate(); DrawCreateTuning(); } else DrawCharacterSelect();
            DrawBoothTuning();
            DrawScreenshotStatus();
            return;
        }

        DrawConnecting();
        DrawScreenshotStatus();
    }

    // The glue canvas the login layout is authored in (Blizzard's 1024x768 UI space). Every position
    // below is in these units, scaled to the window by (height / 768) and anchored to a screen edge -
    // exactly how the real client's UIParent lays the login out. Numbers transcribed from benilla
    // login/screen.rs (which read them from AccountLogin.xml); the cosmetic side/bottom buttons
    // benilla cut are placed by eye and want a screenshot pass.
    private const float GlueCanvasH = 768f;

    private void DrawLoginScreen()
    {
        var io = ImGui.GetIO();
        Vector2 disp = io.DisplaySize;
        float s = MathF.Max(disp.Y / GlueCanvasH, 0.5f);   // glue scales with height, letterboxes width
        float cx = disp.X * 0.5f;
        var host = LoginUiLaw.Host(disp);

        if (!_loginInit)
        {
            LaunchConfigurationSetting? launch = ActiveLaunchConfiguration();
            string savedAccount = !string.IsNullOrWhiteSpace(launch?.Account)
                ? launch.Account
                : string.IsNullOrWhiteSpace(Settings.SavedAccountName)
                    ? _config.Server.Account ?? ""
                    : Settings.SavedAccountName;
            WriteBuf(_acctBuf, savedAccount);
            if (launch is { SavePassword: true }) WriteBuf(_passBuf, launch.Password);
            _rememberAccount = !string.IsNullOrEmpty(savedAccount);
            _loginInit = true;
        }
        bool loginFailureOpen = _net is { State: NetState.Failed } &&
            !_loginFailureDismissed && !string.IsNullOrWhiteSpace(_net.Status);

        // Full-screen, transparent, input-catching window kept at the back (NoBringToFrontOnFocus)
        // so the dev HUD / settings modal sit over it. The glue widgets hit-test inside it.
        ImGui.SetNextWindowPos(host.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(host.Size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground
                  | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus
                  | ImGuiWindowFlags.NoSavedSettings;
        bool open = ImGui.Begin("##glue-login", flags);
        ImGui.PopStyleVar();
        if (!open) { ImGui.End(); return; }

        var dl = ImGui.GetWindowDrawList();
        float savedScale = _skin?.Scale ?? 1f;
        if (_skin is not null) _skin.Scale = s;   // glue widgets scale with the login canvas

        // Static art: the WoW logo (TOPLEFT 3,7 at 256x128) and the Blizzard logo (BOTTOM 0,8 100x100).
        if (_skin is not null)
        {
            _skin.GlueImage(dl, "glue.logo", new Vector2(3f, 7f) * s, new Vector2(259f, 135f) * s);
            float bw = 100f * s, bh = 100f * s;
            var bmin = new Vector2(cx - bw * 0.5f, disp.Y - 8f * s - bh);
            _skin.GlueImage(dl, "glue.blizz", bmin, bmin + new Vector2(bw, bh));
        }

        // Version (BOTTOMLEFT) + copyright (BOTTOM), both GlueFontNormalSmall gold.
        GlueText(dl, "Version 1.12.1 (5875) (Release)", 4f * s, disp.Y - 34f * s, 12f * s, WowSkin.GlueGold, 0);
        GlueText(dl, "Sep 19 2006", 4f * s, disp.Y - 19f * s, 12f * s, WowSkin.GlueGold, 0);
        GlueText(dl, "Copyright 2004-2006  Blizzard Entertainment. All Rights Reserved.",
                 cx, disp.Y - 17f * s, 12f * s, WowSkin.GlueGold, 1);

        if (loginFailureOpen) ImGui.BeginDisabled();

        // Creator mode and serverless client mode have no account. Serverless
        // shows BOTH destinations as their own buttons - clicking one IS the
        // mode selection (saved sticky), so there is no way to land in the
        // wrong world. Networked client mode keeps the account + password
        // fields (160x37, bottom-anchored 345 / 270, centered; password Enter
        // submits) and the Login button.
        bool creatorMode = CreatorLaunchActive;
        bool serverless = !_config.Server.Enabled;
        var loginSize = new Vector2(170f * s, 45f * s * GlueTune.ButtonHeightMul);
        if (serverless)
        {
            // No server configured: creator mode IS the offline world, so one
            // button covers it (the separate offline viewer was redundant).
            // Hidden while the Launch Options modal is up — its top edge pokes
            // into the modal's bottom border and would steal those clicks.
            if (!LoginConfigurationModalOpen)
            {
                var bigSize = new Vector2(230f * s, 45f * s * GlueTune.ButtonHeightMul);
                ImGui.SetCursorScreenPos(new Vector2(cx - bigSize.X * 0.5f, 505f * s));
                if (_skin?.GlueButton("Enter Creator Mode", bigSize) ?? ImGui.Button("Enter Creator Mode", bigSize))
                {
                    SetLaunchMode(LaunchModeCreator);
                    EnterOfflineWorld();
                }
            }
        }
        else if (creatorMode)
        {
            if (!LoginConfigurationModalOpen)   // login configuration modals sit over this spot
            {
                GlueText(dl, "Creator Mode", cx, disp.Y - 320f * s, 15f * s, WowSkin.GlueGold, 1);
                GlueText(dl, "The offline sandbox: spells, characters, gear and world tools.",
                         cx, disp.Y - 296f * s, 12f * s, WowSkin.Muted, 1);
            }
            var bigSize = new Vector2(230f * s, 45f * s * GlueTune.ButtonHeightMul);
            ImGui.SetCursorScreenPos(new Vector2(cx - bigSize.X * 0.5f, 519f * s));
            if (_skin?.GlueButton("Enter Creator Mode", bigSize) ?? ImGui.Button("Enter Creator Mode", bigSize))
                EnterOfflineWorld();
        }
        else if (!LoginConfigurationModalOpen)
        {
            // The account/password fields and Login button sit exactly where the Launch
            // Options modal draws. They are real ImGui items: left visible under the
            // modal they EAT its clicks wherever the rects overlap (the "Creator Mode"
            // button's centre lands on the account box — clicking it did nothing), and
            // their foreground text bled through the parchment. While the modal is up,
            // this whole cluster simply does not exist.
            float boxW = 160f * s, boxH = 37f * s;
            LoginField(dl, "Account Name", "##acct", _acctBuf, cx, disp.Y - 345f * s, boxW, boxH, s, false,
                out bool acctSubmit);
            // Enter in the ACCOUNT box with no password yet advances to the password box rather
            // than submitting nothing - 1.12's own tab order, and the reason Enter felt dead here.
            // SetKeyboardFocusHere targets the next submitted item, which is the password
            // InputText: LoginField draws its backdrop and label straight to the draw list, so it
            // claims no ImGui item before that.
            bool passwordEmpty = BufToString(_passBuf).Length == 0;
            if (acctSubmit && passwordEmpty) ImGui.SetKeyboardFocusHere();
            LoginField(dl, "Account Password", "##pass", _passBuf, cx, disp.Y - 270f * s, boxW, boxH, s, true, out bool submit);

            // Login (170x45, TOP 519, centered). Height is live-tunable (grows downward from 519).
            ImGui.SetCursorScreenPos(new Vector2(cx - loginSize.X * 0.5f, 519f * s));
            bool loginClick = _skin?.GlueButton("Login", loginSize) ?? ImGui.Button("Login", loginSize);
            // Enter is Login's default-button key, the same contract char-create already honours
            // (GameLoop.CharCreate: Enter = Create). Three ways in: the password box's own
            // EnterReturnsTrue, Enter from the account box once a password exists, and Enter while
            // NOTHING is focused - the case that made this feel broken, because clicking the
            // background dropped focus and then the key did nothing at all.
            bool loginKey = !ImGui.GetIO().WantTextInput &&
                (ImGui.IsKeyPressed(ImGuiKey.Enter, false) ||
                 ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false));
            if (loginClick || submit || (acctSubmit && !passwordEmpty) || loginKey)
            {
                string a = BufToString(_acctBuf), p = BufToString(_passBuf);
                if (a.Length > 0 && p.Length > 0 && _net is not null)
                {
                    _loginFailureDismissed = false;
                    string savedAccount = _rememberAccount ? a : "";
                    _config.Server.Account = savedAccount;
                    if (!string.Equals(Settings.SavedAccountName, savedAccount,
                        StringComparison.Ordinal))
                    {
                        Settings.SavedAccountName = savedAccount;
                        SettingsFile?.Save();
                    }
                    PersistManualLogin(a, p);
                    _net.Login(a, p);
                }
            }
        }

        // Remember Account Name checkbox (at 17,653). Box + label sizes are live-tunable - the label
        // needs an explicit glue size or it renders at the tiny ambient font next to the s-scaled box.
        if (_skin is not null && !creatorMode && !serverless)
        {
            ImGui.SetCursorScreenPos(new Vector2(17f * s, 653f * s));
            _skin.CheckBox("Remember Account Name", ref _rememberAccount,
                           GlueTune.CheckBoxUnits, GlueTune.CheckLabelUnits * s);
        }

        // The main-menu buttons. Positions are by eye (benilla cut these) - screenshot pass.
        // The LEFT column is BOTTOM-anchored at canvas 645, eight units above the Remember
        // Account Name checkbox (top 653), and stacks upward with a constant five-unit gap.
        // Button height is 34 * GlueTune.ButtonHeightMul, live-tunable over [0.8, 1.8] from the
        // tune window: with the old fixed literals a third button punched through the checkbox
        // above mul ~1.20 and the buttons' own hit rects overlapped above ~1.235. Deriving from
        // the bottom keeps the 8 units of clearance and 5 units of air true at EVERY mul, and at
        // the shipped 1.086 it reproduces the eyeballed 565/607 to within ~1.2 units.
        var small = new Vector2(150f * s, 34f * s * GlueTune.ButtonHeightMul);
        var connectionMenuSize = new Vector2(176f * s, small.Y);
        float gap = small.Y + 6f * s;
        float rightX = disp.X - 24f * s - small.X;
        GlueMenuButton("Cinematics", new Vector2(rightX, 300f * s), small);
        GlueMenuButton("Credits", new Vector2(rightX, 300f * s + gap), small);
        GlueMenuButton("Terms of Use", new Vector2(rightX, 300f * s + 2f * gap), small);
        float leftX = 17f * s;
        float menuH = connectionMenuSize.Y;
        float menuPitch = menuH + 5f * s;
        float menuBottom = 645f * s;
        float menuY3 = menuBottom - menuH;
        float menuY2 = menuY3 - menuPitch;
        float menuY1 = menuY2 - menuPitch;
        ImGui.SetCursorScreenPos(new Vector2(leftX, menuY1));
        if (_skin?.GlueButton("Manage Connection", connectionMenuSize) == true)
            OpenConnectionManager();
        ImGui.SetCursorScreenPos(new Vector2(leftX, menuY2));
        if (_skin?.GlueButton("Launch Configurations", connectionMenuSize) == true)
            OpenLaunchConfigurationManager();
        ImGui.SetCursorScreenPos(new Vector2(leftX, menuY3));
        if (_skin?.GlueButton("MSUI Web Connection", connectionMenuSize) == true)
            OpenMsuiWebConnection();

        // Launch Options - the one wired menu button: what does this client boot into?
        ImGui.SetCursorScreenPos(new Vector2(rightX, 300f * s + 3f * gap));
        if (_skin?.GlueButton("Launch Options", small) == true)
        {
            _launchMenuOpen = !_launchMenuOpen;
            if (_launchMenuOpen)
            {
                _manageConnectionsOpen = false;
                _launchConfigurationsOpen = false;
                _msuiWebOpen = false;
            }
        }

        // The realm line (opens the realm modal in Stage B) and Quit (150x38, BOTTOMRIGHT 5,29).
        var quitSize = new Vector2(150f * s, 38f * s * GlueTune.ButtonHeightMul);
        float quitTop = disp.Y - 29f * s - quitSize.Y;
        if (!creatorMode && !serverless)
        {
            string realm = RealmDisplayName();   // the realm NAME once connected, else configured name, else host:port
            GlueText(dl, "Realm", disp.X - 6f * s, quitTop - 46f * s, 11f * s, WowSkin.Muted, 2);
            float rScale = ImGui.GetFontSize() > 0f ? (13f * s / ImGui.GetFontSize()) : 1f;
            Vector2 rSz = ImGui.CalcTextSize(realm) * rScale;
            var rPos = new Vector2(disp.X - 6f * s - rSz.X, quitTop - 32f * s);
            ImGui.SetCursorScreenPos(rPos);
            ImGui.InvisibleButton("##realm", new Vector2(MathF.Max(rSz.X, 40f), MathF.Max(rSz.Y, 12f * s)));
            // Stage B wires this click to the realm-select modal; for now it's a hover affordance.
            GlueText(dl, realm, disp.X - 6f * s, quitTop - 32f * s, 13f * s,
                     ImGui.IsItemHovered() ? WowSkin.Highlight : WowSkin.GlueGold, 2);
        }
        else if (!LoginConfigurationModalOpen)
        {
            // Creator and serverless have no realm, so this slot is free - and it is the
            // right one: the web app is exactly the "what am I connected to" fact here,
            // and creator mode is where the Spell Workshop's push actually matters.
            string web = SuiWebAppUrl.Length > 0 ? SuiWebAppUrl : "not set up";
            GlueText(dl, "MSUI Web", disp.X - 6f * s, quitTop - 46f * s, 11f * s,
                     WowSkin.Muted, 2);
            float wScale = ImGui.GetFontSize() > 0f ? (13f * s / ImGui.GetFontSize()) : 1f;
            Vector2 wSz = ImGui.CalcTextSize(web) * wScale;
            ImGui.SetCursorScreenPos(new Vector2(disp.X - 6f * s - wSz.X, quitTop - 32f * s));
            if (ImGui.InvisibleButton("##msui-web-status",
                    new Vector2(MathF.Max(wSz.X, 40f), MathF.Max(wSz.Y, 12f * s))))
                OpenMsuiWebConnection();
            bool hovered = ImGui.IsItemHovered();
            GlueText(dl, web, disp.X - 6f * s, quitTop - 32f * s, 13f * s,
                     hovered ? WowSkin.Highlight
                             : SuiWebAppUrl.Length > 0 ? WowSkin.GlueGold : WowSkin.Muted, 2);
            if (hovered)
                ImGui.SetTooltip(SuiWebAppUrl.Length > 0
                    ? $"Spell Workshop pushes go to {SuiWebAppUrl}.\nClick to change or verify."
                    : "No MangosSuperUI web app set - the Spell Workshop can preview\n" +
                      "spells but cannot push them. Click to set the address.");
        }

        ImGui.SetCursorScreenPos(new Vector2(disp.X - 5f * s - quitSize.X, quitTop));
        if (_skin?.GlueButton("Quit", quitSize) ?? ImGui.Button("Quit", quitSize))
            _quitRequested = true;

        // Escape on the login screen quits, same as the Quit button - skipped while the login
        // failure dialog or a connection/launch-configuration modal is up, since those already
        // own Escape themselves (dismiss/close) and must not also exit the client.
        if (!loginFailureOpen && !LoginConfigurationModalOpen &&
            ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            _quitRequested = true;

        // Dev: a small top-right toggle for the live tuning modal (clear of the logo at top-left).
        ImGui.SetCursorScreenPos(new Vector2(disp.X - 58f * s, 6f * s));
        if (ImGui.InvisibleButton("##glue-tune-toggle", new Vector2(52f * s, 18f * s)))
            _glueTuneOpen = !_glueTuneOpen;
        GlueText(dl, "tune", disp.X - 6f * s, 6f * s, 12f * s,
                 (_glueTuneOpen || ImGui.IsItemHovered()) ? WowSkin.Highlight : WowSkin.Muted, 2);

        // The Launch Options modal draws last so it sits over the rest of the login chrome.
        DrawLaunchOptionsMenu(dl, s);
        if (loginFailureOpen)
        {
            ImGui.EndDisabled();
            DrawLoginFailureDialog(dl, disp, s, _net!.Status);
        }

        if (_skin is not null) _skin.Scale = savedScale;
        ImGui.End();
    }

    private bool _glueTuneOpen;
    private bool _loginFailureDismissed;

    /// <summary>
    /// The live login-tuning modal - the same skinned-ImGui approach as the in-game options modal.
    /// Every slider writes straight into GlueTune, which the glue widgets read next frame, so the
    /// login updates as the slider moves. "Log values" prints the current set to the console so a
    /// dialed-in look can be read off and baked into GlueTune's defaults. Toggled by the small "tune"
    /// text at the login's top-right; rendered from NetHud right after the login screen.
    /// </summary>
    private void DrawGlueTuning()
    {
        if (!_glueTuneOpen) return;

        var tuningWindow = LoginUiLaw.TuningWindow;
        ImGui.SetNextWindowSize(tuningWindow.Size, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(tuningWindow.Min, ImGuiCond.FirstUseEver);
        _skin?.PushStyle();
        // PushStyle makes WindowBg transparent (the in-game frames paint their own backdrop); this is
        // a plain window, so give it an opaque dark panel so the sliders are readable over the scene.
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.05f, 0.04f, 0.03f, 0.96f));
        bool open = _glueTuneOpen;
        if (ImGui.Begin("Glue Login Tuning", ref open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextWrapped("Drag this panel aside by its title bar - it is covering the Login button. "
                            + "Values apply live as you drag a slider.");
            ImGui.Spacing();
            ImGui.TextDisabled("'Caption shadow' is the drop shadow behind the Login text.");
            ImGui.TextDisabled("Hover the button (text turns white) to see it most clearly.");
            ImGui.Spacing();

            ImGui.TextDisabled("BUTTONS");
            ImGui.SliderFloat("Height x", ref GlueTune.ButtonHeightMul, 0.8f, 1.8f);
            ImGui.SliderFloat("Caption size", ref GlueTune.CaptionSizeRatio, 0.25f, 0.65f);
            ImGui.SliderFloat("Caption lift", ref GlueTune.CaptionLift, 0f, 0.30f);
            ImGui.SliderFloat("Caption shadow", ref GlueTune.CaptionShadowRatio, 0f, 0.14f);
            ImGui.SliderFloat("Hover glow", ref GlueTune.HoverGlow, 0f, 4f);
            ImGui.SliderFloat("Text outline", ref GlueTune.OutlinePx, 0f, 0.14f);
            ImGui.Spacing();

            ImGui.TextDisabled("SHADOW (labels, fields, checkbox)");
            ImGui.SliderFloat("Strength", ref GlueTune.ShadowAlpha, 0.30f, 1.00f);
            ImGui.SliderFloat("Offset", ref GlueTune.ShadowOffsetRatio, 0.02f, 0.20f);
            ImGui.Spacing();

            ImGui.TextDisabled("FIELDS");
            ImGui.SliderFloat("Label size", ref GlueTune.FieldLabelUnits, 10f, 30f);
            ImGui.SliderFloat("Typed text size", ref GlueTune.TypedTextUnits, 10f, 30f);
            ImGui.Spacing();

            ImGui.TextDisabled("REMEMBER CHECKBOX");
            ImGui.SliderFloat("Box size", ref GlueTune.CheckBoxUnits, 12f, 40f);
            ImGui.SliderFloat("Label size##chk", ref GlueTune.CheckLabelUnits, 8f, 30f);
            ImGui.Spacing();

            ImGui.TextDisabled("GLUE SCENE");
            ImGui.SliderFloat("Ember size", ref GlueTune.ParticleSize, 0.25f, 3f);
            ImGui.SliderFloat("Brazier size", ref GlueTune.BrazierSize, 0.25f, 3f);
            ImGui.Spacing();
            ImGui.Separator();

            if (ImGui.Button("Log values")) GlueTune.LogValues();
            ImGui.SameLine();
            if (ImGui.Button("Reset")) GlueTune.Reset();
            ImGui.SameLine();
            if (ImGui.Button("Close")) open = false;
        }
        ImGui.End();
        ImGui.PopStyleColor();
        _skin?.PopStyle();
        _glueTuneOpen = open;
    }

    /// <summary>A GlueFont label with a 1px shadow, scaled to sizePx. align: 0=left, 1=centre, 2=right (x is that edge).</summary>
    private static void GlueText(ImDrawListPtr dl, string text, float x, float y, float sizePx, Vector4 col, int align)
    {
        var font = ImGui.GetFont();
        float baseFs = ImGui.GetFontSize();
        float scale = baseFs > 0f ? sizePx / baseFs : 1f;
        Vector2 sz = ImGui.CalcTextSize(text) * scale;
        float left = align == 1 ? x - sz.X * 0.5f : align == 2 ? x - sz.X : x;
        var pos = new Vector2(left, y);
        // The 1.12 MasterFont drop shadow: black, down-right, scaled to the text size so it reads the
        // same at every glue font size. Strength (alpha) and offset are live-tunable via GlueTune.
        float so = MathF.Max(1f, MathF.Round(sizePx * GlueTune.ShadowOffsetRatio));
        dl.AddText(font, sizePx, pos + new Vector2(so, so), ImGui.ColorConvertFloat4ToU32(GlueTune.ShadowColor), text);
        WowSkin.OutlineText(dl, font, sizePx, pos, text);
        dl.AddText(font, sizePx, pos, ImGui.ColorConvertFloat4ToU32(col), text);
    }

    /// <summary>
    /// One AccountLogin edit box: a dark bordered box (benilla BOX_FILL/BOX_BORDER), a gold label
    /// centred ~9px above it, and an ImGui InputText inset 15px (AccountLogin.xml TextInsets). The
    /// box is bottom-anchored at boxBottomY and centred on cx. Password boxes mask + submit on Enter.
    /// </summary>
    private void LoginField(ImDrawListPtr dl, string label, string id, byte[] buf, float cx,
                            float boxBottomY, float boxW, float boxH, float s, bool password, out bool submitted)
    {
        var boxMin = new Vector2(cx - boxW * 0.5f, boxBottomY - boxH);
        var boxMax = new Vector2(cx + boxW * 0.5f, boxBottomY);
        // The field is the real AccountLogin.xml Backdrop: UI-Tooltip-Background tiled inside a
        // Glue-Tooltip-Border nine-slice (WowSkin.GlueEditBox) - the recessed frame, not a flat
        // dark rectangle. The InputText on top is frameless (every FrameBg state transparent, no
        // border), so there is no second rectangle inside it. Fallback to a plain box with no skin.
        // Tinted with AccountLogin's DEFAULT_TOOLTIP_COLOR (benilla BOX_FILL 0.09 / BOX_BORDER 0.8):
        // the UI-Tooltip-Background sheet is light, so at full white it read whitish-grey over the
        // bright valley; the near-black fill tint is what makes it the OG's dark recessed well.
        if (_skin is not null)
            _skin.DrawBackdrop(dl, boxMin, boxMax, WowSkin.GlueEditBox, WowSkin.GlueBoxFill, WowSkin.GlueBoxBorder);
        else
        {
            dl.AddRectFilled(boxMin, boxMax, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.72f)));
            dl.AddRect(boxMin, boxMax, ImGui.ColorConvertFloat4ToU32(WowSkin.GoldDim));
        }
        GlueText(dl, label, cx, boxMin.Y - 22f * s, GlueTune.FieldLabelUnits * s, WowSkin.GlueGold, 1);

        // The typed line is GlueEditBoxFont (ARIALN 18, benilla EDIT_FONT_SIZE) - enlarge the window
        // font to that size for the InputText only, then restore it. Frame height is read AFTER the
        // scale so the text is vertically centred at its real height inside the 37px box.
        float typedPx = GlueTune.TypedTextUnits * s;
        float baseFs = ImGui.GetFontSize();
        float wfScale = baseFs > 0f ? typedPx / baseFs : 1f;
        ImGui.SetWindowFontScale(wfScale);

        float inset = 15f * s;
        float frameH = ImGui.GetFrameHeight();
        ImGui.SetCursorScreenPos(new Vector2(boxMin.X + inset, boxMin.Y + (boxH - frameH) * 0.5f));
        ImGui.SetNextItemWidth(boxW - inset - 6f * s);
        var clear = new Vector4(0f, 0f, 0f, 0f);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, clear);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, clear);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, clear);
        ImGui.PushStyleColor(ImGuiCol.Text, WowSkin.Normal);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        var iflags = ImGuiInputTextFlags.EnterReturnsTrue | (password ? ImGuiInputTextFlags.Password : ImGuiInputTextFlags.None);
        submitted = ImGui.InputText(id, buf, (uint)buf.Length, iflags);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        ImGui.SetWindowFontScale(1f);
    }

    /// <summary>A cosmetic main-menu glue button placed at an absolute screen pos. Draws + hovers; click
    /// is a no-op for now (Cinematics / Credits / URLs get wired later). No-op with no glue art.</summary>
    private void GlueMenuButton(string label, Vector2 pos, Vector2 size)
    {
        if (_skin is null) return;
        ImGui.SetCursorScreenPos(pos);
        _skin.GlueButton(label, size);
    }

    /// <summary>
    /// The logon progress dialog. 1.12 runs the same states we do - connecting, authenticating,
    /// "Authentication Successful", retrieving the character list - and shows each in a GlueDialog:
    /// the riveted DialogFrame box (the same border the in-game menus use), a gold caption, and one
    /// Cancel button. This used to be a bare ImGui window with the raw enum name in it, which broke
    /// the illusion at exactly the moment the player is staring at the screen waiting.
    /// </summary>
    private void DrawConnecting()
    {
        var io = ImGui.GetIO();
        var disp = io.DisplaySize;
        float s = MathF.Max(disp.Y / 768f, 0.1f);
        var host = LoginUiLaw.Host(disp);

        ImGui.SetNextWindowPos(host.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(host.Size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground
                  | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus
                  | ImGuiWindowFlags.NoSavedSettings;
        bool open = ImGui.Begin("##glue-connecting", flags);
        ImGui.PopStyleVar();
        if (!open) { ImGui.End(); return; }

        var dl = ImGui.GetWindowDrawList();
        float savedScale = _skin?.Scale ?? 1f;
        if (_skin is not null) _skin.Scale = s;

        string caption = LogonCaption(_net!.State);
        float linePitch = LoginUiLaw.MessageFontSize * 1.25f;
        LoginUiLaw.DialogLayout dialog = LoginUiLaw.Dialog(disp, s, linePitch);

        if (_skin is not null)
            _skin.DrawBackdrop(dl, dialog.Frame.Min, dialog.Frame.Max, WowSkin.Dialog);
        else
            dl.AddRectFilled(dialog.Frame.Min, dialog.Frame.Max,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.85f)));

        GlueText(dl, caption, dialog.Frame.Min.X + dialog.Frame.Size.X * .5f,
            dialog.Message.Min.Y, LoginUiLaw.MessageFontSize * s, WowSkin.GlueGold, 1);

        ImGui.SetCursorScreenPos(dialog.Button.Min);
        bool cancel = _skin?.GlueButton("Cancel", dialog.Button.Size) ??
            ImGui.Button("Cancel", dialog.Button.Size);
        if (cancel) { _net.Stop(); Array.Clear(_passBuf); }

        if (_skin is not null) _skin.Scale = savedScale;
        ImGui.End();
    }

    /// <summary>The 1.12 GlueDialog captions for the logon states we actually pass through.</summary>
    private static string LogonCaption(NetState s) => s switch
    {
        NetState.ConnectingRealm => "Connecting",
        NetState.Authenticating => "Authenticating",
        NetState.ConnectingWorld => "Handshaking",
        NetState.EnteringWorld => "Entering World",
        _ => "Connecting",
    };

    private void DrawLoginFailureDialog(ImDrawListPtr dl, Vector2 disp, float s, string status)
    {
        string message = LoginUiLaw.FailureText(status);
        float wrapScale = s * LoginUiLaw.MessageFontSize / 16f;
        string[] lines = WrapTooltipText(message, "GameFontNormalLarge", wrapScale,
            LoginUiLaw.MessageWidth * s).ToArray();
        if (lines.Length == 0) lines = ["Unable to connect"];
        float linePitch = LoginUiLaw.MessageFontSize * 1.25f;
        LoginUiLaw.DialogLayout dialog = LoginUiLaw.Dialog(disp, s, lines.Length * linePitch);

        if (_skin is not null)
            _skin.DrawBackdrop(dl, dialog.Frame.Min, dialog.Frame.Max, WowSkin.Dialog);
        else
            dl.AddRectFilled(dialog.Frame.Min, dialog.Frame.Max,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, .85f)));
        float centerX = dialog.Frame.Min.X + dialog.Frame.Size.X * .5f;
        for (int i = 0; i < lines.Length; i++)
            GlueText(dl, lines[i], centerX, dialog.Message.Min.Y + i * linePitch * s,
                LoginUiLaw.MessageFontSize * s, WowSkin.GlueGold, 1);

        ImGui.SetCursorScreenPos(dialog.Button.Min);
        bool okay = _skin?.GlueButton("Okay", dialog.Button.Size) ??
            ImGui.Button("Okay", dialog.Button.Size);
        if (okay || ImGui.IsKeyPressed(ImGuiKey.Escape) ||
            ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))
            _loginFailureDismissed = true;
    }

    private bool _boothTuneOpen;
    private ulong _deleteConfirmGuid;   // non-zero while the delete confirmation is up
    private string _deleteConfirmName = "";
    private byte _deleteConfirmLevel;
    private byte _deleteConfirmClass;
    private readonly byte[] _deleteConfirmText = new byte[33];
    private bool _deleteConfirmFocus;

    private void OpenDeleteConfirm(Character character)
    {
        _deleteConfirmGuid = character.Guid;
        _deleteConfirmName = character.Name;
        _deleteConfirmLevel = character.Level;
        _deleteConfirmClass = character.Class;
        Array.Clear(_deleteConfirmText);
        _deleteConfirmFocus = true;
    }

    private void CloseDeleteConfirm()
    {
        _deleteConfirmGuid = 0;
        _deleteConfirmName = "";
        _deleteConfirmLevel = 0;
        _deleteConfirmClass = 0;
        Array.Clear(_deleteConfirmText);
        _deleteConfirmFocus = false;
    }

    /// <summary>
    /// The 1.12 character-select 2D chrome, full-bleed over the 3D booth - the same skinned-ImGui
    /// approach as the login (WowSkin logo / tinted tooltip backdrop / GlueButton / GlueText), scaled
    /// to a 1024x768 glue canvas by s = height/768. Layout from SYSTEM_CHARACTER_SELECT.md section 4
    /// (benilla char_select/screen.rs + CharacterSelect.xml): WoW logo top-left; the selected name over
    /// the model; Enter World + a rotate pair bottom-centre; Back + Delete bottom-right; a right-column
    /// frame holding the realm banner, Change Realm, up to ten character rows, and Create
    /// New Character. The realm chooser, delete dialog, create screen and AreaTable zone names are
    /// later phases (those buttons are present-but-disabled for now).
    /// </summary>
    private void DrawCharacterSelect()
    {
        bool characterLoginFailureOpen = PrepareCharacterLoginFailureDialog();
        bool characterRenameOpen = PrepareCharacterRenameDialog();
        var io = ImGui.GetIO();
        Vector2 disp = io.DisplaySize;
        float s = MathF.Max(disp.Y / GlueCanvasH, 0.5f);
        var chars = _net!.Characters;
        if (chars.Count > 0 && _selectedChar >= chars.Count) _selectedChar = 0;
        if (!_charSelectionRestored && chars.Count > 0)
        {
            _charSelectionRestored = true;
            ulong remembered = Settings.LastCharacterGuid;
            int rememberedIndex = -1;
            if (remembered != 0)
                for (int i = 0; i < chars.Count; i++)
                    if (chars[i].Guid == remembered) { rememberedIndex = i; break; }
            if (rememberedIndex >= 0) _selectedChar = rememberedIndex;
        }

        // A just-created character: select its row once the refreshed roster arrives.
        if (_ccArmName is not null)
            for (int i = 0; i < chars.Count; i++)
                if (string.Equals(chars[i].Name, _ccArmName, StringComparison.OrdinalIgnoreCase))
                { _selectedChar = i; _ccArmName = null; break; }

        // AreaTable.dbc once (zone names for the roster rows); best-effort, a miss just hides the zone.
        if (!_areasLoaded)
        {
            _areasLoaded = true;
            try
            {
                var atBytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, AreaTableCatalog.MpqPath);
                if (atBytes is not null) _areas = AreaTableCatalog.Parse(atBytes);
            }
            catch (Exception e) { Console.WriteLine($"[glue] AreaTable load failed: {e.Message}"); }
        }

        var host = CharSelectUiLaw.Host(disp);
        ImGui.SetNextWindowPos(host.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(host.Size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground
                  | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus
                  | ImGuiWindowFlags.NoSavedSettings;
        bool open = ImGui.Begin("##glue-charselect", flags);
        ImGui.PopStyleVar();
        if (!open) { ImGui.End(); return; }

        var dl = ImGui.GetWindowDrawList();
        float savedScale = _skin?.Scale ?? 1f;
        if (_skin is not null) _skin.Scale = s;
        // Capture the glue font + atlas for the after-render GL text pass (valid only in-frame).
        _glueAdd?.SetGlueFont(ImGui.GetFont(), (uint)ImGui.GetIO().Fonts.TexID.ToInt64());

        if (characterLoginFailureOpen || characterRenameOpen) ImGui.BeginDisabled();
        ulong enterGuid = 0;

        // WoW logo, TOPLEFT (3,7) 256x128 - same asset/placement as the login.
        _skin?.GlueImage(dl, "glue.logo", new Vector2(3f, 7f) * s, new Vector2(259f, 135f) * s);

        // Right-column character frame: 260x642 at TOPRIGHT (-5,-15). The tinted Glue-Tooltip backdrop
        // is the SAME mechanism the login edit boxes use (WowSkin.DrawBackdrop with the box tints).
        float frameW = 260f * s, frameH = 642f * s;
        var frameMin = new Vector2(disp.X - 5f * s - frameW, 15f * s);
        var frameMax = frameMin + new Vector2(frameW, frameH);

        // Row geometry is needed BEFORE the panel is drawn, because the lit row's highlight card cuts a
        // hole in the panel fill (see below). These are the same numbers the row loop uses further down.
        float rowX = frameMin.X + 12f * s, rowW = frameW - 24f * s;
        int rows = Math.Min(chars.Count, 10);
        float rowsTop = frameMin.Y + 74f * s;
        float createTop = frameMax.Y - (GlueTune.CreateCharH + GlueTune.CreateCharBottom) * s;
        float pitch = CharSelectUiLaw.RosterPitch(rows, createTop - rowsTop - 12f * s, s);
        float rowScale = pitch / 60f, rowH = 54f * rowScale;
        int litSel = _selectedChar;

        // Which row the mouse is over, resolved HERE rather than from ImGui.IsItemHovered in the loop:
        // the panel is drawn before the row buttons exist, and the hole and the glow must agree on the
        // same frame. IsWindowHovered keeps a window on top (the tuning modal) from stealing the
        // highlight, exactly as IsItemHovered would. The rows' InvisibleButtons still own the clicking;
        // this is a read-only hit test and changes no ImGui state.
        int hoverRow = -1;
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
        {
            var mp = io.MousePos;
            for (int i = 0; i < rows; i++)
            {
                float rt = rowsTop + i * pitch;
                if (mp.X >= rowX && mp.X <= rowX + rowW && mp.Y >= rt && mp.Y <= rt + rowH) { hoverRow = i; break; }
            }
        }

        // THE ROW-TEXT LAYERING FIX. benilla order is panel -> glow -> TEXT: the row text sits in FRONT
        // of the additive highlight card. The card can only be a dedicated GL pass (an ImGui blend
        // callback tears the Silk render loop down), and re-drawing the text over an on-top card needs a
        // SECOND ImGui frame - which destroys ImGui's cross-frame hover/click tracking and is exactly
        // what made this whole screen unclickable. So neither the text nor the card moves: the PANEL
        // does. The glow composites UNDER the single ImGui pass (text in front for free, interaction
        // untouched); the panel would dim it from up there, so the panel FILL moves down with it - it is
        // drawn in the GL pass, from the panel's own tiled art (BackdropFillSlice -> EnqueueBlend), and
        // ImGui draws only the nine-sliced EDGE over the top. The panel looks exactly as before: only the
        // layer order changed. One ImGui frame, one GL pass, no interaction change.
        //
        // AND IT IS THE WHOLE FILL, NOT A BAND. The first cut punched a hole in the ImGui fill just
        // behind each lit card and patched that band into the GL pass. It worked, but left a hairline
        // under the lower card: a band edge is a seam between two rasterisers (ImGui clips with an
        // integer glScissor; the GL quad rasterises by its own pixel-centre rule) and snapping the edge
        // to whole pixels did not reliably close it. Moving the ENTIRE fill leaves no internal boundary
        // to misalign - the fill is one continuous stretch of the same tiled art it always was.
        var rosterFill = WowSkin.GlueBoxFill;
        rosterFill.W *= GlueTune.RosterAlpha;          // lower = more cobblestone through
        List<Vector2>? hiHoles = null;
        bool panelUnderGlow = !GlueTune.SelectHiOnTop && GlueTune.SelectHiPanelHole
                              && _skin is not null && _glueAdd is not null
                              && _skin.Has("glue.select.hi");
        if (panelUnderGlow
            && _skin!.BackdropFillSlice(frameMin, frameMax, WowSkin.GlueEditBox, frameMin.Y, frameMax.Y,
                                        out uint bgTex, out var pMin, out var pMax,
                                        out var pUv0, out var pUv1))
        {
            _glueAdd!.EnqueueBlend(pMin, pMax, pUv0, pUv1, rosterFill, false, bgTex);
            hiHoles = [new Vector2(frameMin.Y, frameMax.Y)];   // ImGui: edge only, no fill at all
        }

        if (_skin is not null)
            _skin.DrawBackdrop(dl, frameMin, frameMax, WowSkin.GlueEditBox, rosterFill, WowSkin.GlueBoxBorder, hiHoles);
        else
            dl.AddRectFilled(frameMin, frameMax, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.72f)));

        // Realm banner and a worker-ordered return to a refreshed auth realm list.
        GlueText(dl, RealmDisplayName(), frameMin.X + frameW * 0.5f, frameMin.Y + 10f * s, 14f * s, WowSkin.GlueGold, 1);
        // Change Realm: width = the panel minus an inset each side, so it stays centred in the column
        // however the panel is resized. Height and both offsets are dials (they were 12 / 32 / 30).
        float crInset = GlueTune.ChangeRealmInset * s;
        ImGui.SetCursorScreenPos(new Vector2(frameMin.X + crInset, frameMin.Y + GlueTune.ChangeRealmTop * s));
        if (_skin?.GlueButton("Change Realm", new Vector2(frameW - crInset * 2f, GlueTune.ChangeRealmH * s)) == true)
        {
            if (_net.RequestRealmSelection())
            {
                CloseDeleteConfirm();
                CancelPendingWorldCurtain();
                _charSelectionRestored = false;
            }
        }

        // Up to ten character rows (name / "Level X Class" / zone). The 1.12 palette: the NAME stays
        // gold (GlueGold) whether selected or not - the bright-yellow highlight box IS the selection
        // cue, not a text-colour change; "Level X Class" is WHITE; the zone (AreaTable name) is a gray
        // third line, shown only when known. All keep the black drop shadow. The highlight box hugs the
        // content - it grows to include the zone line so a location never spills out the bottom.
        for (int i = 0; i < rows; i++)
        {
            Character c = chars[i];
            var rMin = new Vector2(rowX, rowsTop + i * pitch);
            ImGui.SetCursorScreenPos(rMin);
            bool rowClicked = ImGui.InvisibleButton($"##row{i}", new Vector2(rowW, rowH));
            // InvisibleButton only returns true on the RELEASE of a click, one frame after
            // IsMouseDoubleClicked goes true on the second click's PRESS - checking it nested
            // inside the button's own return value compared two different frames and so never
            // agreed, and a double-click on a row silently did nothing. IsItemHovered right after
            // the button call is valid regardless of the button's own pressed/released timing.
            bool rowDoubleClicked = ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
            if (rowClicked || rowDoubleClicked)
            {
                _selectedChar = i;
                RememberCharacterSelection(c.Guid);
                if (rowDoubleClicked) enterGuid = c.Guid;
            }
            // Use the pre-panel snapshot, not ImGui.IsItemHovered / the live _selectedChar: the panel
            // hole above was cut from exactly these two, and a lit card with no hole (or vice versa)
            // would flicker for a frame on click.
            bool selected = i == litSel, hovered = i == hoverRow;

            // The row highlight is the REAL 1.12 art - Interface\Glues\CharacterSelect\
            // Glue-CharacterSelect-Highlight.blp, a yellow glow authored on black for ADD blend (WowSkin
            // rebuilds its alpha from luma so an alpha draw only brightens, exactly as the login button
            // highlight does). benilla char_select/screen.rs draws it as ONE 256x74 card and refresh.rs:145
            // shows it for `lit = selected || hovered` - hover and the LOCKED selected row at the SAME
            // full brightness (the ref's LockHighlight). The soft glow edge (the "shadow" around the
            // yellow) is baked into the BLP; there is no rounded rect and no dimmer hover.
            bool lit = selected || hovered;
            if (lit)
            {
                var hiMin = new Vector2(frameMin.X + GlueTune.SelectHiInsetX * s, rMin.Y + GlueTune.SelectHiTop * rowScale);
                var hiMax = new Vector2(frameMin.X + frameW - GlueTune.SelectHiInsetX * s, hiMin.Y + GlueTune.SelectHiHeight * rowScale);
                if (_skin is not null && _skin.Has("glue.select.hi"))
                {
                    // TRUE additive (alphaMode="ADD") - the blend benilla's AddUiMaterial uses for this
                    // card (glue/add_material.rs, SrcAlpha/One). Enqueue the RAW (non-luma) copy of the
                    // BLP as an additive quad; GlueAdditive flushes it onto the framebuffer BEFORE the
                    // ImGui HUD pass, so the row text below draws in front of it. The panel would
                    // normally dim it from there, which is why its fill is cut away behind the card
                    // (hiHoles, above). SelectHi.W is the glow intensity, RGB the tint.
                    // Falls back to the straight-alpha translucent draw if the overlay/raw art is absent.
                    uint rawTex = _skin.TextureHandle("glue.select.hi.raw");
                    if (_glueAdd is not null && rawTex != 0)
                        _glueAdd.Enqueue(hiMin, hiMax, GlueTune.SelectHi, GlueTune.SelectHiGain, GlueTune.SelectHiContrast, GlueTune.SelectHiOnTop, rawTex);
                    else
                        _skin.GlueImage(dl, "glue.select.hi", hiMin, hiMax, GlueTune.SelectHi);
                }
                else   // art missing from the MPQs: a faint fallback so the selection stays legible
                    dl.AddRectFilled(hiMin, hiMax,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.87f, 0.12f, 0.30f)), 8f * s);
            }

            // Row text (benilla screen.rs row_button): NAME gold 15 (+5), INFO "Level X Class" white 12
            // (+24), LOCATION gray 12 (+38) - all carrying the glue black drop shadow via GlueText.
            GlueText(dl, c.Name, rMin.X + 10f * s, rMin.Y + 5f * rowScale, 15f * rowScale, WowSkin.GlueGold, 0);
            GlueText(dl, $"Level {c.Level} {ClassName(c.Class)}" + (c.IsGhost ? "  (dead)" : ""),
                     rMin.X + 10f * s, rMin.Y + 24f * rowScale, 12f * rowScale, WowSkin.Normal, 0);
            string zone = _areas?.ZoneName(c.Zone) ?? "";
            if (zone.Length > 0)
                GlueText(dl, zone, rMin.X + 10f * s, rMin.Y + 38f * rowScale, 12f * rowScale, WowSkin.Muted, 0);

            // The on-top glow draws OVER this row text. Queue it as GL TEXT (same additive pass, after
            // the glow, using the ImGui font atlas) so it redraws crisp IN FRONT of the glow - no second
            // ImGui frame (that broke interaction). benilla: the row text sits over the ADD card.
            if (lit && GlueTune.SelectHiOnTop && _glueAdd is not null)
            {
                _glueAdd.EnqueueText(c.Name, rMin.X + 10f * s, rMin.Y + 5f * rowScale, 15f * rowScale, WowSkin.GlueGold);
                _glueAdd.EnqueueText($"Level {c.Level} {ClassName(c.Class)}" + (c.IsGhost ? "  (dead)" : ""),
                                     rMin.X + 10f * s, rMin.Y + 24f * rowScale, 12f * rowScale, WowSkin.Normal);
                if (zone.Length > 0)
                    _glueAdd.EnqueueText(zone, rMin.X + 10f * s, rMin.Y + 38f * rowScale, 12f * rowScale, WowSkin.Muted);
            }
        }

        // Create New Character at the frame's bottom - opens the create screen (Program.CharCreate.cs).
        // Size and offsets were hardcoded (34 tall, inset 12, 12 up from the panel bottom); dials now,
        // with the width derived from the inset so it stays centred in the column at any panel width.
        float cnInset = GlueTune.CreateCharInset * s;
        ImGui.SetCursorScreenPos(new Vector2(frameMin.X + cnInset,
                                             createTop));
        if (_skin?.GlueButton("Create New Character", new Vector2(frameW - cnInset * 2f, GlueTune.CreateCharH * s),
                              enabled: true, captionPx: GlueTune.CreateCharTextPx * s) ?? false)
            OpenCharCreate();

        // Enter World (200x60 at BOTTOM 0,30), flanked by a rotate pair that spins the model while held.
        // Enter World: size and bottom margin are dials (they were 200 x 60 at 30 off the bottom).
        // ButtonHeightMul still rides on top so the shared glue-button proportion applies here too.
        var ewSize = new Vector2(GlueTune.EnterWorldW * s, GlueTune.EnterWorldH * s * GlueTune.ButtonHeightMul);
        float ewTop = disp.Y - GlueTune.EnterWorldBottom * s - ewSize.Y;

        // The selected character's NAME over the model, clearly ABOVE the Enter World button
        // (GlueFontNormalHuge; benilla centres it over the model, above the button).
        float nameY = ewTop - 30f * s - 16f * s;
        if (chars.Count > 0)
            GlueText(dl, chars[_selectedChar].Name, disp.X * 0.5f, nameY, 30f * s, WowSkin.GlueGold, 1);
        else
            GlueText(dl, "No characters on this account", disp.X * 0.5f, nameY, 20f * s, WowSkin.Muted, 1);

        ImGui.SetCursorScreenPos(new Vector2(disp.X * 0.5f - ewSize.X * 0.5f, ewTop));
        bool ewClick = _skin?.GlueButton("Enter World", ewSize, chars.Count > 0, GlueTune.EnterWorldTextPx * s)
                       ?? ImGui.Button("Enter World", ewSize);
        if (ewClick && chars.Count > 0) enterGuid = chars[_selectedChar].Guid;

        // Enter World is this screen's default button, so Enter presses it - matching the login
        // screen above and char-create's Enter = Create. Gated on exactly what the BUTTON is
        // gated on (a roster with a live selection), and stood down while the delete confirmation
        // owns the keyboard - it wants the typed character name, and Enter there must not enter
        // the world instead - or while a launch-configuration modal is up.
        bool enterKey = !characterLoginFailureOpen && !characterRenameOpen && !ImGui.GetIO().WantTextInput && _deleteConfirmGuid == 0 &&
            !LoginConfigurationModalOpen &&
            (ImGui.IsKeyPressed(ImGuiKey.Enter, false) ||
             ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false));
        if (enterKey && _selectedChar >= 0 && _selectedChar < chars.Count)
            enterGuid = chars[_selectedChar].Guid;

        // The stylized rotate pair, UNDER Enter World and centred on it - the 1.12 placement, and the
        // same UI-RotationRight-Big art the create screen uses (RotateButton, shared partial). It used
        // to be two text glue buttons flanking Enter World, which is neither the art nor the position.
        float rot = GlueTune.RotateSize * s;
        float rotSpan = rot * 2f + GlueTune.RotateGap * s;
        float rotX = disp.X * 0.5f - rotSpan * 0.5f + GlueTune.RotateDX * s;
        float rotY = ewTop + ewSize.Y + GlueTune.RotateTop * s;
        RotateButton(dl, "##selRotL", true, new Vector2(rotX, rotY), rot);
        RotateButton(dl, "##selRotR", false, new Vector2(rotX + rot + GlueTune.RotateGap * s, rotY), rot);

        // Back (100x35 at BOTTOMRIGHT -30,25) and Delete Character (165x35 to its left; disabled for now).
        var backSize = new Vector2(100f * s, 35f * s * GlueTune.ButtonHeightMul);
        float backTop = disp.Y - 25f * s - backSize.Y;
        var delSize = new Vector2(165f * s, 35f * s * GlueTune.ButtonHeightMul);
        ImGui.SetCursorScreenPos(new Vector2(disp.X - 30f * s - backSize.X - 8f * s - delSize.X, backTop));
        bool canDelete = chars.Count > 0 && _selectedChar >= 0 && _selectedChar < chars.Count;
        if ((_skin?.GlueButton("Delete Character", delSize, canDelete) ?? false) && canDelete)
        {
            PlayUiSound(CharSelectUiLaw.DeleteSound, CharSelectUiLaw.SoundCategory);
            OpenDeleteConfirm(chars[_selectedChar]);
        }
        ImGui.SetCursorScreenPos(new Vector2(disp.X - 30f * s - backSize.X, backTop));
        bool backClicked = _skin?.GlueButton("Back", backSize) ?? ImGui.Button("Back", backSize);
        // Escape does the same thing as Back, unless the delete confirmation is up - that dialog
        // owns Escape itself (cancels the delete) and must not also back out to the login screen.
        bool backKey = !characterLoginFailureOpen && !characterRenameOpen && _deleteConfirmGuid == 0 && ImGui.IsKeyPressed(ImGuiKey.Escape, false);
        if (backClicked || backKey)
        {
            PlayUiSound(CharSelectUiLaw.BackSound, CharSelectUiLaw.SoundCategory);
            _net.Stop();
            Array.Clear(_passBuf);
        }

        // Dev: the booth-tuning toggle (small "tune" at the top-right, clear of the logo).
        ImGui.SetCursorScreenPos(new Vector2(disp.X - 58f * s, 6f * s));
        if (ImGui.InvisibleButton("##booth-tune-toggle", new Vector2(52f * s, 18f * s)))
            _boothTuneOpen = !_boothTuneOpen;
        GlueText(dl, "tune", disp.X - 6f * s, 6f * s, 12f * s,
                 (_boothTuneOpen || ImGui.IsItemHovered()) ? WowSkin.Highlight : WowSkin.Muted, 2);

        // DRAG THE MODEL TO SPIN IT (benilla char_select/input.rs rotate_model; 1.12 lets you grab the
        // character anywhere on the scene). Registered LAST on purpose: ImGui gives hover to the FIRST
        // item that claims a position, so every real widget above - the rows, Enter World, the rotate
        // pair, Back, and the tune toggle - wins the hit test and this catcher only ever sees what
        // is left, i.e. the booth itself. It also stops at the roster panel's left edge, so dragging on
        // the panel does nothing, same as the reference. Left-drag only; a plain click does nothing.
        float dragW = MathF.Max(frameMin.X, 1f);
        if (_deleteConfirmGuid == 0)
        {
            ImGui.SetCursorScreenPos(Vector2.Zero);
            ImGui.InvisibleButton("##booth-drag", new Vector2(dragW, MathF.Max(disp.Y, 1f)));
        }
        // Sign: drag RIGHT turns the model's front toward the viewer's right, i.e. you push the shoulder
        // you grabbed. Same convention as the `>` button, which decreases the yaw.
        if (_deleteConfirmGuid == 0 && ImGui.IsItemActive() && io.MouseDelta.X != 0f)
            BoothTune.CharYawDegrees += io.MouseDelta.X * BoothTune.DragRotateDegPerPx;

        // Keep the facing in [-180,180] so the tuning slider stays meaningful after a long spin
        // (the drag and the </> buttons both accumulate freely).
        if (BoothTune.CharYawDegrees is > 180f or < -180f)
            BoothTune.CharYawDegrees -= 360f * MathF.Round(BoothTune.CharYawDegrees / 360f);

        // The delete confirmation (benilla char_select/dialog.rs): a GlueDialog over the roster, the
        // doomed character named in it, Accept/Cancel. The worker does the CMSG_CHAR_DELETE and
        // re-enumerates on success, so the row simply disappears.
        if (_deleteConfirmGuid != 0)
        {
            var doomed = chars.FirstOrDefault(c => c.Guid == _deleteConfirmGuid);
            if (doomed is null) CloseDeleteConfirm();
            else
            {
                CharSelectUiLaw.DeleteDialogLayout dialog = CharSelectUiLaw.DeleteDialog(disp, s);
                if (_skin is not null)
                {
                    _skin.DrawBackdrop(dl, dialog.Frame.Min, dialog.Frame.Max, WowSkin.Dialog);
                    _skin.GlueImage(dl, "dialog.alert", dialog.Alert.Min, dialog.Alert.Max);
                    _skin.GlueImageUv(dl, "chat.input.left", dialog.EditBorderLeft.Min,
                        dialog.EditBorderLeft.Max, CharSelectUiLaw.EditLeftUvMin,
                        CharSelectUiLaw.EditLeftUvMax);
                    _skin.GlueImageUv(dl, "chat.input.right", dialog.EditBorderRight.Min,
                        dialog.EditBorderRight.Max, CharSelectUiLaw.EditRightUvMin,
                        CharSelectUiLaw.EditRightUvMax);
                }
                else
                    dl.AddRectFilled(dialog.Frame.Min, dialog.Frame.Max,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.9f)));

                GlueText(dl, "Do you want to delete", dialog.LeadCenter.X, dialog.LeadCenter.Y,
                    18f * s, WowSkin.GlueGold, 1);
                GlueText(dl,
                    $"{_deleteConfirmName}   Level {_deleteConfirmLevel}   {ClassName(_deleteConfirmClass)}?",
                    dialog.IdentityCenter.X, dialog.IdentityCenter.Y, 18f * s, WowSkin.Normal, 1);
                GlueText(dl, "Type \"DELETE\" into the field to confirm.",
                    dialog.InstructionsCenter.X, dialog.InstructionsCenter.Y,
                    12f * s, WowSkin.GlueGold, 1);

                float baseFont = ImGui.GetFontSize();
                ImGui.SetWindowFontScale(baseFont > 0f ? 15f * s / baseFont : 1f);
                float inputY = dialog.Edit.Min.Y +
                    MathF.Max(0f, (dialog.Edit.Size.Y - ImGui.GetFrameHeight()) * .5f);
                ImGui.SetCursorScreenPos(new Vector2(dialog.Edit.Min.X, inputY));
                ImGui.SetNextItemWidth(dialog.Edit.Size.X);
                ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
                ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
                ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
                if (_deleteConfirmFocus)
                {
                    ImGui.SetKeyboardFocusHere();
                    _deleteConfirmFocus = false;
                }
                ImGui.InputText("##delete-confirm-text", _deleteConfirmText,
                    (uint)_deleteConfirmText.Length);
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(3);
                ImGui.SetWindowFontScale(1f);
                bool armed = string.Equals(BufToString(_deleteConfirmText), CharSelectUiLaw.ConfirmText,
                    StringComparison.OrdinalIgnoreCase);
                bool cancelDelete = ImGui.IsKeyPressed(ImGuiKey.Escape);
                bool confirmDelete = armed && ImGui.IsKeyPressed(ImGuiKey.Enter);

                ImGui.SetCursorScreenPos(dialog.Okay.Min);
                bool okay = _skin?.GlueButton("Okay", dialog.Okay.Size, armed) ??
                    (armed && ImGui.Button("Okay", dialog.Okay.Size));
                bool acceptedDelete = (okay && armed) || confirmDelete;
                if (acceptedDelete)
                {
                    PlayUiSound(CharSelectUiLaw.AcceptSound, CharSelectUiLaw.SoundCategory);
                    _net.DeleteCharacter(_deleteConfirmGuid);
                    CloseDeleteConfirm();
                }
                ImGui.SetCursorScreenPos(dialog.Cancel.Min);
                bool cancelPressed = _skin?.GlueButton("Cancel", dialog.Cancel.Size) ??
                    ImGui.Button("Cancel", dialog.Cancel.Size);
                if (!acceptedDelete && (cancelPressed || cancelDelete))
                {
                    PlayUiSound(CharSelectUiLaw.CancelSound, CharSelectUiLaw.SoundCategory);
                    CloseDeleteConfirm();
                }
            }
        }

        // Keep the selection inside the roster after a delete shrinks it.
        if (_net.TryTakeDeleteResult(out byte delCode))
        {
            Console.WriteLine($"[net] SMSG_CHAR_DELETE result 0x{delCode:X2}");
            _selectedChar = 0;
        }

        if (enterGuid != 0 && BeginRequiredCharacterRename(enterGuid)) enterGuid = 0;
        if (enterGuid != 0)
        {
            PlayUiSound(CharSelectUiLaw.EnterWorldSound, CharSelectUiLaw.SoundCategory);
            RememberCharacterSelection(enterGuid);
            if (_gl is not null)
            {
                Character? entering = chars.FirstOrDefault(c => c.Guid == enterGuid);
                ArmEnterWorldCurtain(_gl, entering is null ? _config.Start.Map : (int)entering.Map);

                // An automated Enter action can land on the first character-select frame,
                // before DrawCharacterSelectScene has built the selected booth avatar. Build it
                // now, behind the already-armed curtain, so enter-world can transfer that exact
                // renderer instead of falling back to an in-load equipment rebuild.
                if (entering is not null) _booth?.SetCharacter(entering);
            }
            _net.SelectCharacter(enterGuid);
        }
        if (characterLoginFailureOpen || characterRenameOpen) ImGui.EndDisabled();
        if (characterLoginFailureOpen) DrawCharacterLoginFailureDialog(dl, disp, s);
        else if (characterRenameOpen) DrawCharacterRenameDialog(dl, disp, s);
        if (_skin is not null) _skin.Scale = savedScale;
        ImGui.End();
    }

    private void RememberCharacterSelection(ulong guid)
    {
        if (guid == 0 || Settings.LastCharacterGuid == guid) return;
        Settings.LastCharacterGuid = guid;
        SettingsFile?.Save();
    }

    /// <summary>ClientWindow.OnOverlay: flush the queued additive glue quads (char-select row
    /// highlight, alphaMode=ADD) onto the framebuffer, beneath the ImGui HUD pass. No-op when nothing
    /// was queued (off the character-select screen).</summary>
    public void Overlay()    => _glueAdd?.Flush(ImGui.GetIO().DisplaySize, onTop: false);

    /// <summary>ClientWindow.OnOverlayTop: the "on top" additive glue quads, drawn over the HUD.</summary>
    public void OverlayTop()
    {
        _glueAdd?.Flush(ImGui.GetIO().DisplaySize, onTop: true);
        FinishGameplayDump();
        CaptureFrameRecorder();
        FinishPainterlyComparisonCapture();
        FinishUiParityCapture();
        FinishBagContainmentCapture();
        FinishScreenshotCapture();
    }


    /// <summary>The booth dev-tuning modal (fine-tune nudges on the scene-camera placement). Toggled by
    /// the "tune" text at char-select; the same skinned-window pattern as the login's DrawGlueTuning.</summary>
    private void DrawBoothTuning()
    {
        if (!_boothTuneOpen) return;

        var tuningWindow = CharSelectUiLaw.TuningWindow;
        ImGui.SetNextWindowSize(tuningWindow.Size, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(tuningWindow.Min, ImGuiCond.FirstUseEver);
        _skin?.PushStyle();
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.05f, 0.04f, 0.03f, 0.96f));
        bool open = _boothTuneOpen;
        if (ImGui.Begin("Character-Select Booth Tuning", ref open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextDisabled("Fine-tune nudges on the scene-camera placement.");
            ImGui.Spacing();
            ImGui.TextDisabled("Selected-row highlight (drag the RGBA / alpha bar):");
            ImGui.ColorEdit4("Row highlight (RGB tint, A = coverage)", ref GlueTune.SelectHi, ImGuiColorEditFlags.AlphaBar);
            ImGui.SliderFloat("Highlight brightness (ADD gain)", ref GlueTune.SelectHiGain, 0f, 6f);
            ImGui.SliderFloat("Highlight crispness (contrast)", ref GlueTune.SelectHiContrast, 1f, 5f);
            ImGui.Checkbox("Glow on top (over panel AND text - benilla draws it UNDER the text)", ref GlueTune.SelectHiOnTop);
            ImGui.Checkbox("Panel behind the glow, not over it (undimmed, text in front)", ref GlueTune.SelectHiPanelHole);
            if (GlueTune.SelectHiOnTop) ImGui.TextDisabled("   (ignored while 'Glow on top' is ticked)");
            ImGui.SliderFloat("Roster panel opacity", ref GlueTune.RosterAlpha, 0.2f, 1f);
            ImGui.Spacing();
            ImGui.TextDisabled("Character-select buttons:");
            ImGui.SliderFloat("Enter World width", ref GlueTune.EnterWorldW, 100f, 420f);
            ImGui.SliderFloat("Enter World height", ref GlueTune.EnterWorldH, 24f, 120f);
            ImGui.SliderFloat("Enter World bottom margin", ref GlueTune.EnterWorldBottom, 0f, 160f);
            ImGui.SliderFloat("Enter World text px (0 = auto)", ref GlueTune.EnterWorldTextPx, 0f, 48f);
            ImGui.SliderFloat("Change Realm height", ref GlueTune.ChangeRealmH, 16f, 80f);
            ImGui.SliderFloat("Change Realm top", ref GlueTune.ChangeRealmTop, 0f, 140f);
            ImGui.SliderFloat("Change Realm side inset", ref GlueTune.ChangeRealmInset, 0f, 60f);
            ImGui.SliderFloat("Create Character height", ref GlueTune.CreateCharH, 16f, 90f);
            ImGui.SliderFloat("Create Character bottom", ref GlueTune.CreateCharBottom, 0f, 120f);
            ImGui.SliderFloat("Create Character side inset", ref GlueTune.CreateCharInset, 0f, 60f);
            ImGui.SliderFloat("Create Character text px (0 = auto)", ref GlueTune.CreateCharTextPx, 0f, 40f);
            ImGui.SliderFloat("Rotate size", ref GlueTune.RotateSize, 16f, 90f);
            ImGui.SliderFloat("Rotate gap", ref GlueTune.RotateGap, -20f, 60f);
            ImGui.SliderFloat("Rotate X nudge", ref GlueTune.RotateDX, -200f, 200f);
            // Wide range on purpose: this is the pair's whole vertical travel, so it can sit well
            // above Enter World (negative) or far below it, not just "a gap under the button".
            ImGui.SliderFloat("Rotate Y offset (under Enter World)", ref GlueTune.RotateTop, -300f, 200f);
            ImGui.SliderFloat("Highlight inset X", ref GlueTune.SelectHiInsetX, -12f, 30f);
            ImGui.SliderFloat("Highlight top", ref GlueTune.SelectHiTop, -30f, 20f);
            ImGui.SliderFloat("Highlight height", ref GlueTune.SelectHiHeight, 36f, 100f);
            ImGui.Spacing();
            ImGui.SliderFloat("Model scale", ref BoothTune.CharScale, 0.2f, 3f);
            ImGui.SliderFloat("Vertical nudge", ref BoothTune.CharZOffset, -3f, 3f);
            ImGui.SliderFloat("Facing tweak deg", ref BoothTune.CharYawDegrees, -180f, 180f);
            ImGui.Checkbox("Auto-rotate", ref BoothTune.AutoRotate);
            ImGui.SameLine();
            ImGui.SliderFloat("deg/s", ref BoothTune.AutoRotateSpeed, 5f, 180f);
            ImGui.SliderFloat("Drag spin deg/px", ref BoothTune.DragRotateDegPerPx, 0.02f, 1.0f);
            ImGui.SliderFloat("Ambient (shadow)", ref BoothTune.AmbientIntensity, 0f, 2f);
            ImGui.SliderFloat("Sun (key)", ref BoothTune.SunIntensity, 0f, 3f);
            ImGui.SliderFloat("Sun warmth", ref BoothTune.CharSunWarmth, -0.5f, 0.5f);
            ImGui.SliderFloat("Shadow warmth", ref BoothTune.CharAmbientWarmth, -0.5f, 0.5f);
            ImGui.SliderFloat("Shadow softness", ref BoothTune.CharShadowSoftness, 0f, 1f);
            ImGui.SliderFloat("Key azimuth deg", ref BoothTune.CharKeyAzimuthDeg, -180f, 180f);
            ImGui.SliderFloat("Key elevation deg", ref BoothTune.CharKeyElevationDeg, 0f, 90f);
            ImGui.Spacing();
            ImGui.TextDisabled("Backdrop (scene mesh) - login unaffected");
            ImGui.SliderFloat("Scene brightness", ref BoothTune.SceneBrightness, 0.5f, 2.5f);
            ImGui.SliderFloat("Scene warmth", ref BoothTune.SceneWarmth, -0.2f, 0.5f);
            ImGui.TextDisabled("Sun fill (rides the scene's own sun; lights the floor)");
            ImGui.SliderFloat("Fill intensity", ref BoothTune.SunFillIntensity, 0f, 1.5f);
            ImGui.SliderFloat("Fill elevation", ref BoothTune.SunFillElevDeg, 0f, 90f);
            ImGui.SliderFloat("Fill azimuth offset", ref BoothTune.SunFillAzimOffsetDeg, -180f, 180f);
            ImGui.SliderFloat("Fill warmth", ref BoothTune.SunFillWarmth, -0.2f, 0.5f);
            ImGui.Spacing();
            if (ImGui.Button("Log booth values")) { BoothTune.LogValues(); GlueTune.LogValues(); }
            ImGui.SameLine();
            if (ImGui.Button("Reset booth")) BoothTune.Reset();
            ImGui.SameLine();
            if (ImGui.Button("Close")) open = false;
        }
        ImGui.End();
        ImGui.PopStyleColor();
        _skin?.PopStyle();
        _boothTuneOpen = open;
    }

    private void DrawInWorldPanel()
    {
        if (!_devOverlayVisible) return;   // F1 — same master switch as the dev overlay
        ImGui.SetNextWindowSize(new Vector2(390, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Server")) { ImGui.End(); return; }
        ImGui.TextUnformatted($"player: {_net!.PlayerName}   guid 0x{_net.PlayerGuid:X}");
        ImGui.TextUnformatted($"entities: {_entities.Count}  (creatures {_entities.CreatureCount}, players {_entities.PlayerCount})");
        ImGui.TextUnformatted($"packets in: {_netInbound}  (updates last frame {_netUpdatesLastFrame})");
        ImGui.TextUnformatted($"moving (splines): {_entities.MovingCount}");
        ImGui.TextUnformatted($"movement out: {_movementSender.PacketsSent}  " +
                              $"flags 0x{_movementSender.LastFlags:X}" +
                              (_movementSender.LastOpcode is { } moveOp ? $"  last {moveOp}" : ""));
        ImGui.TextUnformatted($"combat events: {_combat.ReceivedCount}  buffered {_combat.BufferedCount}  " +
                              $"engaged {_combat.EngagedCount}");
        ImGui.TextUnformatted($"target: hover 0x{_hoveredGuid:X}  selected 0x{_selectionGuid:X}  " +
                              $"attack 0x{_attackTargetGuid:X}");
        ImGui.TextUnformatted($"world combat text: {_worldCombatTextSpawned} spawned  " +
                              $"{_floatingCombatText.Count} active  {_worldCombatTextDropped} dropped  " +
                              $"center {_centerCombatText.Count}");
        if (_combat.LastEvent is { } lastCombat)
            ImGui.TextDisabled($"last combat: {lastCombat.GetType().Name}");
        if (_creatures is not null)
        {
            ImGui.TextUnformatted($"creatures drawn: {_creatures.DrawnLastFrame}" +
                                  (_creatures.Ok ? "" : "  (renderer off - creature DBCs missing)"));
            ImGui.TextUnformatted($"remote players drawn: {_creatures.PlayersDrawnLastFrame}");
            bool drawC = _creatures.Enabled;
            if (ImGui.Checkbox("Draw creatures", ref drawC)) _creatures.Enabled = drawC;
            float hoff = _creatures.HeadingOffsetDegrees;
            if (ImGui.SliderFloat("Creature heading deg", ref hoff, -180f, 180f)) _creatures.HeadingOffsetDegrees = hoff;
            float cscale = _creatures.ScaleMultiplier;
            if (ImGui.SliderFloat("Creature scale x", ref cscale, 0.1f, 3f)) _creatures.ScaleMultiplier = cscale;
            ImGui.TextUnformatted($"animated: {_creatures.AnimatedLastFrame}");
            ImGui.TextUnformatted($"combat anims: {_creatures.CombatActionsTriggered} triggered  " +
                                  $"{_creatures.CombatActionsActive} active" +
                                  (_character is null
                                      ? ""
                                      : $"  self {_character.CombatActionsTriggered} ({_character.CurrentAnimation})"));
            ImGui.TextUnformatted($"mounts drawn: {_creatures.MountsDrawnLastFrame}" +
                                  (SelfMountDisplayId() is var mountDisplay && mountDisplay > 0
                                      ? $"  (you: display {mountDisplay})" : ""));
            if (ImGui.Checkbox("Mount toolkit", ref _mountToolkitOpen)) { }
            bool animC = _creatures.Animate;
            if (ImGui.Checkbox("Animate creatures", ref animC)) _creatures.Animate = animC;
            float adist = _creatures.AnimateDistance;
            if (ImGui.SliderFloat("Animate distance", ref adist, 20f, 300f)) _creatures.AnimateDistance = adist;
        }
        if (_controller is not null && ImGui.CollapsingHeader("Nearest units"))
        {
            Vector3 me = _controller.Position;
            foreach (var e in _entities.NearestUnits(me, 12))
                ImGui.TextUnformatted(
                    $"{Vector3.Distance(e.Position, me),5:F0}yd  " +
                    $"{(e.IsPlayer ? "player" : $"npc {e.Entry}")}  disp {e.DisplayId}  L{e.Level}  " +
                    $"hp {e.HealthFraction * 100f,3:F0}%{(e.IsMoving ? "  walking" : "")}");
        }
        ImGui.Separator();
        if (ImGui.Button("Disconnect")) { _net.Stop(); Array.Clear(_passBuf); }
        ImGui.End();
    }

    /// <summary>The realm banner/label text: the realmlist NAME once connected (NetworkClient.RealmName),
    /// else a configured realm name, else the host:port. Used by both the login and character-select chrome.</summary>
    private string RealmDisplayName()
    {
        if (_net is not null && !string.IsNullOrWhiteSpace(_net.RealmName)) return _net.RealmName;
        if (!string.IsNullOrWhiteSpace(_config.Server.Realm)) return _config.Server.Realm!;
        return $"{_config.RealmdHost}:{_config.RealmdPort}";
    }

    // Compressed-moves record opcodes without a handler, each logged exactly once.
    private readonly HashSet<Op> _compressedMoveSkippedOps = [];

    /// <summary>Creature locomotion: attach the server spline so this body walks. Serves the
    /// standalone SMSG_MONSTER_MOVE case and the records batched inside SMSG_COMPRESSED_MOVES.</summary>
    private void ApplyMonsterMovePacket(byte[] body, bool onTransport = false)
    {
        var mm = MonsterMoveParser.Parse(body, onTransport);
        if (mm is null) return;
        ObserveServerRideSpline(mm);
        _entities.ApplyMonsterMove(mm, MovementInfo.ClientUptimeMs());
        // Dev window: observed-path history (no-op while it is closed).
        RecordDevObservedPath(mm);
    }

    private static string RaceName(byte r) => r switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "Night Elf",
        5 => "Undead", 6 => "Tauren", 7 => "Gnome", 8 => "Troll", _ => "?"
    };

    private static string ClassName(byte c) => c switch
    {
        1 => "Warrior", 2 => "Paladin", 3 => "Hunter", 4 => "Rogue", 5 => "Priest",
        7 => "Shaman", 8 => "Mage", 9 => "Warlock", 11 => "Druid", _ => "?"
    };

    private static string BufToString(byte[] b)
    {
        int n = Array.IndexOf(b, (byte)0);
        if (n < 0) n = b.Length;
        return Encoding.UTF8.GetString(b, 0, n);
    }

    private static void WriteBuf(byte[] b, string s)
    {
        Array.Clear(b);
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        Array.Copy(bytes, b, Math.Min(bytes.Length, b.Length - 1));
    }
}
