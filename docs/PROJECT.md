# Galimapia

The working map and plan of the program. Read it before changing behavior. It says what the program is, what is built and what is next, which file owns which job, the invariants the math depends on, how a crash is recorded, and which bugs were already fixed. Several fixed bugs came back when a later change treated a draft as a cut, treated `X` and `Y` as the center of the page, or trusted a guess about a native crash instead of the dump.

## How to work on it

- One step at a time. Finish the current step, run `dotnet test Galimapia.sln`, and stop. Do not start the next step in the same change.
- The application is Galimapia, version 1.0.8, by Roman Valentyn Badiak (`Canvas/AppInfo.cs`, `Galimapia.csproj`). Do not rename it to MyMapProject. Do not invent another version or author.
- This is a desktop app. Verify with the test suite. Launch the GUI only for something the suite cannot reach, such as the crash watcher end to end, and close it afterwards.
- A native crash is diagnosed from the dump, not from a theory. See "Diagnostics and crash reports".
- Roman builds this in `C:\Projects\C_Sharp\Galimapia`. Version 1.0.8 is the published release.

## What it is

Galimapia is a Windows desktop compositor for many large old cadastral scans. A survey is dozens of book pages and map sheets, often several thousand pixels on a side. The user drops them on an infinite canvas, crops the book margins, rotates and scales each sheet, lines them up, and stretches corners where the paper has warped. Export writes that arrangement to PDF, JPEG, PNG, or a folder of map tiles with a browser viewer.

Each scan is its own object: position, rotation, scale, flip, opacity, brightness, contrast, hue, a crop polygon, and optional warp offsets. There is no bitmap of the assembled map, in memory or on disk. Drawing paints only the sheets that intersect the view, and only a reduced JPEG unless the user has zoomed into that sheet. Source pixels are never rewritten. Crop, color, warp, and a future eraser are data on the fragment.

The real scans already have pyramids under `%LocalAppData%\Galimapia\lod`. Do not delete that cache and do not change its path-hash scheme. Doing either forces every existing scan to rebuild. Roman's live project is `D:\Метрики\Горигляди і навколишні села\Horyhlady` (36 sheets, scans such as `186-1-5774_0057.jpg`, 5642×4684).

## Status

### Built in 1.0.8

Window and camera, import with a level-of-detail pyramid, sheet editing with undo, layer panel, rectangle and polygon crop, two-point join, project folder, icon, welcome screen, live color, corner and crop-point warp, autosave, export to PDF, JPEG, PNG and map tiles, the About window, and the session log.

### Also in 1.0.8

- Crop clicks hit the visible shape of a sheet (`MapScene.HitTestCrop`), so a cut front sheet no longer steals clicks in its hidden margin. Test: `A_cropped_front_sheet_does_not_hide_the_sheet_in_its_old_margin`.
- PDF alpha masks carry `/Matte [1 1 1]`, so a translucent sheet shows the sheet underneath instead of white. Test: `A_translucent_sheet_keeps_the_color_of_the_sheet_underneath`.
- Warp draws an owned `SKVertices` mesh, which stops the heap corruption that used to kill the process.
- Tile export at Fine follows the white export square, so a large survey stays sharp at the closest zoom.
- Session log, crash watcher, and black box. Export can use several CPU threads or the video card.

### Known risks from the 2026-11-05 review

1. Warp texture over the GPU budget. Avalonia's `SkiaOptions.MaxGpuResourceSizeBytes` defaults to 29,491,200 bytes (about 28 MB). A warped sheet is drawn through an image shader, which Skia cannot tile, so the whole decoded level becomes one texture. The full level of a 5642×4684 scan is about 105 MB. When the camera is close enough to load the full level, that texture likely does not stay cached and is uploaded again on every frame. Not measured yet. Not a crash cause.
2. Sliver triangles. Ear clipping on a crop with many points produces very thin triangles. `186-1-5774_0057.jpg` has 69 crop points, 67 triangles, and the flattest is about 0.17 px high. `KeepSolid` only drops triangles whose placed area is under 0.25 px². A drag that pushes a point across a neighboring edge flips a triangle and folds the picture over itself. That is a visual fault, not a crash.
3. Shutdown order. `MapCanvas.OnDetachedFromVisualTree` disposes the scene, and `ImageCache.Dispose` destroys leased images too. A frame still in flight on the render thread would draw a disposed image.
4. Logs and crash reports are never pruned. A session log is tens of kilobytes, so this is slow growth, not urgent.
5. `WarpDrawTests.Four_meshes_meeting_at_one_point_do_not_crash` and the comment on `WarpMesh.KeepSolid` come from an earlier theory that degenerate triangles crashed the process. The dumps show otherwise (see the incident below). Both are harmless. Keep them; do not read them as the reason the crash stopped.

## Plan

In order. Each step is one change with its own tests.

1. Commit the working tree. Run the suite first: 49 tests, all green on 2026-11-05.
2. Warp texture budget. Measure first. Warp-drag a point on a 5642×4684 sheet at zoom 100% and above, and watch frame time and GPU memory. If the upload repeats each frame, either raise the budget in `Program.BuildAvaloniaApp` with `.With(new SkiaOptions { MaxGpuResourceSizeBytes = ... })`, or cap the level that `DrawWarped` uses to the largest level that fits the budget. The half level of a 5642 scan is 2821×2342, about 26 MB. Done when that drag stays smooth and process memory does not climb.
3. A warp mesh that cannot fold. Refuse a warp drag that flips any triangle's orientation relative to its texture triangle (the sign of the cross product), the same way a crop vertex drag that breaks `IsSimple` is ignored. Pick ears for many-point crops by the largest smallest angle, so the 69-point outline has no sub-pixel slivers. Done when a test with the `186-1-5774_0057` outline has no triangle under 1 px high, and an inverting drag leaves the offsets unchanged.
4. Shutdown order. Stop drawing before the cache is disposed, or let `ImageCache.Dispose` retire leased images instead of destroying them. Done when a test that disposes the cache while a lease is held can still read the leased image.
5. Log retention. Keep the newest 30 session logs and 30 crash reports in `%LocalAppData%\Galimapia\logs`. Never delete a dump; Windows rotates those.
6. Eraser. Strokes rasterized to an `Alpha8` mask in `masks/{fragmentId}`. Add it only if a polygon cannot cut a leftover on a real scan.

Not planned: a page-curl mesh, NetVips or BigTIFF (add them only if an archival TIFF needs them), MonoGame.

## Stack

