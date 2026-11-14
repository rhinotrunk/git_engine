#! python3
"""
GLSL outlines for the RhinoTrunk display conduit (Windows, Rhino 8 OpenGL pipeline).

RhinoCommon has no API for custom shaders, so this draws with raw OpenGL through ctypes while
Rhino's context is current (inside a DisplayConduit callback). Every GL state it touches is
saved and restored, so Rhino's own drawing is unaffected.

Technique, per mesh, drawn BEFORE the mesh itself so the outline sits beneath it:
  1. mask   - rasterize the mesh into the stencil buffer (no color, no depth writes)
  2. hull   - draw the mesh again with the vertex shader pushing every vertex outward along its
              (welded) normal by N screen pixels; the fragment shader writes the outline color
              only where the stencil is NOT set, i.e. in a band around the silhouette.
              Depth-tested, so geometry in front still hides the outline.
  3. clear  - rasterize the mesh again to reset its stencil pixels.
Then the caller draws the shaded/ghosted mesh on top. Because the band is outside the mask,
it also works under transparent (ghosted) meshes.

If anything fails (no stencil, GL < 3.3, non-Windows), `available` is False and the caller
falls back to RhinoCommon wires.
"""
import ctypes
import math

GL_FLOAT, GL_UNSIGNED_INT, GL_TRIANGLES = 0x1406, 0x1405, 0x0004
GL_ARRAY_BUFFER, GL_ELEMENT_ARRAY_BUFFER, GL_STATIC_DRAW = 0x8892, 0x8893, 0x88E4
GL_VERTEX_SHADER, GL_FRAGMENT_SHADER = 0x8B31, 0x8B30
GL_COMPILE_STATUS, GL_LINK_STATUS = 0x8B81, 0x8B82
GL_DEPTH_TEST, GL_STENCIL_TEST, GL_CULL_FACE, GL_BLEND = 0x0B71, 0x0B90, 0x0B44, 0x0BE2
GL_CURRENT_PROGRAM, GL_VERTEX_ARRAY_BINDING, GL_ARRAY_BUFFER_BINDING = 0x8B8D, 0x85B5, 0x8894
GL_DEPTH_WRITEMASK, GL_COLOR_WRITEMASK, GL_DEPTH_FUNC, GL_VIEWPORT = 0x0B72, 0x0C23, 0x0B74, 0x0BA2
GL_STENCIL_FUNC, GL_STENCIL_REF, GL_STENCIL_VALUE_MASK = 0x0B92, 0x0B97, 0x0B93
GL_STENCIL_FAIL, GL_STENCIL_PASS_DEPTH_FAIL, GL_STENCIL_PASS_DEPTH_PASS, GL_STENCIL_WRITEMASK = 0x0B94, 0x0B95, 0x0B96, 0x0B98
GL_STENCIL_BITS = 0x0D57
GL_ALWAYS, GL_NOTEQUAL, GL_LEQUAL, GL_KEEP, GL_REPLACE = 0x0207, 0x0205, 0x0203, 0x1E00, 0x1E01
GL_FRAMEBUFFER_BINDING, GL_FRAMEBUFFER, GL_STENCIL, GL_FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE = 0x8CA6, 0x8D40, 0x1802, 0x8CD0
GL_STENCIL_ATTACHMENT, GL_DEPTH_STENCIL_ATTACHMENT = 0x8D20, 0x821A

VERT = b"""#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNrm;
uniform mat4 uMVP;
uniform vec2 uViewport;
uniform float uWidth;
void main() {
    vec4 c = uMVP * vec4(aPos, 1.0);
    if (uWidth > 0.0) {
        // screen-space direction of the normal: derivative of the perspective divide
        vec4 n = uMVP * vec4(aNrm, 0.0);
        vec2 d = (n.xy * c.w - c.xy * n.w) * uViewport;
        float l = length(d);
        if (l > 1e-9) c.xy += (d / l) * (2.0 * uWidth / uViewport) * c.w;
    }
    gl_Position = c;
}
"""

FRAG = b"""#version 330 core
uniform vec4 uColor;
out vec4 fragColor;
void main() { fragColor = uColor; }
"""


