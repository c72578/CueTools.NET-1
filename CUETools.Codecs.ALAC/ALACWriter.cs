/**
 * CUETools.Codecs.ALAC: pure managed ALAC audio encoder
 * Copyright (c) 2009 Gregory S. Chudov
 * Based on ffdshow ALAC audio encoder
 * Copyright (c) 2008  Jaikrishnan Menon, realityman@gmx.net
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301 USA
 */

#define INTEROP

using System;
using System.Text;
using System.IO;
using System.Collections.Generic;
#if INTEROP
using System.Runtime.InteropServices;
#endif
using CUETools.Codecs;

namespace CUETools.Codecs.ALAC
{
	[AudioEncoderClass("libALAC", "m4a", true, "0 1 2 3 4 5 6 7 8 9 10", "3", 1)]
	public class ALACWriter : IAudioDest
	{
		Stream _IO = null;
		string _path;
		long _position;

		// total stream samples
		// if < 0, stream length is unknown
		int sample_count = -1;

		ALACEncodeParams eparams;

		// maximum frame size in bytes
		// this can be used to allocate memory for output
		int max_frame_size;

		int initial_history = 10, history_mult = 40, k_modifier = 14;

		byte[] frame_buffer = null;

		int frame_count = 0;

		long first_frame_offset = 0;

#if INTEROP
		TimeSpan _userProcessorTime;
#endif

		// header bytes
		byte[] header;

		uint[] _sample_byte_size;
		int[] samplesBuffer;
		int[] verifyBuffer;
		int[] residualBuffer;
		float[] windowBuffer;
		int samplesInBuffer = 0;

		int _compressionLevel = 5;
		int _blocksize = 0;
		int _totalSize = 0;
		int _windowsize = 0, _windowcount = 0;

		Crc8 crc8;
		Crc16 crc16;
		ALACFrame frame;
		ALACReader verify;

		int mdat_pos;

		bool inited = false;

		List<int> chunk_pos;

		AudioPCMConfig _pcm;

		public ALACWriter(string path, Stream IO, AudioPCMConfig pcm)
		{
			_pcm = pcm;

			if (_pcm.BitsPerSample != 16)
				throw new Exception("Bits per sample must be 16.");
			if (_pcm.ChannelCount != 2)
				throw new Exception("ChannelCount must be 2.");

			_path = path;
			_IO = IO;

			samplesBuffer = new int[Alac.MAX_BLOCKSIZE * (_pcm.ChannelCount == 2 ? 5 : _pcm.ChannelCount)];
			residualBuffer = new int[Alac.MAX_BLOCKSIZE * (_pcm.ChannelCount == 2 ? 6 : _pcm.ChannelCount + 1)];
			windowBuffer = new float[Alac.MAX_BLOCKSIZE * 2 * Alac.MAX_LPC_WINDOWS];

			eparams.set_defaults(_compressionLevel);
			eparams.padding_size = 8192;

			crc8 = new Crc8();
			crc16 = new Crc16();
			frame = new ALACFrame(_pcm.ChannelCount == 2 ? 5 : _pcm.ChannelCount);
			chunk_pos = new List<int>();
		}

		public ALACWriter(string path, AudioPCMConfig pcm)
			: this(path, null, pcm)
		{
		}

		public int TotalSize
		{
			get
			{
				return _totalSize;
			}
		}

		public int PaddingLength
		{
			get
			{
				return eparams.padding_size;
			}
			set
			{
				eparams.padding_size = value;
			}
		}

		public int CompressionLevel
		{
			get
			{
				return _compressionLevel;
			}
			set
			{
				if (value < 0 || value > 10)
					throw new Exception("unsupported compression level");
				_compressionLevel = value;
				eparams.set_defaults(_compressionLevel);
			}
		}

		public string Options
		{
			set
			{
				if (value == null || value == "") return;
				string[] args = value.Split();
				for (int i = 0; i < args.Length; i++)
				{
					if (args[i] == "--padding-length" && (++i) < args.Length)
					{
						PaddingLength = int.Parse(args[i]);
						continue;
					}
					if (args[i] == "--verify")
					{
						DoVerify = true;
						continue;
					}
					throw new Exception("Unsupported options " + value);
				}
			}
		}

#if INTEROP
		[DllImport("kernel32.dll")]
		static extern bool GetThreadTimes(IntPtr hThread, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);
		[DllImport("kernel32.dll")]
		static extern IntPtr GetCurrentThread();
#endif

		void chunk_start(BitWriter bitwriter)
		{
			bitwriter.flush();
			chunk_pos.Add(bitwriter.Length);
			bitwriter.writebits(32, 0); // length placeholder
		}

		void chunk_end(BitWriter bitwriter)
		{
			bitwriter.flush();
			int pos = chunk_pos[chunk_pos.Count - 1];
			chunk_pos.RemoveAt(chunk_pos.Count - 1);
			int chunk_end = bitwriter.Length;
			bitwriter.Length = pos;
			bitwriter.writebits(32, chunk_end - pos);
			bitwriter.Length = chunk_end;
		}

		void DoClose()
		{
			if (inited)
			{
				while (samplesInBuffer > 0)
					output_frame(samplesInBuffer);

				if (_IO.CanSeek)
				{
					int mdat_len = (int)_IO.Position - mdat_pos;
					_IO.Position = mdat_pos;
					BitWriter bitwriter = new BitWriter(header, 0, 4);
					bitwriter.writebits(32, mdat_len);
					bitwriter.flush();
					_IO.Write(header, 0, 4);

					if (sample_count <= 0 && _position != 0)
						sample_count = (int)_position;

					_IO.Position = _IO.Length;
					int trailer_len = write_trailers();
					_IO.Write(header, 0, trailer_len);
				}
				_IO.Close();
				inited = false;
			}

#if INTEROP
			long fake, KernelStart, UserStart;
			GetThreadTimes(GetCurrentThread(), out fake, out fake, out KernelStart, out UserStart);
			_userProcessorTime = new TimeSpan(UserStart);
#endif
		}

		public void Close()
		{
			DoClose();
			if (sample_count > 0 && _position != sample_count)
				throw new Exception("Samples written differs from the expected sample count.");
		}

		public void Delete()
		{
			if (inited)
			{
				_IO.Close();
				inited = false;
			}

			if (_path != "")
				File.Delete(_path);
		}

		public long Position
		{
			get
			{
				return _position;
			}
		}

		public long FinalSampleCount
		{
			set { sample_count = (int)value; }
		}

		public long BlockSize
		{
			set { _blocksize = (int)value; }
			get { return _blocksize == 0 ? eparams.block_size : _blocksize; }
		}

		public OrderMethod OrderMethod
		{
			get { return eparams.order_method; }
			set { eparams.order_method = value; }
		}

		public StereoMethod StereoMethod
		{
			get { return eparams.stereo_method; }
			set { eparams.stereo_method = value; }
		}

		public WindowMethod WindowMethod
		{
			get { return eparams.window_method; }
			set { eparams.window_method = value; }
		}

		public WindowFunction WindowFunction
		{
			get { return eparams.window_function; }
			set { eparams.window_function = value; }
		}

		public bool DoVerify
		{
			get { return eparams.do_verify; }
			set { eparams.do_verify = value; }
		}

