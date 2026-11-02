using System;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace aisilol.Common
{
    public static class Log
    {
        public enum Level
        {
            Info,
            Warning,
            Error,
        }
        
        [HideInCallstack]
        public static void WriteLog(Level level, string message, 
            UnityEngine.Object context = null, 
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string memberName = "")
        {
            var text =
                $"[{Path.GetFileName(filePath)}:{lineNumber}] {Path.GetFileNameWithoutExtension(filePath)}::{memberName}() \"{message}\"";

            switch (level)
            {
                default:
                    throw new ArgumentOutOfRangeException(nameof(level), level, null);

                case Level.Info: Debug.Log(text, context); break;
                case Level.Warning: Debug.LogWarning(text, context); break;
                case Level.Error: Debug.LogError(text, context); break;
            }
        }

        [HideInCallstack]
        public static void Error(string message, 
            UnityEngine.Object context = null, 
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string memberName = "") => WriteLog(Level.Error, message, context, filePath, lineNumber, memberName);
    }
}