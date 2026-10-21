# Building Interaction Range

Requirements: Windows, Windows PowerShell 5.1 or PowerShell 7, a .NET SDK supporting the .NET Framework 4.7.2 target and `dotnet msbuild -getProperty`, an owned Obenseuer installation, and extracted Windows x64 references for both supported loaders:

- [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5)
- [BepInEx 6.0.0-be.755 Unity Mono](https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.Mono-win-x64-6.0.0-be.755%2B3fab71a.zip)

The game references can come from any Obenseuer version. Version 1.0.2 was startup-tested against Steam build 25969064 (Unity 6000.0.77f1). Extract each loader separately. The default reference directories are `.local/dependencies/BepInEx5/BepInEx/core` and `.local/dependencies/BepInEx6-755/BepInEx/core`. Every loader assembly is checked against the pinned release hash, and the build rejects missing, mixed, or different references.

Select one loader for an individual build or test:

```powershell
.\scripts\Build.ps1 -LoaderTarget BepInEx5 -GamePath 'D:\Games\Obenseuer' -BepInExCorePath 'D:\Dependencies\BepInEx5\BepInEx\core'
.\scripts\Test.ps1 -LoaderTarget BepInEx5 -GamePath 'D:\Games\Obenseuer' -BepInExCorePath 'D:\Dependencies\BepInEx5\BepInEx\core'
.\scripts\Build.ps1 -LoaderTarget BepInEx6 -GamePath 'D:\Games\Obenseuer' -BepInExCorePath 'D:\Dependencies\BepInEx6\BepInEx\core'
.\scripts\Test.ps1 -LoaderTarget BepInEx6 -GamePath 'D:\Games\Obenseuer' -BepInExCorePath 'D:\Dependencies\BepInEx6\BepInEx\core'
```

Build, test, and package both targets together:

```powershell
.\scripts\Package.ps1 -GamePath 'D:\Games\Obenseuer' -BepInEx5CorePath 'D:\Dependencies\BepInEx5\BepInEx\core' -BepInEx6CorePath 'D:\Dependencies\BepInEx6\BepInEx\core'
```

To omit them, create the ignored file `.local/LocalPaths.props`:

```xml
<Project>
  <PropertyGroup>
    <GamePath Condition="'$(GamePath)' == ''">D:\Games\Obenseuer</GamePath>
    <BepInEx5CorePath Condition="'$(BepInEx5CorePath)' == ''">D:\Dependencies\BepInEx5\BepInEx\core</BepInEx5CorePath>
    <BepInEx6CorePath Condition="'$(BepInEx6CorePath)' == ''">D:\Dependencies\BepInEx6\BepInEx\core</BepInEx6CorePath>
  </PropertyGroup>
</Project>
```

The scripts stop on failed commands so an old DLL cannot be mistaken for a successful build. Game and loader references are never copied into the plugin output. Acquire those dependencies separately and keep them out of source control and mod packages.

The package script writes one `InteractionRange-<version>.zip` to `dist/` from an explicit five-file allowlist. It contains `InteractionRange.dll` for BepInEx 6, `InteractionRange.BepInEx5.dll` for BepInEx 5, and the README, changelog, and license inside `BepInEx/plugins/InteractionRange`. No loader or game assemblies are included.

The script checks the extracted file list, DLL hashes, assembly and plugin versions, and loader references. It runs each loader's metadata scanner against the two-DLL folder and verifies that exactly one matching plugin is discovered, including a simulated cached upgrade from the old one-DLL layout. It also scans the package for local path markers, then builds and tests both targets from a fresh internal source stage and compares each DLL with its original candidate. The source stage is a validation input and is not distributed.

The tests use each loader's actual configuration, metadata scanner, and Harmony libraries, the game method bodies, synthetic managed objects, and Harmony wrappers for behavior that can run outside Unity. They do not establish live targeting, configuration-manager rendering, upgrade behavior inside the game, or gameplay compatibility. The 1.0.2 universal layout was launched under both pinned loaders with one plugin instance and its existing setting, but no controlled interaction-range test was performed.
