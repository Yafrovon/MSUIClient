# Generates the Gilneas World Pack content batches (JSON arrays of { kind, key?, body }).
# Posted by the World Builder script command `content <file>` -> POST /WorldPacks/Content (audited ops).
$ErrorActionPreference = 'Stop'
$out = $PSScriptRoot

function Row($table, $body) { [ordered]@{ kind = "dbrow:$table"; body = $body } }
function Dbc($name, $id, $clone, $fields) {
  $b = [ordered]@{ fields = $fields }
  if ($null -ne $clone) { $b.cloneFrom = $clone }
  [ordered]@{ kind = "dbc:$name"; key = "$id"; body = $b }
}
function Save($name, $items) {
  $json = ConvertTo-Json -InputObject @($items) -Depth 12
  [System.IO.File]::WriteAllText((Join-Path $out $name), $json)
  Write-Host "$name : $($items.Count) item(s)"
}

# ── creature templates ────────────────────────────────────────────────────────
function Npc([int]$entry, [string]$name, [string]$sub, [int]$lvl, [int]$lvlMax, [int]$display, [int]$faction, [int]$flags,
             [int]$rank = 0, [int]$type = 7, [int]$class = 1, [double]$hp = 1, [double]$dmg = 1, [string]$ai = '',
             [int]$gossip = 0, [int]$equip = 0, [int]$loot = 0, [int]$goldMin = 0, [int]$goldMax = 0,
             [int]$trainerType = 0, [int]$trainerClass = 0, [double]$scale = 1) {
  $r = [ordered]@{
    entry = $entry; patch = 0; name = $name; subname = $sub; level_min = $lvl; level_max = $lvlMax; faction = $faction
    npc_flags = $flags; display_id1 = $display; display_scale1 = $scale; display_probability1 = 100; display_total_probability = 100
    speed_walk = 1.0; speed_run = 1.14286; type = $type; rank = $rank; unit_class = $class
    xp_multiplier = 1.0; health_multiplier = $hp; mana_multiplier = 1.0; armor_multiplier = 1.0; damage_multiplier = $dmg
    damage_variance = 0.14; base_attack_time = 2000; ranged_attack_time = 2000; inhabit_type = 3; movement_type = 0
    detection_range = 20; call_for_help_range = 5; leash_range = 0; ai_name = $ai; script_name = ''
    gossip_menu_id = $(if ($gossip) { 62000 + ($gossip - 7000100) } else { 0 }); equipment_id = $equip; loot_id = $loot; gold_min = $goldMin; gold_max = $goldMax
    trainer_type = $trainerType; trainer_class = $trainerClass
  }
  Row 'creature_template' $r
}
function Gossip([int]$id, [string]$text) {
  $menu = 62000 + ($id - 7000100)   # gossip_menu.entry is SMALLINT: its own range
  @( (Row 'broadcast_text' ([ordered]@{ entry = $id; male_text = $text; female_text = $text; chat_type = 0 })),
     (Row 'npc_text' ([ordered]@{ ID = $id; BroadcastTextID0 = $id; Probability0 = 1 })),
     (Row 'gossip_menu' ([ordered]@{ entry = $menu; text_id = $id; script_id = 0; condition_id = 0 })) )
}
function Vendor([int]$entry, [int[]]$items) { $items | ForEach-Object { Row 'npc_vendor' ([ordered]@{ entry = $entry; item = $_; maxcount = 0; incrtime = 0; itemflags = 0; condition_id = 0 }) } }
function Equip([int]$entry, [int]$main, [int]$off, [int]$ranged) { Row 'creature_equip_template' ([ordered]@{ entry = $entry; probability = 100; item1 = $main; item2 = $off; item3 = $ranged; patch_min = 0; patch_max = 10 }) }
function Loot([int]$entry, [int]$item, [double]$chance, [int]$min = 1, [int]$max = 1, [int]$group = 0) {
  Row 'creature_loot_template' ([ordered]@{ entry = $entry; item = $item; ChanceOrQuestChance = $chance; groupid = $group; mincountOrRef = $min; maxcount = $max; condition_id = 0; patch_min = 0; patch_max = 10 })
}
# EventAI: one event → one script row. type 0 = in-combat timer (ms), 2 = HP% window, 4 = aggro, 6 = death.
$script:aiId = 7003000
function Ai([int]$creature, [int]$type, [int[]]$p, [int]$command, [int]$datalong, [int]$target, [int]$textId = 0, [string]$comment = '') {
  $id = $script:aiId++
  @( (Row 'creature_ai_events' ([ordered]@{ id = $id; creature_id = $creature; condition_id = 0; event_type = $type; event_inverse_phase_mask = 0
        event_chance = 100; event_flags = $(if ($type -eq 0) { 1 } else { 0 }); event_param1 = $p[0]; event_param2 = $p[1]; event_param3 = $p[2]; event_param4 = $p[3]
        action1_script = $id; action2_script = 0; action3_script = 0; comment = $comment })),
     (Row 'creature_ai_scripts' ([ordered]@{ id = $id; delay = 0; priority = 0; command = $command; datalong = $datalong; datalong2 = 0; datalong3 = 0; datalong4 = 0
        target_param1 = 0; target_param2 = 0; target_type = $target; data_flags = 0; dataint = $textId; dataint2 = 0; dataint3 = 0; dataint4 = 0
        x = 0; y = 0; z = 0; o = 0; condition_id = 0; comments = $comment })) )
}
function Cast([int]$c, [int]$spell, [int]$first, [int]$every, [int]$target = 1, [string]$what = '') { Ai $c 0 @($first, ($first + 2000), $every, ($every + 3000)) 15 $spell $target 0 $what }
function Yell([int]$c, [int]$eventType, [int]$textId, [string]$text) {
  @( (Row 'broadcast_text' ([ordered]@{ entry = $textId; male_text = $text; female_text = $text; chat_type = 1 })) ) + (Ai $c $eventType @(0, 0, 0, 0) 0 1 0 $textId "yell")
}

