using MSUIClient;
using MSUIClient.Formats;

internal static class MountSheatheSoundClinicalChecks
{
    public static void Run()
    {
        SheatheSoundCatalog catalog = SheatheSoundCatalog.FromRows(
            (2, 0, 1, 698, 700), (2, 0, 2, 697, 699), (4, 6, 0, 696, 701));
        Check(catalog.Count == 3 &&
              catalog.TryGet(2, 15, 1, out SheatheSoundPair dagger) &&
              dagger == new SheatheSoundPair(698, 700) &&
              catalog.TryGet(2, 2, 2, out SheatheSoundPair bow) &&
              bow == new SheatheSoundPair(697, 699) &&
              catalog.TryGet(4, 6, 6, out SheatheSoundPair shield) &&
              shield == new SheatheSoundPair(696, 701) &&
              catalog.TryGet(2, 18, 1, out SheatheSoundPair crossbow) &&
              crossbow == new SheatheSoundPair(698, 700),
            "SheatheSoundLookups material/subclass/shield fallback drift");

        // The cue keys on the item's real class/subclass/material. The local body was the one
        // kit built without them — BuildEquipment supplies display id and inventory type only,
        // leaving all three at zero, and the table has no class-zero row, so the lookup missed
        // every time. SyncLiveEquipmentModel is what carries the ItemTemplate bytes across.
        Check(!catalog.TryGet(0, 0, 0, out _),
            "class-zero kit must not resolve a sheathe cue");

        string root = ClientConfig.FindRepoRoot();
        string worldSound = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Sound.cs"));
        string sheath = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Sheath.cs"));
        Check(worldSound.Contains("SpiritWolf (DONOTRENAME)", StringComparison.Ordinal) &&
              worldSound.Contains("previous != 0 && mount == 0 && playbackAllowed",
                  StringComparison.Ordinal) &&
              worldSound.Contains("_knownMountSoundDisplays[unit.Guid] = mount;",
                  StringComparison.Ordinal),
            "silent first-sight/mount-up or fixed dismount trigger drift");
        string character = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CharacterRenderer.cs"));
        string animator = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "M2Animator.cs"));
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        // The cue is sounded by the TRANSITION, not by the animation. It used to hang off the
        // $SHL/$SHR ceremony event alone, so a body whose rig has no complete hand-to-shoulder
        // arm masks degraded to a silent snap and the combat draw never sounded at all. Every
        // path that moves the pose goes through the one choke point, which is what these
        // assertions pin. The ceremony itself is unchanged and still drives the placement.
        Check(sheath.Contains("private void SetSheathVisualState(byte state, bool audible)",
                  StringComparison.Ordinal) &&
              sheath.Contains("if (audible && previous != state) PlaySheatheSounds(previous, state);",
                  StringComparison.Ordinal) &&
              sheath.Contains("SetSheathVisualState(ceremonialState, audible: true);",
                  StringComparison.Ordinal) &&
              sheath.Contains("SetSheathVisualState(next, audible: true);",
                  StringComparison.Ordinal) &&
              !sheath.Contains("TriggerOneShot(animation)", StringComparison.Ordinal) &&
              // Not quarantined: two voices on a pose change the player asked for is not the
              // renderer-event burst AudioFeaturePolicy exists to hold back.
              !sheath.Contains("AudioFeaturePolicy.ExpandedWorldAudioEnabled",
                  StringComparison.Ordinal) &&
              sheath.Contains("foreach (int equipmentSlot in ranged ? new[] { 17 } : new[] { 15, 16 })",
                  StringComparison.Ordinal) &&
              sheath.Contains("bool drawing = destinationState != 0;", StringComparison.Ordinal) &&
              sheath.Contains("bool ranged = destinationState == 2 || previousState == 2;",
                  StringComparison.Ordinal) &&
              sheath.Contains("drawing ? pair.Unsheathe : pair.Sheathe",
                  StringComparison.Ordinal) &&
              // The resync adoption after a body hand-off is silent; combat outranks the
              // server byte rather than fighting it frame by frame, and leaves state 2 alone
              // only while a ranged action is live (a one-shot pull must not keep the bow up).
              sheath.Contains("bool resync = !_sheathSoundSynced;", StringComparison.Ordinal) &&
              sheath.Contains("bool combatForcesDrawn = !_freeView && player.Engaged;",
                  StringComparison.Ordinal) &&
              sheath.Contains("bool rangedPoseHeld = _visualSheathState == 2 && RangedActionLive();",
                  StringComparison.Ordinal) &&
              sheath.Contains("combatForcesDrawn && !rangedPoseHeld",
                  StringComparison.Ordinal) &&
              sheath.Contains("_character.BeginSheathCeremony()", StringComparison.Ordinal) &&
              sheath.Contains("_character.ConsumeSheathSwap()", StringComparison.Ordinal) &&
              character.Contains("89, 90, 92", StringComparison.Ordinal) &&
              character.Contains("EvaluateWithArmOverlays", StringComparison.Ordinal) &&
              character.Contains("$SHL", StringComparison.Ordinal) &&
              animator.Contains("ResolveArmRoots", StringComparison.Ordinal) &&
              animator.Contains("ApplyOverlayChannels", StringComparison.Ordinal) &&
              inventory.Contains("existing.EquipmentSlot == piece.EquipmentSlot", StringComparison.Ordinal) &&
              inventory.Contains("existing.Sheath == piece.Sheath", StringComparison.Ordinal),
            "transition-sounded per-arm sheathe routing drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
