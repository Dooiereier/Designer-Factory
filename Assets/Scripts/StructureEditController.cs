using System;
using System.Collections.Generic;
using System.IO;
using ModApi.Ui;
using UnityEngine;
using UnityEngine.Scripting;

namespace DesignerBackgroundTest
{
    // Lightweight editor for imported Planet Studio structures (Resources-loadable
    // prefabs like "Flight/GameView/Structures/Hangar1" - see the CelestialDatabase
    // XML backing stock celestial bodies for the full catalog). Deliberately NOT a
    // full drag-handle 3D gizmo or a mouse-drag scheme: Unity's Handles API (which
    // makes drag-handles easy) is Editor-only and unavailable at runtime in a built
    // game, and mouse-drag turned out not to work reliably for this user's setup
    // anyway. Instead: an OnGUI panel lists the catalog (click to add) and the
    // currently placed structures (click to select, Delete to remove); the selected
    // one is moved along world-fixed axes and rotated (yaw/pitch/roll) with held
    // keys (continuous, like a nudge) and scaled by typing exact X/Y/Z values into
    // the panel and clicking Apply.
    //
    // F2 works independently of the walk camera (WalkCameraController/F1) - the
    // orbit Designer camera's own controls are mouse-driven, and none of
    // I/J/K/L/U/O/Q/E/R/F/Z/C overlap with them, so there's no need to require walk
    // mode just to place a structure.
    [DefaultExecutionOrder(32001)]
    public class StructureEditController : MonoBehaviour, IDialog
    {
        public KeyCode ToggleKey = KeyCode.F2;
        public WalkCameraController WalkCamera;

        // Set false to ship a "locked" build - LoadConfig() still runs (from
        // DesignerBackgroundTestPatch.Postfix) and instantiates whatever was already
        // placed/saved, so the finished layout still shows up, but F2 stops opening
        // the panel at all, so players can't add/move/delete/save anything. See
        // DesignerBackgroundTestPatch.EnableStructureEditor for the one switch to
        // flip before publishing.
        public bool EditingEnabled = true;

        // Where new structures get parented (a persistent root that survives hangar
        // rebuilds - see DesignerBackgroundTestPatch.Postfix - so a placement doesn't
        // get wiped out just because a different craft got loaded) and where the next
        // "Add" spawns, kept in sync with the current hangar's size every rebuild.
        public Transform SpawnParent;
        public Vector3 SpawnPosition;

        // Built-in Planet Studio structures, Resources-loadable straight from the
        // game's own shipped assets (e.g. "Flight/GameView/Structures/Hangar1").
        public string[] CatalogPaths = new string[0];

        // User-imported props (e.g. from Poly Pizza via PropSetupEditor), bundled
        // with THIS mod instead - loaded through Mod.Instance.ResourceLoader rather
        // than UnityEngine.Resources, since that's the loader for assets the mod
        // ships itself (see WanderingDrood.cs for the same distinction).
        public string[] CustomCatalogPaths = new string[0];

        // Move/rotate are continuous while the key is held (units/degrees per
        // second), not a one-shot nudge - holding Shift multiplies both for covering
        // larger distances/angles quickly.
        public float MoveSpeed = 4f;
        public float RotateSpeed = 60f;
        public float ShiftMultiplier = 4f;
        public float MinScale = 0.05f;

        // Two fully independent, symmetric axis-tracking toggles - each only ever
        // touches its own axis (X or Z), never the other. Ticking just one tracks a
        // single wall/side; ticking both together tracks whichever corner the
        // structure started nearest to, since each axis picks its own nearest side
        // independently. Replaces an earlier single "nearest of all four sides" pick
        // that could end up fighting with a separate Z-only toggle over which one
        // controlled Z - this design can't have that conflict, since X and Z never
        // touch the same field.
        [Preserve]
        public enum XAxisSide { Left, Right }

        [Preserve]
        public enum ZAxisSide { Back, Front }

        private class PlacedEntry
        {
            public string PrefabPath;
            public bool IsCustomAsset;
            public GameObject Instance;
            public bool IsXAxisAnchored;
            public XAxisSide XAxisAnchorSide;
            public float XAxisAnchorInset;
            // White = untinted (Standard shader multiplies _Color over _MainTex, so
            // white leaves a textured prop's original look unchanged).
            public Color Tint = Color.white;
            // Marks this placed structure as a stop on the car loop - see
            // GetWaypointPositions(). Order follows _placed's own order (the order
            // structures were placed/loaded in), not physical proximity.
            public bool IsWaypoint;
            // The canteen's whole room moves in world space as the hangar resizes
            // (its origin is literally HalfWidth/-HalfDepth - see the cafeteria
            // section of BuildHangar) - CanteenOffset is this structure's position
            // relative to that origin at the moment "Stick to canteen" was toggled
            // on, so SyncCanteenAnchors can keep it in the same relative spot inside
            // the room as it shifts, rather than being left behind in empty space.
            public bool IsCanteenAnchored;
            public Vector3 CanteenOffset;
            // "Tied to hangar Z axis" - always tracks the nearest of Back/Front,
            // independent of (and combinable with) the wall-anchor toggle above.
            public bool IsZAxisAnchored;
            public ZAxisSide ZAxisAnchorSide;
            public float ZAxisAnchorInset;
        }

        // [Preserve] on these two - and on every field below - stops IL2CPP's
        // managed code stripping from removing the reflection metadata JsonUtility
        // needs to serialize them. Without it, a privately-scoped [Serializable]
        // class used only inside a List<T> (never directly referenced by typed code
        // elsewhere) can get silently stripped in a release build: the code compiles
        // and runs fine, save.Structures.Count is correct in memory, but
        // JsonUtility.ToJson comes out with no "Structures" key at all - exactly the
        // "Saved N structure(s)" yet an empty file on disk symptom this was chasing.
        [Serializable, Preserve]
        public class StructureSaveEntry
        {
            [Preserve] public string PrefabPath;
            [Preserve] public bool IsCustomAsset;
            [Preserve] public Vector3 Position;
            [Preserve] public Vector3 EulerRotation;
            [Preserve] public Vector3 Scale;
            [Preserve] public bool IsXAxisAnchored;
            [Preserve] public XAxisSide XAxisAnchorSide;
            [Preserve] public float XAxisAnchorInset;
            [Preserve] public Color Tint = Color.white;
            [Preserve] public bool IsWaypoint;
            [Preserve] public bool IsCanteenAnchored;
            [Preserve] public Vector3 CanteenOffset;
            [Preserve] public bool IsZAxisAnchored;
            [Preserve] public ZAxisSide ZAxisAnchorSide;
            [Preserve] public float ZAxisAnchorInset;
        }

