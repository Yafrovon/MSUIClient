# Road styles (generated)

Generated 2026-11-15 by `dotnet run -c Release --project tools/road-survey` from the stock archives in
`GameData/Data` (World Content Packs in `patch-7.MPQ` excluded). Do not edit by hand: rerun the tool. The same run
writes `MSUIClient/Data/road-styles.json`, the list the World Builder's Paths page offers under **Road style**.
How each number is measured is at the top of `tools/road-survey/Program.cs`; the paint rule that uses them is
`TerrainPaintLaw.RoadAmountAt` (client) and its verbatim copy `TerrainPaint.RoadAmountAt` (web app builder).

- **road**: a texture named as a road/path/cobble/brick/paving, measured where it forms long bands in that zone.
- **trail**: a plain dirt-type texture that forms long road-like bands in that zone (an unnamed dirt road).
- **surface**: a named paving texture used in the world but never as a measurable band (plazas, short spurs);
  it still gets a style so every road texture of the world can be picked.

Maps surveyed: Eastern Kingdoms (687 tiles), Kalimdor (1018 tiles), Alterac Valley (35 tiles), Shadowfang Keep (25 tiles), Deadmines (36 tiles), Azshara Crater (30 tiles), Razorfen Kraul (6 tiles), Razorfen Downs (24 tiles), Scarlet Monastery (36 tiles), Zul'Farrak (21 tiles), Caverns of Time (39 tiles), Scholomance (16 tiles), Zul'Gurub (25 tiles), Stratholme (20 tiles), Blackwing Lair (16 tiles), Warsong Gulch (16 tiles), Ruins of Ahn'Qiraj (40 tiles), Arathi Basin (16 tiles), Ahn'Qiraj Temple (25 tiles), Naxxramas (24 tiles).

**238 styles** - 95 road, 142 trail, 1 surface.

