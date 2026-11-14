"""
Generate a RhinoTrunk repository full of real Rhino geometry for testing visualization.

Run inside Rhino 8 (CPython 3 script editor, or the Rhino MCP `run_python` tool) while the
server is running (`dotnet run --project src/RhinoTrunk.Server`):

    exec(open(r"<repo>/samples/rhino_design_options.py").read())

The script builds a small pavilion site and several competing roof "options", each on its own
branch with two or more commits, then registers `preview-mesh.v1` derived meshes and a metrics
annotation on every option head. Geometry is created with RhinoCommon only in memory; set
BAKE = True to also add the latest version of each option to the open document, side by side.

Resulting repository (name printed at the end, default "pavilion-options"):

    main                  site slab, boundary, grid, survey points  ->  + context trees
    tag v0-site           first main commit
    option/a-gridshell    NURBS gridshell: skin surface, pipe members, footings  ->  crown raised + edge beams
    option/b-folded-plate planar folded plates + columns  ->  6 folds become 8 (adds, deletes, modifies)
    option/c-timber-grid  CLT panels, glulam beams, columns (Extrusions)  ->  attribute-only regrade + recolor
    agent/subd-canopy     agent-authored SubD canopy, legs, vertex-colored sun-hours mesh  ->  refined
"""

import base64
import hashlib
import json
import math
import time
import urllib.error
import urllib.request
import uuid

import System
import Rhino
import Rhino.Geometry as rg

BASE_URL = globals().get("RT_BASE_URL", "http://localhost:5280")
REPO = globals().get("RT_REPO", "pavilion-options")
BAKE = globals().get("RT_BAKE", True)
PREVIEW_RECIPE = "preview-mesh.v1"
TOL = 0.001
ATOL = math.radians(1.0)
NS = uuid.UUID("6f1c2b8e-3d4a-4e5f-9a7b-0c1d2e3f4a5b")

HUMAN = {"id": "alex", "name": "Alex (designer)", "kind": "human"}
HUMAN2 = {"id": "sam", "name": "Sam (engineer)", "kind": "human"}
AGENT = {"id": "agent:canopy-bot", "name": "Canopy bot", "kind": "agent"}


# --------------------------------------------------------------------------- HTTP

def http(method, path, body=None, raw=None, ok_conflict=False):
    url = BASE_URL.rstrip("/") + path
    data, headers = None, {}
    if raw is not None:
        data, headers["content-type"] = raw, "application/octet-stream"
    elif body is not None:
        data, headers["content-type"] = json.dumps(body).encode("utf-8"), "application/json"
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req) as resp:
            text = resp.read().decode("utf-8")
            return json.loads(text) if text else None
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        if ok_conflict and e.code == 409:
            return None
        raise RuntimeError("{} {} -> {}: {}".format(method, path, e.code, detail))


def sha(b):
    return "sha256:" + hashlib.sha256(b).hexdigest()


def upload(blobs):
    """blobs: dict hash -> bytes. Sends only what the server lacks."""
    hashes = list(blobs)
    for i in range(0, len(hashes), 1000):
        missing = http("POST", "/repos/{}/objects/missing".format(REPO), {"hashes": hashes[i:i + 1000]})["missing"]
        for h in missing:
            http("PUT", "/repos/{}/objects/{}".format(REPO, h), raw=blobs[h])


def enc(s):
    return urllib.request.quote(s, safe="")


# --------------------------------------------------------------------------- serialization

def ser_options():
    o = Rhino.FileIO.SerializationOptions()
    o.RhinoVersion = 8
    o.WriteUserData = False
    for prop in ("WriteRenderMeshes", "WriteAnalysisMeshes"):
        try:
            setattr(o, prop, False)
        except Exception:
            pass
    return o


SER = ser_options()


def geom_bytes(g):
    return g.ToJSON(SER).encode("utf-8")


def gid(*parts):
    return str(uuid.uuid5(NS, "/".join(parts)))


def hexcolor(c):
    return "#{:02x}{:02x}{:02x}".format(*c)


