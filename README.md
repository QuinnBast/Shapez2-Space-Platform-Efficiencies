# Platform Efficiency Overlay

See what your factory is actually moving, and where it is stuck — no belt readers required.

Select a platform to get its total output on the game's own efficiency gauge:

![The platform side panel](screenshots/platform-panel-gauge.png)

Or switch on the overlay to colour every machine, belt, space belt and space pipe by how
hard it is working:

![The overlay in the world](screenshots/overlay-in-world.png)

## Reading it

**Colour** is how much of its capacity something is using — dark red is stopped, green is
keeping up.

**A green pip** means it is being fed. That is what tells you which way to walk:

| | |
|---|---|
| dark **with** a pip | fed and still not keeping up — the problem is here or downstream |
| dark, **no** pip | starved — the problem is upstream |

Zoom out and each platform gets a single colour and pip for the whole thing.

## Installing

Needs [Shapez Shifter](https://steamcommunity.com/sharedfiles/filedetails/?id=3542611357)
1.2 or newer. Put the mod folder in:

```
%LOCALAPPDATA%Low\tobspr Games\shapez 2\mods\PlatformEfficiencyOverlay\
```

Then enable it in the game's mod list and press the **Platform Efficiency** button in the
visualization bar, bottom right. It remembers whether it was on.

Nothing is written to your save, so you can remove it at any time.

## Options

The debug console (**F1**) has a few knobs, if the defaults do not suit you:

| | |
|---|---|
| `peo.report` | show all current settings |
| `peo.inspect` | measured rate and capacity for the selected buildings |
| `peo.machine-labels 1` | draw the measured number on each machine |
| `peo.tint-alpha 0.4` | make the colours more or less opaque |
| `peo.pips 0` | hide the "it is fed" pips |
| `peo.tracking 0` | stop measuring entirely |

## Known limits

- Machines with speed research read as pinned at 100%.
- Fluid moving directly between docked platforms is not measured (space pipes are).
- On a platform mixing belts and pipes, the gauge's percentage is right but its absolute
  figure adds items and fluid packages together.

## Building it yourself

Set `SPZ2_PATH`, `SPZ2_PERSISTENT` and `SPZ2_SHIFTER` — running the game once with
`--set-modding-env-vars` does this for you — then `dotnet build`. The output goes straight
to the mods folder. Restart the game to pick up changes.

Built on [Shapez Shifter](https://github.com/tobspr-games/shapez2-shifter) by tobspr Games.
Licensed under [Apache 2.0](LICENSE).
