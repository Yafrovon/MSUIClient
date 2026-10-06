using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const float GameObjectInteractDistance = 6f;
    private const float MagePortalClickInteractDistance = 10f;
    // Core GameObjectInfo::GetInteractionDistance permits the fishing cast's
    // distant bobber. Cursor/channel ownership and server ownership still apply.
    private float GameObjectUseDistance(WorldEntity go) => go.GameObjectType == 17
        ? 100f : IsRealPortalEntry(go.Entry)
            ? MagePortalClickInteractDistance : GameObjectInteractDistance;
    private ulong _gameObjectGuid;
    private uint _gameObjectAnimation;
    private readonly List<(uint Id, string Text, uint Next)> _gameObjectPages = [];
    private readonly HashSet<uint> _pageTextPending = [];
    private sealed record GameObjectTemplate(uint Entry, uint Type, uint DisplayId, string Name,
        string Icon, int[] Data)
    {
        public uint PageHead => Type switch
        {
            9 => unchecked((uint)Math.Max(0, Data[0])),
            10 => unchecked((uint)Math.Max(0, Data[7])),
            _ => 0,
        };
        public uint PageMaterial => Type switch
        {
            9 => unchecked((uint)Math.Max(0, Data[2])),
            10 => unchecked((uint)Math.Max(0, Data[9])),
            _ => 0,
        };
        public uint LockId => Type switch
        {
            0 or 1 => unchecked((uint)Math.Max(0, Data[1])),
            2 or 3 or 6 or 10 or 12 or 13 or 24 or 26 => unchecked((uint)Math.Max(0, Data[0])),
            25 => unchecked((uint)Math.Max(0, Data[4])),
            _ => 0
        };
    }
    private readonly Dictionary<uint, GameObjectTemplate> _gameObjectTemplates = [];
    private readonly HashSet<uint> _gameObjectTemplatePending = [];
    private LockCatalog? _locks;
    private SpellFocusCatalog? _spellFoci;

    private readonly record struct GameObjectInteractionFacts(
        int Type, uint Flags, uint DynamicFlags, int? HighlightColumn,
        bool? HostileTowardPlayer, bool FishingChannelOwned, bool MeetingStoneQueued);

    private GameObjectInteractionFacts ResolveGameObjectInteractionFacts(WorldEntity go)
    {
        RequireGameObjectTemplate(go);
        _gameObjectTemplates.TryGetValue(go.Entry, out GameObjectTemplate? template);
        int type = unchecked((int)go.GameObjectType);
        int? highlight = type switch
        {
            5 when template is not null && template.Data.Length > 1 => template.Data[1],
            29 when template is not null && template.Data.Length > 19 => template.Data[19],
            _ => null,
        };
        bool channelOwned = _entities.TryGet(ControlledGuid, out WorldEntity player) &&
            player.Fields.ChannelObject == go.Guid;
        // Current Benilla carries no meeting-stone queue; the reference global is therefore zero.
        bool meetingQueued = type == 23 && template is not null && template.Data.Length > 2 &&
            template.Data[2] == 0;
        return new(type, go.Fields.GameObjectFlags, go.Fields.GameObjectDynamicFlags, highlight,
            GameObjectHostileTowardPlayer(go), channelOwned, meetingQueued);
    }

    private bool? GameObjectHostileTowardPlayer(WorldEntity go)
    {
        uint goFaction = go.Fields.GameObjectFaction;
        if (goFaction == 0) return false;
        if (_factions is null || !_entities.TryGet(ControlledGuid, out WorldEntity player) ||
            !_factions.TryGet(goFaction, out FactionTemplateRow goTemplate) ||
            !_factions.TryGet(player.Fields.FactionTemplate, out FactionTemplateRow playerTemplate))
            return null;
        if (_forcedReactions.TryGet(ReactionPlayerOwner(player), goTemplate.Faction, out FactionReaction forced))
            return forced == FactionReaction.Hostile;
        if (TryReputationReaction(go, goTemplate, player, playerTemplate, out FactionReaction standing))
            return standing == FactionReaction.Hostile;
        return goTemplate.ReactionToward(playerTemplate) == FactionReaction.Hostile;
    }

    // Picking and using are separate reference decisions. Every rendered GO except the transport
    // family may own mouseover/name/highlight; interaction eligibility is applied on use.
    private static bool GameObjectMouseoverEligible(WorldEntity go) =>
        go.GameObjectType is not (11 or 14 or 15);

    private bool GameObjectHighlightable(WorldEntity go)
    {
        GameObjectInteractionFacts f = ResolveGameObjectInteractionFacts(go);
        return WorldCursorUiLaw.HighlightableGameObject(f.Type, f.Flags, f.DynamicFlags,
            f.HostileTowardPlayer, f.FishingChannelOwned, f.MeetingStoneQueued);
    }

    private static bool GameObjectBrightens(WorldEntity go) =>
        go.GameObjectType is not (11 or 14 or 15);

    private static string GameObjectKind(uint type) => type switch
    {
        0 => "door", 1 => "button/lever", 2 => "questgiver", 3 => "chest",
        5 => "generic", 6 => "trap", 8 => "spell-focus", 9 => "text",
        10 => "goober", 19 => "mailbox", 22 => "spellcaster", _ => $"type-{type}"
    };

    private void ResetGameObjects()
    {
        ResetGameObjectTransportState();
        CloseItemText(playSound: false);
        _gameObjectGuid = 0;
        _gameObjectAnimation = 0;
    }

    private void RequireGameObjectTemplate(WorldEntity entity)
    {
        if (!entity.IsGameObject || entity.Entry == 0 || _gameObjectTemplates.ContainsKey(entity.Entry) ||
            !_gameObjectTemplatePending.Add(entity.Entry)) return;
        _net?.GameObjectQuery(entity.Entry, entity.Guid);
        EmitInterface("gathering", "template-query", "SENT", entity.Guid, $"entry={entity.Entry}");
    }

    private void ApplyGameObjectQuery(byte[] body)
    {
        var r = new PacketReader(body);
        uint rawEntry = r.ReadU32();
        uint entry = rawEntry & 0x7fff_ffffu;
        _gameObjectTemplatePending.Remove(entry);
        if ((rawEntry & 0x8000_0000u) != 0)
        {
            EmitInterface("gathering", "template", "MISSING", 0, $"entry={entry}");
            return;
        }
        uint type = r.ReadU32(), display = r.ReadU32();
        string name = r.ReadCString();
        r.ReadCString(); r.ReadCString(); r.ReadCString();
        string icon = r.ReadCString();
        var data = new int[24];
        for (int i = 0; i < data.Length; i++) data[i] = r.ReadI32();
        var template = new GameObjectTemplate(entry, type, display, name, icon, data);
        _gameObjectTemplates[entry] = template;
        RefreshOpenGameObjectText(template);
        EnsureLockCatalog();
        uint lockType = _locks?.ResourceLockType(template.LockId) ?? 0;
        EmitInterface("gathering", "template", "DECODED", 0,
            $"entry={entry};name={SanitizeEvidence(name)};type={type};display={display};lock={template.LockId};lockType={lockType};resource={ResourceKind(lockType)}");
    }

    private void EnsureLockCatalog()
    {
        if (_locks is not null || _mpq is null) return;
        _locks = LockCatalog.Load(_mpq);
        EmitInterface("gathering", "lock-catalog", _locks is null ? "FAILED" : "LOADED", 0,
            $"rows={_locks?.Count ?? 0}");
    }

    private void EnsureSpellFocusCatalog()
    {
        if (_spellFoci is not null || _mpq is null) return;
        _spellFoci = SpellFocusCatalog.Load(_mpq);
    }

    private string SpellFocusName(uint focusId)
    {
        EnsureSpellFocusCatalog();
        return _spellFoci?.Name(focusId) ?? $"Spell Focus {focusId}";
    }

    private bool HasNearbySpellFocus(uint focusId)
    {
        if (focusId == 0) return true;
        if (!TryGetControlledBodyPose(out WorldBodyPose controlledBody)) return false;
        foreach (WorldEntity go in _entities.Entities.Values.Where(x => x.IsGameObject))
        {
            RequireGameObjectTemplate(go);
            if (!_gameObjectTemplates.TryGetValue(go.Entry, out GameObjectTemplate? template) ||
                template.Type != 8 || template.Data.Length < 2 ||
                unchecked((uint)Math.Max(0, template.Data[0])) != focusId) continue;
            float limit = Math.Clamp(template.Data[1], 0, 10);
            if (limit > 0 && Vector3.Distance(go.Position, controlledBody.Position) <= limit) return true;
        }
        return false;
    }

    private static string ResourceKind(uint lockType) => lockType switch
    {
        2 => "herb", 3 => "mineral", _ => "none"
    };

    private bool UseGameObject(ulong guid)
    {
        WorldEntity? go = null;
        float distance = float.PositiveInfinity;
        float interactDistance = GameObjectInteractDistance;
        string outcome;
        if (_net is not { IsInWorld: true }) outcome = "REFUSED_NOT_IN_WORLD";
        else if (!_entities.TryGet(guid, out go) || !go.IsGameObject) outcome = "REFUSED_NOT_GAMEOBJECT";
        else
        {
            // Mail and text panels open for the body standing at them — the driven bot while
            // possessing (the server threads the mail handlers through GetSuiActor; owner
            // 2026-09-03: the mailbox stayed grey for a possessed bot), else the session
            // character. Every other game-object use is authored by the controlled body; the
            // detached camera only picks.
            bool sessionScoped = go.GameObjectType is 19 or 9;
            WorldBodyPose actorBody;
            bool bodyAvailable = sessionScoped
                ? TryGetInteractionBodyPose(out actorBody)
                : TryGetControlledBodyPose(out actorBody);
            if (!bodyAvailable) outcome = "REFUSED_NO_BODY";
            else
            {
                distance = Vector3.Distance(actorBody.Position, go.Position);
                interactDistance = GameObjectUseDistance(go);
                if (!sessionScoped && !CanAuthorControlledGameplay)
                    outcome = "REFUSED_OBSERVER";
                else if (distance > interactDistance)
                    outcome = "REFUSED_RANGE";
                else if (go.GameObjectType == 19)
                {
                    // Mailbox use is a local panel open. The mail window's CheckInbox equivalent owns the
                    // first CMSG_GET_MAIL_LIST; build 5875 sends no CMSG_GAMEOBJ_USE for a mailbox.
                    outcome = RequestMail(guid) ? "OPENED_MAIL" : "REFUSED_MAIL";
                }
                else if (go.GameObjectType == 9)
                {
                    // Current Benilla opens plaques/books locally from GAMEOBJECT_TYPE_TEXT template
                    // data. There is no CMSG_GAMEOBJ_USE (and no CMSG_READ_ITEM) for this route.
                    OpenGameObjectText(go);
                    outcome = "OPENED_ITEM_TEXT";
                }
                else
                {
                    GameObjectLockOutcome lockOutcome = ResolveGameObjectLock(go);
                    if (lockOutcome.Kind == GameObjectLockOutcomeKind.OpenBySpell)
                    {
                        uint opener = lockOutcome.Id;
                        outcome = _net.CastSpellOnGameObject(opener, guid) ? "SENT_OPEN_LOCK_SPELL" : "SEND_FAILED";
                        if (outcome.StartsWith("SENT", StringComparison.Ordinal)) _pendingCastSpell = opener;
                    }
                    else if (lockOutcome.Kind == GameObjectLockOutcomeKind.Unmet)
                    {
                        ShowUiError("Locked.");
                        outcome = "REFUSED_LOCK";
                    }
                    else
                    {
                        bool sent = _net.GameObjectUse(guid);
                        outcome = sent ? "SENT" : "SEND_FAILED";
                        // Arm only after a READY portal's ordinary authoritative use
                        // was successfully queued. Proximity, clicks on other objects,
                        // and failed sends retain the normal loading-screen path.
                        if (sent && IsRealPortalEntry(go.Entry))
                            ArmRealPortalHandoffAfterSuccessfulUse(guid);
                    }
                }
            }
        }
        if (outcome.StartsWith("SENT", StringComparison.Ordinal))
        { _gameObjectGuid = guid; _gameObjectAnimation = 0; }
        uint lockSpell = go is null ? 0 : FindKnownOpenLockSpell(go);
        string body = go?.GameObjectType == 19 ? "LOCAL_MAIL_OPEN" :
            go?.GameObjectType == 9 ? "LOCAL_ITEM_TEXT_OPEN" : lockSpell != 0
            ? Convert.ToHexString(WorldSession.BuildCastSpellOnGameObjectBody(lockSpell, guid))
            : Convert.ToHexString(WorldSession.BuildGameObjectUseBody(guid));
        EmitInterface("gameobject", "use", outcome, guid,
            $"entry={go?.Entry ?? 0};type={go?.GameObjectType ?? 0};kind={GameObjectKind(go?.GameObjectType ?? uint.MaxValue)};distance={distance:R};limit={interactDistance:R};openSpell={lockSpell};body={body}");
        return outcome.StartsWith("SENT", StringComparison.Ordinal) ||
            outcome is "OPENED_MAIL" or "OPENED_ITEM_TEXT";
    }

    private GameObjectLockOutcome ResolveGameObjectLock(WorldEntity go)
    {
        if (!_gameObjectTemplates.TryGetValue(go.Entry, out GameObjectTemplate? template) ||
            template.LockId == 0 || _spellCatalog is null)
            return new(GameObjectLockOutcomeKind.Unlocked, 0);
        EnsureLockCatalog();
        if (_locks is null) return new(GameObjectLockOutcomeKind.Unlocked, 0);
        _entities.TryGet(ControlledGuid, out WorldEntity? player);
        SpellInfo? Spell(uint id) => _spellCatalog.TryGet(id, out SpellInfo info) ? info : null;
        uint Skill(uint spellId)
        {
            uint line = _skillLines?.SpellLine(spellId) ?? 0;
            return player is null || line == 0 ? 0 : player.Fields.PlayerSkillValueWithBonuses(line);
        }
        bool Holds(uint entry)
        {
            if (player is null) return false;
            for (int slot = 0; slot < InventoryUiLaw.KeyringAddressableSlots; slot++)
            {
                ulong keyGuid = player.Fields.PlayerKeyringSlot(slot);
                if (keyGuid != 0 && _entities.TryGet(keyGuid, out WorldEntity key) &&
                    key.Entry == entry) return true;
            }
            foreach (ulong itemGuid in EnumeratePlayerInventoryGuids(player))
                if (itemGuid != 0 && _entities.TryGet(itemGuid, out WorldEntity item) &&
                    item.Entry == entry) return true;
            return false;
        }
        return GameObjectLockLaw.Resolve(_locks.Slots(template.LockId), _actions.KnownSpells,
            Spell, Skill, Holds, go.Fields.GameObjectState,
            (go.Fields.GameObjectFlags & WorldCursorUiLaw.GameObjectLocked) != 0,
            go.Fields.GameObjectLevel);
    }

    private uint FindKnownOpenLockSpell(WorldEntity go)
    {
        GameObjectLockOutcome outcome = ResolveGameObjectLock(go);
        return outcome.Kind == GameObjectLockOutcomeKind.OpenBySpell ? outcome.Id : 0;
    }

    private void SnapshotGameObjects()
    {
        var rows = _entities.Entities.Values.Where(x => x.IsGameObject)
            .OrderBy(x => _controller is null ? 0 : Vector3.Distance(x.Position, _controller.Position)).Take(128).ToArray();
        foreach (WorldEntity go in rows)
        {
            float distance = _controller is null ? float.PositiveInfinity : Vector3.Distance(go.Position, _controller.Position);
            EmitInterface("gameobject", "presence", "OBSERVED", go.Guid,
                $"entry={go.Entry};type={go.GameObjectType};kind={GameObjectKind(go.GameObjectType)};distance={distance:R};display={go.Fields.GameObjectDisplayId}");
        }
        uint[] required = [0, 1, 3, 8];
        foreach (uint type in required)
            EmitInterface("gameobject", "class-presence", rows.Any(x => x.GameObjectType == type) ? "PRESENT" : "ABSENT", 0,
                $"type={type};kind={GameObjectKind(type)};observed={rows.Count(x => x.GameObjectType == type)}");
    }

    private void SnapshotGathering()
    {
        EnsureLockCatalog();
        uint mask = 0;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            mask = player.Fields.PlayerTrackResources;
        var rows = _entities.Entities.Values.Where(x => x.IsGameObject).OrderBy(x =>
            _controller is null ? 0 : Vector3.Distance(x.Position, _controller.Position)).ToArray();
        foreach (WorldEntity go in rows) RequireGameObjectTemplate(go);
        foreach (WorldEntity bobber in rows.Where(x => x.GameObjectType == 17))
            EmitInterface("gathering", "fishing-bobber", "OBSERVED", bobber.Guid,
                $"entry={bobber.Entry};distance={(_controller is null ? float.PositiveInfinity : Vector3.Distance(bobber.Position, _controller.Position)):R};" +
                $"position={bobber.Position.X:R}|{bobber.Position.Y:R}|{bobber.Position.Z:R}");
        foreach (WorldEntity go in rows)
        {
            if (!_gameObjectTemplates.TryGetValue(go.Entry, out GameObjectTemplate? template)) continue;
            uint lockType = _locks?.ResourceLockType(template.LockId) ?? 0;
            if (lockType is not (2 or 3)) continue;
            float distance = _controller is null ? float.PositiveInfinity :
                Vector3.Distance(go.Position, _controller.Position);
            EmitInterface("gathering", "node", "OBSERVED", go.Guid,
                $"entry={go.Entry};name={SanitizeEvidence(template.Name)};resource={ResourceKind(lockType)};" +
                $"lock={template.LockId};lockType={lockType};distance={distance:R};tracked={_locks?.MatchesResourceMask(template.LockId, mask) == true};" +
                $"position={go.Position.X:R}|{go.Position.Y:R}|{go.Position.Z:R}");
        }
        EmitInterface("gathering", "snapshot", "COMPLETE", _net?.PlayerGuid ?? 0,
            $"mask=0x{mask:X8};objects={rows.Length};templates={_gameObjectTemplates.Count};pending={_gameObjectTemplatePending.Count};" +
            $"herbs={rows.Count(x => _gameObjectTemplates.TryGetValue(x.Entry, out GameObjectTemplate? t) && _locks?.ResourceLockType(t.LockId) == 2)};" +
            $"minerals={rows.Count(x => _gameObjectTemplates.TryGetValue(x.Entry, out GameObjectTemplate? t) && _locks?.ResourceLockType(t.LockId) == 3)}");
    }

    private void ApplyGameObjectCustomAnim(byte[] body)
    {
        if (body.Length != 12)
            throw new InvalidDataException($"custom animation bytes={body.Length}");
        var r = new PacketReader(body); ulong guid = r.ReadU64(); uint animation = r.ReadU32();
        _gameObjectGuid = guid; _gameObjectAnimation = animation;
        int? animationId = GameObjectAnimationLaw.CustomAnimationId(animation);
        bool familyA = _entities.TryGet(guid, out WorldEntity go) && go.IsGameObject &&
            GameObjectAnimationLaw.Animates(go.GameObjectType);
        bool armed = familyA && animationId is { } id &&
            _doodads?.TryPlayDynamicAnimation(guid, id, out _) == true;
        EmitInterface("gameobject", "animation", armed ? "ARMED" : "DROPPED", guid,
            $"wireAnimation={animation};animationData={animationId?.ToString() ?? "REJECTED"};bytes={body.Length}");
    }

    private void ApplyGameObjectDespawnAnim(byte[] body)
    {
        if (body.Length != 8)
            throw new InvalidDataException($"despawn animation bytes={body.Length}");
        ulong guid = new PacketReader(body).ReadU64();
        _gameObjectDespawnAnimations.Add(guid);
        EmitInterface("gameobject", "despawn-animation", "ANNOUNCED", guid,
            $"animationData={GameObjectAnimationLaw.DespawnAnimationId};bytes={body.Length}");
    }

    private void ApplyDestroyObject(byte[] body)
    {
        if (body.Length != 8)
            throw new InvalidDataException($"destroy object bytes={body.Length}");
        ulong guid = new PacketReader(body).ReadU64();
        bool familyA = _entities.TryGet(guid, out WorldEntity go) && go.IsGameObject &&
            GameObjectAnimationLaw.Animates(go.GameObjectType);
        bool announced = _gameObjectDespawnAnimations.Remove(guid);
        bool placementPresent = _gameObjectPlacements.ContainsKey(guid) &&
            _doodads?.HasDynamic(guid) == true;
        float duration = 0;
        bool clipOwned = familyA && announced && placementPresent &&
            _doodads?.TryPlayDynamicAnimation(guid,
            GameObjectAnimationLaw.DespawnAnimationId, out duration) == true;
        bool retained = GameObjectAnimationLaw.ShouldRetainDestroy(
            announced, placementPresent, clipOwned);
        if (retained)
            _gameObjectRetainedDestroys[guid] = GameObjectAnimationLaw.RetainedUntil(
                _doodads?.NowSeconds ?? 0, duration);
        else if (placementPresent)
            RemoveGameObjectPlacement(guid);
        _entities.Remove(guid);
        _spellChainBeams?.ClearUnit(guid);
        EmitInterface("gameobject", "destroy", retained ? "RETAINED_ANIMATION" : "REMOVED",
            guid, $"announced={announced};placement={placementPresent};clip={clipOwned};duration={duration:R}");
    }

    private void ApplyPageText(byte[] body)
    {
        var r = new PacketReader(body); uint id = r.ReadU32(); string text = r.ReadCString(); uint next = r.ReadU32();
        if (r.Remaining != 0) throw new InvalidDataException($"page trailing={r.Remaining}");
        _pageTextPending.Remove(id);
        _gameObjectPages.RemoveAll(x => x.Id == id); _gameObjectPages.Add((id, text, next));
        EmitInterface("gameobject", "page", "DECODED", _gameObjectGuid,
            $"page={id};next={next};chars={text.Length};text={SanitizeEvidence(text)}");
    }

    private void SimulateGameObjectFlow()
    {
        ulong[] guids = [0xF110000003000001, 0xF110000000000002, 0xF110000001000003, 0xF110000008000004];
        uint[] types = [3, 0, 1, 8];
        for (int i = 0; i < types.Length; i++)
            EmitInterface("gameobject", "replay-use", "SERVER_EFFECT", guids[i],
                $"type={types[i]};kind={GameObjectKind(types[i])};body={Convert.ToHexString(WorldSession.BuildGameObjectUseBody(guids[i]))};effect={(types[i] == 3 ? "loot-open" : types[i] == 8 ? "focus-present" : "state-animation")}");
        var anim = new PacketWriter(); anim.WriteU64(guids[1]); anim.WriteU32(1); ApplyGameObjectCustomAnim(anim.ToArray());
        var page = new PacketWriter(); page.WriteU32(77); page.WriteCString("A weathered inscription."); page.WriteU32(0); ApplyPageText(page.ToArray());
        EmitInterface("gameobject", "profession-prerequisite", "PRESENT", 0,
            "herb=Silverleaf;vein=Copper Vein;professionItem=2-8:CLOSED-PASS");
    }

    private void DrawGameObjectFrame()
    {
        // Ordinary doors, chests and devices have world effects, not a diagnostic
        // dialog. Books and plaques retain their authored item-text panel.
        if (_itemTextRead is not null && _gameplayArt is not null) DrawItemTextFrame();
    }

}
