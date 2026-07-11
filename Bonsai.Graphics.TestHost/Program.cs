using System;
using System.Runtime.InteropServices;
using Bonsai.Graphics;
using Bonsai.Graphics.Win32;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Bonsai.Graphics.TestHost
{
    /// <summary>
    /// DX12 bootstrap host: opens a raw Win32 window and renders a colored
    /// triangle over an animated clear color, vsynced, with resize and
    /// borderless-fullscreen support (F11; ESC quits).
    ///
    /// --selftest: runs a scripted sequence (render, resize, fullscreen,
    /// restore, screenshot, teardown) headless-verifiable: exits 0 when the
    /// debug layer recorded no errors and the screenshot contains the triangle.
    /// --screenshot [path]: saves a PNG of a rendered frame.
    /// </summary>
    internal static class Program
    {
        private const string ShaderSource = @"
struct VSInput  { float3 position : POSITION; float4 color : COLOR; };
struct PSInput  { float4 position : SV_POSITION; float4 color : COLOR; };

PSInput VSMain(VSInput input)
{
    PSInput result;
    result.position = float4(input.position, 1.0);
    result.color = input.color;
    return result;
}

float4 PSMain(PSInput input) : SV_TARGET
{
    return input.color;
}";

        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            public float X, Y, Z;
            public float R, G, B, A;
            public Vertex(float x, float y, float z, float r, float g, float b)
            {
                X = x; Y = y; Z = z; R = r; G = g; B = b; A = 1f;
            }
        }

        private const int VK_ESCAPE = 0x1B;
        private const int VK_F11 = 0x7A;

        [STAThread]
        private static int Main(string[] args)
        {
            // ImGui menu flow demo/selftest (issue 10)
            if (Array.IndexOf(args, "--menu") >= 0 || Array.IndexOf(args, "--menutest") >= 0)
            {
                string menuOut = args.Length > 1 && !args[args.Length - 1].StartsWith("--")
                    ? args[args.Length - 1] : Environment.CurrentDirectory;
                return MenuDemo.Run(FindRepoRoot(), Array.IndexOf(args, "--menutest") >= 0, menuOut);
            }

            // Effect materials demo/selftest (issue 09)
            if (Array.IndexOf(args, "--effects") >= 0 || Array.IndexOf(args, "--effectstest") >= 0)
            {
                string effectsOut = args.Length > 1 && !args[args.Length - 1].StartsWith("--")
                    ? args[args.Length - 1] : Environment.CurrentDirectory;
                return EffectsDemo.Run(FindRepoRoot(), Array.IndexOf(args, "--effectstest") >= 0, effectsOut);
            }

            // World rendering demo/selftest (issue 08)
            if (Array.IndexOf(args, "--scenery") >= 0 || Array.IndexOf(args, "--scenerytest") >= 0 ||
                Array.IndexOf(args, "--photo") >= 0 || Array.IndexOf(args, "--phototest") >= 0)
            {
                bool photoMode = Array.IndexOf(args, "--photo") >= 0 || Array.IndexOf(args, "--phototest") >= 0;
                bool testMode = Array.IndexOf(args, "--scenerytest") >= 0 || Array.IndexOf(args, "--phototest") >= 0;
                string outputDir = args.Length > 1 && !args[args.Length - 1].StartsWith("--")
                    ? args[args.Length - 1] : Environment.CurrentDirectory;
                return SceneryDemo.Run(FindRepoRoot(), photoMode, testMode, outputDir);
            }

            // Scene-graph demo/selftest (issue 07)
            int sceneArg = Array.IndexOf(args, "--scene");
            int sceneTestArg = Array.IndexOf(args, "--scenetest");
            if (sceneArg >= 0 || sceneTestArg >= 0)
            {
                string repoRoot = FindRepoRoot();
                string aircraftDir = System.IO.Path.Combine(repoRoot, "RCSim", "Aircraft", "extra");
                string dataDir = System.IO.Path.Combine(repoRoot, "RCSim", "data");
                string outDir = sceneTestArg >= 0 && sceneTestArg + 1 < args.Length && !args[sceneTestArg + 1].StartsWith("--")
                    ? args[sceneTestArg + 1] : Environment.CurrentDirectory;
                return SceneDemo.Run(aircraftDir, dataDir, sceneTestArg >= 0, outDir);
            }

            bool selfTest = Array.IndexOf(args, "--selftest") >= 0;
            string screenshotPath = null;
            int screenshotArg = Array.IndexOf(args, "--screenshot");
            if (screenshotArg >= 0)
                screenshotPath = screenshotArg + 1 < args.Length ? args[screenshotArg + 1] : "triangle.png";
            if (selfTest && screenshotPath == null)
                screenshotPath = "selftest.png";

            Console.WriteLine("creating window...");
            using (var window = new Win32Window("Bonsai DX12 Bootstrap", 1280, 720))
            using (var device = CreateDevice(window))
            {
                Console.WriteLine("device ready: {0}x{1}", device.Width, device.Height);
                window.Resized += (w, h) => device.Resize(w, h);
                window.KeyDown += key =>
                {
                    if (key == VK_ESCAPE)
                        window.Dispose();
                    else if (key == VK_F11)
                        window.SetFullscreen(!window.IsFullscreen);
                };

                // --- Triangle pipeline ---
                Console.WriteLine("compiling shaders...");
                byte[] vs = ShaderCompiler.Compile(ShaderSource, "VSMain", DxcShaderStage.Vertex, "triangle.hlsl");
                byte[] ps = ShaderCompiler.Compile(ShaderSource, "PSMain", DxcShaderStage.Pixel, "triangle.hlsl");
                Console.WriteLine("shaders compiled ({0}/{1} bytes)", vs.Length, ps.Length);

                using ID3D12RootSignature rootSignature = device.NativeDevice.CreateRootSignature(
                    new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout));

                var psoDesc = new GraphicsPipelineStateDescription
                {
                    RootSignature = rootSignature,
                    VertexShader = vs,
                    PixelShader = ps,
                    InputLayout = new InputLayoutDescription(
                        new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                        new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 12, 0)),
                    RasterizerState = RasterizerDescription.CullNone,
                    BlendState = BlendDescription.Opaque,
                    DepthStencilState = DepthStencilDescription.Default,
                    DepthStencilFormat = GraphicsDevice.DepthFormat,
                    RenderTargetFormats = new[] { GraphicsDevice.BackBufferFormat },
                    PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                    SampleDescription = SampleDescription.Default,
                };
                using ID3D12PipelineState pipeline = device.NativeDevice.CreateGraphicsPipelineState(psoDesc);

                var vertices = new[]
                {
                    new Vertex( 0.0f,  0.6f, 0.5f, 1f, 0f, 0f),
                    new Vertex( 0.5f, -0.6f, 0.5f, 0f, 1f, 0f),
                    new Vertex(-0.5f, -0.6f, 0.5f, 0f, 0f, 1f),
                };
                int vbSize = Marshal.SizeOf<Vertex>() * vertices.Length;
                using ID3D12Resource vertexBuffer = device.NativeDevice.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload), HeapFlags.None,
                    ResourceDescription.Buffer((ulong)vbSize), ResourceStates.GenericRead);
                unsafe
                {
                    void* mapped;
                    vertexBuffer.Map(0, null, &mapped).CheckError();
                    fixed (Vertex* src = vertices)
                        System.Buffer.MemoryCopy(src, mapped, vbSize, vbSize);
                    vertexBuffer.Unmap(0);
                }
                var vbView = new VertexBufferView(vertexBuffer.GPUVirtualAddress, (uint)vbSize, (uint)Marshal.SizeOf<Vertex>());

                // --- Frame loop ---
                int frame = 0;
                int debugErrors = 0;
                bool screenshotOk = false;

                while (window.PumpMessages())
                {
                    if (window.IsMinimized)
                    {
                        System.Threading.Thread.Sleep(50);
                        continue;
                    }

                    // Scripted self-test sequence
                    if (selfTest)
                    {
                        if (frame == 20) window.SetClientSize(1024, 600);
                        if (frame == 40) window.SetFullscreen(true);
                        if (frame == 60) window.SetFullscreen(false);
                        if (frame == 80)
                        {
                            screenshotOk = SaveScreenshot(device, pipeline, rootSignature, vbView, screenshotPath);
                            debugErrors = device.ReportDebugMessages();
                            break;
                        }
                    }
                    else if (screenshotPath != null && frame == 10)
                    {
                        SaveScreenshot(device, pipeline, rootSignature, vbView, screenshotPath);
                        break;
                    }

                    float pulse = 0.15f + 0.1f * (float)Math.Sin(frame * 0.02);
                    var commandList = device.BeginFrame(new Color4(pulse, pulse, 0.25f, 1f));
                    DrawTriangle(commandList, pipeline, rootSignature, vbView);
                    device.EndFrame();
                    frame++;
                    if (frame % 20 == 0)
                        Console.WriteLine("frame {0}", frame);
                }

                device.WaitIdle();

                if (selfTest)
                {
                    Console.WriteLine("frames rendered : {0}", frame);
                    Console.WriteLine("debug errors    : {0}", debugErrors);
                    Console.WriteLine("triangle pixels : {0}", screenshotOk ? "OK" : "MISSING");
                    bool pass = frame >= 80 && debugErrors == 0 && screenshotOk;
                    Console.WriteLine(pass ? "SELFTEST PASS" : "SELFTEST FAIL");
                    return pass ? 0 : 1;
                }
                return 0;
            }
        }

        private static string FindRepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RCDeskPilot.sln")))
                    return dir.FullName;
            throw new InvalidOperationException("Repo root not found from " + AppDomain.CurrentDomain.BaseDirectory);
        }

        private static GraphicsDevice CreateDevice(Win32Window window)
        {
            Console.WriteLine("creating D3D12 device...");
            var device = new GraphicsDevice(window.Handle, window.ClientWidth, window.ClientHeight, enableDebugLayer: true);
            return device;
        }

        private static void DrawTriangle(ID3D12GraphicsCommandList4 commandList,
            ID3D12PipelineState pipeline, ID3D12RootSignature rootSignature, VertexBufferView vbView)
        {
            commandList.SetPipelineState(pipeline);
            commandList.SetGraphicsRootSignature(rootSignature);
            commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            commandList.IASetVertexBuffers(0, vbView);
            commandList.DrawInstanced(3, 1, 0, 0);
        }

        /// <summary>
        /// Renders one frame, copies the back buffer to a readback buffer,
        /// saves it as PNG, and verifies the triangle's three corner colors
        /// are present.
        /// </summary>
        private static bool SaveScreenshot(GraphicsDevice device,
            ID3D12PipelineState pipeline, ID3D12RootSignature rootSignature, VertexBufferView vbView,
            string path)
        {
            int width = device.Width;
            int height = device.Height;
            uint rowPitch = (uint)((width * 4 + 255) & ~255); // 256-byte aligned
            ulong bufferSize = (ulong)(rowPitch * height);

            using ID3D12Resource readback = device.NativeDevice.CreateCommittedResource(
                new HeapProperties(HeapType.Readback), HeapFlags.None,
                ResourceDescription.Buffer(bufferSize), ResourceStates.CopyDest);

            var commandList = device.BeginFrame(new Color4(0.15f, 0.15f, 0.25f, 1f));
            DrawTriangle(commandList, pipeline, rootSignature, vbView);

            // Copy the rendered back buffer out before presenting.
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

            byte[] pixels = new byte[bufferSize];
            unsafe
            {
                void* mapped;
                readback.Map(0, null, &mapped).CheckError();
                Marshal.Copy((IntPtr)mapped, pixels, 0, (int)bufferSize);
                readback.Unmap(0);
            }

            bool sawRed = false, sawGreen = false, sawBlue = false, sawBackground = false;
            using (var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = (int)(y * rowPitch + x * 4);
                        byte r = pixels[i], g = pixels[i + 1], b = pixels[i + 2];
                        bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(255, r, g, b));
                        if (r > 200 && g < 80 && b < 80) sawRed = true;
                        else if (g > 200 && r < 80 && b < 80) sawGreen = true;
                        else if (b > 200 && r < 80 && g < 80) sawBlue = true;
                        else if (r < 80 && g < 80 && b > 40) sawBackground = true;
                    }
                }
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine("screenshot saved: {0}", path);
            return sawRed && sawGreen && sawBlue && sawBackground;
        }
    }
}
