#define PACKETLANE_LOG
using System;
using System.Diagnostics;

namespace Netki
{
	public interface BufferFactory
	{
		Bitstream.Buffer GetBuffer(uint minSize);
		void ReturnBuffer(Bitstream.Buffer buf);
	}

	public static class PacketLane
	{
		public struct AckRange
		{
			public uint Begin;
			public uint End;
		}

		public struct Done
		{
			public uint SeqId;
			public Lane Lane;
			public bool Reliable;
			public Bitstream.Buffer Data;
			public DateTime ArrivalTime;
			public DateTime CompletionTime;
		}

		public struct Send
		{
			public Lane Lane;
			public Bitstream.Buffer Data;
		}

		// Unreliable buffer out
		public struct OutUBuf
		{
			public byte[] Data;
			public uint Pos;
			public uint Length;
		}

		public struct Statistics
		{
			public uint SentPackets;
			public uint SentBytesTotal;
			public uint SentMessagesReliable;
			public uint SentMessagesUnreliable;
			public uint SentBytesReliable;
			public uint SentBytesUnreliable;
			public uint RecvPackets;
			public uint RecvBytesTotal;
			public uint RecvMessagesReliable;
			public uint RecvMessagesUnreliable;
			public uint RecvBytesReliable;
			public uint RecvBytesUnreliable;
		}

		public struct InFlight
		{
			public uint Begin;
			public uint End;
			public DateTime ResendTime;
			public DateTime FirstSendTime;
			public byte ResendCount;
		}

		public class Lane
		{
			// id is user data.
			public Lane(ulong id, int slots, int bufferSize = 2048)
			{
				OutU = new OutUBuf[slots];
				Done = new Done[slots];
				RecvBuffer = new byte[bufferSize];
				SendBuffer = new byte[bufferSize];
				SendFutureAcks = new AckRange[4];
				SendInFlights = new InFlight[64];
				Id = id;
				LagMsMin = 0;
				SendPeerRecvMax = 2048; // assume at least 4k buffer
			}

			public ulong Id;

			public byte[] RecvBuffer;
			public uint RecvHead;
			public uint RecvSeqCursor;  // Complete data pos.
			public uint RecvTail;       // Read/decode cursor
			public uint RecvLastSeenSeq;

			// 
			public byte[] SendBuffer;
			public uint SendHead;
			public uint SendPeerRecv;    // How far peer has received.
			public uint SendPeerRecvMax; // How far peer can accept.
			public uint SendCursor;
			public AckRange[] SendFutureAcks;
			public uint SendFutureAckCount;
			public InFlight[] SendInFlights;
			public uint SendInFlightCount;
			public bool DoSendAcks;

			// 
			public OutUBuf[] OutU;
			public uint OutUCount;

			public Done[] Done;
			public uint DoneHead;
			public uint DoneTail;

			public uint OutgoingSeq;
			public uint LagMsMin;
			public float[] LagTimings = new float[16];
			public uint LagTimingCount;

			public int Errors;
			public Statistics Stats;

			public DateTime LastIncomingTime;
		}

		public struct IncomingInternal
		{
			public uint Seq;
			public bool IsReliable;
			public bool IsFinalPiece;
		}

		public struct Incoming
		{
			public Lane Lane;
			public DateTime ArrivalTime;
			public byte[] Data;
			public uint Pos;
			public uint Length;
		}

		public struct LaneSetup
		{
			public BufferFactory Factory;
			public uint MaxPacketSize;
			public uint ReservedHeaderBytes;
			public float MinRoundTripMs;
		}

		[Conditional("DEBUG")]
		private static void Log(string s)
		{
			#if PACKETLANE_LOG
			Console.WriteLine("PL: " +s);
			#endif
		}

		[Conditional("DEBUG")]
		private static void Log(Lane l, string s)
		{
			#if PACKETLANE_LOG
			Log("PL[" + l.Id + "]:" +s);
			#endif
		}

