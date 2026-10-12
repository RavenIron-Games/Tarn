# Tarn: AI handoff (2026-10-11, version 0.4.0)

Read this before touching Tarn. It covers what the mod is, how it is built and shipped, what has been proven in game,
the traps already hit, and what is still open.

## What it is

Tarn (`com.raveniron.tarn`, studio Raven Iron, owner NomadicWar) is a Valheim BepInEx plugin (net472). It does two
things:

1. **Lakes at any height.** Valheim has one water level (world y 30). Tarn reads a per-world lake list and builds real
   vanilla water in each basin at its own level. Each lake is a flood fill over the generated ground from the lake's
   deepest point, drawn with vanilla's 64 m sea tiles and made swimmable with trigger boxes on the WaterVolume layer.
   It also draws lakes on the map and plays a lake sound. Lakes are built near the player and dropped behind, within a
   time budget each frame. There is no coroutine (house rule 2).
2. **Clearing (new in 0.2.0).** A per-world bitmask of ground kept free of vegetation (trees, rocks, bushes, pickable
   debris): the Ravenrest map's roads and lake beds. It works through vanilla's own `ZoneSystem.PlaceVegetation`
   `clearAreas` list.
3. **Lake beds (new in 0.4.0).** The same patch also clears every lake's bed straight from the lake list (config
   `Clearing.LakeBeds`, default on), so lakes made with `tarn here` need no `.clear` file. Asked for by a public user on
   2026-10-11 (via the RavensRealm session on BAMF).

