# Unity Project

Unity project synced via git so it can be worked on from more than one machine.

## Working on a second machine

First time:

```bash
git clone https://github.com/xBurtusx/Unity-Project-.git
```

Then open the cloned folder with Unity Hub (**Add project from disk**). Unity will
rebuild `Library/` on first open — that takes a few minutes and is normal.

## Daily routine

Start of a session, before opening Unity:

```bash
git pull
```

End of a session, with Unity **closed** (so everything is saved to disk):

```bash
git add -A
git commit -m "describe what changed"
git push
```

## What is and isn't in the repo

Versioned: `Assets/`, `Packages/`, `ProjectSettings/`, `Docs/`, `Tools/`.

Not versioned (see `.gitignore`):

- `Library/`, `Temp/`, `Logs/`, `UserSettings/`, `Build/` — Unity regenerates these.
- `AssetSource/` — ~2 GB of raw `.blend` / 4K PNG / EXR source art. Too large for
  GitHub without a paid LFS data pack. The imported results are in `Assets/`, so
  the project opens and builds fine without it. Sync these separately (external
  drive or cloud storage) if you need to re-author the source art elsewhere.

## Project settings that matter for git

- **Asset Serialization: Force Text** — scenes and prefabs save as YAML so git can
  diff them. Do not change this.
- **New script line endings: Unix** — consistent across machines.

Scene and prefab files do not merge cleanly. Avoid editing the same scene on both
machines without pushing in between; if you hit a conflict, pick one side rather
than hand-merging the YAML.