		public bool DoSeekTable
		{
			get { return eparams.do_seektable; }
			set { eparams.do_seektable = value; }
		}

		public int MinLPCOrder
		{
			get
			{
				return eparams.min_prediction_order;
			}
			set
			{
				if (value < 1)
					throw new Exception("invalid MinLPCOrder " + value.ToString());
				eparams.min_prediction_order = value;
				if (eparams.max_prediction_order < value)
					eparams.max_prediction_order = value;
			}
		}

		public int MaxLPCOrder
		{
			get
			{
				return eparams.max_prediction_order;
			}
			set
			{
				if (value > 30 || value < eparams.min_prediction_order)
					throw new Exception("invalid MaxLPCOrder " + value.ToString());
				eparams.max_prediction_order = value;
				if (eparams.min_prediction_order > value)
					eparams.min_prediction_order = value;
			}
		}

		public int MinHistoryModifier
		{
			get
			{
				return eparams.min_modifier;
			}
			set
			{
				if (value < 1)
					throw new Exception("invalid MinHistoryModifier " + value.ToString());
				eparams.min_modifier = value;
				if (eparams.max_modifier < value)
					eparams.max_modifier = value;
			}
		}

		public int MaxHistoryModifier
		{
			get
			{
				return eparams.max_modifier;
			}
			set
			{
				if (value > 7)
					throw new Exception("invalid MaxHistoryModifier " + value.ToString());
				eparams.max_modifier = value;
				if (eparams.min_modifier > value)
					eparams.min_modifier = value;
			}
		}

		public int HistoryMult
		{
			get
			{
				return history_mult;
			}
			set
			{
				if (value < 1 || value > 255)
					throw new Exception("invalid history_mult");
				history_mult = value;
			}
		}

		public int InitialHistory
		{
			get
			{
				return initial_history;
			}
			set
			{
				if (value < 1 || value > 255)
					throw new Exception("invalid initial_history");
				initial_history = value;
			}
		}

		public int EstimationDepth
		{
			get
			{
				return eparams.estimation_depth;
			}
			set
			{
				if (value > 32 || value < 1)
					throw new Exception("invalid estimation_depth " + value.ToString());
				eparams.estimation_depth = value;
			}
		}

		public int AdaptivePasses
		{
			get
			{
				return eparams.adaptive_passes;
			}
			set
			{
				if (value >= lpc.MAX_LPC_PRECISIONS || value < 0)
					throw new Exception("invalid adaptive_passes " + value.ToString());
				eparams.adaptive_passes = value;
			}			
		}

		public TimeSpan UserProcessorTime
		{
			get
			{
#if INTEROP
				return _userProcessorTime;
#else
				return TimeSpan(0);
#endif
			}
		}

		public AudioPCMConfig PCM
		{
			get { return _pcm; }
		}

		/// <summary>
		/// Copy channel-interleaved input samples into separate subframes
		/// </summary>
		/// <param name="samples"></param>
		/// <param name="pos"></param>
		/// <param name="block"></param>
 		unsafe void copy_samples(int[,] samples, int pos, int block)
		{
			fixed (int* fsamples = samplesBuffer, src = &samples[pos, 0])
			{
				if (_pcm.ChannelCount == 2)
					AudioSamples.Deinterlace(fsamples + samplesInBuffer, fsamples + Alac.MAX_BLOCKSIZE + samplesInBuffer, src, block);
				else
					for (int ch = 0; ch < _pcm.ChannelCount; ch++)
					{
						int* psamples = fsamples + ch * Alac.MAX_BLOCKSIZE + samplesInBuffer;
						int channels = _pcm.ChannelCount;
						for (int i = 0; i < block; i++)
							psamples[i] = src[i * channels + ch];
					}
			}
			samplesInBuffer += block;
		}

		unsafe static void channel_decorrelation(int* leftS, int* rightS, int* leftM, int* rightM, int blocksize, int leftweight, int shift)
		{
			for (int i = 0; i < blocksize; i++)
			{
				leftM[i] = rightS[i] + ((leftS[i] - rightS[i]) * leftweight >> shift);
				rightM[i] = leftS[i] - rightS[i];
			}
		}

		private static int extend_sign32(int val, int bits)
		{
			return (val << (32 - bits)) >> (32 - bits);
		}

		private static int sign_only(int val)
		{
			return (val >> 31) + ((val - 1) >> 31) + 1;
		}

		unsafe static void alac_encode_residual_31(int* res, int* smp, int n)
		{
			res[0] = smp[0];
			for (int i = 1; i < n; i++)
				res[i] = smp[i] - smp[i - 1];
		}

		unsafe static void alac_encode_residual_0(int* res, int* smp, int n)
		{
			AudioSamples.MemCpy(res, smp, n);
		}

		unsafe static void alac_encode_residual(int* res, int* smp, int n, int order, int* coefs, int shift, int bps)
		{
			int csum = 0;

			for (int i = order - 1; i >= 0; i--)
				csum += coefs[i];

			if (n <= order || order <= 0 || order > 30)
				throw new Exception("invalid output");

			/* generate warm-up samples */
			res[0] = smp[0];
			for (int i = 1; i <= order; i++)
				res[i] = smp[i] - smp[i - 1];

			/* general case */
			for (int i = order + 1; i < n; i++)
			{
				int sample = *(smp++);
				int/*long*/ sum = (1 << (shift - 1));
				for (int j = 0; j < order; j++)
					sum += (smp[j] - sample) * coefs[j];
				int resval = extend_sign32(smp[order] - sample - (int)(sum >> shift), bps);
				res[i] = resval;

				if (resval > 0)
				{
					for (int j = 0; j < order && resval > 0; j++)
					{
						int val = smp[j] - sample;
						int sign = sign_only(val);
						coefs[j] += sign;
						resval -= (val * sign >> shift) * (j + 1);
					}
				}
				else
				{
					for (int j = 0; j < order && resval < 0; j++)
					{
						int val = smp[j] - sample;
						int sign = -sign_only(val);
						coefs[j] += sign;
						resval -= (val * sign >> shift) * (j + 1);
					}
				}
			}
			res[n] = 1; // Stop byte to help alac_entropy_coder;
		}

		unsafe static int encode_scalar(int x, int k, int bps)
		{
			int divisor = (1 << k) - 1;
			int q = x / divisor;
			int r = x % divisor;
			return q > 8 ? 9 + bps : q + k + (r - 1 >> 31) + 1;//== 0 ? 0 : 1);
		}

		unsafe void encode_scalar(BitWriter bitwriter, int x, int k, int bps)
		{
			k = Math.Min(k, k_modifier);
			int divisor = (1 << k) - 1;
			int q = x / divisor;
			int r = x % divisor;

			if (q > 8)
			{
				// write escape code and sample value directly
				bitwriter.writebits(9, 0x1ff);
				bitwriter.writebits(bps, x);
				return;
			}

			// q times one, then 1 zero, e.g. q == 3 is written as 1110
			int unary = ((1 << (q + 1)) - 2);
			if (r == 0)
			{
				bitwriter.writebits(q + k, unary << (k - 1));
				return;
			}

			bitwriter.writebits(q + 1 + k, (unary << k) + r + 1); 
		}

