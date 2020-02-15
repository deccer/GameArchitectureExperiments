using System;
using System.Threading;
using System.Collections.Generic;
using System.Net;

#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network
{
	/// <summary>
	/// Represents a local peer capable of holding zero, one or more connections to remote peers
	/// </summary>
	public partial class NetPeer
	{
		private static int _initializedPeersCount;
        private readonly object _messageReceivedEventCreationLock = new object();

		internal readonly List<NetConnection> m_connections;
		private readonly Dictionary<NetEndPoint, NetConnection> _connectionLookup;

		private string _shutdownReason;

		/// <summary>
		/// Gets the NetPeerStatus of the NetPeer
		/// </summary>
		public NetPeerStatus Status { get { return _status; } }

		/// <summary>
		/// Signalling event which can be waited on to determine when a message is queued for reading.
		/// Note that there is no guarantee that after the event is signaled the blocked thread will
		/// find the message in the queue. Other user created threads could be preempted and dequeue
		/// the message before the waiting thread wakes up.
		/// </summary>
		public AutoResetEvent MessageReceivedEvent
		{
			get
			{
				if (_messageReceivedEvent == null)
				{
					lock (_messageReceivedEventCreationLock) // make sure we don't create more than one event object
					{
						if (_messageReceivedEvent == null)
							_messageReceivedEvent = new AutoResetEvent(false);
					}
				}
				return _messageReceivedEvent;
			}
		}

		/// <summary>
		/// Gets a unique identifier for this NetPeer based on Mac address and ip/port. Note! Not available until Start() has been called!
		/// </summary>
		public long UniqueIdentifier { get { return m_uniqueIdentifier; } }

        /// <summary>
        /// Gets the port number this NetPeer is listening and sending on, if Start() has been called
        /// </summary>
        public int Port { get; private set; }

        /// <summary>
        /// Returns an UPnP object if enabled in the NetPeerConfiguration
        /// </summary>
        public NetUPnP UPnP { get { return _upnp; } }

        /// <summary>
        /// Gets or sets the application defined object containing data about the peer
        /// </summary>
        public object Tag { get; set; }

        /// <summary>
        /// Gets a copy of the list of connections
        /// </summary>
        public List<NetConnection> Connections
		{
			get
			{
				lock (m_connections)
					return new List<NetConnection>(m_connections);
			}
		}

		/// <summary>
		/// Gets the number of active connections
		/// </summary>
		public int ConnectionsCount
		{
			get { return m_connections.Count; }
		}

		/// <summary>
		/// Statistics on this NetPeer since it was initialized
		/// </summary>
		public NetPeerStatistics Statistics
		{
			get { return m_statistics; }
		}

		/// <summary>
		/// Gets the configuration used to instanciate this NetPeer
		/// </summary>
		public NetPeerConfiguration Configuration { get { return m_configuration; } }

		/// <summary>
		/// NetPeer constructor
		/// </summary>
		public NetPeer(NetPeerConfiguration config)
		{
			m_configuration = config;
			m_statistics = new NetPeerStatistics(this);
			_releasedIncomingMessages = new NetQueue<NetIncomingMessage>(4);
			m_unsentUnconnectedMessages = new NetQueue<NetTuple<NetEndPoint, NetOutgoingMessage>>(2);
			m_connections = new List<NetConnection>();
			_connectionLookup = new Dictionary<NetEndPoint, NetConnection>();
			m_handshakes = new Dictionary<NetEndPoint, NetConnection>();
            var address = config.DualStack ? IPAddress.IPv6Any : IPAddress.Any;
            _senderRemote = (EndPoint)new NetEndPoint(address, 0);
			_status = NetPeerStatus.NotRunning;
			_receivedFragmentGroups = new Dictionary<NetConnection, Dictionary<int, ReceivedFragmentGroup>>();
		}

		/// <summary>
		/// Binds to socket and spawns the networking thread
		/// </summary>
		public void Start()
		{
			if (_status != NetPeerStatus.NotRunning)
			{
				// already running! Just ignore...
				LogWarning("Start() called on already running NetPeer - ignoring.");
				return;
			}

			_status = NetPeerStatus.Starting;

			// fix network thread name
			if (m_configuration.NetworkThreadName == "Lidgren network thread")
			{
				int pc = Interlocked.Increment(ref _initializedPeersCount);
				m_configuration.NetworkThreadName = "Lidgren network thread " + pc.ToString();
			}

			InitializeNetwork();

            // start network thread
            _networkThread = new Thread(new ThreadStart(NetworkLoop))
            {
                Name = m_configuration.NetworkThreadName,
                IsBackground = true
            };
            _networkThread.Start();

			// send upnp discovery
			_upnp?.Discover(this);

			// allow some time for network thread to start up in case they call Connect() or UPnP calls immediately
			NetUtility.Sleep(50);
		}

		/// <summary>
		/// Get the connection, if any, for a certain remote endpoint
		/// </summary>
		public NetConnection GetConnection(NetEndPoint ep)
		{
            // this should not pose a threading problem, m_connectionLookup is never added to concurrently
            // and TryGetValue will not throw an exception on fail, only yield null, which is acceptable
            _connectionLookup.TryGetValue(ep, out NetConnection retval);

            return retval;
		}

		/// <summary>
		/// Read a pending message from any connection, blocking up to maxMillis if needed
		/// </summary>
	        public NetIncomingMessage WaitMessage(int maxMillis)
	        {
	            NetIncomingMessage msg = ReadMessage();

	            while (msg == null)
	            {
	                // This could return true...
	                if (!MessageReceivedEvent.WaitOne(maxMillis))
	                {
	                    return null;
	                }

	                // ... while this will still returns null. That's why we need to cycle.
	                msg = ReadMessage();
	            }

	            return msg;
        	}

		/// <summary>
		/// Read a pending message from any connection, if any
		/// </summary>
		public NetIncomingMessage ReadMessage()
		{
            if (_releasedIncomingMessages.TryDequeue(out NetIncomingMessage retval) && retval.MessageType == NetIncomingMessageType.StatusChanged)
            {
                retval.SenderConnection.m_visibleStatus = (NetConnectionStatus)retval.PeekByte();
            }
            return retval;
		}

        	/// <summary>
	        /// Reads a pending message from any connection, if any.
	        /// Returns true if message was read, otherwise false.
	        /// </summary>
	        /// <returns>True, if message was read.</returns>
	        public bool ReadMessage(out NetIncomingMessage message)
	        {
	            message = ReadMessage();
	            return message != null;
	        }

		/// <summary>
		/// Read a pending message from any connection, if any
		/// </summary>
		public int ReadMessages(IList<NetIncomingMessage> addTo)
		{
			int added = _releasedIncomingMessages.TryDrain(addTo);
			if (added > 0)
			{
				for (int i = 0; i < added; i++)
				{
					var index = addTo.Count - added + i;
					var nim = addTo[index];
					if (nim.MessageType == NetIncomingMessageType.StatusChanged)
					{
						NetConnectionStatus status = (NetConnectionStatus)nim.PeekByte();
						nim.SenderConnection.m_visibleStatus = status;
					}
				}
			}
			return added;
		}

		// send message immediately and recycle it
		internal void SendLibrary(NetOutgoingMessage msg, NetEndPoint recipient)
		{
			VerifyNetworkThread();
			NetException.Assert(!msg.m_isSent);

            int len = msg.Encode(m_sendBuffer, 0, 0);
            SendPacket(len, recipient, 1, out _);

			// no reliability, no multiple recipients - we can just recycle this message immediately
			msg.m_recyclingCount = 0;
			Recycle(msg);
		}

		private static NetEndPoint GetNetEndPoint(string host, int port)
		{
			IPAddress address = NetUtility.Resolve(host);
			if (address == null)
				throw new NetException("Could not resolve host");
			return new NetEndPoint(address, port);
		}

		/// <summary>
		/// Create a connection to a remote endpoint
		/// </summary>
		public NetConnection Connect(string host, int port)
		{
			return Connect(GetNetEndPoint(host, port), null);
		}

		/// <summary>
		/// Create a connection to a remote endpoint
		/// </summary>
		public NetConnection Connect(string host, int port, NetOutgoingMessage hailMessage)
		{
			return Connect(GetNetEndPoint(host, port), hailMessage);
		}

		/// <summary>
		/// Create a connection to a remote endpoint
		/// </summary>
		public NetConnection Connect(NetEndPoint remoteEndPoint)
		{
			return Connect(remoteEndPoint, null);
		}

		/// <summary>
		/// Create a connection to a remote endpoint
		/// </summary>
		public virtual NetConnection Connect(NetEndPoint remoteEndPoint, NetOutgoingMessage hailMessage)
		{
			if (remoteEndPoint == null)
				throw new ArgumentNullException(nameof(remoteEndPoint));
            if(m_configuration.DualStack)
                remoteEndPoint = NetUtility.MapToIPv6(remoteEndPoint);

			lock (m_connections)
			{
				if (_status == NetPeerStatus.NotRunning)
					throw new NetException("Must call Start() first");

				if (_connectionLookup.ContainsKey(remoteEndPoint))
					throw new NetException("Already connected to that endpoint!");

                if (m_handshakes.TryGetValue(remoteEndPoint, out NetConnection hs))
                {
                    // already trying to connect to that endpoint; make another try
                    switch (hs.m_status)
                    {
                        case NetConnectionStatus.InitiatedConnect:
                            // send another connect
                            hs.m_connectRequested = true;
                            break;
                        case NetConnectionStatus.RespondedConnect:
                            // send another response
                            hs.SendConnectResponse(NetTime.Now, false);
                            break;
                        default:
                            // weird
                            LogWarning("Weird situation; Connect() already in progress to remote endpoint; but hs status is " + hs.m_status);
                            break;
                    }
                    return hs;
                }

                NetConnection conn = new NetConnection(this, remoteEndPoint);
                conn.SetStatus(NetConnectionStatus.InitiatedConnect, "user called connect");
				conn.m_localHailMessage = hailMessage;

				// handle on network thread
				conn.m_connectRequested = true;
				conn.m_connectionInitiator = true;

				m_handshakes.Add(remoteEndPoint, conn);

				return conn;
			}
		}

		/// <summary>
		/// Send raw bytes; only used for debugging
		/// </summary>
		public void RawSend(byte[] arr, int offset, int length, NetEndPoint destination)
		{
			// wrong thread - this miiiight crash with network thread... but what's a boy to do.
			Array.Copy(arr, offset, m_sendBuffer, 0, length);
            SendPacket(length, destination, 1, out _);
        }

		/// <summary>
		/// In DEBUG, throws an exception, in RELEASE logs an error message
		/// </summary>
		/// <param name="message"></param>
		internal void ThrowOrLog(string message)
		{
#if DEBUG
			throw new NetException(message);
#else
			LogError(message);
#endif
		}

		/// <summary>
		/// Disconnects all active connections and closes the socket
		/// </summary>
		public void Shutdown(string bye)
		{
			// called on user thread
			if (_socket == null)
				return; // already shut down

			LogDebug("Shutdown requested");
			_shutdownReason = bye;
			_status = NetPeerStatus.ShutdownRequested;
		}
	}
}
