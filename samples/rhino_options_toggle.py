#! python3
"""
Show the four pavilion options (from samples/rhino_design_options.py) in ONE location and switch
between them quickly, to test how fast RhinoTrunk-backed option switching feels.

Two switching modes, both timed:

  Layers    All four options are loaded once from the engine onto sublayers of "Options" at the same
            location; switching toggles layer visibility (pure Rhino speed, no network).
  Checkout  One working copy on layer "Options::Checkout". Switching asks the engine for the diff
            between the current and the target branch and applies only that: deletes, adds and
            replaces objects (Rhino object id = entity id). Blobs are cached by hash, so only the
            first visit to an option pays for downloads.

Rhino command (install once; the agent did this for you):  TrunkOptions
    = _-RunPythonScript "<repo>\\samples\\rhino_options_toggle.py"
  Then at the prompt:  A B C D = show that option, Enter = next, All = all overlaid (Layers mode),
                       Mode = Layers/Checkout, Reload = refetch from the engine, Esc = done.

Agent use (non-interactive, e.g. through the Rhino MCP run_python tool):
    RT_ACTION = "load"            # or "show:B", "checkout:C", "bench"
    exec(open(r"<repo>/samples/rhino_options_toggle.py", encoding="utf-8").read())
"""

import base64
import json
import time
import urllib.request

import System
import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc

BASE_URL = globals().get("RT_BASE_URL", "http://localhost:5280")
REPO = globals().get("RT_REPO", "pavilion-options")
ACTION = globals().get("RT_ACTION", "interactive")
ROOT = "Options"
SITE = ("Site", "main")
OPTIONS = [("A", "option/a-gridshell", "A gridshell"),
           ("B", "option/b-folded-plate", "B folded plate"),
           ("C", "option/c-timber-grid", "C timber grid"),
           ("D", "agent/subd-canopy", "D subd canopy (agent)")]
STATE_KEY = "rhinotrunk.options.state"


def doc():
    return globals().get("__rhino_doc__") or Rhino.RhinoDoc.ActiveDoc


def state():
    s = sc.sticky.get(STATE_KEY)
    if not s or s.get("repo") != REPO:
        s = {"repo": REPO, "geom": {}, "attrs": {}, "manifests": {}, "layer_colors": None,
             "mode": "Layers", "loaded": False, "current": None, "co_rev": None, "co_ids": set()}
        sc.sticky[STATE_KEY] = s
    return s


# --------------------------------------------------------------------------- engine access

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
    """hash -> bytes for every hash, 1000 per call."""
    out, hashes = {}, list(hashes)
    for i in range(0, len(hashes), 1000):
        res = post("/repos/{}/objects/batch-get".format(REPO), {"hashes": hashes[i:i + 1000]})
        for h, b64 in res["objects"].items():
            out[h] = base64.b64decode(b64)
    return out


def ensure_blobs(st, manifests):
    """Download and decode geometry/attributes not yet cached. Returns number fetched."""
    need = set()
    for m in manifests:
        comps = m.get("components", {})
        if comps.get("geometry") and comps["geometry"] not in st["geom"]:
            need.add(comps["geometry"])
        if comps.get("attributes") and comps["attributes"] not in st["attrs"]:
            need.add(comps["attributes"])
    if not need:
        return 0
    blobs = batch_get(need)
    for m in manifests:
        comps = m.get("components", {})
        g, a = comps.get("geometry"), comps.get("attributes")
        if g in blobs and g not in st["geom"]:
            st["geom"][g] = Rhino.Runtime.CommonObject.FromJSON(blobs[g].decode("utf-8"))
        if a in blobs and a not in st["attrs"]:
            st["attrs"][a] = json.loads(blobs[a].decode("utf-8"))
    return len(need)


def manifests_by_version(st, versions):
    need = [v for v in versions if v not in st["manifests"]]
    for h, b in batch_get(need).items():
        st["manifests"][h] = json.loads(b.decode("utf-8"))
    return [st["manifests"][v] for v in versions]


def layer_colors(st):
    if st["layer_colors"] is None:
        ents = get("/repos/{}/commits/main/entities?kind=layer&manifests=true".format(REPO))["entities"]
        for rev in [o[1] for o in OPTIONS]:
            ents += get("/repos/{}/commits/{}/entities?kind=layer&manifests=true".format(REPO, q(rev)))["entities"]
        ensure_blobs(st, [e["manifest"] for e in ents])
        st["layer_colors"] = {e["id"]: st["attrs"][e["manifest"]["components"]["attributes"]].get("color", "#808080")
                              for e in ents}
    return st["layer_colors"]


# --------------------------------------------------------------------------- Rhino side

def color(hexstr):
    return System.Drawing.ColorTranslator.FromHtml(hexstr)


