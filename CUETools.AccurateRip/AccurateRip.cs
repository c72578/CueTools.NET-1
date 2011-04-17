using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml.Serialization;
using CUETools.Parity;
using CUETools.CDImage;
using CUETools.Codecs;

namespace CUETools.AccurateRip
{
	[Serializable]
	public class OffsetSafeCRCRecord
	{
		private uint[] val;

		public OffsetSafeCRCRecord()
		{
			this.val = new uint[1];
		}

		public OffsetSafeCRCRecord(AccurateRipVerify ar)
			: this(new uint[64 + 64])
		{
			int offset = 64 * 64;
			for (int i = 0; i < 64; i++)
				this.val[i] = ar.CTDBCRC(0, (i + 1) * 64, offset, 2 * offset);
			for (int i = 0; i < 64; i++)
				this.val[i + 64] = ar.CTDBCRC(0, 63 - i, offset, 2 * offset);
		}

		public OffsetSafeCRCRecord(uint[] val)
		{
			this.val = val;
		}

		[XmlIgnore]
		public uint[] Value
		{
			get
			{
				return val;
			}
		}

		public unsafe string Base64
		{
			get
			{
				byte[] res = new byte[val.Length * 4];
				fixed (byte* pres = &res[0])
				fixed (uint* psrc = &val[0])
					AudioSamples.MemCpy(pres, (byte*)psrc, res.Length);
				var b64 = new char[res.Length * 2 + 4];
				int b64len = Convert.ToBase64CharArray(res, 0, res.Length, b64, 0);
				StringBuilder sb = new StringBuilder(b64len + b64len / 4 + 1);
				for (int i = 0; i < b64len; i += 64)
				{
					sb.Append(b64, i, Math.Min(64, b64len - i));
					sb.AppendLine();
				}
				return sb.ToString();
			}

			set
			{
				if (value == null)
					throw new ArgumentNullException();
				byte[] bytes = Convert.FromBase64String(value);
				if (bytes.Length % 4 != 0)
					throw new InvalidDataException();
				val = new uint[bytes.Length / 4];
				fixed (byte* pb = &bytes[0])
				fixed (uint* pv = &val[0])
					AudioSamples.MemCpy((byte*)pv, pb, bytes.Length);
			}
		}

		public override bool Equals(object obj)
		{
			return obj is OffsetSafeCRCRecord && this == (OffsetSafeCRCRecord)obj;
		}

		public override int GetHashCode()
		{
			return (int)val[0];
		}

		public static bool operator ==(OffsetSafeCRCRecord x, OffsetSafeCRCRecord y)
		{
			if (x as object == null || y as object == null) return x as object == null && y as object == null;
			if (x.Value.Length != y.Value.Length) return false;
			for (int i = 0; i < x.Value.Length; i++)
				if (x.Value[i] != y.Value[i])
					return false;
			return true;
		}

		public static bool operator !=(OffsetSafeCRCRecord x, OffsetSafeCRCRecord y)
		{
			return !(x == y);
		}

		public bool DifferByOffset(OffsetSafeCRCRecord rec)
		{
			int offset;
			return FindOffset(rec, out offset);
		}

		public bool FindOffset(OffsetSafeCRCRecord rec, out int offset)
		{
			if (this.Value.Length != 128 || rec.Value.Length != 128)
			{
				offset = 0;
				return false;
				//throw new InvalidDataException("Unsupported OffsetSafeCRCRecord");
			}

			for (int i = 0; i < 64; i++)
			{
				if (rec.Value[0] == Value[i])
				{
					offset = i * 64;
					return true;
				}
				if (Value[0] == rec.Value[i])
				{
					offset = -i * 64;
					return true;
				}
				for (int j = 0; j < 64; j++)
				{
					if (rec.Value[i] == Value[64 + j])
					{
						offset = i * 64 + j + 1;
						return true;
					}
					if (Value[i] == rec.Value[64 + j])
					{
						offset = -i * 64 - j - 1;
						return true;
					}
				}
			}
			offset = 0;
			return false;
		}
	}

	public class AccurateRipVerify : IAudioDest
	{
		public AccurateRipVerify(CDImageLayout toc, IWebProxy proxy)
		{
			this.proxy = proxy;
			_accDisks = new List<AccDisk>();
			_hasLogCRC = false;
			_CRCLOG = new uint[toc.AudioTracks + 1];
			ExceptionStatus = WebExceptionStatus.Pending;
			Init(toc);
		}

		public uint Confidence(int iTrack)
		{
			if (ARStatus != null)
				return 0U;
			uint conf = 0;
			int trno = iTrack + _toc.FirstAudio - 1;
			for (int di = 0; di < (int)AccDisks.Count; di++)
				if (trno < AccDisks[di].tracks.Count
					&& (CRC(iTrack) == AccDisks[di].tracks[trno].CRC
					  || CRCV2(iTrack) == AccDisks[di].tracks[trno].CRC))
					conf += AccDisks[di].tracks[iTrack + _toc.FirstAudio - 1].count;
			return conf;
		}

		public uint WorstTotal()
		{
			uint worstTotal = 0xffff;
			for (int iTrack = 0; iTrack < _toc.AudioTracks; iTrack++)
			{
				uint sumTotal = Total(iTrack);
				if (worstTotal > sumTotal && sumTotal != 0)
					worstTotal = sumTotal;
			}
			return worstTotal == 0xffff ? 0 : worstTotal;
		}

		// TODO: Replace min(sum) with sum(min)!!!
		public uint WorstConfidence()
		{
			uint worstConfidence = 0xffff;
			for (int iTrack = 0; iTrack < _toc.AudioTracks; iTrack++)
			{
				uint sumConfidence = SumConfidence(iTrack);
				if (worstConfidence > sumConfidence && (Total(iTrack) != 0 || CRC(iTrack) != 0))
					worstConfidence = sumConfidence;
			}
			return worstConfidence;
		}

		public uint SumConfidence(int iTrack)
		{
			if (ARStatus != null)
				return 0U;
			uint conf = 0;
			int trno = iTrack + _toc.FirstAudio - 1;
			for (int di = 0; di < AccDisks.Count; di++)
				for (int oi = -_arOffsetRange; oi <= _arOffsetRange; oi++)
					if (trno < AccDisks[di].tracks.Count
					&& (CRC(iTrack, oi) == AccDisks[di].tracks[trno].CRC
					  || oi == 0 && CRCV2(iTrack) == AccDisks[di].tracks[trno].CRC))
						conf += AccDisks[di].tracks[iTrack + _toc.FirstAudio - 1].count;
			return conf;
		}

		public uint Confidence(int iTrack, int oi)
		{
			if (ARStatus != null)
				return 0U;
			uint conf = 0;
			int trno = iTrack + _toc.FirstAudio - 1;
			for (int di = 0; di < (int)AccDisks.Count; di++)
				if (trno < AccDisks[di].tracks.Count
					&& (CRC(iTrack, oi) == AccDisks[di].tracks[trno].CRC
					  || oi == 0 && CRCV2(iTrack) == AccDisks[di].tracks[trno].CRC))
					conf += AccDisks[di].tracks[iTrack + _toc.FirstAudio - 1].count;
			return conf;
		}

		public uint Total(int iTrack)
		{
			if (ARStatus != null)
				return 0U;
			uint total = 0;
			for (int di = 0; di < (int)AccDisks.Count; di++)
				if (iTrack + _toc.FirstAudio - 1 < AccDisks[di].tracks.Count)
					total += AccDisks[di].tracks[iTrack + _toc.FirstAudio - 1].count;
			return total;
		}