$town = @()
# Duskhaven (7000100..)
$town += Npc 7000100 'Innkeeper Hollis Grey' 'Innkeeper' 35 35 3688 35 (1 + 4 + 128) -gossip 7000100
$town += Gossip 7000100 'Welcome to the Greymane Arms, friend. The walls keep the beasts out... mostly. Rest while you can.'
$town += Vendor 7000100 @(159, 1179, 1205, 1645, 4540, 4541, 4542, 4544, 117, 2287, 3770, 3771)
$town += Npc 7000101 'Mara Blackwood' 'General Goods' 33 33 3696 35 (1 + 4) -gossip 7000101
$town += Gossip 7000101 'Thread, flint, bags, a fishing pole. Everything a Gilnean needs except a way over the Wall.'
$town += Vendor 7000101 @(4496, 4497, 6256, 4471, 4470, 2320, 2321, 2678, 2880, 3371, 3372, 5042, 159, 4541)
$town += Npc 7000102 'Borin Ashford' 'Blacksmith' 36 36 3698 35 (1 + 4 + 16384) -gossip 7000102 -equip 7000102
$town += Gossip 7000102 'If it has an edge, I can sharpen it. If it has a dent, I can hammer it out. If it has fangs - bring it to the guard.'
$town += Equip 7000102 5956 0 0
$town += Vendor 7000102 @(2488, 2489, 2490, 2491, 2492, 2493, 2494, 2495, 2862, 2863, 7964, 3239, 3240, 7965, 2901, 5956)
$town += Npc 7000103 'Sergeant Morris Blackwald' 'Duskhaven Watch' 40 40 1861 35 (1 + 2) -gossip 7000103 -equip 7000103 -hp 2 -dmg 1.5
$town += Gossip 7000103 'The Bloodfang pack grows bolder every night. If you can hold a blade, the Watch needs you.'
$town += Equip 7000103 1899 143 0
$town += Npc 7000104 'Lorna Crowley' '' 38 38 3647 35 (1 + 2) -gossip 7000104 -equip 7000104
$town += Gossip 7000104 'My father says the Moonrage came down from the eastern ridge. He is wrong about many things - not about this.'
$town += Equip 7000104 2208 0 2507
$town += Npc 7000105 'Father Garrick Elms' 'Light of Duskhaven' 37 37 3702 35 (1 + 2) -class 2 -gossip 7000105
$town += Gossip 7000105 'The Light has not abandoned Gilneas, child. Only its lords have.'
$town += Npc 7000106 'Duskhaven Watchman' '' 40 42 1769 35 1 -hp 3 -dmg 2 -equip 7000106 -gossip 7000106
$town += Gossip 7000106 'Keep to the lanterns after dark.'
$town += Equip 7000106 1899 143 0
$town += Npc 7000107 'Grace Bellamy' 'First Aid Trainer' 35 35 3649 35 (1 + 16) -trainerType 2 -gossip 7000107
$town += Gossip 7000107 'Bandages and a steady hand have saved more Gilneans than any wall.'
$town += Npc 7000108 'Banker Edwin Thorne' 'Banker' 34 34 3666 35 (1 + 256) -gossip 7000108
$town += Gossip 7000108 'Your coin is safer with the Crown than in your boot.'
$town += Npc 7000109 'Spirit Healer' '' 60 60 5233 35 (1 + 32) -type 6
$town += Npc 7000110 'Duskhaven Resident' '' 20 25 3658 35 0
# Keel Harbor (7000120..)
$town += Npc 7000120 'Harbormaster Kelwin Ashe' 'Keel Harbor' 38 38 3697 35 (1 + 2) -gossip 7000120
$town += Gossip 7000120 'Three ships lost to the Slitherblade this month. Three. The sea used to be the one thing we could trust.'
$town += Npc 7000121 'Old Salt Brannigan' 'Fish Merchant' 33 33 3693 35 (1 + 4) -gossip 7000121
$town += Gossip 7000121 'Fresh this morning. Well. Fresh-ish.'
$town += Vendor 7000121 @(4592, 4593, 4594, 787, 6256, 6529, 6530, 6532, 159, 1179)
$town += Npc 7000122 'Keel Harbor Dockhand' '' 25 28 3690 35 0
# Stormglen (7000130..)
$town += Npc 7000130 'Farmer Hollister Moss' '' 36 36 3690 35 (1 + 2) -gossip 7000130
$town += Gossip 7000130 'The mastiffs used to guard the flocks. Now something has turned them. They bite anything that moves.'
$town += Npc 7000131 'Merchant Tobias Reed' 'Traveling Merchant' 34 34 3692 35 (1 + 2 + 4) -gossip 7000131
$town += Gossip 7000131 'The Emberstone road is not safe. Highwaymen, and worse. Buy what you need here.'
$town += Vendor 7000131 @(159, 1179, 4541, 4542, 117, 2287, 4496, 2320, 6256)
$town += Npc 7000132 'Stormglen Farmhand' '' 22 26 3695 35 0
# Greymane Manor (7000140..)
$town += Npc 7000140 'Lord Darius Crowley' 'Lord of Duskhaven' 45 45 3222 35 (1 + 2) -rank 1 -hp 4 -dmg 2 -gossip 7000140 -equip 7000140
$town += Gossip 7000140 'Genn built a wall to keep the world out. He forgot to ask what was already inside it.'
$town += Equip 7000140 2244 0 0
$town += Npc 7000141 'Manor Guard' '' 42 43 1769 35 1 -hp 3 -dmg 2 -equip 7000106

