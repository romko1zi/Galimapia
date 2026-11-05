# Galimapia

This file is the working map of the program. Read it before changing behavior. It records what the program is, which file owns which job, the invariants later math depends on, and the bugs that were already fixed. A short plan is not enough: several of those bugs came back when a later change treated a draft as a cut, or treated `X` and `Y` as the center of the page.

Work one step at a time. Finish the current step, run `dotnet test Galimapia.sln`, and stop. Do not start the next step in the same change.

The application name is Galimapia. Do not rename it to MyMapProject.

## What it is

Galimapia is a Windows desktop compositor for many large old cadastral scans. A survey is dozens of book pages and map sheets, often several thousand pixels on a side. The user drops them on an infinite canvas, crops the book margins, rotates and scales each sheet, and lines them up. Export writes that arrangement to PDF, JPEG, or PNG. Version 1.0.8. Author: Roman Valentyn Badiak.

Each scan is its own object: position, rotation, scale, flip, opacity, brightness, contrast, hue, a crop polygon, and optional corner offsets. There is no bitmap of the assembled map, in memory or on disk. Drawing paints only the sheets that intersect the view, and only a reduced JPEG unless the user has zoomed into that sheet. Source pixels are never rewritten. Crop, color, warp, and later the eraser are data on the fragment.

The user, Roman, builds this in `C:\Projects\C_Sharp\Galimapia`. The real scans already have pyramids under `%LocalAppData%\Galimapia\lod`. Do not delete that cache and do not change its path-hash scheme. Doing either forces every existing scan to rebuild.

## What is built, and what is next

Built through version 1.0.8: window and camera, import with a level-of-detail pyramid, sheet editing and undo, layer panel, crop, two-point join, project folder, icon, welcome screen, live color, corner warp, autosave, PDF/JPEG/PNG export, and the About window.

Not built, and not required for 1.0.8:

1. Eraser. Strokes rasterized to an `Alpha8` mask. Add it only if a polygon cannot cut the leftover on a real scan.
2. A tile pyramid plus an HTML viewer, and a page-curl mesh. The export that ships is a PDF of placed scans and a flattened JPEG or PNG.

Do not add NetVips or BigTIFF unless a later archival TIFF needs them. PDF is SkiaSharp's `SKDocument`.

## Stack

- .NET 9 only (`net9.0`). The machine has the .NET 9 SDK. Nullable and implicit usings are on.
- Avalonia 12.1.5, Fluent dark theme, Inter font. One process. The window shell is XAML. The canvas is a custom `Control` plus Skia.
- SkiaSharp 3.119.4 comes through `Avalonia.Skia`. Do not add MonoGame.
- CommunityToolkit.Mvvm 8.4.2 is referenced. The canvas does not use a view model. `ViewModels/MainViewModel.cs` is empty. `ViewLocator.cs` is the Avalonia template and is not on the editing path.
- AvaloniaUI.DiagnosticsSupport 2.2.3 is debug-only.
- Tests are xunit 2.9.2 in `tests/Galimapia.Tests`, which references the app project and SkiaSharp directly.

Run: `dotnet run --project Galimapia.csproj`

Tests: `dotnet test Galimapia.sln`

This is a desktop app. Verify with the test suite. Do not launch the GUI just to look at it.

The test project sits inside the app directory. `Galimapia.csproj` removes `tests/**` from `Compile`, `EmbeddedResource`, and `None`. Keep that remove. Without it the SDK compiles the test project into the WinExe.

## Repository

```
Galimapia.sln
Galimapia.csproj          WinExe, net9.0, ApplicationIcon = Assets\galimapia.ico
app.manifest              Windows 10 compatibility. Leave it. Avalonia uses it for transparency.
Program.cs                STAThread, classic desktop lifetime, Inter font, developer tools in Debug.
App.axaml                 Fluent dark. ViewLocator data template.
App.axaml.cs              Creates MainWindow. DataContext is the empty MainViewModel.
docs/PROJECT.md           This file.
Assets/galimapia.ico      Windows exe, taskbar, and shortcut. Sizes 16, 32, 48, 256. Classic BMP-in-ICO.
Assets/avalonia-logo.ico  Template leftover. The window does not use it.
Assets/icons/             Avalonia resources. Toolbar and welcome images.
Models/                   Empty folder include from the template. Nothing lives here.
ViewModels/               Empty MainViewModel and ViewModelBase. Do not move canvas state here casually.
Views/MainWindow.axaml    Editor chrome and the welcome overlay.
Views/MainWindow.axaml.cs Toolbar, layers, welcome, save keys.
Canvas/                   The map. All of the behavior that matters is here.
tests/Galimapia.Tests/    xunit. Not compiled into the app.
bin/, obj/                Build output. Ignore.
```

`Assets/**` is an `AvaloniaResource`. Icons are addressed as `avares://Galimapia/Assets/icons/<file>.png`. A loose file on disk is not enough for `AssetLoader` or an `Image` in XAML. Welcome status icons are loaded with `AssetLoader.Open`, not from `AppContext.BaseDirectory`.

Icon files and what they are for:

