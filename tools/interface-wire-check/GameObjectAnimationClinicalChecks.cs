using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;

internal static class GameObjectAnimationClinicalChecks
{
    public static void Run()
    {
        Check(GameObjectAnimationLaw.CustomAnimationId(0) == 153 &&
              GameObjectAnimationLaw.CustomAnimationId(1) == 154 &&
              GameObjectAnimationLaw.CustomAnimationId(2) == 155 &&
              GameObjectAnimationLaw.CustomAnimationId(3) == 156 &&
              GameObjectAnimationLaw.CustomAnimationId(4) is null &&
              GameObjectAnimationLaw.DespawnAnimationId == 157,
            "GameObject Custom0..3/despawn AnimationData mapping drift");
        Check(GameObjectAnimationLaw.ShouldRetainDestroy(true, true, true) &&
              !GameObjectAnimationLaw.ShouldRetainDestroy(false, true, true) &&
              !GameObjectAnimationLaw.ShouldRetainDestroy(true, false, true) &&
              !GameObjectAnimationLaw.ShouldRetainDestroy(true, true, false) &&
              GameObjectAnimationLaw.RetainedUntil(12.5, 1.25f) == 13.75 &&
              !GameObjectAnimationLaw.RetentionFinished(13.749, 13.75) &&
              GameObjectAnimationLaw.RetentionFinished(13.75, 13.75),
            "GameObject retained-destroy ownership/timing law drift");
        Check(new ObjectFields().GameObjectState == GameObjectAnimationLaw.StateActive &&
              new ObjectFields().AsCreated().GameObjectState == GameObjectAnimationLaw.StateActive &&
              !GameObjectAnimationLaw.ColliderIsSolid(null) &&
              !GameObjectAnimationLaw.ColliderIsSolid(GameObjectAnimationLaw.StateActive) &&
              GameObjectAnimationLaw.ColliderIsSolid(GameObjectAnimationLaw.StateReady) &&
              !GameObjectAnimationLaw.ColliderIsSolid(GameObjectAnimationLaw.StateAlternative),
            "GameObject omitted-wire-state/open-door collision polarity drift");
        Check(GameObjectAnimationLaw.Animates(0) &&
              GameObjectAnimationLaw.Animates(3) &&
              GameObjectAnimationLaw.Animates(10) &&
              GameObjectAnimationLaw.Animates(30) &&
              !GameObjectAnimationLaw.Animates(5) &&
              !GameObjectAnimationLaw.Animates(11) &&
              !GameObjectAnimationLaw.Animates(15) &&
              GameObjectAnimationLaw.CollisionFollowsState(0) &&
              GameObjectAnimationLaw.CollisionFollowsState(1) &&
              !GameObjectAnimationLaw.CollisionFollowsState(3),
            "GameObject family-A animation/collision type census drift");
        Check(GameObjectAnimationLaw.ResolveStatePlay(null, 0) is
                  { AnimationId: 149, Kind: GameObjectAnimationLaw.StatePlayKind.Rest } &&
              GameObjectAnimationLaw.ResolveStatePlay(null, 1) is
                  { AnimationId: 147, Kind: GameObjectAnimationLaw.StatePlayKind.Rest } &&
              GameObjectAnimationLaw.ResolveStatePlay(1, 0) is
                  { AnimationId: 148, Kind: GameObjectAnimationLaw.StatePlayKind.Motion } &&
              GameObjectAnimationLaw.ResolveStatePlay(0, 1) is
                  { AnimationId: 146, Kind: GameObjectAnimationLaw.StatePlayKind.Motion } &&
              GameObjectAnimationLaw.ResolveStatePlay(1, 2) is
                  { AnimationId: 150, Kind: GameObjectAnimationLaw.StatePlayKind.Motion } &&
              GameObjectAnimationLaw.ResolveStatePlay(2, 1) is
                  { AnimationId: 152, Kind: GameObjectAnimationLaw.StatePlayKind.Motion },
            "GameObject state rest/one-window transition table drift");
        HashSet<int> aqDoorRoots = [0, 148];
        Check(GameObjectAnimationLaw.RemapMissing(147, aqDoorRoots.Contains) is
                  { AnimationId: 148, Frozen: true } &&
              GameObjectAnimationLaw.RemapMissing(149, id => id is 0 or 146) is
                  { AnimationId: 146, Frozen: true },
            "GameObject missing rest-pose frozen remap drift");

        string root = ClientConfig.FindRepoRoot();
        string gameObjects = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Scene", "GameLoop.GameObjects.cs"));
        string gameObjectRender = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Scene", "GameLoop.GameObjectRender.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string instances = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Instances.cs"));
        string doodads = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Doodads",
            "DoodadRenderer.cs"));
        string casting = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Casting.cs"));
        string loot = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Loot.cs"));
        string controller = SourceText.Read(Path.Combine(root, "MSUIClient", "Player",
            "CharacterController.cs"));
        string sounds = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.GameObjectSounds.cs"));

        Check(net.Contains("case Op.SMSG_GAMEOBJECT_CUSTOM_ANIM:", StringComparison.Ordinal) &&
              net.Contains("case Op.SMSG_GAMEOBJECT_DESPAWN_ANIM:", StringComparison.Ordinal) &&
              net.Contains("case Op.SMSG_DESTROY_OBJECT:", StringComparison.Ordinal) &&
              net.Contains("ApplyGameObjectDespawnAnim(body);", StringComparison.Ordinal) &&
              net.Contains("ApplyDestroyObject(body);", StringComparison.Ordinal),
            "GameObject custom/despawn/destroy opcode dispatch drift");
        Check(gameObjects.Contains("body.Length != 12", StringComparison.Ordinal) &&
              gameObjects.Contains("body.Length != 8", StringComparison.Ordinal) &&
              gameObjects.Contains("GameObjectAnimationLaw.CustomAnimationId(animation)",
                  StringComparison.Ordinal) &&
              gameObjects.Contains("GameObjectAnimationLaw.Animates(go.GameObjectType)",
                  StringComparison.Ordinal) &&
              gameObjects.Contains("familyA && announced && placementPresent",
                  StringComparison.Ordinal) &&
              gameObjects.Contains("GameObjectAnimationLaw.DespawnAnimationId",
                  StringComparison.Ordinal) &&
              gameObjects.Contains("_entities.Remove(guid);", StringComparison.Ordinal),
            "GameObject strict packet/mapping/immediate authority removal drift");
        Check(doodads.Contains("TryPlayDynamicAnimation", StringComparison.Ordinal) &&
              doodads.Contains("[0, 153, 154, 155, 156, 157]", StringComparison.Ordinal) &&
              doodads.Contains("TryApplyDynamicStateAnimation", StringComparison.Ordinal) &&
              doodads.Contains("StateAnimation", StringComparison.Ordinal) &&
              // Stateful GameObject models leave instancing per-model (a closed and
              // an open copy of one crate cannot share the one instanced VBO pose)
              // and draw through the per-instance pass instead of a global toggle.
              doodads.Contains("_animatedGoModels.Contains(model)", StringComparison.Ordinal) &&
              doodads.Contains("RenderNonInstanced(", StringComparison.Ordinal) &&
              doodads.Contains("UpdateAnimatedVertices(model, instance)", StringComparison.Ordinal) &&
              doodads.Contains("FindOrBake(oneShot.AnimationId)", StringComparison.Ordinal),
            "Doodad state pose/exact one-shot/per-instance rendering handoff drift");
        Check(gameObjectRender.Contains("UpdateGameObjectStateAnimations();",
                  StringComparison.Ordinal) &&
              gameObjectRender.Contains("state.LastWire != wire", StringComparison.Ordinal) &&
              gameObjectRender.Contains("TryApplyDynamicStateAnimation", StringComparison.Ordinal) &&
              gameObjectRender.Contains("ProbeStatefulGameObjectCollision", StringComparison.Ordinal) &&
              gameObjectRender.Contains("CollisionFollowsState(e.GameObjectType)",
                  StringComparison.Ordinal) &&
              controller.Contains("DynamicCollisionProbe", StringComparison.Ordinal) &&
              controller.Contains("RaycastGeometry", StringComparison.Ordinal),
            "GameObject state observer/live door collision ownership drift");
        Check(doodads.Contains("out double playbackSeconds", StringComparison.Ordinal) &&
              doodads.Contains("StateAnimation is { } state", StringComparison.Ordinal) &&
              sounds.Contains("previous.Sequence != sequence", StringComparison.Ordinal) &&
              sounds.Contains("currentAnimationClock", StringComparison.Ordinal),
            "GameObject currently-armed event sequence/local-clock handoff drift");
        Check(casting.Contains("effect is 33 or 59", StringComparison.Ordinal) &&
              casting.Contains("PredictGameObjectAnimationState(goTarget",
                  StringComparison.Ordinal) &&
              loot.Contains("PredictGameObjectAnimationState(source",
                  StringComparison.Ordinal) &&
              loot.Contains("PredictGameObjectAnimationState(guid",
                  StringComparison.Ordinal),
            "GameObject open-lock/loot-release lid prediction drift");
        Check(gameObjectRender.Contains("_gameObjectRetainedDestroys", StringComparison.Ordinal) &&
              gameObjectRender.Contains("GameObjectAnimationLaw.RetentionFinished",
                  StringComparison.Ordinal) &&
              gameObjectRender.Contains("!_gameObjectRetainedDestroys.ContainsKey(guid)",
                  StringComparison.Ordinal) &&
              instances.Contains("_gameObjectDespawnAnimations.Clear();",
                  StringComparison.Ordinal) &&
              instances.Contains("_gameObjectAnimationStates.Clear();",
                  StringComparison.Ordinal) &&
              instances.Contains("_gameObjectRetainedDestroys.Clear();",
                  StringComparison.Ordinal),
            "GameObject retained placement expiry/reset fence drift");

        CheckActualCrate(root);
    }

    private static void CheckActualCrate(string root)
    {
        string data = ClientDataRoot.Path;
        if (!Directory.Exists(data)) return;
        using var mpq = new MpqMount(data);
        byte[] bytes = mpq.ReadFile(@"World\Goober\G_Crate01.m2") ??
            throw new InvalidDataException("actual G_Crate01 state-animation fixture unavailable");
        M2Model model = M2Reader.Parse(bytes) ??
            throw new InvalidDataException("actual G_Crate01 did not parse");
        M2Animator animator = M2Animator.Build(model, [], includeStaticSequences: true) ??
            throw new InvalidDataException("actual G_Crate01 has no animator");

        M2Animator.Clip Clip(int id) => animator.FindOrBake(
            id, includeStaticSequences: true) ??
            throw new InvalidDataException($"actual G_Crate01 does not own animation {id}");
        int[] family =
        [
            GameObjectAnimationLaw.CloseAnimationId,
            GameObjectAnimationLaw.ClosedAnimationId,
            GameObjectAnimationLaw.OpenAnimationId,
            GameObjectAnimationLaw.OpenedAnimationId,
        ];
        M2Animator.Clip[] clips = family.Select(Clip).ToArray();
        Check(clips.All(clip => clip.Looping && clip.DurationSeconds > 0f),
            "actual G_Crate01 door family no longer authors four looping windows");

        static (float Min, float Max) LidAngle(M2Animator.Clip clip)
        {
            float[] values = clip.Bones[8].RotationKeys
                .Select(rotation => 2f * MathF.Acos(
                    Math.Clamp(MathF.Abs(rotation.W), 0f, 1f))).ToArray();
            if (values.Length == 0)
                throw new InvalidDataException(
                    $"actual G_Crate01 animation {clip.AnimationId} has no lid rotation");
            return (values.Min(), values.Max());
        }

        (float closeMin, float closeMax) = LidAngle(Clip(GameObjectAnimationLaw.CloseAnimationId));
        (float openMin, float openMax) = LidAngle(Clip(GameObjectAnimationLaw.OpenAnimationId));
        (float closedMin, float closedMax) = LidAngle(Clip(GameObjectAnimationLaw.ClosedAnimationId));
        (float openedMin, float openedMax) = LidAngle(Clip(GameObjectAnimationLaw.OpenedAnimationId));
        Check(closeMin < .01f && closeMax > 1f &&
              openMin < .01f && openMax > 1f &&
              closedMax < .01f && openedMin > 1f,
            $"actual G_Crate01 lid motion/rest endpoints drift: " +
            $"close={closeMin:R}..{closeMax:R};open={openMin:R}..{openMax:R};" +
            $"closed={closedMin:R}..{closedMax:R};opened={openedMin:R}..{openedMax:R}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
