#! python3
"""
TrunkOptions: preview design options from RhinoTrunk in a display conduit, import only on Accept.

While previewing, nothing is added to the document (no objects, no layers). Each option is drawn
from the engine's `preview-mesh.v1` derived meshes (curves and points are drawn from their light
geometry). Switching options swaps what the conduit draws and redraws the viewport. Only when you
Accept an option does the tool fetch the real geometry and import it, with its layers.

What is previewed: the option's changes since its common ancestor with the document's base
revision (`rhinotrunk.base` in the document strings). For an empty/untracked document that is the
whole option snapshot. Objects the option modifies or deletes are hidden by the conduit (culled),
not touched. Accept applies exactly those changes (add / replace / delete) to the document.

Rhino command (alias):  TrunkOptions = ! _-RunPythonScript "<repo>\\samples\\rhino_option_preview.py"
    A B C D   preview that option        Enter   next option
    Accept    import the option shown     Bench   time 12 switches
    Esc       stop previewing, document unchanged

Agent use (Rhino MCP run_python):
    RT_ACTION = "preview:B"   # conduit on, shows B, returns (stays on until "off"/"accept")
    RT_ACTION = "accept"      # import the option currently previewed
    RT_ACTION = "off"         # conduit off, document unchanged
    RT_ACTION = "bench"
    exec(open(r"<repo>/samples/rhino_option_preview.py", encoding="utf-8").read(), globals())
"""

import base64
import json
import time
import urllib.error
import urllib.request

import System
import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

BASE_URL = globals().get("RT_BASE_URL", "http://localhost:5280")
REPO = globals().get("RT_REPO", "pavilion-options")
ACTION = globals().get("RT_ACTION", "interactive")
RECIPE = "preview-mesh.v1"
OPTIONS = [("A", "option/a-gridshell", "Gridshell"),
           ("B", "option/b-folded-plate", "Folded plate"),
           ("C", "option/c-timber-grid", "Timber grid"),
           ("D", "agent/subd-canopy", "SubD canopy (agent)")]
STICKY = "rhinotrunk.preview"


def doc():
    return globals().get("__rhino_doc__") or Rhino.RhinoDoc.ActiveDoc


def ms(t0):
    return (time.perf_counter() - t0) * 1000.0


# --------------------------------------------------------------------------- engine

def get(path):
    with urllib.request.urlopen(BASE_URL + path) as r:
        return json.loads(r.read().decode("utf-8"))


def post(path, body):
    req = urllib.request.Request(BASE_URL + path, data=json.dumps(body).encode("utf-8"),
                                 headers={"content-type": "application/json"}, method="POST")
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read().decode("utf-8"))


def q(s):
    return urllib.request.quote(s, safe="")


def batch_get(hashes):
    out, hashes = {}, [h for h in dict.fromkeys(hashes) if h]
    for i in range(0, len(hashes), 1000):
        for h, b64 in post("/repos/{}/objects/batch-get".format(REPO), {"hashes": hashes[i:i + 1000]})["objects"].items():
            out[h] = base64.b64decode(b64)
    return out


def derived(h):
    try:
        return get("/repos/{}/derived/{}/{}".format(REPO, h, RECIPE))["result"]
    except urllib.error.HTTPError:
        return None


def merge_base(a, b):
    """Common ancestor with the highest generation (from the commit graph)."""
    commits = {c["id"]: c for c in get("/repos/{}/graph?limit=5000".format(REPO))["commits"]}

    def ancestors(x):
        seen, todo = set(), [x]
        while todo:
            c = todo.pop()
            if c in seen or c not in commits:
                continue
            seen.add(c)
            todo.extend(commits[c]["parents"])
        return seen

    common = ancestors(a) & ancestors(b)
    return max(common, key=lambda c: commits[c]["generation"]) if common else None


# --------------------------------------------------------------------------- option model

class Item(object):
    __slots__ = ("id", "manifest", "attrs", "layer_id", "color", "mesh", "curve", "point", "false_colors")


def color_of(hexstr):
    return System.Drawing.ColorTranslator.FromHtml(hexstr)