		public uint DBCRC(int iTrack)
		{
			return ARStatus == null && iTrack + _toc.FirstAudio - 1 < AccDisks[0].tracks.Count
				? AccDisks[0].tracks[iTrack + _toc.FirstAudio - 1].CRC : 0U;
		}

		public uint CRC(int iTrack)
		{
			return CRC(iTrack, 0);
		}

		public uint CRCV2(int iTrack)
		{
			int offs0 = iTrack == 0 ? 5 * 588 - 1 : 0;
			int offs1 = iTrack == _toc.AudioTracks - 1 ? 2 * maxOffset - 5 * 588 : 0;
			uint crcA1 = _CRCAR[iTrack + 1, offs1] - (offs0 > 0 ? _CRCAR[iTrack + 1, offs0] : 0);
			uint crcA2 = _CRCV2[iTrack + 1, offs1] - (offs0 > 0 ? _CRCV2[iTrack + 1, offs0] : 0);
			return crcA1 + crcA2;
		}

		public uint CRC(int iTrack, int oi)
		{
			int offs0 = iTrack == 0 ? 5 * 588 + oi - 1 : oi;
			int offs1 = iTrack == _toc.AudioTracks - 1 ? 2 * maxOffset - 5 * 588 + oi : (oi >= 0 ? 0 : 2 * maxOffset + oi);
			uint crcA = _CRCAR[iTrack + 1, offs1] - (offs0 > 0 ? _CRCAR[iTrack + 1, offs0] : 0);
			uint sumA = _CRCSM[iTrack + 1, offs1] - (offs0 > 0 ? _CRCSM[iTrack + 1, offs0] : 0);
			uint crc = crcA - sumA * (uint)oi;
			if (oi < 0 && iTrack > 0)
			{
				uint crcB = _CRCAR[iTrack, 0] - _CRCAR[iTrack, 2 * maxOffset + oi];
				uint sumB = _CRCSM[iTrack, 0] - _CRCSM[iTrack, 2 * maxOffset + oi];
				uint posB = _toc[iTrack + _toc.FirstAudio - 1].Length * 588 + (uint)oi;
				crc += crcB - sumB * posB;
			}
			if (oi > 0 && iTrack < _toc.AudioTracks - 1)
			{
				uint crcB = _CRCAR[iTrack + 2, oi];
				uint sumB = _CRCSM[iTrack + 2, oi];
				uint posB = _toc[iTrack + _toc.FirstAudio].Length * 588 + (uint)-oi;
				crc += crcB + sumB * posB;
			}
			return crc;
		}

		public uint CRC450(int iTrack, int oi)
		{
			uint crca = _CRCAR[iTrack + 1, 2 * maxOffset + 1 + 5 * 588 + oi];
			uint crcb = _CRCAR[iTrack + 1, 2 * maxOffset + 1 + 6 * 588 + oi];
			uint suma = _CRCSM[iTrack + 1, 2 * maxOffset + 1 + 5 * 588 + oi];
			uint sumb = _CRCSM[iTrack + 1, 2 * maxOffset + 1 + 6 * 588 + oi];
			uint offs = 450 * 588 + (uint)oi;
			return crcb - crca - offs * (sumb - suma);
		}

		public int PeakLevel()
		{
			int peak = 0;
			for (int track = 0; track <= _toc.AudioTracks; track++)
				if (peak < _Peak[track])
					peak = _Peak[track];
			return peak;
		}

		public int PeakLevel(int iTrack)
		{
			return _Peak[iTrack];
		}

		public OffsetSafeCRCRecord OffsetSafeCRC
		{
			get
			{
				return new OffsetSafeCRCRecord(this);
			}
		}

		public uint CRC32(int iTrack)
		{
			return CRC32(iTrack, 0);
		}

		public uint CRC32(int iTrack, int oi)
		{
			if (_CacheCRC32[iTrack, _arOffsetRange + oi] == 0)
			{
				uint crc = 0;
				if (iTrack == 0)
				{
					int dlen = (int)_toc.AudioLength * 588;
					if (oi > 0)
					{
						// whole disc crc
						crc = _CRC32[_toc.AudioTracks, 2 * maxOffset];
						// - prefix
						crc = Crc32.Combine(_CRC32[0, oi], crc, (dlen - oi) * 4);
						// + zero suffix
						crc = Crc32.Combine(crc, 0, oi * 4);
					}
					else // if (oi <= 0)
					{
						crc = _CRC32[_toc.AudioTracks, 2 * maxOffset + oi];
					}

					// Use 0xffffffff as an initial state
					crc ^= _CRCMASK[0];
				}
				else
				{
					int trackLength = (int)(iTrack > 0 ? _toc[iTrack + _toc.FirstAudio - 1].Length : _toc[_toc.FirstAudio].Pregap) * 588 * 4;
					if (oi > 0)
					{
						crc = iTrack < _toc.AudioTracks ? _CRC32[iTrack + 1, oi]
							: Crc32.Combine(_CRC32[iTrack, 2 * maxOffset], 0, oi * 4);
						crc = Crc32.Combine(_CRC32[iTrack, oi], crc, trackLength);
					}
					else //if (oi <= 0)
					{
						crc = Crc32.Combine(_CRC32[iTrack - 1, 2 * maxOffset + oi], _CRC32[iTrack, 2 * maxOffset + oi], trackLength);
					}
					// Use 0xffffffff as an initial state
					crc ^= _CRCMASK[iTrack];
				}
				_CacheCRC32[iTrack, _arOffsetRange + oi] = crc;
			}
			return _CacheCRC32[iTrack, _arOffsetRange + oi];
		}

		public uint CRCWONULL(int iTrack)
		{
			return CRCWONULL(iTrack, 0);
		}

		public uint CRCWONULL(int iTrack, int oi)
		{
			if (_CacheCRCWN[iTrack, _arOffsetRange + oi] == 0)
			{
				uint crc;
				int cnt;
				if (iTrack == 0)
				{
					if (oi > 0)
					{
						// whole disc crc
						cnt = _CRCNL[_toc.AudioTracks, 2 * maxOffset] * 2;
						crc = _CRCWN[_toc.AudioTracks, 2 * maxOffset];
						// - prefix
						cnt -= _CRCNL[0, oi] * 2;
						crc = Crc32.Combine(_CRCWN[0, oi], crc, cnt);
					}
					else // if (oi <= 0)
					{
						cnt = _CRCNL[_toc.AudioTracks, 2 * maxOffset + oi] * 2;
						crc = _CRCWN[_toc.AudioTracks, 2 * maxOffset + oi];
					}
				}
				else
				{
					if (oi > 0)
					{
						cnt = (iTrack < _toc.AudioTracks ? _CRCNL[iTrack + 1, oi] : _CRCNL[iTrack, 2 * maxOffset]) * 2;
						crc = iTrack < _toc.AudioTracks ? _CRCWN[iTrack + 1, oi] : _CRCWN[iTrack, 2 * maxOffset];

						cnt -= _CRCNL[iTrack, oi] * 2;
						crc = Crc32.Combine(_CRCWN[iTrack, oi], crc, cnt);
					}
					else //if (oi <= 0)
					{
						cnt = _CRCNL[iTrack, 2 * maxOffset + oi] * 2;
						crc = _CRCWN[iTrack, 2 * maxOffset + oi];

						cnt -= _CRCNL[iTrack - 1, 2 * maxOffset + oi] * 2;
						crc = Crc32.Combine(_CRCWN[iTrack - 1, 2 * maxOffset + oi], crc, cnt);
					}

				}
				// Use 0xffffffff as an initial state
				crc = Crc32.Combine(0xffffffff, crc, cnt);
				_CacheCRCWN[iTrack, _arOffsetRange + oi] = crc ^ 0xffffffff;
			}
			return _CacheCRCWN[iTrack, _arOffsetRange + oi];
		}

