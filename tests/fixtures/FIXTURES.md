# Test files

Made by `make-fixtures.sh`. Rerun it to rebuild them all. The C2PA files are signed by c2patool
with a throwaway certificate made in a temp directory; its key is never written here. No file names
a real AI tool.

| File | Holds |
| --- | --- |
| `clean.png`, `clean.jpg`, `clean.webp`, `clean.svg` | 16x16 solid image, no metadata |
| `c2pa.png`, `c2pa.jpg`, `c2pa.webp`, `c2pa.svg` | the clean file signed by c2patool, claim generator `provstrip-fixtures` 1.0 |
| `camera.jpg`, `camera.png`, `camera.webp` | EXIF: Make `Fixture`, Model `Fixture Cam 1`, DateTimeOriginal `2026:01:02 03:04:05`, GPS 1.5 N 2.5 E, Orientation 6 |
| `orientation-only.jpg` | EXIF with only Orientation 6 |
| `exif-no-orientation.webp` | the camera EXIF without Orientation, VP8X EXIF flag set |
| `animated.webp` | two frames, with the camera EXIF |
| `tool-xmp.png` | iTXt XMP with `xmp:CreatorTool="Fixture Tool 1.0"`, and tEXt `Software` `Fixture Tool 1.0` |
| `plain-xmp.jpg` | XMP with only `dc:title` |
| `mixed-xmp.jpg` | one XMP packet with `xmp:CreatorTool` and `exif:GPSLatitude` |
| `iptc.jpg` | APP13 IPTC with a by-line, and a COM comment |
| `icc-standard.jpg`, `icc-machine.jpg` | APP2 ICC profile described as `sRGB IEC61966-2.1` and `Fixture Monitor 27` |
| `unknown-ancillary.png` | an extra chunk `prVt` (lowercase first letter, safe to drop) |
| `unknown-critical.png` | an extra chunk `QXQX` (uppercase first letter, critical) |
| `progressive.jpg` | progressive scan, several SOS segments |
| `mpf-trailer.jpg` | APP2 MPF and a second JPEG after EOI |
| `trailer-no-mpf.jpg` | bytes after EOI with no MPF |
| `generator-comment.svg` | a `Generator:` comment, Inkscape and Sodipodi editor data, and a plain comment |
| `truncated.png`, `bad-crc.png`, `no-eoi.jpg`, `riff-too-long.webp` | broken copies of the clean files |
