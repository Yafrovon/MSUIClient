using MSUIClient;
using MSUIClient.Engine.UI;

internal static class EquipmentReadinessClinicalChecks
{
    public static void Run()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
            count++;
        }
        Check(EquipmentReadinessLaw.TryParse("0:1102522,15:0,18:4294967295", out var requested, out _) &&
            requested.Count == 3 && requested[15] == 0 && requested[18] == uint.MaxValue, "Valid uint and empty slots rejected");
        foreach (string? invalid in new string?[] { null, "", " ", "0", "0:", ":1", "-1:2", "19:2", "0:-1", "0:+1",
            "+0:1", "0:4294967296", "0:1,", ",0:1", "0:1,,1:2", "0:1,0:1", "0:1,00:2", "0:1:2", "0: 1", "0:0x1" })
            Check(!EquipmentReadinessLaw.TryParse(invalid, out var rejected, out _) && rejected.Count == 0,
                "Malformed specification accepted: " + invalid);
        string full = string.Join(',', Enumerable.Range(0, 19).Select(slot => $"{slot}:0"));
        Check(EquipmentReadinessLaw.TryParse(full, out var all, out _) && all.Count == 19, "Full slot table rejected");
        Check(!EquipmentReadinessLaw.TryParse(full + ",0:0", out _, out _), "Overlong/duplicate slot table accepted");
        uint[] visible = new uint[19];
        visible[0] = 1102522;
        Check(EquipmentReadinessLaw.Matches("0:1102522,15:0", visible, out _), "Correct actual visible entries rejected");
        Check(EquipmentReadinessLaw.Matches("15:0", visible, out _), "Unlisted slot was incorrectly required to be empty");
        Check(!EquipmentReadinessLaw.Matches("0:0", visible, out var mismatch) &&
            mismatch.Contains("slot=0:expected=0:actual=1102522", StringComparison.Ordinal), "Mismatch lacks exact actual entry");
        Check(!EquipmentReadinessLaw.Matches("0:1102523,15:7", visible, out mismatch) &&
            mismatch.Contains("slot=0:", StringComparison.Ordinal) && mismatch.Contains("slot=15:", StringComparison.Ordinal),
            "Multiple mismatches not retained");
        Check(!EquipmentReadinessLaw.Matches("15:0", null, out _), "Unavailable actor accepted as empty equipment");
        Check(!EquipmentReadinessLaw.Matches("15:0", new uint[18], out _), "Incomplete visible slot snapshot accepted");
        string source = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "GameLoop", "Dev", "GameLoop.LiveRun.Equipment.cs"));
        int first = source.IndexOf("if (args.Length >= 2 && args[1] == \"require-items\")", StringComparison.Ordinal);
        int next = source.IndexOf("args[1] == \"require-guid\"", StringComparison.Ordinal);
        Check(first >= 0 && next > first, "require-items guard no longer precedes observation dispatch");
        string branch = source[first..next];
        Check(branch.Contains("ControlledGuid : _selectionGuid", StringComparison.Ordinal) &&
            branch.Contains("itemUnit.IsPlayer", StringComparison.Ordinal) &&
            branch.Contains("itemUnit.Fields.PlayerVisibleItemEntry", StringComparison.Ordinal) &&
            !branch.Contains("LocalPlayerGuid", StringComparison.Ordinal), "Readiness actor or actual visible field source drift");
        Check(branch.Contains("args.Length == 4", StringComparison.Ordinal) &&
            branch.Contains("subjectName is \"self\" or \"selection\"", StringComparison.Ordinal) &&
            branch.Contains("EquipmentReadinessLaw.Matches(expected, visible, out actual)", StringComparison.Ordinal) &&
            branch.Contains("if (!matches && _liveRunOptions is not null)", StringComparison.Ordinal) &&
            branch.Contains("FinishLiveBootstrap(\"EQUIPMENT_READINESS_MISMATCH\"", StringComparison.Ordinal),
            "Invalid request or mismatch no longer terminates live protocol");
        Console.WriteLine($"interface-wire-check: EquipmentReadiness PASS ({count} checks)");
    }
}