def preview_mesh(g):
    """preview-mesh.v1: coarse, joined render mesh; None for curves and points."""
    mp = rg.MeshingParameters.FastRenderMesh
    if isinstance(g, rg.Mesh):
        return g
    if isinstance(g, rg.Extrusion):
        g = g.ToBrep()
    if isinstance(g, rg.SubD):
        try:
            return rg.Mesh.CreateFromSubD(g, 2)
        except Exception:
            g = g.ToBrep(rg.SubDToBrepOptions.Default)
    if isinstance(g, rg.Surface):
        g = rg.Brep.CreateFromSurface(g)
    if isinstance(g, rg.Brep):
        parts = rg.Mesh.CreateFromBrep(g, mp)
        if not parts:
            return None
        m = rg.Mesh()
        for p in parts:
            m.Append(p)
        m.Normals.ComputeNormals()
        m.Compact()
        return m
    return None


# --------------------------------------------------------------------------- repo writer

class Repo(object):
    def __init__(self):
        self.heads = {}          # branch -> commit
        self.layers = {}         # key -> (id, attrs, parent key)
        self.objects = {}        # branch -> {key: (id, name, layer key, geometry, attrs)}
        self.blob_cache = {}     # geometry hash -> geometry (for previews)
        self.bake_rows = {}

    # layers -------------------------------------------------------------
    def layer_change(self, key, name, color, parent=None):
        lid = gid("layer", key)
        self.layers[key] = lid
        refs = [{"kind": "layer", "id": self.layers[parent], "role": "parent"}] if parent else []
        return {"op": "put", "kind": "layer", "id": lid,
                "manifest": {"attributes": {"name": name, "color": hexcolor(color), "visible": True,
                                            "locked": False, "userText": {}},
                             "refs": refs}}

    # objects ------------------------------------------------------------
    def object_change(self, key, name, layer_key, g, blobs, color=None, user_text=None):
        oid = gid("object", key)
        b = geom_bytes(g)
        h = sha(b)
        blobs[h] = b
        self.blob_cache[h] = g
        attrs = {"name": name, "colorSource": "object" if color else "layer",
                 "color": hexcolor(color) if color else "#000000", "materialSource": "layer",
                 "linetypeSource": "layer", "plotColorSource": "layer", "plotWeightSource": "layer",
                 "visible": True, "mode": "normal", "displayOrder": 0, "space": "model",
                 "userText": user_text or {}}
        return {"op": "put", "kind": "object", "id": oid,
                "manifest": {"subtype": type(g).__name__, "attributes": attrs,
                             "components": {"geometry": h},
                             "refs": [{"kind": "layer", "id": self.layers[layer_key], "role": "layer"}]}}

    def delete_object(self, key):
        return {"op": "delete", "kind": "object", "id": gid("object", key)}

    def note_change(self, key, title, markdown):
        return {"op": "put", "kind": "note", "id": gid("note", key),
                "manifest": {"attributes": {"title": title, "markdown": markdown}}}

    # commits ------------------------------------------------------------
    def commit(self, branch, message, author, changes, blobs, meta=None):
        upload(blobs)
        body = {"branch": branch, "expectedHead": self.heads.get(branch), "message": message,
                "author": author, "changes": changes}
        if meta:
            body["meta"] = meta
        res = http("POST", "/repos/{}/commits".format(REPO), body)
        self.heads[branch] = res["commit"]
        print("  {:<24} {}  {}".format(branch, res["commit"][7:19], message))
        return res["commit"]

    def branch_from(self, name, source_branch):
        http("PUT", "/repos/{}/refs/branches/{}".format(REPO, name),
             {"commit": self.heads[source_branch], "expected": None})
        self.heads[name] = self.heads[source_branch]

    def annotate(self, branch, namespace, summary, data, author, status="info"):
        http("POST", "/repos/{}/commits/{}/annotations".format(REPO, self.heads[branch]),
             {"namespace": namespace, "status": status, "summary": summary, "data": data, "author": author})

    def publish_previews(self):
        made = 0
        for h, g in self.blob_cache.items():
            try:
                http("GET", "/repos/{}/derived/{}/{}".format(REPO, h, PREVIEW_RECIPE))
                continue
            except RuntimeError:
                pass
            m = preview_mesh(g)
            if m is None:
                continue
            mb = geom_bytes(m)
            mh = sha(mb)
            upload({mh: mb})
            http("PUT", "/repos/{}/derived/{}/{}".format(REPO, h, PREVIEW_RECIPE), {"result": mh})
            made += 1
        return made


# --------------------------------------------------------------------------- geometry helpers

P = rg.Point3d
Z = rg.Vector3d.ZAxis


def box_brep(x0, y0, z0, x1, y1, z1):
    return rg.Box(rg.BoundingBox(P(x0, y0, z0), P(x1, y1, z1))).ToBrep()