		public uint CRCLOG(int iTrack)
		{
			return _CRCLOG[iTrack];
		}

		public void CRCLOG(int iTrack, uint value)
		{
			_hasLogCRC = true;
			_CRCLOG[iTrack] = value;
		}

		internal ushort[,] syndrome;
		internal byte[] parity;
		internal ushort[, ,] encodeTable;
		internal ushort[, ,] decodeTable;
		private int maxOffset;
		internal ushort[] leadin;
		internal ushort[] leadout;
		private int stride = 1, laststride = 1, stridecount = 1, npar = 1;
		private bool calcSyn = false;
		private bool calcParity = false;

		internal void InitCDRepair(int stride, int laststride, int stridecount, int npar, bool calcSyn, bool calcParity)
		{
			if (npar != 8)
				throw new ArgumentOutOfRangeException("npar");
			if (stride % 2 != 0 || laststride % 2 != 0)
				throw new ArgumentOutOfRangeException("stride");
			this.stride = stride;
			this.laststride = laststride;
			this.stridecount = stridecount;
			this.npar = npar;
			this.calcSyn = calcSyn;
			this.calcParity = calcParity;
			Init(_toc);
		}

		public unsafe uint CTDBCRC(int iTrack, int oi, int prefixSamples, int suffixSamples)
		{
			prefixSamples += oi;
			suffixSamples -= oi;
			if (prefixSamples < 0 || prefixSamples >= maxOffset || suffixSamples < 0 || suffixSamples > maxOffset)
				throw new ArgumentOutOfRangeException();

			uint crc;
			if (iTrack == 0)
			{
				int discLen = (int)_toc.AudioLength * 588;
				int chunkLen = discLen - prefixSamples - suffixSamples;
				crc = Crc32.Combine(
					_CRC32[0, prefixSamples],
					_CRC32[_toc.AudioTracks, 2 * maxOffset - suffixSamples],
					chunkLen * 4);
				return Crc32.Combine(0xffffffff, crc, chunkLen * 4) ^ 0xffffffff;
			}
			int posA = (int)_toc[iTrack + _toc.FirstAudio - 1].Start * 588 + (iTrack > 1 ? oi : prefixSamples);
			int posB = iTrack < _toc.AudioTracks ?
				(int)_toc[iTrack + 1 + _toc.FirstAudio - 1].Start * 588 + oi :
				(int)_toc.AudioLength * 588 - suffixSamples;
			uint crcA, crcB;
			if (oi > 0)
			{
				crcA = iTrack > 1 ?
					_CRC32[iTrack, oi] :
					_CRC32[iTrack, prefixSamples];
				crcB = iTrack < _toc.AudioTracks ?
					_CRC32[iTrack + 1, oi] :
					_CRC32[iTrack, maxOffset * 2 - suffixSamples];
			}
			else //if (oi <= 0)
			{
				crcA = iTrack > 1 ?
					_CRC32[iTrack - 1, maxOffset * 2 + oi] :
					_CRC32[iTrack, prefixSamples];
				crcB = iTrack < _toc.AudioTracks ?
					_CRC32[iTrack, maxOffset * 2 + oi] :
					_CRC32[iTrack, maxOffset * 2 - suffixSamples];
			}
			crc = Crc32.Combine(crcA, crcB, (posB - posA) * 4);
			// Use 0xffffffff as an initial state
			crc = Crc32.Combine(0xffffffff, crc, (posB - posA) * 4) ^ 0xffffffff;
			return crc;
		}

		public uint CTDBCRC(int offset)
		{
			return CTDBCRC(0, offset, stride / 2, laststride / 2);
		}

		private unsafe static void CalcSyn8(ushort* pt, ushort* syn, uint lo, uint hi)
		{
			syn[0] ^= (ushort)lo;
			syn[1] = (ushort)(lo ^ pt[(syn[1] & 255) * 16 + 1] ^ pt[(syn[1] >> 8) * 16 + 1 + 8]);
			syn[2] = (ushort)(lo ^ pt[(syn[2] & 255) * 16 + 2] ^ pt[(syn[2] >> 8) * 16 + 2 + 8]);
			syn[3] = (ushort)(lo ^ pt[(syn[3] & 255) * 16 + 3] ^ pt[(syn[3] >> 8) * 16 + 3 + 8]);
			syn[4] = (ushort)(lo ^ pt[(syn[4] & 255) * 16 + 4] ^ pt[(syn[4] >> 8) * 16 + 4 + 8]);
			syn[5] = (ushort)(lo ^ pt[(syn[5] & 255) * 16 + 5] ^ pt[(syn[5] >> 8) * 16 + 5 + 8]);
			syn[6] = (ushort)(lo ^ pt[(syn[6] & 255) * 16 + 6] ^ pt[(syn[6] >> 8) * 16 + 6 + 8]);
			syn[7] = (ushort)(lo ^ pt[(syn[7] & 255) * 16 + 7] ^ pt[(syn[7] >> 8) * 16 + 7 + 8]);
			syn[8] ^= (ushort)hi;
			syn[9] = (ushort)(hi ^ pt[(syn[9] & 255) * 16 + 1] ^ pt[(syn[9] >> 8) * 16 + 1 + 8]);
			syn[10] = (ushort)(hi ^ pt[(syn[10] & 255) * 16 + 2] ^ pt[(syn[10] >> 8) * 16 + 2 + 8]);
			syn[11] = (ushort)(hi ^ pt[(syn[11] & 255) * 16 + 3] ^ pt[(syn[11] >> 8) * 16 + 3 + 8]);
			syn[12] = (ushort)(hi ^ pt[(syn[12] & 255) * 16 + 4] ^ pt[(syn[12] >> 8) * 16 + 4 + 8]);
			syn[13] = (ushort)(hi ^ pt[(syn[13] & 255) * 16 + 5] ^ pt[(syn[13] >> 8) * 16 + 5 + 8]);
			syn[14] = (ushort)(hi ^ pt[(syn[14] & 255) * 16 + 6] ^ pt[(syn[14] >> 8) * 16 + 6 + 8]);
			syn[15] = (ushort)(hi ^ pt[(syn[15] & 255) * 16 + 7] ^ pt[(syn[15] >> 8) * 16 + 7 + 8]);
		}

