# The Holiest Fluffiness
A tweak plugin born for the free company 'The Holiest Fluffiness', now open for all to enjoy.

Originally created as a custom tweak plugin for the free company 'The Holiest Fluffiness', this project is now open-source and available for everyone to enjoy.

>[!NOTE]
> This project leans on AI to speed up development while I learn C#. It provided a solid foundation to get things running quickly, and I now manually maintain and expand upon that base layer using my background in programming. It works smoothly, and it's been a great way to learn in the process.

## Requirements
- [Dalamud](https://github.com/goatcorp/Dalamud)

## Installation
Add the repo URL to your Dalamud custom plugin repositories:

```
https://raw.githubusercontent.com/AlexFlipnote/XIV_HolyPlugin/release/repo.json
```

Once added, simply search for **The Holiest Fluffiness** in the plugin installer.

## Features
## Features
A comprehensive suite of quality-of-life tools, including:

- **Login Info:** Displays your character name, world, data center, FC tag, adventure plate, and housing locations upon login. Output is highly customizable: choose between a chat message, toast notification, or a dedicated popup window.
- **Accessory Auto-Equip:** Automatically equips a fashion accessory (via `/fashion`) on login. Includes customizable delays, skips if an accessory is already equipped, and features inventory slot threshold checks.
- **Anti-AFK:** Prevents idle kicks by sending a silent keypress to the game window when your AFK timer runs high, perfect for when you're just vibing.
- **Auto-Reconnect (No-Kill):** Intercepts lobby disconnects and automatically reconnects you instead of booting you back to the title screen. Integrates seamlessly with [Lifestream](https://github.com/NightmareXIV/Lifestream) to ensure you log back into the correct character.
- **Gear Repair Indicator:** Adds a handy durability icon to your server info bar. Features configurable warning and critical thresholds so you never get caught with broken gear mid-duty.
- **Doorbell:** Alerts you when players enter or leave your current house. Great for tracking visitors while AFK inside your FC estate.
- **Ready Check Overlay:** Highlights party frames with ready/not-ready indicators during a check. Optionally sends a chat message calling out who hasn't readied up yet.
- **Commendation Tracker:** Celebrates every commendation you receive after a duty, because you earned it.
- **Nearby Player List:** Displays a live radar of nearby players, sorted by Party > Friends > FC > Others. Includes filters for AFK or low-level characters, and shows exactly who is currently targeting you.
- **Server Info Bar:** Shows your live ping (or a 30-ping rolling average) and FPS directly in the server info bar.
- **Housing Lottery Tracker:** Automatically logs your active housing bids in a local database, clearing them out once the lottery concludes or you interact with the result dialog.
- **Character Database:** Maintains a local, periodically updated record of all your characters across logins, storing their FC, search info, housing locations, gil, and specific inventory items.
- **Physics Cap:** Throttles the game's physics simulation to a set frame rate. Saves your GPU from overworking itself on physics rendering while you're AFK.

> All features are completely optional (opt-in by default) and can be toggled at any time via the settings menu (`/hf` command shortcut).
