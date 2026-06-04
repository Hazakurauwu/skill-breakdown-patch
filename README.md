# ShinraMeter Rotation Patch

Patches the ShinraMeter mod for TeraToolbox (Asura) to send hit-by-hit skill data on each encounter upload. This unlocks the **Skill Breakdown** and **DPS Graph** features on [enragedon.com](https://enragedon.com).

Without the patch, enragedon.com only receives aggregated skill stats (total damage per skill). With it, the site receives the full chronological sequence of every hit, which is used to render the timeline and rolling DPS graphs.

---

## What the patch does

Two things are changed in `DamageMeter.dll`:

1. A `dealtSkillLog` field is added to the `Members` class — this is the list that holds the hit-by-hit data.
2. A call to `ShinraRotationPatch.RotationEnricher.Enrich(stats)` is injected into `DataExporter.AutomatedExport`, right after the existing null check on `stats`. This call populates `dealtSkillLog` for each player before the upload JSON is serialized.

`ShinraRotationPatch.dll` is the helper assembly that does the actual work. Its source is in [`src/RotationEnricher.cs`](src/RotationEnricher.cs) — it mirrors the same loop that `JsonExporter.JsonSave` uses internally, so the data is identical to what ShinraMeter already computes locally.

The patcher source is in [`src/Patcher.cs`](src/Patcher.cs). It uses [dnlib](https://github.com/0xd4d/dnlib) to do the IL injection.

Nothing else is changed. The meter still works exactly the same way — the only difference is that uploads now include the extra field.

---

## Install

**Requirements:** TeraToolbox with ShinraMeter installed.

1. Download the latest release (zip) from the [Releases](../../releases) page
2. Extract anywhere
3. Run `install.bat`
4. The script will find your TeraToolbox folder automatically. If it can't, it asks for the path.
5. Restart TeraToolbox

Backups of your original files are saved as `*.prepatch.bak` in the ShinraMeter folder.

---

## Uninstall

In your `TeraToolbox/mods/ShinraMeter/` folder:

- Copy `DamageMeter.dll.prepatch.bak` → `DamageMeter.dll`
- Copy `ShinraMeter.deps.json.prepatch.bak` → `ShinraMeter.deps.json`
- Copy `module.json.prepatch.bak` → `module.json`
- Delete `ShinraRotationPatch.dll`

---

## Notes

- Auto-update is disabled in `module.json` to prevent TeraToolbox from overwriting the patched DLL with the original.
- The patch is applied to the meter version included in this repo. If ShinraMeter updates to a new version, re-run `build-patch.ps1` against the new DLL (see below).

---

## Re-patching a new meter version

If ShinraMeter updates:

```powershell
# From shinra-rotation-patch root (requires .NET SDK 8)
.\build-patch.ps1 -MeterDir "path\to\new\ShinraMeter" -OutDir ".\release"
```

This re-applies both passes and regenerates `manifest.json`. The script is in [`build-patch.ps1`](build-patch.ps1).

---

## Compatibility

Tested on Windows 10/11 x64. Requires .NET Framework 4.7.2 (already required by ShinraMeter itself).
