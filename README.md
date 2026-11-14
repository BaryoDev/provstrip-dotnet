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
