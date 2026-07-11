using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;

namespace Bonsai.Graphics.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    public struct VertexPositionNormalTexture
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TexCoord;

        public const int SizeInBytes = 32;
    }

    /// <summary>GPU vertex/index buffers for one drawable mesh.</summary>
    public sealed class Mesh : IDisposable
    {
        private ID3D12Resource vertexBuffer;
        private ID3D12Resource indexBuffer;

        public VertexBufferView VertexBufferView { get; private set; }
        public IndexBufferView IndexBufferView { get; private set; }
        public int IndexCount { get; private set; }
        public bool IsDynamic { get { return true; } }

        /// <summary>Object-space bounds (useful for pivots and camera framing).</summary>
        public Vector3 BoundsMin { get; private set; }
        public Vector3 BoundsMax { get; private set; }
        public Vector3 BoundsCenter { get { return (BoundsMin + BoundsMax) * 0.5f; } }

        public unsafe Mesh(GraphicsDevice device, VertexPositionNormalTexture[] vertices, uint[] indices)
        {
            int vbSize = vertices.Length * VertexPositionNormalTexture.SizeInBytes;
            int ibSize = indices.Length * sizeof(uint);

            vertexBuffer = device.NativeDevice.CreateCommittedResource(
                new HeapProperties(HeapType.Upload), HeapFlags.None,
                ResourceDescription.Buffer((ulong)vbSize), ResourceStates.GenericRead);
            void* mapped;
            vertexBuffer.Map(0, null, &mapped).CheckError();
            fixed (VertexPositionNormalTexture* src = vertices)
                Buffer.MemoryCopy(src, mapped, vbSize, vbSize);
            vertexBuffer.Unmap(0);

            indexBuffer = device.NativeDevice.CreateCommittedResource(
                new HeapProperties(HeapType.Upload), HeapFlags.None,
                ResourceDescription.Buffer((ulong)ibSize), ResourceStates.GenericRead);
            indexBuffer.Map(0, null, &mapped).CheckError();
            fixed (uint* src = indices)
                Buffer.MemoryCopy(src, mapped, ibSize, ibSize);
            indexBuffer.Unmap(0);

            VertexBufferView = new VertexBufferView(vertexBuffer.GPUVirtualAddress, (uint)vbSize, VertexPositionNormalTexture.SizeInBytes);
            IndexBufferView = new IndexBufferView(indexBuffer.GPUVirtualAddress, (uint)ibSize, Vortice.DXGI.Format.R32_UInt);
            IndexCount = indices.Length;

            Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
            for (int i = 0; i < vertices.Length; i++)
            {
                min = Vector3.Min(min, vertices[i].Position);
                max = Vector3.Max(max, vertices[i].Position);
            }
            BoundsMin = min;
            BoundsMax = max;
        }

        /// <summary>
        /// Creates a dynamic quad-list mesh: capacity for maxQuads billboards,
        /// pre-built indices, vertices rewritten per frame via UpdateQuads.
        /// </summary>
        public static Mesh CreateDynamicQuads(GraphicsDevice device, int maxQuads)
        {
            var vertices = new VertexPositionNormalTexture[maxQuads * 4];
            var indices = new uint[maxQuads * 6];
            for (int q = 0; q < maxQuads; q++)
            {
                uint b = (uint)(q * 4);
                int i = q * 6;
                indices[i] = b; indices[i + 1] = b + 1; indices[i + 2] = b + 2;
                indices[i + 3] = b; indices[i + 4] = b + 2; indices[i + 5] = b + 3;
            }
            var mesh = new Mesh(device, vertices, indices);
            mesh.IndexCount = 0;
            return mesh;
        }

        /// <summary>Rewrites the vertex buffer (upload heap) and sets the drawn quad count.</summary>
        public unsafe void UpdateQuads(VertexPositionNormalTexture[] vertices, int quadCount)
        {
            int bytes = quadCount * 4 * VertexPositionNormalTexture.SizeInBytes;
            void* mapped;
            vertexBuffer.Map(0, null, &mapped).CheckError();
            fixed (VertexPositionNormalTexture* src = vertices)
                Buffer.MemoryCopy(src, mapped, VertexBufferView.SizeInBytes, bytes);
            vertexBuffer.Unmap(0);
            IndexCount = quadCount * 6;
        }

        public void Dispose()
        {
            if (vertexBuffer != null) { vertexBuffer.Dispose(); vertexBuffer = null; }
            if (indexBuffer != null) { indexBuffer.Dispose(); indexBuffer = null; }
        }
    }
}
