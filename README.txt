Pocket Rogues Trainer 1.2.0  (BepInEx 5 plugin)
================================================

Open/close the menu in game: F8 (Esc also closes it). Works in the fortress and in runs.
On a laptop, if F8 does nothing, try Fn+F8 (F-keys are often media keys), or pick another
key with ToggleMenu in the config file below.
While the menu is open, the game stops reading keyboard/mouse; during a run it is also paused.

Player tab   (needs an active dungeon run)
  - God mode: no damage taken, HP kept full (including poison/bleed ticks)
  - Infinite mana
  - One-hit kills / damage multiplier x1-x100 (enemies only; pets/summons unaffected)
  - Full heal + mana, +1 level

Items tab    (needs an active dungeon run)
  - Every saveable item in the game, searchable, filtered by category
  - Quality picker (Common..Legendary) for equipment that can roll quality, and crafting gems
  - Count 1-99. Items go to your inventory the same way pickups do; if the inventory is full
    they drop at your feet. Take them home through a portal/exit as usual to keep them.
  - "Effects..." (rings, weapons, helmets, armor, off-hands): pick the exact effects first.
      Rings: everything is editable - add/remove any effect (base effect included), set
        levels I-V (each effect only goes as high as the game defines), and choose the skill
        or skill slot for Ring Skill effects (< > buttons).
      Weapons/armor: add effects with levels; default effects stay (the game re-adds them on
        load at a level that follows quality).
      Pool: "This item's pool" = what this item can roll naturally; "All ... effects" = every
        effect any item of that kind can roll. No duplicate effect types; ring
        incompatibilities (e.g. Blind fury vs Blind defense) are respected.

    The game's limits (it re-applies these every time an item loads, equips or leaves a chest):
      Rings:        each effect costs level+1 points (I = 1 ... V = 5);
                    total allowed = Common 2, Uncommon 3, Rare 4, Epic 5, Legendary 6
      Weapons/armor: effects allowed = default effects + 0/1/2/3/4 (Common..Legendary)
      Fortress chest: keeps at most 5 effects (4 if the item has no base/default effect)
    By default the editor stays inside these limits, so the item stays exactly as built even
    if you remove the trainer. The points/effects counter shows what's left.

    "No limits" (saved setting): any number of effects at any level. Such items keep their
    extras only while No limits stays on with the trainer installed, offline. Turn it off,
    remove the trainer or play online, and the game trims them at their next load.

    Every custom item is run through the game's own save/load code before you get it; the
    message says whether every chosen effect survives.

Unlocks tab (works anywhere)
  - Shop blueprints: found / total per category (helmets, armor, weapons and off-hands per
    class, artifacts, consumables, passives). "Unlock" marks blueprints as found so the fortress
    shop sells them. Only "not found" becomes "found"; owned items, durability and stock are
    untouched, and quest/event items (like the Endless Crypt key) are never touched.
    The shop picks new blueprints up the next time the fortress loads (after a run, or restart).
  - Forge: learn every combination recipe, and reveal every effect in the forge's effect list.
  - Normal blueprint rules, for reference: an item drop becomes a blueprint ~8.5% of the time,
    at most once every ~6 minutes, only after 12+ completed raids and with the Blacksmith built
    (artifacts: Artifacts building level 3+; consumables: 8%, no raid requirement).

Currency tab (works anywhere, including the fortress)
  - Add/set gold and crystals (capped at 2,000,000,000 so the game can't overflow them to 0)

All toggles start OFF on every launch. Every cheat switches itself off during online
multiplayer sessions.

Settings: BepInEx\config\local.pocketrogues.trainer.cfg (created on first launch)
  ToggleMenu (e.g. F8, Insert, BackQuote), PauseWhileOpen, UiScale,
  Diagnostics (writes troubleshooting lines to the log)

Log: BepInEx\LogOutput.log  (look for "Pocket Rogues Trainer")

Uninstall
  Remove the plugin only:  delete BepInEx\plugins\PocketRoguesTrainer\
  Remove all modding:      delete winhttp.dll, doorstop_config.ini, .doorstop_version and the
                           BepInEx folder from the game folder.
  Neither touches your save.
