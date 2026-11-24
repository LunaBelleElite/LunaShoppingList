# Luna Shopping List

**Turn a crafting list into a shopping list that tells you which world to buy each thing on.**

Luna Shopping List is a Dalamud plugin for Final Fantasy XIV. Open one of your
Artisan crafting lists, or a list you made in the plugin, and press Find prices.
It asks Universalis what everything costs right now and shows each item once,
with the worlds selling it. You can pick where to buy each thing yourself, or
let it suggest the plan with the fewest worlds for what you're willing to spend.
It also shows how many of each item you already have, can leave that out of
the bill, and reads and writes Teamcraft and Artisan lists.

It never travels and never buys anything. It tells you where to go. If you have
Lifestream, clicking a world name asks Lifestream to take you there.

I use it for my own crafting and fix bugs as I hit them. If you find one, open
an issue here and say what you did.

## Add the repository in the game

The plugin lives in its own Dalamud repository, so you add that once.

1. Type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this address:
   `https://raw.githubusercontent.com/LunaBelleElite/LunaShoppingList/main/repo.json`
3. Click the plus button, then **Save and Close**.
4. Open `/xlplugins`, search for **Luna Shopping List** and install it.

Dalamud warns you when you add a third-party repository. Anyone can publish a
plugin this way, so only add repositories from people you trust.

## Pick a list

Type `/lsl` to open the list browser. It has three tabs. A list you pick opens
in its own window, and Find prices there opens a results window. You can have
several of each open at once.

**Artisan crafting lists.** Click a list to open it. The filter box narrows the
names, and **Refresh lists** reads them from Artisan again. Tick **Untick crafted
items when a list opens** and lists open with every craftable ingredient
unticked, so you only buy raw materials. Artisan's premade and relic lists can't
be read here. Copy them with Artisan's **Export List** and use the Import tab.

**My shopping lists.** Lists you build yourself.

1. Name the list and press **Create** or Enter.
2. Select it, type an item name and pick it from the suggestions. The words can
   be in any order. Only items sold on the market board are offered.
3. Set the quantity. **Add** puts the item on the list as it is. **Add with
   subcrafts** keeps it as a goal and works out the raw materials for all your
   goals together, crystals included, so a part two goals share is only counted
   once. Where several recipes make an item, it uses the first one. The button
   is greyed out for items that can't be crafted.
4. Change a quantity and the materials follow. **Remove** takes a row or goal
   off. The materials are worked out for you, so you can't edit them.
5. **Shop for this list** opens it. **Rename** and **Delete** are there too.
   Delete changes to **Confirm delete**, so you press it twice.

**Import / Export.**

