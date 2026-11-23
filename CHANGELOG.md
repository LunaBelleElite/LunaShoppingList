# Changelog

## 0.14.0.0 - 2026-11-23

This covers everything since 0.13.0.0.

- The Vendor column now says when an ingredient is a monster drop. An item nothing sells reads Monster drop, and one with a vendor or a craft gets it added, such as Craftable, monster drop or 1,200 gil, monster drop. Hover the cell and it explains that the plugin doesn't know which monsters, and that the Monster Loot Hunter plugin can look that up. Sorting by Vendor ignores drops.
- The drop list is downloaded once when the plugin starts, from Teamcraft's public repository on GitHub. It's a plain request that sends nothing about you, and it tries up to 3 times. If it never loads, you get no drop labels that session and nothing on screen. The list is small, about 237 items, so plenty of real drops show nothing. Drop data credit goes to Teamcraft.
- After you click a world name, the note under the Route title now reads Requesting travel to the world via Lifestream.
- Not yet checked in the game: any of the above. The riskiest parts are the download itself, and how the label and the wrapped hover note draw.

## 0.13.0.0 - 2026-11-23

This covers everything since 0.12.1.0.

- New House column in the ingredients window, after Retainers. It counts furniture placed inside your house plus what is in the storeroom. It turns green when your house alone covers the quantity, and you can sort by it. Hover a number to see each house, what is placed and what is in the storeroom, and when it was last seen.
- The House column is only filled while the Furnishings window is open and you are standing inside the house. It remembers each house as of your last visit, so open Furnishings once in each house. Until you do, the figure is dim. The yard and the outdoor storeroom are not counted.
- New Exclude items in my house button. It unticks every ingredient your house already covers, including ones you ticked by hand. An item whose house count isn't known yet stays ticked. Exclude items I have enough of doesn't count houses.
- World names in the Route panel and in each expanded item on the left are now clickable. A click asks Lifestream, if you have it installed, to take you there. It never travels on its own. The note under the Route title only says Lifestream was asked, and if Lifestream can't go, it shows its own message in the game. If it isn't installed, the note says so.
- The Toggle all crystals/shards/clusters button is gone. Exclude items I have enough of and Exclude crafted items cover that.
- The ingredient buttons wrap onto a new line when the window is narrow.
- Not yet checked in the game: any of the above. The riskiest parts are reading the house contents from the Furnishings window, and whether clicking a world name in an expanded item leaves that section open.

## 0.12.1.0 - 2026-11-23

This covers everything since 0.12.0.0.

- New Exclude crafted items button in the ingredients window, after Exclude items I have enough of. It unticks every craftable ingredient so only raw materials remain. Crystals, shards and clusters are never touched. It also unticks ones you ticked by hand. If the recipe data is still loading, it says to press again.
- The Vendor column now shows Craftable for items that nothing sells. Where a vendor does sell the item and it can also be crafted, it shows the vendor text followed by (craftable). Hover a craftable-only cell and it says the item can be crafted.
- Not yet checked in the game: any of the above.

## 0.12.0.0 - 2026-11-22

This covers everything since 0.10.0.5.

- Every table header is now clickable. One click sorts ascending and a second sorts descending, and a third clears the sort. It works on the ingredients table, both tables in a shopping list, the offers table in the results window and the route tables. The sort isn't saved, so closing the window puts the rows back in the list's own order.
- Sorting puts unknown counts lowest in On you and Retainers, and ticked rows first in Buy. In the Vendor column, rows with a vendor come first, gil vendors before currency vendors. If you sort a route table, the item list on the left follows it.
- The Vendor column is now as wide as its longest text, so entries such as "150 Purple Gatherers' Scrip" are no longer cut off. In the results window the For sale, Unit price, Cost, Qty and Price columns are fitted the same way, so a range such as "15,150-17,000" no longer runs into the next column.
- The route tables in the results window have columns you can drag wider or narrower.
- New Show item list checkbox in the results window. Untick it to hide the per-item prices and give the route the whole window. It isn't saved.
- The item list on the left of the results window now follows the route: the first world's items first, then down each world, with items the plan buys nowhere last. Every item starts collapsed, so open one to see the other worlds that sell it. The header still says if the plan is short.
- Item headers now read "have 0 of 1 - plan buys 1" instead of "bought 1 of 1". Have is what is on you right now and goes up as you buy. Plan buys is what the current plan would purchase.
- New Exclude items I have enough of button in the ingredients window, next to the crystals toggle. It unticks every ingredient where what you hold already covers the quantity, counting bags, crystals, saddlebag and retainers as of your last visit. An item whose count isn't known yet stays ticked. It's a one-off press, and the result is saved with the list's exclusions.
- Fixed: tables were already sorted by their first column before you clicked anything, so the route didn't match the item list on the left. They now start in their natural order.
- Not yet checked in the game: any of the above.

## 0.10.0.5 - 2026-11-22

This covers everything since 0.10.0.1.

