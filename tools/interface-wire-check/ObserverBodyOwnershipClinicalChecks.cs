using MSUIClient;
using MSUIClient.Engine;

/// <summary>
/// Pins the Ctrl+F ownership boundary: the detached controller is a camera/command rig, while
/// gameplay pose, reach, facing, movement acknowledgements, and body animation stay attached
/// to the appropriate streamed unit.
/// </summary>
internal static class ObserverBodyOwnershipClinicalChecks
{
    public static void Run()
    {
        CheckPoseLaw();

        string root = ClientConfig.FindRepoRoot();
        string client = Path.Combine(root, "MSUIClient");
        string control = Read(client, "GameLoop", "Scene", "GameLoop.Control.cs");
        string net = Read(client, "GameLoop", "Scene", "GameLoop.Net.cs");
        string cursor = Read(client, "GameLoop", "Hud", "GameLoop.WorldCursor.cs");
        string instances = Read(client, "GameLoop", "Scene", "GameLoop.Instances.cs");
        string portals = Read(client, "GameLoop", "Scene", "GameLoop.RealPortals.cs");
        string runtime = Read(client, "Program.cs");
        string combat = Read(client, "GameLoop", "Combat", "GameLoop.CombatAnimations.cs");
        // The victim-side swing reaction moved out of CombatAnimations into MeleeSounds.
        string melee = Read(client, "GameLoop", "Combat", "GameLoop.MeleeSounds.cs");
        string casting = Read(client, "GameLoop", "Combat", "GameLoop.Casting.cs");
        string modes = Read(client, "GameLoop", "Scene", "GameLoop.MovementModes.cs");
        string speeds = Read(client, "GameLoop", "Scene", "GameLoop.MovementSpeed.cs");
        string transports = Read(client, "GameLoop", "Scene", "GameLoop.Transports.cs");
        string targeting = Read(client, "GameLoop", "Combat", "GameLoop.Targeting.cs");
        string help = Read(client, "GameLoop", "Panels", "GameLoop.Help.cs");
        string cursorLaw = Read(client, "Engine", "UI", "WorldCursorUiLaw.cs");
        string loot = Read(client, "GameLoop", "Panels", "GameLoop.Loot.cs");
        string creatures = Read(client, "World", "Units", "CreatureRenderer.cs");
        string professions = Read(client, "GameLoop", "Panels", "GameLoop.Professions.cs");
        string chat = Read(client, "GameLoop", "Panels", "GameLoop.Chat.cs");
        string pet = Read(client, "GameLoop", "Panels", "GameLoop.Pet.cs");
        string petMenu = Read(client, "GameLoop", "Panels", "GameLoop.PetMenu.cs");
        string unitPopup = Read(client, "GameLoop", "Hud", "GameLoop.UnitPopup.cs");
        string inventory = Read(client, "GameLoop", "Panels", "GameLoop.Inventory.cs");
        string characterPage = Read(client, "GameLoop", "Panels", "GameLoop.CharacterPage.cs");
        string deleteItem = Read(client, "GameLoop", "Panels", "GameLoop.DeleteItem.cs");
        string talents = Read(client, "GameLoop", "Panels", "GameLoop.Talents.cs");
        string skills = Read(client, "GameLoop", "Panels", "GameLoop.SkillFrame.cs");
        string taxi = Read(client, "GameLoop", "Panels", "GameLoop.Taxi.cs");
        string quest = Read(client, "GameLoop", "Panels", "GameLoop.Quest.cs");
        string auction = Read(client, "GameLoop", "Panels", "GameLoop.Auction.cs");
        string tabard = Read(client, "GameLoop", "Panels", "GameLoop.Tabard.cs");

        Check(control.Contains("private bool ControllerOwnsControlledBodyPose", Ordinal) &&
              control.Contains("WorldBodyPoseLaw.ControllerOwnsPose(", Ordinal) &&
              control.Contains("!_movementSender.Parked && !_controller.Flying", Ordinal) &&
              control.Contains("private bool TryGetControlledBodyPose", Ordinal) &&
              control.Contains("private bool TryGetSessionBodyPose", Ordinal),
            "observer/body pose resolver or controller-authority fence drift");

        Check(net.Contains(
                  "TryGetWorldBodyPose(net.PlayerGuid, out WorldBodyPose sessionBody)", Ordinal) &&
              net.Contains(
                  "FaceIdleTargets(dt, net.PlayerGuid, sessionBody.Position)", Ordinal) &&
              !net.Contains(
                  "FaceIdleTargets(dt, net.PlayerGuid, _controller.Position)", Ordinal),
            "idle NPC facing must follow the session body, never the Free View rig");

        string streamedTeleport = Slice(net,
            "if (moverGuid != ControlledGuid || !ControllerOwnsControlledBodyPose)",
            "// A same-map teleport can still be thousands of yards");
        Check(streamedTeleport.Contains("_entities.ApplyServerAuthoredMove(", Ordinal) &&
              streamedTeleport.Contains("ObserveTeleportApplied(", Ordinal) &&
              streamedTeleport.Contains("net.TeleportAck(moverGuid, counter);", Ordinal) &&
              // The adopt-and-ACK arm now exits with return; rather than a switch break;.
              // Either way the requirement is the same: leave before the camera/residency work.
              streamedTeleport.Contains("return;", Ordinal) &&
              !streamedTeleport.Contains("_controller.Teleport", Ordinal) &&
              !streamedTeleport.Contains("_window.Camera", Ordinal) &&
              !streamedTeleport.Contains("_residentCentre", Ordinal) &&
              !streamedTeleport.Contains("_movementSender", Ordinal),
            "streamed session teleports must update/ACK the body without moving the observer rig");

        Check(net.Contains("streamedRider.Transport is { } streamedRide", Ordinal) &&
              net.Contains("_entities.ClearExcept(streamedRide.Guid);", Ordinal) &&
              net.Contains(
                  "_controlledTransportRide = crossingRideBelongsToController ? crossingRide : null;",
                  Ordinal) &&
              net.Contains(
                  "if (crossingRide is not null && crossingRideBelongsToController)", Ordinal) &&
              control.Contains("driven.Transport = _controller.Transport;", Ordinal),
            "Free View transport seams must preserve the streamed rider tail without attaching " +
            "the observer rig to the vessel");

        Check(cursor.Contains("bool goSessionScoped = hoveredGo.GameObjectType is 9 or 19;",
                  Ordinal) &&
              // POSSESS_LAW 2.1 names the world cursor verdict and loot explicitly: they gate
              // on TryGetInteractionBodyPose, NEVER TryGetSessionBodyPose. This block used to
              // require the session call for both the session-scoped game object and the loot
              // branch, so it demanded the one call the law forbids here. Assert the law.
              cursor.Contains("TryGetInteractionBodyPose(out goActorBody)", Ordinal) &&
              cursor.Contains("TryGetControlledBodyPose(out goActorBody)", Ordinal) &&
              cursor.Contains("if (serviceKind is not null)", Ordinal) &&
              cursor.Contains("hasActorPose = TryGetInteractionBodyPose(out actorPose);", Ordinal) &&
              cursor.Contains("else if (unit.IsDead", Ordinal) &&
              cursor.Contains("POSSESS_LAW 2.1: loot is done by the driven body", Ordinal) &&
              cursor.Contains("hasActorPose = TryGetControlledBodyPose(out actorPose);", Ordinal) &&
              !cursor.Contains("TryGetSessionBodyPose", Ordinal) &&
              !cursor.Contains("_controller.Position", Ordinal),
            "world cursor must use interaction-body reach for loot, services and " +
            "session-scoped game objects (POSSESS_LAW 2.1), and controlled-body reach " +
            "for combat");

        // POSSESS_LAW 2.1 lists area triggers among the gates that use
        // TryGetInteractionBodyPose and never TryGetSessionBodyPose. The out-variable is still
        // named sessionBody in GameLoop.Instances.cs, which is why the rest of this block
        // still matched while the call itself had already moved.
        Check(instances.Contains(
                  "TryGetInteractionBodyPose(out WorldBodyPose sessionBody)", Ordinal) &&
              instances.Contains("Vector3 bodyPosition = sessionBody.Position;", Ordinal) &&
              instances.Contains("?.Contains(bodyPosition) == true", Ordinal) &&
              instances.Contains("_areaTriggers.Containing(mapId, bodyPosition)", Ordinal),
            "area triggers must gate on the interaction body (POSSESS_LAW 2.1), never the " +
            "observer rig");
        Check(portals.Contains(
                  "if (!ControllerOwnsControlledBodyPose || !RealPortalsEnabled", Ordinal),
            "local real-portal crossing must be disabled when the controller is an observer rig");

        Check(runtime.Contains(
                  "UpdateCastMovementInput(ControllerOwnsControlledBodyPose &&", Ordinal) &&
              runtime.Contains("(translating || input.Jump));", Ordinal),
            "observer camera movement must not interrupt the streamed body's cast/channel");

        Check(combat.Contains(
                  "swing.Attacker == ControlledGuid && !ControlledBodyIsStreamed", Ordinal) &&
              melee.Contains(
                  "swing.Victim == ControlledGuid && !ControlledBodyIsStreamed", Ordinal) &&
              combat.Contains("_creatures?.TriggerCombatSwing(swing.Attacker", Ordinal) &&
              melee.Contains("_creatures?.TriggerCombatReaction(swing.Victim", Ordinal) &&
              casting.Contains(
                  "target == ControlledGuid && !ControlledBodyIsStreamed", Ordinal) &&
              casting.Contains(
                  "unit == ControlledGuid && !ControlledBodyIsStreamed", Ordinal) &&
              casting.Contains(
                  "guid == ControlledGuid && TryGetWorldBodyPose(guid", Ordinal),
            "streamed controlled bodies must own swing, wound, pushed, and spell-position visuals");

        Check(modes.Contains("ApplyStreamedMovementMode(change)", Ordinal) &&
              modes.Contains("if (ControllerOwnsMovementPose(change.Guid))", Ordinal) &&
              modes.Contains("TrySnapshotMovementAck(change.Guid, streamedModeFlags", Ordinal) &&
              modes.Contains(
                  "MovementInfo.Create(body.Position, body.Orientation, streamedFlags)", Ordinal) &&
              modes.Contains("_entities.StopMovement(change.Guid);", Ordinal),
            "movement-mode packets must mutate/ack the addressed body without mutating the rig");
        Check(speeds.Contains("ApplyEntitySpeed(change.Guid, change.Kind, change.Speed);", Ordinal) &&
              speeds.Contains("if (ControllerOwnsMovementPose(change.Guid))", Ordinal) &&
              speeds.Contains("TrySnapshotMovementAck(change.Guid, MovementFlags.None", Ordinal) &&
              speeds.Contains(
                  "change.Movement is { } movement && !ControllerOwnsMovementPose(change.Guid)",
                  Ordinal) &&
              speeds.Contains(
                  "entity.Guid != ControlledGuid || !ControllerOwnsControlledBodyPose", Ordinal),
            "forced/observer speed routing must keep detached body state off the camera controller");

        Check(transports.Contains(
                  "rider.Guid == ControlledGuid && !ControlledBodyIsStreamed", Ordinal) &&
              !transports.Contains("if (rider.Guid == ControlledGuid ||", Ordinal) &&
              Slice(transports, "private void CarryControlledTransportRider()",
                  "private void ReconcileControlledTransportRider()").Contains(
                      "if (!ControllerOwnsControlledBodyPose", Ordinal) &&
              Slice(transports, "private void ReconcileControlledTransportRider()",
                  "private static float ShortestYawDelta").Contains(
                      "if (!ControllerOwnsControlledBodyPose)", Ordinal),
            "streamed controlled riders must participate in observed transport composition");

        Check(targeting.Contains("bool canAuthor = CanAuthorControlledGameplay;", Ordinal) &&
              targeting.Contains(
                  "if (changed && canAuthorSelection) StopPetAttackForOldTargetChange", Ordinal) &&
              targeting.Contains("if (canAuthorSelection) _net?.SetSelection(guid);", Ordinal) &&
              targeting.Contains("if (canAuthor && _net is not null && guid != 0", Ordinal) &&
              targeting.Contains("if (!CanAuthorControlledGameplay || _net is null", Ordinal),
            "plain Free View selection must remain local and wire-silent");

        Check(help.Contains(
                  "TryGetSessionBodyPose(out WorldBodyPose sessionBody)", Ordinal) &&
              help.Contains("? sessionBody.Position : Vector3.Zero", Ordinal) &&
              !help.Contains("_controller?.Position", Ordinal),
            "GM ticket location must describe the session body, never the observer rig");

        Check(cursorLaw.Contains(
                  "public static float UnitMeleeReachSquared", Ordinal) &&
              cursorLaw.Contains(
                  "UnitMeleeReachSquared(playerCombatReach, unitCombatReach)", Ordinal) &&
              loot.Contains("TryGetInteractionBodyPose(out WorldBodyPose sessionBody)",
                  Ordinal) &&
              loot.Contains("WorldCursorUiLaw.UnitMeleeReachSquared(", Ordinal) &&
              AppearsBefore(loot, "WorldCursorUiLaw.UnitMeleeReachSquared(",
                  "bool sent = _net.Loot(guid);") &&
              loot.Contains("if (ControlledBodyIsStreamed)", Ordinal) &&
              loot.Contains("_creatures?.SetLootKneel(ControlledGuid, kneeling);", Ordinal) &&
              creatures.Contains("private readonly HashSet<ulong> _lootKneeling", Ordinal) &&
              creatures.Contains("_lootKneeling.Contains(e.Guid)", Ordinal),
            "loot reach and optimistic kneel belong to the driven body: interaction-body " +
            "reach (POSSESS_LAW 2.1) and ControlledGuid kneel (2.2), never the main");

        Check(casting.Contains(
                  "if (!CanAuthorControlledGameplay || _net is not { IsInWorld: true }) return false;",
                  Ordinal) &&
              professions.Contains(
                  "if (CanAuthorControlledGameplay) _net?.CancelCast(_professionCraftSpell);",
                  Ordinal),
            "stale Escape/profession UI must not cancel a body's cast from observer mode");
        Check(chat.Contains("if (!CanAuthorControlledGameplay)", Ordinal) &&
              AppearsBefore(chat, "if (!CanAuthorControlledGameplay)",
                  "_net?.SendTextEmote((uint)id, _selectionGuid);"),
            "observer-local selection must never become a text-emote body target");
        Check(pet.Contains("private void TogglePetAutocast", Ordinal) &&
              pet.Contains("private void PickupPetAction", Ordinal) &&
              pet.Contains("private void PlacePetAction", Ordinal) &&
              Count(pet, "if (!CanAuthorControlledGameplay)") >= 4 &&
              petMenu.Contains("private void ShowPetAbandonPopup", Ordinal) &&
              petMenu.Contains("private void ShowPetRenamePopup", Ordinal) &&
              petMenu.Contains(
                  "// Revalidate at the irreversible packet tail", Ordinal) &&
              unitPopup.Contains(
                  "row is UnitPopupRow.PetRename or UnitPopupRow.PetAbandon or UnitPopupRow.PetDismiss",
                  Ordinal),
            "pet actions and destructive pet popups must remain wire-silent for observers");
        Check(inventory.Contains("private bool CanAuthorSessionInventory", Ordinal) &&
              inventory.Contains(
                  "CanAuthorControlledGameplay && ControlledGuid == LocalPlayerGuid", Ordinal) &&
              inventory.Contains(
                  "if (CanAuthorControlledOrSelf) _net.AutoEquipItem(wire.Bag, wire.Slot);",
                  Ordinal) &&
              inventory.Contains(
                  "if (!CanAuthorSessionInventory || _net is null || slot is < 0 or >= 19",
                  Ordinal) &&
              characterPage.Contains("if (!CanAuthorSessionInventory) return false;", Ordinal) &&
              characterPage.Contains("sent = _net.SetAmmo(carried.Entry);", Ordinal) &&
              deleteItem.Contains("if (!CanAuthorControlledOrSelf ||", Ordinal) &&
              talents.Contains(
                  "if (!CanAuthorControlledGameplay || ControlledGuid != LocalPlayerGuid",
                  Ordinal) &&
              skills.Contains(
                  "if (!CanAuthorControlledGameplay || ControlledGuid != LocalPlayerGuid)",
                  Ordinal),
            "threaded equip must allow a possessed body, while unthreaded unequip/ammo tails " +
            "must remain session-inventory-only");

        string taxiSweep = Slice(taxi, "private void UpdateTaxiNodeStatusQueries()",
            "private void ApplyTaxiNodes");
        string questSweep = Slice(quest, "private void UpdateQuestGiverStatusQueries()",
            "private void BumpQuestStatusReask");
        Check(taxiSweep.Contains(
                  "TryGetInteractionBodyPose(out WorldBodyPose sessionBody)", Ordinal) &&
              taxiSweep.Contains(
                  "Vector3.DistanceSquared(sessionBody.Position, unit.Position)", Ordinal) &&
              questSweep.Contains(
                  "TryGetInteractionBodyPose(out WorldBodyPose sessionBody)", Ordinal) &&
              questSweep.Contains(
                  "_entities.TryGet(net.PlayerGuid, out WorldEntity player)", Ordinal) &&
              questSweep.Contains("QuestStatusSessionNeighborhoodSquared", Ordinal) &&
              !questSweep.Contains("_entities.TryGet(ControlledGuid", Ordinal),
            "automatic taxi queries must follow the session body and quest-giver queries the " +
            "interaction body, never the camera stream");

        Check(auction.Contains("private bool AuctioneerEligible(", Ordinal) &&
              auction.Contains(
                  "TryGetInteractionBodyPose(out WorldBodyPose body)", Ordinal) &&
              auction.Contains("private bool AuctionSessionInRange", Ordinal) &&
              auction.Contains("private bool UpdateAuctionLifecycle()", Ordinal) &&
              tabard.Contains("private bool TabardDesignerEligible(", Ordinal) &&
              tabard.Contains("private bool UpdateTabardLifecycle()", Ordinal) &&
              tabard.Contains("bool eligible = _tabardOpen && TabardDesignerEligible(", Ordinal) &&
              runtime.Contains("UpdateAuctionLifecycle();", Ordinal) &&
              runtime.Contains("UpdateTabardLifecycle();", Ordinal),
            "auction/tabard sessions and commit tails must retain session-body service range");
    }