- .NET 9 only (`net9.0`). The machine has the .NET 9 SDK. Nullable and implicit usings are on.
- Avalonia 12.1.5, Fluent dark theme, Inter font. One app process. The window shell is XAML. The canvas is a custom `Control` plus Skia.
- SkiaSharp 3.119.4 (Skia m119) comes through `Avalonia.Skia`. On Windows it draws on the GPU (`GrDirectContext`) under WinUI composition. Do not add MonoGame.
- CommunityToolkit.Mvvm 8.4.2 is referenced. The canvas does not use a view model. `ViewModels/MainViewModel.cs` is empty. `ViewLocator.cs` is the Avalonia template and is not on the editing path.
- AvaloniaUI.DiagnosticsSupport 2.2.3 is debug-only.
- Tests are xunit 2.9.2 in `tests/Galimapia.Tests`, which references the app project and SkiaSharp directly.

Run: `dotnet run --project Galimapia.csproj`

Tests: `dotnet test Galimapia.sln`

The test project sits inside the app directory. `Galimapia.csproj` removes `tests/**` from `Compile`, `EmbeddedResource`, and `None`. Keep that remove. Without it the SDK compiles the test project into the WinExe.

## Repository

```
Galimapia.sln
Galimapia.csproj          WinExe, net9.0, ApplicationIcon = Assets\galimapia.ico, Version 1.0.8
app.manifest              Windows 10 compatibility. Leave it. Avalonia uses it for transparency.
Program.cs                Main: the crash watcher if asked, else SessionLog.Start and the Avalonia app.
App.axaml                 Fluent dark. ViewLocator data template.
App.axaml.cs              Creates MainWindow. Logs Dispatcher.UIThread.UnhandledException as a fault.
docs/PROJECT.md           This file.
Assets/galimapia.ico      Windows exe, taskbar, and shortcut. Sizes 16, 32, 48, 256. Classic BMP-in-ICO.
Assets/avalonia-logo.ico  Template leftover. The window does not use it.
Assets/icons/             Avalonia resources. Toolbar and welcome images.
ViewModels/               Empty MainViewModel and ViewModelBase. Do not move canvas state here casually.
Views/MainWindow.axaml    Editor chrome and the welcome overlay.
Views/MainWindow.axaml.cs Toolbar, sliders, layers, welcome, save keys, autosave.
Views/ExportWindow.*      Format, quality, progress, Stop.
Views/AboutWindow.*       Version and author from AppInfo.
Canvas/                   The map, the files on disk, and diagnostics. All of the behavior that matters is here.
tests/Galimapia.Tests/    xunit. Not compiled into the app.
bin/, obj/                Build output. Ignore.
```

`Galimapia.csproj` still has `<Folder Include="Models\" />` from the template. The folder does not exist and nothing needs it.

`Assets/**` is an `AvaloniaResource`. Icons are addressed as `avares://Galimapia/Assets/icons/<file>.png`. A loose file on disk is not enough for `AssetLoader` or an `Image` in XAML. Welcome status icons are loaded with `AssetLoader.Open`, not from `AppContext.BaseDirectory`.

| File | Use |
| --- | --- |
| `icons/icon.png` | Window icon. A 236×236 PNG with transparency. Avalonia accepts that directly. |
| `galimapia.ico` | `ApplicationIcon`. Windows `rc.exe` wants a classic icon, so the PNG was resized to 16, 32, 48, and 256. |
| `icons/rotate.png`, `rotate-cw.png` | Step −90° and +90°. |
| `icons/mirror.png`, `reset.png` | Horizontal flip, and reset of rotation, scale, and flip. |
| `icons/crop.png` | Polygon crop. The rectangle tool is a drawn border, not this image. |
| `icons/delete.png`, `layers.png`, `up.png`, `down.png` | Delete, layer-panel icon, bring forward, send backward. |
| `icons/welcome-map.png`, `welcome-new.png`, `welcome-folder.png` | Welcome title, New project, Open and the Projects button. |
| `icons/welcome-ok.png`, `welcome-missing.png` | Recent project is available, or the folder is gone. |

The emoji originals live in `C:\Projects\128` as `emoji_uXXXX.png`. Copy a chosen file into `Assets/icons` under a short name. Do not reference `C:\Projects\128` from the running app.

A desktop shortcut may exist at `%USERPROFILE%\Desktop\Galimapia.lnk`, outside the repo. Its target is `cmd.exe`, its working directory is this repo, and its icon is `Assets\galimapia.ico`. Do not rewrite that shortcut unless asked. Changing only `IconLocation` is safe. Replacing the whole shortcut wipes the build-and-run arguments.

## Threads, and the life of a frame

- UI thread. Input, every `MapScene` mutation, and `MapCanvas.Render`. `Render` calls `MapScene.Capture`, which copies each visible sheet into a `SheetVisual` and leases its decoded image, then hands the list to a new `MapDrawOperation`.
- Render thread. Avalonia's compositor calls `MapDrawOperation.Render` there. Skia's GPU backend only records the draw calls. The GPU work, and the destruction of the recorded ops, happen later in `GrDirectContext.flushAndSubmit`, called from `Avalonia.Skia.SurfaceRenderTarget.Blit`. A recorded op holds its own references, but only to objects that were still alive when Skia took the reference.
- Workers. Header reads, pyramid builds (2 at a time), decodes (4 at a time), layer thumbnails, save, and export. They post back through `MapScene.RunOnUi` or `AvaloniaUi.RunAsync`.
- Finalizer thread. A SkiaSharp wrapper that nobody disposed releases its native object here, at a moment nobody chooses. See "Native interop rules".
- The crash watcher. A second, hidden `Galimapia.exe` process. See "Diagnostics and crash reports".

A lease keeps an image alive for one frame. `MapDrawOperation.Dispose` disposes every `SheetVisual`, which releases the lease. Eviction of a leased image moves it to `_retired` and destroys it only on release.

## Canvas files

`Camera.cs`. Looks at an infinite plane. `Zoom` is device-independent pixels per world unit. `1` is 100%: one source pixel per DIP when the sheet scale is also 1. Range is `0.002` … `64`.

- `ScreenToWorld`: `center + (screen - viewSize / 2) / zoom`
- `WorldToScreen`: `(world - center) * zoom + viewSize / 2`
- `PanByScreenDelta` subtracts the screen delta divided by zoom.
- `ZoomAt` keeps the world point under the cursor fixed. The wheel uses factor `1.1 ^ ticks`, and ticks are clamped to ±8.
- `Fit` centers a world rectangle and picks the zoom that fits it with a margin. A tall scan in a short window lands well below 100%. That is why key `0` must not call `Fit`.

World coordinates are `double`. Convert to `float` only at the Skia call.

`MapCanvas.cs`. The control. Middle button pans. Wheel zooms toward the cursor. Left button selects and edits, or drives the active tool. Drop image files, or a folder of them, onto the canvas. `MapScene.CreateForApp()` wires the shared LOD store, a 1 GB cache, and `AvaloniaUi.RunAsync`. User gestures are written to the session log here.

