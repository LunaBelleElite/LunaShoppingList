# Changelog

## 0.10.0.1 - 2026-11-22

This covers everything since 0.8.0.0.

- The Paste tab is now Import / Export. Import takes a Teamcraft import link, an Artisan list (Artisan's Export List) or plain `Nx Item Name` lines, such as Teamcraft's Copy as Text. It says which one it found, skips section headers like "Crystals :", and lists lines it couldn't match. Then Save as shopping list (named, with an optional Break down into raw materials) or Open without saving.
- Export: pick any list. Open in Teamcraft opens Teamcraft's import page in your browser. Copy Artisan list (My shopping lists only) copies a list that Artisan's Import List From Clipboard (Artisan Export) accepts, with every sub-craft. Share Teamcraft list copies a link to send to someone else; they open it as a website, because Teamcraft's own import dialog rejects it.
- My shopping lists: Add with subcrafts now keeps the finished item as a goal. The shopping rows are your plain items plus the raw materials worked out from all goals together, so a part that two goals share is crafted once. Change a goal's quantity and the materials follow. Lists saved before this still work.
- New Exclude crafted items checkbox next to Refresh on the Artisan crafting lists tab. Lists then open with crafted ingredients unticked, so you only buy raw materials.
- New Check/uncheck all button in list windows, beside the crystals toggle.
- Fixed: an item that can't be sold on the market board no longer breaks a price lookup. A pasted block of text could freeze the game; it doesn't any more. Item names ending in a space now match. Messages wrap in narrow windows instead of being cut off. Unloading the plugin is safer.
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
- Type a quantity yourself and it overrides the plan; the header says it was edited by hand. Build keeps your budget.
- You can resize the ingredient table's columns now, and the Quantity column doesn't clip any more.
- New "On you" and "Retainers" columns show what you already have: bags, crystals and saddlebag, plus retainer contents, which the plugin remembers per character once you've opened each retainer. With AllaganTools installed, its retainer counts are compared with the plugin's and the larger figure is shown. The columns are there to read; Build still prices the full quantity.

## 0.3.0.0 - 2026-11-21

First public release, and an early preview: tested so far on the global client with Artisan, on one machine.

- `/lsl` opens a list browser with My Lists and Paste tabs and a filter box. Each list gets its own window with its ingredients, so you can have several going at once.
- Build fetches prices from Universalis and opens a separate results window for each list, showing each item's total and a choice of worlds to buy it on.
- Two suggested plans to start from: the cheapest, and one that visits fewer worlds. Totals update as you change quantities.
- You can shop across the worlds of your region. A wider choice across every data center is in there too, but it's untested.
- Install it by adding this repository's custom repository address in Dalamud; the README has the steps. That install route is new in this release.
