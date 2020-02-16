using System.Drawing;
using OpenTK;
using OpenTK.Graphics;

namespace GameDemo
{
    public class GameWindowFactory : IGameWindowFactory
    {
        private const string GameWindowTitle = "GA Experiments";

        public GameWindow CreateGameWindowFullscreen()
        {
            var primaryDisplayBounds = DisplayDevice.Default.Bounds;
            return CreateGameWindow(primaryDisplayBounds.Width, primaryDisplayBounds.Height, false);
        }

        public GameWindow CreateGameWindowWindowed(int width, int height)
        {
            return CreateGameWindow(width, height, true);
        }

        private GameWindow CreateGameWindow(int width, int height, bool isCentered)
        {
            var primaryDisplayBounds = DisplayDevice.Default.Bounds;

            var gameWindow = new GameWindow(width, height, GraphicsMode.Default, GameWindowTitle, GameWindowFlags.Default, DisplayDevice.Default, 3, 3, GraphicsContextFlags.Debug)
            {
                Location = isCentered
                    ? new Point(primaryDisplayBounds.Width / 2 - width / 2, primaryDisplayBounds.Height / 2 - height / 2)
                    : new Point(0, 0),
                    VSync = VSyncMode.Adaptive
            };
            return gameWindow;
        }
    }
}