Public surface the window uses: `ZoomText`, `CenterText`, `CursorText`, `SheetsText`, `ToolHint`, `Selected`, `Sheets`, `SheetCount`, `StructureVersion`, `EditVersion`, `Tool`, `Cropping`, `CanConfirmCrop`, `ProjectFolder`, `Notice`, `SaveProjectAsync`, `OpenProjectAsync`, `ExportAsync`, `HandleKey`, `Select`, `SetTool`, `ConfirmCrop`, `StepRotation`, `CommitRotation`, `ToggleMirror`, `ResetAppearance`, `DeleteSelected`, `BringForward`, `SendBackward`, `BeginOpacityEdit`, `PreviewOpacity`, `PreviewBrightness`, `PreviewContrast`, `PreviewHue`, `EndOpacityEdit`, `MoveSheet`, `IndexOf`.

`SetTool` does nothing useful when no sheet is selected: it clears any tool and returns. `ConfirmCrop` applies the draft and then sets `_selected` to null, so the toolbar locks again. `OnSceneChanged` also clears the tool if the selected fragment has disappeared, with an `_inSceneChange` guard so `CancelDraft` cannot re-enter the handler.

`MapScene.cs`. The sheet list, import, LOD queue, undo, crop and warp sessions, and project install. Mutate it on the UI thread only. Background work posts back through the invoker given to the constructor. `Version` bumps on almost every visual change. `EditVersion` bumps on user edits only, and autosave compares it. `StructureVersion` bumps when sheets are added, removed, or reordered. The layer panel rebuilds cards only when `StructureVersion` changes, so a drag of the sheet does not rebuild the list. Pose changes must not bump `StructureVersion`. Hit tests: `HitTest` uses the visible shape, `HitTestFrame` the full page, `HitTestCrop` the visible shape except for the one sheet whose hidden page is on screen because a new outline is being drawn.

`MapFragment.cs`. One scan. `Id` is a guid `"n"` (32 hex chars, no dashes) unless a project file supplies one. Ids are stable; a future mask file will use the same id. `Name` is the original file name and stays that way after save, even though the copy in `sources/` is named by id. `Retarget` changes `SourcePath` only. Warp helpers: `ControlCount`, `ControlPoint`, `WarpOffset`, `WarpedLocal`, `EnsureWarp`, `SetWarp`, `IsWarped`.

`SheetFrame.cs`. The pose math. See "Coordinates". `NaN` pivot and box fields mean "full page".

`SheetVisual.cs`. A snapshot of one sheet for a single frame: leased image, pose, crop, draft, pivot, content box, color, and warp offsets. `MapScene.Capture` copies the lists into arrays so the draw operation never reads the live fragment. `Dispose` releases the lease, not the source file.

`MapDrawOperation.cs`. `ICustomDrawOperation`. One frame, drawn on the render thread. `Equals` and `GetHashCode` must include every input that affects pixels, including the export page, the rubber-band cursor, the align marks, and the glue ring. `HashCode.Combine` accepts at most eight arguments, so the extra flags are a nested combine. `Render` writes one `trace frame` line to the black box. `DrawWarped` writes one `trace mesh` line per warped sheet.

`SheetGrid.cs`. Column count is `ceil(sqrt(count))`, or 1 for a single sheet. Cells step by the max width and max height of that drop, plus `MapScene.Gap` (48). Row by row. The first sheet's top-left is the world point under the drop.

`SheetAlign.cs`. Two-point join. One translation, one rotation, one scale. Uses `PivotLocalX` and `PivotLocalY`, so an uncropped sheet still pivots on the page center.

`CropPolygon.cs`. Point-in-polygon, rectangle corners, self-intersection, and the close distances. `CloseLoupeDips` is 88. `CloseSnapDips` is 22. `SegmentsCross` is a proper crossing, so a shared endpoint is not a crossing. `CanAppend` refuses a new segment that crosses an existing one. `CanClose` checks the segment from the last point back to the first. `IsSimple` is the test used before the check button is allowed. `Same` compares point lists.

`SheetEdits.cs`. Undo records: `PoseEdit` (move, scale, rotate, flip, opacity, color), `DeleteEdit`, `OrderEdit`, `CropEdit`, `WarpEdit`. Crop undo stores the polygon, the rectangle flag, the committed flag, `X` and `Y` before and after, and the warp offsets before and after, because confirming a crop rebases the position and clears the warp.

`WarpMesh.cs`. Ear-clipping triangulation of the control polygon, `KeepSolid` (drops triangles whose placed area is under 0.25 px², or that index out of range or are not finite), `Collides` (a glued point may not land within 1.5 px of another point of the same sheet). `GlueDips` is 10, `HandleDips` is 18.

`ColorAdjust.cs`. One 4×5 color matrix from brightness, contrast, and hue. Identity (0, 1, 0) returns no filter.

`LodPlan.cs` and `LodStore.cs`. Which reduced JPEGs exist, and where they are written. Two layouts, described below. `PreparedImage` is the result of a prepare: pixel size plus the level list. The full level points at the original file. It is not a second JPEG.

`ImageFiles.cs`. Extensions `.jpg`, `.jpeg`, `.png`, `.webp`, `.bmp`, `.gif`, `.tif`, `.tiff`. `TryReadSize` uses `SKCodec` and does not decode pixels. `Decode` and `DecodeLongSide` copy into an `SKImage` with `FromPixelCopy`. `WriteScaledJpeg` prefers `GetScaledDimensions`, and if the codec returns `InvalidScale` it decodes full and resizes. JPEG quality is 80. The encode writes `destination.tmp` and then moves it. Open files with `FileShare.ReadWrite` so a scan that is still being copied can be read. Dispose the resized bitmap in `finally`.

`ImageCache.cs`. LRU of `SKImage`, default budget 1 GB. Key is `fragmentId + ":" + longSide`. `TryLease` increments a use count. Eviction of a leased image moves the node to `_retired` and disposes it only when the lease is released. `ImageLease` is safe to dispose twice.

`AvaloniaUi.cs`. `RunAsync` runs the action inline when it is already on the UI thread, otherwise `Dispatcher.UIThread.InvokeAsync`. After `ConfigureAwait(false)`, do not assume the Avalonia sync context.

`ExportPage.cs`. The white square. Not a bitmap.

`PageExport.cs`. `ExportSheet`, `ExportKind`, `ExportSettings`, `PageExport`, and the hand-written `PdfDocument`. See "Export".

`ProjectFile.cs`. Read and write `project.json`, copy sources, publish pyramids.

`RecentProjects.cs`. The welcome list. Default file is `%LocalAppData%\Galimapia\recent.json`.

`SessionLog.cs`, `BlackBox.cs`, `CrashWatch.cs`. Diagnostics. See "Diagnostics and crash reports".

`AppInfo.cs`. Name, version, author. The About window and the PDF metadata read it.

## Data on disk

Five places. Do not mix them.

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

`project.json` is version 1, camelCase, indented, nulls omitted. It is written to `project.json.tmp` and then moved. Shape:

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

