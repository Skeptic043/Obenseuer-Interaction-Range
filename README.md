# Interaction Range

Reach a little farther. Pick up items, open doors and containers, and talk to people from a bit further away.

## Install

### Manual installation

1. Install [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) (`BepInEx_win_x64_5.4.23.5.zip`) in your Obenseuer game folder, following its installation instructions. Obenseuer mods use both BepInEx 5 and BepInEx 6, so make sure you download BepInEx 5. This mod has not been tested with BepInEx 6.
2. Download and extract the Interaction Range ZIP. Copy its `BepInEx/plugins/InteractionRange` folder into `BepInEx/plugins` in your Obenseuer folder.
3. Launch Obenseuer normally.

## How it works

Interaction Range extends how far away you can use things you're looking at. Pickups, doors, containers, conversations, and tap or hold actions all use the extended reach. Objects still need to be in plain view, and locks, ownership, theft and inventory limits work as normal. Building, cleaning and removal modes keep their normal range, and this mod leaves melee range and item placement unchanged.

## Configuration

After the first launch, edit `BepInEx/config/com.skeptic043.obenseuer.interactionrange.cfg`, or use the BepInEx 5 version of [BepInEx Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager).

| Setting | Default | Options |
| --- | --- | --- |
| `Range multiplier` | `1.2` | `1`, `1.2`, `1.4`, `1.6`, `1.8` or `2`. Changes in Configuration Manager apply immediately. |

## Compatibility

No known incompatibilities.

## AI Use

AI was used to write all of the code in this project. The original design direction, testing, debugging, and release decisions are my own. If you prefer not to use mods developed with AI assistance, I understand and respect that choice.

## Issues and links

[Report an issue](https://github.com/Skeptic043/Obenseuer-Interaction-Range/issues) with your settings and `BepInEx/LogOutput.log`.

[Nexus Mods](https://www.nexusmods.com/obeseuer/mods/26) · [Source code](https://github.com/Skeptic043/Obenseuer-Interaction-Range) · [Changelog](https://github.com/Skeptic043/Obenseuer-Interaction-Range/blob/main/CHANGELOG.md) · [Build instructions](https://github.com/Skeptic043/Obenseuer-Interaction-Range/blob/main/BUILDING.md) · [MIT License](https://github.com/Skeptic043/Obenseuer-Interaction-Range/blob/main/LICENSE) · [Support on Ko-fi](https://ko-fi.com/skeptic043) · skeptic043