		[Conditional("DEBUG")]
		private static void Assert(bool cond, string desc)
		{
			if (!cond)
			{
				Console.WriteLine("Assert failure! " + desc);
			}
		}

		// This will start reading from the buffers and modify the incoming array.
		public static void HandleIncomingPackets(LaneSetup setup, Incoming[] packets, uint packetsCount)
		{
			for (uint i=0;i<packetsCount;i++)
			{
				byte[] data = packets[i].Data;
				uint pos = packets[i].Pos;
				uint end = packets[i].Pos + packets[i].Length;
				Lane lane = packets[i].Lane;

				uint seq, lastSeenSeq;
				pos = ReadU32(data, pos, out seq);
				pos = ReadU32(data, pos, out lastSeenSeq);

				while ((end - pos) > 5)
				{
					byte type = data[pos++];
					if (type == 0)
					{
						// Ack data.
						if ((end-pos) < 9)
						{
							Log(lane, "Ack chunk but it is too tiny");
							break;
						}
						uint recvSeqCursor, maxRecv;
						pos = ReadU32(data, pos, out recvSeqCursor);
						pos = ReadU32(data, pos, out maxRecv);
						byte count = data[pos++];

						if ((end-pos) < count * 8)
						{
							Log(lane, "Ack chunk but it is too tiny to hold " + count + " future acks!");
							break;
						}

						if (maxRecv >= lane.SendPeerRecvMax)
						{
							lane.SendPeerRecvMax = maxRecv;
							Log(lane, "Peer can receive up to " + maxRecv);
						}
						else
						{
							Log(lane, "Out of order acks, maxRecv=" + maxRecv + " previous=" + lane.SendPeerRecvMax);
						}

						if (recvSeqCursor >= lane.SendPeerRecv)
						{
							lane.SendPeerRecv = recvSeqCursor;
							Log(lane, "Peer has received everything up to " + lane.SendPeerRecv);
						}
						else
						{
							Log(lane, "Out of order recv");
						}

						if (seq > lane.RecvLastSeenSeq)
						{
							lane.RecvLastSeenSeq = seq;
						}

						for (int k=0;k<lane.SendInFlightCount;k++)
						{
							if (lane.SendInFlights[k].End == 0)
							{
								continue;
							}
							if (lane.SendInFlights[k].Begin < recvSeqCursor && lane.SendInFlights[k].End <= recvSeqCursor)
							{								
								Log(lane, "   - clearing in flight - [" + lane.SendInFlights[k].Begin + "," + lane.SendInFlights[k].End + "]");
								// Use only timing for stuff that was not merged, or succeed on the first attempt.
								if (lane.SendInFlights[k].ResendCount == 0 /*&& ackEnd == lane.SendInFlights[k].End*/)
								{
									float roundtripMs = (float) (packets[i].ArrivalTime - lane.SendInFlights[k].FirstSendTime).TotalMilliseconds;
									Log(lane, "Roundtrip time=" + roundtripMs);
									if (roundtripMs < 15.0)
									{
										roundtripMs = 15.0f;
									}
									if (roundtripMs < setup.MinRoundTripMs)
									{
										roundtripMs = setup.MinRoundTripMs;
									}
									lane.LagTimings[(lane.LagTimingCount++) % lane.LagTimings.Length] = roundtripMs;
								}
								lane.SendInFlights[k].End = 0;
							}
						}

						for (uint j=0;j<count;j++)
						{
							if (j < count)
							{
								uint ackBeg, ackEnd;
								pos = ReadU32(data, pos, out ackBeg);
								pos = ReadU32(data, pos, out ackEnd);
								Log(lane, " => AckRange " + j + " = [" + ackBeg + "," + ackEnd + "]");
								if (ackEnd > ackBeg)
								{
									for (int k=0;k<lane.SendInFlightCount;k++)
									{
										if (lane.SendInFlights[k].End == 0)
										{
											continue;
										}
										if (ackBeg == lane.SendInFlights[k].Begin)
										{
											if (ackEnd >= lane.SendInFlights[k].End)
											{
												Log(lane, "   - clearing in flight - [" + lane.SendInFlights[k].Begin + "," + lane.SendInFlights[k].End + "]");
												// Use only timing for stuff that was not merged, or succeed on the first attempt.
												if (lane.SendInFlights[k].ResendCount == 0 /*&& ackEnd == lane.SendInFlights[k].End*/)
												{
													float roundtripMs = (float) (packets[i].ArrivalTime - lane.SendInFlights[k].FirstSendTime).TotalMilliseconds;
													lane.LagTimings[(lane.LagTimingCount++) % lane.LagTimings.Length] = roundtripMs;
													Log(lane, "Roundtrip time=" + roundtripMs);
												}
												lane.SendInFlights[k].End = 0;
												continue;
											}
											else
											{
												Log(lane, "   - shrunk in flight - [" + lane.SendInFlights[k].Begin + "," + lane.SendInFlights[k].End + "]");												
												lane.SendInFlights[k].End = ackEnd;
											}
										}
										else if (end == lane.SendInFlights[k].End)
										{
											if (ackEnd < lane.SendInFlights[k].Begin)
											{
												Log(lane, "   - clearing in flight - [" + lane.SendInFlights[k].Begin + "," + lane.SendInFlights[k].End + "]");
												lane.SendInFlights[k].End = 0;
											}
											else if (ackBeg > lane.SendInFlights[k].Begin)
											{
												Log(lane, "   - shrunk in flight - [" + lane.SendInFlights[k].Begin + "," + lane.SendInFlights[k].End + "]");
												lane.SendInFlights[k].Begin = ackBeg;
											}
										}
										else if (lane.SendInFlights[k].End <= recvSeqCursor)
										{
											Log(lane, "   - ");
										}
									}
							    }
								else
								{
									Log(lane, "  => Throwing away junk ackrange");
								}
							}
							else
							{
								pos += 8;
							}
						}
					}
					else if (type == 2)
					{
						// Unreliable
						if ((end - pos) < 3)
						{
							Log(lane, "Too small unreliable data chunk!");
							break;
						}

						uint size = (uint)data[pos+0] + 256 * (uint)data[pos+1];
						pos += 2;

						if ((end - pos) < size)
						{
							Log(lane, "Truncated unreliable data!");
							break;
						}

						if ((lane.DoneTail - lane.DoneHead) == lane.Done.Length)
						{
							Log(lane, "Dropping unreliable because queue is full");
						}
						else
						{
							Bitstream.Buffer tmp = setup.Factory.GetBuffer(size);
							uint idx = (uint)(lane.DoneHead % lane.Done.Length);
							lane.Done[idx].ArrivalTime = packets[i].ArrivalTime;
							lane.Done[idx].CompletionTime = packets[i].ArrivalTime;
							lane.Done[idx].Data = tmp;
							lane.Done[idx].Lane = packets[i].Lane;
							lane.Done[idx].Reliable = false;
							lane.Done[idx].SeqId = seq;
							tmp.bytesize = size;
							Array.Copy(data, pos, tmp.buf, 0, size);
							lane.DoneHead++;
							Log(lane, "Unreliable of size " + size + " arrived on seq " + seq);	
						}
						pos += size;
					}
					else if (type == 1)
					{
						// Reliable data segment
						if ((end - pos) < 9)
						{
							Log(lane, "Too small stream data chunk!");
							break;
						}

						uint segBeg, segEnd;
						pos = ReadU32(data, pos, out segBeg);
						pos = ReadU32(data, pos, out segEnd);

						uint len = segEnd - segBeg;
						if (len > (end-pos))
						{
							Log(lane, "Discarding junk chunk [" + segBeg + "," + segEnd + "] left=" + (end-pos));
							break;
						}
						if (len > lane.RecvBuffer.Length)
						{
							Log(lane, "Discarding junk chunk [" + segBeg + "," + segEnd + "], longer than receive buffer!");
							break;
						}

						Log(lane, "Receiving stream data chunk [" + segBeg + "," + segEnd + "]");

						for (uint w=segBeg;w!=segEnd;w++)
						{
							lane.RecvBuffer[w % lane.RecvBuffer.Length] = data[pos++];
						}

						if (segEnd <= lane.RecvSeqCursor)
						{
							Log(lane, " => This is a duplicate");
							lane.DoSendAcks = true;
						}
						if (segBeg == lane.RecvSeqCursor)
						{
							Log(lane, " => Was continuation of stream");
							lane.RecvSeqCursor = segEnd;
						}
						if (seq > lane.RecvLastSeenSeq)
						{
							lane.RecvLastSeenSeq = seq;
						}

						// Merge ranges.
						bool morework;
						do
						{
							morework = false;
							for (int a=0;a<lane.SendFutureAckCount;a++)
							{
								for (int b=0;b<lane.SendFutureAckCount;b++)
								{
									if (a == b)
									{
										continue;
									}
									if (lane.SendFutureAcks[b].Begin == lane.SendFutureAcks[a].End &&
									    lane.SendFutureAcks[b].End > lane.SendFutureAcks[b].Begin &&
									    lane.SendFutureAcks[a].End > lane.SendFutureAcks[a].Begin)
									{
										Log(lane, "Merging range [" + lane.SendFutureAcks[a].Begin + "," + lane.SendFutureAcks[a].End + "," + lane.SendFutureAcks[b].End + "]");
										lane.SendFutureAcks[a].End = lane.SendFutureAcks[b].End;
										lane.SendFutureAcks[b].Begin = 0;
										lane.SendFutureAcks[b].End = 0;
										morework = true;
									}
								}
							}
						} 
						while (morework);

						bool hadqueued;
						do
						{
							hadqueued = false;
							for (int k=0;k<lane.SendFutureAckCount;k++)
							{
								if (lane.SendFutureAcks[k].Begin == lane.RecvSeqCursor)
								{
									if (lane.RecvSeqCursor < lane.SendFutureAcks[k].End)
									{
										lane.RecvSeqCursor = lane.SendFutureAcks[k].End;
										hadqueued = true;
									}
								}
							}
						} 
						while (hadqueued);

						// Adjust old acks to only include actual future data.
						uint writeAck = 0;
						for (int k=0;k<lane.SendFutureAckCount;k++)
						{
							if (lane.SendFutureAcks[k].Begin < lane.RecvSeqCursor)
							{
								lane.SendFutureAcks[k].Begin = lane.RecvSeqCursor;
							}
							uint diff = lane.SendFutureAcks[k].End - lane.SendFutureAcks[k].Begin;
							if (diff > 0 && diff <= (uint)lane.RecvBuffer.Length)
							{
								lane.SendFutureAcks[writeAck++] = lane.SendFutureAcks[k];
							}
						}
						lane.SendFutureAckCount = writeAck;

						// If there is room for more future acks.. otherwise just ignore and wait for continuation.
						if (lane.SendFutureAckCount < lane.SendFutureAcks.Length)
						{
							if (segBeg > lane.RecvSeqCursor)
							{
								lane.SendFutureAcks[lane.SendFutureAckCount].Begin = segBeg;
								lane.SendFutureAcks[lane.SendFutureAckCount].End = segEnd;
								lane.SendFutureAckCount++;
							}
						}

						lane.DoSendAcks = true;
					}
					else
					{
						break;
					}
				}
			}
		}