        [Serializable, Preserve]
        public class StructureSaveFile
        {
            // Floor height in effect when this file was saved - lets LoadConfig
            // compute how much the floor has since moved (e.g. reloading in a later
            // session with a different craft) and shift every entry's saved absolute
            // Y by that same delta, rather than trusting the raw saved Y as-is.
            [Preserve] public float FloorY;
            [Preserve] public List<StructureSaveEntry> Structures = new List<StructureSaveEntry>();
        }

        // Tracks the hangar's floor height as of the last SyncFloorY call, so a
        // later change (the craft's bounds changed, shifting FloorTopY) can be
        // applied as a Y shift to every already-placed structure instead of leaving
        // them at their old world position while the floor moves out from under them.
        private bool _hasFloorY;
        private float _floorY;

        // Current hangar half-extents, refreshed by SyncWallAnchors every rebuild -
        // cached here so the "Anchor to wall" toggle (clicked at an arbitrary time in
        // the GUI, not during a rebuild) can compute a wall-anchored structure's
        // inset against the hangar's actual current size.
        private float _halfWidth;
        private float _halfDepth;

        private bool _editing;
        private int _selectedIndex = -1;
        private readonly List<PlacedEntry> _placed = new List<PlacedEntry>();

        // IDialog implementation - registering/unregistering this with the game's
        // own UserInterface (Game.Instance.UserInterface.RegisterDialog/
        // UnregisterDialog, the same dialog-tracking system CreateColorPicker's
        // dialog uses) while the panel is open is the sanctioned way to make
        // IUserInterface.AnyDialogsOpen true - the hope is the Designer's own
        // camera-orbit/part-click handling already checks that (the same way most
        // games avoid clicking through their own dialogs) and will leave clicks on
        // this panel alone instead of also acting on whatever's underneath. There's
        // no documented guarantee of this - it's the best available real mechanism,
        // not a confirmed fix.
        public event DialogDelegate Closed;
        public bool AllowCameraZoom => true;

        // Lets WalkCameraController skip WASD/arrow-key input while the panel is
        // open, so typing into a text field (scale/tint) or clicking a toggle
        // doesn't also move or spin the walk camera underneath it - same idea as
        // the IDialog registration above, but for the keyboard instead of the
        // designer's own mouse-drag handling.
        public bool IsOpen => _editing;

        void IDialog.Close()
        {
            SetEditingOpen(false);
        }

        private void SetEditingOpen(bool open)
        {
            if (open == _editing)
            {
                return;
            }
            _editing = open;
            Debug.Log("[DesignerBackgroundTest] Structure editor: " + (_editing ? "ON" : "OFF"));

            IUserInterface userInterface = Assets.Scripts.Game.Instance?.UserInterface;
            if (userInterface == null)
            {
                return;
            }
            if (open)
            {
                userInterface.RegisterDialog(this);
            }
            else
            {
                userInterface.UnregisterDialog(this);
                Closed?.Invoke(this);
            }
        }

        private Rect _windowRect = new Rect(12, 12, 340, 690);
        private Vector2 _catalogScroll;
        private Vector2 _customCatalogScroll;
        private Vector2 _placedScroll;
        private string _statusMessage = "";

        // Scale fields are free-typed text, re-synced from the target's actual scale
        // only when the selection changes - otherwise every keystroke while typing
        // would fight the live value.
        private Transform _scaleFieldsTarget;
        private string _scaleXText = "1";
        private string _scaleYText = "1";
        private string _scaleZText = "1";

        // Tint fields (0-1 range, matching Color's own convention) - same
        // re-sync-only-on-selection-change pattern as the scale fields above.
        private PlacedEntry _tintFieldsTarget;
        private string _tintRText = "1";
        private string _tintGText = "1";
        private string _tintBText = "1";

        private Transform Target => (_selectedIndex >= 0 && _selectedIndex < _placed.Count && _placed[_selectedIndex].Instance != null)
            ? _placed[_selectedIndex].Instance.transform
            : null;

        private PlacedEntry SelectedEntry => (_selectedIndex >= 0 && _selectedIndex < _placed.Count) ? _placed[_selectedIndex] : null;

        // Under UserData/, matching the convention other mods use for their own
        // persistent data (e.g. Ember stores its presets at UserData/Ember/Presets) -
        // a sibling of the game's own UserData/CraftDesigns, UserData/Levels, etc.,
        // rather than living under Mods/ (which holds the installed .sr2-mod package
        // files themselves, not save data). Assets.Scripts.Game.PersistentDataPath is
        // the same static API this mod's save/load code has used elsewhere this
        // session (it is NOT an instance member, despite the name suggesting otherwise).
        private static string ConfigDirectory => Path.Combine(Assets.Scripts.Game.PersistentDataPath, "UserData", "Designer Factory");
        private static string ConfigFilePath => Path.Combine(ConfigDirectory, "PlacedStructures.json");
        private static string BackupDirectory => Path.Combine(ConfigDirectory, "Backups");