		unsafe int alac_entropy_coder(int* res, int n, int bps, out int modifier)
		{
			int size = 1 << 30;
			modifier = eparams.min_modifier;
			for (int i = eparams.min_modifier; i <= eparams.max_modifier; i++)
			{
				int newsize = alac_entropy_estimate(res, n, bps, i);
				if (size > newsize)
				{
					size = newsize;
					modifier = i;
				}
			}
			return size;
		}

		unsafe int alac_entropy_coder(int* res, int n, int bps, int modifier)
		{
			int history = initial_history;
			int sign_modifier = 0;
			int rice_historymult = modifier * history_mult / 4;
			int size = 0;
			int* fin = res + n;

			while (res < fin)
			{
				int k = BitReader.log2i((history >> 9) + 3);
				int x = *(res++);
				x = (x << 1) ^ (x >> 31);

				size += encode_scalar(x - sign_modifier, Math.Min(k, k_modifier), bps);

				history += x * rice_historymult - ((history * rice_historymult) >> 9);

				sign_modifier = 0;
				if (x > 0xFFFF)
					history = 0xFFFF;

				if (history < 128 && res < fin)
				{
					k = 7 - BitReader.log2i(history) + ((history + 16) >> 6);
					int* res1 = res;
					while (*res == 0) // we have a stop byte, so need not check if res < fin
						res++;
					int block_size = (int)(res - res1);
					size += encode_scalar(block_size, Math.Min(k, k_modifier), 16);
					//sign_modifier = (block_size <= 0xFFFF) ? 1 : 0; //never happens
					sign_modifier = 1;
					history = 0;
				}
			}
			return size;
		}

		/// <summary>
		/// Crude estimation of entropy length
		/// </summary>
		/// <param name="res"></param>
		/// <param name="n"></param>
		/// <param name="bps"></param>
		/// <param name="modifier"></param>
		/// <returns></returns>
		unsafe int alac_entropy_estimate(int* res, int n, int bps, int modifier)
		{
			int history = initial_history;
			int rice_historymult = modifier * history_mult / 4;
			int size = 0;
			int* fin = res + n;

			while (res < fin)
			{
				int x = *(res++);
				x = (x << 1) ^ (x >> 31);
				int k = BitReader.log2i((history >> 9) + 3);
				k = k > k_modifier ? k_modifier : k;
				size += (x >> k) > 8 ? 9 + bps : (x >> k) + k + 1;
				history += x * rice_historymult - ((history * rice_historymult) >> 9);
			}
			return size;
		}

		unsafe void alac_entropy_coder(BitWriter bitwriter, int* res, int n, int bps, int modifier)
		{
			int history = initial_history;
			int sign_modifier = 0;
			int rice_historymult = modifier * history_mult / 4;
			int* fin = res + n;

			while (res < fin)
			{
				int k = BitReader.log2i((history >> 9) + 3);
				int x = *(res++);
				x = (x << 1) ^ (x >> 31);

				encode_scalar(bitwriter, x - sign_modifier, k, bps);

				history += x * rice_historymult - ((history * rice_historymult) >> 9);

				sign_modifier = 0;
				if (x > 0xFFFF)
					history = 0xFFFF;

				if (history < 128 && res < fin)
				{
					k = 7 - BitReader.log2i(history) + ((history + 16) >> 6);
					int* res1 = res;
					while (*res == 0) // we have a stop byte, so need not check if res < fin
						res++;
					int block_size = (int)(res - res1);
					encode_scalar(bitwriter, block_size, k, 16);
					sign_modifier = (block_size <= 0xFFFF) ? 1 : 0;
					history = 0;
				}
			}
		}

		unsafe void encode_residual_lpc_sub(ALACFrame frame, float* lpcs, int iWindow, int order, int ch)
		{
			// check if we already calculated with this order, window and precision
			if ((frame.subframes[ch].lpc_ctx[iWindow].done_lpcs[eparams.adaptive_passes] & (1U << (order - 1))) == 0)
			{
				frame.subframes[ch].lpc_ctx[iWindow].done_lpcs[eparams.adaptive_passes] |= (1U << (order - 1));

				uint cbits = 15U;

				frame.current.order = order;
				frame.current.window = iWindow;

				int bps = _pcm.BitsPerSample + _pcm.ChannelCount - 1;

				int* coefs = stackalloc int[lpc.MAX_LPC_ORDER];

				//if (frame.subframes[ch].best.order == order && frame.subframes[ch].best.window == iWindow)
				//{
				//    frame.current.shift = frame.subframes[ch].best.shift;
				//    for (int i = 0; i < frame.current.order; i++)
				//        frame.current.coefs[i] = frame.subframes[ch].best.coefs_adapted[i];
				//}
				//else
				{
					lpc.quantize_lpc_coefs(lpcs + (frame.current.order - 1) * lpc.MAX_LPC_ORDER,
						frame.current.order, cbits, coefs, out frame.current.shift, 15, 1);

					if (frame.current.shift < 0 || frame.current.shift > 15)
						throw new Exception("negative shift");

					for (int i = 0; i < frame.current.order; i++)
						frame.current.coefs[i] = coefs[i];
				}

				for (int i = 0; i < frame.current.order; i++)
					coefs[i] = frame.current.coefs[frame.current.order - 1 - i];
				for (int i = frame.current.order; i < lpc.MAX_LPC_ORDER;  i++)
					coefs[i] = 0;

				alac_encode_residual(frame.current.residual, frame.subframes[ch].samples, frame.blocksize,
					frame.current.order, coefs, frame.current.shift, bps);

				for (int i = 0; i < frame.current.order; i++)
					frame.current.coefs_adapted[i] = coefs[frame.current.order - 1 - i];

				for (int adaptive_pass = 0; adaptive_pass < eparams.adaptive_passes; adaptive_pass++)
				{
					for (int i = 0; i < frame.current.order; i++)
						frame.current.coefs[i] = frame.current.coefs_adapted[i];

					alac_encode_residual(frame.current.residual, frame.subframes[ch].samples, frame.blocksize,
						frame.current.order, coefs, frame.current.shift, bps);

					for (int i = 0; i < frame.current.order; i++)
						frame.current.coefs_adapted[i] = coefs[frame.current.order - 1 - i];
				}

				frame.current.size = (uint)(alac_entropy_estimate(frame.current.residual, frame.blocksize, bps, eparams.max_modifier) + 16 + 16 * order);
				
				frame.ChooseBestSubframe(ch);
			}
		}