		static uint WriteU32(byte[] data, uint writePos, uint v)
		{
			data[writePos+0] = (byte)((v >> 0) & 0xff);
			data[writePos+1] = (byte)((v >> 8) & 0xff);
			data[writePos+2] = (byte)((v >> 16) & 0xff);
			data[writePos+3] = (byte)((v >> 24) & 0xff);
			return writePos + 4;
		}

		static uint ReadU32(byte[] data, uint readPos, out uint v)
		{
			byte s0 = data[readPos+0];
			byte s1 = data[readPos+1];
			byte s2 = data[readPos+2];
			byte s3 = data[readPos+3];			
			v = (uint)s0 + ((uint)s1 << 8) + ((uint)s2 << 16) + ((uint)s3 << 24);
			return readPos + 4;
		}

		static uint ComputeSegmentSizeRequirement(uint length)
		{
			// must match below.
			return length + 9;
		}

		static uint WriteSegmentFromCircular(Lane l, byte[] data, uint writePos, uint maxWritePos, byte[] source, uint begin, uint maxCount, out uint count)
		{
			count = 0;
			const uint extra = 9;
			uint bytesLeft = maxWritePos - writePos;
			if (bytesLeft < extra)
			{
				return writePos;
			}
			uint maxFit = bytesLeft - extra;
			count = maxFit < maxCount ? maxFit : maxCount;
			data[writePos] = 0x1; // 1 = data segment
			writePos = WriteU32(data, writePos + 1, begin);
			writePos = WriteU32(data, writePos, begin + count);
			Log(l, "Sending stream segment [" + begin + ", " + (begin+count) + "]");
			for (uint i=0;i<count;i++)
			{
				data[writePos + i] = source[(begin+i) % source.Length];
			}
			return writePos + count;
		}

