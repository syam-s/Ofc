// LzmaEncoder.cs

using System;
using LZMA.Core.Compress.RangeCoder;

namespace LZMA.Core.Compress.LZMA
{
    public class Encoder : ICoder, ISetCoderProperties, IWriteCoderProperties
	{
	    private enum EMatchFinderType
		{
			Bt2,
			Bt4,
		};

	    private const uint KIfinityPrice = 0xFFFFFFF;

	    private static readonly byte[] GFastPos = new byte[1 << 11];

		static Encoder()
		{
			const byte kFastSlots = 22;
			var c = 2;
			GFastPos[0] = 0;
			GFastPos[1] = 1;
			for (byte slotFast = 2; slotFast < kFastSlots; slotFast++)
			{
				var k = (uint)1 << ((slotFast >> 1) - 1);
				for (uint j = 0; j < k; j++, c++)
					GFastPos[c] = slotFast;
			}
		}

	    private static uint GetPosSlot(uint pos)
		{
			if (pos < 1 << 11)
				return GFastPos[pos];
			if (pos < 1 << 21)
				return (uint)(GFastPos[pos >> 10] + 20);
			return (uint)(GFastPos[pos >> 20] + 40);
		}

	    private static uint GetPosSlot2(uint pos)
		{
			if (pos < 1 << 17)
				return (uint)(GFastPos[pos >> 6] + 12);
			if (pos < 1 << 27)
				return (uint)(GFastPos[pos >> 16] + 32);
			return (uint)(GFastPos[pos >> 26] + 52);
		}

	    private Base.State _state = new Base.State();
	    private byte _previousByte;
	    private readonly uint[] _repDistances = new uint[Base.KNumRepDistances];

	    private void BaseInit()
		{
			_state.Init();
			_previousByte = 0;
			for (uint i = 0; i < Base.KNumRepDistances; i++)
				_repDistances[i] = 0;
		}

	    private const int KDefaultDictionaryLogSize = 22;
	    private const uint KNumFastBytesDefault = 0x20;

	    private class LiteralEncoder
		{
			public struct Encoder2
			{
			    private BitEncoder[] _mEncoders;

				public void Create() { _mEncoders = new BitEncoder[0x300]; }

				public void Init() { for (var i = 0; i < 0x300; i++) _mEncoders[i].Init(); }

				public void Encode(RangeCoder.Encoder rangeEncoder, byte symbol)
				{
					uint context = 1;
					for (var i = 7; i >= 0; i--)
					{
						var bit = (uint)((symbol >> i) & 1);
						_mEncoders[context].Encode(rangeEncoder, bit);
						context = (context << 1) | bit;
					}
				}

				public void EncodeMatched(RangeCoder.Encoder rangeEncoder, byte matchByte, byte symbol)
				{
					uint context = 1;
					var same = true;
					for (var i = 7; i >= 0; i--)
					{
						var bit = (uint)((symbol >> i) & 1);
						var state = context;
						if (same)
						{
							var matchBit = (uint)((matchByte >> i) & 1);
							state += (1 + matchBit) << 8;
							same = matchBit == bit;
						}
						_mEncoders[state].Encode(rangeEncoder, bit);
						context = (context << 1) | bit;
					}
				}

				public uint GetPrice(bool matchMode, byte matchByte, byte symbol)
				{
					uint price = 0;
					uint context = 1;
					var i = 7;
					if (matchMode)
					{
						for (; i >= 0; i--)
						{
							var matchBit = (uint)(matchByte >> i) & 1;
							var bit = (uint)(symbol >> i) & 1;
							price += _mEncoders[((1 + matchBit) << 8) + context].GetPrice(bit);
							context = (context << 1) | bit;
							if (matchBit != bit)
							{
								i--;
								break;
							}
						}
					}
					for (; i >= 0; i--)
					{
						var bit = (uint)(symbol >> i) & 1;
						price += _mEncoders[context].GetPrice(bit);
						context = (context << 1) | bit;
					}
					return price;
				}
			}

		    private Encoder2[] _mCoders;
		    private int _mNumPrevBits;
		    private int _mNumPosBits;
		    private uint _mPosMask;

			public void Create(int numPosBits, int numPrevBits)
			{
				if (_mCoders != null && _mNumPrevBits == numPrevBits && _mNumPosBits == numPosBits)
					return;
				_mNumPosBits = numPosBits;
				_mPosMask = ((uint)1 << numPosBits) - 1;
				_mNumPrevBits = numPrevBits;
				var numStates = (uint)1 << (_mNumPrevBits + _mNumPosBits);
				_mCoders = new Encoder2[numStates];
				for (uint i = 0; i < numStates; i++)
					_mCoders[i].Create();
			}

			public void Init()
			{
				var numStates = (uint)1 << (_mNumPrevBits + _mNumPosBits);
				for (uint i = 0; i < numStates; i++)
					_mCoders[i].Init();
			}

			public Encoder2 GetSubCoder(uint pos, byte prevByte)
			{ return _mCoders[((pos & _mPosMask) << _mNumPrevBits) + (uint)(prevByte >> (8 - _mNumPrevBits))]; }
		}

	    private class LenEncoder
		{
		    private BitEncoder _choice = new BitEncoder();
		    private BitEncoder _choice2 = new BitEncoder();
		    private readonly BitTreeEncoder[] _lowCoder = new BitTreeEncoder[Base.KNumPosStatesEncodingMax];
		    private readonly BitTreeEncoder[] _midCoder = new BitTreeEncoder[Base.KNumPosStatesEncodingMax];
		    private BitTreeEncoder _highCoder = new BitTreeEncoder(Base.KNumHighLenBits);

			public LenEncoder()
			{
				for (uint posState = 0; posState < Base.KNumPosStatesEncodingMax; posState++)
				{
					_lowCoder[posState] = new BitTreeEncoder(Base.KNumLowLenBits);
					_midCoder[posState] = new BitTreeEncoder(Base.KNumMidLenBits);
				}
			}

			public void Init(uint numPosStates)
			{
				_choice.Init();
				_choice2.Init();
				for (uint posState = 0; posState < numPosStates; posState++)
				{
					_lowCoder[posState].Init();
					_midCoder[posState].Init();
				}
				_highCoder.Init();
			}

			public void Encode(RangeCoder.Encoder rangeEncoder, uint symbol, uint posState)
			{
				if (symbol < Base.KNumLowLenSymbols)
				{
					_choice.Encode(rangeEncoder, 0);
					_lowCoder[posState].Encode(rangeEncoder, symbol);
				}
				else
				{
					symbol -= Base.KNumLowLenSymbols;
					_choice.Encode(rangeEncoder, 1);
					if (symbol < Base.KNumMidLenSymbols)
					{
						_choice2.Encode(rangeEncoder, 0);
						_midCoder[posState].Encode(rangeEncoder, symbol);
					}
					else
					{
						_choice2.Encode(rangeEncoder, 1);
						_highCoder.Encode(rangeEncoder, symbol - Base.KNumMidLenSymbols);
					}
				}
			}

