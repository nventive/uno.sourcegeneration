﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

// This file is inspired by the work the Roslyn compiler, adapter for source generation.
// Original source: https://github.com/dotnet/roslyn/commit/f15d8f701eee5a783b11e73d64b2e04f20ab64a7

using System;
using System.Diagnostics;
using System.IO;

namespace Uno.SourceGeneration.Host.Helpers
{
    internal struct FileKey : IEquatable<FileKey>
    {
        /// <summary>
        /// Full case-insensitive path.
        /// </summary>
        public readonly string FullPath;

        /// <summary>
        /// Last write time (UTC).
        /// </summary>
        public readonly DateTime Timestamp;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="fullPath">Full path.</param>
        /// <param name="timestamp">Last write time (UTC).</param>
        public FileKey(string fullPath, DateTime timestamp)
        {
            Debug.Assert(PathUtilities.IsAbsolute(fullPath));
            Debug.Assert(timestamp.Kind == DateTimeKind.Utc);

            FullPath = fullPath;
            Timestamp = timestamp;
        }

        /// <exception cref="IOException"/>
        public static FileKey Create(string fullPath)
        {
            return new FileKey(fullPath, FileUtilities.GetFileTimeStamp(fullPath));
        }

        public override int GetHashCode()
        {
            return Hash.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(this.FullPath),
                this.Timestamp.GetHashCode());
        }

        public override bool Equals(object obj)
        {
            return obj is FileKey && Equals((FileKey)obj);
        }

        public override string ToString()
        {
            return string.Format("'{0}'@{1}", FullPath, Timestamp);
        }

        public bool Equals(FileKey other)
        {
            return
                this.Timestamp == other.Timestamp &&
                string.Equals(this.FullPath, other.FullPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
