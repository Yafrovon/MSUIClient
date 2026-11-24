namespace MSUIClient.Engine.UI;

/// <summary>
/// Quest guardrails (shared_docs/QUEST_TABLETOP.md §13.2), pure: what a quest draft would leave broken in the world.
///
///   Unreachable    no one spawned gives it or takes it back; its prerequisite is missing, loops back to it, or can
///                  never be started itself; its next quest is missing.
///   Uncompletable  a kill target / object / drop source that stands nowhere; an item with no known way to get it.
///   Shared content quests that follow this one; a kill target that gives or takes other quests; a drop source whose
///                  loot already serves other quests.
///   Characters     who holds this quest (in the log, completed, turned in).
///
/// Every finding must be RESOLVED before Save: a Blocker only by changing the draft (each comes with fixes that do), a
/// Confirm by an explicit confirmation of what it says (or a fix). Nothing is silently repaired. <see cref="CheckDelete"/>
/// is the same law for removing a quest: its followers must be re-linked first.
/// </summary>
public static class QuestGuardrailLaw
{
    public enum Level { Blocker, Confirm }
    public enum Area { Unreachable, Uncompletable, SharedContent, Characters }

    /// <summary>A way to resolve a finding. Actions: "confirm", "resolve:&lt;what&gt;" (resolves it AND does something),
    /// "pick:npc:&lt;picker&gt;", "pick:object:&lt;picker&gt;",
    /// "clear:prev", "clear:next", "ender:giver", "goal:remove:&lt;i&gt;", "relink:&lt;quest&gt;".</summary>
    public sealed record Fix(string Label, string Action);

    public sealed record Finding(string Key, Area Area, Level Level, string Message, IReadOnlyList<Fix> Fixes);

    /// <summary>What the world (live DB + unpublished packs + the board) says. null = not known (yet).</summary>
    public interface IWorld
    {
        int? CreatureSpawns(uint entry);
        int? ObjectSpawns(uint entry);
        /// <summary>Quests this creature gives or takes back, other than the draft itself.</summary>
        int? CreatureQuestRoles(uint entry);
        string CreatureName(uint entry);
        string ObjectName(uint entry);
        bool? QuestExists(uint id);
        string QuestTitle(uint id);
        /// <summary>The PrevQuestId of another quest (0 = none), or null when unknown.</summary>
        int? PrevOf(uint id);
        /// <summary>Can someone spawned start this quest?</summary>
        bool? Startable(uint id);
        /// <summary>Quests that require this one (|PrevQuestId| == id), the draft excluded.</summary>
        IReadOnlyList<(uint Id, string Title, bool SamePack)> Followers(uint id);
        int? ItemSources(uint item);
        string ItemName(uint item);
        /// <summary>Other quests whose quest-only drops already come from this creature's loot.</summary>
        int DropSharedBy(uint creature, uint item);
        IReadOnlyList<(string Name, string State)>? Holders(uint quest);
        /// <summary>A stock quest's stock givers (they keep offering it: a pack cannot remove them). Empty otherwise.</summary>
        IReadOnlyCollection<uint> StockGivers(uint quest);
    }

    private const int MaxChain = 64;