		private unsafe static void CalcPar8(ushort* pt, ushort* wr, uint lo, uint hi)
		{
#if !sdfs
			uint wrlo = wr[0] ^ lo;
			uint wrhi = wr[8] ^ hi;
			ushort* ptiblo0 = pt + (wrlo & 255) * 16;
			ushort* ptiblo1 = pt + (wrlo >> 8) * 16 + 8;
			ushort* ptibhi0 = pt + (wrhi & 255) * 16;
			ushort* ptibhi1 = pt + (wrhi >> 8) * 16 + 8;
			wr[8] = 0;
			((ulong*)wr)[0] = ((ulong*)(wr + 1))[0] ^ ((ulong*)ptiblo0)[0] ^ ((ulong*)ptiblo1)[0];
			((ulong*)wr)[1] = ((ulong*)(wr + 1))[1] ^ ((ulong*)ptiblo0)[1] ^ ((ulong*)ptiblo1)[1];
			((ulong*)wr)[2] = ((ulong*)(wr + 1))[2] ^ ((ulong*)ptibhi0)[0] ^ ((ulong*)ptibhi1)[0];
			((ulong*)wr)[3] = (((ulong*)(wr))[3] >> 16) ^ ((ulong*)ptibhi0)[1] ^ ((ulong*)ptibhi1)[1];
#else
			const int npar = 8;
			ushort* ptiblo = pt + (wr[0] ^ lo) * npar;
			ushort* ptibhi = pt + (wr[npar] ^ hi) * npar;
			for (int i = 0; i < npar - 1; i++)
			{
				wr[i] = (ushort)(wr[i + 1] ^ ptiblo[i]);
				wr[npar + i] = (ushort)(wr[i + 1 + npar] ^ ptibhi[i]);
			}
			wr[npar - 1] = ptiblo[npar - 1];
			wr[2 * npar - 1] = ptibhi[npar - 1];
#endif
		}


		/// <summary>
		/// This function calculates three different CRCs and also 
		/// collects some additional information for the purposes of 
		/// offset detection.
		/// 
		/// crcar is AccurateRip CRC
		/// crc32 is CRC32
		/// crcwn is CRC32 without null samples (EAC)
		/// crcsm is sum of samples
		/// crcnl is a count of null samples
		/// </summary>
		/// <param name="pSampleBuff"></param>
		/// <param name="count"></param>
		/// <param name="offs"></param>
		public unsafe void CalculateCRCs(uint* t, ushort* syn, ushort* wr, ushort* pte, ushort* ptd, uint* pSampleBuff, int count, int offs)
		{
			int currentStride = ((int)_sampleCount * 2) / stride;
			bool doSyn = currentStride >= 1 && currentStride <= stridecount && calcSyn;
			bool doPar = currentStride >= 1 && currentStride <= stridecount && calcParity;
			uint n = (uint)(stridecount - currentStride);

			int crcTrack = _currentTrack + (_samplesDoneTrack == 0 && _currentTrack > 0 ? -1 : 0);
			uint crcar = _CRCAR[_currentTrack, 0];
			uint crcsm = _CRCSM[_currentTrack, 0];
			uint crc32 = _CRC32[crcTrack, 2 * maxOffset];
			uint crcwn = _CRCWN[crcTrack, 2 * maxOffset];
			int crcnl = _CRCNL[crcTrack, 2 * maxOffset];
			uint crcv2 = _CRCV2[_currentTrack, 0];
			int peak = _Peak[_currentTrack];

			for (int i = 0; i < count; i++)
			{
				if (offs >= 0)
				{
					_CRCAR[_currentTrack, offs + i] = crcar;
					_CRCSM[_currentTrack, offs + i] = crcsm;
					_CRC32[_currentTrack, offs + i] = crc32;
					_CRCWN[_currentTrack, offs + i] = crcwn;
					_CRCNL[_currentTrack, offs + i] = crcnl;
					_CRCV2[_currentTrack, offs + i] = crcv2;
				}

				uint sample = *(pSampleBuff++);
				crcsm += sample;
				ulong calccrc = sample * (ulong)(_samplesDoneTrack + i + 1);
				crcar += (uint)calccrc;
				crcv2 += (uint)(calccrc >> 32);

				uint lo = sample & 0xffff;
				crc32 = (crc32 >> 8) ^ t[(byte)(crc32 ^ lo)];
				crc32 = (crc32 >> 8) ^ t[(byte)(crc32 ^ (lo >> 8))];
				if (lo != 0)
				{
					crcwn = (crcwn >> 8) ^ t[(byte)(crcwn ^ lo)];
					crcwn = (crcwn >> 8) ^ t[(byte)(crcwn ^ (lo >> 8))];
					crcnl++;
				}

				uint hi = sample >> 16;
				
				if (doSyn) CalcSyn8(ptd, syn + i * 16, lo, hi);
				if (doPar) CalcPar8(pte, wr + i * 16, lo, hi);

				crc32 = (crc32 >> 8) ^ t[(byte)(crc32 ^ hi)];
				crc32 = (crc32 >> 8) ^ t[(byte)(crc32 ^ (hi >> 8))];
				if (hi != 0)
				{
					crcwn = (crcwn >> 8) ^ t[(byte)(crcwn ^ hi)];
					crcwn = (crcwn >> 8) ^ t[(byte)(crcwn ^ (hi >> 8))];
					crcnl++;
				}

				int pk = ((int)(lo << 16)) >> 16;
				peak = Math.Max(peak, (pk << 1) ^ (pk >> 31));
				pk = ((int)(hi << 16)) >> 16;
				peak = Math.Max(peak, (pk << 1) ^ (pk >> 31));
			}

			_CRCAR[_currentTrack, 0] = crcar;
			_CRCSM[_currentTrack, 0] = crcsm;
			_CRC32[_currentTrack, 2 * maxOffset] = crc32;
			_CRCWN[_currentTrack, 2 * maxOffset] = crcwn;
			_CRCNL[_currentTrack, 2 * maxOffset] = crcnl;
			_CRCV2[_currentTrack, 0] = crcv2;
			_Peak[_currentTrack] = peak;
		}

		private int _samplesRemTrack = 0;
		private int _samplesDoneTrack = 0;

		public long Position
		{
			get
			{
				return _sampleCount;
			}
			set
			{
				_sampleCount = value;
				int tempLocation = 0; // NOT (int)_toc[_toc.FirstAudio][0].Start * 588;
				for (int iTrack = 0; iTrack <= _toc.AudioTracks; iTrack++)
				{
					int tempLen = (int)(iTrack == 0 ? _toc[_toc.FirstAudio].Pregap : _toc[_toc.FirstAudio + iTrack - 1].Length) * 588;
					if (tempLocation + tempLen > _sampleCount)
					{
						_currentTrack = iTrack;
						_samplesRemTrack = tempLocation + tempLen - (int)_sampleCount;
						_samplesDoneTrack = (int)_sampleCount - tempLocation;
						return;
					}
					tempLocation += tempLen;
				}
				throw new ArgumentOutOfRangeException();
			}
		}

