#!/usr/bin/env python3
"""Generate printable tagStandard41h12 AprilTags as SVG, for the ERUPT AR module.

The bit patterns come from the official AprilRobotics images:

    git clone https://github.com/AprilRobotics/apriltag-imgs
    python3 make_apriltag_svg.py --imgs ../apriltag-imgs/tagStandard41h12 --ids 0-3 --size 0.10

--size is the DETECTION square in metres: the inner square the detector measures, which for
tagStandard41h12 is the 5 central cells of the 9-cell printed pattern, NOT the full printed
square. It is the value to enter as Tag Size in Unity (MarkerAnchor, AprilTagTracker). With
--size 0.10 each cell is 2 cm and the printed black-and-white pattern is 18 cm wide; with
--size 0.05 each cell is 1 cm and the pattern is 9 cm wide.

Each tag is written centred on a page (--page, 200 x 270 mm by default, which fits both A4 and
US Letter). Print at 100 % scale (no "fit to page") and measure the inner 5-cell square with a
ruler: if it differs from --size, enter what you measure in Unity.

No dependencies: the tiny PNGs are decoded with the standard library.
"""
import argparse
import os
import sys

TAG_CELLS = 9          # tagStandard41h12 total width in cells
DETECTION_CELLS = 5    # cells spanned by the pose-estimation square
FAMILY_PREFIX = "tag41_12_"


def parse_ids(spec):
    ids = []
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            lo, hi = part.split("-", 1)
            ids.extend(range(int(lo), int(hi) + 1))
        else:
            ids.append(int(part))
    return ids