		// Return if there is more to come.
		public static bool ProcessLanesSend(LaneSetup setup, Lane[] lanes, DateTime lastRecvTime, DateTime now, Send[] output, out uint numOut)
		{
			numOut = 0;

			for (int i=0;i<lanes.Length;i++)
			{
				Lane lane = lanes[i];
			}

			for (int i=0;i<output.Length;i++)
			{
				Lane lane = output[i].Lane;
			}

			bool hasMore = false;

			// Unsent acks
			for (int i=0;i<lanes.Length;i++)
			{
				Lane lane = lanes[i];

				Bitstream.Buffer buf = setup.Factory.GetBuffer(setup.MaxPacketSize);
				byte[] data = buf.buf;
				uint writePos = setup.ReservedHeaderBytes + 8;
				uint maxWritePos = setup.MaxPacketSize;

				bool containsAnything = false;

				if (lane.DoSendAcks)
				{
					// 1. Put in reliable sequence update / acks.
					data[writePos] = 0x0; // 0 = reliable sequence update.
					writePos = WriteU32(data, writePos + 1, lane.RecvSeqCursor); // How far have received all data.
					writePos = WriteU32(data, writePos, lane.RecvTail + (uint)lane.RecvBuffer.Length); // How far can receive.
					Log(lane, "Sending ack (counts=" + lane.SendFutureAckCount + ") RecvSeqCursor=" + lane.RecvSeqCursor + " RecvMax=" + (lane.RecvTail + (uint)lane.RecvBuffer.Length));

					// Future ranges.
					data[writePos++] = (byte)lane.SendFutureAckCount;
					for (uint j=0;j<lane.SendFutureAckCount;j++)
					{
						writePos = WriteU32(data, writePos, lane.SendFutureAcks[j].Begin);
						writePos = WriteU32(data, writePos, lane.SendFutureAcks[j].End);
					}
					lane.DoSendAcks = false;
					containsAnything = true;
				}

				// Clear out old records.
				uint outCount = 0;
				for (uint j=0;j<lane.SendInFlightCount;j++)
				{
					if (lane.SendInFlights[j].End == 0)
						continue;
					if (j != outCount)
						lane.SendInFlights[outCount] = lane.SendInFlights[j];
					++outCount;
				}
				lane.SendInFlightCount = outCount;

				double resendMs = 1000.0f;

				// If have something to send and data to compute for
				if ((outCount > 0 || lane.SendHead != lane.SendCursor) && lane.LagTimingCount > 2)
				{
					// Compute resend time
					uint mx = lane.LagTimingCount < (uint)lane.LagTimings.Length ? lane.LagTimingCount : (uint)lane.LagTimings.Length;
					float min = lane.LagTimings[0];
					float sum = min;
					for (int k=1;k<mx;k++)
					{
						sum += lane.LagTimings[k];
						if (lane.LagTimings[k] < min)
							min = lane.LagTimings[k];
					}

					if (min < 1.0f)
					{
						min = 1.0f;
					}

					float avg = sum / mx;
					resendMs = 2.05f * (min + 0.5f * (avg - min));
					Log(lane, "Roundtrip ms min=" + min + " avg=" + avg + " resendMs=" + resendMs);
				}

				bool didResends = false;
				for (uint j=0;j<lane.SendInFlightCount;j++)
				{
					if (lane.SendInFlights[j].ResendTime >= now)
					{
						continue;
					}

					double over = (now - lane.SendInFlights[j].ResendTime).TotalMilliseconds;

					uint count = lane.SendInFlights[j].End - lane.SendInFlights[j].Begin;
					if ((maxWritePos - writePos) < ComputeSegmentSizeRequirement(count))
					{
						Log(lane, "Ignoring resend segment " + lane.SendInFlights[j].Begin + "," + lane.SendInFlights[j].End + " because it would not fit!");
						// Try again without the acks maybe.
						hasMore = true;
						continue;
					}

					Log(lane, "Resending segmnt [" + lane.SendInFlights[j].Begin + "," + lane.SendInFlights[j].End + "] resendCount=" + lane.SendInFlights[j].ResendCount + " PeerRecvSeq=" + lane.SendPeerRecv + " msover=" + over);

					uint numWritten;
					writePos = WriteSegmentFromCircular(lane, data, writePos, maxWritePos, lane.SendBuffer, 
					                                    lane.SendInFlights[j].Begin, count, out numWritten);

					Assert(numWritten == count, "Did not actually fit buffer!");

					++lane.SendInFlights[j].ResendCount;
					lane.SendInFlights[j].ResendTime = now.AddMilliseconds(resendMs * lane.SendInFlights[j].ResendCount);

					didResends = true;
					containsAnything = true;
				}

				if (!didResends && lane.SendHead != lane.SendCursor && lane.SendInFlightCount < lane.SendInFlights.Length)
				{
					// Send previsouly unsent data up to the recv window, excluding already received acks.
					uint inQueue = lane.SendHead - lane.SendCursor;
					uint maxSend = lane.SendPeerRecvMax - lane.SendCursor;
					uint toInsert = inQueue < maxSend ? inQueue : maxSend;
					Log(lane, "writePos=" + writePos + " sendCursor=" + lane.SendCursor + " sendEnd=" + lane.SendHead + " maxSend=" + maxSend + " toInsert=" + toInsert + " peerRecvMax=" + lane.SendPeerRecvMax + " resendMs=" + resendMs);
					if (toInsert > 0)
					{
						uint beg = lane.SendCursor;
						uint fin = lane.SendCursor + toInsert;
						while (beg < fin)
						{
							uint end = fin;
							uint count = end - beg;
							uint bytes;

							writePos = WriteSegmentFromCircular(lane, data, writePos, maxWritePos, lane.SendBuffer, beg, count, out bytes);

							if (bytes > 0)
							{
								uint idx = lane.SendInFlightCount++;
								lane.SendInFlights[idx].Begin = beg;
								lane.SendInFlights[idx].End = beg + bytes;
								lane.SendInFlights[idx].FirstSendTime = now;
								lane.SendInFlights[idx].ResendTime = now.AddMilliseconds((double)resendMs);
								lane.SendInFlights[idx].ResendCount = 0;
								containsAnything = true;
								beg += bytes;
							}

							if (count != bytes)
							{
								// Filled up buffer.
								hasMore = true;
								break;
							}
						}
						lane.SendCursor = beg;
					}
				}

				// Unreliable
				for (uint k=0;k<lane.OutUCount;k++)
				{
					if (lane.OutU[k].Length == 0)
					{
						continue;
					}
					const uint extraU = 3;
					bool isRoom = (lane.OutU[k].Length + extraU) < (maxWritePos - writePos);
					if (!isRoom)
					{
						hasMore = true;
					}
					else
					{
						Log(lane, "Adding unreliable packet sz=" + lane.OutU[k].Length);
						data[writePos+0] = 0x2; // 2 unreliable
						data[writePos+1] = (byte)(lane.OutU[k].Length & 0xff);
						data[writePos+2] = (byte)(lane.OutU[k].Length >> 8);
						writePos += extraU;
						byte[] u = lane.OutU[k].Data;
						uint p = lane.OutU[k].Pos;
						uint l = lane.OutU[k].Length;
						for (uint j=0;j<l;j++)
						{
							data[writePos + j] = u[p+j];
						}
						writePos += l;
						containsAnything = true;
						lane.OutU[k].Length = 0;
					}
				}

				uint write = 0;
				for (uint u=0;u<lane.OutUCount;u++)
				{
					if (lane.OutU[u].Length == 0)
						continue;
					if (u != write)
						lane.OutU[write] = lane.OutU[u];
					write++;
				}
				lane.OutUCount = write;

				if (!containsAnything)
				{
					setup.Factory.ReturnBuffer(buf);
				}
				else
				{
					WriteU32(data, setup.ReservedHeaderBytes, lane.OutgoingSeq++);
					WriteU32(data, setup.ReservedHeaderBytes + 4, lane.RecvLastSeenSeq);

					buf.bytesize = writePos;
					output[numOut].Data = buf;
					output[numOut].Lane = lane;
					if (++numOut == output.Length)
					{
						return true;
					}
				}
			}

			return hasMore;
		}

