using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AirPlayReceiver.Protocol;

/// <summary>
/// Minimal Apple binary property list (<c>bplist00</c>) reader and writer,
/// tailored to the small subset AirPlay 2 SETUP uses: dictionaries, arrays,
/// integers, booleans, ASCII strings and data blobs.
///
/// Parsed values map to: <see cref="Dictionary{String,Object}"/>,
/// <see cref="List{Object}"/>, <see cref="long"/>, <see cref="bool"/>,
/// <see cref="string"/>, <see cref="byte"/>[] (data), and null.
///
/// Format reference: CoreFoundation CFBinaryPList (8-byte "bplist00" header,
/// object table, offset table, 32-byte trailer).
/// </summary>
public static class BinaryPlist
{
    // ── Parsing ───────────────────────────────────────────────────────────────

    public static object? Parse(byte[] buffer)
    {
        if (buffer.Length < 40 || Encoding.ASCII.GetString(buffer, 0, 8) != "bplist00")
            throw new FormatException("Not a binary plist");

        int trailer = buffer.Length - 32;
        int offsetSize = buffer[trailer + 6];
        int objRefSize = buffer[trailer + 7];
        long numObjects        = ReadBE(buffer, trailer + 8,  8);
        long topObject         = ReadBE(buffer, trailer + 16, 8);
        long offsetTableOffset = ReadBE(buffer, trailer + 24, 8);

        var offsets = new long[numObjects];
        for (int i = 0; i < numObjects; i++)
            offsets[i] = ReadBE(buffer, (int)(offsetTableOffset + i * offsetSize), offsetSize);

        return ReadObject(buffer, offsets, objRefSize, (int)topObject);
    }

    private static object? ReadObject(byte[] buf, long[] offsets, int refSize, int index)
    {
        int pos = (int)offsets[index];
        byte marker = buf[pos];
        int type = marker >> 4;
        int n    = marker & 0x0F;

        switch (type)
        {
            case 0x0:
                return marker switch { 0x09 => true, 0x08 => false, _ => null };

            case 0x1: // integer (2^n bytes, big-endian)
                return ReadBE(buf, pos + 1, 1 << n);

            case 0x2: // real
                return 1 << n == 4
                    ? BitConverter.ToSingle(ReversedSlice(buf, pos + 1, 4))
                    : BitConverter.ToDouble(ReversedSlice(buf, pos + 1, 8));

            case 0x4: // data
            {
                int p = pos + 1;
                int len = ReadLength(buf, n, ref p);
                var data = new byte[len];
                Array.Copy(buf, p, data, 0, len);
                return data;
            }

            case 0x5: // ASCII string
            {
                int p = pos + 1;
                int len = ReadLength(buf, n, ref p);
                return Encoding.ASCII.GetString(buf, p, len);
            }

            case 0x6: // UTF-16BE string
            {
                int p = pos + 1;
                int len = ReadLength(buf, n, ref p);
                return Encoding.BigEndianUnicode.GetString(buf, p, len * 2);
            }

            case 0xA: // array
            {
                int p = pos + 1;
                int count = ReadLength(buf, n, ref p);
                var list = new List<object?>(count);
                for (int i = 0; i < count; i++)
                {
                    int r = (int)ReadBE(buf, p + i * refSize, refSize);
                    list.Add(ReadObject(buf, offsets, refSize, r));
                }
                return list;
            }

            case 0xD: // dictionary
            {
                int p = pos + 1;
                int count = ReadLength(buf, n, ref p);
                var dict = new Dictionary<string, object?>(count);
                for (int i = 0; i < count; i++)
                {
                    int keyRef = (int)ReadBE(buf, p + i * refSize, refSize);
                    int valRef = (int)ReadBE(buf, p + (count + i) * refSize, refSize);
                    string key = (string)ReadObject(buf, offsets, refSize, keyRef)!;
                    dict[key] = ReadObject(buf, offsets, refSize, valRef);
                }
                return dict;
            }

            default:
                throw new FormatException($"Unsupported bplist marker 0x{marker:x2}");
        }
    }

    /// <summary>Reads a collection/data length: inline nibble, or a follow-on int object when the nibble is 0xF.</summary>
    private static int ReadLength(byte[] buf, int nibble, ref int pos)
    {
        if (nibble != 0xF) return nibble;
        byte intMarker = buf[pos];
        int intBytes = 1 << (intMarker & 0x0F);
        int len = (int)ReadBE(buf, pos + 1, intBytes);
        pos += 1 + intBytes;
        return len;
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    public static byte[] Write(object? root)
    {
        var objects = new List<object?>();
        var arrayChildren = new Dictionary<object, List<int>>(ReferenceEqualityComparer.Instance);
        var dictChildren  = new Dictionary<object, (List<int> keys, List<int> vals)>(ReferenceEqualityComparer.Instance);

        Flatten(root, objects, arrayChildren, dictChildren);

        int objRefSize = ByteWidth(objects.Count);

        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("bplist00"));

        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = ms.Position;
            WriteObject(ms, objects[i], objRefSize, arrayChildren, dictChildren);
        }

