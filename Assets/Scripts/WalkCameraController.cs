using System.Collections.Generic;
using ModApi.Ui;
using UnityEngine;

namespace DesignerBackgroundTest
{
    // An axis-aligned walkable surface (floor, catwalk section, or stair step) that
    // the walk camera can step up onto. Deliberately NOT physics/collider-based - we
    // stripped colliders from all of our own geometry earlier this session specifically
    // to avoid the designer's part-connection system misbehaving around them, and a
    // real Physics.Raycast ground-check would need those colliders back. Since we
    // already know exactly what geometry we built, plain XZ-rectangle bounds-checking
    // in code gives the same result with zero collider risk.
    public struct WalkPlatform
    {
        public float MinX;
        public float MaxX;
        public float MinZ;
        public float MaxZ;
        public float TopY;

        public bool Contains(float x, float z)
        {
            return x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
        }
    }


    // Ground-based FPS-style walk camera for exploring the hangar: WASD moves along
    // the horizontal plane at a fixed eye height (using yaw only, so looking up/down
    // doesn't make you fly), arrow keys control look direction, and a small view-bob
    // simulates footsteps. Toggle with F1 to hand control back to the Designer's own
    // orbit/zoom camera.
    //
    // Deliberately does not touch the mouse or cursor lock state at all - an earlier
    // version tried mouse-look (both via Input.GetAxis and via raw mousePosition
    // deltas) and locked/hid the cursor while walking, but the mouse axis never
    // produced usable input in this game, and locking the cursor fought with the
    // Escape menu's own attempt to unlock it. The mouse is left completely alone so
    // it stays available for normal game interaction while walking.
    //
    // Runs its LateUpdate after every default-order script (DefaultExecutionOrder set
    // far past 0) so it always gets the final say on the camera's transform for the
    // frame, regardless of whether the Designer's own camera script also runs in
    // LateUpdate - without this, execution order between the two would be unspecified
    // and our positioning could get silently overwritten.
    [DefaultExecutionOrder(32000)]
    public class WalkCameraController : MonoBehaviour, IDialog
    {
        public float FloorY;
        public float EyeHeight = 1.8f;
        public float WalkSpeed = 8f;
        public float ArrowLookSpeed = 90f; // degrees per second

        // Walkable surfaces above the base floor (catwalk sections, stair steps) -
        // repopulated by BuildHangar every time the hangar is rebuilt. Standing over
        // one of these steps the camera up onto it instead of staying at FloorY.
        public List<WalkPlatform> Platforms = new List<WalkPlatform>();

        // How fast the camera's height eases toward the ground height under it, so
        // stepping onto a stair step or the catwalk is a brief smooth rise rather than
        // an instant snap - there's no real "leg" animation happening, so without this
        // every step change would be a jarring teleport. Must climb faster than WASD
        // can outrun it up the stairs' own slope (rise/run ~10m/12.6m at WalkSpeed=8
        // needs ~6.3 vertical m/s just to keep the camera's feet level with the tread
        // it's standing on), or the camera lags below the stairs and appears to clip
        // straight through them - hence well above that, not just a hair over it.
        public float StepClimbSpeed = 10f;
        private float _currentGroundY;
        // F alone opens the designer's part-shape tool, so this stays off that key.
        public KeyCode ToggleKey = KeyCode.F1;

        // View bob: a vertical bounce plus a subtler side-to-side sway, advanced by
        // actual distance walked (not just time) so the rhythm matches how fast you're
        // moving rather than ticking at a fixed rate regardless of speed.
        public float BobCyclesPerMeter = 0.8f;
        public float BobHeightAmplitude = 0.05f;
        public float BobSwayAmplitude = 0.025f;

        // Disabled while walking so the Designer's own orbit/pan/zoom mouse-drag logic
        // (which lives in its Update loop) doesn't also react to our WASD/mouse input,
        // and re-enabled on toggle-off.
        public Assets.Scripts.Design.DesignerCameraScript DesignerCameraScriptRef;