        void Update()
        {
            if (WalkCamera == null)
            {
                return;
            }

            if (EditingEnabled && Input.GetKeyDown(ToggleKey))
            {
                SetEditingOpen(!_editing);
            }

            if (!_editing)
            {
                return;
            }

            Transform target = Target;
            if (target == null)
            {
                return;
            }

            float speedMultiplier = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? ShiftMultiplier : 1f;
            float dt = Time.deltaTime;

            // World-fixed axes rather than camera-relative - I/K and J/L always move
            // along world Z/X no matter which way you're currently facing, so a
            // structure's position doesn't shift meaning depending on where you
            // walked to look at it from.
            Vector3 moveDirection = Vector3.zero;
            if (Input.GetKey(KeyCode.I)) moveDirection += Vector3.forward;
            if (Input.GetKey(KeyCode.K)) moveDirection -= Vector3.forward;
            if (Input.GetKey(KeyCode.L)) moveDirection += Vector3.right;
            if (Input.GetKey(KeyCode.J)) moveDirection -= Vector3.right;
            if (Input.GetKey(KeyCode.U)) moveDirection += Vector3.up;
            if (Input.GetKey(KeyCode.O)) moveDirection -= Vector3.up;

            if (moveDirection.sqrMagnitude > 0f)
            {
                target.position += moveDirection.normalized * (MoveSpeed * speedMultiplier * dt);
            }

            float yawInput = 0f;
            if (Input.GetKey(KeyCode.E)) yawInput += 1f;
            if (Input.GetKey(KeyCode.Q)) yawInput -= 1f;
            if (yawInput != 0f)
            {
                target.Rotate(Vector3.up, yawInput * RotateSpeed * speedMultiplier * dt, Space.World);
            }

            // Pitch/roll rotate around the object's OWN current axes (Space.Self)
            // rather than world ones, so tilting still behaves intuitively after
            // the object has already been yawed away from its default orientation.
            float pitchInput = 0f;
            if (Input.GetKey(KeyCode.R)) pitchInput += 1f;
            if (Input.GetKey(KeyCode.F)) pitchInput -= 1f;
            if (pitchInput != 0f)
            {
                target.Rotate(Vector3.right, pitchInput * RotateSpeed * speedMultiplier * dt, Space.Self);
            }

            float rollInput = 0f;
            if (Input.GetKey(KeyCode.C)) rollInput += 1f;
            if (Input.GetKey(KeyCode.Z)) rollInput -= 1f;
            if (rollInput != 0f)
            {
                target.Rotate(Vector3.forward, rollInput * RotateSpeed * speedMultiplier * dt, Space.Self);
            }
        }

        private GameObject AddStructure(string path, bool isCustomAsset, Vector3 position, Quaternion rotation, Vector3 scale,
            bool isXAxisAnchored = false, XAxisSide xAxisAnchorSide = XAxisSide.Left, float xAxisAnchorInset = 0f, bool isWaypoint = false,
            bool isCanteenAnchored = false, Vector3 canteenOffset = default,
            bool isZAxisAnchored = false, ZAxisSide zAxisAnchorSide = ZAxisSide.Back, float zAxisAnchorInset = 0f)
        {
            if (SpawnParent == null)
            {
                return null;
            }
            GameObject prefab = isCustomAsset
                ? Assets.Scripts.Mod.Instance.ResourceLoader.LoadAsset<GameObject>(path)
                : Resources.Load<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogWarning("[DesignerBackgroundTest] Could not load structure: " + path);
                return null;
            }

            GameObject instance = UnityEngine.Object.Instantiate(prefab, SpawnParent);
            instance.name = ShortName(path) + "_" + _placed.Count;
            instance.transform.position = position;
            instance.transform.rotation = rotation;
            instance.transform.localScale = scale;
            _placed.Add(new PlacedEntry
            {
                PrefabPath = path,
                IsCustomAsset = isCustomAsset,
                Instance = instance,
                IsXAxisAnchored = isXAxisAnchored,
                XAxisAnchorSide = xAxisAnchorSide,
                XAxisAnchorInset = xAxisAnchorInset,
                IsWaypoint = isWaypoint,
                IsCanteenAnchored = isCanteenAnchored,
                CanteenOffset = canteenOffset,
                IsZAxisAnchored = isZAxisAnchored,
                ZAxisAnchorSide = zAxisAnchorSide,
                ZAxisAnchorInset = zAxisAnchorInset,
            });
            return instance;
        }

        // Called from the "Tied to hangar X axis" toggle - turning it on locks in
        // the structure's current distance from whichever of Left/Right it's
        // currently closest to, so SyncWallAnchors can keep it flush against that
        // same side as the hangar is resized; turning it off just leaves it wherever
        // it is. Only ever touches X - see ZAxisSide's sibling toggle for Z.
        private void SetXAxisAnchored(PlacedEntry entry, bool anchored)
        {
            if (entry == null || entry.Instance == null)
            {
                return;
            }
            entry.IsXAxisAnchored = anchored;
            if (!anchored)
            {
                return;
            }

            float x = entry.Instance.transform.position.x;
            float distLeft = x + _halfWidth;
            float distRight = _halfWidth - x;
            if (distLeft <= distRight)
            {
                entry.XAxisAnchorSide = XAxisSide.Left;
                entry.XAxisAnchorInset = distLeft;
            }
            else
            {
                entry.XAxisAnchorSide = XAxisSide.Right;
                entry.XAxisAnchorInset = distRight;
            }
        }

        // Called from the "Stick to canteen" toggle - the canteen room's own origin
        // is literally (HalfWidth, -HalfDepth) (see the cafeteria section of
        // BuildHangar - it's pinned to the main hangar's right wall/back corner), so
        // this just locks in the structure's current position relative to that point
        // rather than a wall of the main hangar; turning it off leaves it in place.
        private void SetCanteenAnchored(PlacedEntry entry, bool anchored)
        {
            if (entry == null || entry.Instance == null)
            {
                return;
            }
            entry.IsCanteenAnchored = anchored;
            if (!anchored)
            {
                return;
            }
            Vector3 canteenOrigin = new Vector3(_halfWidth, 0f, -_halfDepth);
            entry.CanteenOffset = entry.Instance.transform.position - canteenOrigin;
        }

