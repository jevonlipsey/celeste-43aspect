# mdinfo

A small Mono/.NET assembly inspector. It reads the metadata tables and method bodies directly instead of loading the assembly, so it works on things this machine cannot run — Wine-Mono builds, x86 assemblies on ARM, or an Everest-patched `Celeste.exe` whose references are only present on the handheld.

It is what found the hook points this mod uses.

## Usage

```bash
dotnet build mdinfo.csproj -c Release

# what methods does a type have?
dotnet bin/Release/net9.0/mdinfo.dll types Celeste.exe SolidTiles

# disassemble them
dotnet bin/Release/net9.0/mdinfo.dll il Celeste.exe GameplayRenderer

# which types can a mod reference at all? (visibility of the type itself)
dotnet bin/Release/net9.0/mdinfo.dll vis Celeste.exe Oui
```

`<assembly>` is any managed `.exe` or `.dll`; `<typeNameFilter>` is a case-insensitive substring of the type name, so `Camera` prints `Monocle.Camera` and anything else that matches.

Analyse the **Everest-patched** `Celeste.exe` from the game folder, not `Celeste.dll` from the install. Everest rewrites method bodies (moving originals to `orig_*`) and that rewritten assembly is what actually runs, so it is the one whose IL you need to match when hooking.
