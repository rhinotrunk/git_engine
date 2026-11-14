#! python3
"""
RhinoTrunk commands for Rhino 8 (CPython): COMMIT, PULL, BRANCH, MERGE, COMPARE.

Prototype client used to exercise the RhinoTrunk engine (docs/API.md). One file so it can be exec'd;
the TrunkXxx.py stubs next to it set RT_CMD and run it, so each command is a Rhino alias:

    TrunkCommit   ! _-RunPythonScript "<repo>/rhino/TrunkCommit.py"      (same for Pull, Branch, Merge, Compare)

Agents (e.g. through an MCP run_python tool) set globals and exec this file; no prompts are shown and the
outcome is left in RT_RESULT:

    RT_CMD = "compare"
    RT_ARGS = {"options": ["agent/a", "agent/b"], "mode": "overlay"}
    RT_AUTHOR = {"id": "agent:massing-bot", "name": "Massing bot", "kind": "agent"}
    exec(open(r"<repo>/rhino/rhinotrunk.py", encoding="utf-8").read(), globals())
    print(RT_RESULT)

Commands and their RT_ARGS:
    commit   message, path (agent: Save As target when the file was never saved), repo, branch (first commit only)
    pull     rev (branch, tag or commit; default: the file's branch), force (agent: discard local changes)
    branch   name, commit (also commit local changes to the new branch), message, repo (unlinked file)
    merge    into (default: the branch's parent), resolutions [{id, kind, take}], switch (pull the target afterwards)
    compare  options [branches], at (point on main, default: the file's commit), mode cycle|overlay|side,
             show (option to display), approve (option to approve, human only)
    status   (no args) local changes against the file's commit
    off      hide every preview

Document state lives in RhinoDoc.Strings (saved with the .3dm):
    rhinotrunk.server / .repo / .branch / .base   which commit the file is at
    rhinotrunk.basemap                           JSON {"kind/id": [version, geometryHash, attributesFingerprint]}
    rhinotrunk.dirty                             JSON list of entity keys edited since the base (written on save)
"""
import getpass
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

import Rhino
import Rhino.Geometry as rg
import scriptcontext as sc
import System

_HERE = os.path.dirname(os.path.abspath(globals().get("__file__") or "")) if globals().get("__file__") else \
    os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(Rhino.RhinoDoc.ActiveDoc.Path or "."))), "rhino")
if globals().get("RT_HOME"):
    _HERE = RT_HOME  # noqa: F821  (set by the stubs)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
import importlib  # noqa: E402
import rhinotrunk_gl  # noqa: E402

DEFAULT_SERVER = os.environ.get("RT_SERVER", "http://localhost:5280")
RECIPE = "preview-mesh.v1"
STICKY = "rhinotrunk.client"
K = "rhinotrunk."
CURVE_TYPES = ("Point", "LineCurve", "PolylineCurve", "NurbsCurve", "ArcCurve", "PolyCurve", "Curve")

GREEN = (0, 220, 70)     # new to the current file
RED = (235, 40, 40)      # removed from the current file (ghosted)
BLUE = (40, 120, 255)    # modified (current hidden, new shown)
AMBER = (255, 170, 0)    # unresolved conflict

CMD = globals().get("RT_CMD", "status")
ARGS = dict(globals().get("RT_ARGS") or {})
AUTHOR_IN = globals().get("RT_AUTHOR")


# =============================================================================================== basics

def doc():
    return Rhino.RhinoDoc.ActiveDoc


def ms(t0):
    return (time.perf_counter() - t0) * 1000.0


def is_agent():
    return bool(AUTHOR_IN) and AUTHOR_IN.get("kind") == "agent"


def author():
    if AUTHOR_IN:
        return {"id": AUTHOR_IN["id"], "name": AUTHOR_IN.get("name"), "kind": AUTHOR_IN.get("kind", "agent")}
    user = getpass.getuser()
    return {"id": user, "name": user, "kind": "human"}


def sha(b):
    return "sha256:" + hashlib.sha256(b).hexdigest()


