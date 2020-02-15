using System;

namespace Lidgren.Network
{
	internal sealed class NetReliableOrderedReceiver : NetReceiverChannelBase
	{
		private int _windowStart;
		private readonly int _windowSize;
		private readonly NetBitVector _earlyReceived;
		internal NetIncomingMessage[] m_withheldMessages;

		public NetReliableOrderedReceiver(NetConnection connection, int windowSize)
			: base(connection)
		{
			_windowSize = windowSize;
			m_withheldMessages = new NetIncomingMessage[windowSize];
			_earlyReceived = new NetBitVector(windowSize);
		}

		private void AdvanceWindow()
		{
			_earlyReceived.Set(_windowStart % _windowSize, false);
			_windowStart = (_windowStart + 1) % NetConstants.NumSequenceNumbers;
		}

		internal override void ReceiveMessage(NetIncomingMessage message)
		{
			int relate = NetUtility.RelativeSequenceNumber(message.m_sequenceNumber, _windowStart);

			// ack no matter what
			m_connection.QueueAck(message.m_receivedMessageType, message.m_sequenceNumber);

			if (relate == 0)
			{
				// Log("Received message #" + message.SequenceNumber + " right on time");

				//
				// excellent, right on time
				//
				//m_peer.LogVerbose("Received RIGHT-ON-TIME " + message);

				AdvanceWindow();
				m_peer.ReleaseMessage(message);

				// release withheld messages
				int nextSeqNr = (message.m_sequenceNumber + 1) % NetConstants.NumSequenceNumbers;

				while (_earlyReceived[nextSeqNr % _windowSize])
				{
					message = m_withheldMessages[nextSeqNr % _windowSize];
					NetException.Assert(message != null);

					// remove it from withheld messages
					m_withheldMessages[nextSeqNr % _windowSize] = null;

					m_peer.LogVerbose("Releasing withheld message #" + message);

					m_peer.ReleaseMessage(message);

					AdvanceWindow();
					nextSeqNr++;
				}

				return;
			}

			if (relate < 0)
			{
				// duplicate
				m_connection.m_statistics.MessageDropped();
				m_peer.LogVerbose("Received message #" + message.m_sequenceNumber + " DROPPING DUPLICATE");
				return;
			}

			// relate > 0 = early message
			if (relate > _windowSize)
			{
				// too early message!
				m_connection.m_statistics.MessageDropped();
				m_peer.LogDebug("Received " + message + " TOO EARLY! Expected " + _windowStart);
				return;
			}

			_earlyReceived.Set(message.m_sequenceNumber % _windowSize, true);
			m_peer.LogVerbose("Received " + message + " WITHHOLDING, waiting for " + _windowStart);
			m_withheldMessages[message.m_sequenceNumber % _windowSize] = message;
		}
	}
}
