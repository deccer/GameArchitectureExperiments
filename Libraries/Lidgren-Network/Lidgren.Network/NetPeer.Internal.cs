﻿using System;
using System.Net;
using System.Threading;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Collections.Generic;

#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network
{
    public partial class NetPeer
    {
        private NetPeerStatus _status;
        private Thread _networkThread;
        private Socket _socket;
        internal byte[] m_sendBuffer;
        internal byte[] m_receiveBuffer;
        internal NetIncomingMessage m_readHelperMessage;
        private EndPoint _senderRemote;
        private readonly object _initializeLock = new object();
        private uint _frameCounter;
        private double _lastHeartbeat;
        private double _lastSocketBind = float.MinValue;
        private NetUPnP _upnp;
        internal bool m_needFlushSendQueue;

        internal readonly NetPeerConfiguration m_configuration;
        private readonly NetQueue<NetIncomingMessage> _releasedIncomingMessages;
        internal readonly NetQueue<NetTuple<NetEndPoint, NetOutgoingMessage>> m_unsentUnconnectedMessages;

        internal Dictionary<NetEndPoint, NetConnection> m_handshakes;

        internal readonly NetPeerStatistics m_statistics;
        internal long m_uniqueIdentifier;
        internal bool m_executeFlushSendQueue;

        private AutoResetEvent _messageReceivedEvent;
        private List<NetTuple<SynchronizationContext, SendOrPostCallback>> _receiveCallbacks;

        /// <summary>
        /// Gets the socket, if Start() has been called
        /// </summary>
        public Socket Socket { get { return _socket; } }

        /// <summary>
        /// Call this to register a callback for when a new message arrives
        /// </summary>
        public void RegisterReceivedCallback(SendOrPostCallback callback, SynchronizationContext syncContext = null)
        {
            if (syncContext == null)
                syncContext = SynchronizationContext.Current;
            if (syncContext == null)
                throw new NetException("Need a SynchronizationContext to register callback on correct thread!");
            (_receiveCallbacks ?? (_receiveCallbacks = new List<NetTuple<SynchronizationContext, SendOrPostCallback>>())).Add(new NetTuple<SynchronizationContext, SendOrPostCallback>(syncContext, callback));
        }

        /// <summary>
        /// Call this to unregister a callback, but remember to do it in the same synchronization context!
        /// </summary>
        public void UnregisterReceivedCallback(SendOrPostCallback callback)
        {
            if (_receiveCallbacks == null)
                return;

            // remove all callbacks regardless of sync context
            _receiveCallbacks.RemoveAll(tuple => tuple.Item2.Equals(callback));

            if (_receiveCallbacks.Count < 1)
                _receiveCallbacks = null;
        }

        internal void ReleaseMessage(NetIncomingMessage msg)
        {
            NetException.Assert(msg.m_incomingMessageType != NetIncomingMessageType.Error);

            if (msg.m_isFragment)
            {
                HandleReleasedFragment(msg);
                return;
            }

            _releasedIncomingMessages.Enqueue(msg);

            _messageReceivedEvent?.Set();

            if (_receiveCallbacks != null)
            {
                foreach (var tuple in _receiveCallbacks)
                {
                    try
                    {
                        tuple.Item1.Post(tuple.Item2, this);
                    }
                    catch (Exception ex)
                    {
                        LogWarning("Receive callback exception:" + ex);
                    }
                }
            }
        }

        private void BindSocket(bool reBind)
        {
            double now = NetTime.Now;
            if (now - _lastSocketBind < 1.0)
            {
                LogDebug("Suppressed socket rebind; last bound " + (now - _lastSocketBind) + " seconds ago");
                return; // only allow rebind once every second
            }
            _lastSocketBind = now;

            using (var mutex = new Mutex(false, "Global\\lidgrenSocketBind"))
            {
                try
                {
                    mutex.WaitOne();

                    if (_socket == null)
                        _socket = new Socket(m_configuration.LocalAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

                    if (reBind)
                        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, (int)1);

                    _socket.ReceiveBufferSize = m_configuration.ReceiveBufferSize;
                    _socket.SendBufferSize = m_configuration.SendBufferSize;
                    _socket.Blocking = false;

                    if (m_configuration.DualStack && m_configuration.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6)
                        _socket.DualMode = true;

                    var localAddress = m_configuration.DualStack
                        ? m_configuration.LocalAddress.MapToIPv6()
                        : m_configuration.LocalAddress;

                    var ep = (EndPoint)new NetEndPoint(localAddress, reBind ? Port : m_configuration.Port);
                    _socket.Bind(ep);

                    try
                    {
                        const uint IOC_IN = 0x80000000;
                        const uint IOC_VENDOR = 0x18000000;
                        const uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
                        _socket.IOControl(unchecked((int)SIO_UDP_CONNRESET), new byte[] { Convert.ToByte(false) }, null);
                    }
                    catch
                    {
                        // ignore; SIO_UDP_CONNRESET not supported on this platform
                    }
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }

            var boundEp = _socket.LocalEndPoint as NetEndPoint;
            LogDebug("Socket bound to " + boundEp + ": " + _socket.IsBound);
            Port = boundEp.Port;
        }

        private void InitializeNetwork()
        {
            lock (_initializeLock)
            {
                m_configuration.Lock();

                if (_status == NetPeerStatus.Running)
                    return;

                if (m_configuration.m_enableUPnP)
                    _upnp = new NetUPnP(this);

                InitializePools();

                _releasedIncomingMessages.Clear();
                m_unsentUnconnectedMessages.Clear();
                m_handshakes.Clear();

                // bind to socket
                BindSocket(false);

                m_receiveBuffer = new byte[m_configuration.ReceiveBufferSize];
                m_sendBuffer = new byte[m_configuration.SendBufferSize];
                m_readHelperMessage = new NetIncomingMessage(NetIncomingMessageType.Error)
                {
                    m_data = m_receiveBuffer
                };

                byte[] macBytes = NetUtility.GetMacAddressBytes();

                var boundEp = _socket.LocalEndPoint as NetEndPoint;
                byte[] epBytes = BitConverter.GetBytes(boundEp.GetHashCode());
                byte[] combined = new byte[epBytes.Length + macBytes.Length];
                Array.Copy(epBytes, 0, combined, 0, epBytes.Length);
                Array.Copy(macBytes, 0, combined, epBytes.Length, macBytes.Length);
                m_uniqueIdentifier = BitConverter.ToInt64(NetUtility.ComputeSHAHash(combined), 0);

                _status = NetPeerStatus.Running;
            }
        }

        private void NetworkLoop()
        {
            VerifyNetworkThread();

            LogDebug("Network thread started");

            //
            // Network loop
            //
            do
            {
                try
                {
                    Heartbeat();
                }
                catch (Exception ex)
                {
                    LogWarning(ex.ToString());
                }
            } while (_status == NetPeerStatus.Running);

            //
            // perform shutdown
            //
            ExecutePeerShutdown();
        }

        private void ExecutePeerShutdown()
        {
            VerifyNetworkThread();

            LogDebug("Shutting down...");

            // disconnect and make one final heartbeat
            var list = new List<NetConnection>(m_handshakes.Count + m_connections.Count);
            lock (m_connections)
            {
                foreach (var conn in m_connections)
                {
                    if (conn != null)
                    {
                        list.Add(conn);
                    }
                }
            }

            lock (m_handshakes)
            {
                foreach (var hs in m_handshakes.Values)
                {
                    if (hs != null && !list.Contains(hs))
                    {
                        list.Add(hs);
                    }
                }
            }

            // shut down connections
            foreach (NetConnection conn in list)
            {
                conn.Shutdown(_shutdownReason);
            }
            FlushDelayedPackets();

            // one final heartbeat, will send stuff and do disconnect
            Heartbeat();

            NetUtility.Sleep(10);

            lock (_initializeLock)
            {
                try
                {
                    if (_socket != null)
                    {
                        try
                        {
                            _socket.Shutdown(SocketShutdown.Receive);
                        }
                        catch (Exception ex)
                        {
                            LogDebug("Socket.Shutdown exception: " + ex.ToString());
                        }

                        try
                        {
                            _socket.Close(2); // 2 seconds timeout
                        }
                        catch (Exception ex)
                        {
                            LogDebug("Socket.Close exception: " + ex.ToString());
                        }
                    }
                }
                finally
                {
                    _socket = null;
                    _status = NetPeerStatus.NotRunning;
                    LogDebug("Shutdown complete");

                    // wake up any threads waiting for server shutdown
                    _messageReceivedEvent?.Set();
                }

                _lastSocketBind = float.MinValue;
                m_receiveBuffer = null;
                m_sendBuffer = null;
                m_unsentUnconnectedMessages.Clear();
                m_connections.Clear();
                _connectionLookup.Clear();
                m_handshakes.Clear();
            }

            return;
        }

        private void Heartbeat()
        {
            VerifyNetworkThread();

            double now = NetTime.Now;
            double delta = now - _lastHeartbeat;

            int maxCHBpS = 1250 - m_connections.Count;
            if (maxCHBpS < 250)
            {
                maxCHBpS = 250;
            }

            if (delta > (1.0 / (double)maxCHBpS) || delta < 0.0) // max connection heartbeats/second max
            {
                _frameCounter++;
                _lastHeartbeat = now;

                // do handshake heartbeats
                if ((_frameCounter % 3) == 0)
                {
                    foreach (var kvp in m_handshakes)
                    {
                        NetConnection conn = kvp.Value as NetConnection;
#if DEBUG
                        // sanity check
                        if (kvp.Key != kvp.Key)
                            LogWarning("Sanity fail! Connection in handshake list under wrong key!");
#endif
                        conn.UnconnectedHeartbeat(now);
                        if (conn.m_status == NetConnectionStatus.Connected || conn.m_status == NetConnectionStatus.Disconnected)
                        {
#if DEBUG
                            // sanity check
                            if (conn.m_status == NetConnectionStatus.Disconnected && m_handshakes.ContainsKey(conn.RemoteEndPoint))
                            {
                                LogWarning("Sanity fail! Handshakes list contained disconnected connection!");
                                m_handshakes.Remove(conn.RemoteEndPoint);
                            }
#endif
                            break; // collection has been modified
                        }
                    }
                }

#if DEBUG
                SendDelayedPackets();
#endif

                // update m_executeFlushSendQueue
                if (m_configuration.m_autoFlushSendQueue && m_needFlushSendQueue)
                {
                    m_executeFlushSendQueue = true;
                    m_needFlushSendQueue = false; // a race condition to this variable will simply result in a single superfluous call to FlushSendQueue()
                }

                // do connection heartbeats
                lock (m_connections)
                {
                    for (int i = m_connections.Count - 1; i >= 0; i--)
                    {
                        var conn = m_connections[i];
                        conn.Heartbeat(now, _frameCounter);
                        if (conn.m_status == NetConnectionStatus.Disconnected)
                        {
                            //
                            // remove connection
                            //
                            m_connections.RemoveAt(i);
                            _connectionLookup.Remove(conn.RemoteEndPoint);
                        }
                    }
                }
                m_executeFlushSendQueue = false;

                // send unsent unconnected messages
                while (m_unsentUnconnectedMessages.TryDequeue(out NetTuple<NetEndPoint, NetOutgoingMessage> unsent))
                {
                    NetOutgoingMessage om = unsent.Item2;

                    int len = om.Encode(m_sendBuffer, 0, 0);

                    Interlocked.Decrement(ref om.m_recyclingCount);
                    if (om.m_recyclingCount <= 0)
                        Recycle(om);

                    SendPacket(len, unsent.Item1, 1, out bool connReset);
                }
            }

            _upnp?.CheckForDiscoveryTimeout();

            //
            // read from socket
            //
            if (_socket == null)
                return;

            if (!_socket.Poll(1000, SelectMode.SelectRead)) // wait up to 1 ms for data to arrive
                return;

            //if (m_socket == null || m_socket.Available < 1)
            //	return;

            // update now
            now = NetTime.Now;

            try
            {
                do
                {
                    ReceiveSocketData(now);
                } while (_socket.Available > 0);
            }
            catch (SocketException sx)
            {
                switch (sx.SocketErrorCode)
                {
                    case SocketError.ConnectionReset:
                        // connection reset by peer, aka connection forcibly closed aka "ICMP port unreachable"
                        // we should shut down the connection; but m_senderRemote seemingly cannot be trusted, so which connection should we shut down?!
                        // So, what to do?
                        LogWarning("ConnectionReset");
                        return;

                    case SocketError.NotConnected:
                        // socket is unbound; try to rebind it (happens on mobile when process goes to sleep)
                        BindSocket(true);
                        return;

                    default:
                        LogWarning("Socket exception: " + sx.ToString());
                        return;
                }
            }
        }

        private void ReceiveSocketData(double now)
        {
            int bytesReceived = _socket.ReceiveFrom(m_receiveBuffer, 0, m_receiveBuffer.Length, SocketFlags.None, ref _senderRemote);

            if (bytesReceived < NetConstants.HeaderByteSize)
                return;

            //LogVerbose("Received " + bytesReceived + " bytes");

            var ipsender = (NetEndPoint)_senderRemote;

            if (_upnp != null && now < _upnp.m_discoveryResponseDeadline && bytesReceived > 32)
            {
                // is this an UPnP response?
                string resp = System.Text.Encoding.UTF8.GetString(m_receiveBuffer, 0, bytesReceived);
                if (resp.Contains("upnp:rootdevice") || resp.Contains("UPnP/1.0"))
                {
                    try
                    {
                        resp = resp.Substring(resp.IndexOf("location:", StringComparison.CurrentCultureIgnoreCase) + 9);
                        resp = resp.Substring(0, resp.IndexOf("\r")).Trim();
                        _upnp.ExtractServiceUrl(resp);
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogDebug("Failed to parse UPnP response: " + ex.ToString());

                        // don't try to parse this packet further
                        return;
                    }
                }
            }

            _connectionLookup.TryGetValue(ipsender, out NetConnection sender);

            //
            // parse packet into messages
            //
            int numMessages = 0;
            int numFragments = 0;
            int ptr = 0;
            while ((bytesReceived - ptr) >= NetConstants.HeaderByteSize)
            {
                // decode header
                //  8 bits - NetMessageType
                //  1 bit  - Fragment?
                // 15 bits - Sequence number
                // 16 bits - Payload length in bits

                numMessages++;

                NetMessageType tp = (NetMessageType)m_receiveBuffer[ptr++];

                byte low = m_receiveBuffer[ptr++];
                byte high = m_receiveBuffer[ptr++];

                bool isFragment = ((low & 1) == 1);
                ushort sequenceNumber = (ushort)((low >> 1) | (((int)high) << 7));

                if (isFragment)
                    numFragments++;

                ushort payloadBitLength = (ushort)(m_receiveBuffer[ptr++] | (m_receiveBuffer[ptr++] << 8));
                int payloadByteLength = NetUtility.BytesToHoldBits(payloadBitLength);

                if (bytesReceived - ptr < payloadByteLength)
                {
                    LogWarning("Malformed packet; stated payload length " + payloadByteLength + ", remaining bytes " + (bytesReceived - ptr));
                    return;
                }

                if (tp >= NetMessageType.Unused1 && tp <= NetMessageType.Unused29)
                {
                    ThrowOrLog("Unexpected NetMessageType: " + tp);
                    return;
                }

                try
                {
                    if (tp >= NetMessageType.LibraryError)
                    {
                        if (sender != null)
                            sender.ReceivedLibraryMessage(tp, ptr, payloadByteLength);
                        else
                            ReceivedUnconnectedLibraryMessage(now, ipsender, tp, ptr, payloadByteLength);
                    }
                    else
                    {
                        if (sender == null && !m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.UnconnectedData))
                            return; // dropping unconnected message since it's not enabled

                        NetIncomingMessage msg = CreateIncomingMessage(NetIncomingMessageType.Data, payloadByteLength);
                        msg.m_isFragment = isFragment;
                        msg.m_receiveTime = now;
                        msg.m_sequenceNumber = sequenceNumber;
                        msg.m_receivedMessageType = tp;
                        msg.m_senderConnection = sender;
                        msg.m_senderEndPoint = ipsender;
                        msg.m_bitLength = payloadBitLength;

                        Buffer.BlockCopy(m_receiveBuffer, ptr, msg.m_data, 0, payloadByteLength);
                        if (sender != null)
                        {
                            if (tp == NetMessageType.Unconnected)
                            {
                                // We're connected; but we can still send unconnected messages to this peer
                                msg.m_incomingMessageType = NetIncomingMessageType.UnconnectedData;
                                ReleaseMessage(msg);
                            }
                            else
                            {
                                // connected application (non-library) message
                                sender.ReceivedMessage(msg);
                            }
                        }
                        else
                        {
                            // at this point we know the message type is enabled
                            // unconnected application (non-library) message
                            msg.m_incomingMessageType = NetIncomingMessageType.UnconnectedData;
                            ReleaseMessage(msg);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogError("Packet parsing error: " + ex.Message + " from " + ipsender);
                }
                ptr += payloadByteLength;
            }

            m_statistics.PacketReceived(bytesReceived, numMessages, numFragments);
            if (sender != null)
                sender.m_statistics.PacketReceived(bytesReceived, numMessages, numFragments);
        }

        /// <summary>
		/// If NetPeerConfiguration.AutoFlushSendQueue() is false; you need to call this to send all messages queued using SendMessage()
		/// </summary>
		public void FlushSendQueue()
        {
            m_executeFlushSendQueue = true;
        }

        internal void HandleIncomingDiscoveryRequest(double now, NetEndPoint senderEndPoint, int ptr, int payloadByteLength)
        {
            if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.DiscoveryRequest))
            {
                NetIncomingMessage dm = CreateIncomingMessage(NetIncomingMessageType.DiscoveryRequest, payloadByteLength);
                if (payloadByteLength > 0)
                    Buffer.BlockCopy(m_receiveBuffer, ptr, dm.m_data, 0, payloadByteLength);
                dm.m_receiveTime = now;
                dm.m_bitLength = payloadByteLength * 8;
                dm.m_senderEndPoint = senderEndPoint;
                ReleaseMessage(dm);
            }
        }

        internal void HandleIncomingDiscoveryResponse(double now, NetEndPoint senderEndPoint, int ptr, int payloadByteLength)
        {
            if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.DiscoveryResponse))
            {
                NetIncomingMessage dr = CreateIncomingMessage(NetIncomingMessageType.DiscoveryResponse, payloadByteLength);
                if (payloadByteLength > 0)
                    Buffer.BlockCopy(m_receiveBuffer, ptr, dr.m_data, 0, payloadByteLength);
                dr.m_receiveTime = now;
                dr.m_bitLength = payloadByteLength * 8;
                dr.m_senderEndPoint = senderEndPoint;
                ReleaseMessage(dr);
            }
        }

        private void ReceivedUnconnectedLibraryMessage(double now, NetEndPoint senderEndPoint, NetMessageType tp, int ptr, int payloadByteLength)
        {
            if (m_handshakes.TryGetValue(senderEndPoint, out NetConnection shake))
            {
                shake.ReceivedHandshake(now, tp, ptr, payloadByteLength);
                return;
            }

            //
            // Library message from a completely unknown sender; lets just accept Connect
            //
            switch (tp)
            {
                case NetMessageType.Discovery:
                    HandleIncomingDiscoveryRequest(now, senderEndPoint, ptr, payloadByteLength);
                    return;
                case NetMessageType.DiscoveryResponse:
                    HandleIncomingDiscoveryResponse(now, senderEndPoint, ptr, payloadByteLength);
                    return;
                case NetMessageType.NatIntroduction:
                    if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
                        HandleNatIntroduction(ptr);
                    return;
                case NetMessageType.NatPunchMessage:
                    if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
                        HandleNatPunch(ptr, senderEndPoint);
                    return;
                case NetMessageType.NatIntroductionConfirmRequest:
                    if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
                        HandleNatPunchConfirmRequest(ptr, senderEndPoint);
                    return;
                case NetMessageType.NatIntroductionConfirmed:
                    if (m_configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
                        HandleNatPunchConfirmed(ptr, senderEndPoint);
                    return;
                case NetMessageType.ConnectResponse:

                    lock (m_handshakes)
                    {
                        foreach (var hs in m_handshakes)
                        {
                            if (hs.Key.Address.Equals(senderEndPoint.Address))
                            {
                                if (hs.Value.m_connectionInitiator)
                                {
                                    //
                                    // We are currently trying to connection to XX.XX.XX.XX:Y
                                    // ... but we just received a ConnectResponse from XX.XX.XX.XX:Z
                                    // Lets just assume the router decided to use this port instead
                                    //
                                    var hsconn = hs.Value;
                                    _connectionLookup.Remove(hs.Key);
                                    m_handshakes.Remove(hs.Key);

                                    LogDebug("Detected host port change; rerouting connection to " + senderEndPoint);
                                    hsconn.MutateEndPoint(senderEndPoint);

                                    _connectionLookup.Add(senderEndPoint, hsconn);
                                    m_handshakes.Add(senderEndPoint, hsconn);

                                    hsconn.ReceivedHandshake(now, tp, ptr, payloadByteLength);
                                    return;
                                }
                            }
                        }
                    }

                    LogWarning("Received unhandled library message " + tp + " from " + senderEndPoint);
                    return;
                case NetMessageType.Connect:
                    if (!m_configuration.AcceptIncomingConnections)
                    {
                        LogWarning("Received Connect, but we're not accepting incoming connections!");
                        return;
                    }
                    // handle connect
                    // It's someone wanting to shake hands with us!

                    int reservedSlots = m_handshakes.Count + m_connections.Count;
                    if (reservedSlots >= m_configuration.m_maximumConnections)
                    {
                        // server full
                        NetOutgoingMessage full = CreateMessage("Server full");
                        full.m_messageType = NetMessageType.Disconnect;
                        SendLibrary(full, senderEndPoint);
                        return;
                    }

                    // Ok, start handshake!
                    NetConnection conn = new NetConnection(this, senderEndPoint)
                    {
                        m_status = NetConnectionStatus.ReceivedInitiation
                    };
                    m_handshakes.Add(senderEndPoint, conn);
                    conn.ReceivedHandshake(now, tp, ptr, payloadByteLength);
                    return;

                case NetMessageType.Disconnect:
                    // this is probably ok
                    LogVerbose("Received Disconnect from unconnected source: " + senderEndPoint);
                    return;
                default:
                    LogWarning("Received unhandled library message " + tp + " from " + senderEndPoint);
                    return;
            }
        }

        internal void AcceptConnection(NetConnection conn)
        {
            // LogDebug("Accepted connection " + conn);
            conn.InitExpandMTU(NetTime.Now);

            if (!m_handshakes.Remove(conn.m_remoteEndPoint))
                LogWarning("AcceptConnection called but m_handshakes did not contain it!");

            lock (m_connections)
            {
                if (m_connections.Contains(conn))
                {
                    LogWarning("AcceptConnection called but m_connection already contains it!");
                }
                else
                {
                    m_connections.Add(conn);
                    _connectionLookup.Add(conn.m_remoteEndPoint, conn);
                }
            }
        }

        [Conditional("DEBUG")]
        internal void VerifyNetworkThread()
        {
            Thread ct = Thread.CurrentThread;
            if (Thread.CurrentThread != _networkThread)
                throw new NetException("Executing on wrong thread! Should be library system thread (is " + ct.Name + " mId " + ct.ManagedThreadId + ")");
        }

        internal NetIncomingMessage SetupReadHelperMessage(int ptr, int payloadLength)
        {
            VerifyNetworkThread();

            m_readHelperMessage.m_bitLength = (ptr + payloadLength) * 8;
            m_readHelperMessage.m_readPosition = (ptr * 8);
            return m_readHelperMessage;
        }
    }
}
