# The Holiest Fluffiness
A [Dalamud](https://github.com/goatcorp/Dalamud) tweak plugin born for the FFXIV Free Company "The Holiest Fluffiness", now open for all to enjoy.

Yes, this project uses AI heavily, mostly to get things done quickly since I barely know C#. It works, and that's what matters.

## Installation
Add the repo URL to your Dalamud custom plugin repositories:

```
https://raw.githubusercontent.com/AlexFlipnote/XIV_HolyPlugin/release/repo.json
```

Then find "The Holiest Fluffiness" in the plugin installer.

## What it does
Way too many things, including:

- **Login info** - shows your character name, world, data center, FC tag, search info (adventure plate), private house location, and FC house location on login. Displayed as a chat message, toast notification, or popup window,  your choice.
- **Accessory auto-equip** - automatically equips a fashion accessory (glamour item) on login via `/fashion`. Supports a configurable delay, skips if already equipped, and can skip based on inventory slot thresholds.
- **Anti-AFK** - sends a silent keypress to the game window when the AFK timer gets too high, so you don't get kicked while you're just vibing.
- **No-kill / auto-reconnect** - intercepts lobby disconnects and automatically reconnects you instead of booting you to the title screen. Works with [Lifestream](https://github.com/NightmareXIV/Lifestream) to log back into the right character.
- **Gear repair indicator** - adds an icon to your server info bar when your gear durability drops below a configurable threshold, with separate warning and critical levels.
- **Doorbell** - notifies you when players enter or leave the house you're in. Useful when you're AFK inside your FC house and want to know who stopped by.
- **Ready check overlay** - draws ready/not-ready indicators on party frames during a ready check, with an optional chat message listing who didn't ready up.
- **Commendation tracker** - celebrates when you receive commendations after a duty, because you deserve it.
- **Nearby player list** - shows a live list of players near you, with filtering for AFK players and low-level characters, sorted by party > friends > same FC > everyone else. Also shows who is currently targeting you.
- **Server info bar** - displays your ping (live or 20-ping rolling average) and FPS in the server info bar.
- **Housing lottery tracker** - automatically records your active housing lottery bids in a local database and removes them when they conclude or you get the result dialog.
- **Character database** - keeps a local record of all your characters across logins, storing their FC, search info, house locations, gil, and select inventory items. Updated periodically while logged in.
- **Physics cap** - lets you throttle the game's physics simulation to a configurable FPS, useful if your GPU is cooking itself on physics while you're AFK.

All features are optional and can be toggled in the settings window (`/hf`).
