using System.Numerics;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.World.Units;

internal static class ItemGlowClinicalChecks
{
    public static void Run()
    {
        ItemVisualCatalog visuals = ItemVisualCatalog.FromRows(
        [
            (25u, new string?[] { null, null, null, "base.mdx", null }),
            (61u, new string?[] { null, null, null, "enchant.mdx", null }),
        ]);
        EnchantCatalog enchants = EnchantCatalog.FromRows(
        [
            new EnchantInfo(1, "Rockbiter", 0, 61),
            new EnchantInfo(7, "Base-shaped", 0, 25),
            new EnchantInfo(999, "No visual", 0, 0),
        ]);
        Check(ItemGlowLaw.EffectiveVisual(visuals, enchants, 25, [1u]) == 25 &&
              ItemGlowLaw.EffectiveVisual(visuals, enchants, 0, [999u, 1u]) == 61 &&
              ItemGlowLaw.EffectiveVisual(visuals, null, -1, [1u]) == 0,
            "intrinsic-wins/first-enchant/signed item visual fork drift");

        var model = new M2Model();
        model.Attachments.Add(new M2Attachment { Id = 99, Position = new Vector3(1, 2, 3) });
        model.AttachmentLookup.Add(-1);
        model.AttachmentLookup.Add(-1);
        model.AttachmentLookup.Add(0);
        Check(ItemGlowLaw.AttachmentPosition(model, 2) == new Vector3(1, 2, 3) &&
              ItemGlowLaw.AttachmentPosition(model, 0) is null &&
              ItemGlowLaw.AttachmentPosition(model, 4) is null,
            "item glow attachment lookup-only/miss suppression drift");
        Check(new MSUIClient.World.Units.ItemGlowPlacement(
                  "glow", "glow.m2", Matrix4x4.Identity).RenderMesh &&
              !new MSUIClient.World.Units.ItemGlowPlacement(
                  "item", "item.m2", Matrix4x4.Identity, RenderMesh: false).RenderMesh,
            "attached item effect mesh-suppression contract drift");

        var cameraFacing = new M2Model();
        cameraFacing.Bones.Add(new M2Bone
        {
            Flags = AttachedItemBillboardLaw.BillboardMask,
            ParentBone = -1,
            Pivot = new Vector3(1, 2, 3),
        });
        cameraFacing.Vertices.Add(new M2Vertex
        {
            BoneWeight0 = 255,
            BoneIndex0 = 0,
        });
        var inheritedFacing = new M2Model();
        inheritedFacing.Bones.Add(new M2Bone
        {
            Flags = AttachedItemBillboardLaw.BillboardMask,
            ParentBone = -1,
        });
        inheritedFacing.Bones.Add(new M2Bone { ParentBone = 0 });
        inheritedFacing.Vertices.Add(new M2Vertex
        {
            BoneWeight0 = 255,
            BoneIndex0 = 1,
        });
        var rigid = new M2Model();
        rigid.Bones.Add(new M2Bone { ParentBone = -1 });
        rigid.Vertices.Add(new M2Vertex { BoneWeight0 = 255, BoneIndex0 = 0 });
        var itemPalette = new Matrix4x4[M2Animator.MaxBones];
        int itemBones = AttachedItemBillboardLaw.PreparePalette(cameraFacing, true,
            Matrix4x4.Identity, new Vector3(0, 0, 5), Vector3.UnitX, itemPalette);
        Check(AttachedItemBillboardLaw.UsesCameraFacingPalette(cameraFacing) &&
              AttachedItemBillboardLaw.UsesCameraFacingPalette(inheritedFacing) &&
              !AttachedItemBillboardLaw.UsesCameraFacingPalette(rigid) &&
              itemBones == 1 && itemPalette[0] != Matrix4x4.Identity &&
              AttachedItemBillboardLaw.PreparePalette(rigid, false, Matrix4x4.Identity,
                  Vector3.Zero, -Vector3.UnitZ, itemPalette) == 0,
            "attached-item camera-facing palette selection drift");

        var materialModel = new M2Model();
        var color = new M2ColorAnimation();
        color.Color.Timestamps.Add(0);
        color.Color.Keys.Add(new Vector3(.25f, .5f, .75f));
        color.Alpha.Timestamps.Add(0);
        color.Alpha.Keys.Add((short)MathF.Round(.5f * 32767f));
        materialModel.Colors.Add(color);
        var weight = new M2AnimTrack<short>();
        weight.Timestamps.Add(0);
        weight.Keys.Add((short)MathF.Round(.6f * 32767f));
        materialModel.TransparencyTracks.Add(weight);
        materialModel.TransparencyLookup.Add(0);
        var transform = new M2TextureTransform();
        transform.Translation.Timestamps.Add(0);
        transform.Translation.Keys.Add(new Vector3(.125f, -.25f, 99f));
        materialModel.TextureTransforms.Add(transform);
        materialModel.TextureTransformLookup.Add(0);
        var materialBatch = new M2Batch
        {
            ColorIndex = 0,
            TextureWeightIndex = 0,
            TextureCount = 1,
            TextureTransformIndex = 0,
        };
        AttachedItemMaterialLaw.Sample material = AttachedItemMaterialLaw.At(
            materialModel, materialBatch, 9f);
        AttachedItemMaterialLaw.Sample defaultMaterial = AttachedItemMaterialLaw.At(
            materialModel, null, 9f);
        Check(Vector3.DistanceSquared(material.Tint, new Vector3(.25f, .5f, .75f)) <
                  .000001f &&
              MathF.Abs(material.Alpha - .3f) < .0001f &&
              material.UvOffset == new Vector2(.125f, -.25f) &&
              material.Visible && material.Translucent &&
              defaultMaterial == new AttachedItemMaterialLaw.Sample(
                  Vector3.One, 1f, Vector2.Zero) &&
              AttachedItemMaterialLaw.FogPolicy(3, false) == 1 &&
              AttachedItemMaterialLaw.FogPolicy(5, false) == 2 &&
              AttachedItemMaterialLaw.FogPolicy(6, false) == 3 &&
              AttachedItemMaterialLaw.FogPolicy(0, true) == 4,
            "attached-item authored colour-alpha/weight/UV combine drift");

        string root = ClientConfig.FindRepoRoot();
        string attached = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "AttachedItemRenderer.cs"));
        string effects = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "SpellEffectSource.cs"));
        string effectMeshes = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "SpellEffectMeshRenderer.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string creature = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CreatureRenderer.cs"));
        string portraits = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.Portraits.cs"));
        string glueBooth = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine",
            "GlueBooth.cs"));
        string character = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CharacterRenderer.cs"));
        string attachedVertex = SourceText.Read(Path.Combine(root, "MSUIClient", "Shaders",
            "attached.vert"));
        string characterFragment = SourceText.Read(Path.Combine(root, "MSUIClient", "Shaders",
            "character.frag"));
        Check(attached.Contains("ItemGlowLaw.EffectiveVisual", StringComparison.Ordinal) &&
              attached.Contains("Matrix4x4.CreateTranslation(local) * itemRoot", StringComparison.Ordinal) &&
              attached.Contains("worldInstance.M41 += camera.Position.X", StringComparison.Ordinal) &&
              attached.Contains("AppendItemModelEffects(mount, itemRoot)",
                  StringComparison.Ordinal) &&
              attached.Contains("source.ParticleEmitters.Count == 0 && source.RibbonEmitters.Count == 0",
                  StringComparison.Ordinal) &&
              attached.Contains("RenderMesh: false", StringComparison.Ordinal) &&
              attached.Contains("FloatsPerVertex = 18", StringComparison.Ordinal) &&
              attached.Contains("AttachedItemBillboardLaw.PreparePalette(",
                  StringComparison.Ordinal) &&
              attached.Contains("shader.SetVec4Array(\"uBones\"",
                  StringComparison.Ordinal) &&
              attached.Contains("AttachedItemMaterialLaw.At(", StringComparison.Ordinal) &&
              attached.Contains("BodyTint * material.Tint", StringComparison.Ordinal) &&
              attached.Contains("bodyAlpha * material.Alpha", StringComparison.Ordinal) &&
              attached.Contains("uUvOffset", StringComparison.Ordinal) &&
              attached.Contains("batch.NoZTest", StringComparison.Ordinal) &&
              attached.Contains("uUnlit", StringComparison.Ordinal) &&
              attached.Contains("uFogPolicy", StringComparison.Ordinal) &&
              attached.Contains("!m2.IsBatchConstantInvisible", StringComparison.Ordinal) &&
              attached.Contains("var visibleBatches = m2.Batches", StringComparison.Ordinal) &&
              attached.Contains("textureRef.Filename.Length > 0", StringComparison.Ordinal) &&
              attached.Contains("m2.UsesEnvironmentMapForBatch(batch)", StringComparison.Ordinal) &&
              !attached.Contains("suppressedEffectPasses", StringComparison.Ordinal) &&
              attached.Contains("model.Batches.Count == 0 && m2.Batches.Count == 0",
                  StringComparison.Ordinal) &&
              attachedVertex.Contains("layout (location = 3) in vec4 aBoneWeights",
                  StringComparison.Ordinal) &&
              attachedVertex.Contains("uniform int uBoneCount", StringComparison.Ordinal) &&
              attachedVertex.Contains("uniform int uEnvironmentMap", StringComparison.Ordinal) &&
              attachedVertex.Contains("- 2.0 * dot(viewPosition, viewNormal) * viewNormal;", StringComparison.Ordinal) &&
              attachedVertex.Contains("vUV = mappedUV + (uEnvironmentMap != 0 ? vec2(0.0) : uUvOffset);", StringComparison.Ordinal) &&
              characterFragment.Contains("if (uUnlit == 0)", StringComparison.Ordinal) &&
              characterFragment.Contains("uFogPolicy == 4 ? 0.0", StringComparison.Ordinal) &&
              effects.Contains("SyncItemGlows", StringComparison.Ordinal) &&
              effects.Contains("item-glow:{asset.Path}#{glow.Id}", StringComparison.Ordinal) &&
              effects.Contains("!glow.RenderMesh || !asset.Model.IsValid",
                  StringComparison.Ordinal) &&
              effectMeshes.Contains("GetTextureTransformForBatch(source)",
                  StringComparison.Ordinal) &&
              effectMeshes.Contains("vUV + uUvOffset", StringComparison.Ordinal) &&
              program.Contains("_spellEffects.SyncItemGlows", StringComparison.Ordinal) &&
              creature.Contains("PlayerVisibleItemEnchant", StringComparison.Ordinal),
            "item/enchant glow attachment or shared effect-pipeline wiring drift");

        int portraitGlowStart = portraits.IndexOf(
            "private bool RenderPortraitItemGlows", StringComparison.Ordinal);
        int portraitBakeStart = portraits.IndexOf(
            "private void BakeDirtyPortraits", portraitGlowStart, StringComparison.Ordinal);
        Check(portraitGlowStart >= 0 && portraitBakeStart > portraitGlowStart,
            "item/enchant glow model-pane renderer slice missing");
        string portraitGlow = portraits[portraitGlowStart..portraitBakeStart];
        Check(portraits.Contains("private sealed class PortraitGlowLane", StringComparison.Ordinal) &&
              portraits.Contains("_paperDollGlow", StringComparison.Ordinal) &&
              portraits.Contains("_inspectPaperDollGlow", StringComparison.Ordinal) &&
              portraits.Contains("_dressUpGlow", StringComparison.Ordinal) &&
              portraits.Contains("_creatures.BeginItemGlowFrame();", StringComparison.Ordinal) &&
              portraits.Contains("PlayerVisibleItemEnchant(slot, enchantSlot)",
                  StringComparison.Ordinal) &&
              portraitGlow.Contains("lane.Source.SyncItemGlows", StringComparison.Ordinal) &&
              portraitGlow.Contains("lane.Particles.Simulate", StringComparison.Ordinal) &&
              portraitGlow.Contains("lane.Particles.Render(camera)", StringComparison.Ordinal) &&
              portraitGlow.Contains("lane.Ribbons.Render", StringComparison.Ordinal) &&
              portraitGlow.Contains("placement.RenderMesh", StringComparison.Ordinal) &&
              portraitGlow.Contains("meshes.FogEnabled = false", StringComparison.Ordinal) &&
              !portraitGlow.Contains("ImGui.", StringComparison.Ordinal) &&
              !portraitGlow.Contains("new Vector2", StringComparison.Ordinal),
            "isolated live Character/Inspect/DressUp item-glow booth wiring drift");

        Check(!portraits.Contains("RenderPortraitItemGlows(ref _playerPortrait",
                  StringComparison.Ordinal) &&
              !portraits.Contains("RenderPortraitItemGlows(ref _targetPortrait",
                  StringComparison.Ordinal) &&
              !portraits.Contains("RenderPortraitItemGlows(ref _petPortrait",
                  StringComparison.Ordinal),
            "cached round unit portraits must not inherit live item-glow simulation");

        Check(glueBooth.Contains("equipmentSlot: i", StringComparison.Ordinal) &&
              glueBooth.Contains("r.SheathState = 1", StringComparison.Ordinal) &&
              glueBooth.Contains("private sealed class ItemEffectLane", StringComparison.Ordinal) &&
              glueBooth.Contains("_itemEffects.Source.SyncItemGlows", StringComparison.Ordinal) &&
              glueBooth.Contains("_itemEffects.Particles.Render(_cam)", StringComparison.Ordinal) &&
              glueBooth.Contains("_itemEffects.Ribbons.Render", StringComparison.Ordinal),
            "character-select melee-slot/ranged-skip or isolated item-effect lane drift");
        Check(character.Contains("_characterGeosets?.Visible(", StringComparison.Ordinal) &&
              character.Contains("BuildEquipGeosets()", StringComparison.Ordinal) &&
              character.Contains("HelmetGeosetVisTable.Parse", StringComparison.Ordinal),
            "booth character must share the faithful robe/helm geoset engine");

        CheckCharacterGeosetParity();

        CheckActualDataIfPresent(root);
    }

    private static void CheckCharacterGeosetParity()
    {
        CharHairGeosetsTable hair = CharHairGeosetsTable.Parse(Dbc([1, 1, 0, 7, 5])) ??
            throw new InvalidDataException("synthetic CharHairGeosets did not parse");
        HelmetGeosetVisTable helmet = HelmetGeosetVisTable.Parse(
            Dbc([42, 1u << 1, 0, 0, 0, 0])) ??
            throw new InvalidDataException("synthetic HelmetGeosetVisData did not parse");
        var geosets = new CharacterGeosets(hair, null, helmet);

        HashSet<int> helmed = geosets.Visible(1, 0, 7, 0,
            new EquipGeosets { HelmVis = (42, 42) });
        Check(helmed.Contains(1) && !helmed.Contains(5),
            "HelmetGeosetVisData must select the base scalp, not delete hair geometry");

        var robe = new ItemDisplayRow { GeosetGroup = [0, 0, 1] };
        var boots = new ItemDisplayRow { GeosetGroup = [2, 0, 0] };
        var tabard = new ItemDisplayRow { GeosetGroup = [1, 0, 0] };
        var equip = new EquipGeosets();
        equip.Bodyslots[1] = robe;
        equip.Bodyslots[4] = boots;
        equip.Bodyslots[7] = tabard;
        HashSet<int> robed = geosets.Visible(1, 0, 7, 0, equip);
        Check(robed.Contains(1302) &&
              !robed.Any(id => id is >= 501 and <= 599) &&
              robed.Contains(1201) && !robed.Contains(1202),
            "robe must hide boot and tabard geometry while selecting its skirt");
    }

    private static byte[] Dbc(params uint[][] rows)
    {
        int fields = rows.Length == 0 ? 0 : rows[0].Length;
        byte[] data = new byte[20 + rows.Length * fields * 4 + 1];
        data[0] = (byte)'W'; data[1] = (byte)'D'; data[2] = (byte)'B'; data[3] = (byte)'C';
        BitConverter.GetBytes(rows.Length).CopyTo(data, 4);
        BitConverter.GetBytes(fields).CopyTo(data, 8);
        BitConverter.GetBytes(fields * 4).CopyTo(data, 12);
        BitConverter.GetBytes(1).CopyTo(data, 16);
        int offset = 20;
        foreach (uint[] row in rows)
            foreach (uint value in row)
            {
                BitConverter.GetBytes(value).CopyTo(data, offset);
                offset += 4;
            }
        return data;
    }

    private static void CheckActualDataIfPresent(string root)
    {
        string data = ClientDataRoot.Path;
        if (!Directory.Exists(data)) return;
        ItemVisualCatalog visuals = ItemVisualCatalog.Load(data) ??
            throw new InvalidDataException("ItemVisuals chain unavailable");
        EnchantCatalog enchants = EnchantCatalog.Load(data) ??
            throw new InvalidDataException("SpellItemEnchantment unavailable");
        Check(visuals.Count == 34 &&
              enchants.Rows.Count(row => row.VisualId != 0) == 102 &&
              enchants.Visual(1) == 61 &&
              visuals.Effects(61)?[3]?.Contains("Enchantments", StringComparison.OrdinalIgnoreCase) == true,
            "actual build-5875 item/enchant visual chain drift");
        Check(ItemGlowLaw.EffectiveVisual(visuals, enchants, 0, [999u, 1u]) == 61,
            "first visual-bearing actual enchant selection drift");

        using var mpq = new MpqMount(data);
        byte[] torchBytes = mpq.ReadFile(
            @"Item\ObjectComponents\Weapon\Club_1H_Torch_A_01.m2") ??
            throw new InvalidDataException("actual torch item model unavailable");
        M2Model torch = M2Reader.Parse(torchBytes) ??
            throw new InvalidDataException("actual torch item model did not parse");
        Check(torch.ParticleEmitters.Count > 0,
            "actual equipped torch no longer anchors the item-authored emitter lane");
        Check(AttachedItemBillboardLaw.UsesCameraFacingPalette(torch),
            "actual equipped torch no longer exercises the camera-facing item batch lane");

        byte[] guardSwordBytes = mpq.ReadFile(
            @"Item\ObjectComponents\Weapon\Sword_1H_Long_A_02.m2") ??
            throw new InvalidDataException("actual Stormwind guard sword unavailable");
        M2Model guardSword = M2Reader.Parse(guardSwordBytes) ??
            throw new InvalidDataException("actual Stormwind guard sword did not parse");
        Check(guardSword.TextureUnitLookup.SequenceEqual(new short[] { 0, -1 }) &&
              guardSword.Batches.Any(guardSword.UsesEnvironmentMapForBatch),
            "actual Stormwind guard sword environment-map lookup/pass drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
