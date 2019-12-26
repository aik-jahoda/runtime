// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0.
// See THIRD-PARTY-NOTICES.TXT in the project root for license information.

using System.Collections.Generic;
using System.Diagnostics;

namespace System.Net.Http.HPack
{
    internal class HPackEncoder
    {
        private IEnumerator<KeyValuePair<string, string>> _enumerator;

        private readonly DynamicTable _dynamicTable;
        private readonly uint _maxDynamicTableSize = 4096;

        private uint? _headerTableSizeToConfirm = null;

        public HPackEncoder(int maxDynamicTableSize = HPackDecoder.DefaultHeaderTableSize)
        {
            _dynamicTable = new DynamicTable(maxDynamicTableSize);
        }

        public bool BeginEncode(IEnumerable<KeyValuePair<string, string>> headers, Span<byte> buffer, out int length)
        {
            _enumerator = headers.GetEnumerator();
            _enumerator.MoveNext();

            return Encode(buffer, out length);
        }

        public bool BeginEncode(int statusCode, IEnumerable<KeyValuePair<string, string>> headers, Span<byte> buffer, out int length)
        {
            _enumerator = headers.GetEnumerator();
            _enumerator.MoveNext();

            int statusCodeLength = EncodeStatusCode(statusCode, buffer);
            bool done = Encode(buffer.Slice(statusCodeLength), throwIfNoneEncoded: false, out int headersLength);
            length = statusCodeLength + headersLength;

            return done;
        }

        public bool Encode(Span<byte> buffer, out int length)
        {
            return Encode(buffer, throwIfNoneEncoded: true, out length);
        }

        private bool Encode(Span<byte> buffer, bool throwIfNoneEncoded, out int length)
        {
            Debug.Assert(_enumerator != null);
            int currentLength = 0;
            do
            {
                if (!EncodeHeader(_enumerator.Current.Key, _enumerator.Current.Value, buffer.Slice(currentLength), out int headerLength))
                {
                    if (currentLength == 0 && throwIfNoneEncoded)
                    {
                        throw new HPackEncodingException(SR.net_http_hpack_encode_failure);
                    }

                    length = currentLength;
                    return false;
                }

                currentLength += headerLength;
            }
            while (_enumerator.MoveNext());

            length = currentLength;

            return true;
        }

        private int EncodeStatusCode(int statusCode, Span<byte> buffer)
        {
            switch (statusCode)
            {
                // Status codes which exist in the HTTP/2 StaticTable.
                case 200:
                case 204:
                case 206:
                case 304:
                case 400:
                case 404:
                case 500:
                    buffer[0] = (byte)(0x80 | StaticTable.StatusIndex[statusCode]);
                    return 1;
                default:
                    // Send as Literal Header Field Without Indexing - Indexed Name
                    buffer[0] = 0x08;

                    ReadOnlySpan<byte> statusBytes = StatusCodes.ToStatusBytes(statusCode);
                    buffer[1] = (byte)statusBytes.Length;
                    statusBytes.CopyTo(buffer.Slice(2));

                    return 2 + statusBytes.Length;
            }
        }

        private bool EncodeHeader(string name, string value, Span<byte> buffer, out int length)
        {
            int i = 0;
            length = 0;

            if (buffer.Length == 0)
            {
                return false;
            }

            buffer[i++] = 0;

            if (i == buffer.Length)
            {
                return false;
            }

            if (!EncodeStringLiteral(name, buffer.Slice(i), out int nameLength, lowercase: true, onlyAscii: true))
            {
                return false;
            }

            i += nameLength;

            if (i >= buffer.Length)
            {
                return false;
            }

            if (!EncodeStringLiteral(value, buffer.Slice(i), out int valueLength))
            {
                return false;
            }

            i += valueLength;

            length = i;
            return true;
        }

        public HeaderTableIndex GetIndex(byte[] name, byte[] value)
        {
            return _dynamicTable.GetIndex(name, value);
        }

        public bool EncodeLiteralField(HeaderTableIndex index, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value, Span<byte> destination, out int bytesWritten)
        {
            if (index.HeaderWithValueIndex.HasValue)
            {
                return EncodeIndexedHeaderField(index.HeaderWithValueIndex.GetValueOrDefault(), destination, out bytesWritten);
            }

            if (index.HeaderIndex.HasValue)
            {
                bool encodeHeaderIndexSuccess = EncodeLiteralHeaderFieldWithIncrementalIndexing(index.HeaderIndex.GetValueOrDefault(), value, destination, out bytesWritten);

                if (encodeHeaderIndexSuccess)
                {
                    _dynamicTable.Insert(index.HeaderIndex.GetValueOrDefault(), value);
                }

                return encodeHeaderIndexSuccess;
            }

            bool success = EncodeLiteralHeaderFieldWithIncrementalIndexingNewName(name, value, destination, out bytesWritten);

            if (success)
            {
                _dynamicTable.Insert(name, value);
            }

            return success;

        }

