"""Convert countries.geo.json to WPF StreamGeometry path data (equirectangular).

Output: one line per country polygon-set:
    "<ISO3>|<Name>|<label cx>,<label cy>,<label font size>|<path data>"
The label point is the shoelace centroid of the largest polygon; the font size
is fitted so the name roughly spans that polygon (map units — it scales with
the map, and the app hides labels that would be too small at the current zoom).
Projection: x = (lon + 180) / 360 * 1000 ; y = (90 - lat) / 180 * 500
So the canvas is 1000 x 500 and dots can be plotted with the same formula.
"""
import json
import os

W, H = 1000.0, 500.0

def proj(lon, lat):
    x = (lon + 180.0) / 360.0 * W
    y = (90.0 - lat) / 180.0 * H
    return round(x, 1), round(y, 1)

def label_spec(polys, name):
    """Centroid of the largest outer ring + a font size fitted to its bbox."""
    best, best_area = None, 0.0
    for poly in polys:
        ring = [proj(lon, lat) for lon, lat in poly[0]]
        a = cx = cy = 0.0
        for i in range(len(ring) - 1):
            (x0, y0), (x1, y1) = ring[i], ring[i + 1]
            cross = x0 * y1 - x1 * y0
            a += cross
            cx += (x0 + x1) * cross
            cy += (y0 + y1) * cross
        if abs(a) > best_area:
            best_area = abs(a)
            if a != 0:
                cx, cy = cx / (3 * a), cy / (3 * a)
            else:
                cx = sum(p[0] for p in ring) / len(ring)
                cy = sum(p[1] for p in ring) / len(ring)
            xs = [p[0] for p in ring]
            ys = [p[1] for p in ring]
            best = (cx, cy, max(xs) - min(xs), max(ys) - min(ys))
    if best is None:
        return None
    cx, cy, bw, bh = best
    # Rings that cross the ±180° seam produce a garbage centroid — no label.
    if bw > 500 or not (0 <= cx <= W) or not (0 <= cy <= H):
        return None
    # Fit the name inside the polygon's bbox (text width ~ 0.62 * size * chars),
    # then shrink — labels should whisper, not dominate the map.
    size = min(bw / (0.62 * max(len(name), 3)), bh * 0.55) * 0.6
    size = max(1.4, min(8.0, size))
    return round(cx, 1), round(cy, 1), round(size, 1)

def ring_to_path(ring):
    # Drop consecutive duplicate projected points to keep geometry small.
    pts = []
    for lon, lat in ring:
        p = proj(lon, lat)
        if not pts or p != pts[-1]:
            pts.append(p)
    if len(pts) < 3:
        return ""
    parts = [f"M{pts[0][0]},{pts[0][1]}"]
    parts += [f"L{x},{y}" for x, y in pts[1:]]
    parts.append("Z")
    return "".join(parts)

def main():
    here = os.path.dirname(os.path.abspath(__file__))
    src = os.path.join(here, "countries.geo.json")
    dst = os.path.join(here, "..", "Assets", "worldmap.txt")
    with open(src, encoding="utf-8") as f:
        gj = json.load(f)

    lines = []
    for feat in gj["features"]:
        iso = feat.get("id") or feat["properties"].get("name", "?")
        name = feat["properties"].get("name", iso)
        geom = feat["geometry"]
        if geom["type"] == "Polygon":
            polys = [geom["coordinates"]]
        elif geom["type"] == "MultiPolygon":
            polys = geom["coordinates"]
        else:
            continue
        path = "".join(ring_to_path(ring) for poly in polys for ring in poly)
        if not path:
            continue
        spec = label_spec(polys, name) or (0, 0, 0)  # 0 size = no label
        cx, cy, size = spec
        lines.append(f"{iso}|{name}|{cx},{cy},{size}|{path}")

    with open(dst, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    print(f"Wrote {len(lines)} countries, {os.path.getsize(dst)} bytes")

if __name__ == "__main__":
    main()