			public void SetPrices(uint posState, uint numSymbols, uint[] prices, uint st)
			{
				var a0 = _choice.GetPrice0();
				var a1 = _choice.GetPrice1();
				var b0 = a1 + _choice2.GetPrice0();
				var b1 = a1 + _choice2.GetPrice1();
				uint i = 0;
				for (i = 0; i < Base.KNumLowLenSymbols; i++)
				{
					if (i >= numSymbols)
						return;
					prices[st + i] = a0 + _lowCoder[posState].GetPrice(i);
				}
				for (; i < Base.KNumLowLenSymbols + Base.KNumMidLenSymbols; i++)
				{
					if (i >= numSymbols)
						return;
					prices[st + i] = b0 + _midCoder[posState].GetPrice(i - Base.KNumLowLenSymbols);
				}
				for (; i < numSymbols; i++)
					prices[st + i] = b1 + _highCoder.GetPrice(i - Base.KNumLowLenSymbols - Base.KNumMidLenSymbols);
			}
		};

	    private const uint KNumLenSpecSymbols = Base.KNumLowLenSymbols + Base.KNumMidLenSymbols;

	    private class LenPriceTableEncoder : LenEncoder
		{
		    private readonly uint[] _prices = new uint[Base.KNumLenSymbols << Base.KNumPosStatesBitsEncodingMax];
		    private uint _tableSize;
		    private readonly uint[] _counters = new uint[Base.KNumPosStatesEncodingMax];

			public void SetTableSize(uint tableSize) { _tableSize = tableSize; }

			public uint GetPrice(uint symbol, uint posState)
			{
				return _prices[posState * Base.KNumLenSymbols + symbol];
			}

		    private void UpdateTable(uint posState)
			{
				SetPrices(posState, _tableSize, _prices, posState * Base.KNumLenSymbols);
				_counters[posState] = _tableSize;
			}

			public void UpdateTables(uint numPosStates)
			{
				for (uint posState = 0; posState < numPosStates; posState++)
					UpdateTable(posState);
			}

			public new void Encode(RangeCoder.Encoder rangeEncoder, uint symbol, uint posState)
			{
				base.Encode(rangeEncoder, symbol, posState);
				if (--_counters[posState] == 0)
					UpdateTable(posState);
			}
		}

	    private const uint KNumOpts = 1 << 12;

	    private class Optimal
		{
			public Base.State State;

			public bool Prev1IsChar;
			public bool Prev2;

			public uint PosPrev2;
			public uint BackPrev2;

			public uint Price;
			public uint PosPrev;
			public uint BackPrev;

			public uint Backs0;
			public uint Backs1;
			public uint Backs2;
			public uint Backs3;

			public void MakeAsChar() { BackPrev = 0xFFFFFFFF; Prev1IsChar = false; }
			public void MakeAsShortRep() { BackPrev = 0; ; Prev1IsChar = false; }
			public bool IsShortRep() { return BackPrev == 0; }
		};

	    private readonly Optimal[] _optimum = new Optimal[KNumOpts];
	    private LZ.IMatchFinder _matchFinder;
	    private readonly RangeCoder.Encoder _rangeEncoder = new RangeCoder.Encoder();

	    private readonly BitEncoder[] _isMatch = new BitEncoder[Base.KNumStates << Base.KNumPosStatesBitsMax];
	    private readonly BitEncoder[] _isRep = new BitEncoder[Base.KNumStates];
	    private readonly BitEncoder[] _isRepG0 = new BitEncoder[Base.KNumStates];
	    private readonly BitEncoder[] _isRepG1 = new BitEncoder[Base.KNumStates];
	    private readonly BitEncoder[] _isRepG2 = new BitEncoder[Base.KNumStates];
	    private readonly BitEncoder[] _isRep0Long = new BitEncoder[Base.KNumStates << Base.KNumPosStatesBitsMax];

	    private readonly BitTreeEncoder[] _posSlotEncoder = new BitTreeEncoder[Base.KNumLenToPosStates];

	    private readonly BitEncoder[] _posEncoders = new BitEncoder[Base.KNumFullDistances - Base.KEndPosModelIndex];
	    private BitTreeEncoder _posAlignEncoder = new BitTreeEncoder(Base.KNumAlignBits);

	    private readonly LenPriceTableEncoder _lenEncoder = new LenPriceTableEncoder();
	    private readonly LenPriceTableEncoder _repMatchLenEncoder = new LenPriceTableEncoder();

	    private readonly LiteralEncoder _literalEncoder = new LiteralEncoder();

	    private readonly uint[] _matchDistances = new uint[Base.KMatchMaxLen * 2 + 2];

	    private uint _numFastBytes = KNumFastBytesDefault;
	    private uint _longestMatchLength;
	    private uint _numDistancePairs;

	    private uint _additionalOffset;

	    private uint _optimumEndIndex;
	    private uint _optimumCurrentIndex;

	    private bool _longestMatchWasFound;

	    private readonly uint[] _posSlotPrices = new uint[1 << (Base.KNumPosSlotBits + Base.KNumLenToPosStatesBits)];
	    private readonly uint[] _distancesPrices = new uint[Base.KNumFullDistances << Base.KNumLenToPosStatesBits];
	    private readonly uint[] _alignPrices = new uint[Base.KAlignTableSize];
	    private uint _alignPriceCount;

	    private uint _distTableSize = KDefaultDictionaryLogSize * 2;

	    private int _posStateBits = 2;
	    private uint _posStateMask = 4 - 1;
	    private int _numLiteralPosStateBits;
	    private int _numLiteralContextBits = 3;

	    private uint _dictionarySize = 1 << KDefaultDictionaryLogSize;
	    private uint _dictionarySizePrev = 0xFFFFFFFF;
	    private uint _numFastBytesPrev = 0xFFFFFFFF;

	    private long _nowPos64;
	    private bool _finished;
	    private System.IO.Stream _inStream;

	    private EMatchFinderType _matchFinderType = EMatchFinderType.Bt4;
	    private bool _writeEndMark;

	    private bool _needReleaseMfStream;