        public bool WriteHeadersBegin(Span<byte> destination, out int bytesWritten)
        {
            if (_headerTableSizeToConfirm.HasValue)
            {
                if (EncodeDynamicTableSizeUpdate(_headerTableSizeToConfirm.GetValueOrDefault(), destination, out bytesWritten))
                {
                    _headerTableSizeToConfirm = null;
                    return true;
                }

                bytesWritten = 0;
                return false;
            }

            bytesWritten = 0;
            return true;
        }

        // Things we should add:
        // * Huffman encoding
        //
        // Things we should consider adding:
        // * Dynamic table encoding:
        //   This would make the encoder stateful, which complicates things significantly.
        //   Additionally, it's not clear exactly what strings we would add to the dynamic table
        //   without some additional guidance from the user about this.
        //   So for now, don't do dynamic encoding.

        /// <summary>Encodes an "Indexed Header Field".</summary>
        public static bool EncodeIndexedHeaderField(int index, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.1
            // ----------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 1 |        Index (7+)         |
            // +---+---------------------------+

            if (destination.Length != 0)
            {
                destination[0] = 0x80;
                return IntegerEncoder.Encode(index, 7, destination, out bytesWritten);
            }

            bytesWritten = 0;
            return false;
        }

        private static bool EncodeLiteralHeaderFieldWithIncrementalIndexing(int index, ReadOnlySpan<byte> value, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.2.1
            // ------------------------------------------------------
            //  0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 0 | 1 |      Index (6+)       |
            // +---+---+-----------------------+
            // | H |     Value Length (7+)     |
            // +---+---------------------------+
            // | Value String (Length octets)  |
            // +-------------------------------+

            if ((uint)destination.Length >= 2)
            {
                destination[0] = 0x40;
                if (IntegerEncoder.Encode(index, 6, destination, out int indexLength))
                {
                    Debug.Assert(indexLength >= 1);

                    if (EncodeOctets(value, destination.Slice(indexLength), out int nameLength))
                    {
                        bytesWritten = indexLength + nameLength;
                        return true;
                    }
                }
            }

            bytesWritten = 0;
            return false;
        }

        private static bool EncodeLiteralHeaderFieldWithIncrementalIndexing(int index, string value, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.2.1
            // ------------------------------------------------------
            //  0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 0 | 1 |      Index (6+)       |
            // +---+---+-----------------------+
            // | H |     Value Length (7+)     |
            // +---+---------------------------+
            // | Value String (Length octets)  |
            // +-------------------------------+

            if ((uint)destination.Length >= 2)
            {
                destination[0] = 0x40;
                if (IntegerEncoder.Encode(index, 6, destination, out int indexLength))
                {
                    Debug.Assert(indexLength >= 1);
                    if (EncodeStringLiteral(value, destination.Slice(indexLength), out int nameLength))
                    {
                        bytesWritten = indexLength + nameLength;
                        return true;
                    }
                }
            }

            bytesWritten = 0;
            return false;
        }


        private static bool EncodeLiteralHeaderFieldWithIncrementalIndexingNewName(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.2.1
            // ------------------------------------------------------
            //      0   1   2   3   4   5   6   7
            //    +---+---+---+---+---+---+---+---+
            //    | 0 | 1 |           0           |
            //    +---+---+-----------------------+
            //    | H |     Name Length (7+)      |
            //    +---+---------------------------+
            //    |  Name String (Length octets)  |
            //    +---+---------------------------+
            //    | H |     Value Length (7+)     |
            //    +---+---------------------------+
            //    | Value String (Length octets)  |
            //    +-------------------------------+

            if ((uint)destination.Length >= 3)
            {
                destination[0] = 0x40;
                if (EncodeOctets(name, destination.Slice(1), out int nameLength) &&
                    EncodeOctets(value, destination.Slice(1 + nameLength), out int valueLength))
                {
                    bytesWritten = 1 + nameLength + valueLength;
                    return true;
                }
            }

            bytesWritten = 0;
            return false;
        }

