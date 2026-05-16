# TriggerHelper

A ModSharp plugin for CS2 that highlights mapped entities (triggers, doors,
buttons, …) with a configurable glow so players can see them in-game. Built
for ze / surf / KZ communities where invisible volumes hurt UX.

## Features

- **Helper-prop highlights.** Every matched source entity gets a translucent
  chicken (`models/chicken/chicken.vmdl`) spawned at its bbox center, tinted
  with the configured glow color and outlined by the rim-lit glow. The source
  itself is never touched, so triggers and other model-less entities work the
  same as renderable props.
- **Two glow modes per entity**
  - `glow` — static rim-lit highlight on the chicken.
  - `pulse` — same as `glow` plus the engine's flashing flag (strobe).
- **Classname labels.** A billboard `point_worldtext` floats above each
  chicken with the source entity's classname. The label rotates around the
  up-axis to always face the viewer and is rim-lit with the same glow color
  and range clamps as the chicken, so it's visible through walls. Toggle live
  with `trigger_helper_labels_enabled 0 / 1`.
- **Per-classname configuration.** Each entry has its own glow mode, visibility
  range, and size filter. First-match-wins by file order.
- **Wildcard matching.** `trigger_*`, `func_door?` — case-insensitive.
- **Per-map overrides.** Drop `<map-name>.jsonc` next to `global.jsonc` and it
  replaces the global config wholesale for that map (no merging).
- **Size filter.** Skip map-spanning trigger volumes via `max_size`, skip tiny
  markers via `min_size`. Measured as `(length + width)` of the entity's bbox
  in world units.
- **Runtime toggle.** `trigger_helper_enabled 0 / 1` applies / clears live.
- **Live config reload.** Edit a `.jsonc` file and the plugin reapplies
  the new settings without a map change — also exposed as the admin
  command `ms_th_reload`.
- **Admin debug command.** Lists every entity currently being tracked.
- **Hot-path discipline.** Per-frame batched applies, memoized pattern
  matching, `FrozenDictionary` lookups, source-gen JSON, zero per-entity
  closure allocations.

## Deploy layout

After publish, the plugin lives in two places under your ModSharp install:

```
sharp/
├── modules/
│   └── TriggerHelper/
│       ├── TriggerHelper.dll
│       ├── TriggerHelper.deps.json
│       └── TriggerHelper.runtimeconfig.json
└── configs/
    └── TriggerHelper/
        ├── global.jsonc                 # fallback for any map
        └── maps/
            └── <map-name>.jsonc         # optional per-map override
```

`<map-name>` is the BSP name returned by `IModSharp.GetMapName()` (no
extension, lowercase). A per-map file replaces `global.jsonc` outright — the
two are never merged.

## Config schema

```jsonc
{
    "entities": [
        {
            "name": "trigger_teleport",  // exact name or wildcard (* and ?)
            "glow_type": "pulse",        // "glow" | "pulse"
            "min_glow_range": 0,         // world units, 0 = no lower clamp
            "max_glow_range": 4000,      // world units, 0 = unlimited
            "min_size": 0,               // bbox L+W in world units, 0 = no lower
            "max_size": 100000           // bbox L+W in world units, 0 = no upper
        }
    ],
    "exclude": ["trigger_*_clientside"], // skip these even if matched above
    "style": {
        "color": { "r": 0, "g": 200, "b": 255, "a": 255 }
    }
}
```

### Glow modes

| Mode    | What it does                                                 | When to use                                          |
|---------|--------------------------------------------------------------|------------------------------------------------------|
| `glow`  | Spawn a chicken helper at the entity's bbox center, rim-lit. | Most cases.                                          |
| `pulse` | Same as `glow` plus the engine's flashing flag (strobe).     | Attention-grabbing markers for objectives / hazards. |

Both modes spawn the helper prop — the source entity itself is never
modified, so model-less classes like `trigger_*` work the same as renderable
props. The choice is purely visual.

### Size filter

`min_size` and `max_size` clamp by `(maxs.X - mins.X) + (maxs.Y - mins.Y)`
of the entity's local-space bounding box — sum of length and width in world
units. `0` on either side disables that bound.

Typical use: cap `max_size` to skip map-spanning `trigger_multiple` volumes
that would clutter the map with chickens.

### First-match-wins

`entities` is evaluated top-to-bottom. Put specific classnames before wildcard
fallbacks:

