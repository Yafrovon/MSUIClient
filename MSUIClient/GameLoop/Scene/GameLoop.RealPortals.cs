using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World;
using MSUIClient.World.Doodads;
using MSUIClient.World.Portals;
using MSUIClient.World.Wmo;

using ScenePortalDescriptor = MSUIClient.World.Portals.PortalDescriptor;

namespace MSUIClient;

/// <summary>
/// Per-session REAL_PORTALS orchestration. The ordinary type-22 click path is
/// deliberately untouched: this side channel predicts an immediate aperture,
/// asks the server for authoritative preview metadata, prepares one isolated
/// destination scene, and reports local readiness. A local swept crossing may
/// request the ordinary authoritative GameObject use; it never treats the
/// preview destination itself as teleport authority.
/// </summary>
public sealed partial class GameLoop
{
    private const float RealPortalPreloadRadius = PortalWarmRelevanceLaw.EnterRadius;
    private const float RealPortalPreloadExitRadius = PortalWarmRelevanceLaw.ExitRadius;
    private const float RealPortalHalfWidth = 3f;
    private const float RealPortalHalfHeight = 4f;
    private const float RealPortalBottomClearance = 0.10f;
    private const float RealPortalPreAnimationSeconds = 0.60f;
    private const float RealPortalLiveFadeSeconds = 0.20f;
    private const float RealPortalRenewLeadSeconds = 1.50f;
    private const double RealPortalPrepareRefreshSeconds = 20.0;
    private const double RealPortalCandidateWatchdogSeconds = 120.0;
    private const double RealPortalReplyTimeoutSeconds = 3.0;
    private const double RealPortalRetrySeconds = 5.0;
    private const double RealPortalLoadFailureRetrySeconds = 15.0;
    private const double RealPortalCrossingLatchSeconds = 2.0;
    private const double RealPortalHandoffArmSeconds = 10.0;
    private const double RealPortalCastSpawnGraceSeconds = 10.0;

    private sealed class RealPortalVisual
    {
        public required ulong Guid;
        public required uint Entry;
        public Vector3 GroundPosition;
        public float Yaw;
        public double DiscoveredAt;
        public int SeenScan;
        public double LastEntitySeenAt;
        public bool WarmRelevant;
        public bool PresentationRelevant;
        public bool TemplateEligible;
        public bool TemplateRejectionLogged;
        public float DistanceSquared;

        public uint RequestId;
        public bool PreparePending;
        public double ReplyDeadline;
        public double NextPrepareAt;

        public ScenePortalDescriptor? Descriptor;
        public double DescriptorExpiresAt = double.PositiveInfinity;
        public bool LoadFailed;
        public bool ReadySent;
        public double ReadyReplyDeadline;
        public bool ReadyConfirmed;
        public double ReadyLeaseExpiresAt;
        public float LiveBlend;
        public int CrossingArmedSide;
        public double LastCrossingUseAt = double.NegativeInfinity;
        public bool UnreadyCrossingLogged;
        public double UnreadyHoldSince = double.NaN;
    }

    /// <summary>
    /// How long a crossing may wait at an unready window before it takes the ordinary authoritative
    /// use (the loading curtain). A kart reaches a portal at 21 yd/s (WoW Karting): an unbounded hold
    /// turned a slow or failed destination load into a wall in the road.
    /// </summary>
    private const double RealPortalUnreadyHoldSeconds = 0.75;

    private sealed class RealPortalCastPrewarm
    {
        public required ulong CasterGuid;
        public required PortalPrewarmHint Hint;
        public required HashSet<ulong> ExistingPortalGuids;
        public double ExpiresAt;
        public bool SpellGoObserved;
        public ulong PortalGuid;
        // WoW Karting: a race prewarms the NEXT leg's placed pack portal (no cast, no summoner): it binds to the
        // first object with the hint's entry (GameLoop.Karting.cs BeginKartPortalPrewarm).
        public bool Placed;
    }

    private readonly Dictionary<ulong, RealPortalVisual> _realPortals = [];
    private readonly List<ulong> _realPortalRemoveScratch = [];
    private PortalDestinationScene? _realPortalScene;
    private ulong _realPortalSceneGuid;
    private ScenePortalDescriptor? _realPortalSceneDescriptor;
    private WorldAtmosphere? _realPortalAtmosphere;
    private double _realPortalSceneStartedAt;
    private uint _realPortalNextRequestId;
    private int _realPortalScan;
    private bool _realPortalsUnavailable;
    private bool _realPortalWasInWorld;
    private bool _realPortalCapabilityProbeSent;
    private bool _realPortalCapabilityProbePending;
    private bool _realPortalProtocolAvailable;
    private PortalPrewarmHint[] _realPortalPrewarmCatalog = [];
    private RealPortalCastPrewarm? _realPortalCastPrewarm;
    private double _realPortalCapabilityReplyDeadline;
    private (ulong Guid, uint Spawn, uint Revision, ulong Ticket)?
        _realPortalMinimapWarmIdentity;
    private WmoRenderer.InteriorMinimapContext? _realPortalMinimapWarmContext;

    private enum RealPortalHandoffPhase
    {
        None,
        Armed,
        Tentative,
        Transit,
    }

    // The handoff copy is independent from PortalDestinationScene. Map teardown
    // retires that scene before the collision-gated main-world loader runs; an
    // owned copy is the only safe texture to sample during that interval.
    private PortalHandoffSnapshot? _realPortalHandoffSnapshot;
    private ScenePortalDescriptor? _realPortalHandoffDescriptor;
    private RealPortalHandoffPhase _realPortalHandoffPhase;
    private double _realPortalHandoffArmedAt;

    // The lightweight procedural aperture and the heavyweight destination scene
    // have deliberately separate availability. A shader/FBO/secondary-scene
    // failure must leave the summoned portal as a large sealed doorway instead
    // of silently falling back to only the narrow stock M2 effect.
    private bool RealPortalsEnabled =>
        _config.Server.Enabled && _config.Server.RealPortals;

    private bool RealPortalPreviewEnabled =>
        RealPortalsEnabled && _realPortalProtocolAvailable &&
        !_realPortalsUnavailable && _realPortalScene is not null;

    /// <summary>
    /// Allocate the one reusable candidate renderer while the startup curtain is
    /// already up. Runtime approach frames then schedule only bounded warm/adopt
    /// work; shader compilation and liquid texture creation are not paid at the
    /// portal threshold.
    /// </summary>
    private void InitRealPortals(GL gl, string shaderDirectory)
    {
        // Pre-create the preview slot whenever the feature is configured, even
        // if this boot starts at the serverless front door and switches to Client
        // mode later. The procedural aperture itself needs only ParticleRenderer.
        if (!_config.Server.RealPortals) return;
        if (_uploads is null || _assetWorkers is null || _sky is null || _particles is null)
        {
            _realPortalsUnavailable = true;
            Console.WriteLine(
                "[real-portals] live preview unavailable: rendering dependencies did not initialise; " +
                "sealed procedural apertures remain enabled");
            return;
        }

        try
        {
            // Portal descriptors carry map ids, so resolve Map.dbc/WDT identity
            // once under the startup curtain instead of on the approach frame.
            EnsureInstanceData();
            (int width, int height) = RealPortalTargetSize();
            _realPortalScene = new PortalDestinationScene(
                gl, _config, _uploads, _assetWorkers, _sky, _overrides,
                shaderDirectory, width, height, tileRadius: 1);
            Console.WriteLine(
                $"[real-portals] ready: one isolated {width}x{height} environmental preview slot");
        }
        catch (Exception ex)
        {
            _realPortalScene?.Dispose();
            _realPortalScene = null;
            _realPortalsUnavailable = true;
            Console.WriteLine(
                $"[real-portals] live preview unavailable: {ex.Message}; " +
                "sealed procedural apertures remain enabled");
        }
    }

    private void UpdateRealPortals(float dt)
    {
        double now = RealPortalNow();
        ExpireRealPortalHandoff(now);
        // Retiring worlds are already advanced once near the top of Update,
        // before every loading/glue early return. Do not finalize the same old
        // renderer bundle a second time later in the gameplay frame.
        if (_realPortalScene?.Retiring != true)
            _realPortalScene?.Step();

        bool inWorld = _net?.State == NetState.InWorld;
        if (!inWorld && _realPortalWasInWorld)
            ResetRealPortalCapability();
        _realPortalWasInWorld = inWorld;

        if (!RealPortalsEnabled || !inWorld || _controller is null)
        {
            ResetRealPortals();
            if (_realPortalScene?.Retiring != true)
                _realPortalScene?.Step();
            return;
        }

        AdvanceRealPortalCapabilityProbe(now);
        ExpireRealPortalState(now);
        ScanRealPortalGameObjects(now);

        if (RealPortalPreviewEnabled)
        {
            SyncRealPortalTargetSize();
            AdvanceRealPortalCastPrewarm(now);
            RealPortalVisual? prepareTarget = SelectRealPortalPrepareTarget();
            if (prepareTarget is not null)
                AdvanceRealPortalHandshake(prepareTarget, now);

            RealPortalVisual? sceneTarget = SelectRealPortalSceneTarget();
            AdvanceRealPortalScene(sceneTarget, now);
        }
        PublishRealPortalApertures(dt, now);
    }

    /// <summary>
    /// A map transition skips normal world updates while the curtain is up.
    /// Retired candidate jobs still need their nonblocking adoption/drain step
    /// so the reusable slot never strands shared-context uploads.
    /// </summary>
    private void StepRealPortalRetirement()
    {
        if (_realPortalScene?.Retiring == true) _realPortalScene.Step();
    }