		unsafe void encode_residual(ALACFrame frame, int ch, int pass, int best_window)
		{
			int* smp = frame.subframes[ch].samples;
			int i, n = frame.blocksize;
			int bps = _pcm.BitsPerSample + _pcm.ChannelCount - 1;

			// FIXED
			//if (0 == (2 & frame.subframes[ch].done_fixed) && (pass != 1 || n < eparams.max_prediction_order))
			//{
			//    frame.subframes[ch].done_fixed |= 2;
			//    frame.current.order = 31;
			//    frame.current.window = -1;
			//    alac_encode_residual_31(frame.current.residual, frame.subframes[ch].samples, frame.blocksize);
			//    frame.current.size = (uint)(alac_entropy_coder(frame.current.residual, frame.blocksize, bps, out frame.current.ricemodifier) + 16);
			//    frame.ChooseBestSubframe(ch);
			//}
			//if (0 == (1 & frame.subframes[ch].done_fixed) && (pass != 1 || n < eparams.max_prediction_order))
			//{
			//    frame.subframes[ch].done_fixed |= 1;
			//    frame.current.order = 0;
			//    frame.current.window = -1;
			//    alac_encode_residual_0(frame.current.residual, frame.subframes[ch].samples, frame.blocksize);
			//    frame.current.size = (uint)(alac_entropy_coder(frame.current.residual, frame.blocksize, bps, out frame.current.ricemodifier) + 16);
			//    frame.ChooseBestSubframe(ch);
			//}

			// LPC
			if (n < eparams.max_prediction_order)
				return;

			float* lpcs = stackalloc float[lpc.MAX_LPC_ORDER * lpc.MAX_LPC_ORDER];
			int min_order = eparams.min_prediction_order;
			int max_order = eparams.max_prediction_order;

			for (int iWindow = 0; iWindow < _windowcount; iWindow++)
			{
				if (best_window != -1 && iWindow != best_window)
					continue;

				LpcContext lpc_ctx = frame.subframes[ch].lpc_ctx[iWindow];

				lpc_ctx.GetReflection(max_order, smp, n, frame.window_buffer + iWindow * Alac.MAX_BLOCKSIZE * 2);
				lpc_ctx.ComputeLPC(lpcs);
				lpc_ctx.SortOrdersAkaike(frame.blocksize, eparams.estimation_depth, max_order, 5.0, 1.0/18);
				for (i = 0; i < eparams.estimation_depth && i < max_order; i++)
					encode_residual_lpc_sub(frame, lpcs, iWindow, lpc_ctx.best_orders[i], ch);
			}
		}

		unsafe void output_frame_header(ALACFrame frame, BitWriter bitwriter)
		{
			bitwriter.writebits(3, _pcm.ChannelCount - 1);
			bitwriter.writebits(16, 0);
			bitwriter.writebits(1, frame.blocksize != eparams.block_size ? 1 : 0); // sample count is in the header
			bitwriter.writebits(2, 0); // wasted bytes
			bitwriter.writebits(1, frame.type == FrameType.Verbatim ? 1 : 0); // is verbatim
			if (frame.blocksize != eparams.block_size)
				bitwriter.writebits(32, frame.blocksize);
			if (frame.type != FrameType.Verbatim)
			{
				bitwriter.writebits(8, frame.interlacing_shift);
				bitwriter.writebits(8, frame.interlacing_leftweight);
				for (int ch = 0; ch < _pcm.ChannelCount; ch++)
				{
					bitwriter.writebits(4, 0); // prediction type
					bitwriter.writebits(4, frame.subframes[ch].best.shift);
					bitwriter.writebits(3, frame.subframes[ch].best.ricemodifier);
					bitwriter.writebits(5, frame.subframes[ch].best.order);
					if (frame.subframes[ch].best.order != 31)
						for (int c = 0; c < frame.subframes[ch].best.order; c++)
							bitwriter.writebits_signed(16, frame.subframes[ch].best.coefs[c]);
				}
			}
		}

		void output_frame_footer(BitWriter bitwriter)
		{
			bitwriter.writebits(3, 7);
			bitwriter.flush();
		}

		unsafe void encode_residual_pass1(ALACFrame frame, int ch, int best_window)
		{
			int max_prediction_order = eparams.max_prediction_order;
			int estimation_depth = eparams.estimation_depth;
			int min_modifier = eparams.min_modifier;
			int adaptive_passes = eparams.adaptive_passes;
			eparams.max_prediction_order = Math.Min(8,eparams.max_prediction_order);
			eparams.estimation_depth = 1;
			eparams.min_modifier = eparams.max_modifier;
			eparams.adaptive_passes = 0;
			encode_residual(frame, ch, 1, best_window);
			eparams.max_prediction_order = max_prediction_order;
			eparams.estimation_depth = estimation_depth;
			eparams.min_modifier = min_modifier;
			eparams.adaptive_passes = adaptive_passes;
		}

		unsafe void encode_residual_pass2(ALACFrame frame, int ch)
		{
			encode_residual(frame, ch, 2, estimate_best_window(frame, ch));
		}

		unsafe int estimate_best_window(ALACFrame frame, int ch)
		{
			if (_windowcount == 1)
				return 0;
			switch (eparams.window_method)
			{
				case WindowMethod.Estimate:
					{
						int best_window = -1;
						double best_error = 0;
						int order = 2;
						for (int i = 0; i < _windowcount; i++)
						{
							frame.subframes[ch].lpc_ctx[i].GetReflection(order, frame.subframes[ch].samples, frame.blocksize, frame.window_buffer + i * Alac.MAX_BLOCKSIZE * 2);
							double err = frame.subframes[ch].lpc_ctx[i].prediction_error[order - 1] / frame.subframes[ch].lpc_ctx[i].autocorr_values[0];
							if (best_window == -1 || best_error > err)
							{
								best_window = i;
								best_error = err;
							}
						}
						return best_window;
					}
				case WindowMethod.Evaluate:
					encode_residual_pass1(frame, ch, -1);
					return frame.subframes[ch].best.window;
				case WindowMethod.Search:
					return -1;
			}
			return -1;
		}

		unsafe void estimate_frame(ALACFrame frame, bool do_midside)
		{
			int subframes = do_midside ? 5 : _pcm.ChannelCount;

			switch (eparams.stereo_method)
			{
				case StereoMethod.Estimate:
					for (int ch = 0; ch < subframes; ch++)
					{
						LpcContext lpc_ctx = frame.subframes[ch].lpc_ctx[0];
						int stereo_order = Math.Min(8, eparams.max_prediction_order);
						double alpha = 1.5; // 4.5 + eparams.max_prediction_order / 10.0;
						lpc_ctx.GetReflection(stereo_order, frame.subframes[ch].samples, frame.blocksize, frame.window_buffer);
						lpc_ctx.SortOrdersAkaike(frame.blocksize, 1, stereo_order, alpha, 0);
						frame.subframes[ch].best.size = (uint)Math.Max(0, lpc_ctx.Akaike(frame.blocksize, lpc_ctx.best_orders[0], alpha, 0));
					}
					break;
				case StereoMethod.Evaluate:
					for (int ch = 0; ch < subframes; ch++)
						encode_residual_pass1(frame, ch, 0);
					break;
				case StereoMethod.Search:
					for (int ch = 0; ch < subframes; ch++)
						encode_residual_pass2(frame, ch);
					break;
			}
		}

		unsafe uint measure_frame_size(ALACFrame frame, bool do_midside)
		{
			// crude estimation of header/footer size
			uint total = 16 + 3;

			if (do_midside)
			{
				uint bitsBest = frame.subframes[0].best.size + frame.subframes[1].best.size;
				frame.interlacing_leftweight = 0;
				frame.interlacing_shift = 0;

				if (bitsBest > frame.subframes[3].best.size + frame.subframes[0].best.size) // leftside
				{
					bitsBest = frame.subframes[3].best.size + frame.subframes[0].best.size;
					frame.interlacing_leftweight = 1;
					frame.interlacing_shift = 0;
				}
				if (bitsBest > frame.subframes[3].best.size + frame.subframes[2].best.size) // midside
				{
					bitsBest = frame.subframes[3].best.size + frame.subframes[2].best.size;
					frame.interlacing_leftweight = 1;
					frame.interlacing_shift = 1;
				}
				if (bitsBest > frame.subframes[3].best.size + frame.subframes[4].best.size) // rightside
				{
					bitsBest = frame.subframes[3].best.size + frame.subframes[4].best.size;
					frame.interlacing_leftweight = 1;
					frame.interlacing_shift = 31;
				}

				return total + bitsBest;
			}

			for (int ch = 0; ch < _pcm.ChannelCount; ch++)
				total += frame.subframes[ch].best.size;

			return total;
		}

