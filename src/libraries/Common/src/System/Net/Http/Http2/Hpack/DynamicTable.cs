// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0.
// See THIRD-PARTY-NOTICES.TXT in the project root for license information.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace System.Net.Http.HPack
{
    internal class DynamicTable
    {
        private HeaderField[] _buffer;
        private int _maxSize;
        private int _size;
        private int _count;
        private int _insertIndex;
        private int _removeIndex;
        private int _totalItems = 0;

        private Dictionary<(string, string), int> _lookupTable;

        public DynamicTable(int maxSize)
        {
            _buffer = new HeaderField[maxSize / HeaderField.RfcOverhead];
            _maxSize = maxSize;
            _lookupTable = new Dictionary<(string, string), int>();
        }

        public HeaderTableIndex GetIndex(string name, string value)
        {
            if (value != null && _lookupTable.TryGetValue((name, value), out int headerWithValueIndex))
                return new HeaderTableIndex(null, getDynamicTableIndex(headerWithValueIndex));
            if (_lookupTable.TryGetValue((name, (string)null), out int headerIndex))
                return new HeaderTableIndex(getDynamicTableIndex(headerIndex), null);
            return new HeaderTableIndex(null, null);
        }

        private int getDynamicTableIndex(int itemIndex)
        {
            return StaticTable.Count - itemIndex + _totalItems;
        }

        public int Count => _count;

        public int Size => _size;

        public int MaxSize => _maxSize;

        public HeaderField this[int index]
        {
            get
            {
                index = index - StaticTable.Count - 1;
                if (index >= _count || index < 0)
                {
                    throw new IndexOutOfRangeException();
                }

                index = _insertIndex - index - 1;

                if (index < 0)
                {
                    // _buffer is circular; wrap the index back around.
                    index += _buffer.Length;
                }

                return _buffer[index];
            }
        }

        public void Insert(int index, string value)
        {
            Insert(this[index].Name, value);
        }

        public void Insert(string name, string value)
        {
            int entryLength = HeaderField.GetLength(name.Length, value.Length);
            EnsureAvailable(entryLength);

            if (entryLength > _maxSize)
            {
                // http://httpwg.org/specs/rfc7541.html#rfc.section.4.4
                // It is not an error to attempt to add an entry that is larger than the maximum size;
                // an attempt to add an entry larger than the maximum size causes the table to be emptied
                // of all existing entries and results in an empty table.
                return;
            }

            var entry = new HeaderField(name, value);

            _buffer[_insertIndex] = entry;
            _insertIndex = (_insertIndex + 1) % _buffer.Length;
            _size += entry.Length;
            _count++;

            // TODO: add remove from lookup table
            _lookupTable[(entry.Name, entry.Value)] = _totalItems;
            _lookupTable[(entry.Name, null)] = _totalItems;
            _totalItems++;

            return;
        }

        public void Resize(int maxSize)
        {
            if (maxSize > _maxSize)
            {
                var newBuffer = new HeaderField[maxSize / HeaderField.RfcOverhead];

                int headCount = Math.Min(_buffer.Length - _removeIndex, _count);
                int tailCount = _count - headCount;

                Array.Copy(_buffer, _removeIndex, newBuffer, 0, headCount);
                Array.Copy(_buffer, 0, newBuffer, headCount, tailCount);

                _buffer = newBuffer;
                _removeIndex = 0;
                _insertIndex = _count;
                _maxSize = maxSize;
            }
            else
            {
                _maxSize = maxSize;
                EnsureAvailable(0);
            }
        }

        private void EnsureAvailable(int available)
        {
            while (_count > 0 && _maxSize - _size < available)
            {
                int dynamicTableIndex = _totalItems - _count;

                ref HeaderField field = ref _buffer[_removeIndex];
                _size -= field.Length;

                _count--;
                _removeIndex = (_removeIndex + 1) % _buffer.Length;

                if (dynamicTableIndex == _lookupTable[(field.Name, null)])
                {
                    _lookupTable.Remove((field.Name, null));
                }

                if (dynamicTableIndex == _lookupTable[(field.Name, field.Value)])
                {
                    _lookupTable.Remove((field.Name, field.Value));
                }

                field = default;
            }
        }
    }
}