        // While the F2 structure-editor panel is open, WASD/arrow-key input is
        // skipped entirely (see LateUpdate) - otherwise typing into one of its text
        // fields (scale/tint) or just clicking a toggle would also walk/spin the
        // camera underneath it, the same double-registration problem the IDialog
        // registration on StructureEditController already fights for mouse clicks.
        public StructureEditController StructureEditor;

        // IDialog implementation - registering while walk mode is active is a
        // best-effort attempt (same caveat as StructureEditController's own use of
        // this: there's no documented "suppress the designer's own keyboard-driven
        // part move/rotate" flag, only IUserInterface.AnyDialogsOpen, and whether
        // the designer's part-manipulation code actually checks it is unconfirmed)
        // to stop the SAME WASD presses this script reads for walking from also
        // rotating/moving whatever part is currently selected in the designer.
        public event DialogDelegate Closed;
        public bool AllowCameraZoom => true;
        void IDialog.Close()
        {
            if (_active)
            {
                DeactivateWalkMode();
            }
        }

        // Exposed so other camera-attached tools (StructureEditController) can gate
        // themselves on walk mode being on - they read raw mouse movement the same
        // way this controller's look does, which would fight the Designer's own
        // orbit-camera dragging if both listened to the mouse while it's off.
        public bool IsActive => _active;

        private bool _active;
        private float _yaw;
        private float _pitch;
        private Vector3 _preWalkPosition;
        private Quaternion _preWalkRotation;

        // The "clean" walked position, without the bob/sway offset baked in - the bob
        // is recomputed fresh from this each frame rather than added directly to
        // transform.position, otherwise it would accumulate into permanent drift
        // instead of oscillating in place.
        private Vector3 _walkPosition;
        private float _bobPhase;

        // Purely cosmetic held wrench, shown only while walking. Built once on first
        // use and just toggled active/inactive afterward, rather than being tied to
        // the hangar's own rebuild cycle - it belongs to the camera, not the scene.
        private GameObject _wrench;
        private static readonly Vector3 WrenchLocalPosition = new Vector3(0.35f, -0.3f, 0.6f);
        private static readonly Vector3 WrenchLocalEuler = new Vector3(15f, -20f, -10f);

        // The imported mesh's own pivot/proportions don't match the hand-built
        // primitive wrench's, so it needs its own tuned transform rather than
        // reusing WrenchLocalPosition/Euler directly - pulled closer to camera and
        // further to the bottom-right corner so it reads as a held tool only
        // partially in frame (like the original), not a whole object floating in
        // the open. Scale is separate too, since imported real-world-scale meshes
        // are frequently the wrong size out of the box.
        private static readonly Vector3 ImportedWrenchLocalPosition = new Vector3(0.4f, -0.4f, 0.35f);
        private static readonly Vector3 ImportedWrenchLocalEuler = new Vector3(70f, 150f, 0f);
        private static readonly Vector3 ImportedWrenchLocalScale = new Vector3(1f, 1f, 1f);