# ── zone mobs (7000200..) ─────────────────────────────────────────────────────
$mobs = @()
$mobs += Npc 7000200 'Bloodfang Stalker' '' 35 37 657 16 0 -loot 7000200 -goldMin 150 -goldMax 400
$mobs += Npc 7000201 'Bloodfang Ripper' '' 37 39 522 16 0 -loot 7000201 -goldMin 180 -goldMax 450 -ai 'EventAI'
$mobs += Cast 7000201 3604 3000 12000 1 'Tendon Rip'
$mobs += Npc 7000202 'Moonrage Howler' '' 38 40 736 16 0 -loot 7000202 -goldMin 200 -goldMax 500
$mobs += Npc 7000203 'Moonrage Shadowcaster' '' 38 40 203 16 0 -class 2 -loot 7000203 -goldMin 220 -goldMax 520 -ai 'EventAI'
$mobs += Cast 7000203 2767 1000 14000 4 'Shadow Word: Pain'
$mobs += Cast 7000203 18557 5000 16000 1 'Drain Life'
$mobs += Npc 7000204 'Rabid Gilnean Mastiff' '' 34 36 9562 16 0 -type 1 -loot 7000204
$mobs += Npc 7000205 'Highland Mountain Lion' '' 35 37 1058 16 0 -type 1 -loot 7000205
$mobs += Npc 7000206 'Gilnean Highwayman' '' 36 38 2342 16 0 -loot 7000206 -goldMin 250 -goldMax 600 -equip 7000206 -ai 'EventAI'
$mobs += Equip 7000206 2208 2208 0
$mobs += Cast 7000206 8721 4000 11000 1 'Backstab'
$mobs += Npc 7000207 'Slitherblade Raider' '' 38 40 9135 16 0 -type 7 -loot 7000207 -goldMin 200 -goldMax 480
$mobs += Npc 7000208 'Forsaken Deathstalker' 'Scout' 39 41 4342 16 0 -type 6 -loot 7000208 -goldMin 260 -goldMax 600 -ai 'EventAI'
$mobs += Cast 7000208 8721 3000 10000 1 'Backstab'
$mobs += Npc 7000209 'Gorefang' 'Bloodfang Alpha' 41 41 2352 16 0 -rank 4 -type 1 -hp 3 -dmg 2 -loot 7000209 -goldMin 1500 -goldMax 3000 -ai 'EventAI' -scale 1.3
$mobs += Cast 7000209 3604 4000 12000 1 'Tendon Rip'
$mobs += Ai 7000209 2 @(30, 0, 0, 0) 15 8599 0 0 'Enrage at 30%'
# Loot: cloth/leather/trade goods + a sliver of the dungeon blues.
foreach ($e in 7000200, 7000201, 7000202, 7000203, 7000206, 7000208) {
  $mobs += Loot $e 4306 30 1 2    # Silk Cloth
  $mobs += Loot $e 4338 12 1 1    # Mageweave Cloth
  $mobs += Loot $e 4599 8 1 1     # Cured Ham Steak
  $mobs += Loot $e 1710 5 1 1     # Greater Healing Potion
  $mobs += Loot $e 14898 0.4 1 1  # Saltstone Girdle (green)
  $mobs += Loot $e 9933 0.4 1 1   # Brigade Leggings (green)
  $mobs += Loot $e 10777 0.05 1 1 # Arachnid Gloves (blue)
}
foreach ($e in 7000204, 7000205) {
  $mobs += Loot $e 4234 40 1 2    # Heavy Leather
  $mobs += Loot $e 4304 15 1 1    # Thick Leather
  $mobs += Loot $e 3712 25 1 1    # Turtle Meat (stand-in cooking meat)
}
$mobs += Loot 7000207 4306 25 1 2
$mobs += Loot 7000207 5504 12 1 1   # Tangy Clam Meat
$mobs += Loot 7000207 7909 3 1 1    # Aquamarine
$mobs += Loot 7000209 4091 25 1 1   # Widowmaker
$mobs += Loot 7000209 12469 25 1 1  # Mutilator
$mobs += Loot 7000209 15599 50 1 1  # Ancient Greaves

