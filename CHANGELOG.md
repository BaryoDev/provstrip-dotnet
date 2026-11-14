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
- `BaryoDev.Umbraco.ProvStrip`: checks every image saved to the Umbraco 17 and 18 media library. Labels AI
  attribution and camera data with Delete and Skip on the media item, or strips them on upload with `Auto`. A
  dashboard scans the existing library in the background and offers Delete all. Delete writes the file in place
  and keeps its URL.