        /// <summary>Encodes a "Literal Header Field without Indexing".</summary>
        public static bool EncodeLiteralHeaderFieldWithoutIndexing(int index, string value, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.2.2
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 0 | 0 | 0 | 0 |  Index (4+)   |
            // +---+---+-----------------------+
            // | H |     Value Length (7+)     |
            // +---+---------------------------+
            // | Value String (Length octets)  |
            // +-------------------------------+

            if ((uint)destination.Length >= 2)
            {
                destination[0] = 0;
                if (IntegerEncoder.Encode(index, 4, destination, out int indexLength))
                {
                    Debug.Assert(indexLength >= 1);
                    if (EncodeStringLiteral(value, destination.Slice(indexLength), out int nameLength))
                    {
                        bytesWritten = indexLength + nameLength;
                        return true;
                    }
                }
            }

            bytesWritten = 0;
            return false;
        }

        /// <summary>
        /// Encodes a "Literal Header Field without Indexing", but only the index portion;
        /// a subsequent call to <see cref="EncodeStringLiteral"/> must be used to encode the associated value.
        /// </summary>
        public static bool BeginEncodeLiteralHeaderFieldWithoutIndexing(int index, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.2.2
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 0 | 0 | 0 | 0 |  Index (4+)   |
            // +---+---+-----------------------+
            //
            // ... expected after this:
            //
            // | H |     Value Length (7+)     |
            // +---+---------------------------+
            // | Value String (Length octets)  |
            // +-------------------------------+

            if ((uint)destination.Length != 0)
            {
                destination[0] = 0;
                if (IntegerEncoder.Encode(index, 4, destination, out int indexLength))
                {
                    Debug.Assert(indexLength >= 1);
                    bytesWritten = indexLength;
                    return true;
                }
            }

            bytesWritten = 0;
            return false;
        }

        /// <summary>Encodes a "Literal Header Field without Indexing - New Name".</summary>
        public static bool EncodeLiteralHeaderFieldWithoutIndexingNewName(string name, ReadOnlySpan<string> values, string separator, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.2.2
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 0 | 0 | 0 | 0 |       0       |
            // +---+---+-----------------------+
            // | H |     Name Length (7+)      |
            // +---+---------------------------+
            // |  Name String (Length octets)  |
            // +---+---------------------------+
            // | H |     Value Length (7+)     |
            // +---+---------------------------+
            // | Value String (Length octets)  |
            // +-------------------------------+

            if ((uint)destination.Length >= 3)
            {
                destination[0] = 0;
                if (EncodeStringLiteral(name, destination.Slice(1), out int nameLength, lowercase: true, onlyAscii: true) &&
                    EncodeStringLiterals(values, separator, destination.Slice(1 + nameLength), out int valueLength))
                {
                    bytesWritten = 1 + nameLength + valueLength;
                    return true;
                }
            }

            bytesWritten = 0;
            return false;
        }

        /// <summary>
        /// Encodes a "Literal Header Field without Indexing - New Name", but only the name portion;
        /// a subsequent call to <see cref="EncodeStringLiteral"/> must be used to encode the associated value.
        /// </summary>
        public static bool BeginEncodeLiteralHeaderFieldWithoutIndexingNewName(string name, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.2.2
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 0 | 0 | 0 | 0 |       0       |
            // +---+---+-----------------------+
            // | H |     Name Length (7+)      |
            // +---+---------------------------+
            // |  Name String (Length octets)  |
            // +---+---------------------------+
            //
            // ... expected after this:
            //
            // | H |     Value Length (7+)     |
            // +---+---------------------------+
            // | Value String (Length octets)  |
            // +-------------------------------+

            if ((uint)destination.Length >= 2)
            {
                destination[0] = 0;
                if (EncodeStringLiteral(name, destination.Slice(1), out int nameLength, lowercase: true, onlyAscii: true))
                {
                    bytesWritten = 1 + nameLength;
                    return true;
                }
            }

            bytesWritten = 0;
            return false;
        }

        private static bool EncodeStringLiteralValue(ReadOnlySpan<char> value, Span<byte> destination, out int bytesWritten, bool lowercase = false, bool onlyAscii = false)
        {
            if (value.Length <= destination.Length)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    if (onlyAscii && (c & 0xFF80) != 0)
                    {
                        throw new HPackEncodingException(SR.net_http_request_invalid_char_encoding);
                    }
                    destination[i] = (byte)(lowercase && (uint)(c - 'A') <= ('Z' - 'A') ? c | 0x20 : c);
                }

                bytesWritten = value.Length;
                return true;
            }