# ── Greymane Fortress (7000300..) ─────────────────────────────────────────────
$dun = @()
$dun += Npc 7000300 'Cursed Gilnean Guard' '' 40 41 1769 16 0 -rank 1 -hp 2.5 -dmg 1.6 -loot 7000300 -goldMin 400 -goldMax 800 -equip 7000106 -ai 'EventAI'
$dun += Cast 7000300 11971 5000 14000 1 'Sunder Armor'
$dun += Cast 7000300 11972 8000 18000 1 'Shield Bash'
$dun += Npc 7000301 'Bloodfang Houndmaster' '' 41 41 522 16 0 -rank 1 -hp 2.5 -dmg 1.7 -loot 7000301 -goldMin 400 -goldMax 800 -ai 'EventAI'
$dun += Cast 7000301 9080 3000 12000 1 'Hamstring'
$dun += Npc 7000302 'Greymane Retainer' '' 40 41 3666 16 0 -rank 1 -class 2 -hp 2 -dmg 1.4 -loot 7000302 -goldMin 400 -goldMax 800 -ai 'EventAI'
$dun += Cast 7000302 2767 1000 12000 4 'Shadow Word: Pain'
$dun += Cast 7000302 6064 9000 20000 0 'Heal (self)'
$dun += Npc 7000303 'Plagued Hound' '' 40 40 9562 16 0 -type 1 -hp 1.2 -loot 7000303
# Bosses
$dun += Npc 7000310 'Baron Ashbury' '' 42 42 3222 16 0 -rank 3 -class 2 -hp 12 -dmg 4 -loot 7000310 -goldMin 3000 -goldMax 5000 -ai 'EventAI'
$dun += Yell 7000310 4 7000310 'Guests? At this hour? How... appetizing.'
$dun += Cast 7000310 18557 4000 15000 1 'Drain Life'
$dun += Cast 7000310 2767 2000 9000 4 'Shadow Word: Pain'
$dun += Cast 7000310 12542 12000 25000 4 'Fear'
$dun += Npc 7000311 'Lord Walden' 'Alchemist of the Court' 43 43 3223 16 0 -rank 3 -class 2 -hp 12 -dmg 4 -loot 7000311 -goldMin 3000 -goldMax 5000 -ai 'EventAI'
$dun += Yell 7000311 4 7000311 'You interrupt my work! Very well - you will do as the next ingredient.'
$dun += Cast 7000311 20819 2000 7000 1 'Frostbolt'
$dun += Cast 7000311 11921 5000 12000 4 'Fireball (Volatile Flask)'
$dun += Cast 7000311 11831 9000 22000 0 'Frost Nova'
$dun += Npc 7000312 'Lord Godfrey' 'Master of Greymane Fortress' 44 44 2353 16 0 -rank 3 -hp 16 -dmg 5 -loot 7000312 -goldMin 5000 -goldMax 8000 -ai 'EventAI' -equip 7000312
$dun += Equip 7000312 2244 0 2507
$dun += Yell 7000312 4 7000312 'Greymane abandoned this fortress to the beasts. I will not abandon it to you!'
$dun += Cast 7000312 13737 5000 11000 1 'Mortal Strike'
$dun += Cast 7000312 9008 3000 9000 4 'Shoot (Pistol Barrage)'
$dun += Cast 7000312 15548 8000 16000 0 'Thunderclap'
$dun += Ai 7000312 2 @(30, 0, 0, 0) 15 8599 0 0 'Enrage at 30%'
$dun += Yell 7000312 6 7000313 'The curse... will... outlive... us all...'
foreach ($e in 7000300, 7000301, 7000302) {
  $dun += Loot $e 4338 30 1 2; $dun += Loot $e 1710 8 1 1; $dun += Loot $e 9933 1 1 1; $dun += Loot $e 14946 1 1 1
  $dun += Loot $e 9432 0.5 1 1; $dun += Loot $e 9433 0.5 1 1
}
$dun += Loot 7000303 4234 40 1 2
# Boss loot: one of each group guaranteed.
foreach ($i in @(@(7000310, @(9469, 9482, 10777, 9433)), @(7000311, @(9480, 9511, 13082, 8346)), @(7000312, @(4091, 12469, 7726, 9640, 12470)))) {
  $e = $i[0]; $list = $i[1]; $share = [math]::Round(100 / $list.Count, 2)
  foreach ($it in $list) { $dun += Loot $e $it $share 1 1 1 }
  $dun += Loot $e 4338 60 2 4
}