class _GL(object):
    """Lazily bound OpenGL entry points (core 1.1 from opengl32.dll, the rest via wglGetProcAddress)."""

    def __init__(self):
        self.dll = ctypes.WinDLL("opengl32")
        self.dll.wglGetProcAddress.restype = ctypes.c_void_p
        self.dll.wglGetProcAddress.argtypes = [ctypes.c_char_p]
        self.dll.wglGetCurrentContext.restype = ctypes.c_void_p
        F = ctypes.WINFUNCTYPE
        u, i, f, p, b = ctypes.c_uint, ctypes.c_int, ctypes.c_float, ctypes.c_void_p, ctypes.c_ubyte
        sig = {
            "glEnable": (None, u), "glDisable": (None, u), "glIsEnabled": (b, u),
            "glGetIntegerv": (None, u, ctypes.POINTER(i)), "glGetBooleanv": (None, u, ctypes.POINTER(b)),
            "glDepthMask": (None, b), "glColorMask": (None, b, b, b, b), "glDepthFunc": (None, u),
            "glStencilFunc": (None, u, i, u), "glStencilOp": (None, u, u, u), "glStencilMask": (None, u),
            "glDrawElements": (None, u, i, u, p), "glGetError": (u,),
            "glCreateShader": (u, u), "glShaderSource": (None, u, i, ctypes.POINTER(ctypes.c_char_p), p),
            "glCompileShader": (None, u), "glGetShaderiv": (None, u, u, ctypes.POINTER(i)),
            "glGetShaderInfoLog": (None, u, i, p, ctypes.c_char_p),
            "glCreateProgram": (u,), "glAttachShader": (None, u, u), "glLinkProgram": (None, u),
            "glGetProgramiv": (None, u, u, ctypes.POINTER(i)), "glUseProgram": (None, u),
            "glGetUniformLocation": (i, u, ctypes.c_char_p),
            "glUniformMatrix4fv": (None, i, i, b, ctypes.POINTER(f)), "glUniform2f": (None, i, f, f),
            "glUniform1f": (None, i, f), "glUniform4f": (None, i, f, f, f, f),
            "glGenBuffers": (None, i, ctypes.POINTER(u)), "glBindBuffer": (None, u, u),
            "glBufferData": (None, u, ctypes.c_ssize_t, p, u), "glDeleteBuffers": (None, i, ctypes.POINTER(u)),
            "glGenVertexArrays": (None, i, ctypes.POINTER(u)), "glBindVertexArray": (None, u),
            "glDeleteVertexArrays": (None, i, ctypes.POINTER(u)),
            "glEnableVertexAttribArray": (None, u), "glVertexAttribPointer": (None, u, i, u, b, i, p),
            "glGetFramebufferAttachmentParameteriv": (None, u, u, u, ctypes.POINTER(i)),
        }
        for name, (res, *args) in sig.items():
            addr = getattr(self.dll, name, None) if name in ("glEnable", "glDisable", "glIsEnabled", "glGetIntegerv",
                                                              "glGetBooleanv", "glDepthMask", "glColorMask", "glDepthFunc",
                                                              "glStencilFunc", "glStencilOp", "glStencilMask",
                                                              "glDrawElements", "glGetError") else None
            if addr is not None:
                fn = addr
                fn.restype, fn.argtypes = res, list(args)
            else:
                ptr = self.dll.wglGetProcAddress(name.encode())
                if not ptr:
                    raise RuntimeError("OpenGL function missing: " + name)
                fn = F(res, *args)(ptr)
            setattr(self, name[2:], fn)

    def geti(self, pname, n=1):
        arr = (ctypes.c_int * max(n, 4))()
        self.GetIntegerv(pname, arr)
        return list(arr[:n]) if n > 1 else arr[0]

    def getb(self, pname, n=1):
        arr = (ctypes.c_ubyte * max(n, 4))()
        self.GetBooleanv(pname, arr)
        return list(arr[:n]) if n > 1 else arr[0]


class _MeshBuffers(object):
    __slots__ = ("vao", "vbo", "ebo", "count")


def _mesh_arrays(mesh):
    """Interleaved positions + welded normals (vertices at the same position share one averaged normal,
    so the expanded hull has no cracks at hard edges) and triangle indices."""
    mesh.Normals.ComputeNormals()
    n = mesh.Vertices.Count
    pos = [(float(v.X), float(v.Y), float(v.Z)) for v in mesh.Vertices]
    nrm = [(float(v.X), float(v.Y), float(v.Z)) for v in mesh.Normals]
    groups = {}
    for k, p in enumerate(pos):
        groups.setdefault((round(p[0], 6), round(p[1], 6), round(p[2], 6)), []).append(k)
    welded = [None] * n
    for ks in groups.values():
        sx = sum(nrm[k][0] for k in ks); sy = sum(nrm[k][1] for k in ks); sz = sum(nrm[k][2] for k in ks)
        l = math.sqrt(sx * sx + sy * sy + sz * sz) or 1.0
        for k in ks:
            welded[k] = (sx / l, sy / l, sz / l)
    data = []
    for k in range(n):
        data.extend(pos[k]); data.extend(welded[k])
    idx = []
    for f in mesh.Faces:
        idx.extend((f.A, f.B, f.C))
        if f.IsQuad:
            idx.extend((f.A, f.C, f.D))
    return data, idx


