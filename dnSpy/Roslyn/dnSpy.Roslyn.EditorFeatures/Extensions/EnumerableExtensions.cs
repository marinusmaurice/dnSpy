// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;

namespace dnSpy.Roslyn.EditorFeatures.Extensions
{
    internal static class EnumerableExtensions
    {
        // Removed from Roslyn's internal utilities
        public static T? FirstOrNullable<T>(this IEnumerable<T> source) where T : struct
        {
            foreach (var item in source)
            {
                return item;
            }

            return null;
        }
    }
}
