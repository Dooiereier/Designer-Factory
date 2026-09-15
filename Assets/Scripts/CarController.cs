using System.Collections.Generic;
using UnityEngine;

namespace DesignerBackgroundTest
{
    // Spawns cars that loop through whatever structures have been flagged "Waypoint
    // for car path" in the F2 structure editor (see
    // StructureEditController.GetWaypointPositions) - place a few primitives (or
    // anything else) around the hangar/yard, tick that box on each one, and cars
    // drive a loop through them in placement order.
    //
    // Three vehicle types - caller picks the exact mix via VehicleKind (see
    // BuildHangar's fixed spawn list) rather than this factory rolling it randomly.
    //
    // Mod-bundled prefab loaded through Mod.Instance.ResourceLoader rather than
    // UnityEngine.Resources - same reasoning as every other imported prop/character
    // this mod ships (see WanderingDrood.cs) - assets this mod bundles itself use the
    // mod's own resource loader, not the game's.
    public static class CarFactory
    {
        public enum VehicleKind
        {
            TuskTruck,
            M939Truck,
            CargoTruck,
        }

        private const string TuskTruckPrefabPath = "Assets/Models/Props/Tusk_Truck.prefab";
        private const string M939TruckPrefabPath = "Assets/Models/Props/M939Truck.prefab";
        private const string CargoTruckPrefabPath = "Assets/Models/Props/CargoTruck.prefab";

        // The M939 truck model's own mesh forward axis doesn't line up with Unity's
        // forward convention the way the Range Rover's does - CarController's
        // movement code sets the ROOT's rotation to face the direction of travel
        // directly, so without a correction the truck drives sideways. Rather than
        // bake a fixed rotation into the root (which the movement code would just
        // overwrite next frame), this is applied to the MODEL as a child of the
        // moving root, so the root still faces travel direction correctly while the
        // mesh underneath it is rotated to compensate. -90 around Y = 90 degrees
        // counter-clockwise viewed from above (Unity's Y-axis rotation is clockwise
        // from above for positive angles).
        private const float TruckModelYawCorrectionDegrees = -90f;

        // Uniform multiplier on top of each model's own imported scale (1,1,1) -
        // same reasoning as WanderingDrood.DroodScaleMultiplier.
        private const float CarScaleMultiplier = 1.3f;

        private static string GetPrefabPath(VehicleKind kind)
        {
            switch (kind)
            {
                case VehicleKind.M939Truck: return M939TruckPrefabPath;
                case VehicleKind.CargoTruck: return CargoTruckPrefabPath;
                default: return TuskTruckPrefabPath;
            }
        }

        // CargoTruck and TuskTruck haven't been checked in-game yet, so they
        // default to no correction (same as the Range Rover before it) until
        // confirmed one way or the other - if either drives sideways like the M939
        // originally did, this is the first place to add a correction for it.
        private static Quaternion GetModelYawCorrection(VehicleKind kind)
        {
            if (kind == VehicleKind.M939Truck)
            {
                return Quaternion.Euler(0f, TruckModelYawCorrectionDegrees, 0f);
            }
            return Quaternion.identity;
        }

        public static CarController Create(Transform parent, Vector3 startPosition, float floorY, StructureEditController waypointSource, int startWaypointIndex, VehicleKind kind)
        {
            string prefabPath = GetPrefabPath(kind);
            GameObject prefab = Assets.Scripts.Mod.Instance.ResourceLoader.LoadAsset<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogWarning("[DesignerBackgroundTest] Could not load car prefab at " + prefabPath);
                return null;
            }

            GameObject root = new GameObject("Car");
            root.transform.SetParent(parent, false);
            root.transform.position = new Vector3(startPosition.x, floorY, startPosition.z);