	    private void Create()
		{
			if (_matchFinder == null)
			{
				var bt = new LZ.BinTree();
				var numHashBytes = 4;
				if (_matchFinderType == EMatchFinderType.Bt2)
					numHashBytes = 2;
				bt.SetType(numHashBytes);
				_matchFinder = bt;
			}
			_literalEncoder.Create(_numLiteralPosStateBits, _numLiteralContextBits);

			if (_dictionarySize == _dictionarySizePrev && _numFastBytesPrev == _numFastBytes)
				return;
			_matchFinder.Create(_dictionarySize, KNumOpts, _numFastBytes, Base.KMatchMaxLen + 1);
			_dictionarySizePrev = _dictionarySize;
			_numFastBytesPrev = _numFastBytes;
		}

		public Encoder()
		{
			for (var i = 0; i < KNumOpts; i++)
				_optimum[i] = new Optimal();
			for (var i = 0; i < Base.KNumLenToPosStates; i++)
				_posSlotEncoder[i] = new BitTreeEncoder(Base.KNumPosSlotBits);
		}

	    private void SetWriteEndMarkerMode(bool writeEndMarker)
		{
			_writeEndMark = writeEndMarker;
		}

	    private void Init()
		{
			BaseInit();
			_rangeEncoder.Init();

			uint i;
			for (i = 0; i < Base.KNumStates; i++)
			{
				for (uint j = 0; j <= _posStateMask; j++)
				{
					var complexState = (i << Base.KNumPosStatesBitsMax) + j;
					_isMatch[complexState].Init();
					_isRep0Long[complexState].Init();
				}
				_isRep[i].Init();
				_isRepG0[i].Init();
				_isRepG1[i].Init();
				_isRepG2[i].Init();
			}
			_literalEncoder.Init();
			for (i = 0; i < Base.KNumLenToPosStates; i++)
				_posSlotEncoder[i].Init();
			for (i = 0; i < Base.KNumFullDistances - Base.KEndPosModelIndex; i++)
				_posEncoders[i].Init();

			_lenEncoder.Init((uint)1 << _posStateBits);
			_repMatchLenEncoder.Init((uint)1 << _posStateBits);

			_posAlignEncoder.Init();

			_longestMatchWasFound = false;
			_optimumEndIndex = 0;
			_optimumCurrentIndex = 0;
			_additionalOffset = 0;
		}

	    private void ReadMatchDistances(out uint lenRes, out uint numDistancePairs)
		{
			lenRes = 0;
			numDistancePairs = _matchFinder.GetMatches(_matchDistances);
			if (numDistancePairs > 0)
			{
				lenRes = _matchDistances[numDistancePairs - 2];
				if (lenRes == _numFastBytes)
					lenRes += _matchFinder.GetMatchLen((int)lenRes - 1, _matchDistances[numDistancePairs - 1],
						Base.KMatchMaxLen - lenRes);
			}
			_additionalOffset++;
		}


	    private void MovePos(uint num)
		{
			if (num > 0)
			{
				_matchFinder.Skip(num);
				_additionalOffset += num;
			}
		}

	    private uint GetRepLen1Price(Base.State state, uint posState)
		{
			return _isRepG0[state.Index].GetPrice0() +
					_isRep0Long[(state.Index << Base.KNumPosStatesBitsMax) + posState].GetPrice0();
		}

	    private uint GetPureRepPrice(uint repIndex, Base.State state, uint posState)
		{
			uint price;
			if (repIndex == 0)
			{
				price = _isRepG0[state.Index].GetPrice0();
				price += _isRep0Long[(state.Index << Base.KNumPosStatesBitsMax) + posState].GetPrice1();
			}
			else
			{
				price = _isRepG0[state.Index].GetPrice1();
				if (repIndex == 1)
					price += _isRepG1[state.Index].GetPrice0();
				else
				{
					price += _isRepG1[state.Index].GetPrice1();
					price += _isRepG2[state.Index].GetPrice(repIndex - 2);
				}
			}
			return price;
		}

	    private uint GetRepPrice(uint repIndex, uint len, Base.State state, uint posState)
		{
			var price = _repMatchLenEncoder.GetPrice(len - Base.KMatchMinLen, posState);
			return price + GetPureRepPrice(repIndex, state, posState);
		}

	    private uint GetPosLenPrice(uint pos, uint len, uint posState)
		{
			uint price;
			var lenToPosState = Base.GetLenToPosState(len);
			if (pos < Base.KNumFullDistances)
				price = _distancesPrices[lenToPosState * Base.KNumFullDistances + pos];
			else
				price = _posSlotPrices[(lenToPosState << Base.KNumPosSlotBits) + GetPosSlot2(pos)] +
					_alignPrices[pos & Base.KAlignMask];
			return price + _lenEncoder.GetPrice(len - Base.KMatchMinLen, posState);
		}

	    private uint Backward(out uint backRes, uint cur)
		{
			_optimumEndIndex = cur;
			var posMem = _optimum[cur].PosPrev;
			var backMem = _optimum[cur].BackPrev;
			do
			{
				if (_optimum[cur].Prev1IsChar)
				{
					_optimum[posMem].MakeAsChar();
					_optimum[posMem].PosPrev = posMem - 1;
					if (_optimum[cur].Prev2)
					{
						_optimum[posMem - 1].Prev1IsChar = false;
						_optimum[posMem - 1].PosPrev = _optimum[cur].PosPrev2;
						_optimum[posMem - 1].BackPrev = _optimum[cur].BackPrev2;
					}
				}
				var posPrev = posMem;
				var backCur = backMem;

				backMem = _optimum[posPrev].BackPrev;
				posMem = _optimum[posPrev].PosPrev;

				_optimum[posPrev].BackPrev = backCur;
				_optimum[posPrev].PosPrev = cur;
				cur = posPrev;
			}
			while (cur > 0);
			backRes = _optimum[0].BackPrev;
			_optimumCurrentIndex = _optimum[0].PosPrev;
			return _optimumCurrentIndex;
		}

	    private readonly uint[] _reps = new uint[Base.KNumRepDistances];
	    private readonly uint[] _repLens = new uint[Base.KNumRepDistances];


