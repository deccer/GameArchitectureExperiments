using System;
using System.Threading;

namespace Lidgren.Network
{
	/// <summary>
	/// Sender part of Selective repeat ARQ for a particular NetChannel
	/// </summary>
	internal sealed class NetReliableSenderChannel : NetSenderChannelBase
	{
		private readonly NetConnection _connection;
		private int _windowStart;
		private int _sendStart;

		private bool _anyStoredResends;

		private readonly NetBitVector _receivedAcks;
		internal NetStoredReliableMessage[] m_storedMessages;

		internal double m_resendDelay;

        internal override int WindowSize { get; }

        internal override bool NeedToSendMessages()
		{
			return base.NeedToSendMessages() || _anyStoredResends;
		}

		internal NetReliableSenderChannel(NetConnection connection, int windowSize)
		{
			_connection = connection;
            WindowSize = windowSize;
			_windowStart = 0;
			_sendStart = 0;
			_anyStoredResends = false;
			_receivedAcks = new NetBitVector(NetConstants.NumSequenceNumbers);
			m_storedMessages = new NetStoredReliableMessage[WindowSize];
			m_queuedSends = new NetQueue<NetOutgoingMessage>(8);
			m_resendDelay = _connection.GetResendDelay();
		}

		internal override int GetAllowedSends()
		{
			int retval = WindowSize - ((_sendStart + NetConstants.NumSequenceNumbers - _windowStart) % NetConstants.NumSequenceNumbers);
			NetException.Assert(retval >= 0 && retval <= WindowSize);
			return retval;
		}

		internal override void Reset()
		{
			_receivedAcks.Clear();
			for (int i = 0; i < m_storedMessages.Length; i++)
				m_storedMessages[i].Reset();
			_anyStoredResends = false;
			m_queuedSends.Clear();
			_windowStart = 0;
			_sendStart = 0;
		}

		internal override NetSendResult Enqueue(NetOutgoingMessage message)
		{
			m_queuedSends.Enqueue(message);
			_connection.m_peer.m_needFlushSendQueue = true; // a race condition to this variable will simply result in a single superflous call to FlushSendQueue()
			if (m_queuedSends.Count <= GetAllowedSends())
				return NetSendResult.Sent;
			return NetSendResult.Queued;
		}

		// call this regularely
		internal override void SendQueuedMessages(double now)
		{
			//
			// resends
			//
			_anyStoredResends = false;
			for (int i = 0; i < m_storedMessages.Length; i++)
			{
				var storedMsg = m_storedMessages[i];
				NetOutgoingMessage om = storedMsg.Message;
				if (om == null)
					continue;

				_anyStoredResends = true;

				double t = storedMsg.LastSent;
				if (t > 0 && (now - t) > m_resendDelay)
				{
					// deduce sequence number
					/*
					int startSlot = m_windowStart % m_windowSize;
					int seqNr = m_windowStart;
					while (startSlot != i)
					{
						startSlot--;
						if (startSlot < 0)
							startSlot = m_windowSize - 1;
						seqNr--;
					}
					*/

					//m_connection.m_peer.LogVerbose("Resending due to delay #" + m_storedMessages[i].SequenceNumber + " " + om.ToString());
					_connection.m_statistics.MessageResent(MessageResendReason.Delay);

					Interlocked.Increment(ref om.m_recyclingCount); // increment this since it's being decremented in QueueSendMessage
					_connection.QueueSendMessage(om, storedMsg.SequenceNumber);

					m_storedMessages[i].LastSent = now;
					m_storedMessages[i].NumSent++;
				}
			}

			int num = GetAllowedSends();
			if (num < 1)
				return;

			// queued sends
			while (num > 0 && m_queuedSends.Count > 0)
			{
                if (m_queuedSends.TryDequeue(out NetOutgoingMessage om))
                    ExecuteSend(now, om);
                num--;
				NetException.Assert(num == GetAllowedSends());
			}
		}

		private void ExecuteSend(double now, NetOutgoingMessage message)
		{
			int seqNr = _sendStart;
			_sendStart = (_sendStart + 1) % NetConstants.NumSequenceNumbers;

			// must increment recycle count here, since it's decremented in QueueSendMessage and we want to keep it for the future in case or resends
			// we will decrement once more in DestoreMessage for final recycling
			Interlocked.Increment(ref message.m_recyclingCount);

			_connection.QueueSendMessage(message, seqNr);

			int storeIndex = seqNr % WindowSize;
			NetException.Assert(m_storedMessages[storeIndex].Message == null);

			m_storedMessages[storeIndex].NumSent++;
			m_storedMessages[storeIndex].Message = message;
			m_storedMessages[storeIndex].LastSent = now;
			m_storedMessages[storeIndex].SequenceNumber = seqNr;
			_anyStoredResends = true;

			return;
		}