def fingerprint(obj):
    """Client-side fingerprint of an attribute dict (the server hashes its own canonical form)."""
    return sha(json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8"))


class TrunkError(Exception):
    def __init__(self, status, body):
        self.status = status
        self.body = body if isinstance(body, dict) else {"code": "http_%s" % status, "message": str(body)}
        Exception.__init__(self, "%s %s: %s" % (status, self.body.get("code"), self.body.get("message")))

    @property
    def code(self):
        return self.body.get("code")


class UserAbort(Exception):
    pass


# =============================================================================================== HTTP

class Api(object):
    def __init__(self, server, repo):
        self.server = server.rstrip("/")
        self.repo = repo
        self.calls = 0
        self.bytes = 0

    def _req(self, method, path, body=None, raw=None):
        url = self.server + path
        data, headers = None, {}
        if raw is not None:
            data, headers["content-type"] = raw, "application/octet-stream"
        elif body is not None:
            data, headers["content-type"] = json.dumps(body).encode("utf-8"), "application/json"
        req = urllib.request.Request(url, data=data, method=method, headers=headers)
        self.calls += 1
        try:
            with urllib.request.urlopen(req) as resp:
                text = resp.read()
                self.bytes += len(text)
                return json.loads(text.decode("utf-8")) if text else None
        except urllib.error.HTTPError as e:
            raw_body = e.read().decode("utf-8", "replace")
            try:
                parsed = json.loads(raw_body)
            except ValueError:
                parsed = raw_body
            raise TrunkError(e.code, parsed)
        except urllib.error.URLError as e:
            raise TrunkError(0, {"code": "unreachable", "message": "RhinoTrunk server %s unreachable (%s)" % (self.server, e.reason)})

    def r(self, path):
        return "/repos/%s%s" % (urllib.parse.quote(self.repo, safe=""), path)

    def get(self, path):
        return self._req("GET", self.r(path))

    def post(self, path, body):
        return self._req("POST", self.r(path), body)

    def put(self, path, body):
        return self._req("PUT", self.r(path), body)

    # ---- objects
    def batch_get(self, hashes, chunk=500):
        out = {}
        hashes = [h for h in dict.fromkeys(hashes) if h]
        for i in range(0, len(hashes), chunk):
            res = self.post("/objects/batch-get", {"hashes": hashes[i:i + chunk]})
            for h, b64 in res["objects"].items():
                out[h] = System.Convert.FromBase64String(b64)
        return {h: bytes(b) for h, b in out.items()}

    def upload(self, blobs):
        hashes = list(blobs)
        sent = 0
        for i in range(0, len(hashes), 1000):
            for h in self.post("/objects/missing", {"hashes": hashes[i:i + 1000]})["missing"]:
                self._req("PUT", self.r("/objects/" + h), raw=blobs[h])
                sent += 1
        return sent

    def derived(self, h):
        try:
            return self.get("/derived/%s/%s" % (h, RECIPE))["result"]
        except TrunkError as e:
            if e.status == 404:
                return None
            raise

    # ---- refs
    def branch(self, name):
        try:
            return self.get("/branches/" + "/".join(urllib.parse.quote(p, safe="") for p in name.split("/")))
        except TrunkError as e:
            if e.status == 404:
                return None
            raise

    def branches(self):
        return self.get("/branches")["branches"]

    def resolve(self, rev):
        return self.get("/commits/" + urllib.parse.quote(rev, safe=""))["id"]


# =============================================================================================== document state

class State(object):
    """Which repo/branch/commit the file is at, and the base map used to detect local changes."""

    def __init__(self, d):
        self.d = d
        s = d.Strings
        self.server = s.GetValue(K + "server") or DEFAULT_SERVER
        self.repo = s.GetValue(K + "repo")
        self.branch = s.GetValue(K + "branch")
        self.base = s.GetValue(K + "base")
        raw = s.GetValue(K + "basemap")
        self.basemap = json.loads(raw) if raw else {}

    @property
    def linked(self):
        return bool(self.repo and self.base)

    def save(self):
        s = self.d.Strings
        s.SetString(K + "server", self.server)
        s.SetString(K + "repo", self.repo or "")
        s.SetString(K + "branch", self.branch or "")
        s.SetString(K + "base", self.base or "")
        s.SetString(K + "basemap", json.dumps(self.basemap, separators=(",", ":")))


# =============================================================================================== Rhino <-> entities

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


def hexcolor(c):
    return "#{:02x}{:02x}{:02x}".format(c.R, c.G, c.B)


def color(hexstr):
    return System.Drawing.ColorTranslator.FromHtml(hexstr or "#808080")


def gid(s):
    return System.Guid(s)


def sid(g):
    return str(g).lower()


_SRC = {"ColorFromLayer": "layer", "ColorFromObject": "object", "ColorFromMaterial": "material", "ColorFromParent": "parent",
        "MaterialFromLayer": "layer", "MaterialFromObject": "object", "MaterialFromParent": "parent",
        "LinetypeFromLayer": "layer", "LinetypeFromObject": "object", "LinetypeFromParent": "parent",
        "PlotColorFromLayer": "layer", "PlotColorFromObject": "object", "PlotColorFromDisplay": "display", "PlotColorFromParent": "parent",
        "PlotWeightFromLayer": "layer", "PlotWeightFromObject": "object", "PlotWeightFromParent": "parent"}


def object_attrs(a):
    """ObjectAttributes -> attribute JSON (CLIENT_GUIDE.md section 3)."""
    ut = a.GetUserStrings()
    return {
        "name": a.Name or "", "colorSource": _SRC.get(str(a.ColorSource), "layer"), "color": hexcolor(a.ObjectColor),
        "materialSource": _SRC.get(str(a.MaterialSource), "layer"), "linetypeSource": _SRC.get(str(a.LinetypeSource), "layer"),
        "plotColorSource": _SRC.get(str(a.PlotColorSource), "layer"), "plotWeightSource": _SRC.get(str(a.PlotWeightSource), "layer"),
        "visible": bool(a.Visible), "mode": str(a.Mode).lower(), "displayOrder": int(a.DisplayOrder),
        "space": "page" if str(a.Space) == "PageSpace" else "model",
        "userText": {k: ut[k] for k in ut.AllKeys},
    }


def apply_object_attrs(a, attrs, layer_index):
    a.LayerIndex = layer_index
    a.Name = attrs.get("name", "")
    cs = attrs.get("colorSource", "layer")
    a.ColorSource = {"object": Rhino.DocObjects.ObjectColorSource.ColorFromObject,
                     "material": Rhino.DocObjects.ObjectColorSource.ColorFromMaterial,
                     "parent": Rhino.DocObjects.ObjectColorSource.ColorFromParent}.get(cs, Rhino.DocObjects.ObjectColorSource.ColorFromLayer)
    a.ObjectColor = color(attrs.get("color", "#000000"))
    a.Visible = bool(attrs.get("visible", True))
    mode = attrs.get("mode", "normal")
    a.Mode = {"locked": Rhino.DocObjects.ObjectMode.Locked, "hidden": Rhino.DocObjects.ObjectMode.Hidden}.get(mode, Rhino.DocObjects.ObjectMode.Normal)
    a.DisplayOrder = int(attrs.get("displayOrder", 0))
    a.DeleteAllUserStrings()
    for k, v in (attrs.get("userText") or {}).items():
        a.SetUserString(k, str(v))
    return a


def layer_attrs(layer):
    ut = layer.GetUserStrings()
    return {"name": layer.Name, "color": hexcolor(layer.Color), "visible": bool(layer.IsVisible), "locked": bool(layer.IsLocked),
            "userText": {k: ut[k] for k in ut.AllKeys}}


def settings_attrs(d):
    user = {}
    for i in range(d.Strings.Count):
        k = d.Strings.GetKey(i)
        if k and not k.startswith(K) and "\\" not in k:
            user[k] = d.Strings.GetValue(k)
    return {"unitSystem": str(d.ModelUnitSystem), "absoluteTolerance": d.ModelAbsoluteTolerance,
            "angleToleranceRadians": round(d.ModelAngleToleranceRadians, 9), "relativeTolerance": d.ModelRelativeTolerance,
            "userText": user}


def geometry_bytes(g):
    return g.ToJSON(SER).encode("utf-8")


def model_objects(d):
    s = Rhino.DocObjects.ObjectEnumeratorSettings()
    s.HiddenObjects = s.LockedObjects = True
    s.IncludeLights = False
    s.IncludeGrips = False
    s.ReferenceObjects = False
    return [o for o in d.Objects.GetObjectList(s) if not o.IsInstanceDefinitionGeometry]


def live_layers(d):
    return [l for l in d.Layers if not l.IsDeleted and not l.IsReference]


def object_entity(d, obj, geometry_hash=None):
    """-> (manifest input, geometry hash, geometry bytes, attrs fingerprint).
    geometry_hash: reuse it instead of serializing (attribute-only edits). Re-serializing untouched geometry
    can give different bytes (Rhino fills in cached data once an object is displayed), i.e. a false change."""
    geo = obj.Geometry
    b = None if geometry_hash else geometry_bytes(geo)
    h = geometry_hash or sha(b)
    attrs = object_attrs(obj.Attributes)
    layer = d.Layers[obj.Attributes.LayerIndex]
    m = {"subtype": type(geo).__name__, "attributes": attrs, "components": {"geometry": h},
         "refs": [{"kind": "layer", "id": sid(layer.Id), "role": "layer"}]}
    return m, h, b, fingerprint(attrs)


def layer_entity(d, layer):
    attrs = layer_attrs(layer)
    refs = []
    if layer.ParentLayerId != System.Guid.Empty:
        refs.append({"kind": "layer", "id": sid(layer.ParentLayerId), "role": "parent"})
    return {"attributes": attrs, "refs": refs}, fingerprint([attrs, refs])


# =============================================================================================== change tracking

class Tracker(object):
    """Candidates for local edits, from document events. Comparison against the base map decides what changed."""

    def __init__(self):
        self.dirty = {}          # doc runtime serial -> set of keys
        self.watched = set()     # doc serials watched since open (or found unmodified)
        self.suspended = False
        self.handlers = []

    def _mark(self, d, key):
        if not self.suspended and d is not None:
            self.dirty.setdefault(d.RuntimeSerialNumber, set()).add(key)

    def install(self):
        R = Rhino.RhinoDoc

        def on_obj(sender, e):
            try:
                self._mark(e.TheObject.Document, "object/" + sid(e.ObjectId))
            except Exception:
                pass

        def on_replace(sender, e):
            try:
                self._mark(e.Document, "object/" + sid(e.ObjectId))
            except Exception:
                pass

        def on_attr(sender, e):
            try:
                self._mark(e.Document, "attr:object/" + sid(e.RhinoObject.Id))   # attributes only: geometry untouched
            except Exception:
                pass

        def on_save(sender, e):
            try:
                d = e.Document
                if d.Strings.GetValue(K + "repo"):
                    d.Strings.SetString(K + "dirty", json.dumps(sorted(self.keys(d))))
            except Exception:
                pass

        def on_open(sender, e):
            try:
                d = e.Document
                raw = d.Strings.GetValue(K + "dirty")
                self.dirty[d.RuntimeSerialNumber] = set(json.loads(raw)) if raw else set()
                self.watched.add(d.RuntimeSerialNumber)
            except Exception:
                pass

        pairs = [(R.AddRhinoObject, on_obj), (R.DeleteRhinoObject, on_obj), (R.UndeleteRhinoObject, on_obj),
                 (R.ReplaceRhinoObject, on_replace), (R.ModifyObjectAttributes, on_attr),
                 (R.BeginSaveDocument, on_save), (R.EndOpenDocument, on_open)]
        for ev, fn in pairs:
            ev += fn
        self.handlers = pairs

    def uninstall(self):
        for ev, fn in self.handlers:
            try:
                ev -= fn
            except Exception:
                pass
        self.handlers = []

    def keys(self, d):
        return self.dirty.get(d.RuntimeSerialNumber, set())

    def adopt(self, d):
        """First use in this session: trust the persisted dirty list only if the file is unmodified since opening."""
        if d.RuntimeSerialNumber in self.watched:
            return True
        raw = d.Strings.GetValue(K + "dirty")
        if not d.Modified:
            self.dirty[d.RuntimeSerialNumber] = set(json.loads(raw)) if raw else set()
            self.watched.add(d.RuntimeSerialNumber)
            return True
        return False

    def clear(self, d, keys=None):
        if keys is None:
            self.dirty[d.RuntimeSerialNumber] = set()
        else:
            self.dirty.get(d.RuntimeSerialNumber, set()).difference_update(keys)
        self.watched.add(d.RuntimeSerialNumber)


# =============================================================================================== session (sticky)

def session():
    s = sc.sticky.get(STICKY)
    if s is None:
        s = sc.sticky[STICKY] = {"meshes": {}, "geoms": {}, "manifests": {}, "attrs": {}}
    old = s.get("tracker")
    tr = Tracker()
    if old is not None:  # keep state, rebind to this run's code
        tr.dirty, tr.watched = old.dirty, old.watched
        old.uninstall()
    tr.install()
    s["tracker"] = tr
    if "gl" not in s or not s["gl"].available:
        s["gl"] = rhinotrunk_gl.OutlineRenderer()
    if s.get("conduit") is not None:
        s["conduit"].Enabled = False
    s["conduit"] = make_conduit()
    return s


# =============================================================================================== status

class Status(object):
    def __init__(self):
        self.changes = []        # commit changes
        self.blobs = {}          # hash -> bytes
        self.entries = {}        # key -> new basemap entry (after commit: version filled from the response)
        self.summary = {"added": [], "modified": [], "deleted": []}
        self.geom_changed = set()
        self.full_scan = False
        self.ms = 0.0

    @property
    def count(self):
        return sum(len(v) for v in self.summary.values())

    def keys(self):
        return set(self.summary["added"]) | set(self.summary["modified"]) | set(self.summary["deleted"])


def compute_status(S, st, d):
    """Local changes against the base map. Objects: only event candidates are serialized (all of them after a
    full scan); layers and settings are always compared (small tables)."""
    t0 = time.perf_counter()
    out = Status()
    bm = st.basemap
    tracker = S["tracker"]
    trusted = tracker.adopt(d) if st.linked else False
    out.full_scan = not trusted
    candidates = tracker.keys(d)

    def add(kind, key, op, manifest=None, entry=None):
        _, eid = key.split("/", 1)
        if op == "delete":
            out.changes.append({"op": "delete", "kind": kind, "id": eid})
        else:
            out.changes.append({"op": "put", "kind": kind, "id": eid, "manifest": manifest})
            out.entries[key] = entry
        out.summary["deleted" if op == "delete" else ("added" if key not in bm else "modified")].append(key)

    # settings
    sa = settings_attrs(d)
    fp = fingerprint(sa)
    if bm.get("settings/settings", [None, None, None])[2] != fp:
        add("settings", "settings/settings", "put", {"attributes": sa}, [None, None, fp])

    # layers
    present = set()
    for layer in live_layers(d):
        key = "layer/" + sid(layer.Id)
        present.add(key)
        m, fp = layer_entity(d, layer)
        if bm.get(key, [None, None, None])[2] != fp:
            add("layer", key, "put", m, [None, None, fp])

    # objects
    objs = {"object/" + sid(o.Id): o for o in model_objects(d)}
    for key, o in objs.items():
        base = bm.get(key)
        geo_dirty = not trusted or base is None or key in candidates
        if not geo_dirty and "attr:" + key not in candidates:
            continue
        m, h, b, afp = object_entity(d, o, None if geo_dirty else base[1])
        base = bm.get(key)
        if base and base[1] == h and base[2] == afp and m["refs"][0]["id"] == base_layer(S, st, key):
            continue
        if not base or base[1] != h:
            out.blobs[h] = b
            out.geom_changed.add(key)
        add("object", key, "put", m, [None, h, afp, m["refs"][0]["id"]])
    # deletions (objects first so layer deletions validate)
    for key in bm:
        kind = key.split("/", 1)[0]
        if kind == "object" and key not in objs:
            add("object", key, "delete")
    for key in bm:
        if key.split("/", 1)[0] == "layer" and key not in present:
            add("layer", key, "delete")
    out.ms = ms(t0)
    return out


def base_layer(S, st, key):
    """Layer id of an object at the base (kept in the base map as a 4th element)."""
    e = st.basemap.get(key)
    return e[3] if e and len(e) > 3 else None


# =============================================================================================== prompts

def choose(prompt, options, default=None, arg=None):
    """Human: command-line option list. Agent: ARGS[arg] or the default (never blocks)."""
    if is_agent() or ARGS.get("_noprompt"):
        v = ARGS.get(arg) if arg else None
        return v if v is not None else default
    go = Rhino.Input.Custom.GetOption()
    go.SetCommandPrompt(prompt)
    idx = {}
    for o in options:
        idx[go.AddOption(o)] = o
    if default:
        go.SetDefaultString(default)
        go.AcceptNothing(True)
    res = go.Get()
    if res == Rhino.Input.GetResult.Option:
        return idx.get(go.OptionIndex())
    if res == Rhino.Input.GetResult.Nothing and default:
        return default
    raise UserAbort()


def ask_string(prompt, default=None, arg=None):
    if is_agent() or ARGS.get("_noprompt"):
        v = ARGS.get(arg) if arg else None
        if v is None and default is None:
            raise TrunkError(400, {"code": "missing_argument", "message": "RT_ARGS['%s'] is required" % arg})
        return v if v is not None else default
    if arg and ARGS.get(arg) is not None:
        return ARGS[arg]
    gs = Rhino.Input.Custom.GetString()
    gs.SetCommandPrompt(prompt)
    if default:
        gs.SetDefaultString(default)
    gs.AcceptNothing(bool(default))
    res = gs.Get()
    if res == Rhino.Input.GetResult.String:
        return gs.StringResult().strip() or default
    if res == Rhino.Input.GetResult.Nothing and default:
        return default
    raise UserAbort()


def say(msg):
    print(msg)
    RESULT.setdefault("log", []).append(msg)


# =============================================================================================== saving

def run_script(cmd):
    return Rhino.RhinoApp.RunScript(cmd, False)


def ensure_saved(d):
    """COMMIT needs a saved file. Unsaved edits: human chooses Save or Revert (to the last save); agents save."""
    if not d.Path:
        path = ARGS.get("path")
        if is_agent() or path:
            if not path:
                raise TrunkError(400, {"code": "unsaved_file", "message": "The file has never been saved; pass RT_ARGS['path']."})
            run_script('_-SaveAs "%s"' % path)      # RhinoDoc.SaveAs writes a copy; the command re-points the document
            if not doc().Path:
                raise TrunkError(500, {"code": "save_failed", "message": "Save As failed: " + path})
            return doc()
        say("Commit needs a saved file.")
        if choose("This file has never been saved", ["SaveAs", "Cancel"], "SaveAs") != "SaveAs":
            raise UserAbort()
        if not run_script("_SaveAs") or not doc().Path:
            raise UserAbort()
        return doc()
    if not d.Modified:
        return d
    if is_agent():
        d.Save()
        return doc()
    pick = choose("The file has changes since it was last saved. Commit needs a saved file", ["Save", "Revert", "Cancel"], "Save")
    if pick == "Save":
        if not d.Save():
            raise TrunkError(500, {"code": "save_failed", "message": "Saving failed."})
    elif pick == "Revert":
        path = d.Path
        d.Modified = False
        run_script('_-Open "%s"' % path)
        say("Reverted to the last saved version of %s." % os.path.basename(path))
    else:
        raise UserAbort()
    return doc()


def quiet_save(d):
    if d.Path:
        d.Save()


# =============================================================================================== preview meshes

def local_mesh(geo):
    if isinstance(geo, rg.Mesh):
        return geo
    if isinstance(geo, rg.Extrusion):
        geo = geo.ToBrep()
    if isinstance(geo, rg.SubD):
        try:
            return rg.Mesh.CreateFromSubD(geo, 2)
        except Exception:
            geo = geo.ToBrep(rg.SubDToBrepOptions.Default)
    if isinstance(geo, rg.Surface):
        geo = rg.Brep.CreateFromSurface(geo)
    if isinstance(geo, rg.Brep):
        m = rg.Mesh()
        for p in rg.Mesh.CreateFromBrep(geo, rg.MeshingParameters.FastRenderMesh) or []:
            m.Append(p)
        m.Normals.ComputeNormals()
        m.Compact()
        return m if m.Vertices.Count else None
    return None


def publish_previews(api, blobs):
    """preview-mesh.v1 for new geometry, so other clients can compare without the real geometry."""
    made = 0
    for h, b in blobs.items():
        if api.derived(h):
            continue
        geo = Rhino.Runtime.CommonObject.FromJSON(b.decode("utf-8"))
        if geo is None or isinstance(geo, (rg.Curve, rg.Point)):
            continue
        m = local_mesh(geo)
        if m is None or m is geo:
            continue
        mb = geometry_bytes(m)
        mh = sha(mb)
        api.upload({mh: mb})
        api.put("/derived/%s/%s" % (h, RECIPE), {"result": mh})
        made += 1
    return made


# =============================================================================================== COMMIT

def cmd_commit(S, api, st, d):
    d = ensure_saved(d)
    st = State(d)
    if not st.repo:
        st.repo = ARGS.get("repo") or ask_string("Repository for this file", arg="repo")
        api = Api(st.server, st.repo)
    ensure_repo(api)
    if not st.branch:
        st.branch = ARGS.get("branch") or ask_string("Branch", "main", arg="branch")
    info = api.branch(st.branch)
    if info and info.get("protected") and is_agent():
        raise TrunkError(403, {"code": "forbidden", "message": "Agents cannot commit to '%s'. Run branch first (RT_CMD='branch')." % st.branch})
    if info and not st.base:
        raise TrunkError(409, {"code": "not_linked", "message": "Branch '%s' already exists; pull it first, or commit to a new branch." % st.branch})
    if info and st.base and info["commit"] != st.base:
        raise TrunkError(409, {"code": "ref_conflict", "message": "'%s' moved to %s since this file's commit %s. Pull (or branch) first."
                               % (st.branch, info["commit"][7:19], st.base[7:19])})

    status = compute_status(S, st, d)
    if status.count == 0:
        say("Nothing to commit: the file matches %s." % (st.base or "")[7:19])
        RESULT.update(status="clean")
        return
    message = ask_string("Commit message", "Update from Rhino", arg="message")
    t0 = time.perf_counter()
    sent = api.upload(status.blobs)
    body = {"branch": st.branch, "expectedHead": st.base if info else None, "message": message, "author": author(),
            "changes": status.changes, "meta": {"client": "rhinotrunk.py", "file": os.path.basename(d.Path)}}
    if not info and ARGS.get("parent"):
        body["parent"] = ARGS["parent"]
    res = api.post("/commits", body)
    versions = {"%s/%s" % (v["kind"], v["id"]): v["version"] for v in res["versions"]}
    for key in status.summary["deleted"]:
        st.basemap.pop(key, None)
    for key, entry in status.entries.items():
        entry[0] = versions.get(key)
        st.basemap[key] = entry
    st.base = res["commit"]
    st.save()
    S["tracker"].clear(d)
    commit_ms = ms(t0)
    previews = publish_previews(api, status.blobs)
    quiet_save(d)
    s = status.summary
    say("Committed %s to %s: +%d ~%d -%d (status %.0f ms%s, upload %d blobs, commit %.0f ms, %d previews)." % (
        res["commit"][7:19], st.branch, len(s["added"]), len(s["modified"]), len(s["deleted"]), status.ms,
        ", full scan" if status.full_scan else "", sent, commit_ms, previews))
    RESULT.update(status="committed", commit=res["commit"], branch=st.branch,
                  added=len(s["added"]), modified=len(s["modified"]), deleted=len(s["deleted"]))


def ensure_repo(api):
    try:
        api._req("GET", "/repos/" + api.repo)
    except TrunkError as e:
        if e.status != 404:
            raise
        api._req("POST", "/repos", {"name": api.repo})
        say("Created repository '%s'." % api.repo)


# =============================================================================================== PULL

def cmd_pull(S, api, st, d, rev=None, confirm=True):
    """Bring the file to any commit, branch or tag, touching only what differs (server diff + local edits)."""
    if not st.repo:
        st.repo = ARGS.get("repo") or ask_string("Repository", arg="repo")
        api = Api(st.server, st.repo)
    rev = rev or ARGS.get("rev")
    if not rev:
        names = [b["name"] for b in api.branches()]
        say("Branches: " + ", ".join(names))
        rev = ask_string("Pull which branch, tag or commit", st.branch or "main", arg="rev")
    t0 = time.perf_counter()
    target = api.resolve(rev)
    local = compute_status(S, st, d) if st.linked else None

    if st.linked and st.repo == api.repo:
        if confirm and local.count and not ARGS.get("force"):
            if is_agent():
                raise TrunkError(409, {"code": "local_changes", "message": "%d uncommitted changes would be discarded; commit them or pass force=True." % local.count})
            if choose("Pull discards %d uncommitted changes" % local.count, ["Discard", "Cancel"], "Cancel") != "Discard":
                raise UserAbort()
        diff = api.get("/diff?from=%s&to=%s&manifests=true" % (st.base, target))["changes"]
    else:
        n = len(model_objects(d))
        if confirm and n and not ARGS.get("force"):
            if is_agent():
                raise TrunkError(409, {"code": "not_linked", "message": "This file is not linked to '%s' and has %d objects; pass force=True to replace them." % (api.repo, n)})
            if choose("Replace the %d objects of this file with %s" % (n, rev), ["Replace", "Cancel"], "Cancel") != "Replace":
                raise UserAbort()
        diff = api.get("/diff?to=%s&manifests=true" % target)["changes"]
        st.basemap = {}
        local = compute_status(S, st, d)   # everything in the file is "local": removed unless the target has it

    plan = plan_pull(S, api, st, d, diff, local, target)
    apply_ms = apply_plan(S, api, st, d, plan)
    st.base, st.repo = target, api.repo
    st.branch = rev if api.branch(rev) is not None else ""   # tag or commit: detached until BRANCH
    st.save()
    S["tracker"].clear(d)
    d.Views.Redraw()
    say("Pulled %s (%s) in %.0f ms: %d entities written, %d deleted, %d geometry blobs fetched; %d HTTP calls, %.1f KB (apply %.0f ms)." % (
        rev, target[7:19], ms(t0), plan["written"], plan["deleted"], plan["geometry_fetched"], api.calls, api.bytes / 1024.0, apply_ms))
    RESULT.update(status="pulled", rev=rev, commit=target, written=plan["written"], deleted=plan["deleted"],
                  geometryFetched=plan["geometry_fetched"], ms=round(ms(t0)))


def plan_pull(S, api, st, d, diff, local, target):
    """Entities to write/delete. Server diff covers base->target; local edits are reverted to the target version."""
    todo = {}   # key -> (version or None, manifest or None, geometry_needed)
    for c in diff:
        key = "%s/%s" % (c["kind"], c["id"])
        need_geom = c["change"] == "added" or "geometry" in (c.get("components") or []) or c.get("subtypeChanged")
        todo[key] = (c.get("to"), c.get("toManifest"), bool(need_geom))
    if local is not None:
        for key in local.keys():
            if key in todo:
                kv = todo[key]
                todo[key] = (kv[0], kv[1], kv[2] or key in local.geom_changed or key in local.summary["deleted"])
                continue
            base = st.basemap.get(key)
            if base is None:                      # added locally, unknown to the target
                todo[key] = (None, None, False)
            else:                                 # edited or deleted locally: restore the base (= target) version
                todo[key] = (base[0], None, key in local.geom_changed or key in local.summary["deleted"])
        # entities deleted locally but changed on the server are covered above
    need_manifest = [v for v, m, _ in todo.values() if v and m is None]
    fetched = api.batch_get(need_manifest)
    plan = {"todo": {}, "written": 0, "deleted": 0, "geometry_fetched": 0}
    for key, (v, m, g) in todo.items():
        if v and m is None:
            m = json.loads(fetched[v].decode("utf-8"))
        plan["todo"][key] = (v, m, g)
    return plan


def apply_plan(S, api, st, d, plan):
    todo = plan["todo"]
    attr_hashes = [m["components"].get("attributes") for v, m, g in todo.values() if m]
    geo_hashes = [m["components"].get("geometry") for v, m, g in todo.values() if m and g and m["components"].get("geometry")]
    blobs = api.batch_get(attr_hashes + geo_hashes)
    plan["geometry_fetched"] = len(geo_hashes)
    attrs = {h: json.loads(blobs[h].decode("utf-8")) for h in attr_hashes if h in blobs}

    t0 = time.perf_counter()
    tracker = S["tracker"]
    tracker.suspended = True
    d.Views.RedrawEnabled = False
    undo = d.BeginUndoRecord("RhinoTrunk pull")   # one Ctrl+Z reverts a pull; also the rollback on failure
    ok = False
    try:
        # settings
        if "settings/settings" in todo and todo["settings/settings"][1]:
            v, m, _ = todo["settings/settings"]
            sa = attrs.get(m["components"].get("attributes"), {})
            unit = sa.get("unitSystem")
            if unit and str(d.ModelUnitSystem) != unit:
                d.ModelUnitSystem = getattr(Rhino.UnitSystem, unit)          # adopt, never scale
            if sa.get("absoluteTolerance"):
                d.ModelAbsoluteTolerance = float(sa["absoluteTolerance"])
            if sa.get("angleToleranceRadians"):
                d.ModelAngleToleranceRadians = float(sa["angleToleranceRadians"])
            for k, val in (sa.get("userText") or {}).items():
                d.Strings.SetString(k, str(val))
            st.basemap["settings/settings"] = [v, None, fingerprint(settings_attrs(d))]
            plan["written"] += 1

        # 1. objects to delete (frees their layers)
        for key, (v, m, g) in todo.items():
            if not key.startswith("object/") or m is not None:
                continue
            o = d.Objects.FindId(gid(key.split("/", 1)[1]))
            if o is not None:
                d.Objects.Delete(o, True, True)
            st.basemap.pop(key, None)
            plan["deleted"] += 1

        # 2. layers to delete, children first (frees their names); retried at the end if objects still use them
        def delete_layers(keys):
            failed = []
            for key in sorted(keys, key=lambda k: -layer_depth(d, k)):
                lay = d.Layers.FindId(gid(key.split("/", 1)[1]))
                if lay is not None and not lay.IsDeleted:
                    if d.Layers.CurrentLayerIndex == lay.Index:
                        keep = next((l for l in live_layers(d) if l.Index != lay.Index and sid(l.Id) and "layer/" + sid(l.Id) not in keys), None)
                        if keep is not None:
                            d.Layers.SetCurrentLayerIndex(keep.Index, True)
                    if not d.Layers.Delete(lay.Index, True):
                        failed.append(key)
                        continue
                st.basemap.pop(key, None)
                plan["deleted"] += 1
            return failed

        retry = delete_layers([k for k, (v, m, g) in todo.items() if k.startswith("layer/") and m is None])

        # 3. layers to write, parents first
        written = set()

        def write_layer(key):
            if key in written:
                return
            written.add(key)
            v, m, _ = todo[key]
            la = attrs.get(m["components"].get("attributes"), {})
            parent = [r["id"] for r in m.get("refs", []) if r.get("role") == "parent"]
            if parent and "layer/" + parent[0] in todo and todo["layer/" + parent[0]][1]:
                write_layer("layer/" + parent[0])
            lid = gid(key.split("/", 1)[1])
            existing = d.Layers.FindId(lid)
            if existing is None:
                # deleted layers keep their id (undo) and FindId skips them: undelete, or Rhino issues a new id
                dead_layer = next((l for l in d.Layers if l.Id == lid and l.IsDeleted), None)
                if dead_layer is not None and d.Layers.Undelete(dead_layer.Index):
                    existing = d.Layers.FindId(lid)
            lay = Rhino.DocObjects.Layer() if existing is None or existing.IsDeleted else existing
            lay.Name = la.get("name", "Layer")
            lay.Color = color(la.get("color", "#000000"))
            lay.IsVisible = bool(la.get("visible", True))
            lay.IsLocked = bool(la.get("locked", False))
            lay.ParentLayerId = gid(parent[0]) if parent else System.Guid.Empty
            if existing is None or existing.IsDeleted:
                lay.Id = lid
                d.Layers.Add(lay)
            else:
                d.Layers.Modify(lay, existing.Index, True)
            final = d.Layers.FindId(lid)
            if final is None or final.IsDeleted:
                raise TrunkError(500, {"code": "id_lost", "message": "Rhino did not keep layer id %s (name '%s' taken?)" % (key, la.get("name"))})
            mm, fp = layer_entity(d, final)
            st.basemap[key] = [v, None, fp]
            plan["written"] += 1

        for key in [k for k, (v, m, g) in todo.items() if k.startswith("layer/") and m]:
            write_layer(key)

        # 4. objects to write
        dead = None
        for key, (v, m, g) in todo.items():
            if not key.startswith("object/") or m is None:
                continue
            oid = gid(key.split("/", 1)[1])
            oa = attrs.get(m["components"].get("attributes"), {})
            lref = [r["id"] for r in m.get("refs", []) if r.get("role") == "layer"]
            layer = d.Layers.FindId(gid(lref[0])) if lref else None
            a = apply_object_attrs(Rhino.DocObjects.ObjectAttributes(), oa, layer.Index if layer else d.Layers.CurrentLayerIndex)
            h = m["components"].get("geometry")
            existing = d.Objects.FindId(oid)
            if existing is None:
                if dead is None:
                    dead = deleted_objects(d)
                existing = dead.get(oid)
                if existing is not None:
                    d.Objects.Undelete(existing)     # same reason as layers: keep the entity id
                    existing = d.Objects.FindId(oid)
                    g = True
            if g and h not in blobs:
                blobs.update(api.batch_get([h]))
                plan["geometry_fetched"] += 1
            if existing is not None and not existing.IsDeleted:
                if g:
                    geo = Rhino.Runtime.CommonObject.FromJSON(blobs[h].decode("utf-8"))
                    d.Objects.Replace(oid, geo, True)
                d.Objects.ModifyAttributes(oid, a, True)
            else:
                geo = Rhino.Runtime.CommonObject.FromJSON(blobs[h].decode("utf-8"))
                a.ObjectId = oid
                d.Objects.Add(geo, a)
            obj = d.Objects.FindId(oid)
            if obj is None:
                raise TrunkError(500, {"code": "id_lost", "message": "Rhino did not keep object id %s" % key})
            st.basemap[key] = [v, h, fingerprint(object_attrs(obj.Attributes)), lref[0] if lref else None]
            plan["written"] += 1

        # 5. layers that still had objects before step 4
        failed = delete_layers(retry)
        if failed:
            raise TrunkError(500, {"code": "layer_delete_failed", "message": "Could not delete layers: " + ", ".join(failed)})
        ok = True
    finally:
        if undo:
            d.EndUndoRecord(undo)
        if not ok and undo:
            d.Undo()                                    # leave the file as it was before the pull
        d.Views.RedrawEnabled = True
        tracker.suspended = False
    return ms(t0)


def deleted_objects(d):
    """Deleted (undo-able) objects by id; they still own their ids."""
    s = Rhino.DocObjects.ObjectEnumeratorSettings()
    s.DeletedObjects = True
    s.NormalObjects = s.LockedObjects = s.HiddenObjects = False
    return {o.Id: o for o in d.Objects.GetObjectList(s) if o.IsDeleted}


def layer_depth(d, key):
    lay = d.Layers.FindId(gid(key.split("/", 1)[1]))
    n = 0
    while lay is not None and lay.ParentLayerId != System.Guid.Empty and n < 64:
        lay = d.Layers.FindId(lay.ParentLayerId)
        n += 1
    return n


# =============================================================================================== BRANCH

def cmd_branch(S, api, st, d):
    name = ask_string("New branch name", arg="name")
    if not st.linked:
        # first commit of a fresh file goes straight to the new branch
        ARGS.setdefault("branch", name)
        st.branch = name
        st.save()
        say("This file is not in a repository yet: committing it as the first commit of '%s'." % name)
        cmd_commit(S, api, State(d), d)
        RESULT.update(status="branched", branch=name)
        return
    info = api.post("/branches", {"name": name, "from": st.base, "parent": st.branch, "author": author()})
    parent = st.branch
    st.branch = name
    st.save()
    say("Created branch '%s' from %s at %s; this file now commits to '%s'." % (name, parent, st.base[7:19], name))
    RESULT.update(status="branched", branch=name, parent=parent, commit=info["commit"])
    local = compute_status(S, st, d)
    if local.count:
        do = ARGS.get("commit")
        if do is None and not is_agent():
            do = choose("Commit the %d local changes to '%s' now" % (local.count, name), ["Yes", "No"], "Yes") == "Yes"
        if do:
            cmd_commit(S, api, State(d), d)


# =============================================================================================== display conduit

class Drawable(object):
    __slots__ = ("key", "mesh", "curve", "point", "fill", "alpha", "outline", "width", "offset", "false_colors")

    def __init__(self, key, geo, fill, outline=None, alpha=0.0, width=4.0, offset=None):
        self.key, self.fill, self.outline, self.alpha, self.width, self.offset = key, fill, outline, alpha, width, offset
        self.mesh = self.curve = self.point = None
        if isinstance(geo, rg.Mesh):
            self.mesh = geo
        elif isinstance(geo, rg.Curve):
            self.curve = geo
        elif isinstance(geo, rg.Point):
            self.point = geo.Location
        elif isinstance(geo, rg.Point3d):
            self.point = geo
        self.false_colors = self.mesh is not None and self.mesh.VertexColors.Count == self.mesh.Vertices.Count > 0


class Scene(object):
    def __init__(self):
        self.items = []          # Drawables
        self.hidden = set()      # document object ids (str) to hide
        self.copies = []         # (RhinoObject, Transform) for side-by-side slots
        self.labels = []         # (Point3d, text)
        self.hud = []            # lines
        self.bbox = rg.BoundingBox.Empty


def make_conduit():
    ns = "RhinoTrunkConduit%d" % int(time.time() * 1000)
    S = sc.sticky[STICKY]

    class TrunkConduit(Rhino.Display.DisplayConduit):
        __namespace__ = ns

        def __init__(self):
            super().__init__()
            self.scene = None
            self.materials = {}

        def material(self, c, alpha):
            k = (c.ToArgb(), alpha)
            m = self.materials.get(k)
            if m is None:
                m = self.materials[k] = Rhino.Display.DisplayMaterial(c, alpha)
            return m

        def CalculateBoundingBox(self, e):
            if self.scene and self.scene.bbox.IsValid:
                e.IncludeBoundingBox(self.scene.bbox)

        def CalculateBoundingBoxZoomExtents(self, e):
            if self.scene and self.scene.bbox.IsValid:
                e.IncludeBoundingBox(self.scene.bbox)

        def ObjectCulling(self, e):
            if self.scene and self.scene.hidden and sid(e.RhinoObject.Id) in self.scene.hidden:
                e.CullObject = True

        def PostDrawObjects(self, e):
            sc_ = self.scene
            if not sc_:
                return
            dp = e.Display
            for obj, xf in sc_.copies:
                dp.DrawObject(obj, xf)
            outlined = [(it.key, it.mesh, it.outline, it.offset) for it in sc_.items if it.mesh is not None and it.outline]
            gl = S["gl"]
            widths = set(it.width for it in sc_.items if it.mesh is not None and it.outline)
            ok = True
            for w in widths:   # one GL pass per outline width
                ok = gl.draw(e.Viewport, [(it.key, it.mesh, it.outline, it.offset) for it in sc_.items
                                          if it.mesh is not None and it.outline and it.width == w], w) and ok
            for opaque in (True, False):
                for it in sc_.items:
                    if (it.alpha == 0.0) != opaque:
                        continue
                    xf = None if it.offset is None else rg.Transform.Translation(it.offset)
                    if xf is not None:
                        dp.PushModelTransform(xf)
                    try:
                        if it.mesh is not None:
                            if not ok and it.outline:
                                dp.DrawMeshWires(it.mesh, System.Drawing.Color.FromArgb(*it.outline), 2)
                            if it.false_colors and it.alpha == 0.0:
                                dp.DrawMeshFalseColors(it.mesh)
                            else:
                                dp.DrawMeshShaded(it.mesh, self.material(it.fill, it.alpha))
                        elif it.curve is not None:
                            if it.outline:
                                dp.DrawCurve(it.curve, System.Drawing.Color.FromArgb(*it.outline), int(it.width) + 3)
                            dp.DrawCurve(it.curve, it.fill if it.alpha < 0.5 else System.Drawing.Color.FromArgb(120, it.fill), 2)
                        elif it.point is not None:
                            if it.outline:
                                dp.DrawPoint(it.point, Rhino.Display.PointStyle.RoundSimple, 8, System.Drawing.Color.FromArgb(*it.outline))
                            dp.DrawPoint(it.point, Rhino.Display.PointStyle.RoundSimple, 4, it.fill)
                    finally:
                        if xf is not None:
                            dp.PopModelTransform()
            for pt, text, size in sc_.labels:
                dp.Draw2dText(text, System.Drawing.Color.FromArgb(20, 20, 20), pt, True, size)

        def DrawForeground(self, e):
            if not self.scene or not self.scene.hud:
                return
            y = 28
            for i, line in enumerate(self.scene.hud):
                e.Display.Draw2dText(line, System.Drawing.Color.FromArgb(25, 25, 25), rg.Point2d(18, y), False, 15 if i == 0 else 13)
                y += 20 if i == 0 else 17

    return TrunkConduit()


def show(S, scene):
    c = S["conduit"]
    c.scene = scene
    c.Enabled = scene is not None
    sc.sticky[STICKY]["scene"] = scene
    doc().Views.Redraw()


# =============================================================================================== visual diff

def geometry_for(S, api, manifest):
    """Drawable geometry for a manifest: real geometry for curves/points, preview mesh otherwise (cached by hash)."""
    h = manifest["components"].get("geometry")
    if not h:
        return None
    cache = S["geoms"]
    if h in cache:
        return cache[h]
    return None


def load_geometries(S, api, manifests):
    """Fetch what is needed to draw these manifests: preview meshes (or real geometry for curves, points and
    objects without a preview). Everything is cached by content hash for the whole Rhino session."""
    cache = S["geoms"]
    want = {}
    for m in manifests:
        h = m["components"].get("geometry")
        if h and h not in cache:
            want[h] = m.get("subtype")
    previews, real = {}, []
    for h, sub in want.items():
        if sub in CURVE_TYPES:
            real.append(h)
        else:
            p = api.derived(h)
            if p:
                previews[h] = p
            else:
                real.append(h)
    blobs = api.batch_get(real + list(previews.values()))
    for h in want:
        if h in previews and previews[h] in blobs:
            cache[h] = Rhino.Runtime.CommonObject.FromJSON(blobs[previews[h]].decode("utf-8"))
        elif h in blobs:
            g = Rhino.Runtime.CommonObject.FromJSON(blobs[h].decode("utf-8"))
            cache[h] = g if isinstance(g, (rg.Curve, rg.Point)) else local_mesh(g)


def load_attrs(S, api, manifests):
    cache = S["attrs"]
    need = [m["components"].get("attributes") for m in manifests if m["components"].get("attributes") not in cache]
    for h, b in api.batch_get(need).items():
        cache[h] = json.loads(b.decode("utf-8"))


def doc_drawable_geo(S, obj):
    """Mesh (or curve/point) of an existing document object, cached by runtime serial number."""
    key = "doc:%s:%d" % (sid(obj.Id), obj.RuntimeSerialNumber)
    cache = S["meshes"]
    if key not in cache:
        g = obj.Geometry
        if isinstance(g, (rg.Curve, rg.Point)):
            cache[key] = g
        else:
            ms_ = obj.GetMeshes(Rhino.Geometry.MeshType.Render)
            if ms_ and len(ms_):
                m = rg.Mesh()
                for p in ms_:
                    m.Append(p)
                m.Normals.ComputeNormals()
                cache[key] = m
            else:
                cache[key] = local_mesh(g.ToBrep() if isinstance(g, rg.Extrusion) else g)
    return key, cache[key]


def object_color(S, d, m, layer_colors):
    a = S["attrs"].get(m["components"].get("attributes"), {})
    if a.get("colorSource") == "object":
        return color(a.get("color"))
    lref = [r["id"] for r in m.get("refs", []) if r.get("role") == "layer"]
    if lref:
        if lref[0] in layer_colors:
            return color(layer_colors[lref[0]])
        lay = d.Layers.FindId(gid(lref[0]))
        if lay is not None:
            return lay.Color
    return System.Drawing.Color.Gray


def build_diff(S, api, d, changes, offset=None, ghost=False, width=4.0, conflicts=None, from_server=False, outline=True):
    """Scene parts for a list of DiffEntry (with manifests) relative to the current file:
    added -> green outline, deleted -> ghost + red outline, modified -> current hidden, new + blue outline.
    from_server: removed geometry comes from the server ("from" version) instead of the document.
    outline=False: draw the new state without highlighting (used to make the view match a result)."""
    objs = [c for c in changes if c["kind"] == "object"]
    manifests = [c["toManifest"] for c in objs if c.get("toManifest")]
    if from_server:
        manifests += [c["fromManifest"] for c in objs if c["change"] == "deleted" and c.get("fromManifest")]
    load_attrs(S, api, manifests + [c["toManifest"] for c in changes if c["kind"] == "layer" and c.get("toManifest")])
    load_geometries(S, api, manifests)
    layer_colors = {}
    for c in changes:
        if c["kind"] == "layer" and c.get("toManifest"):
            la = S["attrs"].get(c["toManifest"]["components"].get("attributes"), {})
            layer_colors[c["id"]] = la.get("color", "#808080")
    items, hidden = [], set()
    bbox = rg.BoundingBox.Empty
    conflicts = conflicts or set()
    for c in objs:
        alpha = 0.75 if ghost else 0.0
        if c["change"] in ("added", "modified"):
            m = c["toManifest"]
            g = S["geoms"].get(m["components"].get("geometry"))
            if g is None:
                continue
            ol = None if not outline else AMBER if c["id"] in conflicts else (GREEN if c["change"] == "added" else BLUE)
            items.append(Drawable(m["components"]["geometry"], g, object_color(S, d, m, layer_colors), ol, alpha, width, offset))
            bbox.Union(g.GetBoundingBox(False))
            hidden.add(c["id"])          # modified: hide the current version; added: hide a copy the file may already have
        if c["change"] == "deleted" and from_server and c.get("fromManifest"):
            m = c["fromManifest"]
            g = S["geoms"].get(m["components"].get("geometry"))
            hidden.add(c["id"])
            if g is not None and outline:
                items.append(Drawable(m["components"]["geometry"], g, object_color(S, d, m, layer_colors), RED, 0.8, width, offset))
                bbox.Union(g.GetBoundingBox(False))
            continue
        if c["change"] == "deleted":
            obj = d.Objects.FindId(gid(c["id"]))
            if obj is None:
                continue
            key, g = doc_drawable_geo(S, obj)
            if g is None:
                continue
            if not outline:
                hidden.add(c["id"])
                continue
            items.append(Drawable(key, g, obj.Attributes.DrawColor(d), RED, 0.8, width, offset))
            hidden.add(c["id"])
            bbox.Union(g.GetBoundingBox(False))
    if offset is not None and bbox.IsValid:
        bbox.Transform(rg.Transform.Translation(offset))
    return items, hidden, bbox


def summarize(changes):
    n = {"added": 0, "modified": 0, "deleted": 0}
    for c in changes:
        if c["kind"] in ("object", "layer"):
            n[c["change"]] += 1
    return "+%d ~%d -%d" % (n["added"], n["modified"], n["deleted"])


def doc_bbox(d):
    bb = rg.BoundingBox.Empty
    for o in model_objects(d):
        bb.Union(o.Geometry.GetBoundingBox(False))
    return bb


# =============================================================================================== COMPARE

def cmd_compare(S, api, st, d):
    if not st.linked:
        raise TrunkError(400, {"code": "not_linked", "message": "Compare needs a file linked to a repository; pull a commit first."})
    at = ARGS.get("at") or st.base
    target_branch = ARGS.get("branch") or (st.branch if st.branch else "main")
    branches = api.branches()
    explicit = ARGS.get("options")
    # default: every open proposal = unprotected branch not yet merged into the target (children of the target first)
    candidates = explicit or sorted((b["name"] for b in branches if not b.get("protected") and b["name"] not in (st.branch, target_branch)),
                                    key=lambda n: (next(b for b in branches if b["name"] == n).get("parent") != target_branch, n))
    t0 = time.perf_counter()
    previews, options = {}, []
    for name in candidates:
        p = api.post("/merges/preview", {"ours": at, "branch": target_branch, "theirs": name, "compareTo": st.base,
                                         "manifests": True, "author": author()})
        if p["status"] == "upToDate" and not explicit:
            continue
        options.append(name)
        previews[chr(64 + len(options))] = p
        if len(options) == 26:
            break
    if not options:
        raise TrunkError(404, {"code": "no_options", "message": "No unmerged branches to compare against %s." % target_branch})
    letters = [chr(65 + i) for i in range(len(options))]
    load_ms = ms(t0)
    # geometry for every option, once
    t1 = time.perf_counter()
    parts = {L: build_diff(S, api, d, p["changes"], conflicts={c["id"] for c in p["conflicts"]}) for L, p in previews.items()}
    geo_ms = ms(t1)
    say("Compare %d options at %s (%s): previews %.0f ms, geometry %.0f ms." % (len(options), at[7:19], target_branch, load_ms, geo_ms))
    for L, name in zip(letters, options):
        p = previews[L]
        say("  %s  %-28s %-10s %s  conflicts %d  rules %d  %s" % (
            L, name, p["status"], summarize(p["changes"]), len(p["conflicts"]), len(p["autoResolved"]),
            ("approval: " + ",".join(p["approvalReasons"])) if p["requiresApproval"] else ""))

    state = {"active": ARGS.get("show") or "A", "mode": ARGS.get("mode") or "cycle"}
    if state["active"] in options:
        state["active"] = letters[options.index(state["active"])]
    base_bb = doc_bbox(d)

    def render():
        t = time.perf_counter()
        scene = Scene()
        mode, active = state["mode"], state["active"]
        if mode == "side":
            width = (base_bb.Max.X - base_bb.Min.X) if base_bb.IsValid else 50.0
            step = width * 1.3 + 5.0
            scene.bbox = rg.BoundingBox(base_bb.Min, base_bb.Max) if base_bb.IsValid else rg.BoundingBox.Empty
            top = base_bb.Max.Z if base_bb.IsValid else 0.0
            cy = (base_bb.Min.Y + base_bb.Max.Y) / 2 if base_bb.IsValid else 0.0
            x0 = base_bb.Min.X if base_bb.IsValid else 0.0
            front = base_bb.Min.Y if base_bb.IsValid else 0.0
            scene.labels.append((rg.Point3d(x0 + width / 2, cy, top + 3), "file", 22))
            objs = model_objects(d)
            for i, L in enumerate(letters):
                off = rg.Vector3d(step * (i + 1), 0, 0)
                items, hidden, bb = build_diff(S, api, d, previews[L]["changes"], offset=off, width=5.0 if L == active else 3.0,
                                               conflicts={c["id"] for c in previews[L]["conflicts"]})
                xf = rg.Transform.Translation(off)
                scene.copies.extend((o, xf) for o in objs if sid(o.Id) not in hidden and o.Visible)
                scene.items.extend(items)
                scene.labels.append((rg.Point3d(x0 + width / 2 + off.X, cy, top + 3), L, 30 if L == active else 22))
                scene.labels.append((rg.Point3d(x0 + width / 2 + off.X, front - 4, 0), options[letters.index(L)], 11))
                if bb.IsValid:
                    scene.bbox.Union(bb)
                if base_bb.IsValid:
                    b2 = rg.BoundingBox(base_bb.Min, base_bb.Max)
                    b2.Transform(xf)
                    scene.bbox.Union(b2)
        elif mode == "overlay":
            for L in letters:
                is_active = L == active
                items, hidden, bb = build_diff(S, api, d, previews[L]["changes"], ghost=not is_active, width=5.0 if is_active else 2.0,
                                               conflicts={c["id"] for c in previews[L]["conflicts"]})
                scene.items.extend(items)
                if is_active:
                    scene.hidden |= hidden
                scene.bbox.Union(bb)
        else:
            items, hidden, bb = parts[active]
            scene.items, scene.hidden, scene.bbox = list(items), set(hidden), bb
        p = previews[active]
        name = options[letters.index(active)]
        scene.hud = ["COMPARE  %s  %s   [%s]  at %s on %s" % (active, name, {"cycle": "cycle", "overlay": "overlay", "side": "side by side"}[mode],
                                                           at[7:19], target_branch),
                     "%s vs this file   conflicts %d   rule-resolved %d   %s" % (
                         summarize(p["changes"]), len(p["conflicts"]), len(p["autoResolved"]),
                         ("needs approval (" + ", ".join(p["approvalReasons"]) + ")") if p["requiresApproval"] else "no approval needed"),
                     "green = new   red = removed (ghost)   blue = modified   amber = conflict"]
        show(S, scene)
        return ms(t)

    shown = render()
    if state["mode"] == "side" or ARGS.get("frame"):
        frame(S, square=state["mode"] == "side")
    RESULT.update(status="comparing", at=at, branch=target_branch,
                  options={L: {"branch": n, "status": previews[L]["status"], "summary": summarize(previews[L]["changes"]),
                               "conflicts": len(previews[L]["conflicts"]), "autoResolved": len(previews[L]["autoResolved"]),
                               "approvalReasons": previews[L]["approvalReasons"]} for L, n in zip(letters, options)},
                  active=state["active"], mode=state["mode"], firstDrawMs=round(shown, 1))

    if ARGS.get("approve"):
        approve(api, target_branch, ARGS["approve"])
    if is_agent() or ARGS.get("_noprompt"):
        if ARGS.get("bench"):
            bench(render, state, letters)
        return

    # interactive loop
    try:
        while True:
            opts = letters + ["Next", "Cycle", "Overlay", "SideBySide", "Approve", "Merge", "Bench"]
            pick = choose("Compare: option, mode, Approve, Merge (Esc to exit)", opts, "Next")
            if pick in letters:
                state["active"] = pick
            elif pick == "Next":
                state["active"] = letters[(letters.index(state["active"]) + 1) % len(letters)]
            elif pick in ("Cycle", "Overlay", "SideBySide"):
                state["mode"] = {"Cycle": "cycle", "Overlay": "overlay", "SideBySide": "side"}[pick]
            elif pick == "Approve":
                approve(api, target_branch, options[letters.index(state["active"])])
                continue
            elif pick == "Merge":
                show(S, None)
                ARGS["theirs"] = options[letters.index(state["active"])]
                ARGS["into"] = target_branch
                cmd_merge(S, api, st, d)
                return
            elif pick == "Bench":
                bench(render, state, letters)
                continue
            t = render()
            if pick == "SideBySide":
                frame(S, square=True)
            say("%s %s: %.1f ms" % (state["active"], state["mode"], t))
    except UserAbort:
        show(S, None)
        say("Compare closed; the file is unchanged.")


def frame(S, square=False):
    """Zoom to the preview. square: look at the row of side-by-side slots from the front (-Y), slightly above."""
    scene = S.get("scene")
    if scene and scene.bbox.IsValid:
        bb = rg.BoundingBox(scene.bbox.Min, scene.bbox.Max)
        vp = doc().Views.ActiveView.ActiveViewport
        if square and vp.IsPerspectiveProjection:
            c = bb.Center
            dirn = rg.Vector3d(0, 1, -0.55)
            dirn.Unitize()
            vp.SetCameraLocations(c, c - dirn * bb.Diagonal.Length)
        bb.Inflate(bb.Diagonal.Length * 0.02)
        vp.ZoomBoundingBox(bb)
        if square:
            vp.Magnify(1.5, False)   # ZoomBoundingBox frames the bounding sphere; a flat row can come closer
        doc().Views.Redraw()


def bench(render, state, letters, rounds=3):
    times = []
    for _ in range(rounds):
        for L in letters:
            state["active"] = L
            times.append(render())
    say("Bench (%s): %d switches, avg %.1f ms, min %.1f, max %.1f" % (state["mode"], len(times), sum(times) / len(times), min(times), max(times)))
    RESULT["bench"] = {"mode": state["mode"], "avg": round(sum(times) / len(times), 1), "min": round(min(times), 1), "max": round(max(times), 1)}


def approve(api, target_branch, theirs):
    if author()["kind"] != "human":
        raise TrunkError(403, {"code": "forbidden", "message": "Only a human can approve a merge."})
    a = api.post("/approvals", {"branch": target_branch, "theirs": theirs, "by": author(), "note": "Approved in Rhino (COMPARE)"})
    say("Approved merging %s into %s at %s (approval %s)." % (theirs, target_branch, a["ours"][7:19], a["id"][:8]))
    RESULT["approval"] = a["id"]


# =============================================================================================== MERGE

def cmd_merge(S, api, st, d):
    if not st.linked:
        raise TrunkError(400, {"code": "not_linked", "message": "Merge needs a file linked to a repository."})
    theirs = ARGS.get("theirs") or st.branch
    info = api.branch(theirs) or {}
    into = ARGS.get("into") or info.get("parent") or "main"
    if theirs == into:
        raise TrunkError(400, {"code": "invalid_request", "message": "Cannot merge '%s' into itself." % into})
    if theirs == st.branch:
        local = compute_status(S, st, d)
        if local.count:
            if is_agent():
                raise TrunkError(409, {"code": "local_changes", "message": "Commit the %d local changes before merging." % local.count})
            if choose("%d uncommitted changes are not part of the merge" % local.count, ["CommitFirst", "Cancel"], "CommitFirst") != "CommitFirst":
                raise UserAbort()
            cmd_commit(S, api, st, d)
            st = State(doc())

    resolutions = {(r.get("kind", "object"), r["id"]): r["take"] for r in ARGS.get("resolutions") or []}
    while True:
        p = preview_merge(S, api, st, d, into, theirs, resolutions)
        if p["status"] == "upToDate":
            say("'%s' is already merged into '%s'." % (theirs, into))
            show(S, None)
            RESULT.update(status="upToDate")
            return
        if p["conflicts"] and not is_agent():
            resolutions = conflict_session(S, api, st, d, p, into, theirs, resolutions)
            continue
        break

    if p["conflicts"]:
        RESULT.update(status="conflicts", conflicts=p["conflicts"], into=into, theirs=theirs)
        say("%d conflicts; pass RT_ARGS['resolutions'] = [{'id', 'kind', 'take': 'ours'|'theirs'}] or ask a human." % len(p["conflicts"]))
        return
    if p["validation"]:
        show(S, None)
        raise TrunkError(422, {"code": "validation_failed", "message": "; ".join(v["message"] for v in p["validation"])})

    body = {"branch": into, "expectedHead": p["ours"], "theirs": p["theirs"], "author": author(),
            "resolutions": [{"kind": k, "id": i, "take": t} for (k, i), t in resolutions.items()],
            "message": ARGS.get("message") or "Merge %s into %s" % (theirs, into)}
    if is_agent():
        if p["requiresApproval"]:
            approvals = [a for a in api.get("/approvals?branch=%s" % urllib.parse.quote(into, safe=""))["approvals"]
                         if a["status"] == "active" and a["theirs"] == p["theirs"]]
            if not approvals:
                api.post("/commits/%s/annotations" % p["theirs"], {
                    "namespace": "review/merge-request", "status": "pending", "author": author(),
                    "summary": "Merge %s into %s: %s" % (theirs, into, summarize(p["changes"])),
                    "data": {"into": into, "theirs": theirs, "reasons": p["approvalReasons"]}})
                RESULT.update(status="approval_required", reasons=p["approvalReasons"], into=into, theirs=theirs)
                say("Merging %s into %s needs a human approval (%s). Merge request posted; the preview stays on screen."
                    % (theirs, into, ", ".join(p["approvalReasons"])))
                return
            body["approval"] = approvals[0]["id"]
    else:
        reasons = p["approvalReasons"]
        note = ""
        if p["autoResolved"]:
            note = " — %d conflicts resolved by rules (%s)" % (len(p["autoResolved"]), ", ".join(sorted({a["rule"] for a in p["autoResolved"]})))
            body["approveRuleResolutions"] = True
        prompt = "Merge %s into %s%s%s" % (theirs, into, " [APPROVAL: " + ", ".join(reasons) + "]" if reasons else "", note)
        if choose(prompt, ["Merge", "Cancel"], "Merge") != "Merge":
            show(S, None)
            raise UserAbort()
    try:
        res = api.post("/merges", body)
    finally:
        show(S, None)
    say("Merged %s into %s: %s %s%s." % (theirs, into, res["status"], res["commit"][7:19],
                                          (" (approved by %s)" % res["approvedBy"]["id"]) if res.get("approvedBy") else ""))
    RESULT.update(status="merged", result=res["status"], commit=res["commit"], into=into, theirs=theirs)
    switch = ARGS.get("switch")
    if switch is None and not is_agent():
        switch = choose("Switch this file to '%s'" % into, ["Yes", "No"], "No") == "Yes"
    if switch:
        cmd_pull(S, api, State(doc()), doc(), rev=into)


def preview_merge(S, api, st, d, into, theirs, resolutions):
    t0 = time.perf_counter()
    body = {"branch": into, "theirs": theirs, "manifests": True, "author": author(),
            "resolutions": [{"kind": k, "id": i, "take": t} for (k, i), t in resolutions.items()]}
    # what lands on the target (highlighted), and file -> result (to make the view show the merged state)
    p = api.post("/merges/preview", body)
    to_file = api.post("/merges/preview", dict(body, compareTo=st.base))["changes"]
    on_target = {c["id"] for c in p["changes"]}
    neutral = [c for c in to_file if c["id"] not in on_target]
    n_items, n_hidden, _ = build_diff(S, api, d, neutral, outline=False)
    items, hidden, bb = build_diff(S, api, d, p["changes"], conflicts={c["id"] for c in p["conflicts"]}, from_server=True)
    scene = Scene()
    scene.items, scene.hidden, scene.bbox = n_items + items, n_hidden | hidden, bb
    rules = ""
    if p["autoResolved"]:
        rules = "   RULES resolved %d: %s" % (len(p["autoResolved"]), ", ".join(sorted({a["rule"] for a in p["autoResolved"]})))
    scene.hud = ["MERGE  %s -> %s   (%s)" % (theirs, into, p["status"]),
                 "into %s: +%d ~%d -%d   conflicts %d%s" % (into, p["incoming"]["added"], p["incoming"]["modified"], p["incoming"]["deleted"],
                                                          len(p["conflicts"]), rules),
                 "approval: " + (", ".join(p["approvalReasons"]) if p["requiresApproval"] else "not required"),
                 "showing the merged result; relative to %s: green = new   red = removed   blue = modified   amber = conflict" % into]
    show(S, scene)
    say("Merge preview %s -> %s: %s, %s on %s, %d conflicts, %d rule-resolved (%.0f ms)." % (
        theirs, into, p["status"], summarize(p["changes"]), into, len(p["conflicts"]), len(p["autoResolved"]), ms(t0)))
    return p


def conflict_session(S, api, st, d, p, into, theirs, resolutions):
    """Human diffing session: per conflicting entity, flip between our and their version in the viewport, pick one."""
    conflicts = p["conflicts"]
    versions = [v for c in conflicts for v in (c.get("ours"), c.get("theirs")) if v]
    mans = {h: json.loads(b.decode("utf-8")) for h, b in api.batch_get(versions).items()}
    load_attrs(S, api, list(mans.values()))
    load_geometries(S, api, [m for m in mans.values() if m.get("kind") == "object"])
    picks = {}
    i, side = 0, "theirs"
    base_scene = sc.sticky[STICKY].get("scene")
    while i < len(conflicts):
        c = conflicts[i]
        scene = Scene()
        if base_scene:
            scene.items = [it for it in base_scene.items]
            scene.hidden = set(base_scene.hidden)
            scene.bbox = base_scene.bbox
        scene.hidden.add(c["id"])
        bb = rg.BoundingBox.Empty
        for s_, alpha, outline in ((side, 0.0, BLUE), ("ours" if side == "theirs" else "theirs", 0.8, AMBER)):
            v = c.get(s_)
            if v and v in mans and mans[v].get("kind") == "object":
                g = S["geoms"].get(mans[v]["components"].get("geometry"))
                if g is not None:
                    scene.items.append(Drawable(mans[v]["components"]["geometry"] + s_, g, object_color(S, d, mans[v], {}), outline, alpha, 6.0))
                    bb.Union(g.GetBoundingBox(False))
        scene.hud = ["CONFLICT %d/%d  %s %s" % (i + 1, len(conflicts), c["kind"], c["id"][:8]),
                     "%s   fields: %s" % (c["reason"], ", ".join(c.get("fields") or ["whole entity"])),
                     "showing %s (%s) solid, the other side ghosted   ours = %s, theirs = %s" % (
                         side.upper(), "absent" if not c.get(side) else "present", into, theirs)]
        show(S, scene)
        if bb.IsValid:
            d.Views.ActiveView.ActiveViewport.ZoomBoundingBox(rg.BoundingBox(bb.Min - rg.Vector3d(2, 2, 2), bb.Max + rg.Vector3d(2, 2, 2)))
            d.Views.Redraw()
        pick = choose("Conflict %d/%d: keep which side" % (i + 1, len(conflicts)), ["Ours", "Theirs", "Toggle", "AllOurs", "AllTheirs"], "Toggle")
        if pick == "Toggle":
            side = "ours" if side == "theirs" else "theirs"
            continue
        if pick in ("AllOurs", "AllTheirs"):
            for cc in conflicts[i:]:
                picks[(cc["kind"], cc["id"])] = pick[3:].lower()
            break
        picks[(c["kind"], c["id"])] = pick.lower()
        i, side = i + 1, "theirs"
    out = dict(resolutions)
    out.update(picks)
    return out


# =============================================================================================== status / off

def cmd_status(S, api, st, d):
    if not st.linked:
        say("Not linked to a repository.")
        RESULT.update(status="unlinked")
        return
    s = compute_status(S, st, d)
    say("%s @ %s on %s: %d local changes (+%d ~%d -%d), %.0f ms%s" % (
        st.repo, st.base[7:19], st.branch, s.count, len(s.summary["added"]), len(s.summary["modified"]), len(s.summary["deleted"]),
        s.ms, ", full scan" if s.full_scan else ""))
    RESULT.update(status="ok", repo=st.repo, branch=st.branch, base=st.base, changes=s.summary, ms=round(s.ms, 1))


# =============================================================================================== main

RESULT = {"command": CMD}


def main():
    S = session()
    d = doc()
    st = State(d)
    api = Api(st.server, st.repo or ARGS.get("repo") or "")
    if ARGS.get("repo") and not st.repo:
        st.repo = ARGS["repo"]
    t0 = time.perf_counter()
    try:
        {"commit": cmd_commit, "pull": cmd_pull, "branch": cmd_branch, "merge": cmd_merge,
         "compare": cmd_compare, "status": cmd_status,
         "off": lambda *a: (show(S, None), RESULT.update(status="off"))}[CMD](S, api, st, d)
    except UserAbort:
        say("Cancelled.")
        RESULT["status"] = "cancelled"
    except TrunkError as e:
        say("RhinoTrunk: " + str(e))
        RESULT.update(status="error", error=e.body)
    RESULT["ms"] = round(ms(t0), 1)
    gl = S.get("gl")
    if gl is not None and not gl.available and gl.error:
        RESULT["glFallback"] = gl.error
    return RESULT


RT_RESULT = main()
