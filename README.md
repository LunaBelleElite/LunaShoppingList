# Luna Shopping List

**Turn an Artisan crafting list into a shopping list that tells you which world to buy each thing on.**

Luna Shopping List is a Dalamud plugin for Final Fantasy XIV. Open one of your
Artisan crafting lists, or a list you made in the plugin, press Build shopping
list, and it
asks Universalis what everything costs right now. You get each item once, with
the worlds selling it, and you decide where to buy what. If you'd rather not
decide, it will suggest a plan, and the route and totals keep up as you change
quantities. The ingredient list also shows how many of each item you already
hold. It can also bring in a Teamcraft list and send one back out.

It never travels on its own and it doesn't buy anything. It tells you where to
go. If you have Lifestream, clicking a world name asks it to take you there.
The shopping is yours.

I use it for my own crafting in the game, and I fix bugs as I run into them. If
you hit one, open an issue on this repository and say what you did.

## Add the repository in the game

Dalamud installs plugins from repositories, and this one lives in its own, so
you add it once.

1. Type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this address:
   `https://raw.githubusercontent.com/LunaBelleElite/LunaShoppingList/main/repo.json`
3. Click the plus button, then **Save and Close**.
4. Open `/xlplugins`, search for **Luna Shopping List** and install it.

Dalamud warns you about third-party repositories when you add one, and it's
right to. Anyone can publish a plugin this way, so only add repositories from
people you trust.

## How to use each part

Type `/lsl` and the list browser opens, with three tabs: Artisan crafting
lists, My shopping lists and Import / Export. Pick a list and it gets its own
window. **Build shopping list** in that window opens a results window. You can have several of
each open at once.

**Artisan crafting lists tab.** Your Artisan lists are shown here.

- Click one to open it. Type in the filter box to narrow the names.
- Refresh re-reads them from Artisan.
- Tick **Exclude crafted items** and lists open with every crafted ingredient
  unticked, so you only buy raw materials. You can still tick things yourself.

**My shopping lists tab.** Lists you put together yourself.

1. Name the list and press Create.
2. Select it, type an item name and pick it from the suggestions. Only items
   that sell on the market board are offered.
3. Set the quantity. **Add** puts the item itself on the list, to buy as it is.
   **Add with subcrafts** keeps the finished item as a goal, and the list works
   out the raw materials for all your goals together, crystals included. A
   part that two goals share is crafted once. Where several recipes make an
   item it uses the first one it finds, and the button is greyed out for
   anything that can't be crafted.
4. Change a quantity in a row or a goal and the materials follow. Remove takes
   a row or goal off. The materials section is worked out for you, so you can't
   edit it.
5. **Shop for this list** opens it. Rename and Delete sit on the line below
   the list's name, after it. Delete asks you to click twice.

**Import / Export tab.** Brings a list in from Teamcraft or Artisan, or sends
one out.

- Import: paste a Teamcraft import link, an Artisan list (Artisan's **Export
  List**) or plain `3x Item Name` lines, such as Teamcraft's **Copy as Text**,
  and press Parse. It tells you which one it found, and lists any lines it
  couldn't match. **Paste from clipboard** fills the box with whatever you
  copied.
- Then **Save as shopping list** (give it a name, and tick **Break down into raw
  materials** if you want the crafts worked out), or **Open without saving**.
- Export: pick one of your lists. **Open in Teamcraft** opens Teamcraft's
  import page in your browser. **Copy Artisan list** (My shopping lists only)
  copies every sub-craft, ready for Artisan's **Import List From Clipboard
  (Artisan Export)**. **Share Teamcraft list** copies a link to send to someone
  else. They open it in a browser, not in Teamcraft's import dialog.

**A list window.** This is where you decide what to price.

- **Where to shop** sets the worlds you're willing to buy from. It's one choice
  shared by every list window. **Cross-world** covers the worlds you can reach
  from a main-city aetheryte, which is your region. **Cross data center** covers
  everywhere, and other regions need Data Center Travel.
- Untick **Buy** on anything you don't need. **Check/uncheck all** ticks or
  unticks every row at once. Your choices are remembered for Artisan and My shopping lists.
- **Exclude items I have enough of** unticks every row where what you hold
  already covers the quantity. A row whose count isn't known yet stays ticked.
  It doesn't count your houses.
- **Exclude items in my house** unticks every row your house already covers,
  even one you ticked by hand. A row whose house count isn't known yet stays
  ticked.
- **Exclude crafted items** unticks every craftable ingredient, so only raw
  materials stay ticked. Crystals, shards and clusters are left alone, and it
  also unticks ones you ticked by hand. If recipe data is still loading, it
  tells you to press it again.
- Click a column header to sort by it, and click again to reverse it. Sorting
  isn't saved, so closing the window puts the rows back in the list's own order.
- **On you** and **Retainers** show how many you already hold. Hover Retainers
  to see which retainer has it. **House** counts furniture placed in your house
  and its storeroom, and hovering it lists each house. The **Vendor** column shows when an NPC also
  sells the item. Hover it for the vendor and where they stand. An item nothing
  sells shows **Craftable** there, and one that has a vendor and can also be
  crafted adds (craftable) after the vendor text. If the item is also a monster
  drop it says so, such as **Craftable, monster drop**, and hovering it points
  you to the Monster Loot Hunter plugin to find which monsters. Columns start
  as wide as their longest text, and you can drag a border to change that.