def read_png_luminance(path):
    """Decode a small PNG with the standard library. Returns (width, height, rows of 0-255 luminance)."""
    import struct
    import zlib
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        sys.exit(f"{path}: not a PNG")
    pos, idat, palette = 8, b"", None
    width = height = depth = ctype = None
    while pos < len(data):
        length, ctype_name = struct.unpack(">I4s", data[pos:pos + 8])
        chunk = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if ctype_name == b"IHDR":
            width, height, depth, ctype, _, _, interlace = struct.unpack(">IIBBBBB", chunk)
            if interlace:
                sys.exit(f"{path}: interlaced PNGs are not supported")
        elif ctype_name == b"PLTE":
            palette = [chunk[i:i + 3] for i in range(0, len(chunk), 3)]
        elif ctype_name == b"IDAT":
            idat += chunk
        elif ctype_name == b"IEND":
            break
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ctype]
    bits_per_pixel = channels * depth
    stride = (width * bits_per_pixel + 7) // 8
    bpp = max(1, bits_per_pixel // 8)
    raw = zlib.decompress(idat)
    rows, prev = [], bytearray(stride)
    for y in range(height):
        f = raw[y * (stride + 1)]
        line = bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            if f == 1:
                line[i] = (line[i] + a) & 0xFF
            elif f == 2:
                line[i] = (line[i] + b) & 0xFF
            elif f == 3:
                line[i] = (line[i] + (a + b) // 2) & 0xFF
            elif f == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pred = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[i] = (line[i] + pred) & 0xFF
        rows.append(bytes(line))
        prev = line
    lum = []
    for line in rows:
        out = []
        for x in range(width):
            if depth < 8:
                bit = x * depth
                v = (line[bit // 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1)
                if ctype == 3:
                    r, g, b = palette[v]
                    out.append((r + g + b) // 3)
                else:
                    out.append(v * 255 // ((1 << depth) - 1))
            else:
                step = depth // 8
                px = line[x * channels * step:(x + 1) * channels * step:step]
                if ctype == 3:
                    r, g, b = palette[px[0]]
                    out.append((r + g + b) // 3)
                elif ctype in (0, 4):
                    out.append(px[0])
                else:
                    out.append((px[0] + px[1] + px[2]) // 3)
        lum.append(out)
    return width, height, lum


def load_pattern(path):
    w, h, lum = read_png_luminance(path)
    if (w, h) == (TAG_CELLS, TAG_CELLS):
        # The original images: one pixel per cell, the whole image is the pattern.
        return [[lum[y][x] < 128 for x in range(TAG_CELLS)] for y in range(TAG_CELLS)]
    # A copy with a uniform white border around the pattern: crop to the black pixels. This is
    # only right for an id whose outer ring has a black cell on every side.
    blacks = [(x, y) for y in range(h) for x in range(w) if lum[y][x] < 128]
    if not blacks:
        sys.exit(f"{path}: no black pixels found")
    x0, x1 = min(p[0] for p in blacks), max(p[0] for p in blacks)
    y0, y1 = min(p[1] for p in blacks), max(p[1] for p in blacks)
    width, height = x1 - x0 + 1, y1 - y0 + 1
    if width != TAG_CELLS or height != TAG_CELLS:
        sys.exit(f"{path}: expected a {TAG_CELLS}x{TAG_CELLS} pattern, found {width}x{height}. "
                 "Pass the original 1-pixel-per-cell PNGs from apriltag-imgs.")
    return [[lum[y0 + y][x0 + x] < 128 for x in range(TAG_CELLS)] for y in range(TAG_CELLS)]


def tag_svg_elements(pattern, cell_cm, ox_cm, oy_cm):
    rects = []
    for y, row in enumerate(pattern):
        for x, black in enumerate(row):
            if black:
                rects.append(f'  <rect x="{ox_cm + x * cell_cm:.4f}" y="{oy_cm + y * cell_cm:.4f}" '
                             f'width="{cell_cm:.4f}" height="{cell_cm:.4f}" fill="#000"/>')
    return rects


def single_svg(tag_id, pattern, size_m, page):
    """One tag centred on a page; the detection square is outlined in the caption, not on the tag."""
    cell_cm = size_m * 100.0 / DETECTION_CELLS
    pattern_cm = cell_cm * TAG_CELLS
    page_w, page_h = page
    caption_h = 2.4
    # At least half a cell of white around the pattern on the page itself; the paper adds its own margin.
    if pattern_cm + cell_cm > page_w or pattern_cm + cell_cm + caption_h > page_h:
        sys.exit(f"a {size_m:.3f} m tag is {pattern_cm:.1f} cm wide and needs a white margin of at least {cell_cm / 2:.1f} cm on each side; "
                 f"it does not fit on a {page_w:.0f} x {page_h:.0f} cm page. Use a smaller --size or a larger --page.")
    ox = (page_w - pattern_cm) / 2
    oy = (page_h - caption_h - pattern_cm) / 2
    lines = [f'<svg xmlns="http://www.w3.org/2000/svg" shape-rendering="crispEdges" width="{page_w:.4f}cm" height="{page_h:.4f}cm" '
             f'viewBox="0 0 {page_w:.4f} {page_h:.4f}">',
             f'  <rect x="0" y="0" width="{page_w:.4f}" height="{page_h:.4f}" fill="#fff"/>']
    lines += tag_svg_elements(pattern, cell_cm, ox, oy)
    text_y = oy + pattern_cm + max(cell_cm / 2, 0.6) + 0.6
    inner_cm = cell_cm * DETECTION_CELLS
    for i, text in enumerate((
            f'tagStandard41h12 id {tag_id} - Tag Size = {size_m:.3f} m - the arrow marks the top edge of the tag',
            f'Print at 100 % (no "fit to page"). The tag size is the inner square of {DETECTION_CELLS} cells '
            f'({inner_cm:.1f} cm), not the printed pattern ({pattern_cm:.1f} cm).',
            'Measure the inner square with a ruler and enter what you measure. Keep the white margin around the pattern.')):
        lines.append(f'  <text x="{page_w / 2:.4f}" y="{text_y + i * 0.55:.4f}" font-family="sans-serif" '
                     f'font-size="0.34" text-anchor="middle">{text}</text>')
    # A tick above the pattern marks its top edge, so the tag is laid down the right way round.
    tick = oy - max(cell_cm / 2, 0.6)
    if tick - 0.7 > 0:
        lines.append(f'  <path d="M {page_w / 2 - 0.4:.4f} {tick:.4f} L {page_w / 2:.4f} {tick - 0.7:.4f} '
                     f'L {page_w / 2 + 0.4:.4f} {tick:.4f} Z" fill="#888"/>')
    lines.append('</svg>')
    return "\n".join(line for line in lines if line) + "\n"


def parse_page(spec):
    named = {"a4": (21.0, 29.7), "letter": (21.59, 27.94), "both": (20.0, 27.0)}
    if spec.lower() in named:
        return named[spec.lower()]
    try:
        w, h = spec.lower().split("x", 1)
        return float(w) / 10.0, float(h) / 10.0
    except ValueError:
        sys.exit(f"--page {spec}: expected a4, letter, both, or WIDTHxHEIGHT in millimetres")


def sheet_svg(tags, size_m, margin_cells, page=(21.0, 29.7)):
    cell_cm = size_m * 100.0 / DETECTION_CELLS
    pattern_cm = cell_cm * TAG_CELLS
    margin_cm = cell_cm * margin_cells
    slot = pattern_cm + 2 * margin_cm
    caption_h = 0.6
    slot_h = slot + caption_h
    page_w, page_h = page
    page_margin = 1.0
    cols = max(1, int((page_w - 2 * page_margin) // slot))
    rows = max(1, int((page_h - 2 * page_margin) // slot_h))
    if len(tags) > cols * rows:
        print(f"warning: only {cols * rows} tags fit on one page; {len(tags) - cols * rows} omitted", file=sys.stderr)
    lines = [f'<svg xmlns="http://www.w3.org/2000/svg" shape-rendering="crispEdges" width="{page_w}cm" height="{page_h}cm" viewBox="0 0 {page_w} {page_h}">',
             f'  <rect x="0" y="0" width="{page_w}" height="{page_h}" fill="#fff"/>']
    for i, (tag_id, pattern) in enumerate(tags[:cols * rows]):
        c, r = i % cols, i // cols
        ox = page_margin + c * slot + margin_cm
        oy = page_margin + r * slot_h + margin_cm
        lines += tag_svg_elements(pattern, cell_cm, ox, oy)
        lines.append(f'  <text x="{ox + pattern_cm / 2:.4f}" y="{oy + pattern_cm + margin_cm + 0.4:.4f}" '
                     f'font-family="sans-serif" font-size="0.3" text-anchor="middle">id {tag_id} — {size_m:.3f} m</text>')
    lines.append('</svg>')
    return "\n".join(lines) + "\n"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--imgs", required=True, help="path to apriltag-imgs/tagStandard41h12")
    ap.add_argument("--ids", default="0-3", help="IDs, e.g. 0-3 or 1,2,5")
    ap.add_argument("--size", type=float, default=0.10, help="detection-square size in metres (default 0.10)")
    ap.add_argument("--page", default="both", help="page of a single tag: a4, letter, both (200 x 270 mm, fits either; "
                                                   "the default) or WIDTHxHEIGHT in millimetres")
    ap.add_argument("--margin-cells", type=float, default=1.0, help="white margin around each pattern on the sheet, in cells")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "tags"), help="output directory")
    ap.add_argument("--sheet", action="store_true", help="also write one A4 sheet with all tags (small tags)")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    page = parse_page(args.page)
    tags = []
    for tag_id in parse_ids(args.ids):
        path = os.path.join(args.imgs, f"{FAMILY_PREFIX}{tag_id:05d}.png")
        if not os.path.exists(path):
            sys.exit(f"missing {path}")
        pattern = load_pattern(path)
        tags.append((tag_id, pattern))
        out = os.path.join(args.out, f"tag41_12_id{tag_id}_{int(round(args.size * 1000))}mm.svg")
        with open(out, "w") as f:
            f.write(single_svg(tag_id, pattern, args.size, page))
        print(out)
    if args.sheet:
        out = os.path.join(args.out, f"tag41_12_sheet_{int(round(args.size * 1000))}mm.svg")
        with open(out, "w") as f:
            f.write(sheet_svg(tags, args.size, args.margin_cells))
        print(out)


if __name__ == "__main__":
    main()
