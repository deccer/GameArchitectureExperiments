namespace Lidgren.Network
{
    public partial class NetConnection
	{
		private enum ExpandMTUStatus
		{
			None,
			InProgress,
			Finished
		}

		private const int c_protocolMaxMTU = (int)((((float)ushort.MaxValue / 8.0f) - 1.0f));

		private ExpandMTUStatus _expandMTUStatus;

		private int _largestSuccessfulMTU;
		private int _smallestFailedMTU;

		private int _lastSentMTUAttemptSize;
		private double _lastSentMTUAttemptTime;
		private int _mtuAttemptFails;

		internal int m_currentMTU;

		/// <summary>
		/// Gets the current MTU in bytes. If PeerConfiguration.AutoExpandMTU is false, this will be PeerConfiguration.MaximumTransmissionUnit.
		/// </summary>
		public int CurrentMTU { get { return m_currentMTU; } }

		internal void InitExpandMTU(double now)
		{
			_lastSentMTUAttemptTime = now + m_peerConfiguration.m_expandMTUFrequency + 1.5f + _averageRoundtripTime; // wait a tiny bit before starting to expand mtu
			_largestSuccessfulMTU = 512;
			_smallestFailedMTU = -1;
			m_currentMTU = m_peerConfiguration.MaximumTransmissionUnit;
		}

		private void MTUExpansionHeartbeat(double now)
		{
			if (_expandMTUStatus == ExpandMTUStatus.Finished)
				return;

			if (_expandMTUStatus == ExpandMTUStatus.None)
			{
				if (m_peerConfiguration.m_autoExpandMTU == false)
				{
					FinalizeMTU(m_currentMTU);
					return;
				}

				// begin expansion
				ExpandMTU(now);
				return;
			}

			if (now > _lastSentMTUAttemptTime + m_peerConfiguration.ExpandMTUFrequency)
			{
				_mtuAttemptFails++;
				if (_mtuAttemptFails == 3)
				{
					FinalizeMTU(m_currentMTU);
					return;
				}

				// timed out; ie. failed
				_smallestFailedMTU = _lastSentMTUAttemptSize;
				ExpandMTU(now);
			}
		}

		private void ExpandMTU(double now)
		{
			int tryMTU;

			// we've nevered encountered failure
			if (_smallestFailedMTU == -1)
			{
				// we've never encountered failure; expand by 25% each time
				tryMTU = (int)((float)m_currentMTU * 1.25f);
				//m_peer.LogDebug("Trying MTU " + tryMTU);
			}
			else
			{
				// we HAVE encountered failure; so try in between
				tryMTU = (int)(((float)_smallestFailedMTU + (float)_largestSuccessfulMTU) / 2.0f);
				//m_peer.LogDebug("Trying MTU " + m_smallestFailedMTU + " <-> " + m_largestSuccessfulMTU + " = " + tryMTU);
			}

			if (tryMTU > c_protocolMaxMTU)
				tryMTU = c_protocolMaxMTU;

			if (tryMTU == _largestSuccessfulMTU)
			{
				//m_peer.LogDebug("Found optimal MTU - exiting");
				FinalizeMTU(_largestSuccessfulMTU);
				return;
			}

			SendExpandMTU(now, tryMTU);
		}

		private void SendExpandMTU(double now, int size)
		{
			NetOutgoingMessage om = m_peer.CreateMessage(size);
			byte[] tmp = new byte[size];
			om.Write(tmp);
			om.m_messageType = NetMessageType.ExpandMTURequest;
			int len = om.Encode(m_peer.m_sendBuffer, 0, 0);

			bool ok = m_peer.SendMTUPacket(len, m_remoteEndPoint);
			if (ok == false)
			{
				//m_peer.LogDebug("Send MTU failed for size " + size);

				// failure
				if (_smallestFailedMTU == -1 || size < _smallestFailedMTU)
				{
					_smallestFailedMTU = size;
					_mtuAttemptFails++;
					if (_mtuAttemptFails >= m_peerConfiguration.ExpandMTUFailAttempts)
					{
						FinalizeMTU(_largestSuccessfulMTU);
						return;
					}
				}
				ExpandMTU(now);
				return;
			}

			_lastSentMTUAttemptSize = size;
			_lastSentMTUAttemptTime = now;

			m_statistics.PacketSent(len, 1);
			m_peer.Recycle(om);
		}

		private void FinalizeMTU(int size)
		{
			if (_expandMTUStatus == ExpandMTUStatus.Finished)
				return;
			_expandMTUStatus = ExpandMTUStatus.Finished;
			m_currentMTU = size;
			if (m_currentMTU != m_peerConfiguration.m_maximumTransmissionUnit)
				m_peer.LogDebug("Expanded Maximum Transmission Unit to: " + m_currentMTU + " bytes");
			return;
		}

		private void SendMTUSuccess(int size)
		{
			NetOutgoingMessage om = m_peer.CreateMessage(4);
			om.Write(size);
			om.m_messageType = NetMessageType.ExpandMTUSuccess;
			int len = om.Encode(m_peer.m_sendBuffer, 0, 0);
            m_peer.SendPacket(len, m_remoteEndPoint, 1, out _);
            m_peer.Recycle(om);

			//m_peer.LogDebug("Received MTU expand request for " + size + " bytes");

			m_statistics.PacketSent(len, 1);
		}

		private void HandleExpandMTUSuccess(double now, int size)
		{
			if (size > _largestSuccessfulMTU)
				_largestSuccessfulMTU = size;

			if (size < m_currentMTU)
			{
				//m_peer.LogDebug("Received low MTU expand success (size " + size + "); current mtu is " + m_currentMTU);
				return;
			}

			//m_peer.LogDebug("Expanding MTU to " + size);
			m_currentMTU = size;

			ExpandMTU(now);
		}
	}
}
