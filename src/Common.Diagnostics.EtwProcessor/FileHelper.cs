// -----------------------------------------------------------------------
// <copyright file="FileHelper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace EtwProcessor
{
    public static class FileHelper
    {
        public static List<string> FindTraceFiles(string directory)
        {
            var files = new List<string>();

            foreach (var ext in new[] { "*.etl", "*.evtx" })
            {
                try
                {
                    files.AddRange(Directory.GetFiles(directory, ext, SearchOption.AllDirectories));
                }
                catch { }
            }

            return files;
        }
    }
}