| File | Use |
| --- | --- |
| `icons/icon.png` | Window icon. Source is a 236×236 PNG with transparency. Avalonia accepts that directly. |
| `galimapia.ico` | `ApplicationIcon`. Windows `rc.exe` wants a classic icon, so the PNG was resized to 16, 32, 48, and 256. |
| `icons/rotate.png`, `rotate-cw.png` | Step −90° and +90°. |
| `icons/mirror.png`, `reset.png` | Horizontal flip, and reset of rotation, scale, and flip. |
| `icons/crop.png` | Polygon crop. The rectangle tool is a drawn border, not this image. |
| `icons/delete.png`, `layers.png`, `up.png`, `down.png` | Delete, layer-panel icon, bring forward, send backward. |
| `icons/welcome-map.png`, `welcome-new.png`, `welcome-folder.png` | Welcome title, New project, Open and the Projects button. |
| `icons/welcome-ok.png`, `welcome-missing.png` | Recent project is available, or the folder is gone. |

The emoji originals live in `C:\Projects\128` as `emoji_uXXXX.png`. Copy a chosen file into `Assets/icons` under a short name. Do not reference `C:\Projects\128` from the running app.

A desktop shortcut may exist at `%USERPROFILE%\Desktop\Galimapia.lnk`. It is outside the repo. Its target is `cmd.exe`, its working directory is this repo, and its icon is `Assets\galimapia.ico`. Do not rewrite that shortcut unless asked. Changing only `IconLocation` is safe. Replacing the whole shortcut wipes the build-and-run arguments.

## Canvas files

`Canvas/Camera.cs`. Looks at an infinite plane. `Zoom` is device-independent pixels per world unit. `1` is 100%, which means one source pixel per DIP when the sheet scale is also 1. Range is `0.002` … `64`.

- `ScreenToWorld`: `center + (screen - viewSize / 2) / zoom`
- `WorldToScreen`: `(world - center) * zoom + viewSize / 2`
- `PanByScreenDelta` subtracts the screen delta divided by zoom.
- `ZoomAt` keeps the world point under the cursor fixed. The wheel uses factor `1.1 ^ ticks`, and ticks are clamped to ±8.
- `Fit` centers a world rectangle and picks the zoom that fits it with a margin. A tall scan in a short window lands well below 100%. That is why key `0` must not call `Fit`.

World coordinates are `double`. Convert to `float` only at the Skia call.

`Canvas/MapCanvas.cs`. The control. Middle button pans. Wheel zooms toward the cursor. Left button selects and edits, or drives the active tool. Drop image files, or a folder of them, onto the canvas. `MapScene.CreateForApp()` wires the shared LOD store, a 1 GB cache, and `AvaloniaUi.RunAsync`.

Public surface the window uses: `ZoomText`, `CenterText`, `CursorText`, `SheetsText`, `ToolHint`, `Selected`, `Sheets`, `SheetCount`, `StructureVersion`, `Tool`, `CanConfirmCrop`, `ProjectFolder`, `Notice`, `SaveProjectAsync`, `OpenProjectAsync`, `HandleKey`, `Select`, `SetTool`, `ConfirmCrop`, `StepRotation`, `CommitRotation`, `ToggleMirror`, `ResetAppearance`, `DeleteSelected`, `BringForward`, `SendBackward`, `BeginOpacityEdit`, `PreviewOpacity`, `EndOpacityEdit`, `MoveSheet`, `IndexOf`.

`SetTool` does nothing useful when no sheet is selected: it clears any tool and returns. `ConfirmCrop` applies the draft and then sets `_selected` to null, so the toolbar locks again. `OnSceneChanged` also clears the tool if the selected fragment has disappeared, with an `_inSceneChange` guard so `CancelDraft` cannot re-enter the handler.

`Canvas/MapScene.cs`. The sheet list, import, LOD queue, undo, crop session, and project install. Mutate it on the UI thread only. Background work posts back through the invoker given to the constructor. `Version` bumps on almost every visual change. `StructureVersion` bumps when sheets are added, removed, or reordered. The layer panel rebuilds cards only when `StructureVersion` changes, so a drag of the sheet does not rebuild the list. Pose changes must not bump `StructureVersion`.

`Canvas/MapFragment.cs`. One scan. `Id` is a guid `"n"` (32 hex chars, no dashes) unless a project file supplies one. Ids are stable. A future mask file will use the same id. `Name` is the original file name and stays that way after save, even though the copy in `sources/` is named by id. `Retarget` changes `SourcePath` only.

`Canvas/SheetFrame.cs`. The pose math. See the coordinate section. `NaN` pivot and box fields mean "full page".

`Canvas/SheetVisual.cs`. A snapshot of one sheet for a single frame: leased image, pose, crop, draft, pivot, and content box. `Capture` copies those fields so the draw operation does not read the live fragment. `Dispose` releases the lease, not the source file.

`Canvas/MapDrawOperation.cs`. `ICustomDrawOperation`. One frame. `Equals` and `GetHashCode` must include every input that affects pixels, including the export page and the rubber-band cursor. If a field is missing, Avalonia reuses the previous operation and the screen lies. `HashCode.Combine` accepts at most eight arguments, so the extra flags are a nested combine.

`Canvas/SheetGrid.cs`. Column count is `ceil(sqrt(count))`, or 1 for a single sheet. Cells step by the max width and max height of that drop, plus `MapScene.Gap` (48). Row by row. The first sheet's top-left is the world point under the drop.