```jsonc
"entities": [
    { "name": "trigger_hurt", "glow_type": "pulse", … },
    { "name": "trigger_*",    "glow_type": "glow",  … }  // catches the rest
]
```

`exclude` is evaluated **before** `entities`, so an excluded classname is
never matched even if a wildcard would have caught it.

## ConVars

| Name                            | Default | Description                                                         |
|---------------------------------|---------|---------------------------------------------------------------------|
| `trigger_helper_enabled`        | `1`     | `1` = plugin active, `0` = clear all chickens / labels and pause.   |
| `trigger_helper_labels_enabled` | `1`     | `1` = render classname labels above chickens, `0` = hide labels.    |

Both are live-toggle. Set in console:

```
trigger_helper_enabled 0          // turn the whole plugin off
trigger_helper_enabled 1          // re-enable, sweep map, re-apply
trigger_helper_labels_enabled 0   // keep chickens, drop text labels
trigger_helper_labels_enabled 1   // bring labels back without re-spawning chickens
```

## Commands

| Command                          | Scope                   | Permission       | Description                                                                                                           |
|----------------------------------|-------------------------|------------------|-----------------------------------------------------------------------------------------------------------------------|
| `ms_th_debug [classname] [unit]` | Server / client console | `admin:debug`    | List live entities matching `classname` (wildcard, default `*`) within `unit` world units (default / `0` = map-wide). |
| `ms_th_reload`                   | Server / client console | `admin:debug`    | Re-read the active config from disk and re-apply glow.                                                                |

Both positional args are optional. Wildcards in `classname` use the same
`*` / `?` syntax as the config (`trigger_*`, `func_door?`,
`*push*`, …). When invoked from server console there's no player position,
so the radius filter is silently dropped and the scan is map-wide.

Output is capped at 50 rows; the count line still reports the unfiltered
total so you know if you're truncating.

Sample `ms_th_debug` outputs:

```
] ms_th_debug trigger_* 1500
[trigger-helper] 4 entities matching 'trigger_*' within 1500u of player
  #42 trigger_teleport dist=423 origin=(100 200 50) size=(64 64 32)
  #43 trigger_push     dist=812 origin=(900 150 50) size=(128 64 32)
  #51 trigger_multiple dist=1100 origin=(-200 600 80) size=(256 128 64)
  #52 trigger_hurt     dist=1430 origin=(300 -400 50) size=(64 64 128)

] ms_th_debug
[trigger-helper] 412 entities on map
  #0  worldspawn              origin=(0 0 0)       size=(0 0 0)
  #1  cs_player_controller    origin=(0 0 0)       size=(0 0 0)
  …
  …and 362 more (capped at 50)
```

### Live config reload

On plugin load the directory `sharp/configs/TriggerHelper/` is watched
recursively. Any save of a `*.jsonc` file under it triggers a reload after
a 300 ms debounce — edits coming in a burst (multi-stage editor saves)
collapse into a single reload. The flow is:

1. Clear every active glow attachment — the chicken and its label are both
   destroyed.
2. Re-parse the relevant config — per-map if one exists for the current
   map, otherwise `global.jsonc`.
3. Sweep all live entities and re-apply if `trigger_helper_enabled 1`. Labels
   are spawned alongside chickens only when `trigger_helper_labels_enabled 1`.

`ms_th_reload` runs the same flow synchronously and prints a one-line
status to the caller's console. It works both with and without the file
watcher running. If the watcher fails to start (missing directory,
permission issue), the plugin logs a warning at load time and falls back
to manual reload only.

## Build & publish

```bash
dotnet publish -c Release -o ./publish
```

Output (only what the host needs — `Sharp.Shared` and `Microsoft.Extensions.*`
are provided by ModSharp itself and are excluded from the publish folder):

```
publish/
├── TriggerHelper.dll
├── TriggerHelper.deps.json
└── TriggerHelper.runtimeconfig.json
```

Drop those three files into `sharp/modules/TriggerHelper/` and the sample
configs into `sharp/configs/TriggerHelper/`. One-liner for redeploy after a
local rebuild:

```bash
dotnet publish -c Release -o ./publish && \
  cp publish/TriggerHelper.{dll,deps.json,runtimeconfig.json} \
     /path/to/sharp/modules/TriggerHelper/
```