		public unsafe void Write(AudioBuffer sampleBuffer)
		{
			sampleBuffer.Prepare(this);

			int pos = 0;
			fixed (uint* t = Crc32.table)
			fixed (ushort* synptr1 = syndrome, pte = encodeTable, ptd = decodeTable)
			fixed (byte* pSampleBuff = &sampleBuffer.Bytes[0], bpar = parity)
				while (pos < sampleBuffer.Length)
				{
					// Process no more than there is in the buffer, no more than there is in this track, and no more than up to a sector boundary.
					int copyCount = Math.Min(Math.Min(sampleBuffer.Length - pos, (int)_samplesRemTrack), 588 - (int)_sampleCount % 588);
					uint* samples = ((uint*)pSampleBuff) + pos;
					int currentPart = ((int)_sampleCount * 2) % stride;
					ushort* synptr = synptr1 + npar * currentPart;
					ushort* wr = ((ushort*)bpar) + npar * currentPart;
					int currentStride = ((int)_sampleCount * 2) / stride;

					for (int i = 0; i < Math.Min(leadin.Length - (int)_sampleCount * 2, copyCount * 2); i++)
						leadin[_sampleCount * 2 + i] = ((ushort*)samples)[i];

					for (int i = Math.Max(0, (int)(_finalSampleCount - _sampleCount) * 2 - leadout.Length); i < copyCount * 2; i++)
					//if (currentStride >= stridecount && leadout != null)
					//for (int i = 0; i < copyCount * 2; i++)
					{
						int remaining = (int)(_finalSampleCount - _sampleCount) * 2 - i - 1;
						leadout[remaining] = ((ushort*)samples)[i];
					}

					int offset = _samplesDoneTrack < maxOffset ? _samplesDoneTrack
						: _samplesRemTrack <= maxOffset ? 2 * maxOffset - _samplesRemTrack
						: _samplesDoneTrack >= 445 * 588 && _samplesDoneTrack <= 455 * 588 ? 2 * maxOffset + 1 + _samplesDoneTrack - 445 * 588
						: -1;

					CalculateCRCs(t, synptr, wr, pte, ptd, samples, copyCount, offset);

					// duplicate prefix to suffix
					if (_samplesDoneTrack < maxOffset && _samplesRemTrack <= maxOffset)
					{
						Array.Copy(_CRC32, _currentTrack * 3 * maxOffset + _samplesDoneTrack,
							_CRC32, _currentTrack * 3 * maxOffset + 2 * maxOffset - _samplesRemTrack,
							copyCount);
						Array.Copy(_CRCWN, _currentTrack * 3 * maxOffset + _samplesDoneTrack,
							_CRCWN, _currentTrack * 3 * maxOffset + 2 * maxOffset - _samplesRemTrack,
							copyCount);
						Array.Copy(_CRCNL, _currentTrack * 3 * maxOffset + _samplesDoneTrack,
							_CRCNL, _currentTrack * 3 * maxOffset + 2 * maxOffset - _samplesRemTrack,
							copyCount);
					}
					// duplicate prefix to pregap
					if (_sampleCount < maxOffset && _currentTrack == 1)
					{
						Array.Copy(_CRC32, _currentTrack * 3 * maxOffset + _samplesDoneTrack,
							_CRC32, _sampleCount,
							copyCount);
						Array.Copy(_CRCWN, _currentTrack * 3 * maxOffset + _samplesDoneTrack,
							_CRCWN, _sampleCount,
							copyCount);
						Array.Copy(_CRCNL, _currentTrack * 3 * maxOffset + _samplesDoneTrack,
							_CRCNL, _sampleCount,
							copyCount);
					}

					pos += copyCount;
					_samplesRemTrack -= copyCount;
					_samplesDoneTrack += copyCount;
					_sampleCount += copyCount;

					while (_samplesRemTrack <= 0)
					{
						if (++_currentTrack > _toc.AudioTracks)
							return;
						_samplesRemTrack = (int)_toc[_currentTrack + _toc.FirstAudio - 1].Length * 588;
						_samplesDoneTrack = 0;
					}
				}
		}

		public void Combine(AccurateRipVerify part, int start, int end)
		{
			for (int i = 0; i < leadin.Length; i++)
			{
				int currentOffset = i / 2;
				if (currentOffset >= start && currentOffset < end)
					this.leadin[i] = part.leadin[i];
			}
			for (int i = 0; i < leadout.Length; i++)
			{
				int currentOffset = (int)_finalSampleCount - i / 2 - 1;
				if (currentOffset >= start && currentOffset < end)
					this.leadout[i] = part.leadout[i];
			}
			int iSplitTrack = -1;
			for (int iTrack = 0; iTrack <= _toc.AudioTracks; iTrack++)
			{
				int tempLocation = (int)(iTrack == 0 ? 0 : _toc[_toc.FirstAudio + iTrack - 1].Start - _toc[_toc.FirstAudio][0].Start) * 588;
				int tempLen = (int)(iTrack == 0 ? _toc[_toc.FirstAudio].Pregap : _toc[_toc.FirstAudio + iTrack - 1].Length) * 588;
				if (start > tempLocation && start <= tempLocation + tempLen)
				{
					iSplitTrack = iTrack;
					break;
				}
			}

			uint crc32 = _CRC32[iSplitTrack, 2 * maxOffset];
			uint crcwn = _CRCWN[iSplitTrack, 2 * maxOffset];
			int crcnl = _CRCNL[iSplitTrack, 2 * maxOffset];

			for (int iTrack = 0; iTrack <= _toc.AudioTracks; iTrack++)
			{
				// ??? int tempLocation = (int) (iTrack == 0 ? _toc[_toc.FirstAudio][0].Start : _toc[_toc.FirstAudio + iTrack - 1].Start) * 588;
				int tempLocation = (int)(iTrack == 0 ? 0 : _toc[_toc.FirstAudio + iTrack - 1].Start - _toc[_toc.FirstAudio][0].Start) * 588;
				int tempLen = (int)(iTrack == 0 ? _toc[_toc.FirstAudio].Pregap : _toc[_toc.FirstAudio + iTrack - 1].Length) * 588;
				int trStart = Math.Max(tempLocation, start);
				int trEnd = Math.Min(tempLocation + tempLen, end);
				if (trStart >= trEnd)
					continue;

				uint crcar = _CRCAR[iTrack, 0];
				uint crcv2 = _CRCV2[iTrack, 0];
				uint crcsm = _CRCSM[iTrack, 0];
				_CRCAR[iTrack, 0] = crcar + part._CRCAR[iTrack, 0];
				_CRCSM[iTrack, 0] = crcsm + part._CRCSM[iTrack, 0];
				_CRCV2[iTrack, 0] = crcv2 + part._CRCV2[iTrack, 0];

				for (int i = 0; i < 3 * maxOffset; i++)
				{
					int currentOffset;
					if (i < maxOffset)
						currentOffset = tempLocation + i;
					else if (i < 2 * maxOffset)
						currentOffset = tempLocation + tempLen + i - 2 * maxOffset;
					else if (i == 2 * maxOffset)
						currentOffset = trEnd;
					else //if (i > 2 * maxOffset)
						currentOffset = tempLocation + i - 1 - 2 * maxOffset + 445 * 588;

					if (currentOffset < trStart || currentOffset > trEnd)
						continue;

					_CRC32[iTrack, i] = Crc32.Combine(crc32, part._CRC32[iTrack, i], 4 * (currentOffset - start));
					_CRCWN[iTrack, i] = Crc32.Combine(crcwn, part._CRCWN[iTrack, i], part._CRCNL[iTrack, i] * 2);
					_CRCNL[iTrack, i] = crcnl + part._CRCNL[iTrack, i];
					if (i == 0 || i == 2 * maxOffset) continue;
					_CRCAR[iTrack, i] = crcar + part._CRCAR[iTrack, i];
					_CRCV2[iTrack, i] = crcv2 + part._CRCV2[iTrack, i];
					_CRCSM[iTrack, i] = crcsm + part._CRCSM[iTrack, i];
				}
				_Peak[iTrack] = Math.Max(_Peak[iTrack], part._Peak[iTrack]);
			}
		}

