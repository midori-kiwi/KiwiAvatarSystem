using UnityEngine;

namespace KiwiAvatarSystem.Runtime
{
    /// <summary>
    /// Secondary motion simulation for tails, ears, hair with adjustable drag/spring params.
    /// </summary>
    public class KiwiDynamicSecondaryPhysics : MonoBehaviour
    {
        [Header("Bone Setup")]
        [SerializeField] private Transform[] tailBones;

        [Header("Physics Controls")]
        [Range(0.0f, 1.0f)] [SerializeField] private float stiffness = 0.3f;
        [Range(0.0f, 1.0f)] [SerializeField] private float damping = 0.5f;
        [SerializeField] private Vector3 gravity = new Vector3(0, -9.81f, 0);

        private Vector3[] _boneVelocities;
        private Vector3[] _prevPositions;

        private void Start()
        {
            InitializeBones();
        }

        public void InitializeBones()
        {
            if (tailBones == null || tailBones.Length == 0) return;

            _boneVelocities = new Vector3[tailBones.Length];
            _prevPositions = new Vector3[tailBones.Length];

            for (int i = 0; i < tailBones.Length; i++)
            {
                if (tailBones[i] != null)
                {
                    _prevPositions[i] = tailBones[i].position;
                    _boneVelocities[i] = Vector3.zero;
                }
            }
        }

        private void LateUpdate()
        {
            if (tailBones == null) return;

            float dt = Time.deltaTime;
            if (dt <= 0.0001f) return;

            for (int i = 1; i < tailBones.Length; i++)
            {
                Transform bone = tailBones[i];
                Transform parent = tailBones[i - 1];

                if (bone == null || parent == null) continue;

                Vector3 currentPos = bone.position;
                Vector3 targetPos = parent.TransformPoint(bone.localPosition);

                // Velocity & Spring calculation
                Vector3 velocity = (currentPos - _prevPositions[i]) / dt;
                velocity *= (1.0f - damping);

                Vector3 force = (targetPos - currentPos) * (stiffness * 100f) + gravity;
                velocity += force * dt;

                Vector3 newPos = currentPos + velocity * dt;

                // Constraint distance
                float restLength = Vector3.Distance(parent.position, targetPos);
                Vector3 dir = (newPos - parent.position).normalized;
                bone.position = parent.position + dir * restLength;

                // Align Rotation
                if (dir != Vector3.zero)
                {
                    bone.rotation = Quaternion.LookRotation(dir, parent.up);
                }

                _prevPositions[i] = currentPos;
            }
        }
    }
}