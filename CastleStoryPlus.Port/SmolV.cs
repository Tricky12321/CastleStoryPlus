using System;
using System.Collections.Generic;
using System.IO;

namespace CastleStoryPlus.Port;

// SMOL-V (encoding version 1), the compressed SPIR-V Unity keeps a Vulkan program's stages in. Written from the format
// (smol-v by Aras Pranckevičius, MIT): checked against Unity 6's own programs, which decode to the length their header
// says and encode back byte for byte.
//   header: 'SMOL', SPIR-V version | encoding version << 24, generator, bound, schema, decoded size in bytes;
//   per instruction: length and (remapped) opcode in one varint, then the type (varint), the result (zigzag delta from
//   the previous result), for decorations the target (zigzag delta from the previous one), a number of operands as
//   zigzag deltas from the last result, the rest as varints or raw words, depending on the opcode.
public static class SmolV
{
	private const uint SmolMagic = 0x534D4F4C;
	private const uint SpirvMagic = 0x07230203;
	private const int EncodingVersion = 1;

	private const int OpDecorate = 71;
	private const int OpMemberDecorate = 72;
	private const int OpVectorShuffle = 79;
	private const int OpLoad = 61;
	private const int OpAccessChain = 65;
	// smol-v's own opcode for a vector shuffle of at most four components, each below 4: one byte for the swizzle.
	private const int OpVectorShuffleCompact = 13;
	private const int DecorationOffset = 35;

	// Per opcode: has a result (bit 0), has a type (bit 1), how many operands are deltas from the result (bits 2-5),
	// whether the rest are varints (bit 6). Opcodes past the table have none of these.
	private static readonly byte[] OpData = Hex(
		"000300400000000040030001434b40404040034141414141414141414141414141414141414141400303030327430303" +
		"0303032703034303002703430347480000430303030303404001000003474b4b27474b070303034b4b4f4f4b4b4f4f4b" +
		"4f4f4b4c0707070b070b070703070707070707070707070707070747070307070b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b" +
		"0b0b0b0b0b0b0b0b0b03070707070707070b0b0b0b0b0b0b070f0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b" +
		"03030b0b0b0b0b0b07130f0f07070303030303030303030303030000000003030c080303000303030303030303030303" +
		"030303030303484401044c00000000000000030300030303030303030303030303030303030303030000030303030300" +
		"00030303030303030300000303000003034b4b4f4f4b4b4f4f4b4f4f0700030003030303030303034348034040474747" +
		"47474747474747474747474747474747474747474747474747474747474747");
	// The most frequent opcodes get small numbers (pairs swapped both ways).
	private static readonly int[,] Swaps =
	{
		{ OpDecorate, 0 }, { OpLoad, 1 }, { 62, 2 }, { OpAccessChain, 3 }, { OpVectorShuffle, 4 }, { OpMemberDecorate, 7 },
		{ 248, 8 }, { 59, 9 }, { 133, 10 }, { 129, 11 }, { 32, 14 }, { 127, 15 },
	};

