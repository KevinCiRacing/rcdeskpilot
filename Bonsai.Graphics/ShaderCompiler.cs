using System;
using Vortice.Dxc;

namespace Bonsai.Graphics
{
    /// <summary>Compiles HLSL source to SM6 DXIL bytecode via DXC.</summary>
    public static class ShaderCompiler
    {
        public static byte[] Compile(string source, string entryPoint, DxcShaderStage stage, string debugName)
        {
            using (IDxcResult result = DxcCompiler.Compile(stage, source, entryPoint,
                new DxcCompilerOptions { ShaderModel = DxcShaderModel.Model6_0 }, fileName: debugName))
            {
                if (result.GetStatus().Failure)
                    throw new InvalidOperationException(string.Format(
                        "Shader '{0}' ({1}) failed to compile: {2}", debugName, entryPoint, result.GetErrors()));
                return result.GetObjectBytecodeArray();
            }
        }
    }
}