    private void ScanRealPortalGameObjects(double now)
    {
        int scan = ++_realPortalScan;
        Vector3 player = _controller!.Position;

        foreach (WorldEntity entity in _entities.Entities.Values)
        {
            if (!IsPredictedMagePortal(entity))
            {
                // A pack portal is recognised from its template, which arrives asynchronously.
                if (entity.IsGameObject && entity.Entry >= PackPortalTemplateBase)
                    RequireGameObjectTemplate(entity);
                continue;
            }

            bool tracked = _realPortals.TryGetValue(
                entity.Guid, out RealPortalVisual? portal);
            bool wasPresentationRelevant = tracked && portal!.PresentationRelevant;
            bool warmRelevant = PortalWarmRelevanceLaw.IsRelevant(
                player, entity.Position,
                currentlyTracked: tracked && portal!.WarmRelevant);
            if (!warmRelevant)
            {
                // Same-map movement teleports do not clear the entity store.
                // If the server's source-object OutOfRange update is delayed or
                // omitted, this warm candidate would otherwise survive forever
                // at arbitrary distance after ResetRealPortals cleared it.
                if (tracked)
                {
                    portal!.SeenScan = scan;
                    portal.LastEntitySeenAt = now;
                    portal.WarmRelevant = false;
                    portal.PresentationRelevant = false;
                    if (wasPresentationRelevant)
                        _particles?.RemoveMagePortalAperture(entity.Guid);
                }
                continue;
            }

            RequireGameObjectTemplate(entity);
            bool templateKnown = _gameObjectTemplates.TryGetValue(entity.Entry, out GameObjectTemplate? template);
            bool templateEligible = templateKnown && IsStockPortalTemplate(entity.Entry, template!);
            // The exact stock entry is enough for immediate local presentation.
            // Template validation gates only protocol IO; a missing/stale query
            // must never erase the large sealed doorway and reveal only the M2.

            if (!tracked)
            {
                portal = new RealPortalVisual
                {
                    Guid = entity.Guid,
                    Entry = entity.Entry,
                    GroundPosition = entity.Position,
                    Yaw = entity.GameObjectFacing,
                    DiscoveredAt = now,
                    LastEntitySeenAt = now,
                    WarmRelevant = true,
                };
                _realPortals.Add(entity.Guid, portal);
                Console.WriteLine(
                    $"[real-portals] tracking warm candidate 0x{entity.Guid:X}, " +
                    $"entry {entity.Entry}, distance {MathF.Sqrt(Vector3.DistanceSquared(player, entity.Position)):F1} yd");
            }
            else if (portal!.Entry != entity.Entry)
            {
                InvalidateRealPortal(portal, retireScene: true);
                portal.Entry = entity.Entry;
                portal.DiscoveredAt = now;
                portal.TemplateRejectionLogged = false;
            }

            portal.GroundPosition = entity.Position;
            portal.Yaw = entity.GameObjectFacing;
            portal.WarmRelevant = true;
            portal.TemplateEligible = templateEligible;
            if (templateKnown && !templateEligible && !portal.TemplateRejectionLogged)
            {
                portal.TemplateRejectionLogged = true;
                uint data0 = template!.Data.Length > 0 ? unchecked((uint)template.Data[0]) : 0;
                Console.WriteLine(
                    $"[real-portals] preview withheld for 0x{entity.Guid:X}: template " +
                    $"type {template.Type}, data0 spell {data0}");
            }
            portal.DistanceSquared = Vector3.DistanceSquared(player, entity.Position);
            portal.SeenScan = scan;
            portal.LastEntitySeenAt = now;
            bool presentationRelevant = PortalVisualRelevanceLaw.IsRelevant(
                player, entity.Position, wasPresentationRelevant);
            if (!wasPresentationRelevant && presentationRelevant)
            {
                portal.DiscoveredAt = now;
                Console.WriteLine(
                    $"[real-portals] procedural 6x8 aperture 0x{entity.Guid:X}, " +
                    $"entry {entity.Entry}, reported type {entity.GameObjectType}");
            }
            else if (wasPresentationRelevant && !presentationRelevant)
            {
                _particles?.RemoveMagePortalAperture(entity.Guid);
            }
            portal.PresentationRelevant = presentationRelevant;

            if (_realPortalCastPrewarm is { PortalGuid: 0 } cast &&
                entity.Entry == cast.Hint.PortalEntry &&
                (cast.Placed ||
                 cast.SpellGoObserved &&
                 entity.Fields.GameObjectCreatedBy == cast.CasterGuid &&
                 !cast.ExistingPortalGuids.Contains(entity.Guid)))
            {
                cast.PortalGuid = entity.Guid;
                Console.WriteLine(
                    $"[real-portals] cast prewarm bound to spawned object 0x{entity.Guid:X}");
            }
        }

        _realPortalRemoveScratch.Clear();
        foreach ((ulong guid, RealPortalVisual portal) in _realPortals)
        {
            if (portal.SeenScan == scan && portal.WarmRelevant) continue;
            if (portal.SeenScan != scan &&
                !PortalVisualRelevanceLaw.MissingEntityGraceExpired(
                    now, portal.LastEntitySeenAt))
                continue;
            _realPortalRemoveScratch.Add(guid);
            if (_realPortalSceneGuid == guid) RetireRealPortalScene();
            if (_realPortalCastPrewarm?.PortalGuid == guid)
                CancelRealPortalCastPrewarm("bound portal disappeared", retireScene: true);
            _particles?.RemoveMagePortalAperture(guid);
        }
        foreach (ulong guid in _realPortalRemoveScratch) _realPortals.Remove(guid);
    }

    private RealPortalVisual? SelectRealPortalPrepareTarget()
    {
        float radiusSquared = RealPortalPreloadRadius * RealPortalPreloadRadius;

        // The exact creator GUID binds a cast-start warm to its eventual object.
        // Give that object the next authoritative PREPARE correlation even if an
        // older portal still owns the reusable scene slot.
        if (_realPortalCastPrewarm is { PortalGuid: not 0 } cast &&
            _realPortals.TryGetValue(cast.PortalGuid, out RealPortalVisual? castPortal) &&
            castPortal.TemplateEligible && castPortal.DistanceSquared <= radiusSquared)
            return castPortal;

        // Keep ownership stable while a useful candidate exists. The server v1
        // correlation record is per session, so spraying PREPARE at every portal
        // would invalidate the one whose destination is actually loading.
        if (_realPortalSceneGuid != 0 &&
            _realPortals.TryGetValue(_realPortalSceneGuid, out RealPortalVisual? active) &&
            active.TemplateEligible && active.DistanceSquared <= radiusSquared)
            return active;

        RealPortalVisual? retained = _realPortals.Values
            .Where(p => p.TemplateEligible && p.DistanceSquared <= radiusSquared &&
                        (p.PreparePending || p.Descriptor is not null))
            .OrderBy(p => p.DistanceSquared)
            .FirstOrDefault();
        if (retained is not null) return retained;

        return _realPortals.Values
            .Where(p => p.TemplateEligible && p.DistanceSquared <= radiusSquared)
            .OrderBy(p => p.DistanceSquared)
            .FirstOrDefault();
    }

    /// <summary>
    /// Probe through the oldest SUI opcode understood by both old and new
    /// SuperUI-Core builds. A zero-guid control request is a harmless denial on
    /// an old server; a portal-capable server appends a capability trailer to
    /// that normal ACK. Never optimistically send opcode 844: older cores close
    /// the socket when an opcode is beyond their table.
    /// </summary>
    private void AdvanceRealPortalCapabilityProbe(double now)
    {
        if (!_realPortalCapabilityProbeSent)
        {
            if (_net?.SuiControlRequest(0) != true) return;
            _realPortalCapabilityProbeSent = true;
            _realPortalCapabilityProbePending = true;
            _realPortalCapabilityReplyDeadline = now + RealPortalReplyTimeoutSeconds;
            Console.WriteLine("[real-portals] probing server capability through SUI control ACK");
            return;
        }

        if (_realPortalCapabilityProbePending && now >= _realPortalCapabilityReplyDeadline)
        {
            _realPortalCapabilityProbePending = false;
            Console.WriteLine(
                "[real-portals] server did not advertise portal-v1; keeping sealed apertures only");
        }
    }

    /// <summary>
    /// Consume an optional extension trailer after the fixed 25-byte
    /// SMSG_SUI_CONTROL_ACK body. Returns true for a zero-guid capability probe
    /// so its expected old-server denial is not surfaced as a UI error.
    /// </summary>
    private bool ApplyRealPortalCapabilityAck(ulong guid, PacketReader reader)
    {
        bool factionProbeReply = guid == 0 && _factionControlGroupsProbePending;
        bool isProbeReply = guid == 0 && (_config.Server.RealPortals || factionProbeReply);
        bool trailerValid = SuiCapabilityWire.TryRead(
            reader, out uint capabilities, out PortalPrewarmHint[] prewarmCatalog);
        // Braces are load-bearing: without them the member-facts apply below ran
        // unconditionally, so a trailerless ACK (an old core, or any future ACK
        // that omits the suffix) silently cleared capabilities the client had
        // already been told about. Latent against a current core — SendAck always
        // writes the trailer — but a trap for every capability added after it.
        if (trailerValid || capabilities != 0 || factionProbeReply)
        {
            ApplyFactionControlGroupsCapability(capabilities, factionProbeReply);
            ApplyPartyMemberFactsCapability(capabilities);
            ApplyPartyTaxiCapability(capabilities);
            ApplyTacticalFreezeCapability(capabilities);
            _commanderRaidAvailable = (capabilities & CommanderRaidWire.Capability) != 0;
        }
        if (trailerValid || capabilities != 0)
        {
            bool available = (capabilities & SuiCapabilityWire.RealPortalsV1) != 0;
            bool hadProtocol = _realPortalProtocolAvailable;
            if (available != hadProtocol)
                Console.WriteLine(available
                    ? "[real-portals] server advertised portal-v1"
                    : "[real-portals] server capability ACK has no portal-v1 bit");
            _realPortalProtocolAvailable = available;

            bool prewarmAvailable = available && trailerValid &&
                (capabilities & SuiCapabilityWire.PortalPrewarmCatalogV1) != 0 &&
                prewarmCatalog.Length == PortalPrewarmLaw.CatalogCount;
            bool hadPrewarm = _realPortalPrewarmCatalog.Length != 0;
            _realPortalPrewarmCatalog = prewarmAvailable ? prewarmCatalog : [];
            if (prewarmAvailable != hadPrewarm)
                Console.WriteLine(prewarmAvailable
                    ? "[real-portals] server supplied six cast-start prewarm destinations"
                    : "[real-portals] cast-start prewarm catalog unavailable");
            if (!prewarmAvailable && _realPortalCastPrewarm is not null)
                CancelRealPortalCastPrewarm(
                    "server withdrew cast-start prewarm catalog", retireScene: true);
            if (hadProtocol && !available)
                ResetRealPortals(resetCapability: false);
        }

        if (!isProbeReply) return false;
        _realPortalCapabilityProbePending = false;
        if (!_realPortalProtocolAvailable)
            Console.WriteLine(
                "[real-portals] server has no portal-v1 advertisement; keeping sealed apertures only");
        return true;
    }

    private void ResetRealPortalCapability()
    {
        _realPortalCapabilityProbeSent = false;
        _realPortalCapabilityProbePending = false;
        _realPortalProtocolAvailable = false;
        _realPortalCapabilityReplyDeadline = 0;
        _realPortalPrewarmCatalog = [];
        CancelRealPortalCastPrewarm("capability reset", retireScene: true);
    }

