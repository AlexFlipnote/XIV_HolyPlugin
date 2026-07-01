# The Holiest Fluffiness
A tweak plugin born for the free company 'The Holiest Fluffiness', now open for all to enjoy.

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
A comprehensive suite of quality-of-life tools, grouped the same way as the in-plugin settings:

### Login
- **Character Picker:** Optional popup on the main menu for quickly picking which character to log into.
- **Login Info:** Shows your name, world, data center, FC tag, adventure plate, and housing locations as a chat message, popup, or toast on login.
- **Skip Intro Logo:** Jumps straight to the title screen instead of playing the intro movie on launch.
- **Preload Territory:** Starts loading your destination zone in the background while you're still in the login queue.
- **Accessory Auto-Equip:** Automatically equips a fashion accessory via `/fashion` on login, with configurable delays and inventory slot thresholds.

### Client
- **Disable Idle Movie:** Skips the looping intro video on the title screen.
- **Fast Mouse Click Fix:** Removes an artificial delay the client imposes between mouse clicks.
- **Window Title:** Customizes the game window's title, optionally appending your logged-in character's name.
- **Taskbar Flash:** Flashes the FFXIV taskbar icon on tells, ready checks, alarms, combat, or synthesis completion.
- **Auto-Reconnect (No-Kill):** Intercepts lobby disconnects and reconnects you automatically instead of booting you to the title screen, integrating with [Lifestream](https://github.com/NightmareXIV/Lifestream) to log back into the correct character.
- **Physics Cap:** Throttles the game's physics simulation to a target FPS so hair/cloth physics behave correctly and your GPU isn't overworked while AFK.
- **Anti-AFK:** Sends a silent keypress when your AFK timer runs high so you never get idle-kicked.

### Indicators
- **Cast Bar Aetheryte Names:** Shows the actual aetheryte name instead of generic text when teleporting.
- **Duty Queue Timer:** Displays the estimated remaining queue time in the duty ready check dialog.
- **Hide Hotbar Lock:** Removes the padlock icon from the action bar.
- **Server Info Bar:** Adds live FPS, nearby player count, and ping (with a rolling average) to the server info bar.
- **Gear Repair Indicator:** Adds a durability warning icon with configurable low and critical thresholds so you never get caught with broken gear mid-duty.
- **Food Check Helper:** Warns when party members are missing or low on food during ready checks and countdowns.
- **Ready Check Overlay:** Highlights party frames with ready/not-ready icons and can call out stragglers in chat.
- **Combat Hits:** Adds customizable sounds and text for critical hits, direct hits, and heals.

### Social
- **Nameplate Tweaks:** Replaces cross-world "Wanderer/Traveller" FC tags with the player's actual home world.
- **Nearby Player List:** A live radar of nearby players sorted by Party > Friends > FC > Others, with AFK/low-level filters and alerts for who's targeting you.
- **Doorbell:** Alerts you when players enter, leave, or are already inside your current house.
- **Commendation Tracker:** Plays a sound based on how many commendations you received after a duty.

### Database
- **Character Database:** Keeps a local, periodically updated record of every character you've logged into, including FC, gil, MGP, housing, and tracked inventory items.
- **Housing Lottery Tracker:** Automatically logs your active housing bids and clears them once the lottery concludes.

> All features are completely optional (opt-in by default) and can be toggled at any time via the settings menu (`/hf` command shortcut).