`source` is relative to the project folder, with forward slashes. `Relative` refuses a path that is rooted or starts with `../`. `Resolve` refuses a path that escapes the folder after `GetFullPath`. Sheet ids must be valid file names. A scale of 0 or less reads as 1. Missing color values read as brightness 0, contrast 1, hue 0.

`ready.txt` is the text `v1|{width}x{height}|{comma-separated long sides}`. `TryLoadFragment` checks the stamp and that each reduced JPEG exists. It does not decode pixels. `PublishFragment` returns immediately when that stamp already matches, so a second save does not rewrite `512.jpg`. Otherwise it copies missing reduced JPEGs from the old path-hash directory (`FindPyramid`) and encodes only the levels that are still missing.

Recent projects:

```
%LocalAppData%\Galimapia\recent.json
{ "projects": [ { "name", "folder", "openedUtc" } ] }
```

Newest first, at most 12. Remembering the same folder updates it in place and moves it to the top. A missing folder stays in the list. `IsAvailable` is `Directory.Exists(folder)` and `File.Exists(project.json)`. It is computed, not stored. A corrupt file loads as an empty list.

Diagnostics:

```
%LocalAppData%\Galimapia\logs\galimapia-{yyyyMMdd-HHmmss}.log       one per launch
%LocalAppData%\Galimapia\logs\crash-{yyyyMMdd-HHmmss}-{pid}.txt     one per crash
%LocalAppData%\CrashDumps\Galimapia.exe.{pid}.dmp                     written by Windows, not by Galimapia
```

The dump folder is whatever Windows Error Reporting's `LocalDumps` key says. On Roman's machine the key `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps` exists with no values of its own, so Windows keeps a minidump of every crashing app in `%LocalAppData%\CrashDumps`, the newest 10 per app.

## Welcome, then the editor

`MainWindow` starts with the `Welcome` overlay visible and covering all three rows. The overlay is hit-tested, so the canvas underneath does not receive clicks. `OnPreviewKeyDown` does not pass Delete and the other map keys through while the overlay is visible. Ctrl+S is the exception: it saves the open project and leaves the overlay up. The text box still receives keys because the handler does not mark them handled.

New project asks for a name and a parent folder, then creates `{parent}/{name}/`. `RecentProjects.TryProjectName` trims, rejects empty names, names longer than 80 characters, any character in `Path.GetInvalidFileNameChars`, `.` and `..`, and Windows device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`) compared on the file name without extension. If that folder already contains `project.json`, it is opened. If it exists, is not a project, and is not empty, creation stops with an error. An empty directory is used. `ProjectFile.CreateEmpty` writes version 1 with no sheets, camera at the origin, zoom 1, and no export page.

Open requires `project.json`. A plain folder of scans is not a project. Drop scans after the project is open.

Recent rows show the name, the full path, and Available or Unavailable. An unavailable row does not open. The folder button in the edit toolbar (`ProjectsButton`) shows the welcome screen again. Back is visible only when a project is already open, and it hides the overlay without changing the canvas.

Once a project is open, Save and Ctrl+S write `Map.ProjectFolder`. They do not ask for a folder. The window title becomes `Galimapia — {folder name}`. Every five minutes, and again when the window closes, a project with user edits is written by itself, and the status line says `Autosaved`. Loading a pyramid or decoding a level does not count as an edit. Opening or creating a project marks the current `EditVersion` as clean.

Opening a project replaces the canvas. There is no "save changes?" prompt. `Install` clears the gesture, the crop and warp sessions, both undo stacks, and every draft. Save does not clear undo.

`OpenProjectAsync` reads on a worker thread, then `Install` on the UI thread. It restores `CenterX`, `CenterY`, and `Zoom` from the file. Zoom is clamped to the camera range. It does not call `Fit`. A zoom of 0 or less in the file becomes 1.

## Save and the white page

`SaveProjectAsync` snapshots sheets on the UI thread, computes `ExportPage.TryFrom`, then `ProjectFile.Write` on a worker. `ApplySave` points each fragment at `sources/{id}…`, swaps in the project LOD store, and marks the pyramids ready. The status line says `Saving`, then `Saved`, or the exception message.

`CopySource` skips the copy when the destination is the same path, or when the destination exists with the same length and the source is not newer. The original file the user dropped is not modified. Display `Name` is not replaced with the id file name.

The white page is a world-space square drawn behind the sheets. It is not a raster and must not become one.

`ExportPage.TryFrom` unions `GetVisibleBounds` of every sheet. That box includes rotation, the crop polygon, and warp offsets, not the hidden page margin. The side is `max(width, height)`. The square is centered on the union, so the short axis has equal margin and the long axis touches the extremes. No extra padding. Side below 1, or no sheets, means no page. The page on the canvas is recomputed only on save; dragging a sheet does not move it. It is stored in `project.json` and restored on open.

Draw order in `MapDrawOperation.Draw`: dark background `#242220`, grid, white export square, sheets from back to front, handles or the crop draft or the warp points, align guides, the close-up loupe, the "Drop scans here" message when empty, and the drop hover frame. The white fill has a 1.5 DIP stroke `#C4BEB4`, in screen space via `ScreenX` and `ScreenY`. Hit testing ignores the square.

## Import and level of detail

Drop uses `IDataTransfer.TryGetFiles()` and `IStorageItem.TryGetLocalPath()`. A directory is enumerated one level, not recursively. Paths are sorted ordinal-ignore-case. Unknown extensions are skipped and counted in the status line.

`ImportAsync` reads headers off the UI thread (`ConfigureAwait(false)`), then places fragments on the UI thread through `RunOnUi`. It must not `Invoke` back onto the UI thread while a test pump is blocked inside `RunOnUi`. The first drop, and any drop of two or more files, frames the camera on the sheets just added. One extra sheet stays at the cursor and does not change the zoom.

Reduced long sides are 512, 1024, and 2048 when each is smaller than the source, plus half the source when that half is greater than 2048 and still smaller than the source. A 4032×3024 scan gets 512, 1024, and 2048. A 5642×4684 scan also gets 2821. The original is the last level and is not copied into the pyramid. Pyramid builds are limited to 2 at a time, decodes to 4. Release a semaphore in `finally`, only after it was acquired, and catch `ObjectDisposedException` on release.

`BuildPyramidAsync` tries `_projectStore.TryLoadFragment` first. On a hit it does not encode. Otherwise it captures `var store = _store` and calls `Prepare` on that local, so a store swap during the await cannot race the lambda. `ApplyPyramid` ignores the callback when the fragment is no longer in `_fragments`, so a pyramid from a replaced project cannot attach.

