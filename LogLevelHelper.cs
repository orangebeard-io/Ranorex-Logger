using Orangebeard.Client.Entities;
using System;
using System.Linq;

namespace RanorexOrangebeardListener
{
    public class LogLevelHelper
    {
        private static readonly SimpleConsoleLogger logger = new SimpleConsoleLogger();

        public static LogLevel DetermineLogLevel(string levelStr)
        {
            LogLevel level;

            // Case-insensitive test to see if the given "levelStr" exists in our LogLevel enum.
            string[] logLevelNames = Enum.GetNames(typeof(LogLevel));
            bool levelStrContained = logLevelNames.Any(logLevelName => string.Compare(logLevelName, levelStr, StringComparison.InvariantCultureIgnoreCase) == 0);


            if (levelStrContained)
            {
                level = (LogLevel)Enum.Parse(typeof(LogLevel), levelStr.ToLower());
            }
            else if (levelStr.Equals("Success", StringComparison.InvariantCultureIgnoreCase))
            {
                level = LogLevel.info;
            }
            else if (levelStr.Equals("Failure", StringComparison.InvariantCultureIgnoreCase))
            {
                level = LogLevel.error;
            }
            else if (levelStr.Equals("Warn", StringComparison.InvariantCultureIgnoreCase))
            {
                level = LogLevel.warn;
            }
            else
            {
                logger.LogError($"Unknown log level: {levelStr}");
                level = LogLevel.unknown;
            }
            return level;
        }

        /// Determine if a LogLevel has at least a minimum severity.
        /// For example, <code>LogLevel.Error</code> is more severe than <code>LogLevel.Warning</code>; so <code>MeetsMinimumSeverity(Error, Warning)</code> is <code>true</code>.
        /// Similarly, <code>LogLevel.Warning</code> is at severity level <code>Warning</code>; so <code>MeetsMinimumSeverity(Warning, Warning)</code> is also <code>true</code>.
        /// But <code>LogLevel.Info</code> is less severe than <code>LogLevel.Warning</code>, so <code>MeetsMinimumSeverity(Warning, Info)</code> is <code>false</code>.
        /// <param name="level">The LogLevel whose severity must be checked.</param>
        /// <param name="threshold">The severity level to check against.</param>
        /// <returns>The boolean value <code>true</code> if and only if the given log level has at least the same level of severity as the threshold value.</returns>
        public static bool MeetsMinimumSeverity(LogLevel level, LogLevel threshold)
        {
            return ((int)level) >= (int)threshold;
        }

    }
}