		unsafe void encode_estimated_frame(ALACFrame frame)
		{
			switch (eparams.stereo_method)
			{
				case StereoMethod.Estimate:
					for (int ch = 0; ch < _pcm.ChannelCount; ch++)
					{
						frame.subframes[ch].best.size = AudioSamples.UINT32_MAX;
						encode_residual_pass2(frame, ch);
					}
					break;
				case StereoMethod.Evaluate:
					for (int ch = 0; ch < _pcm.ChannelCount; ch++)
						encode_residual_pass2(frame, ch);
					break;
				case StereoMethod.Search:
					break;
			}
		}

		unsafe delegate void window_function(float* window, int size);

		unsafe void calculate_window(float * window, window_function func, WindowFunction flag)
		{
			if ((eparams.window_function & flag) == 0 || _windowcount == Alac.MAX_LPC_WINDOWS)
				return;
			int sz = _windowsize;
			float* pos = window + _windowcount * Alac.MAX_BLOCKSIZE * 2;
			do
			{
				func(pos, sz);
				if ((sz & 1) != 0)
					break;
				pos += sz;
				sz >>= 1;
			} while (sz >= 32);
			_windowcount++;
		}

		unsafe int encode_frame(ref int size)
		{
			fixed (int* s = samplesBuffer, r = residualBuffer)
			fixed (float * window = windowBuffer)
			{
				frame.InitSize(size);

				if (frame.blocksize != _windowsize && frame.blocksize > 4)
				{
					_windowsize = frame.blocksize;
					_windowcount = 0;
					calculate_window(window, lpc.window_welch, WindowFunction.Welch);
					calculate_window(window, lpc.window_bartlett, WindowFunction.Bartlett);
					calculate_window(window, lpc.window_tukey, WindowFunction.Tukey);
					calculate_window(window, lpc.window_hann, WindowFunction.Hann);
					calculate_window(window, lpc.window_flattop, WindowFunction.Flattop);
					if (_windowcount == 0)
						throw new Exception("invalid windowfunction");
				}
				frame.window_buffer = window;

				int bps = _pcm.BitsPerSample + _pcm.ChannelCount - 1;
				if (_pcm.ChannelCount != 2 || frame.blocksize <= 32 || eparams.stereo_method == StereoMethod.Independent)
				{
					frame.current.residual = r + _pcm.ChannelCount * Alac.MAX_BLOCKSIZE;

					for (int ch = 0; ch < _pcm.ChannelCount; ch++)
						frame.subframes[ch].Init(s + ch * Alac.MAX_BLOCKSIZE, r + ch * Alac.MAX_BLOCKSIZE);

					for (int ch = 0; ch < _pcm.ChannelCount; ch++)
						encode_residual_pass2(frame, ch);
				}
				else
				{
					channel_decorrelation(s, s + Alac.MAX_BLOCKSIZE, s + 2 * Alac.MAX_BLOCKSIZE, s + 3 * Alac.MAX_BLOCKSIZE, frame.blocksize, 1, 1);
					channel_decorrelation(s, s + Alac.MAX_BLOCKSIZE, s + 4 * Alac.MAX_BLOCKSIZE, s + 3 * Alac.MAX_BLOCKSIZE, frame.blocksize, 1, 31);
					frame.current.residual = r + 5 * Alac.MAX_BLOCKSIZE;
					for (int ch = 0; ch < 5; ch++)
						frame.subframes[ch].Init(s + ch * Alac.MAX_BLOCKSIZE, r + ch * Alac.MAX_BLOCKSIZE);
					estimate_frame(frame, true);
					measure_frame_size(frame, true);
					frame.ChooseSubframes();
					encode_estimated_frame(frame);
				}

				for (int ch = 0; ch < _pcm.ChannelCount; ch++)
				{
					if (eparams.min_modifier == eparams.max_modifier)
						frame.subframes[ch].best.ricemodifier = eparams.max_modifier;
					else
						/*frame.subframes[ch].best.size = 16 + 16 * order + */
						alac_entropy_coder(frame.subframes[ch].best.residual, frame.blocksize, bps, out frame.subframes[ch].best.ricemodifier);
				}

				uint fs = measure_frame_size(frame, false);
				frame.type = ((int)fs > frame.blocksize * _pcm.ChannelCount * bps) ? FrameType.Verbatim : FrameType.Compressed;
				BitWriter bitwriter = new BitWriter(frame_buffer, 0, max_frame_size);
				output_frame_header(frame, bitwriter);
				if (frame.type == FrameType.Verbatim)
				{
					int obps = _pcm.BitsPerSample;
					for (int i = 0; i < frame.blocksize; i++)
						for (int ch = 0; ch < _pcm.ChannelCount; ch++)
							bitwriter.writebits_signed(obps, frame.subframes[ch].samples[i]);
				}
				else if (frame.type == FrameType.Compressed)
				{
					for (int ch = 0; ch < _pcm.ChannelCount; ch++)
						alac_entropy_coder(bitwriter, frame.subframes[ch].best.residual, frame.blocksize, 
							bps, frame.subframes[ch].best.ricemodifier);
				}
				output_frame_footer(bitwriter);

				if (_sample_byte_size.Length <= frame_count)
				{
					uint[] tmp = new uint[frame_count * 2];
					Array.Copy(_sample_byte_size, tmp, _sample_byte_size.Length);
					_sample_byte_size = tmp;
				}
				_sample_byte_size[frame_count++] = (uint)bitwriter.Length;

				size = frame.blocksize;
				return bitwriter.Length;
			}
		}

		unsafe int output_frame(int blocksize)
		{
			if (verify != null)
			{
				fixed (int* s = verifyBuffer, r = samplesBuffer)
					for (int ch = 0; ch < _pcm.ChannelCount; ch++)
						AudioSamples.MemCpy(s + ch * Alac.MAX_BLOCKSIZE, r + ch * Alac.MAX_BLOCKSIZE, eparams.block_size);
			}

			//if (0 != eparams.variable_block_size && 0 == (eparams.block_size & 7) && eparams.block_size >= 128)
			//    fs = encode_frame_vbs();
			//else
			int bs = blocksize;
			int fs = encode_frame(ref bs);

			_position += bs;
			_IO.Write(frame_buffer, 0, fs);
			_totalSize += fs;

			if (verify != null)
			{
				int decoded = verify.DecodeFrame(frame_buffer, 0, fs);
				if (decoded != fs || verify.Remaining != bs)
					throw new Exception("validation failed!");
				int[,] deinterlaced = new int[bs, _pcm.ChannelCount];
				verify.deinterlace(deinterlaced, 0, bs);
				fixed (int* s = verifyBuffer, r = deinterlaced)
				{
					int channels = _pcm.ChannelCount;
					for (int i = 0; i < bs; i++)
						for (int ch = 0; ch < _pcm.ChannelCount; ch++)
							if (r[i * channels + ch] != s[ch * Alac.MAX_BLOCKSIZE + i])
								throw new Exception("validation failed!");
				}
			}

			if (bs < blocksize)
			{
				fixed (int* s = samplesBuffer)
					for (int ch = 0; ch < _pcm.ChannelCount; ch++)
						AudioSamples.MemCpy(s + ch * Alac.MAX_BLOCKSIZE, s + bs + ch * Alac.MAX_BLOCKSIZE, eparams.block_size - bs);
			}

			samplesInBuffer -= bs;

			return bs;
		}