Only sheets whose visible bounds intersect the view, expanded by 80 DIPs, are decoded. `NeededPixels` is `ceil(max(PixelWidth, PixelHeight) * abs(Scale) * Zoom)`, the on-screen long side of the whole sheet, not the crop box. Do not switch it to the crop box. The level is the smallest whose long side covers that need. Hysteresis keeps the held level while the need is at most that level and at least 70% of it. While a sharper level is loading, `BestAvailable` draws the closest cached level. A failed decode stays in `_failed` until that sheet changes level, or until the project is opened or saved. The cache key includes the sheet id, so two sheets do not share a decoded image.

`PreviewPath` is the smallest non-full level, used for the layer thumbnail. The original is skipped when its long side is above 512. Thumbnails are decoded to width 144 off the UI thread.

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

`ConfirmDraft` reads the old pivot from the frame before it replaces the crop. `EndCrop` (the older path the tests still call) computes the old pivot from the snapshot via `PivotOf`, not from the live frame, because `AddCropPoint` has already mutated the polygon.

Draw matches the pivot. Translate to `frame.Center`, rotate by the sheet rotation, scale by `zoom * scale` with the flip signs, and draw the bitmap into `(-pivotX, -pivotY, width - pivotX, height - pivotY)`. The clip path is the crop points minus the pivot.

After a cut, scale corners and the rotate stick use the content box (`EdgeLeft` and the other edges), not the full-page corners. The rotate stick sits 22 DIPs beyond the top edge of that box. Handles are drawn when the content's long side is at least 48 DIPs on screen. Pick radius is 10 DIPs.

`GetVisibleBounds` uses the warped control points when there is a warp, the crop polygon's world points when there is a cut, and the four page corners otherwise. Culling, the export square, and `Reveal` all use it. `ContainsWorld` uses the same shape, so a click in the hidden margin does not select the sheet. `ContainsFrame` and `HitTestFrame` use the full pixel rectangle.

Scale is clamped to `0.05` … `8`. Opacity is `0` … `1`. A fully transparent sheet can still be selected.

## Editing and the toolbar

The window is three rows: canvas plus layer panel, the edit toolbar, the status bar.

The status fields are padded Consolas: zoom, center, cursor, then the sheet summary. The hint `Middle mouse pan, wheel zoom, 0 = 100%` is its own right-aligned text block. Do not put that hint back into the coordinate string. Notices (`Saving`, `Saved`, `Opened`, `Exporting`, errors) are appended to the sheet summary after the cache size.

The edit row is a horizontal `ScrollViewer`. Delete stays in its own column on the right, outside the scroller, so variable text cannot move it.

Tools stay disabled until a sheet is selected: rotate, mirror, reset, both crop modes, join, warp, the sliders, delete, and the layer up and down buttons. The rule is: click the sheet, then use a tool. The green check applies the crop and clears the selection, so the tools lock until the next click. Clicking a different sheet while a tool is armed selects that sheet and does not also edit it.

Buttons, grouped:

- Projects (folder icon) and Save. Export and About.
- Step −90° and +90°, then absolute `0` `90` `180` `270`, then horizontal mirror.
- Reset. Rotation, scale, and both flips return to the loaded look. Position, opacity, and crop stay.
- Crop: a small rectangle icon, scissors, and a green check. The check is visible only when `CanConfirmCrop` is true: the tool is rectangle or polygon, the draft has at least four points, `IsSimple` is true, and the box is at least 1×1. Enter confirms. The check is the only way the UI cuts the page.
- Join, in its own group.
- Warp, in its own group. It is not the move tool. While it is on, dragging the sheet body does nothing. Only the blue points stretch.
- Opacity slider `0` … `100`. Bright −100…100, contrast 0…200 with 100 as the original, tint −180…180 degrees, each 28 DIPs tall. One undo entry per slider gesture, from pointer press to pointer release, including keyboard arrows. Do not steal Left and Right from a slider.
- Delete.

`[` and `]` send backward and bring forward. The same commands are the up and down buttons in the layer-panel header.

The layer panel is 280 DIPs open and 48 collapsed, on the right. Cards are front at the top. Index 0 in `_fragments` is the back and is painted first. Drag a card to reorder; the cards around the drop slot get a 16 DIP margin instead of 3, so the gap shows where the card will land.

Click selects. Drag the body to move. Drag a corner to scale around the pivot. Drag the rotate stick to rotate around the pivot. Middle button still pans during those gestures. Escape during a move, scale, or rotate cancels that gesture. Escape with nothing in progress clears the selection. A second Escape is needed when a tool is on, because the first one leaves the tool.

Delete and Backspace remove the selection, except while a crop tool is on: Backspace removes the last draft point. Undo puts a deleted sheet back at the same index.

One undo entry per gesture. `BeginEdit` captures the pose. `EndEdit` pushes a `PoseEdit` only when the pose changed. Delete, reorder, reset, join, an applied crop, and a warp drag are their own entries. The undo stack keeps 100 entries. Ctrl+Z undoes. Ctrl+Y and Ctrl+Shift+Z redo. Ctrl+Z during a drag cancels that drag and does not also pop the previous edit. The order inside `Undo` is: cancel a warp drag, else cancel the pose gesture, else cancel a draft, else cancel an old crop session, else pop the stack. Redo uses the same cancel order.

Key `0` and numpad `0`, without Control or Alt, set `Zoom = 1` and leave the center where it is.

## Crop

The polygon is in the sheet's pixel space. The source file stays whole. Two lists: `Crop` is the applied cut, `Draft` is the editing buffer. `HasCut` is `CropCommitted && Crop.Count >= 4`. Clip, pivot, handles, hit testing for selection, and the export bounds use `Crop` only when `HasCut` is true.

While a new outline is drawn, the whole page is visible. `hideCut` is true only when the sheet is selected, a crop tool is on, the draft is non-empty, and the draft is not an exact copy of the committed crop. Opening the tool on an already cut sheet keeps the cut. Copying the committed polygon into the draft so its vertices can be dragged does not reveal the margin by itself. Starting a new outline, or moving a vertex, does.

Crop clicks use `MapScene.HitTestCrop`. Every sheet is hit by its visible shape, except the selected sheet while its hidden page is on screen, which is hit by its full page. A click in the cut-away margin of a front sheet therefore reaches the sheet behind it.

The draft is bright red (`255, 36, 48`). The first point is a larger ring with a light core. While the polygon is still open, the closing edge from the last point back to the first is not drawn; a rubber line follows the cursor instead. After the user closes the outline, and before the check, the closing edge is drawn.

Move the cursor toward the first point and, inside 88 DIPs, a circular loupe appears above the cursor (below it if there is no room). Magnification runs from about 2.2 to about 4.8 as the cursor gets closer. Inside 22 DIPs, with at least four points and `CanClose`, the rubber line snaps to the start point, turns green (`#8FDB6A`), and a check mark is drawn. The hit radius of that first point grows from 16 to 22 DIPs so the click lands. Other vertices stay at 11 DIPs. Clicking the start point closes the outline and does not add a duplicate point. A double-click does not close the outline.

