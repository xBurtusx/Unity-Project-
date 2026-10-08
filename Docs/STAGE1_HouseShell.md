# Stage 1 — House Shell

## What exists now

| File | What it is |
|---|---|
| `Assets/_Project/Data/floorplan.json` | The house, as numbers. **This is the single source of truth.** Everything else is generated from it. |
| `Assets/_Project/Editor/HouseBuilder.cs` | Builds the shell in Unity from that data. |
| `Assets/_Project/Editor/HouseValidator.cs` | Checks the house obeys the rules a real building obeys. |
| `Assets/_Project/Runtime/Architecture/DoorMeta.cs` | Identity on every door — kind, swing, sound set, lock state. |
| `Assets/_Project/Runtime/Architecture/WindowMeta.cs` | Identity on every window — room, operable, curtain state. |

## How to run it

1. Open any scene.
2. Menu: **Horror House → Stage 1 - Build House Shell**
3. Menu: **Horror House → Stage 1 - Validate House**, and read the Console.

Rebuilding deletes and recreates the `HOUSE_SHELL` object. Nothing else in your scene is touched.

## The important idea

**Do not move walls by hand.** Edit `floorplan.json` and rebuild.

Walls are not placed individually. The plan is rasterised onto a quarter-foot grid of
room-occupancy cells, and a wall is generated wherever two adjacent cells belong to different
rooms — or to a room and the outside. Collinear runs are then merged into segments.

The consequence is that walls *cannot* fail to meet, float, or leave gaps, because every wall
comes from a real boundary between two occupied areas. It also means a room that is one inch
away from its neighbour produces a visible defect immediately, rather than a hidden one later.

## What the data check caught

The first draft of the layout had four pairs of rooms occupying the same space and seven doors
in walls that did not exist — the master bedroom and the powder room overlapped, the garage ate
the basement stair, and the house-to-garage door connected two rooms that did not touch.

None of that would have been obvious by looking at the model. All of it is fixed. The current
data passes with zero problems: no overlaps, every door fits its wall, every window fits its
wall, and every room is reachable from the front door.

## Where I deviated from the plan, and why

These are judgement calls, listed in `_deviations` in the JSON. Tell me if any are wrong:

1. **`HallToEntry`** — the Entry at its printed 8'-8" × 7'-0" cannot physically reach the Great
   Room. A short open threshold joins them. The plan appears to show exactly this.
2. **`Mudroom`** — not labelled on the plan, but a house-to-garage door has to land somewhere,
   and the lockers/broom symbols sit in this zone.
3. **Garage 22'-3" wide** instead of 21'-6", so its east wall lines up with the house's. Parking
   space is unaffected.
4. **Pantry 7'-9" wide** instead of 7'-0", to close a gap against the garage wall.
5. **Entry closet omitted** — at 2'-4" × 4'-2" it is better built as a prop in Stage 6.
6. **Front door = `D_GreatFront`**, the double doors on the Great Room's south wall onto the
   front porch. The plan's `ENTRY` room reads as an interior foyer serving the garage, master
   and service rooms, not as the main entrance. **Worth confirming — I may have this backwards.**

Rooms whose dimensions were *inferred* rather than printed are flagged `"assumed": true` and the
validator warns about each one. Those are the first things to correct if something looks off.

## Known limits of this stage

- Walls, floors, ceilings, door leaves and glass are **box geometry**. Correct in size, position,
  thickness and pivot — but untextured and undetailed. That is deliberate: get the building right
  before dressing it.
- Doors have correct hinge pivots and colliders but **no opening behaviour yet**. That is Stage 7.
- No roof beyond the front porch cover.
- The basement is a stair going down into nothing. The plan is main-floor only.
- Window glass has a collider, so you cannot walk through it; curtains come later.

## Roof, site and player (added after the first build)

**Roof** — three gables, in the `roofs` block of the JSON. They are generated from the same
dimensions as the walls, so the roof cannot drift out of register with the building:

| Roof | Ridge | Pitch | Apex above the floor |
|---|---|---|---|
| Main body | east–west at z=17 | 6:12 | 18'-6" |
| Garage | north–south, cross gable | 6:12 | 15'-7" |
| Front porch | north–south | 6:12 | 14'-6" |

The garage and porch ridges deliberately sit below the main ridge, which is what makes a house
read as having a main mass with subordinate wings rather than as a pile of equal boxes.

The roof plane passes through the top of the wall and continues outward *and downward* past it
by the overhang — that is how a real eave works, and why the soffit sits below the wall plate
rather than level with it. Getting this backwards is what makes game roofs look pasted on.

The front porch got a **gable** rather than a shed roof for a specific reason: the plan calls for
a 10'-0" porch ceiling, and a shed roof tying in below the main eave physically cannot hold that
height. This is noted in the JSON.

### Three roof bugs found by looking at it, and fixed

**1. The garage gable was punching through the main roof.** It ran only to z=5.5, but a 15'-7"
ridge doesn't meet the main roof's slope until z=11.125 — so it burst out through the main roof
trailing an open end hanging in mid-air. The porch gable had the same fault.

