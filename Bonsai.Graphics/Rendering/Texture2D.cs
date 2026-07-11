using System;
using System.IO;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Bonsai.Graphics.Rendering
{
    /// <summary>
    /// A shader-resource texture. Loads DDS (uncompressed BGRA/RGB and
    /// BC1/BC2/BC3) natively and PNG/JPG/BMP/GIF via System.Drawing.
    /// </summary>
    public sealed class Texture2D : IDisposable
    {
        private ID3D12Resource resource;

        public Format Format { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public ID3D12Resource Resource { get { return resource; } }

        /// <summary>Slot assigned by the renderer's SRV heap (set by SceneRenderer).</summary>
        internal int SrvIndex = -1;

        private bool ownsResource = true;

        private Texture2D() { }

        /// <summary>Wraps an externally owned resource (e.g. a render target).</summary>
        public static Texture2D Wrap(ID3D12Resource resource, int width, int height, Format format)
        {
            return new Texture2D { resource = resource, Width = width, Height = height, Format = format, ownsResource = false };
        }

        public static Texture2D Load(GraphicsDevice device, string path)
        {
            if (string.Equals(Path.GetExtension(path), ".dds", StringComparison.OrdinalIgnoreCase))
                return LoadDds(device, path);
            return LoadViaGdi(device, path);
        }

        /// <summary>Creates a texture from raw RGBA8 pixel data.</summary>
        public static Texture2D FromPixels(GraphicsDevice device, int width, int height, byte[] rgba)
        {
            var texture = new Texture2D { Width = width, Height = height, Format = Format.R8G8B8A8_UNorm };
            texture.Upload(device, rgba, width * 4, height);
            return texture;
        }

        #region GDI formats (PNG/JPG/BMP/GIF)
        private static Texture2D LoadViaGdi(GraphicsDevice device, string path)
        {
            using (var bitmap = new System.Drawing.Bitmap(path))
            {
                int width = bitmap.Width, height = bitmap.Height;
                var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                byte[] bgra = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * width * 4, width * 4);
                bitmap.UnlockBits(data);

                var texture = new Texture2D { Width = width, Height = height, Format = Format.B8G8R8A8_UNorm };
                texture.Upload(device, bgra, width * 4, height);
                return texture;
            }
        }
        #endregion

        #region DDS
        private const uint DdsMagic = 0x20534444;      // "DDS "
        private const uint FourCcDxt1 = 0x31545844;    // "DXT1"
        private const uint FourCcDxt3 = 0x33545844;    // "DXT3"
        private const uint FourCcDxt5 = 0x35545844;    // "DXT5"
        private const uint DdpfFourCc = 0x4;
        private const uint DdpfRgb = 0x40;
        private const uint DdpfAlphaPixels = 0x1;

        private static Texture2D LoadDds(GraphicsDevice device, string path)
        {
            byte[] file = File.ReadAllBytes(path);
            if (file.Length < 128 || BitConverter.ToUInt32(file, 0) != DdsMagic)
                throw new InvalidDataException("Not a DDS file: " + path);

            int height = BitConverter.ToInt32(file, 12);
            int width = BitConverter.ToInt32(file, 16);
            uint pfFlags = BitConverter.ToUInt32(file, 80);
            uint fourCc = BitConverter.ToUInt32(file, 84);
            uint rgbBitCount = BitConverter.ToUInt32(file, 88);
            uint rMask = BitConverter.ToUInt32(file, 92);
            int dataOffset = 128;

            var texture = new Texture2D { Width = width, Height = height };

            if ((pfFlags & DdpfFourCc) != 0)
            {
                Format format;
                int blockSize;
                switch (fourCc)
                {
                    case FourCcDxt1: format = Format.BC1_UNorm; blockSize = 8; break;
                    case FourCcDxt3: format = Format.BC2_UNorm; blockSize = 16; break;
                    case FourCcDxt5: format = Format.BC3_UNorm; blockSize = 16; break;
                    default:
                        throw new NotSupportedException(string.Format("DDS fourCC 0x{0:X8} not supported: {1}", fourCc, path));
                }
                texture.Format = format;
                int blocksWide = Math.Max(1, (width + 3) / 4);
                int blocksHigh = Math.Max(1, (height + 3) / 4);
                int rowPitch = blocksWide * blockSize;
                byte[] payload = new byte[rowPitch * blocksHigh];
                Array.Copy(file, dataOffset, payload, 0, Math.Min(payload.Length, file.Length - dataOffset));
                texture.Upload(device, payload, rowPitch, blocksHigh);
                return texture;
            }

            if ((pfFlags & DdpfRgb) != 0)
            {
                bool hasAlpha = (pfFlags & DdpfAlphaPixels) != 0;
                int srcStride;
                byte[] rgba = new byte[width * height * 4];
                if (rgbBitCount == 32)
                {
                    srcStride = width * 4;
                    bool redFirst = rMask == 0x000000FF;
                    for (int i = 0; i < width * height; i++)
                    {
                        int s = dataOffset + i * 4;
                        byte c0 = file[s], c1 = file[s + 1], c2 = file[s + 2], a = hasAlpha ? file[s + 3] : (byte)255;
                        if (redFirst) { rgba[i * 4] = c0; rgba[i * 4 + 1] = c1; rgba[i * 4 + 2] = c2; }
                        else { rgba[i * 4] = c2; rgba[i * 4 + 1] = c1; rgba[i * 4 + 2] = c0; }
                        rgba[i * 4 + 3] = a;
                    }
                }
                else if (rgbBitCount == 24)
                {
                    srcStride = width * 3;
                    for (int i = 0; i < width * height; i++)
                    {
                        int s = dataOffset + i * 3;
                        rgba[i * 4] = file[s + 2]; rgba[i * 4 + 1] = file[s + 1]; rgba[i * 4 + 2] = file[s]; rgba[i * 4 + 3] = 255;
                    }
                }
                else
                {
                    throw new NotSupportedException("DDS bit count " + rgbBitCount + " not supported: " + path);
                }
                texture.Format = Format.R8G8B8A8_UNorm;
                texture.Upload(device, rgba, width * 4, height);
                return texture;
            }

            throw new NotSupportedException("Unrecognized DDS pixel format: " + path);
        }
        #endregion

        /// <summary>Creates the default-heap texture and uploads one subresource through a staging buffer.</summary>
        private unsafe void Upload(GraphicsDevice device, byte[] data, int rowPitch, int rowCount)
        {
            resource = device.NativeDevice.CreateCommittedResource(
                new HeapProperties(HeapType.Default), HeapFlags.None,
                ResourceDescription.Texture2D(Format, (uint)Width, (uint)Height, 1, 1),
                ResourceStates.CopyDest);

            uint alignedPitch = (uint)((rowPitch + 255) & ~255);
            using (ID3D12Resource staging = device.NativeDevice.CreateCommittedResource(
                new HeapProperties(HeapType.Upload), HeapFlags.None,
                ResourceDescription.Buffer((ulong)(alignedPitch * rowCount)), ResourceStates.GenericRead))
            {
                void* mapped;
                staging.Map(0, null, &mapped).CheckError();
                fixed (byte* src = data)
                {
                    for (int row = 0; row < rowCount; row++)
                        Buffer.MemoryCopy(src + (long)row * rowPitch, (byte*)mapped + (long)row * alignedPitch, rowPitch, rowPitch);
                }
                staging.Unmap(0);

                var footprint = new PlacedSubresourceFootPrint
                {
                    Offset = 0,
                    Footprint = new SubresourceFootPrint(Format, (uint)Width, (uint)Height, 1, alignedPitch),
                };
                device.ExecuteOneShot(list =>
                {
                    list.CopyTextureRegion(new TextureCopyLocation(resource, 0), 0, 0, 0,
                        new TextureCopyLocation(staging, footprint), null);
                    list.ResourceBarrierTransition(resource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
                });
            }
        }

        public void Dispose()
        {
            if (resource != null)
            {
                if (ownsResource)
                    resource.Dispose();
                resource = null;
            }
        }
    }
}
