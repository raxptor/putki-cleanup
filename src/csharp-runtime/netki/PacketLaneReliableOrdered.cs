using System;

namespace Netki
{
	public class PacketLaneReliableOrdered
	{
		Bitstream.Buffer[] _sent = new Bitstream.Buffer[256];
		Bitstream.Buffer[] _recv = new Bitstream.Buffer[256];
		DateTime[] _timestamp = new DateTime[256];

		float _resendTime = 0.50f;
		float _ackFlushTimer = 0.0f;
		float[] _sendTimer = new float[256];

		byte[] _ackOut = new byte[256];
		byte _ackOutHead = 0, _ackOutTail = 0;

		byte _sendHead = 0, _sendAckTail = 0;
		byte _recvPos = 0, _recvPending = 0;

		uint _sentTotal = 0, _resentTotal = 0;
		BufferFactory _bufferFactory;

		public PacketLaneReliableOrdered(BufferFactory factory)
		{
			for (int i = 0; i < 256; i++)
			{
				_sent[i] = new Bitstream.Buffer();
				_recv[i] = new Bitstream.Buffer();
			}
			_bufferFactory = factory;
		}

		public void Incoming(Bitstream.Buffer stream, DateTime timestamp)
		{
			// read ack numbers
			while (true)
			{
				uint hasAck = Bitstream.ReadBits(stream, 1);
				if (hasAck == 0 || stream.error != 0)
					break;
				byte idx = (byte)Bitstream.ReadBits(stream, 8);
				if (stream.error != 0)
				{
					Error("Broken packet 3");
					return;
				}
				// free up the slot.
				_sent[idx].buf = null;
			}

			if (stream.BitsLeft() < 8)
				return;

			byte seq = (byte)Bitstream.ReadBits(stream, 8);
			if (stream.error != 0)
			{
				Error("Broken packet 2");
				return;
			}

			// add to ack list.
			if ((byte)(_ackOutHead + 1) == _ackOutTail)
				Error("AckOut is full");
			else
				_ackOut[_ackOutHead++] = seq;

			byte diff = (byte)(seq - _recvPos);
			if (diff > 128)
				// old packet, thorw.
				return;

			if (_recv[seq].buf == null)
				_recvPending++;

			Bitstream.Copy(_recv[seq], stream);
			_timestamp[seq] = timestamp;
		}

		public void Send(Bitstream.Buffer stream)
		{
			if (_sent[_sendHead].buf != null)
				Error("Send queue full!");
			_sendTimer[_sendHead] = 0.0f;
			Bitstream.Copy(_sent[_sendHead++], stream);
		}

		public void WrapOut(Bitstream.Buffer dest, Bitstream.Buffer src, byte seq)
		{
			// Format in all the ack outs.
			while (_ackOutTail != _ackOutHead)
			{
				Bitstream.PutBits(dest, 1, 1);
				Bitstream.PutBits(dest, 8, _ackOut[_ackOutTail]);
				_ackOutTail++;
			}

			Bitstream.PutBits(dest, 1, 0);

			if (src != null)
			{
				uint bytepos = src.bytepos;
				byte bitpos = src.bitpos;
				Bitstream.PutBits(dest, 8, seq);
				Bitstream.Insert(dest, src);
				src.bytepos = bytepos;
				src.bitpos = bitpos;
			}
		}

		public bool Update(float dt, PacketLaneOutput outputFn, ref LanePacket incoming)
		{
			// dequeue in order.
			if (dt < 0)
			{
				if (_recv[_recvPos].buf != null)
				{
					_recvPending--;
					incoming.Buffer = new Bitstream.Buffer();
					Bitstream.Copy(incoming.Buffer, _recv[_recvPos]);
					incoming.Timestamp = _timestamp[_recvPos];
					_recv[_recvPos++].buf = null;
					return true;
				}
				return false;
			}

			// advance ack tail.
			while (_sendAckTail != _sendHead && _sent[_sendAckTail].buf == null)
				_sendAckTail++;

			// all outgoing packets
			for (byte i = _sendAckTail; i != _sendHead; i++)
			{
				if (_sent[i].buf == null)
					continue;

				// If pending sends
				if (_sendTimer[i] <= 0.0f)
				{
					Bitstream.Buffer buf = _bufferFactory.GetBuffer(_sent[i].bytepos + 16);
					WrapOut(buf, _sent[i], i);
					buf.Flip();
					outputFn(buf);
					_ackFlushTimer = 0.0f;
					if (_sendTimer[i] < 0.0f)
						_resentTotal++;
					_sendTimer[i] = _resendTime;
					_sentTotal++;
				}
				else
				{
					_sendTimer[i] -= dt;
					if (_sendTimer[i] == 0)
						_sendTimer[i] = -0.01f;
				}
			}

			if (_ackOutHead != _ackOutTail)
			{
				int count = (int)_ackOutHead - (int)_ackOutTail;
				if (count < 0)
				{
					count += 256;
				}

				_ackFlushTimer += dt;
				if (_ackFlushTimer > 0.30f * _resendTime || count > 32)
				{
					Bitstream.Buffer buf = _bufferFactory.GetBuffer(64);
					WrapOut(buf, null, 0);
					buf.Flip();
					outputFn(buf);
				}
			}

			return false;
		}

		public float ComputePacketLoss()
		{
			if (_sentTotal < 10)
				return 0.0f;

			return (float)_resentTotal / (float)(_sentTotal);
		}

		public void Error(string desc)
		{
			Console.WriteLine("PacketLaneReliableOrdered: " + desc);
		}
	}
}