def load_option(key, rev, base):
    """Fetch everything needed to draw one option. Nothing touches the document."""
    t0 = time.perf_counter()
    head = get("/repos/{}/refs/branches/{}".format(REPO, rev))["commit"]
    mb = merge_base(base, head) if base else None
    path = "/repos/{}/diff?to={}".format(REPO, head) + ("&from=" + mb if mb else "")
    changes = [c for c in get(path)["changes"] if c["kind"] == "object"]
    put = [c for c in changes if c["change"] in ("added", "modified")]
    hidden = set(c["id"] for c in changes if c["change"] in ("modified", "deleted"))

    manifests = {h: json.loads(b) for h, b in batch_get([c["to"] for c in put]).items()}
    layers = get("/repos/{}/commits/{}/entities?kind=layer&manifests=true".format(REPO, head))["entities"]
    attr_hashes = [manifests[c["to"]]["components"].get("attributes") for c in put] + \
                  [e["manifest"]["components"].get("attributes") for e in layers]
    attrs = {h: json.loads(b) for h, b in batch_get(attr_hashes).items()}
    layer_color = {e["id"]: attrs[e["manifest"]["components"]["attributes"]].get("color", "#808080") for e in layers}

    items, preview_of, need_geom = [], {}, []
    for c in put:
        m = manifests[c["to"]]
        it = Item()
        it.id, it.manifest = c["id"], m
        it.attrs = attrs.get(m["components"].get("attributes"), {})
        lref = [r["id"] for r in m.get("refs", []) if r.get("role") == "layer"]
        it.layer_id = lref[0] if lref else None
        it.color = color_of(it.attrs["color"] if it.attrs.get("colorSource") == "object"
                            else layer_color.get(it.layer_id, "#808080"))
        it.mesh = it.curve = it.point = None
        it.false_colors = False
        g = m["components"].get("geometry")
        if m.get("subtype") in ("Point", "LineCurve", "PolylineCurve", "NurbsCurve", "ArcCurve", "PolyCurve"):
            need_geom.append(g)
        else:
            preview_of[it.id] = derived(g)
            if preview_of[it.id] is None:
                need_geom.append(g)    # no preview registered: mesh locally from the real geometry
        items.append(it)

    blobs = batch_get(need_geom + list(preview_of.values()))
    for it in items:
        g = it.manifest["components"].get("geometry")
        ph = preview_of.get(it.id)
        if ph:
            it.mesh = Rhino.Runtime.CommonObject.FromJSON(blobs[ph].decode("utf-8"))
        else:
            geo = Rhino.Runtime.CommonObject.FromJSON(blobs[g].decode("utf-8"))
            if isinstance(geo, rg.Point):
                it.point = geo.Location
            elif isinstance(geo, rg.Curve):
                it.curve = geo
            else:
                it.mesh = local_mesh(geo)
        if it.mesh is not None:
            it.false_colors = it.mesh.VertexColors.Count == it.mesh.Vertices.Count and it.mesh.Vertices.Count > 0

    bbox = rg.BoundingBox.Empty
    for it in items:
        bbox.Union((it.mesh or it.curve).GetBoundingBox(False) if (it.mesh or it.curve) else rg.BoundingBox(it.point, it.point))
    return {"key": key, "rev": rev, "head": head, "mergeBase": mb, "items": items, "hidden": hidden,
            "changes": changes, "layers": layers, "attrs": attrs, "bbox": bbox, "load_ms": ms(t0)}


def local_mesh(geo):
    if isinstance(geo, rg.Extrusion):
        geo = geo.ToBrep()
    if isinstance(geo, rg.Mesh):
        return geo
    if isinstance(geo, rg.SubD):
        return rg.Mesh.CreateFromSubD(geo, 2)
    if isinstance(geo, rg.Brep):
        m = rg.Mesh()
        for p in rg.Mesh.CreateFromBrep(geo, rg.MeshingParameters.FastRenderMesh) or []:
            m.Append(p)
        return m
    return None


# --------------------------------------------------------------------------- display conduit