class OutlineRenderer(object):
    def __init__(self):
        self.available = True
        self.error = None
        self.gl = None
        self.contexts = {}      # GL context handle -> {"program", "loc", "meshes": {key: _MeshBuffers}}
        self.arrays = {}        # mesh key -> (vertex floats, indices); shared between contexts

    # ------------------------------------------------------------------ setup
    def _context(self):
        if self.gl is None:
            self.gl = _GL()
        gl = self.gl
        hglrc = gl.dll.wglGetCurrentContext()
        if not hglrc:
            raise RuntimeError("no current OpenGL context")
        ctx = self.contexts.get(hglrc)
        if ctx is None:
            ctx = {"program": self._program(), "meshes": {}}
            ctx["loc"] = {n: gl.GetUniformLocation(ctx["program"], n.encode()) for n in ("uMVP", "uViewport", "uWidth", "uColor")}
            self.contexts[hglrc] = ctx
        return ctx

    def _shader(self, kind, src):
        gl = self.gl
        sh = gl.CreateShader(kind)
        arr = (ctypes.c_char_p * 1)(src)
        gl.ShaderSource(sh, 1, arr, None)
        gl.CompileShader(sh)
        ok = ctypes.c_int()
        gl.GetShaderiv(sh, GL_COMPILE_STATUS, ctypes.byref(ok))
        if not ok.value:
            log = ctypes.create_string_buffer(2048)
            gl.GetShaderInfoLog(sh, 2048, None, log)
            raise RuntimeError("shader compile failed: " + log.value.decode(errors="replace"))
        return sh

    def _program(self):
        gl = self.gl
        prog = gl.CreateProgram()
        gl.AttachShader(prog, self._shader(GL_VERTEX_SHADER, VERT))
        gl.AttachShader(prog, self._shader(GL_FRAGMENT_SHADER, FRAG))
        gl.LinkProgram(prog)
        ok = ctypes.c_int()
        gl.GetProgramiv(prog, GL_LINK_STATUS, ctypes.byref(ok))
        if not ok.value:
            raise RuntimeError("shader link failed")
        return prog

    def _buffers(self, ctx, key, mesh):
        mb = ctx["meshes"].get(key)
        if mb is not None:
            return mb
        gl = self.gl
        arrays = self.arrays.get(key)
        if arrays is None:
            arrays = self.arrays[key] = _mesh_arrays(mesh)
        data, idx = arrays
        mb = _MeshBuffers()
        h = ctypes.c_uint()
        gl.GenVertexArrays(1, ctypes.byref(h)); mb.vao = h.value
        gl.BindVertexArray(mb.vao)
        gl.GenBuffers(1, ctypes.byref(h)); mb.vbo = h.value
        gl.BindBuffer(GL_ARRAY_BUFFER, mb.vbo)
        vbuf = (ctypes.c_float * len(data))(*data)
        gl.BufferData(GL_ARRAY_BUFFER, ctypes.sizeof(vbuf), vbuf, GL_STATIC_DRAW)
        gl.GenBuffers(1, ctypes.byref(h)); mb.ebo = h.value
        gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, mb.ebo)
        ibuf = (ctypes.c_uint * len(idx))(*idx)
        gl.BufferData(GL_ELEMENT_ARRAY_BUFFER, ctypes.sizeof(ibuf), ibuf, GL_STATIC_DRAW)
        gl.EnableVertexAttribArray(0)
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 24, ctypes.c_void_p(0))
        gl.EnableVertexAttribArray(1)
        gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, 24, ctypes.c_void_p(12))
        mb.count = len(idx)
        ctx["meshes"][key] = mb
        return mb

    def forget(self, keys=None):
        """Drop cached CPU arrays (GPU buffers of dropped keys are rebuilt on next use)."""
        if keys is None:
            self.arrays.clear()
            for ctx in self.contexts.values():
                ctx["meshes"].clear()
        else:
            for k in keys:
                self.arrays.pop(k, None)
                for ctx in self.contexts.values():
                    ctx["meshes"].pop(k, None)

    # ------------------------------------------------------------------ state save/restore
    def _save(self):
        gl = self.gl
        return {
            "program": gl.geti(GL_CURRENT_PROGRAM), "vao": gl.geti(GL_VERTEX_ARRAY_BINDING),
            "abuf": gl.geti(GL_ARRAY_BUFFER_BINDING),
            "enabled": {c: gl.IsEnabled(c) for c in (GL_DEPTH_TEST, GL_STENCIL_TEST, GL_CULL_FACE)},
            "depthmask": gl.getb(GL_DEPTH_WRITEMASK), "colormask": gl.getb(GL_COLOR_WRITEMASK, 4),
            "depthfunc": gl.geti(GL_DEPTH_FUNC),
            "sfunc": gl.geti(GL_STENCIL_FUNC), "sref": gl.geti(GL_STENCIL_REF), "smask": gl.geti(GL_STENCIL_VALUE_MASK),
            "sfail": gl.geti(GL_STENCIL_FAIL), "szfail": gl.geti(GL_STENCIL_PASS_DEPTH_FAIL),
            "szpass": gl.geti(GL_STENCIL_PASS_DEPTH_PASS), "swmask": gl.geti(GL_STENCIL_WRITEMASK),
        }

    def _restore(self, s):
        gl = self.gl
        gl.UseProgram(s["program"])
        gl.BindVertexArray(s["vao"])
        gl.BindBuffer(GL_ARRAY_BUFFER, s["abuf"])
        for cap, on in s["enabled"].items():
            (gl.Enable if on else gl.Disable)(cap)
        gl.DepthMask(s["depthmask"])
        gl.ColorMask(*s["colormask"])
        gl.DepthFunc(s["depthfunc"])
        gl.StencilFunc(s["sfunc"], s["sref"], s["smask"] & 0xFFFFFFFF)
        gl.StencilOp(s["sfail"], s["szfail"], s["szpass"])
        gl.StencilMask(s["swmask"] & 0xFFFFFFFF)

    # ------------------------------------------------------------------ drawing
    def draw(self, viewport, items, width_px=4.0):
        """items: iterable of (key, mesh, (r, g, b), offset_vector_or_None). Call before drawing the meshes."""
        if not self.available:
            return False
        items = list(items)
        if not items:
            return True
        try:
            ctx = self._context()
            gl = self.gl
            saved = self._save()
            try:
                import Rhino
                w2c = viewport.GetTransform(Rhino.DocObjects.CoordinateSystem.World, Rhino.DocObjects.CoordinateSystem.Clip)
                vp = gl.geti(GL_VIEWPORT, 4)
                loc = ctx["loc"]
                gl.UseProgram(ctx["program"])
                gl.Uniform2f(loc["uViewport"], float(vp[2]), float(vp[3]))
                gl.Enable(GL_STENCIL_TEST)
                gl.Enable(GL_DEPTH_TEST)
                gl.Disable(GL_CULL_FACE)
                gl.StencilMask(0xFF)
                gl.DepthMask(0)
                mats = {}
                for key, mesh, rgb, offset in items:
                    mb = self._buffers(ctx, key, mesh)
                    okey = None if offset is None else (offset.X, offset.Y, offset.Z)
                    m = mats.get(okey)
                    if m is None:
                        t = w2c if offset is None else w2c * Rhino.Geometry.Transform.Translation(offset)
                        m = mats[okey] = (ctypes.c_float * 16)(*[float(t[r, c]) for r in range(4) for c in range(4)])
                    gl.UniformMatrix4fv(loc["uMVP"], 1, 1, m)
                    gl.BindVertexArray(mb.vao)
                    # 1. mask
                    gl.ColorMask(0, 0, 0, 0)
                    gl.DepthFunc(GL_ALWAYS)
                    gl.StencilFunc(GL_ALWAYS, 1, 0xFF)
                    gl.StencilOp(GL_KEEP, GL_KEEP, GL_REPLACE)
                    gl.Uniform1f(loc["uWidth"], 0.0)
                    gl.DrawElements(GL_TRIANGLES, mb.count, GL_UNSIGNED_INT, None)
                    # 2. hull, outside the mask only, depth-tested against the scene
                    gl.ColorMask(1, 1, 1, 1)
                    gl.DepthFunc(GL_LEQUAL)
                    gl.StencilFunc(GL_NOTEQUAL, 1, 0xFF)
                    gl.StencilOp(GL_KEEP, GL_KEEP, GL_KEEP)
                    gl.Uniform1f(loc["uWidth"], float(width_px))
                    gl.Uniform4f(loc["uColor"], rgb[0] / 255.0, rgb[1] / 255.0, rgb[2] / 255.0, 1.0)
                    gl.DrawElements(GL_TRIANGLES, mb.count, GL_UNSIGNED_INT, None)
                    # 3. clear the mask
                    gl.ColorMask(0, 0, 0, 0)
                    gl.DepthFunc(GL_ALWAYS)
                    gl.StencilFunc(GL_ALWAYS, 0, 0xFF)
                    gl.StencilOp(GL_KEEP, GL_KEEP, GL_REPLACE)
                    gl.Uniform1f(loc["uWidth"], 0.0)
                    gl.DrawElements(GL_TRIANGLES, mb.count, GL_UNSIGNED_INT, None)
            finally:
                self._restore(saved)
            return True
        except Exception as e:  # never break Rhino's frame: disable and fall back
            self.available = False
            self.error = repr(e)
            return False
