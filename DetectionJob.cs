using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Spectre.Console;

namespace Unai.MpegTsRecover;

public class DetectionJob
{
	// Parameters
	public long StartOffset { get; set; }
	public long SkipAlignment { get; set; }
	public bool WriteFiltered { get; set; }

	public string InputFile { get; set; }
	public List<MpegTsDataRange> DataRanges { get; } = [];

	internal FileStream _inputStr = null;
	internal FileStream _outputStr = null;
	internal BinaryReader _br = null;
	internal BinaryWriter _bw = null;

	internal bool _insideTs = false;
	internal bool _isM2ts = false;
	internal int _curM2tsTimestamp = -1;
	internal long _curPcr = -1;
	internal DateTime? _curDateTime = null;

	public object SyncLock { get; set; } = new();
	public string JobDescription { get; set; } = "Initializing…";
	public double JobProgress { get; set; } = 0;

	public void Start()
	{
		_inputStr = File.OpenRead(InputFile);
		_br = new BinaryReader(_inputStr);

		_inputStr.Position = StartOffset;

		while (_inputStr.Position < _inputStr.Length)
		{
			var detectionOfs = _inputStr.Position;

			var pktMagic = _br.ReadByte();
			// Is this a possible MPEG-TS packet magic number? If not, keep reading.
			if (pktMagic != 0x47)
			{
				UpdateJobMessages();
				continue;
			}
			// I'm about to read a MPEG-TS packet. Is there enough remaining data for that? If not, abort.
			if (_inputStr.Length - _inputStr.Position < 191) break;

			var buf = _br.ReadBytes(191 + 192 + 192); // Size is for three possible M2TS packets (minus one already-read byte).
			if (buf[192 - 1] == 0x47 && buf[384 - 1] == 0x47) // Are more M2TS packets detected?
			{
				_isM2ts = true;
				_insideTs = true;
			}
			else if (buf[188 - 1] == 0x47 && buf[376 - 1] == 0x47 && buf[564 - 1] == 0x47) // Are more MPEG-TS packets detected?
			{
				_isM2ts = false;
				_insideTs = true;
			}

			if (_insideTs)
			{
				// Calculate the alignment for the parsing method:
				// - For BDAV M2TS, leave offset at the preceding 4-byte timestamp.
				// - For normal MPEG-TS, leave offset at the next 0x47 header.
				int jumpLength = _isM2ts ? 192 - 4 : 188 - 12;

				var buf2 = _br.ReadBytes(jumpLength);

				if (WriteFiltered)
				{
					_outputStr?.Close();
					_outputStr = File.OpenWrite($"{Path.GetFileNameWithoutExtension(InputFile)}.0x{detectionOfs:x12}.ts");
					_bw = new BinaryWriter(_outputStr);

					// The output must be consistent, therefore:
					// - If the input is M2TS, convert it to MPEG-TS and write it to output.
					// - If the input is MPEG-TS, write it as is.
					if (_isM2ts)
					{
						// Join all read bytes into a unified array/buffer.
						var allBufs = new byte[1 + buf.Length + buf2.Length];
						allBufs[0] = 0x47;
						Array.Copy(buf,		0,	allBufs,	1,					buf.Length);
						Array.Copy(buf2,	0,	allBufs,	1 + buf.Length,		buf2.Length);

						for (int i = 0; i < allBufs.Length - 192; i += 192)
						{
							_bw.Write(allBufs[i..(i + 188)]);
						}
					}
					else
					{
						_bw.Write(0x47);
						_bw.Write(buf);
						_bw.Write(buf2);
					}
				}

				ParseMpegTsStream(detectionOfs);
			}
			else if (SkipAlignment > 0)
			{
				var jumpLength = SkipAlignment - (_inputStr.Position % SkipAlignment);
				Debug.Assert((_inputStr.Position + jumpLength) % SkipAlignment == 0);
				if (_inputStr.Position + jumpLength <= _inputStr.Length)
				{
					_inputStr.Position += jumpLength;
				}
			}
			else
			{
				_inputStr.Position += 1;
			}

			_insideTs = false;
			_curM2tsTimestamp = -1;
			_curPcr = -1;

			UpdateJobMessages();
		}

		UpdateJobMessages();
	}

