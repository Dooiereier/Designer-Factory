using System.Collections.Generic;
using UnityEngine;

namespace DesignerBackgroundTest
{
    // Spawns the real, rigged-and-animated Drood character(s) imported from Mixamo
    // (Assets/Models/Drood/ - DroodCharacter[2].fbx skinned meshes + DroodWalk.fbx/
    // DroodIdle.fbx/DroodRunBackward.fbx animations shared by both, wired into
    // DroodAnimator.controller and baked into DroodPrefab[2].prefab by
    // Assets/Editor/DroodSetupEditor.cs), replacing the earlier procedural
    // box-figure stand-in now that real characters were available. Both prefabs are
    // registered in ModData.asset's Prefabs list so they survive into the built mod,
    // and loaded here through Mod.Instance.ResourceLoader (Jundroo.ModTools.
    // IModResourceLoader) - the loader for assets THIS mod bundled itself, a
    // different system from ModApi.Common.Game.Instance.ResourceLoader (the game's
    // own loader, used for the game's shipped assets like the Planet Studio
    // structures) or UnityEngine.Resources (same reasoning, game-only).
    public static class DroodFactory
    {
        private const string PrefabPath = "Assets/Models/Drood/DroodPrefab.prefab";
        private const string SecondPrefabPath = "Assets/Models/Drood/DroodPrefab2.prefab";
        private const string ThirdPrefabPath = "Assets/Models/Drood/DroodPrefab3.prefab";

        // Character spawn mix: 50% first character, 30% second, 20% third.
        private const int FirstCharacterPercent = 50;
        private const int SecondCharacterPercent = 30;

        public static WanderingDrood Create(Transform parent, Vector3 groundPosition, float floorY, System.Random random, WalkCameraController playerCamera)
        {
            int roll = random.Next(100);
            string prefabPath = roll < FirstCharacterPercent
                ? PrefabPath
                : roll < FirstCharacterPercent + SecondCharacterPercent
                    ? SecondPrefabPath
                    : ThirdPrefabPath;
            GameObject prefab = Assets.Scripts.Mod.Instance.ResourceLoader.LoadAsset<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogWarning("[DesignerBackgroundTest] Could not load Drood prefab at " + prefabPath);
                return null;
            }

            GameObject instance = Object.Instantiate(prefab);
            instance.name = "Drood";
            instance.transform.SetParent(parent, false);
            instance.transform.position = new Vector3(groundPosition.x, floorY, groundPosition.z);

            Animator animator = instance.GetComponentInChildren<Animator>();
            if (animator == null)
            {
                Debug.LogWarning("[DesignerBackgroundTest] Drood prefab has no Animator - it will stand still.");
            }

