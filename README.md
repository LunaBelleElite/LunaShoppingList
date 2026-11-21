# Luna Shopping List

**Turn an Artisan crafting list into a shopping list that tells you which world to buy each thing on.**

Luna Shopping List is a Dalamud plugin for Final Fantasy XIV. You open one of
your Artisan crafting lists, press Build, and it asks Universalis for current
prices. A results window then shows each item once, with the worlds that sell it,
so you decide where to buy what. It can also suggest a plan for you, and your
route and totals update as you change quantities.

It never travels for you and never buys anything. It only tells you where to go.

## This is an early preview

It has been tested so far on the global client, with Artisan, on one machine.
Expect rough edges. Several things below are marked untested; if you hit a
problem, open an issue on this repository and say what you did.

## Add the repository in the game

Dalamud installs plugins from a list of repositories, and this plugin lives in
its own, so you add it once. This install route is new in this release, so it
may not go smoothly for everyone yet.

1. Type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this address:
   `https://raw.githubusercontent.com/LunaBelleElite/LunaShoppingList/main/repo.json`
3. Click the plus button, then **Save and Close**.
4. Open `/xlplugins`, search for **Luna Shopping List** and install it.

Dalamud shows a warning about third-party repositories when you add one. That's
Dalamud being careful: anyone can publish a plugin this way, so only add
repositories from people you trust.

## What you'll see

1. Type `/lsl`. A list browser opens with a **My Lists** tab (your Artisan lists,
   with a filter box) and a **Paste** tab.
2. Click a list. It opens in its own window with its ingredients, and you can
   have several lists open at once.
3. Choose where you're willing to shop. The cross-world choice has been tried from
   a North American world, and worlds from several North American data centers
   showed up.
4. Press Build. Prices come from Universalis, and a separate results window opens
   for that list. Each item shows the total you need and a choice of worlds to
   buy it on.
5. Two suggested plans are offered: **cheapest**, and **fewest worlds**. Change
   any quantity yourself and the totals follow.

## What it won't do

- **It doesn't travel or buy for you.** It plans; you do the shopping.
- **It doesn't know what's in your inventory or retainers**, because it can't see
  them. Untick anything you already own.
- **It prices at the cheapest listing of either quality**, normal or high, and
  doesn't show which one a listing is, so it can't promise you HQ.
- **It supports the global client only.** The China and Korea clients use
  different data and it hasn't been built or tried for them.

## Not tested yet

These are in the plugin but haven't been seen working in the game, so treat them
as uncertain:

- Pasting Artisan's plain-text export instead of picking a list.
- The cross data center choice, and whether the worlds listed match what the game
  lets you visit. Getting to another region needs the game's own Data Center
  Travel; the plugin doesn't do that for you.
- Skipping items, the stale-listing marker, the short-by and over-by indications,
  and Reload list.
- Esc and gamepad behaviour when closing windows, and updating the plugin.

## Why the numbers can be a little off

- **Prices come from Universalis**, which is built from what other players
  upload, so a listing can already be sold by the time you arrive.
- **Prices exclude market tax**, so what you actually pay can differ slightly.
- **Prices assume you can buy any number of units from a listing.** If the game
  makes you take a whole stack, a partly bought stack costs more than shown.

## What you need

- Final Fantasy XIV (global client) with Dalamud, built for Dalamud API level 15.
- The Artisan plugin loaded, to pick a crafting list.
- An internet connection, because prices come from Universalis.

## Licence, credits and what this is

Luna Shopping List is free software under the GNU Affero General Public License
version 3, which you'll find in the `LICENSE` file. Copyright 2026 LunaBelleElite.

Prices are provided by [Universalis](https://universalis.app), a community-run
market board service. Thanks to the people who keep it running.

It isn't affiliated with or endorsed by Square Enix. Final Fantasy is a
trademark of Square Enix.

It started from the SamplePlugin template published by the Dalamud authors, for
the Dalamud plugin framework.
