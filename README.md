# Tarn

**Water at any height.** Valheim has one water level, so every lake above the sea is a dry hollow. Tarn puts real
vanilla water in them, at their own height. You can swim in it, sail on it, and see it on the map. It was made for
the Ravenrest map, by Raven Iron.

**Prototype.** It works and is used on test worlds, but the details may still change.

## What it does

- **Lakes at their own level.** Each lake fills its basin: everything below its level that joins its deepest point.
  The water is vanilla's own (sea tiles and water volumes), so swimming, wet status, boats, floating and the
  underwater view all behave as at sea. Lakes are built as you come near and dropped behind you, a few milliseconds
  a frame.
- **On the map.** Lakes are drawn as water on the minimap and the large map.
- **Multiplayer.** The server, or the host in single player, owns the lake list and sends it to every player. A
  dedicated server builds no water of its own.
- **Clearing (0.2.0; synced from the server since 0.3.0).** No trees, rocks, bushes or pickable debris are placed
  on ground a map maker marked, such as roads and lake beds. Tarn uses vanilla's own clear areas, the same ones that keep trees out of locations. Only what
  would have stood on a marked spot is removed; everything else is placed exactly as vanilla places it.
- **Lake beds (0.4.0).** Every lake's bed is kept clear the same way, from the lake list alone, with no `.clear` file:
  the ground under the water, and a little past its edge (`Clearing.LakeBedShore`). That includes lakes made with `tarn here`. Only zones
  generated after the lake exists are affected, so a lake made on explored ground keeps the trees already standing in
  it. In multiplayer, the server's `Clearing.LakeBeds` setting applies to everyone.
  For a lake made on explored ground, `tarn clearbeds` removes what already stands on its bed (see Commands).

## Files (per world, in `BepInEx/config/Tarn/`)

- **`<world name>.txt`**: the lake list, read on the server or host. One lake per line:
  `name;x;z;level;minX;minZ;maxX;maxZ;depth`. Here x and z are the deepest point, `level` is the absolute world
  height of the water surface, and the box is the area the lake may fill. Lines starting with `#` are ignored.
- **`<world name>.clear`**: the clearing mask (optional). The header is `TARNCLR1`, then an int32 size and a float32
  half-extent in metres, then size × size bits: row 0 is the north edge, high bit first (numpy `packbits` order).
  It's read on the server, the host, or in single player, and sent to every joining player (0.3.0+), because whoever
  generates a zone places its vegetation. It only affects zones generated after it exists, so make a new world, or
  reset those zones. If a player generates a zone in the seconds before the mask arrives, the log names it.

## Commands (console)

- `tarn here [radius] [margin]`: fill the hollow you're standing in, up to just under its spill point.
- `tarn list [n]`: the nearest lakes, and whether they're built.
- `tarn depth [scale]`: how deep lakes look (colour, foam, waves). Applies live; on a server it's the server's
  setting, so a player's `tarn depth <value>` is sent there (admins only) and reaches everyone.
- `tarn reload`: re-read this world's lake file and Tarn's config on the server.
- `tarn clearbeds <lake|all>`: count the trees, rocks, bushes and pickables already standing on lake beds in explored
  ground. Add `confirm` to remove them, which can't be undone. Only vanilla vegetation goes, never anything a player
  built. A tree grown from a planted sapling looks the same as a wild one, so it goes too if it stands on a bed.
- `tarn at <x> <z> [radius] [margin]`, `tarn remove <name|all>`, `tarn info`.

On a server, changing commands need the player to be on `adminlist.txt`.

## Config

On a server, every setting is the server's: players get them on joining and whenever they change (an admin's `tarn`
command, a config manager, or the server's `.cfg` edited, which is noticed within seconds). A player's own file is
used again in single player.

| Setting | Default | What it does |
|---|---|---|
| `Look.DepthScale` | 0.5 | How deep lakes look. |
| `Sound.LakeVolume` | 0.35 | Lake water sound. |
| `Performance.BuildRange` | 400 | Lakes within this many metres are built. |
| `Performance.BuildBudgetMs` | 2 | Milliseconds per frame spent building lakes (and shaping nearby lake beds ahead of zone generation). |
| `Clearing.LakeBeds` | true | Keep vegetation off every lake's bed. In multiplayer, the server's setting applies. |
| `Clearing.LakeBedShore` | 2 | How far past the water's edge lake-bed clearing reaches, in metres (0 to 10). Lakes are sampled every 4 m, so at 2 the cleared edge lands 0 to 4 m past the real waterline. At 0, a bush may still stand in the shallows. In multiplayer, the server's setting applies. |

## Notes

- **Grass:** vanilla's grass clutter uses a fixed water level, so a lake bed needs dirt paint on its ground, or grass
  stands up through the water. The Ravenrest map paints its beds. Clearing (the mask, or lake beds) removes trees,
  rocks and bushes, not grass.
