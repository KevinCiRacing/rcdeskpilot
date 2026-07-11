using System;
using System.Runtime.InteropServices;
using Bonsai.Graphics;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace Bonsai.Graphics.TestHost
{
    /// <summary>Renders one frame and reads the back buffer into a tightly packed RGBA array.</summary>
    internal static class FrameCapture
    {
        public static unsafe byte[] RenderAndReadback(GraphicsDevice device,
            Action<ID3D12GraphicsCommandList4> draw, Color4 clearColor)
        {
            int width = device.Width;
            int height = device.Height;
            uint rowPitch = (uint)((width * 4 + 255) & ~255);
            ulong bufferSize = (ulong)(rowPitch * height);

            using (ID3D12Resource readback = device.NativeDevice.CreateCommittedResource(
                new HeapProperties(HeapType.Readback), HeapFlags.None,
                ResourceDescription.Buffer(bufferSize), ResourceStates.CopyDest))
            {
                ID3D12GraphicsCommandList4 commandList = device.BeginFrame(clearColor);
                draw(commandList);

                commandList.ResourceBarrierTransition(device.CurrentRenderTarget, ResourceStates.RenderTarget, ResourceStates.CopySource);
                var footprint = new PlacedSubresourceFootPrint
                {
                    Offset = 0,
                    Footprint = new SubresourceFootPrint(GraphicsDevice.BackBufferFormat, (uint)width, (uint)height, 1, rowPitch),
                };
                commandList.CopyTextureRegion(
                    new TextureCopyLocation(readback, footprint), 0, 0, 0,
                    new TextureCopyLocation(device.CurrentRenderTarget, 0), null);
                commandList.ResourceBarrierTransition(device.CurrentRenderTarget, ResourceStates.CopySource, ResourceStates.RenderTarget);

                device.EndFrame();
                device.WaitIdle();

                byte[] pixels = new byte[width * height * 4];
                void* mapped;
                readback.Map(0, null, &mapped).CheckError();
                for (int y = 0; y < height; y++)
                    Marshal.Copy((IntPtr)((byte*)mapped + (long)y * rowPitch), pixels, y * width * 4, width * 4);
                readback.Unmap(0);
                return pixels;
            }
        }
    }
}
