# Changelog

## 0.4.1 (2026-10-12)

- **How far lake-bed clearing reaches past the water is a setting now:** `Clearing.LakeBedShore`, in metres. The
  default is 2, the same as 0.4.0.
  - A lake's shape is sampled every 4 m, so at the default the cleared edge lands 0 to 4 m past the real waterline,
    about 2 m on average.
  - Lower it for less bare shore. At 0, a bush can still stand in the shallows here and there.
  - `tarn clearbeds` uses the same distance.
  - In multiplayer, the server's value applies to everyone; it's sent with the lakes. A 0.4.0 server counts as 2.
- **Every setting follows the server.** The server sends all of Tarn's settings to each player who joins, and again
  to everyone whenever one changes. While connected, a player's game uses the server's values in memory only; their
  own config file is untouched, so their settings are back in single player. A setting changes on the server when:
  - an admin runs a `tarn` command. `tarn depth` from a player now goes to the server, admins only, and comes back to
    everyone.
  - a config-manager mod changes it.
  - the server's `.cfg` file is edited. The server notices within a few seconds, and `tarn reload` re-reads it at once.
  - A 0.4.0 server sends no settings, so players keep their own (and its lake message's lake-bed values).
- **The store page links to the source** on GitHub.

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