		private void DestoreMessage(double now, int storeIndex, out bool resetTimeout)
		{
			// reset timeout if we receive ack within kThreshold of sending it
			const double kThreshold = 2.0;
			var srm = m_storedMessages[storeIndex];
			resetTimeout = (srm.NumSent == 1) && (now - srm.LastSent < kThreshold);

			var storedMessage = srm.Message;

			// on each destore; reduce recyclingcount so that when all instances are destored, the outgoing message can be recycled
			Interlocked.Decrement(ref storedMessage.m_recyclingCount);
#if DEBUG
			if (storedMessage == null)
				throw new NetException("m_storedMessages[" + storeIndex + "].Message is null; sent " + m_storedMessages[storeIndex].NumSent + " times, last time " + (NetTime.Now - m_storedMessages[storeIndex].LastSent) + " seconds ago");
#else
			if (storedMessage != null)
			{
#endif
			if (storedMessage.m_recyclingCount <= 0)
				_connection.m_peer.Recycle(storedMessage);

#if !DEBUG
			}
#endif
			m_storedMessages[storeIndex] = new NetStoredReliableMessage();
		}

		// remoteWindowStart is remote expected sequence number; everything below this has arrived properly
		// seqNr is the actual nr received
		internal override void ReceiveAcknowledge(double now, int seqNr)
		{
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
                DestoreMessage(now, _windowStart % WindowSize, out bool resetTimeout);
				_windowStart = (_windowStart + 1) % NetConstants.NumSequenceNumbers;

				// advance window if we already have early acks
				while (_receivedAcks.Get(_windowStart))
				{
					//m_connection.m_peer.LogDebug("Using early ack for #" + m_windowStart + "...");
					_receivedAcks[_windowStart] = false;
                    DestoreMessage(now, _windowStart % WindowSize, out bool rt);
                    resetTimeout |= rt;

					NetException.Assert(m_storedMessages[_windowStart % WindowSize].Message == null); // should already be destored
					_windowStart = (_windowStart + 1) % NetConstants.NumSequenceNumbers;
					//m_connection.m_peer.LogDebug("Advancing window to #" + m_windowStart);
				}
				if (resetTimeout)
					_connection.ResetTimeout(now);
				return;
			}

			//
			// early ack... (if it has been sent!)
			//
			// If it has been sent either the m_windowStart message was lost
			// ... or the ack for that message was lost
			//

			//m_connection.m_peer.LogDebug("Received early ack for #" + seqNr);

			int sendRelate = NetUtility.RelativeSequenceNumber(seqNr, _sendStart);
			if (sendRelate <= 0)
			{
				// yes, we've sent this message - it's an early (but valid) ack
				if (_receivedAcks[seqNr])
				{
					// we've already destored/been acked for this message
				}
				else
				{
					_receivedAcks[seqNr] = true;
				}
			}
			else if (sendRelate > 0)
			{
				// uh... we haven't sent this message yet? Weird, dupe or error...
				NetException.Assert(false, "Got ack for message not yet sent?");
				return;
			}

			// Ok, lets resend all missing acks
			int rnr = seqNr;
			do
			{
				rnr--;
				if (rnr < 0)
					rnr = NetConstants.NumSequenceNumbers - 1;

				if (_receivedAcks[rnr])
				{
					// m_connection.m_peer.LogDebug("Not resending #" + rnr + " (since we got ack)");
				}
				else
				{
					int slot = rnr % WindowSize;
					NetException.Assert(m_storedMessages[slot].Message != null);
					if (m_storedMessages[slot].NumSent == 1)
					{
						// just sent once; resend immediately since we found gap in ack sequence
						NetOutgoingMessage rmsg = m_storedMessages[slot].Message;
						//m_connection.m_peer.LogVerbose("Resending #" + rnr + " (" + rmsg + ")");

						if (now - m_storedMessages[slot].LastSent < (m_resendDelay * 0.35))
						{
							// already resent recently
						}
						else
						{
							m_storedMessages[slot].LastSent = now;
							m_storedMessages[slot].NumSent++;
							_connection.m_statistics.MessageResent(MessageResendReason.HoleInSequence);
							Interlocked.Increment(ref rmsg.m_recyclingCount); // increment this since it's being decremented in QueueSendMessage
							_connection.QueueSendMessage(rmsg, rnr);
						}
					}
				}

			} while (rnr != _windowStart);
		}
	}
}