def ensure_layer(name, parent=None, visible=True):
    d = doc()
    full = (parent + "::" + name) if parent else name
    idx = d.Layers.FindByFullPath(full, -1)
    if idx < 0:
        lay = Rhino.DocObjects.Layer()
        lay.Name = name
        if parent:
            lay.ParentLayerId = d.Layers[ensure_layer(parent)].Id
        idx = d.Layers.Add(lay)
    lay = d.Layers[idx]
    if lay.IsVisible != visible:
        lay.IsVisible = visible
        lay.SetPersistentVisibility(visible)
        lay.CommitChanges()
    return idx


def make_attrs(st, manifest, layer_index, object_id=None):
    a = Rhino.DocObjects.ObjectAttributes()
    a.LayerIndex = layer_index
    attrs = st["attrs"].get(manifest["components"].get("attributes"), {})
    a.Name = attrs.get("name", "")
    if attrs.get("colorSource") == "object":
        hexc = attrs["color"]
    else:
        lref = [r["id"] for r in manifest.get("refs", []) if r.get("role") == "layer"]
        hexc = layer_colors(st).get(lref[0], "#808080") if lref else "#808080"
    a.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject
    a.ObjectColor = color(hexc)
    for k, v in attrs.get("userText", {}).items():
        a.SetUserString(k, str(v))
    if object_id:
        a.ObjectId = System.Guid(object_id)
    return a


def add(st, manifest, layer_index, object_id=None):
    g = st["geom"][manifest["components"]["geometry"]]
    return doc().Objects.Add(g, make_attrs(st, manifest, layer_index, object_id))


def clear_layer(full):
    d = doc()
    idx = d.Layers.FindByFullPath(full, -1)
    if idx >= 0:
        for o in d.Objects.FindByLayer(d.Layers[idx]) or []:
            d.Objects.Delete(o, True)


def ms(t0):
    return (time.perf_counter() - t0) * 1000.0


# --------------------------------------------------------------------------- Layers mode

def load_layers(st):
    """Site once + each option's own objects (diff vs main), all at the same location."""
    t0 = time.perf_counter()
    d = doc()
    d.Views.RedrawEnabled = False
    try:
        for name in [SITE[0]] + [o[2] for o in OPTIONS]:
            clear_layer(ROOT + "::" + name)
        site_ents = get("/repos/{}/commits/main/entities?kind=object&manifests=true".format(REPO))["entities"]
        ensure_blobs(st, [e["manifest"] for e in site_ents])
        li = ensure_layer(SITE[0], ROOT, True)
        for e in site_ents:
            add(st, e["manifest"], li)
        counts = {"Site": len(site_ents)}
        for key, rev, name in OPTIONS:
            diff = get("/repos/{}/diff?from=main&to={}".format(REPO, q(rev)))["changes"]
            versions = [c["to"] for c in diff if c["kind"] == "object" and c["change"] in ("added", "modified")]
            mans = manifests_by_version(st, versions)
            ensure_blobs(st, mans)
            li = ensure_layer(name, ROOT, key == "A")
            for m in mans:
                add(st, m, li)
            counts[key] = len(mans)
        st["loaded"], st["current"] = True, "A"
    finally:
        d.Views.RedrawEnabled = True
    d.Views.Redraw()
    print("Loaded options into layer '{}' at one location in {:.0f} ms: {}".format(ROOT, ms(t0), counts))


def show_layers(st, key):
    d = doc()
    if not st["loaded"]:
        load_layers(st)
    t0 = time.perf_counter()
    for k, _, name in OPTIONS:
        ensure_layer(name, ROOT, key in (k, "All"))
    d.Views.Redraw()
    st["current"] = key
    print("[Layers] {} shown in {:.1f} ms (visibility toggle + redraw)".format(key, ms(t0)))


# --------------------------------------------------------------------------- Checkout mode