Rectangle mode replaces the draft with `CropPolygon.Rectangle` (top-left, top-right, bottom-right, bottom-left) while the pointer is down. Releasing the pointer does not apply the cut. A later drag of a rectangle vertex rebuilds the rectangle from the opposite corner (`index + 2` mod 4).

Right-click cancels the draft and stays in the tool. Escape during a drag cancels the draft and stays in the tool. Escape otherwise cancels the draft and leaves the tool. Switching tools, selecting another sheet, or `StopCrop` cancels the draft and does not apply it. A click that misses every sheet does not change the draft.

Self-intersection is rejected. A new point that fails `CanAppend` is ignored. A vertex drag that would make the polygon non-simple is ignored.

After the check: red points disappear, the outside of the polygon is clipped, handles and the rotate pivot move to the crop's extreme pixels, and `X` and `Y` are rebased so the picture does not jump. The cut edge is the normal gold or brown sheet border. One `CropEdit` restores the polygon, the position, and the warp.

`EndCrop` is still used by tests that call `AddCropPoint` and `PreviewCrop`. A gesture of one or two points restores the snapshot. Three or more points can be kept, but the page clips only at four or more and only when committed.

## Two-point join

Join does not bend the page. The sheet that stays is not moved.

1. Click a spot on the sheet that should stay. That click is stored as a world point.
2. Click a second world point on that same sheet, at least 8 DIPs from the first on screen.
3. Click the same first spot on the sheet that should move. That click is stored as a pixel position.
4. Click the same second pixel position, at least 4 pixels from the third. `SheetAlign.TryPlace` sets `X`, `Y`, rotation, and one scale.

Flip is kept. If the two local points or the two world points are closer than 1 unit, the placement is refused. If the required scale is outside `0.05` … `8`, it is clamped; the first pair still meets and the second pair is only approximate. One undo entry. Escape or right-click leaves the tool. Marks are numbered in screen space. The rubber line is hidden between mark 2 and mark 3 because those two marks are on different sheets.

## Color

Brightness, contrast, and hue are numbers on the fragment. `ColorAdjust` builds one color matrix and `MapDrawOperation` sets it on the image paint, or on the mesh paint for a warped sheet. Nothing is written back to the JPEG. Contrast is stored in `project.json` as a nullable number so an omitted field does not become 0.

## Warp

`MapTool.Warp` is a separate tool. Move, scale, and rotate do not run while Warp is selected. A press that is not on a blue point does not move the sheet. A press on another sheet only selects it.

Control points are the four corners of the full page. After a confirmed crop they are the crop vertices, in the same order. Each control has an offset in pixel space, stored in `MapFragment.Warp`. The offset is added before `ToWorld`, so rotation, scale, and moving `X` and `Y` carry the stretch along. Dragging a point changes only that offset. `X` and `Y` stay put.

The mesh is the ear-clipped triangulation of the unmoved control polygon (`WarpMesh.Triangulate`). Vertex positions are the moved points. Texture coordinates are the unmoved points, scaled into the decoded level, so a 512 JPEG still covers the full page. `KeepSolid` drops triangles that collapsed on the moved points. If triangulation fails, the sheet draws the ordinary way. While every offset is near zero, or the offset list length does not match the control count, the ordinary `DrawImage` path is used. Confirming a crop that changes the polygon clears the offsets, and undo of that crop puts them back.

`DrawWarped` must own the `SKVertices` it draws: `using var mesh = SKVertices.CreateCopy(...)`, then `canvas.DrawVertices(mesh, SKBlendMode.Modulate, paint)`. Never call the `DrawVertices` overloads that take point arrays. See the incident below. The test `The_app_draws_vertices_only_from_an_SKVertices_it_owns` scans the app's IL for those overloads.

The blue point is hit in screen space within 18 DIPs, before any sheet body test, because a corner lies on the polygon edge and a body test misses it. Another sheet covering that pixel does not steal the drag from the selected sheet. The point follows the cursor from where it was grabbed. It snaps to another sheet's control point within 10 DIPs and a green ring shows the glue, unless that would land within 1.5 px of a different point of the same sheet. The snap only moves that one offset. It does not call `SheetAlign` and it does not move the rest of the sheet. One `WarpEdit` per drag. Escape during the drag restores the offsets and stays in the tool.

## Export

Export measures the sheets on the canvas at that moment (`ExportPage.TryFrom`) and writes the white square. It does not reuse the square from the last save. It refuses to start while a save is running. `PageExport` draws back to front, with crop, warp, flip, opacity, and the color filter. Each scan is decoded from its source file only as large as it appears at the chosen quality, and disposed after its sheet. There is still no bitmap of the whole map at full scan resolution.

The export window offers a format and a quality. Screen is 2048 px at JPEG 75, Print is 4096 at 85, and Fine is the white export square itself at JPEG 92. A single JPEG, PNG, or PDF is still at most 8192 px on a side (a PDF page is also at most 14400 points). Map tiles are not: Fine builds the pyramid out until one scan pixel is one tile pixel, so a large survey is as sharp at the closest zoom as a small one. The tile side stops at 65536.

- PDF. One page, at most 14400 points on a side. Each sheet is rendered on its own transparent surface, laid on white, encoded as JPEG, and placed with its own Flate-compressed alpha mask. The mask carries `/Matte [1 1 1]`, so a viewer takes the white back off where the sheet is translucent or soft-edged. The PDF is written by `PdfDocument`, not by Skia's document encoder: that encoder stored RGB samples in a JPEG and then marked them as already converted, so every viewer turned the paper green.
- JPEG and PNG. One flattened picture of the square on white.
- Map tiles. A folder `{project} tiles` with `tiles/{level}/{x}/{y}.jpg` and `index.html`. Tiles are 512 px. Level 0 is one tile; the finest level is the first whose width covers the output side. A browser opens the html file and loads only the tiles on screen. Drag pans, the wheel changes level.

A warped sheet is exported as one triangle mesh (`SKVertices`) on a transparent surface the size of the warped box, then placed with `DrawImage`. The box outside the triangles stays transparent. Antialiasing is off on that mesh: smoothed edges were blending with the white page and leaving white rays. The same owned `SKVertices` path is what the screen uses, so a later change must not go back to the array overload of `DrawVertices`.

The export window has two checkboxes. Several CPU threads paint map tiles, or PDF sheets, at the same time. The video card opens a hidden OpenGL context and draws the surfaces there; it is used only when threads are off, because one context cannot be shared across those threads. If the driver cannot make a context, the export stays on the CPU and the session log says so.

The export window shows a percent, the sheet or tile being written, and Stop. Stop deletes the unfinished file or folder.

## Diagnostics and crash reports

Three parts, because no single one survives every kind of failure.