    public static List<Finding> Check(QuestAuthoringLaw.Draft d, IWorld w)
    {
        var f = new List<Finding>();
        bool existing = d.Entry != 0;
        var pickGiver = new Fix("Pick another giver on the board", "pick:npc:wbq-giver");

        // ── unreachable ─────────────────────────────────────────────────────
        if (d.Giver != 0)
        {
            switch (w.CreatureSpawns(d.Giver))
            {
                case 0: f.Add(new($"giver.nospawn:{d.Giver}", Area.Unreachable, Level.Blocker,
                    $"Nobody can start it: {w.CreatureName(d.Giver)} is not spawned anywhere.", new[] { pickGiver })); break;
                case null: f.Add(new($"giver.unknown:{d.Giver}", Area.Unreachable, Level.Confirm,
                    $"Cannot tell where {w.CreatureName(d.Giver)} stands. Confirm they are spawned in the world.", new[] { Confirm("They are spawned"), pickGiver })); break;
            }
        }
        if (d.Ender != 0 && d.Ender != d.Giver)
        {
            var back = new Fix("Return it to the quest giver", "ender:giver");
            var pickEnder = new Fix("Pick another on the board", "pick:npc:wbq-ender");
            switch (w.CreatureSpawns(d.Ender))
            {
                case 0: f.Add(new($"ender.nospawn:{d.Ender}", Area.Unreachable, Level.Blocker,
                    $"It can never be turned in: {w.CreatureName(d.Ender)} is not spawned anywhere.", new[] { back, pickEnder })); break;
                case null: f.Add(new($"ender.unknown:{d.Ender}", Area.Unreachable, Level.Confirm,
                    $"Cannot tell where {w.CreatureName(d.Ender)} stands. Confirm they are spawned in the world.", new[] { Confirm("They are spawned"), back })); break;
            }
        }
        uint prev = (uint)Math.Abs((long)d.PrevQuest);
        if (prev != 0)
        {
            var clear = new Fix("Remove the prerequisite", "clear:prev");
            string title = w.QuestTitle(prev);
            switch (w.QuestExists(prev))
            {
                case false: f.Add(new($"prev.missing:{prev}", Area.Unreachable, Level.Blocker,
                    $"Its prerequisite (quest {prev}) does not exist, so it can never be offered.", new[] { clear })); break;
                case null: f.Add(new($"prev.unknown:{prev}", Area.Unreachable, Level.Confirm,
                    $"Cannot check the prerequisite \"{title}\" ({prev}). Confirm it exists and can be done.", new[] { Confirm("It exists"), clear })); break;
                default:
                    if (existing && LoopsBack(prev, d.Entry, w))
                        f.Add(new($"prev.loop:{prev}", Area.Unreachable, Level.Blocker,
                            $"Its prerequisite chain through \"{title}\" comes back to this quest: it can never be started.", new[] { clear }));
                    else if (w.Startable(prev) == false)
                        f.Add(new($"prev.unstartable:{prev}", Area.Unreachable, Level.Blocker,
                            $"Its prerequisite \"{title}\" has no spawned giver, so this quest can never be reached.", new[] { clear }));
                    break;
            }
        }
        if (d.NextInChain != 0 && w.QuestExists((uint)d.NextInChain) == false)
            f.Add(new($"next.missing:{d.NextInChain}", Area.Unreachable, Level.Blocker,
                $"Its next quest ({d.NextInChain}) does not exist.", new[] { new Fix("Remove the next quest", "clear:next") }));

        // ── uncompletable ───────────────────────────────────────────────────
        for (int i = 0; i < d.Goals.Length; i++)
        {
            var g = d.Goals[i];
            var remove = new Fix("Remove this objective", $"goal:remove:{i}");
            switch (g.Kind)
            {
                case QuestAuthoringLaw.ObjectiveKind.Kill when g.Target != 0:
                {
                    var pick = new Fix("Pick another target on the board", $"pick:npc:wbq-kill{i}");
                    switch (w.CreatureSpawns(g.Target))
                    {
                        case 0: f.Add(new($"kill.nospawn:{i}:{g.Target}", Area.Uncompletable, Level.Blocker,
                            $"Objective {i + 1} can never be done: {w.CreatureName(g.Target)} is not spawned anywhere.", new[] { pick, remove })); break;
                        case null: f.Add(new($"kill.unknown:{i}:{g.Target}", Area.Uncompletable, Level.Confirm,
                            $"Objective {i + 1}: cannot tell where {w.CreatureName(g.Target)} is. Confirm it is spawned (or summoned) in the world.", new[] { Confirm("It is spawned"), pick, remove })); break;
                    }
                    if (w.CreatureQuestRoles(g.Target) is > 0 and var roles)
                        f.Add(new($"kill.questnpc:{i}:{g.Target}:{roles}", Area.SharedContent, Level.Confirm,
                            $"Objective {i + 1} sends players to kill {w.CreatureName(g.Target)}, who gives or takes {roles} other quest(s).",
                            new[] { Confirm("Killing them is intended"), pick }));
                    break;
                }
                case QuestAuthoringLaw.ObjectiveKind.UseObject when g.Target != 0:
                    switch (w.ObjectSpawns(g.Target))
                    {
                        case 0: f.Add(new($"object.nospawn:{i}:{g.Target}", Area.Uncompletable, Level.Blocker,
                            $"Objective {i + 1} can never be done: {w.ObjectName(g.Target)} is not placed anywhere.", new[] { new Fix("Pick another object on the board", "pick:object:wbq-object"), remove })); break;
                        case null: f.Add(new($"object.unknown:{i}:{g.Target}", Area.Uncompletable, Level.Confirm,
                            $"Objective {i + 1}: cannot tell where {w.ObjectName(g.Target)} is placed. Confirm it is in the world.", new[] { Confirm("It is placed"), remove })); break;
                    }
                    break;
                case QuestAuthoringLaw.ObjectiveKind.Collect when g.Item != 0:
                    if (g.DropFrom != 0)
                    {
                        var pick = new Fix("Pick another drop source on the board", $"pick:npc:wbq-drop{i}");
                        switch (w.CreatureSpawns(g.DropFrom))
                        {
                            case 0: f.Add(new($"drop.nospawn:{i}:{g.DropFrom}", Area.Uncompletable, Level.Blocker,
                                $"Objective {i + 1} can never be done: {w.CreatureName(g.DropFrom)}, who drops {w.ItemName(g.Item)}, is not spawned anywhere.", new[] { pick, remove })); break;
                            case null: f.Add(new($"drop.unknown:{i}:{g.DropFrom}", Area.Uncompletable, Level.Confirm,
                                $"Objective {i + 1}: cannot tell where {w.CreatureName(g.DropFrom)} stands. Confirm they are spawned.", new[] { Confirm("They are spawned"), pick })); break;
                        }
                        int shared = w.DropSharedBy(g.DropFrom, g.Item);
                        if (shared > 0)
                            f.Add(new($"drop.shared:{i}:{g.DropFrom}:{shared}", Area.SharedContent, Level.Confirm,
                                $"{w.CreatureName(g.DropFrom)}'s loot already serves {shared} other quest drop(s); this adds {w.ItemName(g.Item)} to it.",
                                new[] { Confirm("Share the loot table"), pick }));
                    }
                    else
                    {
                        switch (w.ItemSources(g.Item))
                        {
                            case 0: f.Add(new($"item.nosource:{i}:{g.Item}", Area.Uncompletable, Level.Confirm,
                                $"Objective {i + 1}: nothing in the world drops, sells or makes {w.ItemName(g.Item)}. Choose a drop source, or confirm players get it another way (a script, another quest).",
                                new[] { Confirm("Players get it another way"), new Fix("Pick a drop source on the board", $"pick:npc:wbq-drop{i}"), remove })); break;
                            case null: f.Add(new($"item.unknown:{i}:{g.Item}", Area.Uncompletable, Level.Confirm,
                                $"Objective {i + 1}: cannot check where {w.ItemName(g.Item)} comes from. Confirm players can get it.",
                                new[] { Confirm("Players can get it"), remove })); break;
                        }
                    }
                    break;
            }
        }

        // ── stock quests (slice 3): stock givers stay; retiring strands what requires it ──
        if (existing && w.StockGivers(d.Entry) is { Count: > 0 } stockGivers && d.Giver != 0 && !stockGivers.Contains(d.Giver))
            f.Add(new($"stock.giver.kept:{d.Entry}:{d.Giver}", Area.SharedContent, Level.Confirm,
                $"{Titles(stockGivers.Select(w.CreatureName))} still offer{(stockGivers.Count == 1 ? "s" : "")} this quest too (a pack cannot remove a Blizzard giver); {w.CreatureName(d.Giver)} offers it as well.",
                new[] { Confirm("Both may offer it") }));
        if (existing && d.Retired)
        {
            foreach (var (id, title, samePack) in w.Followers(d.Entry))
                f.Add(new($"retire.follower:{id}", Area.Unreachable, Level.Blocker,
                    samePack ? $"\"{title}\" ({id}) requires this quest: retiring it would strand that quest."
                             : $"\"{title}\" ({id}) requires this quest: retiring it would strand that quest. Change that quest's prerequisite first (open it on the board), or keep this quest.",
                    samePack ? new[] { new Fix("Re-link it to this quest's prerequisite", $"relink:{id}"), new Fix("Keep the quest", "unretire") }
                             : new[] { new Fix("Keep the quest", "unretire") }));
        }

        // ── shared content: quests that follow this one ─────────────────────
        if (existing && !d.Retired && w.Followers(d.Entry) is { Count: > 0 } followers)
            f.Add(new($"followers:{d.Entry}:{followers.Count}", Area.SharedContent, Level.Confirm,
                $"{followers.Count} quest(s) follow this one ({Titles(followers.Select(x => x.Title))}); they will follow the changed quest.",
                new[] { Confirm("They should follow the changed quest") }));

        // ── characters ──────────────────────────────────────────────────────
        if (existing && w.Holders(d.Entry) is { Count: > 0 } holders)
            f.Add(new($"holders:{d.Entry}:{holders.Count}", Area.Characters, Level.Confirm,
                $"{holders.Count} character(s) hold this quest: {Titles(holders.Select(h => $"{h.Name} ({h.State})"))}. They keep their current progress.",
                new[]
                {
                    Confirm("Accept their current progress"),
                    // A reset runs once, at the next publish that restarts the server (WorldPackQuestResets in the web app).
                    new Fix("Drop it from their quest logs at publish", "resolve:reset:log"),
                    new Fix("Reset it for everyone (they can take it again)", "resolve:reset:all"),
                }));
        return f;
    }

