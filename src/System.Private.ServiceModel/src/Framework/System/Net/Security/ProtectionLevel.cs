// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace System.Net.Security
{
    // This will request security properties of a NegotiateStream
    public enum ProtectionLevel
    {
        // Used only with Negotiate on Win9x platform
        None = 0,

        // Data integrity only
        Sign = 1,

        // Both data confidentiality and integrity
        EncryptAndSign = 2
    }
}

