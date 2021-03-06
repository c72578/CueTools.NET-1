using System;
using System.Collections.Generic;
using System.Text;

namespace CUETools.Ripper.SCSI
{
	public class Galois
	{
		public const int POLYNOMIAL = 0x1d;
		public static Galois instance = new Galois();
		private int[] expTbl = new int[255 * 2];	// 二重にもつことによりmul, div等を簡略化
		private int[] logTbl = new int[255 + 1];

		/**
		 * スカラー、ベクターの相互変換テーブルの作成
		 */
		Galois() 
		{
			int d = 1;
			for (int i = 0; i < 255; i++)
			{
				expTbl[i] = expTbl[255 + i] = d;
				logTbl[d] = i;
				d <<= 1;
				if ((d & 0x100) != 0)
				{
					d = (d ^ POLYNOMIAL) & 0xff;
				}
			}
		}

		/**
		 * スカラー -> ベクター変換
		 *
		 * @param a int
		 * @return int
		 */
		public int toExp(int a)
		{
			return expTbl[a];
		}

		/**
		 * ベクター -> スカラー変換
		 *
		 * @param a int
		 * @return int
		 */
		public int toLog(int a)
		{
			return logTbl[a];
		}

		/**
		 * 誤り位置インデックスの計算
		 *
		 * @param length int
		 * 		データ長
		 * @param a int
		 * 		誤り位置ベクター
		 * @return int
		 * 		誤り位置インデックス
		 */
		public int toPos(int length, int a)
		{
			return length - 1 - logTbl[a];
		}

		/**
		 * 掛け算
		 *
		 * @param a int
		 * @param b int
		 * @return int
		 * 		= a * b
		 */
		public int mul(int a, int b)
		{
			return (a == 0 || b == 0) ? 0 : expTbl[logTbl[a] + logTbl[b]];
		}

		/**
		 * 掛け算
		 *
		 * @param a int
		 * @param b int
		 * @return int
		 * 		= a * α^b
		 */
		public int mulExp(int a, int b)
		{
			return (a == 0) ? 0 : expTbl[logTbl[a] + b];
		}

		/**
		 * 割り算
		 *
		 * @param a int
		 * @param b int
		 * @return int
		 * 		= a / b
		 */
		public int div(int a, int b)
		{
			return (a == 0) ? 0 : expTbl[logTbl[a] - logTbl[b] + 255];
		}

		/**
		 * 割り算
		 *
		 * @param a int
		 * @param b int
		 * @return int
		 * 		= a / α^b
		 */
		public int divExp(int a, int b)
		{
			return (a == 0) ? 0 : expTbl[logTbl[a] - b + 255];
		}

		/**
		 * 逆数
		 *
		 * @param a int
		 * @return int
		 * 		= 1/a
		 */
		public int inv(int a)
		{
			return expTbl[255 - logTbl[a]];
		}

		/**
		 * 数式の掛け算
		 *
		 * @param seki int[]
		 * 		seki = a * b
		 * @param a int[]
		 * @param b int[]
		 */
		public void mulPoly(int[] seki, int[] a, int[] b)
		{
			Array.Clear(seki, 0, seki.Length);
			for (int ia = 0; ia < a.Length; ia++)
			{
				if (a[ia] != 0)
				{
					int loga = logTbl[a[ia]];
					int ib2 = Math.Min(b.Length, seki.Length - ia);
					for (int ib = 0; ib < ib2; ib++)
					{
						if (b[ib] != 0)
						{
							seki[ia + ib] ^= expTbl[loga + logTbl[b[ib]]];	// = a[ia] * b[ib]
						}
					}
				}
			}
		}

		/**
		 * シンドロームの計算
		 * @param data int[]
		 *		入力データ配列
		 * @param length int
		 *		データ長
		 * @param syn int[]
		 *		(x - α^0) (x - α^1) (x - α^2) ...のシンドローム
		 * @return boolean
		 *		true: シンドロームは総て0
		 */
		public bool calcSyndrome(byte[] data, int length, int[] syn)
		{
			int hasErr = 0;
			for (int i = 0; i < syn.Length; i++)
			{
				int wk = 0;
				for (int idx = 0; idx < length; idx++)
				{
					wk = data[idx] ^ ((wk == 0) ? 0 : expTbl[logTbl[wk] + i]);		// wk = data + wk * α^i
				}
				syn[i] = wk;
				hasErr |= wk;
			}
			return hasErr == 0;
		}
	}
}