def box_extrusion(x0, y0, z0, x1, y1, z1):
    return rg.Extrusion.CreateBoxExtrusion(rg.Box(rg.BoundingBox(P(x0, y0, z0), P(x1, y1, z1))), True)


def column(x, y, r, h, z0=0.0):
    cyl = rg.Cylinder(rg.Circle(rg.Plane(P(x, y, z0), Z), r), h)
    return rg.Extrusion.CreateCylinderExtrusion(cyl, True, True)


def pipe(curve, r):
    res = rg.Brep.CreatePipe(curve, r, False, rg.PipeCapMode.Flat, True, TOL, ATOL)
    return res[0] if res else None


def area(g):
    if isinstance(g, rg.Extrusion):
        g = g.ToBrep()
    amp = rg.AreaMassProperties.Compute(g)
    return amp.Area if amp else 0.0


# --------------------------------------------------------------------------- scene: site (main)

def build_site(repo):
    blobs, ch = {}, []
    ch.append({"op": "put", "kind": "settings", "id": "settings",
               "manifest": {"attributes": {"unitSystem": "Meters", "absoluteTolerance": TOL,
                                           "angleToleranceRadians": ATOL, "relativeTolerance": 0.01,
                                           "userText": {"project": "Pavilion options", "site": "Lot 7"}}}})
    ch.append(repo.layer_change("site", "Site", (120, 120, 120)))
    ch.append(repo.layer_change("site/ground", "Ground", (170, 160, 140), "site"))
    ch.append(repo.layer_change("site/boundary", "Boundary", (220, 50, 50), "site"))
    ch.append(repo.layer_change("site/grid", "Grid", (90, 140, 200), "site"))
    ch.append(repo.layer_change("site/survey", "Survey", (250, 170, 0), "site"))

    ch.append(repo.object_change("slab", "Ground slab", "site/ground", box_brep(-22, -16, -0.4, 22, 16, 0), blobs))
    boundary = rg.PolylineCurve([P(-21, -15, 0), P(19, -15.5, 0), P(21.5, 9, 0), P(12, 15.5, 0),
                                 P(-20, 14.5, 0), P(-21, -15, 0)])
    ch.append(repo.object_change("boundary", "Lot boundary", "site/boundary", boundary, blobs))
    for i, x in enumerate(range(-12, 13, 6)):
        ch.append(repo.object_change("grid/x%d" % i, "Grid %d" % (i + 1), "site/grid",
                                     rg.LineCurve(P(x, -11, 0.01), P(x, 11, 0.01)), blobs))
    for j, y in enumerate(range(-8, 9, 4)):
        ch.append(repo.object_change("grid/y%d" % j, "Grid %s" % "ABCDE"[j], "site/grid",
                                     rg.LineCurve(P(-15, y, 0.01), P(15, y, 0.01)), blobs))
    for k, pt in enumerate([P(-21, -15, 0), P(19, -15.5, 0), P(21.5, 9, 0), P(12, 15.5, 0), P(-20, 14.5, 0)]):
        ch.append(repo.object_change("survey/%d" % k, "SP-%02d" % (k + 1), "site/survey", rg.Point(pt), blobs,
                                     user_text={"survey.elevation": "%.3f" % (0.0 + 0.05 * k)}))
    ch.append(repo.note_change("brief", "Design brief",
                               "# Pavilion brief\n\n- Covered area ~ 24 x 16 m on grids 1-5 / A-E\n"
                               "- Max height 8 m\n- Compare structural options on separate branches."))
    repo.commit("main", "Site, grid and survey", HUMAN, ch, blobs)


def add_trees(repo):
    blobs, ch = {}, [repo.layer_change("site/trees", "Trees", (60, 150, 70), "site")]
    for i, (x, y, r) in enumerate([(-18, -12, 2.2), (-18, 11, 2.6), (17, -12, 2.0), (18, 6, 2.4), (6, 13, 1.8)]):
        trunk = rg.Cylinder(rg.Circle(rg.Plane(P(x, y, 0), Z), 0.25), 3.0).ToBrep(True, True)
        crown = rg.Sphere(P(x, y, 3.0 + r * 0.8), r).ToBrep()
        ch.append(repo.object_change("tree/%d/trunk" % i, "Tree %d trunk" % (i + 1), "site/trees", trunk, blobs,
                                     color=(110, 80, 50)))
        ch.append(repo.object_change("tree/%d/crown" % i, "Tree %d crown" % (i + 1), "site/trees", crown, blobs))
    repo.commit("main", "Add existing trees from survey", HUMAN, ch, blobs)