`Canvas/SheetAlign.cs`. Two-point join. One translation, one rotation, one scale. Uses `PivotLocalX` and `PivotLocalY`, so an uncropped sheet still pivots on the page center and the old test numbers stay valid.

`Canvas/CropPolygon.cs`. Point-in-polygon, rectangle corners, self-intersection, and the close distances. `CloseLoupeDips` is 88. `CloseSnapDips` is 22. `SegmentsCross` is a proper crossing, so a shared endpoint is not a crossing. `CanAppend` refuses a new segment that crosses an existing one. `CanClose` checks the segment from the last point back to the first. `IsSimple` is the test used before the check button is allowed. `Same` compares drafts to the committed polygon.

`Canvas/SheetEdits.cs`. Undo records. `PoseEdit` (move, scale, rotate, flip, opacity), `DeleteEdit`, `OrderEdit`, `CropEdit`. Crop undo stores the polygon, the rectangle flag, the committed flag, and `X`, `Y` before and after, because confirming a crop rebases the position.

`Canvas/LodPlan.cs` and `Canvas/LodStore.cs`. Which reduced JPEGs exist, and where they are written. Two layouts, described below. `PreparedImage` is the result of a prepare: pixel size plus the level list. The full level points at the original file. It is not a second JPEG.

`Canvas/ImageFiles.cs`. Extensions `.jpg`, `.jpeg`, `.png`, `.webp`, `.bmp`, `.gif`, `.tif`, `.tiff`. `TryReadSize` uses `SKCodec` and does not decode pixels. `Decode` copies into an `SKImage`. `WriteScaledJpeg` prefers `GetScaledDimensions`, and if the codec returns `InvalidScale` it decodes full and resizes. JPEG quality is 80. The encode writes `destination.tmp` and then moves it. Open files with `FileShare.ReadWrite` so a scan that is still being copied can be read. Dispose the resized bitmap in `finally`. An earlier bug double-disposed the pixmap.

`Canvas/ImageCache.cs`. LRU of `SKImage`, default budget 1 GB. Key is `fragmentId + ":" + longSide`. `TryLease` increments a use count. Eviction of a leased image moves the node to `_retired` and disposes it only when the lease is released. A frame must dispose its `SheetVisual`, which disposes the lease. `ImageLease` is safe to dispose twice.

`Canvas/AvaloniaUi.cs`. `RunAsync` runs the action inline when it is already on the UI thread, otherwise `Dispatcher.UIThread.InvokeAsync`. After `ConfigureAwait(false)`, do not assume the Avalonia sync context. Post scene mutations through `scene.RunOnUi`.

`Canvas/ExportPage.cs`. The white square. Not a bitmap.

`Canvas/ProjectFile.cs`. Read and write `project.json`.

`Canvas/RecentProjects.cs`. The welcome list. Default file is `%LocalAppData%\Galimapia\recent.json`.

## Data on disk

Three different places. Do not mix them.

Unsaved pyramids, shared by every drop before it is saved:

```
%LocalAppData%\Galimapia\lod\{20-hex sha256}\
    ready.txt
    512.jpg
    1024.jpg
    2048.jpg
    and sometimes {half}.jpg
```

The fingerprint is `sha256(fullPath + "|" + length + "|" + LastWriteTimeUtc.Ticks)`, first 20 hex characters, lower case. The full-resolution level is the file the user dropped, not a copy in this folder. A move or a timestamp change is a different key, so the pyramid rebuilds. That is acceptable for unsaved drops. It is not acceptable for a saved project, which is why the project store is keyed by sheet id.

A project folder, created by the welcome screen as `{parent folder}/{project name}/`:

```
project.json
sources/{fragmentId}{ext}     ext is lower case, ".img" if the source had none
cache_lod/sheets/{fragmentId}/ready.txt
cache_lod/sheets/{fragmentId}/{longSide}.jpg
masks/                        empty until the eraser exists
```

`project.json` is version 1, camelCase, indented, nulls omitted. Shape:

```
version, camera { centerX, centerY, zoom },
export { x, y, size } or omitted,
sheets[] {
  id, name, source, pixelWidth, pixelHeight,
  x, y, rotation, scale, flipX, flipY, opacity,
  cropCommitted, cropRectangle,
  crop[] { x, y },
  brightness, contrast, hue,
  warp[] { x, y }    omitted when there is no stretch
}
```

`source` is relative to the project folder, with forward slashes. `Relative` refuses a path that is rooted or starts with `../`. `Resolve` refuses a path that escapes the folder after `GetFullPath`. Sheet ids must be valid file names.

`ready.txt` is the text `v1|{width}x{height}|{comma-separated long sides}`. `TryLoadFragment` checks the stamp and that each reduced JPEG exists. It does not decode pixels. `PublishFragment` returns immediately when that stamp already matches, so a second save does not rewrite `512.jpg`. Otherwise it copies missing reduced JPEGs from the old path-hash directory (`FindPyramid`) and encodes only the levels that are still missing.

Recent projects:

```
%LocalAppData%\Galimapia\recent.json
{ "projects": [ { "name", "folder", "openedUtc" } ] }
```

Newest first, at most 12. Remembering the same folder updates it in place and moves it to the top. A missing folder stays in the list. `IsAvailable` is `Directory.Exists(folder)` and `File.Exists(project.json)`. It is computed, not stored. A corrupt file loads as an empty list.