# ── quests (7000100..) ────────────────────────────────────────────────────────
function Quest([int]$entry, [string]$title, [string]$details, [string]$objectives, [string]$complete, [int]$lvl, [int]$minLvl,
               [int]$giver, [int]$ender, [int]$kill, [int]$killCount, [int]$xp, [int]$money, [int[]]$choices, [int]$prev = 0) {
  $q = [ordered]@{
    entry = $entry; patch = 0; Method = 2; ZoneOrSort = 7001; MinLevel = $minLvl; MaxLevel = 0; QuestLevel = $lvl; Type = $(if ($kill -ge 7000300) { 81 } else { 0 })
    Title = $title; Details = $details; Objectives = $objectives; OfferRewardText = $complete; RequestItemsText = ''; EndText = ''
    ReqCreatureOrGOId1 = $kill; ReqCreatureOrGOCount1 = $(if ($kill) { $killCount } else { 0 }); PrevQuestId = $prev
    RewXP = $xp; RewOrReqMoney = $money; RewMoneyMaxLevel = $money
  }
  for ($i = 0; $i -lt $choices.Count; $i++) { $q["RewChoiceItemId$($i+1)"] = $choices[$i]; $q["RewChoiceItemCount$($i+1)"] = 1 }
  @( (Row 'quest_template' $q),
     (Row 'creature_questrelation' ([ordered]@{ id = $giver; quest = $entry; patch_min = 0; patch_max = 10 })),
     (Row 'creature_involvedrelation' ([ordered]@{ id = $ender; quest = $entry; patch_min = 0; patch_max = 10 })) )
}
$quests = @()
$quests += Quest 7000110 'Word from Keel Harbor' 'Duskhaven is the heart of what is left of free Gilneas. Take this report to Sergeant Morris Blackwald - the Watch will want to know the harbor still stands.' 'Speak with Sergeant Morris Blackwald in Duskhaven.' 'The harbor holds? Good. Then there is still a Gilneas worth fighting for.' 36 33 7000120 7000103 0 0 1200 500 @()
$quests += Quest 7000100 'Wolves at the Wall' 'The Bloodfang pack stalks the western ridge above Duskhaven. Every night they come closer to the houses. Thin their numbers: slay 10 Bloodfang Stalkers.' 'Slay 10 Bloodfang Stalkers.' 'Ten fewer throats howling at our walls. The Watch thanks you.' 37 34 7000103 7000103 7000200 10 3200 2500 @(7494, 12257, 15163)
$quests += Quest 7000101 "The Ripper's Den" 'Behind the Stalkers come the Rippers - bigger, meaner, and they hamstring their prey before they feed. Hunt 8 Bloodfang Rippers on the western ridge.' 'Slay 8 Bloodfang Rippers.' 'You came back with all your tendons. Impressive.' 38 35 7000103 7000103 7000201 8 3600 3000 @(14946, 9933, 7480) 7000100
$quests += Quest 7000102 'Howls on the Eastern Ridge' 'The Moonrage came down from the eastern ridge the night the manor fell silent. Slay 10 Moonrage Howlers before their pack swallows the farms.' 'Slay 10 Moonrage Howlers.' 'The ridge is quieter already. My father will pretend he ordered it.' 39 36 7000104 7000104 7000202 10 3800 3200 @(14247, 7479, 14898)
$quests += Quest 7000103 'Shadowcasters' 'Some of the Moonrage remember the old magic. They weave shadow among the pines of the eastern ridge. Six of them, the scouts say. End them.' 'Slay 6 Moonrage Shadowcasters.' 'The Light burns brighter tonight. Go with its blessing.' 39 36 7000105 7000105 7000203 6 3800 3200 @(4736, 14250, 9910)
$quests += Quest 7000104 'Stormglen Mastiffs' 'My own hounds. Raised them from pups. Now they foam at the mouth and tear at the sheep pens. Please - put 10 of them down before they get the children.' 'Slay 10 Rabid Gilnean Mastiffs around Stormglen.' 'It had to be done. Thank you, stranger.' 36 33 7000130 7000130 7000204 10 3000 2200 @(15599, 14654, 14592)
$quests += Quest 7000105 'Highway Robbery' 'Highwaymen prey on the Emberstone road between Stormglen and Duskhaven. I lose a wagon a week. Slay 8 Gilnean Highwaymen and the road is yours to walk.' 'Slay 8 Gilnean Highwaymen.' 'Ha! Business is looking up. Take your pick.' 38 35 7000131 7000131 7000206 8 3500 4000 @(4081, 7942, 1521)
$quests += Quest 7000106 'Naga on the Shore' 'The Slitherblade raid our boats from the rocks north of the harbor. Kill 10 Slitherblade Raiders and our fishers can sail again.' 'Slay 10 Slitherblade Raiders north of Keel Harbor.' 'The nets will be full tomorrow. Thank you.' 39 36 7000120 7000120 7000207 10 3800 3500 @(9966, 14421, 7494)
$quests += Quest 7000107 'Forsaken Eyes' 'The dead walk our northern hills, taking notes for their Dark Lady. I will not have Sylvanas counting Gilnean graves. Slay 6 Forsaken Deathstalkers in the northern hills.' 'Slay 6 Forsaken Deathstalkers.' 'Let them count that.' 40 37 7000140 7000140 7000208 6 4200 4500 @(9910, 14946, 4081)
$quests += Quest 7000108 'The Alpha' 'Gorefang leads the Bloodfang. Kill the alpha and the pack scatters. It prowls the far end of the western ridge - bring friends.' 'Slay Gorefang.' 'Gorefang is dead? Then we might see the spring.' 41 38 7000104 7000104 7000209 1 5200 6000 @(9966, 14898, 15599)
$quests += Quest 7000109 'The Fall of Greymane Fortress' 'Lord Godfrey has sealed himself inside Greymane Fortress with the cursed court. He must answer for the lives he sold to the curse. Enter the fortress on the hill east of Duskhaven and slay him.' 'Slay Lord Godfrey inside Greymane Fortress.' 'It is done, then. Gilneas owes you a debt no wall can repay.' 44 40 7000140 7000140 7000312 1 7500 12000 @(7726, 13082, 8346)

