namespace GitLive
{
    /// <summary>
    /// Console logger with verbosity level support.
    /// </summary>
    public class ConsoleLogger
    {
        /// <summary>
        /// Verbosity level for console output.
        /// </summary>
        public enum VerbosityLevel
        {
            /// <summary>Normal output - only essential messages.</summary>
            Normal = 0,

            /// <summary>Verbose output - includes more detailed progress information.</summary>
            Verbose = 1,

            /// <summary>Very verbose output - includes all diagnostic messages.</summary>
            VeryVerbose = 2
        }

        private readonly VerbosityLevel _level;

        /// <summary>
        /// Creates a new console logger with the specified verbosity level.
        /// </summary>
        /// <param name="level">The verbosity level to use.</param>
        public ConsoleLogger(VerbosityLevel level = VerbosityLevel.Normal)
        {
            _level = level;
        }

        /// <summary>
        /// Logs a normal-level message (always shown).
        /// </summary>
        public void Normal(string message)
        {
            if (_level >= VerbosityLevel.Normal)
            {
                Console.WriteLine(message);
            }
        }

        /// <summary>
        /// Logs a verbose-level message (shown at verbose and very-verbose levels).
        /// </summary>
        public void Verbose(string message)
        {
            if (_level >= VerbosityLevel.Verbose)
            {
                Console.WriteLine(message);
            }
        }

        /// <summary>
        /// Logs a very-verbose-level message (shown only at very-verbose level).
        /// </summary>
        public void VeryVerbose(string message)
        {
            if (_level >= VerbosityLevel.VeryVerbose)
            {
                Console.WriteLine(message);
            }
        }

        /// <summary>
        /// Logs an error message (always shown on stderr).
        /// </summary>
        public void Error(string message)
        {
            Console.Error.WriteLine(message);
        }
    }
}