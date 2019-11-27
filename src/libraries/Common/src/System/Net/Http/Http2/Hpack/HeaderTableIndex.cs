// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0.
// See THIRD-PARTY-NOTICES.TXT in the project root for license information.

namespace System.Net.Http.HPack
{
    internal readonly struct HeaderTableIndex
    {

        public HeaderTableIndex(int? headerIndex, int? headerWithValueIndex) => (HeaderIndex, HeaderWithValueIndex) = (headerIndex, headerWithValueIndex);

        public int? HeaderIndex { get; }

        public int? HeaderWithValueIndex { get; }
    }
}