- **Reload list** reads the list again. Use it if you changed the list somewhere
  else, or if the window says recipe data is still loading.
- Press **Build shopping list**. Prices come from Universalis and the results
  window opens.

**The results window.** This is where you pick the route.

- **Willing to pay extra** takes a percent over the cheapest total, or an
  amount of gil. It picks the plan that visits the fewest worlds within that
  budget. Click a different option in the list to use it, or slide to zero for
  the cheapest. **Reset to cheapest** does the same in one click.
- The item list on the left follows the route, first world first, and every
  item starts collapsed. Open one to see its worlds. **Show item list** hides
  the whole list if you'd rather give the route the room.
- Each item lists the worlds selling it. Type a quantity next to a world to
  change what you buy there, and the totals follow. The header then says the
  plan was edited by hand.
- The route lists each world with what to buy there. Click an item's name and
  it's copied, so you can paste it into the market board search.
- Click a world name, in the route or in an expanded item, to ask Lifestream to
  take you there. It only asks when you click. The note under the Route title
  reads "Requesting travel to the world via Lifestream", and Lifestream shows
  its own message if it can't go.
- Press Build shopping list again in the list window and the prices refresh. Your quantity
  changes are reset, but the budget stays.

## What it won't do

It won't travel on its own or buy for you. It plans, and you do the shopping.
A click on a world name only asks Lifestream to go.

It can only read your house while the Furnishings window is open and you are
inside it, so it remembers each house as of your last visit and you open
Furnishings once in each. The yard and the outdoor storeroom aren't counted.

It doesn't knock what you already own off the bill by itself. Building still prices
the full quantity, whatever **On you** and **Retainers** say. Those columns are
there for you to read. Press **Exclude items I have enough of**, or untick
anything you don't need to buy.

It only knows about a small list of monster drops, about 237 items, so many
real drops show nothing in the Vendor column. A blank there doesn't mean the
item isn't a drop. The game's own data has no drop information, which is why
the list comes from Teamcraft.

It can only see a retainer or the saddlebag while it's open. So it remembers
what each held the last time you opened it, per character, and shows a dim
figure and a note asking you to open each retainer once. A `-` means it doesn't
know yet. Equipped and armoury items aren't counted.

It doesn't know whether you can actually buy from a vendor in the Vendor
column. Some need a quest, a rank or an unlock first, and it doesn't check any
of that. A few vendors can't be matched to an NPC, so those don't show up at
all. Building still prices everything from the market board either way.

It can't promise the cheapest plan. Its plans come from a fast search, and
they're good, but nobody has proven them optimal.

It can't promise you HQ either. It prices at the cheapest listing of either
quality, normal or high, and doesn't show which one a listing is.

It only supports the global client. The China and Korea clients use different
data, and it hasn't been built or tried for them.

## A few corners I haven't checked closely

I haven't compared the worlds the cross data center choice lists with what the
game actually lets you visit. Getting to another region needs the
game's own Data Center Travel, which the plugin doesn't do for you.

I haven't tried importing a Teamcraft link or an Artisan export back into the
Import tab, so going the other way (Open in Teamcraft, Copy Artisan list) is
the better-trodden direction. The same goes for the any-order search, the market
board note, the sortable column headers, the Exclude items I have enough of button,
the collapsed item list, the Exclude crafted items checkbox, Check/uncheck all, removing goals, old lists still loading after an
update, the stale-listing marker, the short-by and over-by notes, Reload list,
closing windows with Esc or a gamepad, and updating the plugin. The newest
ones are the monster drop label and the download behind it, the House column, Exclude items in my house, clicking a world name to
ask Lifestream (including whether it collapses an open item), and the buttons
wrapping in a narrow window. If any of those
misbehaves, I'd like to hear about it.

## Why the numbers can be a little off

Universalis is built from what other players upload, so a listing can be gone
by the time you get there.

Prices leave out market tax, so what you actually pay can differ slightly.

It also assumes you can buy any number of units from a listing. Where the game
makes you take the whole stack, a stack you only needed part of costs more than
shown.

## What you need

- Final Fantasy XIV on the global client, with Dalamud. It's built for Dalamud
  API level 15.
- The Artisan plugin loaded, to pick a crafting list.
- Lifestream, only if you want world names to be clickable. Everything else
  works without it.
- An internet connection. Prices come from Universalis. When the plugin starts
  it also downloads a list of monster drops from Teamcraft's public repository
  on GitHub. The drop list request sends nothing about you. If it
  can't be fetched after 3 tries, you just get no drop labels that
  session.

## Licence and credits

Luna Shopping List is free software under the GNU Affero General Public License
version 3, which you'll find in the `LICENSE` file. Copyright 2026 LunaBelleElite.

Prices come from [Universalis](https://universalis.app), a community-run market
board service. Thanks to the people who keep it running.

The monster drop list comes from Teamcraft's open
repository. Thanks to its authors for sharing it.

Teamcraft and Artisan are separate projects, and this isn't affiliated with either.
It isn't affiliated with or endorsed by Square Enix. Final Fantasy is a
trademark of Square Enix.

It started from the SamplePlugin template the Dalamud authors publish for the
Dalamud plugin framework.