- Import: paste a Teamcraft import link, an Artisan list from **Export List**,
  or lines like `3x Iron Ore` (Teamcraft's **Copy as Text** gives you those),
  and press **Parse**. **Paste from clipboard** fills the box for you. It says
  what it found and lists any lines it couldn't match.
- Then press **Save as shopping list** or **Open without saving**. Tick **Break
  down into raw materials** before saving if you want crafts swapped for their
  materials.
- Export: pick a list. **Open in Teamcraft** opens Teamcraft's import page in
  your browser. **Copy for Artisan** copies the list with every subcraft, ready
  for Artisan's **Import List From Clipboard (Artisan Export)**. It works for My
  shopping lists only. **Copy Teamcraft link** copies a link to send someone.
  They open it in a browser, not in Teamcraft's import dialog.
- Exports only include items that can be crafted, and tell you what was left out.

## Choose what to buy

The list window shows every ingredient with a **Buy** box.

- **Where to shop**: **My region (cross-world)** is the worlds you can reach from
  a main-city aetheryte. **All data centers** adds the other regions, which need
  Data Center Travel. Every list window shares this choice.
- Untick anything you don't want to buy. **Tick all** ticks every row, and reads
  **Untick all** once they're all ticked. Your ticks are saved for Artisan lists and My
  shopping lists.
- **Untick items I have enough of** unticks rows where your bags, saddlebag and
  retainers already cover the quantity. It doesn't count your house.
- **Untick items in my house** unticks rows your house already covers.
- **Untick crafted items** unticks everything that can be crafted, so only raw
  materials are left. Crystals, shards and clusters stay as they are. If recipe
  data is still loading, it asks you to press it again.
- Those three buttons also untick rows you ticked by hand, and leave a row
  ticked if its count isn't known yet. **Undo** takes back the last button you
  pressed. It only goes back one step, and loading the list or ticking a row
  yourself clears it.
- Hover a greyed-out button to see why. Find prices needs at least one row ticked.
- **Buy only what I lack**, next to Find prices, is off until you tick it. Then
  Find prices only prices what's left after your bags, saddlebag and retainers.
  Hover it to read this. The results show `need 10 (you hold 40 of 50)`, and a
  dim note lists items you already have enough of.
- **Ignore listings older than (hours)**, in the list browser, skips old
  listings. It starts at 24, and 0 turns it off. The results say how many
  listings were hidden.
- Click a column header to sort, and click again to reverse it. Closing the
  window puts the list's own order back.
- **Reload list** reads the list again. Use it after changing the list somewhere
  else, or when the window says recipe data is still loading. If recipe data
  fails to load, it tries 3 times and stops, and Reload list starts it again.
- **Find prices** gets prices from Universalis and opens the results window.
  **Cancel build** stops it.

The columns:

- **On you** counts your bags, crystals and saddlebag. **Retainers** counts what
  your retainers hold, market listings included. Hover it to see which retainer.
  **House** counts furniture placed in your house and its storeroom. Hover it to
  see each house. A green number means you have enough, and a `-` means it
  doesn't know yet.
- **Vendor** shows when an NPC sells the item, for gil or a currency. Hover it
  for the vendor and where they stand. An item no vendor sells says
  **Craftable** if it can be crafted, and a craftable item with a vendor gets
  (craftable) after the vendor. A known monster drop adds "monster drop", as in
  **Craftable, monster drop**. Hover it for a pointer to the Monster Loot Hunter
  plugin, which can tell you which monsters.

## Plan the route

- **Willing to pay extra** takes a percent or an amount of gil over the cheapest
  total. It picks the plan with the fewest worlds within that. Click another
  option in the list to use it, or **Reset to cheapest** to go back.
- The item list on the left follows the route, first world first. Items start
  collapsed, so open one to see every world selling it. Untick **Show item
  list** to give the route the whole window.
- Type a quantity next to a world to change what you buy there. The totals
  follow, and the header says the plan was edited by hand.
- The **Route** panel lists each world and what to buy there. When you shop on
  more than one data center, the worlds sit under a heading for each, with its
  gil subtotal. Click an item name to copy it for the market board search.
- Click a world name, in the route or in an open item, to ask Lifestream to take
  you there. If it can't go, Lifestream tells you in the game.
- The **Age** column marks a listing stale when it's over a day old.
- Press Find prices again to refresh. Quantities you typed are reset, and the
  budget stays.

## What it won't do

It only takes what you own off the bill if you tick Buy only what I lack.
Without it, Find prices prices the full quantity, so untick what you don't need.
With it, your house still isn't counted, and an item whose count isn't known
yet is bought in full.

It only sees a retainer or the saddlebag while it's open, so it remembers what
they held last time, per character. Open each retainer once. Equipped and
armoury items aren't counted.

It only reads your house while the Furnishings window is open and you're inside
the house. Open Furnishings once in each house. The yard and the outdoor
storeroom aren't counted.

It knows about 237 monster drops, from a list Teamcraft keeps, because the
game's own data has none. Plenty of real drops show nothing, so a blank doesn't
mean the item isn't a drop.

It doesn't check whether you can buy from a vendor. Some need a quest or a rank
first. A few vendors can't be matched to an NPC, so they don't show.

Its plans come from a fast search. They're good, but nobody has proven them the
cheapest.

It prices the cheapest listing of either quality and doesn't show which is HQ.

It works on the global client only. The China and Korea clients use different
data, and it hasn't been tried on them.

## Why the numbers can be a little off

Universalis is built from what other players upload, so a listing can be gone by
the time you get there. Prices leave out market tax. And it assumes you can buy
part of a stack, so where the game makes you take the whole stack, you pay more
than shown.

## What you need

- Final Fantasy XIV on the global client, with Dalamud (API level 15).
- Artisan, to open Artisan lists. My shopping lists and Import work without it.
- Lifestream, only for clickable world names.
- AllaganTools if you like. When it's installed, Retainers shows the higher of
  its count and the plugin's own.
- An internet connection. Prices come from Universalis. When the plugin starts
  it also downloads the monster drop list from Teamcraft's public repository on
  GitHub. That request sends nothing about you. It keeps a copy in the plugin's
  config folder and uses it if the next download fails 3 times. With no copy,
  you get no drop labels that session.

## Something not working right?

[Open an issue](https://github.com/LunaBelleElite/LunaShoppingList/issues) and
tell me what happened. I can't promise a fix by tomorrow, but I read every one.
The form asks for your plugin and Dalamud versions and what you did. If you can,
paste the lines from `/xllog` around the time it went wrong. That's usually what
turns "it broke" into something I can chase down.

## Licence and credits

Luna Shopping List is free software under the GNU Affero General Public License
version 3, which you'll find in the `LICENSE` file. Copyright 2026 LunaBelleElite.

Prices come from [Universalis](https://universalis.app), a community-run market
board service. Thanks to the people who keep it running. The monster drop list
comes from Teamcraft's open repository. Thanks to its authors for sharing it.

Teamcraft and Artisan are separate projects, and this isn't affiliated with
either. It isn't affiliated with or endorsed by Square Enix. Final Fantasy is a
trademark of Square Enix.

It started from the SamplePlugin template the Dalamud authors publish.