## Welcome, then the editor

`MainWindow` starts with the `Welcome` overlay visible and covering all three rows. The overlay is hit-tested, so the canvas underneath does not receive clicks. `OnPreviewKeyDown` does not pass Delete and the other map keys through while the overlay is visible. Ctrl+S is the exception: it saves the open project and leaves the overlay up. The text box still receives keys because the handler does not mark them handled.

New project asks for a name and a parent folder, then creates `{parent}/{name}/`. `RecentProjects.TryProjectName` trims, rejects empty names, names longer than 80 characters, any character in `Path.GetInvalidFileNameChars`, `.` and `..`, and Windows device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`) compared on the file name without extension. If that folder already contains `project.json`, it is opened. If it exists, is not a project, and is not empty, creation stops with an error. An empty directory is used. `ProjectFile.CreateEmpty` writes version 1 with no sheets, camera at the origin, zoom 1, and no export page.

Open requires `project.json`. A plain folder of scans is not a project. Drop scans after the project is open.

Recent rows show the name, the full path, and Available or Unavailable. An unavailable row does not open. The folder button in the edit toolbar (`ProjectsButton`, the open-folder icon) shows the welcome screen again. Back is visible only when a project is already open, and it hides the overlay without changing the canvas.

Once a project is open, Save and Ctrl+S write `Map.ProjectFolder`. They do not ask for a folder. Ctrl+S still saves while the welcome screen is open, and it leaves that screen up. The window title becomes `Galimapia — {folder name}`. Every five minutes, and again when the window closes, a project that has user edits is written by itself. The status line says `Autosaved`. Loading a pyramid or decoding a level does not count as an edit. `MapScene.EditVersion` is the counter. Opening or creating a project marks the current value as clean. The first version of Save asked for a folder and wrote `project.json` directly into the folder the user picked, which made it unclear that a project is a directory full of copies. The welcome screen exists so the name and the parent are chosen before any of those directories appear.

Opening a project replaces the canvas. There is no "save changes?" prompt. `Install` clears the gesture, the crop session, both undo stacks, and every draft. Save does not clear undo.

`OpenProjectAsync` reads on a worker thread, then `Install` on the UI thread. It restores `CenterX`, `CenterY`, and `Zoom` from the file. Zoom is clamped to the camera range. It does not call `Fit`. A zoom of 0 or less in the file becomes 1.

## Save and the white page

`SaveProjectAsync` snapshots sheets on the UI thread, computes `ExportPage.TryFrom`, then `ProjectFile.Write` on a worker. `ApplySave` points each fragment at `sources/{id}…`, swaps in the project LOD store, and marks the pyramids ready. The notice in the status line says `Saving`, then `Saved`, or the exception message.

`CopySource` skips the copy when the destination is the same path, or when the destination exists with the same length and the source is not newer. The original file the user dropped is not modified. Display `Name` is not replaced with the id file name.

The white page is a world-space square drawn behind the sheets. It is the boundary a future export will use. It is not a raster and must not become one. A bitmap of the union of many 4000-pixel scans would break the "no whole-map bitmap" rule and would make open slow.

`ExportPage.TryFrom` unions `GetVisibleBounds` of every sheet. That box includes rotation and the crop polygon, not the hidden page margin. The side is `max(width, height)`. The square is centered on the union, so the short axis has equal margin and the long axis touches the extremes. No extra padding. Side below 1, or no sheets, means no page. The page is recomputed only on save. Dragging a sheet does not move it. It is stored in `project.json` and restored on open.

Draw order in `MapDrawOperation.Draw`: dark background `#242220`, grid, white export square, sheets from back to front, crop handles or the draft, the close-up loupe, the "Drop scans here" message when empty, and the drop hover frame. The white fill is `SKColors.White` with a 1.5 DIP stroke `#C4BEB4`, in screen space via `ScreenX` and `ScreenY`. Hit testing ignores the square. The grid is still drawn underneath, so the square covers the grid inside the page.

## Import and level of detail

Drop uses `IDataTransfer.TryGetFiles()` and `IStorageItem.TryGetLocalPath()`. A directory is enumerated one level, not recursively. Paths are sorted ordinal-ignore-case. Unknown extensions are skipped and counted in the status line.

`ImportAsync` reads headers off the UI thread (`ConfigureAwait(false)`), then places fragments on the UI thread through `RunOnUi`. It must not `Invoke` back onto the UI thread while a test pump is blocked inside `RunOnUi`. The placed callback runs on the UI thread. The first drop, and any drop of two or more files, frames the camera on the sheets just added. It does not reframe sheets already on the canvas. One extra sheet stays at the cursor and does not change the zoom.

Reduced long sides are 512, 1024, and 2048 when each is smaller than the source, plus half the source when that half is greater than 2048 and still smaller than the source. A 4032×3024 scan therefore gets 512, 1024, and 2048. The original is the last level and is not copied into the pyramid. Pyramid builds are limited to 2 at a time, decodes to 4. Release the semaphore in `finally`, and catch `ObjectDisposedException` on release. An earlier bug released a semaphore that had not been acquired.