    private void BeginRealPortalCastPrewarm(SpellStartPacket packet)
    {
        if (_realPortalCastPrewarm is { } existing &&
            existing.CasterGuid == packet.Caster &&
            existing.Hint.SummonSpellId != packet.SpellId)
            CancelRealPortalCastPrewarm(
                "caster began a different spell", retireScene: true);

        if (!RealPortalPreviewEnabled || packet.Caster != ControlledGuid ||
            !PortalPrewarmLaw.TryFind(
                _realPortalPrewarmCatalog, packet.SpellId, out PortalPrewarmHint hint))
            return;

        CancelRealPortalCastPrewarm("superseded by a new portal cast", retireScene: true);
        double now = RealPortalNow();
        _realPortalCastPrewarm = new RealPortalCastPrewarm
        {
            CasterGuid = packet.Caster,
            Hint = hint,
            ExistingPortalGuids = _entities.Entities.Values
                .Where(entity => entity.IsGameObject &&
                    entity.Entry == hint.PortalEntry)
                .Select(entity => entity.Guid)
                .ToHashSet(),
            ExpiresAt = now + packet.CastTimeMs / 1000.0 + RealPortalCastSpawnGraceSeconds,
        };

        // One destination slot exists. An accepted local portal cast is the
        // highest-priority warm request; retire a different old candidate and
        // begin as soon as its asynchronous producers drain.
        if (_realPortalScene?.IsActive == true &&
            (_realPortalScene.Descriptor is not null ||
             _realPortalScene.PrewarmHint != hint))
            RetireRealPortalScene();

        Console.WriteLine(
            $"[real-portals] cast-start prewarm spell {packet.SpellId} -> " +
            $"map {hint.PreviewMapId}");
    }

    private void ObserveRealPortalCastGo(SpellGoPacket packet)
    {
        if (_realPortalCastPrewarm is not { } cast ||
            cast.CasterGuid != packet.Caster || cast.Hint.SummonSpellId != packet.SpellId)
            return;

        cast.SpellGoObserved = true;
        cast.ExpiresAt = RealPortalNow() + RealPortalCastSpawnGraceSeconds;
    }

    private void DelayRealPortalCastPrewarm(ulong caster, uint delayMs)
    {
        if (_realPortalCastPrewarm is { } cast && cast.CasterGuid == caster &&
            !cast.SpellGoObserved)
            cast.ExpiresAt += delayMs / 1000.0;
    }

    private void FailRealPortalCastPrewarm(ulong caster, uint spellId)
    {
        if (_realPortalCastPrewarm is { } cast && cast.CasterGuid == caster &&
            cast.Hint.SummonSpellId == spellId)
            CancelRealPortalCastPrewarm("portal cast failed", retireScene: true);
    }

    private void FailRealPortalCastPrewarmResult(uint spellId)
    {
        // SMSG_CAST_RESULT has no caster GUID. It is the result of this
        // session's submitted cast, which can belong to a possessed bot rather
        // than PlayerGuid, so correlate it by the one active portal spell.
        if (_realPortalCastPrewarm is { } cast &&
            cast.Hint.SummonSpellId == spellId)
            CancelRealPortalCastPrewarm("portal cast was rejected", retireScene: true);
    }

    private void CancelRealPortalCastPrewarm(string reason, bool retireScene)
    {
        if (_realPortalCastPrewarm is not { } cast) return;
        bool ownsProvisionalScene = _realPortalScene?.Descriptor is null &&
            (_realPortalScene?.PrewarmHint == cast.Hint ||
             (_realPortalSceneGuid == 0 && _realPortalSceneStartedAt > 0));
        _realPortalCastPrewarm = null;
        if (retireScene && ownsProvisionalScene) RetireRealPortalScene();
        Console.WriteLine($"[real-portals] cancelled cast-start prewarm: {reason}");
    }

    private void AdvanceRealPortalCastPrewarm(double now)
    {
        if (_realPortalCastPrewarm is not { } cast || _realPortalScene is null) return;
        if (now >= cast.ExpiresAt)
        {
            CancelRealPortalCastPrewarm("spawn correlation timed out", retireScene: true);
            return;
        }

        if (_realPortalScene.Failure is not null &&
            _realPortalScene.Descriptor is null &&
            _realPortalSceneGuid == 0 && _realPortalSceneStartedAt > 0)
        {
            CancelRealPortalCastPrewarm(
                $"destination warm failed: {_realPortalScene.Failure}", retireScene: true);
            return;
        }

        if (_realPortalScene.IsActive)
        {
            if (_realPortalScene.Descriptor is null &&
                _realPortalScene.PrewarmHint == cast.Hint)
                return;
            RetireRealPortalScene();
            return;
        }
        if (_realPortalScene.Retiring || !_realPortalScene.RetirementComplete) return;

        try
        {
            EnsureInstanceData();
            MapRow? map = _maps?.Get((int)cast.Hint.PreviewMapId);
            WdtFile? wdt = _mapWdts?.GetValueOrDefault((int)cast.Hint.PreviewMapId);
            if (map is null || wdt is null)
                throw new InvalidOperationException(
                    $"destination map {cast.Hint.PreviewMapId} has no loadable Map.dbc/WDT identity");

            _realPortalAtmosphere = NewRealPortalAtmosphere();
            _realPortalScene.BeginPrewarm(cast.Hint, map.Directory, wdt);
            _realPortalSceneGuid = 0;
            _realPortalSceneDescriptor = null;
            _realPortalSceneStartedAt = now;
            Console.WriteLine(
                $"[real-portals] warming map {cast.Hint.PreviewMapId} during cast " +
                $"{cast.Hint.SummonSpellId}");
        }
        catch (Exception ex)
        {
            CancelRealPortalCastPrewarm(
                $"destination warm could not start: {ex.Message}", retireScene: true);
        }
    }

    private void AdvanceRealPortalHandshake(RealPortalVisual portal, double now)
    {
        if (portal.PreparePending && now >= portal.ReplyDeadline)
        {
            portal.PreparePending = false;
            portal.NextPrepareAt = now + RealPortalRetrySeconds;
            Console.WriteLine($"[real-portals] descriptor timeout for 0x{portal.Guid:X}");
        }

        if (portal.ReadySent && !portal.ReadyConfirmed && now >= portal.ReadyReplyDeadline)
        {
            portal.ReadySent = false;
            portal.NextPrepareAt = Math.Min(portal.NextPrepareAt, now + 1.0);
        }

        if (portal.PreparePending || now < portal.NextPrepareAt) return;

        // A descriptor which has not yet finished loading owns the correlation;
        // do not replace it merely because its initial READY is still pending.
        if (portal.Descriptor is not null && !portal.ReadyConfirmed &&
            !portal.LoadFailed && portal.ReadySent && now < portal.ReplyDeadline)
            return;

        uint requestId = NextRealPortalRequestId();
        if (_net?.SuiPortalPrepare(requestId, portal.Guid) != true)
        {
            portal.NextPrepareAt = now + RealPortalRetrySeconds;
            return;
        }

        InvalidateOtherRealPortalCorrelations(portal.Guid);
        portal.RequestId = requestId;
        portal.PreparePending = true;
        portal.ReplyDeadline = now + RealPortalReplyTimeoutSeconds;
        // This is also the earliest retry if a malformed/denied response clears
        // the pending edge without supplying a server retry hint.
        portal.NextPrepareAt = now + RealPortalRetrySeconds;
        Console.WriteLine($"[real-portals] preparing 0x{portal.Guid:X} request {requestId}");
    }

    private RealPortalVisual? SelectRealPortalSceneTarget()
    {
        float enterRadiusSquared = RealPortalPreloadRadius * RealPortalPreloadRadius;
        float exitRadiusSquared = RealPortalPreloadExitRadius * RealPortalPreloadExitRadius;

        if (_realPortalCastPrewarm is { PortalGuid: not 0 } cast &&
            _realPortals.TryGetValue(cast.PortalGuid, out RealPortalVisual? castPortal) &&
            castPortal.Descriptor is not null && !castPortal.LoadFailed &&
            castPortal.DistanceSquared <= exitRadiusSquared)
            return castPortal;

        // Retain the loaded candidate through a wider exit radius so threshold
        // jitter does not tear down a useful warm while the player backs away.
        if (_realPortalSceneGuid != 0 &&
            _realPortals.TryGetValue(_realPortalSceneGuid, out RealPortalVisual? active) &&
            active.Descriptor is not null && !active.LoadFailed &&
            active.DistanceSquared <= exitRadiusSquared)
            return active;

        return _realPortals.Values
            .Where(p => p.Descriptor is not null && !p.LoadFailed &&
                        p.DistanceSquared <= enterRadiusSquared)
            .OrderBy(p => p.DistanceSquared)
            .FirstOrDefault();
    }