# --------------------------------------------------------------------------- option A: gridshell

def gridshell_surface(crown):
    pts = []
    nu, nv, a, b = 7, 5, 13.0, 9.0
    for i in range(nu):
        for j in range(nv):
            x = -a + 2 * a * i / (nu - 1)
            y = -b + 2 * b * j / (nv - 1)
            z = crown * (1 - (x / a) ** 2) * (1 - (y / b) ** 2) + 0.6 * (abs(x) / a) * (abs(y) / b)
            pts.append(P(x, y, z))
    return rg.NurbsSurface.CreateFromPoints(pts, nu, nv, 3, 3)


def build_option_a(repo, crown, with_edge_beams):
    blobs, ch = {}, []
    br = "option/a-gridshell"
    if br not in repo.objects:
        ch.append(repo.layer_change("A", "Option A - Gridshell", (200, 80, 40)))
        ch.append(repo.layer_change("A/skin", "Skin", (230, 200, 160), "A"))
        ch.append(repo.layer_change("A/members", "Members", (140, 70, 30), "A"))
        ch.append(repo.layer_change("A/supports", "Supports", (110, 110, 110), "A"))
    repo.objects[br] = True
    srf = gridshell_surface(crown)
    skin = rg.Brep.CreateFromSurface(srf)
    ch.append(repo.object_change("A/skin", "Gridshell skin", "A/skin", skin, blobs, user_text={"element": "skin"}))
    dom_u, dom_v = srf.Domain(0), srf.Domain(1)
    total_len, n = 0.0, 0
    for i in range(11):
        v = dom_v.ParameterAt(i / 10.0)
        c = srf.IsoCurve(0, v)
        total_len += c.GetLength()
        ch.append(repo.object_change("A/member/u%d" % i, "Lath U%02d" % i, "A/members", pipe(c, 0.07), blobs,
                                     user_text={"element": "lath", "section": "GL 140x70"}))
        n += 1
    for i in range(15):
        u = dom_u.ParameterAt(i / 14.0)
        c = srf.IsoCurve(1, u)
        total_len += c.GetLength()
        ch.append(repo.object_change("A/member/v%d" % i, "Lath V%02d" % i, "A/members", pipe(c, 0.07), blobs,
                                     user_text={"element": "lath", "section": "GL 140x70"}))
        n += 1
    corners = [(-13, -9), (13, -9), (13, 9), (-13, 9)]
    for k, (x, y) in enumerate(corners):
        ch.append(repo.object_change("A/footing/%d" % k, "Footing %d" % (k + 1), "A/supports",
                                     box_extrusion(x - 1, y - 1, -1.2, x + 1, y + 1, 0.2), blobs,
                                     user_text={"element": "footing"}))
    if with_edge_beams:
        for k, (side, param) in enumerate([(0, dom_v.Min), (0, dom_v.Max), (1, dom_u.Min), (1, dom_u.Max)]):
            c = srf.IsoCurve(side, param)
            total_len += c.GetLength()
            ch.append(repo.object_change("A/edge/%d" % k, "Edge beam %d" % (k + 1), "A/members", pipe(c, 0.16), blobs,
                                         color=(90, 40, 20), user_text={"element": "edge beam", "section": "GL 320x200"}))
    metrics = {"roofArea_m2": round(area(skin), 1), "crownHeight_m": crown, "memberCount": n + (4 if with_edge_beams else 0),
               "memberLength_m": round(total_len, 1), "supports": 4}
    return ch, blobs, metrics


# --------------------------------------------------------------------------- option B: folded plate

