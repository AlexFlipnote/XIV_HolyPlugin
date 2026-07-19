# The Holiest Fluffiness
A tweak plugin born for the free company 'The Holiest Fluffiness', now open for all to enjoy.

>[!NOTE]
> This project leans on AI to speed up development while I learn C#. It provided a solid foundation to get things running quickly, and I now manually maintain and expand upon that base layer using my background in programming. It works smoothly, and it's been a great way to learn in the process.

## Installation
With [Dalamud](https://github.com/goatcorp/Dalamud) installed to your FFXIV game, add the repo URL to your Dalamud custom plugin repositories:

```
https://raw.githubusercontent.com/AlexFlipnote/XIV_HolyPlugin/release/repo.json
```

Once added, simply search for **The Holiest Fluffiness** in the plugin installer.

## Features
There are too many now to list without this turning into a wall of text, so here are a few of the better ones: auto-reconnect that puts you back on the right character when the lobby drops you, a physics FPS cap so hair and cloth behave without cooking your GPU, gear durability warnings before you get caught mid-duty, a food check before the pull, and live FPS, nearby player count and ping in the server info bar.

The rest live in the same five groups you'll find in the settings window, Login, Client, Indicators, Social and Database. Install it and have a scroll.

## Building
Build with `make`. The default target is a lint-enforced build that fails on unused usings and style violations, so the standard stays green without anyone having to remember a separate step.

| Target | What it does |
| --- | --- |
| `make` / `make lint` | Lint-enforced debug build, the one to use by default |
| `make build` | Plain debug build with no lint gate, for tight edit loops |
| `make release` | Release build |
| `make pack` | Release build copied into `dist/` with the manifest and icon |
| `make scan` | Re-scan the game executable for every tracked signature |
| `make check` | Fail if a signature has drifted out of `Sigs.cs` |
| `make clean` | Drop build output and `dist/` |

### Signatures
Any game function the plugin hooks that ClientStructs does not already resolve needs a byte signature, and those break whenever the client is patched. They all live in exactly one file, `HoliestFluffiness/Utils/Sigs.cs`, and `make check` fails the build if one ever appears anywhere else.

`SigTracker/` keeps a per-patch history of where each signature resolved to, along with the bytes that were there. After a patch, `make scan` reports which ones broke and uses those saved bytes to work out whether the function simply moved or was rewritten. See [SigTracker/README.md](SigTracker/README.md) for the full workflow, including how to point it at a non-default game install.

## Why this exists
It began with one very specific problem. The FC needed a tool for something niche enough that no existing plugin really covered it, and the closest options either solved half the problem or came with a pile of settings we would never touch. So it got written.

Once that first thing worked, the list kept growing. Something else turned out to be broken, or a feature already existed elsewhere but was heavier or clunkier than it needed to be, or an annoyance had just been quietly accepted for years because nobody had gotten around to it. Each of those became another toggle here, and what started as a plugin for one free company ended up being a general set of fixes worth sharing.

There is a plainer argument for working this way too. The ecosystem is full of tiny plugins that each do one simple thing, and every one of them carries its own weight: its own hooks into the game, its own settings window, its own update to chase, its own author to hope is still around after the next patch. Folding that work into a single plugin cuts all of it down to one of each. The point was never just to bundle things though, anything that lands here gets rebuilt to the same standard rather than copied in, so it reads, behaves, and performs like one plugin instead of a dozen strangers sharing a process.

Quality of life shouldn't cost you frames, every feature is opt-in and stays off until you want it, and where the game or [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) already exposes what's needed, the plugin uses that instead of reaching into memory by hand. Less to break when the client is patched, and less running that you never asked for.

It should just work, sensible defaults, plain wording, and one settings window grouped the same way as the features it controls. Switching something on should be enough to get the useful behaviour out of it, with the knobs sitting there for when you actually want them rather than as homework before you can start.