	void ParseMpegTsStream(long startOffset = -1)
	{
		if (startOffset < 0)
		{
			startOffset = _inputStr.Position; // This value might be skewed forwards by a few bytes from the actual first packet.
		}

		var mpegTsRange = new MpegTsDataRange()
		{
			StartOffset = startOffset,
			IsM2TS = _isM2ts,
		};
		DataRanges.Add(mpegTsRange);

		AnsiConsole.MarkupLineInterpolated($"[blue]0x{_inputStr.Position:x12}[/]/[yellow]{_inputStr.Position/1048576} MiB[/]: [green]Detected {(_isM2ts ? "M2TS" : "MPEG-TS")} data[/].");

		// PrintCurrentStreamData();

		while (_inputStr.Position < _inputStr.Length - 192)
		{
			var pktOfs = _inputStr.Position;
			mpegTsRange.EndOffset = pktOfs;

			_curM2tsTimestamp = _isM2ts ? _br.ReadInt32() : -1;

			var pkt = _br.ReadBytes(188);

			if (pkt[0] != 0x47)
			{
				AnsiConsole.MarkupLineInterpolated($"[blue]0x{_inputStr.Position:x12}[/]/[yellow]{_inputStr.Position / 1048576} MiB[/]: [red]Sync lost after {mpegTsRange.Length / 1048576} MiB[/].");
				return;
			}

			if (WriteFiltered)
			{
				_bw.Write(pkt);
			}

			ParseMpegTsPacket(mpegTsRange, pkt);

			UpdateJobMessages();
		}
	}

	private void ParseMpegTsPacket(MpegTsDataRange mpegTsRange, byte[] pkt)
	{
		var pktPid = pkt[2] | ((pkt[1] & 0x1f) << 8);
		var pktAdapFieldCtrlRaw = (pkt[3] & 0x30) >> 4;
		bool pktPayloadPresent = (pktAdapFieldCtrlRaw | 1) != 0;
		bool pktAdapFieldPresent = (pktAdapFieldCtrlRaw >> 1) != 0;

		var pktPayloadOffset = 5 + (pktAdapFieldPresent ? pkt[4] + 1 : 0);

		if (pktAdapFieldPresent)
		{
			var pktAdapFieldLen = pkt[4];
			var pktPcrPresent = ((pkt[5] & 0x10) >> 8) != 0;

			if (pktPcrPresent)
			{
				// TODO: Where extension?
				var pktPcrBase = BinaryPrimitives.ReadInt32BigEndian(pkt[6..9]);
				_curPcr = pktPcrBase * 300;
			}
		}

		if (pktPid == 0x14) // TDT/TOT
		{
			var isTdt = pkt[pktPayloadOffset] == 0x70;

			if (isTdt)
			{
				var mjd = BinaryPrimitives.ReadUInt16LittleEndian([pkt[pktPayloadOffset + 4], pkt[pktPayloadOffset + 3]]);
				var hour = Utils.FromBcd(pkt[pktPayloadOffset + 5]);
				var minute = Utils.FromBcd(pkt[pktPayloadOffset + 6]);
				var second = Utils.FromBcd(pkt[pktPayloadOffset + 7]);

				_curDateTime = new DateTime(1858, 11, 17, hour, minute, second).AddDays(mjd);
				mpegTsRange.FirstDateTime ??= _curDateTime;
			}
		}
	}

	void PrintCurrentStreamData(int totalPackets = 4)
	{
		var curOfs = _inputStr.Position;

		for (int pktNum = 0; pktNum < totalPackets; pktNum++)
		{
			PrintPacket(_br.ReadBytes(_isM2ts ? 192 : 188));
		}

		_inputStr.Position = curOfs;
	}

	static void PrintPacket(byte[] pkt)
	{
		StringBuilder sb = new("    ");
		for (int i = 0; i < pkt.Length; i++)
		{
			var b = pkt[i];
			if (i >= 80) continue;
			if (b == 0x47)
			{
				sb.Append("[cyan]G[/]");
			}
			else if (b == '[' || b == ']')
			{
				sb.Append('.');
			}
			else if (b >= 0x20 && b < 0x7f)
			{
				sb.Append((char)b);
			}
			else
			{
				sb.Append('.');
			}
		}
		AnsiConsole.MarkupLine(sb.ToString());
	}

	void UpdateJobMessages()
	{
		lock (SyncLock)
		{
			JobProgress = _inputStr.Position / (double)_inputStr.Length;
			if (_insideTs)
			{
				JobDescription = $"Reading at 0x{_inputStr.Position:x12}…";
				// if (_curM2tsTimestamp > -1)
				// {
				// 	JobDescription += $" (TS={_curM2tsTimestamp})";
				// }
				// if (_curPcr > -1)
				// {
				// 	JobDescription += $" (PCR={_curPcr})";
				// }
				if (_curDateTime.HasValue)
				{
					JobDescription += $" ({_curDateTime.Value:s})";
				}
			}
			else
			{
				JobDescription = $"Searching at 0x{_inputStr.Position:x12}…";
			}
		}
	}
}