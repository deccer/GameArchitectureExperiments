using System;
using System.Drawing;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Input;
using Serilog;

namespace GameDemo
{
    public class Game : IGame
    {
        private KeyboardState _previousKeyboardState;
        private readonly ILogger _logger;

        private readonly GameWindow _window;

        public Game(ILogger logger)
        {
            var primaryDisplayBounds = DisplayDevice.Default.Bounds;

            _logger = logger;
            _window = new GameWindow(1280, 720, GraphicsMode.Default, "GameArchitecture Experiments", GameWindowFlags.Default, DisplayDevice.Default, 3, 3, GraphicsContextFlags.Debug);
            _window.Location = new System.Drawing.Point(primaryDisplayBounds.Width / 2 - _window.Width / 2, primaryDisplayBounds.Height / 2 - _window.Height / 2);
            _window.Load += WindowLoad;
            _window.RenderFrame += WindowRenderFrame;
            _window.UpdateFrame += WindowUpdateFrame;
        }

        public void Run(string[] args)
        {
            Initialize();

            _window.Run(30.0, 60.0);

            Cleanup();
        }

        private void Cleanup()
        {
            _logger.Debug("Cleaning up...");
            _logger.Debug("Cleaning up...Done.");
        }

        private void Initialize()
        {
            _logger.Debug("Initializing...");

            _previousKeyboardState = Keyboard.GetState();
            
            _logger.Debug("Initializing...Done.");
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