		public struct ToSendWithBuffer
		{
			public Lane Lane;
			public bool Reliable;
			public Bitstream.Buffer Data;
		}

		// Will hold the buffers until they are sent
		public static void ScheduleSend(LaneSetup setup, ToSendWithBuffer[] tosend, uint count)
		{
			ToSend[] pkt = new ToSend[tosend.Length];
			for (uint i=0;i<count;i++)
			{
				pkt[i].Data = tosend[i].Data.buf;
				pkt[i].Pos = tosend[i].Data.bytepos;
				pkt[i].Length = tosend[i].Data.bytesize - tosend[i].Data.bytepos;
				pkt[i].Reliable = tosend[i].Reliable;
				pkt[i].Lane = tosend[i].Lane;
			}
			ScheduleSend(setup, pkt, count);
		}

		public struct ToSend
		{
			public Lane Lane;
			public bool Reliable;
			public byte[] Data;
			public uint Pos;
			public uint Length;
		}

		// Will hold the buffers until they are sent
		public static void ScheduleSend(LaneSetup setup, ToSend[] tosend, uint count)
		{
			for (uint i=0;i<count;i++)
			{
				if (tosend[i].Reliable)
				{
					InsertReliable(setup, tosend[i].Lane, tosend[i].Data, tosend[i].Pos, tosend[i].Length);
				}
				else
				{
					InsertUnreliable(setup, tosend[i].Lane, tosend[i].Data, tosend[i].Pos, tosend[i].Length);
				}
			}
		}

