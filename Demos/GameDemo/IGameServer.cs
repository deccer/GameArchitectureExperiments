using System;

namespace GameDemo
{
    public interface IGameServer : IDisposable
    {
        void Shutdown();

        void Start();
    }
}