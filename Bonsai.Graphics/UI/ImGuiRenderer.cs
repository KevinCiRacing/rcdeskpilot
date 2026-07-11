using System;
using Bonsai.Graphics.Rendering;
using Bonsai.Graphics.Win32;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.D3D12;
using Hexa.NET.ImGui.Backends.Win32;
using Vortice.Direct3D12;
using ID3D12DescriptorHeap = Vortice.Direct3D12.ID3D12DescriptorHeap;
using ID3D12GraphicsCommandList4 = Vortice.Direct3D12.ID3D12GraphicsCommandList4;

namespace Bonsai.Graphics.UI
{
    /// <summary>
    /// Dear ImGui over the DX12 device (ADR 0003): Win32 input via the
    /// backend's WndProc handler hooked into Win32Window, rendering into the
    /// current frame's command list. Replaces the DXUT-style Bonsai toolkit.
    /// </summary>
    public sealed unsafe class ImGuiRenderer : IDisposable
    {
        private const int HeapSlots = 64;
        private readonly GraphicsDevice device;
        private readonly Win32Window window;
        private ID3D12DescriptorHeap srvHeap;
        private int srvSize;
        private int nextSlot = 1; // slot 0 = font atlas
        private ImGuiContextPtr context;

        // Descriptor allocator callbacks required by the 1.92 dynamic-texture
        // backend. Static: single ImGui instance per process.
        private static ulong heapCpuStart;
        private static ulong heapGpuStart;
        private static int heapIncrement;
        private static int backendNextSlot;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void AllocDescriptor(ImGuiImplDX12InitInfo* info, D3D12CpuDescriptorHandle* cpu, D3D12GpuDescriptorHandle* gpu)
        {
            int slot = backendNextSlot++;
            cpu->Ptr = (nuint)(heapCpuStart + (ulong)(slot * heapIncrement));
            gpu->Ptr = heapGpuStart + (ulong)(slot * heapIncrement);
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void FreeDescriptor(ImGuiImplDX12InitInfo* info, D3D12CpuDescriptorHandle cpu, D3D12GpuDescriptorHandle gpu)
        {
            // Slots are never recycled; the heap is generously sized for the
            // font atlas + menu icons.
        }

        public ImGuiRenderer(GraphicsDevice device, Win32Window window)
        {
            this.device = device;
            this.window = window;
            context = ImGui.CreateContext();
            ImGui.SetCurrentContext(context);
            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            ImGui.StyleColorsDark();
            var style = ImGui.GetStyle();
            style.WindowRounding = 8f;
            style.FrameRounding = 5f;
            style.WindowTitleAlign = new System.Numerics.Vector2(0.5f, 0.5f);

            srvHeap = device.NativeDevice.CreateDescriptorHeap(new DescriptorHeapDescription(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                HeapSlots, DescriptorHeapFlags.ShaderVisible));
            srvSize = (int)device.NativeDevice.GetDescriptorHandleIncrementSize(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

            ImGuiImplWin32.SetCurrentContext(context);
            ImGuiImplWin32.Init((void*)window.Handle);
            window.MessageHook = (hwnd, msg, wParam, lParam) =>
                (IntPtr)ImGuiImplWin32.WndProcHandler(hwnd, msg, (nuint)(nint)wParam, lParam);

            var initInfo = new ImGuiImplDX12InitInfo
            {
                Device = new ID3D12DevicePtr((Hexa.NET.ImGui.Backends.D3D12.ID3D12Device*)device.NativeDevice.NativePointer),
                CommandQueue = new ID3D12CommandQueuePtr((Hexa.NET.ImGui.Backends.D3D12.ID3D12CommandQueue*)device.Queue.NativePointer),
                NumFramesInFlight = GraphicsDevice.FrameCount,
                RTVFormat = 28, // DXGI_FORMAT_R8G8B8A8_UNORM
                DSVFormat = 40, // DXGI_FORMAT_D32_FLOAT
                SrvDescriptorHeap = new ID3D12DescriptorHeapPtr((Hexa.NET.ImGui.Backends.D3D12.ID3D12DescriptorHeap*)srvHeap.NativePointer),
                SrvDescriptorAllocFn = (delegate* unmanaged<ImGuiImplDX12InitInfo*, D3D12CpuDescriptorHandle*, D3D12GpuDescriptorHandle*, void>)&AllocDescriptor,
                SrvDescriptorFreeFn = (delegate* unmanaged<ImGuiImplDX12InitInfo*, D3D12CpuDescriptorHandle, D3D12GpuDescriptorHandle, void>)&FreeDescriptor,
            };
            heapCpuStart = (ulong)srvHeap.GetCPUDescriptorHandleForHeapStart().Ptr;
            heapGpuStart = srvHeap.GetGPUDescriptorHandleForHeapStart().Ptr;
            heapIncrement = srvSize;
            backendNextSlot = HeapSlots / 2; // backend-owned half; RegisterTexture uses the lower half
            ImGuiImplD3D12.SetCurrentContext(context);
            if (!ImGuiImplD3D12.Init(ref initInfo))
                throw new InvalidOperationException("ImGui DX12 backend init failed.");
        }

        /// <summary>Registers a texture for ImGui.Image use; returns its handle.</summary>
        public ImTextureID RegisterTexture(Texture2D texture)
        {
            if (nextSlot >= HeapSlots)
                throw new InvalidOperationException("ImGui SRV heap full.");
            int slot = nextSlot++;
            device.NativeDevice.CreateShaderResourceView(texture.Resource, null,
                srvHeap.GetCPUDescriptorHandleForHeapStart() + slot * srvSize);
            return new ImTextureID((ulong)(srvHeap.GetGPUDescriptorHandleForHeapStart() + slot * srvSize).Ptr);
        }

        public void NewFrame()
        {
            ImGuiImplD3D12.NewFrame();
            ImGuiImplWin32.NewFrame();
            ImGui.NewFrame();
        }

        /// <summary>Renders the accumulated UI into the command list (call after scene rendering).</summary>
        public void Render(ID3D12GraphicsCommandList4 commandList)
        {
            ImGui.Render();
            commandList.SetDescriptorHeaps(srvHeap);
            ImGuiImplD3D12.RenderDrawData(ImGui.GetDrawData(),
                new ID3D12GraphicsCommandListPtr((Hexa.NET.ImGui.Backends.D3D12.ID3D12GraphicsCommandList*)commandList.NativePointer));
        }

        public void Dispose()
        {
            // Unhook before shutting the backend down: DestroyWindow later
            // pumps messages through the hook, which must not reach the
            // destroyed ImGui context.
            window.MessageHook = null;
            device.WaitIdle();
            ImGuiImplD3D12.Shutdown();
            ImGuiImplWin32.Shutdown();
            ImGui.DestroyContext(context);
            if (srvHeap != null) srvHeap.Dispose();
        }
    }
}
