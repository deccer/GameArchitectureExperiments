using System;
using OpenTK;
using OpenTK.Graphics.OpenGL4;

namespace GameDemo.Graphics
{
    public class Shader : IDisposable
    {
        private readonly int _programHandle;

        public void Dispose()
        {
            GL.DeleteProgram(_programHandle);
        }

        public Shader(int programHandle)
        {
            _programHandle = programHandle;
        }

        public int GetUniformLocation(string locationName)
        {
            return GL.GetUniformLocation(_programHandle, locationName);
        }

        public void SetValue(int location, Vector3 value)
        {
            GL.Uniform3(location, value);
        }

        public void SetValue(int location, Matrix4 value)
        {
            GL.UniformMatrix4(location, false, ref value);
        }

        public void Use()
        {
            GL.UseProgram(_programHandle);
            //GL.BindFragDataLocation(_programHandle, 0, "o_frag_color");
        }
    }
}