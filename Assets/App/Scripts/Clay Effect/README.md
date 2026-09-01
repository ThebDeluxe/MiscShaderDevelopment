# Clay Effect

Runtime mesh deformation for clay-like characters: surfaces dent where they are touched, the
dents fade over time, and a blob character can additionally squash, stretch, morph between
shapes and absorb other blobs.

Everything is vertex displacement in a shader. Nothing rebuilds meshes at runtime, there are
no compute shaders and no readbacks, so it runs on WebGL 2.

---

## How the dent effect works

The core idea is a **dent map**: a small RenderTexture holding one texel per vertex, storing
that vertex's displacement in object space.

1. `DentVertexUVGenerator` bakes a mapping at startup — vertex *i* gets texel *(i % width,
   i / width)*. It writes those coordinates into a UV channel and builds a point-topology
   copy of the mesh.
2. `DentContactSource` finds what the character is touching and describes each surface as a
   *stamp* — a plane, a sphere, a capsule.
3. `DentManager` draws the point mesh into the dent map each frame. One point per vertex, so
   each texel is written by exactly the vertex it belongs to. The shader evaluates every
   stamp at that vertex's world position and stores the result.
4. The character's own shader reads the dent map at the same UV and offsets the vertex.

Decay happens inside that stamp pass: each texel fades toward zero a little every frame, so
dents recover on their own.

**Why a point mesh.** The mapping is by vertex *index*, which has no spatial adjacency — two
neighbouring texels are unrelated vertices. Rasterising triangles would smear each one across
a meaningless area. Points write exactly one texel each.

**Why per-material, not global.** Each character owns its own map and its own material
instance. A global texture would be last-writer-wins between characters.

---

## Setting up a character

### Required

| Component | Goes on | Purpose |
|---|---|---|
| `DentVertexUVGenerator` | the mesh renderer | Bakes the index mapping and point mesh |
| `DentManager` | the mesh renderer, or above it | Owns the dent map and runs the stamp pass |
| `DentContactSource` | the Rigidbody's object | Finds contacts and drives stamps |
| `ClayCharacterController` | the character root | Movement, jumping, ground detection |

The mesh **must have Read/Write Enabled** in its import settings. Without it, `mesh.vertices`
returns empty in a build — the mesh renders correctly in the editor and vanishes when built.

### Optional

| Component | Mode | Purpose |
|---|---|---|
| `ClayHeightFieldSampler` | any | Samples terrain as a height map instead of plane stamps |
| `SquashStretch` | blob | Whole-body squash driven by jumps and landings |
| `ClayShapeMorph` | blob | Morphs between pancake, plank, cone and so on |
| `ClayShapeColliders` | blob | Builds colliders matching the current shape |
| `BlobMerger` | blob | Absorbs and throws `ClayBlob` objects |
| `ClayMorphTrigger` | blob | A volume that changes the character's shape on contact |
| `DentDebugView` | any | On-screen dent map, channel isolation, per-phase timings |

### Shader graph properties

The character's shader needs these, all **Per Material** scope (Global cannot be set per
object, so several characters would overwrite each other):

- `_CustomRT_Dents` (Texture2D) and `_Max_Dent_Depth` (Float) — the dent map and its scale
- `_SquashAxis`, `_SquashAmount`, `_SquashPivot` — for `SquashStretch`
- `_ShapeAxis`, `_ShapePivot`, `_ShapeSize`, `_ShapeParams`, `_ShapeSpread`, `_ShapeAmount` —
  for `ClayShapeMorph`

Vertex position chain, in order:

```
Position (Object) -> ApplyClayShape -> Add (dent offset) -> ApplySquashStretch -> Position
```

The dent map is an **offset** and must be added. `ApplyClayShape` and `ApplySquashStretch` are
**transforms** — they replace the position they are given, so they chain directly. Mixing the
two conventions at one Add node doubles the character's scale, which is the usual symptom of
getting this wrong.

---

## Character kinds

`ClayCharacterController.Character Kind` decides which features apply.

**Blob** — rolls with torque, and everything applies.

**Humanoid** — walks upright. `SquashStretch`, `ClayShapeMorph`, `ClayShapeColliders` and
`BlobMerger` are disabled at runtime, because each scales the whole mesh about a single point
and would squash a jointed character's head into its feet.