`BuildPyramidAsync` tries `_projectStore.TryLoadFragment` first. On a hit it does not encode. Otherwise it captures `var store = _store` and calls `Prepare` on that local, so a store swap during the await cannot race the lambda. `ApplyPyramid` ignores the callback when the fragment is no longer in `_fragments`, so a pyramid from a replaced project cannot attach. It still bumps `Version` and fills the queue when it does apply.

Only sheets whose visible bounds intersect the view, expanded by 80 DIPs, are decoded. `NeededPixels` is `ceil(max(PixelWidth, PixelHeight) * abs(Scale) * Zoom)`. That is the on-screen long side of the whole sheet, not the crop box. Do not switch it to the crop AABB. The level is the smallest whose long side covers that need. Hysteresis: keep the held level while the need is at most that level and at least 70% of it. While a sharper level is loading, `BestAvailable` draws the closest cached level. A failed decode stays in `_failed` until that sheet changes level, or until the project is opened or saved. It is not retried on every mouse move. After one more failure it is remembered again. The cache key includes the sheet id, so two sheets do not share a decoded image.

`PreviewPath` is the smallest non-full level, used for the layer thumbnail. The original is skipped when its long side is above 512, so the card does not decode a multi-megapixel scan. Thumbnails are decoded to width 144 off the UI thread.

## Coordinates, and why X and Y must stay the full-page corner

`X` and `Y` are the top-left of the unrotated full pixel box. They are not the center, and they are not the top-left of the crop. Scale 1 and rotation 0 still put source pixel `(0, 0)` at `(X, Y)`, whether or not a crop is applied.

Rotation and scale are around the pivot. An uncropped pivot is the page center (`Width / 2`, `Height / 2`). An applied crop (`CropCommitted` and at least four points, with a box at least 1×1) pivots around the crop's axis-aligned center in pixel space. `SheetFrame` stores that pivot and the crop box. `NaN` means full page.

```
world = (X + pivot) + R(scale * flip * (local - pivot))
```

At rotation 0 and scale 1 this is `(X + localX, Y + localY)` regardless of the pivot. Flip is applied in local space before the rotation.

Changing the pivot on an already rotated sheet moves every pixel unless `X` and `Y` are rebased with the same matrix. `MovePivot` does that:

```
sx = (newPivotX - oldPivotX) * (flipX ? -scale : scale)
sy = (newPivotY - oldPivotY) * (flipY ? -scale : scale)
X += oldPivotX + (sx * cos - sy * sin) - newPivotX
Y += oldPivotY + (sx * sin + sy * cos) - newPivotY
```

`ConfirmDraft` reads the old pivot from the frame before it replaces the crop. `EndCrop` (the older path the tests still call) computes the old pivot from the snapshot via `PivotOf`, not from the live frame, because `AddCropPoint` has already mutated the polygon. Using the live pivot there makes the picture jump, and the undo test that captures `ToWorld(10, 10)` fails.

Draw matches the pivot. Translate to `frame.Center`, rotate by the sheet rotation, scale by `zoom * scale` with the flip signs, and draw the bitmap into `(-pivotX, -pivotY, width - pivotX, height - pivotY)`. The clip path is the crop points minus the pivot, not minus `width / 2`. At pivot = center this is the old destination `(-W/2 .. W/2)`.

After a cut, scale corners and the rotate stick use the content box (`EdgeLeft` and the other edges), not the full-page corners. The rotate stick sits 22 DIPs beyond the top edge of that box, away from the content center. Handles are drawn when the content's long side is at least 48 DIPs on screen. Pick radius is 10 DIPs. An earlier bug left the full-page corners and the rotate stick on the uncut page, so the handles and the picture disagreed about what the object was.

`GetVisibleBounds` uses the four corners of the full page when there is no cut, and the crop polygon's world points when there is one. Culling, the export square, and `Reveal` all use that. `ContainsWorld` uses the polygon too, so a click in the hidden margin does not select the sheet. `ContainsFrame` and `HitTestFrame` use the full pixel rectangle. The crop tool uses `HitTestFrame`, so a new outline can be started on the margin that is still visible while drafting.

Scale is clamped to `0.05` … `8`. Opacity is `0` … `1`. A fully transparent sheet can still be selected.

## Editing and the toolbar

The window is three rows: canvas plus layer panel, the edit toolbar, the status bar.

The status fields are padded Consolas: zoom, center, cursor, then the sheet summary. The hint `Middle mouse pan, wheel zoom, 0 = 100%` is its own right-aligned text block. Do not put that hint back into the coordinate string. Digit changes were shoving it sideways. Notices (`Saving`, `Saved`, `Opened`, errors) are appended to the sheet summary after the cache size.

The edit row is a horizontal `ScrollViewer`. Delete stays in its own column on the right, outside the scroller. Variable text (sheet name, tool hint, pose) can change width without moving Delete.

Tools stay disabled until a sheet is selected. That includes rotate, mirror, reset, both crop modes, join, opacity, delete, and the layer up and down buttons. Crop and join used to enable whenever any sheet existed, so a click on the tool followed by a click on a page both selected the page and started a crop. After the check, choosing the tool again showed the whole uncut page and looked like the crop had been lost. The rule now is: click the sheet, then use a tool. The green check applies the crop and clears the selection, so the tools lock until the next click. Clicking a different sheet while a tool is armed selects that sheet and does not also place a crop point. The next click edits it.