    private void AdvanceRealPortalScene(RealPortalVisual? target, double now)
    {
        if (_realPortalScene is null) return;

        if (_realPortalScene.Failure is not null && _realPortalSceneGuid != 0 &&
            _realPortals.TryGetValue(_realPortalSceneGuid, out RealPortalVisual? failed))
        {
            ReportRealPortalLoadFailure(failed, _realPortalScene.Failure, now);
            RetireRealPortalScene();
        }

        if (_realPortalScene.IsActive && _realPortalScene.Descriptor is null)
        {
            if (_realPortalCastPrewarm is not { } cast ||
                _realPortalScene.PrewarmHint != cast.Hint)
            {
                RetireRealPortalScene();
                return;
            }

            if (cast.PortalGuid == 0 || target?.Guid != cast.PortalGuid ||
                target.Descriptor is not { } spawnedDescriptor)
                return;

            if (!_realPortalScene.TryBindAuthoritative(spawnedDescriptor))
            {
                CancelRealPortalCastPrewarm(
                    "spawned descriptor did not match the server cast hint",
                    retireScene: true);
                return;
            }

            _realPortalSceneGuid = target.Guid;
            _realPortalSceneDescriptor = spawnedDescriptor;
            _realPortalCastPrewarm = null;
            Console.WriteLine(
                $"[real-portals] reused cast-start warm for 0x{target.Guid:X}");
        }

        if (_realPortalScene.IsActive)
        {
            bool candidateLocallyReady =
                _realPortalScene.VisualReady && _realPortalScene.ArrivalSupport;
            bool minimapWarmReady = candidateLocallyReady && target is not null &&
                AdvanceRealPortalMinimapWarm(target);
            if ((!candidateLocallyReady || !minimapWarmReady) &&
                _realPortalSceneStartedAt > 0 &&
                now - _realPortalSceneStartedAt >= RealPortalCandidateWatchdogSeconds &&
                target is not null)
            {
                ReportRealPortalLoadFailure(target,
                    $"candidate did not become fully ready within " +
                    $"{RealPortalCandidateWatchdogSeconds:F0}s", now);
                RetireRealPortalScene();
                return;
            }

            bool stillMatches = target is not null && target.Guid == _realPortalSceneGuid &&
                target.Descriptor is { } current && _realPortalSceneDescriptor is { } loaded &&
                SamePreparedDestination(current, loaded);
            if (!stillMatches && target is not null && target.Guid == _realPortalSceneGuid &&
                target.Descriptor is { } refreshed && _realPortalSceneDescriptor is { } previous &&
                SameDestinationGeometry(refreshed, previous) &&
                _realPortalScene.TryRefreshDescriptor(refreshed))
            {
                _realPortalSceneDescriptor = refreshed;
                stillMatches = true;
            }
            if (!stillMatches) RetireRealPortalScene();
            else
            {
                AdvanceRealPortalReadiness(target!, now);
                return;
            }
        }

        if (_realPortalScene.Retiring || !_realPortalScene.RetirementComplete) return;
        if (target?.Descriptor is not { } descriptor) return;

        try
        {
            EnsureInstanceData();
            MapRow? map = _maps?.Get((int)descriptor.PreviewMapId);
            WdtFile? wdt = _mapWdts?.GetValueOrDefault((int)descriptor.PreviewMapId);
            if (map is null || wdt is null)
                throw new InvalidOperationException(
                    $"destination map {descriptor.PreviewMapId} has no loadable Map.dbc/WDT identity");

            _realPortalAtmosphere = NewRealPortalAtmosphere();
            _realPortalScene.Begin(descriptor, map.Directory, wdt);
            _realPortalSceneGuid = target.Guid;
            _realPortalSceneDescriptor = descriptor;
            _realPortalSceneStartedAt = now;
            Console.WriteLine(
                $"[real-portals] loading map {descriptor.PreviewMapId} for 0x{target.Guid:X}");
        }
        catch (Exception ex)
        {
            ReportRealPortalLoadFailure(target, ex.Message, now);
            RetireRealPortalScene();
        }

        AdvanceRealPortalReadiness(target, now);
    }

    private void AdvanceRealPortalReadiness(RealPortalVisual target, double now)
    {
        if (target.DistanceSquared <= RealPortalPreloadRadius * RealPortalPreloadRadius &&
            _realPortalSceneGuid == target.Guid &&
            _realPortalScene?.VisualReady == true && _realPortalScene.ArrivalSupport &&
            !target.PreparePending &&
            now - target.DiscoveredAt >= RealPortalPreAnimationSeconds && !target.ReadySent &&
            AdvanceRealPortalMinimapWarm(target))
        {
            SendRealPortalReady(target, PortalLoadResult.Ready, now);
        }
    }

    private bool AdvanceRealPortalMinimapWarm(RealPortalVisual target)
    {
        if (_realPortalScene is null || target.Descriptor is not { } descriptor)
            return true;
        var identity = descriptor.Identity;
        if (_realPortalMinimapWarmIdentity != identity)
        {
            _realPortalMinimapWarmIdentity = identity;
            _realPortalMinimapWarmContext = null;
        }

        // READY must remain valid if the player changes the indoor zoom before
        // crossing. Prepare the widest authored view once; every smaller zoom
        // is then a strict subset and needs no additional texture work.
        float radius = WmoMinimapProjection.ZoomRadiusYards[0];
        _realPortalMinimapWarmContext ??=
            _realPortalScene.ResolveArrivalInteriorMinimap(radius);
        return _realPortalMinimapWarmContext is not { } context ||
            AdvanceMinimapTexturePreparation(
                context, descriptor.PreviewPosition, radius);
    }

    private void PublishRealPortalApertures(float dt, double now)
    {
        foreach (RealPortalVisual portal in _realPortals.Values)
        {
            if (!portal.PresentationRelevant) continue;
            ScenePortalDescriptor? descriptor = portal.Descriptor;
            Vector3 center = descriptor?.SourceCenter ??
                portal.GroundPosition + Vector3.UnitZ *
                (RealPortalHalfHeight + RealPortalBottomClearance);
            float yaw = descriptor?.SourceYaw ?? portal.Yaw;
            float halfWidth = descriptor?.HalfWidth ?? RealPortalHalfWidth;
            float halfHeight = descriptor?.HalfHeight ?? RealPortalHalfHeight;
            Vector3 right = new(MathF.Sin(yaw), -MathF.Cos(yaw), 0f);

            bool activeTexture = portal.Guid == _realPortalSceneGuid &&
                _realPortalScene?.VisualReady == true;
            bool leaseReady = portal.ReadyConfirmed && now < portal.ReadyLeaseExpiresAt;
            bool animationReady = now - portal.DiscoveredAt >= RealPortalPreAnimationSeconds;
            float targetBlend = activeTexture && leaseReady && animationReady ? 1f : 0f;
            float fadeStep = MathF.Max(0f, dt) / RealPortalLiveFadeSeconds;
            portal.LiveBlend = targetBlend > portal.LiveBlend
                ? MathF.Min(targetBlend, portal.LiveBlend + fadeStep)
                : MathF.Max(targetBlend, portal.LiveBlend - fadeStep);

            uint liveTexture = activeTexture && portal.LiveBlend > 0f
                ? _realPortalScene!.Texture
                : 0;
            float sealProgress = Math.Clamp(
                (float)((now - portal.DiscoveredAt) / RealPortalPreAnimationSeconds), 0f, 1f);

            _particles?.UpsertMagePortalAperture(
                portal.Guid, center, right, Vector3.UnitZ,
                halfWidth, halfHeight, sealProgress, sealAlpha: 0.82f,
                liveTexture, portal.LiveBlend);
        }
    }

    /// <summary>
    /// Resolve the local development/fallback walk-through before this frame's
    /// position is copied to the driven entity or put on the wire.  A
    /// portal-v1 server gets time to finish its per-player preview first; an
    /// older SUI server falls back immediately to the ordinary authoritative
    /// CMSG_GAMEOBJ_USE path and therefore retains the normal loading curtain.
    /// </summary>
    private void ResolveRealPortalMovement(Vector3 previousFeet)
    {
        // This resolver mutates the local controller and emits a real
        // CMSG_GAMEOBJ_USE.  A Free View controller is an observer rig, not a
        // body that may cross/use gameplay portals; pending/parked/fly states
        // likewise leave authoritative pose on the streamed body.
        if (!ControllerOwnsControlledBodyPose || !RealPortalsEnabled || _controller is null ||
            _net is not { IsInWorld: true } || _realPortals.Count == 0)
            return;

        Vector3 proposedFeet = _controller.Position;
        if (Vector3.DistanceSquared(previousFeet, proposedFeet) <= 1e-10f) return;

        double now = RealPortalNow();
        foreach (RealPortalVisual portal in _realPortals.Values.OrderBy(p => p.DistanceSquared))
        {
            if (!portal.PresentationRelevant) continue;
            PortalFrame frame = RealPortalSourceFrame(portal);
            if (!frame.TryNormalize(out PortalFrame normalized)) continue;

            float epsilon = MathF.Max(0.05f, portal.Descriptor?.PlaneEpsilon ?? 0.35f);
            float previousDistance = Vector3.Dot(previousFeet - normalized.Center, normalized.Normal);
            portal.CrossingArmedSide = PortalCrossingLaw.ResolveArmedSide(
                portal.CrossingArmedSide,
                previousDistance,
                epsilon,
                now - portal.LastCrossingUseAt >= RealPortalCrossingLatchSeconds);

            if (!PortalCrossingLaw.TryCross(
                    previousFeet, proposedFeet, normalized,
                    portal.Descriptor?.HalfWidth ?? RealPortalHalfWidth,
                    portal.Descriptor?.HalfHeight ?? RealPortalHalfHeight,
                    MathF.Max(0f, _config.Movement.Radius),
                    MathF.Max(0.1f, _config.Movement.Height),
                    out PortalCrossingLaw.Crossing crossing) ||
                portal.CrossingArmedSide == 0 ||
                crossing.FromSide != portal.CrossingArmedSide)
                continue;

            bool previewReady = portal.ReadyConfirmed && now < portal.ReadyLeaseExpiresAt;
            // The hold clock belongs to one approach: a stale stamp from an earlier one restarts it.
            if (!previewReady && (double.IsNaN(portal.UnreadyHoldSince) || now - portal.UnreadyHoldSince > 3.0))
                portal.UnreadyHoldSince = now;
            // An AIRBORNE body (a kart jumping off a ramp into the window) is never held: pinned to the plane it
            // falls out of the window during the hold and the crossing is lost (2026-10-04 live, raceway jump).
            bool holdExpired = portal.LoadFailed || _controller.Grounded != true ||
                now - portal.UnreadyHoldSince >= RealPortalUnreadyHoldSeconds;
            if (_realPortalProtocolAvailable && !previewReady && !holdExpired)
            {
                // A capable server promised the prepared path, so do not let a
                // source-world movement packet escape beyond an unready film.
                _controller.Position = crossing.FeetIntersection +
                    normalized.Normal * (crossing.FromSide * epsilon);
                if (!portal.UnreadyCrossingLogged)
                {
                    portal.UnreadyCrossingLogged = true;
                    Console.WriteLine(
                        $"[real-portals] holding crossing 0x{portal.Guid:X} until destination READY");
                }
                return;
            }

            portal.UnreadyCrossingLogged = false;
            portal.UnreadyHoldSince = double.NaN;
            if (!UseGameObject(portal.Guid))
            {
                // Keep the player on the side from which the failed use was
                // attempted. The server remains authoritative and a later
                // armed crossing may retry rather than silently walking through.
                _controller.Position = crossing.FeetIntersection +
                    normalized.Normal * (crossing.FromSide * epsilon);
                return;
            }

            portal.CrossingArmedSide = 0;
            portal.LastCrossingUseAt = now;
            NoteFrameRecorderEvent($"portal-{portal.Entry}");
            Console.WriteLine(
                $"[real-portals] crossed 0x{portal.Guid:X}; sent authoritative GAMEOBJ_USE" +
                (!_realPortalProtocolAvailable ? " through compatibility fallback"
                    : previewReady ? " after READY" : " after an unready hold (loading curtain)"));
            return;
        }
    }

