using System.Text;
using System.Security.Cryptography;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.World.Units;

internal static class CharacterSkinExtraClinicalChecks
{
    public static void Run(string? dataPath)
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
            count++;
        }
        var table = CharSectionsTable.Parse(Fixture())!;
        Check(table.SkinExtraTexture(6, 0, 0) == "male-extra", "Skin-extra did not select skin Texture2");
        Check(table.SkinExtraTexture(6, 1, 0) == "female-extra", "Sex-specific skin-extra selection drift");
        Check(table.SkinExtraTexture(6, 0, 1) == "male-extra-1", "Skin colour did not select its own extra sheet");
        Check(table.SkinExtraTexture(6, 0, -1) == "", "Invalid skin colour became an any-colour lookup");
        Check(table.SkinExtraTexture(6, 0, 2) == "", "Unknown skin colour borrowed another colour");
        Check(table.SkinExtraTexture(1, 0, 0) == "", "Empty human extra sheet fell back to the body skin");
        Check(table.SkinExtraTexture(5, 0, 0) == "", "Missing row fabricated a skin-extra source");
        Check(table.SkinExtraTexture(9, 0, 0) == "different-race-extra", "Routing was hardcoded to Tauren");

        using var mpq = new MpqMount(dataPath ?? Path.Combine(ClientConfig.FindRepoRoot(), "GameData", "Data"));
        var actual = CharSectionsTable.Parse(mpq.ReadFile(CharSectionsTable.MpqPath)!)!;
        string[] expected = [
            @"Character\Tauren\Male\TaurenMaleSkin00_00_Extra.blp",
            @"Character\Tauren\Female\TaurenFemaleSkin00_00_Extra.blp"];
        string[] hashes = ["6f2e884192cd3273b1d02f2ab5be78db0bac4f5ff5738a9be8070ec8121cf283",
            "f075d8b2d91841cba0e23680225dd58df7ad3025ebd869550d02c8b16503f970"];
        for (uint sex = 0; sex < 2; sex++)
        {
            string path = actual.SkinExtraTexture(6, sex, 0);
            Check(path == expected[sex], "Original mounted skin-extra declaration changed");
            byte[] extraBytes = mpq.ReadFile(path)!;
            Check(Convert.ToHexString(SHA256.HashData(extraBytes)).ToLowerInvariant() == hashes[sex],
                "Original mounted extra texture bytes changed; re-audit provenance");
            byte[] extra = BlpDecoder.GetPixels(extraBytes, 0, out int ew, out int eh);
            Check(ew == 128 && eh == 128, "Expected original 128-square extra texture");
            string gender = sex == 0 ? "Male" : "Female";
            M2Model model = M2Reader.Parse(mpq.ReadFile($@"Character\Tauren\{gender}\Tauren{gender}.m2")!)!;
            uint Type(M2Batch batch) => model.Textures[model.TextureLookup[batch.TextureIndex]].Type;
            int sharedGeoset = sex == 0 ? 1501 : 0;
            var group15 = model.Batches.Where(b => model.Submeshes[b.SubmeshIndex].Id == sharedGeoset).ToArray();
            Check(group15.Any(b => Type(b) == 8) && group15.Any(b => Type(b) == 1),
                "Extra surfaces and dressed body must retain separate texture sources within a shared geoset");
            string bodyPath = actual.Find(6, sex, CharSectionsTable.SectionSkin, -1, 0)!.Texture1;
            byte[] body = BlpDecoder.GetPixels(mpq.ReadFile(bodyPath)!, 0, out int bw, out int bh);
            var kit = new CharacterEquipment();
            kit.Add("test torso", 1, CharacterEquipment.Slot.Chest);
            var row = new ItemDisplayRow();
            Array.Fill(row.BodyTextures, "");
            row.BodyTextures[3] = "test-torso";
            kit.Pieces[0].Row = row;
            byte[] dressed = kit.Composite(body, bw, bh, _ => (Enumerable.Repeat((byte)127, 128 * 64 * 4).ToArray(), 128, 64), false);
            Check(!dressed.SequenceEqual(body), "Type1 body lost equipment compositing");
            Check(actual.SkinExtraTexture(6, sex, 0) == path && extra.SequenceEqual(BlpDecoder.GetPixels(mpq.ReadFile(path)!, 0, out _, out _)),
                "Equipment altered the independent extra source");
        }

        // Keep every renderer route on the same tested selector. Actual GL binding
        // is verified separately by the paired native capture, never inferred here.
        string root = ClientConfig.FindRepoRoot();
        string controlled = SourceText.Read(Path.Combine(root, "MSUIClient/World/Units/CharacterRenderer.cs"));
        Check(controlled.Split("8 => skinExtraPath").Length == 3 && controlled.Split("_charSections.SkinExtraTexture(").Length == 3,
            "Controlled sync/async source bindings diverged");
        string remote = SourceText.Read(Path.Combine(root, "MSUIClient/World/Units/CreatureRenderer.cs"));
        Check(remote.Contains("case 8:\n                return NpcSkinExtraTextureCandidates(info);", StringComparison.Ordinal) &&
            remote.Contains("_charSections.SkinExtraTexture(info.ExtRace, info.ExtSex, (int)info.ExtSkin)", StringComparison.Ordinal),
            "Remote player/NPC no longer uses the independent extra source");
        Check(remote.Contains("else if (textureType != 8) preparedTexture = carriedTexture;", StringComparison.Ordinal) &&
            remote.Contains("else if (reference.Type != 8) texture = carriedTexture;", StringComparison.Ordinal),
            "Missing type8 must never borrow the preceding dressed batch texture");
        Console.WriteLine($"interface-wire-check: CharacterSkinExtra PASS ({count} checks)");
    }

    private static byte[] Fixture()
    {
        (uint race, uint sex, uint section, uint color, string body, string extra)[] rows = [
            (6, 0, 4, 0, "wrong-underwear", "wrong-underwear-extra"),
            (6, 0, 3, 0, "wrong-hair", "wrong-hair-extra"),
            (6, 0, 0, 0, "male-body", "male-extra"),
            (6, 1, 0, 0, "female-body", "female-extra"),
            (6, 0, 0, 1, "male-body-1", "male-extra-1"),
            (1, 0, 0, 0, "human-body", ""),
            (9, 0, 0, 0, "different-race-body", "different-race-extra")];
        using var strings = new MemoryStream(); strings.WriteByte(0);
        uint Put(string text) { uint offset = (uint)strings.Position; strings.Write(Encoding.UTF8.GetBytes(text)); strings.WriteByte(0); return offset; }
        using var records = new MemoryStream(); using var rw = new BinaryWriter(records);
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            foreach (uint field in new uint[] { (uint)i + 1, r.race, r.sex, r.section, 0, r.color, Put(r.body), Put(r.extra), 0, 0 }) rw.Write(field);
        }
        using var result = new MemoryStream(); using var writer = new BinaryWriter(result);
        writer.Write(Encoding.ASCII.GetBytes("WDBC")); writer.Write(rows.Length); writer.Write(10); writer.Write(40); writer.Write((int)strings.Length);
        writer.Write(records.ToArray()); writer.Write(strings.ToArray()); return result.ToArray();
    }
}
