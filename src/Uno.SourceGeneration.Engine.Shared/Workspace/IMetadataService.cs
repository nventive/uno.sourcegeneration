﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;

namespace Uno.SourceGeneration.Engine.Workspace
{
    internal interface IMetadataService : IWorkspaceService
    {
        PortableExecutableReference GetReference(string resolvedPath, MetadataReferenceProperties properties);
    }
}