		public void Write(AudioBuffer buff)
		{
			if (!inited)
			{
				if (_IO == null)
					_IO = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
				int header_size = encode_init();
				_IO.Write(header, 0, header_size);
				if (_IO.CanSeek)
					first_frame_offset = _IO.Position;
				inited = true;
			}

			buff.Prepare(this);

			int pos = 0;
			int len = buff.Length;
			while (len > 0)
			{
				int block = Math.Min(len, eparams.block_size - samplesInBuffer);

				copy_samples(buff.Samples, pos, block);

				len -= block;
				pos += block;

				while (samplesInBuffer >= eparams.block_size)
					output_frame(eparams.block_size);
			}
		}

		public string Path { get { return _path; } }

		private DateTime? _creationTime = null;

		public DateTime CreationTime
		{
			set
			{
				_creationTime = value;
			}
		}

		public string Vendor
		{
			get
			{
				return vendor_string;
			}
			set
			{
				vendor_string = value;
			}
		}

		string vendor_string = "CUETools.2.05";

		int select_blocksize(int samplerate, int time_ms)
		{
			int target = (samplerate * time_ms) / 1000;
			int blocksize = 1024;
			while (target >= blocksize)
				blocksize <<= 1;
			return blocksize >> 1;
		}

		void write_chunk_mvhd(BitWriter bitwriter, TimeSpan UnixTime)
		{
			chunk_start(bitwriter);
			{
				bitwriter.write('m', 'v', 'h', 'd');
				bitwriter.writebits(32, 0);
				bitwriter.writebits(32, (int)UnixTime.TotalSeconds);
				bitwriter.writebits(32, (int)UnixTime.TotalSeconds);
				bitwriter.writebits(32, 1000);
				bitwriter.writebits(32, sample_count);
				bitwriter.writebits(32, 0x00010000); // reserved (preferred rate) 1.0 = normal
				bitwriter.writebits(16, 0x0100); // reserved (preferred volume) 1.0 = normal
				bitwriter.writebytes(10, 0); // reserved
				bitwriter.writebits(32, 0x00010000); // reserved (matrix structure)
				bitwriter.writebits(32, 0x00000000); // reserved (matrix structure)
				bitwriter.writebits(32, 0x00000000); // reserved (matrix structure)
				bitwriter.writebits(32, 0x00000000); // reserved (matrix structure)
				bitwriter.writebits(32, 0x00010000); // reserved (matrix structure)
				bitwriter.writebits(32, 0x00000000); // reserved (matrix structure)
				bitwriter.writebits(32, 0x00000000); // reserved (matrix structure)
				bitwriter.writebits(32, 0x00000000); // reserved (matrix structure)
				bitwriter.writebits(32, 0x40000000); // reserved (matrix structure)
				bitwriter.writebits(32, 0); // preview time
				bitwriter.writebits(32, 0); // preview duration
				bitwriter.writebits(32, 0); // poster time
				bitwriter.writebits(32, 0); // selection time
				bitwriter.writebits(32, 0); // selection duration
				bitwriter.writebits(32, 0); // current time
				bitwriter.writebits(32, 2); // next track ID
			}
			chunk_end(bitwriter);
		}

		void write_chunk_minf(BitWriter bitwriter)
		{
			chunk_start(bitwriter);
			{
				bitwriter.write('m', 'i', 'n', 'f');
				chunk_start(bitwriter);
				{
					bitwriter.write('s', 'm', 'h', 'd');
					bitwriter.writebits(32, 0); // version & flags
					bitwriter.writebits(16, 0); // reserved (balance)
					bitwriter.writebits(16, 0); // reserved
				}
				chunk_end(bitwriter);
				chunk_start(bitwriter);
				{
					bitwriter.write('d', 'i', 'n', 'f');
					chunk_start(bitwriter);
					{
						bitwriter.write('d', 'r', 'e', 'f');
						bitwriter.writebits(32, 0); // version & flags
						bitwriter.writebits(32, 1); // entry count
						chunk_start(bitwriter);
						{
							bitwriter.write('u', 'r', 'l', ' ');
							bitwriter.writebits(32, 1); // version & flags
						}
						chunk_end(bitwriter);
					}
					chunk_end(bitwriter);
				}
				chunk_end(bitwriter);
				chunk_start(bitwriter);
				{
					bitwriter.write('s', 't', 'b', 'l');
					chunk_start(bitwriter);
					{
						bitwriter.write('s', 't', 's', 'd');
						bitwriter.writebits(32, 0); // version & flags
						bitwriter.writebits(32, 1); // entry count
						chunk_start(bitwriter);
						{
							bitwriter.write('a', 'l', 'a', 'c');
							bitwriter.writebits(32, 0); // reserved
							bitwriter.writebits(16, 0); // reserved
							bitwriter.writebits(16, 1); // data reference index
							bitwriter.writebits(16, 0); // version
							bitwriter.writebits(16, 0); // revision
							bitwriter.writebits(32, 0); // reserved
							bitwriter.writebits(16, 2); // reserved channels
							bitwriter.writebits(16, 16); // reserved bps
							bitwriter.writebits(16, 0); // reserved compression ID
							bitwriter.writebits(16, 0); // packet size
							bitwriter.writebits(16, _pcm.SampleRate); // time scale
							bitwriter.writebits(16, 0); // reserved
							chunk_start(bitwriter);
							{
								bitwriter.write('a', 'l', 'a', 'c');
								bitwriter.writebits(32, 0); // reserved
								bitwriter.writebits(32, eparams.block_size); // max frame size
								bitwriter.writebits(8, 0); // reserved
								bitwriter.writebits(8, _pcm.BitsPerSample);
								bitwriter.writebits(8, history_mult);
								bitwriter.writebits(8, initial_history);
								bitwriter.writebits(8, k_modifier);
								bitwriter.writebits(8, _pcm.ChannelCount); // channels
								bitwriter.writebits(16, 0); // reserved
								bitwriter.writebits(32, max_frame_size);
								bitwriter.writebits(32, _pcm.SampleRate * _pcm.ChannelCount * _pcm.BitsPerSample); // average bitrate
								bitwriter.writebits(32, _pcm.SampleRate);
							}
							chunk_end(bitwriter);
						}
						chunk_end(bitwriter);
					}
					chunk_end(bitwriter);
					chunk_start(bitwriter);
					{
						bitwriter.write('s', 't', 't', 's');
						bitwriter.writebits(32, 0); // version & flags
						if (sample_count % eparams.block_size == 0)
						{
							bitwriter.writebits(32, 1); // entries
							bitwriter.writebits(32, sample_count / eparams.block_size);
							bitwriter.writebits(32, eparams.block_size);
						}
						else
						{
							bitwriter.writebits(32, 2); // entries
							bitwriter.writebits(32, sample_count / eparams.block_size);
							bitwriter.writebits(32, eparams.block_size);
							bitwriter.writebits(32, 1);
							bitwriter.writebits(32, sample_count % eparams.block_size);
						}
					}
					chunk_end(bitwriter);
					chunk_start(bitwriter);
					{
						bitwriter.write('s', 't', 's', 'c');
						bitwriter.writebits(32, 0); // version & flags
						bitwriter.writebits(32, 1); // entry count
						bitwriter.writebits(32, 1); // first chunk
						bitwriter.writebits(32, 1); // samples in chunk
						bitwriter.writebits(32, 1); // sample description index
					}
					chunk_end(bitwriter);
					chunk_start(bitwriter);
					{
						bitwriter.write('s', 't', 's', 'z');
						bitwriter.writebits(32, 0); // version & flags
						bitwriter.writebits(32, 0); // sample size (0 == variable)
						bitwriter.writebits(32, frame_count); // entry count
						for (int i = 0; i < frame_count; i++)
							bitwriter.writebits(32, _sample_byte_size[i]);
					}
					chunk_end(bitwriter);
					chunk_start(bitwriter);
					{
						bitwriter.write('s', 't', 'c', 'o');
						bitwriter.writebits(32, 0); // version & flags
						bitwriter.writebits(32, frame_count); // entry count
						uint pos = (uint)mdat_pos + 8;
						for (int i = 0; i < frame_count; i++)
						{
							bitwriter.writebits(32, pos);
							pos += _sample_byte_size[i];
						}
					}
					chunk_end(bitwriter);
				}
				chunk_end(bitwriter);
			}
			chunk_end(bitwriter);
		}