1. The session log, `SessionLog`. One file per launch in `%LocalAppData%\Galimapia\logs`. A line is `HH:mm:ss.fff kind message`. Kinds: `session` and `machine` at start, `watch` for the watcher, `action` for user gestures, `move` (at most four per second while dragging), `once`, `fault` (with the exception stack and a machine snapshot), and, written by the watcher after the app is gone, `end` or `crash`. The file is opened without a managed buffer, so every line reaches the operating system immediately and survives the process dying. It is not flushed to the disk platter, so a Windows crash can lose the last lines. Interpolated messages go through the `LogText` handler, which formats with the invariant culture; `27514.2,30465` used to read `27514,2,30465` under a Ukrainian or Polish culture.
2. The black box, `BlackBox`. Named shared memory `Local\Galimapia-blackbox-{pid}-{startTicks}`, 1024 slots of 256 bytes, holding the newest lines. Every session log line goes here too, plus `trace` lines that are too frequent for the file: one `trace frame` per render and one `trace mesh` per warped sheet drawn. A slot is written sequence-last, so a slot cut off by a crash reads as empty. A clean exit sets a flag in the header.
3. The crash watcher, `CrashWatch`. `SessionLog.Start` creates the black box and starts `Galimapia.exe --crash-watch {pid} {startTicks} {logPath} {boxName}` with no window. `Program.Main` runs `CrashWatch.TryRun` before anything else, so the watcher never loads Avalonia. The watcher opens the app's process handle and the black box, checks the start time against pid reuse, and waits. When the app ends:
   - exit code 0: nothing.
   - a code that is not a crash (1 from Task Manager, `0xFFFFFFFF` from a debugger stop, `0xC000013A` from Ctrl+C), without the clean flag: one `end` line in the session log.
   - a crash code (a status with both top bits set, `0xC0000000` and up, except `0xFFFFFFFF` and `0xC000013A`; or `0x80131506`, `0x80131623`): it waits up to 20 s for the Windows dump, writes `crash-{yyyyMMdd-HHmmss}-{pid}.txt`, and appends a `crash` line with the report and dump paths to the session log. A crash code is reported even when the clean flag is set, because a crash during shutdown is still a crash.

A crash report holds the version, the process times, the exit code and what it means, the session log path, the dump path with a ready `cdb` command line, and the black box lines oldest first. Without a black box it falls back to the last 80 lines of the session log. Without a `LocalDumps` key it says how to create one.

Inside the app the remaining handlers are `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, and `Dispatcher.UIThread.UnhandledException`. They write `fault` lines with the managed stack. There is no `SetUnhandledExceptionFilter` any more: heap corruption and other native fail-fast errors never reached it, and running managed code inside a process whose heap is broken can hang it instead of letting Windows write the dump.

Reading a dump. `cdb.exe` ships inside WinDbg from the Microsoft Store, for example `C:\Program Files\WindowsApps\Microsoft.WinDbg_1.2610.1001.0_x64__8wekyb3d8bbwe\amd64\cdb.exe`:

```
cdb -z "%LOCALAPPDATA%\CrashDumps\Galimapia.exe.{pid}.dmp" -y "srv*C:\symbols*https://msdl.microsoft.com/download/symbols" -c "!analyze -v; .ecxr; kn 60; q"
```

The first run downloads Windows symbols into `C:\symbols` and takes about a minute. The SkiaSharp package ships `libSkiaSharp.pdb` next to the DLL (`bin\Debug\net9.0\runtimes\win-x64\native`), so on this machine cdb names Skia functions and inlined frames. Look at `FAILURE_BUCKET_ID` and at the stack after `.ecxr`.

`CrashReportTests` cover the black box and the watcher without the GUI: `cmd.exe /c exit -1073740940` ends with exactly `0xC0000374`.

## Native interop rules

- Every SkiaSharp object passed into a native call must be owned by the caller until the call returns. Use `using` or keep it in a field. A wrapper that is only a temporary can be finalized by a GC in the middle of the native call; its finalizer releases the native object on the finalizer thread.
- Do not use SkiaSharp convenience overloads that create a native object internally and do not dispose it. `SKCanvas.DrawVertices` with point arrays is one; it creates an `SKVertices` and never disposes or keeps it alive. `SKCanvas.DrawText(string, …)` is safe; it disposes its own text blob after the call.
- The GPU backend records now and executes at flush. Raster tests cannot reproduce a lifetime bug that only shows on the GPU, because raster draws immediately and keeps nothing.
- The render thread reads only `SheetVisual` snapshots and leased images. Never pass a live `MapFragment`, `List<T>`, or an unleased `SKImage` into a draw operation.
- `ImageCache` disposes images on whichever thread releases the last lease. That is safe for raster `SKImage`; do not put GPU-backed images in it.

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
- `SkiaOptions.MaxGpuResourceSizeBytes` defaults to about 28 MB. See the plan.

Grid lines: step is `NiceStep(96 / zoom)`, so the spacing stays near 96 DIPs. Every fifth line is heavier. The line through world 0 is gold `#D7B98A`. The loop bails out after 400 lines so a tiny zoom cannot hang the frame. Antialiasing is off for the grid.

Sheet borders: gold `#D7B98A` when selected, brown `#8A7355` otherwise, 1.5 DIPs in screen space (`stroke width / abs(pixelScale)`). Opacity multiplies the image and the border. A missing image draws a brown placeholder and, when it is large enough on screen, the file name.

## Tests

`dotnet test Galimapia.sln`, 49 tests. The suites lock the behavior above. Do not weaken them to match a regression. Each async test builds its own `TestUi` pump: a background thread plus a `BlockingCollection`, private to the fixture. `Invoke` runs inline when it is already on that thread. Do not block that thread waiting for `InvokeAsync` onto itself. A new test file needs its own pump.