	    private uint GetOptimum(uint position, out uint backRes)
		{
			if (_optimumEndIndex != _optimumCurrentIndex)
			{
				var lenRes = _optimum[_optimumCurrentIndex].PosPrev - _optimumCurrentIndex;
				backRes = _optimum[_optimumCurrentIndex].BackPrev;
				_optimumCurrentIndex = _optimum[_optimumCurrentIndex].PosPrev;
				return lenRes;
			}
			_optimumCurrentIndex = _optimumEndIndex = 0;

			uint lenMain, numDistancePairs;
			if (!_longestMatchWasFound)
			{
				ReadMatchDistances(out lenMain, out numDistancePairs);
			}
			else
			{
				lenMain = _longestMatchLength;
				numDistancePairs = _numDistancePairs;
				_longestMatchWasFound = false;
			}

			var numAvailableBytes = _matchFinder.GetNumAvailableBytes() + 1;
			if (numAvailableBytes < 2)
			{
				backRes = 0xFFFFFFFF;
				return 1;
			}
			if (numAvailableBytes > Base.KMatchMaxLen)
				numAvailableBytes = Base.KMatchMaxLen;

			uint repMaxIndex = 0;
			uint i;			
			for (i = 0; i < Base.KNumRepDistances; i++)
			{
				_reps[i] = _repDistances[i];
				_repLens[i] = _matchFinder.GetMatchLen(0 - 1, _reps[i], Base.KMatchMaxLen);
				if (_repLens[i] > _repLens[repMaxIndex])
					repMaxIndex = i;
			}
			if (_repLens[repMaxIndex] >= _numFastBytes)
			{
				backRes = repMaxIndex;
				var lenRes = _repLens[repMaxIndex];
				MovePos(lenRes - 1);
				return lenRes;
			}

			if (lenMain >= _numFastBytes)
			{
				backRes = _matchDistances[numDistancePairs - 1] + Base.KNumRepDistances;
				MovePos(lenMain - 1);
				return lenMain;
			}
			
			var currentByte = _matchFinder.GetIndexByte(0 - 1);
			var matchByte = _matchFinder.GetIndexByte((int)(0 - _repDistances[0] - 1 - 1));

			if (lenMain < 2 && currentByte != matchByte && _repLens[repMaxIndex] < 2)
			{
				backRes = 0xFFFFFFFF;
				return 1;
			}

			_optimum[0].State = _state;

			var posState = position & _posStateMask;

			_optimum[1].Price = _isMatch[(_state.Index << Base.KNumPosStatesBitsMax) + posState].GetPrice0() +
					_literalEncoder.GetSubCoder(position, _previousByte).GetPrice(!_state.IsCharState(), matchByte, currentByte);
			_optimum[1].MakeAsChar();

			var matchPrice = _isMatch[(_state.Index << Base.KNumPosStatesBitsMax) + posState].GetPrice1();
			var repMatchPrice = matchPrice + _isRep[_state.Index].GetPrice1();

			if (matchByte == currentByte)
			{
				var shortRepPrice = repMatchPrice + GetRepLen1Price(_state, posState);
				if (shortRepPrice < _optimum[1].Price)
				{
					_optimum[1].Price = shortRepPrice;
					_optimum[1].MakeAsShortRep();
				}
			}

			var lenEnd = lenMain >= _repLens[repMaxIndex] ? lenMain : _repLens[repMaxIndex];

			if(lenEnd < 2)
			{
				backRes = _optimum[1].BackPrev;
				return 1;
			}
			
			_optimum[1].PosPrev = 0;

			_optimum[0].Backs0 = _reps[0];
			_optimum[0].Backs1 = _reps[1];
			_optimum[0].Backs2 = _reps[2];
			_optimum[0].Backs3 = _reps[3];

			var len = lenEnd;
			do
				_optimum[len--].Price = KIfinityPrice;
			while (len >= 2);

			for (i = 0; i < Base.KNumRepDistances; i++)
			{
				var repLen = _repLens[i];
				if (repLen < 2)
					continue;
				var price = repMatchPrice + GetPureRepPrice(i, _state, posState);
				do
				{
					var curAndLenPrice = price + _repMatchLenEncoder.GetPrice(repLen - 2, posState);
					var optimum = _optimum[repLen];
					if (curAndLenPrice < optimum.Price)
					{
						optimum.Price = curAndLenPrice;
						optimum.PosPrev = 0;
						optimum.BackPrev = i;
						optimum.Prev1IsChar = false;
					}
				}
				while (--repLen >= 2);
			}

			var normalMatchPrice = matchPrice + _isRep[_state.Index].GetPrice0();
			
			len = _repLens[0] >= 2 ? _repLens[0] + 1 : 2;
			if (len <= lenMain)
			{
				uint offs = 0;
				while (len > _matchDistances[offs])
					offs += 2;
				for (; ; len++)
				{
					var distance = _matchDistances[offs + 1];
					var curAndLenPrice = normalMatchPrice + GetPosLenPrice(distance, len, posState);
					var optimum = _optimum[len];
					if (curAndLenPrice < optimum.Price)
					{
						optimum.Price = curAndLenPrice;
						optimum.PosPrev = 0;
						optimum.BackPrev = distance + Base.KNumRepDistances;
						optimum.Prev1IsChar = false;
					}
					if (len == _matchDistances[offs])
					{
						offs += 2;
						if (offs == numDistancePairs)
							break;
					}
				}
			}

			uint cur = 0;

			while (true)
			{
				cur++;
				if (cur == lenEnd)
					return Backward(out backRes, cur);
				uint newLen;
				ReadMatchDistances(out newLen, out numDistancePairs);
				if (newLen >= _numFastBytes)
				{
					_numDistancePairs = numDistancePairs;
					_longestMatchLength = newLen;
					_longestMatchWasFound = true;
					return Backward(out backRes, cur);
				}
				position++;
				var posPrev = _optimum[cur].PosPrev;
				Base.State state;
				if (_optimum[cur].Prev1IsChar)
				{
					posPrev--;
					if (_optimum[cur].Prev2)
					{
						state = _optimum[_optimum[cur].PosPrev2].State;
						if (_optimum[cur].BackPrev2 < Base.KNumRepDistances)
							state.UpdateRep();
						else
							state.UpdateMatch();
					}
					else
						state = _optimum[posPrev].State;
					state.UpdateChar();
				}
				else
					state = _optimum[posPrev].State;
				if (posPrev == cur - 1)
				{
					if (_optimum[cur].IsShortRep())
						state.UpdateShortRep();
					else
						state.UpdateChar();
				}
				else
				{
					uint pos;
					if (_optimum[cur].Prev1IsChar && _optimum[cur].Prev2)
					{
						posPrev = _optimum[cur].PosPrev2;
						pos = _optimum[cur].BackPrev2;
						state.UpdateRep();
					}
					else
					{
						pos = _optimum[cur].BackPrev;
						if (pos < Base.KNumRepDistances)
							state.UpdateRep();
						else
							state.UpdateMatch();
					}
					var opt = _optimum[posPrev];
					if (pos < Base.KNumRepDistances)
					{
						if (pos == 0)
						{
							_reps[0] = opt.Backs0;
							_reps[1] = opt.Backs1;
							_reps[2] = opt.Backs2;
							_reps[3] = opt.Backs3;
						}
						else if (pos == 1)
						{
							_reps[0] = opt.Backs1;
							_reps[1] = opt.Backs0;
							_reps[2] = opt.Backs2;
							_reps[3] = opt.Backs3;
						}
						else if (pos == 2)
						{
							_reps[0] = opt.Backs2;
							_reps[1] = opt.Backs0;
							_reps[2] = opt.Backs1;
							_reps[3] = opt.Backs3;
						}
						else
						{
							_reps[0] = opt.Backs3;
							_reps[1] = opt.Backs0;
							_reps[2] = opt.Backs1;
							_reps[3] = opt.Backs2;
						}
					}
					else
					{
						_reps[0] = pos - Base.KNumRepDistances;
						_reps[1] = opt.Backs0;
						_reps[2] = opt.Backs1;
						_reps[3] = opt.Backs2;
					}
				}
				_optimum[cur].State = state;
				_optimum[cur].Backs0 = _reps[0];
				_optimum[cur].Backs1 = _reps[1];
				_optimum[cur].Backs2 = _reps[2];
				_optimum[cur].Backs3 = _reps[3];
				var curPrice = _optimum[cur].Price;

				currentByte = _matchFinder.GetIndexByte(0 - 1);
				matchByte = _matchFinder.GetIndexByte((int)(0 - _reps[0] - 1 - 1));

				posState = position & _posStateMask;

				var curAnd1Price = curPrice +
					_isMatch[(state.Index << Base.KNumPosStatesBitsMax) + posState].GetPrice0() +
					_literalEncoder.GetSubCoder(position, _matchFinder.GetIndexByte(0 - 2)).
					GetPrice(!state.IsCharState(), matchByte, currentByte);

				var nextOptimum = _optimum[cur + 1];

				var nextIsChar = false;
				if (curAnd1Price < nextOptimum.Price)
				{
					nextOptimum.Price = curAnd1Price;
					nextOptimum.PosPrev = cur;
					nextOptimum.MakeAsChar();
					nextIsChar = true;
				}

				matchPrice = curPrice + _isMatch[(state.Index << Base.KNumPosStatesBitsMax) + posState].GetPrice1();
				repMatchPrice = matchPrice + _isRep[state.Index].GetPrice1();

				if (matchByte == currentByte &&
					!(nextOptimum.PosPrev < cur && nextOptimum.BackPrev == 0))
				{
					var shortRepPrice = repMatchPrice + GetRepLen1Price(state, posState);
					if (shortRepPrice <= nextOptimum.Price)
					{
						nextOptimum.Price = shortRepPrice;
						nextOptimum.PosPrev = cur;
						nextOptimum.MakeAsShortRep();
						nextIsChar = true;
					}
				}

				var numAvailableBytesFull = _matchFinder.GetNumAvailableBytes() + 1;
				numAvailableBytesFull = Math.Min(KNumOpts - 1 - cur, numAvailableBytesFull);
				numAvailableBytes = numAvailableBytesFull;

				if (numAvailableBytes < 2)
					continue;
				if (numAvailableBytes > _numFastBytes)
					numAvailableBytes = _numFastBytes;
				if (!nextIsChar && matchByte != currentByte)
				{
					// try Literal + rep0
					var t = Math.Min(numAvailableBytesFull - 1, _numFastBytes);
					var lenTest2 = _matchFinder.GetMatchLen(0, _reps[0], t);
					if (lenTest2 >= 2)
					{
						var state2 = state;
						state2.UpdateChar();
						var posStateNext = (position + 1) & _posStateMask;
						var nextRepMatchPrice = curAnd1Price +
							_isMatch[(state2.Index << Base.KNumPosStatesBitsMax) + posStateNext].GetPrice1() +
							_isRep[state2.Index].GetPrice1();
						{
							var offset = cur + 1 + lenTest2;
							while (lenEnd < offset)
								_optimum[++lenEnd].Price = KIfinityPrice;
							var curAndLenPrice = nextRepMatchPrice + GetRepPrice(
								0, lenTest2, state2, posStateNext);
							var optimum = _optimum[offset];
							if (curAndLenPrice < optimum.Price)
							{
								optimum.Price = curAndLenPrice;
								optimum.PosPrev = cur + 1;
								optimum.BackPrev = 0;
								optimum.Prev1IsChar = true;
								optimum.Prev2 = false;
							}
						}
					}
				}

				uint startLen = 2; // speed optimization 

				for (uint repIndex = 0; repIndex < Base.KNumRepDistances; repIndex++)
				{
					var lenTest = _matchFinder.GetMatchLen(0 - 1, _reps[repIndex], numAvailableBytes);
					if (lenTest < 2)
						continue;
					var lenTestTemp = lenTest;
					do
					{
						while (lenEnd < cur + lenTest)
							_optimum[++lenEnd].Price = KIfinityPrice;
						var curAndLenPrice = repMatchPrice + GetRepPrice(repIndex, lenTest, state, posState);
						var optimum = _optimum[cur + lenTest];
						if (curAndLenPrice < optimum.Price)
						{
							optimum.Price = curAndLenPrice;
							optimum.PosPrev = cur;
							optimum.BackPrev = repIndex;
							optimum.Prev1IsChar = false;
						}
					}
					while(--lenTest >= 2);
					lenTest = lenTestTemp;

					if (repIndex == 0)
						startLen = lenTest + 1;

					// if (_maxMode)
					if (lenTest < numAvailableBytesFull)
					{
						var t = Math.Min(numAvailableBytesFull - 1 - lenTest, _numFastBytes);
						var lenTest2 = _matchFinder.GetMatchLen((int)lenTest, _reps[repIndex], t);
						if (lenTest2 >= 2)
						{
							var state2 = state;
							state2.UpdateRep();
							var posStateNext = (position + lenTest) & _posStateMask;
							var curAndLenCharPrice = 
									repMatchPrice + GetRepPrice(repIndex, lenTest, state, posState) + 
									_isMatch[(state2.Index << Base.KNumPosStatesBitsMax) + posStateNext].GetPrice0() +
									_literalEncoder.GetSubCoder(position + lenTest, 
									_matchFinder.GetIndexByte((int)lenTest - 1 - 1)).GetPrice(true,
									_matchFinder.GetIndexByte((int)lenTest - 1 - (int)(_reps[repIndex] + 1)), 
									_matchFinder.GetIndexByte((int)lenTest - 1));
							state2.UpdateChar();
							posStateNext = (position + lenTest + 1) & _posStateMask;
							var nextMatchPrice = curAndLenCharPrice + _isMatch[(state2.Index << Base.KNumPosStatesBitsMax) + posStateNext].GetPrice1();
							var nextRepMatchPrice = nextMatchPrice + _isRep[state2.Index].GetPrice1();
							
							// for(; lenTest2 >= 2; lenTest2--)
							{
								var offset = lenTest + 1 + lenTest2;
								while(lenEnd < cur + offset)
									_optimum[++lenEnd].Price = KIfinityPrice;
								var curAndLenPrice = nextRepMatchPrice + GetRepPrice(0, lenTest2, state2, posStateNext);
								var optimum = _optimum[cur + offset];
								if (curAndLenPrice < optimum.Price) 
								{
									optimum.Price = curAndLenPrice;
									optimum.PosPrev = cur + lenTest + 1;
									optimum.BackPrev = 0;
									optimum.Prev1IsChar = true;
									optimum.Prev2 = true;
									optimum.PosPrev2 = cur;
									optimum.BackPrev2 = repIndex;
								}
							}
						}
					}
				}

				if (newLen > numAvailableBytes)
				{
					newLen = numAvailableBytes;
					for (numDistancePairs = 0; newLen > _matchDistances[numDistancePairs]; numDistancePairs += 2) ;
					_matchDistances[numDistancePairs] = newLen;
					numDistancePairs += 2;
				}
				if (newLen >= startLen)
				{
					normalMatchPrice = matchPrice + _isRep[state.Index].GetPrice0();
					while (lenEnd < cur + newLen)
						_optimum[++lenEnd].Price = KIfinityPrice;

					uint offs = 0;
					while (startLen > _matchDistances[offs])
						offs += 2;

					for (var lenTest = startLen; ; lenTest++)
					{
						var curBack = _matchDistances[offs + 1];
						var curAndLenPrice = normalMatchPrice + GetPosLenPrice(curBack, lenTest, posState);
						var optimum = _optimum[cur + lenTest];
						if (curAndLenPrice < optimum.Price)
						{
							optimum.Price = curAndLenPrice;
							optimum.PosPrev = cur;
							optimum.BackPrev = curBack + Base.KNumRepDistances;
							optimum.Prev1IsChar = false;
						}

						if (lenTest == _matchDistances[offs])
						{
							if (lenTest < numAvailableBytesFull)
							{
								var t = Math.Min(numAvailableBytesFull - 1 - lenTest, _numFastBytes);
								var lenTest2 = _matchFinder.GetMatchLen((int)lenTest, curBack, t);
								if (lenTest2 >= 2)
								{
									var state2 = state;
									state2.UpdateMatch();
									var posStateNext = (position + lenTest) & _posStateMask;
									var curAndLenCharPrice = curAndLenPrice +
										_isMatch[(state2.Index << Base.KNumPosStatesBitsMax) + posStateNext].GetPrice0() +
										_literalEncoder.GetSubCoder(position + lenTest,
										_matchFinder.GetIndexByte((int)lenTest - 1 - 1)).
										GetPrice(true,
										_matchFinder.GetIndexByte((int)lenTest - (int)(curBack + 1) - 1),
										_matchFinder.GetIndexByte((int)lenTest - 1));
									state2.UpdateChar();
									posStateNext = (position + lenTest + 1) & _posStateMask;
									var nextMatchPrice = curAndLenCharPrice + _isMatch[(state2.Index << Base.KNumPosStatesBitsMax) + posStateNext].GetPrice1();
									var nextRepMatchPrice = nextMatchPrice + _isRep[state2.Index].GetPrice1();

									var offset = lenTest + 1 + lenTest2;
									while (lenEnd < cur + offset)
										_optimum[++lenEnd].Price = KIfinityPrice;
									curAndLenPrice = nextRepMatchPrice + GetRepPrice(0, lenTest2, state2, posStateNext);
									optimum = _optimum[cur + offset];
									if (curAndLenPrice < optimum.Price)
									{
										optimum.Price = curAndLenPrice;
										optimum.PosPrev = cur + lenTest + 1;
										optimum.BackPrev = 0;
										optimum.Prev1IsChar = true;
										optimum.Prev2 = true;
										optimum.PosPrev2 = cur;
										optimum.BackPrev2 = curBack + Base.KNumRepDistances;
									}
								}
							}
							offs += 2;
							if (offs == numDistancePairs)
								break;
						}
					}
				}
			}
		}

