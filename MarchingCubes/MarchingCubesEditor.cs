using UnityEngine;
using UnityEngine.InputSystem;

namespace MarchingCubes
{
    /// <summary>
    /// Attach to a Camera. When holding Shift and left-clicking or right-clicking on the marching cubes mesh,
    /// adds or subtracts density in a spherical region of the target's density RenderTexture, then recomputes the mesh.
    /// The marching cubes GameObject must have a Collider (e.g. MeshCollider) for raycasting.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class MarchingCubesEditor : MonoBehaviour
    {
        [Tooltip("The marching cubes object to edit. Must have a Collider (e.g. MeshCollider) for raycasting.")]
        public MarchingCubesRenderer target;

        [Tooltip("Layers to raycast against. Default: everything.")]
        public LayerMask raycastLayers = -1;

        [Header("Brush")]
        [Tooltip("Brush radius in world units.")]
        public float brushRadiusWorld = 1f;

        [Tooltip("Density added at brush center when Shift + Left Click (falloff to edge).")]
        public float addAmount = 0.1f;

        [Tooltip("Density subtracted at brush center when Shift + Right Click (falloff to edge).")]
        public float subtractAmount = 0.1f;

        [Tooltip("When holding the button, minimum seconds between paint operations. 0 = every frame.")]
        [Range(0f, 0.2f)]
        public float holdPaintInterval = 0.05f;

        [Header("Gizmos")]
        [Tooltip("Length of the ray when no hit (world units).")]
        public float gizmoRayLength = 100f;

        const string KernelName = "PaintDensity";
        ComputeShader _paintDensityCS;
        int _kernelPaint;

        Ray _lastRay;
        bool _lastHitValid;
        Vector3 _lastHitPoint;
        Vector3 _lastHitNormal;
        float _lastPaintTime = -1f;

        void OnEnable()
        {
            _paintDensityCS = Resources.Load<ComputeShader>("PaintDensity");
            if (_paintDensityCS == null)
                Debug.LogError("MarchingCubesEditor: Could not load PaintDensity compute shader from Resources.");
            else
                _kernelPaint = _paintDensityCS.FindKernel(KernelName);
        }

        void Update()
        {
            Camera cam = GetComponent<Camera>();
            Mouse mouse = Mouse.current;
            if (cam != null && mouse != null)
            {
                Vector2 mousePos = mouse.position.ReadValue();
                _lastRay = cam.ScreenPointToRay(new Vector3(mousePos.x, mousePos.y, 0f));
                _lastHitValid = Physics.Raycast(_lastRay, out RaycastHit hit, float.MaxValue, raycastLayers);
                if (_lastHitValid)
                {
                    _lastHitPoint = hit.point;
                    _lastHitNormal = hit.normal;
                }
            }

            if (target == null || _paintDensityCS == null || target.densityMap == null)
                return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || mouse == null)
                return;

            bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            if (!shift)
                return;

            bool addClick = mouse.leftButton.wasPressedThisFrame;
            bool subtractClick = mouse.rightButton.wasPressedThisFrame;
            bool addHold = mouse.leftButton.isPressed;
            bool subtractHold = mouse.rightButton.isPressed;

            bool add = addClick || addHold;
            bool subtract = subtractClick || subtractHold;
            if (!add && !subtract)
                return;
            if (add && subtract)
                return; // both held: do nothing

            // When holding (not initial click), throttle by holdPaintInterval
            if (!addClick && !subtractClick && holdPaintInterval > 0f && _lastPaintTime >= 0f)
            {
                if (Time.time - _lastPaintTime < holdPaintInterval)
                    return;
            }

            if (!_lastHitValid)
                return;

            if (target == null)
                return;

            RaycastHit hitForPaint = default;
            if (!Physics.Raycast(_lastRay, out hitForPaint, float.MaxValue, raycastLayers) ||
                hitForPaint.collider.gameObject != target.gameObject)
                return;

            // Mesh uses centered local space: coordToWorld(coord) = coord * voxelSize - size/2, so local is in [-size/2, size/2].
            // Therefore voxel coord = local / voxelSize + (w,h,d)/2.
            Vector3 local = target.transform.InverseTransformPoint(hitForPaint.point);
            float voxelSize = target.voxelSize;
            int w = target.densityMap.width;
            int h = target.densityMap.height;
            int d = target.densityMap.volumeDepth;
            float halfW = w * 0.5f;
            float halfH = h * 0.5f;
            float halfD = d * 0.5f;

            float cx = local.x / voxelSize + halfW;
            float cy = local.y / voxelSize + halfH;
            float cz = local.z / voxelSize + halfD;
            cx = Mathf.Clamp(cx, 0f, w);
            cy = Mathf.Clamp(cy, 0f, h);
            cz = Mathf.Clamp(cz, 0f, d);

            float radiusVoxels = brushRadiusWorld / voxelSize;
            float delta = add ? addAmount : -subtractAmount;

            if (!target.densityMap.enableRandomWrite)
            {
                Debug.LogWarning("MarchingCubesEditor: target.densityMap must have Enable Random Write enabled.");
                return;
            }

            _paintDensityCS.SetTexture(_kernelPaint, "DensityMap", target.densityMap);
            _paintDensityCS.SetInts("densityMapSize", w, h, d);
            _paintDensityCS.SetVector("centerVoxel", new Vector3(cx, cy, cz));
            _paintDensityCS.SetFloat("radiusVoxels", radiusVoxels);
            _paintDensityCS.SetFloat("delta", delta);

            int tx = (w + 7) / 8;
            int ty = (h + 7) / 8;
            int tz = (d + 7) / 8;
            _paintDensityCS.Dispatch(_kernelPaint, tx, ty, tz);

            target.InvalidateDensityCache();
            target.RecomputeMesh();
            _lastPaintTime = Time.time;
        }

        void OnDrawGizmos()
        {
            if (_lastRay.direction.sqrMagnitude < 0.0001f)
                return;

            Gizmos.color = _lastHitValid ? Color.green : Color.yellow;
            Vector3 end = _lastHitValid ? _lastHitPoint : _lastRay.origin + _lastRay.direction * gizmoRayLength;
            Gizmos.DrawLine(_lastRay.origin, end);
            if (_lastHitValid)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireSphere(_lastHitPoint, brushRadiusWorld);
                Gizmos.DrawLine(_lastHitPoint, _lastHitPoint + _lastHitNormal * (brushRadiusWorld * 0.5f));
            }
        }
    }
}