| Zone | Kind | Texture | Width | Soft | Strength | Ragged | Patchy | Shoulder (+yd) | Measured |
|---|---|---|---|---|---|---|---|---|---|
| Badlands | surface | `Tileset\The Badlands\CobraPillarTile02Alpha.blp` | 6 | 2 | 1 | 1 | 0.1 | - | - |
| Alterac Mountains | road | `Tileset\AlteracMtns\AlteracRockRoadBase.blp` | 2.7 | 6.3 | 0.8 | 1.6 | 0.4 | - | 3385 yd |
| Alterac Mountains | trail | `Tileset\AlteracMtns\AlteracDirtBase02.blp` | 4.5 | 10 | 0.85 | 1.7 | 0.5 | `AlteracRockBase` +8.3 | 128805 yd |
| Alterac Mountains | trail | `Tileset\SilverPine\SilverPineDirt.blp` | 4.1 | 10 | 0.9 | 1.7 | 0.55 | `SilverPineRock` +6.3 | 3727 yd |
| Alterac Mountains | trail | `Tileset\AlteracMtns\AlteracRubbleBase.blp` | 3.4 | 6.3 | 0.75 | 1.3 | 0.4 | - | 2235 yd |
| Arathi Highlands | road | `Tileset\AraithiHighlands\ArathiHighlandsCityFloor.blp` | 4.4 | 6.3 | 0.9 | 1.3 | 0.25 | `ArathiHighlandsRubbleBase` +3.1 | 5185 yd |
| Arathi Highlands | road | `Tileset\Ashenvale\AshenvaleRoad.blp` | 3 | 6.3 | 0.75 | 1 | 0.3 | `ArathiHighlandsDirt` +3.1 | 2545 yd |
| Arathi Highlands | road | `Tileset\Wetlands\Wetlandsroad01.blp` | 3.4 | 6.3 | 0.75 | 1.2 | 0.35 | - | 327 yd |
| Arathi Highlands | trail | `Tileset\AraithiHighlands\ArathiHighlandsDirt.blp` | 4.6 | 10 | 0.9 | 1.8 | 0.45 | - | 99575 yd |
| Arathi Highlands | trail | `Tileset\AraithiHighlands\ArathiHighlandsRubbleBase.blp` | 2.8 | 6.3 | 0.85 | 2 | 0.55 | - | 15763 yd |
| Arathi Highlands | trail | `Tileset\AlteracMtns\AlteracDirtBase02.blp` | 3 | 10 | 0.7 | 2 | 0.7 | `AlteracRockBase` +8.3 | 6530 yd |
| Arathi Highlands | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 3.9 | 4.2 | 0.8 | 2.1 | 0.4 | - | 2586 yd |
| Badlands | trail | `Tileset\The Badlands\BadlandsDirt.blp` | 6 | 4.2 | 0.9 | 2.1 | 0.5 | `BadlandsRock` +7.3 | 250667 yd |
| Blasted Lands | road | `Tileset\The Blasted Lands\BlastedLandsRoad.blp` | 3.6 | 4.2 | 0.9 | 1 | 0.3 | `BlastedLandsRockHighlight01` +9.4 | 5024 yd |
| Blasted Lands | trail | `Tileset\The Blasted Lands\BlastedLandsDirt01.blp` | 5.2 | 10 | 0.95 | 1.9 | 0.55 | `BlastedLandsRock` +5.2 | 146128 yd |
| Blasted Lands | trail | `Tileset\Deadwind Pass\DeadwindPassDirt.blp` | 5.3 | 4.2 | 0.9 | 1.9 | 0.55 | `DeadwindPassRockBase` +8.3 | 61551 yd |
| Burning Steppes | road | `Tileset\BurningStepps\BurningSteppsSpireRoad01.blp` | 3.8 | 6.3 | 0.95 | 0.9 | 0.3 | `BurningSteppsCharcoal01` +4.2 | 5306 yd |
| Burning Steppes | trail | `Tileset\BurningStepps\BurningSteppsAsh01.blp` | 6.5 | 4.2 | 0.9 | 1.9 | 0.45 | - | 222168 yd |
| Deadwind Pass | trail | `Tileset\Deadwind Pass\DeadwindPassDirt.blp` | 3.7 | 4.2 | 0.85 | 1.8 | 0.55 | - | 64985 yd |
| Dun Morogh | road | `Tileset\IronForge\IronForgeRock06road2.blp` | 4.1 | 6.3 | 0.85 | 1.8 | 0.45 | `IronForgeSnow05ScrapeBrown` +3.1 | 10765 yd |
| Dun Morogh | road | `Tileset\IronForge\IronforgeFootprintsAlpha.blp` | 3.9 | 4.2 | 0.95 | 2.1 | 0.25 | - | 202 yd |
| Dun Morogh | road | `Tileset\Wetlands\Wetlandsroad01.blp` | 2.8 | 10 | 0.75 | 1 | 0.25 | `WetlandsGrassDark02` +7.3 | 127 yd |
| Duskwood | road | `Tileset\Duskwood\DuskwoodCobblestone.blp` | 4.6 | 4.2 | 0.95 | 0.9 | 0.25 | `DuskwoodDirt` +7.3 | 3144 yd |
| Duskwood | road | `Tileset\Duskwood\DuskwoodCobblestone2.blp` | 3.1 | 8.4 | 0.85 | 1.6 | 0.5 | `DuskwoodGrassBase02` +3.1 | 1743 yd |
| Duskwood | road | `Tileset\Kalidar\KalidarDarnRoad.blp` | 4.4 | 4.2 | 0.85 | 0.8 | 0.15 | - | 446 yd |
| Duskwood | trail | `Tileset\Duskwood\DuskwoodDirt.blp` | 4 | 8.4 | 0.85 | 1.7 | 0.5 | - | 165815 yd |
| Duskwood | trail | `Tileset\Elwynn\ElwynnDirtBase2.blp` | 7.9 | 6.3 | 0.9 | 1.7 | 0.35 | `ElwynnRockBaseTest2` +5.2 | 17626 yd |
| Eastern Plaguelands | road | `Tileset\PlagueLandsEast\EastPlaguedRoadBase.blp` | 2.9 | 6.3 | 0.75 | 1.1 | 0.35 | `EastPlaguedBaseDirt` +5.2 | 12162 yd |
| Eastern Plaguelands | road | `Tileset\TirisFall\TirisFallStoneRoad01.blp` | 3.9 | 4.2 | 0.8 | 0.9 | 0.3 | - | 1777 yd |
| Eastern Plaguelands | road | `Tileset\Aerie Peaks\AeriePeaksRockRoadBase.blp` | 4.7 | 4.2 | 0.95 | 0.6 | 0 | `AeriePeaksRockyMudBase` +3.1 | 142 yd |
| Eastern Plaguelands | trail | `Tileset\PlagueLandsEast\EastPlaguedBaseGround.blp` | 5.4 | 6.3 | 0.85 | 1.2 | 0.35 | `EastPlaguedCracks` +4.2 | 411450 yd |
| Eastern Plaguelands | trail | `Tileset\PlagueLandsEast\EastPlaguedBaseDirt.blp` | 5.1 | 10 | 0.8 | 1.3 | 0.3 | - | 164593 yd |
| Eastern Plaguelands | trail | `Tileset\PlagueLandsWest\WestPlaguedBaseDirt.blp` | 5.1 | 10 | 0.7 | 1.1 | 0.25 | - | 4447 yd |
| Eastern Plaguelands | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 4 | 4.2 | 0.9 | 2.3 | 0.4 | - | 3746 yd |
| Elwynn Forest | road | `Tileset\Elwynn\ElwynnCobbleStoneBase.blp` | 4.2 | 6.3 | 0.8 | 0.8 | 0.25 | `ElwynnDirtBase2` +5.2 | 10079 yd |
| Elwynn Forest | trail | `Tileset\Elwynn\ElwynnDirtBase2.blp` | 4.1 | 8.4 | 0.85 | 1.7 | 0.5 | `ElwynnRockBaseTest2` +10.4 | 136589 yd |
| Elwynn Forest | trail | `Tileset\BurningStepps\BurningSteppsAsh01.blp` | 8.5 | 4.2 | 0.9 | 1.3 | 0.25 | `BurningSteppsRock01` +7.3 | 4103 yd |
| Hillsbrad Foothills | road | `Tileset\AlteracMtns\AlteracRockRoadBase.blp` | 2.7 | 6.3 | 0.75 | 1 | 0.3 | `AlteracDirtBase02` +3.1 | 4314 yd |
| Hillsbrad Foothills | road | `Tileset\SilverPine\SilverPineRoad.blp` | 2.6 | 6.3 | 0.7 | 1.3 | 0.45 | - | 522 yd |
| Hillsbrad Foothills | trail | `Tileset\AlteracMtns\AlteracDirtBase02.blp` | 4.9 | 10 | 0.85 | 1.7 | 0.45 | `AlteracRockBase` +7.3 | 133154 yd |
| Hillsbrad Foothills | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 9 | 10 | 0.85 | 1.4 | 0.25 | - | 39633 yd |
| Hillsbrad Foothills | trail | `Tileset\AlteracMtns\AlteracRubbleBase.blp` | 3 | 8.4 | 0.85 | 1.5 | 0.4 | - | 6147 yd |
| Hillsbrad Foothills | trail | `Tileset\SilverPine\SilverPineDirt.blp` | 6.2 | 4.2 | 0.95 | 1.8 | 0.35 | - | 5076 yd |
| Loch Modan | road | `Tileset\Loch Modan\LochModanBrickRoadBase.blp` | 2.4 | 6.3 | 0.8 | 1.5 | 0.45 | `LochModanDirt` +5.2 | 780 yd |
| Loch Modan | trail | `Tileset\Loch Modan\LochModanDirt.blp` | 5.1 | 4.2 | 0.85 | 1.4 | 0.3 | `LochModanScrubBrushBase` +6.3 | 136959 yd |
| Loch Modan | trail | `Tileset\Loch Modan\LochModanGravelDirtAlpha.blp` | 3.4 | 4.2 | 0.85 | 1.5 | 0.45 | `LochModanDirt` +6.3 | 11138 yd |
| Redridge Mountains | road | `Tileset\RedRidge\RedridgeRockRoadBase.blp` | 4.5 | 6.3 | 0.85 | 0.6 | 0.15 | - | 3627 yd |
| Redridge Mountains | trail | `Tileset\RedRidge\RedridgeDirtBase.blp` | 5 | 4.2 | 0.95 | 1.9 | 0.45 | `RedridgeRockBase` +7.3 | 108339 yd |
| Searing Gorge | trail | `Tileset\BurningStepps\BurningSteppsAsh01.blp` | 6.5 | 4.2 | 0.9 | 1.9 | 0.45 | - | 136491 yd |
| Silverpine Forest | road | `Tileset\SilverPine\SilverPineRoad.blp` | 3.2 | 6.3 | 0.85 | 1.1 | 0.4 | - | 8842 yd |
| Silverpine Forest | trail | `Tileset\SilverPine\SilverPineDirt.blp` | 6.2 | 4.2 | 0.9 | 1.8 | 0.4 | `SilverPineRock` +7.3 | 260662 yd |
| Silverpine Forest | trail | `Tileset\AlteracMtns\AlteracDirtBase02.blp` | 4.6 | 10 | 0.95 | 1.4 | 0.4 | `SilverPineGrass` +11.5 | 33615 yd |
| Stormwind City | trail | `Tileset\Elwynn\ElwynnDirtBase2.blp` | 5.6 | 10 | 0.9 | 2.1 | 0.45 | - | 6501 yd |
| Stranglethorn Vale | road | `Tileset\Stranglethorn\StrangleThornCobbleStoneRoad.blp` | 2.7 | 10 | 0.75 | 1.4 | 0.5 | `StrangleThornDirt03` +5.2 | 10897 yd |
| Stranglethorn Vale | road | `Tileset\ZulGurub\ZulGurubRoad01.blp` | 3.2 | 10 | 0.75 | 1.3 | 0.4 | - | 7062 yd |
| Stranglethorn Vale | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 7.3 | 10 | 0.85 | 1.8 | 0.35 | `WetlandsBaseRock` +10.4 | 315989 yd |
| Stranglethorn Vale | trail | `Tileset\ZulGurub\ZulGurubDirt01.blp` | 5.4 | 4.2 | 0.95 | 2.1 | 0.45 | - | 52942 yd |
| Stranglethorn Vale | trail | `Tileset\Duskwood\DuskwoodDirt.blp` | 3.4 | 8.4 | 0.8 | 1.4 | 0.4 | - | 1931 yd |
| Swamp of Sorrows | road | `Tileset\Swamp of Sorrows\SwampSorrowsStoneRoad07.blp` | 3.6 | 10 | 0.75 | 1.2 | 0.35 | - | 10841 yd |
| Swamp of Sorrows | trail | `Tileset\Swamp of Sorrows\SwampSorrowsdirt02.blp` | 7.9 | 10 | 0.85 | 1 | 0.25 | - | 9501 yd |
| Swamp of Sorrows | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 6.8 | 4.2 | 0.95 | 1.9 | 0.3 | - | 6601 yd |
| The Hinterlands | road | `Tileset\AlteracMtns\AlteracRockRoadBase.blp` | 3.5 | 6.3 | 0.8 | 0.8 | 0.2 | `AeriePeaksRockyMudBase` +4.2 | 2543 yd |
| The Hinterlands | road | `Tileset\Aerie Peaks\AeriePeaksRockRoadBase.blp` | 3.1 | 6.3 | 0.75 | 1 | 0.25 | `AeriePeaksRockyMudBase` +3.1 | 2376 yd |
| The Hinterlands | road | `Tileset\Aerie Peaks\AeriePeaksTrollTile.blp` | 3.4 | 10 | 0.9 | 1.4 | 0.6 | `ArathiHighlandsRubbleBase` +4.2 | 842 yd |
| The Hinterlands | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 7.1 | 10 | 0.85 | 1.7 | 0.35 | `UnderwaterRock01` +3.1 | 99169 yd |
| Tirisfal Glades | road | `Tileset\TirisFall\TirisFallStoneRoad01.blp` | 3.8 | 6.3 | 0.8 | 0.8 | 0.2 | - | 8793 yd |
| Tirisfal Glades | road | `Tileset\SilverPine\SilverPineRoad.blp` | 3.8 | 6.3 | 0.9 | 0.6 | 0.2 | - | 434 yd |
| Tirisfal Glades | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 5.7 | 6.3 | 0.75 | 1.9 | 0.35 | `WestFallRockBase` +8.3 | 48334 yd |
| Tirisfal Glades | trail | `Tileset\SilverPine\SilverPineDirt.blp` | 5.4 | 10 | 0.85 | 2 | 0.5 | `SilverPineGrass` +2.1 | 45024 yd |
| Tirisfal Glades | trail | `Tileset\AlteracMtns\AlteracDirtBase02.blp` | 12 | 10 | 1 | 1.3 | 0.2 | `SilverPineGrass` +12 | 9523 yd |
| Western Plaguelands | road | `Tileset\TirisFall\TirisFallStoneRoad01.blp` | 3.5 | 6.3 | 0.8 | 1.3 | 0.3 | - | 12630 yd |
| Western Plaguelands | road | `Tileset\Duskwood\DuskwoodCobblestone.blp` | 3.1 | 6.3 | 0.85 | 1 | 0.3 | `PlaguedEarthcreep01` +11.5 | 1529 yd |
| Western Plaguelands | trail | `Tileset\Duskwood\DuskwoodDirt2.blp` | 8.8 | 10 | 0.85 | 1.4 | 0.3 | `TirisFallRockyMudGrassHighlight` +3.1 | 40916 yd |
| Western Plaguelands | trail | `Tileset\SilverPine\SilverPineDirt.blp` | 3.8 | 10 | 0.9 | 1.9 | 0.7 | `DuskwallowMuck` +6.3 | 22490 yd |
| Western Plaguelands | trail | `Tileset\PlagueLandsWest\WestPlaguedBaseDirt.blp` | 5.4 | 10 | 0.85 | 1.1 | 0.2 | - | 10566 yd |
| Western Plaguelands | trail | `Tileset\AlteracMtns\AlteracDirtBase02.blp` | 3.4 | 6.3 | 0.75 | 1.8 | 0.6 | - | 8890 yd |
| Western Plaguelands | trail | `Tileset\PlagueLandsEast\EastPlaguedBaseDirt.blp` | 9.3 | 10 | 0.85 | 1.2 | 0.2 | `EastPlaguedBaseGround` +7.3 | 5185 yd |
| Western Plaguelands | trail | `Tileset\PlagueLandsEast\EastPlaguedBaseGround.blp` | 3.6 | 6.3 | 0.7 | 1 | 0.3 | `EastPlaguedBaseDirt` +10.4 | 3794 yd |
| Western Plaguelands | trail | `Tileset\AlteracMtns\AlteracRubbleBase.blp` | 2.8 | 8.4 | 0.75 | 1.4 | 0.55 | - | 1752 yd |
| Westfall | road | `Tileset\Westwood\WestFallCobbleStoneBase.blp` | 2.3 | 6.3 | 0.85 | 2.1 | 0.55 | - | 2248 yd |
| Westfall | road | `Tileset\Elwynn\ElwynnCobbleStoneBase.blp` | 2.3 | 4.2 | 0.85 | 0.8 | 0.5 | - | 309 yd |
| Westfall | trail | `Tileset\Westwood\WestFallDirtBase.blp` | 3.1 | 10 | 0.8 | 2 | 0.65 | - | 248691 yd |
| Westfall | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 7.1 | 8.4 | 0.85 | 1.8 | 0.35 | - | 186433 yd |
| Wetlands | road | `Tileset\Wetlands\Wetlandsroad01.blp` | 2.5 | 8.4 | 0.8 | 1.3 | 0.4 | - | 4806 yd |
| Wetlands | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 7 | 10 | 0.9 | 1.6 | 0.35 | `AlteracRockBase` +8.3 | 241590 yd |
| Wetlands | trail | `Tileset\Loch Modan\LochModanDirt.blp` | 7.4 | 4.2 | 1 | 1.6 | 0.2 | - | 1555 yd |
|  | road | `Tileset\Desolace\DesolaceDirtFootPrints.blp` | 9.9 | 10 | 0.85 | 1.7 | 0.3 | `DesolaceRock01` +12 | 9921 yd |
|  | road | `Tileset\BurningStepps\BurningSteppsBrick01.blp` | 8.4 | 10 | 0.9 | 1 | 0.2 | `SilithisSand01` +9.4 | 9753 yd |
| Ashenvale | road | `Tileset\Kalidar\KalidarRoad.blp` | 3 | 6.3 | 0.8 | 0.9 | 0.35 | - | 17057 yd |
| Ashenvale | road | `Tileset\Darkshore\DarkshoreRoad.blp` | 2.1 | 6.3 | 0.7 | 1.3 | 0.3 | - | 181 yd |
| Ashenvale | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 7.9 | 6.3 | 0.85 | 1.8 | 0.3 | `UnderwaterGrass02` +1 | 65816 yd |
| Ashenvale | trail | `Tileset\Deadwind Pass\DeadwindPassDirt.blp` | 4.3 | 10 | 0.85 | 1.4 | 0.65 | `DeadwindPassRockBase` +7.3 | 19054 yd |
| Ashenvale | trail | `Tileset\Felwood\FelwoodDirt.blp` | 3.5 | 10 | 0.75 | 1.9 | 0.65 | `DuskwoodRockBase` +10.4 | 14229 yd |
| Ashenvale | trail | `Tileset\Ashzara\AshzaraDirt.blp` | 3.9 | 4.2 | 0.75 | 1.3 | 0.45 | - | 1556 yd |
| Azshara | road | `Tileset\Ashzara\AshzaraRoad.blp` | 2.8 | 6.3 | 0.8 | 1.1 | 0.3 | `AshzaraDirt` +5.2 | 3353 yd |
| Azshara | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 8 | 10 | 0.8 | 1.7 | 0.3 | `WestFallRockBase` +11.5 | 400765 yd |
| Azshara | trail | `Tileset\Ashzara\AshzaraDirt.blp` | 4 | 6.3 | 0.8 | 2 | 0.5 | `AlteracRockBase` +7.3 | 185178 yd |
| Azshara | trail | `Tileset\AraithiHighlands\ArathiHighlandsRubbleBase.blp` | 3.4 | 6.3 | 0.8 | 1.4 | 0.4 | - | 2131 yd |
| Darkshore | road | `Tileset\Kalidar\KalidarRoad.blp` | 3.1 | 6.3 | 0.85 | 0.4 | 0.2 | `KalidarFerns` +7.3 | 519 yd |
| Darkshore | road | `Tileset\Darkshore\DarkshoreRoad.blp` | 2.4 | 8.4 | 0.7 | 1.6 | 0.5 | - | 248 yd |
| Darkshore | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 6.1 | 8.4 | 0.85 | 1.9 | 0.4 | `DarkshoreRockLighter` +11.5 | 322112 yd |
| Darnassus | road | `Tileset\Kalidar\KalidarDarnRoad.blp` | 3.9 | 4.2 | 1 | 1 | 0.2 | - | 3387 yd |
| Darnassus | trail | `Tileset\Kalidar\KalidarWoodGold.blp` | 2.9 | 10 | 0.85 | 1.5 | 0.5 | - | 3758 yd |
| Desolace | road | `Tileset\Desolace\DesolaceDirtFootPrints.blp` | 5.9 | 10 | 0.8 | 1.3 | 0.2 | - | 40037 yd |
| Desolace | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 6 | 10 | 0.85 | 2 | 0.45 | `SwampSorrowsMuckdark05` +2.1 | 269425 yd |
| Durotar | road | `Tileset\Durotar\DurotarRoad.blp` | 5.2 | 6.3 | 0.8 | 1.3 | 0.3 | `DurotarRock` +4.2 | 29709 yd |
| Durotar | trail | `Tileset\Durotar\DurotarDirt.blp` | 2.8 | 4.2 | 0.85 | 2.2 | 0.75 | `DurotarRock` +6.3 | 290792 yd |
| Durotar | trail | `Tileset\Durotar\DurotarDirtSmooth.blp` | 2.2 | 10 | 0.75 | 2.6 | 0.8 | - | 264547 yd |
| Durotar | trail | `Tileset\Barrens\BarrensBaseDirt.blp` | 7 | 6.3 | 0.85 | 1.8 | 0.35 | - | 232165 yd |
| Durotar | trail | `Tileset\Ashzara\AshzaraDirt.blp` | 11.3 | 8.4 | 0.9 | 1.9 | 0.3 | `AlteracRockBase` +8.3 | 20227 yd |
| Durotar | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 10.3 | 10 | 0.9 | 1.4 | 0.2 | `WestFallRockBase` +8.3 | 3700 yd |
| Dustwallow Marsh | road | `Tileset\Duskwallow Marsh\DuskwallowStoneRoad.blp` | 4.4 | 4.2 | 0.9 | 0.7 | 0.15 | `DuskwallowVines` +6.3 | 5959 yd |
| Dustwallow Marsh | road | `Tileset\Duskwood\DuskwoodCobblestone.blp` | 5.3 | 4.2 | 1 | 0.9 | 0.15 | `ElwynnGrassBase` +10.4 | 1527 yd |
| Dustwallow Marsh | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 8.6 | 10 | 0.85 | 1.8 | 0.25 | `WestFallRockBase` +11.5 | 97001 yd |
| Dustwallow Marsh | trail | `Tileset\Duskwallow Marsh\DuskwallowDirt.blp` | 5.5 | 10 | 0.8 | 1.5 | 0.4 | - | 26748 yd |
| Dustwallow Marsh | trail | `Tileset\Feralas\FeralasDirt.blp` | 6.3 | 6.3 | 0.9 | 1.5 | 0.3 | - | 10343 yd |
| Dustwallow Marsh | trail | `Tileset\AlteracMtns\AlteracDirtBase.blp` | 6.6 | 6.3 | 0.95 | 2.1 | 0.35 | `ElwynnGrassBase` +7.3 | 5499 yd |
| Dustwallow Marsh | trail | `Tileset\Barrens\BarrensBaseDirt.blp` | 6.2 | 10 | 0.85 | 1.5 | 0.25 | - | 3955 yd |
| Felwood | road | `Tileset\Felwood\FelwoodRoad.blp` | 3.4 | 6.3 | 0.8 | 0.7 | 0.25 | - | 8844 yd |
| Felwood | trail | `Tileset\Felwood\FelwoodDirt.blp` | 4.6 | 8.4 | 0.8 | 1.7 | 0.55 | `DuskwoodRockBase` +7.3 | 342626 yd |
| Feralas | road | `Tileset\Feralas\FeralasRoad.blp` | 2.9 | 6.3 | 0.75 | 1 | 0.3 | `FeralasBrush` +7.3 | 7715 yd |
| Feralas | trail | `Tileset\Feralas\FeralasDirt.blp` | 4 | 4.2 | 0.9 | 2 | 0.55 | - | 573493 yd |
| Feralas | trail | `Tileset\AlteracMtns\AlteracDirtBase02.blp` | 7.3 | 10 | 0.85 | 1.7 | 0.35 | `AlteracRockBase` +4.2 | 254708 yd |
| Feralas | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 8.4 | 10 | 0.85 | 1.5 | 0.3 | `AlteracRockBase` +8.3 | 187452 yd |
| Feralas | trail | `Tileset\AlteracMtns\AlteracRubbleBase.blp` | 3.6 | 10 | 0.85 | 1.4 | 0.5 | - | 14036 yd |
| Gates of Ahn'Qiraj | road | `Tileset\BurningStepps\BurningSteppsBrick01.blp` | 7 | 10 | 0.9 | 1 | 0.25 | `SilithisSand01` +8.3 | 2693 yd |
| Hyjal | road | `Tileset\Hyjal\AlphaHyjalRoad.blp` | 3.6 | 6.3 | 0.8 | 0.7 | 0.2 | `HyjalGrassBase` +10.4 | 7129 yd |
| Hyjal | road | `Tileset\BurningStepps\BurningSteppsSpireRoad01.blp` | 2.9 | 6.3 | 0.8 | 1.1 | 0.4 | `BurningSteppsAsh01` +9.4 | 752 yd |
| Hyjal | trail | `Tileset\BurningStepps\BurningSteppsAsh01.blp` | 3.1 | 10 | 0.7 | 1.7 | 0.5 | `BurningSteppsRock02` +7.3 | 15155 yd |
| Moonglade | road | `Tileset\Kalidar\KalidarDarnRoad.blp` | 5 | 4.2 | 0.9 | 1 | 0.1 | - | 2649 yd |
| Mulgore | road | `Tileset\Mullgore\MullgoreRoad01.blp` | 2.7 | 6.3 | 0.85 | 0.9 | 0.3 | `MullgoreBaseDirt` +2.1 | 4653 yd |
| Mulgore | road | `Tileset\Barrens\BarrensRoad01.blp` | 2.9 | 6.3 | 0.8 | 1.1 | 0.4 | `BarrensBaseDirt` +6.3 | 265 yd |
| Mulgore | trail | `Tileset\Mullgore\MullgoreBaseDirt.blp` | 6.3 | 10 | 0.85 | 1.8 | 0.45 | `MullgoreRockSolid` +6.3 | 191163 yd |
| Mulgore | trail | `Tileset\Barrens\BarrensBaseDirt.blp` | 4.2 | 6.3 | 0.9 | 1.5 | 0.35 | - | 10590 yd |
| Orgrimmar | trail | `Tileset\Durotar\DurotarDirt.blp` | 6.8 | 2.1 | 0.95 | 1.9 | 0.4 | `DurotarRock` +6.3 | 13377 yd |
| Orgrimmar | trail | `Tileset\Durotar\DurotarDirtSmooth.blp` | 2.4 | 10 | 0.8 | 3 | 0.8 | - | 8010 yd |
| Silithus | road | `Tileset\BurningStepps\BurningSteppsBrick01.blp` | 6.4 | 10 | 0.95 | 1.2 | 0.25 | `SilithisSandDetail` +2.1 | 1545 yd |
| Silithus | trail | `Tileset\Tanaris\TanarisPurpleBug03.blp` | 3.2 | 10 | 0.85 | 2.7 | 0.75 | `TanarisPurpleBug01` +3.1 | 53077 yd |
| Silithus | trail | `Tileset\Feralas\FeralasDirt.blp` | 9.1 | 4.2 | 0.95 | 2 | 0.35 | `FeralasRockBrown` +5.2 | 19399 yd |
| Silithus | trail | `Tileset\Tanaris\TanarisPurpleBug01.blp` | 2.9 | 6.3 | 0.8 | 1.7 | 0.55 | `TanarisPurpleBug03` +5.2 | 10189 yd |
| Silithus | trail | `Tileset\Deadwind Pass\DeadwindPassDirt.blp` | 3.6 | 10 | 0.85 | 2.1 | 0.7 | `DeadwindPassShale` +8.3 | 9599 yd |
| Silithus | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 6.1 | 4.2 | 0.95 | 2 | 0.3 | - | 8579 yd |
| Tanaris | road | `Tileset\The Badlands\CobraPillarTile01.blp` | 6.4 | 8.4 | 0.85 | 1.1 | 0.2 | `TanarisCrackedGround` +8.3 | 554 yd |
| Tanaris | road | `Tileset\Tanaris\TanarisDirtFootprint.blp` | 2.6 | 4.2 | 0.95 | 1 | 0.1 | - | 352 yd |
| Tanaris | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 8.5 | 8.4 | 0.9 | 1.7 | 0.3 | - | 284809 yd |
| Tanaris | trail | `Tileset\Tanaris\TanarisPurpleBug01.blp` | 3.4 | 10 | 0.85 | 1.5 | 0.55 | `TanarisPurpleBug03` +5.2 | 30477 yd |
| Tanaris | trail | `Tileset\Tanaris\TanarisPurpleBug03.blp` | 3.4 | 8.4 | 0.9 | 2.4 | 0.75 | `TanarisPurpleBug01` +3.1 | 23146 yd |
| Tanaris | trail | `Tileset\Tanaris\TanarisPurpleBug02.blp` | 2.6 | 4.2 | 0.8 | 2 | 0.6 | `TanarisRockBase01` +4.2 | 5104 yd |
| Tanaris | trail | `Tileset\Feralas\FeralasDirt.blp` | 5.5 | 10 | 0.85 | 1.5 | 0.5 | `FeralasRockBrown` +6.3 | 4400 yd |
| Teldrassil | road | `Tileset\Kalidar\KalidarRoad.blp` | 2.4 | 6.3 | 0.8 | 1.8 | 0.3 | - | 3632 yd |
| Teldrassil | trail | `Tileset\Kalidar\KalidarWoodDarker.blp` | 2.7 | 10 | 0.85 | 3 | 0.8 | `KalidarWood` +2.1 | 802311 yd |
| Teldrassil | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 9.4 | 10 | 0.9 | 1.5 | 0.25 | `DarkshoreRockLighter` +11.5 | 317499 yd |
| Teldrassil | trail | `Tileset\Kalidar\KalidarWood.blp` | 2.3 | 2.1 | 0.7 | 2.1 | 0.45 | - | 10151 yd |
| Teldrassil | trail | `Tileset\Kalidar\KalidarWoodGold.blp` | 3 | 10 | 0.8 | 1.5 | 0.45 | - | 8091 yd |
| The Barrens | road | `Tileset\Barrens\BarrensRoad01.blp` | 3 | 6.3 | 0.75 | 1 | 0.35 | `BarrensBaseDirt` +5.2 | 27406 yd |
| The Barrens | road | `Tileset\Duskwallow Marsh\DuskwallowStoneRoad.blp` | 3.2 | 4.2 | 0.8 | 0.9 | 0.15 | `BarrensBaseDirt` +2.1 | 111 yd |
| The Barrens | trail | `Tileset\Barrens\BarrensBaseDirt.blp` | 4.2 | 10 | 0.85 | 1.7 | 0.45 | - | 646554 yd |
| The Barrens | trail | `Tileset\Feralas\FeralasDirt.blp` | 3.5 | 8.4 | 0.85 | 1.6 | 0.4 | `FeralasBrush` +12 | 16171 yd |
| Thousand Needles | road | `Tileset\Desolace\DesolaceDirtFootPrints.blp` | 2.6 | 6.3 | 0.7 | 2.7 | 0.6 | - | 2247 yd |
| Thunder Bluff | road | `Tileset\Mullgore\MullgoreRoad01.blp` | 3.4 | 6.3 | 0.85 | 1.8 | 0.25 | `MullgoreBaseGrass` +8.3 | 3235 yd |
| Thunder Bluff | trail | `Tileset\Mullgore\MullgoreBaseDirt.blp` | 3.4 | 4.2 | 0.85 | 1.9 | 0.75 | `MullgoreRockSolid` +4.2 | 28530 yd |
| Un'Goro Crater | trail | `Tileset\Feralas\FeralasDirt.blp` | 5.2 | 4.2 | 0.85 | 2 | 0.5 | `FeralasRockBrown` +5.2 | 444850 yd |
| Un'Goro Crater | trail | `Tileset\Felwood\FelwoodDirt.blp` | 8.4 | 10 | 0.85 | 1.5 | 0.25 | `FeralasRockBrown` +8.3 | 11810 yd |
| Winterspring | road | `Tileset\BurningStepps\BurningSteppsSpireRoad01.blp` | 4.3 | 6.3 | 0.9 | 0.5 | 0.1 | - | 2322 yd |
| Winterspring | road | `Tileset\Winterspring Grove\WinterspringFootprints.blp` | 3.4 | 10 | 0.7 | 1.4 | 0.35 | - | 144 yd |
| Winterspring | trail | `Tileset\BurningStepps\BurningSteppsAsh01.blp` | 6 | 4.2 | 0.9 | 1.5 | 0.4 | `BurningSteppsCharcoal01` +7.3 | 25963 yd |
| Winterspring | trail | `Tileset\Winterspring Grove\WinterspringDirt.blp` | 8 | 6.3 | 0.9 | 0.7 | 0.1 | - | 12985 yd |
| Alterac Valley | trail | `Tileset\Winterspring Grove\WinterspringDirt.blp` | 4.9 | 8.4 | 0.8 | 1.4 | 0.25 | - | 17490 yd |
| Alterac Valley | trail | `Tileset\The Blasted Lands\BlastedLandsDirt01.blp` | 13.3 | 10 | 0.95 | 1.6 | 0.25 | `BadlandsRock` +6.3 | 4040 yd |
| Alterac Valley | trail | `Tileset\Deadwind Pass\DeadwindPassDirt.blp` | 3.4 | 6.3 | 0.75 | 1.4 | 0.55 | - | 3076 yd |
| Shadowfang Keep | road | `Tileset\SilverPine\SilverPineRoad.blp` | 3.2 | 6.3 | 0.85 | 1.1 | 0.4 | - | 7309 yd |
| Shadowfang Keep | road | `Tileset\AlteracMtns\AlteracRockRoadBase.blp` | 9.4 | 2.1 | 1 | 0.5 | 0 | - | 186 yd |
| Shadowfang Keep | trail | `Tileset\SilverPine\SilverPineDirt.blp` | 5.8 | 4.2 | 0.9 | 1.8 | 0.45 | - | 172673 yd |
| The Deadmines | trail | `Tileset\Deadwind Pass\DeadwindPassDirt.blp` | 3.9 | 2.1 | 0.85 | 2.2 | 0.55 | - | 272882 yd |
| Azshara Crater | road | `Tileset\Ashzara\AshzaraRoad.blp` | 2.3 | 6.3 | 0.7 | 1.8 | 0.25 | `AshzaraDirt` +3.1 | 877 yd |
| Azshara Crater | trail | `Tileset\Ashzara\AshzaraDirt.blp` | 6.8 | 10 | 0.85 | 1.8 | 0.45 | `AlteracRockBase` +8.3 | 214945 yd |
| Razorfen Kraul | road | `Tileset\Barrens\BarrensRoad01.blp` | 2.9 | 6.3 | 0.75 | 1.1 | 0.4 | `BarrensBaseDirt` +5.2 | 2192 yd |
| Razorfen Kraul | road | `Tileset\Feralas\FeralasRoad.blp` | 2.4 | 8.4 | 0.7 | 1.2 | 0.35 | `FeralasBrush` +10.4 | 237 yd |
| Razorfen Kraul | trail | `Tileset\Barrens\BarrensBaseDirt.blp` | 3.9 | 6.3 | 0.85 | 1.7 | 0.45 | - | 16671 yd |
| Razorfen Kraul | trail | `Tileset\Feralas\FeralasDirt.blp` | 3.4 | 10 | 0.85 | 1.7 | 0.65 | `FeralasBrush` +10.4 | 15706 yd |
| Zul'Farrak | road | `Tileset\Tanaris\TanarisDirtFootprint.blp` | 2.6 | 4.2 | 0.95 | 1 | 0.1 | - | 352 yd |
| Zul'Farrak | road | `Tileset\Desolace\DesolaceDirtFootPrints.blp` | 2.4 | 8.4 | 0.65 | 2.6 | 0.6 | - | 148 yd |
| Zul'Farrak | road | `Tileset\The Badlands\CobraPillarTile01.blp` | 4.9 | 4.2 | 0.95 | 1.5 | 0.1 | - | 97 yd |
| Zul'Farrak | trail | `Tileset\Feralas\FeralasDirt.blp` | 5.7 | 6.3 | 0.9 | 1.9 | 0.55 | `FeralasRockBrown` +6.3 | 62055 yd |
| Old Hillsbrad Foothills | road | `Tileset\AlteracMtns\AlteracRockRoadBase.blp` | 2.7 | 6.3 | 0.8 | 1.3 | 0.35 | `AlteracDirtBase02` +3.1 | 7767 yd |
| Old Hillsbrad Foothills | road | `Tileset\AraithiHighlands\ArathiHighlandsCityFloor.blp` | 4.3 | 6.3 | 0.9 | 1.5 | 0.25 | `ArathiHighlandsRubbleBase` +4.2 | 1939 yd |
| Old Hillsbrad Foothills | road | `Tileset\SilverPine\SilverPineRoad.blp` | 4.4 | 6.3 | 0.9 | 1.2 | 0.3 | - | 1320 yd |
| Old Hillsbrad Foothills | road | `Tileset\Aerie Peaks\AeriePeaksRockRoadBase.blp` | 3.5 | 6.3 | 0.75 | 0.6 | 0.2 | - | 1224 yd |
| Old Hillsbrad Foothills | road | `Tileset\Ashenvale\AshenvaleRoad.blp` | 2.7 | 6.3 | 0.75 | 1.4 | 0.25 | `ArathiHighlandsDirt` +3.1 | 466 yd |
| Old Hillsbrad Foothills | road | `Tileset\TirisFall\TirisFallStoneRoad01.blp` | 4.4 | 8.4 | 0.8 | 1 | 0.25 | `WestPlaguedBaseDirt` +5.2 | 273 yd |
| Old Hillsbrad Foothills | trail | `Tileset\AlteracMtns\AlteracDirtBase02.blp` | 4.7 | 10 | 0.85 | 1.7 | 0.5 | `AlteracRockBase` +7.3 | 257720 yd |
| Old Hillsbrad Foothills | trail | `Tileset\SilverPine\SilverPineDirt.blp` | 5.2 | 6.3 | 0.9 | 1.8 | 0.45 | `SilverPineRock` +8.3 | 79800 yd |
| Old Hillsbrad Foothills | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 8.6 | 10 | 0.85 | 1.4 | 0.25 | - | 23786 yd |
| Old Hillsbrad Foothills | trail | `Tileset\AraithiHighlands\ArathiHighlandsDirt.blp` | 4.9 | 10 | 0.9 | 1.6 | 0.4 | - | 16955 yd |
| Old Hillsbrad Foothills | trail | `Tileset\AlteracMtns\AlteracRubbleBase.blp` | 3.1 | 8.4 | 0.8 | 1.4 | 0.4 | - | 8104 yd |
| Old Hillsbrad Foothills | trail | `Tileset\AraithiHighlands\ArathiHighlandsRubbleBase.blp` | 2.5 | 4.2 | 0.85 | 2.4 | 0.7 | `ArathiHighlandsCityFloor` +7.3 | 2757 yd |
| The Black Morass | road | `Tileset\Swamp of Sorrows\SwampSorrowsStoneRoad07.blp` | 4.1 | 10 | 0.75 | 1.4 | 0.3 | `SwampSorrowsMuckdark01` +5.2 | 205 yd |
|  | road | `Tileset\TirisFall\TirisFallStoneRoad01.blp` | 3.7 | 6.3 | 0.8 | 0.8 | 0.25 | - | 2841 yd |
|  | road | `Tileset\PlagueLandsEast\EastPlaguedRoadBase.blp` | 2.6 | 6.3 | 0.75 | 1.2 | 0.35 | `EastPlaguedBaseDirt` +7.3 | 2093 yd |
| Scholomance | road | `Tileset\Duskwood\DuskwoodCobblestone.blp` | 3.1 | 6.3 | 0.85 | 1 | 0.3 | `PlaguedEarthcreep01` +11.5 | 1528 yd |
| Scholomance | road | `Tileset\PlagueLandsEast\EastPlaguedRoadBase.blp` | 2 | 10 | 0.75 | 3 | 0.25 | `EastPlaguedBaseDirt` +6.3 | 102 yd |
| Scholomance | trail | `Tileset\Duskwood\DuskwoodDirt2.blp` | 8.5 | 10 | 0.85 | 1.4 | 0.3 | `TirisFallRockyMudGrassHighlight` +3.1 | 33737 yd |
| Scholomance | trail | `Tileset\PlagueLandsEast\EastPlaguedBaseGround.blp` | 3.9 | 10 | 0.75 | 1.2 | 0.45 | - | 25955 yd |
| Scholomance | trail | `Tileset\PlagueLandsEast\EastPlaguedBaseDirt.blp` | 6.9 | 10 | 0.9 | 1.4 | 0.3 | - | 14546 yd |
| Scholomance | trail | `Tileset\AlteracMtns\AlteracRubbleBase.blp` | 2.8 | 8.4 | 0.75 | 1.4 | 0.55 | - | 1752 yd |
| Zul'Gurub | road | `Tileset\ZulGurub\ZulGurubRoad01.blp` | 3.2 | 10 | 0.75 | 1.3 | 0.35 | - | 6912 yd |
| Zul'Gurub | road | `Tileset\The Blasted Lands\BlastedLandsRoad.blp` | 3.9 | 6.3 | 0.85 | 0.7 | 0.2 | `BlastedLandsRockHighlight01` +8.3 | 2937 yd |
| Zul'Gurub | road | `Tileset\Stranglethorn\StrangleThornCobbleStoneRoad.blp` | 2.6 | 8.4 | 0.7 | 1.3 | 0.45 | - | 879 yd |
| Zul'Gurub | road | `Tileset\Duskwood\DuskwoodCobblestone.blp` | 4.8 | 4.2 | 0.95 | 1 | 0.2 | - | 572 yd |
| Zul'Gurub | trail | `Tileset\Deadwind Pass\DeadwindPassDirt.blp` | 4.4 | 4.2 | 0.9 | 1.8 | 0.6 | - | 105472 yd |
| Zul'Gurub | trail | `Tileset\The Blasted Lands\BlastedLandsDirt01.blp` | 4.6 | 10 | 0.9 | 2 | 0.6 | `BlastedLandsRock` +5.2 | 96120 yd |
| Zul'Gurub | trail | `Tileset\ZulGurub\ZulGurubDirt01.blp` | 4.7 | 4.2 | 0.95 | 2.1 | 0.5 | - | 54149 yd |
| Zul'Gurub | trail | `Tileset\Duskwood\DuskwoodDirt.blp` | 4.7 | 8.4 | 0.9 | 1.7 | 0.45 | - | 43420 yd |
| Zul'Gurub | trail | `Tileset\Wetlands\Wetlandsdirt01.blp` | 8.7 | 4.2 | 0.95 | 1.9 | 0.25 | - | 3862 yd |
| Stratholme | road | `Tileset\PlagueLandsEast\EastPlaguedRoadBase.blp` | 3.7 | 4.2 | 0.8 | 0.8 | 0.25 | - | 3749 yd |
| Stratholme | trail | `Tileset\PlagueLandsEast\EastPlaguedBaseGround.blp` | 5.2 | 6.3 | 0.85 | 1.2 | 0.4 | `EastPlaguedCracks` +4.2 | 64443 yd |
| Stratholme | trail | `Tileset\PlagueLandsEast\EastPlaguedBaseDirt.blp` | 4.5 | 10 | 0.8 | 1.3 | 0.35 | - | 26949 yd |
| Blackwing Lair | road | `Tileset\BurningStepps\BurningSteppsSpireRoad01.blp` | 3.9 | 6.3 | 0.95 | 0.8 | 0.25 | `BurningSteppsCharcoal01` +4.2 | 3746 yd |
| Blackwing Lair | trail | `Tileset\BurningStepps\BurningSteppsAsh01.blp` | 6.2 | 4.2 | 0.9 | 1.9 | 0.45 | - | 254062 yd |
| Blackwing Lair | trail | `Tileset\Elwynn\ElwynnDirtBase2.blp` | 5.6 | 6.3 | 0.95 | 1.7 | 0.35 | `ElwynnGrassBase` +5.2 | 11862 yd |
| Blackwing Lair | trail | `Tileset\The Badlands\BadlandsDirt.blp` | 8.3 | 6.3 | 0.95 | 1.6 | 0.45 | `BadlandsRock` +6.3 | 6462 yd |
| Warsong Gulch | road | `Tileset\Ashenvale\AshenvaleRoad.blp` | 2.2 | 6.3 | 0.75 | 1.9 | 0.45 | - | 837 yd |
| Warsong Gulch | road | `Tileset\Barrens\BarrensRoad01.blp` | 2.7 | 6.3 | 0.75 | 1.1 | 0.3 | - | 352 yd |
| Warsong Gulch | trail | `Tileset\Barrens\BarrensBaseDirt.blp` | 5.4 | 8.4 | 0.85 | 1.8 | 0.35 | `BarrensBaseGrassGrey` +7.3 | 11940 yd |
|  | road | `Tileset\BurningStepps\BurningSteppsBrick01.blp` | 6.5 | 10 | 0.9 | 1.1 | 0.25 | `SilithisSand01` +8.3 | 3848 yd |
| Ruins of Ahn'Qiraj | road | `Tileset\BurningStepps\BurningSteppsBrick01.blp` | 7.3 | 10 | 0.9 | 1 | 0.2 | `SilithisSand01` +8.3 | 22938 yd |
| Ruins of Ahn'Qiraj | trail | `Tileset\Tanaris\TanarisPurpleBug03.blp` | 6.5 | 10 | 0.9 | 1.7 | 0.4 | `TanarisPurpleBug01` +4.2 | 19183 yd |
| Ruins of Ahn'Qiraj | trail | `Tileset\Tanaris\TanarisPurpleBug02.blp` | 5.4 | 10 | 0.95 | 1.2 | 0.25 | - | 5011 yd |
| Ruins of Ahn'Qiraj | trail | `Tileset\Tanaris\TanarisPurpleBug01.blp` | 3.7 | 4.2 | 0.9 | 1.2 | 0.3 | - | 2148 yd |
| Silithus | trail | `Tileset\Tanaris\TanarisPurpleBug03.blp` | 3.1 | 10 | 0.9 | 2.4 | 0.8 | `TanarisPurpleBug01` +4.2 | 11272 yd |
| Silithus | trail | `Tileset\Tanaris\TanarisPurpleBug01.blp` | 2.8 | 8.4 | 0.8 | 1.7 | 0.55 | `TanarisPurpleBug03` +5.2 | 4619 yd |
| Arathi Basin | trail | `Tileset\AraithiHighlands\ArathiHighlandsDirt.blp` | 4.6 | 4.2 | 0.95 | 2.3 | 0.5 | - | 40051 yd |
| Arathi Basin | trail | `Tileset\AraithiHighlands\ArathiHighlandsRubbleBase.blp` | 2.2 | 6.3 | 0.75 | 1.5 | 0.4 | - | 1901 yd |
| Ahn'Qiraj | road | `Tileset\BurningStepps\BurningSteppsBrick01.blp` | 6.5 | 10 | 0.9 | 1.3 | 0.25 | `SilithisSand01` +8.3 | 2711 yd |
| Naxxramas | road | `Tileset\PlagueLandsEast\EastPlaguedRoadBase.blp` | 3.6 | 6.3 | 0.8 | 0.8 | 0.25 | `EastPlaguedCreep03` +4.2 | 4810 yd |
| Naxxramas | trail | `Tileset\PlagueLandsEast\EastPlaguedBaseGround.blp` | 5.6 | 8.4 | 0.85 | 1.1 | 0.35 | `EastPlaguedCracks` +4.2 | 164081 yd |
| Naxxramas | trail | `Tileset\PlagueLandsEast\EastPlaguedBaseDirt.blp` | 4.7 | 10 | 0.8 | 1.3 | 0.3 | - | 56800 yd |
| Naxxramas | trail | `Tileset\PlagueLandsWest\WestPlaguedBaseDirt.blp` | 4.1 | 10 | 0.65 | 1.2 | 0.3 | `EastPlaguedCracks` +4.2 | 2377 yd |