- Open in Teamcraft and Share Teamcraft list now send only items that can be crafted, the same rule Copy Artisan list uses. Anything left out is named in a note. If nothing on the list can be crafted, it says so and sends nothing.
- The item search now matches every word you type, in any order. "xii materia" and "savage xii" both find Savage Aim Materia XII. It still only offers items that sell on the market board.
- Items that can't be sold on the market board are no longer looked up on Universalis. They show a dim "(not sold on the market board)" after the name, and the results window lists them in one note. They no longer look like a failed lookup or count as "not fully covered".
- Exclude crafted items now remembers your own ticks. If you tick a crafted ingredient back on, Reload list, reopening the list and Check all leave it ticked.
- Break down into raw materials on the Import tab now works out shared parts once, the same way goals do, so it no longer asks for more than you need.
- Add with subcrafts refuses a goal that would make the list too large to work out, and tells you why. Your unticked choices stay put.
- On a list with goals, Build shopping list is greyed until the recipe data has loaded, and the materials then appear by themselves. If the recipe data can't load, the window says so and offers Try again.
- Fixed: a quantity box could save to the wrong list when you switched lists while typing. An Artisan list with a broken character in its name was rejected whole. An Artisan list with every entry set to Skip said it was empty.
- Not yet checked in the game: any of the above. The exports and the goals totals were checked before these changes, so I'd like to hear if any of them behave differently now.

## 0.10.0.1 - 2026-11-22

This covers everything since 0.8.0.0.

- The Paste tab is now Import / Export. Import takes a Teamcraft import link, an Artisan list (Artisan's Export List) or plain `Nx Item Name` lines, such as Teamcraft's Copy as Text. It says which one it found, skips section headers like "Crystals :", and lists lines it couldn't match. Then Save as shopping list (named, with an optional Break down into raw materials) or Open without saving.
- Export: pick any list. Open in Teamcraft opens Teamcraft's import page in your browser. Copy Artisan list (My shopping lists only) copies a list that Artisan's Import List From Clipboard (Artisan Export) accepts, with every sub-craft. Share Teamcraft list copies a link to send to someone else. They open it as a website, because Teamcraft's own import dialog rejects it.
- My shopping lists: Add with subcrafts now keeps the finished item as a goal. The shopping rows are your plain items plus the raw materials worked out from all goals together, so a part that two goals share is crafted once. Change a goal's quantity and the materials follow. Lists saved before this still work.
- New Exclude crafted items checkbox next to Refresh on the Artisan crafting lists tab. Lists then open with crafted ingredients unticked, so you only buy raw materials.
- New Check/uncheck all button in list windows, beside the crystals toggle.
- Fixed: an item that can't be sold on the market board no longer breaks a price lookup. A pasted block of text could freeze the game, and it doesn't any more. Item names ending in a space now match. Messages wrap in narrow windows instead of being cut off. Unloading the plugin is safer.
- Not yet checked in the game: importing a Teamcraft link or an Artisan export back into the Import tab, Exclude crafted items, Check/uncheck all, removing goals, and old lists loading after the update.

## 0.8.0.0 - 2026-11-22

- My shopping lists has a new Add with subcrafts button, beside Quantity and Add. It takes the item you picked, works out every crafting level down to the raw materials (crystals included), adds up the amounts and puts only those materials on the list. The item itself and the intermediate crafts aren't added. Where several recipes make an item, it uses the first one it finds. The button is greyed out for items that can't be crafted.
- Plain Add still puts the item itself on the list, to buy as it is.
- The per-row Add ingredients button is gone. It swapped a craftable row for one level of ingredients, and Add with subcrafts does that job properly. Each row now has just Remove.
- Items that vendors also sell are still broken down all the way.

## 0.7.0.0 - 2026-11-22

This covers everything since 0.5.0.0.

- New My shopping lists tab, for lists you put together yourself. Name the list first, then type an item, pick it from the suggestions, set how many and press Add. Only items that sell on the market board are offered. Lists are saved, and you can rename, edit or delete them. Open one and it prices like an Artisan list, and a craftable item on it can be swapped for its ingredients.
- The first tab is now called Artisan crafting lists. It used to be My Lists.
- A new Vendor column shows when an NPC also sells an ingredient, for gil or for currencies such as Bicolor Gemstones, tomestones, scrips or Grand Company seals. Hover it for the vendor and where they are. It's there to read: it doesn't check whether a vendor is unlocked or available to you, and a few vendors can't be matched to an NPC, so those aren't shown.
- Hover a Retainers number to see which retainer holds the item, and whether it's in their inventory, their crystals or their market listings.
- Click an item's name in the route list to copy it.

## 0.5.0.0 - 2026-11-22

Still an early preview. I haven't tried the Plan options panel or the owned-item counts in the game yet. They're built and unit-tested and nothing more, so they may well misbehave.

- The two plan buttons are gone. In their place is a Plan options panel with a "Willing to pay extra" slider, set as a percent over the cheapest total or in gil. It shows the plan that visits the fewest worlds within that budget, and a clickable list gives every option with its extra cost. Slide it to zero for the cheapest. Plans come from a fast search, so they're good but not proven the cheapest.
- Type a quantity yourself and it overrides the plan. The header then says it was edited by hand. Build keeps your budget.
- You can resize the ingredient table's columns now, and the Quantity column doesn't clip any more.
- New "On you" and "Retainers" columns show what you already have: bags, crystals and saddlebag, plus retainer contents, which the plugin remembers per character once you've opened each retainer. With AllaganTools installed, its retainer counts are compared with the plugin's and the larger figure is shown. The columns are there to read. Build still prices the full quantity.

## 0.3.0.0 - 2026-11-21

First public release, and an early preview: tested so far on the global client with Artisan, on one machine.

- `/lsl` opens a list browser with My Lists and Paste tabs and a filter box. Each list gets its own window with its ingredients, so you can have several going at once.
- Build fetches prices from Universalis and opens a separate results window for each list, showing each item's total and a choice of worlds to buy it on.
- Two suggested plans to start from: the cheapest, and one that visits fewer worlds. Totals update as you change quantities.
- You can shop across the worlds of your region. A wider choice across every data center is in there too, but it's untested.
- Install it by adding this repository's custom repository address in Dalamud. The README has the steps. That install route is new in this release.
