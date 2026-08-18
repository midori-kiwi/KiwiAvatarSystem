using System;
using System.Collections.Generic;
using UnityEngine;

namespace KiwiAvatarSystem.Runtime
{
    /// <summary>
    /// 1Euro Filter Implementation for ultra-low latency & jitter-free tracking.
    /// </summary>
    public class OneEuroFilterVector3
    {
        private float _minCutoff;
        private float _beta;
        private float _dCutoff;

        private Vector3 _xPrev;
        private Vector3 _dxPrev;
        private bool _isFirst = true;

        public OneEuroFilterVector3(float minCutoff = 1.0f, float beta = 0.0f, float dCutoff = 1.0f)
        {
            _minCutoff = minCutoff;
            _beta = beta;
            _dCutoff = dCutoff;
        }

        public Vector3 Filter(Vector3 x, float deltaTime)
        {
            if (_isFirst)
            {
                _isFirst = false;
                _xPrev = x;
                _dxPrev = Vector3.zero;
                return x;
            }

            if (deltaTime <= 0f) return _xPrev;

            Vector3 dx = (x - _xPrev) / deltaTime;
            Vector3 dxHat = Vector3.Lerp(_dxPrev, dx, Alpha(deltaTime, _dCutoff));

            float cutoff = _minCutoff + _beta * dxHat.magnitude;
            Vector3 xHat = Vector3.Lerp(_xPrev, x, Alpha(deltaTime, cutoff));

            _xPrev = xHat;
            _dxPrev = dxHat;
            return xHat;
        }

        private float Alpha(float deltaTime, float cutoff)
        {
            float tau = 1.0f / (2.0f * Mathf.PI * cutoff);
            return 1.0f / (1.0f + tau / deltaTime);
        }

        public void UpdateParams(float minCutoff, float beta)
        {
            _minCutoff = minCutoff;
            _beta = beta;
        }

        public void Reset()
        {
            _isFirst = true;
        }
    }

    /// <summary>
    /// Precision Tracking and Adaptive Face Fitting System
    /// </summary>
    [DisallowMultipleComponent]
    public class KiwiPrecisionFaceTracker : MonoBehaviour
    {
        [Header("Target Mesh / Base Geometry")]
        [SerializeField] private Transform targetModelHead;
        [SerializeField] private MeshFilter targetMeshFilter;
        [SerializeField] private bool autoFitPartsOnStart = true;

        [Header("Face Parts Anchors")]
        [SerializeField] private Transform leftEyeAnchor;
        [SerializeField] private Transform rightEyeAnchor;
        [SerializeField] private Transform mouthAnchor;

        [Header("Filter Settings (1Euro Filter)")]
        [Range(0.01f, 10f)] [SerializeField] private float minCutoff = 1.0f; // Low value = smooth, no jitter
        [Range(0.001f, 1f)] [SerializeField] private float beta = 0.05f;      // High value = zero latency during rapid movement
        [SerializeField] private float dCutoff = 1.0f;

        [Header("Tracking Multipliers")]
        [SerializeField] private Vector3 positionSensitivity = Vector3.one;
        [SerializeField] private Vector3 rotationSensitivity = Vector3.one;

        private OneEuroFilterVector3 _posFilter = new OneEuroFilterVector3();
        private OneEuroFilterVector3 _rotFilter = new OneEuroFilterVector3();

        private Vector3 _rawRawPosition;
        private Vector3 _rawRawRotationEuler;

        private void Awake()
        {
            _posFilter = new OneEuroFilterVector3(minCutoff, beta, dCutoff);
            _rotFilter = new OneEuroFilterVector3(minCutoff, beta, dCutoff);

            if (autoFitPartsOnStart)
            {
                AutoFitFaceParts();
            }
        }

        public void SetTrackingParameters(float newMinCutoff, float newBeta)
        {
            minCutoff = newMinCutoff;
            beta = newBeta;
            _posFilter.UpdateParams(minCutoff, beta);
            _rotFilter.UpdateParams(minCutoff, beta);
        }

        /// <summary>
        /// Update tracking transformation using raw landmark input.
        /// Fully mapped to Kiwi's relative direction (Right movement/turn/tilt = Kiwi's right).
        /// </summary>
        public void UpdateLandmarkTransform(Vector3 rawHeadPosition, Quaternion rawHeadRotation)
        {
            float dt = Time.deltaTime;
            if (dt <= 0.0f) return;

            // 1. Process 1Euro Filtering for ultra-fast & stable tracking
            Vector3 filteredPos = _posFilter.Filter(rawHeadPosition, dt);
            Vector3 filteredRotEuler = _rotFilter.Filter(rawHeadRotation.eulerAngles, dt);
            Quaternion filteredRot = Quaternion.Euler(filteredRotEuler);

            // 2. Coordinate Mapping: Match "Right turn / tilt / move -> Kiwi's Right"
            // Multiply sensitivities dynamically
            Vector3 targetPos = Vector3.Scale(filteredPos, positionSensitivity);
            
            // Mirror coordinate fix for relative Kiwi orientation
            targetPos.x = -targetPos.x; // Align mirror x-axis to Kiwi's natural right

            Vector3 euler = filteredRot.eulerAngles;
            // Normalize angles
            euler.x = NormalizeAngle(euler.x) * rotationSensitivity.x;
            euler.y = -NormalizeAngle(euler.y) * rotationSensitivity.y; // Match rotation direction
            euler.z = -NormalizeAngle(euler.z) * rotationSensitivity.z; // Match tilt direction

            if (targetModelHead != null)
            {
                targetModelHead.localPosition = targetPos;
                targetModelHead.localRotation = Quaternion.Euler(euler);
            }
        }

        /// <summary>
        /// Raycasts onto complex target geometry (spherical, flat, organic) to auto-place face parts.
        /// </summary>

        public void AutoFitFaceParts()
        {
            if (targetMeshFilter == null || targetModelHead == null) return;

            // Fit eyes and mouth anchors onto arbitrary surface mesh
            FitPartToSurface(leftEyeAnchor, new Vector3(-0.15f, 0.05f, 1.0f));
            FitPartToSurface(rightEyeAnchor, new Vector3(0.15f, 0.05f, 1.0f));
            FitPartToSurface(mouthAnchor, new Vector3(0.0f, -0.1f, 1.0f));
        }

        private void FitPartToSurface(Transform part, Vector3 localRayOrigin)
        {
            if (part == null) return;

            Ray ray = new Ray(targetModelHead.TransformPoint(localRayOrigin), -targetModelHead.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, 2.0f))
            {
                part.position = hit.point;
                part.rotation = Quaternion.LookRotation(-hit.normal, targetModelHead.up);
            }
        }

        private float NormalizeAngle(float angle)
        {
            while (angle > 180f) angle -= 360f;
            while (angle < -180f) angle += 360f;
            return angle;
        }
    }
}
