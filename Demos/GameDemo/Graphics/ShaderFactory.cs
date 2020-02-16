using System;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using Serilog;

namespace GameDemo.Graphics
{
    public class ShaderFactory : IShaderFactory
    {
        private readonly ILogger _logger;

        public ShaderFactory(ILogger logger)
        {
            _logger = logger;
        }

        public Shader CreateShader(string vertexShaderFileName, string fragmentShaderFileName)
        {
            var (vertexShaderOk, vertexShader) = CreateShader(ShaderType.VertexShader, vertexShaderFileName);
            if (!vertexShaderOk)
            {
                return null;
            }

            var (fragmentShaderOk, fragmentShader) = CreateShader(ShaderType.FragmentShader, fragmentShaderFileName);
            if (!fragmentShaderOk)
            {
                return null;
            }

            var programHandle = GL.CreateProgram();
            GL.AttachShader(programHandle, vertexShader);
            GL.AttachShader(programHandle, fragmentShader);

            GL.LinkProgram(programHandle);
            GL.GetProgram(programHandle, GetProgramParameterName.LinkStatus, out var linkStatus);
            if (linkStatus == 0)
            {
                var linkErrorMessage = GL.GetProgramInfoLog(programHandle);
                _logger.Error("Shader - Unable to link program\n{linkErrorMessage}", linkErrorMessage);
                return null;
            }

            GL.DetachShader(programHandle, vertexShader);
            GL.DetachShader(programHandle, fragmentShader);

            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);

            return new Shader(programHandle);
        }

        private (bool, int) CreateShader(ShaderType shaderType, string shaderFileName)
        {
            var fileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, shaderFileName);

            _logger.Debug("Shader - {shaderFileName}", shaderFileName);

            if (!File.Exists(fileName))
            {
                _logger.Error("Shader - {fileName} does not exist.", fileName);
                return (false, 0);
            }

            var shaderSource = File.ReadAllText(fileName);
            var shader = GL.CreateShader(shaderType);
            GL.ShaderSource(shader, shaderSource);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var compileStatus);
            if (compileStatus == 0)
            {
                GL.GetShaderInfoLog(shader, out var shaderError);
                _logger.Error("Shader - Error during {shaderType} creation\n{shaderError}", shaderType, shaderError);
                return (false, 0);
            }

            return (true, shader);
        }
    }
}