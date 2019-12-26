// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0.
// See THIRD-PARTY-NOTICES.TXT in the project root for license information.

using System.Buffers;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Net.Http;
using System.Net.Http.HPack;
using Xunit;

namespace System.Net.Http.Unit.Tests.HPack
{
    public class HPackEncoderTests
    {

        [Fact]
        public void EncodeIndexedHeaderField()
        {
            var buffer = CreateBuffer();

            int index = 0xAAA;
            Assert.True(HPackEncoder.EncodeIndexedHeaderField(index, buffer, out int bytesWritten));

            Assert.Equal(3, bytesWritten);

            // From https://tools.ietf.org/html/rfc7541#section-6.1
            Assert.Equal(new byte[] {
             0b10000000 // start pattern
            | 0b01111111, // Index (7+)
            171, // Index
            20 // Index
            }, buffer[..bytesWritten]);
        }

        [Fact]
        public void EncodeLiteralHeaderFieldWithoutIndexing()
        {
            var buffer = CreateBuffer();

            int index = 0xAAA;
            var value = "value";
            Assert.True(HPackEncoder.EncodeLiteralHeaderFieldWithoutIndexing(index, value, buffer, out int bytesWritten));

            Assert.Equal(9, bytesWritten);

            Assert.Equal(new byte[] {
            0 // start pattern
            | 0b00001111, // Index (4+)
            155, // Index
            21, // Index
            0 // No hufman encoding
            | 5, // Value length
            (byte)'v', (byte)'a', (byte)'l', (byte)'u', (byte)'e' // Value
            }, buffer[..bytesWritten]);
        }

        [Fact]
        public void BeginEncodeLiteralHeaderFieldWithoutIndexing()
        {
            var buffer = CreateBuffer();

            int index = 0xAAA;
            Assert.True(HPackEncoder.BeginEncodeLiteralHeaderFieldWithoutIndexing(index, buffer, out int bytesWritten));

            Assert.Equal(3, bytesWritten);

            // From https://tools.ietf.org/html/rfc7541#section-6.2.2
            Assert.Equal(new byte[] {
            0 // start pattern
            | 0b00001111, // Index (4+)
            155, // Index
            21 // Index
            }, buffer[..bytesWritten]);
        }

        public static IEnumerable<object[]> EncodeLiteralHeaderFieldWithoutIndexingNewNameSamples()
        {
            return data().Select(data => new object[] { data.Item1, data.Item2, data.Item3 });

            IEnumerable<(string[], string, byte[])> data()
            {
                yield return (Array.Empty<string>(), ";", new byte[] { 0 // No hufman encoding
                    | 0, // Value length
                });
                yield return (new[] { "single" }, ";", new byte[] { 0 // No hufman encoding
                    | 6, // Value length
                    (byte)'s', (byte)'i', (byte)'n', (byte)'g', (byte)'l', (byte)'e'
                });
                yield return (new[] { "first", "second" }, ";", new byte[] { 0 // No hufman encoding
                    | 12, // Value length
                    (byte)'f', (byte)'i', (byte)'r', (byte)'s', (byte)'t', (byte)';',
                    (byte)'s', (byte)'e', (byte)'c', (byte)'o', (byte)'n', (byte)'d'
                });
            }

        }

        [Theory, MemberData(nameof(EncodeLiteralHeaderFieldWithoutIndexingNewNameSamples))]
        public void EncodeLiteralHeaderFieldWithoutIndexingNewName(string[] values, string separator, byte[] encodedValues)
        {
            var buffer = CreateBuffer();

            string name = "name";

            Assert.True(HPackEncoder.EncodeLiteralHeaderFieldWithoutIndexingNewName(name, values, separator, buffer, out int bytesWritten));

            Assert.NotEqual(0, bytesWritten);

            // From https://tools.ietf.org/html/rfc7541#section-6.2.2
            Assert.Equal(new byte[] {
            0, // start pattern
            0 // No hufman encoding
            | 4, // Header length
            (byte)'n', (byte)'a', (byte)'m', (byte)'e', // Header
            }.Concat(encodedValues) // Value length + octet literal
            , buffer[..bytesWritten]);
        }