        void LateUpdate()
        {
            if (Input.GetKeyDown(ToggleKey))
            {
                if (_active)
                {
                    DeactivateWalkMode();
                }
                else
                {
                    ActivateWalkMode();
                }
            }

            if (!_active)
            {
                return;
            }

            // Panel's open - freeze movement/look entirely rather than just the keys
            // that happen to collide with a text field, so clicking a toggle or
            // dragging the window doesn't unexpectedly spin/walk the camera either.
            bool editorOpen = StructureEditor != null && StructureEditor.IsOpen;

            // Arrow keys are the only look control - see the class comment for why
            // mouse-look was removed entirely.
            if (!editorOpen)
            {
                if (Input.GetKey(KeyCode.LeftArrow)) _yaw -= ArrowLookSpeed * Time.deltaTime;
                if (Input.GetKey(KeyCode.RightArrow)) _yaw += ArrowLookSpeed * Time.deltaTime;
                if (Input.GetKey(KeyCode.UpArrow)) _pitch += ArrowLookSpeed * Time.deltaTime;
                if (Input.GetKey(KeyCode.DownArrow)) _pitch -= ArrowLookSpeed * Time.deltaTime;
            }

            _pitch = Mathf.Clamp(_pitch, -80f, 80f);

            Quaternion yawOnly = Quaternion.Euler(0f, _yaw, 0f);
            Vector3 move = Vector3.zero;
            if (!editorOpen)
            {
                if (Input.GetKey(KeyCode.W)) move += yawOnly * Vector3.forward;
                if (Input.GetKey(KeyCode.S)) move -= yawOnly * Vector3.forward;
                if (Input.GetKey(KeyCode.D)) move += yawOnly * Vector3.right;
                if (Input.GetKey(KeyCode.A)) move -= yawOnly * Vector3.right;
            }

            bool isMoving = move.sqrMagnitude > 0f;
            if (isMoving)
            {
                move.Normalize();
                float step = WalkSpeed * Time.deltaTime;
                _walkPosition += move * step;
                _bobPhase += step * BobCyclesPerMeter * Mathf.PI * 2f;
            }
            else
            {
                // Ease back toward a neutral phase (feet together) instead of freezing
                // mid-stride the instant you stop.
                float neutralPhase = Mathf.Round(_bobPhase / Mathf.PI) * Mathf.PI;
                _bobPhase = Mathf.MoveTowards(_bobPhase, neutralPhase, Time.deltaTime * 6f);
            }

            float verticalBob = isMoving || Mathf.Abs(_bobPhase) > 0.001f ? Mathf.Sin(_bobPhase) * BobHeightAmplitude : 0f;
            float sway = isMoving || Mathf.Abs(_bobPhase) > 0.001f ? Mathf.Sin(_bobPhase * 0.5f) * BobSwayAmplitude : 0f;

            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            // Ground height comes from whichever platform (catwalk/stair step) the
            // player is over, eased toward smoothly rather than snapped, plus the bob
            // offset - this is what makes it read as "walking" rather than a perfectly
            // smooth glide, while still never actually flying.
            float targetGroundY = GetGroundHeight(_walkPosition, _currentGroundY);
            _currentGroundY = Mathf.MoveTowards(_currentGroundY, targetGroundY, StepClimbSpeed * Time.deltaTime);

            Vector3 pos = _walkPosition;
            pos.y = _currentGroundY + EyeHeight + verticalBob;
            pos += yawOnly * Vector3.right * sway;
            transform.position = pos;

            // Give the held wrench a little of the same footstep rhythm, exaggerated
            // slightly, so it swings naturally with each step instead of sitting dead
            // still relative to the camera.
            if (_wrench != null)
            {
                _wrench.transform.localPosition = WrenchLocalPosition + new Vector3(sway * 1.5f, verticalBob * 1.5f, 0f);
            }
        }

        private void ActivateWalkMode()
        {
            _active = true;
            _preWalkPosition = transform.position;
            _preWalkRotation = transform.rotation;

            _walkPosition = transform.position;
            _bobPhase = 0f;
            // Reference is FloorY (not a carried-over height) since this is a
            // fresh entry into walk mode - starting under a stacked balcony
            // column should never teleport you straight up onto it.
            _currentGroundY = GetGroundHeight(_walkPosition, FloorY);

            Vector3 startPos = _walkPosition;
            startPos.y = _currentGroundY + EyeHeight;
            transform.position = startPos;

            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;

            if (DesignerCameraScriptRef != null)
            {
                DesignerCameraScriptRef.enabled = false;
            }
            if (_wrench == null)
            {
                _wrench = CreateWrench();
            }
            _wrench.SetActive(true);

            IUserInterface userInterface = Assets.Scripts.Game.Instance?.UserInterface;
            if (userInterface != null)
            {
                userInterface.RegisterDialog(this);
            }

            Debug.Log("[DesignerBackgroundTest] Walk camera: ON (F1 to exit, WASD to move, arrow keys to look)");
        }

        private void DeactivateWalkMode()
        {
            _active = false;
            transform.position = _preWalkPosition;
            transform.rotation = _preWalkRotation;
            if (DesignerCameraScriptRef != null)
            {
                DesignerCameraScriptRef.enabled = true;
            }
            if (_wrench != null)
            {
                _wrench.SetActive(false);
            }

            IUserInterface userInterface = Assets.Scripts.Game.Instance?.UserInterface;
            if (userInterface != null)
            {
                userInterface.UnregisterDialog(this);
            }
            Closed?.Invoke(this);

            Debug.Log("[DesignerBackgroundTest] Walk camera: OFF");
        }