		public void Init(CDImageLayout toc)
		{
			_toc = toc;
			_finalSampleCount = _toc.AudioLength * 588;
			_CRCMASK = new uint[_toc.AudioTracks + 1];
			_CRCMASK[0] = 0xffffffff ^ Crc32.Combine(0xffffffff, 0, (int)_finalSampleCount * 4);
			for (int iTrack = 1; iTrack <= _toc.AudioTracks; iTrack++)
				_CRCMASK[iTrack] = 0xffffffff ^ Crc32.Combine(0xffffffff, 0, (int)_toc[iTrack + _toc.FirstAudio - 1].Length * 588 * 4);

			maxOffset = Math.Max(4096 * 2, (calcSyn || calcParity) ? stride + laststride : 0);
			if (maxOffset % 588 != 0)
				maxOffset += 588 - maxOffset % 588;
			_CRCAR = new uint[_toc.AudioTracks + 1, 3 * maxOffset];
			_CRCSM = new uint[_toc.AudioTracks + 1, 3 * maxOffset];
			_CRC32 = new uint[_toc.AudioTracks + 1, 3 * maxOffset];
			_CacheCRC32 = new uint[_toc.AudioTracks + 1, 3 * maxOffset];
			_CRCWN = new uint[_toc.AudioTracks + 1, 3 * maxOffset];
			_CacheCRCWN = new uint[_toc.AudioTracks + 1, 3 * maxOffset];
			_CRCNL = new int[_toc.AudioTracks + 1, 3 * maxOffset];
			_CRCV2 = new uint[_toc.AudioTracks + 1, 3 * maxOffset];

			_Peak = new int[_toc.AudioTracks + 1];
			syndrome = new ushort[calcSyn ? stride : 1, npar];
			parity = new byte[stride * npar * 2];
			if (calcParity && npar == 8)
				encodeTable = Galois16.instance.makeEncodeTable(npar);
			if (calcSyn && npar == 8)
				decodeTable = Galois16.instance.makeDecodeTable(npar);

			int leadin_len = Math.Max(4096 * 4, (calcSyn || calcParity) ? stride * 2 : 0);
			int leadout_len = Math.Max(4096 * 4, (calcSyn || calcParity) ? stride + laststride : 0);
			leadin = new ushort[leadin_len];
			leadout = new ushort[leadout_len];
			_currentTrack = 0;
			Position = 0; // NOT _toc[_toc.FirstAudio][0].Start * 588;
		}

		private uint readIntLE(byte[] data, int pos)
		{
			return (uint)(data[pos] + (data[pos + 1] << 8) + (data[pos + 2] << 16) + (data[pos + 3] << 24));
		}

		static DateTime last_accessed;
		static readonly TimeSpan min_interval = new TimeSpan(5000000); // 0.5 second
		static readonly object server_mutex = new object();

		public void ContactAccurateRip(string accurateRipId)
		{
			// Calculate the three disc ids used by AR
			uint discId1 = 0;
			uint discId2 = 0;
			uint cddbDiscId = 0;

			string[] n = accurateRipId.Split('-');
			if (n.Length != 3)
			{
				ExceptionStatus = WebExceptionStatus.RequestCanceled;
				throw new Exception("Invalid accurateRipId.");
			}
			discId1 = UInt32.Parse(n[0], NumberStyles.HexNumber);
			discId2 = UInt32.Parse(n[1], NumberStyles.HexNumber);
			cddbDiscId = UInt32.Parse(n[2], NumberStyles.HexNumber);

			string url = String.Format("http://www.accuraterip.com/accuraterip/{0:x}/{1:x}/{2:x}/dBAR-{3:d3}-{4:x8}-{5:x8}-{6:x8}.bin",
				discId1 & 0xF, discId1 >> 4 & 0xF, discId1 >> 8 & 0xF, _toc.AudioTracks, discId1, discId2, cddbDiscId);

			HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
			req.Method = "GET";
			req.Proxy = proxy;

			lock (server_mutex)
			{
				// Don't access the AR server twice within min_interval
				if (last_accessed != null)
				{
					TimeSpan time = DateTime.Now - last_accessed;
					if (min_interval > time)
						Thread.Sleep((min_interval - time).Milliseconds);
				}

				try
				{
					using (HttpWebResponse response = (HttpWebResponse)req.GetResponse())
					{
						ExceptionStatus = WebExceptionStatus.ProtocolError;
						ResponseStatus = response.StatusCode;
						if (ResponseStatus == HttpStatusCode.OK)
						{
							ExceptionStatus = WebExceptionStatus.Success;

							// Retrieve response stream and wrap in StreamReader
							Stream respStream = response.GetResponseStream();

							// Allocate byte buffer to hold stream contents
							byte[] urlData = new byte[13];
							int urlDataLen, bytesRead;

							_accDisks.Clear();
							while (true)
							{
								for (urlDataLen = 0; urlDataLen < 13; urlDataLen += bytesRead)
								{
									bytesRead = respStream.Read(urlData, urlDataLen, 13 - urlDataLen);
									if (0 == bytesRead)
										break;
								}
								if (urlDataLen == 0)
									break;
								if (urlDataLen < 13)
								{
									ExceptionStatus = WebExceptionStatus.ReceiveFailure;
									return;
								}
								AccDisk dsk = new AccDisk();
								dsk.count = urlData[0];
								dsk.discId1 = readIntLE(urlData, 1);
								dsk.discId2 = readIntLE(urlData, 5);
								dsk.cddbDiscId = readIntLE(urlData, 9);

								for (int i = 0; i < dsk.count; i++)
								{
									for (urlDataLen = 0; urlDataLen < 9; urlDataLen += bytesRead)
									{
										bytesRead = respStream.Read(urlData, urlDataLen, 9 - urlDataLen);
										if (0 == bytesRead)
										{
											ExceptionStatus = WebExceptionStatus.ReceiveFailure;
											return;
										}
									}
									AccTrack trk = new AccTrack();
									trk.count = urlData[0];
									trk.CRC = readIntLE(urlData, 1);
									trk.Frame450CRC = readIntLE(urlData, 5);
									dsk.tracks.Add(trk);
								}
								_accDisks.Add(dsk);
							}
							respStream.Close();
						}
					}
				}
				catch (WebException ex)
				{
					ExceptionStatus = ex.Status;
					ExceptionMessage = ex.Message;
					if (ExceptionStatus == WebExceptionStatus.ProtocolError)
						ResponseStatus = (ex.Response as HttpWebResponse).StatusCode;
				}
				finally
				{
					last_accessed = DateTime.Now;
				}
			}
		}

		public void Close()
		{
			if (_sampleCount != _finalSampleCount)
				throw new Exception("_sampleCount != _finalSampleCount");
		}

		public void Delete()
		{
			throw new Exception("unsupported");
		}

		public int CompressionLevel
		{
			get { return 0; }
			set { }
		}

		public object Settings
		{
			get
			{
				return null;
			}
			set
			{
				if (value != null && value.GetType() != typeof(object))
					throw new Exception("Unsupported options " + value);
			}
		}

		public long Padding
		{
			set { }
		}

		public AudioPCMConfig PCM
		{
			get { return AudioPCMConfig.RedBook; }
		}

		public long FinalSampleCount
		{
			get
			{
				return _finalSampleCount;
			}
			set
			{
				if (value != _finalSampleCount)
					throw new Exception("invalid FinalSampleCount");
			}
		}

		public long BlockSize
		{
			set { throw new Exception("unsupported"); }
		}

		public string Path
		{
			get { throw new Exception("unsupported"); }
		}