    /// <summary>Ray-pick the full procedural window from either side.</summary>
    private bool TryPickRealPortalAperture(
        Vector3 origin, Vector3 direction, float limit,
        out ulong guid, out float distance)
    {
        guid = 0;
        distance = float.PositiveInfinity;
        if (!RealPortalsEnabled) return false;

        float nearest = limit;
        foreach (RealPortalVisual portal in _realPortals.Values)
        {
            if (!portal.PresentationRelevant) continue;
            if (!PortalCrossingLaw.TryRayHit(
                    origin, direction, RealPortalSourceFrame(portal),
                    portal.Descriptor?.HalfWidth ?? RealPortalHalfWidth,
                    portal.Descriptor?.HalfHeight ?? RealPortalHalfHeight,
                    nearest, out float hit))
                continue;
            nearest = hit;
            guid = portal.Guid;
        }

        if (guid == 0) return false;
        distance = nearest;
        return true;
    }

    private static PortalFrame RealPortalSourceFrame(RealPortalVisual portal) =>
        portal.Descriptor?.SourceFrame ?? PortalFrame.FromYaw(
            portal.GroundPosition + Vector3.UnitZ *
            (RealPortalHalfHeight + RealPortalBottomClearance),
            portal.Yaw);

    /// <summary>
    /// Called only after the ordinary GAMEOBJ_USE was successfully put on the
    /// wire. A READY lease and a published complete preview are both mandatory;
    /// ordinary teleports never gain a portal-themed curtain by proximity alone.
    /// </summary>
    private void ArmRealPortalHandoffAfterSuccessfulUse(ulong portalGuid)
    {
        double now = RealPortalNow();
        if (_gl is null ||
            !_realPortals.TryGetValue(portalGuid, out RealPortalVisual? portal) ||
            portal.Descriptor is not { } descriptor ||
            !portal.ReadyConfirmed || now >= portal.ReadyLeaseExpiresAt ||
            _realPortalSceneGuid != portalGuid ||
            _realPortalSceneDescriptor is not { } renderedDescriptor ||
            !SamePreparedDestination(descriptor, renderedDescriptor) ||
            _realPortalScene?.VisualReady != true || _realPortalScene.Texture == 0)
        {
            return;
        }

        try
        {
            _realPortalHandoffSnapshot ??= new PortalHandoffSnapshot(_gl);
            Vector2 size = _realPortalScene.PublishedSize;
            _realPortalHandoffSnapshot.Capture(
                _realPortalScene.Texture, Math.Max(1, (int)size.X), Math.Max(1, (int)size.Y));
            _realPortalHandoffDescriptor = descriptor;
            _realPortalHandoffPhase = RealPortalHandoffPhase.Armed;
            _realPortalHandoffArmedAt = now;
            Console.WriteLine(
                $"[real-portals] retained complete destination frame for 0x{portalGuid:X}");
        }
        catch (Exception ex)
        {
            CancelRealPortalHandoff($"snapshot failed: {ex.Message}");
        }
    }

    /// <summary>
    /// TRANSFER_PENDING has only a map id. It may show the retained frame
    /// immediately when that map agrees, but NEW_WORLD still has to validate the
    /// authoritative position before teardown commits to the handoff.
    /// </summary>
    private bool BeginRealPortalWorldTransfer(uint destinationMapId)
    {
        if (_realPortalHandoffPhase != RealPortalHandoffPhase.Armed ||
            _realPortalHandoffDescriptor is not { } descriptor ||
            _realPortalHandoffSnapshot?.HasFrame != true)
            return false;

        if (RealPortalNow() - _realPortalHandoffArmedAt > RealPortalHandoffArmSeconds ||
            descriptor.PreviewMapId != destinationMapId)
        {
            CancelRealPortalHandoff("authoritative transfer did not match the prepared portal");
            return false;
        }

        _realPortalHandoffPhase = RealPortalHandoffPhase.Tentative;
        Console.WriteLine(
            $"[real-portals] portal handoff covering transfer to map {destinationMapId}");
        return true;
    }

    /// <summary>Validate and consume the retained image for NEW_WORLD or a
    /// non-resident same-map movement teleport.</summary>
    private bool ConfirmRealPortalHandoff(uint destinationMapId, in Vector3 destination)
    {
        if (_realPortalHandoffPhase is not
                (RealPortalHandoffPhase.Armed or RealPortalHandoffPhase.Tentative) ||
            _realPortalHandoffDescriptor is not { } descriptor ||
            _realPortalHandoffSnapshot?.HasFrame != true)
            return false;

        if (RealPortalNow() - _realPortalHandoffArmedAt > RealPortalHandoffArmSeconds ||
            !PortalHandoffLaw.MatchesPreparedDestination(
                descriptor.PreviewMapId, descriptor.PreviewPosition,
                destinationMapId, destination))
        {
            CancelRealPortalHandoff("authoritative destination did not match the prepared preview");
            return false;
        }

        _realPortalHandoffPhase = RealPortalHandoffPhase.Transit;
        Console.WriteLine(
            $"[real-portals] authoritative destination matched retained portal frame");
        return true;
    }

    /// <summary>
    /// Exchange the isolated READY destination for the active renderer set.
    /// This is the part that makes preparation real: the terrain, buildings,
    /// doodads, liquids, ADT cache and collision which produced the live portal
    /// image become the world the character stands in. The source bundle moves
    /// into the preview slot and drains incrementally after the crossing.
    /// </summary>
    private bool TryPromotePreparedRealPortalWorld(
        uint destinationMapId, in Vector3 authoritativePosition)
    {
        if (_realPortalHandoffPhase != RealPortalHandoffPhase.Transit ||
            _realPortalHandoffDescriptor is not { } expected ||
            _realPortalScene is null || _terrain is null || _wmo is null ||
            _liquid is null || _adts is null || _controller is null ||
            !ClientGeometryCollision ||
            !PreparedRealPortalMinimapReadyAt(authoritativePosition))
            return false;

        EnsureInstanceData();
        WdtFile? activeWdt = _mapWdts?.GetValueOrDefault(_config.Start.Map) ??
                             WdtFile.Read(_config.ClientDataPath, _config.Start.MapName);
        if (activeWdt is null)
        {
            Console.WriteLine("[real-portals] prepared-world promotion unavailable: " +
                              "active WDT identity is missing");
            return false;
        }
        if (destinationMapId > int.MaxValue)
            return false;
        int promotedMapId = (int)destinationMapId;

        TerrainRenderer oldTerrain = _terrain;
        WmoRenderer oldWmo = _wmo;
        DoodadRenderer? oldDoodads = _doodads;
        LiquidRenderer oldLiquid = _liquid;
        AdtCache oldAdts = _adts;
        var oldCentre = _residentCentre ??
                        TerrainRenderer.TileAt(_controller.Position.X, _controller.Position.Y);
        var oldRing = oldTerrain.LoadedTiles.ToArray();
        Task? oldExternalDrain = CombineRealPortalRetirementDrains(
            _backgroundAdtLoad, _collisionBuildTask);

        // The old renderer callbacks write into the active load trace. Once the
        // bundle belongs to the hidden retirement slot, its queue completions
        // must not be attributed to the destination world.
        oldTerrain.PreloadDequeued = null;
        oldWmo.PreloadDequeued = null;
        if (oldDoodads is not null) oldDoodads.PreloadDequeued = null;

        var replacement = new PortalWorldBundle(
            oldTerrain, oldWmo, oldDoodads, oldLiquid, oldAdts,
            activeWdt, oldAdts.MapName, oldCentre, oldRing, _collision,
            oldExternalDrain);

        if (!_realPortalScene.TryExchangePreparedWorld(
                expected, destinationMapId, authoritativePosition,
                replacement, out PortalWorldBundle? prepared) || prepared is null)
        {
            // No ownership changed on failure; restore the live trace hooks.
            oldTerrain.PreloadDequeued = NoteLoadTerrainDequeue;
            oldWmo.PreloadDequeued = (path, distanceSq) =>
                NoteLoadAssetDequeue("wmo", path, distanceSq);
            if (oldDoodads is not null)
                oldDoodads.PreloadDequeued = NoteLoadAssetDequeue;
            return false;
        }

        try
        {
            // Publish the new owners immediately after the atomic exchange.
            // From this point on GameLoop never retains a renderer that the
            // scene owns; later tuning work cannot create a double-owner gap.
            _terrain = prepared.Terrain;
            _wmo = prepared.Wmo;
            _doodads = prepared.Doodads;
            _liquid = prepared.Liquid;
            _adts = prepared.Adts;
            _config.Start.Map = promotedMapId;
            _config.Start.MapName = prepared.MapName;
            _collision = prepared.Collision is { IsEmpty: false }
                ? prepared.Collision
                : null;
            _vmaps = null;

            _controller.RebindTerrain(_terrain);
            _controller.Collision = _collision;

            // Clear source-world protocol/streaming state without touching either
            // side of the renderer exchange. ResetRealPortals sees the scene already
            // retiring and therefore only drops aperture/correlation state.
            TearDownWorldContent(preserveWorldBundle: true);

            CopyPromotedRendererTuning(oldTerrain, oldWmo, oldDoodads, prepared);

            _residentCentre = prepared.RingCenter;
            _loadCentre = prepared.RingCenter;
            _globalWmoPlacement = prepared.Wdt.UsesGlobalWmo
                ? prepared.Wdt.GlobalWmo
                : null;

            _controller.TerrainAbsentByDesign = _globalWmoPlacement is not null;
            _wmo.OcclusionWorld = _collision;
            _wmo.Overrides = _overrides;
            _wmo.PreloadDequeued = (path, distanceSq) =>
                NoteLoadAssetDequeue("wmo", path, distanceSq);
            _terrain.PreloadDequeued = NoteLoadTerrainDequeue;
            if (_doodads is not null)
            {
                _doodads.PortalVisibility = _wmo.IsDoodadPortalVisible;
                _doodads.PreloadDequeued = NoteLoadAssetDequeue;
                _doodads.HighlightedDynamicKey = 0;
            }

            _worldShown = true;
            _creatureLifecycle.BeginWorldLoad(Stopwatch.GetTimestamp());
            _wmo.WorldShown = true;
            _wmo.NowSeconds = _worldTime;
            if (_doodads is not null)
            {
                _doodads.WorldShown = true;
                _doodads.NowSeconds = _worldTime;
            }
            ApplyWater(Settings);

            _lastDemandCentre = new Vector2(
                authoritativePosition.X, authoritativePosition.Y);
            _doodadDemandDelay = 0.25f;
            _newDoodadModels.Clear();
            _newDoodadModelKeys.Clear();
            _foliage?.ForceRescatter();

            // The prepared inner ring is immediately renderable. Speculative lead
            // terrain and the wider WMO discovery ring may resume asynchronously;
            // neither is allowed back onto the crossing path.
            if (_globalWmoPlacement is null)
            {
                _terrain.QueuePreload(
                    TerrainRenderer.TileRing(
                        prepared.RingCenter.col, prepared.RingCenter.row,
                        _config.Start.TileRadius + 1),
                    _adts, prepared.RingCenter);

                var loaded = _terrain.LoadedTiles.ToHashSet();
                foreach (var tile in TerrainRenderer.TileRing(
                             prepared.RingCenter.col, prepared.RingCenter.row,
                             WmoPreloadRadius)
                         .Where(tile => !loaded.Contains(tile))
                         .OrderBy(tile => Math.Abs(tile.col - prepared.RingCenter.col) +
                                          Math.Abs(tile.row - prepared.RingCenter.row)))
                    _backgroundDiscovery.Enqueue(tile);
            }

            Console.WriteLine(
                $"[real-portals] promoted prepared map {destinationMapId} " +
                $"({prepared.Terrain.TileCount} terrain, {prepared.Wmo.InstanceCount} WMO, " +
                $"{prepared.Doodads?.InstanceCount ?? 0} doodads); source retiring asynchronously");
            return true;
        }
        catch (Exception ex)
        {
            // The exchange cannot be rolled back: the preview slot already owns
            // the source bundle. Keep the prepared owners installed and let the
            // caller enter the ordinary collision-gated loader with them. That
            // fallback may be slower, but it remains ownership-safe and never
            // exposes a partially configured scene.
            _terrain = prepared.Terrain;
            _wmo = prepared.Wmo;
            _doodads = prepared.Doodads;
            _liquid = prepared.Liquid;
            _adts = prepared.Adts;
            _config.Start.Map = promotedMapId;
            _config.Start.MapName = prepared.MapName;
            _collision = prepared.Collision is { IsEmpty: false }
                ? prepared.Collision
                : null;
            _vmaps = null;
            _controller.RebindTerrain(_terrain);
            _controller.Collision = _collision;
            Console.WriteLine(
                $"[real-portals] prepared-world activation failed after exchange: " +
                $"{ex.Message}; using guarded world loader");
            return false;
        }
    }

