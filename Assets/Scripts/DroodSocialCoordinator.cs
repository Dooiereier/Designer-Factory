using System.Collections.Generic;
using UnityEngine;

namespace DesignerBackgroundTest
{
    // Lightweight matchmaker for the "two Droods stop to talk" behavior - keeps a
    // registry of every currently-spawned Drood and, on a slow timer, looks for pairs
    // that are both free (idle - not walking somewhere, not already talking, not
    // fleeing the camera) and close enough to start a conversation. Deliberately
    // simple (first match wins, no queueing/priority), matching the rest of this
    // mod's wandering system - a missed pairing this tick just gets tried again next
    // tick.
    public static class DroodSocialCoordinator
    {
        public const float CheckInterval = 3f;
        public const float PairRadius = 6f;

        // Droods all spawn clustered around the same start arc, easily within
        // PairRadius of each other - without a startup delay, the very first Tick()
        // (which runs immediately, before anyone's taken a step) pairs several of
        // them up instantly, so the hangar opens with multiple conversations already
        // in progress. This gives them time to actually wander apart first.
        public const float InitialCheckDelay = 15f;

        // Each Drood's distance from the shared meeting midpoint, and how far it
        // turns away from directly facing its partner - two 45-degree turns (one
        // mirrored) put 90 degrees total between their facing directions instead of
        // 180 (dead-on) or 0.
        public const float ConversationStandoff = 0.3f;
        public const float ConversationAngle = 37f;
        public const float MinTalkSeconds = 5f;
        public const float MaxTalkSeconds = 30f;

        private static readonly List<WanderingDrood> _all = new List<WanderingDrood>();
        private static float _nextCheckTime;

        public static void Register(WanderingDrood drood)
        {
            _all.Add(drood);
        }

        // Every OTHER currently-registered Drood's position - used for Drood-Drood
        // avoidance when picking a new wander target, so they stop stacking directly
        // on top of each other (most visible in the canteen, where a crowded room
        // plus no Drood-Drood avoidance meant several could converge on the exact
        // same spot).
        public static List<Vector3> GetOtherPositions(WanderingDrood exclude)
        {
            List<Vector3> positions = new List<Vector3>(_all.Count);
            foreach (WanderingDrood drood in _all)
            {
                if (drood != null && drood != exclude)
                {
                    positions.Add(drood.transform.position);
                }
            }
            return positions;
        }

        // Called once per hangar rebuild before respawning Droods - the old ones
        // (and this registry's references to them) are about to be destroyed along
        // with the old hangarRoot, so stale entries need clearing first.
        public static void Clear()
        {
            _all.Clear();
            _nextCheckTime = Time.time + InitialCheckDelay;
        }

        // Safe to call from every Drood's Update() every frame - the time-gate below
        // means the actual O(n^2) pairing scan only ever runs once per
        // CheckInterval, not once per Drood per frame.
        public static void Tick()
        {
            if (Time.time < _nextCheckTime)
            {
                return;
            }
            _nextCheckTime = Time.time + CheckInterval;
            TryPairIdleDroods();
        }

        private static void TryPairIdleDroods()
        {
            for (int i = 0; i < _all.Count; i++)
            {
                WanderingDrood a = _all[i];
                if (a == null || !a.IsFreeForConversation)
                {
                    continue;
                }
                for (int j = i + 1; j < _all.Count; j++)
                {
                    WanderingDrood b = _all[j];
                    if (b == null || !b.IsFreeForConversation)
                    {
                        continue;
                    }
                    if (Vector3.Distance(a.transform.position, b.transform.position) <= PairRadius)
                    {
                        StartConversation(a, b);
                        break;
                    }
                }
            }
        }

        private static void StartConversation(WanderingDrood a, WanderingDrood b)
        {
            Vector3 posA = a.transform.position;
            Vector3 posB = b.transform.position;
            Vector3 midpoint = (posA + posB) * 0.5f;

            Vector3 dirAtoB = posB - posA;
            dirAtoB.y = 0f;
            dirAtoB = dirAtoB.sqrMagnitude > 0.0001f ? dirAtoB.normalized : Vector3.forward;

            Vector3 standA = midpoint - dirAtoB * ConversationStandoff;
            Vector3 standB = midpoint + dirAtoB * ConversationStandoff;

            // The craft sits at local/world (0,0) - same assumption every other
            // craft-avoidance check in this mod makes (see WanderingDrood's
            // AvoidRadius, which each Drood already carries). If this pairing would
            // plant either standing spot inside that keep-out circle, skip it
            // outright rather than pulling two Droods onto/into the craft - same as
            // any other missed pairing, it just gets tried again next Tick().
            float craftAvoidRadius = Mathf.Max(a.AvoidRadius, b.AvoidRadius);
            if (craftAvoidRadius > 0f &&
                (IsInsideCraftCircle(standA, craftAvoidRadius) || IsInsideCraftCircle(standB, craftAvoidRadius)))
            {
                return;
            }

            // Face the partner directly, then turn outward by ConversationAngle in
            // mirrored directions - see the class comment for why that lands the two
            // facing directions 90 degrees apart rather than dead-on.
            Quaternion facingA = Quaternion.LookRotation(dirAtoB) * Quaternion.Euler(0f, ConversationAngle, 0f);
            Quaternion facingB = Quaternion.LookRotation(-dirAtoB) * Quaternion.Euler(0f, -ConversationAngle, 0f);

            float talkSeconds = UnityEngine.Random.Range(MinTalkSeconds, MaxTalkSeconds);
            a.BeginTalking(standA, facingA, b, talkSeconds);
            b.BeginTalking(standB, facingB, a, talkSeconds);
        }

        private static bool IsInsideCraftCircle(Vector3 point, float radius)
        {
            return point.x * point.x + point.z * point.z < radius * radius;
        }
    }
}