	    private bool ChangePair(uint smallDist, uint bigDist)
		{
			const int kDif = 7;
			return smallDist < (uint)1 << (32 - kDif) && bigDist >= smallDist << kDif;
		}

	    private void WriteEndMarker(uint posState)
		{
			if (!_writeEndMark)
				return;

			_isMatch[(_state.Index << Base.KNumPosStatesBitsMax) + posState].Encode(_rangeEncoder, 1);
			_isRep[_state.Index].Encode(_rangeEncoder, 0);
			_state.UpdateMatch();
			var len = Base.KMatchMinLen;
			_lenEncoder.Encode(_rangeEncoder, len - Base.KMatchMinLen, posState);
			uint posSlot = (1 << Base.KNumPosSlotBits) - 1;
			var lenToPosState = Base.GetLenToPosState(len);
			_posSlotEncoder[lenToPosState].Encode(_rangeEncoder, posSlot);
			var footerBits = 30;
			var posReduced = ((uint)1 << footerBits) - 1;
			_rangeEncoder.EncodeDirectBits(posReduced >> Base.KNumAlignBits, footerBits - Base.KNumAlignBits);
			_posAlignEncoder.ReverseEncode(_rangeEncoder, posReduced & Base.KAlignMask);
		}

	    private void Flush(uint nowPos)
		{
			ReleaseMfStream();
			WriteEndMarker(nowPos & _posStateMask);
			_rangeEncoder.FlushData();
			_rangeEncoder.FlushStream();
		}