		public void GenerateLog(TextWriter sw, int oi, bool v2)
		{
			uint maxTotal = 0;
			for (int iTrack = 0; iTrack < _toc.AudioTracks; iTrack++)
				maxTotal = Math.Max(maxTotal, Total(iTrack));

			uint maxConf = 0;
			for (int iTrack = 0; iTrack < _toc.AudioTracks; iTrack++)
			{
				uint crcOI = v2 ? CRCV2(iTrack) : CRC(iTrack, oi);
				for (int di = 0; di < (int)AccDisks.Count; di++)
				{
					int trno = iTrack + _toc.FirstAudio - 1;
					if (trno < AccDisks[di].tracks.Count
						&& crcOI == AccDisks[di].tracks[trno].CRC
						&& 0 != AccDisks[di].tracks[trno].CRC
						)
						maxConf = Math.Max(maxConf, AccDisks[di].tracks[trno].count);
				}
			}
			if (maxConf == 0 && v2)
				return;
			if (v2)
				sw.WriteLine("AccurateRip v2:");
			string ifmt = maxTotal < 10 ? ":0" : maxTotal < 100 ? ":00" : ":000";
			//string ifmt = maxTotal < 10 ? ",1" : maxTotal < 100 ? ",2" : ",3";
			for (int iTrack = 0; iTrack < _toc.AudioTracks; iTrack++)
			{
				uint count = 0;
				uint partials = 0;
				uint conf = 0;
				uint crcOI = v2 ? CRCV2(iTrack) : CRC(iTrack, oi);
				uint crc450OI = CRC450(iTrack, oi);
				for (int di = 0; di < (int)AccDisks.Count; di++)
				{
					int trno = iTrack + _toc.FirstAudio - 1;
					if (trno >= AccDisks[di].tracks.Count)
						continue;
					count += AccDisks[di].tracks[trno].count;
					if (crcOI == AccDisks[di].tracks[trno].CRC
						&& 0 != AccDisks[di].tracks[trno].CRC)
						conf += AccDisks[di].tracks[trno].count;
					if (crc450OI == AccDisks[di].tracks[trno].Frame450CRC
						&& 0 != AccDisks[di].tracks[trno].Frame450CRC)
						partials++;
				}
				string status;
				if (conf > 0)
					status = "Accurately ripped";
				else if (count == 0 && crcOI == 0)
					status = "Silent track";
				else if (partials > 0)
					status = "No match but offset";
				else
					status = "No match";
				sw.WriteLine(String.Format(" {0:00}     [{1:x8}] ({3" + ifmt + "}/{2" + ifmt + "}) {4}", iTrack + 1, crcOI, count, conf, status));
			}
		}

		public void GenerateFullLog(TextWriter sw, bool verbose, string id)
		{
			sw.WriteLine("[AccurateRip ID: {0}] {1}.", id, ARStatus ?? "found");
			if (ExceptionStatus == WebExceptionStatus.Success)
			{
				if (verbose)
				{
					sw.WriteLine("Track   [ CRC    ] Status");
					GenerateLog(sw, 0, false);
					GenerateLog(sw, 0, true);
					uint offsets_match = 0;
					for (int oi = -_arOffsetRange; oi <= _arOffsetRange; oi++)
					{
						uint matches = 0;
						for (int iTrack = 0; iTrack < _toc.AudioTracks; iTrack++)
						{
							int trno = iTrack + _toc.FirstAudio - 1;
							for (int di = 0; di < (int)AccDisks.Count; di++)
								if (trno < AccDisks[di].tracks.Count
									&& (CRC(iTrack, oi) == AccDisks[di].tracks[trno].CRC
									&& AccDisks[di].tracks[trno].CRC != 0))
								{
									matches++;
									break;
								}
						}
						if (matches == _toc.AudioTracks && oi != 0)
						{
							if (offsets_match++ > 16)
							{
								sw.WriteLine("More than 16 offsets match!");
								break;
							}
							sw.WriteLine("Offsetted by {0}:", oi);
							GenerateLog(sw, oi, false);
						}
					}
					offsets_match = 0;
					for (int oi = -_arOffsetRange; oi <= _arOffsetRange; oi++)
					{
						uint matches = 0, partials = 0;
						for (int iTrack = 0; iTrack < _toc.AudioTracks; iTrack++)
						{
							uint crcOI = CRC(iTrack, oi);
							uint crc450OI = CRC450(iTrack, oi);
							for (int di = 0; di < (int)AccDisks.Count; di++)
							{
								int trno = iTrack + _toc.FirstAudio - 1;
								if (trno >= AccDisks[di].tracks.Count)
									continue;
								if (crcOI == AccDisks[di].tracks[trno].CRC
									&& AccDisks[di].tracks[trno].CRC != 0)
								{
									matches++;
									break;
								}
								if (crc450OI == AccDisks[di].tracks[trno].Frame450CRC
									&& AccDisks[di].tracks[trno].Frame450CRC != 0)
									partials++;
							}
						}
						if (matches != _toc.AudioTracks && oi != 0 && matches + partials != 0)
						{
							if (offsets_match++ > 16)
							{
								sw.WriteLine("More than 16 offsets match!");
								break;
							}
							sw.WriteLine("Offsetted by {0}:", oi);
							GenerateLog(sw, oi, false);
						}
					}
				}
				else
				{
					sw.WriteLine("Track    Status");
					for (int iTrack = 0; iTrack < _toc.AudioTracks; iTrack++)
					{
						uint total = Total(iTrack);
						uint conf = 0;
						bool zeroOffset = false;
						StringBuilder pressings = new StringBuilder();
						for (int oi = -_arOffsetRange; oi <= _arOffsetRange; oi++)
							for (int iDisk = 0; iDisk < AccDisks.Count; iDisk++)
							{
								int trno = iTrack + _toc.FirstAudio - 1;
								if (trno < AccDisks[iDisk].tracks.Count
									&& (CRC(iTrack, oi) == AccDisks[iDisk].tracks[trno].CRC
									  || oi == 0 && CRCV2(iTrack) == AccDisks[iDisk].tracks[trno].CRC
									  )
									&& (AccDisks[iDisk].tracks[trno].CRC != 0 || oi == 0))
								{
									conf += AccDisks[iDisk].tracks[trno].count;
									if (oi == 0)
										zeroOffset = true;
									pressings.AppendFormat("{0}{1}({2})", pressings.Length > 0 ? "," : "", oi, AccDisks[iDisk].tracks[trno].count);
								}
							}
						if (conf > 0 && zeroOffset && pressings.Length == 0)
							sw.WriteLine(String.Format(" {0:00}      ({2:00}/{1:00}) Accurately ripped", iTrack + 1, total, conf));
						else if (conf > 0 && zeroOffset)
							sw.WriteLine(String.Format(" {0:00}      ({2:00}/{1:00}) Accurately ripped, all offset(s) {3}", iTrack + 1, total, conf, pressings));
						else if (conf > 0)
							sw.WriteLine(String.Format(" {0:00}      ({2:00}/{1:00}) Accurately ripped with offset(s) {3}", iTrack + 1, total, conf, pressings));
						else if (total > 0)
							sw.WriteLine(String.Format(" {0:00}      (00/{1:00}) NOT ACCURATE", iTrack + 1, total));
						else
							sw.WriteLine(String.Format(" {0:00}      (00/00) Track not present in database", iTrack + 1));
					}
				}
			}
			if (CRC32(0) != 0 && (_hasLogCRC || verbose))
			{
				sw.WriteLine("");
				sw.WriteLine("Track Peak [ CRC32  ] [W/O NULL] {0:10}", _hasLogCRC ? "[  LOG   ]" : "");
				for (int iTrack = 0; iTrack <= _toc.AudioTracks; iTrack++)
				{
					string inLog, extra = "";
					if (CRCLOG(iTrack) == 0)
						inLog = "";
					else if (CRCLOG(iTrack) == CRC32(iTrack))
						inLog = "  CRC32   ";
					else if (CRCLOG(iTrack) == CRCWONULL(iTrack))
						inLog = " W/O NULL ";
					else
					{
						inLog = String.Format("[{0:X8}]", CRCLOG(iTrack));
						for (int jTrack = 1; jTrack <= _toc.AudioTracks; jTrack++)
						{
							if (CRCLOG(iTrack) == CRC32(jTrack))
							{
								extra = string.Format(": CRC32 for track {0}", jTrack);
								break;
							}
							if (CRCLOG(iTrack) == CRCWONULL(jTrack))
							{
								extra = string.Format(": W/O NULL for track {0}", jTrack);
								break;
							}
						}
						if (extra == "")
							for (int oi = -_arOffsetRange; oi <= _arOffsetRange; oi++)
								if (CRCLOG(iTrack) == CRC32(iTrack, oi))
								{
									inLog = "  CRC32   ";
									extra = string.Format(": offset {0}", oi);
									break;
								}
						if (extra == "")
							for (int oi = -_arOffsetRange; oi <= _arOffsetRange; oi++)
								if (CRCLOG(iTrack) == CRCWONULL(iTrack, oi))
								{
									inLog = " W/O NULL ";
									if (extra == "")
										extra = string.Format(": offset {0}", oi);
									else
									{
										extra = string.Format(": with offset");
										break;
									}
								}
					}
					sw.WriteLine(" {0}  {5,5:F1} [{1:X8}] [{2:X8}] {3,10}{4}",
						iTrack == 0 ? "--" : string.Format("{0:00}", iTrack),
						CRC32(iTrack),
						CRCWONULL(iTrack),
						inLog,
						extra,
						((iTrack == 0 ? PeakLevel() : PeakLevel(iTrack)) * 1000 / 65534) * 0.1);
				}
			}
		}

