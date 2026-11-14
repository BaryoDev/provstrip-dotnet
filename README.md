<p align="center"><img src="https://raw.githubusercontent.com/BaryoDev/provstrip-dotnet/main/assets/header.svg" alt="ProvStrip" width="100%"></p>

# ProvStrip for .NET

Design tools and AI image tools write attribution into the files they export: C2PA content credentials,
XMP naming the tool, Software tags. Cameras and phones add EXIF with the camera model, the time and the GPS
position. ProvStrip finds that data in PNG, JPEG, WebP and SVG files and cuts it out.

It cuts bytes and never re-encodes. Every segment it keeps is copied byte for byte, so pixels do not change.
Anything it does not recognise stops the operation and is named, instead of being kept or dropped on a guess.
After every strip the output is parsed again and checked before anything is written.

## What it removes

Findings come in three categories, and each category is handled on its own.

| Category | What | Where |
| --- | --- | --- |
| AI and tool provenance | C2PA manifests | PNG `caBX`, JPEG APP11 (JUMBF), WebP `C2PA`, SVG `<c2pa:manifest>` |
| | XMP that names a tool (`CreatorTool`, `softwareAgent`, C2PA markers) or gives an IPTC digital source type of generated media (`trainedAlgorithmicMedia`, `algorithmicMedia`, `compositeSynthetic`) | PNG `iTXt`, JPEG APP1, WebP `XMP `, SVG `<metadata>` |
| | Software and generator text | PNG `tEXt`/`zTXt`/`iTXt` (`Software`, and the `parameters`, `prompt` and `workflow` keys image generators write), EXIF `Software` when it names a tool from `softwareTools` (a phone's OS version stays camera data), SVG generator comments |
| | Editor data | SVG `inkscape:` and `sodipodi:` elements, attributes and namespace declarations |
| Camera data | EXIF: camera model, date taken, GPS | JPEG APP1, PNG `eXIf`, WebP `EXIF` |
| | IPTC and comments | JPEG APP13 and COM |
| | Other text that names no tool | PNG `tEXt`/`zTXt`/`iTXt` |
| | XMP that names no tool | as above |
| | A colour profile that names a machine | PNG `iCCP`, JPEG APP2, WebP `ICCP` |
| Unknown | Anything on neither list | always reported, never removed on its own |

A segment that matches a provenance rule is provenance even when it also holds camera data, so a mixed
XMP packet goes as a whole.

## What it always keeps

- Pixel data, byte for byte. This is checked by tests on every fixture.
- EXIF orientation, so photos do not turn sideways. Removing camera data rewrites the EXIF block to hold only
  the orientation tag. That rewritten block is the one segment that is not byte identical to the input.
- Standard colour profiles (sRGB, Display P3, Adobe RGB and the like), so colours do not shift.
- Everything a decoder needs: PNG critical chunks, JPEG tables and scans, WebP image and animation chunks, and
  the drawing elements of an SVG.

When a WebP loses its `EXIF`, `XMP ` or `ICCP` chunk, the matching flag bit in `VP8X` is cleared and the RIFF size is
rewritten. Those are header edits, not re-encoding.

## Packages

| Package | What it is | Install |
| --- | --- | --- |
| `BaryoDev.ProvStrip` | The engine, a library with no dependencies | `dotnet add package BaryoDev.ProvStrip` |
| `BaryoDev.ProvStrip.Tool` | The `provstrip` command | `dotnet tool install -g BaryoDev.ProvStrip.Tool` |
| `BaryoDev.ProvStrip.Build` | A `dotnet pack` step that checks packed images | `dotnet add package BaryoDev.ProvStrip.Build` |
| `BaryoDev.Umbraco.ProvStrip` | The engine in the Umbraco 17 and 18 media library | `dotnet add package BaryoDev.Umbraco.ProvStrip` |

Using the engine:

```csharp
using BaryoDev.ProvStrip;
using BaryoDev.ProvStrip.Engine;

var bytes = File.ReadAllBytes("photo.jpg");
var inspection = ProvStripEngine.Inspect(bytes);
var decision = new StripPolicy(Provenance: CategoryMode.Auto, Camera: CategoryMode.Flag).Decide(inspection);
if (!decision.Blocked)
{
    var result = ProvStripEngine.Strip(bytes, decision.Remove);
    File.WriteAllBytes("photo.clean.jpg", result.Output);
}

// Or in place, written through a temp file and an atomic move:
FileStripper.Run("photo.jpg", new StripPolicy(CategoryMode.Auto, CategoryMode.Auto));
```

The format comes from the bytes, never the file name.

## The `provstrip` command

```
provstrip strip <input> [<output>]
provstrip strip -i <file|dir|glob>...
provstrip verify <file|dir|glob>...

options: --provenance auto|flag   (default auto for strip)
         --camera auto|flag       (default auto for strip)
         --keep-unknown           keep unknown segments and report them
         --rules <file>           use a rules file instead of the built-in rules
         --json                   machine-readable output
         --version, --help
```

`strip -i` refuses a symbolic link with an error and writes nothing. Name the file it points at, or give an
output path. It also stops, writing nothing, if the file changes while it is being stripped.

A directory means every supported file below it. More than one input needs `-i`, and an output path needs
one input file.

| Exit code | Meaning |
| --- | --- |
| 0 | Done, nothing left that was asked about |
| 1 | `strip` in flag mode left findings, which are reported |
| 2 | `verify` found something, an unknown segment blocked `strip`, or a file could not be parsed |
| 64 | Usage error |

```
$ provstrip strip -i assets/
assets/icon.png: stripped C2PA
assets/photo.jpg: stripped GPS, camera model, date taken
assets/logo.svg: already clean
```

## The pack step

Referencing `BaryoDev.ProvStrip.Build` makes `dotnet pack` check every image that goes into the package,
`PackageIcon` included. By default it fails the pack and names the file. It never edits your source files: in
`auto` mode it strips the copy that goes into the package.

| Property | Default | Meaning |
| --- | --- | --- |
| `ProvStripEnabled` | `true` | Turn the check off |
| `ProvStripProvenance` | `flag` | `flag` fails the pack, `auto` strips the packed copy |
| `ProvStripCamera` | `flag` | The same, for camera data |
| `ProvStripKeepUnknown` | `false` | Keep unknown segments with a warning instead of failing |
| `ProvStripRulesFile` | empty | A custom rules file |

To let one file through, set `ProvStripSkip="true"` on its item:

```xml
<None Include="assets/sample.jpg" Pack="true" PackagePath="/" ProvStripSkip="true" />
```

An unknown segment fails the pack even when `auto` is set, unless `ProvStripKeepUnknown` is `true`.

`auto` swaps in a cleaned copy only where that copy can take the original's place. Two cases fail the pack
instead and name the source file to strip with `provstrip strip -i`:

- `PROVSTRIP005`: a static web asset in a Razor class library. The package records its length and hash.
- `PROVSTRIP006`: an item with no `PackagePath`. NuGet places it by where it sits in the project.

A file with an image name (`.png`, `.jpg`, `.jpeg`, `.webp`, `.svg`, `.svgz`) that the tool cannot read, such as
a UTF-16 SVG, fails the pack with `PROVSTRIP007` rather than going through unchecked. Set `ProvStripSkip="true"`
on the item to let it through.

## Umbraco

`BaryoDev.Umbraco.ProvStrip` checks the Umbraco media library. It runs on Umbraco 17.8.1 and later 17.x, and 18.x,
on .NET 10. Install the package and start the site; it creates its two tables on first start.

### On upload

Every media item saved with a new file is checked before the save completes: backoffice uploads, the Management
API, imports and code. Each category is set to `Flag` or `Auto`.

- `Flag` (the default) stores the file as uploaded. The item's Info view shows a ProvStrip box with one line per
  category, such as "AI attribution found (C2PA)" or "Camera data found (GPS, camera model)", each with **Delete**
  and **Skip**.
- `Auto` strips that category from the stored file before the save completes. The URL does not change.

An upload is never blocked. A file that cannot be read, is larger than `MaxFileBytes`, or holds a segment on
neither list is stored as uploaded and shown as "Needs a look" with the reason. With `KeepUnknown` off, a file with
an unknown segment is not stripped at all, `Auto` included, so its camera data stays until someone removes it.

![The ProvStrip box on a media item](https://raw.githubusercontent.com/BaryoDev/provstrip-dotnet/main/docs/screenshots/info-before.png)

### Delete and Skip

**Delete** strips the category from the stored file in place and keeps the URL. There is no backup copy: keep the
original elsewhere if you need it. Delete is refused when the file changed since it was checked; check it again
and retry.

**Skip** hides a label. It is stored against the file's content hash, so it holds for that exact file, and for
identical copies of it elsewhere in the library, and it no longer applies once the file changes.

### Scanning the library

Media added before the package was installed has no labels until it is checked. The ProvStrip dashboard in the
Media section has **Scan media library**. A background job then checks `ScanBatchSize` items every 5 seconds, in id
order, and stores where it stopped, so a restart resumes the scan. Umbraco starts background jobs a few minutes
after the site starts. The scan changes no file.

Results are listed by folder, each with Delete and Skip. **Delete all** removes AI attribution and camera data from
every listed item in the background, one item at a time, and leaves any item whose file changed since its check.

### Permissions

The Info view box and its Delete and Skip work on items under the user's media start nodes. Scanning, scan results
and Delete all need media access from the root.

### Options

```json
{
  "BaryoDev": {
    "ProvStrip": {
      "Provenance": "Flag",
      "Camera": "Auto"
    }
  }
}
```

| Setting under `BaryoDev:ProvStrip` | Default | Meaning |
| --- | --- | --- |
| `Provenance` | `Flag` | `Flag` labels AI and tool provenance, `Auto` strips it on upload |
| `Camera` | `Flag` | The same, for camera data |
| `KeepUnknown` | `false` | Strip on upload even when a file has an unknown segment, keeping that segment |
| `ScanBatchSize` | `50` | Items checked per run of the scan job |
| `MaxFileBytes` | `67108864` | Files larger than this (64 MB) are not read and show as "too large to check" |

### Load-balanced sites

The upload check runs on the server that saves the item. The scan job runs on the scheduling server only. Delete
all runs on the server that received the request. Files are written through Umbraco's media file system, so blob
storage providers see the change.

### Umbraco limits

- Resized copies are rebuilt from the cleaned original, because ImageSharp copies EXIF into the crops it makes. A
  crop size already served can come from the old file for up to 30 seconds after a Delete, because ImageSharp.Web
  holds a file's details in memory that long. A size not served before is clean at once.
- Copies a CDN or a browser already holds are not purged. Purge your CDN after a Delete.

## Rules

The built-in rules are in [`src/BaryoDev.ProvStrip/Rules/rules.json`](src/BaryoDev.ProvStrip/Rules/rules.json).
A new marker inside a format already parsed is a rules edit, not a code change. Pass your own file with
`--rules`, `ProvStripRulesFile` or `RuleSet.Load(path)`.

```json
{
  "provenanceMarkers": ["c2pa", "CreatorTool", "softwareAgent"],
  "softwareTools": ["Adobe", "Midjourney", "ComfyUI"],
  "standardIccDescriptions": ["sRGB IEC61966-2.1", "Display P3"],
  "rules": [
    { "id": "png-cabx", "format": "png", "label": "caBX", "category": "provenance", "describe": "C2PA" },
    { "id": "jpeg-xmp", "format": "jpeg", "label": "APP1", "keyword": "http://ns.adobe.com/xap/1.0/", "classify": "xmp" }
  ]
}
```

| Field | Meaning |
| --- | --- |
| `id` | Unique name, reported with each finding |
| `format` | `png`, `jpeg`, `webp` or `svg` |
| `label` | The segment: a PNG chunk type, a JPEG marker (`APP1`, `COM`, `TRAILER`), a WebP FourCC (`XMP ` keeps its space), or an SVG name (`metadata`, `c2pa:*`, `#comment`). Alternatives split with `\|`, and a trailing `*` matches a prefix. |
| `keyword` | Optional. A PNG text keyword or the identifier at the start of a JPEG APPn payload. Same matching as `label`. |
| `contains` | Optional. The rule matches only when the segment contains one of these strings. |
| `category` and `describe` | `provenance` or `camera`, and the text shown to people |
| `classify` | Instead of a category: `xmp` (provenance when it names a tool), `exif` (camera, orientation kept) or `icc` (kept when standard) |

`provenanceMarkers` make XMP provenance when it contains one. `softwareTools` make an EXIF `Software` value
provenance when it contains one, so a phone's OS version there stays camera data.

The first rule that matches wins. A segment no rule matches and no format keeps is unknown.

## Limits

- The pack step runs under `dotnet pack` only, not under Visual Studio's .NET Framework MSBuild.
- PNG, JPEG, WebP and SVG only. Not AVIF, HEIC, GIF or PDF.
- A JPEG with an HDR gain map (Ultra HDR, recent phones) carries a second image after the first, with metadata
  of its own. ProvStrip reports it as an unknown trailer, so stripping such a file needs `--keep-unknown`, which
  keeps the gain map as it is.
- Metadata only. It does not look at pixels, so it does not detect generated content or remove watermarks
  drawn into the image.
- Copies already served by a CDN or cached elsewhere are not purged.

## License

MIT