Where a secondary gable crosses the main one, its ridge has to stop *exactly* where the main
slope has risen to meet it. Solve `mainRidgeHeight - |z - mainRidgeMid| x slope = wingRidgeHeight`.
Stop short and it bursts through; run long and it pokes out the far side.

The validator now checks this arithmetically and names the fix, because it is invisible from
inside the house and easy to reintroduce by changing a pitch.

**2. The ceiling slabs were growing upward through the roof.** Each ceiling sat *on top of* its
stated height and was 9" thick, so within 18" of each eave — where the roof plane is only inches
above the wall plate — it pushed straight out through the roof. That was the pale stripe running
parallel to the eaves. Ceilings now hang down from the stated height and are thin.

**3. The roof deck was centred on the roof plane** rather than sitting on it, so half its
thickness was buried in the wall tops. Real rafters sit *on* the wall plate, so the deck is now
lifted until its underside lies on the plane — measured perpendicular to the slope and converted
back to vertical, which is why the pitch appears in that calculation.

**Site** — grass lot, a six-foot board fence enclosing the rear garden (it leaves the house's west
wall, runs the lot line around the back, and returns to the garage's east wall, the way a real
rear-yard fence is run), plus street, sidewalk, driveway up to the garage door, and a front walk
centred on the front door. All in the `site` block.

**Player** — `Horror House → Stage 3 - Create Player`. Spawns on the front walk facing the house.

> W A S D walk · mouse look · Left Shift hurry · Left Ctrl crouch · Escape frees the cursor

Walk speed is **1.35 m/s**, which is real human pace and deliberately slower than most games run.
This matters more than it sounds: moving at a believable speed is what gives a room a size you can
feel. Run speed is 3.1 m/s — hurried, not athletic, because this is a frightened person and not a
sprinter. Head bob is 2.2 cm vertical and 1.3 cm lateral, dipping twice per stride and swaying
once, as walking actually does. Heavy bob reads as seasickness, not presence.

Everything is tunable in the Inspector on the Player object.

## Stage 5 — textures and the VHS night look

**Textures are generated, not downloaded.** `Horror House → Stage 5 - Generate Textures` builds
13 tiling albedo + normal map pairs procedurally and wires them into the materials. Thirteen
surfaces: drywall, ceiling, siding, trim, wood floor, tile, carpet, concrete, shingle, grass,
asphalt, paving, fence.

Two things in there matter more than the patterns themselves:

**Grime.** A clean surface reads as a 3D model; an unevenly dirty one reads as a place someone
lives in. Every material gets a low-frequency blotch layer that both darkens *and desaturates*
unevenly — because real grime is grey, not just dark. One constant, `TextureFactory.Grime`,
currently `0.45`. Raise it for squalor, drop it for a show home.

**Tiling in world units.** The mesh UVs are in metres, so a texture at scale 1 repeats once per
metre regardless of wall size. A floorboard is the same width in the bedroom as in the garage.
That consistency is a large part of why a building stops looking like a collage.

**Night lighting and grade** — `Horror House → Stage 5 - Night Lighting and VHS Look`.

The important decision: **night is not black.** A black screen isn't frightening, it's unreadable,
and players just turn their monitor up until the mood is gone. So the moonlight is weak (0.16) but
never zero, shadow strength is 0.82 rather than 1.0, ambient is a cool three-colour gradient, and
the grade *lifts the blacks* — which is also exactly what tape does, since VHS could never hold a
true black. Fog does the outdoor work: hides the edge of the world and makes distance uncertain.

The grade: ACES tonemap, white balance pushed cold and slightly green (consumer tape never got
indoor white balance right), saturation −26, lifted blacks, film grain 0.42, vignette, chromatic
aberration 0.18, a little lens distortion and barely-there motion blur.

**`VhsOverlay`** (on the Player) adds what a grade can't: scanlines, the dirty head-switching band
along the bottom of the frame, and a rare tracking wobble — averaging one every 26 seconds, brief.
Built as a screen-space Canvas rather than a shader so it costs nothing and every part can be
dialled to zero independently. The brief says the player must still see the house, so the defaults
are deliberately restrained.

### Sky banding

Darkening the sky with exposure alone crushed its gradient into visible blocks — an 8-bit sky has
very few steps left once it's that dim. The skybox now renders brighter (exposure 0.62) and the
colour grade brings it down (post-exposure −0.55), where there's far more precision to work with.

## Driving the build without clicking menus

`Assets/_Project/Editor/AutoRun.cs` lets the build be triggered from outside the Editor. Write a
file at `Temp/horrorhouse_autorun` containing any of `build`, `player`, `validate`, `aerial`,
`front`, `rear`, then make Unity refresh (Ctrl+R, or just give it focus). The next recompile runs
those steps and saves the scene. The marker is deleted before it runs, so it can never loop.

`Temp/validation_report.txt` holds the last validation report, readable without the Editor open.

## Next

**Stage 3 (player controller) before Stage 2 (door/window detail).** You cannot judge whether a
house feels right from the Scene view — only from standing in it at eye height. A first-person
controller is about an hour of work and it changes every decision after it.