def checkout(st, key):
    rev = dict((k, r) for k, r, _ in OPTIONS)[key]
    d = doc()
    t0 = time.perf_counter()
    head = get("/repos/{}/refs/branches/{}".format(REPO, rev))["commit"]
    if st["co_rev"] == head:
        print("[Checkout] already on", key)
        return
    path = "/repos/{}/diff?to={}".format(REPO, head) + ("&from=" + st["co_rev"] if st["co_rev"] else "")
    changes = [c for c in get(path)["changes"] if c["kind"] == "object"]
    t_diff = ms(t0)

    t1 = time.perf_counter()
    put = [c for c in changes if c["change"] in ("added", "modified")]
    mans = manifests_by_version(st, [c["to"] for c in put])
    fetched = ensure_blobs(st, mans)
    t_fetch = ms(t1)

    t2 = time.perf_counter()
    li = ensure_layer("Checkout", ROOT, True)
    d.Views.RedrawEnabled = False
    try:
        n_add = n_rep = n_del = 0
        for c in changes:
            if c["change"] == "deleted":
                d.Objects.Delete(System.Guid(c["id"]), True)
                st["co_ids"].discard(c["id"])
                n_del += 1
        for c, m in zip(put, mans):
            gid = System.Guid(c["id"])
            if c["change"] == "modified" and c["id"] in st["co_ids"]:
                comps = c.get("components", [])
                if "geometry" in comps:
                    d.Objects.Replace(gid, st["geom"][m["components"]["geometry"]])
                if "attributes" in comps or c.get("refsChanged"):
                    d.Objects.ModifyAttributes(gid, make_attrs(st, m, li), True)
                n_rep += 1
            else:
                add(st, m, li, c["id"])
                st["co_ids"].add(c["id"])
                n_add += 1
    finally:
        d.Views.RedrawEnabled = True
    t_apply = ms(t2)
    t3 = time.perf_counter()
    d.Views.Redraw()
    t_draw = ms(t3)
    st["co_rev"], st["current"] = head, key
    print("[Checkout] {:<2} total {:6.0f} ms | diff {:4.0f} ms ({} changes) | fetch {:4.0f} ms ({} blobs) | "
          "apply {:4.0f} ms (+{} ~{} -{}) | redraw {:3.0f} ms".format(
              key, ms(t0), t_diff, len(changes), t_fetch, fetched, t_apply, n_add, n_rep, n_del, t_draw))


def enter_checkout(st):
    for k, _, name in OPTIONS:
        ensure_layer(name, ROOT, False)
    ensure_layer(SITE[0], ROOT, False)
    ensure_layer("Checkout", ROOT, True)


def enter_layers(st):
    ensure_layer("Checkout", ROOT, False)
    ensure_layer(SITE[0], ROOT, True)


# --------------------------------------------------------------------------- entry points

def show(st, key):
    if st["mode"] == "Checkout":
        if key == "All":
            print("All is a Layers-mode view; switch Mode first.")
            return
        checkout(st, key)
    else:
        show_layers(st, key)


def next_key(st):
    keys = [o[0] for o in OPTIONS]
    cur = st["current"] if st["current"] in keys else keys[-1]
    return keys[(keys.index(cur) + 1) % len(keys)]


def interactive(st):
    if not st["loaded"]:
        load_layers(st)
    while True:
        go = Rhino.Input.Custom.GetOption()
        go.SetCommandPrompt("Option [{} | {} mode] (Enter = next)".format(st["current"], st["mode"]))
        for k, _, _ in OPTIONS:
            go.AddOption(k)
        go.AddOption("All")
        i_mode = go.AddOptionList("Mode", ["Layers", "Checkout"], 0 if st["mode"] == "Layers" else 1)
        go.AddOption("Bench")
        go.AddOption("Reload")
        go.AcceptNothing(True)
        res = go.Get()
        if res == Rhino.Input.GetResult.Nothing:
            show(st, next_key(st))
            continue
        if res != Rhino.Input.GetResult.Option:
            break
        opt = go.Option()
        if opt.Index == i_mode:
            st["mode"] = ["Layers", "Checkout"][opt.CurrentListOptionIndex]
            (enter_checkout if st["mode"] == "Checkout" else enter_layers)(st)
            show(st, st["current"] if st["current"] in "ABCD" else "A")
        elif opt.EnglishName == "Reload":
            st.update({"geom": {}, "attrs": {}, "manifests": {}, "layer_colors": None, "co_rev": None})
            clear_layer(ROOT + "::Checkout")
            st["co_ids"] = set()
            load_layers(st)
        elif opt.EnglishName == "Bench":
            bench(st)
        else:
            show(st, opt.EnglishName)


def bench(st, rounds=3):
    """Cycle A->B->C->D a few times in the current mode and report the per-switch times."""
    print("Bench: {} rounds in {} mode".format(rounds, st["mode"]))
    t0 = time.perf_counter()
    n = 0
    for _ in range(rounds):
        for k, _, _ in OPTIONS:
            show(st, k)
            n += 1
    print("Bench: {} switches, {:.0f} ms average".format(n, ms(t0) / n))


def main():
    st = state()
    if ACTION == "interactive":
        interactive(st)
    elif ACTION == "load":
        load_layers(st)
    elif ACTION == "bench":
        for mode in ("Layers", "Checkout"):
            st["mode"] = mode
            (enter_checkout if mode == "Checkout" else enter_layers)(st)
            bench(st)
        st["mode"] = "Layers"
        enter_layers(st)
        show(st, "A")
    elif ACTION.startswith("show:"):
        st["mode"] = "Layers"
        enter_layers(st)
        show(st, ACTION[5:])
    elif ACTION.startswith("checkout:"):
        st["mode"] = "Checkout"
        enter_checkout(st)
        show(st, ACTION[9:])


main()
