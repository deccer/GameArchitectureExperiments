using System;
using System.Net;

namespace GameDemo
{
    public interface IGameClient : IDisposable
    {
        void Cleanup();

        void Connect(IPEndPoint endPoint);

        void ConnectLocalhost();

        void Disconnect();

        void Initialize();

        void Run();
    }
}