		public void CodeOneBlock(out long inSize, out long outSize, out bool finished)
		{
			inSize = 0;
			outSize = 0;
			finished = true;

			if (_inStream != null)
			{
				_matchFinder.SetStream(_inStream);
				_matchFinder.Init();
				_needReleaseMfStream = true;
				_inStream = null;
				if (_trainSize > 0)
					_matchFinder.Skip(_trainSize);
			}

			if (_finished)
				return;
			_finished = true;


			var progressPosValuePrev = _nowPos64;
			if (_nowPos64 == 0)
			{
				if (_matchFinder.GetNumAvailableBytes() == 0)
				{
					Flush((uint)_nowPos64);
					return;
				}
				uint len, numDistancePairs; // it's not used
				ReadMatchDistances(out len, out numDistancePairs);
				var posState = (uint)_nowPos64 & _posStateMask;
				_isMatch[(_state.Index << Base.KNumPosStatesBitsMax) + posState].Encode(_rangeEncoder, 0);
				_state.UpdateChar();
				var curByte = _matchFinder.GetIndexByte((int)(0 - _additionalOffset));
				_literalEncoder.GetSubCoder((uint)_nowPos64, _previousByte).Encode(_rangeEncoder, curByte);
				_previousByte = curByte;
				_additionalOffset--;
				_nowPos64++;
			}
			if (_matchFinder.GetNumAvailableBytes() == 0)
			{
				Flush((uint)_nowPos64);
				return;
			}
			while (true)
			{
				uint pos;
				var len = GetOptimum((uint)_nowPos64, out pos);
				
				var posState = (uint)_nowPos64 & _posStateMask;
				var complexState = (_state.Index << Base.KNumPosStatesBitsMax) + posState;
				if (len == 1 && pos == 0xFFFFFFFF)
				{
					_isMatch[complexState].Encode(_rangeEncoder, 0);
					var curByte = _matchFinder.GetIndexByte((int)(0 - _additionalOffset));
					var subCoder = _literalEncoder.GetSubCoder((uint)_nowPos64, _previousByte);
					if (!_state.IsCharState())
					{
						var matchByte = _matchFinder.GetIndexByte((int)(0 - _repDistances[0] - 1 - _additionalOffset));
						subCoder.EncodeMatched(_rangeEncoder, matchByte, curByte);
					}
					else
						subCoder.Encode(_rangeEncoder, curByte);
					_previousByte = curByte;
					_state.UpdateChar();
				}
				else
				{
					_isMatch[complexState].Encode(_rangeEncoder, 1);
					if (pos < Base.KNumRepDistances)
					{
						_isRep[_state.Index].Encode(_rangeEncoder, 1);
						if (pos == 0)
						{
							_isRepG0[_state.Index].Encode(_rangeEncoder, 0);
							if (len == 1)
								_isRep0Long[complexState].Encode(_rangeEncoder, 0);
							else
								_isRep0Long[complexState].Encode(_rangeEncoder, 1);
						}
						else
						{
							_isRepG0[_state.Index].Encode(_rangeEncoder, 1);
							if (pos == 1)
								_isRepG1[_state.Index].Encode(_rangeEncoder, 0);
							else
							{
								_isRepG1[_state.Index].Encode(_rangeEncoder, 1);
								_isRepG2[_state.Index].Encode(_rangeEncoder, pos - 2);
							}
						}
						if (len == 1)
							_state.UpdateShortRep();
						else
						{
							_repMatchLenEncoder.Encode(_rangeEncoder, len - Base.KMatchMinLen, posState);
							_state.UpdateRep();
						}
						var distance = _repDistances[pos];
						if (pos != 0)
						{
							for (var i = pos; i >= 1; i--)
								_repDistances[i] = _repDistances[i - 1];
							_repDistances[0] = distance;
						}
					}
					else
					{
						_isRep[_state.Index].Encode(_rangeEncoder, 0);
						_state.UpdateMatch();
						_lenEncoder.Encode(_rangeEncoder, len - Base.KMatchMinLen, posState);
						pos -= Base.KNumRepDistances;
						var posSlot = GetPosSlot(pos);
						var lenToPosState = Base.GetLenToPosState(len);
						_posSlotEncoder[lenToPosState].Encode(_rangeEncoder, posSlot);

						if (posSlot >= Base.KStartPosModelIndex)
						{
							var footerBits = (int)((posSlot >> 1) - 1);
							var baseVal = (2 | (posSlot & 1)) << footerBits;
							var posReduced = pos - baseVal;

							if (posSlot < Base.KEndPosModelIndex)
								BitTreeEncoder.ReverseEncode(_posEncoders,
										baseVal - posSlot - 1, _rangeEncoder, footerBits, posReduced);
							else
							{
								_rangeEncoder.EncodeDirectBits(posReduced >> Base.KNumAlignBits, footerBits - Base.KNumAlignBits);
								_posAlignEncoder.ReverseEncode(_rangeEncoder, posReduced & Base.KAlignMask);
								_alignPriceCount++;
							}
						}
						var distance = pos;
						for (var i = Base.KNumRepDistances - 1; i >= 1; i--)
							_repDistances[i] = _repDistances[i - 1];
						_repDistances[0] = distance;
						_matchPriceCount++;
					}
					_previousByte = _matchFinder.GetIndexByte((int)(len - 1 - _additionalOffset));
				}
				_additionalOffset -= len;
				_nowPos64 += len;
				if (_additionalOffset == 0)
				{
					// if (!_fastMode)
					if (_matchPriceCount >= 1 << 7)
						FillDistancesPrices();
					if (_alignPriceCount >= Base.KAlignTableSize)
						FillAlignPrices();
					inSize = _nowPos64;
					outSize = _rangeEncoder.GetProcessedSizeAdd();
					if (_matchFinder.GetNumAvailableBytes() == 0)
					{
						Flush((uint)_nowPos64);
						return;
					}

					if (_nowPos64 - progressPosValuePrev >= 1 << 12)
					{
						_finished = false;
						finished = false;
						return;
					}
				}
			}
		}