            GameObject model = Object.Instantiate(prefab, root.transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = GetModelYawCorrection(kind);
            // Applied before the bounds measurement below, not after - groundOffset
            // and the steering pivot shift both derive from combined.size/min, so
            // scaling first keeps both proportioned to the car's actual (now 30%
            // larger) size instead of being computed against the pre-scale mesh.
            model.transform.localScale = Vector3.one * CarScaleMultiplier;

            // The model's pivot isn't necessarily at ground level (many downloaded
            // FBX models are centered on the mesh instead) - measure the actual
            // mesh bounds and lift the ROOT (not the model child) so its lowest
            // point touches the floor, rather than assuming the pivot already sits
            // there. Without this the car was ending up partially underground.
            //
            // Taken here (root is still at identity rotation, and the model's own
            // yaw correction above already aligned its forward with root's local
            // +Z, which at this exact moment is also world +Z) rather than after
            // Update() starts rotating the root - that's what makes this bounds
            // measurement's Z-extent a reliable stand-in for the car's real
            // front-to-back length, used again just below for the steering pivot.
            Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                Bounds combined = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                {
                    combined.Encapsulate(renderers[i].bounds);
                }
                float groundOffset = floorY - combined.min.y;
                root.transform.position += new Vector3(0f, groundOffset, 0f);

                // Update() turns the root itself toward the travel direction each
                // frame, same as WanderingDrood - fine for a pedestrian, but a car
                // pivoting around its centre reads as crabbing sideways rather than
                // steering. Shifting the model forward off the root (rather than
                // moving where the root travels) puts the root - the thing that
                // actually turns - back near the rear axle instead of the
                // geometric centre, so during a turn the back stays closer to the
                // path while the front swings out, reading as front-wheel steering
                // without changing any of the actual path-following logic.
                const float SteeringPivotRearShiftFraction = 0.3f;
                model.transform.localPosition += new Vector3(0f, 0f, combined.size.z * SteeringPivotRearShiftFraction);
            }

            CarController controller = root.AddComponent<CarController>();
            controller.Initialize(waypointSource, floorY, startWaypointIndex);
            return controller;
        }
    }

    // Deliberately simple, code-only follower (same style as WanderingDrood) rather
    // than a physics-driven vehicle - reads the CURRENT waypoint list fresh every
    // frame from StructureEditController instead of caching a snapshot at spawn
    // time, so adding/moving/removing waypoints via F2 takes effect on the car's
    // next lap without needing to respawn it.
    public class CarController : MonoBehaviour
    {
        public float Speed = 9f;
        public float TurnSpeed = 90f;
        public float ArriveDistance = 1.5f;

        // How far off-heading (in degrees, between the car's current facing and the
        // direction it actually needs to go this frame) counts as a "sharp turn" -
        // at or beyond this angle mismatch, speed is cut to MinTurnSpeedFraction; at
        // zero mismatch (driving straight), full Speed applies. transform.rotation
        // only turns toward the target direction at TurnSpeed degrees/sec rather
        // than snapping instantly, so this angle is naturally large right as a
        // sharp waypoint-to-waypoint direction change begins and shrinks back to
        // zero as the car straightens out into it - exactly the "sharp turn" window
        // this is meant to slow down for.
        public float SharpTurnAngle = 60f;
        public float MinTurnSpeedFraction = 0.35f;

        private StructureEditController _waypointSource;
        private float _floorY;
        private int _waypointIndex;

        public void Initialize(StructureEditController waypointSource, float floorY, int startWaypointIndex)
        {
            _waypointSource = waypointSource;
            _floorY = floorY;
            _waypointIndex = startWaypointIndex;
        }

        void Update()
        {
            if (_waypointSource == null)
            {
                return;
            }

            List<Vector3> waypoints = _waypointSource.GetWaypointPositions();
            if (waypoints.Count == 0)
            {
                return;
            }
            if (_waypointIndex >= waypoints.Count)
            {
                _waypointIndex = 0;
            }

            Vector3 target = waypoints[_waypointIndex];
            target.y = _floorY;
            Vector3 toTarget = target - transform.position;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;

            if (distance < ArriveDistance)
            {
                _waypointIndex = (_waypointIndex + 1) % waypoints.Count;
                return;
            }

            Vector3 direction = toTarget / distance;

            float angleToTarget = Vector3.Angle(transform.forward, direction);
            float turnFactor = Mathf.Clamp01(angleToTarget / SharpTurnAngle);
            float speedMultiplier = Mathf.Lerp(1f, MinTurnSpeedFraction, turnFactor);

            transform.position += direction * (Speed * speedMultiplier * Time.deltaTime);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), TurnSpeed * Time.deltaTime);
        }
    }
}