def make_conduit_class():
    ns = "RhinoTrunkPreview{}".format(int(time.time() * 1000))

    class PreviewConduit(Rhino.Display.DisplayConduit):
        __namespace__ = ns

        def __init__(self):
            super().__init__()
            self.option = None
            self.materials = {}
            self.hud = ""

        def set_option(self, option, hud):
            self.option, self.hud = option, hud

        def material(self, c):
            m = self.materials.get(c.ToArgb())
            if m is None:
                m = Rhino.Display.DisplayMaterial(c)
                self.materials[c.ToArgb()] = m
            return m

        def CalculateBoundingBox(self, e):
            if self.option:
                e.IncludeBoundingBox(self.option["bbox"])

        def ObjectCulling(self, e):
            if self.option and self.option["hidden"] and str(e.RhinoObject.Id) in self.option["hidden"]:
                e.CullObject = True

        def PostDrawObjects(self, e):
            if not self.option:
                return
            dp = e.Display
            for it in self.option["items"]:
                if it.mesh is not None:
                    if it.false_colors:
                        dp.DrawMeshFalseColors(it.mesh)
                    else:
                        dp.DrawMeshShaded(it.mesh, self.material(it.color))
                elif it.curve is not None:
                    dp.DrawCurve(it.curve, it.color, 2)
                elif it.point is not None:
                    dp.DrawPoint(it.point, Rhino.Display.PointStyle.RoundSimple, 5, it.color)

        def DrawForeground(self, e):
            if self.hud:
                e.Display.Draw2dText(self.hud, System.Drawing.Color.FromArgb(30, 30, 30), rg.Point2d(20, 30), False, 16)

    return PreviewConduit


# --------------------------------------------------------------------------- session (survives between runs)

class Session(object):
    def __init__(self):
        self.conduit = make_conduit_class()()
        self.options = {}
        self.current = None
        self.base = None

    def preload(self):
        d = doc()
        self.base = d.Strings.GetValue("rhinotrunk.base") if d.Strings.GetValue("rhinotrunk.repo") == REPO else None
        t0 = time.perf_counter()
        for key, rev, _ in OPTIONS:
            self.options[key] = load_option(key, rev, self.base)
        n = sum(len(o["items"]) for o in self.options.values())
        print("Preloaded {} options ({} preview items) from the engine in {:.0f} ms; base = {}".format(
            len(self.options), n, ms(t0), self.base[7:19] if self.base else "none (empty document)"))

    def show(self, key, quiet=False):
        o = self.options[key]
        title = dict((k, t) for k, _, t in OPTIONS)[key]
        hud = "Option {}  {}  ({} objects)   [A B C D | Enter next | Accept | Esc]".format(key, title, len(o["items"]))
        t0 = time.perf_counter()
        self.conduit.set_option(o, hud)
        self.conduit.Enabled = True
        doc().Views.Redraw()
        self.current = key
        if not quiet:
            print("Preview {}  {:.1f} ms (swap + redraw)".format(key, ms(t0)))
        return ms(t0)

    def off(self):
        self.conduit.Enabled = False
        self.conduit.set_option(None, "")
        doc().Views.Redraw()

    def accept(self):
        o = self.options[self.current]
        t0 = time.perf_counter()
        n = import_option(o)
        self.off()
        print("Accepted option {}: imported {} changes in {:.0f} ms".format(self.current, n, ms(t0)))


def session():
    s = sc.sticky.get(STICKY)
    if s is None:
        s = Session()
        sc.sticky[STICKY] = s
    elif type(s).__name__ == "Session" and not isinstance(s, Session):
        # created by an earlier run of this script: keep its state (conduit, preloaded options),
        # but use this run's code so edits to the script take effect immediately
        fresh = Session.__new__(Session)
        fresh.__dict__.update(s.__dict__)
        sc.sticky[STICKY] = s = fresh
    return s


# --------------------------------------------------------------------------- import on accept