    /// <summary>
    /// The core may adjust its final landing pose within the handoff tolerance.
    /// Re-resolve that exact point in the still-isolated destination and promote
    /// only when every authored tile it can reveal is already adopted. A rare
    /// boundary mismatch therefore falls back to the safe world-load path
    /// instead of turning a seamless crossing into a first-frame UI hitch.
    /// </summary>
    private bool PreparedRealPortalMinimapReadyAt(in Vector3 authoritativePosition)
    {
        if (_realPortalScene is null || _gameplayArt is null) return true;
        float radius = WmoMinimapProjection.ZoomRadiusYards[0];
        var context = _realPortalScene.ResolvePreparedInteriorMinimap(
            authoritativePosition, radius);
        if (context is null) return true;

        foreach (string path in MinimapInteriorArtPaths(
                     context, authoritativePosition, radius))
        {
            if (_gameplayArt.IsResolved(path)) continue;
            Console.WriteLine(
                "[real-portals] prepared-world promotion declined: authoritative " +
                $"landing exposes an unprepared interior minimap tile ({path})");
            return false;
        }
        return true;
    }

    private static Task? CombineRealPortalRetirementDrains(params Task?[] candidates)
    {
        Task[] drains = candidates.Where(static task => task is not null)
            .Cast<Task>().Distinct().ToArray();
        return drains.Length switch
        {
            0 => null,
            1 => drains[0],
            _ => Task.WhenAll(drains),
        };
    }

    private static void CopyPromotedRendererTuning(
        TerrainRenderer sourceTerrain,
        WmoRenderer sourceWmo,
        DoodadRenderer? sourceDoodads,
        PortalWorldBundle destination)
    {
        destination.Terrain.DebugMode = sourceTerrain.DebugMode;
        destination.Terrain.ApplyHoles = sourceTerrain.ApplyHoles;
        destination.Terrain.TextureScale = sourceTerrain.TextureScale;
        destination.Terrain.AuthoredShadowStrength = sourceTerrain.AuthoredShadowStrength;
        destination.Terrain.ChunkCulling = sourceTerrain.ChunkCulling;
        destination.Terrain.PartySight = sourceTerrain.PartySight;

        WmoRenderer wmo = destination.Wmo;
        wmo.PartySight = sourceWmo.PartySight;
        wmo.Enabled = sourceWmo.Enabled;
        wmo.FrustumCulling = sourceWmo.FrustumCulling;
        wmo.UseDistanceLodShells = sourceWmo.UseDistanceLodShells;
        wmo.SuppressDistanceLodShells = sourceWmo.SuppressDistanceLodShells;
        wmo.AppearFade = sourceWmo.AppearFade;
        wmo.AppearFadeSeconds = sourceWmo.AppearFadeSeconds;
        wmo.MarkPlacedOpaque();
        wmo.InsideInstanceMargin = sourceWmo.InsideInstanceMargin;
        wmo.DumpLargeWmoGroups = sourceWmo.DumpLargeWmoGroups;
        wmo.InteriorCullDistance = sourceWmo.InteriorCullDistance;
        wmo.UsePortalCulling = sourceWmo.UsePortalCulling;
        wmo.ShellNearGuard = sourceWmo.ShellNearGuard;
        bool reclassifyShells = wmo.ImpostorMaxVertices != sourceWmo.ImpostorMaxVertices;
        wmo.ImpostorMaxVertices = sourceWmo.ImpostorMaxVertices;
        wmo.VisTrace = sourceWmo.VisTrace;
        wmo.OcclusionCulling = sourceWmo.OcclusionCulling;
        wmo.OcclusionMinDistance = sourceWmo.OcclusionMinDistance;
        wmo.OcclusionMargin = sourceWmo.OcclusionMargin;
        wmo.DrawDistance = sourceWmo.DrawDistance;
        wmo.ForceTwoSided = sourceWmo.ForceTwoSided;
        wmo.AlphaCutoff = sourceWmo.AlphaCutoff;
        wmo.UseVertexColors = sourceWmo.UseVertexColors;
        wmo.VertexColorScale = sourceWmo.VertexColorScale;
        wmo.InteriorBrightness = sourceWmo.InteriorBrightness;
        if (reclassifyShells)
            wmo.ReclassifyShells();

        if (sourceDoodads is null || destination.Doodads is null) return;
        DoodadRenderer doodads = destination.Doodads;
        doodads.PartySight = sourceDoodads.PartySight;
        doodads.Enabled = sourceDoodads.Enabled;
        doodads.FrustumCulling = sourceDoodads.FrustumCulling;
        doodads.DemandStreaming = sourceDoodads.DemandStreaming;
        doodads.UseInstancing = sourceDoodads.UseInstancing;
        doodads.FlatCullBounds = sourceDoodads.FlatCullBounds;
        doodads.DrawDistance = sourceDoodads.DrawDistance;
        doodads.AlphaCutoff = sourceDoodads.AlphaCutoff;
        doodads.VertexColorScale = sourceDoodads.VertexColorScale;
        doodads.InteriorLighting = sourceDoodads.InteriorLighting;
        doodads.AppearFade = sourceDoodads.AppearFade;
        doodads.AppearFadeSeconds = sourceDoodads.AppearFadeSeconds;
        doodads.MarkPlacedOpaque();
        doodads.CollisionBasisIndex = sourceDoodads.CollisionBasisIndex;
    }

    /// <summary>Drop any transfer curtain without entering the generic loader.</summary>
    private void CompletePromotedRealPortalTransition()
    {
        // The stock destination may sit inside its paired return trigger. Seed
        // that volume before gameplay resumes, exactly as the ordinary loader's
        // Finish phase does, so seamless arrival cannot bounce straight back.
        _portalLatch = _controller is null
            ? 0
            : _areaTriggers?.Containing(
                _config.Start.Map, _controller.Position)?.Id ?? 0;
        _portalLatchMap = _config.Start.Map;
        if (_portalLatch != 0)
            Console.WriteLine($"[portal] promoted arrival latched trigger {_portalLatch}");

        _worldEntryTransitionStage = 0;
        _worldLoading = false;
        _worldLoadingMapId = null;
        _loadPhase = WorldLoadPhase.Done;
        _loadProgress = 1f;
        _loadCurtainAlpha = 0f;
        _loadRevealHoldRemaining = 0f;
        _loadFadeWarmStage = 0;
        _loadScreen?.Dispose();
        _loadScreen = null;
        _loadScreenMapId = null;
        _preWorldHudPrimed = true;
        _travelStatus = "portal destination ready";
        CancelRealPortalHandoff("prepared destination became the active world");
    }

    private bool DrawRealPortalHandoff(float alpha)
    {
        if (_realPortalHandoffPhase is not
                (RealPortalHandoffPhase.Tentative or RealPortalHandoffPhase.Transit) ||
            _realPortalHandoffSnapshot?.HasFrame != true || _loadScreen is null)
            return false;

        _loadScreen.RenderPortalHandoff(_realPortalHandoffSnapshot.Texture, alpha);
        return true;
    }

    private void ExpireRealPortalHandoff(double now)
    {
        if (_realPortalHandoffPhase is
                (RealPortalHandoffPhase.Armed or RealPortalHandoffPhase.Tentative) &&
            !_worldLoading && now - _realPortalHandoffArmedAt > RealPortalHandoffArmSeconds)
            CancelRealPortalHandoff("authoritative transfer did not arrive before the handoff timeout");
    }

    private void CancelRealPortalHandoff(string reason)
    {
        if (_realPortalHandoffPhase != RealPortalHandoffPhase.None)
            Console.WriteLine($"[real-portals] released portal handoff: {reason}");
        _realPortalHandoffPhase = RealPortalHandoffPhase.None;
        _realPortalHandoffDescriptor = null;
        _realPortalHandoffArmedAt = 0;
        _realPortalHandoffSnapshot?.Clear();
    }

    private void RenderRealPortalPreview(float dt)
    {
        if (_realPortalScene is null || !_realPortalScene.VisualGeometryReady ||
            _realPortalSceneGuid == 0 || _realPortalAtmosphere is null ||
            !_realPortals.TryGetValue(_realPortalSceneGuid, out RealPortalVisual? portal) ||
            portal.Descriptor is not { } descriptor)
            return;

        SyncRealPortalTargetSize();

        ConfigureRealPortalAtmosphere(_realPortalAtmosphere, descriptor);
        _realPortalScene.RenderPreview(
            _window.Camera, descriptor.SourceFrame, _realPortalAtmosphere, dt,
            _coupleFarPlaneToFog);
    }

