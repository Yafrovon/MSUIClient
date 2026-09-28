using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Encounters;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private CommanderRaidPlan _commanderRaidDraft = CommanderRaidPlan.Empty;
    private CommanderRaidPlan? _commanderRaidApplied;
    private CommanderRaidStatus? _commanderRaidStatus;
    private CommanderRaidPlan? _commanderRaidPendingPlan;
    private bool _commanderRaidAvailable, _commanderRaidLoaded, _commanderRaidAirMap;
    private uint _commanderRaidSequence, _commanderRaidPending;
    private CommanderRaidOperation _commanderRaidPendingOperation;
    private bool _commanderRaidMutationFailed;
    private double _commanderRaidSentAt, _commanderRaidPollAt, _commanderRaidStatusAt;
    private string _commanderRaidMessage = "Load an encounter preset, review assignments, then apply to your raid.";
    private int _commanderRaidPage;
    private IReadOnlyList<CommanderRaidMember> _commanderRaidRosterCache = [];
    private CommanderEncounterDefinition? _commanderRaidRosterDefinition;
    private double _commanderRaidRosterNext;
    private readonly HashSet<ulong> _commanderFactsSeen = [];
    private bool _commanderAutoAssignPending;
    private double _commanderAutoAssignAt;
    private IReadOnlyList<CommanderEncounterDefinition> _commanderEncounters = [CommanderEncounterCatalog.Default];
    private bool _commanderRaidPositionPending = true;
    private IReadOnlySet<uint>? _commanderWellFedSpells;
    private double _commanderPrepareSentAt, _commanderSupplySentAt;
    private const byte SuiOrderPrepare = 15, SuiOrderSupply = 16;
    private string CommanderRaidPath => Path.Combine(_config.RepoRoot, "commander-plans", _commanderRaidDraft.Encounter.Id + ".json");

    private void ResetCommanderRaid()
    {
        _commanderRaidAvailable = false;
        _commanderRaidApplied = null;
        _commanderRaidStatus = null;
        _commanderRaidPending = 0;
        _commanderRaidPendingPlan = null;
        _commanderAutoAssignPending = false;
        _commanderRaidMutationFailed = false;
        _commanderFactsSeen.Clear();
        _commanderRaidRosterNext = 0;
        _commanderRaidRosterCache = [];
        _commanderRaidRosterDefinition = null;
        _commanderRaidDraft = CommanderRaidPlan.Empty;
        _commanderRaidLoaded = false;
    }

    private IReadOnlyList<CommanderRaidMember> CommanderRaidRoster(bool fresh = false)
    {
        double now = NowSeconds();
        if (!fresh && now < _commanderRaidRosterNext && ReferenceEquals(_commanderRaidRosterDefinition, _commanderRaidDraft.Encounter))
            return _commanderRaidRosterCache;
        _commanderRaidRosterNext = now + .25;
        _commanderRaidRosterDefinition = _commanderRaidDraft.Encounter;
        var result = new List<CommanderRaidMember>();
        foreach (var member in OwnCharacterPartyRow().Concat(_partyMembers).DistinctBy(m => m.Guid))
        {
            _entities.TryGet(member.Guid, out WorldEntity entity);
            var spells = ActionsFor(member.Guid).KnownSpells.ToHashSet();
            uint classId = entity?.Fields.Bytes0.Class ?? (_playerTraits.TryGetValue(member.Guid, out var traits) ? traits.Class : 0u);
            _partyStats.TryGetValue(member.Guid, out var stats);
            var candidate = new CommanderRaidMember(member.Guid, member.Name, classId,
                member.Guid != LocalPlayerGuid && IsRtsGroupableBot(member.Guid),
                entity is not null ? entity.Fields.MaxHealth > 0 && !entity.Fields.IsDead && !entity.Fields.PlayerIsGhost : stats.Health.GetValueOrDefault() > 0,
                spells)
            {
                FactsReady = classId != 0 && spells.Count > 0 && (member.Guid == LocalPlayerGuid || _commanderFactsSeen.Contains(member.Guid)),
                MaxHealth = entity?.Fields.MaxHealth ?? stats.MaxHealth ?? 0,
                Level = entity?.Fields.Level ?? stats.Level ?? 0,
                Armor = (uint)Math.Max(0, entity?.Fields.Resistance(0) ?? 0),
            };
            result.Add(CommanderRaidCapabilityLaw.Resolve(candidate, CommanderRaidFacts(spells), _commanderRaidDraft.Encounter.ImmuneSchools));
        }
        _commanderRaidRosterCache = result;
        return result;
    }

    private IEnumerable<CommanderRaidSpellFact> CommanderRaidFacts(IEnumerable<uint> spells)
    {
        foreach (uint id in spells)
            if (_spellCatalog?.TryGet(id, out SpellInfo spell) == true)
                yield return new(id, spell.Name, spell.SpellLevel, spell.School, spell.Passive,
                    spell.EffectIds ?? [], spell.AuraIds ?? [], (spell.ImplicitTargetsA ?? []).Concat(spell.ImplicitTargetsB ?? []).ToArray(),
                    spell.EffectBasePoints ?? [], Math.Max(spell.RecoveryMs, spell.CategoryRecoveryMs), spell.CastTimeMs, spell.PowerType, spell.ManaCost, spell.AutoRepeat,
                    _spellCatalog.TryGetRange(spell.RangeIndex, out var range) ? range.Max : 0);
    }

    /// <summary>Pre-pull readiness from the member's visible auras; empty when the unit is not streamed in.</summary>
    private CommanderRaidReadiness CommanderReadiness(ulong guid)
    {
        if (_spellCatalog is null || !_entities.TryGet(guid, out WorldEntity entity) || entity is null) return CommanderRaidReadiness.None;
        _commanderWellFedSpells ??= CommanderRaidReadinessLaw.WellFedSpells(_spellCatalog.Spells.Select(CommanderAuraFact));
        return CommanderRaidReadinessLaw.Read(entity.Fields.Auras().Select(a => a.SpellId),
            id => _spellCatalog.TryGet(id, out SpellInfo spell) ? CommanderAuraFact(spell) : null, _commanderWellFedSpells);
    }

    private static CommanderRaidAuraFact CommanderAuraFact(SpellInfo spell) => new(spell.Id, spell.SpellFamily, spell.DurationMs,
        spell.EffectIds ?? [], spell.AuraIds ?? [], spell.ImplicitTargetsA ?? [], spell.EffectTriggerSpells ?? []);

    /// <summary>ORDER_PREPARE for every commandable member: buffs, elixirs, absorb potion, food and pets from their own bags.</summary>
    private void PrepareCommanderRaid(IReadOnlyList<CommanderRaidMember> roster)
    {
        var bots = roster.Where(m => m.Commandable && m.Alive).Select(m => m.Guid).ToArray();
        if (bots.Length == 0) { _commanderRaidMessage = "No commandable raid members to prepare."; return; }
        if (_net is not { IsInWorld: true }) { _commanderRaidMessage = "Not in the world."; return; }
        uint schools = 0;
        foreach (var policy in _commanderRaidDraft.Encounter.Mechanics.DamagePolicies) schools |= policy.Schools;
        if (!_net.SuiOrder(SuiOrderPrepare, bots, 0, schools, 0, 0)) { _commanderRaidMessage = "Could not send the preparation order."; return; }
        _commanderPrepareSentAt = NowSeconds();
        _commanderRaidMessage = $"Preparing {bots.Length} members: buffs, elixirs, food and pets from their own bags. The raid reports when done.";
        Console.WriteLine($"[commander-raid] prepare members={bots.Length} schools={schools}");
    }

    /// <summary>ORDER_SUPPLY per role bucket: the server's quartermaster policy tops each member's bags up for the role it will play.</summary>
    private void SupplyCommanderRaid(IReadOnlyList<CommanderRaidMember> roster)
    {
        if (_net is not { IsInWorld: true }) { _commanderRaidMessage = "Not in the world."; return; }
        var buckets = new Dictionary<CommanderRaidRole, List<ulong>>();
        foreach (var member in roster.Where(m => m.Commandable && m.Alive))
        {
            // The plan's assignment decides the loadout role; without one, the member's learned abilities do.
            var role = _commanderRaidDraft.Assignments.FirstOrDefault(a => a.Guid == member.Guid)?.Role
                ?? (member.CanTank ? CommanderRaidRole.MainTank
                    : member.PreferredHeal != 0 && member.Spells.Contains(member.PreferredHeal) ? CommanderRaidRole.Healer
                    : member.PreferredDamage != 0 ? CommanderRaidRole.Ranged : CommanderRaidRole.Melee);
            (buckets.TryGetValue(role, out var list) ? list : buckets[role] = []).Add(member.Guid);
        }
        if (buckets.Count == 0) { _commanderRaidMessage = "No commandable raid members to supply."; return; }
        foreach (var (role, guids) in buckets)
            if (!_net.SuiOrder(SuiOrderSupply, guids, 0, (int)role, 0, 0)) { _commanderRaidMessage = "Could not send the supply order."; return; }
        _commanderSupplySentAt = NowSeconds();
        int total = buckets.Sum(b => b.Value.Count);
        _commanderRaidMessage = $"Supplying {total} members by role: " + string.Join(", ", buckets.OrderBy(b => b.Key)
            .Select(b => $"{b.Value.Count} {CommanderRaidPlanLaw.RoleName(b.Key).ToLowerInvariant()}")) + ". The raid reports what was granted.";
        Console.WriteLine($"[commander-raid] supply members={total} buckets={buckets.Count}");
    }

    private void AutoAssignCommanderRaid(IReadOnlyList<CommanderRaidMember> roster, ulong mainGuid)
    {
        if (_commanderRaidDraft.MainRole == CommanderRaidRole.Unassigned)
        { _commanderRaidMessage = "Choose your main character's role first."; return; }
        _commanderRaidDraft = CommanderRaidPlanLaw.AutoAssign(roster, _commanderRaidDraft.Encounter, mainGuid, _commanderRaidDraft.MainRole);
        _commanderRaidApplied = null;
        _partyTacticsGuid = mainGuid;
        _commanderRaidPage = 0;
        var errors = CommanderRaidPlanLaw.Validate(_commanderRaidDraft, roster);
        _commanderRaidMessage = errors.Count == 0 ? "Assigned. Review primary patients, teams and duties, then apply." : errors[0];
    }

    /// <summary>
    /// Load the authored fight that owns the current target (boss OR any member of a compiled
    /// trash pack), falling back to the boss catalogue's generated basic plan. Returns false when
    /// nothing in the world is selected that any definition or catalogue fact covers.
    /// </summary>
    private bool LoadCommanderEncounterForTarget(ulong mainGuid, int groupSize, bool announce)
    {
        ulong guid = _selectionGuid != 0 ? _selectionGuid : NearestCommanderEncounterGuid();
        if (!_entities.TryGet(guid, out var target))
        {
            if (announce) _commanderRaidMessage = "Select the pack or boss you want the raid to fight.";
            return false;
        }
        uint entry = target.Fields.Entry ?? 0;
        uint map = (uint)_config.Start.Map;
        Vector3 targetPosition = UnitWorldPosition(target);
        CommanderEncounterDefinition? authored =
            CommanderEncounterSelectionLaw.FindForEntry(_commanderEncounters, map, entry, targetPosition);
        CommanderEncounterDefinition definition;
        if (authored is not null) definition = authored;
        else if (CommanderBossCatalog.Find(entry) is { } fact)
            definition = CommanderEncounterSelectionLaw.Select(_commanderEncounters, fact, map,
                targetPosition, groupSize);
        else
        {
            if (announce)
                _commanderRaidMessage = $"No fight definition covers that target (entry {entry}) on this map.";
            return false;
        }
        if (!ReferenceEquals(definition, _commanderRaidDraft.Encounter))
        {
            _commanderRaidDraft = CommanderRaidPlan.ForEncounter(definition) with
            { MainGuid = mainGuid, MainRole = _commanderRaidDraft.MainRole };
            _commanderRaidApplied = null; _commanderRaidPage = 0; _commanderAutoAssignPending = false;
        }
        if (announce)
            _commanderRaidMessage = authored is not null
                ? $"Loaded {definition.Name}. Auto-assign when ready."
                : "Basic plan built from this target's live facts; special mechanics need a fight definition.";
        return true;
    }

    /// <summary>The nearest live hostile the raid could be fighting, when nothing is selected.</summary>
    private ulong NearestCommanderEncounterGuid()
    {
        if (!TryGetControlledBodyPose(out WorldBodyPose body)) return 0;
        WorldEntity? best = null; float bestDistance = float.MaxValue;
        foreach (WorldEntity unit in _entities.Units)
        {
            if (!unit.IsCreature || unit.IsDead || !CanAttack(unit)) continue;
            float distance = Vector3.DistanceSquared(unit.Position, body.Position);
            // Same reach the executor's own engage circle uses; further than that is not
            // "the pack in front of me".
            if (distance > 60 * 60 || distance >= bestDistance) continue;
            best = unit; bestDistance = distance;
        }
        return best?.Guid ?? 0;
    }

    private enum CommanderOneClickStage { Idle, Applying, Arming }
    private CommanderOneClickStage _commanderRaidOneClick;
    private double _commanderRaidOneClickAt;

    /// <summary>Target -> definition -> auto-assign -> Apply; Arm follows on the server's ack.</summary>
    private void BeginCommanderOneClick(ulong mainGuid, IReadOnlyList<CommanderRaidMember> roster)
    {
        if (_commanderRaidDraft.MainRole == CommanderRaidRole.Unassigned)
        { _commanderRaidMessage = "Choose your own role first - the assignment fills around it."; return; }
        if (!LoadCommanderEncounterForTarget(mainGuid, roster.Count, announce: false)) return;
        AutoAssignCommanderRaid(roster, mainGuid);
        var errors = CommanderRaidPlanLaw.Validate(_commanderRaidDraft, roster);
        if (errors.Count > 0) { _commanderRaidMessage = errors[0]; return; }
        SendCommanderRaid(CommanderRaidOperation.Apply);
        if (_commanderRaidPending == 0) return;          // the send itself was refused
        _commanderRaidOneClick = CommanderOneClickStage.Applying;
        _commanderRaidOneClickAt = NowSeconds();
        _commanderRaidMessage = $"Handling {_commanderRaidDraft.Encounter.Name}...";
    }

    /// <summary>
    /// Drives the Apply -> Arm handshake the one-click button started. Arm is a separate request
    /// that the server only accepts once it has acknowledged the applied plan, so this waits for
    /// the ack rather than firing both and hoping.
    /// </summary>
    private void AdvanceCommanderOneClick()
    {
        if (_commanderRaidOneClick == CommanderOneClickStage.Idle) return;
        if (NowSeconds() - _commanderRaidOneClickAt > 10)
        { _commanderRaidOneClick = CommanderOneClickStage.Idle; return; }
        if (_commanderRaidPending != 0) return;
        if (_commanderRaidOneClick == CommanderOneClickStage.Applying)
        {
            if (!ReferenceEquals(_commanderRaidApplied, _commanderRaidDraft) ||
                _commanderRaidStatus?.State is not (1 or 3))
            { _commanderRaidOneClick = CommanderOneClickStage.Idle; return; }
            SendCommanderRaid(CommanderRaidOperation.Arm);
            _commanderRaidOneClick = _commanderRaidPending == 0
                ? CommanderOneClickStage.Idle : CommanderOneClickStage.Arming;
            return;
        }
        _commanderRaidOneClick = CommanderOneClickStage.Idle;
    }

    private void SendCommanderRaid(CommanderRaidOperation operation)
    {
        if (!_commanderRaidAvailable || _net is not { IsInWorld: true } || _commanderRaidPending != 0) return;
        if (operation != CommanderRaidOperation.Inspect && RefuseTacticalFreezeLiveCommand("changing the raid plan")) return;
        if (operation == CommanderRaidOperation.Apply)
        {
            var errors = CommanderRaidPlanLaw.Validate(_commanderRaidDraft, CommanderRaidRoster(fresh: true));
            if (errors.Count > 0) { _commanderRaidMessage = errors[0]; return; }
        }
        uint request = ++_commanderRaidSequence;
        if (!_net.SuiCommanderRaid(CommanderRaidWire.Build(request, _commanderRaidStatus?.Revision ?? 0,
                operation, _commanderRaidDraft))) { _commanderRaidMessage = "Could not send the request."; return; }
        _commanderRaidSentAt = NowSeconds();
        _commanderRaidPending = request;
        _commanderRaidPendingOperation = operation;
        _commanderRaidPendingPlan = operation == CommanderRaidOperation.Apply ? _commanderRaidDraft : null;
        if (operation != CommanderRaidOperation.Inspect) _commanderRaidMessage = "Waiting for the raid's response...";
    }

    private void ApplyCommanderRaidStatus(byte[] body)
    {
        if (!_commanderRaidAvailable || !CommanderRaidWire.TryParse(body, out var status) || status is null) return;
        if (_commanderRaidStatus is not null && status.Revision < _commanderRaidStatus.Revision) return;
        _commanderRaidStatus = status;
        _commanderRaidStatusAt = NowSeconds();
        if (status.RequestId == _commanderRaidPending)
        {
            bool mutation = _commanderRaidPendingOperation != CommanderRaidOperation.Inspect;
            if (mutation) _commanderRaidMutationFailed = status.Result != 0;
            Console.WriteLine($"[commander-raid] operation={_commanderRaidPendingOperation} request={status.RequestId} result={status.Result} state={status.State} phase={status.Phase} boss={status.BossGuid}");
            if (_commanderRaidPendingPlan is not null && status.Result == 0) _commanderRaidApplied = _commanderRaidPendingPlan;
            _commanderRaidPending = 0;
            _commanderRaidPendingPlan = null;
            if (status.Result != 0) _commanderRaidMessage = CommanderRaidWire.ResultText(status.Result);
            else if (mutation || !_commanderRaidMutationFailed) _commanderRaidMessage = status.State switch
            {
                0 => "No plan applied.", 1 => "Plan accepted. Arm when the raid is ready.",
                2 => "Armed. Pull when ready; the bots move when combat starts.",
                3 => "Raid plan paused. Manual orders remain available.", 4 => "Encounter complete.", _ => "Accepted.",
            };
        }
    }

    private void DrawCommanderRaidPlanner(IReadOnlyList<CommanderRaidMember>? previewRoster = null)
    {
        if (!_partyTacticsOpen) return;
        if (!_commanderRaidLoaded)
        {
            _commanderRaidLoaded = true;
            try { _commanderEncounters = CommanderEncounterCatalog.Load(Path.Combine(_config.RepoRoot, "encounter-definitions")); }
            catch (Exception ex) { _commanderRaidMessage = "Definition error: " + ex.Message; }
            if (File.Exists(CommanderRaidPath))
                try { _commanderRaidDraft = CommanderRaidPlanStore.Load(CommanderRaidPath); }
                catch (Exception ex) { _commanderRaidMessage = "Could not load plan: " + ex.Message; }
        }
        double now = NowSeconds();
        if (_commanderRaidPending != 0 && now - _commanderRaidSentAt > 5)
        {
            _commanderRaidPending = 0; _commanderRaidPendingPlan = null; _commanderRaidApplied = null;
            _commanderRaidMessage = "No response. Refresh status before arming.";
        }
        AdvanceCommanderOneClick();
        if (_commanderRaidAvailable && _commanderRaidPending == 0 && now >= _commanderRaidPollAt)
        {
            bool escaping = _commanderRaidStatus is { State: 2 } live &&
                live.Actors.Any(a => a.Guid == ControlledGuid && a.Guidance.State is 2 or 4);
            _commanderRaidPollAt = now + (escaping ? .2 : 1);
            SendCommanderRaid(CommanderRaidOperation.Inspect);
        }

        float s = MathF.Min(GameplayUiScale(), MathF.Min(ImGui.GetIO().DisplaySize.X / 1000, ImGui.GetIO().DisplaySize.Y / 740));
        if (_commanderRaidPositionPending)
        {
            ImGui.SetNextWindowPos((ImGui.GetIO().DisplaySize - new Vector2(960, 690) * s) * .5f, ImGuiCond.Always);
            _commanderRaidPositionPending = false;
        }
        ImGui.SetNextWindowSize(new Vector2(960, 690) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("###party-tactics", ref _partyTacticsOpen,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse))
        { ImGui.End(); return; }
        DrawVanillaPanelChrome("Rotations & Raid Plan", s, ref _partyTacticsOpen);
        var dl = ImGui.GetWindowDrawList();
        Vector2 origin = ImGui.GetWindowPos() + new Vector2(18, 56) * s;
        dl.AddRectFilled(origin - new Vector2(5, 5) * s, origin + new Vector2(928, 617) * s, 0xee151619);
        void Text(string text, float x, float y, uint color = 0xffddd9cf) =>
            GameText.Draw(dl, "GameFontNormalSmall", text, origin + new Vector2(x, y) * s, s, color);
        bool Button(string id, string text, float x, float y, float w, bool enabled = true) =>
            VanillaButton(dl, id, text, origin + new Vector2(x, y) * s, new Vector2(w, 23), s, enabled);

        var roster = previewRoster ?? CommanderRaidRoster();
        Text(GameText.EllipsizeToBox("GameFontNormalSmall", _commanderRaidDraft.Encounter.Name.ToUpperInvariant(), 390, 20, s), 0, 0, VanillaGold);
        int readyCount = roster.Count(m => CommanderReadiness(m.Guid).Ready);
        Text($"{roster.Count} members / {_commanderRaidDraft.Assignments.Count(a => !a.Manual)} assigned bots / {readyCount} fed and flasked", 420, 0);
        bool editing = _commanderRaidStatus?.State != 2 && _commanderRaidPending == 0;
        ulong mainGuid = previewRoster is not null ? previewRoster.First().Guid : LocalPlayerGuid;
        if (Button("raid-main-role", "My role: " + CommanderRaidPlanLaw.RoleName(_commanderRaidDraft.MainRole), 0, 24, 208, editing))
        {
            _commanderRaidDraft = _commanderRaidDraft with { MainGuid = mainGuid,
                MainRole = (CommanderRaidRole)((int)_commanderRaidDraft.MainRole % 5 + 1), Assignments = [] };
            _commanderRaidApplied = null;
        }
        if (Button("raid-preset", "Auto-assign", 216, 24, 112, editing && _commanderRaidDraft.MainRole != CommanderRaidRole.Unassigned))
        {
            if (roster.Any(m => (m.Commandable || m.Guid == mainGuid) && !m.FactsReady) && previewRoster is null)
            {
                RequestPartyMemberFacts("encounter auto-assign");
                _commanderAutoAssignPending = true; _commanderAutoAssignAt = now;
                _commanderRaidMessage = "Refreshing the group's learned abilities before assigning duties...";
            }
            else AutoAssignCommanderRaid(roster, mainGuid);
        }
        if (_commanderAutoAssignPending && editing)
        {
            if (roster.All(m => !m.Commandable && m.Guid != mainGuid || m.FactsReady))
            { _commanderAutoAssignPending = false; AutoAssignCommanderRaid(roster, mainGuid); }
            else if (now - _commanderAutoAssignAt > 10)
            { _commanderAutoAssignPending = false; _commanderRaidMessage = "Some members' live abilities are still unavailable. Refresh the group and retry."; }
            else RequestPartyMemberFacts("waiting for encounter abilities");
        }
        if (Button("raid-save", "Save", 336, 24, 78))
            try { CommanderRaidPlanStore.Save(CommanderRaidPath, _commanderRaidDraft); _commanderRaidMessage = "Plan saved on this computer."; }
            catch (Exception ex) { _commanderRaidMessage = "Save failed: " + ex.Message; }
        if (Button("raid-encounter", "Encounter", 422, 24, 92, editing))
        {
            int current = _commanderEncounters.ToList().FindIndex(d => d.Id == _commanderRaidDraft.Encounter.Id);
            _commanderRaidDraft = CommanderRaidPlan.ForEncounter(_commanderEncounters[(current + 1) % _commanderEncounters.Count]) with { MainGuid = mainGuid, MainRole = _commanderRaidDraft.MainRole };
            _commanderRaidApplied = null;
            _commanderRaidPage = 0;
            if (File.Exists(CommanderRaidPath))
                try { _commanderRaidDraft = CommanderRaidPlanStore.Load(CommanderRaidPath); }
                catch (Exception ex) { _commanderRaidMessage = "Could not load plan: " + ex.Message; }
        }
        if (Button("raid-reload-definitions", "Reload fights", 676, 24, 132, editing))
            try
            {
                var definitions = CommanderEncounterCatalog.Load(Path.Combine(_config.RepoRoot, "encounter-definitions"));
                _commanderEncounters = definitions;
                var refreshed = definitions.FirstOrDefault(d => d.Id == _commanderRaidDraft.Encounter.Id);
                if (refreshed is not null) _commanderRaidDraft = CommanderRaidPlan.ForEncounter(refreshed) with { MainGuid = mainGuid, MainRole = _commanderRaidDraft.MainRole };
                _commanderRaidMessage = "Definitions loaded. Build a preset for the selected encounter.";
            }
            catch (Exception ex) { _commanderRaidMessage = "Definition error: " + ex.Message; }
        if (Button("raid-use-target", "Use target", 816, 24, 96, editing && previewRoster is null))
            LoadCommanderEncounterForTarget(mainGuid, roster.Count, announce: true);

        Text("Choose your role, then Auto-assign. Select a member to review their duty. " +
            "Your orders override the bots.", 0, 61);
        Text("CHARACTER", 0, 87, VanillaGold); Text("ROLE", 138, 87, VanillaGold);
        Text("TEAM", 248, 87, VanillaGold); Text("LIVE DUTY", 312, 87, VanillaGold);
        var assignments = _commanderRaidDraft.Assignments;
        const int pageSize = 16;
        _commanderRaidPage = Math.Clamp(_commanderRaidPage, 0, Math.Max(0, (assignments.Count - 1) / pageSize));
        for (int i = _commanderRaidPage * pageSize; i < Math.Min(assignments.Count, (_commanderRaidPage + 1) * pageSize); i++)
        {
            var row = assignments[i]; float y = 108 + (i % pageSize) * 25;
            if (row.Guid == _partyTacticsGuid) dl.AddRectFilled(origin + new Vector2(-2, y - 1) * s, origin + new Vector2(457, y + 23) * s, 0xff343028);
            if (Button("raid-member-" + row.Guid, (row.Manual ? "You: " : "") + row.Name, 0, y, 132)) _partyTacticsGuid = row.Guid;
            if (Button("raid-role-" + row.Guid, CommanderRaidPlanLaw.RoleName(row.Role), 138, y, 104, editing && !row.Manual))
            {
                var changed = row with { Role = (CommanderRaidRole)((int)row.Role % 5 + 1) };
                var layout = CommanderRaidPlanLaw.Layout(_commanderRaidDraft with
                { Assignments = assignments.Select(a => a.Guid == row.Guid ? changed : a).ToArray() });
                ChangeCommanderRaidRow(changed with { Ground = layout.Assignments.First(a => a.Guid == row.Guid).Ground });
                _commanderRaidDraft = CommanderRaidPlanLaw.AssignHealing(_commanderRaidDraft);
            }
            if (Button("raid-team-" + row.Guid, GameText.EllipsizeToBox("GameFontNormalSmall", _commanderRaidDraft.Encounter.Teams[(int)row.Team].Name, 48, 20, s), 248, y, 58, editing))
            {
                var definition = _commanderRaidDraft.Encounter;
                var team = (CommanderRaidTeam)(((int)row.Team + 1) % definition.Teams.Length);
                var offset = CommanderEncounterLaw.Point(definition.Teams[(int)team].Anchor) - CommanderEncounterLaw.Point(definition.Teams[(int)row.Team].Anchor);
                ChangeCommanderRaidRow(row with { Team = team, Ground = row.Ground + offset, Air = row.Air + offset });
                _commanderRaidDraft = CommanderRaidPlanLaw.AssignHealing(_commanderRaidDraft);
            }
            var live = _commanderRaidStatus?.Actors.FirstOrDefault(a => a.Guid == row.Guid);
            var guidance = CommanderRaidGuidanceLaw.Current(_commanderRaidStatus, row.Guid, _commanderRaidDraft.Encounter, NowSeconds() - _commanderRaidStatusAt);
            // Before the plan is armed the column reads pre-pull readiness; once armed it reads the live duty.
            var readiness = _commanderRaidStatus?.State == 2 ? null : CommanderReadiness(row.Guid);
            Text(readiness is not null ? readiness.Text : guidance is not null ? CommanderRaidGuidanceLaw.Text(guidance.State) :
                live is { Known: true, Alive: false } ? "Dead" : row.Manual ? "You control" : live is null ? "Not applied" : CommanderRaidWire.DutyText(live.Duty),
                312, y + 5, readiness is { Ready: false } ? 0xff80b7f1 : 0xffa9bcb0);
        }
        if (Button("raid-prev", "Previous", 0, 514, 92, _commanderRaidPage > 0)) _commanderRaidPage--;
        Text($"Page {_commanderRaidPage + 1} / {Math.Max(1, (assignments.Count + 15) / 16)}", 111, 521);
        if (Button("raid-next", "Next", 226, 514, 80, (_commanderRaidPage + 1) * pageSize < assignments.Count)) _commanderRaidPage++;
        if (Button("raid-map-phase", _commanderRaidAirMap ? "Alternate stations" : "Primary stations", 522, 24, 146)) _commanderRaidAirMap = !_commanderRaidAirMap;
        Text(_commanderRaidDraft.Encounter.Coverage == "basic" ? "BASIC PLAN: special mechanics are not mapped." : "North is up. Select a member, then click their station.", 482, 61);
        DrawCommanderRaidMap(dl, origin + new Vector2(482, 87) * s, new Vector2(430, 266) * s, s, editing);

        var selected = assignments.FirstOrDefault(a => a.Guid == _partyTacticsGuid);
        if (selected is not null)
        {
            Text(selected.Name + " - " + CommanderRaidPlanLaw.RoleName(selected.Role), 482, 363, VanillaGold);
            string duty = CommanderRaidPlanLaw.Duty(selected.Role, _commanderRaidAirMap);
            float dutyY = 383;
            foreach (string line in WrapTooltipText(duty, "GameFontNormalSmall", s, 422 * s).Take(1))
            { Text(line, 482, dutyY); dutyY += 13; }
            if (selected.Role == CommanderRaidRole.Healer)
            {
                string patient = assignments.FirstOrDefault(a => a.Guid == selected.HealPrimary)?.Name ?? "Unassigned";
                if (Button("raid-primary-patient", "Primary: " + patient, 482, 397, 238, editing))
                {
                    int index = assignments.ToList().FindIndex(a => a.Guid == selected.HealPrimary);
                    ChangeCommanderRaidRow(selected with { HealPrimary = assignments[(index + 1) % assignments.Count].Guid });
                }
                Text("Team + emergency backup", 726, 403);
            }
            else Text(selected.Manual ? "Your chosen role informs the bots; you control your character." : "Support: " +
                (selected.InterruptSpell != 0 ? "interrupt " : "") + (selected.DispelSpell != 0 ? "dispel " : "") +
                (selected.TauntSpell != 0 ? "taunt " : ""), 482, 399);
            if (Button("raid-heal-spell", "Heal: " + RaidSpellName(selected.HealSpell), 482, 420, 208, editing))
                CycleCommanderRaidSpell(selected, true, roster);
            if (Button("raid-damage-spell", "Attack: " + RaidSpellName(selected.DamageSpell), 697, 420, 214, editing))
                CycleCommanderRaidSpell(selected, false, roster);
        }
        if (Button("raid-breath", (_commanderRaidDraft.AvoidBreath ? "On: " : "Off: ") + "Avoid mapped hazards", 482, 455, 205, editing))
            _commanderRaidDraft = _commanderRaidDraft with { AvoidBreath = !_commanderRaidDraft.AvoidBreath };
        if (Button("raid-spread", (_commanderRaidDraft.SpreadFireballs ? "On: " : "Off: ") + "Spread from targets", 695, 455, 216, editing))
            _commanderRaidDraft = _commanderRaidDraft with { SpreadFireballs = !_commanderRaidDraft.SpreadFireballs };
        if (Button("raid-ward", (_commanderRaidDraft.MaintainFearWard ? "On: " : "Off: ") + "Maintain assigned buffs", 482, 486, 250, editing))
            _commanderRaidDraft = _commanderRaidDraft with { MaintainFearWard = !_commanderRaidDraft.MaintainFearWard };
        var errors = CommanderRaidPlanLaw.Validate(_commanderRaidDraft, roster);
        string message = !_commanderRaidAvailable ? "Planning only: matching Core update required. " + _commanderRaidMessage
            : errors.Count > 0 ? $"{errors.Count} readiness issues: {errors[0]}" : _commanderRaidMessage;
        Text(GameText.EllipsizeToBox("GameFontNormalSmall", message, 900, 24, s), 0, 554, errors.Count > 0 ? 0xff80b7f1 : VanillaGold);
        bool ready = _commanderRaidAvailable && _commanderRaidPending == 0;
        if (Button("raid-apply", "Apply to raid", 0, 584, 130, ready && editing && errors.Count == 0)) SendCommanderRaid(CommanderRaidOperation.Apply);
        if (Button("raid-arm", "Arm plan", 139, 584, 112, ready && ReferenceEquals(_commanderRaidApplied, _commanderRaidDraft) && _commanderRaidStatus?.State is 1 or 3)) SendCommanderRaid(CommanderRaidOperation.Arm);
        if (Button("raid-pause", "Pause", 260, 584, 90, ready && _commanderRaidStatus?.State == 2)) SendCommanderRaid(CommanderRaidOperation.Pause);
        if (Button("raid-clear", "Clear live plan", 359, 584, 130, ready && _commanderRaidStatus?.State is 1 or 3 or 4)) SendCommanderRaid(CommanderRaidOperation.Clear);
        if (Button("raid-supply", "Supply raid", 498, 584, 108, editing && previewRoster is null && NowSeconds() - _commanderSupplySentAt > 5))
            SupplyCommanderRaid(roster);
        if (Button("raid-prepare", "Prepare raid", 615, 584, 118, editing && previewRoster is null && NowSeconds() - _commanderPrepareSentAt > 5))
            PrepareCommanderRaid(roster);
        // One click for the pack in front of you: find its fight, assign duties, apply and arm.
        // The six-step version (cycle Encounter x N, role, auto-assign, apply, arm) is unusable
        // when a Molten Core clear is seventy-six packs.
        if (Button("raid-handle", "Handle target pack", 742, 584, 170,
                _commanderRaidAvailable && previewRoster is null && _commanderRaidPending == 0 &&
                _commanderRaidOneClick == CommanderOneClickStage.Idle))
            BeginCommanderOneClick(mainGuid, roster);
        ImGui.End();
    }

    private void CycleCommanderRaidSpell(CommanderRaidAssignment row, bool heal, IReadOnlyList<CommanderRaidMember> roster)
    {
        var member = roster.FirstOrDefault(m => m.Guid == row.Guid);
        if (member is null) return;
        var facts = CommanderRaidFacts(member.Spells).ToArray();
        var choices = facts.Where(f => heal ? f.Effects.Contains(10u) || f.Name == "Holy Light"
            : (f.Effects.Contains(2u) || f.AutoRepeat) && f.Targets.Contains(6u) && !f.Effects.Contains(68u) &&
                (_commanderRaidDraft.Encounter.ImmuneSchools & (1u << (int)f.School)) == 0)
            .OrderBy(f => f.Level).ThenBy(f => f.Id).Select(f => f.Id).ToArray();
        if (choices.Length == 0) return;
        int current = Array.IndexOf(choices, heal ? row.HealSpell : row.DamageSpell);
        uint next = choices[(current + 1) % choices.Length];
        ChangeCommanderRaidRow(heal ? row with { HealSpell = next } : row with { DamageSpell = next });
    }

    private string RaidSpellName(uint spellId) => _spellCatalog?.TryGet(spellId, out SpellInfo spell) == true
        ? spell.Name + " " + spell.Rank : "No learned spell";

    private void ChangeCommanderRaidRow(CommanderRaidAssignment replacement) =>
        _commanderRaidDraft = _commanderRaidDraft with
        { Assignments = _commanderRaidDraft.Assignments.Select(a => a.Guid == replacement.Guid ? replacement : a).ToArray() };

    private void DrawCommanderRaidMap(ImDrawListPtr dl, Vector2 min, Vector2 size, float scale, bool editable)
    {
        dl.AddRectFilled(min, min + size, 0xff202329);
        var definition = _commanderRaidDraft.Encounter;
        Vector3 low = CommanderEncounterLaw.Point(definition.Bounds.Min), high = CommanderEncounterLaw.Point(definition.Bounds.Max);
        Vector2 Project(Vector3 p) => min + new Vector2((high.Y - p.Y) / (high.Y - low.Y) * size.X, (high.X - p.X) / (high.X - low.X) * size.Y);
        Vector2 center = min + size * .5f;
        for (int i = 0; i < 48; i++)
        {
            float a = MathF.Tau * i / 48, b = MathF.Tau * (i + 1) / 48;
            dl.AddLine(center + new Vector2(MathF.Cos(a) * size.X * .47f, MathF.Sin(a) * size.Y * .46f),
                center + new Vector2(MathF.Cos(b) * size.X * .47f, MathF.Sin(b) * size.Y * .46f), 0xff667588, scale);
        }
        GameText.Draw(dl, "GameFontNormalSmall", "NORTH", min + new Vector2(size.X * .38f, 3 * scale), scale, VanillaGold);
        dl.PushClipRect(min, min + size, true);
        foreach (var row in _commanderRaidDraft.Assignments)
        {
            Vector2 p = Project(_commanderRaidAirMap ? row.Air : row.Ground);
            uint color = row.Role == CommanderRaidRole.Healer ? 0xff70cf75 : row.Role is CommanderRaidRole.MainTank or CommanderRaidRole.AddTank ? 0xff80aff0 : 0xffdab66f;
            dl.AddCircleFilled(p, (row.Role == CommanderRaidRole.MainTank ? 6 : 4) * scale, color);
            if (row.Role == CommanderRaidRole.MainTank) GameText.Draw(dl, "GameFontNormalSmall", "MT", p + new Vector2(7, -5) * scale, scale, color);
            if (row.Guid == _partyTacticsGuid) dl.AddCircle(p, 8 * scale, 0xffffffff, 20, 2 * scale);
            var guidance = CommanderRaidGuidanceLaw.Current(_commanderRaidStatus, row.Guid, definition, NowSeconds() - _commanderRaidStatusAt);
            if (guidance is { State: 2 or 4 } && _entities.TryGet(row.Guid, out var observed))
            {
                Vector2 from = Project(observed.Position), next = Project(guidance.Waypoint);
                uint guidanceColor = guidance.State == 4 ? 0xff609cff : 0xff80e6a0;
                dl.AddLine(from, next, guidanceColor, 2 * scale);
                dl.AddCircle(next, 7 * scale, guidanceColor, 20, 2 * scale);
            }
        }
        dl.PopClipRect();
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("raid-room-map", size);
        if (editable && ImGui.IsItemClicked() && _commanderRaidDraft.Assignments.FirstOrDefault(a => a.Guid == _partyTacticsGuid) is { } selected)
        {
            Vector2 point = (ImGui.GetIO().MousePos - min) / size;
            Vector3 position = new(high.X - point.Y * (high.X - low.X), high.Y - point.X * (high.Y - low.Y), selected.Ground.Z);
            ChangeCommanderRaidRow(_commanderRaidAirMap ? selected with { Air = position } : selected with { Ground = position });
        }
    }
}
