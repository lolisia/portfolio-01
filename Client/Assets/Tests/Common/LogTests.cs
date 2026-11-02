using System;
using aisilol.Common;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace aisilol.Tests.Common
{
    public class LogTests
    {
        [TestCase(Log.Level.Info, LogType.Log)]
        [TestCase(Log.Level.Warning, LogType.Warning)]
        [TestCase(Log.Level.Error, LogType.Error)]
        public void WriteLog_WritesCallerInfoWithMatchingLogType(Log.Level level, LogType logType)
        {
            LogAssert.Expect(logType, TestHelper.Pattern(@"^\[LogTests\.cs:\d+\] LogTests::WriteLog_WritesCallerInfoWithMatchingLogType\(\) ""hello""$"));

            Log.WriteLog(level, "hello");
        }

        [Test]
        public void WriteLog_UnknownLevel_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Log.WriteLog((Log.Level)100, "hello"));
        }

        [Test]
        public void Error_WritesErrorLog()
        {
            LogAssert.Expect(LogType.Error, TestHelper.Pattern(@"LogTests::Error_WritesErrorLog\(\) ""failed""$"));

            Log.Error("failed");
        }
    }
}