		void write_chunk_mdia(BitWriter bitwriter, TimeSpan UnixTime)
		{
			chunk_start(bitwriter);
			{
				bitwriter.write('m', 'd', 'i', 'a');
				chunk_start(bitwriter);
				{
					bitwriter.write('m', 'd', 'h', 'd');
					bitwriter.writebits(32, 0); // version & flags
					bitwriter.writebits(32, (int)UnixTime.TotalSeconds);
					bitwriter.writebits(32, (int)UnixTime.TotalSeconds);
					bitwriter.writebits(32, _pcm.SampleRate);
					bitwriter.writebits(32, sample_count);
					bitwriter.writebits(16, 0x55c4); // language
					bitwriter.writebits(16, 0); // quality
				}
				chunk_end(bitwriter);
				chunk_start(bitwriter);
				{
					bitwriter.write('h', 'd', 'l', 'r');
					bitwriter.writebits(32, 0); // version & flags
					bitwriter.writebits(32, 0); // hdlr
					bitwriter.write('s', 'o', 'u', 'n');
					bitwriter.writebits(32, 0); // reserved
					bitwriter.writebits(32, 0); // reserved
					bitwriter.writebits(32, 0); // reserved
					bitwriter.writebits(8, "SoundHandler".Length);
					bitwriter.write("SoundHandler");
				}
				chunk_end(bitwriter);
				write_chunk_minf(bitwriter);
			}
			chunk_end(bitwriter);
		}

		void write_chunk_trak(BitWriter bitwriter, TimeSpan UnixTime)
		{
			chunk_start(bitwriter);
			{
				bitwriter.write('t', 'r', 'a', 'k');
				chunk_start(bitwriter);
				{
					bitwriter.write('t', 'k', 'h', 'd');
					bitwriter.writebits(32, 15); // version
					bitwriter.writebits(32, (int)UnixTime.TotalSeconds);
					bitwriter.writebits(32, (int)UnixTime.TotalSeconds);
					bitwriter.writebits(32, 1); // track ID
					bitwriter.writebits(32, 0); // reserved
					bitwriter.writebits(32, sample_count / _pcm.SampleRate);
					bitwriter.writebits(32, 0); // reserved
					bitwriter.writebits(32, 0); // reserved
					bitwriter.writebits(32, 0); // reserved (layer & alternate group)
					bitwriter.writebits(16, 0x0100); // reserved (preferred volume) 1.0 = normal
					bitwriter.writebits(16, 0); // reserved
					bitwriter.writebits(32, 0x00010000); // reserved (matrix structure)
					bitwriter.writebits(32, 0x00000000); // reserved (matrix structure)
					bitwriter.writebits(32, 0x00000000); // reserved (matrix structure)
					bitwriter.writebits(32, 0x00000000); // reserved (matrix structure)
					bitwriter.writebits(32, 0x00010000); // reserved (matrix structure)
					bitwriter.writebits(32, 0x00000000); // reserved (matrix structure)
					bitwriter.writebits(32, 0x00000000); // reserved (matrix structure)
					bitwriter.writebits(32, 0x00000000); // reserved (matrix structure)
					bitwriter.writebits(32, 0x40000000); // reserved (matrix structure)
					bitwriter.writebits(32, 0); // reserved (width)
					bitwriter.writebits(32, 0); // reserved (height)
				}
				chunk_end(bitwriter);
				write_chunk_mdia(bitwriter, UnixTime);
			}
			chunk_end(bitwriter);
		}

		void write_chunk_udta(BitWriter bitwriter)
		{
			chunk_start(bitwriter);
			{
				bitwriter.write('u', 'd', 't', 'a');
				chunk_start(bitwriter);
				{
					bitwriter.write('m', 'e', 't', 'a');
					bitwriter.writebits(32, 0);
					chunk_start(bitwriter);
					{
						bitwriter.write('h', 'd', 'l', 'r');
						bitwriter.writebits(32, 0);
						bitwriter.writebits(32, 0);
						bitwriter.write('m', 'd', 'i', 'r');
						bitwriter.write('a', 'p', 'p', 'l');
						bitwriter.writebits(32, 0);
						bitwriter.writebits(32, 0);
						bitwriter.writebits(16, 0);
					}
					chunk_end(bitwriter);
					chunk_start(bitwriter);
					{
						bitwriter.write('i', 'l', 's', 't');
						chunk_start(bitwriter);
						{
							bitwriter.write((char)0xA9, 't', 'o', 'o');
							chunk_start(bitwriter);
							{
								bitwriter.write('d', 'a', 't', 'a');
								bitwriter.writebits(32, 1);
								bitwriter.writebits(32, 0);
								bitwriter.write(vendor_string);
							}
							chunk_end(bitwriter);
						}
						chunk_end(bitwriter);
					}
					chunk_end(bitwriter);
				}
				chunk_end(bitwriter);
			}
			chunk_end(bitwriter);
		}

		int write_trailers()
		{
			TimeSpan UnixTime = (_creationTime ?? DateTime.Now) - new DateTime(1970, 1, 1, 0, 0, 0, 0).ToLocalTime();
			header = new byte[0x1000 + frame_count * 8 + eparams.padding_size]; // FIXME!!! Possible buffer overrun
			BitWriter bitwriter = new BitWriter(header, 0, header.Length);
			chunk_start(bitwriter);
			{
				bitwriter.write('m', 'o', 'o', 'v');
				write_chunk_mvhd(bitwriter, UnixTime);
				write_chunk_trak(bitwriter, UnixTime);
				write_chunk_udta(bitwriter);
			}
			chunk_end(bitwriter);
			chunk_start(bitwriter); // padding
			{
				bitwriter.write('f', 'r', 'e', 'e');
				bitwriter.writebytes(eparams.padding_size, 0);
			}
			chunk_end(bitwriter);
			return bitwriter.Length;
		}

