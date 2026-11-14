# Lays a brick wall that tiles every PERIOD metres from the whole bricks of a scan, then bakes its occlusion in Cycles.
#
#   blender -b --factory-startup --python wall.py -- --scan DIR --bricks bricks.json --mortar DIR --out DIR
#       [--size 4096] [--period 2.7] [--scan-metres 1.3] [--seed 7]
#
# tools/textures runs it for a material of textures.json with "laid" (main.go's lay), after bricks.go has found the
# scan's whole bricks. It writes albedo.png, normal.png, arm.png and height.png, as Poly Haven's maps, and wall.json.
#
# Each slot of a stretcher bond (bricks 225 x 75 mm with their joint) takes one of the scan's bricks, chosen at random
# (never the same as its neighbour to the left or the two below), flipped or not, slightly resized, turned, set forward
# or back and leant. The bricks keep their scanned outline: each is cut with a margin of the scan's own mortar, its
# height is set in metres, and a height test against the mortar bed and the bricks already laid decides what shows,
# so chipped corners and worn edges stay as photographed. The bed is cast concrete tinted to the scan's mortar.
# Everything wraps at the period, so the tile has no seam. Normals come from the laid height; the occlusion is baked
# by Cycles from the height laid as a mesh, with an eighth of the next periods around so the edges see their neighbours.
import argparse
import json
import math
import os
import sys

import bpy
import numpy as np


def args():
    p = argparse.ArgumentParser()
    p.add_argument("--scan", required=True)
    p.add_argument("--bricks", required=True)
    p.add_argument("--mortar", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--size", type=int, default=4096)
    p.add_argument("--period", type=float, default=2.7)
    p.add_argument("--scan-metres", type=float, default=1.3)
    p.add_argument("--seed", type=int, default=7)
    p.add_argument("--ao-grid", type=int, default=1024)
    return p.parse_args(sys.argv[sys.argv.index("--") + 1:])


def load(path, channels):
    img = bpy.data.images.load(path)
    img.colorspace_settings.name = "Non-Color"
    w, h = img.size
    px = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4)[::-1, :, :channels].copy()  # rows top-down


def save(path, rgb):
    h, w = rgb.shape[:2]
    rgba = np.ones((h, w, 4), dtype=np.float32)
    rgba[:, :, :3] = np.clip(rgb, 0, 1)
    img = bpy.data.images.new(os.path.basename(path), w, h, alpha=False, float_buffer=False)
    img.colorspace_settings.name = "Non-Color"
    img.pixels.foreach_set(rgba[::-1].ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)


def bilinear(src, sx, sy):
    """Samples src (h, w, c) at float pixel coordinates, clamped to the image."""
    h, w = src.shape[:2]
    sx = np.clip(sx, 0, w - 1.001)
    sy = np.clip(sy, 0, h - 1.001)
    x0 = sx.astype(np.int32)
    y0 = sy.astype(np.int32)
    fx = (sx - x0)[..., None]
    fy = (sy - y0)[..., None]
    a = src[y0, x0] * (1 - fx) + src[y0, x0 + 1] * fx
    b = src[y0 + 1, x0] * (1 - fx) + src[y0 + 1, x0 + 1] * fx
    return a * (1 - fy) + b * fy


