namespace MSUIClient.Engine.UI;

/// <summary>
/// Vanilla's own loot-method slash verbs, straight out of GlobalStrings.lua:
/// SLASH_LOOT_FFA1 "/ffa", SLASH_LOOT_ROUNDROBIN1 "/roundrobin", SLASH_LOOT_MASTER1 "/master".
/// ChatFrame.lua's SlashCmdList entries call SetLootMethod("freeforall" / "roundrobin" /
/// "master", target), which is the same CMSG the unit popup sends.
///
/// The 1.12 client ships no slash alias for group loot or need-before-greed, so neither does
/// this law: those two stay unit-popup only, exactly as the reference has them.
/// </summary>
public static class LootMethodSlashCommandLaw
{
    /// <summary>The wire loot-method value for a vanilla slash verb, or null when it is not one.</summary>
    public static byte? Resolve(string command) => command.ToLowerInvariant() switch
    {
        "/ffa" => 0,
        "/roundrobin" => 1,
        "/master" => 2,
        _ => null,
    };

    /// <summary>Master loot is the only one that names a player; the rest ignore their argument.</summary>
    public static bool NeedsTarget(byte method) => method == 2;
}
