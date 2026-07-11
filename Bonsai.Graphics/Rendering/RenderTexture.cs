using System;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Bonsai.Graphics.Rendering
{
    /// <summary>An offscreen color+depth target (planar water reflections).</summary>
    public sealed class RenderTexture : IDisposable
    {
        private readonly GraphicsDevice device;
        private ID3D12DescriptorHeap rtvHeap;
        private ID3D12DescriptorHeap dsvHeap;
        private ID3D12Resource depth;
        private bool inRenderState;

        public ID3D12Resource Color { get; private set; }
        public int Width { get; }
        public int Height { get; }
        /// <summary>Wrapped as a Texture2D so materials/SRV heap can bind it.</summary>
        public Texture2D AsTexture { get; }

        public RenderTexture(GraphicsDevice device, int width, int height)
        {
            this.device = device;
            Width = width;
            Height = height;

            Color = device.NativeDevice.CreateCommittedResource(
                new HeapProperties(HeapType.Default), HeapFlags.None,
                ResourceDescription.Texture2D(GraphicsDevice.BackBufferFormat, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget),
                ResourceStates.PixelShaderResource,
                new ClearValue(GraphicsDevice.BackBufferFormat, new Color4(0.4f, 0.55f, 0.75f, 1f)));
            depth = device.NativeDevice.CreateCommittedResource(
                new HeapProperties(HeapType.Default), HeapFlags.None,
                ResourceDescription.Texture2D(GraphicsDevice.DepthFormat, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.AllowDepthStencil),
                ResourceStates.DepthWrite,
                new ClearValue(GraphicsDevice.DepthFormat, 1f, 0));

            rtvHeap = device.NativeDevice.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
            dsvHeap = device.NativeDevice.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
            device.NativeDevice.CreateRenderTargetView(Color, null, rtvHeap.GetCPUDescriptorHandleForHeapStart());
            device.NativeDevice.CreateDepthStencilView(depth, null, dsvHeap.GetCPUDescriptorHandleForHeapStart());

            AsTexture = Texture2D.Wrap(Color, width, height, GraphicsDevice.BackBufferFormat);
        }

        /// <summary>Binds and clears this target on the command list.</summary>
        public void Begin(ID3D12GraphicsCommandList4 commandList, Color4 clear)
        {
            commandList.ResourceBarrierTransition(Color, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
            inRenderState = true;
            commandList.OMSetRenderTargets(rtvHeap.GetCPUDescriptorHandleForHeapStart(), dsvHeap.GetCPUDescriptorHandleForHeapStart());
            commandList.ClearRenderTargetView(rtvHeap.GetCPUDescriptorHandleForHeapStart(), clear);
            commandList.ClearDepthStencilView(dsvHeap.GetCPUDescriptorHandleForHeapStart(), ClearFlags.Depth, 1f, 0);
            commandList.RSSetViewport(new Viewport(0, 0, Width, Height, 0f, 1f));
            commandList.RSSetScissorRect(new RectI(0, 0, Width, Height));
        }

        /// <summary>Transitions back to shader-readable. Caller rebinds the main target.</summary>
        public void End(ID3D12GraphicsCommandList4 commandList)
        {
            commandList.ResourceBarrierTransition(Color, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
            inRenderState = false;
        }

        public void Dispose()
        {
            AsTexture.Dispose();
            if (depth != null) depth.Dispose();
            if (rtvHeap != null) rtvHeap.Dispose();
            if (dsvHeap != null) dsvHeap.Dispose();
        }
    }
}