It was made for the Ravenrest map (Norway's Ryfylke, real elevation, Better Continents). The map pipeline that writes
Tarn's input files is in `source\repos\RavenrestMap\v2` (see the last section).

## Repo

`C:\Users\donfr\source\repos\Tarn`, pushed to github.com/RavenIron-Games/Tarn (public, like the other released mods)
since 2026-10-11. The branch is `main`, with a tag per release (`v0.4.0` first). Commit or push only on the owner's word.

| Path | What it is |
|---|---|
| `Tarn\Plugin.cs` | The entry point: config, the Update loop, lake load and stream, `tarn` console commands. |
| `Tarn\LakeShape.cs` | One lake's shape: generator heights every 4 m over its box, and the flood (wet cells, mask). Shared by the builder and the lake beds. |
| `Tarn\LakeBuilder.cs` | Builds one lake from its shape: tiles and boxes. |
| `Tarn\ClearBeds.cs` | `tarn clearbeds <lake|all> [confirm]`: counts, then removes, the vegetation ZDOs ALREADY on lake beds. A job on the authority, with its own admin-checked routed RPC that answers the requester. |
| `Tarn\LakeBeds.cs` | Clears lake beds: wet cells near a zone become rectangles, then vanilla square clear areas. Also shapes lakes near players ahead of time, on a per-frame budget. |
| `Tarn\Water.cs` | Vanilla water templates and the swim-box handling. |
| `Tarn\Spill.cs` | The spill-point flood behind `tarn here`. |
| `Tarn\MapLakes.cs` | Draws lakes onto the minimap texture. |
| `Tarn\LakeSync.cs` | The server sends the lake list to clients (protocol 1). |
| `Tarn\Clearing.cs` | The clearing mask and the `PlaceVegetation` patch. |
| `Tarn\ClearSync.cs` | The server sends the clearing mask to clients: deflated, in 48 KB chunks (protocol 1, since 0.3.0). |
| `tools\fetch-libs.ps1` | Fills `libs\` with the game and BepInEx DLLs (gitignored). Byte-identical to the other Raven Iron repos. |
| `tools\package.ps1` | Builds `dist\RavenIron-Tarn-<v>.zip` (Thunderstore layout for Hexium). Refuses to run unless Plugin.Version, the csproj and manifest.json agree. |

Store files: `manifest.json`, `README.md`, `CHANGELOG.md`, `icon.png`. **The icon is a generated placeholder**, not
studio art.

### Build, install and package

```powershell
dotnet build Tarn\Tarn.csproj          # Debug -> Tarn\bin\Debug\Tarn.dll
.\tools\package.ps1                    # Release zip -> dist\RavenIron-Tarn-<version>.zip
```

- **Test install:** the owner's `BC` Gale profile, at
  `%APPDATA%\com.kesomannen.gale\valheim\profiles\BC\BepInEx\plugins\RavenIron-Tarn\Tarn.dll`.
- **Close Valheim before copying.** It locks the DLL.
- **Delete the old file before copying, never overwrite.** Gale hardlinks profile files.
- The log is `...\profiles\BC\BepInEx\LogOutput.log`.
- The pre-clearing DLL is kept as `Tarn.dll.pre-clearing-20261006.bak` at the repo root.

## File formats (per world, in `BepInEx\config\Tarn\`)

- **`<world>.txt`**: one lake per line, `name;x;z;level;minX;minZ;maxX;maxZ;depth`, written with InvariantCulture.
  - `x;z` is the deepest point.
  - `level` is the ABSOLUTE world y of the surface (the map pipeline writes 30 + its height above the sea).
  - The box is the area the flood may fill, plus 12 m.
  - Lines starting with `#` are comments.
  - Only the authority reads it: a dedicated server, the host, or single player.
- **`<world>.clear`**:
  - It starts with the ASCII bytes `TARNCLR1`, then an int32 size (4096), then a float32 half-extent (10500).
  - Then size × size bits, row 0 the NORTH edge, high bit first (numpy `packbits`).
  - Pixel (r, c) is centred at x = (c + 0.5) · mpx − extent, z = extent − (r + 0.5) · mpx, where mpx = 2 · extent / size.
  - Each marked pixel becomes one square clear area (half-width mpx/2 + 0.25 m) for zones within reach.
  - **Vegetation is placed by whichever machine GENERATES a zone**, usually a player in multiplayer. So since 0.3.0 the
    authority reads the file and `ClearSync` sends it to every joining player (Ravenrest's 2 MB mask deflates to
    68 KB). A connected player never reads their own file. A zone a player generates before the mask arrives keeps
    vanilla's vegetation; the log names the first such zone.
  - It only affects zones generated after it exists.

## House rules (from the studio's mods; see `RagnaroksWrath\CLAUDE.md`)

1. **Harmony.**
   - Behaviour prefixes run at `Priority.Low`, honour `__runOriginal`, and never skip the original ("no opinion" means
     run it).
   - Postfixes only decorate a return value.
   - Tarn's clearing patch is a Low-priority prefix plus a finalizer that removes exactly the areas it added.
2. **No long-lived coroutines.** Use time-budgeted work from one `Update`.
3. **Keep cosmetics off the gameplay path.** Use try/catch around anything visual.
4. **Publicized assemblies are COMPILE-TIME ONLY.** At run time the real assembly applies, with its own access
   modifiers. This bit Tarn on 2026-10-06:
   - `ZoneSystem.ClearArea` is a **private nested class** in the real `assembly_valheim.dll`.
   - `new ZoneSystem.ClearArea(...)` compiled cleanly against `libs\assembly_valheim_publicized.dll`, then threw
     `MethodAccessException` in game (Raventest21).
   - The fix: find the type with `AccessTools.Inner(typeof(ZoneSystem), "ClearArea")`, build it through a cached
     `ConstructorInfo`, and handle the list as `object` / non-generic `IList`.
   - The IL now contains NO reference to the type. Check with `ilspycmd -il Tarn.dll | grep ZoneSystem/ClearArea`,
     which must return 0.
   - **RW's `tools\revprobe` does NOT catch this kind of failure.** It said BINDS CLEAN for the broken build too. It
     checks that members exist, not that they are accessible. For any game member Tarn touches, decompile the REAL
     DLL (`<Valheim>\valheim_Data\Managed\assembly_valheim.dll`) and read its modifier.
5. **Debugging discipline.** A silent success and a silent no-op look the same. Spend a round trip on one log line
   proving the code ran.

## Lake beds (0.4.0): how it works

- **Where it runs.** Zones are generated on BOTH sides (decompiled `ZoneSystem.Update`, 1.0.16). Each client generates
  the zones near itself with `CreateLocalZones`. The server generates ghost zones around every peer with
  `CreateGhostZones`, when it has nothing local to do. The lake list is on both sides (LakeSync), so no new sync was
  needed.
- **The setting.** The server's `Clearing.LakeBeds` setting rides at the END of the lake message, as a trailing bool.
  The protocol stays 1:
  - a 0.3.0 client stops reading before the bool;
  - a 0.4.0 client checks `GetPos() < Size()`, and a 0.3.0 server means no clearing.
- **The shape.** `LakeShape` is generator heights plus the flood, the same one that draws the water.
  - The cleared ground is the wet cells, widened 4 m (each cell's own 2 m plus 2 m to the next sample).
  - Merged into maximal rectangles (`LakeBuilder.Rectangles`), then squares along each rectangle's long side, because
    vanilla's `ClearArea` is a square.
  - A zone inside a lake gets 1 area. A shore zone gets tens to hundreds, where one per cell would be thousands. Vanilla
    scans its areas linearly for every vegetation spot it tries.
- **Cost.** RavensRealm's rl003 is 589 x 606 = 357k samples. Shaping it all at once would freeze a frame for about a
  second (an estimate, not measured).
  - `LakeBeds.Warm` shapes the nearest unshaped lake within 640 m of any player, row by row within `BuildBudgetMs`.
    The players come from the local player plus `ZNet.GetPeers()` on the server. It runs only while the builder is
    idle, because the builder shapes as it goes.
  - When generation needs a lake that isn't ready, it's shaped synchronously. Anything over 20 ms is logged as
    `lake beds: shaped lake ... in N ms`. If those lines show up in play, the warm-up isn't keeping ahead.
- **Shapes are kept** across lake drops, and across list replacements whose lake line is unchanged.

## What is proven in game

- **Lakes:**
  - Water at its own height: the player swims and gets the Wet status, 60 m above the sea.
  - Lakes drawn on the map.
  - Dedicated-server sync: the TarnTest server (port 2496) on 2026-10-05.
  - 172 lakes loaded on Raventest20, 21 and 22, the Ravenrest v2 map.
- **Mask sync on the real RavensRealm dedicated server (BAMF), 2026-10-11** (0.3.0 release build, md5 e656dc16). The
  `Rogaland Admin` (07:37) and `Rogaland Client` (08:27) profiles each logged `172 lake(s) from the server (RavensRealm)`,
  then `clear sync: the server's clearing mask for 'RavensRealm' ... 11.22 km2`, then lakes built as they moved. There
  were no Tarn warnings or errors. Roads generated by a client have not been checked by eye.
- **The laptop RavensRealm server, 2026-10-06:** the server itself logged `clearing: first zone with marked ground` after
  a player joined, so a dedicated server does place vegetation (ghost zones). A client without Tarn joined that
  session, which is why the server logged no `sent` lines then.
- **Clearing:** Raventest22, 2026-10-06.
  - The log line `clearing mask for world 'Raventest22': 4096 x 4096 over +-10500 m, 11.22 km2 kept free of vegetation`,
    with no `clearing:` error.
  - The owner saw the roads clean by eye.
  - Raventest21 (the broken build) is the control: the mask loaded, then the patch threw, and debris stood on the
    roads.

## Open items, in order

1. **Prove 0.4.0 lake beds in game (built 2026-10-11, NOT yet run).**
   - In single player, on a NEW world, or on unexplored ground, put a lake on the list (`tarn at <x> <z>` from far
     away, or a line in the `.txt`).
   - Walk there. Look for the `lake beds: first zone with a lake bed at ...` line, and for no tree standing in the water.
   - Control: the same with `Clearing.LakeBeds = false`.
   - Then run a dedicated server and look for the joining player's line `... lake beds kept clear`.
2. **The race at join:** zones a player generates before the lakes and the mask arrive (a few seconds) keep vanilla's
   vegetation. That is logged, not prevented. The server's ghost zones may cover much of this. Holding zone generation
   back until both arrive would close it.
3. **Performance:**
   - `InsideClearArea` is a linear scan; lake beds add tens to hundreds of areas to shore zones.
   - The synchronous shaping fallback has not been measured.
   - If either ever shows, move the test into the prefix: a bitmap lookup, using a transpiler or a replacement check.
4. **`tarn clearbeds`: SINGLE PLAYER VERIFIED 2026-10-11** (`testing` profile, Release md5 180d298f).
   - lake1: the count found 49 objects (shrub_2 x17, Pinetree_01 x15, Pickable_Stone x5, ...). `confirm` removed 49,
     and the owner saw the trees gone.
   - The recount said nothing stands, and still did after the world was loaded again.
   - lake2: 33 found and 33 removed.
   - Each run took under 0.1 s.
   - **Still to prove: the server path,** where an admin's answer comes back by RPC and a non-admin is refused.
   Original plan:
   - In single player, make a lake with `tarn here` among trees.
   - `tarn clearbeds lake1` must count them. `tarn clearbeds lake1 confirm` must make them vanish. A second count must
     say nothing stands there.
   - Then the same from a joined admin on a server (the answer must reach the admin's console), and from a non-admin
     (refused).
   - How it works:
     - Prefabs come from `ZoneSystem.m_vegetation`, and anything with a `creator` is skipped.
     - Objects are found with `ZDOMan.FindSectorObjects(zone, new SimulationDistance(0, 0, classic: true))`, which
       lists the ZDOs of just that zone.
     - The bed test is `LakeShape.InBed`, the same ground the clearing covers.
     - Removal is `SetOwner(self)` then `DestroyZDO`, because DestroyZDO only sends for an owner.
     - Vanilla's `RPC_RemoteCommand` prints nothing back to the admin who ran it (decompiled: `InternalCommand` just
       runs `Console.TryRunCommand`), so clearbeds has its own request and reply RPCs.
5. **Placeholder icon.** It needs studio art before any store upload.
6. **0.4.0 is on Hexium** (uploaded by the owner on 2026-10-11, which answered a public user's question about `.clear`
   files). It is in git since 2026-10-11 (tag `v0.4.0`). Releases go to Hexium (team RavenIronStudios) through `tools\package.ps1`
   only, never a hand-made zip, and only on the owner's word. The owner uploads.

## Where the input files come from (the Ravenrest map, `source\repos\RavenrestMap\v2`)

- **The pipeline:**
  - `02_heights.py`: heights in Valheim bands, plus the Vikedal river.
  - `05_lakes.py`: digs and levels the real lakes, and writes `out\tarn-lakes.txt` and `tarn_lakes.bits`.
  - `07_roads_bosses.py`: the owner's drawn roads, bosses and towns.
  - `06_maps.py`: the rough, paint, vegetation and spawn maps, and `stage-v2d\tarn-clear.bin` (roads + 1 px, lake
    beds + 3 px).
- **The stage folder** is `stage-v2d`, which the BC profile's `BetterContinents.cfg` points at.
- **Per test world**, copy:
  - `tarn-lakes.txt` to `BepInEx\config\Tarn\<world>.txt`;
  - `tarn-clear.bin` to `BepInEx\config\Tarn\<world>.clear`.
- **Lake beds need dirt paint.** Vanilla grass clutter uses a fixed water level, so the pipeline paints the beds.
- **Why Better Continents can't clear vegetation:** its vegetation map is read once per 64 m zone, at the zone's
  centre. That is why Tarn does it.
- **Design notes** are in `v2\DESIGN.md`, steps 1–14.

## Owner preferences that matter here

- Terse instructions. Absorb a run of screenshots first, then fix everything together.
- Never restart the live Ravenrest server. Test worlds are new single-player worlds (RaventestNN), created on the BC
  Gale profile.
- Use Sonnet for subagents.
