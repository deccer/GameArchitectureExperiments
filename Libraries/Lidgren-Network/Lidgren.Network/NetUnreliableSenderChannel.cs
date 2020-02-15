using System;
using System.Threading;

namespace Lidgren.Network
{
	/// <summary>
	/// Sender part of Selective repeat ARQ for a particular NetChannel
	/// </summary>
	internal sealed class NetUnreliableSenderChannel : NetSenderChannelBase
	{
		private readonly NetConnection _connection;
		private int _windowStart;
		private readonly int _windowSize;
		private int _sendStart;
		private readonly bool _doFlowControl;

		private readonly NetBitVector _receivedAcks;

		internal override int WindowSize { get { return _windowSize; } }

		internal NetUnreliableSenderChannel(NetConnection connection, int windowSize, NetDeliveryMethod method)
		{
			_connection = connection;
			_windowSize = windowSize;
			_windowStart = 0;
			_sendStart = 0;
			_receivedAcks = new NetBitVector(NetConstants.NumSequenceNumbers);
			m_queuedSends = new NetQueue<NetOutgoingMessage>(8);

			_doFlowControl = true;
			if (method == NetDeliveryMethod.Unreliable && connection.Peer.Configuration.SuppressUnreliableUnorderedAcks == true)
				_doFlowControl = false;
		}

		internal override int GetAllowedSends()
		{
			if (!_doFlowControl)
				return int.MaxValue; // always allowed to send without flow control!
			int retval = _windowSize - ((_sendStart + NetConstants.NumSequenceNumbers) - _windowStart) % _windowSize;
			NetException.Assert(retval >= 0 && retval <= _windowSize);
			return retval;
		}

		internal override void Reset()
		{
			_receivedAcks.Clear();
			m_queuedSends.Clear();
			_windowStart = 0;
			_sendStart = 0;
		}

		internal override NetSendResult Enqueue(NetOutgoingMessage message)
		{
			int queueLen = m_queuedSends.Count + 1;
			int left = GetAllowedSends();
			if (queueLen > left || (message.LengthBytes > _connection.m_currentMTU && _connection.m_peerConfiguration.UnreliableSizeBehaviour == NetUnreliableSizeBehaviour.DropAboveMTU))
			{
				// drop message
				return NetSendResult.Dropped;
			}

			m_queuedSends.Enqueue(message);
			_connection.m_peer.m_needFlushSendQueue = true; // a race condition to this variable will simply result in a single superflous call to FlushSendQueue()
			return NetSendResult.Sent;
		}

		// call this regularely
		internal override void SendQueuedMessages(double now)
		{
			int num = GetAllowedSends();
			if (num < 1)
				return;

			// queued sends
			while (num > 0 && m_queuedSends.Count > 0)
			{
                if (m_queuedSends.TryDequeue(out NetOutgoingMessage om))
                    ExecuteSend(om);
                num--;
			}
		}

		private void ExecuteSend(NetOutgoingMessage message)
		{
			_connection.m_peer.VerifyNetworkThread();

			int seqNr = _sendStart;
			_sendStart = (_sendStart + 1) % NetConstants.NumSequenceNumbers;

			_connection.QueueSendMessage(message, seqNr);

			if (message.m_recyclingCount <= 0)
				_connection.m_peer.Recycle(message);

			return;
		}

		// remoteWindowStart is remote expected sequence number; everything below this has arrived properly
		// seqNr is the actual nr received
		internal override void ReceiveAcknowledge(double now, int seqNr)
		{
			if (_doFlowControl == false)
			{
				// we have no use for acks on this channel since we don't respect the window anyway
				_connection.m_peer.LogWarning("SuppressUnreliableUnorderedAcks sender/receiver mismatch!");
				return;
			}

			// late (dupe), on time or early ack?
			int relate = NetUtility.RelativeSequenceNumber(seqNr, _windowStart);

			if (relate < 0)
			{
				//m_connection.m_peer.LogDebug("Received late/dupe ack for #" + seqNr);
				return; // late/duplicate ack
			}

			if (relate == 0)
			{
				//m_connection.m_peer.LogDebug("Received right-on-time ack for #" + seqNr);

				// ack arrived right on time
				NetException.Assert(seqNr == _windowStart);

				_receivedAcks[_windowStart] = false;
				_windowStart = (_windowStart + 1) % NetConstants.NumSequenceNumbers;

				return;
			}

			// Advance window to this position
			_receivedAcks[seqNr] = true;

			while (_windowStart != seqNr)
			{
				_receivedAcks[_windowStart] = false;
				_windowStart = (_windowStart + 1) % NetConstants.NumSequenceNumbers;
			}
		}
	}
}