    private static void CheckPoseLaw()
    {
        Check(WorldBodyPoseLaw.ControllerOwnsPose(
                freeView: false,
                stableEmbodiedControlState: true,
                queriedControlledBody: true,
                controllerMovementAuthoritative: true),
            "ordinary embodied control must use the predicted controller pose");
        Check(!WorldBodyPoseLaw.ControllerOwnsPose(
                freeView: true,
                stableEmbodiedControlState: true,
                queriedControlledBody: true,
                controllerMovementAuthoritative: true),
            "Free View must always use the streamed body pose");
        Check(!WorldBodyPoseLaw.ControllerOwnsPose(
                freeView: false,
                stableEmbodiedControlState: false,
                queriedControlledBody: true,
                controllerMovementAuthoritative: true),
            "pending control hand-offs must use the streamed body pose");
        Check(!WorldBodyPoseLaw.ControllerOwnsPose(
                freeView: false,
                stableEmbodiedControlState: true,
                queriedControlledBody: false,
                controllerMovementAuthoritative: true),
            "a controller may never substitute for a different/session body");
        Check(!WorldBodyPoseLaw.ControllerOwnsPose(
                freeView: false,
                stableEmbodiedControlState: true,
                queriedControlledBody: true,
                controllerMovementAuthoritative: false),
            "parked or flying controllers must defer to the streamed body pose");
    }

    private static string Read(string root, params string[] parts) =>
        SourceText.Read(Path.Combine([root, .. parts]));

    private static string Slice(string text, string start, string end)
    {
        int from = text.IndexOf(start, Ordinal);
        if (from < 0) return "";
        int to = text.IndexOf(end, from, Ordinal);
        return to < 0 ? "" : text[from..to];
    }

    private static bool AppearsBefore(string text, string first, string second)
    {
        int a = text.IndexOf(first, Ordinal);
        int b = text.IndexOf(second, Ordinal);
        return a >= 0 && b > a;
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int at = 0; (at = text.IndexOf(needle, at, Ordinal)) >= 0; at += needle.Length)
            count++;
        return count;
    }

    private static readonly StringComparison Ordinal = StringComparison.Ordinal;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