            WanderingDrood wanderer = instance.AddComponent<WanderingDrood>();
            wanderer.Initialize(animator, floorY, playerCamera);
            return wanderer;
        }
    }

    // Ambles between random points on the hangar floor (and, sometimes, into the
    // cafeteria annex through its doorway), driving the real Animator's "IsWalking"/
    // "IsFleeing" bools (see DroodAnimator.controller) rather than any hand-rolled
    // limb animation - the imported mesh/rig/clips handle the actual look of moving
    // now. Deliberately not collider/physics based, consistent with everything else
    // built by this mod - it just wanders within rectangular bounds (with a circular
    // keep-out around the craft on the hangar side) rather than pathfinding around
    // real geometry, so it never touches the part-connection corruption risk
    // discovered earlier with real colliders.
    //
    // Four behavior states:
    //  - Idle: standing still, counting down to the next wander or a possible
    //    conversation (see DroodSocialCoordinator).
    //  - Walking: heading to a wander target (with a doorway waypoint if that means
    //    crossing between the hangar and the cafeteria - straight-line movement would
    //    otherwise cut through the wall between them).
    //  - Talking: walking to a shared meeting spot assigned by DroodSocialCoordinator,
    //    then holding a fixed facing angle opposite a paired partner for a while.
    //  - Fleeing: overrides everything else, whenever the walk camera (the player,
    //    while actually walking around in F1 mode) gets close - faces the camera and
    //    backs away using the RunBackward clip, until the camera is far enough away
    //    again.
    public class WanderingDrood : MonoBehaviour
    {
        public float WalkSpeed = 1.4f;
        public float TurnSpeed = 180f;
        public float MinIdleSeconds = 3f;
        public float MaxIdleSeconds = 10f;
        public float ArriveDistance = 0.3f;

        public float WanderMinX;
        public float WanderMaxX;
        public float WanderMinZ;
        public float WanderMaxZ;
        public float AvoidRadius;
        public float FloorY;

        // Structures placed via the F2 editor to steer wander targets clear of
        // (forklifts, crates, containers, ...) - read live each pick rather than
        // cached, same reasoning as CarController's waypoint list.
        public StructureEditController StructureSource;
        // Extra margin added around each structure's actual measured footprint (not
        // a substitute for one - see IsPointBlocked).
        public float StructureAvoidRadius = 3f;

        // Fixed keep-out zones for hangar-built geometry that ISN'T an F2-placed
        // structure and so never shows up in StructureSource's footprint list - the
        // stairs, specifically (see DesignerBackgroundTestPatch.BuildHangar's Drood
        // spawn setup for where this gets populated). Static per hangar build, so
        // these are handed in once rather than read live like the placed-structure
        // footprints are.
        public System.Collections.Generic.List<Bounds> ExtraAvoidZones;

        // How far a new wander target must be from every OTHER Drood's CURRENT
        // position - read live from DroodSocialCoordinator's registry each pick, not
        // cached (same reasoning as everything else here).
        public float DroodAvoidDistance = 2.5f;

        // Cafeteria wander bounds and the chance (per idle-to-walking transition) of
        // heading there instead of somewhere else in the hangar. Left at their
        // default (Min >= Max) the Drood simply never visits it - CanteenEnabled
        // reflects whether valid bounds were actually supplied.
        public float CanteenMinX;
        public float CanteenMaxX;
        public float CanteenMinZ;
        public float CanteenMaxZ;
        public float CanteenVisitChance = 0.3f;
        public Vector3 DoorwayPoint;
        private bool CanteenEnabled => CanteenMaxX > CanteenMinX && CanteenMaxZ > CanteenMinZ;

        // Fleeing only ever triggers while the player is actually walking around
        // (F1) - an orbiting design-view camera isn't "someone approaching" the way
        // the walk camera is. Release distance is further than the trigger distance
        // (hysteresis) so a Drood doesn't flicker in and out of fleeing right at the
        // boundary - with a stationary camera, the gap between the two (1m) is
        // roughly how far a Drood actually backs away before stopping.
        public WalkCameraController PlayerCamera;
        public float FleeTriggerDistance = 2.5f;
        public float FleeReleaseDistance = 4f;
        public float FleeSpeed = 3.5f;

        private enum BehaviorState
        {
            Idle,
            Walking,
            Talking,
            Fleeing,
        }

        private static readonly int IsWalkingHash = Animator.StringToHash("IsWalking");
        private static readonly int IsFleeingHash = Animator.StringToHash("IsFleeing");

        private Animator _animator;
        private BehaviorState _state = BehaviorState.Idle;

        private Vector3 _target;
        // Points to pass through before reaching _target - the doorway (when the
        // route crosses between hangar and cafeteria) and/or a detour point around
        // the craft's keep-out circle (see ComputeCraftDetour), in walk order.
        // Queued rather than a single slot since a route can need both at once.
        private readonly Queue<Vector3> _waypoints = new Queue<Vector3>();
        private float _idleTimer;

        private Quaternion _talkFacing;
        private WanderingDrood _talkPartner;
        private float _talkTimer;
        private float _talkDeadline;
        private float _talkCooldownUntil;

        // Generous upper bound on how long the walk-to-meet-spot phase is allowed to
        // take before the conversation is abandoned outright (see UpdateTalking).
        private const float MaxTalkApproachSeconds = 8f;

        // A Drood that just finished talking is still standing right next to its
        // former partner (only ConversationStandoff apart) - without a cooldown,
        // the next DroodSocialCoordinator.Tick() (every CheckInterval seconds) pairs
        // them straight back up, and the pair chains from one conversation into the
        // next forever, looking like one never-ending talk from the outside. This
        // cooldown forces a stretch of normal idle/wander time in between.
        private const float ConversationCooldownSeconds = 20f;

        public bool IsFreeForConversation => _state == BehaviorState.Idle && Time.time >= _talkCooldownUntil;

        public void Initialize(Animator animator, float floorY, WalkCameraController playerCamera)
        {
            _animator = animator;
            FloorY = floorY;
            PlayerCamera = playerCamera;
            DroodSocialCoordinator.Register(this);
        }

        void Update()
        {
            DroodSocialCoordinator.Tick();

            if (UpdateFleeCheck())
            {
                return;
            }

            switch (_state)
            {
                case BehaviorState.Idle:
                    _idleTimer -= Time.deltaTime;
                    if (_idleTimer <= 0f)
                    {
                        PickNewTarget();
                    }
                    break;
                case BehaviorState.Walking:
                    UpdateWalking();
                    break;
                case BehaviorState.Talking:
                    UpdateTalking();
                    break;
            }
        }

        // Highest-priority check, run every frame regardless of current state.
        // Returns true if fleeing behavior was (or continues to be) applied this
        // frame, in which case the rest of Update() should be skipped.
        private bool UpdateFleeCheck()
        {
            bool cameraIsThreat = PlayerCamera != null && PlayerCamera.IsActive;
            float distToCam = cameraIsThreat ? Vector3.Distance(transform.position, PlayerCamera.transform.position) : float.MaxValue;

            if (_state != BehaviorState.Fleeing && cameraIsThreat && distToCam < FleeTriggerDistance)
            {
                _state = BehaviorState.Fleeing;
                _talkPartner = null;
                SetAnim(IsWalkingHash, false);
                SetAnim(IsFleeingHash, true);
            }
            else if (_state == BehaviorState.Fleeing && distToCam > FleeReleaseDistance)
            {
                _state = BehaviorState.Idle;
                _idleTimer = UnityEngine.Random.Range(MinIdleSeconds, MaxIdleSeconds);
                SetAnim(IsFleeingHash, false);
            }

            if (_state != BehaviorState.Fleeing)
            {
                return false;
            }

            Vector3 toCam = PlayerCamera.transform.position - transform.position;
            toCam.y = 0f;
            if (toCam.sqrMagnitude > 0.0001f)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(toCam), TurnSpeed * Time.deltaTime);
            }
            transform.position -= transform.forward * (FleeSpeed * Time.deltaTime);
            return true;
        }

        private void UpdateWalking()
        {
            if (_waypoints.Count == 0)
            {
                _state = BehaviorState.Idle;
                SetAnim(IsWalkingHash, false);
                _idleTimer = UnityEngine.Random.Range(MinIdleSeconds, MaxIdleSeconds);
                return;
            }

            Vector3 currentGoal = _waypoints.Peek();
            Vector3 pos = transform.position;
            Vector3 toGoal = currentGoal - pos;
            toGoal.y = 0f;
            float distance = toGoal.magnitude;

            if (distance < ArriveDistance)
            {
                // Reached this leg's point - dequeue it. If that was the last one
                // (the real target), go idle now instead of waiting another frame.
                _waypoints.Dequeue();
                if (_waypoints.Count == 0)
                {
                    _state = BehaviorState.Idle;
                    SetAnim(IsWalkingHash, false);
                    _idleTimer = UnityEngine.Random.Range(MinIdleSeconds, MaxIdleSeconds);
                }
                return;
            }

            MoveToward(currentGoal, WalkSpeed);
        }

        private void UpdateTalking()
        {
            // Hard cap on the whole Talking state (walk-to-meet + hold), independent
            // of whether the pair ever actually reaches ArriveDistance - a Drood that
            // for any reason (bad target, obstruction, an oddly-proportioned imported
            // character, ...) never closes that last bit of distance would otherwise
            // stay "talking" forever, since _talkTimer below only ever ticks down
            // after arriving.
            if (Time.time >= _talkDeadline)
            {
                _state = BehaviorState.Idle;
                _talkPartner = null;
                _talkCooldownUntil = Time.time + ConversationCooldownSeconds;
                SetAnim(IsWalkingHash, false);
                _idleTimer = UnityEngine.Random.Range(MinIdleSeconds, MaxIdleSeconds);
                return;
            }

            Vector3 pos = transform.position;
            Vector3 toTarget = _target - pos;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;

            if (distance > ArriveDistance)
            {
                SetAnim(IsWalkingHash, true);
                MoveToward(_target, WalkSpeed);
                return;
            }

            // Arrived at the meeting spot - hold the assigned conversation angle
            // (see DroodSocialCoordinator) until the shared timer runs out.
            SetAnim(IsWalkingHash, false);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, _talkFacing, TurnSpeed * Time.deltaTime);

            _talkTimer -= Time.deltaTime;
            if (_talkTimer <= 0f)
            {
                _state = BehaviorState.Idle;
                _talkPartner = null;
                _talkCooldownUntil = Time.time + ConversationCooldownSeconds;
                _idleTimer = UnityEngine.Random.Range(MinIdleSeconds, MaxIdleSeconds);
            }
        }

        private void MoveToward(Vector3 goal, float speed)
        {
            Vector3 pos = transform.position;
            Vector3 toGoal = goal - pos;
            toGoal.y = 0f;
            float distance = toGoal.magnitude;
            if (distance < 0.0001f)
            {
                return;
            }
            Vector3 direction = SteerAwayFromObstacles(pos, toGoal / distance);
            float step = Mathf.Min(speed * Time.deltaTime, distance);
            transform.position = pos + direction * step;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), TurnSpeed * Time.deltaTime);
        }

        // Target-picking (PickHangarPoint/PickCanteenPoint) only ever validates the
        // eventual GOAL point against structures/stairs/other Droods - the straight
        // line walked to get there was never checked, so a Drood could still clip
        // straight through whatever happened to sit between two otherwise-valid
        // points, or through another Drood that wandered into the way after the
        // target was picked. This blends the straight-line direction toward the
        // goal with a repulsion push away from anything currently too close, so
        // movement visibly steers around obstacles instead of clipping through -
        // not real pathfinding (consistent with the rest of this mod's heuristic-
        // only approach to movement), just a continuous nudge.
        private Vector3 SteerAwayFromObstacles(Vector3 pos, Vector3 towardGoal)
        {
            Vector2 pos2D = new Vector2(pos.x, pos.z);
            Vector2 push = Vector2.zero;

            List<Vector3> otherDroods = DroodSocialCoordinator.GetOtherPositions(this);
            foreach (Vector3 other in otherDroods)
            {
                Vector2 away = pos2D - new Vector2(other.x, other.z);
                float dist = away.magnitude;
                if (dist > 0.001f && dist < DroodAvoidDistance)
                {
                    push += away / dist * (DroodAvoidDistance - dist);
                }
            }

            if (StructureSource != null)
            {
                foreach (Bounds footprint in StructureSource.GetPlacedFootprints())
                {
                    push += PushAwayFromRect(pos2D,
                        footprint.min.x - StructureAvoidRadius, footprint.max.x + StructureAvoidRadius,
                        footprint.min.z - StructureAvoidRadius, footprint.max.z + StructureAvoidRadius);
                }
            }

            if (ExtraAvoidZones != null)
            {
                foreach (Bounds zone in ExtraAvoidZones)
                {
                    push += PushAwayFromRect(pos2D, zone.min.x, zone.max.x, zone.min.z, zone.max.z);
                }
            }

            if (push.sqrMagnitude < 0.0001f)
            {
                return towardGoal;
            }

            Vector2 goal2D = new Vector2(towardGoal.x, towardGoal.z);
            Vector2 blended = goal2D + push;
            if (blended.sqrMagnitude < 0.0001f)
            {
                // Push exactly cancels the goal direction - step sideways instead of
                // stalling dead in place against whatever it's pushing away from.
                blended = new Vector2(-goal2D.y, goal2D.x);
            }
            Vector2 result = blended.normalized;
            return new Vector3(result.x, 0f, result.y);
        }

        // If `pos` (world X/Z, Y-component of the Vector2 holding world Z) is inside
        // the given rectangle, returns a vector pointing out through whichever edge
        // is nearest, scaled by how far in that direction it'd take to actually
        // clear it - a shallow graze near an edge gets a soft nudge, while deep
        // inside a large footprint gets pushed hard. Zero when already outside.
        private static Vector2 PushAwayFromRect(Vector2 pos, float minX, float maxX, float minZ, float maxZ)
        {
            if (pos.x < minX || pos.x > maxX || pos.y < minZ || pos.y > maxZ)
            {
                return Vector2.zero;
            }
            float distToMinX = pos.x - minX;
            float distToMaxX = maxX - pos.x;
            float distToMinZ = pos.y - minZ;
            float distToMaxZ = maxZ - pos.y;
            float smallest = Mathf.Min(Mathf.Min(distToMinX, distToMaxX), Mathf.Min(distToMinZ, distToMaxZ));

            if (smallest == distToMinX) return new Vector2(-1f, 0f) * smallest;
            if (smallest == distToMaxX) return new Vector2(1f, 0f) * smallest;
            if (smallest == distToMinZ) return new Vector2(0f, -1f) * smallest;
            return new Vector2(0f, 1f) * smallest;
        }

        private void SetAnim(int hash, bool value)
        {
            if (_animator != null)
            {
                _animator.SetBool(hash, value);
            }
        }

        // Called by DroodSocialCoordinator once it's paired this Drood up with a
        // partner - takes over from whatever Idle was about to do.
        public void BeginTalking(Vector3 standPosition, Quaternion facing, WanderingDrood partner, float talkSeconds)
        {
            _state = BehaviorState.Talking;
            _target = standPosition;
            _waypoints.Clear();
            _talkFacing = facing;
            _talkPartner = partner;
            _talkTimer = talkSeconds;
            _talkDeadline = Time.time + MaxTalkApproachSeconds + talkSeconds;
        }

        private void PickNewTarget()
        {
            bool visitingCanteen = CanteenEnabled && UnityEngine.Random.value < CanteenVisitChance;
            Vector3 candidate = visitingCanteen ? PickCanteenPoint() : PickHangarPoint();

            // Whichever side of the doorway's X we're currently on vs. where we're
            // headed - if they differ, route through the doorway first instead of
            // cutting straight through the wall between them.
            bool startInCanteen = CanteenEnabled && transform.position.x > DoorwayPoint.x;
            bool endInCanteen = CanteenEnabled && candidate.x > DoorwayPoint.x;

            _waypoints.Clear();
            Vector3 legStart = transform.position;
            if (startInCanteen != endInCanteen)
            {
                EnqueueLeg(legStart, DoorwayPoint);
                legStart = DoorwayPoint;
            }
            EnqueueLeg(legStart, candidate);

            _target = candidate;
            _state = BehaviorState.Walking;
            SetAnim(IsWalkingHash, true);
        }

        // Queues `end`, inserting a detour point first if the straight line from
        // `start` to `end` would cut through the craft's keep-out circle.
        // PickHangarPoint already keeps wander TARGETS outside that circle, but a
        // straight-line path between two valid targets can still clip through it if
        // the craft sits between them - this is what stopped Droods walking right
        // through/into it mid-route.
        private void EnqueueLeg(Vector3 start, Vector3 end)
        {
            Vector3? detour = ComputeCraftDetour(start, end);
            if (detour.HasValue)
            {
                _waypoints.Enqueue(detour.Value);
            }
            _waypoints.Enqueue(end);
        }

        // The craft sits at local/world (0, FloorY, 0) - same assumption
        // PickHangarPoint's own AvoidRadius check and the spawn-ring placement in
        // BuildHangar both already make. Returns null if the start->end segment
        // never comes within AvoidRadius of that point; otherwise a point just
        // outside the circle, on whichever side the segment already leans toward,
        // which is enough to route around it without real pathfinding.
        private Vector3? ComputeCraftDetour(Vector3 start, Vector3 end)
        {
            if (AvoidRadius <= 0f)
            {
                return null;
            }
            Vector2 a = new Vector2(start.x, start.z);
            Vector2 b = new Vector2(end.x, end.z);
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            float t = lenSq > 0.0001f ? Mathf.Clamp01(Vector2.Dot(-a, ab) / lenSq) : 0f;
            Vector2 closest = a + ab * t;
            float dist = closest.magnitude;
            if (dist >= AvoidRadius)
            {
                return null;
            }
            Vector2 outward = dist > 0.001f ? closest / dist : Vector2.Perpendicular(lenSq > 0.0001f ? ab.normalized : Vector2.right);
            Vector2 detour = outward * (AvoidRadius + 1.5f);
            return new Vector3(detour.x, FloorY, detour.y);
        }

        private Vector3 PickHangarPoint()
        {
            System.Collections.Generic.List<Bounds> structureFootprints =
                StructureSource != null ? StructureSource.GetPlacedFootprints() : null;
            System.Collections.Generic.List<Vector3> otherDroods = DroodSocialCoordinator.GetOtherPositions(this);

            Vector3 lastCandidate = new Vector3(AvoidRadius + 1f, FloorY, 0f);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                float x = UnityEngine.Random.Range(WanderMinX, WanderMaxX);
                float z = UnityEngine.Random.Range(WanderMinZ, WanderMaxZ);
                lastCandidate = new Vector3(x, FloorY, z);
                if (x * x + z * z < AvoidRadius * AvoidRadius)
                {
                    continue;
                }
                if (IsPointBlocked(x, z, structureFootprints, otherDroods))
                {
                    continue;
                }
                return lastCandidate;
            }
            // Every attempt landed inside the keep-out circle or too close to
            // something (structure/stairs/another Drood) - a very small or heavily
            // crowded wander area. Use the LAST randomly-generated candidate anyway
            // (still different every call) rather than falling back to one fixed
            // point, which is what was causing every Drood in a crowded area (like
            // the canteen) to converge on the exact same spot.
            return lastCandidate;
        }

        // Real footprint (measured renderer bounds, from StructureAvoidRadius as
        // margin around it) rather than a fixed radius around just the pivot - a
        // fixed radius badly under-covered large scaled-up props (couches, plants,
        // forklifts - some placed at 40-300x scale) while over-covering tiny ones,
        // which is why Droods kept wandering straight through/into them. Also checks
        // ExtraAvoidZones (stairs, ...) - hangar-built geometry that never shows up
        // in StructureSource's list since it isn't an F2-placed structure - and other
        // Droods' current positions, so they stop picking targets on top of each other.
        private bool IsPointBlocked(float x, float z, System.Collections.Generic.List<Bounds> structureFootprints,
            System.Collections.Generic.List<Vector3> otherDroods)
        {
            if (structureFootprints != null)
            {
                foreach (Bounds footprint in structureFootprints)
                {
                    if (x >= footprint.min.x - StructureAvoidRadius && x <= footprint.max.x + StructureAvoidRadius &&
                        z >= footprint.min.z - StructureAvoidRadius && z <= footprint.max.z + StructureAvoidRadius)
                    {
                        return true;
                    }
                }
            }
            if (ExtraAvoidZones != null)
            {
                foreach (Bounds zone in ExtraAvoidZones)
                {
                    if (x >= zone.min.x && x <= zone.max.x && z >= zone.min.z && z <= zone.max.z)
                    {
                        return true;
                    }
                }
            }
            if (otherDroods != null)
            {
                foreach (Vector3 dPos in otherDroods)
                {
                    float dx = x - dPos.x;
                    float dz = z - dPos.z;
                    if (dx * dx + dz * dz < DroodAvoidDistance * DroodAvoidDistance)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // Same retry-and-check pattern as PickHangarPoint - previously this just
        // picked one random point with no validation at all, which (combined with a
        // canteen now packed with large couches/plants) is why every Drood visiting
        // it kept converging on the exact same fixed fallback spot.
        private Vector3 PickCanteenPoint()
        {
            System.Collections.Generic.List<Bounds> structureFootprints =
                StructureSource != null ? StructureSource.GetPlacedFootprints() : null;
            System.Collections.Generic.List<Vector3> otherDroods = DroodSocialCoordinator.GetOtherPositions(this);

            Vector3 lastCandidate = new Vector3((CanteenMinX + CanteenMaxX) * 0.5f, FloorY, (CanteenMinZ + CanteenMaxZ) * 0.5f);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                float x = UnityEngine.Random.Range(CanteenMinX, CanteenMaxX);
                float z = UnityEngine.Random.Range(CanteenMinZ, CanteenMaxZ);
                lastCandidate = new Vector3(x, FloorY, z);
                if (!IsPointBlocked(x, z, structureFootprints, otherDroods))
                {
                    return lastCandidate;
                }
            }
            // Every attempt landed too close to something - use the LAST randomly
            // generated candidate anyway rather than one fixed centre point.
            return lastCandidate;
        }
    }
}
