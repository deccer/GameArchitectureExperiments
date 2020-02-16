using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using GameDemo.Graphics;
using Lidgren.Network;
using OpenTK;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Input;
using Serilog;

namespace GameDemo
{

    public class GameClient : IGameClient
    {
        private readonly ILogger _logger;

        private readonly IShaderFactory _shaderFactory;

        private readonly GameWindow _window;

        private KeyboardState _previousKeyboardState;

        private readonly NetClient _netClient;

        private Shader _simpleProgram;
        private Matrix4 _modelMatrix;
        private Matrix4 _projectionMatrix;
        private Matrix4 _viewMatrix;

        [StructLayout(LayoutKind.Sequential, Pack = 16)]
        struct VertexPositionColor
        {
            public Vector3 Position;
            public Vector3 Color;
        }

        private readonly VertexPositionColor[] _vertices = new VertexPositionColor[]
        {
            new VertexPositionColor{Position = new Vector3(-1.0f, -1.0f, 0.0f), Color = new Vector3(0.0f, 0.0f, 0.9f)},
            new VertexPositionColor{Position = new Vector3(+1.0f, -1.0f, 0.0f), Color = new Vector3(1.0f, 1.0f, 0.0f)},
            new VertexPositionColor{Position = new Vector3(+0.0f, +1.0f, 0.0f), Color = new Vector3(1.0f, 0.1f, 0.1f)}
        };

        private int _vao;
        private int _vbo;

        public void Cleanup()
        {
            GL.DeleteBuffer(_vbo);
            GL.DeleteVertexArray(_vao);
        }

        public void Connect(IPEndPoint endPoint)
        {
            _netClient.Connect(endPoint, null);
        }

        public void ConnectLocalhost()
        {
            _netClient.Connect(new IPEndPoint(IPAddress.Loopback, 43434));
        }

        public void Disconnect()
        {
            _netClient.Disconnect(string.Empty);
        }

        public void Dispose()
        {
            _window.Dispose();
        }

        public GameClient(ILogger logger, IGameWindowFactory gameWindowFactory, IShaderFactory shaderFactory)
        {
            _logger = logger;
            _shaderFactory = shaderFactory;

            _logger.Debug("Creating Window...");
            _window = gameWindowFactory.CreateGameWindowWindowed(1280, 720);
            _window.Load += WindowLoad;
            _window.RenderFrame += WindowRenderFrame;
            _window.UpdateFrame += WindowUpdateFrame;
            _window.Resize += WindowResize;
            _logger.Debug("Creating Window...Done");

            var peerConfiguration = new NetPeerConfiguration("GAE");
            _netClient = new NetClient(peerConfiguration);
        }

        public unsafe void Initialize()
        {
            _previousKeyboardState = Keyboard.GetState();

            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);
            GL.FrontFace(FrontFaceDirection.Ccw);

            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);

            _simpleProgram = _shaderFactory.CreateShader(
                "Assets\\Shaders\\_Simple.vs.glsl",
                "Assets\\Shaders\\_Simple.ps.glsl");
            if (_simpleProgram == null)
            {
                throw new Exception();
            }
            _modelMatrix = Matrix4.CreateTranslation(0, 0, 0);
            _projectionMatrix = Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver4, 1920 / 1080f, 0.01f, 128.0f);
            _viewMatrix = Matrix4.LookAt(new Vector3(0, 0, 5), new Vector3(0, 0, 0), Vector3.UnitY);

            _vbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, _vertices.Length * sizeof(VertexPositionColor), _vertices, BufferUsageHint.StaticDraw);

            _vao = GL.GenVertexArray();
            GL.BindVertexArray(_vao);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, sizeof(VertexPositionColor), 0);

            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, sizeof(VertexPositionColor), sizeof(Vector3));

        }

        public void Run()
        {
            _window.Run(30.0, 144.0);
        }

        private void WindowUpdateFrame(object sender, FrameEventArgs e)
        {
            var currentKeyboardState = Keyboard.GetState();
            if (currentKeyboardState.IsKeyDown(Key.Escape))
            {
                _window.Close();
            }

            _previousKeyboardState = currentKeyboardState;
        }

        private void WindowRenderFrame(object sender, FrameEventArgs e)
        {
            GL.ClearColor(Color.FromArgb(255, 0, 0, 250));
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            _simpleProgram.Use();
            _simpleProgram.SetValue(0, _modelMatrix);
            _simpleProgram.SetValue(1, _viewMatrix);
            _simpleProgram.SetValue(2, _projectionMatrix);

            GL.BindVertexArray(_vao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            _window.SwapBuffers();
        }

        private void WindowLoad(object sender, EventArgs e)
        {
            _logger.Information("Loading...");
            _logger.Information("Loading...Done");
        }

        private void WindowResize(object sender, EventArgs e)
        {
            GL.Viewport(0, 0, _window.ClientSize.Width, _window.ClientSize.Height);
        }
    }
}