def build_option_b(repo, folds, previous_folds=0):
    blobs, ch = {}, []
    if previous_folds == 0:
        ch.append(repo.layer_change("B", "Option B - Folded plate", (40, 110, 190)))
        ch.append(repo.layer_change("B/plates", "Plates", (150, 190, 230), "B"))
        ch.append(repo.layer_change("B/columns", "Columns", (60, 60, 70), "B"))
    x0, x1, y0, y1 = -12.0, 12.0, -8.0, 8.0
    lo, hi = 4.2, 6.2
    xs = [x0 + (x1 - x0) * i / folds for i in range(folds + 1)]
    total_area = 0.0
    for i in range(folds):
        xa, xb = xs[i], xs[i + 1]
        xm = 0.5 * (xa + xb)
        rise = 0.6 * math.sin(math.pi * (i + 0.5) / folds)
        for half, (pa, pb) in enumerate([((xa, lo), (xm, hi + rise)), ((xm, hi + rise), (xb, lo))]):
            (u0, z0), (u1, z1) = pa, pb
            plate = rg.Brep.CreateFromCornerPoints(P(u0, y0, z0), P(u1, y0, z1), P(u1, y1, z1), P(u0, y1, z0), TOL)
            total_area += area(plate)
            shade = (150, 190, 230) if half == 0 else (110, 160, 215)
            ch.append(repo.object_change("B/plate/%d" % (2 * i + half), "Plate %02d" % (2 * i + half + 1), "B/plates",
                                         plate, blobs, color=shade, user_text={"element": "plate", "thickness": "CLT 160"}))
    for i in range(folds, previous_folds):
        ch.append(repo.delete_object("B/plate/%d" % (2 * i)))
        ch.append(repo.delete_object("B/plate/%d" % (2 * i + 1)))
    cols = 0
    for i, x in enumerate(xs):
        for side, y in enumerate((y0, y1)):
            ch.append(repo.object_change("B/col/%d/%d" % (i, side), "Column %d%s" % (i + 1, "AB"[side]), "B/columns",
                                         column(x, y, 0.18, lo), blobs, user_text={"element": "column", "section": "CHS 356"}))
            cols += 1
    for i in range(folds + 1, previous_folds + 1):
        for side in (0, 1):
            ch.append(repo.delete_object("B/col/%d/%d" % (i, side)))
    metrics = {"roofArea_m2": round(total_area, 1), "folds": folds, "plateCount": 2 * folds, "columns": cols}
    return ch, blobs, metrics


# --------------------------------------------------------------------------- option C: timber grid

def build_option_c(repo, grade, panel_color, first):
    blobs, ch = {}, []
    if first:
        ch.append(repo.layer_change("C", "Option C - Timber grid", (60, 150, 80)))
        ch.append(repo.layer_change("C/panels", "CLT panels", (225, 190, 130), "C"))
        ch.append(repo.layer_change("C/beams", "Glulam beams", (190, 140, 80), "C"))
        ch.append(repo.layer_change("C/columns", "Columns", (160, 110, 60), "C"))
    xs, ys, h = [-12, -6, 0, 6, 12], [-8, 0, 8], 4.5
    for i, x in enumerate(xs):
        for j, y in enumerate(ys):
            ch.append(repo.object_change("C/col/%d/%d" % (i, j), "Column %d%s" % (i + 1, "ABC"[j]), "C/columns",
                                         box_extrusion(x - 0.16, y - 0.16, 0, x + 0.16, y + 0.16, h), blobs,
                                         user_text={"element": "column", "grade": grade, "section": "320x320"}))
    for j, y in enumerate(ys):
        ch.append(repo.object_change("C/beam/%d" % j, "Beam %s" % "ABC"[j], "C/beams",
                                     box_extrusion(-12.5, y - 0.12, h, 12.5, y + 0.12, h + 0.8), blobs,
                                     user_text={"element": "beam", "grade": grade, "section": "240x800"}))
    n = 10
    for k in range(n):
        xa = -12.5 + 25.0 * k / n
        ch.append(repo.object_change("C/panel/%d" % k, "CLT %02d" % (k + 1), "C/panels",
                                     box_extrusion(xa + 0.01, -8.6, h + 0.8, xa + 2.5 - 0.01, 8.6, h + 1.0), blobs,
                                     color=panel_color, user_text={"element": "panel", "grade": grade, "layup": "5s-200"}))
    metrics = {"roofArea_m2": round(25.0 * 17.2, 1), "columns": len(xs) * len(ys), "beams": len(ys),
               "panels": n, "grade": grade}
    return ch, blobs, metrics


# --------------------------------------------------------------------------- option D: agent SubD canopy