Buttons, grouped:

- Projects (folder icon) and Save.
- Step −90° and +90°, then absolute `0` `90` `180` `270`, then horizontal mirror.
- Reset. Rotation, scale, and both flips return to the loaded look. Position, opacity, and crop stay.
- Crop: a small rectangle icon, scissors, and a green check. The check is visible only when `CanConfirmCrop` is true: the tool is rectangle or polygon, the draft has at least four points, `IsSimple` is true, and the box is at least 1×1. Enter confirms. The check is the only way the UI cuts the page.
- Join, in its own group, not inside the crop group.
- Warp, in its own group. It is not the move tool. While it is on, dragging the sheet body does nothing. Only the blue points stretch.
- Opacity slider `0` … `100`. Bright, contrast, and tint sit beside it, each 28 DIPs tall. Bright is −100…100, contrast is 0…200 with 100 as the original, tint is −180…180 degrees. One undo entry per slider gesture, from pointer press to pointer release, including keyboard arrows. Do not steal Left and Right from a slider. The window's preview handler must not treat those keys as map keys while a slider is editing. The filters are `SKColorFilter` color matrices in `Canvas/ColorAdjust.cs`. Identity (0, 1, 0) draws the scan unchanged.
- Delete.

`[` and `]` still send backward and bring forward. The same two commands are the up and down buttons in the layer-panel header. They are not on the edit row.

The layer panel is 280 DIPs open and 48 collapsed, on the right. Cards are front at the top. Index 0 in `_fragments` is the back and is painted first. Drag a card to reorder. While dragging, the cards around the drop slot use a vertical margin of 16 instead of 3, so the gap shows where the card will land. Thumbnails load lazily.

Click selects. Drag the body to move. Drag a corner to scale around the pivot. Drag the rotate stick to rotate around the pivot. Middle button still pans during those gestures. Escape during a move, scale, or rotate cancels that gesture. Escape with nothing in progress clears the selection. A second Escape is needed to clear the selection when a tool is on, because the first Escape leaves the tool.

Delete and Backspace remove the selection, except while a crop tool is on: Backspace removes the last draft point. Undo puts a deleted sheet back at the same index.

One undo entry per gesture. `BeginEdit` captures the pose. `EndEdit` pushes a `PoseEdit` only when the pose changed. Delete, reorder, reset, join, and an applied crop are their own entries. Ctrl+Z undoes. Ctrl+Y and Ctrl+Shift+Z redo. Ctrl+Z during a drag cancels that drag and does not also pop the previous edit. The order inside `Undo` is: cancel the pose gesture, else cancel a draft, else cancel an old crop session, else pop the stack. Redo uses the same cancel order.

Key `0` and numpad `0`, without Control or Alt, set `Zoom = 1` and leave the center where it is. Fitting the view on that key was the bug that jumped a 3024-pixel sheet from 100% to about 23%.

## Crop

The polygon is in the sheet's pixel space. The source file stays whole. Two lists: `Crop` is the applied cut, `Draft` is the editing buffer. `HasCut` is `CropCommitted && Crop.Count >= 4`. Clip, pivot, handles, hit testing for selection, and the export bounds use `Crop` only when `HasCut` is true.

While a new outline is being drawn, the whole page is visible. `hideCut` is true only when the sheet is selected, a crop tool is on, the draft is non-empty, and the draft is not an exact copy of the committed crop. Opening the tool on an already cut sheet keeps the cut. Copying the committed polygon into the draft so its vertices can be dragged does not, by itself, reveal the margin. Starting a new outline, or moving a vertex, does.

The draft is bright red (`255, 36, 48`). The first point is a larger ring with a light core. Other points are small handles. Segments are drawn between placed points. While the polygon is still open, the closing edge from the last point back to the first is not drawn. That early closing edge was the red "boundary" that appeared as soon as there were four points and made an unfinished outline look like a finished shape. A rubber line follows the cursor instead. After the user closes the outline, and before the check, the closing edge is drawn, because the draft is finished.

Move the cursor toward the first point and, inside 88 DIPs, a circular loupe appears above the cursor (below it if there is no room). Magnification runs from about 2.2 to about 4.8 as the cursor gets closer. The start ring on the page also grows. Inside 22 DIPs, with at least four points and `CanClose`, the rubber line snaps to the start point, turns green (`#8FDB6A`), and a check mark is drawn. The tool hint says the start point is matched. The hit radius of that first point grows from 16 to 22 DIPs so the click lands. Other vertices stay at 11 DIPs. Clicking the start point sets `_polyOpen` false and does not add a duplicate point. A double-click does not close the outline by itself. The check button can then cut.

Rectangle mode replaces the draft with `CropPolygon.Rectangle` (top-left, top-right, bottom-right, bottom-left) while the pointer is down. Releasing the pointer does not apply the cut. It only ends the gesture. A later drag of a rectangle vertex stores the opposite corner at press (`index + 2` mod 4) and rebuilds the rectangle from that anchor.

Right-click cancels the draft and stays in the tool. Escape during a drag cancels the draft and stays in the tool. Escape otherwise cancels the draft and leaves the tool. Switching tools, selecting another sheet, or `StopCrop` cancels the draft and does not apply it. A click that misses the page does not change the draft.