    /// <summary>Removing a quest: every quest that requires it would be stranded until re-linked.</summary>
    public static List<Finding> CheckDelete(uint entry, int prevOfDeleted, IWorld w)
    {
        var f = new List<Finding>();
        foreach (var (id, title, samePack) in w.Followers(entry))
        {
            string to = prevOfDeleted != 0 ? $"to this quest's prerequisite ({Math.Abs((long)prevOfDeleted)})" : "as a quest with no prerequisite";
            f.Add(new($"delete.follower:{id}", Area.Unreachable, Level.Blocker,
                $"\"{title}\" ({id}) requires this quest and could never be offered without it.",
                samePack ? new[] { new Fix($"Re-link it {to}", $"relink:{id}") } : Array.Empty<Fix>()));
        }
        if (w.Holders(entry) is { Count: > 0 } holders)
            f.Add(new($"delete.holders:{entry}:{holders.Count}", Area.Characters, Level.Confirm,
                $"{holders.Count} character(s) hold this quest: {Titles(holders.Select(h => $"{h.Name} ({h.State})"))}. It disappears from their log.",
                new[] { Confirm("Remove it anyway") }));
        return f;
    }

    /// <summary>Unresolved = every Blocker, and every Confirm not in <paramref name="confirmed"/>.</summary>
    public static int Unresolved(IEnumerable<Finding> findings, IReadOnlySet<string> confirmed)
        => findings.Count(x => x.Level == Level.Blocker || !confirmed.Contains(x.Key));

    private static Fix Confirm(string label) => new(label, "confirm");

    private static bool LoopsBack(uint start, uint self, IWorld w)
    {
        uint at = start;
        for (int step = 0; at != 0 && step < MaxChain; step++)
        {
            if (at == self) return true;
            at = w.PrevOf(at) is { } p ? (uint)Math.Abs((long)p) : 0;
        }
        return false;
    }

    private static string Titles(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 4 ? string.Join(", ", list) : string.Join(", ", list.Take(4)) + $" and {list.Count - 4} more";
    }
}