def main():
    o = args()
    rng = np.random.default_rng(o.seed)
    N, P = o.size, o.period
    m = P / N  # metres per output pixel

    albedo = load(os.path.join(o.scan, "albedo.jpg"), 3)
    height = load(os.path.join(o.scan, "height.png"), 1)[:, :, 0]
    arm = load(os.path.join(o.scan, "arm.png"), 3)
    S = albedo.shape[0]
    sm = o.scan_metres / S  # metres per scan pixel
    spec = json.load(open(o.bricks))
    bricks = spec["bricks"]
    thr = spec["threshold"]

    # The scan's height in metres: its bricks' mean face 12 mm before its mortar's mean.
    mortar_mask = height < thr - 0.05
    hb = np.mean([height[int(b["Y0"] * S):int(b["Y1"] * S), int(b["X0"] * S):int(b["X1"] * S)].mean() for b in bricks])
    hm = height[mortar_mask].mean()
    scale = 0.012 / (hb - hm)
    mortar_colour = albedo[mortar_mask].mean(axis=0)
    mortar_rough = arm[mortar_mask][:, 1].mean()
    print(f"WALL scan {S}px, {len(bricks)} bricks, brick {hb:.3f} mortar {hm:.3f} -> {scale*1000:.1f} mm per unit, "
          f"mortar colour {mortar_colour.round(3).tolist()}")

    # The bed: cast concrete, its grain kept and its colour brought to the scan's mortar, 10 mm behind the faces.
    conc = load(os.path.join(o.mortar, "albedo.jpg"), 3)
    conc_h = load(os.path.join(o.mortar, "height.png"), 1)[:, :, 0]
    C = conc.shape[0]
    reps = max(1, round(P / 1.35))  # the concrete tiles a whole number of times in the period
    yy, xx = np.mgrid[0:N, 0:N].astype(np.float32)
    cx = (xx * reps * C / N) % C
    cy = (yy * reps * C / N) % C
    grain = bilinear(conc, cx, cy)
    lum = grain.mean(axis=2, keepdims=True)
    lum = lum / lum.mean()
    # A coarser weathering over the whole period: the concrete once across it, its luminance smoothed by 32 px.
    wx = (xx * C / N) % C
    wy = (yy * C / N) % C
    coarse = bilinear(conc, wx, wy).mean(axis=2)
    for _ in range(5):
        for ax in (0, 1):
            coarse = (np.roll(coarse, 16, ax) + coarse + np.roll(coarse, -16, ax)) / 3
    coarse = np.clip(coarse / coarse.mean(), 0.75, 1.2)[:, :, None]
    out_rgb = mortar_colour * np.clip(1 + 1.1 * (lum - 1), 0.6, 1.4) * (1 + 0.6 * (coarse - 1))
    out_rgb = np.clip(out_rgb, 0, 1).astype(np.float32)
    del wx, wy, coarse
    ch = bilinear(conc_h[:, :, None], cx, cy)[:, :, 0]
    out_h = (-0.010 + (ch - ch.mean()) * 0.002).astype(np.float32)
    out_rough = np.full((N, N), mortar_rough, dtype=np.float32)
    out_ao = np.ones((N, N), dtype=np.float32)
    del grain, lum, ch, cx, cy

    # The bond.
    pitch_x, pitch_y = 0.225, 0.075
    cols, rows = round(P / pitch_x), round(P / pitch_y)
    if abs(cols * pitch_x - P) > 1e-6 or abs(rows * pitch_y - P) > 1e-6 or rows % 2:
        raise SystemExit(f"a period of {P} m does not hold whole bricks of 225 x 75 mm in an even number of courses")
    choice = -np.ones((rows, cols), dtype=np.int32)
    margin = 10  # scan pixels of the scan's mortar kept around each brick
    laid = 0
    for j in range(rows):
        for i in range(cols):
            near = {choice[j, (i - 1) % cols], choice[(j - 1) % rows, i], choice[(j - 1) % rows, (i + 1) % cols],
                    choice[(j - 1) % rows, (i - 1) % cols]}
            k = int(rng.integers(len(bricks)))
            while k in near:
                k = int(rng.integers(len(bricks)))
            choice[j, i] = k
            b = bricks[k]
            # The brick in the scan, in scan pixels, and its size in the wall.
            bx0, by0, bx1, by1 = b["X0"] * S, b["Y0"] * S, b["X1"] * S, b["Y1"] * S
            bw, bh = bx1 - bx0, by1 - by0
            length = 0.215 + rng.uniform(-0.003, 0.003)
            tall = 0.065 + rng.uniform(-0.002, 0.002)
            sx_per_m = bw / length  # scan pixels per wall metre, across and up
            sy_per_m = bh / tall
            cxm = (i + 0.5 * (j % 2) + 0.5) * pitch_x + rng.uniform(-0.002, 0.002)
            cym = (j + 0.5) * pitch_y + rng.uniform(-0.001, 0.001)
            angle = math.radians(rng.uniform(-0.5, 0.5))
            flip_x = rng.random() < 0.5
            flip_y = rng.random() < 0.25
            forward = rng.uniform(-0.0015, 0.0015)
            lean = rng.uniform(-0.001, 0.001)  # metres across the brick's length
            # The output pixels it may cover (wrapping), and where each falls in the scan.
            half_w = length / 2 + (margin / sx_per_m)
            half_h = tall / 2 + (margin / sy_per_m)
            reach = math.hypot(half_w, half_h)
            px0 = int(math.floor((cxm - reach) / m))
            py0 = int(math.floor((cym - reach) / m))
            n = int(math.ceil(2 * reach / m)) + 2
            gy, gx = np.mgrid[py0:py0 + n, px0:px0 + n].astype(np.float32)
            dx = (gx + 0.5) * m - cxm
            dy = (gy + 0.5) * m - cym
            ca, sa = math.cos(angle), math.sin(angle)
            u = ca * dx + sa * dy  # along the brick, metres from its centre
            v = -sa * dx + ca * dy
            inside = (np.abs(u) <= half_w) & (np.abs(v) <= half_h)
            if flip_x:
                u = -u
            if flip_y:
                v = -v
            sx = (bx0 + bx1) / 2 + u * sx_per_m
            sy = (by0 + by1) / 2 + v * sy_per_m
            hs = bilinear(height[:, :, None], sx, sy)[:, :, 0]
            z = (hs - hb) * scale + forward + lean * (u / length)
            ry = gy.astype(np.int64) % N
            rx = gx.astype(np.int64) % N
            # Only the brick itself: what the scan shows of its mortar is left to the bed.
            win = inside & (hs > thr) & (z > out_h[ry, rx])
            if not win.any():
                continue
            ry, rx = ry[win], rx[win]
            out_h[ry, rx] = z[win]
            out_rgb[ry, rx] = bilinear(albedo, sx[win], sy[win])
            a = bilinear(arm, sx[win], sy[win])
            out_ao[ry, rx] = a[:, 0]
            out_rough[ry, rx] = a[:, 1]
            laid += 1
    print(f"WALL laid {laid} bricks in {cols} x {rows}, period {P} m, {N} px ({N / P:.0f} px/m)")

    # Normals from the laid height (OpenGL: green up), the height first softened by one pixel.
    hsoft = (out_h + np.roll(out_h, 1, 0) + np.roll(out_h, -1, 0) + np.roll(out_h, 1, 1) + np.roll(out_h, -1, 1)) / 5
    dzdx = (np.roll(hsoft, -1, 1) - np.roll(hsoft, 1, 1)) / (2 * m)
    dzdy = (np.roll(hsoft, 1, 0) - np.roll(hsoft, -1, 0)) / (2 * m)  # rows go down; y goes up
    nrm = np.stack([-dzdx, -dzdy, np.ones_like(dzdx)], axis=2)
    nrm /= np.linalg.norm(nrm, axis=2, keepdims=True)
    del hsoft, dzdx, dzdy

    # Occlusion baked in Cycles: the height as a mesh three periods wide, a plane over the middle period.
    G = o.ao_grid
    f = N // G
    hg = out_h.reshape(G, f, G, f).mean(axis=(1, 3))
    pad = G // 8  # the neighbours' edge each side, wider than the occlusion's reach
    T = G + 2 * pad
    big = np.tile(hg, (3, 3))[G - pad:2 * G + pad, G - pad:2 * G + pad]
    ys, xs = np.mgrid[0:T, 0:T]
    verts = np.stack([(xs - pad + 0.5) * (P / G), P - (ys - pad + 0.5) * (P / G), big], axis=2).reshape(-1, 3)
    a = (ys[:-1, :-1] * T + xs[:-1, :-1]).ravel()
    quads = np.stack([a, a + T, a + T + 1, a + 1], axis=1)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    mesh = bpy.data.meshes.new("wall")
    mesh.vertices.add(len(verts))
    mesh.vertices.foreach_set("co", verts.astype(np.float32).ravel())
    mesh.loops.add(len(quads) * 4)
    mesh.loops.foreach_set("vertex_index", quads.astype(np.int32).ravel())
    mesh.polygons.add(len(quads))
    mesh.polygons.foreach_set("loop_start", (np.arange(len(quads)) * 4).astype(np.int32))
    mesh.polygons.foreach_set("loop_total", np.full(len(quads), 4, dtype=np.int32))
    mesh.update()
    mesh.validate()
    wall = bpy.data.objects.new("wall", mesh)
    plane_mesh = bpy.data.meshes.new("target")
    plane_mesh.from_pydata([(0, 0, 0.03), (P, 0, 0.03), (P, P, 0.03), (0, P, 0.03)], [], [(0, 1, 2, 3)])
    uv = plane_mesh.uv_layers.new()
    for li, (uu, vv) in enumerate([(0, 0), (1, 0), (1, 1), (0, 1)]):
        uv.data[li].uv = (uu, vv)
    plane = bpy.data.objects.new("target", plane_mesh)
    scene = bpy.data.scenes.new("bake") if not bpy.data.scenes else bpy.data.scenes[0]
    scene.collection.objects.link(wall)
    scene.collection.objects.link(plane)
    world = bpy.data.worlds.new("world")
    scene.world = world
    world.light_settings.distance = 0.06
    ao_img = bpy.data.images.new("ao", G * 2, G * 2, alpha=False, float_buffer=True)
    mat = bpy.data.materials.new("target")
    mat.use_nodes = True
    node = mat.node_tree.nodes.new("ShaderNodeTexImage")
    node.image = ao_img
    mat.node_tree.nodes.active = node
    plane_mesh.materials.append(mat)
    wall_mat = bpy.data.materials.new("wall")
    mesh.materials.append(wall_mat)
    scene.render.engine = "CYCLES"
    prefs = bpy.context.preferences.addons["cycles"].preferences
    for kind in ("OPTIX", "CUDA"):
        try:
            prefs.compute_device_type = kind
            prefs.get_devices()
            if any(d.type == kind for d in prefs.devices):
                for d in prefs.devices:
                    d.use = d.type == kind
                scene.cycles.device = "GPU"
                print(f"WALL baking on {kind}")
                break
        except TypeError:
            continue
    else:
        print("WALL baking on the CPU: no OptiX or CUDA device")
    scene.cycles.samples = 128
    for ob in scene.objects:
        ob.select_set(False)
    view_layer = scene.view_layers[0]
    wall.select_set(True)
    plane.select_set(True)
    view_layer.objects.active = plane
    bpy.ops.object.bake(type="AO", use_selected_to_active=True, cage_extrusion=0.0, max_ray_distance=0.1,
                        margin=0, use_clear=True)
    aopx = np.empty(ao_img.size[0] * ao_img.size[1] * 4, dtype=np.float32)
    ao_img.pixels.foreach_get(aopx)
    ao = aopx.reshape(G * 2, G * 2, 4)[::-1, :, 0]
    ao = np.repeat(np.repeat(ao, N // (G * 2), 0), N // (G * 2), 1)
    print(f"WALL baked occlusion: mean {ao.mean():.3f}, min {ao.min():.3f}")

    os.makedirs(o.out, exist_ok=True)
    save(os.path.join(o.out, "albedo.png"), out_rgb)
    save(os.path.join(o.out, "normal.png"), nrm * 0.5 + 0.5)
    hn = (out_h - out_h.min()) / max(out_h.max() - out_h.min(), 1e-6)
    save(os.path.join(o.out, "height.png"), np.repeat(hn[:, :, None], 3, axis=2))
    save(os.path.join(o.out, "arm.png"), np.stack([out_ao * ao, out_rough, np.zeros_like(out_rough)], axis=2))
    json.dump({"period": P, "size": N, "depth": float(out_h.max() - out_h.min()), "seed": o.seed, "bricks": len(bricks),
               "laid": laid, "ao_mean": float(ao.mean())}, open(os.path.join(o.out, "wall.json"), "w"), indent=1)
    print(f"WALL wrote {o.out}: relief {1000 * (out_h.max() - out_h.min()):.1f} mm")


main()