Self-intersection is rejected. A new point that fails `CanAppend` is ignored. A vertex drag that would make the polygon non-simple is ignored.

After the check: red points disappear, the outside of the polygon is clipped, handles and the rotate pivot move to the crop's extreme pixels, and `X` and `Y` are rebased so the picture does not jump. The cut edge is the normal gold or brown sheet border, not red. One `CropEdit` restores both the polygon and the position.

`EndCrop` is still used by tests that call `AddCropPoint` and `PreviewCrop`. A gesture of one or two points restores the snapshot. Three or more points can be kept, but the page clips only at four or more and only when committed. Do not make `EndCrop` read the pivot from the already mutated frame.

## Two-point join

Join does not bend the page. The sheet that stays is not moved.

1. Click a spot on the sheet that should stay. That click is stored as a world point.
2. Click a second world point on that same sheet.
3. Click the same first spot on the sheet that should move. That click is stored as a pixel position.
4. Click the same second pixel position. `SheetAlign.TryPlace` sets `X`, `Y`, rotation, and one scale.

Flip is kept. If the two local points or the two world points are closer than 1 unit, the placement is refused. If the required scale is outside `0.05` … `8`, the scale is clamped. The first pair still meets and the second pair is only approximate. One undo entry. Escape leaves the tool. Marks are numbered in screen space. The rubber line is hidden between mark 2 and mark 3 because those two pairs are on different sheets.

## Color

Brightness, contrast, and hue are numbers on the fragment. `Canvas/ColorAdjust.cs` builds one color matrix and `MapDrawOperation` sets it on the image paint. A warped sheet uses the same filter on the mesh paint. Nothing is written back to the JPEG. Missing values in an old `project.json` mean brightness 0, contrast 1, hue 0. Contrast is stored as a `double?` so an omitted field does not become 0.

## Warp

`MapTool.Warp` is a separate tool. Move, scale, and rotate stay on the normal pointer path and do not run while Warp is selected. A press that is not on a blue point does not move the sheet. A press on another sheet only selects it.

Control points are the four corners of the full page. After a confirmed crop they are the crop vertices, in the same order. Each control has an offset in pixel space, stored in `MapFragment.Warp`. The offset is added before `ToWorld`, so rotation, scale, and moving `X` and `Y` carry the stretch along. Dragging a point changes only that offset. `X` and `Y` stay put.

The mesh is an ear-clipped triangulation in `Canvas/WarpMesh.cs`. Texture coordinates are the unmoved control points, scaled into the decoded level so a 512 JPEG still covers the full page. If triangulation fails, the sheet draws the ordinary way. While every offset is near zero, or the offset list length does not match the control count, the ordinary `DrawImage` path is used. Confirming a crop that actually changes the polygon clears the offsets, and undo of that crop puts them back.

The blue point is hit in screen space, including its center, before any sheet body test. A corner lies on the polygon edge, so a body test misses it and the click used to fall through. Another sheet covering that same pixel does not steal the drag from the selected sheet. The point follows the cursor from where it was grabbed. It snaps to another sheet's control point within 10 DIPs, unless that would land on a different point of the same sheet and collapse a triangle. A collapsed triangle is not sent to the GPU: `KeepSolid` drops it. That junction of three or four sheets was crashing the process. The snap is only in the warp tool. It does not call `SheetAlign` and it does not move the rest of the sheet. One `WarpEdit` per drag. Escape during the drag restores the offsets and stays in the tool.

## Export

Export measures the sheets on the canvas at that moment (`ExportPage.TryFrom`) and writes the white square. It does not reuse a stale square from the last save. `Canvas/PageExport.cs` draws back to front, with crop, warp, flip, opacity, and the color filter. Each scan is decoded from its source file one at a time and disposed. There is still no bitmap of the whole map at full scan resolution.

A warped sheet is painted as ordinary image triangles, one triangle at a time, then placed with `DrawImage`. A vertex mesh samples the scan in the wrong place and turns patches of the sheet gray. The PDF is not written by Skia's document encoder. That encoder stored RGB samples in a JPEG and then marked them as already converted, so every viewer turned the paper green. Each sheet is encoded with the same JPEG writer as the JPEG export and placed on the page with its own transparency mask. Quality picks the long side of that picture: Screen 2048, Print 4096, Fine 8192. The page is never larger than the world square, and a PDF page stays at or under 14400 points. Each source is decoded only as large as it will appear at that quality.

Map tiles are the format for a project with many sheets. The export is a folder: `tiles/{level}/{x}/{y}.jpg` plus `index.html`. A browser opens the html file, and only the tiles on screen are loaded. Tile size is 512. The finest level is the chosen long side.

Export shows a window with the format, the quality, a percent, the sheet or tile it is writing, and Stop. Stop deletes the unfinished file or folder. Author and creator in the PDF metadata are `AppInfo`. The About button opens `Views/AboutWindow` and shows version 1.0.8 and Roman Valentyn Badiak.

## Drawing rules that Avalonia 12 gets wrong