		private static uint sumDigits(uint n)
		{
			uint r = 0;
			while (n > 0)
			{
				r = r + (n % 10);
				n = n / 10;
			}
			return r;
		}

		static string CachePath
		{
			get
			{
				string cache = System.IO.Path.Combine(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CUE Tools"), "AccurateRipCache");
				if (!Directory.Exists(cache))
					Directory.CreateDirectory(cache);
				return cache;
			}
		}

		public static bool FindDriveReadOffset(string driveName, out int driveReadOffset)
		{
			string fileName = System.IO.Path.Combine(CachePath, "DriveOffsets.bin");
			if (!File.Exists(fileName) || (DateTime.Now - File.GetLastWriteTime(fileName) > TimeSpan.FromDays(10)))
			{
				HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://www.accuraterip.com/accuraterip/DriveOffsets.bin");
				req.Method = "GET";
				try
				{
					HttpWebResponse resp = (HttpWebResponse)req.GetResponse();
					if (resp.StatusCode != HttpStatusCode.OK)
					{
						driveReadOffset = 0;
						return false;
					}
					Stream respStream = resp.GetResponseStream();
					FileStream myOffsetsSaved = new FileStream(fileName, FileMode.Create, FileAccess.Write);
					byte[] buff = new byte[0x8000];
					do
					{
						int count = respStream.Read(buff, 0, buff.Length);
						if (count == 0) break;
						myOffsetsSaved.Write(buff, 0, count);
					} while (true);
					respStream.Close();
					myOffsetsSaved.Close();
				}
				catch (WebException ex)
				{
					driveReadOffset = 0;
					return false;
				}
			}
			FileStream myOffsets = new FileStream(fileName, FileMode.Open, FileAccess.Read);
			BinaryReader offsetReader = new BinaryReader(myOffsets);
			do
			{
				short readOffset = offsetReader.ReadInt16();
				byte[] name = offsetReader.ReadBytes(0x21);
				byte[] misc = offsetReader.ReadBytes(0x22);
				int len = name.Length;
				while (len > 0 && name[len - 1] == '\0') len--;
				string strname = Encoding.ASCII.GetString(name, 0, len);
				if (strname == driveName)
				{
					driveReadOffset = readOffset;
					return true;
				}
			} while (myOffsets.Position + 0x45 <= myOffsets.Length);
			offsetReader.Close();
			driveReadOffset = 0;
			return false;
		}

		public static string CalculateCDDBQuery(CDImageLayout toc)
		{
			StringBuilder query = new StringBuilder(CalculateCDDBId(toc));
			query.AppendFormat("+{0}", toc.TrackCount);
			for (int iTrack = 1; iTrack <= toc.TrackCount; iTrack++)
				query.AppendFormat("+{0}", toc[iTrack].Start + 150);
			query.AppendFormat("+{0}", toc.Length / 75 - toc[1].Start / 75);
			return query.ToString();
		}

		public static string CalculateCDDBId(CDImageLayout toc)
		{
			uint cddbDiscId = 0;
			for (int iTrack = 1; iTrack <= toc.TrackCount; iTrack++)
				cddbDiscId += sumDigits(toc[iTrack].Start / 75 + 2); // !!!!!!!!!!!!!!!!! %255 !!
			return string.Format("{0:X8}", (((cddbDiscId % 255) << 24) + ((toc.Length / 75 - toc[1].Start / 75) << 8) + (uint)toc.TrackCount) & 0xFFFFFFFF);
		}

		public static string CalculateAccurateRipId(CDImageLayout toc)
		{
			// Calculate the three disc ids used by AR
			uint discId1 = 0;
			uint discId2 = 0;
			uint num = 0;

			for (int iTrack = 1; iTrack <= toc.TrackCount; iTrack++)
				if (toc[iTrack].IsAudio)
				{
					discId1 += toc[iTrack].Start;
					discId2 += Math.Max(toc[iTrack].Start, 1) * (++num);
				}
			discId1 += toc.Length;
			discId2 += Math.Max(toc.Length, 1) * (++num);
			discId1 &= 0xFFFFFFFF;
			discId2 &= 0xFFFFFFFF;
			return string.Format("{0:x8}-{1:x8}-{2}", discId1, discId2, CalculateCDDBId(toc).ToLower());
		}

		public List<AccDisk> AccDisks
		{
			get
			{
				return _accDisks;
			}
		}

		private string ExceptionMessage;
		public HttpStatusCode ResponseStatus { get; set; }
		public WebExceptionStatus ExceptionStatus { get; set; }
		public string ARStatus
		{
			get
			{
				return ExceptionStatus == WebExceptionStatus.Success ? null :
					ExceptionStatus != WebExceptionStatus.ProtocolError ? ("database access error: " + (ExceptionMessage ?? ExceptionStatus.ToString())) :
					ResponseStatus != HttpStatusCode.NotFound ? "database access error: " + ResponseStatus.ToString() :
					"disk not present in database";
			}
		}

		CDImageLayout _toc;
		long _sampleCount, _finalSampleCount;
		int _currentTrack;
		private List<AccDisk> _accDisks;
		internal uint[,] _CRCAR;
		private uint[,] _CRCV2;
		internal uint[,] _CRCSM;
		internal uint[,] _CRC32;
		internal uint[,] _CRCWN;
		internal int[,] _CRCNL;
		private uint[,] _CacheCRCWN;
		private uint[,] _CacheCRC32;
		internal int[] _Peak;
		private uint[] _CRCLOG;
		private uint[] _CRCMASK;
		private IWebProxy proxy;

		private bool _hasLogCRC;

		private const int _arOffsetRange = 5 * 588 - 1;
	}

	public struct AccTrack
	{
		public uint count;
		public uint CRC;
		public uint Frame450CRC;
	}

	public class AccDisk
	{
		public uint count;
		public uint discId1;
		public uint discId2;
		public uint cddbDiscId;
		public List<AccTrack> tracks;

		public AccDisk()
		{
			tracks = new List<AccTrack>();
		}
	}
}
