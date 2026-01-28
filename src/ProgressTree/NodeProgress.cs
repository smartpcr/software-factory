// -----------------------------------------------------------------------
// <copyright file="NodeProgress.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ProgressTree
{
    public struct NodeProgress(
        ProgressStatus status,
        double durationMs,
        double progressPercent,
        DateTime? startTime,
        DateTime? finishTime,
        string statusMessage,
        string errorMessage)
    {
        public ProgressStatus Status = status;
        public double DurationMs = durationMs;
        public double ProgressPercent = progressPercent;
        public DateTime? StartTime = startTime;
        public DateTime? FinishTime = finishTime;
        public string StatusMessage = statusMessage;
        public string ErrorMessage = errorMessage;
    }
}