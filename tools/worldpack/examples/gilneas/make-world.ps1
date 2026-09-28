# Gilneas world batch: re-stamped tiles (buildings kept, Shadowfang dropped, subzones painted), subzone
# areas, map lights, graveyards, the fortress ghost entrance and the four portals.
$ErrorActionPreference = 'Stop'
$out = $PSScriptRoot
function Row($table, $body) { [ordered]@{ kind = "dbrow:$table"; body = $body } }
function F($v) { [ordered]@{ f = [double]$v } }
$items = @()

$paints = @(
  [ordered]@{ areaId = 7007; x = -1300; y = -66;  radius = 150 },   # The Greymane Wall
  [ordered]@{ areaId = 7002; x = -921;  y = -56;  radius = 210 },   # Duskhaven
  [ordered]@{ areaId = 7005; x = -420;  y = -120; radius = 120 },   # Greymane Manor
  [ordered]@{ areaId = 7003; x = -640;  y = -740; radius = 220 },   # Stormglen
  [ordered]@{ areaId = 7006; x = -147;  y = -558; radius = 80 },    # Emberstone Mine
  [ordered]@{ areaId = 7004; x = 400;   y = 260;  radius = 180 }    # Keel Harbor
)
for ($i = 0; $i -lt 4; $i++) { for ($j = 0; $j -lt 5; $j++) {
  $items += [ordered]@{ kind = 'tile'; body = [ordered]@{
    map = 800; col = 30 + $i; row = 30 + $j; sourceMap = 'Azeroth'; sourceCol = 27 + $i; sourceRow = 29 + $j
    keepDoodads = $true; keepWmos = $true; dropWmos = @('ld_shadowfang.wmo'); areaId = 7001; areaPaint = $paints } }
} }

# Subzones: AreaTable.dbc (clone of Silverpine 130) + area_template. Explore bit = 1100 + (id - 7000).
# Faction is CHOSEN, never inherited: the clone carried Silverpine's FactionGroupMask 4 (Horde) and the minimap
# painted Duskhaven red for Alliance players while area_template said 0. Field 20 and team agree: 2 = Alliance
# (0 contested, 4 Horde).
foreach ($a in @(@(7002, 'Duskhaven', 36), @(7003, 'Stormglen', 36), @(7004, 'Keel Harbor', 38), @(7005, 'Greymane Manor', 40),
                 @(7006, 'Emberstone Mine', 38), @(7007, 'The Greymane Wall', 35))) {
  $id = $a[0]; $bit = 1100 + ($id - 7000)
  $items += [ordered]@{ kind = 'dbc:AreaTable'; key = "$id"; body = [ordered]@{ cloneFrom = 130; fields = [ordered]@{ '1' = 800; '2' = 7001; '3' = $bit; '10' = $a[2]; '11' = $a[1]; '20' = 2 } } }
  $items += Row 'area_template' ([ordered]@{ entry = $id; map_id = 800; zone_id = 7001; explore_flag = $bit; flags = 64; area_level = $a[2]; name = $a[1]; team = 2; liquid_type = 0 })
}

# Map-wide lights (Light.dbc row 1 = Eastern Kingdoms' global light).
$items += [ordered]@{ kind = 'dbc:Light'; key = '7001'; body = [ordered]@{ cloneFrom = 1; fields = [ordered]@{ '1' = 800 } } }
$items += [ordered]@{ kind = 'dbc:Light'; key = '7002'; body = [ordered]@{ cloneFrom = 1; fields = [ordered]@{ '1' = 801 } } }

# Graveyards: WorldSafeLocs (client + server DBC) + game_graveyard_zone.
$items += [ordered]@{ kind = 'dbc:WorldSafeLocs'; key = '7001'; body = [ordered]@{ fields = [ordered]@{ '1' = 800; '2' = (F -890); '3' = (F 60); '4' = (F 19.5); '5' = 'Gilneas, Duskhaven' } } }
$items += [ordered]@{ kind = 'dbc:WorldSafeLocs'; key = '7002'; body = [ordered]@{ fields = [ordered]@{ '1' = 800; '2' = (F -745); '3' = (F -60); '4' = (F 50); '5' = 'Gilneas, Greymane Fortress' } } }
$items += Row 'game_graveyard_zone' ([ordered]@{ id = 7001; ghost_zone = 7001; faction = 0; patch_min = 0; patch_max = 10 })
$items += Row 'game_graveyard_zone' ([ordered]@{ id = 7002; ghost_zone = 7020; faction = 0; patch_min = 0; patch_max = 10 })
foreach ($z in 7002, 7003, 7004, 7005, 7006, 7007) { $items += Row 'game_graveyard_zone' ([ordered]@{ id = 7001; ghost_zone = $z; faction = 0; patch_min = 0; patch_max = 10 }) }

# The fortress instance: ghost entrance outside on the hill.
$items += Row 'map_template' ([ordered]@{ entry = 801; patch = 0; parent = 0; map_type = 1; linked_zone = 7020; player_limit = 5; reset_delay = 0
  ghost_entrance_map = -1; ghost_entrance_x = 0; ghost_entrance_y = 0; map_name = 'Greymane Fortress'; script_name = '' })

# Portals: AreaTrigger.dbc + areatrigger_template + areatrigger_teleport.
function Portal($id, $name, $map, $x, $y, $z, $r, $toMap, $tx, $ty, $tz, $to, $minLevel) {
  @(
    [ordered]@{ kind = 'dbc:AreaTrigger'; key = "$id"; body = [ordered]@{ fields = [ordered]@{ '1' = $map; '2' = (F $x); '3' = (F $y); '4' = (F $z); '5' = (F $r) } } },
    (Row 'areatrigger_template' ([ordered]@{ id = $id; build = 5875; name = $name; map_id = $map; x = $x; y = $y; z = $z; radius = $r
      box_x = 0; box_y = 0; box_z = 0; box_orientation = 0; cooldown = 0; condition_id = 0; script_id = 0; script_name = '' })),
    (Row 'areatrigger_teleport' ([ordered]@{ id = $id; patch = 0; name = $name
      message = $(if ($minLevel -gt 0) { "You must be at least level $minLevel to enter." } else { '' })
      required_level = $minLevel; required_condition = 0; target_map = $toMap
      target_position_x = $tx; target_position_y = $ty; target_position_z = $tz; target_orientation = $to }))
  )
}
$items += Portal 7010 'Greymane Wall - to Gilneas'        0   -774   1534  20   6   800  -1255  -66  17.5 0       30
$items += Portal 7011 'Greymane Wall - to Silverpine'     800 -1307  -66   20   6   0    -730   1534 17   0       0
$items += Portal 7012 'Greymane Fortress - Entrance'      800 -767   -39   62   7   801  380    330  63.3 3.14159 35
$items += Portal 7013 'Greymane Fortress - Exit'          801 410    330   64   6   800  -745 -60 49.3 3.14159 0

$json = ConvertTo-Json -InputObject @($items) -Depth 12
[System.IO.File]::WriteAllText((Join-Path $out 'c-world.json'), $json)
Write-Host "c-world.json : $($items.Count) item(s)"
