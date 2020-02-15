using Lidgren.Network;

namespace GameDemo
{
    public class GameServer : IGameServer
    {
        private readonly NetServer _netServer;

        public void Dispose()
        {
            Shutdown();
        }

        public GameServer()
        {
            var netPeerConfiguration = new NetPeerConfiguration("GAE");

            _netServer = new NetServer(netPeerConfiguration);

        }

        public void Shutdown()
        {
            if (_netServer.Status != NetPeerStatus.ShutdownRequested)
            {
                _netServer.Shutdown(string.Empty);
            }
        }

        public void Start()
        {
            _netServer.Start();
        }
    }
}