        long offsetTableOffset = ms.Position;
        int offsetSize = ByteWidth(offsetTableOffset);
        foreach (long off in offsets)
            WriteBE(ms, off, offsetSize);

        // 32-byte trailer: 6 unused, offsetSize, objRefSize, numObjects, topObject, offsetTableOffset.
        ms.Write(new byte[6]);
        ms.WriteByte((byte)offsetSize);
        ms.WriteByte((byte)objRefSize);
        WriteBE(ms, objects.Count, 8);
        WriteBE(ms, 0, 8); // root is object 0
        WriteBE(ms, offsetTableOffset, 8);

        return ms.ToArray();
    }

    private static int Flatten(
        object? o,
        List<object?> objects,
        Dictionary<object, List<int>> arrayChildren,
        Dictionary<object, (List<int>, List<int>)> dictChildren)
    {
        int index = objects.Count;
        objects.Add(o);

        switch (o)
        {
            case Dictionary<string, object?> dict:
            {
                var keys = new List<int>();
                var vals = new List<int>();
                dictChildren[dict] = (keys, vals);
                foreach (var kv in dict)
                {
                    keys.Add(Flatten(kv.Key, objects, arrayChildren, dictChildren));
                    vals.Add(Flatten(kv.Value, objects, arrayChildren, dictChildren));
                }
                break;
            }
            case List<object?> list:
            {
                var children = new List<int>();
                arrayChildren[list] = children;
                foreach (var item in list)
                    children.Add(Flatten(item, objects, arrayChildren, dictChildren));
                break;
            }
        }
        return index;
    }

    private static void WriteObject(
        Stream s, object? o, int refSize,
        Dictionary<object, List<int>> arrayChildren,
        Dictionary<object, (List<int> keys, List<int> vals)> dictChildren)
    {
        switch (o)
        {
            case null:
                s.WriteByte(0x00);
                break;

            case bool b:
                s.WriteByte((byte)(b ? 0x09 : 0x08));
                break;

            case string str:
            {
                byte[] bytes = Encoding.ASCII.GetBytes(str);
                WriteMarkerWithLength(s, 0x50, bytes.Length);
                s.Write(bytes);
                break;
            }

            case byte[] data:
                WriteMarkerWithLength(s, 0x40, data.Length);
                s.Write(data);
                break;

            case int or long or short or uint or ushort or byte:
                WriteIntObject(s, Convert.ToInt64(o));
                break;

            case List<object?> list:
            {
                var children = arrayChildren[list];
                WriteMarkerWithLength(s, 0xA0, children.Count);
                foreach (int r in children) WriteBE(s, r, refSize);
                break;
            }

            case Dictionary<string, object?> dict:
            {
                var (keys, vals) = dictChildren[dict];
                WriteMarkerWithLength(s, 0xD0, keys.Count);
                foreach (int r in keys) WriteBE(s, r, refSize);
                foreach (int r in vals) WriteBE(s, r, refSize);
                break;
            }

            default:
                throw new NotSupportedException($"bplist cannot serialize {o.GetType()}");
        }
    }

    private static void WriteMarkerWithLength(Stream s, byte baseMarker, int length)
    {
        if (length < 15)
        {
            s.WriteByte((byte)(baseMarker | length));
        }
        else
        {
            s.WriteByte((byte)(baseMarker | 0x0F));
            WriteIntObject(s, length);
        }
    }

    private static void WriteIntObject(Stream s, long v)
    {
        if (v >= 0 && v <= 0xFF)            { s.WriteByte(0x10); WriteBE(s, v, 1); }
        else if (v >= 0 && v <= 0xFFFF)     { s.WriteByte(0x11); WriteBE(s, v, 2); }
        else if (v >= 0 && v <= 0xFFFFFFFFL){ s.WriteByte(0x12); WriteBE(s, v, 4); }
        else                                { s.WriteByte(0x13); WriteBE(s, v, 8); }
    }

    // ── Low-level helpers ─────────────────────────────────────────────────────

    private static long ReadBE(byte[] buf, int offset, int length)
    {
        long v = 0;
        for (int i = 0; i < length; i++) v = (v << 8) | buf[offset + i];
        return v;
    }

    private static void WriteBE(Stream s, long value, int length)
    {
        for (int i = length - 1; i >= 0; i--) s.WriteByte((byte)((value >> (i * 8)) & 0xFF));
    }

    private static byte[] ReversedSlice(byte[] buf, int offset, int length)
    {
        var slice = new byte[length];
        for (int i = 0; i < length; i++) slice[i] = buf[offset + length - 1 - i];
        return slice;
    }

    private static int ByteWidth(long maxValue)
        => maxValue <= 0xFF ? 1 : maxValue <= 0xFFFF ? 2 : maxValue <= 0xFFFFFFFFL ? 4 : 8;

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
