// -----------------------------------------------------------------------
// <copyright file="ProviderStatistics.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace EtwProcessor
{
    using System.Collections.Concurrent;

    public class ProviderStatistics
    {
        public required string ProviderName { get; set; }
        public long EventCount { get; set; }
        public DateTime StartTime { get; set; } = DateTime.MaxValue;
        public DateTime EndTime { get; set; } = DateTime.MinValue;
        public ConcurrentDictionary<string, long> EventTypes { get; set; } = new();
    }
}