def canopy_mesh(lift, nx=8, ny=6):
    m = rg.Mesh()
    a, b = 12.0, 8.0
    for j in range(ny + 1):
        for i in range(nx + 1):
            x = -a + 2 * a * i / nx
            y = -b + 2 * b * j / ny
            r = math.sqrt((x / a) ** 2 + (y / b) ** 2)
            z = 4.0 + lift * math.cos(min(r, 1.0) * math.pi / 2) + 0.8 * math.sin(x / 3.0)
            m.Vertices.Add(x, y, z)
    for j in range(ny):
        for i in range(nx):
            v = j * (nx + 1) + i
            m.Faces.AddFace(v, v + 1, v + nx + 2, v + nx + 1)
    m.Normals.ComputeNormals()
    return m


def sun_hours_mesh(canopy_lift):
    m = rg.Mesh()
    nx, ny, a, b = 44, 32, 22.0, 16.0
    for j in range(ny + 1):
        for i in range(nx + 1):
            x = -a + 2 * a * i / nx
            y = -b + 2 * b * j / ny
            m.Vertices.Add(x, y, 0.02)
            shade = math.exp(-((x + 2) / 11.0) ** 2 - ((y - 2) / (6.0 + canopy_lift)) ** 2)
            t = max(0.0, min(1.0, 1.0 - shade))
            m.VertexColors.Add(System.Drawing.Color.FromArgb(int(40 + 215 * t), int(60 + 150 * t * (1 - t) * 4 * 0.6), int(200 * (1 - t))))
    for j in range(ny):
        for i in range(nx):
            v = j * (nx + 1) + i
            m.Faces.AddFace(v, v + 1, v + nx + 2, v + nx + 1)
    m.Normals.ComputeNormals()
    return m


def build_option_d(repo, lift, legs, first):
    blobs, ch = {}, []
    if first:
        ch.append(repo.layer_change("D", "Option D - SubD canopy (agent)", (150, 70, 180)))
        ch.append(repo.layer_change("D/canopy", "Canopy", (210, 180, 230), "D"))
        ch.append(repo.layer_change("D/legs", "Legs", (90, 60, 110), "D"))
        ch.append(repo.layer_change("D/analysis", "Sun hours", (255, 200, 0), "D"))
    subd = rg.SubD.CreateFromMesh(canopy_mesh(lift))
    ch.append(repo.object_change("D/canopy", "Canopy", "D/canopy", subd, blobs, user_text={"element": "canopy"}))
    for k, (x, y) in enumerate(legs):
        top = P(x * 0.8, y * 0.8, 4.2 + lift * 0.5)
        crv = rg.NurbsCurve.Create(False, 3, [P(x, y, 0), P(x * 1.05, y * 1.05, 1.5), P(x * 0.95, y * 0.95, 3.0), top])
        ch.append(repo.object_change("D/leg/%d" % k, "Leg %d" % (k + 1), "D/legs", pipe(crv, 0.22), blobs,
                                     user_text={"element": "leg"}))
        ch.append(repo.object_change("D/leg/%d/axis" % k, "Leg %d axis" % (k + 1), "D/legs", crv, blobs,
                                     color=(255, 0, 255), user_text={"element": "axis"}))
    ch.append(repo.object_change("D/sun", "Sun hours (Jun 21)", "D/analysis", sun_hours_mesh(lift), blobs,
                                 user_text={"analysis": "sun-hours", "date": "2026-06-21"}))
    metrics = {"canopyLift_m": lift, "legs": len(legs)}
    return ch, blobs, metrics


# --------------------------------------------------------------------------- bake (optional)

def bake(repo):
    doc = globals().get("__rhino_doc__") or Rhino.RhinoDoc.ActiveDoc
    entities = {}
    for k, br in enumerate(["main", "option/a-gridshell", "option/b-folded-plate", "option/c-timber-grid", "agent/subd-canopy"]):
        listing = http("GET", "/repos/{}/commits/{}/entities?kind=object&manifests=true".format(REPO, enc(br)))
        lname = "RhinoTrunk::" + br.replace("/", "-")
        idx = doc.Layers.FindByFullPath(lname, -1)
        if idx < 0:
            root = doc.Layers.FindByFullPath("RhinoTrunk", -1)
            if root < 0:
                root = doc.Layers.Add("RhinoTrunk", System.Drawing.Color.Black)
            lay = Rhino.DocObjects.Layer()
            lay.Name = br.replace("/", "-")
            lay.ParentLayerId = doc.Layers[root].Id
            idx = doc.Layers.Add(lay)
        shift = rg.Transform.Translation(0, -40.0 * k, 0)
        hashes = [e["manifest"]["components"]["geometry"] for e in listing["entities"]]
        objs = {}
        for i in range(0, len(hashes), 500):
            objs.update(http("POST", "/repos/{}/objects/batch-get".format(REPO), {"hashes": hashes[i:i + 500]})["objects"])
        for e in listing["entities"]:
            g = Rhino.Runtime.CommonObject.FromJSON(base64.b64decode(objs[e["manifest"]["components"]["geometry"]]).decode("utf-8"))
            g.Transform(shift)
            attrs = Rhino.DocObjects.ObjectAttributes()
            attrs.LayerIndex = idx
            doc.Objects.Add(g, attrs)
        entities[br] = len(listing["entities"])
    doc.Views.Redraw()
    return entities


