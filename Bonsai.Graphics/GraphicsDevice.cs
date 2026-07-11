using System;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Bonsai.Graphics
{
    /// <summary>
    /// The DX12 device layer (ADR 0002): adapter selection, command queue,
    /// flip-model swapchain, per-frame fence synchronization, depth buffer,
    /// and the begin/end frame loop. Host-agnostic: constructed from an HWND
    /// and a client size only.
    /// </summary>
    public sealed class GraphicsDevice : IDisposable
    {
        public const int FrameCount = 3;
        public const Format BackBufferFormat = Format.R8G8B8A8_UNorm;
        public const Format DepthFormat = Format.D32_Float;

        private readonly IntPtr hwnd;
        private IDXGIFactory4 factory;
        private ID3D12Device2 device;
        private ID3D12CommandQueue queue;
        private IDXGISwapChain3 swapChain;

        private ID3D12DescriptorHeap rtvHeap;
        private ID3D12DescriptorHeap dsvHeap;
        private int rtvDescriptorSize;
        private readonly ID3D12Resource[] renderTargets = new ID3D12Resource[FrameCount];
        private ID3D12Resource depthBuffer;

        private readonly ID3D12CommandAllocator[] commandAllocators = new ID3D12CommandAllocator[FrameCount];
        private ID3D12GraphicsCommandList4 commandList;

        private ID3D12Fence fence;
        private readonly ulong[] fenceValues = new ulong[FrameCount];
        private AutoResetEvent fenceEvent;
        private int frameIndex;

        private readonly bool debugLayerEnabled;

        public ID3D12Device2 NativeDevice { get { return device; } }
        public ID3D12CommandQueue Queue { get { return queue; } }
        public ID3D12GraphicsCommandList4 CommandList { get { return commandList; } }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int FrameIndex { get { return frameIndex; } }
        public ID3D12Resource CurrentRenderTarget { get { return renderTargets[frameIndex]; } }

        /// <summary>True when constructed without a window: uploads and one-shot
        /// GPU work are available, but there is no swapchain to render to.</summary>
        public bool IsHeadless { get { return swapChain == null; } }

        /// <summary>Creates a headless device (no window/swapchain) for offscreen
        /// work such as asset upload validation. BeginFrame/EndFrame/Resize are
        /// unavailable; ExecuteOneShot and WaitIdle work normally.</summary>
        public GraphicsDevice(bool enableDebugLayer)
            : this(IntPtr.Zero, 1, 1, enableDebugLayer)
        {
        }

        public GraphicsDevice(IntPtr windowHandle, int width, int height, bool enableDebugLayer)
        {
            hwnd = windowHandle;
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            debugLayerEnabled = enableDebugLayer;

            if (enableDebugLayer)
            {
                ID3D12Debug debug;
                if (D3D12.D3D12GetDebugInterface(out debug).Success)
                {
                    debug.EnableDebugLayer();
                    debug.Dispose();
                }
            }

            factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(enableDebugLayer);

            device = CreateDeviceOnBestAdapter();
            queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));

            if (hwnd != IntPtr.Zero)
            {
                var swapChainDesc = new SwapChainDescription1
                {
                    Width = (uint)Width,
                    Height = (uint)Height,
                    Format = BackBufferFormat,
                    BufferCount = FrameCount,
                    BufferUsage = Usage.RenderTargetOutput,
                    SwapEffect = SwapEffect.FlipDiscard,
                    SampleDescription = SampleDescription.Default,
                };
                using (IDXGISwapChain1 swapChain1 = factory.CreateSwapChainForHwnd(queue, hwnd, swapChainDesc))
                {
                    swapChain = swapChain1.QueryInterface<IDXGISwapChain3>();
                }
                // Fullscreen transitions are borderless (window-style based), never exclusive.
                factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
            }

            rtvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, FrameCount));
            dsvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
            rtvDescriptorSize = (int)device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

            for (int i = 0; i < FrameCount; i++)
                commandAllocators[i] = device.CreateCommandAllocator(CommandListType.Direct);

            commandList = device.CreateCommandList<ID3D12GraphicsCommandList4>(CommandListType.Direct, commandAllocators[0]);
            commandList.Close();

            fence = device.CreateFence(0, FenceFlags.None);
            fenceValues[0] = 1;
            fenceEvent = new AutoResetEvent(false);

            if (swapChain != null)
            {
                CreateSizeDependentResources();
                frameIndex = (int)swapChain.CurrentBackBufferIndex;
            }
        }

        private ID3D12Device2 CreateDeviceOnBestAdapter()
        {
            // Prefer the highest-performance hardware adapter.
            using (IDXGIFactory6 factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>())
            {
                if (factory6 != null)
                {
                    for (uint i = 0; ; i++)
                    {
                        IDXGIAdapter1 adapter;
                        if (factory6.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out adapter).Failure)
                            break;
                        using (adapter)
                        {
                            if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
                                continue;
                            ID3D12Device2 result;
                            if (D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out result).Success)
                                return result;
                        }
                    }
                }
            }
            // Fallback: default adapter order.
            for (uint i = 0; ; i++)
            {
                IDXGIAdapter1 adapter;
                if (factory.EnumAdapters1(i, out adapter).Failure)
                    break;
                using (adapter)
                {
                    if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
                        continue;
                    ID3D12Device2 result;
                    if (D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out result).Success)
                        return result;
                }
            }
            throw new InvalidOperationException("No Direct3D 12 capable hardware adapter found.");
        }

        private void CreateSizeDependentResources()
        {
            CpuDescriptorHandle rtvHandle = rtvHeap.GetCPUDescriptorHandleForHeapStart();
            for (int i = 0; i < FrameCount; i++)
            {
                renderTargets[i] = swapChain.GetBuffer<ID3D12Resource>((uint)i);
                device.CreateRenderTargetView(renderTargets[i], null, rtvHandle);
                rtvHandle += rtvDescriptorSize;
            }

            depthBuffer = device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                ResourceDescription.Texture2D(DepthFormat, (uint)Width, (uint)Height, 1, 1, 1, 0, ResourceFlags.AllowDepthStencil),
                ResourceStates.DepthWrite,
                new ClearValue(DepthFormat, 1.0f, 0));
            device.CreateDepthStencilView(depthBuffer, null, dsvHeap.GetCPUDescriptorHandleForHeapStart());
        }

        private void ReleaseSizeDependentResources()
        {
            for (int i = 0; i < FrameCount; i++)
            {
                if (renderTargets[i] != null)
                {
                    renderTargets[i].Dispose();
                    renderTargets[i] = null;
                }
            }
            if (depthBuffer != null)
            {
                depthBuffer.Dispose();
                depthBuffer = null;
            }
        }

        /// <summary>Resizes the swapchain. Call with the new client size; no-op if unchanged.</summary>
        public void Resize(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (swapChain == null || (width == Width && height == Height))
                return;

            WaitIdle();
            ReleaseSizeDependentResources();

            Width = width;
            Height = height;
            swapChain.ResizeBuffers(FrameCount, (uint)width, (uint)height, BackBufferFormat, SwapChainFlags.None).CheckError();

            CreateSizeDependentResources();
            frameIndex = (int)swapChain.CurrentBackBufferIndex;

            // Reset per-slot fence bookkeeping: the back-buffer order changed,
            // so stale slot values would make a future frame signal a lower
            // value than already completed and then wait on one never reached.
            ulong highest = 0;
            for (int i = 0; i < FrameCount; i++)
                if (fenceValues[i] > highest)
                    highest = fenceValues[i];
            for (int i = 0; i < FrameCount; i++)
                fenceValues[i] = highest;
        }

        /// <summary>
        /// Begins the frame: resets the frame's allocator and the command list,
        /// transitions the back buffer to render-target, binds and clears
        /// RTV + depth, and sets viewport/scissor.
        /// </summary>
        public ID3D12GraphicsCommandList4 BeginFrame(Color4 clearColor)
        {
            if (swapChain == null)
                throw new InvalidOperationException("Headless device has no swapchain to render to.");
            commandAllocators[frameIndex].Reset();
            commandList.Reset(commandAllocators[frameIndex]);

            commandList.ResourceBarrierTransition(renderTargets[frameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

            CpuDescriptorHandle rtvHandle = rtvHeap.GetCPUDescriptorHandleForHeapStart() + frameIndex * rtvDescriptorSize;
            CpuDescriptorHandle dsvHandle = dsvHeap.GetCPUDescriptorHandleForHeapStart();
            commandList.OMSetRenderTargets(rtvHandle, dsvHandle);
            commandList.ClearRenderTargetView(rtvHandle, clearColor);
            commandList.ClearDepthStencilView(dsvHandle, ClearFlags.Depth, 1.0f, 0);

            commandList.RSSetViewport(new Viewport(0, 0, Width, Height, 0.0f, 1.0f));
            commandList.RSSetScissorRect(new RectI(0, 0, Width, Height));
            return commandList;
        }

        /// <summary>Rebinds the swapchain render target + depth and restores
        /// viewport/scissor (after an offscreen render-to-texture pass).</summary>
        public void BindBackbuffer(ID3D12GraphicsCommandList4 list)
        {
            CpuDescriptorHandle rtvHandle = rtvHeap.GetCPUDescriptorHandleForHeapStart() + frameIndex * rtvDescriptorSize;
            list.OMSetRenderTargets(rtvHandle, dsvHeap.GetCPUDescriptorHandleForHeapStart());
            list.RSSetViewport(new Viewport(0, 0, Width, Height, 0.0f, 1.0f));
            list.RSSetScissorRect(new RectI(0, 0, Width, Height));
        }

        /// <summary>Ends the frame: transitions to present, executes, presents (vsync), and advances the fence.</summary>
        public void EndFrame()
        {
            commandList.ResourceBarrierTransition(renderTargets[frameIndex], ResourceStates.RenderTarget, ResourceStates.Present);
            commandList.Close();
            queue.ExecuteCommandList(commandList);

            swapChain.Present(1, PresentFlags.None).CheckError();

            MoveToNextFrame();
        }

        private void MoveToNextFrame()
        {
            ulong currentValue = fenceValues[frameIndex];
            queue.Signal(fence, currentValue);

            frameIndex = (int)swapChain.CurrentBackBufferIndex;

            if (fence.CompletedValue < fenceValues[frameIndex])
            {
                fence.SetEventOnCompletion(fenceValues[frameIndex], fenceEvent);
                fenceEvent.WaitOne();
            }
            fenceValues[frameIndex] = currentValue + 1;
        }

        /// <summary>
        /// Records commands into a temporary command list, executes them, and
        /// blocks until finished. For one-shot work like resource uploads.
        /// </summary>
        public void ExecuteOneShot(Action<ID3D12GraphicsCommandList4> record)
        {
            using (ID3D12CommandAllocator allocator = device.CreateCommandAllocator(CommandListType.Direct))
            using (ID3D12GraphicsCommandList4 list = device.CreateCommandList<ID3D12GraphicsCommandList4>(CommandListType.Direct, allocator))
            {
                record(list);
                list.Close();
                queue.ExecuteCommandList(list);
                WaitIdle();
            }
        }

        /// <summary>Blocks until the GPU has drained all submitted work.</summary>
        public void WaitIdle()
        {
            ulong value = fenceValues[frameIndex];
            queue.Signal(fence, value);
            fence.SetEventOnCompletion(value, fenceEvent);
            fenceEvent.WaitOne();
            fenceValues[frameIndex] = value + 1;
        }

        /// <summary>
        /// Returns the number of error/corruption messages the debug layer has
        /// recorded, and writes them to the console. 0 when the debug layer is off.
        /// </summary>
        public int ReportDebugMessages()
        {
            if (!debugLayerEnabled)
                return 0;
            using (ID3D12InfoQueue infoQueue = device.QueryInterfaceOrNull<ID3D12InfoQueue>())
            {
                if (infoQueue == null)
                    return 0;
                int errors = 0;
                ulong count = infoQueue.NumStoredMessages;
                for (ulong i = 0; i < count; i++)
                {
                    Message message = infoQueue.GetMessage(i);
                    if (message.Severity == MessageSeverity.Error || message.Severity == MessageSeverity.Corruption)
                    {
                        errors++;
                        Console.Error.WriteLine("[D3D12 {0}] {1}", message.Severity, message.Description);
                    }
                }
                return errors;
            }
        }

        public void Dispose()
        {
            WaitIdle();
            ReleaseSizeDependentResources();
            if (commandList != null) commandList.Dispose();
            for (int i = 0; i < FrameCount; i++)
                if (commandAllocators[i] != null) commandAllocators[i].Dispose();
            if (fence != null) fence.Dispose();
            if (fenceEvent != null) fenceEvent.Dispose();
            if (rtvHeap != null) rtvHeap.Dispose();
            if (dsvHeap != null) dsvHeap.Dispose();
            if (swapChain != null) swapChain.Dispose();
            if (queue != null) queue.Dispose();
            if (device != null) device.Dispose();
            if (factory != null) factory.Dispose();
        }
    }
}
