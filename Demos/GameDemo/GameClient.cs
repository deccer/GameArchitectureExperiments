using System;
using System.Drawing;
using System.Net;
using Lidgren.Network;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Input;
using Serilog;

namespace GameDemo
{
    public interface IGameClient : IDisposable
    {
        void Connect(IPEndPoint endPoint);

        void ConnectLocalhost();

        void Disconnect();
    }

    public class GameClient : IGameClient
    {
        private readonly ILogger _logger;

        private readonly GameWindow _window;

        private KeyboardState _previousKeyboardState;

        private readonly NetClient _netClient;

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

        public GameClient()
        {
            var primaryDisplayBounds = DisplayDevice.Default.Bounds;

            _window = new GameWindow(1280, 720, GraphicsMode.Default, "GameArchitecture Experiments", GameWindowFlags.Default, DisplayDevice.Default, 3, 3, GraphicsContextFlags.Debug);
            _window.Location = new System.Drawing.Point(primaryDisplayBounds.Width / 2 - _window.Width / 2, primaryDisplayBounds.Height / 2 - _window.Height / 2);
            _window.Load += WindowLoad;
            _window.RenderFrame += WindowRenderFrame;
            _window.UpdateFrame += WindowUpdateFrame;

            var peerConfiguration = new NetPeerConfiguration("GAE");
            _netClient = new NetClient(peerConfiguration);
        }

        public void Initialize()
        {
            _previousKeyboardState = Keyboard.GetState();
        }

        public void Run()
        {
            _window.Run(30.0, 60.0);
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
            GL.ClearColor(Color.Teal);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            _window.SwapBuffers();
        }

        private void WindowLoad(object sender, EventArgs e)
        {
            _logger.Information("Loading...");
            _logger.Information("Loading...Done");
        }
    }
}