<!-- README schema v2 -->
# 🏞️ Tarn

![TARN](https://img.shields.io/badge/TARN-WATER_AT_ANY_HEIGHT-c9a227?style=for-the-badge&labelColor=1b1b1b)
![VERSION](https://img.shields.io/badge/VERSION-0.4.2-8b1e1e?style=for-the-badge&labelColor=1b1b1b)
![CLIENT & SERVER](https://img.shields.io/badge/CLIENT_%26_SERVER-INSTALL_ON_BOTH-1f4e79?style=for-the-badge&labelColor=1b1b1b)

> *The gods poured one sea around Midgard and filled it to a single line. Every hollow in the high fells stayed dry, and the Vikings who climbed there drank snow. Now the hollows hold water, cold and still, each at its own height.*

### Real lakes above the sea. Swim in them, sail on them, see them on the map.

Valheim has one water level, so every lake above the sea is a dry hollow. Tarn puts real vanilla water in them, at their own height. It was made for the Ravenrest map, by Raven Iron.

It also keeps trees, rocks and bushes off ground a map maker marks, such as roads, and off every lake's bed. The server owns the lakes and sends them to every player.

---

![NEW IN 0.4.2](https://img.shields.io/badge/NEW_IN_0.4.2-VALHEIM_1.0.21-8b1e1e?style=for-the-badge&labelColor=1b1b1b)

**This version is Tarn built for Valheim 1.0.21.**

| You | It |
|---|---|
| **Update the server and your players together** | Valheim 1.0.21 uses network version 41: the server and every player's game must be on it to meet |
| **Keep your lakes and settings** | nothing else changes |

---

![BEFORE YOU INSTALL](https://img.shields.io/badge/BEFORE_YOU_INSTALL-READ_THIS-b8860b?style=for-the-badge&labelColor=1b1b1b)

| | If… | Then… |
|---|---|---|
| 🧪 | you want something **finished** | Tarn is a prototype. It works and is used on test worlds, but the details may still change. |
| 🤝 | you play on a **server** | install Tarn on the server and on every player, the same version. Without Tarn on the server, players get no lakes. |
| 🌲 | you want ground **kept clear** on a world you have explored | clearing only touches zones generated after a lake or mask exists. Make a new world, reset those zones, or use `tarn clearbeds` for lake beds. |
| 🌾 | you see **grass in the water** | paint the lake bed with dirt. Vanilla's grass ignores Tarn's water and its clearing. |

---

![I](https://img.shields.io/badge/I-LAKES_AT_THEIR_OWN_LEVEL-c9a227?style=for-the-badge&labelColor=1b1b1b)

**Each lake fills its basin, and behaves like the sea.**

| | |
|---|---|
| 🔹 **Fills the basin** | everything below the lake's level that joins its deepest point |
| 🔹 **Vanilla water** | swimming, the wet status, boats, floating and the underwater view all behave as at sea |
| 🔹 **On the map** | lakes are drawn as water on the minimap and the large map |
| 🔹 **The sound of water** | near a lake you hear it, a little quieter than the sea |
| 🔹 **How deep it looks** | calm and green, or blue and rough: `tarn depth` changes it live |
| 🔹 **Light on the frame rate** | lakes are built as you come near and dropped behind you, a few milliseconds a frame |

<details>
<summary>📖 <b>Read the saga</b> — how a lake is built</summary>

**The water is vanilla's own:** the sea's tiles and its water volumes, set at the lake's height. That is why everything that works at sea works on a lake.

**Each player's game builds the water.** The server, or the host in single player, owns the lake list and sends it to every player; a dedicated server builds no water of its own. Lakes within reach are built, a little at a time, and dropped again once you are well past them.

**Big lakes are shaped ahead of time,** for lakes near any player, so zone generation finds them ready.

</details>

---

![II](https://img.shields.io/badge/II-MAKE_A_LAKE-1f4e79?style=for-the-badge&labelColor=1b1b1b)

**Stand in a hollow, type one command, and it fills.**

| | |
|---|---|
| 🔹 **`tarn here`** | fills the hollow you stand in, up to just under the point where it would spill |
| 🔹 **`tarn at <x> <z>`** | the same, at a point on the map |
| 🔹 **A lake list per world** | a map maker can write every lake into one file; `tarn here` adds to it |
| 🔹 **Everyone sees it** | a new lake goes to every player at once |

<details>
<summary>📖 <b>Read the saga</b> — what makes a hollow</summary>

**A hollow is ground lower than its rim.** `tarn here` finds where the water would spill out and sets the lake just under it. Stand in the low part. A hollow that would spill into the sea gets no lake, and the command says so.

**On a server it is the server's lake.** A player's `tarn here` is sent to the server, which runs it for admins on `adminlist.txt`; the lake list is saved there.

</details>

---

![III](https://img.shields.io/badge/III-CLEARING-2e5e3a?style=for-the-badge&labelColor=1b1b1b)

**No trees in the lake. No rocks on the road.**

| | |
|---|---|
| 🔹 **Lake beds** | no trees, rocks, bushes or pickable debris are placed on a lake's bed, from the shore down, for every lake, `tarn here` lakes included; on by default |
| 🔹 **A map maker's mask** | ground a map maker marks, such as roads, is kept free the same way |
| 🔹 **Only what would stand there** | everything else is placed exactly as vanilla places it |
| 🔹 **`tarn clearbeds`** | for ground you have already explored: counts what stands on lake beds, then removes it when you add `confirm` |

<details>
<summary>📖 <b>Read the saga</b> — how clearing works, and what it never touches</summary>

**It uses vanilla's own clear areas,** the same ones that keep trees out of locations. Only what would have stood on a marked spot is left out.

**Only new ground.** Whoever generates a zone places its vegetation, so clearing works on zones generated after a lake or a mask exists. A lake made on explored ground keeps the trees already standing in it, until `tarn clearbeds`.

**`tarn clearbeds` takes only vanilla vegetation:** trees, rocks, bushes and pickables, on the bed as Tarn clears it. Nothing a player built is touched, nor stumps, logs, drops or creatures. A tree grown from a planted sapling looks the same as a wild one, so it goes too if it stands on a bed. Removing can't be undone.

**The shore.** Clearing reaches a little past the water's edge, 2 m by default (`Clearing.LakeBedShore`). Lower it for less bare shore; at 0 a bush may still stand in the shallows. Clearing removes trees, rocks and bushes, not grass: paint the bed with dirt for that.

</details>

---

![THE TOOLBOX](https://img.shields.io/badge/THE_TOOLBOX-QUICK_START_%C2%B7_COMMANDS_%C2%B7_HOW--TO-1b1b1b?style=for-the-badge&labelColor=8b1e1e)

**First ten minutes:** install Tarn with your mod manager (on the server and every player) → walk to a dry hollow above the sea → open the console (F5) → `tarn here` → swim.

| Command | |
|---|---|
| `tarn here [radius] [margin]` | fill the hollow you stand in, up to just under its spill point |
| `tarn at <x> <z> [radius] [margin]` | the same, at a point |
| `tarn list [n]` | the nearest lakes, and whether they are built |
| `tarn depth [scale]` | how deep lakes look (colour, foam, waves); applies live |
| `tarn reload` | re-read this world's lake list and Tarn's settings on the server |
| `tarn clearbeds <lake>` · `tarn clearbeds all` | count what stands on lake beds in explored ground; add `confirm` to remove it |
| `tarn remove <name>` · `tarn remove all` | remove a lake, or every lake |
| `tarn info` | the vanilla water Tarn copies |

On a server, the commands that change anything need the player on `adminlist.txt`.

<details>
<summary>📝 <b>Write a lake list</b></summary>

The lake list is `BepInEx/config/Tarn/<world name>.txt`, read on the server or the host. One lake per line:

`name;x;z;level;minX;minZ;maxX;maxZ;depth`

`x` and `z` are the lake's deepest point, `level` is the absolute world height of the water's surface, and the box is the area the lake may fill. Lines starting with `#` are ignored. After editing, run `tarn reload`.

</details>

<details>
<summary>🗺️ <b>Make a clearing mask</b></summary>

The mask is `BepInEx/config/Tarn/<world name>.clear`, optional. Its header is `TARNCLR1`, then an int32 size and a float32 half-extent in metres, then size × size bits: row 0 is the north edge, high bit first (numpy `packbits` order).

It is read on the server, the host, or in single player, and sent to every joining player, because whoever generates a zone places its vegetation. It only affects zones generated after it exists, so make a new world, or reset those zones. If a player generates a zone in the seconds before the mask arrives, the log names it.

</details>

<details>
<summary>🧹 <b>Clear the beds of lakes on explored ground</b></summary>

Run `tarn clearbeds <lake>` (or `all`) to count the trees, rocks, bushes and pickables standing on lake beds. Check the count, then run it again with `confirm` on the end to remove them. It can't be undone. On a server it runs on the server, for admins only, and the answer comes back to your console.

</details>

<details>
<summary>🎨 <b>Tune how lakes look</b></summary>

`tarn depth` shows the current value. Vanilla's water colour, foam and waves follow depth, so `tarn depth 0.2` makes lakes calm and green, and `tarn depth 1` makes them look like the sea. On a server it is the server's setting: a player's `tarn depth <value>` is sent there, admins only, and reaches everyone.

</details>

---

![MULTIPLAYER](https://img.shields.io/badge/MULTIPLAYER-ONE_WATER_FOR_EVERYONE-1f4e79?style=for-the-badge&labelColor=1b1b1b)

| Without Tarn on the server | Needs Tarn on the server | Needs Tarn on both ends |
|---|---|---|
| a player's Tarn finds no lakes, and its log says so after about a minute | the lake list and the clearing mask · every setting · the commands that change anything | the water itself, built on each player's game · lakes on the map · the lake sound · clearing in zones a player generates |

**Every setting follows the server.** Players get the server's settings when they join and whenever one changes: an admin's `tarn` command, a config manager, or the server's `.cfg` edited, which is noticed within seconds. A player's own file is never touched and is used again in single player.

**Same version on both.** If the server's Tarn differs from a player's, the player's log warns: update to match.

---

![COMPATIBILITY](https://img.shields.io/badge/COMPATIBILITY-PLAYS_WELL_WITH_OTHERS-2e5e3a?style=for-the-badge&labelColor=1b1b1b)

| Mod or map | |
|---|---|
| **Config managers** | A change made with one on the server reaches every player. |
| **The Ravenrest map** | The map Tarn was made for: its pipeline writes the lake list and the mask, and paints its lake beds with dirt. |
| **Better Continents** | Ravenrest is a Better Continents map. Better Continents' vegetation map is read once per 64 m zone, too coarse to keep a road clear; Tarn's clearing does that job. |

---

![SETTINGS](https://img.shields.io/badge/SETTINGS-BEPINEX_CONFIG-b8860b?style=for-the-badge&labelColor=1b1b1b)

All in `BepInEx/config/com.raveniron.tarn.cfg`, and all safe to leave alone.

<details>
<summary>🔒 <b>Server-synced</b> — on a server, the server's values apply to everyone</summary>

| Section | Setting | What it does |
|---|---|---|
| Look | `DepthScale` | how deep lakes look (0.5 by default); `tarn depth` sets it live |
| Sound | `LakeVolume` | the lake water sound; 0 is off |
| Performance | `BuildRange` | lakes within this many metres are built (400 by default) |
| Performance | `BuildBudgetMs` | milliseconds per frame spent building and shaping lakes (2 by default) |
| Clearing | `LakeBeds` | keep vegetation off every lake's bed (on by default) |
| Clearing | `LakeBedShore` | how far past the water's edge clearing reaches, in metres, 0 to 10 (2 by default); `tarn clearbeds` uses it too |

</details>

🖥️ **Local:** none while you are connected. In single player your own file is used.

---

![CREDITS](https://img.shields.io/badge/CREDITS-THANK_YOU-1b1b1b?style=for-the-badge&labelColor=c9a227)

**Needs:** [BepInExPack Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) `5.4.2360` · **Source:** [GitHub](https://github.com/RavenIron-Games/Tarn) · **Website:** <https://ravenirongames.com/>

*Made for the Ravenrest map, by Raven Iron.*
