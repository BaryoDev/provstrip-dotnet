# Changelog

## 0.1.0

First release.

- `BaryoDev.ProvStrip`: inspect and strip PNG, JPEG, WebP and SVG files. Removes C2PA, XMP naming a tool,
  Software tags, editor data, EXIF, GPS, IPTC and machine-named colour profiles by cutting bytes. Keeps EXIF
  orientation and standard colour profiles. Unknown segments stop the operation. Every strip is parsed again
  before it is returned.
- `BaryoDev.ProvStrip.Tool`: the `provstrip` command, with `strip` and `verify`, directories and globs,
  per-category `auto` or `flag`, `--keep-unknown`, `--rules` and `--json`.
- `BaryoDev.ProvStrip.Build`: a `dotnet pack` step that checks every packed image, `PackageIcon` included. Fails
  the pack by default, or strips the packed copy with `ProvStripProvenance=auto` and `ProvStripCamera=auto`.