        // Called from the "Tied to hangar Z axis" toggle - always locks onto
        // whichever of Back/Front is nearest right now, regardless of whether
        // Left/Right happens to be even closer (that's what makes this different
        // from the wall-anchor toggle above, which picks whichever single side of
        // all four is nearest overall).
        private void SetZAxisAnchored(PlacedEntry entry, bool anchored)
        {
            if (entry == null || entry.Instance == null)
            {
                return;
            }
            entry.IsZAxisAnchored = anchored;
            if (!anchored)
            {
                return;
            }
            float z = entry.Instance.transform.position.z;
            float distBack = z + _halfDepth;
            float distFront = _halfDepth - z;
            if (distBack <= distFront)
            {
                entry.ZAxisAnchorSide = ZAxisSide.Back;
                entry.ZAxisAnchorInset = distBack;
            }
            else
            {
                entry.ZAxisAnchorSide = ZAxisSide.Front;
                entry.ZAxisAnchorInset = distFront;
            }
        }

        private void DeleteStructure(int index)
        {
            if (index < 0 || index >= _placed.Count)
            {
                return;
            }
            if (_placed[index].Instance != null)
            {
                UnityEngine.Object.Destroy(_placed[index].Instance);
            }
            _placed.RemoveAt(index);
            if (_selectedIndex == index)
            {
                _selectedIndex = -1;
            }
            else if (_selectedIndex > index)
            {
                _selectedIndex--;
            }
        }

        // Writes every placed structure's path + transform to ConfigFilePath as JSON.
        // If a save already exists there, it's copied into Backups/ first (timestamped,
        // never overwritten) so a bad save never destroys the only working copy.
        private void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(ConfigDirectory);

                if (File.Exists(ConfigFilePath))
                {
                    Directory.CreateDirectory(BackupDirectory);
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string backupPath = Path.Combine(BackupDirectory, $"PlacedStructures_{timestamp}.json");
                    File.Copy(ConfigFilePath, backupPath, true);
                }

                StructureSaveFile save = new StructureSaveFile { FloorY = _floorY };
                foreach (PlacedEntry entry in _placed)
                {
                    if (entry.Instance == null)
                    {
                        continue;
                    }
                    save.Structures.Add(new StructureSaveEntry
                    {
                        PrefabPath = entry.PrefabPath,
                        IsCustomAsset = entry.IsCustomAsset,
                        Position = entry.Instance.transform.position,
                        EulerRotation = entry.Instance.transform.eulerAngles,
                        Scale = entry.Instance.transform.localScale,
                        IsXAxisAnchored = entry.IsXAxisAnchored,
                        XAxisAnchorSide = entry.XAxisAnchorSide,
                        XAxisAnchorInset = entry.XAxisAnchorInset,
                        Tint = entry.Tint,
                        IsWaypoint = entry.IsWaypoint,
                        IsCanteenAnchored = entry.IsCanteenAnchored,
                        CanteenOffset = entry.CanteenOffset,
                        IsZAxisAnchored = entry.IsZAxisAnchored,
                        ZAxisAnchorSide = entry.ZAxisAnchorSide,
                        ZAxisAnchorInset = entry.ZAxisAnchorInset,
                    });
                }

