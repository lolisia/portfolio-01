using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using Object = UnityEngine.Object;

namespace aisilol.Common
{
    public static class Utility
    {
        [HideInCallstack]
        public static bool IsValid(Object obj, 
            Object context = null,
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string memberName = "")
        {
            if (obj)
                return true;

            Log.WriteLog(Log.Level.Error, "인자가 null 입니다.", context, filePath, lineNumber, memberName);
            return false;
        }

        [HideInCallstack]
        public static bool IsValid(object obj, 
            Object context = null,
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string memberName = "")
        {
            if (obj != null)
                return true;

            Log.WriteLog(Log.Level.Error, "인자가 null 입니다.", context, filePath, lineNumber, memberName);
            return false;
        }

        [HideInCallstack]
        public static void CheckValid(object obj, 
            Object context = null,
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string memberName = "")
        {
            if (obj != null)
                return;

            Log.WriteLog(Log.Level.Error, "인자가 null 입니다.", context, filePath, lineNumber, memberName);
        }

        [HideInCallstack]
        public static bool TryInstantiate<T>(T prefab, out T instance, 
            Object context = null,
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string memberName = "") where T : Object
        {
            instance = null;

            if (!IsValid(prefab, context, filePath, lineNumber, memberName))
                return false;

            instance = Object.Instantiate(prefab);
            return true;
        }

        [HideInCallstack]
        public static bool TryGetComponent<T>(GameObject gameObject, out T component,
            Object context = null,
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int lineNumber = 0,
            [CallerMemberName] string memberName = "") where T : Component
        {
            component = null;

            if (!IsValid(gameObject, context, filePath, lineNumber, memberName))
                return false;
            
            if (gameObject.TryGetComponent(out component))
                return true;
            
            Log.WriteLog(Log.Level.Error, $"{gameObject.name}에 {typeof(T).Name} Component가 존재하지 않습니다.", context, filePath, lineNumber, memberName);
            return false;
        }
    }
}
