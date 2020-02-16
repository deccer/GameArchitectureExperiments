namespace GameDemo.Graphics
{
    public interface IShaderFactory
    {
        Shader CreateShader(string vertexShaderFileName, string fragmentShaderFileName);
    }
}