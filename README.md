# Luna Shopping List

**Turn an Artisan crafting list into a shopping list that tells you which world to buy each thing on.**

Luna Shopping List is a Dalamud plugin for Final Fantasy XIV. Open one of your
Artisan crafting lists, press Build, and it asks Universalis what everything
costs right now. You get each item once, with the worlds selling it, and you
decide where to buy what. If you'd rather not decide, it will suggest a plan,
and the route and totals keep up as you change quantities. The ingredient list
also shows how many of each item you already hold.

It doesn't travel for you and it doesn't buy anything. It tells you where to
go. The walking is yours.

## It's an early preview

So far it has been tested on the global client, with Artisan, on one machine.
Some things further down are marked untested. The two newest pieces, the Plan
options panel and the owned-item counts, are built and unit-tested, but I
haven't tried them in the game yet, so they're the likeliest to trip you up. If
anything does, open an issue on this repository and say what you did.

## Add the repository in the game

Dalamud installs plugins from repositories, and this one lives in its own, so
you add it once. The install route is new, so it may not go smoothly for
everyone yet.

1. Type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this address:
   `https://raw.githubusercontent.com/LunaBelleElite/LunaShoppingList/main/repo.json`
3. Click the plus button, then **Save and Close**.
4. Open `/xlplugins`, search for **Luna Shopping List** and install it.

Dalamud warns you about third-party repositories when you add one, and it's
right to. Anyone can publish a plugin this way, so only add repositories from
people you trust.

## From a list to a route

1. Type `/lsl`. A list browser opens with a **My Lists** tab, which is your
   Artisan lists plus a filter box, and a **Paste** tab.
2. Click a list and it gets its own window with its ingredients. Open as many
   as you like at once. If a column is too narrow, drag its border. The
   **On you** and **Retainers** columns show how many of each item you already
   hold.
3. Choose where you're willing to shop. The cross-world choice has been tried
   from a North American world, and worlds from several North American data
   centers showed up.
4. Press Build. Prices come from Universalis, and the list gets a separate
   results window: each item with the total you need and the worlds you could
   buy it on.
5. In the **Plan options** panel, the **Willing to pay extra** slider takes a
   percent over the cheapest total, or an amount of gil. It picks the plan that
   visits the fewest worlds within that budget and lists every option with its
   extra cost, so you can click a different one. Slide it to zero for the
   cheapest plan. Type a quantity in yourself and the totals follow, and the
   header then says the plan was edited by hand.

## What it won't do

It won't travel or buy for you. It plans, and you do the shopping.

It doesn't knock what you already own off the bill. Build still prices the full
quantity, whatever **On you** and **Retainers** say; those columns are there for
you to read. Untick anything you don't need to buy.

It can only see a retainer or the saddlebag while it's open. So it remembers
what each held the last time you opened it, per character, and shows a dim
figure and a note asking you to open each retainer once. A `-` means it doesn't
know yet. Equipped and armoury items aren't counted.

It can't promise the cheapest plan. Its plans come from a fast search, and
they're good, but nobody has proven them optimal.

It can't promise you HQ either. It prices at the cheapest listing of either
quality, normal or high, and doesn't show which one a listing is.

It only supports the global client. The China and Korea clients use different
data, and it hasn't been built or tried for them.

## Not seen working in the game yet

These are in the plugin, but nobody has watched them work in the game, so treat
them as uncertain:

- The Plan options panel and its slider, including how it looks and reads.
- The **On you** and **Retainers** counts: bags, crystals, saddlebag, what it
  remembers from retainers, and the optional AllaganTools counts. If you have
  AllaganTools, it compares that plugin's retainer figure with its own record
  and shows the larger one. It never adds the two together.
- Pasting Artisan's plain-text export instead of picking a list.
- The cross data center choice, and whether the worlds it lists match what the
  game actually lets you visit. Getting to another region needs the game's own
  Data Center Travel, which the plugin doesn't do for you.
- Skipping items, the stale-listing marker, the short-by and over-by
  indications, and Reload list.
- Esc and gamepad behaviour when closing windows, and updating the plugin.

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
- An internet connection, because the prices come from Universalis.

## Licence and credits

Luna Shopping List is free software under the GNU Affero General Public License
version 3, which you'll find in the `LICENSE` file. Copyright 2026 LunaBelleElite.

Prices come from [Universalis](https://universalis.app), a community-run market
board service. Thanks to the people who keep it running.

It isn't affiliated with or endorsed by Square Enix. Final Fantasy is a
trademark of Square Enix.

It started from the SamplePlugin template the Dalamud authors publish for the
Dalamud plugin framework.