	    private void ReleaseMfStream()
		{
			if (_matchFinder != null && _needReleaseMfStream)
			{
				_matchFinder.ReleaseStream();
				_needReleaseMfStream = false;
			}
		}

	    private void SetOutStream(System.IO.Stream outStream) { _rangeEncoder.SetStream(outStream); }
	    private void ReleaseOutStream() { _rangeEncoder.ReleaseStream(); }

	    private void ReleaseStreams()
		{
			ReleaseMfStream();
			ReleaseOutStream();
		}

	    private void SetStreams(System.IO.Stream inStream, System.IO.Stream outStream,
				long inSize, long outSize)
		{
			_inStream = inStream;
			_finished = false;
			Create();
			SetOutStream(outStream);
			Init();

			// if (!_fastMode)
			{
				FillDistancesPrices();
				FillAlignPrices();
			}

			_lenEncoder.SetTableSize(_numFastBytes + 1 - Base.KMatchMinLen);
			_lenEncoder.UpdateTables((uint)1 << _posStateBits);
			_repMatchLenEncoder.SetTableSize(_numFastBytes + 1 - Base.KMatchMinLen);
			_repMatchLenEncoder.UpdateTables((uint)1 << _posStateBits);

			_nowPos64 = 0;
		}


		public void Code(System.IO.Stream inStream, System.IO.Stream outStream,
			long inSize, long outSize, ICodeProgress progress)
		{
			_needReleaseMfStream = false;
			try
			{
				SetStreams(inStream, outStream, inSize, outSize);
				while (true)
				{
					long processedInSize;
					long processedOutSize;
					bool finished;
					CodeOneBlock(out processedInSize, out processedOutSize, out finished);
					if (finished)
						return;
					if (progress != null)
					{
						progress.SetProgress(processedInSize, processedOutSize);
					}
				}
			}
			finally
			{
				ReleaseStreams();
			}
		}

