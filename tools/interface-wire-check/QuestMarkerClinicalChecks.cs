using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;

internal static class QuestMarkerClinicalChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    public static void Run()
    {
        M2Model body = BodyWithAttachments(18, 29);
        Check(QuestMarkerModelLaw.Attachment(body, mounted: false) is
                  { ResolvedId: 18, WasFallback: false } &&
              QuestMarkerModelLaw.Attachment(body, mounted: true) is
                  { ResolvedId: 29, WasFallback: false },
            "TalkToMe attachment 18/29 selection drift");

        M2Model onFootOnly = BodyWithAttachments(18);
        Check(QuestMarkerModelLaw.Attachment(onFootOnly, mounted: true) is
                  { ResolvedId: 18, WasFallback: false },
            "mounted TalkToMe marker must fall back specifically to authored attachment 18");

        M2Model spellFallbackOnly = BodyWithAttachments(15, 19);
        Check(QuestMarkerModelLaw.Attachment(spellFallbackOnly, mounted: false) is null &&
              QuestMarkerModelLaw.Attachment(spellFallbackOnly, mounted: true) is null,
            "TalkToMe marker must not inherit spell-effect attachment fallbacks");

        Matrix4x4 joint = Matrix4x4.CreateScale(2f) *
                          Matrix4x4.CreateTranslation(10f, 20f, 30f);
        float counter = QuestMarkerModelLaw.CounterScale(joint) ?? 0f;
        Matrix4x4 seat = QuestMarkerModelLaw.SeatTransform(joint, counter);
        Check(MathF.Abs(counter - .5f) < .0001f &&
              Vector3.TransformNormal(Vector3.UnitX, seat).Length() is > .9999f and < 1.0001f &&
              seat.Translation == new Vector3(10f, 20f, 30f) &&
              QuestMarkerModelLaw.CounterScale(Matrix4x4.Identity) == 1f,
            "TalkToMe one-time 1/L seat counter-scale drift");

        var marker = new M2Model();
        marker.Sequences.Add(new M2Sequence
            { AnimationId = 0, VariationId = 0, StartTimestamp = 0, EndTimestamp = 1000 });
        marker.Sequences.Add(new M2Sequence
            { AnimationId = 190, VariationId = 0, StartTimestamp = 1000, EndTimestamp = 2000 });
        Check(QuestMarkerModelLaw.SequenceIndex(marker, raised: false) == 0 &&
              QuestMarkerModelLaw.SequenceIndex(marker, raised: true) == 1,
            "TalkToMe low/raised animation selection drift");

        var lowOnly = new M2Model();
        lowOnly.Sequences.Add(new M2Sequence
            { AnimationId = 0, VariationId = 0, StartTimestamp = 0, EndTimestamp = 1000 });
        Check(QuestMarkerModelLaw.SequenceIndex(lowOnly, raised: true) == 0,
            "TalkToMe missing raised band must retain the low bob");

        string adapter = SourceText.Read(Path.Combine("MSUIClient", "GameLoop", "Hud",
            "GameLoop.QuestMarkers.cs"));
        // A blanket ban on "ImGui" used to stand here. The adapter now draws party member
        // name labels above quest givers, which is a Command View instrument: "from the
        // sky they are the only way to say WHO has business where. Embodied play (direct
        // control included) keeps the plain vanilla markers and nothing else" (owner,
        // 2026-08-28). The markers themselves are still mesh instances.
        //
        // So assert the rule rather than the word: markers come from mesh instances, and
        // every ImGui draw sits behind the Free View gate, which is what keeps embodied
        // play free of projected HUD glyphs.
        int freeViewGate = adapter.IndexOf("if (!_freeView) return;", StringComparison.Ordinal);
        int firstImGuiDraw = adapter.IndexOf("ImGui.GetBackgroundDrawList()",
            StringComparison.Ordinal);
        Check(!adapter.Contains("DrawPlateText", StringComparison.Ordinal) &&
              adapter.Contains("QuestMarkerMeshInstances", StringComparison.Ordinal) &&
              freeViewGate >= 0 && firstImGuiDraw > freeViewGate,
            "quest marker adapter regressed to projected HUD glyph rendering, or its " +
            "Command View labels escaped the Free View gate");

        CheckRefreshLaw();

        CheckActualAssetsIfPresent();
    }

    private static M2Model BodyWithAttachments(params ushort[] ids)
    {
        var body = new M2Model();
        body.Bones.Add(new M2Bone { ParentBone = -1 });
        for (int i = 0; i <= ids.DefaultIfEmpty((ushort)0).Max(); i++)
            body.AttachmentLookup.Add(-1);
        foreach (ushort id in ids)
        {
            int index = body.Attachments.Count;
            body.Attachments.Add(new M2Attachment
                { Id = id, BoneIndex = 0, Position = new Vector3(0f, id, 0f) });
            body.AttachmentLookup[id] = (short)index;
        }
        return body;
    }

    private static void CheckRefreshLaw()
    {
        var fields = new ObjectFields();
        fields.SetU32(ObjectFields.UNIT_LEVEL, 30);
        fields.SetU32(ObjectFields.UNIT_HEALTH, 100);
        ulong baseline = QuestStatusRefreshLaw.PlayerGeneration(fields, 0);

        // Prove the last of all 128 slots participates, and that the watch is the
        // current-rank half specifically rather than max rank or temporary bonus.
        ushort skill = (ushort)(ObjectFields.PLAYER_SKILL_INFO_1_1 + 127 * 3);
        fields.SetU32(skill, 182);
        fields.SetU32((ushort)(skill + 1), 75u | (300u << 16));
        ulong rank75 = QuestStatusRefreshLaw.PlayerGeneration(fields, 0);
        fields.SetU32((ushort)(skill + 1), 75u | (301u << 16));
        fields.SetU32((ushort)(skill + 2), 25u);
        ulong maxAndBonusOnly = QuestStatusRefreshLaw.PlayerGeneration(fields, 0);
        fields.SetU32((ushort)(skill + 1), 76u | (301u << 16));
        ulong rank76 = QuestStatusRefreshLaw.PlayerGeneration(fields, 0);

        Check(baseline != rank75 && rank75 == maxAndBonusOnly && rank75 != rank76 &&
              fields.PlayerSkills().Single() == ((byte)127, (ushort)182, (ushort)76),
            "quest-status all-skill current-rank descriptor watch drift");
        Check(QuestStatusRefreshLaw.PlayerGeneration(fields, 1) != rank76,
            "quest-status packet epoch does not invalidate the generation");

        Op[] reasks =
        [
            Op.SMSG_SET_FACTION_STANDING,
            Op.SMSG_GROUP_LIST,
            Op.SMSG_QUESTGIVER_QUEST_COMPLETE,
            Op.SMSG_QUESTUPDATE_ADD_KILL,
            Op.SMSG_QUESTUPDATE_ADD_ITEM,
            Op.SMSG_QUESTUPDATE_COMPLETE,
            Op.SMSG_QUESTUPDATE_FAILED,
            Op.SMSG_QUESTUPDATE_FAILEDTIMER,
        ];
        Check(reasks.All(QuestStatusRefreshLaw.PacketReasks) &&
              !QuestStatusRefreshLaw.PacketReasks(Op.SMSG_INITIALIZE_FACTIONS) &&
              !QuestStatusRefreshLaw.PacketReasks(Op.SMSG_GROUP_INVITE) &&
              !QuestStatusRefreshLaw.PacketReasks(Op.SMSG_QUESTGIVER_QUEST_DETAILS),
            "quest-status packet re-ask family drift");

        string net = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient",
            "GameLoop", "Scene", "GameLoop.Net.cs"));
        Check(net.Contains("if (QuestStatusRefreshLaw.PacketReasks(packetOpcode))\n" +
                  "                    BumpQuestStatusReask();", StringComparison.Ordinal),
            "quest-status packet epoch is not wired after successful packet dispatch");
    }

    private static void CheckActualAssetsIfPresent()
    {
        string data = Path.Combine(ClientConfig.FindRepoRoot(), "GameData", "Data");
        if (!Directory.Exists(data)) return;

        string[] paths =
        [
            @"Interface\Buttons\TalkToMeGrey.m2",
            @"Interface\Buttons\TalkToMeQuestion_Grey.m2",
            @"Interface\Buttons\TalkToMeQuestion_LTBlue.m2",
            @"Interface\Buttons\TalkToMe.m2",
            @"Interface\Buttons\TalkToMeQuestionMark.m2",
            @"Interface\Buttons\TalkToMeGreen.m2",
        ];
        using var mpq = new MpqMount(data);
        foreach (string path in paths)
        {
            byte[]? bytes = mpq.ReadFile(path);
            M2Model? model = bytes is null ? null : M2Reader.Parse(bytes);
            Check(model is not null, $"actual TalkToMe asset did not parse: {path}");
            M2Model actual = model!;
            Check(actual.IsValid && actual.Bones.Count == 1 &&
                  actual.Batches.Count > 0 && actual.Submeshes.Count > 0 &&
                  actual.TryFindSequenceIndexByAnimationId(
                      QuestMarkerModelLaw.LowAnimationId) >= 0 &&
                  actual.TryFindSequenceIndexByAnimationId(
                      QuestMarkerModelLaw.RaisedAnimationId) >= 0,
                $"actual TalkToMe asset is missing its one-bone mesh or 0/190 bob: {path}");

            bool question = path.Contains("Question", StringComparison.OrdinalIgnoreCase);
            bool billboard = (actual.Bones[0].Flags & 0x78) != 0;
            Check(question == billboard,
                $"actual TalkToMe plain/billboard model classification drift: {path}");
        }
    }
}
