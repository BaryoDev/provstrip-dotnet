"""Builds the provstrip test images. Run through make-fixtures.sh, which makes the signing key.

usage: python3 -I make_fixtures.py <fixtures dir> <work dir with leaf.key and chain.pem>
"""

import json
import struct
import subprocess
import sys
import zlib
from pathlib import Path

out = Path(sys.argv[1])
work = Path(sys.argv[2])
W = H = 16
BLUE = bytes([0x33, 0x66, 0xCC])


def write(name, data):
    (out / name).write_bytes(data)
    written.append(name)


written = []

# ---------- PNG ----------


def png_chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def png_chunks(png):
    i, chunks = 8, []
    while i < len(png):
        n = struct.unpack(">I", png[i:i + 4])[0]
        chunks.append(png[i:i + 12 + n])
        i += 12 + n
    return chunks


def png_insert_before(png, kind, *new):
    chunks = png_chunks(png)
    at = next(i for i, c in enumerate(chunks) if c[4:8] == kind)
    return png[:8] + b"".join(chunks[:at]) + b"".join(new) + b"".join(chunks[at:])


raw = b"".join(b"\x00" + BLUE * W for _ in range(H))
clean_png = (b"\x89PNG\r\n\x1a\n"
             + png_chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 2, 0, 0, 0))
             + png_chunk(b"IDAT", zlib.compress(raw, 9))
             + png_chunk(b"IEND", b""))
write("clean.png", clean_png)

# ---------- JPEG ----------

ppm = work / "base.ppm"
ppm.write_bytes(b"P6\n16 16\n255\n" + BLUE * W * H)
subprocess.run(["cjpeg", "-quality", "90", "-outfile", str(work / "clean.jpg"), str(ppm)], check=True)
subprocess.run(["cjpeg", "-quality", "90", "-progressive", "-outfile", str(work / "progressive.jpg"), str(ppm)], check=True)
clean_jpg = (work / "clean.jpg").read_bytes()
write("clean.jpg", clean_jpg)
write("progressive.jpg", (work / "progressive.jpg").read_bytes())


def jpeg_seg(marker, payload):
    return b"\xFF" + bytes([marker]) + struct.pack(">H", len(payload) + 2) + payload


def jpeg_insert(jpg, *segs):
    """After SOI and the JFIF APP0."""
    app0_len = struct.unpack(">H", jpg[4:6])[0]
    at = 4 + app0_len
    return jpg[:at] + b"".join(segs) + jpg[at:]


# ---------- WebP ----------

subprocess.run(["cwebp", "-quiet", "-metadata", "none", str(out / "clean.png"), "-o", str(work / "clean.webp")], check=True)
clean_webp = (work / "clean.webp").read_bytes()
write("clean.webp", clean_webp)
vp8 = clean_webp[12:]


def riff_chunk(fourcc, data):
    return fourcc + struct.pack("<I", len(data)) + data + (b"\x00" if len(data) & 1 else b"")


def riff(*chunks):
    body = b"WEBP" + b"".join(chunks)
    return b"RIFF" + struct.pack("<I", len(body)) + body


def vp8x(flags):
    return riff_chunk(b"VP8X", bytes([flags, 0, 0, 0]) + (W - 1).to_bytes(3, "little") + (H - 1).to_bytes(3, "little"))


# ---------- SVG ----------

clean_svg = ('<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 16 16">\n'
             '  <rect width="16" height="16" fill="#3366cc"/>\n'
             '</svg>\n').encode()
write("clean.svg", clean_svg)

# ---------- C2PA, signed by c2patool ----------

manifest = {
    "alg": "es256",
    "private_key": str(work / "leaf.key"),
    "sign_cert": str(work / "chain.pem"),
    "claim_generator_info": [{"name": "provstrip-fixtures", "version": "1.0"}],
    "title": "fixture",
    "assertions": [{
        "label": "c2pa.actions",
        "data": {"actions": [{
            "action": "c2pa.created",
            "digitalSourceType": "http://cv.iptc.org/newscodes/digitalsourcetype/trainedAlgorithmicMedia",
        }]},
    }],
}
(work / "manifest.json").write_text(json.dumps(manifest))
for ext in ["png", "jpg", "webp", "svg"]:
    subprocess.run(["c2patool", str(out / f"clean.{ext}"), "-m", str(work / "manifest.json"),
                    "-o", str(out / f"c2pa.{ext}"), "-f"], check=True, capture_output=True)
    written.append(f"c2pa.{ext}")

# ---------- EXIF (TIFF), built by hand ----------

ASCII, SHORT, LONG, RATIONAL = 2, 3, 4, 5


