using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Lidgren.Network
{
    /// <summary>
    /// Interface for an encryption algorithm
    /// </summary>
    public abstract class NetEncryption
    {
        /// <summary>
        /// NetPeer
        /// </summary>
        protected NetPeer m_peer;

        /// <summary>
        /// Constructor
        /// </summary>
        protected NetEncryption(NetPeer peer)
        {
            m_peer = peer ?? throw new NetException("Peer must not be null");
        }

        public void SetKey(string str)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(str);
            SetKey(bytes, 0, bytes.Length);
        }

        public abstract void SetKey(byte[] data, int offset, int count);

        /// <summary>
        /// Encrypt an outgoing message in place
        /// </summary>
        public abstract bool Encrypt(NetOutgoingMessage msg);

        /// <summary>
        /// Decrypt an incoming message in place
        /// </summary>
        public abstract bool Decrypt(NetIncomingMessage msg);
    }
}
