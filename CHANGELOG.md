# Changelog

## 0.4.0 (2026-10-11)

- **Lake beds are kept clear on their own.** No trees, rocks, bushes or pickable debris are placed on a lake's bed,
  from the shore down. That includes lakes made with `tarn here`, so they no longer need a `.clear` file. The cleared
  ground is the same flood that shapes the water. It goes about 4 m past the waterline, so nothing stands in the
  shallows. It's on by default; the config setting is `Clearing.LakeBeds`.
  - As with the mask, only zones generated after a lake exists are affected. `tarn here` on ground you've already
    explored leaves the trees that stand there.
  - Grass still needs dirt paint on the bed: vanilla's grass follows the ground paint, not clear areas.
- **New command, `tarn clearbeds <lake|all>`,** for lakes made on ground already explored. It counts the trees, rocks,
  bushes and pickables already standing on lake beds. `tarn clearbeds <lake|all> confirm` removes them, which can't be
  undone.
  - Only what vanilla places as vegetation goes, and only on the bed as Tarn clears it. Nothing a player built is
    touched, nor stumps, logs, drops or creatures. A tree grown from a planted sapling looks exactly like a wild one,
    though, so it goes too if it stands on a bed.
  - On a server it runs on the server, for admins on `adminlist.txt` only, and the answer comes back to that player's
    console.
- **In multiplayer, the server's setting applies to everyone.** It's sent with the lakes. The server and every player
  clear the same ground, because each machine generates some of the zones. A 0.3.0 player still gets the lakes but
  clears no beds. A 0.4.0 player on a 0.3.0 server clears none either.
- **Big lakes are shaped ahead of time,** a few milliseconds a frame, for lakes near any player. Zone generation then
  finds them ready. If a zone needs a lake that isn't ready yet, it's shaped on the spot, and the log says how long
  that took when it's slow.
- **A lake re-sent by the server keeps its shape** if it hasn't changed, so `tarn here` no longer makes every player
  re-sample every lake.
- **New log lines:** the load line says whether lake beds are kept clear, and one line names the first zone that had a
  lake bed cleared.

## 0.3.0 (2026-10-06)

- **The clearing mask is synced from the server.** Vegetation is placed by whichever machine generates a zone, which in
  multiplayer is usually a player. The server (or host) now sends its `<world>.clear` to every joining player: deflated
  (Ravenrest's 2 MB mask is 68 KB) and sent in 48 KB chunks. A connected player never reads their own `.clear` file, as
  with lakes. If a zone is generated before the mask arrives, the log names it.
- **One log line proves the clearing ran:** the first zone with marked ground, and how many clear areas it got.
- The server needs 0.3.0 too: an older server never answers, and the player is told so after about a minute.

## 0.2.0 (2026-10-06)

- **Clearing:** a per-world mask (`BepInEx/config/Tarn/<world>.clear`) of ground kept free of vegetation, such as a map
  maker's roads and lake beds. It adds vanilla clear areas to `ZoneSystem.PlaceVegetation`, so only what would have
  stood on a marked spot goes. Better Continents' vegetation map can't do this, because it is read once per 64 m zone,
  at the zone's centre. Verified in game on Raventest22.
- **Fix:** the clear-area type is private in the real game, so it is reached by reflection. A first build compiled
  against the publicized assemblies threw `MethodAccessException` in game.
- **The load line names the real version.** It said "0.1.0" whatever the build.

## 0.1.0 (2026-10-05)

- Lakes at their own height from `BepInEx/config/Tarn/<world>.txt`, built as you come near.
- Lakes drawn on the map, and the lake sound.
- Server-owned lake list, synced to every player. The `tarn` console commands.
