# Building Interaction Range

Requirements: Windows, Windows PowerShell 5.1 or PowerShell 7, a .NET SDK supporting the .NET Framework 4.7.2 target and `dotnet msbuild -getProperty`, an owned Obenseuer installation, and extracted BepInEx 5.4.23.5 Windows x64 references from the [official release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5).

The game references can come from any Obenseuer version; the mod was last tested on 0.5.05 (Unity 6000.0.77f1). Extract the loader separately and point `BepInExCorePath` to its `BepInEx/core` directory, containing `BepInEx.dll` and `0Harmony.dll`. The default is `.local/dependencies/BepInEx5/BepInEx/core`. The build verifies those two files against the BepInEx 5.4.23.5 SHA256 hashes and rejects a different reference set.

Pass your paths to the scripts:

```powershell
.\scripts\Build.ps1 -GamePath 'D:\Games\Obenseuer' -BepInExCorePath 'D:\Dependencies\BepInEx\core'
.\scripts\Test.ps1 -GamePath 'D:\Games\Obenseuer' -BepInExCorePath 'D:\Dependencies\BepInEx\core'
.\scripts\Package.ps1 -GamePath 'D:\Games\Obenseuer' -BepInExCorePath 'D:\Dependencies\BepInEx\core'
```

To omit them, create the ignored file `.local/LocalPaths.props`:

```xml
<Project>
  <PropertyGroup>
    <GamePath Condition="'$(GamePath)' == ''">D:\Games\Obenseuer</GamePath>
  </PropertyGroup>
</Project>
```

The scripts stop on failed commands so an old DLL cannot be mistaken for a successful build. Game and loader references are never copied into the plugin output. Acquire those dependencies separately and keep them out of source control and mod packages.

The package script writes the release ZIP (`InteractionRange-<version>.zip`) and a source ZIP to `dist/` from explicit file lists. It checks the extracted files and plugin hash, scans both packages for local path markers, then builds and tests a fresh source extraction and compares its DLL with the original candidate.

The tests use the actual configuration library and game method bodies, plus synthetic managed objects and Harmony wrappers for behavior that can run outside Unity. They do not establish live targeting, configuration-manager rendering or gameplay compatibility.
