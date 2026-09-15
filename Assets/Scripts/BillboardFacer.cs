using UnityEngine;

namespace DesignerBackgroundTest
{
    // Rotates to always face the given camera - used for the 2D cloud billboards so
    // they read as soft round puffs from any viewing angle without needing real 3D
    // volume (a flat alpha-blended quad only looks right face-on).
    public class BillboardFacer : MonoBehaviour
    {
        public Camera TargetCamera;

        void LateUpdate()
        {
            if (TargetCamera == null)
            {
                return;
            }

            Vector3 toCamera = transform.position - TargetCamera.transform.position;
            if (toCamera.sqrMagnitude < 0.0001f)
            {
                return;
            }

            transform.rotation = Quaternion.LookRotation(toCamera);
        }
    }

    // Like BillboardFacer, but only rotates around the vertical (Y) axis - for
    // standing objects with a clear "up" (trees), a full 3-axis billboard visibly
    // tilts flat when viewed from above, which reads as obviously wrong in a way a
    // blobby cloud puff never does. Locking to Y keeps the trunk vertical no matter
    // where the camera is, while still turning to face the camera horizontally.
    public class YAxisBillboardFacer : MonoBehaviour
    {
        public Camera TargetCamera;

        void LateUpdate()
        {
            if (TargetCamera == null)
            {
                return;
            }

            Vector3 toCamera = transform.position - TargetCamera.transform.position;
            toCamera.y = 0f;
            if (toCamera.sqrMagnitude < 0.0001f)
            {
                return;
            }

            transform.rotation = Quaternion.LookRotation(toCamera);
        }
    }
}
