# Changelog

## 1.0.0.0 - 2026-11-27

- This is the first full release.
- Nothing changed in how the plugin works.

## 0.18.1.0 - 2026-11-27

- All data centers now means your own region plus Materia. It no longer offers worlds in other regions, which the game won't let you travel to.
- This applies on every region, so Europe and Japan get their own region plus Materia too.

## 0.18.0.1 - 2026-11-25

- The plugin description shown in the Dalamud plugin list now says what it does today, including Allagan Tools lists.

## 0.18.0.0 - 2026-11-24

- Plan options on big All data centers lists are now worked out exactly, so the extra gil for each world count is the cheapest it can prove.
- On a very large list that can take up to about 30 seconds. The build says "Working out the cheapest plans..." while it runs, and Cancel build now stops it.
- If the search runs out of time it shows the best plans it found, with the few-worlds end checked first.
- The line saying plans aren't proven to be the cheapest is gone.

## 0.17.0.2 - 2026-11-24

- Help text, tooltips and messages in the plugin are reworded to be shorter and clearer.
- Nothing else changed.

## 0.17.0.1 - 2026-11-24

- Big lists on busy items are solved exactly more often, so you see (estimate) less. Exact prices don't change.

## 0.17.0.0 - 2026-11-24

- New Allagan Tools lists tab. Open a craft list and it works out the materials, subcrafts included.
- New Send to Allagan Tools button makes a craft list from one of yours. Sending twice makes two, and high quality isn't kept.
- Untick crafted items when a list opens and Ignore listings older than (hours) now sit above the tabs.
- The old results window closes when Buy only what I lack leaves nothing to buy.

## 0.16.0.0 - 2026-11-24

- Plans and prices now assume whole stacks, because the market board won't sell part of one. Totals can run higher.
- Buy here has - and + buttons that step through amounts you can really buy. A typed number rounds up.
- Results say things like buy 2 stacks (20 + 99), 119 units, with surplus N and short by N where they apply.
- Willing to pay extra plans use whole stacks too. On very large lists the search stops early and shows the best plans so far.

## 0.15.0.0 - 2026-11-24

- New Buy only what I lack box next to Find prices. It prices only what your bags, saddlebag and retainers don't cover.
- Listings over 24 hours old are ignored. Set Ignore listings older than (hours) to 0 to turn that off.
- The Route is grouped by data center, with a gil subtotal for each.
- The monster drop list is kept on your computer and used if the download fails.

## 0.14.1.0 - 2026-11-23

- Buttons now say what they do. Build shopping list is Find prices, Exclude is Untick, and Check/uncheck all is Tick all.
- New Undo button takes back the last Tick or Untick button.
- Hover a greyed-out button to see why.
- Fixed: recipe data no longer retries forever after a failure.
- Fixed: changing ticks while prices load no longer shows old results.

## 0.14.0.0 - 2026-11-23

- The Vendor column marks known monster drops, with a pointer to the Monster Loot Hunter plugin.
- The drop list downloads from Teamcraft's public repository when the plugin starts.

## 0.13.0.0 - 2026-11-23

- New House column counts furniture in your house and storeroom once you open Furnishings there.
- New Exclude items in my house button.
- Click a world name to ask Lifestream to take you there.
- Removed the crystals toggle.

## 0.12.1.0 - 2026-11-23

- New Exclude crafted items button.
- The Vendor column shows Craftable for items you can craft.

## 0.12.0.0 - 2026-11-22

- Click any column header to sort.
- New Exclude items I have enough of button.
- The results item list follows the route and starts collapsed. Show item list hides it.
- Fixed: wide Vendor and price columns no longer get cut off.

## 0.10.0.5 - 2026-11-22

- Item search matches your words in any order.
- Exports only send craftable items and tell you what was left out.
- Items that can't be sold on the market board are marked, not shown as a failed lookup.
- Fixed: a quantity could save to the wrong list.
- Fixed: some Artisan lists failed to load.

## 0.10.0.1 - 2026-11-22

- The Paste tab is now Import / Export. It reads Teamcraft links, Artisan exports and plain item lines, and exports to both.
- Add with subcrafts keeps the finished item as a goal and works out materials for all goals together.
- New Exclude crafted items checkbox and a Check/uncheck all button.
- Fixed: pasting a big block of text could freeze the game.

## 0.8.0.0 - 2026-11-22

- New Add with subcrafts button breaks an item down to raw materials.
- Removed the per-row Add ingredients button.

## 0.7.0.0 - 2026-11-22

- New My shopping lists tab for lists you build yourself.
- New Vendor column shows when an NPC also sells an item.
- Hover a Retainers number to see which retainer has it.
- Click an item name in the route to copy it.

## 0.5.0.0 - 2026-11-22

- A Willing to pay extra slider replaces the two plan buttons.
- New On you and Retainers columns show what you already have.
- Quantities you type override the plan.

## 0.3.0.0 - 2026-11-21

- First public release.
- `/lsl` opens your Artisan lists, Build checks Universalis, and a results window shows which world to buy each item on.
- Add this repository in Dalamud to install it. The README has the steps.
