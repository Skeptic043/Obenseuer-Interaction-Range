# Building Interaction Range

Requirements: Windows, Windows PowerShell 5.1 or PowerShell 7, a .NET SDK supporting the .NET Framework 4.7.2 target and `dotnet msbuild -getProperty`, an owned Obenseuer installation, and extracted Windows x64 references for both supported loaders:

- [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5)
- [BepInEx 6.0.0-be.755 Unity Mono](https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.Mono-win-x64-6.0.0-be.755%2B3fab71a.zip)

The game references can come from any Obenseuer version. The mod was last tested in game on 0.5.05 (Unity 6000.0.77f1). Extract each loader separately. The default reference directories are `.local/dependencies/BepInEx5/BepInEx/core` and `.local/dependencies/BepInEx6-755/BepInEx/core`. Every loader assembly is checked against the pinned release hash, and the build rejects missing, mixed, or different references.

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

The package script writes `InteractionRange-<version>-BepInEx5.zip`, `InteractionRange-<version>-BepInEx6.zip`, and a source ZIP to `dist/` from explicit file lists. Each plugin ZIP contains exactly one loader-specific `InteractionRange.dll` and no loader assemblies. The script checks the extracted files and plugin hashes, scans every package for local path markers, then builds and tests both targets from a fresh source extraction and compares each DLL with its original candidate.

The tests use each loader's actual configuration and Harmony libraries, the game method bodies, synthetic managed objects, and Harmony wrappers for behavior that can run outside Unity. They do not establish live targeting, configuration-manager rendering, or gameplay compatibility. The 1.0.1 BepInEx 6 artifact has no new live test.
