using MSUIClient;
using MSUIClient.Net;

internal static class PlayerNameClinicalChecks
{
    public static void Run()
    {
        byte[] body = Convert.FromHexString(
            "070000000000000042656e696c6c610000010000000000000001000000");
        PlayerNameQueryResponse response = PlayerNamePackets.ParseResponse(body);
        Check(response == new PlayerNameQueryResponse(7, "Benilla", "",
                new PlayerTraits(1, 1, 0)),
            "player-name response layout/trait order drift");

        byte[] crossRealm = Convert.FromHexString(
            "2a0000000000000041726961004f746865725265616c6d00080000000100000003000000");
        response = PlayerNamePackets.ParseResponse(crossRealm);
        Check(response == new PlayerNameQueryResponse(42, "Aria", "OtherRealm",
                new PlayerTraits(8, 3, 1)),
            "player-name realm/trait parsing drift");

        CheckThrows(() => PlayerNamePackets.ParseResponse(body.Concat(new byte[] { 0 }).ToArray()),
            "player-name trailing bytes accepted");

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string targeting = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Targeting.cs"));
        string quest = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Quest.cs"));
        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        Check(dispatch.Contains("PlayerNamePackets.ParseResponse(body)", StringComparison.Ordinal) &&
              dispatch.Contains("_playerTraits[response.Guid] = response.Traits", StringComparison.Ordinal) &&
              targeting.Contains("Dictionary<ulong, PlayerTraits> _playerTraits", StringComparison.Ordinal) &&
              targeting.Contains("_playerTraits.Clear()", StringComparison.Ordinal) &&
              quest.Contains("_playerTraits.TryGetValue(subjectGuid", StringComparison.Ordinal) &&
              chat.Contains("_playerTraits.TryGetValue(guid", StringComparison.Ordinal),
            "player-name trait cache/macro fallback wiring drift");
    }

    private static void CheckThrows(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
