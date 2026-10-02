using System.Security.Cryptography;
using System.Text.Json;
using MSUIClient.Formats;
using MSUIClient.World.Units;

if (args.Length < 1) throw new ArgumentException("Usage: race-foot-composite-check DATA_DIRECTORY [REPORT_JSON]");
using var mpq = new MpqMount(Path.GetFullPath(args[0]));
var member = mpq.ReadFileWithSupplier(RaceAppearanceTable.MpqPath)
    ?? throw new InvalidOperationException("Missing mounted ChrRaces.dbc");
var table = RaceAppearanceTable.Parse(member.Data) ?? throw new InvalidOperationException("Invalid race table");
var dbc = DbcFile.Parse(member.Data)!;
var rows = Enumerable.Range(0, dbc.RecordCount).Select(row => new {
    id = dbc.GetUInt(row, 0), flags = dbc.GetUInt(row, 1), bareFeet = table.HasBareFeet(dbc.GetUInt(row, 0))
}).ToArray();
var checks = new List<string>();
void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
Require(table.Flags(6) == 14 && table.Flags(8) == 14, "Mounted Tauren/Troll flags are 14");
Require(!table.HasBareFeet(1) && table.HasBareFeet(6) && table.HasBareFeet(8), "Mounted playable race foot policy");
Require(!table.HasBareFeet(999), "Unknown race does not infer bare feet");
Require(RaceAppearanceTable.Parse(null) is null && RaceAppearanceTable.Parse(new byte[4]) is null, "Invalid race data rejected");
var changed = member.Data.ToArray();
for (int row = 0; row < dbc.RecordCount; row++)
    if (dbc.GetUInt(row, 0) == 6) BitConverter.GetBytes(12u).CopyTo(changed, 20 + row * dbc.RecordSize + 4);
Require(RaceAppearanceTable.Parse(changed)!.HasBareFeet(6) == false, "Policy follows data rather than hardcoded race ID");

foreach (uint race in new uint[] { 1, 6, 8 })
{
    byte[] skin = new byte[256 * 256 * 4];
    for (int i = 0; i < skin.Length; i += 4) { skin[i] = 17; skin[i + 1] = 31; skin[i + 2] = 47; skin[i + 3] = 255; }
    var equipment = new CharacterEquipment();
    equipment.Add("synthetic boot", 1, CharacterEquipment.Slot.Feet);
    var display = new ItemDisplayRow();
    Array.Fill(display.BodyTextures, string.Empty);
    display.BodyTextures[6] = "lower_leg_test";
    display.BodyTextures[7] = "foot_test";
    equipment.Pieces[0].Row = display;
    var loaded = new List<string>();
    (byte[] bgra, int w, int h)? Load(string path) { loaded.Add(path); return (new byte[] { 200, 150, 100, 255 }, 1, 1); }
    var result = equipment.Composite(skin, 256, 256, Load, table.HasBareFeet(race));
    int lower = (192 * 256 + 128) * 4, foot = (224 * 256 + 128) * 4;
    Require(result[lower] == 200, $"Race {race}: lower-leg armor retained");
    Require(result[foot] == (table.HasBareFeet(race) ? 17 : 200), $"Race {race}: correct foot pixels");
    Require(!table.HasBareFeet(race) || loaded.All(path => !path.Contains("FootTexture", StringComparison.OrdinalIgnoreCase)), $"Race {race}: suppressed foot texture not loaded");
    Require(skin[lower] == 17 && skin[foot] == 17, $"Race {race}: source skin unmodified");
}
var report = new {
    schemaVersion = 1, evidenceKind = "mounted-race-foot-compositor-check", runtimeVerified = false,
    member = new { path = RaceAppearanceTable.MpqPath, supplier = member.Supplier, byteLength = member.Data.Length,
        sha256 = Convert.ToHexString(SHA256.HashData(member.Data)).ToLowerInvariant() },
    rows, checks, passed = checks.Count
};
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (args.Length > 1) { string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output, json); }
Console.WriteLine(json);