	public static byte[] Encode(byte[] spirv)
	{
		uint[] words = new uint[spirv.Length / 4];
		Buffer.BlockCopy(spirv, 0, words, 0, words.Length * 4);
		if (words.Length < 5 || words[0] != SpirvMagic)
		{
			throw new InvalidDataException("not SPIR-V");
		}
		var output = new MemoryStream();
		var writer = new BinaryWriter(output);
		writer.Write(SmolMagic);
		writer.Write((words[1] & 0x00FFFFFF) | (EncodingVersion << 24));
		writer.Write(words[2]);
		writer.Write(words[3]);
		writer.Write(words[4]);
		writer.Write(spirv.Length);

		uint previousResult = 0;
		uint previousDecorate = 0;
		int position = 5;
		while (position < words.Length)
		{
			int length = (int)(words[position] >> 16);
			int op = (int)(words[position] & 0xFFFF);
			if (length < 1 || position + length > words.Length)
			{
				throw new InvalidDataException("bad SPIR-V instruction at word " + position);
			}
			if (op == OpVectorShuffle && length <= 9 && IsCompactSwizzle(words, position + 5, position + length))
			{
				op = OpVectorShuffleCompact;
			}
			int encodedLength = EncodeLength(op, length);
			int remapped = Remap(op);
			WriteVarint(output, (uint)(((encodedLength >> 4) << 20) | ((remapped >> 4) << 8) | ((encodedLength & 0xF) << 4) | (remapped & 0xF)));

			byte data = Data(op);
			int operand = position + 1;
			if ((data & 2) != 0)
			{
				WriteVarint(output, words[operand++]);
			}
			if ((data & 1) != 0)
			{
				WriteVarint(output, Zig(words[operand] - previousResult));
				previousResult = words[operand++];
			}
			if (op == OpDecorate || op == OpMemberDecorate)
			{
				WriteVarint(output, Zig(words[operand] - previousDecorate));
				previousDecorate = words[operand++];
			}
			if (op == OpMemberDecorate)
			{
				// A row of member decorations of the same type at once: count, then per member the index (delta), the
				// decoration, its length if not known, and its operands (an offset as delta from the previous one).
				uint target = words[position + 1];
				int count = 0;
				int scan = position;
				while (scan < words.Length && (words[scan] & 0xFFFF) == OpMemberDecorate && words[scan + 1] == target && count < 255)
				{
					count++;
					scan += (int)(words[scan] >> 16);
				}
				output.WriteByte((byte)count);
				uint previousIndex = 0;
				uint previousOffset = 0;
				scan = position;
				for (int i = 0; i < count; i++)
				{
					int memberLength = (int)(words[scan] >> 16);
					uint index = words[scan + 2];
					uint decoration = words[scan + 3];
					WriteVarint(output, index - previousIndex);
					previousIndex = index;
					WriteVarint(output, decoration);
					int extra = DecorationExtraOperands(decoration);
					if (extra < 0)
					{
						WriteVarint(output, (uint)(memberLength - 4));
					}
					else if (extra + 4 != memberLength)
					{
						throw new InvalidDataException("bad OpMemberDecorate length");
					}
					if (decoration == DecorationOffset)
					{
						WriteVarint(output, words[scan + 4] - previousOffset);
						previousOffset = words[scan + 4];
					}
					else
					{
						for (int w = 4; w < memberLength; w++)
						{
							WriteVarint(output, words[scan + w]);
						}
					}
					scan += memberLength;
				}
				position = scan;
				continue;
			}
			int end = position + length;
			int deltas = (data >> 2) & 0xF;
			for (int i = 0; i < deltas && operand < end; i++, operand++)
			{
				WriteVarint(output, Zig(previousResult - words[operand]));
			}
			if (op == OpVectorShuffleCompact)
			{
				int swizzle = 0;
				for (int i = 0; i < 4; i++)
				{
					uint component = operand + i < end ? words[operand + i] : 0;
					swizzle |= (int)component << (6 - i * 2);
				}
				output.WriteByte((byte)swizzle);
			}
			else if ((data & 0x40) != 0)
			{
				for (; operand < end; operand++)
				{
					WriteVarint(output, words[operand]);
				}
			}
			else
			{
				for (; operand < end; operand++)
				{
					writer.Write(words[operand]);
				}
			}
			position = end;
		}
		return output.ToArray();
	}

	// The SPIR-V of a SMOL-V stream (development: checks the encoder against Unity's own programs). Encoding
	// version 0 (the game's own 5.6 programs) knows the opcodes up to OpModuleProcessed (330) only, and the streams
	// from before smol-v's change of 31-08-2016 (the ones Unity 5.6 carries) encode three things differently: the
	// decoration targets are plain deltas, member decorations are not batched, and only the branch and barrier
	// operands are zigzag deltas. A version 0 stream is read the old way first, then the later way.
	public static byte[] Decode(byte[] smol)
	{
		if (BitConverter.ToUInt32(smol, 4) >> 24 == 0)
		{
			try
			{
				return Decode(smol, true);
			}
			catch (Exception e) when (e is InvalidDataException || e is IOException || e is IndexOutOfRangeException || e is ArgumentException)
			{
				return Decode(smol, false);
			}
		}
		return Decode(smol, false);
	}

	private static bool ZigzagBeforeZero(int op)
	{
		// ControlBarrier, MemoryBarrier, LoopMerge, SelectionMerge, Branch, BranchConditional, MemoryNamedBarrier.
		return op == 224 || op == 225 || op == 246 || op == 247 || op == 249 || op == 250 || op == 329;
	}