		static void InsertReliable(LaneSetup setup, Lane lane, byte[] data, uint pos, uint len)
		{
			uint bytesLeft = lane.SendPeerRecv + (uint)lane.SendBuffer.Length - lane.SendHead;

			// compute size for header + actual data.
			Assert(len < 65536, "Reliable packet too big!");
			bool makeBig = false;
			uint required = len;
			if (len >= 255)
			{
				required += 5;
				makeBig = true;
			}
			else
			{
				required += 1;
			}

			if (required >= bytesLeft)
			{
				Log(lane, "RDrop, send queue is full");
				return;
			}

			byte[] output = lane.SendBuffer;
			if (!makeBig)
			{
				output[lane.SendHead % output.Length] = (byte)(len);
				lane.SendHead++;
			}
			else
			{
				byte[] tmp = new byte[4];	
				WriteU32(tmp, 0, len);
				output[lane.SendHead % output.Length] = 255;
				lane.SendHead++;
				output[lane.SendHead % output.Length] = tmp[0];
				lane.SendHead++;
				output[lane.SendHead % output.Length] = tmp[1];
				lane.SendHead++;
				output[lane.SendHead % output.Length] = tmp[2];
				lane.SendHead++;
				output[lane.SendHead % output.Length] = tmp[3];
				lane.SendHead++;
			}

			for (int i=0;i<len;i++)
			{
				output[(lane.SendHead + i) % output.Length] = data[pos + i];
			}

			lane.SendHead += len;
		}