        [Fact]
        public void BeginEncodeLiteralHeaderFieldWithoutIndexingNewName()
        {
            var buffer = CreateBuffer();

            string name = "name";
            Assert.True(HPackEncoder.BeginEncodeLiteralHeaderFieldWithoutIndexingNewName(name, buffer, out int bytesWritten));

            //Assert.Equal(6, bytesWritten);

            // From https://tools.ietf.org/html/rfc7541#section-6.2.2
            Assert.Equal(new byte[] {
            0, // start pattern
            0 // No hufman encoding
            | 4, // Header length
            (byte)'n', (byte)'a', (byte)'m', (byte)'e' // Header
            }, buffer[..bytesWritten]);
        }

        [Fact]
        public void EncodeStringLiteral()
        {
            var buffer = CreateBuffer();

            string value = "value";
            Assert.True(HPackEncoder.EncodeStringLiteral(value, buffer, out int bytesWritten));

            Assert.Equal(6, bytesWritten);

            // From https://tools.ietf.org/html/rfc7541#section-5.2
            Assert.Equal(new byte[] {
            0 // No hufman encoding
            | 5, // Value length
            (byte)'v', (byte)'a', (byte)'l', (byte)'u', (byte)'e' // Value
            }, buffer[..bytesWritten]);
        }

        [Fact]
        public void EncodeOctets()
        {
            var buffer = CreateBuffer();

            byte[] value = Encoding.ASCII.GetBytes("value");
            Assert.True(HPackEncoder.EncodeOctets(value, buffer, out int bytesWritten));

            Assert.Equal(6, bytesWritten);

            // From https://tools.ietf.org/html/rfc7541#section-5.2
            Assert.Equal(new byte[] {
            0 // No hufman encoding
            | 5, // Value length
            (byte)'v', (byte)'a', (byte)'l', (byte)'u', (byte)'e' // Value
            }, buffer[..bytesWritten]);
        }

        [Fact]
        public void DynamicTableResize_EncodeDynamicTableSizeUpdate_ExceedMaxSize()
        {
            var buffer = CreateBuffer();

            HPackEncoder encoder = new HPackEncoder();

            // https://tools.ietf.org/html/rfc7541#section-4.2
            HPackEncodingException exception = Assert.Throws<HPackEncodingException>(() => encoder.SetDynamicHeaderTableSize(HPackDecoder.DefaultHeaderTableSize + 1));

            Assert.Equal(SR.Format(SR.net_http_hpack_exceded_dynamic_table_size_update, HPackDecoder.DefaultHeaderTableSize + 1, HPackDecoder.DefaultHeaderTableSize), exception.Message);
        }


        [Fact]
        public void DynamicTableResize_EncodeDynamicTableSizeUpdate_ReduceSize()
        {

            var buffer = CreateBuffer();

            HPackEncoder encoder = new HPackEncoder();

            // https://tools.ietf.org/html/rfc7541#section-4.2
            encoder.SetDynamicHeaderTableSize(HPackDecoder.DefaultHeaderTableSize - 1);

            Assert.True(encoder.WriteHeadersBegin(buffer, out int bytesWritten));

            // https://tools.ietf.org/html/rfc7541#section-6.3
            Assert.Equal(new byte[] {
            0b00100000 // start pattern
            | 0b00011111, // Max size (5+)
            224, 31 // Max size
            }, buffer[..bytesWritten]);
        }