                string json = SerializeSaveFile(save);
                File.WriteAllText(ConfigFilePath, json);
                _statusMessage = $"Saved {save.Structures.Count} structure(s) to {ConfigFilePath}";
                Debug.Log("[DesignerBackgroundTest] " + _statusMessage);
            }
            catch (Exception ex)
            {
                _statusMessage = "Save FAILED: " + ex.Message;
                Debug.LogWarning("[DesignerBackgroundTest] Structure config save failed: " + ex);
            }
        }

        // Call every time the hangar (re)builds and its floor height is known. The
        // very first call just records the baseline (nothing to shift yet, and doing
        // so would double-move structures that were just restored at their saved
        // absolute positions by LoadConfig); every call after that shifts all
        // currently placed structures by however much the floor moved since then.
        public void SyncFloorY(float floorY)
        {
            if (_hasFloorY)
            {
                float delta = floorY - _floorY;
                if (Mathf.Abs(delta) > 0.0001f)
                {
                    Vector3 shift = new Vector3(0f, delta, 0f);
                    foreach (PlacedEntry entry in _placed)
                    {
                        if (entry.Instance != null)
                        {
                            entry.Instance.transform.position += shift;
                        }
                    }
                }
            }
            _floorY = floorY;
            _hasFloorY = true;
        }

        // Call every time the hangar (re)builds, alongside SyncFloorY. Unlike that
        // one, this needs no "first call" baseline - a wall-anchored structure's
        // position is always fully determined by the CURRENT half-extents plus its
        // stored (side, inset) pair, recomputed fresh every time regardless of how
        // the extents got there (fresh craft load, manual refresh, or a save restored
        // at a different hangar size).
        public void SyncWallAnchors(float halfWidth, float halfDepth)
        {
            _halfWidth = halfWidth;
            _halfDepth = halfDepth;

            foreach (PlacedEntry entry in _placed)
            {
                if (entry.Instance == null)
                {
                    continue;
                }

                // "Tied to hangar X axis" - only ever touches X. See IsZAxisAnchored
                // below for Z - the two are fully independent, so ticking both always
                // gives true corner-tracking with no chance of one silently
                // overriding the other's axis.
                if (entry.IsXAxisAnchored)
                {
                    Vector3 pos = entry.Instance.transform.position;
                    pos.x = entry.XAxisAnchorSide == XAxisSide.Left
                        ? -halfWidth + entry.XAxisAnchorInset
                        : halfWidth - entry.XAxisAnchorInset;
                    entry.Instance.transform.position = pos;
                }

                // The canteen room's own origin (HalfWidth, -HalfDepth) - see
                // BuildHangar's cafeteria section - moves independently of the main
                // hangar's walls, so this is a separate offset-from-a-moving-point
                // recompute rather than reusing the wall-anchor switch above.
                if (entry.IsCanteenAnchored)
                {
                    Vector3 canteenOrigin = new Vector3(halfWidth, 0f, -halfDepth);
                    Vector3 pos = entry.Instance.transform.position;
                    Vector3 target = canteenOrigin + entry.CanteenOffset;
                    pos.x = target.x;
                    pos.z = target.z;
                    entry.Instance.transform.position = pos;
                }

                // "Tied to hangar Z axis" - only ever touches Z, mirroring X above.
                if (entry.IsZAxisAnchored)
                {
                    Vector3 pos = entry.Instance.transform.position;
                    pos.z = entry.ZAxisAnchorSide == ZAxisSide.Back
                        ? -halfDepth + entry.ZAxisAnchorInset
                        : halfDepth - entry.ZAxisAnchorInset;
                    entry.Instance.transform.position = pos;
                }
            }
        }

        // Live snapshot of every structure currently flagged "Waypoint for car
        // path", in placement order - read fresh each time by CarController rather
        // than cached anywhere, so adding/moving/removing waypoints via F2 takes
        // effect on the next lap without needing an explicit resync call.
        public List<Vector3> GetWaypointPositions()
        {
            List<Vector3> waypoints = new List<Vector3>();
            foreach (PlacedEntry entry in _placed)
            {
                if (entry.IsWaypoint && entry.Instance != null)
                {
                    waypoints.Add(entry.Instance.transform.position);
                }
            }
            return waypoints;
        }

        // Every currently placed structure's position (every one, not just
        // waypoints) - used to keep the billboard tree band from spawning trees on
        // top of whatever's been placed via this editor.
        public List<Vector3> GetPlacedPositions()
        {
            List<Vector3> positions = new List<Vector3>();
            foreach (PlacedEntry entry in _placed)
            {
                if (entry.Instance != null)
                {
                    positions.Add(entry.Instance.transform.position);
                }
            }
            return positions;
        }

        // Real world-space footprint (combined renderer bounds) per placed
        // structure - used instead of GetPlacedPositions for clearance checks that
        // need to account for actual size (e.g. tree placement), since a fixed
        // radius around just the pivot badly under-covers something like a scaled-up
        // launch pad while over-covering a small crate.
        // Cached rather than recomputed on every call - GetComponentsInChildren<Renderer>
        // per placed structure isn't free, and this is now called every frame by every
        // wandering Drood (for continuous obstacle steering, not just target-picking),
        // not just occasionally like before. Structures don't move on their own outside
        // of F2 editing, so a short time-based cache costs nothing in practice.
        private List<Bounds> _footprintCache;
        private float _footprintCacheTime = -1f;
        private const float FootprintCacheDuration = 1f;

        public List<Bounds> GetPlacedFootprints()
        {
            if (_footprintCache != null && Time.time - _footprintCacheTime < FootprintCacheDuration)
            {
                return _footprintCache;
            }

            List<Bounds> footprints = new List<Bounds>();
            foreach (PlacedEntry entry in _placed)
            {
                if (entry.Instance == null)
                {
                    continue;
                }
                Renderer[] renderers = entry.Instance.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0)
                {
                    continue;
                }
                Bounds combined = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                {
                    combined.Encapsulate(renderers[i].bounds);
                }
                footprints.Add(combined);
            }
            _footprintCache = footprints;
            _footprintCacheTime = Time.time;
            return footprints;
        }

        // Replaces every currently placed structure with whatever is in the save
        // file. Safe to call with no save file present (e.g. on first-ever launch) -
        // it just does nothing in that case.
        public void LoadConfig()
        {
            try
            {
                string json;
                bool usedBundledDefault = false;
                if (File.Exists(ConfigFilePath))
                {
                    json = File.ReadAllText(ConfigFilePath);
                }
                else if (!string.IsNullOrEmpty(DefaultPlacedStructures.Json))
                {
                    // No save of the player's own yet (fresh install, or just never
                    // saved) - seed the hangar from the layout bundled with the mod
                    // instead of starting empty. Nothing is written to disk here;
                    // an explicit "Save Config" is still what turns this into the
                    // player's own persisted file from then on.
                    json = DefaultPlacedStructures.Json;
                    usedBundledDefault = true;
                    Debug.Log("[DesignerBackgroundTest] No saved config found - using the mod's bundled default layout.");
                }
                else
                {
                    _statusMessage = "No saved config found yet.";
                    return;
                }

                StructureSaveFile save = DeserializeSaveFile(json);
                if (save == null || save.Structures == null)
                {
                    _statusMessage = "Saved config was empty or unreadable.";
                    return;
                }

                for (int i = _placed.Count - 1; i >= 0; i--)
                {
                    DeleteStructure(i);
                }

                // The file's absolute positions were only ever correct relative to
                // the floor height in effect when it was saved. If the hangar's
                // current floor sits somewhere else now (different craft, or even the
                // same craft with slightly different computed bounds this session), a
                // LATER manual reload of an already-loaded scene would otherwise drop
                // every structure at a stale height - sunk through the floor or
                // floating, invisible despite loading "successfully". _hasFloorY is
                // still false for the very first automatic LoadConfig call (before the
                // hangar has been built even once), so this is a no-op there, exactly
                // like before.
                float floorDelta = _hasFloorY ? (_floorY - save.FloorY) : 0f;

                foreach (StructureSaveEntry entry in save.Structures)
                {
                    Vector3 position = entry.Position + new Vector3(0f, floorDelta, 0f);
                    GameObject instance = AddStructure(entry.PrefabPath, entry.IsCustomAsset, position, Quaternion.Euler(entry.EulerRotation), entry.Scale,
                        entry.IsXAxisAnchored, entry.XAxisAnchorSide, entry.XAxisAnchorInset, entry.IsWaypoint,
                        entry.IsCanteenAnchored, entry.CanteenOffset,
                        entry.IsZAxisAnchored, entry.ZAxisAnchorSide, entry.ZAxisAnchorInset);
                    if (instance != null)
                    {
                        PlacedEntry placed = _placed[_placed.Count - 1];
                        placed.Tint = entry.Tint;
                        ApplyTint(placed);
                    }
                }

                // Wall-anchored entries are always recomputed directly from the
                // current half-extents (not a delta), so this is safe to call
                // unconditionally alongside the floor correction above.
                if (_hasFloorY)
                {
                    SyncWallAnchors(_halfWidth, _halfDepth);
                }

                _statusMessage = usedBundledDefault
                    ? $"Loaded {save.Structures.Count} structure(s) from the mod's bundled default layout"
                    : $"Loaded {save.Structures.Count} structure(s) from {ConfigFilePath}";
                Debug.Log("[DesignerBackgroundTest] " + _statusMessage);
            }
            catch (Exception ex)
            {
                _statusMessage = "Load FAILED: " + ex.Message;
                Debug.LogWarning("[DesignerBackgroundTest] Structure config load failed: " + ex);
            }
        }

        // JsonUtility.ToJson(save, true) was confirmed (via diagnostic logging) to
        // silently drop the whole "Structures" key on this build - a single
        // StructureSaveEntry serializes correctly on its own, but the SAME entries
        // inside StructureSaveFile's List<StructureSaveEntry> field vanish entirely,
        // even with [Preserve] applied and the types made public. Rather than keep
        // guessing at JsonUtility/IL2CPP internals, this hand-builds the array from
        // individually-serialized entries (the part proven to work) and hand-reads it
        // back the same way, sidestepping JsonUtility's broken List<CustomType>
        // handling entirely instead of depending on it.
        private static string SerializeSaveFile(StructureSaveFile save)
        {
            List<string> entryJson = new List<string>(save.Structures.Count);
            foreach (StructureSaveEntry entry in save.Structures)
            {
                entryJson.Add(JsonUtility.ToJson(entry));
            }
            string floorYText = save.FloorY.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return "{\"FloorY\":" + floorYText + ",\"Structures\":[" + string.Join(",", entryJson) + "]}";
        }

        private static StructureSaveFile DeserializeSaveFile(string json)
        {
            StructureSaveFile save = new StructureSaveFile();

            int floorYIndex = json.IndexOf("\"FloorY\"", StringComparison.Ordinal);
            if (floorYIndex >= 0)
            {
                int colonIndex = json.IndexOf(':', floorYIndex);
                int start = colonIndex + 1;
                // Skip any whitespace between the colon and the number - the game's
                // own SerializeSaveFile never writes any, but a file re-saved through
                // a standard JSON library (e.g. Python's json.dump, used for a manual
                // migration) does by default ("FloorY": -18.11 instead of
                // "FloorY":-18.11), which silently parsed as 0 and threw off the
                // floor-height correction - structures ended up sunk/floating instead
                // of at their saved position. Being whitespace-tolerant here means
                // this file can safely be hand-edited or touched by any standard JSON
                // tool without breaking this specific hand-rolled parser again.
                while (start < json.Length && char.IsWhiteSpace(json[start]))
                {
                    start++;
                }
                int end = start;
                while (end < json.Length && "-+.0123456789eE".IndexOf(json[end]) >= 0)
                {
                    end++;
                }
                string numberText = json.Substring(start, end - start).Trim();
                float.TryParse(numberText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out save.FloorY);
            }

            int structuresIndex = json.IndexOf("\"Structures\"", StringComparison.Ordinal);
            if (structuresIndex >= 0)
            {
                int arrayStart = json.IndexOf('[', structuresIndex);
                if (arrayStart >= 0)
                {
                    int depth = 0;
                    int arrayEnd = -1;
                    for (int i = arrayStart; i < json.Length; i++)
                    {
                        if (json[i] == '[')
                        {
                            depth++;
                        }
                        else if (json[i] == ']')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                arrayEnd = i;
                                break;
                            }
                        }
                    }
                    if (arrayEnd > arrayStart)
                    {
                        string inner = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);
                        foreach (string entryJson in SplitTopLevelJsonObjects(inner))
                        {
                            StructureSaveEntry entry = JsonUtility.FromJson<StructureSaveEntry>(entryJson);
                            if (entry != null)
                            {
                                save.Structures.Add(entry);
                            }
                        }
                    }
                }
            }

            return save;
        }

        // Splits "{...},{...},{...}" into its top-level "{...}" object substrings,
        // tracking brace depth so nested objects (none currently, but safe regardless)
        // don't get split early. Safe for our own controlled output - the only string
        // field (PrefabPath) is always a plain asset path with no brace characters.
        private static List<string> SplitTopLevelJsonObjects(string inner)
        {
            List<string> result = new List<string>();
            int depth = 0;
            int start = -1;
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '{')
                {
                    if (depth == 0)
                    {
                        start = i;
                    }
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        result.Add(inner.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }
            return result;
        }

        // 1.3x - panel renders 30% larger. Applied as a GUI matrix scale around the
        // window's own top-left corner rather than resizing _windowRect itself, so
        // the layout code below keeps working in its original, simpler coordinate
        // space while only the on-screen rendering gets bigger.
        private const float UiScale = 1.3f;

        private static GUIStyle _opaqueWindowStyle;

        // Default GUI.skin.window has a semi-transparent background - builds a solid
        // (alpha 1) replacement once and reuses it, so the panel is fully opaque
        // instead of letting the 3D scene show through.
        private static GUIStyle GetOpaqueWindowStyle()
        {
            // Rebuild if the style was never created OR its background texture got
            // collected out from under it - a runtime Texture2D with nothing but a
            // plain C# GUIStyle field pointing at it isn't something Unity's asset
            // tracker considers "in use", so a later Resources.UnloadUnusedAssets
            // pass (visible in the log as "Unloading N unused Assets" - happens
            // periodically, e.g. on hangar rebuilds) can destroy it, silently making
            // the window render with no background at all after that point. Setting
            // hideFlags below should prevent that outright, but this check is a
            // cheap defensive fallback in case it doesn't fully cover every path.
            if (_opaqueWindowStyle == null || _opaqueWindowStyle.normal.background == null)
            {
                Texture2D background = new Texture2D(1, 1);
                background.SetPixel(0, 0, new Color(0.10f, 0.10f, 0.12f, 1f));
                background.Apply();
                // Tells Unity's asset-unloading sweep to never collect this texture,
                // regardless of whether it detects a "live" reference to it.
                background.hideFlags = HideFlags.HideAndDontSave;
                _opaqueWindowStyle = new GUIStyle(GUI.skin.window);
                // GUI.skin.window normally has a non-zero border for 9-slice
                // rounded-corner rendering - a flat 1x1 texture doesn't 9-slice
                // sensibly under that (parts of it can end up effectively
                // see-through), which is what was actually causing the
                // "transparent" look even after fixing the per-state backgrounds
                // below. Zeroing it out makes the texture just stretch flatly
                // across the whole window instead.
                _opaqueWindowStyle.border = new RectOffset(0, 0, 0, 0);
                _opaqueWindowStyle.normal.background = background;
                _opaqueWindowStyle.onNormal.background = background;
                _opaqueWindowStyle.hover.background = background;
                _opaqueWindowStyle.onHover.background = background;
                _opaqueWindowStyle.active.background = background;
                _opaqueWindowStyle.onActive.background = background;
                _opaqueWindowStyle.focused.background = background;
                _opaqueWindowStyle.onFocused.background = background;
            }
            return _opaqueWindowStyle;
        }

        void OnGUI()
        {
            if (!EditingEnabled || !_editing)
            {
                return;
            }

            Matrix4x4 originalMatrix = GUI.matrix;
            GUIUtility.ScaleAroundPivot(new Vector2(UiScale, UiScale), new Vector2(_windowRect.x, _windowRect.y));
            _windowRect = GUILayout.Window(GetInstanceID(), _windowRect, DrawWindow, "Structure Editor [" + ToggleKey + "]", GetOpaqueWindowStyle());
            GUI.matrix = originalMatrix;
        }

        private void DrawWindow(int windowId)
        {
            Transform target = Target;
            RefreshScaleFieldsIfNeeded(target);
            RefreshTintFieldsIfNeeded(SelectedEntry);

            GUILayout.Label("Move: I/K fwd-back, J/L left-right, U/O up-down\nRotate: Q/E yaw, R/F pitch, Z/C roll   (hold Shift = faster)");
            GUILayout.Label(target != null
                ? $"Selected: {target.name}\nPos {FormatVector(target.position)}\nRot {FormatVector(target.eulerAngles)}"
                : "Selected: (none)");

            if (target != null)
            {
                PlacedEntry selectedEntry = SelectedEntry;
                bool xAxisAnchored = GUILayout.Toggle(selectedEntry.IsXAxisAnchored, "Tied to hangar X axis (left/right)");
                if (xAxisAnchored != selectedEntry.IsXAxisAnchored)
                {
                    SetXAxisAnchored(selectedEntry, xAxisAnchored);
                }

                bool canteenAnchored = GUILayout.Toggle(selectedEntry.IsCanteenAnchored, "Stick to canteen");
                if (canteenAnchored != selectedEntry.IsCanteenAnchored)
                {
                    SetCanteenAnchored(selectedEntry, canteenAnchored);
                }

                bool zAxisAnchored = GUILayout.Toggle(selectedEntry.IsZAxisAnchored, "Tied to hangar Z axis (front/back)");
                if (zAxisAnchored != selectedEntry.IsZAxisAnchored)
                {
                    SetZAxisAnchored(selectedEntry, zAxisAnchored);
                }

                selectedEntry.IsWaypoint = GUILayout.Toggle(selectedEntry.IsWaypoint, "Waypoint for car path");
            }

            GUILayout.Space(8);
            GUILayout.Label("Scale (X, Y, Z):");
            GUILayout.BeginHorizontal();
            _scaleXText = GUILayout.TextField(_scaleXText, GUILayout.Width(55));
            _scaleYText = GUILayout.TextField(_scaleYText, GUILayout.Width(55));
            _scaleZText = GUILayout.TextField(_scaleZText, GUILayout.Width(55));
            GUI.enabled = target != null;
            if (GUILayout.Button("Apply"))
            {
                ApplyScaleFields(target);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("Tint (R, G, B - 0 to 1):");
            GUILayout.BeginHorizontal();
            _tintRText = GUILayout.TextField(_tintRText, GUILayout.Width(55));
            _tintGText = GUILayout.TextField(_tintGText, GUILayout.Width(55));
            _tintBText = GUILayout.TextField(_tintBText, GUILayout.Width(55));
            GUI.enabled = target != null;
            if (GUILayout.Button("Apply"))
            {
                ApplyTintFields(SelectedEntry);
            }
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Pick Color..."))
            {
                OpenNativeColorPicker(SelectedEntry);
            }
            GUI.enabled = true;

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save Config"))
            {
                SaveConfig();
            }
            if (GUILayout.Button("Load Config"))
            {
                LoadConfig();
            }
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUILayout.Label(_statusMessage);
            }

            GUILayout.Space(8);
            GUILayout.Label("Add structure:");
            _catalogScroll = GUILayout.BeginScrollView(_catalogScroll, GUILayout.Height(100));
            foreach (string path in CatalogPaths)
            {
                if (GUILayout.Button("Add " + ShortName(path)))
                {
                    AddStructure(path, false, SpawnPosition, Quaternion.identity, Vector3.one);
                    _selectedIndex = _placed.Count - 1;
                }
            }
            GUILayout.EndScrollView();

            if (CustomCatalogPaths.Length > 0)
            {
                GUILayout.Space(8);
                GUILayout.Label("Add custom prop:");
                _customCatalogScroll = GUILayout.BeginScrollView(_customCatalogScroll, GUILayout.Height(80));
                foreach (string path in CustomCatalogPaths)
                {
                    if (GUILayout.Button("Add " + ShortName(path)))
                    {
                        AddStructure(path, true, SpawnPosition, Quaternion.identity, Vector3.one);
                        _selectedIndex = _placed.Count - 1;
                    }
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(8);
            GUILayout.Label("Placed structures:");
            int pendingDelete = -1;
            _placedScroll = GUILayout.BeginScrollView(_placedScroll, GUILayout.Height(120));
            for (int i = 0; i < _placed.Count; i++)
            {
                if (_placed[i].Instance == null)
                {
                    continue;
                }
                GUILayout.BeginHorizontal();
                string label = (i == _selectedIndex ? "> " : "") + _placed[i].Instance.name;
                if (GUILayout.Button(label))
                {
                    _selectedIndex = i;
                }
                if (GUILayout.Button("Delete", GUILayout.Width(60)))
                {
                    pendingDelete = i;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (pendingDelete >= 0)
            {
                DeleteStructure(pendingDelete);
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void RefreshScaleFieldsIfNeeded(Transform target)
        {
            if (target == _scaleFieldsTarget)
            {
                return;
            }
            _scaleFieldsTarget = target;
            if (target != null)
            {
                _scaleXText = target.localScale.x.ToString("F2");
                _scaleYText = target.localScale.y.ToString("F2");
                _scaleZText = target.localScale.z.ToString("F2");
            }
        }

        private void ApplyScaleFields(Transform target)
        {
            if (target == null)
            {
                return;
            }
            float x = ParseOrKeep(_scaleXText, target.localScale.x);
            float y = ParseOrKeep(_scaleYText, target.localScale.y);
            float z = ParseOrKeep(_scaleZText, target.localScale.z);
            target.localScale = new Vector3(Mathf.Max(MinScale, x), Mathf.Max(MinScale, y), Mathf.Max(MinScale, z));

            // Re-sync the fields to whatever was actually applied (post-clamp).
            _scaleXText = target.localScale.x.ToString("F2");
            _scaleYText = target.localScale.y.ToString("F2");
            _scaleZText = target.localScale.z.ToString("F2");
        }

        private static float ParseOrKeep(string text, float fallback)
        {
            return float.TryParse(text, out float value) ? value : fallback;
        }

        private void RefreshTintFieldsIfNeeded(PlacedEntry entry)
        {
            if (entry == _tintFieldsTarget)
            {
                return;
            }
            _tintFieldsTarget = entry;
            if (entry != null)
            {
                _tintRText = entry.Tint.r.ToString("F2");
                _tintGText = entry.Tint.g.ToString("F2");
                _tintBText = entry.Tint.b.ToString("F2");
            }
        }

        private void ApplyTintFields(PlacedEntry entry)
        {
            if (entry == null || entry.Instance == null)
            {
                return;
            }
            float r = Mathf.Clamp01(ParseOrKeep(_tintRText, entry.Tint.r));
            float g = Mathf.Clamp01(ParseOrKeep(_tintGText, entry.Tint.g));
            float b = Mathf.Clamp01(ParseOrKeep(_tintBText, entry.Tint.b));
            entry.Tint = new Color(r, g, b);
            ApplyTint(entry);
            SyncTintTextFields(entry);
        }

        // Opens the game's own native color picker dialog (the same one used for
        // painting craft parts) instead of the raw R/G/B text fields - IUserInterface.
        // CreateColorPicker is documented public ModApi, reached the same way this
        // mod's native toolbar button was (Assets.Scripts.Game.Instance.UserInterface -
        // see Mod.cs/DesignerFactoryUi.cs). Live-previews the color while the dialog
        // is open, and only commits it to the entry (and re-syncs the text fields)
        // once the user clicks okay.
        private void OpenNativeColorPicker(PlacedEntry entry)
        {
            if (entry == null || entry.Instance == null)
            {
                Debug.Log("[DesignerBackgroundTest] OpenNativeColorPicker: no entry/instance selected.");
                return;
            }
            Debug.Log("[DesignerBackgroundTest] OpenNativeColorPicker: opening dialog for " + entry.Instance.name);
            Assets.Scripts.Game.Instance.UserInterface.CreateColorPicker(
                false,
                entry.Tint,
                color =>
                {
                    Debug.Log("[DesignerBackgroundTest] Color picker onComplete: " + color);
                    entry.Tint = color;
                    ApplyTintColor(entry, color);
                    SyncTintTextFields(entry);
                },
                color =>
                {
                    Debug.Log("[DesignerBackgroundTest] Color picker onPreviewColorChanged: " + color);
                    ApplyTintColor(entry, color);
                },
                false);
        }

        private void SyncTintTextFields(PlacedEntry entry)
        {
            _tintRText = entry.Tint.r.ToString("F2");
            _tintGText = entry.Tint.g.ToString("F2");
            _tintBText = entry.Tint.b.ToString("F2");
        }

        // Tints every renderer under the placed instance (Standard shader multiplies
        // _Color over _MainTex, so this recolors a textured prop rather than just
        // painting it flat) - applied both from the panel's Apply button and when
        // restoring a saved tint via LoadConfig.
        private static void ApplyTint(PlacedEntry entry)
        {
            ApplyTintColor(entry, entry?.Tint ?? Color.white);
        }

        private static void ApplyTintColor(PlacedEntry entry, Color color)
        {
            if (entry?.Instance == null)
            {
                return;
            }
            // White means "untinted" and is deliberately a no-op here rather than
            // actually setting every material's color to white - that assumption
            // ("white leaves a textured prop's look unchanged") only holds for
            // materials whose look comes from a texture multiplied by _Color. A prop
            // like the standing desk defines its surface color directly via _Color
            // with no texture on some parts, so forcing that to white doesn't leave
            // it "untinted" - it visibly bleaches it. Skipping the write entirely for
            // the untinted case keeps every prop's original material colors intact
            // until the user actually picks a real tint.
            if (color == Color.white)
            {
                return;
            }
            // Different structure types use different shaders with different names
            // for their tint color - Standard-shader props (couch, desk, ...) use
            // "_Color", but the game's own stock structures use a custom shader
            // ("Jundroo/SR Standard/SrStandardObjectShader") whose equivalent is
            // "_colorMultiplier" instead (confirmed by enumerating the shader's own
            // properties at runtime via Shader.GetPropertyCount/Name/Type - both
            // default to white, matching the same "white = untinted" convention).
            // Writing whichever of these the shader actually has covers both cases.
            string[] colorPropertyNames = { "_Color", "_colorMultiplier" };

            Renderer[] renderers = entry.Instance.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                Material material = renderer.material;
                MaterialPropertyBlock block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                bool wroteAny = false;
                foreach (string propertyName in colorPropertyNames)
                {
                    if (material.HasProperty(propertyName))
                    {
                        material.SetColor(propertyName, color);
                        block.SetColor(propertyName, color);
                        wroteAny = true;
                    }
                }
                if (wroteAny)
                {
                    renderer.SetPropertyBlock(block);
                }
            }
        }

        private static string ShortName(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        private static string FormatVector(Vector3 v)
        {
            return $"({v.x:F1}, {v.y:F1}, {v.z:F1})";
        }
    }
}
