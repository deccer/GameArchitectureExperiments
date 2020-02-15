using OpenTK;

namespace GameDemo
{
    public interface IGameWindowFactory
    {
        GameWindow CreateGameWindowFullscreen();

        GameWindow CreateGameWindowWindowed(int width, int height);
    }
}