- `LodAndCacheTests`. Overview chooses 512, and a closer view keeps 512 until the sheet grows past the hysteresis. The half step appears only above 2048. `Fit` keeps the rectangle inside the view. The cache drops the oldest image, and a lease keeps that image alive after eviction.
- `ImportFiftyTests`. Fifty 1200×900 JPEGs. A view aimed away decodes nothing. The overview uses 512 and stays under 80 MB. Zoom 1 on the first sheet loads one to three full sheets, and the last sheet is not full. The grid of 50 is 8 columns. `ImportAsync` returns after the UI placement.
- `SheetEditTests`. Grid spacing. Rotation and scale around the center, including flip. Four crop points hide the margin only after `CropCommitted`. A crossing outline is rejected. `SheetAlign.TryPlace`: frame 100×50 at the origin, locals `(10, 10)` and `(90, 10)`, worlds `(100, 200)` and `(100, 280)`, result rotation 90, scale 1, `X = 35`, `Y = 215`. A crop box of `(10, 10)`–`(30, 20)` on a 100×80 page has edges 10, 10, 30, 20 and pivot `(20, 15)`. Edits hit the front sheet and undo. Reset, crop, and reorder are single undo steps; the crop is made after a rotated, scaled, flipped pose is undone back in, keeps `ToWorld(10, 10)` in place, and undo clears both the polygon and the committed flag. A cut front sheet does not hide the back sheet in its old margin.
- `ProjectTests`. A 200×100 sheet rotated 90 produces a square page that contains every extreme. Save of an 800×600 JPEG writes a relative `sources/` path, `512.jpg`, and `ready.txt`; a second write does not change `512.jpg`'s `LastWriteTimeUtc`; a new scene opens that project with `PyramidReady` already true, and the pose, crop, name, camera, and page match.
- `WelcomeTests`. Project names reject a slash, `CON`, and blank. The recent list keeps a missing folder and marks it unavailable. `CreateEmpty` writes `project.json`, `sources/`, and `masks/`, and `Read` returns no sheets.
- `ColorAndWarpTests`. Identity color. A collapsed corner is not sent to the mesh. A rectangle is two triangles. Stretching a corner does not move `X`. Undo of color and of a warp. A project file round-trips both.
- `ExportTests`. PDF and PNG keep the scan and drop the cropped margin. A translucent sheet keeps the color of the sheet underneath. A warped sheet is a real image in the PDF and keeps the source colors. JPEG export is a readable picture.
- `WarpDrawTests`. The app's IL calls `DrawVertices` only with an `SKVertices`. Four raster meshes meeting at one point draw. Disposing a paint does not destroy a shared color filter.
- `CrashReportTests`. The black box keeps the newest 1024 lines in order, cuts a long line between characters, and a second handle sees the lines and the clean flag. Only crash codes get a report. A `0xC0000374` exit writes a report with the dump path, the `cdb` line, and the black box, and appends a `crash` line. Without a black box the report uses the session log tail. A kill adds one `end` line and no report. A clean exit leaves the log alone.

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
- The render thread sees snapshots and leases only. Every SkiaSharp object handed to a native call is owned until the call returns.
- The crash watcher runs before Avalonia, has no window, and never throws out of `TryRun`.
- Version 1.0.8 is the authored release. The About window reads `AppInfo`.

## Fixed bugs

Each of these came from a real session. Do not reintroduce them.

- Key `0` called `Fit`, which jumped a 3024-pixel sheet from 100% to about 23%.
- Crop and join were enabled whenever any sheet existed, so one click both selected a page and started a crop. After the check, choosing the tool again showed the whole uncut page and looked like the crop was lost. Now tools need a selection and the check clears it.
- The closing edge of an open outline was drawn as soon as there were four points, so an unfinished outline looked finished.
- After a cut, the corners and the rotate stick stayed on the full page.
- `EndCrop` read the pivot from the already mutated frame, and the picture jumped.
- The status hint lived in the coordinate string and moved as digits changed.
- The first Save wrote `project.json` straight into the picked folder. The welcome screen now makes the project a named folder.
- A semaphore was released without having been acquired. A resized pixmap was disposed twice.
- Skia's PDF encoder turned every page green. `PdfDocument` writes the PDF.
- A vertex mesh in export turned patches of a warped sheet gray. Export draws triangles one at a time.
- A translucent sheet in the PDF showed white instead of the sheet underneath. The mask has `/Matte [1 1 1]`.
- A crop click in the hidden margin of a cut front sheet hit that sheet instead of the one behind it. `HitTestCrop`.
- Log numbers used the current culture, so `27514,2,30465` could not be read. `LogText`.
- Heap corruption `0xC0000374` during Warp. See below.

## Incident: heap corruption 0xC0000374 during Warp, 2026-11-05

Symptom. `Galimapia.exe` died three times, at 19:40:41 (pid 24136, before the session log existed), 20:13:49 (pid 13900), and 20:23:53 (pid 24720). Both logged sessions died within a second of releasing a warp point, on different sheets: `186-1-5774_0057.jpg` point 60 with no glue and a move of about one pixel, and `186-1-5774_0035.jpg` point 19. Windows logged exception `0xc0000374` in `ntdll.dll`. The session log had no `fault` and no `process exit`. Memory was not short: 5.2 GB of 16 GB free.

Evidence. All three minidumps in `%LocalAppData%\CrashDumps` have the same stack on the render thread:

```
ntdll!RtlFreeHeap
libSkiaSharp!SkNVRefCnt<SkVertices>::unref
libSkiaSharp!(anonymous namespace)::MeshOp::~MeshOp
libSkiaSharp!skgpu::ganesh::OpsTask::deleteOps
libSkiaSharp!GrDrawingManager::flush
libSkiaSharp!gr_direct_context_flush_and_submit
Avalonia_Skia!Avalonia.Skia.SurfaceRenderTarget.Blit
Avalonia_Base!...ServerCompositor.RenderCore
```

`!analyze` bucket: `HEAP_CORRUPTION_ACTIONABLE_BlockNotBusy_DOUBLE_FREE_c0000374_libSkiaSharp.DLL!_free_base`. The vertex buffer of a warp mesh was freed a second time when the GPU op that drew it was deleted after the flush.

Cause. In SkiaSharp 3.119.4, `SKCanvas.DrawVertices(SKVertexMode, SKPoint[], SKPoint[], SKColor[], SKPaint)` calls `SKVertices.CreateCopy` and passes the result to `DrawVertices(SKVertices, SKBlendMode, SKPaint)`, which reads the handle and calls `sk_canvas_draw_vertices`. Nothing disposes the temporary or keeps it alive. `SKNativeObject.Finalize` calls `DisposeNative`, which unrefs the native object. A GC during the native call can therefore finalize the wrapper and free the `SkVertices` before Skia's GPU device takes its own reference. Skia takes that reference only after it has converted the paint and its image shader, so the window is wider than a few instructions. The recorded `MeshOp` then points at freed memory, and its destructor frees it again at the flush. A warp drag builds a new mesh every frame while the UI thread allocates heavily, so sooner or later a GC lands inside the call. `SkVertices` exist only in `DrawWarped`, so the 19:40 dump, which has no log, was a warp frame too.

Not the cause. The flat triangle of `186-1-5774_0057.jpg` (0.17 px), antialiasing, and memory pressure. The stack has no rasterizer in it, and the crash also hit `186-1-5774_0035.jpg`. The earlier "junction of three or four sheets" crash that `KeepSolid` was added for was probably this same bug. No dump of it is left to confirm that; the oldest Galimapia dump is the 19:40 one.

Fix. `DrawWarped` creates `using var mesh = SKVertices.CreateCopy(...)` and calls `canvas.DrawVertices(mesh, SKBlendMode.Modulate, paint)`. The `using` keeps the wrapper alive until the call returns; the GPU op holds its own reference until the flush. The IL test fails if any code in the app calls a `DrawVertices` overload that takes point arrays.

Lesson. A native crash is read from its dump. The in-process filter never fired, the log ended a frame early, and the first theory pointed at geometry. The crash watcher and the black box exist so the next one comes with a report.
