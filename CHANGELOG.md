# Changelog

## Version 1.0.2

- Replaced the separate BepInEx 5 and BepInEx 6 downloads with one package that includes both loader-specific DLLs.
- Kept the plugin ID, configuration file, setting, and default unchanged. Existing settings carry forward when the package is extracted over an official 1.0.0 or 1.0.1 installation.
- Moved the packaged README, changelog, and license into the mod folder so they do not overwrite another mod's root-level files.
- Verified the universal two-DLL layout starts under BepInEx 5.4.23.5 and BepInEx 6.0.0-be.755 with one Interaction Range instance and the existing `1.2` setting. Gameplay behavior is unchanged from earlier versions.

## Version 1.0.1

- Added a separate build for BepInEx 6.0.0-be.755 Unity Mono on Windows x64.
- Kept the BepInEx 5.4.23.5 build, plugin ID, setting, and default unchanged.
- Both packages passed the same offline behavior and game-assembly checks. The BepInEx 6 package did not receive a new in-game test.

## Version 1.0.0

Initial release.

- Reach farther when picking up items, opening doors and containers, and talking to people.
- `Range multiplier` setting from `1` to `2`, default `1.2`.