            bytesWritten = 0;
            return false;
        }

        public static bool EncodeStringLiteral(string value, Span<byte> destination, out int bytesWritten, bool lowercase = false, bool onlyAscii = false)
        {
            // From https://tools.ietf.org/html/rfc7541#section-5.2
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | H |    String Length (7+)     |
            // +---+---------------------------+
            // |      Data (Length octets)     |
            // +-------------------------------+

            if (destination.Length != 0)
            {
                destination[0] = 0; // TODO: Use Huffman encoding
                if (IntegerEncoder.Encode(value.Length, 7, destination, out int integerLength))
                {
                    Debug.Assert(integerLength >= 1);

                    if (EncodeStringLiteralValue(value, destination.Slice(integerLength), out int valueLength, lowercase, onlyAscii))
                    {
                        bytesWritten = integerLength + valueLength;
                        return true;
                    }
                }
            }

            bytesWritten = 0;
            return false;
        }

        public static bool EncodeOctets(ReadOnlySpan<byte> value, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-5.2
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | H |    String Length (7+)     |
            // +---+---------------------------+
            // |  String Data (Length octets)  |
            // +-------------------------------+

            if (destination.Length != 0)
            {
                destination[0] = 0; // TODO: Use Huffman encoding
                if (IntegerEncoder.Encode(value.Length, 7, destination, out int integerLength))
                {
                    Debug.Assert(integerLength >= 1);

                    if (value.TryCopyTo(destination.Slice(integerLength)))
                    {
                        bytesWritten = integerLength + value.Length;
                        return true;
                    }
                }
            }

            bytesWritten = 0;
            return false;
        }


        private static bool EncodeDynamicTableSizeUpdate(uint value, Span<byte> destination, out int bytesWritten)
        {
            // From https://tools.ietf.org/html/rfc7541#section-6.3
            // ------------------------------------------------------
            //   0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 0 | 0 | 1 |   Max size (5+)   |
            // +---+---------------------------+

            if (destination.Length != 0)
            {
                destination[0] = 0x20;
                if (IntegerEncoder.Encode((int)value, 5, destination, out int integerLength))
                {
                    Debug.Assert(integerLength >= 1);
                    bytesWritten = integerLength;
                    return true;
                }
            }

            bytesWritten = 0;
            return false;

        }

        public static bool EncodeStringLiterals(ReadOnlySpan<string> values, string separator, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;

            if (values.Length == 0)
            {
                return EncodeStringLiteral("", destination, out bytesWritten);
            }
            else if (values.Length == 1)
            {
                return EncodeStringLiteral(values[0], destination, out bytesWritten);
            }

            if (destination.Length != 0)
            {
                int valueLength = 0;

                // Calculate length of all parts and separators.
                foreach (string part in values)
                {
                    valueLength = checked((int)(valueLength + part.Length));
                }

                valueLength = checked((int)(valueLength + (values.Length - 1) * separator.Length));

                destination[0] = 0;
                if (IntegerEncoder.Encode(valueLength, 7, destination, out int integerLength))
                {
                    Debug.Assert(integerLength >= 1);

                    int encodedLength = 0;
                    for (int j = 0; j < values.Length; j++)
                    {
                        if (j != 0 && !EncodeStringLiteralValue(separator, destination.Slice(integerLength), out encodedLength))
                        {
                            return false;
                        }

                        integerLength += encodedLength;

                        if (!EncodeStringLiteralValue(values[j], destination.Slice(integerLength), out encodedLength))
                        {
                            return false;
                        }

                        integerLength += encodedLength;
                    }

                    bytesWritten = integerLength;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Encodes a "Literal Header Field without Indexing" to a new array.</summary>
        public static byte[] EncodeLiteralHeaderFieldWithoutIndexingToAllocatedArray(int index, string value)
        {
            Span<byte> span =
#if DEBUG
                stackalloc byte[4]; // to validate growth algorithm
#else
                stackalloc byte[512];
#endif
            while (true)
            {
                if (EncodeLiteralHeaderFieldWithoutIndexing(index, value, span, out int length))
                {
                    return span.Slice(0, length).ToArray();
                }

                // This is a rare path, only used once per HTTP/2 connection and only
                // for very long host names.  Just allocate rather than complicate
                // the code with ArrayPool usage.  In practice we should never hit this,
                // as hostnames should be <= 255 characters.
                span = new byte[span.Length * 2];
            }
        }

        public void SetDynamicHeaderTableSize(uint size)
        {
            if (size > _maxDynamicTableSize)
            {
                throw new HPackEncodingException(SR.Format(SR.net_http_hpack_exceded_dynamic_table_size_update, size, _maxDynamicTableSize));
            }

            if (!_headerTableSizeToConfirm.HasValue || size < _headerTableSizeToConfirm)
            {
                _headerTableSizeToConfirm = size;
                _dynamicTable.Resize((int)size);
            }
        }
    }
}
