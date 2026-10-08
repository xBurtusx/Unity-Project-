"""
Batch-converts Poly Haven .blend files into FBX that Unity can import directly.

Run (Blender does not need to be opened - this is headless):

    blender --background --python Tools/blend_to_fbx.py -- <sourceDir> <outputDir>

Why convert at all, rather than letting Unity import .blend directly? Unity's .blend
support works by silently shelling out to Blender on every import, which is slow, breaks
whenever Blender moves or updates, and leaves the project unopenable on a machine without
Blender. Converting once to FBX removes that dependency: the project then builds anywhere.

Three things matter for the result to be usable in Unity:

  AXES. Blender is Z-up, Unity is Y-up. Exporting with axis_forward='-Z' and axis_up='Y'
  is what makes a model arrive upright and facing the expected way instead of laid on its back.

  SCALE. Poly Haven models are authored at true real-world metres, which is exactly what
  Unity wants, so apply_scale_options keeps that 1:1 rather than folding in a unit conversion.

  TEXTURES. path_mode='COPY' with embed_textures writes the maps into the FBX, so a single
  file carries everything and nothing breaks if the source folder is later moved or deleted.
"""

import bpy
import os
import sys


def argv_after_double_dash():
    """Blender passes its own arguments too; ours are the ones after '--'."""
    if "--" not in sys.argv:
        return []
    return sys.argv[sys.argv.index("--") + 1:]


def convert(blend_path, out_dir):
    name = os.path.splitext(os.path.basename(blend_path))[0]
    # Poly Haven suffixes every file with its texture resolution; it is noise in an asset name
    # and it makes the keyword matcher's job harder, so drop it.
    for suffix in ("_4k", "_2k", "_1k", "_8k"):
        if name.endswith(suffix):
            name = name[: -len(suffix)]

    out_path = os.path.join(out_dir, name + ".fbx")
    if os.path.exists(out_path):
        print(f"[skip] {name} - already converted")
        return True

    try:
        bpy.ops.wm.open_mainfile(filepath=blend_path)
    except Exception as e:  # a corrupt or version-mismatched file must not kill the batch
        print(f"[FAIL] {name} - could not open: {e}")
        return False

    # Drop cameras and lights: the scene's own lighting belongs to Unity, not to each prop.
    for obj in list(bpy.data.objects):
        if obj.type in {"CAMERA", "LIGHT"}:
            bpy.data.objects.remove(obj, do_unlink=True)

    # Only objects present in the VIEW LAYER can be selected. Some .blend files carry extras
    # that live in bpy.data but not in the view layer - rig widgets, objects in excluded
    # collections - and calling select_set on one of those raises, which would otherwise abort
    # the whole batch on a single awkward file.
    meshes = [o for o in bpy.context.view_layer.objects if o.type == "MESH"]
    if not meshes:
        print(f"[FAIL] {name} - contains no meshes in the view layer")
        return False

    bpy.ops.object.select_all(action="DESELECT")
    selected = []
    for o in meshes:
        try:
            o.select_set(True)
            selected.append(o)
        except Exception as e:
            print(f"       (skipped '{o.name}': {e})")

    if not selected:
        print(f"[FAIL] {name} - nothing selectable")
        return False

    bpy.context.view_layer.objects.active = selected[0]
    meshes = selected

    try:
        bpy.ops.export_scene.fbx(
            filepath=out_path,
            use_selection=True,
            apply_unit_scale=True,
            apply_scale_options="FBX_SCALE_NONE",   # keep true metres
            axis_forward="-Z",                      # Blender Z-up -> Unity Y-up
            axis_up="Y",
            object_types={"MESH", "EMPTY"},
            use_mesh_modifiers=True,
            mesh_smooth_type="FACE",
            path_mode="COPY",
            embed_textures=True,
            bake_space_transform=False,
        )
    except Exception as e:
        print(f"[FAIL] {name} - export failed: {e}")
        return False

    size_mb = os.path.getsize(out_path) / (1024 * 1024)
    print(f"[ok]   {name}  ({len(meshes)} mesh(es), {size_mb:.1f} MB)")
    return True


def main():
    args = argv_after_double_dash()
    if len(args) < 2:
        print("usage: blender --background --python blend_to_fbx.py -- <sourceDir> <outputDir>")
        return

    src_dir, out_dir = args[0], args[1]
    os.makedirs(out_dir, exist_ok=True)

    blends = []
    for root, _dirs, files in os.walk(src_dir):
        for f in files:
            if f.lower().endswith(".blend"):
                blends.append(os.path.join(root, f))
    blends.sort()

    print(f"Found {len(blends)} .blend files under {src_dir}")

    ok = 0
    for b in blends:
        # One unconvertible file must never cost us the other twenty-two.
        try:
            if convert(b, out_dir):
                ok += 1
        except Exception as e:
            print(f"[FAIL] {os.path.basename(b)} - unexpected error: {e}")

    print(f"\nConverted {ok} of {len(blends)} into {out_dir}")


main()