## Coverage: every road-named ground texture in use

Textures whose area is under 100 sq yd are listed in a tile's MTEX but barely visible; they get no style.

| Texture | Area (sq yd) | Zones | Styles |
|---|---|---|---|
| `Tileset\Aerie Peaks\AeriePeaksRockRoadBase.blp` | 13623 | The Hinterlands, Old Hillsbrad Foothills, Eastern Plaguelands | 3 |
| `Tileset\Aerie Peaks\AeriePeaksTrollTile.blp` | 3474 | The Hinterlands | 1 |
| `Tileset\AlteracMtns\AlteracRockRoadBase.blp` | 66050 | Old Hillsbrad Foothills, Hillsbrad Foothills, Alterac Mountains, The Hinterlands | 5 |
| `Tileset\AraithiHighlands\ArathiHighlandsCityFloor.blp` | 32783 | Arathi Highlands, Old Hillsbrad Foothills | 2 |
| `Tileset\Ashenvale\AshenvaleRoad.blp` | 17611 | Arathi Highlands, Old Hillsbrad Foothills, Warsong Gulch | 3 |
| `Tileset\Ashzara\AshzaraRoad.blp` | 16565 | Azshara, Azshara Crater | 2 |
| `Tileset\Barrens\BarrensRoad01.blp` | 98424 | The Barrens, Razorfen Kraul, Warsong Gulch, Mulgore | 4 |
| `Tileset\BurningStepps\BurningSteppsBrick01.blp` | 398013 | Ruins of Ahn'Qiraj, Ahn'Qiraj, Gates of Ahn'Qiraj, Silithus | 6 |
| `Tileset\BurningStepps\BurningSteppsSpireRoad01.blp` | 49575 | Burning Steppes, Blackwing Lair, Winterspring, Hyjal | 4 |
| `Tileset\Darkshore\DarkshoreRoad.blp` | 14746 | Darkshore, Ashenvale, Feralas | 2 |
| `Tileset\Desolace\DesolaceDirtFootPrints.blp` | 550806 | Desolace, Thousand Needles, Zul'Farrak, Mulgore | 4 |
| `Tileset\Durotar\DurotarRoad.blp` | 157617 | Durotar | 1 |
| `Tileset\Duskwallow Marsh\DuskwallowStoneRoad.blp` | 27912 | Dustwallow Marsh, The Barrens | 2 |
| `Tileset\Duskwood\DuskwoodCobblestone.blp` | 58867 | Duskwood, Zul'Gurub, Dustwallow Marsh, Western Plaguelands | 5 |
| `Tileset\Duskwood\DuskwoodCobblestone2.blp` | 7732 | Duskwood | 1 |
| `Tileset\Elwynn\ElwynnCobbleStoneBase.blp` | 44304 | Elwynn Forest, Westfall | 2 |
| `Tileset\Felwood\FelwoodRoad.blp` | 30424 | Felwood, Ashenvale | 1 |
| `Tileset\Feralas\FeralasRoad.blp` | 32853 | Feralas, Dire Maul, Razorfen Kraul, Thousand Needles | 2 |
| `Tileset\Hyjal\AlphaHyjalRoad.blp` | 26385 | Hyjal | 1 |
| `Tileset\IronForge\IronforgeFootprintsAlpha.blp` | 3762 | Dun Morogh, Alterac Valley, Wetlands | 1 |
| `Tileset\IronForge\IronForgeRock06road2.blp` | 52916 | Dun Morogh, Searing Gorge | 1 |
| `Tileset\Kalidar\KalidarDarnRoad.blp` | 28780 | Moonglade, Darnassus, Duskwood, Eastern Plaguelands | 3 |
| `Tileset\Kalidar\KalidarRoad.blp` | 72040 | Ashenvale, Teldrassil, Darkshore | 3 |
| `Tileset\Loch Modan\LochModanBrickRoadBase.blp` | 10296 | Loch Modan | 1 |
| `Tileset\Mullgore\MullgoreRoad01.blp` | 26194 | Mulgore, Thunder Bluff | 2 |
| `Tileset\PlagueLandsEast\EastPlaguedRoadBase.blp` | 80188 | Eastern Plaguelands, Naxxramas, Stratholme, Scholomance | 5 |
| `Tileset\RedRidge\RedridgeRockRoadBase.blp` | 17515 | Redridge Mountains, Elwynn Forest | 1 |
| `Tileset\SilverPine\SilverPineRoad.blp` | 81296 | Silverpine Forest, Shadowfang Keep, Old Hillsbrad Foothills, Tirisfal Glades | 5 |
| `Tileset\Stranglethorn\StrangleThornCobbleStoneRoad.blp` | 44060 | Stranglethorn Vale, Zul'Gurub | 2 |
| `Tileset\Swamp of Sorrows\SwampSorrowsStoneRoad07.blp` | 42777 | Swamp of Sorrows, The Black Morass | 2 |
| `Tileset\Tanaris\TanarisDirtFootprint.blp` | 9281 | Tanaris, Zul'Farrak | 2 |
| `Tileset\The Badlands\CobraPillarTile01.blp` | 4867 | Tanaris, The Barrens, Zul'Farrak | 2 |
| `Tileset\The Badlands\CobraPillarTile02Alpha.blp` | 155 | Badlands | 1 |
| `Tileset\The Blasted Lands\BlastedLandsRoad.blp` | 30047 | Blasted Lands, Zul'Gurub, Swamp of Sorrows | 2 |
| `Tileset\TirisFall\TirisFallStoneRoad01.blp` | 102671 | Western Plaguelands, Tirisfal Glades, Eastern Plaguelands, Old Hillsbrad Foothills | 5 |
| `Tileset\Westwood\WestFallCobbleStoneBase.blp` | 18225 | Westfall | 1 |
| `Tileset\Wetlands\Wetlandsroad01.blp` | 29039 | Wetlands, Arathi Highlands, Dun Morogh, Eastern Plaguelands | 3 |
| `Tileset\Winterspring Grove\WinterspringFootprints.blp` | 581 | Winterspring | 1 |
| `Tileset\ZulGurub\ZulGurubRoad01.blp` | 51271 | Stranglethorn Vale, Zul'Gurub | 2 |
