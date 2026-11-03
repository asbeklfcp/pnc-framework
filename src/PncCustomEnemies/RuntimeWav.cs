using System;
using System.IO;
using UnityEngine;

namespace PncEdi;

internal static class RuntimeWav
{
	internal static AudioClip Load(string path, string clipName)
	{
		if (!File.Exists(path)) throw new FileNotFoundException("Capture sound was not found", path);
		byte[] bytes = File.ReadAllBytes(path);
		if (bytes.Length < 44 || ReadAscii(bytes, 0, 4) != "RIFF" || ReadAscii(bytes, 8, 4) != "WAVE")
			throw new InvalidDataException("Only RIFF/WAVE capture sounds are supported: " + path);

		int channels = 0;
		int sampleRate = 0;
		int bitsPerSample = 0;
		int format = 0;
		int dataOffset = -1;
		int dataLength = 0;
		for (int offset = 12; offset + 8 <= bytes.Length;)
		{
			string id = ReadAscii(bytes, offset, 4);
			int size = ReadInt32(bytes, offset + 4);
			int body = offset + 8;
			if (size < 0 || body + size > bytes.Length) throw new InvalidDataException("Invalid WAV chunk in " + path);
			if (id == "fmt " && size >= 16)
			{
				format = ReadUInt16(bytes, body);
				channels = ReadUInt16(bytes, body + 2);
				sampleRate = ReadInt32(bytes, body + 4);
				bitsPerSample = ReadUInt16(bytes, body + 14);
			}
			else if (id == "data")
			{
				dataOffset = body;
				dataLength = size;
			}
			offset = body + size + (size & 1);
		}
		if (format != 1 || channels < 1 || sampleRate < 1 || bitsPerSample != 16 || dataOffset < 0)
			throw new InvalidDataException("Capture sound must be 16-bit PCM WAV: " + path);

		int sampleValueCount = dataLength / 2;
		float[] samples = new float[sampleValueCount];
		for (int i = 0; i < sampleValueCount; i++)
			samples[i] = (short)(bytes[dataOffset + i * 2] | bytes[dataOffset + i * 2 + 1] << 8) / 32768f;
		AudioClip clip = AudioClip.Create(clipName, sampleValueCount / channels, channels, sampleRate, false);
		clip.SetData(samples, 0);
		return clip;
	}

	private static string ReadAscii(byte[] bytes, int offset, int count) => System.Text.Encoding.ASCII.GetString(bytes, offset, count);
	private static int ReadInt32(byte[] bytes, int offset) => bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24;
	private static int ReadUInt16(byte[] bytes, int offset) => bytes[offset] | bytes[offset + 1] << 8;
}
