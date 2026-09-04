using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

internal static class FeedPetClinicalChecks
{
    public static void Run()
    {
        const ulong self = 0x1000;
        const ulong pet = 0x2000;
        const ulong item = 0x3000;
        Check(FeedPetLaw.FeedPetEffect == 0x65 &&
              FeedPetLaw.IsFeedPetEffects([0x65, 0, 0]) &&
              !FeedPetLaw.IsFeedPetEffects([0, 0x65, 0]) &&
              !FeedPetLaw.IsFeedPetEffects(null),
            "Feed Pet learn-time Effect[0] latch drift");
        Check(FeedPetLaw.CanFeed(pet, pet, 883, self, self, 6991, item) &&
              !FeedPetLaw.CanFeed(pet, pet, 0, self, self, 6991, item) &&
              !FeedPetLaw.CanFeed(pet, pet, 883, 1, self, 6991, item) &&
              !FeedPetLaw.CanFeed(pet, pet, 883, self, self, 0, item) &&
              !FeedPetLaw.CanFeed(pet, 9, 883, self, self, 6991, item),
            "Feed Pet ownership/provenance gates drift");

        var fields = new ObjectFields();
        fields.SetU32(ObjectFields.UNIT_CREATED_BY_SPELL, 883);
        fields.SetGuid(ObjectFields.UNIT_FIELD_CREATEDBY, self);
        Check(ObjectFields.UNIT_CREATED_BY_SPELL == 146 && fields.CreatedBySpell == 883 &&
              fields.CreatedBy == self,
            "Feed Pet descriptor gates are not exposed from build-5875 unit fields");

        var reader = new PacketReader(WorldSession.BuildCastSpellOnItemBody(6991, item));
        Check(reader.ReadU32() == 6991 && reader.ReadU16() == 0x0010 &&
              reader.ReadPackedGuid() == item && reader.Remaining == 0,
            "Feed Pet must use bare CMSG_CAST_SPELL with TARGET_FLAG_ITEM");

        string root = ClientConfig.FindRepoRoot();
        string petSource = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Pet.cs"));
        string targetSource = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Combat", "GameLoop.Targeting.cs"));
        int send = petSource.IndexOf("_net.CastSpellOnItem(spellId, item.Guid)",
            StringComparison.Ordinal);
        int clear = petSource.IndexOf("ClearCarriedItem();", send, StringComparison.Ordinal);
        Check(petSource.Contains("FeedPetLaw.CanFeed", StringComparison.Ordinal) &&
              petSource.Contains("if (HasCarriedItem) TryFeedCarriedItemToPet(pet)",
                  StringComparison.Ordinal) && send >= 0 && clear > send &&
              targetSource.Contains("picked == _petGuid", StringComparison.Ordinal) &&
              targetSource.Contains("TryFeedCarriedItemToPet(pickedPet)", StringComparison.Ordinal),
            "PetFrame/world drop seams or success-only cursor clear drift");

        string data = ClientDataRoot.Path;
        if (Directory.Exists(data))
        {
            using var mpq = new MpqMount(data);
            SpellCatalog spells = SpellCatalog.Load(mpq) ??
                throw new InvalidDataException("Spell.dbc unavailable for Feed Pet fixture");
            Check(spells.TryGet(6991, out SpellInfo feed) && FeedPetLaw.IsFeedPetSpell(feed),
                "shipped Feed Pet 6991 no longer carries effect 0x65 in lane zero");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
