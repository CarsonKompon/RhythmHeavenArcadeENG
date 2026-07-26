# Texture Workshop

Texture Workshop is a Windows desktop tool for organizing and generating Flycast replacement textures. It keeps the modified folder flat and ready for Flycast while storing groups, Copy Image relationships, ordering, and brightness in `.texturemod.json`.

## Requirements

- Windows 10 or later
- .NET 8 SDK for building from source

## Run

```powershell
dotnet run --project src/ModdingTool.App/ModdingTool.App.csproj
```

On first launch, choose:

1. **Original folder**: the top-level folder containing dumped PNG textures.
2. **Modified folder**: the top-level Flycast replacement folder that the app updates live.

The last folder pair reopens automatically. Both explorers intentionally show top-level PNG files only.

## Workflow

- Drag an image from **Original Textures** into **Modified / Live Output** to copy it with the same filename.
- Use Ctrl-click or Shift-click to select multiple textures, then drag the selection to copy or group it as one operation.
- Select an original texture to preview it in the inspector without showing output-editing controls.
- Right-click one or more selected originals to mark them as seen, unseen, TODO, or not TODO. Use **Hide seen** and **Hide TODO** above the original browser to focus the review list.
- Drag one modified image onto another to put both in a virtual group. Dragging near either browser's top or bottom edge scrolls while the drag remains active.
- Right-click a modified texture to create or rename its group, remove the selected texture(s) from the group, move a texture within its group, or move the whole group up and down.
- Drop grouped textures onto the blank **Ungrouped** section to remove them from their group. Dropping onto an ungrouped texture still creates a new group containing the source and target textures.
- Output groups have distinct background colors and expandable headers. Expanded/collapsed state and ordering persist in `.texturemod.json`.
- Select a modified image, then drag another modified image into **Copy Image**. The selected image is regenerated from the dragged source but keeps its own filename.
- Adjust **Brightness** from 0 to 100 and select **Save brightness**. Brightness scales RGB while preserving transparency.
- Copy Image chains are supported. Their brightness values multiply through the chain, and cycles are rejected.
- Double-click an image to open it in the Windows-associated image editor. Generated images open their ultimate editable source.
- External saves are detected automatically and dependent images are regenerated.
- Use **List / Grid** to switch between a thumbnail grid and compact file list.

Clearing Copy Image restores the same-named original when one exists. Otherwise, the currently rendered pixels become the image's editable base.

## Project Metadata

The modified folder contains `.texturemod.json`. It records virtual groups and generation settings; PNG output remains flat and Flycast-compatible. Metadata is written atomically, with the prior version retained as `.texturemod.json.bak` after subsequent saves.

Do not edit a generated output PNG directly because regeneration will overwrite it. Double-click it in Texture Workshop to open its resolved base source instead.

## Build And Test

```powershell
dotnet restore ModdingTool.sln
dotnet build ModdingTool.sln --configuration Release
dotnet test ModdingTool.sln --configuration Release
```