	private static byte[] Decode(byte[] smol, bool beforeZero)
	{
		var reader = new BinaryReader(new MemoryStream(smol));
		if (reader.ReadUInt32() != SmolMagic)
		{
			throw new InvalidDataException("not SMOL-V");
		}
		uint version = reader.ReadUInt32();
		if (version >> 24 > EncodingVersion)
		{
			throw new InvalidDataException("SMOL-V encoding version " + (version >> 24));
		}
		int knownOps = version >> 24 == 0 ? 331 : OpData.Length;
		var words = new List<uint> { SpirvMagic, version & 0x00FFFFFF, reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32() };
		int decodedSize = reader.ReadInt32();
		Stream input = reader.BaseStream;
		uint previousResult = 0;
		uint previousDecorate = 0;
		while (input.Position < input.Length)
		{
			uint lengthOp = ReadVarint(input);
			int length = (int)(((lengthOp >> 20) << 4) | ((lengthOp >> 4) & 0xF));
			int op = Remap((int)(((lengthOp >> 4) & 0xFF0) | (lengthOp & 0xF)));
			length = DecodeLength(op, length);
			byte data = op < knownOps ? Data(op) : (byte)0;
			var instruction = new uint[length];
			int operand = 1;
			if ((data & 2) != 0)
			{
				instruction[operand++] = ReadVarint(input);
			}
			if ((data & 1) != 0)
			{
				previousResult += Unzig(ReadVarint(input));
				instruction[operand++] = previousResult;
			}
			if (op == OpDecorate || op == OpMemberDecorate)
			{
				uint delta = ReadVarint(input);
				previousDecorate += beforeZero ? delta : Unzig(delta);
				instruction[operand++] = previousDecorate;
			}
			if (op == OpMemberDecorate && !beforeZero)
			{
				int count = input.ReadByte();
				uint previousIndex = 0;
				uint previousOffset = 0;
				for (int i = 0; i < count; i++)
				{
					previousIndex += ReadVarint(input);
					uint decoration = ReadVarint(input);
					int extra = DecorationExtraOperands(decoration);
					int memberLength = 4 + (extra < 0 ? (int)ReadVarint(input) : extra);
					words.Add((uint)(memberLength << 16) | OpMemberDecorate);
					words.Add(previousDecorate);
					words.Add(previousIndex);
					words.Add(decoration);
					if (decoration == DecorationOffset)
					{
						previousOffset += ReadVarint(input);
						words.Add(previousOffset);
					}
					else
					{
						for (int w = 4; w < memberLength; w++)
						{
							words.Add(ReadVarint(input));
						}
					}
				}
				continue;
			}
			int deltas = (data >> 2) & 0xF;
			for (int i = 0; i < deltas && operand < length; i++)
			{
				uint delta = ReadVarint(input);
				instruction[operand++] = previousResult - (beforeZero && !ZigzagBeforeZero(op) ? delta : Unzig(delta));
			}
			if (op == OpVectorShuffleCompact)
			{
				int swizzle = input.ReadByte();
				for (int i = 0; operand < length; i++)
				{
					instruction[operand++] = (uint)(swizzle >> (6 - i * 2)) & 3;
				}
				op = OpVectorShuffle;
			}
			else if ((data & 0x40) != 0)
			{
				while (operand < length)
				{
					instruction[operand++] = ReadVarint(input);
				}
			}
			else
			{
				while (operand < length)
				{
					instruction[operand++] = reader.ReadUInt32();
				}
			}
			instruction[0] = (uint)(length << 16) | (uint)op;
			words.AddRange(instruction);
		}
		var result = new byte[words.Count * 4];
		Buffer.BlockCopy(words.ToArray(), 0, result, 0, result.Length);
		if (result.Length != decodedSize)
		{
			throw new InvalidDataException("decoded " + result.Length + " bytes, the header says " + decodedSize);
		}
		return result;
	}

	private static bool IsCompactSwizzle(uint[] words, int start, int end)
	{
		for (int i = start; i < end; i++)
		{
			if (words[i] > 3)
			{
				return false;
			}
		}
		return true;
	}

	private static byte Data(int op)
	{
		return op < OpData.Length ? OpData[op] : (byte)0;
	}

	private static int Remap(int op)
	{
		for (int i = 0; i < Swaps.GetLength(0); i++)
		{
			if (op == Swaps[i, 0])
			{
				return Swaps[i, 1];
			}
			if (op == Swaps[i, 1])
			{
				return Swaps[i, 0];
			}
		}
		return op;
	}

	// The length is stored minus what the opcode nearly always has.
	private static int EncodeLength(int op, int length)
	{
		return length - 1 - LengthBias(op);
	}

	private static int DecodeLength(int op, int length)
	{
		return length + 1 + LengthBias(op);
	}

	private static int LengthBias(int op)
	{
		switch (op)
		{
			case OpVectorShuffle:
			case OpVectorShuffleCompact:
				return 4;
			case OpDecorate:
				return 2;
			case OpLoad:
			case OpAccessChain:
				return 3;
			default:
				return 0;
		}
	}

	// Operands a member decoration always has (-1: not known, its length is stored).
	private static int DecorationExtraOperands(uint decoration)
	{
		if (decoration == 0 || (decoration >= 2 && decoration <= 5))
		{
			return 0;
		}
		if (decoration >= 29 && decoration <= 37)
		{
			return 1;
		}
		return -1;
	}

	private static uint Zig(uint value)
	{
		int signed = (int)value;
		return (uint)((signed << 1) ^ (signed >> 31));
	}

	private static uint Unzig(uint value)
	{
		return (value >> 1) ^ (uint)-(int)(value & 1);
	}

	private static void WriteVarint(Stream output, uint value)
	{
		while (value > 0x7F)
		{
			output.WriteByte((byte)((value & 0x7F) | 0x80));
			value >>= 7;
		}
		output.WriteByte((byte)value);
	}

	private static uint ReadVarint(Stream input)
	{
		uint value = 0;
		int shift = 0;
		while (true)
		{
			int b = input.ReadByte();
			if (b < 0)
			{
				throw new EndOfStreamException();
			}
			value |= (uint)(b & 0x7F) << shift;
			if (b < 0x80)
			{
				return value;
			}
			shift += 7;
		}
	}

	private static byte[] Hex(string hex)
	{
		var bytes = new byte[hex.Length / 2];
		for (int i = 0; i < bytes.Length; i++)
		{
			bytes[i] = System.Convert.ToByte(hex.Substring(i * 2, 2), 16);
		}
		return bytes;
	}
}