# ── gossip options ────────────────────────────────────────────────────────────
# A creature with its own gossip menu shows ONLY that menu's options (menu 0's generic
# "browse goods"/"train me" apply to menu-less creatures): mirror every service flag
# (verifier check C4). Texts/icons/option ids copied from the stock menu 0.
$optionDefs = @(
  @{ flag = 2;     icon = 0; text = 'GOSSIP_OPTION_QUESTGIVER';              bt = 0;    opt = 2 },
  @{ flag = 4;     icon = 1; text = 'I want to browse your goods.';          bt = 3370; opt = 3 },
  @{ flag = 16;    icon = 3; text = 'Train me.';                             bt = 0;    opt = 5 },
  @{ flag = 128;   icon = 5; text = 'Make this inn your home.';              bt = 2822; opt = 8 },
  @{ flag = 256;   icon = 6; text = 'I would like to check my deposit box.'; bt = 0;    opt = 9 },
  @{ flag = 16384; icon = 1; text = 'GOSSIP_OPTION_ARMORER';                 bt = 0;    opt = 15 }
)
function Options($items) {
  $out = @()
  foreach ($it in $items) {
    if ($it.kind -ne 'dbrow:creature_template' -or $it.body.gossip_menu_id -eq 0) { continue }
    $i = 0
    foreach ($d in $optionDefs) {
      if (($it.body.npc_flags -band $d.flag) -eq 0) { continue }
      $out += Row 'gossip_menu_option' ([ordered]@{ menu_id = $it.body.gossip_menu_id; id = $i; option_icon = $d.icon; option_text = $d.text
        option_broadcast_text = $d.bt; option_id = $d.opt; npc_option_npcflag = $d.flag; action_menu_id = 0; action_poi_id = 0
        action_script_id = 0; box_coded = 0; box_money = 0; box_text = ''; box_broadcast_text = 0; condition_id = 0 })
      $i++
    }
  }
  $out
}
$town += Options $town

Save 'c-town.json' $town
Save 'c-mobs.json' $mobs
Save 'c-dungeon.json' $dun
Save 'c-quests.json' $quests