- `Control` has no `Background`. `Panel.Render` is sealed. `MapCanvas` is a `Control`. `FillRectangle` is the hit target. The grid is Skia.
- `ICustomDrawOperation` is `Avalonia.Rendering.SceneGraph.ICustomDrawOperation`.
- `ISkiaSharpApiLeaseFeature` is marked unstable. Lease it from `ImmediateDrawingContext`.
- The leased `SKCanvas` already has Avalonia's DPI matrix. `Save`, draw in control-local DIPs, `Restore`. Do not replace the matrix.
- Pointer positions are DIPs, the same space as the draw.
- `Button.ToolTip` does not exist. Set `ToolTip.Tip` in XAML, or `ToolTip.SetTip` in code.
- A `Border` does not take the old `FontFamily` setters that a `TextBlock` does. The compiler reports AVLN2000.
- `TextBox.Watermark` is obsolete. Use `PlaceholderText`.
- The folder picker is `StorageProvider.OpenFolderPickerAsync(FolderPickerOpenOptions)`.
- After `await Task.Run`, marshal back with `RunOnUi` or `AvaloniaUi.RunAsync`.

Grid lines: step is `NiceStep(96 / zoom)`, so the spacing stays near 96 DIPs. Every fifth line is heavier. The line through world 0 is gold `#D7B98A`. The loop bails out after 400 lines so a tiny zoom cannot hang the frame. Antialiasing is off for the grid.

Sheet borders: gold `#D7B98A` when selected, brown `#8A7355` otherwise, 1.5 DIPs in screen space (`stroke width / abs(pixelScale)`). Opacity multiplies the image and the border. A missing image draws a brown placeholder and, when it is large enough on screen, the file name.

## Tests

`dotnet test Galimapia.sln`. The suites lock the behavior above. Do not weaken them to match a regression.

- `LodAndCacheTests`. Overview chooses 512. A closer view keeps 512 until the sheet grows past the hysteresis. The half step appears only above 2048. `Fit` keeps the rectangle inside the view. The cache drops the oldest image, and a lease keeps that image alive after eviction.
- `ImportFiftyTests`. Fifty 1200×900 JPEGs. A view aimed away decodes nothing. The overview uses 512 and stays under 80 MB. Zoom 1 on the first sheet may load one to three full sheets. The last sheet must not be full. The grid of 50 is 8 columns. `ImportAsync` returns after the UI placement.
- `SheetEditTests`. Grid spacing. Rotation and scale around the center, including flip. Four crop points hide the margin only after `CropCommitted`. A crossing outline is rejected. `SheetAlign.TryPlace` then `ToWorld` hits both targets: frame 100×50 at the origin, locals `(10, 10)` and `(90, 10)`, worlds `(100, 200)` and `(100, 280)`, result rotation 90, scale 1, `X = 35`, `Y = 215`. An applied crop box of `(10, 10)`–`(30, 20)` on a 100×80 page has edges 10, 10, 30, 20 and pivot `(20, 15)`. At rotation 90 with `X = Y = 0`, `ToWorld(20, 15)` is `(20, 15)` because the pivot maps to itself. That test does not rebase `X` and `Y`. The undo test crops after a rotated, scaled, flipped pose, captures `ToWorld(10, 10)` before `EndCrop`, and requires the same world point after, plus undo clearing the crop and the committed flag. Each async test builds its own `TestUi` pump. `Invoke` runs inline when it is already on that thread. The pump is private to the fixture. A new test file needs its own.
- `ProjectTests`. A sheet of 200×100 rotated 90 produces a square page that contains every extreme. Save of an 800×600 JPEG writes a relative `sources/` path, `512.jpg`, and `ready.txt`. A second write does not change `512.jpg`'s `LastWriteTimeUtc`. A new scene whose own LOD root is empty opens that project with `PyramidReady` already true, and the pose, crop, name, camera, and page match.
- `WelcomeTests`. Project names reject a slash, `CON`, and blank. The recent list keeps a missing folder and marks it unavailable. `CreateEmpty` writes `project.json`, `sources/`, and `masks/`, and `Read` returns no sheets.
- `ColorAndWarpTests`. Identity color, a rectangle mesh, a corner offset that does not change `X`, undo of brightness and of a warp, and a project file that round-trips both.

`TestUi` is a background thread plus a `BlockingCollection`. Do not block that thread waiting for `InvokeAsync` onto itself.

## Invariants

- No single in-memory bitmap of the assembled map. Do not rasterize the white page.
- Do not rewrite source pixels. Crop, color, warp, and later the mask are data on the fragment.
- `X` and `Y` stay the top-left of the unrotated full pixel box. Rebase them when the pivot changes. Do not store the crop origin in `X` and `Y`.
- `NeededPixels` uses the full sheet's long side, scale, and zoom. Not the crop box.
- The unsaved LOD fingerprint stays `path|length|utc ticks`. The project pyramid stays keyed by sheet id.
- A draft is not a cut. The check, or Enter, is the only UI path that sets `CropCommitted`.
- Tools do nothing until a sheet is selected. The check clears the selection.
- Open does not fit the camera. Key `0` sets zoom to 1 and does not move the center.
- Paths in `project.json` are relative and must not escape the project folder.
- Sheet ids are stable file names. `Name` stays the original file name.
- Save copies sources into the project. Until that copy, read the file the user dropped.
- Do not delete `%LocalAppData%\Galimapia\lod`.
- Version 1.0.8 is the authored release. The About window reads `AppInfo`. Do not invent a different author or version.
