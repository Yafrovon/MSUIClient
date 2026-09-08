using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

internal static class ChatLanguageClinicalChecks
{
    public static void Run()
    {
        Check(ChatLanguageLaw.EffectiveLanguage(ChatFrameLaw.MsgType.Say, 1, true, 0) == 1 &&
              ChatLanguageLaw.EffectiveLanguage(ChatFrameLaw.MsgType.MonsterSay, 7, true, 0) == 7 &&
              ChatLanguageLaw.EffectiveLanguage(ChatFrameLaw.MsgType.Emote, 1, true, 0) == 0 &&
              ChatLanguageLaw.EffectiveLanguage(ChatFrameLaw.MsgType.System, 1, true, 0) == 0 &&
              ChatLanguageLaw.EffectiveLanguage(ChatFrameLaw.MsgType.MonsterEmote, 1, true, 0) == 0 &&
              ChatLanguageLaw.EffectiveLanguage(ChatFrameLaw.MsgType.RaidBossEmote, 1, true, 0) == 0 &&
              ChatLanguageLaw.EffectiveLanguage(ChatFrameLaw.MsgType.Say, 1, false, 0) == 0 &&
              ChatLanguageLaw.EffectiveLanguage(ChatFrameLaw.MsgType.Say, 1, true, 0x8) == 0,
            "chat language universal/no-player/GM gate drift");
        Check(ChatLanguageLaw.DefaultLanguage(1) == 7 && ChatLanguageLaw.DefaultLanguage(4) == 7 &&
              ChatLanguageLaw.DefaultLanguage(2) == 1 && ChatLanguageLaw.DefaultLanguage(8) == 1 &&
              ChatLanguageLaw.Name(33) == "Gutterspeak" &&
              ChatLanguageLaw.Header(6, 7) == "[Dwarvish] " &&
              ChatLanguageLaw.Header(7, 7) == "" && ChatLanguageLaw.Header(0, 7) == "",
            "chat language names, faction tongue, or header suppression drift");
        Check(ChatFrameLaw.FormatLine(ChatFrameLaw.MsgType.Say, "Alice", "", "hello", 0, 6, 7) ==
                  "|Hplayer:Alice|h[Alice]|h says: [Dwarvish] hello" &&
              ChatFrameLaw.FormatLine(ChatFrameLaw.MsgType.MonsterEmote, "Thrall", "",
                  "%s roars.", 0, 0, 1) == "Thrall roars." &&
              ChatFrameLaw.IgnoredSender(false, ChatFrameLaw.MsgType.Say, uint.MaxValue) ==
                  ChatFrameLaw.IgnoredSenderAction.Drop,
            "language header placement, narration suppression, or LANG_ADDON drop drift");

        var fields = new ObjectFields();
        fields.SetU32(ObjectFields.PLAYER_SKILL_INFO_1_1, 98);
        fields.SetU32((ushort)(ObjectFields.PLAYER_SKILL_INFO_1_1 + 1), 100);
        fields.SetU32((ushort)(ObjectFields.PLAYER_SKILL_INFO_1_1 + 2),
            unchecked((uint)((10 << 16) | (ushort)-5)));
        Check(fields.PlayerLanguageSkillValue(98) == 105,
            "language skill base + permanent + temporary fold drift");
        fields.SetU32((ushort)(ObjectFields.PLAYER_SKILL_INFO_1_1 + 1), 0);
        fields.SetU32((ushort)(ObjectFields.PLAYER_SKILL_INFO_1_1 + 2),
            unchecked((uint)((100 << 16) | 5)));
        Check(fields.PlayerLanguageSkillValue(98) == 5,
            "zero-base language skill must exclude permanent but retain temporary bonus");

        Check(ChatLanguageCatalog.HashFolded("hello"u8) ==
                  ChatLanguageCatalog.HashFolded("HELLO"u8) &&
              ChatLanguageCatalog.IsWordCharacter('7') &&
              ChatLanguageCatalog.IsWordCharacter('\'') &&
              ChatLanguageCatalog.IsWordCharacter(0x4e00) &&
              !ChatLanguageCatalog.IsWordCharacter(' '),
            "SStrHash folding or reference tokenizer drift");

        CheckShippedData();
        CheckRuntimeWiring();
    }

    private static void CheckShippedData()
    {
        string data = ClientDataRoot.Path;
        using var mpq = new MpqMount(data);
        ChatLanguageCatalog words = ChatLanguageCatalog.Load(mpq) ??
            throw new InvalidDataException("LanguageWords.dbc did not load");
        Check(words.LanguageCount == 13 && words.WordCount == 1481,
            "LanguageWords.dbc shipped census drift");
        (uint Language, uint Skill, string Input, string Expected)[] vectors =
        [
            (1, 0, "hello", "kazum"),
            (1, 0, "the cat sat on the mat the cat", "mog ruk ogg gi mog gul mog ruk"),
            (1, 0, "hello, world.", "kazum magan "),
            (1, 0, "don't", "re'ka"),
            (1, 0, "12345", "regas"),
            (1, 0, "hEllO", "kAzuM"),
            (7, 0, "For the Alliance!", "Nud ras Landowar "),
            (1, 150, "the quick brown fox jumps over the lazy dog",
                "the quick nogah fox re'ka nogu the maka kil"),
            (1, 300, "hello, world.", "hello, world."),
            (0, 0, "hello", "hello"),
        ];
        foreach (var vector in vectors)
            Check(words.GarbleChat(vector.Language, vector.Skill, vector.Input) == vector.Expected,
                $"chat garble golden vector drift: {vector.Input}");

        SpellCatalog spells = SpellCatalog.Load(mpq) ??
            throw new InvalidDataException("Spell.dbc did not load");
        SkillLineCatalog skills = SkillLineCatalog.Load(mpq) ??
            throw new InvalidDataException("SkillLineAbility.dbc did not load");
        Check(spells.DeclaredLanguage(668) == 7 && skills.SpellLine(668) == 98 &&
              spells.DeclaredLanguage(669) == 1 && skills.SpellLine(669) == 109 &&
              spells.DeclaredLanguage(17737) == 33 && skills.SpellLine(17737) == 673 &&
              spells.DeclaredLanguage(25674) == 11 && skills.SpellLine(25674) == 0 &&
              spells.DeclaredLanguage(133) == 0,
            "known spell -> declared language -> skill-line chain drift");
    }

    private static void CheckRuntimeWiring()
    {
        string root = ClientConfig.FindRepoRoot();
        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        Check(chat.Contains("message = ApplyChatLanguage(type, packet.Language, message",
                  StringComparison.Ordinal) &&
              chat.IndexOf("message = expansion.Text;", StringComparison.Ordinal) <
                  chat.IndexOf("message = ApplyChatLanguage", StringComparison.Ordinal) &&
              chat.Contains("TrySpawnChatBubble(packet.SenderGuid, type, message)",
                  StringComparison.Ordinal) &&
              chat.Contains("_actions.KnownSpells.OrderBy", StringComparison.Ordinal) &&
              chat.Contains("PlayerLanguageSkillValue", StringComparison.Ordinal),
            "expanded-text -> language gate -> shared chat/bubble display seam drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