def tag(t, kind, value):
    if kind == ASCII:
        data = value.encode() + b"\x00"
        return (t, kind, len(data), data)
    if kind == SHORT:
        return (t, kind, 1, struct.pack("<H", value))
    if kind == LONG:
        return (t, kind, 1, struct.pack("<I", value))
    if kind == RATIONAL:
        return (t, kind, len(value), b"".join(struct.pack("<II", a, b) for a, b in value))
    raise ValueError(kind)


def ifd(entries, start):
    entries = sorted(entries)
    head = struct.pack("<H", len(entries))
    data_at = start + 2 + 12 * len(entries) + 4
    data = b""
    for t, kind, count, value in entries:
        if len(value) <= 4:
            head += struct.pack("<HHI", t, kind, count) + value.ljust(4, b"\x00")
        else:
            head += struct.pack("<HHII", t, kind, count, data_at + len(data))
            data += value + (b"\x00" if len(value) & 1 else b"")
    return head + struct.pack("<I", 0) + data


def tiff(ifd0, exif=None, gps=None):
    """Little-endian TIFF: IFD0, then the Exif and GPS sub-IFDs it points to."""
    def build(exif_at, gps_at):
        entries = list(ifd0)
        if exif is not None:
            entries.append(tag(0x8769, LONG, exif_at))
        if gps is not None:
            entries.append(tag(0x8825, LONG, gps_at))
        return ifd(entries, 8)
    first = build(0, 0)
    exif_at = 8 + len(first)
    exif_bytes = ifd(exif, exif_at) if exif is not None else b""
    gps_at = exif_at + len(exif_bytes)
    gps_bytes = ifd(gps, gps_at) if gps is not None else b""
    return b"II*\x00" + struct.pack("<I", 8) + build(exif_at, gps_at) + exif_bytes + gps_bytes


gps = [tag(1, ASCII, "N"), tag(2, RATIONAL, [(1, 1), (30, 1), (0, 1)]),
       tag(3, ASCII, "E"), tag(4, RATIONAL, [(2, 1), (30, 1), (0, 1)])]
camera_ifd0 = [tag(0x010F, ASCII, "Fixture"), tag(0x0110, ASCII, "Fixture Cam 1")]
camera_exif = [tag(0x9003, ASCII, "2026:01:02 03:04:05")]
camera_tiff = tiff(camera_ifd0 + [tag(0x0112, SHORT, 6)], camera_exif, gps)
no_orientation_tiff = tiff(camera_ifd0, camera_exif, gps)
orientation_tiff = tiff([tag(0x0112, SHORT, 6)])

write("camera.jpg", jpeg_insert(clean_jpg, jpeg_seg(0xE1, b"Exif\x00\x00" + camera_tiff)))
write("orientation-only.jpg", jpeg_insert(clean_jpg, jpeg_seg(0xE1, b"Exif\x00\x00" + orientation_tiff)))
write("camera.png", png_insert_before(clean_png, b"IDAT", png_chunk(b"eXIf", camera_tiff)))
write("camera.webp", riff(vp8x(0x08), vp8, riff_chunk(b"EXIF", camera_tiff)))
write("exif-no-orientation.webp", riff(vp8x(0x08), vp8, riff_chunk(b"EXIF", no_orientation_tiff)))
anim = riff_chunk(b"ANIM", b"\x00\x00\x00\x00" + struct.pack("<H", 0))
frame = (0).to_bytes(3, "little") * 2 + (W - 1).to_bytes(3, "little") + (H - 1).to_bytes(3, "little") \
    + (100).to_bytes(3, "little") + b"\x00" + vp8
write("animated.webp", riff(vp8x(0x02 | 0x08), anim, riff_chunk(b"ANMF", frame), riff_chunk(b"ANMF", frame),
                            riff_chunk(b"EXIF", camera_tiff)))

# ---------- XMP ----------


def xmp(description):
    return ('<?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>'
            '<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
            + description + '</rdf:RDF></x:xmpmeta><?xpacket end="w"?>').encode()


tool_xmp = xmp('<rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmp:CreatorTool="Fixture Tool 1.0"/>')
plain_xmp = xmp('<rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title><rdf:Alt>'
                '<rdf:li xml:lang="x-default">fixture</rdf:li></rdf:Alt></dc:title></rdf:Description>')
mixed_xmp = xmp('<rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" '
                'xmlns:exif="http://ns.adobe.com/exif/1.0/" xmp:CreatorTool="Fixture Tool 1.0" '
                'exif:GPSLatitude="1,30.0N"/>')