    partial void ApplyRealPortalDescriptor(PortalDescriptorPacket descriptor)
    {
        PortalDescriptorPacket packet = descriptor;
        double now = RealPortalNow();
        if (!RealPortalPreviewEnabled ||
            !_realPortals.TryGetValue(packet.PortalGuid, out RealPortalVisual? portal) ||
            !portal.PreparePending || packet.RequestId != portal.RequestId)
            return;

        portal.PreparePending = false;
        // A PLACED warm (WoW Karting pushes the next leg's destination at GO) is denied only by distance: a
        // kart at 21 yd/s reaches the preload radius on the client before the server's last known position
        // does (owner, 2026-10-04: "went through the portal, glitching, floor gone" - the denial discarded a
        // minute-old warm and the 5 s retry left under two seconds to load Elwynn cold). Keep the warm and
        // ask again in half a second; the server's answer, not this guess, still decides the crossing.
        if (packet.Result == PortalDescriptorResult.Denied &&
            _realPortalCastPrewarm is { Placed: true } placedWarm && placedWarm.PortalGuid == packet.PortalGuid)
        {
            portal.NextPrepareAt = now + 0.5;
            Console.WriteLine($"[real-portals] descriptor Denied for placed 0x{packet.PortalGuid:X}; keeping the warm, retrying");
            return;
        }
        if (packet.Result != PortalDescriptorResult.Ok)
        {
            portal.NextPrepareAt = now +
                (packet.Result == PortalDescriptorResult.Unsupported ? 60.0 : RealPortalRetrySeconds);
            // A renewal denial is fresh server evidence that this session can no
            // longer prepare the object. Seal immediately; the stock click path
            // remains available and authoritative.
            if (_realPortalCastPrewarm?.PortalGuid == packet.PortalGuid)
                CancelRealPortalCastPrewarm(
                    $"spawned portal descriptor returned {packet.Result}", retireScene: true);
            InvalidateRealPortal(portal, retireScene: true);
            Console.WriteLine(
                $"[real-portals] descriptor {packet.Result} for 0x{packet.PortalGuid:X}");
            return;
        }

        if (packet.PortalEntry != portal.Entry ||
            !IsStockPortalUsePair(packet.PortalEntry, packet.TeleportSpellId))
        {
            portal.NextPrepareAt = now + 60.0;
            if (_realPortalCastPrewarm?.PortalGuid == packet.PortalGuid)
                CancelRealPortalCastPrewarm(
                    "spawned portal descriptor identity mismatched", retireScene: true);
            InvalidateRealPortal(portal, retireScene: true);
            Console.WriteLine(
                $"[real-portals] rejected mismatched descriptor for 0x{packet.PortalGuid:X}");
            return;
        }

        var sceneDescriptor = new ScenePortalDescriptor
        {
            Version = packet.Version,
            Result = (byte)packet.Result,
            Flags = (ushort)packet.Flags,
            RequestId = packet.RequestId,
            PortalGuid = packet.PortalGuid,
            SpawnGeneration = packet.SpawnGeneration,
            DescriptorRevision = packet.DescriptorRevision,
            Ticket = packet.Ticket,
            PortalEntry = packet.PortalEntry,
            TeleportSpellId = packet.TeleportSpellId,
            RemainingLifetimeMs = packet.RemainingLifetimeMs,
            SourceCenter = packet.SourceCenter,
            SourceYaw = packet.SourceYaw,
            HalfWidth = packet.HalfWidth,
            HalfHeight = packet.HalfHeight,
            PlaneEpsilon = packet.PlaneEpsilon,
            PreviewMapId = packet.PreviewMapId,
            PreviewPosition = packet.PreviewPosition,
            PreviewOrientation = packet.PreviewOrientation,
        };
        if (!sceneDescriptor.IsValid)
        {
            portal.NextPrepareAt = now + RealPortalRetrySeconds;
            if (_realPortalCastPrewarm?.PortalGuid == packet.PortalGuid)
                CancelRealPortalCastPrewarm(
                    "spawned portal descriptor was invalid", retireScene: true);
            InvalidateRealPortal(portal, retireScene: true);
            Console.WriteLine($"[real-portals] rejected invalid descriptor for 0x{packet.PortalGuid:X}");
            return;
        }

        bool sameIdentity = portal.Descriptor is { } previous &&
            previous.Identity == sceneDescriptor.Identity;
        bool keepLease = sameIdentity && portal.ReadyConfirmed && now < portal.ReadyLeaseExpiresAt;
        portal.Descriptor = sceneDescriptor;
        portal.DescriptorExpiresAt = packet.RemainingLifetimeMs == uint.MaxValue
            ? double.PositiveInfinity
            : now + Math.Max(0.0,
                packet.RemainingLifetimeMs / 1000.0 - RealPortalNetworkSafetySeconds());
        portal.LoadFailed = false;
        portal.ReadySent = false;
        portal.ReadyConfirmed = keepLease;
        if (!keepLease) portal.ReadyLeaseExpiresAt = 0;
        // READY may legitimately take longer than the core's 30-second
        // preparation lease on a cold/minimized client. Refresh well inside
        // that lease; a matching destination/key keeps the candidate warm.
        portal.NextPrepareAt = now + RealPortalPrepareRefreshSeconds;

        Console.WriteLine(
            $"[real-portals] descriptor 0x{packet.PortalGuid:X} -> map {packet.PreviewMapId}, " +
            $"generation {packet.SpawnGeneration}/{packet.DescriptorRevision}");
    }

    partial void ApplyRealPortalState(PortalStatePacket state)
    {
        PortalStatePacket packet = state;
        double now = RealPortalNow();
        if (!RealPortalPreviewEnabled ||
            !_realPortals.TryGetValue(packet.PortalGuid, out RealPortalVisual? portal) ||
            portal.Descriptor is not { } descriptor ||
            descriptor.Identity !=
                (packet.PortalGuid, packet.SpawnGeneration, packet.DescriptorRevision, packet.Ticket))
            return;

        switch (packet.State)
        {
            case PortalStateCode.Ready:
            {
                double leaseSeconds = Math.Max(0.0,
                    packet.LeaseOrRetryMs / 1000.0 - RealPortalNetworkSafetySeconds());
                // This completion belongs to the current descriptor cycle. Keep
                // ReadySent set so the visual-ready poll does not emit READY on
                // every frame; the next accepted renewal descriptor clears it.
                portal.ReadySent = true;
                portal.ReadyConfirmed = leaseSeconds > 0;
                portal.ReadyLeaseExpiresAt = now + leaseSeconds;
                portal.NextPrepareAt = now + Math.Max(0.25, leaseSeconds - RealPortalRenewLeadSeconds);
                Console.WriteLine(
                    $"[real-portals] READY 0x{packet.PortalGuid:X} for {packet.LeaseOrRetryMs} ms");
                break;
            }

            case PortalStateCode.Entering:
                // The stock transfer packets remain authoritative. Keep the
                // visual alive until NEW_WORLD/teleport adoption tears it down.
                break;

            case PortalStateCode.Revoked:
            case PortalStateCode.Blocked:
            case PortalStateCode.Expired:
            case PortalStateCode.Failed:
                if (_realPortalHandoffDescriptor is { } handoff &&
                    handoff.Identity == descriptor.Identity)
                    CancelRealPortalHandoff(
                        $"server changed prepared portal state to {packet.State}");
                portal.ReadySent = false;
                portal.ReadyConfirmed = false;
                portal.ReadyLeaseExpiresAt = 0;
                double retainedBackoff = Math.Max(0.0, portal.NextPrepareAt - now);
                double stateBackoff = Math.Max(
                    RealPortalRetrySeconds, packet.LeaseOrRetryMs / 1000.0);
                portal.NextPrepareAt = now + Math.Max(retainedBackoff, stateBackoff);
                InvalidateRealPortal(portal, retireScene: true);
                Console.WriteLine(
                    $"[real-portals] {packet.State} 0x{packet.PortalGuid:X}, reason {packet.Reason}");
                break;
        }
    }

    private void ExpireRealPortalState(double now)
    {
        float radiusSquared = RealPortalPreloadExitRadius * RealPortalPreloadExitRadius;
        foreach (RealPortalVisual portal in _realPortals.Values)
        {
            if (portal.Descriptor is not null && now >= portal.DescriptorExpiresAt)
            {
                InvalidateRealPortal(portal, retireScene: true);
                portal.NextPrepareAt = now + RealPortalRetrySeconds;
            }

            if (portal.ReadyConfirmed && now >= portal.ReadyLeaseExpiresAt)
            {
                portal.ReadyConfirmed = false;
                portal.ReadyLeaseExpiresAt = 0;
                // Keep the fully loaded scene warm, but reseal until the
                // per-session lease is renewed.
                portal.NextPrepareAt = Math.Min(portal.NextPrepareAt, now);
            }

            if (portal.DistanceSquared > radiusSquared &&
                (portal.Descriptor is not null || portal.PreparePending))
            {
                InvalidateRealPortal(portal, retireScene: true);
                portal.NextPrepareAt = now;
            }
        }
    }

    private void SendRealPortalReady(
        RealPortalVisual portal, PortalLoadResult result, double now)
    {
        if (portal.Descriptor is not { } descriptor || portal.ReadySent) return;
        if (_net?.SuiPortalReady(
                result, portal.Guid, descriptor.SpawnGeneration,
                descriptor.DescriptorRevision, descriptor.Ticket) != true)
            return;

        portal.ReadySent = true;
        portal.ReadyReplyDeadline = now + RealPortalReplyTimeoutSeconds;
        // Suppress a lease-refresh PREPARE until this READY cycle either gets
        // its state reply or times out. The core has one per-session record and
        // PREPARE would otherwise rebase that record before READY is applied.
        portal.ReplyDeadline = portal.ReadyReplyDeadline;
        if (result == PortalLoadResult.Failed)
            portal.NextPrepareAt = now + RealPortalLoadFailureRetrySeconds;
    }

    private void ReportRealPortalLoadFailure(
        RealPortalVisual portal, string failure, double now)
    {
        if (!portal.ReadySent) SendRealPortalReady(portal, PortalLoadResult.Failed, now);
        portal.LoadFailed = true;
        portal.ReadyConfirmed = false;
        portal.LiveBlend = 0f;
        portal.NextPrepareAt = now + RealPortalLoadFailureRetrySeconds;
        Console.WriteLine($"[real-portals] load failed for 0x{portal.Guid:X}: {failure}");
    }

    private void InvalidateOtherRealPortalCorrelations(ulong keepGuid)
    {
        foreach (RealPortalVisual portal in _realPortals.Values)
        {
            if (portal.Guid == keepGuid) continue;
            InvalidateRealPortal(portal, retireScene: true);
        }
    }