		int write_headers()
		{
			BitWriter bitwriter = new BitWriter(header, 0, header.Length);

			chunk_start(bitwriter);
			bitwriter.write('f', 't', 'y', 'p');
			bitwriter.write('M', '4', 'A', ' ');
			bitwriter.writebits(32, 0x200); // minor version
			bitwriter.write('M', '4', 'A', ' ');
			bitwriter.write('m', 'p', '4', '2');
			bitwriter.write('i', 's', 'o', 'm');
			bitwriter.writebits(32, 0);
			chunk_end(bitwriter);

			chunk_start(bitwriter); // padding
			{
				bitwriter.write('f', 'r', 'e', 'e');
				bitwriter.writebytes(eparams.padding_size, 0);
			}
			chunk_end(bitwriter);

			chunk_start(bitwriter); // padding in case we need extended mdat len
			bitwriter.write('f', 'r', 'e', 'e');
			chunk_end(bitwriter);

			mdat_pos = bitwriter.Length;

			chunk_start(bitwriter); // mdat len placeholder
			bitwriter.write('m', 'd', 'a', 't');
			chunk_end(bitwriter);

			return bitwriter.Length;
		}

		int encode_init()
		{
			int i, header_len;

			//if(flake_validate_params(s) < 0)

			// FIXME: For now, only 44100 samplerate is supported
			if (_pcm.SampleRate != 44100)
				throw new Exception("non-standard samplerate");

			// FIXME: For now, only 16-bit encoding is supported
			if (_pcm.BitsPerSample != 16)
				throw new Exception("non-standard bps");

			if (_blocksize == 0)
			{
				if (eparams.block_size == 0)
					eparams.block_size = select_blocksize(_pcm.SampleRate, eparams.block_time_ms);
				_blocksize = eparams.block_size;
			}
			else
				eparams.block_size = _blocksize;

			// set maximum encoded frame size (if larger, re-encodes in verbatim mode)
			if (_pcm.ChannelCount == 2)
				max_frame_size = 16 + ((eparams.block_size * (_pcm.BitsPerSample + _pcm.BitsPerSample + 1) + 7) >> 3);
			else
				max_frame_size = 16 + ((eparams.block_size * _pcm.ChannelCount * _pcm.BitsPerSample + 7) >> 3);

			//if (_IO.CanSeek && eparams.do_seektable)
			//{
			//}

			// output header bytes
			header = new byte[eparams.padding_size + 0x1000];
			header_len = write_headers();

			frame_buffer = new byte[max_frame_size];
			_sample_byte_size = new uint[Math.Max(0x100, sample_count / eparams.block_size + 1)];

			if (eparams.do_verify)
			{
				verify = new ALACReader(_pcm, history_mult, initial_history, k_modifier, eparams.block_size);
				verifyBuffer = new int[Alac.MAX_BLOCKSIZE * _pcm.ChannelCount];
			}

			return header_len;
		}
	}

	struct ALACEncodeParams
	{
		// compression quality
		// set by user prior to calling encode_init
		// standard values are 0 to 8
		// 0 is lower compression, faster encoding
		// 8 is higher compression, slower encoding
		// extended values 9 to 12 are slower and/or use
		// higher prediction orders
		public int compression;

		// prediction order selection method
		// set by user prior to calling encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 5
		// 0 = use maximum order only
		// 1 = use estimation
		// 2 = 2-level
		// 3 = 4-level
		// 4 = 8-level
		// 5 = full search
		// 6 = log search
		public OrderMethod order_method;


		// stereo decorrelation method
		// set by user prior to calling encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 2
		// 0 = independent L+R channels
		// 1 = mid-side encoding
		public StereoMethod stereo_method;

		public WindowMethod window_method;

		// block size in samples
		// set by the user prior to calling encode_init
		// if set to 0, a block size is chosen based on block_time_ms
		// can also be changed by user before encoding a frame
		public int block_size;

		// block time in milliseconds
		// set by the user prior to calling encode_init
		// used to calculate block_size based on sample rate
		// can also be changed by user before encoding a frame
		public int block_time_ms;

		// padding size in bytes
		// set by the user prior to calling encode_init
		// if set to less than 0, defaults to 4096
		public int padding_size;

		// minimum LPC order
		// set by user prior to calling encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 1 to 32
		public int min_prediction_order;

		// maximum LPC order
		// set by user prior to calling encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 1 to 32 
		public int max_prediction_order;

		// Number of LPC orders to try (for estimate mode)
		// set by user prior to calling encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 1 to 32 
		public int estimation_depth;

		public int adaptive_passes;

		public int min_modifier, max_modifier;

		public WindowFunction window_function;

		public bool do_verify;
		public bool do_seektable;

		public int set_defaults(int lvl)
		{
			compression = lvl;

			if ((lvl < 0 || lvl > 12) && (lvl != 99))
			{
				return -1;
			}

			// default to level 5 params
			window_function = WindowFunction.Flattop | WindowFunction.Tukey;
			order_method = OrderMethod.Estimate;
			stereo_method = StereoMethod.Evaluate;
			window_method = WindowMethod.Evaluate;
			block_size = 0;
			block_time_ms = 105;
			min_modifier = 4;
			max_modifier = 4;
			min_prediction_order = 1;
			max_prediction_order = 12;
			estimation_depth = 1;
			adaptive_passes = 0;
			do_verify = false;
			do_seektable = false;

			// differences from level 6
			switch (lvl)
			{
				case 0:
					stereo_method = StereoMethod.Independent;
					window_function = WindowFunction.Hann;
					max_prediction_order = 6;
					break;
				case 1:
					stereo_method = StereoMethod.Independent;
					window_function = WindowFunction.Hann;
					max_prediction_order = 8;
					break;
				case 2:
					stereo_method = StereoMethod.Estimate;
					window_function = WindowFunction.Hann;
					max_prediction_order = 6;
					break;
				case 3:
					stereo_method = StereoMethod.Estimate;
					window_function = WindowFunction.Hann;
					max_prediction_order = 8;
					break;
				case 4:
					stereo_method = StereoMethod.Estimate;
					window_method = WindowMethod.Estimate;
					max_prediction_order = 8;
					break;
				case 5:
					stereo_method = StereoMethod.Estimate;
					window_method = WindowMethod.Estimate;
					break;
				case 6:
					stereo_method = StereoMethod.Estimate;
					break;
				case 7:
					stereo_method = StereoMethod.Estimate;
					adaptive_passes = 1;
					min_modifier = 2;
					break;
				case 8:
					adaptive_passes = 1;
					min_modifier = 2;
					break;
				case 9:
					adaptive_passes = 1;
					max_prediction_order = 30;
					min_modifier = 2;
					break;
				case 10:
					estimation_depth = 2;
					adaptive_passes = 2;
					max_prediction_order = 30;
					min_modifier = 2;
					break;
			}

			return 0;
		}
	}
}