def import_option(o):
    d = doc()
    s = Rhino.DocObjects.ObjectEnumeratorSettings()
    s.HiddenObjects = s.LockedObjects = True
    empty = not any(True for _ in d.Objects.GetObjectList(s))   # Objects.Count also counts deleted (undo) objects
    if empty and not d.Strings.GetValue("rhinotrunk.base"):
        settings = get("/repos/{}/commits/{}/entities/settings/settings".format(REPO, o["head"]))["manifest"]
        unit = json.loads(batch_get([settings["components"]["attributes"]]).popitem()[1]).get("unitSystem")
        if unit and str(d.ModelUnitSystem) != unit:
            d.ModelUnitSystem = getattr(Rhino.UnitSystem, unit)    # empty document: adopt units, no scaling
    geoms = batch_get([it.manifest["components"]["geometry"] for it in o["items"]])
    layer_index = ensure_layers(o)
    d.Views.RedrawEnabled = False
    count = 0
    try:
        for c in o["changes"]:
            if c["change"] == "deleted" and d.Objects.FindId(System.Guid(c["id"])):
                d.Objects.Delete(System.Guid(c["id"]), True)
                count += 1
        for it in o["items"]:
            geo = Rhino.Runtime.CommonObject.FromJSON(geoms[it.manifest["components"]["geometry"]].decode("utf-8"))
            a = Rhino.DocObjects.ObjectAttributes()
            a.LayerIndex = layer_index.get(it.layer_id, 0)
            a.Name = it.attrs.get("name", "")
            if it.attrs.get("colorSource") == "object":
                a.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject
                a.ObjectColor = color_of(it.attrs["color"])
            for k, v in it.attrs.get("userText", {}).items():
                a.SetUserString(k, str(v))
            gid = System.Guid(it.id)
            if d.Objects.FindId(gid):
                d.Objects.Replace(gid, geo)
                d.Objects.ModifyAttributes(gid, a, True)
            else:
                a.ObjectId = gid
                d.Objects.Add(geo, a)
            count += 1
    finally:
        d.Views.RedrawEnabled = True
    d.Strings.SetString("rhinotrunk.server", BASE_URL)
    d.Strings.SetString("rhinotrunk.repo", REPO)
    if not o["mergeBase"]:
        d.Strings.SetString("rhinotrunk.base", o["head"])        # clean checkout of the option
        d.Strings.SetString("rhinotrunk.branch", o["rev"])
    else:
        d.Strings.SetString("rhinotrunk.pendingMerge", o["head"])  # option merged into the working copy
    d.Views.Redraw()
    return count


def ensure_layers(o):
    """Create (by entity id) the layers the accepted objects need, parents first."""
    d = doc()
    by_id = {e["id"]: e for e in o["layers"]}
    index = {}

    def make(lid):
        if lid in index:
            return index[lid]
        existing = d.Layers.FindId(System.Guid(lid))
        if existing is not None and not existing.IsDeleted:
            index[lid] = existing.Index
            return existing.Index
        e = by_id[lid]
        attrs = o["attrs"][e["manifest"]["components"]["attributes"]]
        lay = Rhino.DocObjects.Layer()
        lay.Id = System.Guid(lid)
        lay.Name = attrs["name"]
        lay.Color = color_of(attrs.get("color", "#000000"))
        parent = [r["id"] for r in e["manifest"].get("refs", []) if r.get("role") == "parent"]
        if parent:
            lay.ParentLayerId = d.Layers[make(parent[0])].Id
        index[lid] = d.Layers.Add(lay)
        return index[lid]

    for it in o["items"]:
        if it.layer_id:
            make(it.layer_id)
    return index


# --------------------------------------------------------------------------- commands

def next_key(cur):
    keys = [k for k, _, _ in OPTIONS]
    return keys[(keys.index(cur) + 1) % len(keys)] if cur in keys else keys[0]


def bench(s, rounds=3):
    times = [s.show(k, quiet=True) for _ in range(rounds) for k, _, _ in OPTIONS]
    print("Bench: {} switches, avg {:.1f} ms, min {:.1f}, max {:.1f}".format(len(times), sum(times) / len(times), min(times), max(times)))


def interactive(s):
    s.preload()
    s.show(s.current or "A")
    accepted = False
    try:
        while True:
            go = Rhino.Input.Custom.GetOption()
            go.SetCommandPrompt("Previewing option {} (Enter = next)".format(s.current))
            for k, _, _ in OPTIONS:
                go.AddOption(k)
            go.AddOption("Accept")
            go.AddOption("Bench")
            go.AcceptNothing(True)
            res = go.Get()
            if res == Rhino.Input.GetResult.Nothing:
                s.show(next_key(s.current))
            elif res == Rhino.Input.GetResult.Option:
                name = go.Option().EnglishName
                if name == "Accept":
                    s.accept()
                    accepted = True
                    break
                elif name == "Bench":
                    bench(s)
                else:
                    s.show(name)
            else:
                break
    finally:
        if not accepted:
            s.off()
            print("Preview closed; document unchanged.")


def main():
    s = session()
    if ACTION == "interactive":
        interactive(s)
    elif ACTION.startswith("preview:"):
        if not s.options:
            s.preload()
        s.show(ACTION.split(":", 1)[1])
    elif ACTION == "bench":
        if not s.options:
            s.preload()
        bench(s)
    elif ACTION == "accept":
        s.accept()
        sc.sticky.pop(STICKY, None)
    elif ACTION == "off":
        s.off()
        sc.sticky.pop(STICKY, None)


main()