    private void InvalidateRealPortal(RealPortalVisual portal, bool retireScene)
    {
        portal.PreparePending = false;
        portal.RequestId = 0;
        portal.Descriptor = null;
        portal.DescriptorExpiresAt = double.PositiveInfinity;
        portal.ReadySent = false;
        portal.ReadyConfirmed = false;
        portal.ReadyLeaseExpiresAt = 0;
        portal.LoadFailed = false;
        portal.LiveBlend = 0f;
        if (retireScene && portal.Guid == _realPortalSceneGuid) RetireRealPortalScene();
    }

    private void RetireRealPortalScene()
    {
        if (_realPortalScene is { Retiring: false, RetirementComplete: false })
            _realPortalScene.Retire();
        _realPortalSceneGuid = 0;
        _realPortalSceneDescriptor = null;
        _realPortalAtmosphere = null;
        _realPortalSceneStartedAt = 0;
        // The context retains the candidate renderer's placed WMO model. Drop
        // it as soon as that candidate retires; decoded minimap textures remain
        // process-owned by GameplayArt and are intentionally reusable.
        _realPortalMinimapWarmIdentity = null;
        _realPortalMinimapWarmContext = null;
    }

    private void ResetRealPortals(bool resetCapability = true)
    {
        CancelRealPortalCastPrewarm("world reset", retireScene: false);
        _particles?.ClearMagePortalApertures();
        _realPortals.Clear();
        _realPortalRemoveScratch.Clear();
        RetireRealPortalScene();
        if (resetCapability) ResetRealPortalCapability();
    }

    private void DisposeRealPortals()
    {
        _realPortalCastPrewarm = null;
        _realPortalPrewarmCatalog = [];
        _particles?.ClearMagePortalApertures();
        _realPortals.Clear();
        _realPortalSceneGuid = 0;
        _realPortalSceneDescriptor = null;
        _realPortalAtmosphere = null;
        _realPortalSceneStartedAt = 0;
        _realPortalMinimapWarmIdentity = null;
        _realPortalMinimapWarmContext = null;
        _realPortalScene?.Dispose();
        _realPortalScene = null;
        _realPortalHandoffSnapshot?.Dispose();
        _realPortalHandoffSnapshot = null;
        _realPortalHandoffDescriptor = null;
        _realPortalHandoffPhase = RealPortalHandoffPhase.None;
        _realPortalHandoffArmedAt = 0;
        ResetRealPortalCapability();
    }

    private WorldAtmosphere NewRealPortalAtmosphere() => new()
    {
        DynamicLighting = _atmosphere.DynamicLighting,
        FogEnabled = _atmosphere.FogEnabled,
        CullAtFogEnd = _atmosphere.CullAtFogEnd,
        TimeOfDayHours = _atmosphere.TimeOfDayHours,
        FogStart = _atmosphere.FogStart,
        FogEnd = _atmosphere.FogEnd,
        SunStrength = _atmosphere.SunStrength,
        AmbientStrength = _atmosphere.AmbientStrength,
        UseAuthoredData = _atmosphere.UseAuthoredData,
        Mode = _atmosphere.Mode,
        ParityDaylightIntensity = _atmosphere.ParityDaylightIntensity,
    };

    private void ConfigureRealPortalAtmosphere(
        WorldAtmosphere atmosphere, in ScenePortalDescriptor descriptor)
    {
        atmosphere.DynamicLighting = _atmosphere.DynamicLighting;
        atmosphere.FogEnabled = _atmosphere.FogEnabled;
        atmosphere.CullAtFogEnd = _atmosphere.CullAtFogEnd;
        atmosphere.TimeOfDayHours = _atmosphere.TimeOfDayHours;
        atmosphere.FogStart = _atmosphere.FogStart;
        atmosphere.FogEnd = _atmosphere.FogEnd;
        atmosphere.SunStrength = _atmosphere.SunStrength;
        atmosphere.AmbientStrength = _atmosphere.AmbientStrength;
        atmosphere.UseAuthoredData = _atmosphere.UseAuthoredData;
        atmosphere.Mode = _atmosphere.Mode;
        atmosphere.ParityDaylightIntensity = _atmosphere.ParityDaylightIntensity;

        var sample = _exteriorLight.Resolve(
            descriptor.PreviewMapId, descriptor.PreviewPosition, atmosphere.TimeOfDayHours);
        if (sample is { HasData: true })
        {
            atmosphere.SetAuthored(
                sample.Ambient, sample.Diffuse, sample.FogColor,
                sample.SkyTop, sample.SkyMiddle, sample.SkyBand1,
                sample.SkyBand2, sample.SkySmog,
                sample.FogStart, sample.FogEnd);
            var dominant = sample.Contributors.Count > 0
                ? _exteriorLight.Params(sample.Contributors[^1].ParamsId)
                : null;
            atmosphere.SetAuthoredWater(
                sample.Colors[LightIntBandTable.OceanCloseBand],
                sample.Colors[LightIntBandTable.OceanFarBand],
                sample.Colors[LightIntBandTable.RiverCloseBand],
                sample.Colors[LightIntBandTable.RiverFarBand],
                dominant?.OceanShallowAlpha ?? 0f, dominant?.OceanDeepAlpha ?? 0f,
                dominant?.WaterShallowAlpha ?? 0f, dominant?.WaterDeepAlpha ?? 0f);
        }
        atmosphere.Evaluate();
    }

    private (int Width, int Height) RealPortalTargetSize()
    {
        Vector2 framebuffer = _window.FramebufferSize;
        int sourceWidth = Math.Max(1, (int)framebuffer.X);
        int sourceHeight = Math.Max(1, (int)framebuffer.Y);
        float scale = MathF.Min(1f,
            MathF.Min(1280f / sourceWidth, 720f / sourceHeight));
        return (Math.Max(64, (int)MathF.Round(sourceWidth * scale)),
                Math.Max(64, (int)MathF.Round(sourceHeight * scale)));
    }

    private void SyncRealPortalTargetSize()
    {
        if (_realPortalScene is null) return;
        (int width, int height) = RealPortalTargetSize();
        if (_realPortalScene.TargetSize.X != width || _realPortalScene.TargetSize.Y != height)
            _realPortalScene.ResizeTarget(width, height);
    }

    private static bool SamePreparedDestination(
        in ScenePortalDescriptor left, in ScenePortalDescriptor right) =>
        left.Identity == right.Identity && SameDestinationGeometry(left, right);

    private static bool SameDestinationGeometry(
        in ScenePortalDescriptor left, in ScenePortalDescriptor right) =>
        Vector3.DistanceSquared(left.SourceCenter, right.SourceCenter) <= 0.0001f &&
        MathF.Abs(left.SourceYaw - right.SourceYaw) <= 0.0001f &&
        MathF.Abs(left.HalfWidth - right.HalfWidth) <= 0.0001f &&
        MathF.Abs(left.HalfHeight - right.HalfHeight) <= 0.0001f &&
        MathF.Abs(left.PlaneEpsilon - right.PlaneEpsilon) <= 0.0001f &&
        left.PreviewMapId == right.PreviewMapId &&
        Vector3.DistanceSquared(left.PreviewPosition, right.PreviewPosition) <= 0.0001f &&
        MathF.Abs(left.PreviewOrientation - right.PreviewOrientation) <= 0.0001f &&
        left.PortalEntry == right.PortalEntry &&
        left.TeleportSpellId == right.TeleportSpellId;

    private bool IsPredictedMagePortal(WorldEntity entity) =>
        // These six template entries are the stock Mage teleport portals. Do not
        // make their immediate presentation depend on GAMEOBJECT_TYPE_ID being
        // present in the sparse create snapshot; the asynchronously fetched
        // template still rejects any contradictory type/spell before protocol IO.
        // A pack portal has no fixed entry, so it waits for its template.
        entity.IsGameObject && IsRealPortalEntry(entity.Entry);

    private bool IsStockPortalTemplate(uint entry, GameObjectTemplate template) =>
        template.Type == 22 && template.Data.Length > 0 &&
        IsStockPortalUsePair(entry, unchecked((uint)template.Data[0]));

    private static bool IsStockPortalEntry(uint entry) => entry is
        176296 or 176497 or 176498 or 176499 or 176500 or 176501;

    /// <summary>World Content Pack templates (MangosSuperUI WorldPackContent.TemplateBase).</summary>
    private const uint PackPortalTemplateBase = 7_000_000;

    /// <summary>A stock Mage portal, or a PUBLIC pack portal (shared_docs/WOW_KARTING.md §2).</summary>
    private bool IsRealPortalEntry(uint entry) => IsStockPortalEntry(entry) || IsPackPortalEntry(entry);

    /// <summary>
    /// A pack portal is a placed type-22 spellcaster (entry 7,000,000+) whose spell is a pack teleport
    /// spell [38,000, 40,000). The core accepts the same shape (SuiPortal IsRealPortalPair); its
    /// descriptor stays the authority for the destination.
    /// </summary>
    private bool IsPackPortalEntry(uint entry) =>
        entry >= PackPortalTemplateBase && _gameObjectTemplates.TryGetValue(entry, out GameObjectTemplate? t) &&
        t.Type == 22 && t.Data.Length > 0 && IsPackPortalSpell(unchecked((uint)t.Data[0]));

    private static bool IsPackPortalSpell(uint spellId) =>
        spellId is >= SpellCatalog.PackSpellIdBase and < SpellCatalog.PackSpellIdCeiling;

    // gameobject_template.data0 contains the spell cast when the portal GO is
    // used. These are not the Mage's 356x self-teleport spells.
    private bool IsStockPortalUsePair(uint entry, uint spellId) => entry switch
    {
        >= PackPortalTemplateBase => IsPackPortalEntry(entry) &&
            unchecked((uint)_gameObjectTemplates[entry].Data[0]) == spellId,
        176296 => spellId == 17334, // Stormwind
        176497 => spellId == 17607, // Ironforge
        176498 => spellId == 17608, // Darnassus
        176499 => spellId == 17609, // Orgrimmar
        176500 => spellId == 17610, // Thunder Bluff
        176501 => spellId == 17611, // Undercity
        _ => false,
    };

    private uint NextRealPortalRequestId()
    {
        _realPortalNextRequestId++;
        if (_realPortalNextRequestId == 0) _realPortalNextRequestId++;
        return _realPortalNextRequestId;
    }

    private double RealPortalNetworkSafetySeconds() =>
        ((_net?.LatencyMs ?? 0) / 1000.0) + 0.25;

    private static double RealPortalNow() => MovementInfo.ClientUptimeMs() / 1000.0;
}