        // Repeated balcony levels stack directly above one another with the exact
        // same X/Z footprint (only TopY differs, by roughly one storey each) - so at
        // any point on the stairs, the platform list can contain several matching
        // rectangles at once, one per level. Without a bound, "highest match wins"
        // always snaps straight to the topmost level regardless of where you're
        // actually standing. This caps how far above the current height a match may
        // be considered - well above a single stair tread's rise (~0.5m) but well
        // below the gap between levels (~10m) - so climbing still tracks one level's
        // flight of stairs at a time instead of teleporting to the top.
        private const float MaxReachableStepUp = 1.5f;

        // Highest platform whose XZ rectangle contains the given position AND is
        // within reach of referenceY, or FloorY if none do (the base floor is always
        // the fallback, so it doesn't need its own platform entry). Simple linear
        // scan - Platforms only ever holds a few dozen entries per level (catwalk
        // segments + stair steps), so this is negligible cost.
        private float GetGroundHeight(Vector3 position, float referenceY)
        {
            float best = FloorY;
            for (int i = 0; i < Platforms.Count; i++)
            {
                WalkPlatform platform = Platforms[i];
                if (platform.Contains(position.x, position.z) && platform.TopY > best && platform.TopY <= referenceY + MaxReachableStepUp)
                {
                    best = platform.TopY;
                }
            }
            return best;
        }

        // Imported model (Assets/Models/Props/Wrench.fbx, built into a prefab by
        // PropSetupEditor same as the other custom props) takes priority; falls back
        // to the original hand-built primitive wrench if that prefab isn't available
        // yet (e.g. Setup Custom Props hasn't been run), so walk mode never ends up
        // with no wrench at all.
        private const string WrenchPrefabPath = "Assets/Models/Props/Wrench.prefab";

        private GameObject CreateWrench()
        {
            GameObject imported = LoadImportedWrench();
            return imported != null ? imported : CreateProceduralWrench();
        }

        private GameObject LoadImportedWrench()
        {
            GameObject prefab = Assets.Scripts.Mod.Instance.ResourceLoader.LoadAsset<GameObject>(WrenchPrefabPath);
            if (prefab == null)
            {
                return null;
            }

            GameObject instance = Object.Instantiate(prefab, transform);
            instance.name = "HeldWrench";
            instance.transform.localPosition = ImportedWrenchLocalPosition;
            instance.transform.localRotation = Quaternion.Euler(ImportedWrenchLocalEuler);
            instance.transform.localScale = ImportedWrenchLocalScale;
            return instance;
        }

        private GameObject CreateProceduralWrench()
        {
            GameObject root = new GameObject("HeldWrench");
            root.transform.SetParent(transform, false);
            root.transform.localPosition = WrenchLocalPosition;
            root.transform.localRotation = Quaternion.Euler(WrenchLocalEuler);

            Color gripColor = new Color(0.3f, 0.3f, 0.32f);
            Color chromeColor = new Color(0.8f, 0.81f, 0.83f);

            CreateWrenchPart(root.transform, "Handle", new Vector3(0f, 0f, 0f), new Vector3(0.04f, 0.28f, 0.04f), gripColor);
            CreateWrenchPart(root.transform, "Head", new Vector3(0f, 0.17f, 0f), new Vector3(0.14f, 0.06f, 0.05f), chromeColor);
            CreateWrenchPart(root.transform, "ProngLeft", new Vector3(-0.05f, 0.24f, 0f), new Vector3(0.04f, 0.08f, 0.05f), chromeColor);
            CreateWrenchPart(root.transform, "ProngRight", new Vector3(0.05f, 0.24f, 0f), new Vector3(0.04f, 0.08f, 0.05f), chromeColor);

            return root;
        }

        private static void CreateWrenchPart(Transform parent, string name, Vector3 localPosition, Vector3 size, Color color)
        {
            GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = size;
            part.GetComponent<Renderer>().material.color = color;

            Collider collider = part.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }
        }
    }
}