		static void InsertUnreliable(LaneSetup setup, Lane lane, byte[] data, uint pos, uint len)
		{
			uint count = lane.OutUCount;
			uint idx;
			if (count < lane.OutU.Length)
			{
				idx = lane.OutUCount++;
			}
			else
			{
				// Clear all pending and start over. Maybe shift array, but order most be preserved.
				Log(lane, "UDrop, send queue is full");
				idx = 0;
				lane.OutUCount = 1;
			}

			if (lane.OutU[idx].Data == null || lane.OutU[idx].Data.Length < len)
			{
				lane.OutU[idx].Data = new byte[len];
			}
			Array.Copy(data, pos, lane.OutU[idx].Data, 0, len);
			lane.OutU[idx].Pos = pos;
			lane.OutU[idx].Length = len;
		}

		// Return if there is more to come.
		public static bool ProcessLanesIncoming(LaneSetup setup, Lane[] lanes, Done[] output, out uint numOut)
		{
			numOut = 0;

			// Check the progress if we can pick another one.
			for (int i=0;i<lanes.Length;i++)
			{
				Lane lane = lanes[i];

				// Reliable
				while (true)
				{
					uint available = lane.RecvSeqCursor - lane.RecvTail;
					if (available < 2)
					{
						break;
					}
					byte d0 = lane.RecvBuffer[lane.RecvTail % lane.RecvBuffer.Length];
					uint size;
					uint start;
					if (d0 == 255)
					{
						if (available < 200)
						{
							break;
						}
						byte[] u32 = new byte[4] {
							lane.RecvBuffer[(lane.RecvTail + 1) % lane.RecvBuffer.Length],
							lane.RecvBuffer[(lane.RecvTail + 2) % lane.RecvBuffer.Length],
							lane.RecvBuffer[(lane.RecvTail + 3) % lane.RecvBuffer.Length],
							lane.RecvBuffer[(lane.RecvTail + 4) % lane.RecvBuffer.Length]
						};
						ReadU32(u32, 0, out size);
						start = lane.RecvTail + 5;
						if (available < (size + 5))
						{
							break;
						}
					}
					else
					{
						size = d0;
						start = lane.RecvTail + 1;
						if (available < (size + 1))
						{
							break;
						}
					}

					Bitstream.Buffer res = setup.Factory.GetBuffer(size);
					for (int w=0;w<size;w++)
					{
						res.buf[w] = lane.RecvBuffer[(start + w) % lane.RecvBuffer.Length];
					}
					res.bytesize = size;
					lane.RecvTail = start + size;
					lane.DoSendAcks = true; // this changes the receive window.
					output[numOut].ArrivalTime = DateTime.Now;
					output[numOut].CompletionTime = DateTime.Now;
					output[numOut].Data = res;
					output[numOut].Lane = lane;
					output[numOut].Reliable = true;
					if (++numOut == output.Length)
					{
						return true;
					}
				}

				// Unreliable
				while (lane.DoneTail != lane.DoneHead)
				{
					output[numOut] = lane.Done[(lane.DoneTail++) % lane.Done.Length];
					if (++numOut == output.Length)
					{
						return true;
					}
				}

			}
			return false;
		}
	}
}