	    private const int KPropSize = 5;
	    private readonly byte[] _properties = new byte[KPropSize];

		public void WriteCoderProperties(System.IO.Stream outStream)
		{
			_properties[0] = (byte)((_posStateBits * 5 + _numLiteralPosStateBits) * 9 + _numLiteralContextBits);
			for (var i = 0; i < 4; i++)
				_properties[1 + i] = (byte)((_dictionarySize >> (8 * i)) & 0xFF);
			outStream.Write(_properties, 0, KPropSize);
		}

	    private readonly uint[] _tempPrices = new uint[Base.KNumFullDistances];
	    private uint _matchPriceCount;

	    private void FillDistancesPrices()
		{
			for (var i = Base.KStartPosModelIndex; i < Base.KNumFullDistances; i++)
			{ 
				var posSlot = GetPosSlot(i);
				var footerBits = (int)((posSlot >> 1) - 1);
				var baseVal = (2 | (posSlot & 1)) << footerBits;
				_tempPrices[i] = BitTreeEncoder.ReverseGetPrice(_posEncoders, 
					baseVal - posSlot - 1, footerBits, i - baseVal);
			}

			for (uint lenToPosState = 0; lenToPosState < Base.KNumLenToPosStates; lenToPosState++)
			{
				uint posSlot;
				var encoder = _posSlotEncoder[lenToPosState];
			
				var st = lenToPosState << Base.KNumPosSlotBits;
				for (posSlot = 0; posSlot < _distTableSize; posSlot++)
					_posSlotPrices[st + posSlot] = encoder.GetPrice(posSlot);
				for (posSlot = Base.KEndPosModelIndex; posSlot < _distTableSize; posSlot++)
					_posSlotPrices[st + posSlot] += ((posSlot >> 1) - 1 - Base.KNumAlignBits) << BitEncoder.KNumBitPriceShiftBits;

				var st2 = lenToPosState * Base.KNumFullDistances;
				uint i;
				for (i = 0; i < Base.KStartPosModelIndex; i++)
					_distancesPrices[st2 + i] = _posSlotPrices[st + i];
				for (; i < Base.KNumFullDistances; i++)
					_distancesPrices[st2 + i] = _posSlotPrices[st + GetPosSlot(i)] + _tempPrices[i];
			}
			_matchPriceCount = 0;
		}

	    private void FillAlignPrices()
		{
			for (uint i = 0; i < Base.KAlignTableSize; i++)
				_alignPrices[i] = _posAlignEncoder.ReverseGetPrice(i);
			_alignPriceCount = 0;
		}


	    private static readonly string[] KMatchFinderIDs = 
		{
			"BT2",
			"BT4",
		};

	    private static int FindMatchFinder(string s)
		{
			for (var m = 0; m < KMatchFinderIDs.Length; m++)
				if (s == KMatchFinderIDs[m])
					return m;
			return -1;
		}
	
		public void SetCoderProperties(CoderPropId[] propIDs, object[] properties)
		{
			for (uint i = 0; i < properties.Length; i++)
			{
				var prop = properties[i];
				switch (propIDs[i])
				{
					case CoderPropId.NumFastBytes:
					{
						if (!(prop is int))
							throw new InvalidParamException();
						var numFastBytes = (int)prop;
						if (numFastBytes < 5 || numFastBytes > Base.KMatchMaxLen)
							throw new InvalidParamException();
						_numFastBytes = (uint)numFastBytes;
						break;
					}
					case CoderPropId.Algorithm:
					{
						/*
						if (!(prop is Int32))
							throw new InvalidParamException();
						Int32 maximize = (Int32)prop;
						_fastMode = (maximize == 0);
						_maxMode = (maximize >= 2);
						*/
						break;
					}
					case CoderPropId.MatchFinder:
					{
						if (!(prop is string))
							throw new InvalidParamException();
						var matchFinderIndexPrev = _matchFinderType;
						var m = FindMatchFinder(((string)prop).ToUpper());
						if (m < 0)
							throw new InvalidParamException();
						_matchFinderType = (EMatchFinderType)m;
						if (_matchFinder != null && matchFinderIndexPrev != _matchFinderType)
							{
							_dictionarySizePrev = 0xFFFFFFFF;
							_matchFinder = null;
							}
						break;
					}
					case CoderPropId.DictionarySize:
					{
						const int kDicLogSizeMaxCompress = 30;
						if (!(prop is int))
							throw new InvalidParamException(); ;
						var dictionarySize = (int)prop;
						if (dictionarySize < (uint)(1 << Base.KDicLogSizeMin) ||
							dictionarySize > (uint)(1 << kDicLogSizeMaxCompress))
							throw new InvalidParamException();
						_dictionarySize = (uint)dictionarySize;
						int dicLogSize;
						for (dicLogSize = 0; dicLogSize < (uint)kDicLogSizeMaxCompress; dicLogSize++)
							if (dictionarySize <= (uint)1 << dicLogSize)
								break;
						_distTableSize = (uint)dicLogSize * 2;
						break;
					}
					case CoderPropId.PosStateBits:
					{
						if (!(prop is int))
							throw new InvalidParamException();
						var v = (int)prop;
						if (v < 0 || v > (uint)Base.KNumPosStatesBitsEncodingMax)
							throw new InvalidParamException();
						_posStateBits = v;
						_posStateMask = ((uint)1 << _posStateBits) - 1;
						break;
					}
					case CoderPropId.LitPosBits:
					{
						if (!(prop is int))
							throw new InvalidParamException();
						var v = (int)prop;
						if (v < 0 || v > Base.KNumLitPosStatesBitsEncodingMax)
							throw new InvalidParamException();
						_numLiteralPosStateBits = v;
						break;
					}
					case CoderPropId.LitContextBits:
					{
						if (!(prop is int))
							throw new InvalidParamException();
						var v = (int)prop;
						if (v < 0 || v > Base.KNumLitContextBitsMax)
							throw new InvalidParamException(); ;
						_numLiteralContextBits = v;
						break;
					}
					case CoderPropId.EndMarker:
					{
						if (!(prop is bool))
							throw new InvalidParamException();
						SetWriteEndMarkerMode((bool)prop);
						break;
					}
					default:
						throw new InvalidParamException();
				}
			}
		}

	    private uint _trainSize;
		public void SetTrainSize(uint trainSize)
		{
			_trainSize = trainSize;
		}
		
	}
}