itxt = png_chunk(b"iTXt", b"XML:com.adobe.xmp\x00\x00\x00\x00\x00" + tool_xmp)
software = png_chunk(b"tEXt", b"Software\x00Fixture Tool 1.0")
write("tool-xmp.png", png_insert_before(clean_png, b"IDAT", itxt, software))
XMP_ID = b"http://ns.adobe.com/xap/1.0/\x00"
write("plain-xmp.jpg", jpeg_insert(clean_jpg, jpeg_seg(0xE1, XMP_ID + plain_xmp)))
write("mixed-xmp.jpg", jpeg_insert(clean_jpg, jpeg_seg(0xE1, XMP_ID + mixed_xmp)))

# ---------- IPTC and comment ----------

byline = b"Fixture Author"
iptc = b"\x1C\x02\x50" + struct.pack(">H", len(byline)) + byline
irb = b"8BIM" + struct.pack(">H", 0x0404) + b"\x00\x00" + struct.pack(">I", len(iptc)) + iptc + (b"\x00" if len(iptc) & 1 else b"")
write("iptc.jpg", jpeg_insert(clean_jpg, jpeg_seg(0xED, b"Photoshop 3.0\x00" + irb), jpeg_seg(0xFE, b"fixture comment")))

# ---------- ICC profiles: header, one-entry tag table, a v2 desc tag ----------


def icc(description):
    text = description.encode() + b"\x00"
    desc = b"desc" + b"\x00" * 4 + struct.pack(">I", len(text)) + text + b"\x00" * 8 + b"\x00" * 3 + b"\x00" * 67
    desc += b"\x00" * (-len(desc) % 4)
    size = 128 + 4 + 12 + len(desc)
    header = (struct.pack(">I", size) + b"\x00" * 4 + struct.pack(">I", 0x02100000) + b"mntrRGB XYZ "
              + b"\x00" * 12 + b"acsp" + b"\x00" * 24 + struct.pack(">I", 0)
              + struct.pack(">iii", 63190, 65536, 54061) + b"\x00" * 48)
    assert len(header) == 128
    return header + struct.pack(">I", 1) + b"desc" + struct.pack(">II", 144, len(desc)) + desc


def icc_seg(description):
    return jpeg_seg(0xE2, b"ICC_PROFILE\x00\x01\x01" + icc(description))


write("icc-standard.jpg", jpeg_insert(clean_jpg, icc_seg("sRGB IEC61966-2.1")))
write("icc-machine.jpg", jpeg_insert(clean_jpg, icc_seg("Fixture Monitor 27")))

# ---------- Unknown segments ----------

write("unknown-ancillary.png", png_insert_before(clean_png, b"IEND", png_chunk(b"prVt", b"private data")))
write("unknown-critical.png", png_insert_before(clean_png, b"IEND", png_chunk(b"QXQX", b"critical data")))

# MPF says a second image follows EOI, as phone cameras write it.
mpf = jpeg_seg(0xE2, b"MPF\x00" + b"II*\x00" + struct.pack("<I", 8) + struct.pack("<H", 0) + struct.pack("<I", 0))
write("mpf-trailer.jpg", jpeg_insert(clean_jpg, mpf) + clean_jpg)
write("trailer-no-mpf.jpg", clean_jpg + b"trailing bytes")

# ---------- SVG with a generator comment and editor data ----------

write("generator-comment.svg", (
    '<?xml version="1.0" encoding="UTF-8"?>\n'
    '<!-- Generator: Fixture Tool 1.0 -->\n'
    '<svg xmlns="http://www.w3.org/2000/svg" '
    'xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape" '
    'xmlns:sodipodi="http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd" '
    'width="16" height="16" viewBox="0 0 16 16" inkscape:version="1.0 (fixture)">\n'
    '  <!-- a plain comment that stays -->\n'
    '  <sodipodi:namedview id="nv" pagecolor="#ffffff"/>\n'
    '  <rect width="16" height="16" fill="#3366cc"/>\n'
    '</svg>\n').encode())

# ---------- Broken files ----------

write("truncated.png", clean_png[:-10])
bad = bytearray(clean_png)
bad[8 + 4 + 4 + 13] ^= 0xFF  # first byte of the IHDR CRC
write("bad-crc.png", bytes(bad))
write("no-eoi.jpg", clean_jpg[:-2])
long_riff = bytearray(clean_webp)
struct.pack_into("<I", long_riff, 4, len(clean_webp) - 8 + 100)
write("riff-too-long.webp", bytes(long_riff))

(out / "manifest.json").write_text(json.dumps(sorted(written), indent=2) + "\n")
print(f"{len(written)} fixtures")
