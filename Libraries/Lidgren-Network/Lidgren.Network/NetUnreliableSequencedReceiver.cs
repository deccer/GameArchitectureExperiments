using System;

namespace Lidgren.Network
{
	internal sealed class NetUnreliableSequencedReceiver : NetReceiverChannelBase
	{
		private int _lastReceivedSequenceNumber = -1;

		public NetUnreliableSequencedReceiver(NetConnection connection)
			: base(connection)
		{
		}

		internal override void ReceiveMessage(NetIncomingMessage msg)
		{
			int nr = msg.m_sequenceNumber;

			// ack no matter what
			m_connection.QueueAck(msg.m_receivedMessageType, nr);

			int relate = NetUtility.RelativeSequenceNumber(nr, _lastReceivedSequenceNumber + 1);
			if (relate < 0)
			{
				m_connection.m_statistics.MessageDropped();
				m_peer.LogVerbose("Received message #" + nr + " DROPPING DUPLICATE");
				return; // drop if late
			}

			_lastReceivedSequenceNumber = nr;
			m_peer.ReleaseMessage(msg);
		}
	}
}
