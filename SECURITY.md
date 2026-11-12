# Security

## Reporting

**Please do not open a public issue.** Use
[GitHub's private vulnerability reporting](https://github.com/BaryoDev/provstrip-dotnet/security/advisories/new),
or the contact route in the [BaryoDev policy](https://github.com/BaryoDev/.github/blob/main/SECURITY.md).

## What counts as a security bug here

This library reads files it did not write: uploads, downloads, package assets. It makes one promise about
them, that a file it calls clean carries nothing its rules name, and that a file it does not understand is
refused rather than passed. So these are real findings:

- **A file that inspects clean but still carries attribution or camera data** the rules name, for example a
  C2PA manifest the parser does not see because of how the file is laid out. Parser differentials, where
  this scanner and a real decoder read the same bytes differently, belong here.
- **A kept segment that changes.** Everything kept is copied byte for byte, except the EXIF block rewritten
  to hold only orientation, the WebP `VP8X` flag byte and the RIFF size.
- **Input that hangs the process, exhausts memory or crashes it** instead of failing with
  `ProvStripFormatException`. Compressed colour profiles are inflated with a 4 MB cap for this reason.
- **A write that lands somewhere other than the target.** In-place writes go to a temp file in the same
  directory, created owner-only, then moved over the original. The pack step writes only under the
  project's `obj` folder and never to a source file.

Reports that a format or a segment is refused as unknown are not vulnerabilities; refusing is the design.
Reports that something unknown was silently kept or dropped are.

## Supported versions

Fixes go to the latest published version.
