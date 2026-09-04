using MSUIClient;
using MSUIClient.Engine.UI;

internal static class ChatTabClinicalChecks
{
    public static void Run()
    {
        Check(ChatFrameLaw.VisibleInTab(ChatFrameLaw.MsgType.System, 0) &&
              ChatFrameLaw.VisibleInTab(ChatFrameLaw.MsgType.Loot, 0) &&
              ChatFrameLaw.VisibleInTab(ChatFrameLaw.MsgType.Skill, 0) &&
              !ChatFrameLaw.VisibleInTab(ChatFrameLaw.MsgType.Money, 0) &&
              !ChatFrameLaw.VisibleInTab(ChatFrameLaw.MsgType.CombatXpGain, 0) &&
              ChatFrameLaw.VisibleInTab(ChatFrameLaw.MsgType.Money, 1) &&
              ChatFrameLaw.VisibleInTab(ChatFrameLaw.MsgType.CombatXpGain, 1) &&
              !ChatFrameLaw.VisibleInTab(ChatFrameLaw.MsgType.System, 1),
            "General/Combat Log default chat-group routing drift");

        Check(ChatFrameLaw.FormatXpGain("Kobold Vermin", 35, 0) ==
                  "Kobold Vermin dies, you gain 35 experience." &&
              ChatFrameLaw.FormatXpGain("Kobold Vermin", 52, 17) ==
                  "Kobold Vermin dies, you gain 52 experience. (+17 exp Rested bonus)" &&
              ChatFrameLaw.FormatXpGain(null, 120, 0) ==
                  "You gain 120 experience.",
            "COMBATLOG_XPGAIN first-person global strings drift");

        string root = ClientConfig.FindRepoRoot();
        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        string combat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        Check(chat.Contains("ChatFrameLaw.VisibleInTab(type, tab)",
                  StringComparison.Ordinal) &&
              chat.Contains("private void PostCombatXpGain(CombatXpGain xp)",
                  StringComparison.Ordinal) &&
              chat.Contains("_pendingChatXp.Add(xp)", StringComparison.Ordinal) &&
              combat.Contains("PostCombatXpGain(xp)", StringComparison.Ordinal),
            "Combat Log filtering or XP feed/query wiring is missing");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
