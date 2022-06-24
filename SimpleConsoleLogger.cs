using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RanorexOrangebeardListener
{
    /// <summary>
    /// A very simple console logger.
    /// The ultimate purpose is to implement ILogger or use an existing ILogger implementation.
    /// </summary>
    class SimpleConsoleLogger
    {
        public void LogError(string str) => Console.WriteLine(str);
    }
}