# --------------------------------------------------------------------------- main

def run():
    global REPO
    if http("POST", "/repos", {"name": REPO}, ok_conflict=True) is None:
        REPO = "{}-{}".format(REPO, int(time.time()))
        http("POST", "/repos", {"name": REPO})
    print("repository", REPO)
    repo = Repo()

    build_site(repo)
    http("PUT", "/repos/{}/refs/tags/v0-site".format(REPO), {"commit": repo.heads["main"]})
    for br in ["option/a-gridshell", "option/b-folded-plate", "option/c-timber-grid", "agent/subd-canopy"]:
        repo.branch_from(br, "main")
    add_trees(repo)

    br = "option/a-gridshell"
    ch, bl, m = build_option_a(repo, 6.0, False)
    repo.commit(br, "Gridshell massing, crown 6.0 m", HUMAN, ch, bl)
    ch, bl, m = build_option_a(repo, 7.5, True)
    repo.commit(br, "Raise crown to 7.5 m, add glulam edge beams", HUMAN2, ch, bl)
    repo.annotate(br, "metrics/summary", "Gridshell: %(roofArea_m2)s m2, %(memberCount)s members" % m, m, HUMAN2)
    repo.annotate(br, "ci/geometry-valid", "All breps valid", {"checked": m["memberCount"] + 5}, HUMAN2, "success")

    br = "option/b-folded-plate"
    ch, bl, m = build_option_b(repo, 6)
    repo.commit(br, "Folded plate, 6 folds", HUMAN, ch, bl)
    ch, bl, m = build_option_b(repo, 8, previous_folds=6)
    repo.commit(br, "Increase to 8 folds for shorter spans", HUMAN2, ch, bl)
    repo.annotate(br, "metrics/summary", "Folded plate: %(roofArea_m2)s m2, %(plateCount)s plates" % m, m, HUMAN2)

    br = "option/c-timber-grid"
    ch, bl, m = build_option_c(repo, "GL24h", (225, 190, 130), True)
    repo.commit(br, "Timber post-and-beam grid with CLT deck", HUMAN, ch, bl)
    ch, bl, m = build_option_c(repo, "GL28h", (200, 150, 90), False)
    repo.commit(br, "Upgrade to GL28h, darker stain on panels (attributes only)", HUMAN2, ch, bl)
    repo.annotate(br, "metrics/summary", "Timber grid: %(columns)s columns, %(panels)s CLT panels" % m, m, HUMAN2)
    repo.annotate(br, "ci/geometry-valid", "Column C3 clashes with tree 5 crown", {"clashes": 1}, HUMAN2, "failure")

    br = "agent/subd-canopy"
    meta = {"agent.model": "claude-opus-5-5", "agent.tool": "rhino_design_options.py"}
    legs3 = [(-9, -5), (9, -5), (0, 7)]
    ch, bl, m = build_option_d(repo, 2.0, legs3, True)
    repo.commit(br, "Agent: SubD canopy on 3 legs + sun-hours study", AGENT, ch, bl, meta)
    legs4 = [(-9, -5), (9, -5), (8, 6), (-8, 6)]
    ch, bl, m = build_option_d(repo, 3.0, legs4, False)
    ch.append(repo.note_change("D/memo", "Agent memo",
                               "Raised canopy by 1 m and added a fourth leg; shade footprint moved north."))
    repo.commit(br, "Agent: lift canopy, 4 legs, rerun sun hours", AGENT, ch, bl, meta)
    repo.annotate(br, "metrics/summary", "SubD canopy, %(legs)s legs" % m, m, AGENT)

    n = repo.publish_previews()
    print("preview meshes registered:", n)
    if BAKE:
        print("baked into document:", bake(repo))
    print("done:", BASE_URL + "/repos/" + REPO + "/graph")
    return REPO


run()
