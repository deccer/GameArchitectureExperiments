using System;

namespace Lidgren.Network
{
	internal sealed class NetUnreliableUnorderedReceiver : NetReceiverChannelBase
	{
		private readonly bool _doFlowControl;

		public NetUnreliableUnorderedReceiver(NetConnection connection)
			: base(connection)
		{
			_doFlowControl = connection.Peer.Configuration.SuppressUnreliableUnorderedAcks == false;
		}

		internal override void ReceiveMessage(NetIncomingMessage msg)
		{
			if (_doFlowControl)
				m_connection.QueueAck(msg.m_receivedMessageType, msg.m_sequenceNumber);

			m_peer.ReleaseMessage(msg);
		}
	}
}
