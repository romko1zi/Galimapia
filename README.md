# Galimapia

Galimapia is a Windows desktop app for lining up many large scans, such as old cadastral map sheets and book pages, and exporting the arrangement.

Drop the scans on an infinite canvas. Crop the margins, rotate and scale each sheet, join two points, and stretch corners where the paper has warped. The program keeps each scan as its own object. It does not build one giant bitmap of the whole map while you edit, so a survey of dozens of multi-megapixel sheets stays workable.

Export writes the white page around the sheets to:

- PDF, one picture per sheet
- JPEG or PNG, one flattened picture
- map tiles and an HTML page you open in a browser

Version 1.0.8. Author: Roman Valentyn Badiak.

## Run it

The release asset `Galimapia-win-x64.exe` is a self-contained Windows 64-bit program. It does not need a separate .NET install. Download it from [Releases](https://github.com/romko1zi/Galimapia/releases), then start it. Windows SmartScreen may warn about an unsigned download; choose More info, then Run anyway, if you trust this release.

## Build from source

Requires the .NET 9 SDK on Windows.

```
dotnet run --project Galimapia.csproj
dotnet test Galimapia.sln
```
