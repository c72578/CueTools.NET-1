using System;
using System.Collections.Generic;
using System.Text;

namespace CUETools.Parity
{
	/**
	 * タイトル: RSコード・エンコーダ
	 *
	 * @author Masayuki Miyazaki
	 * http://sourceforge.jp/projects/reedsolomon/
	 */
	public class RsEncode8
	{
		private static Galois galois = Galois81D.instance;
		private int npar;
		private int[] encodeGx;

		public RsEncode8(int npar)
		{
			this.npar = npar;
			encodeGx = galois.makeEncodeGx(npar);
		}

		/**
		 * RSコードのエンコード
		 *
		 * @param data int[]
		 *		入力データ配列
		 * @param length int
		 *		入力データ長
		 * @param parity int[]
		 *		パリティ格納用配列
		 * @param parityStartPos int
		 *		パリティ格納用Index
		 * @return bool
		 */
		public void encode(byte[] data, int datapos, int length, byte[] parity, int parityStartPos)
		{
			if (length < 0 || length + npar > galois.Max)
				throw new Exception("RsEncode: wrong length");

			/*
			 * パリティ格納用配列
			 * wr[0]        最上位
			 * wr[npar - 1] 最下位		なのに注意
			 * これでパリティを逆順に並べかえなくてよいので、arraycopyが使える
			 */
			byte[] wr = new byte[npar];
			for (int idx = datapos; idx < datapos + length; idx++)
			{
				int ib = wr[0] ^ data[idx];
				for (int i = 0; i < npar - 1; i++)
					wr[i] = (byte)(wr[i + 1] ^ galois.mul(ib, encodeGx[i]));
				wr[npar - 1] = (byte)galois.mul(ib, encodeGx[npar - 1]);
			}
			if (parity != null)
				Array.Copy(wr, 0, parity, parityStartPos, npar);
		}
	}

	public class RsEncode16
	{
		private Galois galois;
		private int npar;
		private int[] encodeGx;
		private ushort[,,] encodeTable;

		public RsEncode16(int npar)
			: this(npar, Galois16.instance)
		{
		}

		public RsEncode16(int npar, Galois galois)
		{
			this.npar = npar;
			this.galois = galois;
			encodeGx = galois.makeEncodeGx(npar);
			encodeTable = galois.makeEncodeTable(npar);
		}

		/**
		 * RSコードのエンコード
		 *
		 * @param data int[]
		 *		入力データ配列
		 * @param length int
		 *		入力データ長
		 * @param parity int[]
		 *		パリティ格納用配列
		 * @param parityStartPos int
		 *		パリティ格納用Index
		 * @return bool
		 */
		public unsafe void encode(ushort* data, int length, ushort* parity)
		{
			if (length < 0 || length + npar > galois.Max)
				throw new Exception("RsEncode: wrong length");

			/*
			 * パリティ格納用配列
			 * wr[0]        最上位
			 * wr[npar - 1] 最下位		なのに注意
			 * これでパリティを逆順に並べかえなくてよいので、arraycopyが使える
			 */
			ushort* wr = stackalloc ushort[npar];
			for (int idx = 0; idx < length; idx++)
			{
				int ib = wr[0] ^ data[idx];
				for (int i = 0; i < npar - 1; i++)
					wr[i] = (ushort)(wr[i + 1] ^ galois.mul(ib, encodeGx[i]));
				wr[npar - 1] = (ushort)galois.mul(ib, encodeGx[npar - 1]);
			}
			for (int i = 0; i < npar; i++)
				parity[i] = wr[i];
		}

		public unsafe void encode(byte[] data, int datapos, int length, byte[] parity, int parityStartPos)
		{
			if ((length & 1) != 0)
				throw new Exception("RsEncode: wrong length");
			fixed (byte* bytes = &data[datapos], par = &parity[parityStartPos])
				encode((ushort*)bytes, length >> 1, (ushort*)par);
		}
	}
}