        [Fact]
        public void DynamicTableResize_EncodeDynamicTableSizeUpdate_SnedMultipleReduceSize()
        {

            var buffer = CreateBuffer();

            HPackEncoder encoder = new HPackEncoder();

            // https://tools.ietf.org/html/rfc7541#section-4.2
            encoder.SetDynamicHeaderTableSize(1);
            encoder.SetDynamicHeaderTableSize(2);

            Assert.True(encoder.WriteHeadersBegin(buffer, out int bytesWritten));

            // https://tools.ietf.org/html/rfc7541#section-6.3
            Assert.Equal(new byte[] {
            0b00100000 // start pattern
            | 1, // Max size (5+)
            }, buffer[..bytesWritten]);
        }

        [Fact]
        public void DynamicTable_MatchNothing()
        {

            var buffer = CreateBuffer();

            HPackEncoder encoder = new HPackEncoder();

            byte[] headerName = Encoding.ASCII.GetBytes("name");
            byte[] headerValue = Encoding.ASCII.GetBytes("value");

            HeaderTableIndex index = encoder.GetIndex(headerName, new byte[0]); // Header missing in the table
            Assert.True(encoder.EncodeLiteralField(index, headerName, headerValue, buffer, out int bytesWritten));


            // https://tools.ietf.org/html/rfc7541#section-6.2.1
            Assert.Equal(new byte[] {
            0b01000000 ,// start pattern
            0 // No hufman encoding
            | 4, // Name length
            (byte)'n', (byte)'a', (byte)'m', (byte)'e', // Name
            0 // No hufman encoding
            | 5, // Value length
            (byte)'v', (byte)'a', (byte)'l', (byte)'u', (byte)'e' // Value
            }, buffer[..bytesWritten]);
        }

        [Fact]
        public void DynamicTable_MatchName()
        {

            var buffer = CreateBuffer();

            HPackEncoder encoder = new HPackEncoder();

            byte[] headerName = Encoding.ASCII.GetBytes("name");
            byte[] header1Value = Encoding.ASCII.GetBytes("value1");
            byte[] header2Value = Encoding.ASCII.GetBytes("value2");

            HeaderTableIndex index = encoder.GetIndex(headerName, header1Value); // Header missing in the table
            Assert.True(encoder.EncodeLiteralField(index, headerName, header1Value, new byte[1024], out _));

            index = encoder.GetIndex(headerName, header2Value); // Found header with different value
            Assert.True(encoder.EncodeLiteralField(index, headerName, header2Value, buffer, out int bytesWritten));

            // https://tools.ietf.org/html/rfc7541#section-6.2.1
            Assert.Equal(new byte[] {
            0b01000000 // start pattern
            | 1, // Index (6+)
            0 // No hufman encoding
            | 6, // Value length
            (byte)'v', (byte)'a', (byte)'l', (byte)'u', (byte)'e', (byte)'2' // Value
            }, buffer[..bytesWritten]);
        }

        [Fact]
        public void DynamicTable_MatchNameAndValue()
        {

            var buffer = CreateBuffer();

            HPackEncoder encoder = new HPackEncoder();

            byte[] headerName = Encoding.ASCII.GetBytes("name");
            byte[] header1Value = Encoding.ASCII.GetBytes("value1");
            byte[] header2Value = Encoding.ASCII.GetBytes("value2");

            HeaderTableIndex index = encoder.GetIndex(headerName, new byte[0]); // Header missing in the table
            Assert.True(encoder.EncodeLiteralField(index, headerName, header1Value, new byte[1024], out _));

            index = encoder.GetIndex(headerName, header2Value); // Found header with different value
            Assert.True(encoder.EncodeLiteralField(index, headerName, header2Value, new byte[1024], out _));

            index = encoder.GetIndex(headerName, header2Value); // Found exactly same header
            Assert.True(encoder.EncodeLiteralField(index, headerName, header2Value, buffer, out int bytesWritten));


            // https://tools.ietf.org/html/rfc7541#section-6.2.1
            Assert.Equal(new byte[] {
             0b10000000 // start pattern
            | 2 // Index (7+)
            }, buffer[..bytesWritten]);
        }

        private byte[] CreateBuffer(int size = 1024)
        {
            var buffer = new byte[size];
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = 0xff;
            }
            return buffer;
        }

    }
}
