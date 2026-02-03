using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MarchingCubes
{
/// <summary>
/// Core marching cubes: Texture3D/RenderTexture input, writes into a Unity Mesh's vertex/index GraphicsBuffers.
/// </summary>
public class MarchingCubesCore
{
    readonly ComputeShader _compute;
    ComputeBuffer _counterBuffer;
    ComputeBuffer _countReadbackBuffer;
    Mesh _mesh;
    GraphicsBuffer _vertexBuffer;
    GraphicsBuffer _indexBuffer;

    int _cachedWidth = -1;
    int _cachedHeight = -1;
    int _cachedDepth = -1;

    readonly uint[] _countReadback = new uint[1];

    AsyncGPUReadbackRequest _readbackRequest;
    bool _readbackPending;
    int _pendingW, _pendingH, _pendingD;

    const int MaxTriangleMultiplier = 5;

    public Mesh Mesh => _mesh;

    public MarchingCubesCore()
    {
        _compute = Resources.Load<ComputeShader>("MarchingCubesMesh");
        if (_compute == null)
            Debug.LogError("MarchingCubesCore: Could not load MarchingCubesMesh compute shader from Resources.");
    }

    void EnsureCapacity(int width, int height, int depth)
    {
        if (width == _cachedWidth && height == _cachedHeight && depth == _cachedDepth)
            return;

        ReleaseMeshAndCounter();

        int numVoxelsX = width - 1;
        int numVoxelsY = height - 1;
        int numVoxelsZ = depth - 1;
        int numVoxels = numVoxelsX * numVoxelsY * numVoxelsZ;
        int maxTriangles = numVoxels * MaxTriangleMultiplier;
        int vertexCount = maxTriangles * 3;

        _mesh = new Mesh();
        _mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
        _mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;

        var vp = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
        var vn = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3);
        _mesh.SetVertexBufferParams(vertexCount, vp, vn);
        _mesh.SetIndexBufferParams(vertexCount, IndexFormat.UInt32);
        _mesh.SetSubMesh(0, new SubMeshDescriptor(0, vertexCount), MeshUpdateFlags.DontRecalculateBounds);

        _vertexBuffer = _mesh.GetVertexBuffer(0);
        _indexBuffer = _mesh.GetIndexBuffer();

        _counterBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Counter);
        _countReadbackBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);

        _cachedWidth = width;
        _cachedHeight = height;
        _cachedDepth = depth;
    }

    void ApplySettings(RenderTexture densityMap, float isoLevel)
    {
        _compute.SetTexture(0, "DensityMap", densityMap);
        _compute.SetInts("densityMapSize", densityMap.width, densityMap.height, densityMap.volumeDepth);
        _compute.SetFloat("isoLevel", isoLevel);
        int numVoxels = (densityMap.width - 1) * (densityMap.height - 1) * (densityMap.volumeDepth - 1);
        _compute.SetInt("MaxTriangle", numVoxels * MaxTriangleMultiplier);
        _compute.SetBuffer(0, "VertexBuffer", _vertexBuffer);
        _compute.SetBuffer(0, "IndexBuffer", _indexBuffer);
        _compute.SetBuffer(0, "Counter", _counterBuffer);
    }

    void ApplySettings(Texture3D densityMap, float isoLevel)
    {
        _compute.SetTexture(0, "DensityMap", densityMap);
        _compute.SetInts("densityMapSize", densityMap.width, densityMap.height, densityMap.depth);
        _compute.SetFloat("isoLevel", isoLevel);
        int numVoxels = (densityMap.width - 1) * (densityMap.height - 1) * (densityMap.depth - 1);
        _compute.SetInt("MaxTriangle", numVoxels * MaxTriangleMultiplier);
        _compute.SetBuffer(0, "VertexBuffer", _vertexBuffer);
        _compute.SetBuffer(0, "IndexBuffer", _indexBuffer);
        _compute.SetBuffer(0, "Counter", _counterBuffer);
    }

    void SetBounds(int width, int height, int depth)
    {
        Vector3 size = new Vector3(width, height, depth);
        float maxDim = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        Vector3 scale = size / maxDim * 2f;
        _mesh.bounds = new Bounds(Vector3.zero, scale);
    }

    void CompleteReadbackAndApply(int w, int h, int d)
    {
        ComputeBuffer.CopyCount(_counterBuffer, _countReadbackBuffer, 0);
        _countReadbackBuffer.GetData(_countReadback);
        int actualVertexCount = (int)_countReadback[0] * 3;
        _mesh.SetSubMesh(0, new SubMeshDescriptor(0, actualVertexCount), MeshUpdateFlags.DontRecalculateBounds);
        SetBounds(w, h, d);
    }

    void RunInternalSync(int w, int h, int d)
    {
        _compute.DispatchThreads(0, w - 1, h - 1, d - 1);
        CompleteReadbackAndApply(w, h, d);
    }

    void RunInternalAsync(int w, int h, int d)
    {
        _compute.DispatchThreads(0, w - 1, h - 1, d - 1);
        ComputeBuffer.CopyCount(_counterBuffer, _countReadbackBuffer, 0);
        _readbackRequest = AsyncGPUReadback.Request(_countReadbackBuffer);
        _readbackPending = true;
        _pendingW = w;
        _pendingH = h;
        _pendingD = d;
    }

    /// <summary>
    /// Completes a pending async readback if one is done. Call from Update. Returns true if mesh was updated.
    /// </summary>
    public bool TryCompleteReadback()
    {
        if (!_readbackPending) return false;
        if (_readbackRequest.hasError)
        {
            _readbackPending = false;
            return false;
        }
        if (!_readbackRequest.done) return false;

        var data = _readbackRequest.GetData<uint>();
        int actualVertexCount = (int)data[0] * 3;
        _mesh.SetSubMesh(0, new SubMeshDescriptor(0, actualVertexCount), MeshUpdateFlags.DontRecalculateBounds);
        SetBounds(_pendingW, _pendingH, _pendingD);
        _readbackPending = false;
        return true;
    }

    public void RunAsync(RenderTexture densityMap, float isoLevel)
    {
        if (_compute == null || densityMap == null) return;

        int w = densityMap.width;
        int h = densityMap.height;
        int d = densityMap.volumeDepth;
        EnsureCapacity(w, h, d);
        _counterBuffer.SetCounterValue(0);
        ApplySettings(densityMap, isoLevel);
        RunInternalAsync(w, h, d);
    }

    public void RunAsync(Texture3D densityMap, float isoLevel)
    {
        if (_compute == null || densityMap == null) return;

        int w = densityMap.width;
        int h = densityMap.height;
        int d = densityMap.depth;
        EnsureCapacity(w, h, d);
        _counterBuffer.SetCounterValue(0);
        ApplySettings(densityMap, isoLevel);
        RunInternalAsync(w, h, d);
    }

    public void Run(RenderTexture densityMap, float isoLevel)
    {
        if (_compute == null || densityMap == null) return;

        int w = densityMap.width;
        int h = densityMap.height;
        int d = densityMap.volumeDepth;
        EnsureCapacity(w, h, d);
        _counterBuffer.SetCounterValue(0);
        ApplySettings(densityMap, isoLevel);
        RunInternalSync(w, h, d);
    }

    public void Run(Texture3D densityMap, float isoLevel)
    {
        if (_compute == null || densityMap == null) return;

        int w = densityMap.width;
        int h = densityMap.height;
        int d = densityMap.depth;
        EnsureCapacity(w, h, d);
        _counterBuffer.SetCounterValue(0);
        ApplySettings(densityMap, isoLevel);
        RunInternalSync(w, h, d);
    }

    void ReleaseMeshAndCounter()
    {
        _readbackPending = false;
        _countReadbackBuffer?.Release();
        _countReadbackBuffer = null;
        _counterBuffer?.Release();
        _counterBuffer = null;
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        _indexBuffer?.Dispose();
        _indexBuffer = null;
        if (_mesh != null)
        {
            UnityEngine.Object.Destroy(_mesh);
            _mesh = null;
        }
    }

    public void Release()
    {
        ReleaseMeshAndCounter();
    }
}
}