Denting works either way. On a humanoid, author the colliders by hand and list them under
**Probe Origins** on `DentContactSource` — otherwise everything probes from one centre, and an
arm hidden behind the torso never registers a contact.

---

## Terrain

Terrain is sampled as a height field rather than described with plane stamps, because terrain
genuinely is one. Plane stamps disagree where they meet, and which of them exist changes as
the character moves, so the seams travel.

Add `ClayHeightFieldSampler`, set its `Ground Mask` to the terrain layer, and set
**`Height Field Layers` on `DentContactSource` to the same layer**. Without that second step
the ground is described twice and the two representations fight.

Keep the values under **Matching The Contact Path** in step with `DentContactSource`. If they
drift, terrain dents harder or fades slower than every other surface for the same contact —
which reads as a bug in the terrain rather than as two numbers disagreeing.

A height map cannot represent a wall, an overhang or a ceiling. Those keep the ordinary
contact path, which is why both run together.

---

## Things that fail silently

Most of the time-consuming bugs in this system produce no error at all.

**A missing reference that has a fallback.** `DentContactSource.shapeColliders`,
`BlobMerger.morph`, `ClayShapeColliders.controller` — each falls back to treating the
character as a plain sphere, which looks almost right until the character morphs. The custom
inspectors warn about these.

**`GetComponentInParent` after a merge.** Absorbing a blob reparents it under the character,
so anything on the blob that searches upward will find the *character's* components. Use
`attachedRigidbody` comparisons to tell self from world — that test is symmetric and keeps
working as blobs come and go.

**Two colliders where one is expected.** Any collider on a Rigidbody contributes to its
collision. `ClayShapeColliders` disables every other one on the body for this reason, blobs
excepted. A leftover sphere is what stops a flat shape lying flat.

**Sink is deliberate, not an error.** The visible mesh is larger than the collider on purpose —
that gap is what the dent flattens. Anything reading the reported overlap has to subtract the
intended amount first. Treating all of it as penetration to escape floats the character.

**The shader and the C# must agree.** `ClayShape.hlsl` has a mirror in `ClayShapeMorph`, and
`DentStamp.hlsl` has one in `DentManager`. The GPU cannot query colliders, so this duplication
is unavoidable. If the maths changes in one, change it in the other — the symptom is contacts
landing where the mesh is not.

---

## Performance

Measured at roughly **0.1 ms per character** on a desktop CPU, most of it in
`DentContactSource`.

Profiler markers, with deep profiling **off**:

```
Dent.CollectSources   Dent.CacheSamples   Dent.PressDepths
Dent.BuildDentArrays  Dent.UploadAndDraw
DentContact.Probe     DentContact.Track   DentContact.Apply
```

`DentDebugView` also reads these in-build, which is easier than attaching a profiler to a
WebGL player.

The main savings already in place:

- **Idle skip.** An object touching nothing with no dents left to fade skips its whole update.
  Most objects, most of the time, once there are more than a handful.
- **Snapshotting.** Source transforms are read once per frame rather than per sample.

If more is needed at scale, the next lever is **time-slicing** — updating object *i* on frame
`i % N`. Decay is computed from elapsed time, so it self-corrects.

`DentLOD` exists to throttle by screen coverage, with hysteresis and a depth fade, but is not
required.

---

## WebGL notes

Compatible, with one thing worth verifying in an actual build: the stamp pass rasterises
points, and on GL the point size comes from `gl_PointSize`, which the shader must write.
If the dent map comes out empty or scrambled in a WebGL build while looking fine on desktop,
that is the first thing to check.

Everything else avoids the usual traps — no compute, no readback, no float texture filtering
(the dent map is point-sampled anyway), and vertex texture fetch is guaranteed in WebGL 2.

Keep `DENT_MAX` at 16. At 32 the stamp shader's fragment stage would need more uniform vectors
than WebGL 2 guarantees.

---

## Known limitations

- **Rolling assumes a sphere.** A morphed blob collides as its new shape but still rolls as a
  ball. Spin rate is matched to travel, which is a deliberate cheat — nothing on a featureless
  clay surface reveals the contact slipping, but surface texture would.
- **Normals are not recalculated** after displacement, so lighting is that of the undeformed
  mesh.
- **Concave meshes are sampled, not solved.** Primitives and convex meshes are handled exactly;
  anything else falls back to raycasting, which is approximate and the noisiest path.
- **A humanoid has no squash or morph.** Skeletal animation does that better; denting layers on
  top as surface detail.
